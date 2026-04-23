using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Cove.Core.Entities;
using Cove.Core.Entities.Galleries.Zip;
using Cove.Core.Interfaces;
using Cove.Data;

namespace Cove.Api.Services;

public interface IThumbnailService
{
    Task<string?> GetSceneThumbnailPathAsync(int sceneId, CancellationToken ct = default);
    Task<string?> GetImageFilePathAsync(int imageId, CancellationToken ct = default);
    Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageStreamAsync(int imageId, CancellationToken ct = default);
    Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageThumbnailStreamAsync(int imageId, int maxDimension = 640, CancellationToken ct = default);
    Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetBlobImageThumbnailStreamAsync(string blobId, int maxDimension = 640, CancellationToken ct = default);
    Task GenerateSceneThumbnailAsync(int sceneId, double? atSeconds = null, CancellationToken ct = default);
    Task GenerateImageThumbnailAsync(int imageId, int maxDimension = 640, bool overwrite = false, CancellationToken ct = default);
    Task GenerateScenePreviewAsync(int sceneId, CancellationToken ct = default);
    Task GenerateSceneSpriteAsync(int sceneId, CancellationToken ct = default);
    string GetThumbnailPathForScene(int sceneId);
    string GetTimestampedThumbnailPath(int sceneId, double seconds);
    string GetPreviewPath(int sceneId);
    string GetSpritePath(int sceneId);
    string GetSpriteVttPath(int sceneId);
    string StartGenerateAllThumbnails();
}

public class ThumbnailService(
    IServiceScopeFactory scopeFactory,
    IJobService jobService,
    CoveConfiguration config,
    IZipFileReader zipFileReader,
    IBlobService blobService,
    ILogger<ThumbnailService> logger) : IThumbnailService
{
    private string ThumbnailDir => Path.Combine(config.GeneratedPath, "screenshots");
    private string ImageThumbnailDir => Path.Combine(config.GeneratedPath, "thumbnails");
    private string PreviewDir => Path.Combine(config.GeneratedPath, "previews");
    private string VttDir => Path.Combine(config.GeneratedPath, "vtt");
    private SemaphoreSlim? _ffmpegSemaphore;
    private int _semaphoreCapacity;
    private string? _cachedFfmpegPath;
    private bool _ffmpegSearched;
    private string? _hwEncoder;
    private bool _hwEncoderSearched;

    /// <summary>Get (or create) a semaphore sized to MaxParallelTasks. FFmpeg threads are
    /// limited so total CPU usage ≈ MaxParallelTasks cores.</summary>
    private SemaphoreSlim GetFfmpegSemaphore()
    {
        var desired = Math.Max(1, config.MaxParallelTasks);
        var current = _ffmpegSemaphore;
        if (current != null && _semaphoreCapacity == desired) return current;
        // Config changed — create a new semaphore (old one will be GC'd after
        // any in-flight waiters release it).
        var sem = new SemaphoreSlim(desired, desired);
        _ffmpegSemaphore = sem;
        _semaphoreCapacity = desired;
        return sem;
    }

    private static readonly Dictionary<string, string> ImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png",
        [".gif"] = "image/gif", [".webp"] = "image/webp", [".bmp"] = "image/bmp",
        [".tiff"] = "image/tiff", [".tif"] = "image/tiff", [".svg"] = "image/svg+xml",
    };
    private static readonly string[] ArchiveExtensions = [".zip", ".cbz"];

    // Preview generation defaults (matching original Cove)
    private const int PreviewSegments = 12;
    private const double PreviewSegmentDuration = 0.75;
    private const int PreviewWidth = 640;
    private const string PreviewPreset = "fast";
    private const int PreviewCrf = 21;
    private const int DefaultImageThumbnailMaxDimension = 640;
    private const int MinImageThumbnailMaxDimension = 64;
    private const int MaxImageThumbnailMaxDimension = 4096;
    private const int ImageThumbnailQuality = 80;

    // Sprite generation defaults
    private const int SpriteFrameCount = 81; // 9x9 grid
    private const int SpriteFrameSize = 160; // px

    public Task<string?> GetSceneThumbnailPathAsync(int sceneId, CancellationToken ct)
    {
        // Cover images are only created by an explicit generate task, never on-demand.
        var thumbPath = GetThumbnailPath(sceneId);
        return Task.FromResult(File.Exists(thumbPath) ? thumbPath : null);
    }

    public async Task<string?> GetImageFilePathAsync(int imageId, CancellationToken ct)
    {
        var imageFile = await GetImageFileRecordAsync(imageId, ct);

        if (imageFile == null) return null;

        var filePath = imageFile.ParentFolder != null
            ? Path.Combine(imageFile.ParentFolder.Path, imageFile.Basename)
            : imageFile.Basename;

        return File.Exists(filePath) ? filePath : null;
    }

    public async Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageStreamAsync(int imageId, CancellationToken ct)
    {
        var imageFile = await GetImageFileRecordAsync(imageId, ct);

        if (imageFile == null) return null;

        return await OpenImageSourceStreamAsync(imageFile, ct);
    }

    public async Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageThumbnailStreamAsync(int imageId, int maxDimension, CancellationToken ct)
    {
        maxDimension = NormalizeImageThumbnailMaxDimension(maxDimension);

        var imageFile = await GetImageFileRecordAsync(imageId, ct);
        if (imageFile == null) return null;

        var thumbnailPath = GetImageThumbnailPath(imageId, maxDimension);
        if (config.WriteImageThumbnails && IsImageThumbnailCurrent(thumbnailPath, imageFile.ModTime))
        {
            var cachedStream = new FileStream(thumbnailPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            return (cachedStream, "image/jpeg", true);
        }

        var source = await OpenImageSourceStreamAsync(imageFile, ct);
        if (source == null) return null;

        if (!CanGenerateImageThumbnail(source.Value.contentType))
            return source;

        try
        {
            if (config.WriteImageThumbnails)
            {
                await using (source.Value.stream)
                {
                    await GenerateImageThumbnailFileAsync(source.Value.stream, thumbnailPath, imageFile.ModTime, maxDimension, ct);
                }

                var cachedStream = new FileStream(thumbnailPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                return (cachedStream, "image/jpeg", true);
            }

            var thumbnailStream = new MemoryStream();
            await WriteImageThumbnailAsync(source.Value.stream, thumbnailStream, maxDimension, ct);
            await source.Value.stream.DisposeAsync();
            thumbnailStream.Position = 0;
            return (thumbnailStream, "image/jpeg", false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Falling back to original image stream for thumbnail {ImageId}", imageId);
            if (source.Value.stream.CanSeek)
                source.Value.stream.Position = 0;
            return source;
        }
    }

    public async Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetBlobImageThumbnailStreamAsync(string blobId, int maxDimension, CancellationToken ct)
    {
        maxDimension = NormalizeImageThumbnailMaxDimension(maxDimension);

        var thumbnailPath = GetBlobImageThumbnailPath(blobId, maxDimension);
        if (File.Exists(thumbnailPath))
        {
            var cachedStream = new FileStream(thumbnailPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            return (cachedStream, "image/jpeg", true);
        }

        var source = await blobService.GetBlobAsync(blobId, ct);
        if (source == null) return null;

        if (!CanGenerateImageThumbnail(source.Value.ContentType))
            return (source.Value.Stream, source.Value.ContentType, source.Value.Stream.CanSeek);

        try
        {
            await using (source.Value.Stream)
            {
                var directory = Path.GetDirectoryName(thumbnailPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                var tempPath = thumbnailPath + $".{Guid.NewGuid():N}.tmp";
                try
                {
                    await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        await WriteImageThumbnailAsync(source.Value.Stream, output, maxDimension, ct);
                    }

                    File.Move(tempPath, thumbnailPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tempPath))
                    {
                        try { File.Delete(tempPath); } catch { }
                    }
                }
            }

            var cachedStream = new FileStream(thumbnailPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            return (cachedStream, "image/jpeg", true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Falling back to original blob stream for entity image thumbnail {BlobId}", blobId);

            var fallback = await blobService.GetBlobAsync(blobId, ct);
            if (fallback == null) return null;
            return (fallback.Value.Stream, fallback.Value.ContentType, fallback.Value.Stream.CanSeek);
        }
    }

    public async Task GenerateImageThumbnailAsync(int imageId, int maxDimension, bool overwrite, CancellationToken ct)
    {
        maxDimension = NormalizeImageThumbnailMaxDimension(maxDimension);

        var imageFile = await GetImageFileRecordAsync(imageId, ct);
        if (imageFile == null) return;

        var thumbnailPath = GetImageThumbnailPath(imageId, maxDimension);
        if (!overwrite && IsImageThumbnailCurrent(thumbnailPath, imageFile.ModTime))
            return;

        var source = await OpenImageSourceStreamAsync(imageFile, ct);
        if (source == null || !CanGenerateImageThumbnail(source.Value.contentType))
            return;

        await using (source.Value.stream)
        {
            await GenerateImageThumbnailFileAsync(source.Value.stream, thumbnailPath, imageFile.ModTime, maxDimension, ct);
        }
    }

    private async Task<ImageFile?> GetImageFileRecordAsync(int imageId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

        return await db.ImageFiles
            .Include(f => f.ParentFolder)
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.ImageId == imageId, ct);
    }

    private async Task<(Stream stream, string contentType, bool supportsRangeRequests)?> OpenImageSourceStreamAsync(ImageFile imageFile, CancellationToken ct)
    {

        var resolvedFilePath = imageFile.Path;

        if (imageFile.ZipFileId.HasValue)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

            var zipFile = await db.Set<BaseFileEntity>()
                .Include(file => file.ParentFolder)
                .AsNoTracking()
                .FirstOrDefaultAsync(file => file.Id == imageFile.ZipFileId.Value, ct);

            if (zipFile != null)
            {
                var zipResult = await TryOpenZipBackedImageStreamAsync(zipFile.Path, GetZipEntryCandidates(imageFile.Basename, resolvedFilePath, zipFile.Path), ct);
                if (zipResult != null) return zipResult;
            }
        }

        if (TryParseArchivePath(resolvedFilePath, out var archivePath, out var entryPath))
        {
            var zipResult = await TryOpenZipBackedImageStreamAsync(archivePath, [entryPath, imageFile.Basename], ct);
            if (zipResult != null) return zipResult;
        }

        if (!File.Exists(resolvedFilePath)) return null;

        var ext = Path.GetExtension(resolvedFilePath);
        var contentType = ImageMimeTypes.GetValueOrDefault(ext, "application/octet-stream");
        var stream = new FileStream(resolvedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return (stream, contentType, true);
    }

    private async Task GenerateImageThumbnailFileAsync(Stream sourceStream, string thumbnailPath, DateTime sourceModifiedAt, int maxDimension, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(thumbnailPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = thumbnailPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await WriteImageThumbnailAsync(sourceStream, output, maxDimension, ct);
            }

            File.Move(tempPath, thumbnailPath, overwrite: true);
            File.SetLastWriteTimeUtc(thumbnailPath, NormalizeUtc(sourceModifiedAt));
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private static async Task WriteImageThumbnailAsync(Stream sourceStream, Stream outputStream, int maxDimension, CancellationToken ct)
    {
        if (sourceStream.CanSeek)
            sourceStream.Position = 0;

        using var image = await SixLabors.ImageSharp.Image.LoadAsync(sourceStream, ct);
        image.Mutate(ctx =>
        {
            ctx.AutoOrient();
            if (image.Width > maxDimension || image.Height > maxDimension)
            {
                ctx.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(maxDimension, maxDimension)
                });
            }
        });

        await image.SaveAsJpegAsync(outputStream, new JpegEncoder { Quality = ImageThumbnailQuality }, ct);
    }

    private static bool CanGenerateImageThumbnail(string contentType)
        => !string.Equals(contentType, "image/svg+xml", StringComparison.OrdinalIgnoreCase);

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private static bool IsImageThumbnailCurrent(string thumbnailPath, DateTime sourceModifiedAt)
    {
        if (!File.Exists(thumbnailPath)) return false;

        var cachedModifiedAt = File.GetLastWriteTimeUtc(thumbnailPath);
        return cachedModifiedAt >= NormalizeUtc(sourceModifiedAt).AddSeconds(-1);
    }

    private static int NormalizeImageThumbnailMaxDimension(int maxDimension)
    {
        if (maxDimension <= 0) return DefaultImageThumbnailMaxDimension;
        return Math.Clamp(maxDimension, MinImageThumbnailMaxDimension, MaxImageThumbnailMaxDimension);
    }

    private string GetBlobImageThumbnailPath(string blobId, int maxDimension)
        => Path.Combine(ImageThumbnailDir, "entity-blobs", blobId[..2], $"{blobId}-{maxDimension}.jpg");

    private async Task<(Stream stream, string contentType, bool supportsRangeRequests)?> TryOpenZipBackedImageStreamAsync(
        string archivePath,
        IEnumerable<string?> entryCandidates,
        CancellationToken ct)
    {
        if (!File.Exists(archivePath)) return null;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in entryCandidates)
        {
            var normalizedEntry = NormalizeZipEntryPath(candidate);
            if (string.IsNullOrWhiteSpace(normalizedEntry) || !seen.Add(normalizedEntry))
                continue;

            try
            {
                var stream = await zipFileReader.ExtractEntryAsync(archivePath, normalizedEntry, ct);
                var contentType = ImageMimeTypes.GetValueOrDefault(Path.GetExtension(normalizedEntry), "application/octet-stream");
                return (stream, contentType, false);
            }
            catch (FileNotFoundException)
            {
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        return null;
    }

    private static IEnumerable<string?> GetZipEntryCandidates(string basename, string resolvedFilePath, string? expectedArchivePath = null)
    {
        if (TryParseArchivePath(resolvedFilePath, out var archivePath, out var entryPath)
            && (expectedArchivePath == null || string.Equals(archivePath, expectedArchivePath, StringComparison.OrdinalIgnoreCase)))
        {
            yield return entryPath;
        }

        yield return basename;
    }

    private static bool TryParseArchivePath(string path, out string archivePath, out string entryPath)
    {
        archivePath = string.Empty;
        entryPath = string.Empty;

        if (string.IsNullOrWhiteSpace(path)) return false;

        var normalizedPath = path.Replace('\\', '/');
        foreach (var extension in ArchiveExtensions)
        {
            var marker = extension + "/";
            var markerIndex = normalizedPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) continue;

            var archiveEnd = markerIndex + extension.Length;
            var candidateArchivePath = path[..archiveEnd];
            var candidateEntryPath = normalizedPath[(archiveEnd + 1)..];
            if (!File.Exists(candidateArchivePath) || string.IsNullOrWhiteSpace(candidateEntryPath))
                continue;

            archivePath = candidateArchivePath;
            entryPath = NormalizeZipEntryPath(candidateEntryPath);
            return !string.IsNullOrWhiteSpace(entryPath);
        }

        return false;
    }

    private static string NormalizeZipEntryPath(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Replace('\\', '/').Trim('/');

    public async Task GenerateSceneThumbnailAsync(int sceneId, double? atSeconds, CancellationToken ct)
    {
        var thumbPath = atSeconds.HasValue
            ? GetTimestampedThumbnailPath(sceneId, atSeconds.Value)
            : GetThumbnailPath(sceneId);

        // Delete existing thumbnail so we always regenerate on explicit request
        if (File.Exists(thumbPath))
        {
            try { File.Delete(thumbPath); } catch { /* best effort */ }
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

        var videoFile = await db.VideoFiles
            .Include(f => f.ParentFolder)
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.SceneId == sceneId, ct);

        if (videoFile == null) return;

        var filePath = videoFile.ParentFolder != null
            ? Path.Combine(videoFile.ParentFolder.Path, videoFile.Basename)
            : videoFile.Basename;

        if (!File.Exists(filePath)) return;

        var ffmpegPath = GetCachedFfmpegPath();
        if (ffmpegPath == null)
        {
            logger.LogWarning("FFmpeg not found. Cannot generate thumbnail for scene {SceneId}", sceneId);
            return;
        }

        var thumbDir = Path.GetDirectoryName(thumbPath)!;
        Directory.CreateDirectory(thumbDir);

        var seekSeconds = atSeconds ?? videoFile.Duration * 0.2;
        if (seekSeconds <= 0) seekSeconds = 1;

        // Limit concurrent FFmpeg processes
        var sem = GetFfmpegSemaphore();
        await sem.WaitAsync(ct);
        try
        {
            // Double-check after acquiring semaphore (another request may have generated it)
            if (File.Exists(thumbPath)) return;

            // Write to a temp file then rename to avoid readers seeing a partial file
            var tempPath = thumbPath + ".tmp";

            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-v error -threads 1 -ss {seekSeconds:F2} -i \"{filePath}\" -vframes 1 -q:v 2 -f image2 -y \"{tempPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            // Drain stderr to prevent buffer deadlocks
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15)); // timeout per thumbnail
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                logger.LogWarning("FFmpeg timed out for scene {SceneId} at {Seconds}s", sceneId, seekSeconds);
                return;
            }

            if (process.ExitCode != 0)
            {
                var stderr = await stderrTask;
                logger.LogWarning("FFmpeg failed for scene {SceneId}: {Error}", sceneId, stderr);
                try { File.Delete(tempPath); } catch { }
            }
            else if (File.Exists(tempPath))
            {
                File.Move(tempPath, thumbPath, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error generating thumbnail for scene {SceneId}", sceneId);
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>Get the path for a timestamp-specific cached thumbnail.</summary>
    public string GetTimestampedThumbnailPath(int sceneId, double seconds)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(sceneId)));
        var subDir = hash[..2];
        var secKey = ((int)seconds).ToString();
        return Path.Combine(ThumbnailDir, subDir, $"{sceneId}_t{secKey}.jpg");
    }

    public string GetThumbnailPathForScene(int sceneId) => GetThumbnailPath(sceneId);

    public string GetPreviewPath(int sceneId)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(sceneId)));
        return Path.Combine(PreviewDir, hash[..2], $"{sceneId}.mp4");
    }

    public string GetSpritePath(int sceneId)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(sceneId)));
        return Path.Combine(VttDir, hash[..2], $"{sceneId}_sprite.jpg");
    }

    public string GetSpriteVttPath(int sceneId)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(sceneId)));
        return Path.Combine(VttDir, hash[..2], $"{sceneId}_thumbs.vtt");
    }

    /// <summary>Generate a multi-segment video preview clip (mp4) for a scene.</summary>
    public async Task GenerateScenePreviewAsync(int sceneId, CancellationToken ct)
    {
        var previewPath = GetPreviewPath(sceneId);
        if (File.Exists(previewPath)) return;

        var (filePath, duration) = await GetSceneFileInfoAsync(sceneId, ct);
        if (filePath == null || duration <= 0) return;

        var ffmpegPath = GetCachedFfmpegPath();
        if (ffmpegPath == null)
        {
            logger.LogWarning("FFmpeg not found, cannot generate preview for scene {SceneId}", sceneId);
            return;
        }

        var previewDir = Path.GetDirectoryName(previewPath)!;
        Directory.CreateDirectory(previewDir);

        var tmpDir = Path.Combine(config.GeneratedPath, "tmp", $"preview_{sceneId}");
        Directory.CreateDirectory(tmpDir);

        var sem = GetFfmpegSemaphore();
        await sem.WaitAsync(ct);
        try
        {
            var segmentCount = PreviewSegments;
            var segmentDuration = PreviewSegmentDuration;

            // If video is too short for all segments, use a single full-video preview
            if (duration < segmentDuration * segmentCount)
            {
                await RunFfmpegAsync(ffmpegPath,
                    $"-v error -threads 1 -y -i \"{filePath}\" -max_muxing_queue_size 1024 -c:v {GetH264Encoder()} -vf \"scale={PreviewWidth}:-2\" -pix_fmt yuv420p -profile:v high -level 4.2 -preset {PreviewPreset} -crf {PreviewCrf} -an \"{previewPath}\"",
                    TimeSpan.FromMinutes(5), ct);
                return;
            }

            var interval = duration / segmentCount;
            var chunkFiles = new List<string>();

            for (int i = 0; i < segmentCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var seekTime = interval * i + interval * 0.5;
                if (seekTime >= duration) seekTime = duration - segmentDuration;
                if (seekTime < 0) seekTime = 0;

                var chunkPath = Path.Combine(tmpDir, $"chunk_{i:D3}.mp4");
                chunkFiles.Add(chunkPath);

                await RunFfmpegAsync(ffmpegPath,
                    $"-v error -threads 1 -y -ss {seekTime:F2} -i \"{filePath}\" -t {segmentDuration:F2} -max_muxing_queue_size 1024 -c:v {GetH264Encoder()} -vf \"scale={PreviewWidth}:-2\" -pix_fmt yuv420p -profile:v high -level 4.2 -preset {PreviewPreset} -crf {PreviewCrf} -an \"{chunkPath}\"",
                    TimeSpan.FromSeconds(60), ct);
            }

            // Create concat file — use forward slashes for FFmpeg compatibility on all platforms
            var concatListPath = Path.Combine(tmpDir, "concat.txt");
            var concatLines = chunkFiles
                .Where(File.Exists)
                .Select(f => $"file '{Path.GetFullPath(f).Replace('\\', '/')}'");
            await File.WriteAllTextAsync(concatListPath, string.Join("\n", concatLines), ct);

            // Concatenate chunks into final preview
            await RunFfmpegAsync(ffmpegPath,
                $"-v error -y -f concat -safe 0 -i \"{concatListPath}\" -c:v copy \"{previewPath}\"",
                TimeSpan.FromSeconds(30), ct);

            if (!File.Exists(previewPath))
                logger.LogWarning("Preview generation failed for scene {SceneId} - output not created", sceneId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error generating preview for scene {SceneId}", sceneId);
        }
        finally
        {
            sem.Release();
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
        }
    }

    /// <summary>Generate a sprite sheet (JPEG grid) and VTT timeline file for a scene.
    /// Uses in-process FFmpeg decoding with seek-based extraction — 5-17× faster than
    /// the fps filter approach which decodes the entire video.</summary>
    public async Task GenerateSceneSpriteAsync(int sceneId, CancellationToken ct)
    {
        var spritePath = GetSpritePath(sceneId);
        var vttPath = GetSpriteVttPath(sceneId);
        if (File.Exists(spritePath) && File.Exists(vttPath)) return;

        var (filePath, duration) = await GetSceneFileInfoAsync(sceneId, ct);
        if (filePath == null || duration <= 0) return;

        var ffmpegPath = GetCachedFfmpegPath();
        if (ffmpegPath == null) return;

        var spriteDir = Path.GetDirectoryName(spritePath)!;
        Directory.CreateDirectory(spriteDir);

        try
        {
            // Initialize FFmpeg.AutoGen with the same DLL directory as the ffmpeg binary
            FfmpegInProcess.EnsureInitialized(ffmpegPath, config.EnableFfmpegHwAccel);

            if (!FfmpegInProcess.IsAvailable)
            {
                logger.LogWarning("In-process FFmpeg not available (shared library mismatch) — sprite generation for scene {SceneId} skipped", sceneId);
                return;
            }

            // Calculate grid dimensions
            var frameCount = Math.Min(SpriteFrameCount, Math.Max(1, (int)(duration / 2)));
            var cols = (int)Math.Ceiling(Math.Sqrt(frameCount));
            var rows = (int)Math.Ceiling((double)frameCount / cols);
            var interval = duration / frameCount;

            // Build timestamps for seek-based extraction (center of each interval)
            var timestamps = new double[frameCount];
            for (var i = 0; i < frameCount; i++)
                timestamps[i] = interval * (i + 0.5);

            // Extract frames in-process — seeks directly to each position instead of
            // decoding the entire video with fps filter. Uses semaphore for concurrency.
            var sem = GetFfmpegSemaphore();
            await sem.WaitAsync(ct);
            Image<Rgba32>[]? frames;
            try
            {
                // thread_count=1: outer parallelism handles scene-level concurrency
                frames = FfmpegInProcess.ExtractFrames(filePath, timestamps, SpriteFrameSize, threadCount: 1, ct);
            }
            finally
            {
                sem.Release();
            }

            if (frames == null)
            {
                logger.LogWarning("Sprite generation failed for scene {SceneId} - frame extraction returned null", sceneId);
                return;
            }

            var fw = frames[0].Width;
            var fh = frames[0].Height;
            try
            {
                // Compose sprite sheet
                using var sheet = new Image<Rgba32>(fw * cols, fh * rows);
                for (var idx = 0; idx < frameCount; idx++)
                {
                    var x = fw * (idx % cols);
                    var y = fh * (idx / cols);
                    sheet.Mutate(ctx => ctx.DrawImage(frames[idx], new Point(x, y), 1f));
                }

                await sheet.SaveAsJpegAsync(spritePath, new JpegEncoder { Quality = 75 }, ct);
            }
            finally
            {
                foreach (var f in frames) f?.Dispose();
            }

            if (!File.Exists(spritePath))
            {
                logger.LogWarning("Sprite generation failed for scene {SceneId}", sceneId);
                return;
            }

            var thumbWidth = fw;
            var thumbHeight = fh;

            // Generate VTT file
            var vttBuilder = new StringBuilder();
            vttBuilder.AppendLine("WEBVTT");
            vttBuilder.AppendLine();

            var spriteFileName = Path.GetFileName(spritePath);
            for (int i = 0; i < frameCount; i++)
            {
                var startTime = i * interval;
                var endTime = Math.Min((i + 1) * interval, duration);
                var col = i % cols;
                var row = i / cols;
                var x = col * thumbWidth;
                var y = row * thumbHeight;

                vttBuilder.AppendLine($"{FormatVttTime(startTime)} --> {FormatVttTime(endTime)}");
                vttBuilder.AppendLine($"{spriteFileName}#xywh={x},{y},{thumbWidth},{thumbHeight}");
                vttBuilder.AppendLine();
            }

            await File.WriteAllTextAsync(vttPath, vttBuilder.ToString(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error generating sprite for scene {SceneId}", sceneId);
        }
    }

    private static string FormatVttTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    private async Task<(string? filePath, double duration)> GetSceneFileInfoAsync(int sceneId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

        var videoFile = await db.VideoFiles
            .Include(f => f.ParentFolder)
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.SceneId == sceneId, ct);

        if (videoFile == null) return (null, 0);

        var filePath = videoFile.ParentFolder != null
            ? Path.Combine(videoFile.ParentFolder.Path, videoFile.Basename)
            : videoFile.Basename;

        return File.Exists(filePath) ? (filePath, videoFile.Duration) : (null, 0);
    }

    private async Task RunFfmpegAsync(string ffmpegPath, string args, TimeSpan timeout, CancellationToken ct)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            logger.LogWarning("FFmpeg timed out: {Args}", args[..Math.Min(200, args.Length)]);
            return;
        }

        if (process.ExitCode != 0)
        {
            var stderr = await stderrTask;
            logger.LogWarning("FFmpeg failed (exit {Code}): {Error}", process.ExitCode, stderr[..Math.Min(500, stderr.Length)]);
        }
    }

    public string StartGenerateAllThumbnails()
    {
        return jobService.Enqueue("generate_thumbnails", "Generating thumbnails", async (progress, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

            var sceneIds = await db.Scenes.Select(s => s.Id).ToListAsync(ct);
            var total = sceneIds.Count;
            var processed = 0;

            foreach (var sceneId in sceneIds)
            {
                ct.ThrowIfCancellationRequested();
                processed++;
                progress.Report((double)processed / total, $"Scene {processed}/{total}");

                var thumbPath = GetThumbnailPath(sceneId);
                if (File.Exists(thumbPath)) continue;

                await GenerateSceneThumbnailAsync(sceneId, null, ct);
            }

            logger.LogInformation("Generated thumbnails for {Count} scenes", total);
        });
    }

    private string GetThumbnailPath(int sceneId)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(sceneId)));
        var subDir = hash[..2];
        return Path.Combine(ThumbnailDir, subDir, $"{sceneId}.jpg");
    }

    private string GetImageThumbnailPath(int imageId, int maxDimension)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(imageId)));
        var subDir = hash[..2];
        return Path.Combine(ImageThumbnailDir, subDir, $"{imageId}_m{maxDimension}.jpg");
    }

    private string? GetCachedFfmpegPath()
    {
        if (_ffmpegSearched) return _cachedFfmpegPath;
        _cachedFfmpegPath = FindFfmpeg();
        _ffmpegSearched = true;
        return _cachedFfmpegPath;
    }

    private string? FindFfmpeg()
    {
        if (!string.IsNullOrEmpty(config.FfmpegPath) && File.Exists(config.FfmpegPath))
            return config.FfmpegPath;

        // Search PATH
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            var ffmpeg = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (File.Exists(ffmpeg)) return ffmpeg;
        }

        return null;
    }

    /// <summary>Get the best available H.264 encoder, preferring HW-accelerated encoders.</summary>
    private string GetH264Encoder()
    {
        if (_hwEncoderSearched) return _hwEncoder ?? "libx264";
        _hwEncoderSearched = true;

        var ffmpegPath = GetCachedFfmpegPath();
        if (ffmpegPath == null) return "libx264";

        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-hide_banner -encoders",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            // Prefer NVENC > QSV > AMF > libx264
            string[] hwEncoders = ["h264_nvenc", "h264_qsv", "h264_amf", "h264_videotoolbox"];
            foreach (var enc in hwEncoders)
            {
                if (output.Contains(enc, StringComparison.OrdinalIgnoreCase))
                {
                    _hwEncoder = enc;
                    logger.LogInformation("Using HW-accelerated H.264 encoder: {Encoder}", enc);
                    return enc;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to detect HW encoders, falling back to libx264");
        }

        return "libx264";
    }
}
