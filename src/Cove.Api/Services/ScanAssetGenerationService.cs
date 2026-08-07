using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cove.Api.Services;

/// <summary>
/// Generates optional thumbnails and fingerprints after scan persistence has completed.
/// </summary>
internal sealed class ScanAssetGenerationService(
    IServiceScopeFactory scopeFactory,
    IFingerprintService fingerprintService,
    IThumbnailService thumbnailService,
    ILogger logger)
{
    public async Task<AssetGenerationSummary> GenerateRequestedAssetsAsync(
        CoveContext db,
        IJobProgress progress,
        HashSet<string> processedVideoPaths,
        HashSet<string> processedImagePaths,
        HashSet<string> processedAudioPaths,
        HashSet<string> processedTextPaths,
        ScanOperationOptions options,
        int maxParallelism,
        CancellationToken ct)
    {
        var generateVideoAssets = options.GenerateCovers || options.GeneratePreviews || options.GenerateSprites || options.GeneratePhashes || options.GenerateMd5;
        var generateImageAssets = options.GenerateImagePhashes || options.GenerateImageThumbnails || options.GenerateMd5;
        var generateAudioAssets = options.GenerateAudioPhashes || options.GenerateMd5;
        var generateTextAssets = options.GenerateTextPhashes || options.GenerateMd5;

        if ((!generateVideoAssets && !generateImageAssets && !generateAudioAssets && !generateTextAssets)
            || (processedVideoPaths.Count == 0 && processedImagePaths.Count == 0 && processedAudioPaths.Count == 0 && processedTextPaths.Count == 0))
        {
            return new AssetGenerationSummary(0);
        }

        var failedItems = 0;

        if (generateVideoAssets && processedVideoPaths.Count > 0)
        {
            progress.Report(0.92, "Generating video assets...");

            var videoDirs = processedVideoPaths
                .Select(path => Path.GetDirectoryName(path))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var candidateFiles = await db.VideoFiles
                .Include(f => f.ParentFolder)
                .Include(f => f.Fingerprints)
                .Where(f => f.ParentFolder != null && videoDirs.Contains(f.ParentFolder.Path))
                .ToListAsync(ct);

            var videoFiles = candidateFiles
                .Where(file => file.ParentFolder != null && processedVideoPaths.Contains(ScanPath.Normalize(Path.Combine(file.ParentFolder.Path, file.Basename))))
                .Where(file => file.VideoId.HasValue && file.VideoId.Value != 0)
                .GroupBy(file => file.VideoId)
                .Select(group => group.First())
                .Where(file =>
                {
                    var videoId = file.VideoId!.Value;
                    return (options.GenerateCovers && !File.Exists(thumbnailService.GetThumbnailPathForVideo(videoId)))
                        || (options.GeneratePreviews && !File.Exists(thumbnailService.GetPreviewPath(videoId)))
                        || (options.GenerateSprites && (!File.Exists(thumbnailService.GetSpritePath(videoId)) || !File.Exists(thumbnailService.GetSpriteVttPath(videoId))))
                        || (options.GeneratePhashes && !file.Fingerprints.Any(fp => fp.Type == "phash" && !string.IsNullOrWhiteSpace(fp.Value)))
                        || (options.GenerateMd5 && !file.Fingerprints.Any(fp => fp.Type == "md5" && !string.IsNullOrWhiteSpace(fp.Value)));
                })
                .ToList();

            var total = Math.Max(videoFiles.Count, 1);
            var completed = 0;
            var failed = 0;

            await Parallel.ForEachAsync(videoFiles, new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = ct }, async (videoFile, token) =>
            {
                var done = Interlocked.Increment(ref completed);
                var videoId = videoFile.VideoId!.Value;
                var thumbnailPath = thumbnailService.GetThumbnailPathForVideo(videoId);
                var previewPath = thumbnailService.GetPreviewPath(videoId);
                var spritePath = thumbnailService.GetSpritePath(videoId);
                var spriteVttPath = thumbnailService.GetSpriteVttPath(videoId);
                var needsCover = options.GenerateCovers && !File.Exists(thumbnailPath);
                var needsPreview = options.GeneratePreviews && !File.Exists(previewPath);
                var needsSprite = options.GenerateSprites && (!File.Exists(spritePath) || !File.Exists(spriteVttPath));
                var failedThisVideo = false;

                progress.Report(0.92 + (0.06 * done / total), $"Generating video assets ({done}/{videoFiles.Count})");

                // Isolate each video: a single corrupt/locked file (e.g. an ffmpeg decode failure or an
                // IO error computing MD5) must not abort the whole batch. Parallel.ForEachAsync cancels
                // all workers on the first unhandled exception, which is why one bad file previously
                // poisoned the entire generation run. Swallow per-file errors and keep going; only real
                // cancellation propagates.
                try
                {
                    if (options.GenerateCovers)
                    {
                        if (needsCover)
                            await thumbnailService.GenerateVideoThumbnailAsync(videoId, null, token);
                    }
                    if (options.GeneratePreviews)
                    {
                        if (needsPreview)
                            await thumbnailService.GenerateVideoPreviewAsync(videoId, token);
                    }
                    if (options.GenerateSprites)
                    {
                        if (needsSprite)
                            await thumbnailService.GenerateVideoSpriteAsync(videoId, token);
                    }
                    if (options.GeneratePhashes
                        && videoFile.ParentFolder != null
                        && !videoFile.Fingerprints.Any(fp => fp.Type == "phash" && !string.IsNullOrWhiteSpace(fp.Value)))
                    {
                        var filePath = Path.Combine(videoFile.ParentFolder.Path, videoFile.Basename);
                        var phash = await fingerprintService.ComputeVideoPhashAsync(filePath, videoFile.Duration, token);
                        if (!string.IsNullOrWhiteSpace(phash))
                        {
                            using var innerScope = scopeFactory.CreateScope();
                            var innerDb = innerScope.ServiceProvider.GetRequiredService<CoveContext>();
                            var existing = await innerDb.FileFingerprints.FirstOrDefaultAsync(fp => fp.FileId == videoFile.Id && fp.Type == "phash", token);
                            if (existing != null)
                            {
                                existing.Value = phash;
                            }
                            else
                            {
                                innerDb.FileFingerprints.Add(new FileFingerprint
                                {
                                    FileId = videoFile.Id,
                                    Type = "phash",
                                    Value = phash,
                                });
                            }
                            await innerDb.SaveChangesAsync(token);
                        }
                        else
                        {
                            failedThisVideo = true;
                        }
                    }
                    if (options.GenerateMd5
                        && videoFile.ParentFolder != null
                        && !videoFile.Fingerprints.Any(fp => fp.Type == "md5" && !string.IsNullOrWhiteSpace(fp.Value)))
                    {
                        var filePath = Path.Combine(videoFile.ParentFolder.Path, videoFile.Basename);
                        var md5 = await fingerprintService.ComputeMd5Async(filePath, token);
                        if (!string.IsNullOrWhiteSpace(md5))
                        {
                            using var innerScope = scopeFactory.CreateScope();
                            var innerDb = innerScope.ServiceProvider.GetRequiredService<CoveContext>();
                            var existing = await innerDb.FileFingerprints.FirstOrDefaultAsync(fp => fp.FileId == videoFile.Id && fp.Type == "md5", token);
                            if (existing != null)
                            {
                                existing.Value = md5;
                            }
                            else
                            {
                                innerDb.FileFingerprints.Add(new FileFingerprint
                                {
                                    FileId = videoFile.Id,
                                    Type = "md5",
                                    Value = md5,
                                });
                            }
                            await innerDb.SaveChangesAsync(token);
                        }
                        else
                        {
                            failedThisVideo = true;
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedThisVideo = true;
                    logger.LogWarning(ex, "Failed generating assets for video {VideoId}; skipping", videoId);
                }

                if ((needsCover && !File.Exists(thumbnailPath))
                    || (needsPreview && !File.Exists(previewPath))
                    || (needsSprite && (!File.Exists(spritePath) || !File.Exists(spriteVttPath))))
                {
                    failedThisVideo = true;
                }

                if (failedThisVideo)
                    Interlocked.Increment(ref failed);
            });

            if (failed > 0)
                logger.LogWarning("Video asset generation completed with {Failed} failed of {Total} videos", failed, videoFiles.Count);
            failedItems += failed;
        }

        if (generateImageAssets && processedImagePaths.Count > 0)
        {
            progress.Report(0.98, "Generating image assets...");

            var imageDirs = processedImagePaths
                .Select(path => Path.GetDirectoryName(path))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var candidateFiles = await db.ImageFiles
                .Include(f => f.ParentFolder)
                .Include(f => f.Fingerprints)
                .Where(f => f.ParentFolder != null && imageDirs.Contains(f.ParentFolder.Path))
                .ToListAsync(ct);

            var imageFiles = candidateFiles
                .Where(file => file.ParentFolder != null && processedImagePaths.Contains(ScanPath.Normalize(Path.Combine(file.ParentFolder.Path, file.Basename))))
                .ToList();

            var total = Math.Max(imageFiles.Count, 1);
            var completed = 0;
            var failed = 0;
            await Parallel.ForEachAsync(imageFiles, new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = ct }, async (imageFile, token) =>
            {
                var done = Interlocked.Increment(ref completed);
                progress.Report(0.98 + (0.01 * done / total), $"Generating image assets ({done}/{imageFiles.Count})");
                var failedThisImage = false;

                if (imageFile.ParentFolder == null)
                    return;

                // Isolate each image so one unreadable/corrupt file can't abort the whole batch.
                try
                {
                    if (options.GenerateImageThumbnails && imageFile.ImageId.HasValue)
                    {
                        if (!await thumbnailService.GenerateImageThumbnailAsync(imageFile.ImageId.Value, overwrite: false, ct: token))
                            failedThisImage = true;
                    }

                    if (options.GenerateImagePhashes
                        && !imageFile.Fingerprints.Any(fp => fp.Type == "phash" && !string.IsNullOrWhiteSpace(fp.Value)))
                    {
                        var filePath = Path.Combine(imageFile.ParentFolder.Path, imageFile.Basename);
                        var phash = await fingerprintService.ComputeImagePhashAsync(filePath, token);
                        if (!string.IsNullOrWhiteSpace(phash))
                        {
                            using var innerScope = scopeFactory.CreateScope();
                            var innerDb = innerScope.ServiceProvider.GetRequiredService<CoveContext>();
                            var existing = await innerDb.FileFingerprints.FirstOrDefaultAsync(fp => fp.FileId == imageFile.Id && fp.Type == "phash", token);
                            if (existing != null)
                            {
                                existing.Value = phash;
                            }
                            else
                            {
                                innerDb.FileFingerprints.Add(new FileFingerprint
                                {
                                    FileId = imageFile.Id,
                                    Type = "phash",
                                    Value = phash,
                                });
                            }
                            await innerDb.SaveChangesAsync(token);
                        }
                        else
                        {
                            failedThisImage = true;
                        }
                    }

                    if (options.GenerateMd5
                        && !imageFile.Fingerprints.Any(fp => fp.Type == "md5" && !string.IsNullOrWhiteSpace(fp.Value)))
                    {
                        var filePath = Path.Combine(imageFile.ParentFolder.Path, imageFile.Basename);
                        var md5 = await fingerprintService.ComputeMd5Async(filePath, token);
                        if (!string.IsNullOrWhiteSpace(md5))
                        {
                            using var innerScope = scopeFactory.CreateScope();
                            var innerDb = innerScope.ServiceProvider.GetRequiredService<CoveContext>();
                            var existing = await innerDb.FileFingerprints.FirstOrDefaultAsync(fp => fp.FileId == imageFile.Id && fp.Type == "md5", token);
                            if (existing != null)
                            {
                                existing.Value = md5;
                            }
                            else
                            {
                                innerDb.FileFingerprints.Add(new FileFingerprint
                                {
                                    FileId = imageFile.Id,
                                    Type = "md5",
                                    Value = md5,
                                });
                            }
                            await innerDb.SaveChangesAsync(token);
                        }
                        else
                        {
                            failedThisImage = true;
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedThisImage = true;
                    logger.LogWarning(ex, "Failed generating assets for image {ImageId}; skipping", imageFile.ImageId);
                }

                if (failedThisImage)
                    Interlocked.Increment(ref failed);
            });

            if (failed > 0)
                logger.LogWarning("Image asset generation completed with {Failed} failed of {Total} images", failed, imageFiles.Count);
            failedItems += failed;
        }

        if (generateAudioAssets && processedAudioPaths.Count > 0)
        {
            progress.Report(0.99, "Generating audio fingerprints...");

            var audioDirs = processedAudioPaths
                .Select(path => Path.GetDirectoryName(path))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var candidateFiles = await db.AudioFiles
                .Include(f => f.ParentFolder)
                .Include(f => f.Fingerprints)
                .Where(f => f.ParentFolder != null && audioDirs.Contains(f.ParentFolder.Path))
                .ToListAsync(ct);

            var audioFiles = candidateFiles
                .Where(file => file.ParentFolder != null && processedAudioPaths.Contains(ScanPath.Normalize(Path.Combine(file.ParentFolder.Path, file.Basename))))
                .ToList();

            var total = Math.Max(audioFiles.Count, 1);
            var completed = 0;
            var failed = 0;
            await Parallel.ForEachAsync(audioFiles, new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = ct }, async (audioFile, token) =>
            {
                var done = Interlocked.Increment(ref completed);
                progress.Report(0.99, $"Generating audio fingerprints ({done}/{audioFiles.Count})");
                var failedThisAudio = false;

                if (audioFile.ParentFolder == null)
                    return;

                // Isolate each audio file so one unreadable/corrupt file can't abort the whole batch.
                try
                {
                    var filePath = Path.Combine(audioFile.ParentFolder.Path, audioFile.Basename);
                    if (options.GenerateAudioPhashes
                        && !audioFile.Fingerprints.Any(fp => fp.Type == "phash" && !string.IsNullOrWhiteSpace(fp.Value)))
                    {
                        var phash = await fingerprintService.ComputeAudioPhashAsync(filePath, token);
                        if (!string.IsNullOrWhiteSpace(phash))
                        {
                            using var innerScope = scopeFactory.CreateScope();
                            var innerDb = innerScope.ServiceProvider.GetRequiredService<CoveContext>();
                            var existing = await innerDb.FileFingerprints.FirstOrDefaultAsync(fp => fp.FileId == audioFile.Id && fp.Type == "phash", token);
                            if (existing != null) existing.Value = phash;
                            else innerDb.FileFingerprints.Add(new FileFingerprint { FileId = audioFile.Id, Type = "phash", Value = phash });
                            await innerDb.SaveChangesAsync(token);
                        }
                        else
                        {
                            failedThisAudio = true;
                        }
                    }

                    if (options.GenerateMd5
                        && !audioFile.Fingerprints.Any(fp => fp.Type == "md5" && !string.IsNullOrWhiteSpace(fp.Value)))
                    {
                        var md5 = await fingerprintService.ComputeMd5Async(filePath, token);
                        if (!string.IsNullOrWhiteSpace(md5))
                        {
                            using var innerScope = scopeFactory.CreateScope();
                            var innerDb = innerScope.ServiceProvider.GetRequiredService<CoveContext>();
                            var existing = await innerDb.FileFingerprints.FirstOrDefaultAsync(fp => fp.FileId == audioFile.Id && fp.Type == "md5", token);
                            if (existing != null) existing.Value = md5;
                            else innerDb.FileFingerprints.Add(new FileFingerprint { FileId = audioFile.Id, Type = "md5", Value = md5 });
                            await innerDb.SaveChangesAsync(token);
                        }
                        else
                        {
                            failedThisAudio = true;
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedThisAudio = true;
                    logger.LogWarning(ex, "Failed generating audio fingerprints for file {FileId}; skipping", audioFile.Id);
                }

                if (failedThisAudio)
                    Interlocked.Increment(ref failed);
            });

            if (failed > 0)
                logger.LogWarning("Audio fingerprint generation completed with {Failed} failed of {Total} files", failed, audioFiles.Count);
            failedItems += failed;
        }

        if (generateTextAssets && processedTextPaths.Count > 0)
        {
            progress.Report(0.99, "Generating text fingerprints...");

            var textDirs = processedTextPaths
                .Select(path => Path.GetDirectoryName(path))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var candidateFiles = await db.TextFiles
                .Include(f => f.ParentFolder)
                .Include(f => f.Fingerprints)
                .Where(f => f.ParentFolder != null && textDirs.Contains(f.ParentFolder.Path))
                .ToListAsync(ct);

            var textFiles = candidateFiles
                .Where(file => file.ParentFolder != null && processedTextPaths.Contains(ScanPath.Normalize(Path.Combine(file.ParentFolder.Path, file.Basename))))
                .ToList();

            var total = Math.Max(textFiles.Count, 1);
            var completed = 0;
            var failed = 0;
            await Parallel.ForEachAsync(textFiles, new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = ct }, async (textFile, token) =>
            {
                var done = Interlocked.Increment(ref completed);
                progress.Report(0.99, $"Generating text fingerprints ({done}/{textFiles.Count})");
                var failedThisText = false;

                if (textFile.ParentFolder == null)
                    return;

                // Isolate each text file so one unreadable/corrupt file can't abort the whole batch.
                try
                {
                    var filePath = Path.Combine(textFile.ParentFolder.Path, textFile.Basename);
                    if (options.GenerateTextPhashes
                        && !textFile.Fingerprints.Any(fp => fp.Type == "phash" && !string.IsNullOrWhiteSpace(fp.Value)))
                    {
                        var phash = await fingerprintService.ComputeTextPhashAsync(filePath, token);
                        if (!string.IsNullOrWhiteSpace(phash))
                        {
                            using var innerScope = scopeFactory.CreateScope();
                            var innerDb = innerScope.ServiceProvider.GetRequiredService<CoveContext>();
                            var existing = await innerDb.FileFingerprints.FirstOrDefaultAsync(fp => fp.FileId == textFile.Id && fp.Type == "phash", token);
                            if (existing != null) existing.Value = phash;
                            else innerDb.FileFingerprints.Add(new FileFingerprint { FileId = textFile.Id, Type = "phash", Value = phash });
                            await innerDb.SaveChangesAsync(token);
                        }
                        else
                        {
                            failedThisText = true;
                        }
                    }

                    if (options.GenerateMd5
                        && !textFile.Fingerprints.Any(fp => fp.Type == "md5" && !string.IsNullOrWhiteSpace(fp.Value)))
                    {
                        var md5 = await fingerprintService.ComputeMd5Async(filePath, token);
                        if (!string.IsNullOrWhiteSpace(md5))
                        {
                            using var innerScope = scopeFactory.CreateScope();
                            var innerDb = innerScope.ServiceProvider.GetRequiredService<CoveContext>();
                            var existing = await innerDb.FileFingerprints.FirstOrDefaultAsync(fp => fp.FileId == textFile.Id && fp.Type == "md5", token);
                            if (existing != null) existing.Value = md5;
                            else innerDb.FileFingerprints.Add(new FileFingerprint { FileId = textFile.Id, Type = "md5", Value = md5 });
                            await innerDb.SaveChangesAsync(token);
                        }
                        else
                        {
                            failedThisText = true;
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedThisText = true;
                    logger.LogWarning(ex, "Failed generating text fingerprints for file {FileId}; skipping", textFile.Id);
                }

                if (failedThisText)
                    Interlocked.Increment(ref failed);
            });

            if (failed > 0)
                logger.LogWarning("Text fingerprint generation completed with {Failed} failed of {Total} files", failed, textFiles.Count);
            failedItems += failed;
        }

        return new AssetGenerationSummary(failedItems);
    }
}

internal sealed record AssetGenerationSummary(int FailedItems);
