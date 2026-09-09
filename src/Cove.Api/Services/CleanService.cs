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
            var pruned = 0;
            await foreach (var ids in plan.ReadAsync("files", ct))
                pruned += await db.Set<BaseFileEntity>().Where(file => ids.Contains(file.Id)).ExecuteDeleteAsync(ct);
            await DeleteParentsAsync(plan, "video", db.Videos, ids => db.VideoFiles.Where(file => file.VideoId.HasValue && ids.Contains(file.VideoId.Value)), ct);
            await DeleteParentsAsync(plan, "image", db.Images, ids => db.ImageFiles.Where(file => file.ImageId.HasValue && ids.Contains(file.ImageId.Value)), ct);
            await DeleteParentsAsync(plan, "gallery", db.Galleries, ids => db.GalleryFiles.Where(file => file.GalleryId.HasValue && ids.Contains(file.GalleryId.Value)), ct);
            await DeleteParentsAsync(plan, "audio", db.Audios, ids => db.AudioFiles.Where(file => file.AudioId.HasValue && ids.Contains(file.AudioId.Value)), ct);
            await DeleteParentsAsync(plan, "text", db.TextDocuments, ids => db.TextFiles.Where(file => file.TextDocumentId.HasValue && ids.Contains(file.TextDocumentId.Value)), ct);

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
            if (pruned + videos + images + galleries + audios + texts + dangling > 0)
            {
                progress.Report(1d, "Recomputing library counts");
                recomputed = await db.RecomputeAllDerivedCountsAsync(cancellationToken: ct);
            }
            logger.LogInformation("Clean completed: removed {Files} missing files, {Videos} videos, {Images} images, {Galleries} galleries, {Audios} audios, {Texts} texts, {DanglingFiles} dangling files; recomputed {Recomputed} entity counts",
                pruned, videos, images, galleries, audios, texts, dangling, recomputed);
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

    private static async Task DeleteParentsAsync<TEntity, TFile>(CleanPlan plan, string kind,
        IQueryable<TEntity> query, Func<int[], IQueryable<TFile>> files, CancellationToken ct)
        where TEntity : BaseEntity where TFile : BaseFileEntity
    {
        await foreach (var ids in plan.ReadAsync(kind, ct))
        {
            // Parent cascades use SetNull for files, so remove owned files first.
            await files(ids).ExecuteDeleteAsync(ct);
            await query.Where(entity => ids.Contains(entity.Id)).ExecuteDeleteAsync(ct);
        }
    }

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
