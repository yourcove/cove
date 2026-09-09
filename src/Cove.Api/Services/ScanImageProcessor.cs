using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;

namespace Cove.Api.Services;

internal sealed class ScanImageProcessor(
    CoveConfiguration config,
    IFingerprintService fingerprintService,
    IThumbnailService thumbnailService,
    ScanFolderResolver folderResolver,
    ScanFileIdentityService fileIdentity,
    ILogger logger)
{
    internal async Task<(Image Entity, bool Relinked, bool Moved)> ProcessAsync(
        CoveContext db,
        string path,
        int? imageId,
        CancellationToken ct,
        FileStat? fileStat = null,
        Dictionary<string, Folder>? folderCache = null,
        bool knownNew = false,
        int? parentFolderId = null,
        bool contentChanged = false,
        ScanOperationOptions? scanOptions = null,
        MoveDetectionIndex? moveIndex = null,
        int? validatedWidth = null,
        int? validatedHeight = null)
    {
        var stat = fileStat ?? ScanPath.GetFileStat(path);
        var dirPath = ScanPath.NormalizeStoredFolderPath(Path.GetDirectoryName(path) ?? path);
        var folderId = parentFolderId ?? (await folderResolver.EnsureAsync(db, dirPath, ct, folderCache)).Id;

        var basename = Path.GetFileName(path);
        var existing = knownNew
            ? null
            : await db.ImageFiles
                .Include(f => f.Image)
                .Include(f => f.Fingerprints)
                .FirstOrDefaultAsync(f => f.ParentFolderId == folderId && f.Basename == basename, ct);

        // Consult entities added but not yet saved in this batch to avoid violating the unique
        // (ParentFolderId, Basename) index when a file is enumerated twice in one pass.
        existing ??= db.ImageFiles.Local.FirstOrDefault(f => f.ParentFolderId == folderId && f.Basename == basename);

        if (existing != null)
        {
            existing.Size = stat.Size;
            existing.ModTime = stat.ModTime;
            ApplyValidatedDimensions(existing, validatedWidth, validatedHeight);

            if (contentChanged)
            {
                await fileIdentity.RefreshChangedFingerprintsAsync(
                    existing, path,
                    md5Enabled: config.CalculateMd5 || scanOptions?.GenerateMd5 == true,
                    moveIndex,
                    ct);
                // Drop the stale thumbnail so the generation phase rebuilds it from the new content.
                if (scanOptions?.GenerateImageThumbnails == true && existing.ImageId is int changedImageId)
                    await thumbnailService.DeleteImageGeneratedFilesAsync(changedImageId, ct);
            }

            return (existing.Image ?? throw new InvalidOperationException($"Image file {path} is not attached to an image"), false, false);
        }

        // Content already in the library: re-link a moved image, or attach a duplicate to its entity.
        if (!imageId.HasValue && moveIndex is { Enabled: true })
        {
            var (match, isMove) = await fileIdentity.MatchExistingAsync(db.ImageFiles, path, folderId, basename, stat, moveIndex, ct);
            if (match?.ImageId is int matchedImageId)
            {
                var parentImage = await db.Images.FirstOrDefaultAsync(item => item.Id == matchedImageId, ct);
                if (parentImage != null)
                {
                    if (isMove)
                    {
                        logger.LogTrace("Re-linked moved image file to {NewPath} (previously {OldPath})", path, match.Path);
                        return (parentImage, true, true);
                    }

                    var duplicateFile = new ImageFile
                    {
                        Basename = basename,
                        ParentFolderId = folderId,
                        Size = stat.Size,
                        ModTime = stat.ModTime,
                        Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                        ImageId = matchedImageId,
                    };
                    ApplyValidatedDimensions(duplicateFile, validatedWidth, validatedHeight);
                    db.ImageFiles.Add(duplicateFile);
                    await EnrichImageFileAsync(duplicateFile, path, ct, moveIndex);
                    logger.LogTrace("Attached duplicate image file {NewPath} to existing image {ImageId}", path, matchedImageId);
                    return (parentImage, true, false);
                }
            }
        }

        var imageFile = new ImageFile
        {
            Basename = basename,
            ParentFolderId = folderId,
            Size = stat.Size,
            ModTime = stat.ModTime,
            Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant()
        };
        ApplyValidatedDimensions(imageFile, validatedWidth, validatedHeight);

        Image image;
        if (imageId.HasValue)
        {
            image = await db.Images
                .Include(item => item.Files)
                .FirstOrDefaultAsync(item => item.Id == imageId.Value, ct)
                ?? throw new InvalidOperationException($"Image {imageId.Value} was not found for downloaded media import");

            if (string.IsNullOrWhiteSpace(image.Title))
                image.Title = Path.GetFileNameWithoutExtension(path);

            image.Files.Add(imageFile);
        }
        else
        {
            // Intentionally leave Title null on scan. Storing the filename as the title makes it
            // impossible to filter for entities that have no real title; the UI falls back to the
            // file basename for display when Title is null.
            image = new Image
            {
                Files = [imageFile]
            };

            db.Images.Add(image);
        }

        await EnrichImageFileAsync(imageFile, path, ct, moveIndex);

        logger.LogTrace("Added image for {Path}", path);
        return (image, false, false);
    }

    // Compute the always-on identity fingerprint (oshash) plus the optional md5 for a new image file.
    // oshash is what lets a later scan recognise this image if it moves or is renamed.
    private async Task EnrichImageFileAsync(
        ImageFile imageFile,
        string path,
        CancellationToken ct,
        MoveDetectionIndex? moveIndex = null)
    {
        var oshash = await ScanFileIdentityService.ComputeOshashAsync(path, moveIndex, ct);
        if (oshash != null)
            ScanFileIdentityService.UpsertFingerprint(imageFile, "oshash", oshash);

        if (config.CalculateMd5)
        {
            var md5 = await fingerprintService.ComputeMd5Async(path, ct);
            if (!string.IsNullOrWhiteSpace(md5))
                ScanFileIdentityService.UpsertFingerprint(imageFile, "md5", md5);
        }
    }

    private static void ApplyValidatedDimensions(ImageFile imageFile, int? width, int? height)
    {
        if (width is > 0)
            imageFile.Width = width.Value;
        if (height is > 0)
            imageFile.Height = height.Value;
    }
}
