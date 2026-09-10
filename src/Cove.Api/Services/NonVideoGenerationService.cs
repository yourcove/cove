using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

public sealed class NonVideoGenerationService(
    IThumbnailService thumbnailService,
    IFingerprintService fingerprintService,
    FileFingerprintWriter fingerprintWriter,
    ILogger<NonVideoGenerationService> logger)
{
    public async Task GenerateAsync(
        CoveContext db,
        GenerateOptionsDto options,
        int parallelism,
        IJobProgress progress,
        CancellationToken ct)
    {
        await GenerateImagesAsync(db, options, parallelism, progress, ct);
        await GenerateGalleriesAsync(db, options, parallelism, progress, ct);
        await GenerateAudiosAsync(db, options, parallelism, progress, ct);
        await GenerateTextsAsync(db, options, parallelism, progress, ct);
    }

    private async Task GenerateImagesAsync(
        CoveContext db,
        GenerateOptionsDto options,
        int parallelism,
        IJobProgress progress,
        CancellationToken ct)
    {
        if (!options.ImagePhashes && !options.ImageThumbnails && !options.Md5)
            return;

        var query = db.ImageFiles.AsNoTracking();
        if (options.ImageIds is { Count: > 0 })
            query = query.Where(file => file.ImageId.HasValue && options.ImageIds.Contains(file.ImageId.Value));
        var total = await CountSelectedAsync(() => GenerationSelection.FilesAsync(query, options.Paths, ct));
        var completed = 0;
        await foreach (var ids in GenerationSelection.FilesAsync(query, options.Paths, ct))
        {
            var files = await query.Where(file => ids.Contains(file.Id))
                .Include(file => file.ParentFolder).Include(file => file.Fingerprints)
                .OrderBy(file => file.Id).ToListAsync(ct);
            await RunParallelWithProgressAsync(files, parallelism, progress, "image", async (file, token) =>
            {
                try
                {
                    var path = GeneratePathFilter.Resolve(file);
                    if (options.ImageThumbnails && file.ImageId.HasValue)
                        await thumbnailService.GenerateImageThumbnailAsync(file.ImageId.Value, overwrite: options.Overwrite, ct: token);

                    if (!File.Exists(path))
                        return;

                    if (options.ImagePhashes && (options.Overwrite || !HasFingerprint(file, "phash")))
                    {
                        var phash = await fingerprintService.ComputeImagePhashAsync(path, token);
                        if (!string.IsNullOrWhiteSpace(phash))
                            await fingerprintWriter.UpsertAsync(file.Id, "phash", phash, token);
                    }

                    if (options.Md5 && (options.Overwrite || !HasFingerprint(file, "md5")))
                    {
                        var md5 = await fingerprintService.ComputeMd5Async(path, token);
                        if (!string.IsNullOrWhiteSpace(md5))
                            await fingerprintWriter.UpsertAsync(file.Id, "md5", md5, token);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Skipped image {ImageId} during generate after an error", file.ImageId);
                }
            }, ct, completed, total);
            completed += files.Count;
        }
    }

    private async Task GenerateGalleriesAsync(
        CoveContext db,
        GenerateOptionsDto options,
        int parallelism,
        IJobProgress progress,
        CancellationToken ct)
    {
        if (!options.GalleryThumbnails && !options.Md5)
            return;

        var total = await CountSelectedAsync(() => GenerationSelection.GalleriesAsync(db, options.Paths, ct));
        var completed = 0;
        await foreach (var ids in GenerationSelection.GalleriesAsync(db, options.Paths, ct))
        {
            var galleries = await db.Galleries.AsNoTracking().Where(gallery => ids.Contains(gallery.Id))
                .Include(gallery => gallery.Folder)
                .Include(gallery => gallery.Files).ThenInclude(file => file.ParentFolder)
                .Include(gallery => gallery.Files).ThenInclude(file => file.Fingerprints)
                .AsSplitQuery().OrderBy(gallery => gallery.Id).ToListAsync(ct);
            var firstImageByGalleryId = await LoadFirstGalleryImagesAsync(db, galleries, ct);
            await RunParallelWithProgressAsync(galleries, parallelism, progress, "gallery", async (gallery, token) =>
            {
                try
                {
                    if (options.GalleryThumbnails)
                    {
                        var coverImageId = gallery.CoverImageId;
                        if (!coverImageId.HasValue && firstImageByGalleryId.TryGetValue(gallery.Id, out var firstImageId))
                            coverImageId = firstImageId;
                        if (coverImageId.HasValue)
                            await thumbnailService.GenerateImageThumbnailAsync(coverImageId.Value, overwrite: options.Overwrite, ct: token);
                    }

                    if (!options.Md5)
                        return;

                    foreach (var file in gallery.Files)
                    {
                        var path = GeneratePathFilter.Resolve(file);
                        if (!File.Exists(path) || (!options.Overwrite && HasFingerprint(file, "md5")))
                            continue;

                        var md5 = await fingerprintService.ComputeMd5Async(path, token);
                        if (!string.IsNullOrWhiteSpace(md5))
                            await fingerprintWriter.UpsertAsync(file.Id, "md5", md5, token);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Skipped gallery {GalleryId} during generate after an error", gallery.Id);
                }
            }, ct, completed, total);
            completed += galleries.Count;
        }
    }

    private async Task GenerateAudiosAsync(
        CoveContext db,
        GenerateOptionsDto options,
        int parallelism,
        IJobProgress progress,
        CancellationToken ct)
    {
        if (!options.AudioPhashes && !options.Md5)
            return;

        var query = db.AudioFiles.AsNoTracking();
        if (options.AudioIds is { Count: > 0 })
            query = query.Where(file => file.AudioId.HasValue && options.AudioIds.Contains(file.AudioId.Value));
        var total = await CountSelectedAsync(() => GenerationSelection.FilesAsync(query, options.Paths, ct));
        var completed = 0;
        await foreach (var ids in GenerationSelection.FilesAsync(query, options.Paths, ct))
        {
            var files = await query.Where(file => ids.Contains(file.Id))
                .Include(file => file.ParentFolder).Include(file => file.Fingerprints)
                .OrderBy(file => file.Id).ToListAsync(ct);
            await RunParallelWithProgressAsync(files, parallelism, progress, "audio", async (file, token) =>
            {
                try
                {
                    var path = GeneratePathFilter.Resolve(file);
                    if (!File.Exists(path))
                        return;

                    if (options.AudioPhashes && (options.Overwrite || !HasFingerprint(file, "phash")))
                    {
                        var phash = await fingerprintService.ComputeAudioPhashAsync(path, token);
                        if (!string.IsNullOrWhiteSpace(phash))
                            await fingerprintWriter.UpsertAsync(file.Id, "phash", phash, token);
                    }

                    if (options.Md5 && (options.Overwrite || !HasFingerprint(file, "md5")))
                    {
                        var md5 = await fingerprintService.ComputeMd5Async(path, token);
                        if (!string.IsNullOrWhiteSpace(md5))
                            await fingerprintWriter.UpsertAsync(file.Id, "md5", md5, token);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Skipped audio {AudioId} during generate after an error", file.AudioId);
                }
            }, ct, completed, total);
            completed += files.Count;
        }
    }

    private async Task GenerateTextsAsync(
        CoveContext db,
        GenerateOptionsDto options,
        int parallelism,
        IJobProgress progress,
        CancellationToken ct)
    {
        if (!options.TextPhashes && !options.Md5)
            return;

        var query = db.TextFiles.AsNoTracking();
        if (options.TextIds is { Count: > 0 })
            query = query.Where(file => file.TextDocumentId.HasValue && options.TextIds.Contains(file.TextDocumentId.Value));
        var total = await CountSelectedAsync(() => GenerationSelection.FilesAsync(query, options.Paths, ct));
        var completed = 0;
        await foreach (var ids in GenerationSelection.FilesAsync(query, options.Paths, ct))
        {
            var files = await query.Where(file => ids.Contains(file.Id))
                .Include(file => file.ParentFolder).Include(file => file.Fingerprints)
                .OrderBy(file => file.Id).ToListAsync(ct);
            await RunParallelWithProgressAsync(files, parallelism, progress, "text", async (file, token) =>
            {
                try
                {
                    var path = GeneratePathFilter.Resolve(file);
                    if (!File.Exists(path))
                        return;

                    if (options.TextPhashes && (options.Overwrite || !HasFingerprint(file, "phash")))
                    {
                        var phash = await fingerprintService.ComputeTextPhashAsync(path, token);
                        if (!string.IsNullOrWhiteSpace(phash))
                            await fingerprintWriter.UpsertAsync(file.Id, "phash", phash, token);
                    }

                    if (options.Md5 && (options.Overwrite || !HasFingerprint(file, "md5")))
                    {
                        var md5 = await fingerprintService.ComputeMd5Async(path, token);
                        if (!string.IsNullOrWhiteSpace(md5))
                            await fingerprintWriter.UpsertAsync(file.Id, "md5", md5, token);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Skipped text {TextId} during generate after an error", file.TextDocumentId);
                }
            }, ct, completed, total);
            completed += files.Count;
        }
    }

    private static async Task<int> CountSelectedAsync(Func<IAsyncEnumerable<int[]>> select)
    {
        var total = 0;
        await foreach (var ids in select())
            total = checked(total + ids.Length);
        return total;
    }

    private static async Task<Dictionary<int, int>> LoadFirstGalleryImagesAsync(
        CoveContext db,
        IReadOnlyCollection<Gallery> galleries,
        CancellationToken ct)
    {
        if (galleries.Count == 0)
            return [];

        var galleryIds = galleries.Select(gallery => gallery.Id).ToList();
        return await db.Set<ImageGallery>()
            .AsNoTracking()
            .Where(link => galleryIds.Contains(link.GalleryId))
            .GroupBy(link => link.GalleryId)
            .Select(group => new { GalleryId = group.Key, ImageId = group.Min(link => link.ImageId) })
            .ToDictionaryAsync(row => row.GalleryId, row => row.ImageId, ct);
    }

    private static async Task RunParallelWithProgressAsync<T>(
        IReadOnlyCollection<T> items,
        int parallelism,
        IJobProgress progress,
        string label,
        Func<T, CancellationToken, Task> work,
        CancellationToken ct,
        int previouslyCompleted,
        int total)
    {
        var completed = 0;
        await Parallel.ForEachAsync(
            items,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (item, token) =>
            {
                try
                {
                    await work(item, token);
                }
                finally
                {
                    var current = previouslyCompleted + Interlocked.Increment(ref completed);
                    progress.Report(
                        total == 0 ? 1d : (double)current / total,
                        $"Generating {label} content ({current}/{total})");
                }
            });
    }

    private static bool HasFingerprint(BaseFileEntity file, string type)
        => file.Fingerprints.Any(fingerprint =>
            fingerprint.Type == type && !string.IsNullOrWhiteSpace(fingerprint.Value));
}
