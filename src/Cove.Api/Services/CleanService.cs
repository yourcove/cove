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
    private sealed record CleanFileInfo(string Path, int? ZipFileId);
    private sealed record CleanFolderInfo(string Path, int? ZipFileId);
    private sealed record CleanEntity(int Id, List<CleanFileInfo> Files);
    private sealed record CleanGallery(int Id, CleanFolderInfo? Folder, List<CleanFileInfo> Files);

    public string StartClean(bool dryRun = false)
    {
        return jobService.Enqueue("clean", dryRun ? "Cleaning (dry run)" : "Cleaning library", async (progress, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

            // Load only the fields needed for existence checks (Path is a stored, indexed column, so
            // ParentFolder is not required), untracked and projected. Loading full tracked entities for
            // every video/image/gallery + their files exhausts memory and can OOM the job on large
            // libraries (hundreds of thousands of rows), leaving orphans uncleaned.
            var videos = await db.Videos.AsNoTracking()
                .Select(v => new CleanEntity(v.Id, v.Files.Select(f => new CleanFileInfo(f.Path, f.ZipFileId)).ToList()))
                .ToListAsync(ct);

            var images = await db.Images.AsNoTracking()
                .Select(i => new CleanEntity(i.Id, i.Files.Select(f => new CleanFileInfo(f.Path, f.ZipFileId)).ToList()))
                .ToListAsync(ct);

            // Zip-based galleries have FolderId == null (their content lives in a .zip GalleryFile), so
            // both the backing folder AND the files are needed to decide orphanhood.
            var galleries = await db.Galleries.AsNoTracking()
                .Select(g => new CleanGallery(
                    g.Id,
                    g.Folder == null ? null : new CleanFolderInfo(g.Folder.Path, g.Folder.ZipFileId),
                    g.Files.Select(f => new CleanFileInfo(f.Path, f.ZipFileId)).ToList()))
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
            foreach (var g in galleries)
                if (g.Folder?.ZipFileId is int folderZipId) zipFileIds.Add(folderZipId);

            var existingZipFileIds = new HashSet<int>();
            if (zipFileIds.Count > 0)
            {
                var zipFiles = await db.Set<BaseFileEntity>()
                    .AsNoTracking()
                    .Where(f => zipFileIds.Contains(f.Id))
                    .Select(f => new { f.Id, f.Path })
                    .ToListAsync(ct);

                foreach (var zip in zipFiles)
                    if (PathExists(zip.Path))
                        existingZipFileIds.Add(zip.Id);
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

            // An item is orphaned when it has no files at all, or none of its files exist.
            // (Previously only Files.FirstOrDefault() was checked, which mis-handled
            // multi-file items in both directions.)
            static bool AllMissing(IEnumerable<CleanFileInfo> files, Func<CleanFileInfo, bool> exists)
                => !files.Any(exists);

            var orphanVideoIds = new List<int>();
            int total = videos.Count;
            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (AllMissing(videos[i].Files, FileExists))
                    orphanVideoIds.Add(videos[i].Id);
                progress.Report((double)(i + 1) / Math.Max(total, 1), $"Checking ({i + 1}/{total})");
            }

            var orphanImageIds = new List<int>();
            foreach (var img in images)
            {
                ct.ThrowIfCancellationRequested();
                if (AllMissing(img.Files, FileExists))
                    orphanImageIds.Add(img.Id);
            }

            var orphanGalleryIds = new List<int>();
            foreach (var gallery in galleries)
            {
                ct.ThrowIfCancellationRequested();

                bool orphan;
                if (gallery.Folder != null)
                {
                    // Folder-backed gallery (loose images in a directory, or a zip-virtual folder).
                    orphan = !FolderExists(gallery.Folder);
                }
                else if (gallery.Files.Count > 0)
                {
                    // File-backed gallery (e.g. a .zip): orphaned when none of its files remain.
                    orphan = gallery.Files.All(f => !FileExists(f));
                }
                else
                {
                    // Metadata-only gallery with no folder and no files: leave it alone.
                    orphan = false;
                }

                if (orphan)
                    orphanGalleryIds.Add(gallery.Id);
            }

            logger.LogInformation("Clean found {Videos} orphaned videos, {Images} orphaned images, {Galleries} orphaned galleries",
                orphanVideoIds.Count, orphanImageIds.Count, orphanGalleryIds.Count);

            if (dryRun)
            {
                logger.LogInformation("Dry run - no changes made");
                return;
            }

            // Remove orphaned records. VideoFile/ImageFile -> parent is OnDelete(SetNull),
            // so deleting the parent alone would leave dangling file rows. Delete the files
            // first to avoid accumulating orphaned ImageFile/VideoFile rows.
            if (orphanVideoIds.Count > 0)
            {
                await db.VideoFiles.Where(f => f.VideoId != null && orphanVideoIds.Contains(f.VideoId.Value)).ExecuteDeleteAsync(ct);
                await db.Videos.Where(s => orphanVideoIds.Contains(s.Id)).ExecuteDeleteAsync(ct);
                logger.LogInformation("Removed {Count} orphaned videos", orphanVideoIds.Count);
            }

            if (orphanImageIds.Count > 0)
            {
                await db.ImageFiles.Where(f => f.ImageId != null && orphanImageIds.Contains(f.ImageId.Value)).ExecuteDeleteAsync(ct);
                await db.Images.Where(im => orphanImageIds.Contains(im.Id)).ExecuteDeleteAsync(ct);
                logger.LogInformation("Removed {Count} orphaned images", orphanImageIds.Count);
            }

            if (orphanGalleryIds.Count > 0)
            {
                await db.GalleryFiles.Where(f => f.GalleryId != null && orphanGalleryIds.Contains(f.GalleryId.Value)).ExecuteDeleteAsync(ct);
                await db.Galleries.Where(g => orphanGalleryIds.Contains(g.Id)).ExecuteDeleteAsync(ct);
                logger.LogInformation("Removed {Count} orphaned galleries", orphanGalleryIds.Count);
            }
        }, exclusive: false);
    }
}
