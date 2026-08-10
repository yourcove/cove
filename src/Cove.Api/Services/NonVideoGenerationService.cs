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

        var files = await db.ImageFiles
            .Include(file => file.ParentFolder)
            .Include(file => file.Fingerprints)
            .ToListAsync(ct);
        files = FilterFiles(files, options.Paths, options.ImageIds, file => file.ImageId);

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
        }, ct);
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

        var galleries = await db.Galleries
            .Include(gallery => gallery.Folder)
            .Include(gallery => gallery.Files).ThenInclude(file => file.ParentFolder)
            .Include(gallery => gallery.Files).ThenInclude(file => file.Fingerprints)
            .AsSplitQuery()
            .ToListAsync(ct);

        var filterPaths = GeneratePathFilter.Normalize(options.Paths);
        if (filterPaths.Count > 0)
        {
            galleries = galleries.Where(gallery =>
                (gallery.Folder is not null && GeneratePathFilter.Contains(gallery.Folder.Path, filterPaths))
                || gallery.Files.Any(file => GeneratePathFilter.Contains(GeneratePathFilter.Resolve(file), filterPaths)))
                .ToList();
        }

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
        }, ct);
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

        var files = await db.AudioFiles
            .Include(file => file.ParentFolder)
            .Include(file => file.Fingerprints)
            .ToListAsync(ct);
        files = FilterFiles(files, options.Paths, options.AudioIds, file => file.AudioId);

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
        }, ct);
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

        var files = await db.TextFiles
            .Include(file => file.ParentFolder)
            .Include(file => file.Fingerprints)
            .ToListAsync(ct);
        files = FilterFiles(files, options.Paths, options.TextIds, file => file.TextDocumentId);

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
        }, ct);
    }

    private static List<TFile> FilterFiles<TFile>(
        List<TFile> files,
        IEnumerable<string>? paths,
        IEnumerable<int>? entityIds,
        Func<TFile, int?> entityIdSelector)
        where TFile : BaseFileEntity
    {
        var filterPaths = GeneratePathFilter.Normalize(paths);
        if (filterPaths.Count > 0)
            files = files.Where(file => GeneratePathFilter.Contains(GeneratePathFilter.Resolve(file), filterPaths)).ToList();

        if (entityIds is not null)
        {
            var idSet = entityIds.ToHashSet();
            if (idSet.Count > 0)
                files = files.Where(file => entityIdSelector(file) is int id && idSet.Contains(id)).ToList();
        }

        return files;
    }

    private static async Task<Dictionary<int, int>> LoadFirstGalleryImagesAsync(
        CoveContext db,
        IReadOnlyCollection<Gallery> galleries,
        CancellationToken ct)
    {
        if (galleries.Count == 0)
            return [];

        var galleryIds = galleries.Select(gallery => gallery.Id).ToList();
        var links = await db.Set<ImageGallery>()
            .AsNoTracking()
            .Where(link => galleryIds.Contains(link.GalleryId))
            .Select(link => new { link.GalleryId, link.ImageId })
            .ToListAsync(ct);
        return links
            .GroupBy(link => link.GalleryId)
            .ToDictionary(group => group.Key, group => group.Min(link => link.ImageId));
    }

    private static async Task RunParallelWithProgressAsync<T>(
        IReadOnlyCollection<T> items,
        int parallelism,
        IJobProgress progress,
        string label,
        Func<T, CancellationToken, Task> work,
        CancellationToken ct)
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
                    var current = Interlocked.Increment(ref completed);
                    progress.Report(
                        items.Count == 0 ? 1d : (double)current / items.Count,
                        $"Generating {label} content ({current}/{items.Count})");
                }
            });
    }

    private static bool HasFingerprint(BaseFileEntity file, string type)
        => file.Fingerprints.Any(fingerprint =>
            fingerprint.Type == type && !string.IsNullOrWhiteSpace(fingerprint.Value));
}
