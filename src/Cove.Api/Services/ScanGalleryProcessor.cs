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
        IReadOnlyList<ZipEntryInfo>? prevalidatedEntries = null)
    {
        var stat = fileStat ?? ScanPath.GetFileStat(path);
        var dirPath = ScanPath.NormalizeStoredFolderPath(Path.GetDirectoryName(path) ?? path);
        var folderId = parentFolderId ?? (await folderResolver.EnsureAsync(db, dirPath, ct, folderCache)).Id;

        var basename = Path.GetFileName(path);
        var existing = await db.Set<GalleryFile>()
            .Include(gf => gf.Gallery)
            .ThenInclude(g => g!.ImageGalleries)
            .FirstOrDefaultAsync(f => f.ParentFolderId == folderId && f.Basename == basename, ct);

        // Consult entities added but not yet saved in this batch to avoid violating the unique
        // (ParentFolderId, Basename) index when a file is enumerated twice in one pass.
        existing ??= db.Set<GalleryFile>().Local.FirstOrDefault(f => f.ParentFolderId == folderId && f.Basename == basename);

        // If gallery exists and already has images, skip re-processing
        if (existing?.Gallery?.ImageGalleries.Count > 0)
        {
            logger.LogTrace("Gallery already processed with {ImageCount} images: {Path}", existing.Gallery.ImageGalleries.Count, path);
            return existing.Gallery;
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

        // Save to get the GalleryFile ID (needed for ZipFileId on images)
        await db.SaveChangesAsync(ct);

        // Now extract images from the zip file
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

            logger.LogTrace("Found {ImageCount} images in gallery: {Path}", distinctEntries.Count, path);

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

            logger.LogTrace("Added gallery with {ImageCount} images: {Path}", distinctEntries.Count, path);
        }
        catch (FileNotFoundException)
        {
            logger.LogError("Zip file not found (may have been moved/deleted): {Path}", path);
        }
        catch (InvalidDataException ex)
        {
            logger.LogError("Invalid or corrupt zip file: {Path} - {Error}", path, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing gallery zip file: {Path}", path);

            // Discard any image rows that failed to persist so the caller's next SaveChanges
            // doesn't retry them and surface the same error a second time. The gallery row
            // itself was already committed above, so it survives (as an empty gallery).
            db.ChangeTracker.Clear();
        }

        return gallery;
    }
}
