using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cove.Core.Interfaces;
using Cove.Data;

namespace Cove.Api.Services;

public class CleanService(
    IJobService jobService,
    IServiceScopeFactory scopeFactory,
    ILogger<CleanService> logger) : ICleanService
{
    private sealed record CleanFileInfo(int Id, string Path, int? ZipFileId);
    private sealed record CleanFolderInfo(string Path, int? ZipFileId);
    private sealed class CleanEntity
    {
        public int Id { get; init; }
        public List<CleanFileInfo> Files { get; init; } = [];
        public CleanFolderInfo? Folder { get; init; }
    }

    public string StartClean(bool dryRun = false, IReadOnlyList<string>? paths = null)
    {
        var scopedPaths = GeneratePathFilter.Normalize(paths);
        return jobService.Enqueue("clean", dryRun ? "Cleaning (dry run)" : "Cleaning library", async (progress, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            await using var plan = await CleanPlan.CreateAsync(ct);

            // Clips use their parent's file; a fileless clip must never be classified as an orphan.
            await InspectAsync(db, plan, "video", db.Videos.AsNoTracking().Where(video => video.ParentVideoId == null)
                .Select(video => new CleanEntity { Id = video.Id, Files = video.Files.Select(file => new CleanFileInfo(file.Id, file.Path, file.ZipFileId)).ToList() }), scopedPaths, progress, ct);
            await InspectAsync(db, plan, "image", db.Images.AsNoTracking()
                .Select(image => new CleanEntity { Id = image.Id, Files = image.Files.Select(file => new CleanFileInfo(file.Id, file.Path, file.ZipFileId)).ToList() }), scopedPaths, progress, ct);
            await InspectAsync(db, plan, "audio", db.Audios.AsNoTracking()
                .Select(audio => new CleanEntity { Id = audio.Id, Files = audio.Files.Select(file => new CleanFileInfo(file.Id, file.Path, file.ZipFileId)).ToList() }), scopedPaths, progress, ct);
            await InspectAsync(db, plan, "text", db.TextDocuments.AsNoTracking()
                .Select(text => new CleanEntity { Id = text.Id, Files = text.Files.Select(file => new CleanFileInfo(file.Id, file.Path, file.ZipFileId)).ToList() }), scopedPaths, progress, ct);
            await InspectAsync(db, plan, "gallery", db.Galleries.AsNoTracking()
                .Select(gallery => new CleanEntity { Id = gallery.Id,
                    Files = gallery.Files.Select(file => new CleanFileInfo(file.Id, file.Path, file.ZipFileId)).ToList(),
                    Folder = gallery.Folder == null ? null : new CleanFolderInfo(gallery.Folder.Path, gallery.Folder.ZipFileId) }), scopedPaths, progress, ct);

            var missing = await plan.CountAsync("files", ct);
            var videos = await plan.CountAsync("video", ct);
            var images = await plan.CountAsync("image", ct);
            var galleries = await plan.CountAsync("gallery", ct);
            var audios = await plan.CountAsync("audio", ct);
            var texts = await plan.CountAsync("text", ct);
            logger.LogInformation("Clean found {MissingFiles} missing files, {Videos} orphaned videos, {Images} orphaned images, {Galleries} orphaned galleries, {Audios} orphaned audios, {Texts} orphaned texts",
                missing, videos, images, galleries, audios, texts);
            if (dryRun)
            {
                logger.LogInformation("Dry run - no changes made");
                return;
            }

            // Finish all filesystem/archive inspection before changing the destination. Deleting a
            // backing archive early would otherwise change decisions for later virtual entries.
            // Orphans go through the same deletion path as a user delete so polymorphic rows (tag
            // applications, segments, group items, provenance), custom field values, cover blobs and
            // generated files go with them and extensions receive the Deleted events.
            var removal = new OrphanRemoval(videos + images + galleries + audios + texts);
            await DeleteOrphansAsync(plan, "video", BulkDeletionEntityKind.Video, removal, progress, ct);
            await DeleteOrphansAsync(plan, "image", BulkDeletionEntityKind.Image, removal, progress, ct);
            await DeleteOrphansAsync(plan, "gallery", BulkDeletionEntityKind.Gallery, removal, progress, ct);
            await DeleteOrphansAsync(plan, "audio", BulkDeletionEntityKind.Audio, removal, progress, ct);
            await DeleteOrphansAsync(plan, "text", BulkDeletionEntityKind.Text, removal, progress, ct);

            // Orphans took their own files with them; this prunes missing files of surviving entities.
            var pruned = 0;
            await foreach (var ids in plan.ReadAsync("files", ct))
                pruned += await db.Set<BaseFileEntity>().Where(file => ids.Contains(file.Id)).ExecuteDeleteAsync(ct);

            await foreach (var ids in plan.ReadAsync("refresh-audio", ct))
            {
                var affected = await db.Audios.Include(audio => audio.Files).Where(audio => ids.Contains(audio.Id)).ToListAsync(ct);
                foreach (var audio in affected) ScanAudioProcessor.RefreshAudioSummary(audio);
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }
            await foreach (var ids in plan.ReadAsync("refresh-text", ct))
            {
                var affected = await db.TextDocuments.Include(text => text.Files).Where(text => ids.Contains(text.Id)).ToListAsync(ct);
                foreach (var text in affected) ScanTextProcessor.RefreshTextSummary(text);
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }

            var dangling = await DeleteDanglingAsync(db.VideoFiles.Where(file => file.VideoId == null), scopedPaths, ct)
                + await DeleteDanglingAsync(db.ImageFiles.Where(file => file.ImageId == null), scopedPaths, ct)
                + await DeleteDanglingAsync(db.GalleryFiles.Where(file => file.GalleryId == null), scopedPaths, ct)
                + await DeleteDanglingAsync(db.AudioFiles.Where(file => file.AudioId == null), scopedPaths, ct)
                + await DeleteDanglingAsync(db.TextFiles.Where(file => file.TextDocumentId == null), scopedPaths, ct);
            var recomputed = 0;
            if (pruned + removal.Removed + dangling > 0)
            {
                progress.Report(1d, "Recomputing library counts");
                recomputed = await db.RecomputeAllDerivedCountsAsync(cancellationToken: ct);
            }
            logger.LogInformation("Clean completed: removed {Removed} of {Orphans} orphaned items ({Failed} failed), {Files} missing files of surviving items, {DanglingFiles} dangling files; recomputed {Recomputed} entity counts",
                removal.Removed, removal.Total, removal.Failed, pruned, dangling, recomputed);
            if (removal.Failed > 0)
                progress.SetSummary($"Removed {removal.Removed} of {removal.Total} orphaned items; {removal.Failed} could not be removed (see log)");
        }, exclusive: false);
    }

    private static async Task InspectAsync(CoveContext db, CleanPlan plan, string kind, IQueryable<CleanEntity> query,
        IReadOnlyList<string> paths, IJobProgress progress, CancellationToken ct)
    {
        int? afterId = null;
        var checkedCount = 0;
        var total = await query.CountAsync(ct);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await query.Where(entity => afterId == null || entity.Id > afterId)
                .OrderBy(entity => entity.Id).Take(CleanPlan.BatchSize).ToListAsync(ct);
            if (batch.Count == 0) break;
            afterId = batch[^1].Id;
            var archiveIds = batch.SelectMany(entity => entity.Files.Select(file => file.ZipFileId).Append(entity.Folder?.ZipFileId))
                .OfType<int>().Distinct().ToArray();
            var archives = new Dictionary<int, string>();
            foreach (var ids in archiveIds.Chunk(CleanPlan.BatchSize))
                foreach (var archive in await db.Set<BaseFileEntity>().AsNoTracking().Where(file => ids.Contains(file.Id))
                    .Select(file => new { file.Id, file.Path }).ToListAsync(ct))
                    archives[archive.Id] = archive.Path;
            // Cache only this batch's checks, including shared archive backing paths.
            var existence = new Dictionary<string, bool>(StringComparer.Ordinal);
            bool Exists(string path)
            {
                if (string.IsNullOrEmpty(path)) return false;
                if (!existence.TryGetValue(path, out var found)) existence[path] = found = File.Exists(path);
                return found;
            }
            string PhysicalPath(string path, int? archiveId) => archiveId is int id && archives.TryGetValue(id, out var archive) ? archive : path;
            bool InScope(string path, int? archiveId) => GeneratePathFilter.Contains(PhysicalPath(path, archiveId), paths);
            bool FileExists(CleanFileInfo file) => file.ZipFileId is int id ? archives.TryGetValue(id, out var archive) && Exists(archive) : Exists(file.Path);
            var missingFiles = new List<int>();
            var orphans = new List<int>();
            var refresh = new List<int>();
            foreach (var entity in batch)
            {
                ct.ThrowIfCancellationRequested();
                var missingIds = entity.Files.Where(file => InScope(file.Path, file.ZipFileId) && !FileExists(file)).Select(file => file.Id).ToHashSet();
                missingFiles.AddRange(missingIds);
                var orphan = (paths.Count == 0 || missingIds.Count > 0) && entity.Files.All(file => missingIds.Contains(file.Id));
                if (kind == "gallery")
                {
                    if (entity.Folder is { } folder)
                    {
                        orphan = InScope(folder.Path, folder.ZipFileId)
                            && !(folder.ZipFileId is int id ? archives.TryGetValue(id, out var archive) && Exists(archive) : Directory.Exists(folder.Path));
                    }
                    else
                        orphan = entity.Files.Count > 0 && missingIds.Count > 0 && entity.Files.All(file => missingIds.Contains(file.Id));
                }
                if (orphan) orphans.Add(entity.Id);
                else if (missingIds.Count > 0 && kind is "audio" or "text") refresh.Add(entity.Id);
            }
            await plan.AddAsync("files", missingFiles, ct);
            await plan.AddAsync(kind, orphans, ct);
            if (refresh.Count > 0) await plan.AddAsync($"refresh-{kind}", refresh, ct);
            checkedCount += batch.Count;
            progress.Report((double)checkedCount / Math.Max(total, 1), $"Checking {kind} records ({checkedCount}/{total})");
        }
    }

    private sealed class OrphanRemoval(int total)
    {
        public int Total { get; } = total;
        public int Removed { get; set; }
        public int Failed { get; set; }
        public int Processed { get; set; }
        public BulkDeletionExecutionContext Context { get; } = new();
    }

    private async Task DeleteOrphansAsync(CleanPlan plan, string kind, BulkDeletionEntityKind entityKind,
        OrphanRemoval removal, IJobProgress progress, CancellationToken ct)
    {
        await foreach (var ids in plan.ReadAsync(kind, ct))
        {
            // A fresh scope per batch keeps the change tracker bounded across large cleans.
            using var scope = scopeFactory.CreateScope();
            var deletion = scope.ServiceProvider.GetRequiredService<BulkEntityDeletionService>();
            foreach (var id in ids)
            {
                // Cancel only between entities: each deletion runs its post-commit cleanup to completion.
                ct.ThrowIfCancellationRequested();
                try
                {
                    // The files are already gone from disk, but their generated files are now useless.
                    // False means the entity was removed concurrently since inspection.
                    if (await deletion.DeleteAsync(entityKind, id, removal.Context, deleteFiles: false, deleteGenerated: true, CancellationToken.None))
                        removal.Removed++;
                }
                catch (Exception ex)
                {
                    removal.Failed++;
                    logger.LogWarning(ex, "Clean could not remove orphaned {Kind} {Id}", kind, id);
                    // Keep the failed entity's file rows so the next clean still classifies it as an orphan
                    // (a fileless entity is never an orphan in a path-scoped clean).
                    using var lookupScope = scopeFactory.CreateScope();
                    var lookupDb = lookupScope.ServiceProvider.GetRequiredService<CoveContext>();
                    await plan.RemoveAsync("files", await OwnedFileIds(lookupDb, entityKind, id).ToListAsync(ct), ct);
                }
                removal.Processed++;
                progress.Report((double)removal.Processed / Math.Max(removal.Total, 1),
                    $"Removing orphaned items ({removal.Processed}/{removal.Total})");
            }
        }
    }

    private static IQueryable<int> OwnedFileIds(CoveContext db, BulkDeletionEntityKind kind, int id) => kind switch
    {
        BulkDeletionEntityKind.Video => db.VideoFiles.AsNoTracking().Where(file => file.VideoId == id).Select(file => file.Id),
        BulkDeletionEntityKind.Image => db.ImageFiles.AsNoTracking().Where(file => file.ImageId == id).Select(file => file.Id),
        BulkDeletionEntityKind.Gallery => db.GalleryFiles.AsNoTracking().Where(file => file.GalleryId == id).Select(file => file.Id),
        BulkDeletionEntityKind.Audio => db.AudioFiles.AsNoTracking().Where(file => file.AudioId == id).Select(file => file.Id),
        BulkDeletionEntityKind.Text => db.TextFiles.AsNoTracking().Where(file => file.TextDocumentId == id).Select(file => file.Id),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static async Task<int> DeleteDanglingAsync<TFile>(IQueryable<TFile> query, IReadOnlyList<string> paths, CancellationToken ct)
        where TFile : BaseFileEntity
    {
        if (paths.Count == 0) return await query.ExecuteDeleteAsync(ct);
        var deleted = 0;
        int? afterId = null;
        while (true)
        {
            var page = await query.AsNoTracking().Where(file => afterId == null || file.Id > afterId)
                .OrderBy(file => file.Id).Take(CleanPlan.BatchSize).Select(file => new { file.Id, file.Path }).ToListAsync(ct);
            if (page.Count == 0) return deleted;
            afterId = page[^1].Id;
            var ids = page.Where(file => GeneratePathFilter.Contains(file.Path, paths)).Select(file => file.Id).ToArray();
            if (ids.Length > 0) deleted += await query.Where(file => ids.Contains(file.Id)).ExecuteDeleteAsync(ct);
        }
    }
}
