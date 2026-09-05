using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Core.Entities.Galleries.Zip;
using Cove.Data;

namespace Cove.Api.Services;

internal sealed class ScanGalleryProcessor(
    ZipGalleryReader zipGalleryReader,
    ScanFolderResolver folderResolver,
    ILogger logger)
{
    internal async Task<Gallery> ProcessAsync(
        CoveContext db,
        string path,
        int? galleryId,
        CancellationToken ct,
        FileStat? fileStat = null,
        Dictionary<string, Folder>? folderCache = null,
        int? parentFolderId = null,
        IReadOnlyList<ZipEntryInfo>? prevalidatedEntries = null,
        bool contentChanged = false)
    {
        var stat = fileStat ?? ScanPath.GetFileStat(path);
        var dirPath = ScanPath.NormalizeStoredFolderPath(Path.GetDirectoryName(path) ?? path);
        var folderId = parentFolderId ?? (await folderResolver.EnsureAsync(db, dirPath, ct, folderCache)).Id;

        var basename = Path.GetFileName(path);
        var existing = await db.Set<GalleryFile>()
            .Include(gf => gf.Gallery)
            .ThenInclude(g => g!.ImageGalleries)
            .ThenInclude(ig => ig.Image)
            .ThenInclude(image => image!.Files)
            .FirstOrDefaultAsync(f => f.ParentFolderId == folderId && f.Basename == basename, ct);

        // Consult entities added but not yet saved in this batch to avoid violating the unique
        // (ParentFolderId, Basename) index when a file is enumerated twice in one pass.
        existing ??= db.Set<GalleryFile>().Local.FirstOrDefault(f => f.ParentFolderId == folderId && f.Basename == basename);
        var existingArchiveFiles = existing == null || existing.Id == 0
            ? new List<ImageFile>()
            : await db.ImageFiles
                .Include(file => file.Image)
                .ThenInclude(image => image!.Files)
                .Where(file => file.ZipFileId == existing.Id)
                .ToListAsync(ct);

        // A forced rescan of an unchanged archive is metadata-only. Confirmed content changes,
        // however, must replace the images derived from the previous archive.
        if (!contentChanged && existingArchiveFiles.Count > 0)
        {
            logger.LogTrace("Gallery already processed with {ImageCount} images: {Path}", existingArchiveFiles.Count, path);
            return existing!.Gallery!;
        }

        // Create or update the gallery file entry
        GalleryFile galleryFile;
        Gallery gallery;

        if (existing != null)
        {
            // Update existing file metadata
            galleryFile = existing;
            galleryFile.Size = stat.Size;
            galleryFile.ModTime = stat.ModTime;
            gallery = existing.Gallery!;
        }
        else
        {
            galleryFile = new GalleryFile
            {
                Basename = basename,
                ParentFolderId = folderId,
                Size = stat.Size,
                ModTime = stat.ModTime
            };

            if (galleryId.HasValue)
            {
                gallery = await db.Galleries
                    .Include(item => item.Files)
                    .Include(item => item.ImageGalleries)
                    .FirstOrDefaultAsync(item => item.Id == galleryId.Value, ct)
                    ?? throw new InvalidOperationException($"Gallery {galleryId.Value} was not found for downloaded media import");

                if (string.IsNullOrWhiteSpace(gallery.Title))
                    gallery.Title = Path.GetFileNameWithoutExtension(path);

                gallery.Files.Add(galleryFile);
            }
            else
            {
                // Intentionally leave Title null on scan. Storing the filename as the title makes it
                // impossible to filter for galleries that have no real title; the UI falls back to the
                // file basename for display when Title is null.
                gallery = new Gallery
                {
                    Files = [galleryFile]
                };

                db.Galleries.Add(gallery);
            }
        }

        try
        {
            // Get all images from the zip, sorted by path
            var imageEntries = prevalidatedEntries?.ToList()
                ?? await zipGalleryReader.GetImageEntriesAsync(path, ct);

            if (imageEntries.Count == 0)
            {
                logger.LogWarning("No images found in gallery zip: {Path}", path);
                return gallery;
            }

            // Wonky zips can contain multiple entries with identical internal paths. Every
            // image in a gallery shares one virtual folder, so duplicate names collide on the
            // (ParentFolderId, Basename) unique constraint and fail the entire gallery insert.
            // Keep the first occurrence of each name (case-sensitive, matching Postgres text).
            var distinctEntries = imageEntries
                .GroupBy(entry => entry.FullName, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            if (distinctEntries.Count != imageEntries.Count)
                logger.LogWarning(
                    "Gallery zip contained {DuplicateCount} duplicate entry name(s); keeping one of each: {Path}",
                    imageEntries.Count - distinctEntries.Count,
                    path);

            // A readable central directory does not prove compressed entry payloads are extractable.
            // Preflight only new and confirmed-changed archives, before mutating the existing gallery.
            foreach (var entry in distinctEntries)
            {
                await using var payload = await zipGalleryReader.ExtractEntryAsync(path, entry.FullName, ct);
            }

            logger.LogTrace("Found {ImageCount} images in gallery: {Path}", distinctEntries.Count, path);

            await using var transaction = existing != null && contentChanged && db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(ct)
                : null;

            if (existing != null && contentChanged)
            {
                var derivedImages = existingArchiveFiles
                    .Select(file => file.Image!)
                    .DistinctBy(image => image.Id)
                    .ToList();
                var imagesByEntryName = derivedImages
                    .Select(image => (Image: image, File: image.Files.Single(file => file.ZipFileId == galleryFile.Id)))
                    .GroupBy(item => item.File.Basename, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                var replacementNames = distinctEntries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
                var removedImages = derivedImages
                    .Where(image => image.Files.Any(file => file.ZipFileId == galleryFile.Id && !replacementNames.Contains(file.Basename)))
                    .ToList();
                db.ImageFiles.RemoveRange(removedImages.SelectMany(image => image.Files));
                db.Images.RemoveRange(removedImages);

                foreach (var entry in distinctEntries)
                {
                    if (!imagesByEntryName.TryGetValue(entry.FullName, out var matched))
                        continue;

                    matched.File.Size = entry.Length;
                    var entryModTime = ScanPath.NormalizeFileModTime(entry.LastWriteTime.UtcDateTime);
                    matched.File.ModTime = entryModTime > matched.File.ModTime
                        ? entryModTime
                        : matched.File.ModTime.AddSeconds(2);
                    matched.File.Format = Path.GetExtension(entry.Name).TrimStart('.').ToLowerInvariant();
                }
            }
            else
            {
                // New gallery files need an ID before their derived ImageFiles can reference it. Existing
                // empty galleries also keep the established two-save flow so authorization-backed derived
                // counts can observe the newly persisted image before their relationship is summarized.
                await db.SaveChangesAsync(ct);
            }

            // Create a virtual folder for this zip's contents
            // This ensures images from different zips don't conflict on the unique constraint (ParentFolderId + Basename)
            var virtualFolderPath = $"{path}#virtual";
            var virtualFolder = await db.Folders.FirstOrDefaultAsync(f => f.Path == virtualFolderPath, ct);
            if (virtualFolder == null)
            {
                virtualFolder = new Folder { Path = virtualFolderPath };
                db.Folders.Add(virtualFolder);
                await db.SaveChangesAsync(ct);
            }

            // Create Image entities for each image in the zip
            foreach (var entry in distinctEntries)
            {
                if (existing != null && contentChanged && existingArchiveFiles.Any(file => file.Basename == entry.FullName))
                    continue;

                // Create ImageFile record representing the image within the zip
                // Use FullName to preserve the internal zip path structure and avoid duplicate basenames
                var imageFile = new ImageFile
                {
                    Basename = entry.FullName,  // Use full internal path to avoid collisions
                    ParentFolderId = virtualFolder.Id,  // Use virtual folder specific to this zip
                    ZipFileId = galleryFile.Id,  // Link to parent zip file
                    Size = entry.Length,
                    ModTime = ScanPath.NormalizeFileModTime(entry.LastWriteTime.UtcDateTime),
                    Format = Path.GetExtension(entry.Name).TrimStart('.').ToLowerInvariant(),
                    // TODO: Extract dimensions using image processing library
                    Width = 0,
                    Height = 0
                };

                // Create Image entity
                var image = new Image
                {
                    Title = Path.GetFileNameWithoutExtension(entry.Name),
                    Files = [imageFile]
                };

                db.Images.Add(image);

                // Link image to gallery via junction table
                // Note: We'll add this after the image is saved and has an ID
                gallery.ImageGalleries.Add(new ImageGallery
                {
                    Image = image,
                    Gallery = gallery
                });
            }

            // Save all images and their gallery associations
            await db.SaveChangesAsync(ct);
            if (transaction != null)
                await transaction.CommitAsync(ct);

            logger.LogTrace("Added gallery with {ImageCount} images: {Path}", distinctEntries.Count, path);
        }
        catch (FileNotFoundException)
        {
            logger.LogError("Zip file not found (may have been moved/deleted): {Path}", path);
            db.ChangeTracker.Clear();
            throw;
        }
        catch (InvalidDataException ex)
        {
            logger.LogError("Invalid or corrupt zip file: {Path} - {Error}", path, ex.Message);
            db.ChangeTracker.Clear();
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing gallery zip file: {Path}", path);

            // Discard failed tracked state after the transaction rolls back so the caller's next
            // SaveChanges cannot retry it, then propagate the failure to the scan job.
            db.ChangeTracker.Clear();
            throw;
        }

        return gallery;
    }
}
