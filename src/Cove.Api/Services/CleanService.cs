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
    // Lightweight projections so clean never materializes (or tracks) full entities + navigation graphs.
    private sealed record CleanFileInfo(int Id, string Path, int? ZipFileId);
    private sealed record CleanFolderInfo(string Path, int? ZipFileId);
    private sealed record CleanEntity(int Id, List<CleanFileInfo> Files);
    private sealed record CleanGallery(int Id, CleanFolderInfo? Folder, List<CleanFileInfo> Files);

    public string StartClean(bool dryRun = false, IReadOnlyList<string>? paths = null)
    {
        var scopedPaths = GeneratePathFilter.Normalize(paths);
        return jobService.Enqueue("clean", dryRun ? "Cleaning (dry run)" : "Cleaning library", async (progress, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

            // Load only the fields needed for existence checks (Path is a stored, indexed column, so
            // ParentFolder is not required), untracked and projected. Loading full tracked entities for
            // every media entity + its files exhausts memory and can OOM the job on large
            // libraries (hundreds of thousands of rows), leaving orphans uncleaned.
            // Only top-level videos are file-backed. Sub-videos/clips (ParentVideoId != null) have no
            // files of their own — they reference the parent's file via a clip range — so they would
            // always look "fileless" and must NOT be treated as orphans, or clean would delete every clip.
            var videos = await db.Videos.AsNoTracking()
                .Where(v => v.ParentVideoId == null)
                .Select(v => new CleanEntity(v.Id, v.Files.Select(f => new CleanFileInfo(f.Id, f.Path, f.ZipFileId)).ToList()))
                .ToListAsync(ct);

            var images = await db.Images.AsNoTracking()
                .Select(i => new CleanEntity(i.Id, i.Files.Select(f => new CleanFileInfo(f.Id, f.Path, f.ZipFileId)).ToList()))
                .ToListAsync(ct);

            var audios = await db.Audios.AsNoTracking()
                .Select(a => new CleanEntity(a.Id, a.Files.Select(f => new CleanFileInfo(f.Id, f.Path, f.ZipFileId)).ToList()))
                .ToListAsync(ct);

            var texts = await db.TextDocuments.AsNoTracking()
                .Select(t => new CleanEntity(t.Id, t.Files.Select(f => new CleanFileInfo(f.Id, f.Path, f.ZipFileId)).ToList()))
                .ToListAsync(ct);

            // Zip-based galleries have FolderId == null (their content lives in a .zip GalleryFile), so
            // both the backing folder AND the files are needed to decide orphanhood.
            var galleries = await db.Galleries.AsNoTracking()
                .Select(g => new CleanGallery(
                    g.Id,
                    g.Folder == null ? null : new CleanFolderInfo(g.Folder.Path, g.Folder.ZipFileId),
                    g.Files.Select(f => new CleanFileInfo(f.Id, f.Path, f.ZipFileId)).ToList()))
                .ToListAsync(ct);

            // Build an existence map for zip archives referenced by zip-backed files/folders.
            // Files inside a gallery zip carry ZipFileId pointing at the .zip's GalleryFile row;
            // they exist on disk only if that archive still exists. Resolving this is the core
            // fix: previously any ZipFileId.HasValue file was assumed to exist unconditionally.
            var pathExists = new Dictionary<string, bool>(StringComparer.Ordinal);
            bool PathExists(string path)
            {
                if (string.IsNullOrEmpty(path)) return false;
                if (!pathExists.TryGetValue(path, out var exists))
                {
                    exists = File.Exists(path);
                    pathExists[path] = exists;
                }
                return exists;
            }

            var zipFileIds = new HashSet<int>();
            foreach (var v in videos)
                foreach (var f in v.Files)
                    if (f.ZipFileId.HasValue) zipFileIds.Add(f.ZipFileId.Value);
            foreach (var im in images)
                foreach (var f in im.Files)
                    if (f.ZipFileId.HasValue) zipFileIds.Add(f.ZipFileId.Value);
            foreach (var audio in audios)
                foreach (var f in audio.Files)
                    if (f.ZipFileId.HasValue) zipFileIds.Add(f.ZipFileId.Value);
            foreach (var text in texts)
                foreach (var f in text.Files)
                    if (f.ZipFileId.HasValue) zipFileIds.Add(f.ZipFileId.Value);
            foreach (var g in galleries)
                if (g.Folder?.ZipFileId is int folderZipId) zipFileIds.Add(folderZipId);

            var existingZipFileIds = new HashSet<int>();
            var zipFilePaths = new Dictionary<int, string>();
            if (zipFileIds.Count > 0)
            {
                var zipFiles = await db.Set<BaseFileEntity>()
                    .AsNoTracking()
                    .Where(f => zipFileIds.Contains(f.Id))
                    .Select(f => new { f.Id, f.Path })
                    .ToListAsync(ct);

                foreach (var zip in zipFiles)
                {
                    zipFilePaths[zip.Id] = zip.Path;
                    if (PathExists(zip.Path))
                        existingZipFileIds.Add(zip.Id);
                }
            }

            bool FileExists(CleanFileInfo file)
            {
                // Zip-backed entry: exists only if its containing archive still exists on disk.
                if (file.ZipFileId.HasValue)
                    return existingZipFileIds.Contains(file.ZipFileId.Value);

                return PathExists(file.Path);
            }

            bool FolderExists(CleanFolderInfo folder)
            {
                // A zip-virtual folder exists only while its archive does.
                if (folder.ZipFileId.HasValue)
                    return existingZipFileIds.Contains(folder.ZipFileId.Value);

                return Directory.Exists(folder.Path);
            }

            bool InScope(CleanFileInfo file)
            {
                var physicalPath = file.ZipFileId is int zipFileId && zipFilePaths.TryGetValue(zipFileId, out var zipPath)
                    ? zipPath
                    : file.Path;
                return GeneratePathFilter.Contains(physicalPath, scopedPaths);
            }

            bool FolderInScope(CleanFolderInfo folder)
            {
                var physicalPath = folder.ZipFileId is int zipFileId && zipFilePaths.TryGetValue(zipFileId, out var zipPath)
                    ? zipPath
                    : folder.Path;
                return GeneratePathFilter.Contains(physicalPath, scopedPaths);
            }

            var missingVideoFileIds = videos.SelectMany(video => video.Files)
                .Where(file => InScope(file) && !FileExists(file))
                .Select(file => file.Id)
                .ToHashSet();
            var missingImageFileIds = images.SelectMany(image => image.Files)
                .Where(file => InScope(file) && !FileExists(file))
                .Select(file => file.Id)
                .ToHashSet();
            var missingAudioFileIds = audios.SelectMany(audio => audio.Files)
                .Where(file => InScope(file) && !FileExists(file))
                .Select(file => file.Id)
                .ToHashSet();
            var missingTextFileIds = texts.SelectMany(text => text.Files)
                .Where(file => InScope(file) && !FileExists(file))
                .Select(file => file.Id)
                .ToHashSet();
            var missingGalleryFileIds = galleries.SelectMany(gallery => gallery.Files)
                .Where(file => InScope(file) && !FileExists(file))
                .Select(file => file.Id)
                .ToHashSet();

            var orphanVideoIds = new List<int>();
            int total = videos.Count;
            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                if ((scopedPaths.Count == 0 || videos[i].Files.Any(file => missingVideoFileIds.Contains(file.Id)))
                    && videos[i].Files.All(file => missingVideoFileIds.Contains(file.Id)))
                    orphanVideoIds.Add(videos[i].Id);
                progress.Report((double)(i + 1) / Math.Max(total, 1), $"Checking ({i + 1}/{total})");
            }

            var orphanImageIds = new List<int>();
            foreach (var img in images)
            {
                ct.ThrowIfCancellationRequested();
                if ((scopedPaths.Count == 0 || img.Files.Any(file => missingImageFileIds.Contains(file.Id)))
                    && img.Files.All(file => missingImageFileIds.Contains(file.Id)))
                    orphanImageIds.Add(img.Id);
            }

            ct.ThrowIfCancellationRequested();
            var orphanAudioIds = audios
                .Where(audio => (scopedPaths.Count == 0 || audio.Files.Any(file => missingAudioFileIds.Contains(file.Id)))
                    && audio.Files.All(file => missingAudioFileIds.Contains(file.Id)))
                .Select(audio => audio.Id)
                .ToList();
            ct.ThrowIfCancellationRequested();
            var orphanTextIds = texts
                .Where(text => (scopedPaths.Count == 0 || text.Files.Any(file => missingTextFileIds.Contains(file.Id)))
                    && text.Files.All(file => missingTextFileIds.Contains(file.Id)))
                .Select(text => text.Id)
                .ToList();

            var orphanGalleryIds = new List<int>();
            foreach (var gallery in galleries)
            {
                ct.ThrowIfCancellationRequested();

                bool orphan;
                if (gallery.Folder != null)
                {
                    // Folder-backed gallery (loose images in a directory, or a zip-virtual folder).
                    orphan = FolderInScope(gallery.Folder) && !FolderExists(gallery.Folder);
                }
                else if (gallery.Files.Count > 0)
                {
                    // File-backed gallery (e.g. a .zip): orphaned when none of its files remain.
                    orphan = gallery.Files.Any(file => missingGalleryFileIds.Contains(file.Id))
                        && gallery.Files.All(file => missingGalleryFileIds.Contains(file.Id));
                }
                else
                {
                    // Metadata-only gallery with no folder and no files: leave it alone.
                    orphan = false;
                }

                if (orphan)
                    orphanGalleryIds.Add(gallery.Id);
            }

            var missingFiles = missingVideoFileIds.Count + missingImageFileIds.Count + missingGalleryFileIds.Count
                + missingAudioFileIds.Count + missingTextFileIds.Count;
            logger.LogInformation("Clean found {MissingFiles} missing files, {Videos} orphaned videos, {Images} orphaned images, {Galleries} orphaned galleries, {Audios} orphaned audios, {Texts} orphaned texts",
                missingFiles, orphanVideoIds.Count, orphanImageIds.Count, orphanGalleryIds.Count, orphanAudioIds.Count, orphanTextIds.Count);

            if (dryRun)
            {
                logger.LogInformation("Dry run - no changes made");
                return;
            }

            // Remove orphaned records. VideoFile/ImageFile -> parent is OnDelete(SetNull),
            // so deleting the parent alone would leave dangling file rows. Delete the files
            // first to avoid accumulating orphaned ImageFile/VideoFile rows.
            var prunedFiles = 0;
            if (missingVideoFileIds.Count > 0)
                prunedFiles += await db.VideoFiles.Where(file => missingVideoFileIds.Contains(file.Id)).ExecuteDeleteAsync(ct);
            if (missingImageFileIds.Count > 0)
                prunedFiles += await db.ImageFiles.Where(file => missingImageFileIds.Contains(file.Id)).ExecuteDeleteAsync(ct);
            if (missingGalleryFileIds.Count > 0)
                prunedFiles += await db.GalleryFiles.Where(file => missingGalleryFileIds.Contains(file.Id)).ExecuteDeleteAsync(ct);
            if (missingAudioFileIds.Count > 0)
                prunedFiles += await db.AudioFiles.Where(file => missingAudioFileIds.Contains(file.Id)).ExecuteDeleteAsync(ct);
            if (missingTextFileIds.Count > 0)
                prunedFiles += await db.TextFiles.Where(file => missingTextFileIds.Contains(file.Id)).ExecuteDeleteAsync(ct);

            if (orphanVideoIds.Count > 0)
            {
                await db.VideoFiles.Where(f => f.VideoId != null && orphanVideoIds.Contains(f.VideoId.Value)).ExecuteDeleteAsync(ct);
                await db.Videos.Where(s => orphanVideoIds.Contains(s.Id)).ExecuteDeleteAsync(ct);
                logger.LogDebug("Removed {Count} orphaned videos", orphanVideoIds.Count);
            }

            if (orphanImageIds.Count > 0)
            {
                await db.ImageFiles.Where(f => f.ImageId != null && orphanImageIds.Contains(f.ImageId.Value)).ExecuteDeleteAsync(ct);
                await db.Images.Where(im => orphanImageIds.Contains(im.Id)).ExecuteDeleteAsync(ct);
                logger.LogDebug("Removed {Count} orphaned images", orphanImageIds.Count);
            }

            if (orphanGalleryIds.Count > 0)
            {
                await db.GalleryFiles.Where(f => f.GalleryId != null && orphanGalleryIds.Contains(f.GalleryId.Value)).ExecuteDeleteAsync(ct);
                await db.Galleries.Where(g => orphanGalleryIds.Contains(g.Id)).ExecuteDeleteAsync(ct);
                logger.LogDebug("Removed {Count} orphaned galleries", orphanGalleryIds.Count);
            }

            if (orphanAudioIds.Count > 0)
            {
                await db.AudioFiles.Where(file => file.AudioId != null && orphanAudioIds.Contains(file.AudioId.Value)).ExecuteDeleteAsync(ct);
                await db.Audios.Where(audio => orphanAudioIds.Contains(audio.Id)).ExecuteDeleteAsync(ct);
                logger.LogDebug("Removed {Count} orphaned audios", orphanAudioIds.Count);
            }

            if (orphanTextIds.Count > 0)
            {
                await db.TextFiles.Where(file => file.TextDocumentId != null && orphanTextIds.Contains(file.TextDocumentId.Value)).ExecuteDeleteAsync(ct);
                await db.TextDocuments.Where(text => orphanTextIds.Contains(text.Id)).ExecuteDeleteAsync(ct);
                logger.LogDebug("Removed {Count} orphaned texts", orphanTextIds.Count);
            }

            var affectedAudioIds = audios
                .Where(audio => audio.Files.Any(file => missingAudioFileIds.Contains(file.Id)) && !orphanAudioIds.Contains(audio.Id))
                .Select(audio => audio.Id)
                .ToList();
            if (affectedAudioIds.Count > 0)
            {
                var affectedAudios = await db.Audios.Include(audio => audio.Files)
                    .Where(audio => affectedAudioIds.Contains(audio.Id))
                    .ToListAsync(ct);
                foreach (var audio in affectedAudios)
                    ScanAudioProcessor.RefreshAudioSummary(audio);
            }

            var affectedTextIds = texts
                .Where(text => text.Files.Any(file => missingTextFileIds.Contains(file.Id)) && !orphanTextIds.Contains(text.Id))
                .Select(text => text.Id)
                .ToList();
            if (affectedTextIds.Count > 0)
            {
                var affectedTexts = await db.TextDocuments.Include(text => text.Files)
                    .Where(text => affectedTextIds.Contains(text.Id))
                    .ToListAsync(ct);
                foreach (var text in affectedTexts)
                    ScanTextProcessor.RefreshTextSummary(text);
            }

            if (affectedAudioIds.Count > 0 || affectedTextIds.Count > 0)
                await db.SaveChangesAsync(ct);

            var deletedAny = prunedFiles > 0 || orphanVideoIds.Count > 0 || orphanImageIds.Count > 0 || orphanGalleryIds.Count > 0
                || orphanAudioIds.Count > 0 || orphanTextIds.Count > 0;

            // Sweep file rows that were detached from their parent by historical SetNull cascades
            // (parent row deleted, file row left behind with a null FK). These accumulate invisibly,
            // are never matched by the FK-scoped deletes above, and keep stale entries around.
            var danglingFiles = 0;
            if (scopedPaths.Count == 0)
            {
                danglingFiles += await db.VideoFiles.Where(f => f.VideoId == null).ExecuteDeleteAsync(ct);
                danglingFiles += await db.ImageFiles.Where(f => f.ImageId == null).ExecuteDeleteAsync(ct);
                danglingFiles += await db.GalleryFiles.Where(f => f.GalleryId == null).ExecuteDeleteAsync(ct);
                danglingFiles += await db.AudioFiles.Where(f => f.AudioId == null).ExecuteDeleteAsync(ct);
                danglingFiles += await db.TextFiles.Where(f => f.TextDocumentId == null).ExecuteDeleteAsync(ct);
            }
            else
            {
                var danglingVideoIds = await db.VideoFiles.AsNoTracking()
                    .Where(file => file.VideoId == null)
                    .Select(file => new { file.Id, file.Path })
                    .ToListAsync(ct);
                var danglingImageIds = await db.ImageFiles.AsNoTracking()
                    .Where(file => file.ImageId == null)
                    .Select(file => new { file.Id, file.Path })
                    .ToListAsync(ct);
                var danglingGalleryIds = await db.GalleryFiles.AsNoTracking()
                    .Where(file => file.GalleryId == null)
                    .Select(file => new { file.Id, file.Path })
                    .ToListAsync(ct);
                var danglingAudioIds = await db.AudioFiles.AsNoTracking()
                    .Where(file => file.AudioId == null)
                    .Select(file => new { file.Id, file.Path })
                    .ToListAsync(ct);
                var danglingTextIds = await db.TextFiles.AsNoTracking()
                    .Where(file => file.TextDocumentId == null)
                    .Select(file => new { file.Id, file.Path })
                    .ToListAsync(ct);
                var scopedDanglingVideoIds = danglingVideoIds.Where(file => GeneratePathFilter.Contains(file.Path, scopedPaths)).Select(file => file.Id).ToList();
                var scopedDanglingImageIds = danglingImageIds.Where(file => GeneratePathFilter.Contains(file.Path, scopedPaths)).Select(file => file.Id).ToList();
                var scopedDanglingGalleryIds = danglingGalleryIds.Where(file => GeneratePathFilter.Contains(file.Path, scopedPaths)).Select(file => file.Id).ToList();
                var scopedDanglingAudioIds = danglingAudioIds.Where(file => GeneratePathFilter.Contains(file.Path, scopedPaths)).Select(file => file.Id).ToList();
                var scopedDanglingTextIds = danglingTextIds.Where(file => GeneratePathFilter.Contains(file.Path, scopedPaths)).Select(file => file.Id).ToList();
                danglingFiles += await db.VideoFiles.Where(file => scopedDanglingVideoIds.Contains(file.Id)).ExecuteDeleteAsync(ct);
                danglingFiles += await db.ImageFiles.Where(file => scopedDanglingImageIds.Contains(file.Id)).ExecuteDeleteAsync(ct);
                danglingFiles += await db.GalleryFiles.Where(file => scopedDanglingGalleryIds.Contains(file.Id)).ExecuteDeleteAsync(ct);
                danglingFiles += await db.AudioFiles.Where(file => scopedDanglingAudioIds.Contains(file.Id)).ExecuteDeleteAsync(ct);
                danglingFiles += await db.TextFiles.Where(file => scopedDanglingTextIds.Contains(file.Id)).ExecuteDeleteAsync(ct);
            }
            if (danglingFiles > 0)
            {
                deletedAny = true;
                logger.LogDebug("Removed {Count} dangling file rows with no parent", danglingFiles);
            }

            // ExecuteDeleteAsync bypasses EF's per-SaveChanges count maintenance, so the bulk deletes
            // above leave denormalized rollups stale (studio/performer/tag counts, per-entity FileCount).
            // That is what makes stats and the "0 files" filter keep reporting removed entries. Repair
            // every denormalized count so the library totals match reality after a clean.
            var recomputed = 0;
            if (deletedAny)
            {
                progress.Report(1.0, "Recomputing library counts");
                recomputed = await db.RecomputeAllDerivedCountsAsync(cancellationToken: ct);
                logger.LogDebug("Recomputed denormalized counts for {Count} entities after clean", recomputed);
            }

            logger.LogInformation("Clean completed: removed {Files} missing files, {Videos} videos, {Images} images, {Galleries} galleries, {Audios} audios, {Texts} texts, {DanglingFiles} dangling files; recomputed {Recomputed} entity counts",
                prunedFiles, orphanVideoIds.Count, orphanImageIds.Count, orphanGalleryIds.Count, orphanAudioIds.Count, orphanTextIds.Count, danglingFiles, recomputed);
        }, exclusive: false);
    }
}
