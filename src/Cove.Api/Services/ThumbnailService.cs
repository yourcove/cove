using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Cove.Core.Common;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Galleries.Zip;
using Cove.Core.Helpers;
using Cove.Core.Interfaces;
using Cove.Data;

namespace Cove.Api.Services;

public interface IThumbnailService
{
    Task<string?> GetVideoThumbnailPathAsync(int videoId, CancellationToken ct = default);
    Task<string?> GetImageFilePathAsync(int imageId, CancellationToken ct = default);
    Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageStreamAsync(int imageId, CancellationToken ct = default);
    Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageThumbnailStreamAsync(int imageId, int maxDimension = 640, CancellationToken ct = default);
    Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetBlobImageThumbnailStreamAsync(string blobId, int maxDimension = 640, CancellationToken ct = default);
    Task DeleteVideoGeneratedFilesAsync(int videoId, CancellationToken ct = default);
    Task DeleteImageGeneratedFilesAsync(int imageId, CancellationToken ct = default);
    Task DeleteBlobGeneratedFilesAsync(string blobId, CancellationToken ct = default);
    Task GenerateVideoThumbnailAsync(int videoId, double? atSeconds = null, CancellationToken ct = default);
    async Task<bool> RegenerateVideoThumbnailAsync(int videoId, double? atSeconds = null, CancellationToken ct = default)
    {
        await GenerateVideoThumbnailAsync(videoId, atSeconds, ct);
        return true;
    }
    Task<bool> GenerateImageThumbnailAsync(int imageId, int maxDimension = 640, bool overwrite = false, CancellationToken ct = default);
    Task GenerateVideoPreviewAsync(int videoId, CancellationToken ct = default);
    async Task<bool> RegenerateVideoPreviewAsync(int videoId, CancellationToken ct = default)
    {
        await GenerateVideoPreviewAsync(videoId, ct);
        return true;
    }
    Task GenerateSegmentAnimatedPreviewAsync(int videoId, double startSec, double? endSec = null, CancellationToken ct = default);
    Task GenerateVideoSpriteAsync(int videoId, CancellationToken ct = default);
    async Task<bool> RegenerateVideoSpriteAsync(int videoId, CancellationToken ct = default)
    {
        await GenerateVideoSpriteAsync(videoId, ct);
        return true;
    }
    string GetThumbnailPathForVideo(int videoId);
    string GetTimestampedThumbnailPath(int videoId, double seconds);
    string GetSegmentAnimatedPreviewPath(int videoId, double seconds);
    string GetPreviewPath(int videoId);
    string GetSpritePath(int videoId);
    string GetSpriteVttPath(int videoId);
    string StartGenerateAllThumbnails();
}

public class ThumbnailService(
    IServiceScopeFactory scopeFactory,
    IJobService jobService,
    CoveConfiguration config,
    IZipFileReader zipFileReader,
    IBlobService blobService,
    ILogger<ThumbnailService> logger,
    VideoGeneratedAssetCoordinator? generatedAssetCoordinator = null,
    FfmpegConcurrencyLimiter? ffmpegConcurrencyLimiter = null,
    HardwareEncodeSessionGate? hwEncodeSessionGate = null) : IThumbnailService, IVideoAssetGenerator
{
    private readonly VideoGeneratedAssetCoordinator _generatedAssetCoordinator = generatedAssetCoordinator ?? new();

    // Shared with the rest of generation so one budget covers every decode in flight, not one per
    // service. Tests that build this type directly get a private limiter sized from their config.
    private readonly FfmpegConcurrencyLimiter ffmpegConcurrency = ffmpegConcurrencyLimiter ?? new FfmpegConcurrencyLimiter(config);

    // Shared with library conversion so the two never overrun the GPU's encode-session limit together.
    private readonly HardwareEncodeSessionGate _hwEncodeSessionGate = hwEncodeSessionGate ?? new(config);
    private string ThumbnailDir => Path.Combine(config.GeneratedPath, "screenshots");
    private string ImageThumbnailDir => Path.Combine(config.GeneratedPath, "thumbnails");
    private string PreviewDir => Path.Combine(config.GeneratedPath, "previews");
    private string SegmentPreviewDir => Path.Combine(config.GeneratedPath, "segment-previews");
    private string VttDir => Path.Combine(config.GeneratedPath, "vtt");
    private SemaphoreSlim? _ffmpegSemaphore;
    private int _semaphoreCapacity;
    private string? _cachedFfmpegPath;
    private bool _ffmpegSearched;
    private string? _hwEncoder;
    private string? _hwEncoderFingerprint;
    private readonly object _hwEncoderLock = new();
    private static readonly SemaphoreSlim[] SpriteGenerationLocks =
        Enumerable.Range(0, 257).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

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
        [".avif"] = "image/avif", [".heic"] = "image/heic", [".heif"] = "image/heif",
        [".qoi"] = "image/qoi", [".tga"] = "image/x-tga", [".pbm"] = "image/x-portable-bitmap",
        [".pgm"] = "image/x-portable-graymap", [".ppm"] = "image/x-portable-pixmap",
        [".pam"] = "image/x-portable-anymap",
    };
    private static readonly HashSet<string> ImageSharpImageContentTypes =
    [
        "image/jpeg",
        "image/png",
        "image/gif",
        "image/webp",
        "image/bmp",
        "image/tiff",
        "image/qoi",
        "image/x-tga",
        "image/x-portable-bitmap",
        "image/x-portable-graymap",
        "image/x-portable-pixmap",
        "image/x-portable-anymap",
    ];
    private static readonly Dictionary<string, string> ImageContentTypeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["image/bmp"] = ".bmp",
        ["image/tiff"] = ".tiff",
        ["image/avif"] = ".avif",
        ["image/heic"] = ".heic",
        ["image/heif"] = ".heif",
        ["image/qoi"] = ".qoi",
        ["image/x-qoi"] = ".qoi",
        ["image/x-tga"] = ".tga",
        ["image/x-portable-bitmap"] = ".pbm",
        ["image/x-portable-graymap"] = ".pgm",
        ["image/x-portable-pixmap"] = ".ppm",
        ["image/x-portable-anymap"] = ".pam",
    };
    private static readonly string[] ArchiveExtensions = [".zip", ".cbz"];
    private static readonly TimeSpan ImageThumbnailFfmpegTimeout = TimeSpan.FromSeconds(30);

    // Preview generation defaults (matching original Cove)
    private const int DefaultPreviewSegments = 12;
    private const double DefaultPreviewSegmentDuration = 0.75;
    private const int PreviewWidth = 640;
    private const int VrThumbnailWidth = 1920;
    private const string PreviewPreset = "fast";
    private const int PreviewCrf = 21;
    private const double SegmentPreviewDefaultDuration = 3.0;
    private const double SegmentPreviewMaxDuration = 5.0;
    private const int SegmentPreviewWidth = 360;
    private const int SegmentPreviewFps = 12;
    private const int DefaultImageThumbnailMaxDimension = 640;
    private const int MinImageThumbnailMaxDimension = 64;
    private const int MaxImageThumbnailMaxDimension = 4096;
    private const int ImageThumbnailQuality = 80;
    private const int VideoThumbnailQuality = 90;
    private const string ImageThumbnailCacheVersion = "v2";

    private readonly record struct ImageThumbnailOutput(string Extension, string ContentType);

    // Sprite generation defaults
    private const int SpriteFrameCount = 81; // 9x9 grid
    private const int SpriteFrameSize = 160; // px

    public Task<string?> GetVideoThumbnailPathAsync(int videoId, CancellationToken ct)
    {
        // Cover images are only created by an explicit generate task, never on-demand.
        var thumbPath = GetThumbnailPath(videoId);
        return Task.FromResult(File.Exists(thumbPath) ? thumbPath : null);
    }

    public async Task<string?> GetImageFilePathAsync(int imageId, CancellationToken ct)
    {
        var imageFile = await GetImageFileRecordAsync(imageId, ct);

        if (imageFile == null) return null;

        var filePath = FilesystemPaths.ToNativePath(imageFile.Path);

        return File.Exists(filePath) ? filePath : null;
    }

    public async Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageStreamAsync(int imageId, CancellationToken ct)
    {
        var imageFile = await GetImageFileRecordAsync(imageId, ct);

        if (imageFile == null) return null;

        return await OpenImageSourceStreamAsync(imageFile, ct);
    }

    public Task DeleteVideoGeneratedFilesAsync(int videoId, CancellationToken ct = default)
    {
        DeleteFileIfExists(GetThumbnailPath(videoId));
        DeleteFileIfExists(GetPreviewPath(videoId));
        DeleteFileIfExists(GetSpritePath(videoId));
        DeleteFileIfExists(GetSpriteVttPath(videoId));
        DeleteFilesByPattern(Path.GetDirectoryName(GetTimestampedThumbnailPath(videoId, 0))!, $"{videoId}_t*.jpg");
        DeleteFilesByPattern(Path.GetDirectoryName(GetSegmentAnimatedPreviewPath(videoId, 0))!, $"{videoId}_t*.webp");
        return Task.CompletedTask;
    }

    public Task DeleteImageGeneratedFilesAsync(int imageId, CancellationToken ct = default)
    {
        DeleteFilesByPattern(Path.GetDirectoryName(GetImageThumbnailBasePath(imageId, 1))!, $"{imageId}_m*.*");
        return Task.CompletedTask;
    }

    public Task DeleteBlobGeneratedFilesAsync(string blobId, CancellationToken ct = default)
    {
        DeleteFilesByPattern(Path.GetDirectoryName(GetBlobImageThumbnailBasePath(blobId, 1))!, $"{blobId}_m*.*");
        return Task.CompletedTask;
    }

    public async Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageThumbnailStreamAsync(int imageId, int maxDimension, CancellationToken ct)
    {
        maxDimension = NormalizeImageThumbnailMaxDimension(maxDimension);

        var imageFile = await GetImageFileRecordAsync(imageId, ct);
        if (imageFile == null) return null;

        var thumbnailBasePath = GetImageThumbnailBasePath(imageId, maxDimension);
        var declaredThumbnailOutput = GetImageThumbnailOutput(GetDeclaredImageContentType(imageFile));
        var declaredThumbnailPath = GetImageThumbnailPath(thumbnailBasePath, declaredThumbnailOutput);
        if (config.WriteImageThumbnails && IsImageThumbnailCurrent(declaredThumbnailPath, imageFile.ModTime))
        {
            var cachedStream = FileReadRace.TryOpenRead(declaredThumbnailPath, pathWasObserved: true);
            if (cachedStream != null)
                return (cachedStream, declaredThumbnailOutput.ContentType, true);
        }

        var source = await OpenImageSourceStreamAsync(imageFile, ct);
        if (source == null) return null;

        var effectiveContentType = await GetEffectiveImageContentTypeAsync(source.Value.stream, source.Value.contentType, ct);
        var sourceContentType = effectiveContentType ?? source.Value.contentType;
        var thumbnailOutput = GetImageThumbnailOutput(sourceContentType);
        var thumbnailPath = GetImageThumbnailPath(thumbnailBasePath, thumbnailOutput);

        if (config.WriteImageThumbnails && !string.Equals(declaredThumbnailPath, thumbnailPath, StringComparison.OrdinalIgnoreCase) && IsImageThumbnailCurrent(thumbnailPath, imageFile.ModTime))
        {
            FileStream? cachedStream;
            try
            {
                cachedStream = FileReadRace.TryOpenRead(thumbnailPath, pathWasObserved: true);
            }
            catch
            {
                await source.Value.stream.DisposeAsync();
                throw;
            }

            if (cachedStream != null)
            {
                await source.Value.stream.DisposeAsync();
                return (cachedStream, thumbnailOutput.ContentType, true);
            }
        }

        if (!CanGenerateImageThumbnail(sourceContentType))
            return (source.Value.stream, sourceContentType, source.Value.supportsRangeRequests);

        var sourceFilePath = TryGetDirectImageSourcePath(imageFile);

        try
        {
            if (config.WriteImageThumbnails)
            {
                if (await TryGenerateImageThumbnailFileAsync(source.Value.stream, sourceFilePath, sourceContentType, thumbnailPath, imageFile.ModTime, maxDimension, thumbnailOutput, ct))
                {
                    await source.Value.stream.DisposeAsync();
                    DeleteAlternateImageThumbnailVariants(thumbnailBasePath, thumbnailPath);

                    var cachedStream = FileReadRace.TryOpenRead(thumbnailPath, pathWasObserved: true);
                    return cachedStream == null ? null : (cachedStream, thumbnailOutput.ContentType, true);
                }

                if (source.Value.stream.CanSeek)
                    source.Value.stream.Position = 0;
                logger.LogTrace("Skipping thumbnail generation for unsupported image format {ImageId}", imageId);
                return (source.Value.stream, sourceContentType, source.Value.supportsRangeRequests);
            }

            var thumbnailStream = await TryCreateImageThumbnailStreamAsync(source.Value.stream, sourceFilePath, sourceContentType, maxDimension, thumbnailOutput, ct);
            if (thumbnailStream != null)
            {
                await source.Value.stream.DisposeAsync();
                return (thumbnailStream, thumbnailOutput.ContentType, false);
            }

            if (source.Value.stream.CanSeek)
                source.Value.stream.Position = 0;
            logger.LogTrace("Skipping thumbnail generation for unsupported image format {ImageId}", imageId);
            return (source.Value.stream, sourceContentType, source.Value.supportsRangeRequests);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Falling back to original image stream for thumbnail {ImageId}", imageId);
            if (source.Value.stream.CanSeek)
                source.Value.stream.Position = 0;
            return (source.Value.stream, sourceContentType, source.Value.supportsRangeRequests);
        }
    }

    public async Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetBlobImageThumbnailStreamAsync(string blobId, int maxDimension, CancellationToken ct)
    {
        maxDimension = NormalizeImageThumbnailMaxDimension(maxDimension);

        var thumbnailBasePath = GetBlobImageThumbnailBasePath(blobId, maxDimension);
        var cachedThumbnail = FindExistingImageThumbnail(thumbnailBasePath);
        if (cachedThumbnail != null)
        {
            var cachedStream = FileReadRace.TryOpenRead(cachedThumbnail.Value.path, pathWasObserved: true);
            if (cachedStream != null)
                return (cachedStream, cachedThumbnail.Value.contentType, true);
        }

        var source = await blobService.GetBlobAsync(blobId, ct);
        if (source == null) return null;

        var effectiveContentType = await GetEffectiveImageContentTypeAsync(source.Value.Stream, source.Value.ContentType, ct);
        var sourceContentType = effectiveContentType ?? source.Value.ContentType;
        var thumbnailOutput = GetImageThumbnailOutput(sourceContentType);
        var thumbnailPath = GetImageThumbnailPath(thumbnailBasePath, thumbnailOutput);

        if (!CanGenerateImageThumbnail(sourceContentType))
            return (source.Value.Stream, sourceContentType, source.Value.Stream.CanSeek);

        try
        {
            if (await TryGenerateImageThumbnailFileAsync(source.Value.Stream, null, sourceContentType, thumbnailPath, DateTime.UtcNow, maxDimension, thumbnailOutput, ct))
            {
                await source.Value.Stream.DisposeAsync();
                DeleteAlternateImageThumbnailVariants(thumbnailBasePath, thumbnailPath);

                var cachedStream = FileReadRace.TryOpenRead(thumbnailPath, pathWasObserved: true);
                return cachedStream == null ? null : (cachedStream, thumbnailOutput.ContentType, true);
            }

            if (source.Value.Stream.CanSeek)
                source.Value.Stream.Position = 0;
            logger.LogTrace("Skipping cached blob thumbnail generation for unsupported image format {BlobId}", blobId);
            return (source.Value.Stream, sourceContentType, source.Value.Stream.CanSeek);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Falling back to original blob stream for entity image thumbnail {BlobId}", blobId);
            if (source.Value.Stream.CanSeek)
                source.Value.Stream.Position = 0;
            return (source.Value.Stream, sourceContentType, source.Value.Stream.CanSeek);
        }
    }

    public async Task<bool> GenerateImageThumbnailAsync(int imageId, int maxDimension, bool overwrite, CancellationToken ct)
    {
        maxDimension = NormalizeImageThumbnailMaxDimension(maxDimension);

        var imageFile = await GetImageFileRecordAsync(imageId, ct);
        if (imageFile == null) return false;

        var source = await OpenImageSourceStreamAsync(imageFile, ct);
        if (source == null)
            return false;

        var effectiveContentType = await GetEffectiveImageContentTypeAsync(source.Value.stream, source.Value.contentType, ct);
        if (!CanGenerateImageThumbnail(effectiveContentType ?? source.Value.contentType))
            return false;

        var thumbnailBasePath = GetImageThumbnailBasePath(imageId, maxDimension);
        var thumbnailOutput = GetImageThumbnailOutput(effectiveContentType ?? source.Value.contentType);
        var thumbnailPath = GetImageThumbnailPath(thumbnailBasePath, thumbnailOutput);
        if (!overwrite && IsImageThumbnailCurrent(thumbnailPath, imageFile.ModTime))
            return true;

        await using (source.Value.stream)
        {
            if (!await TryGenerateImageThumbnailFileAsync(source.Value.stream, TryGetDirectImageSourcePath(imageFile), effectiveContentType ?? source.Value.contentType, thumbnailPath, imageFile.ModTime, maxDimension, thumbnailOutput, ct))
            {
                logger.LogTrace("Skipping generated thumbnail for unsupported image format {ImageId}", imageId);
                return false;
            }

            DeleteAlternateImageThumbnailVariants(thumbnailBasePath, thumbnailPath);
            return true;
        }
    }

    private async Task<ImageFile?> GetImageFileRecordAsync(int imageId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

        return await db.Images
            .Where(image => image.Id == imageId)
            .SelectMany(image => image.Files)
            .Include(f => f.ParentFolder)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);
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

        var ext = Path.GetExtension(resolvedFilePath);
        var contentType = ImageMimeTypes.GetValueOrDefault(ext, "application/octet-stream");
        if (!File.Exists(resolvedFilePath)) return null;

        var stream = FileReadRace.TryOpenRead(resolvedFilePath, pathWasObserved: true);
        return stream == null ? null : (stream, contentType, true);
    }

    private string? TryGetDirectImageSourcePath(ImageFile imageFile)
    {
        if (imageFile.ZipFileId.HasValue)
            return null;

        if (TryParseArchivePath(imageFile.Path, out _, out _))
            return null;

        return File.Exists(imageFile.Path) ? imageFile.Path : null;
    }

    private async Task<bool> TryGenerateImageThumbnailFileAsync(Stream sourceStream, string? sourceFilePath, string? contentType, string thumbnailPath, DateTime sourceModifiedAt, int maxDimension, ImageThumbnailOutput thumbnailOutput, CancellationToken ct)
    {
        try
        {
            await GenerateImageThumbnailFileWithImageSharpAsync(sourceStream, thumbnailPath, sourceModifiedAt, maxDimension, thumbnailOutput, ct);
            return true;
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException)
        {
            // ImageSharp can't handle this file (unrecognized format, or an unsupported variant such as
            // a JPEG using lossless arithmetic coding). Fall back to ffmpeg's decoders; if that also
            // can't decode it, the fallback returns false and the caller skips the thumbnail.
            return await TryGenerateImageThumbnailFileWithFfmpegAsync(sourceStream, sourceFilePath, contentType, thumbnailPath, sourceModifiedAt, maxDimension, thumbnailOutput, ct);
        }
    }

    private async Task GenerateImageThumbnailFileWithImageSharpAsync(Stream sourceStream, string thumbnailPath, DateTime sourceModifiedAt, int maxDimension, ImageThumbnailOutput thumbnailOutput, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(thumbnailPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = thumbnailPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await WriteImageThumbnailAsync(sourceStream, output, maxDimension, thumbnailOutput, ct);
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

    private async Task<bool> TryGenerateImageThumbnailFileWithFfmpegAsync(Stream sourceStream, string? sourceFilePath, string? contentType, string thumbnailPath, DateTime sourceModifiedAt, int maxDimension, ImageThumbnailOutput thumbnailOutput, CancellationToken ct)
    {
        var ffmpegPath = GetCachedFfmpegPath();
        if (ffmpegPath == null)
            return false;

        string? tempInputPath = null;
        try
        {
            var inputPath = sourceFilePath;
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                if (sourceStream.CanSeek)
                    sourceStream.Position = 0;

                var extension = GetImageExtensionForContentType(contentType);
                tempInputPath = Path.Combine(Path.GetTempPath(), $"cove-image-thumb-{Guid.NewGuid():N}{extension}");
                await using (var tempInput = new FileStream(tempInputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await sourceStream.CopyToAsync(tempInput, ct);
                }
                inputPath = tempInputPath;
            }

            var directory = Path.GetDirectoryName(thumbnailPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var tempOutputPath = thumbnailPath + $".{Guid.NewGuid():N}.tmp{thumbnailOutput.Extension}";
            try
            {
                var scaleFilter = $"scale='min(iw,{maxDimension})':'min(ih,{maxDimension})':force_original_aspect_ratio=decrease";
                var args = thumbnailOutput.ContentType == "image/png"
                    ? $"-v error -y -i \"{inputPath}\" -vf \"{scaleFilter}\" -frames:v 1 -f image2 \"{tempOutputPath}\""
                    // -pix_fmt yuvj420p forces full-range JPEG output so the mjpeg encoder doesn't reject
                    // limited-range YUV sources ("Non full-range YUV is non-standard", ffmpeg exit 234).
                    : $"-v error -y -i \"{inputPath}\" -vf \"{scaleFilter}\" -frames:v 1 -q:v 3 -pix_fmt yuvj420p -f image2 \"{tempOutputPath}\"";
                if (!await TryRunFfmpegAsync(ffmpegPath, args, ImageThumbnailFfmpegTimeout, ct))
                    return false;

                if (!File.Exists(tempOutputPath))
                    return false;

                File.Move(tempOutputPath, thumbnailPath, overwrite: true);
                File.SetLastWriteTimeUtc(thumbnailPath, NormalizeUtc(sourceModifiedAt));
                return true;
            }
            finally
            {
                if (File.Exists(tempOutputPath))
                {
                    try { File.Delete(tempOutputPath); } catch { }
                }
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempInputPath) && File.Exists(tempInputPath))
            {
                try { File.Delete(tempInputPath); } catch { }
            }

            if (sourceStream.CanSeek)
                sourceStream.Position = 0;
        }
    }

    private async Task<MemoryStream?> TryCreateImageThumbnailStreamAsync(Stream sourceStream, string? sourceFilePath, string? contentType, int maxDimension, ImageThumbnailOutput thumbnailOutput, CancellationToken ct)
    {
        if (sourceStream.CanSeek)
            sourceStream.Position = 0;

        var thumbnailStream = new MemoryStream();
        try
        {
            if (CanUseImageSharpForContentType(contentType))
            {
                await WriteImageThumbnailAsync(sourceStream, thumbnailStream, maxDimension, thumbnailOutput, ct);
                thumbnailStream.Position = 0;
                return thumbnailStream;
            }

            var ffmpegPath = GetCachedFfmpegPath();
            if (ffmpegPath == null)
            {
                await thumbnailStream.DisposeAsync();
                return null;
            }

            string? tempInputPath = null;
            var tempOutputPath = Path.Combine(Path.GetTempPath(), $"cove-image-thumb-out-{Guid.NewGuid():N}{thumbnailOutput.Extension}");
            try
            {
                var inputPath = sourceFilePath;
                if (string.IsNullOrWhiteSpace(inputPath))
                {
                    var extension = GetImageExtensionForContentType(contentType);
                    tempInputPath = Path.Combine(Path.GetTempPath(), $"cove-image-thumb-in-{Guid.NewGuid():N}{extension}");
                    await using (var tempInput = new FileStream(tempInputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        await sourceStream.CopyToAsync(tempInput, ct);
                    }
                    inputPath = tempInputPath;
                }

                var scaleFilter = $"scale='min(iw,{maxDimension})':'min(ih,{maxDimension})':force_original_aspect_ratio=decrease";
                var args = thumbnailOutput.ContentType == "image/png"
                    ? $"-v error -y -i \"{inputPath}\" -vf \"{scaleFilter}\" -frames:v 1 \"{tempOutputPath}\""
                    // -pix_fmt yuvj420p forces full-range JPEG output so the mjpeg encoder doesn't reject
                    // limited-range YUV sources ("Non full-range YUV is non-standard", ffmpeg exit 234).
                    : $"-v error -y -i \"{inputPath}\" -vf \"{scaleFilter}\" -frames:v 1 -q:v 3 -pix_fmt yuvj420p \"{tempOutputPath}\"";
                if (!await TryRunFfmpegAsync(ffmpegPath, args, ImageThumbnailFfmpegTimeout, ct) || !File.Exists(tempOutputPath))
                {
                    await thumbnailStream.DisposeAsync();
                    return null;
                }

                await using (var generated = new FileStream(tempOutputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
                {
                    await generated.CopyToAsync(thumbnailStream, ct);
                }

                thumbnailStream.Position = 0;
                return thumbnailStream;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(tempInputPath) && File.Exists(tempInputPath))
                {
                    try { File.Delete(tempInputPath); } catch { }
                }

                if (File.Exists(tempOutputPath))
                {
                    try { File.Delete(tempOutputPath); } catch { }
                }

                if (sourceStream.CanSeek)
                    sourceStream.Position = 0;
            }
        }
        catch
        {
            await thumbnailStream.DisposeAsync();
            throw;
        }
    }

    private static async Task<string?> GetEffectiveImageContentTypeAsync(Stream sourceStream, string? contentType, CancellationToken ct)
    {
        var detected = await DetectImageContentTypeAsync(sourceStream, ct);
        return detected ?? NormalizeContentType(contentType);
    }

    private static async Task<string?> DetectImageContentTypeAsync(Stream sourceStream, CancellationToken ct)
    {
        if (!sourceStream.CanSeek)
            return null;

        var originalPosition = sourceStream.Position;
        try
        {
            sourceStream.Position = 0;
            var header = new byte[Math.Min(256, (int)Math.Max(0, Math.Min(sourceStream.Length, 256)))];
            var bytesRead = await sourceStream.ReadAsync(header.AsMemory(0, header.Length), ct);
            return DetectImageContentType(header.AsSpan(0, bytesRead));
        }
        finally
        {
            sourceStream.Position = originalPosition;
        }
    }

    private static string? DetectImageContentType(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return null;

        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return "image/png";

        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return "image/jpeg";

        if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38)
            return "image/gif";

        if (data.Length >= 12 && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
            return "image/webp";

        if (data[0] == 0x42 && data[1] == 0x4D)
            return "image/bmp";

        if (data.Length >= 12 && data[4] == 0x66 && data[5] == 0x74 && data[6] == 0x79 && data[7] == 0x70)
        {
            var brand = System.Text.Encoding.ASCII.GetString(data[8..12]);
            if (brand.StartsWith("avif", StringComparison.OrdinalIgnoreCase)) return "image/avif";
            if (brand.StartsWith("heic", StringComparison.OrdinalIgnoreCase)) return "image/heic";
            if (brand.StartsWith("heif", StringComparison.OrdinalIgnoreCase)) return "image/heif";
        }

        if (LooksLikeSvg(data))
            return "image/svg+xml";

        return null;
    }

    private static bool LooksLikeSvg(ReadOnlySpan<byte> data)
    {
        var head = Encoding.UTF8.GetString(data[..Math.Min(data.Length, 256)]);
        var trimmed = head.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return trimmed.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            || (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("<svg", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task WriteImageThumbnailAsync(Stream sourceStream, Stream outputStream, int maxDimension, ImageThumbnailOutput thumbnailOutput, CancellationToken ct)
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

        if (thumbnailOutput.ContentType == "image/png")
        {
            await image.SaveAsPngAsync(outputStream, new PngEncoder(), ct);
            return;
        }

        await image.SaveAsJpegAsync(outputStream, new JpegEncoder { Quality = ImageThumbnailQuality }, ct);
    }

    private static bool CanUseImageSharpForContentType(string? contentType)
    {
        var normalized = NormalizeContentType(contentType);
        return normalized != null && ImageSharpImageContentTypes.Contains(normalized);
    }

    private static bool CanGenerateImageThumbnail(string contentType)
    {
        var normalized = NormalizeContentType(contentType);
        return normalized != null
            && normalized.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalized, "image/svg+xml", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return null;

        return contentType.Split(';', 2)[0].Trim();
    }

    private static string GetImageExtensionForContentType(string? contentType)
    {
        var normalized = NormalizeContentType(contentType);
        return normalized != null && ImageContentTypeExtensions.TryGetValue(normalized, out var extension)
            ? extension
            : ".img";
    }

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private static bool IsImageThumbnailCurrent(string thumbnailPath, DateTime sourceModifiedAt)
    {
        if (!File.Exists(thumbnailPath)) return false;

        try
        {
            var cachedModifiedAt = File.GetLastWriteTimeUtc(thumbnailPath);
            return cachedModifiedAt >= NormalizeUtc(sourceModifiedAt).AddSeconds(-1);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (UnauthorizedAccessException ex) when (FileReadRace.IsWindowsDeletionRace(ex, thumbnailPath))
        {
            return false;
        }
    }

    private static int NormalizeImageThumbnailMaxDimension(int maxDimension)
    {
        if (maxDimension <= 0) return DefaultImageThumbnailMaxDimension;
        return Math.Clamp(maxDimension, MinImageThumbnailMaxDimension, MaxImageThumbnailMaxDimension);
    }

    private static ImageThumbnailOutput GetImageThumbnailOutput(string? contentType)
        => string.Equals(NormalizeContentType(contentType), "image/jpeg", StringComparison.OrdinalIgnoreCase)
            ? new ImageThumbnailOutput(".jpg", "image/jpeg")
            : new ImageThumbnailOutput(".png", "image/png");

    private static string GetImageThumbnailPath(string thumbnailBasePath, ImageThumbnailOutput thumbnailOutput)
        => thumbnailBasePath + thumbnailOutput.Extension;

    private static (string path, string contentType)? FindExistingImageThumbnail(string thumbnailBasePath)
    {
        foreach (var candidate in EnumerateImageThumbnailCandidates(thumbnailBasePath))
        {
            if (File.Exists(candidate.path))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<(string path, string contentType)> EnumerateImageThumbnailCandidates(string thumbnailBasePath)
    {
        yield return (thumbnailBasePath + ".png", "image/png");
        yield return (thumbnailBasePath + ".jpg", "image/jpeg");
    }

    private static void DeleteAlternateImageThumbnailVariants(string thumbnailBasePath, string currentThumbnailPath)
    {
        foreach (var candidate in EnumerateImageThumbnailCandidates(thumbnailBasePath))
        {
            if (string.Equals(candidate.path, currentThumbnailPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate.path))
                continue;

            try { File.Delete(candidate.path); } catch { }
        }
    }

    private static string? GetDeclaredImageContentType(ImageFile imageFile)
    {
        var basenameExtension = Path.GetExtension(imageFile.Basename);
        if (!string.IsNullOrWhiteSpace(basenameExtension) && ImageMimeTypes.TryGetValue(basenameExtension, out var basenameContentType))
            return basenameContentType;

        var pathExtension = Path.GetExtension(imageFile.Path);
        if (!string.IsNullOrWhiteSpace(pathExtension) && ImageMimeTypes.TryGetValue(pathExtension, out var pathContentType))
            return pathContentType;

        if (string.IsNullOrWhiteSpace(imageFile.Format))
            return null;

        var normalizedFormat = "." + imageFile.Format.Trim().TrimStart('.').ToLowerInvariant();
        return ImageMimeTypes.GetValueOrDefault(normalizedFormat);
    }

    private string GetBlobImageThumbnailBasePath(string blobId, int maxDimension)
        => Path.Combine(ImageThumbnailDir, "entity-blobs", blobId[..2], $"{blobId}-{maxDimension}-{ImageThumbnailCacheVersion}");

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
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (UnauthorizedAccessException ex) when (FileReadRace.IsWindowsDeletionRace(ex, archivePath))
            {
                return null;
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

    public async Task GenerateVideoThumbnailAsync(int videoId, double? atSeconds, CancellationToken ct)
    {
        await GenerateVideoThumbnailCoreAsync(videoId, sourceFileId: null, atSeconds, ct);
    }

    public Task<bool> RegenerateVideoThumbnailAsync(int videoId, double? atSeconds = null, CancellationToken ct = default)
        => GenerateVideoThumbnailCoreAsync(videoId, sourceFileId: null, atSeconds, ct);

    public Task<bool> GenerateThumbnailFromFileAsync(
        int videoId,
        int sourceFileId,
        double? atSeconds,
        CancellationToken ct = default)
        => GenerateVideoThumbnailCoreAsync(videoId, sourceFileId, atSeconds, ct);

    private async Task<bool> GenerateVideoThumbnailCoreAsync(
        int videoId,
        int? sourceFileId,
        double? atSeconds,
        CancellationToken ct)
        => await _generatedAssetCoordinator.RunAsync(videoId, () => GenerateVideoThumbnailUnlockedAsync(videoId, sourceFileId, atSeconds, ct), ct);

    private async Task<bool> GenerateVideoThumbnailUnlockedAsync(
        int videoId,
        int? sourceFileId,
        double? atSeconds,
        CancellationToken ct)
    {
        var thumbPath = atSeconds.HasValue
            ? GetTimestampedThumbnailPath(videoId, atSeconds.Value)
            : GetThumbnailPath(videoId);

        var (filePath, duration) = await GetVideoFileInfoAsync(videoId, sourceFileId, ct);
        if (!File.Exists(filePath)) return false;

        var ffmpegPath = GetCachedFfmpegPath();
        if (ffmpegPath == null)
        {
            logger.LogWarning("FFmpeg not found. Cannot generate thumbnail for video {VideoId}", videoId);
            return false;
        }

        var thumbDir = Path.GetDirectoryName(thumbPath)!;
        Directory.CreateDirectory(thumbDir);

        var seekSeconds = atSeconds ?? duration * 0.2;
        if (seekSeconds <= 0) seekSeconds = 1;
        var vrFilter = VrFrameFilter.OneEyeFlat(await GetVideoVrAsync(videoId, ct), VrThumbnailWidth);

        // Limit concurrent FFmpeg processes
        var sem = GetFfmpegSemaphore();
        await sem.WaitAsync(ct);
        try
        {
            var tempPath = thumbPath + $".tmp.{Guid.NewGuid():N}.jpg";
            try
            {
                var decodeArgs = GetFfmpegDecodeArgs();
                string ThumbnailArgs(string? filter) => $"{decodeArgs} -v error -fflags +discardcorrupt -err_detect ignore_err -y -ss {seekSeconds.ToString("F2", CultureInfo.InvariantCulture)} -i \"{filePath}\"{(filter != null ? $" -vf \"{filter}\"" : string.Empty)} -vframes 1 -q:v 2 -f image2 \"{tempPath}\"";

                // One input, but it still comes out of the same budget: a run generating thumbnails
                // alongside sprites would otherwise add a decode per video on top of a sprite's batch.
                bool decoded;
                await using (await ffmpegConcurrency.AcquireAsync(1, ct))
                {
                    decoded = await TryRunFfmpegAsync(ffmpegPath, ThumbnailArgs(vrFilter), ResolveFrameDecodeTimeout(filePath), ct);
                    // A build without v360, or a layout the filter rejects, still gets the full frame.
                    if (!decoded && vrFilter != null)
                        decoded = await TryRunFfmpegAsync(ffmpegPath, ThumbnailArgs(null), ResolveFrameDecodeTimeout(filePath), ct);
                }

                if (!decoded)
                {
                    logger.LogWarning("FFmpeg failed for video {VideoId} thumbnail generation", videoId);
                    return false;
                }

                return TryCommitGeneratedFile(tempPath, thumbPath, ct);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error generating thumbnail for video {VideoId}", videoId);
            return false;
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// Time budget for decoding a single frame out of <paramref name="filePath"/>.
    ///
    /// The previous flat 20s was tuned on 1080p and silently failed large sources: decoding one
    /// 5400x2700 VR frame means walking from the preceding keyframe through far more data, and a
    /// 1.5 GB 4K VR file measured 22.8s - just over the cliff - so it reported "FFmpeg failed"
    /// despite being a perfectly good file. Scaling with the source size gives big files a
    /// proportionate allowance while keeping small ones from hanging on a genuinely broken source.
    /// </summary>
    internal static TimeSpan ResolveFrameDecodeTimeout(string? filePath)
    {
        const double baseSeconds = 30d;
        const double secondsPerGigabyte = 20d;
        const double maxSeconds = 150d;

        var gigabytes = 0d;
        try
        {
            if (!string.IsNullOrEmpty(filePath))
            {
                var info = new FileInfo(filePath);
                if (info.Exists)
                    gigabytes = info.Length / (double)(1L << 30);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Size is only used to scale the budget; the base allowance stands on its own.
        }

        return TimeSpan.FromSeconds(Math.Clamp(baseSeconds + secondsPerGigabyte * gigabytes, baseSeconds, maxSeconds));
    }

    /// <summary>Get the path for a timestamp-specific cached thumbnail.</summary>
    public string GetTimestampedThumbnailPath(int videoId, double seconds)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(videoId)));
        var subDir = hash[..2];
        var secKey = ((int)seconds).ToString();
        return Path.Combine(ThumbnailDir, subDir, $"{videoId}_t{secKey}.jpg");
    }

    public string GetSegmentAnimatedPreviewPath(int videoId, double seconds)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(videoId)));
        var subDir = hash[..2];
        var secKey = ((int)seconds).ToString();
        return Path.Combine(SegmentPreviewDir, subDir, $"{videoId}_t{secKey}.webp");
    }

    public string GetThumbnailPathForVideo(int videoId) => GetThumbnailPath(videoId);

    public string GetPreviewPath(int videoId)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(videoId)));
        return Path.Combine(PreviewDir, hash[..2], $"{videoId}.mp4");
    }

    public string GetSpritePath(int videoId)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(videoId)));
        return Path.Combine(VttDir, hash[..2], $"{videoId}_sprite.jpg");
    }

    public string GetSpriteVttPath(int videoId)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(videoId)));
        return Path.Combine(VttDir, hash[..2], $"{videoId}_thumbs.vtt");
    }

    public Task GenerateSegmentAnimatedPreviewAsync(
        int videoId,
        double startSec,
        double? endSec,
        CancellationToken ct)
        => GenerateSegmentAnimatedPreviewCoreAsync(videoId, sourceFileId: null, startSec, endSec, ct);

    public Task<bool> GenerateSegmentPreviewFromFileAsync(
        int videoId,
        int sourceFileId,
        double startSec,
        double? endSec,
        bool overwrite,
        CancellationToken ct = default)
        => GenerateSegmentAnimatedPreviewCoreAsync(videoId, sourceFileId, startSec, endSec, ct, overwrite);

    private async Task<bool> GenerateSegmentAnimatedPreviewCoreAsync(
        int videoId,
        int? sourceFileId,
        double startSec,
        double? endSec,
        CancellationToken ct,
        bool overwrite = false)
        => await _generatedAssetCoordinator.RunAsync(videoId, () => GenerateSegmentAnimatedPreviewUnlockedAsync(videoId, sourceFileId, startSec, endSec, ct, overwrite), ct);

    private async Task<bool> GenerateSegmentAnimatedPreviewUnlockedAsync(
        int videoId,
        int? sourceFileId,
        double startSec,
        double? endSec,
        CancellationToken ct,
        bool overwrite = false)
    {
        var previewPath = GetSegmentAnimatedPreviewPath(videoId, startSec);
        if (!overwrite && File.Exists(previewPath)) return true;

        var (filePath, duration) = await GetVideoFileInfoAsync(videoId, sourceFileId, ct);
        if (filePath == null || duration <= 0) return false;

        var ffmpegPath = GetCachedFfmpegPath();
        if (ffmpegPath == null)
        {
            logger.LogWarning("FFmpeg not found, cannot generate segment preview for video {VideoId}", videoId);
            return false;
        }

        var clampedStart = Math.Max(0, Math.Min(startSec, Math.Max(0, duration - 0.1)));
        var requestedDuration = endSec.HasValue && endSec.Value > clampedStart
            ? endSec.Value - clampedStart
            : SegmentPreviewDefaultDuration;
        var previewDuration = Math.Min(SegmentPreviewMaxDuration, Math.Max(0.5, requestedDuration));
        previewDuration = Math.Min(previewDuration, Math.Max(0.5, duration - clampedStart));

        var previewDir = Path.GetDirectoryName(previewPath)!;
        Directory.CreateDirectory(previewDir);

        var sem = GetFfmpegSemaphore();
        await sem.WaitAsync(ct);
        try
        {
            if (!overwrite && File.Exists(previewPath)) return true;

            var tempPath = previewPath + $".tmp.{Guid.NewGuid():N}.webp";
            try
            {
                var decodeArgs = GetFfmpegDecodeArgs();
                var args = $"{decodeArgs} -v error -y -ss {clampedStart.ToString("F2", CultureInfo.InvariantCulture)} -i \"{filePath}\" -t {previewDuration.ToString("F2", CultureInfo.InvariantCulture)} -vf \"fps={SegmentPreviewFps},scale={SegmentPreviewWidth}:-2:flags=lanczos\" -loop 0 -an -quality 75 -compression_level 4 \"{tempPath}\"";
                await RunFfmpegAsync(ffmpegPath, args, TimeSpan.FromSeconds(60), ct);

                if (!File.Exists(tempPath))
                    return false;

                File.Move(tempPath, previewPath, overwrite: true);
                return true;
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error generating segment preview for video {VideoId} at {StartSec}", videoId, startSec);
            return false;
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>Generate a multi-segment video preview clip (mp4) for a video.</summary>
    public async Task GenerateVideoPreviewAsync(int videoId, CancellationToken ct = default)
    {
        await GenerateVideoPreviewCoreAsync(videoId, sourceFileId: null, overwrite: false, ct);
    }

    public Task<bool> RegenerateVideoPreviewAsync(int videoId, CancellationToken ct = default)
        => GenerateVideoPreviewCoreAsync(videoId, sourceFileId: null, overwrite: true, ct);

    public Task<bool> GeneratePreviewFromFileAsync(
        int videoId,
        int sourceFileId,
        bool overwrite,
        CancellationToken ct = default)
        => GenerateVideoPreviewCoreAsync(videoId, sourceFileId, overwrite, ct);

    private async Task<bool> GenerateVideoPreviewCoreAsync(
        int videoId,
        int? sourceFileId,
        bool overwrite,
        CancellationToken ct)
        => await _generatedAssetCoordinator.RunAsync(videoId, () => GenerateVideoPreviewUnlockedAsync(videoId, sourceFileId, overwrite, ct), ct);

    private async Task<bool> GenerateVideoPreviewUnlockedAsync(
        int videoId,
        int? sourceFileId,
        bool overwrite,
        CancellationToken ct)
    {
        var previewPath = GetPreviewPath(videoId);
        if (!overwrite && File.Exists(previewPath)) return true;

        var (filePath, duration) = await GetVideoFileInfoAsync(videoId, sourceFileId, ct);
        if (filePath == null || duration <= 0) return false;
        var previewScale = VrFrameFilter.OneEyeFlat(await GetVideoVrAsync(videoId, ct), PreviewWidth)
            ?? $"scale={PreviewWidth}:-2";

        var ffmpegPath = GetCachedFfmpegPath();
        if (ffmpegPath == null)
        {
            logger.LogWarning("FFmpeg not found, cannot generate preview for video {VideoId}", videoId);
            return false;
        }

        var previewDir = Path.GetDirectoryName(previewPath)!;
        Directory.CreateDirectory(previewDir);

        var tmpDir = Path.Combine(config.GeneratedPath, "tmp", $"preview_{videoId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var generatedPreviewPath = Path.Combine(tmpDir, Path.GetFileName(previewPath));

        var sem = GetFfmpegSemaphore();
        var semaphoreAcquired = false;
        try
        {
            await sem.WaitAsync(ct);
            semaphoreAcquired = true;
            if (!overwrite && File.Exists(previewPath)) return true;

            var segmentCount = Math.Clamp(config.Ui.PreviewSegments <= 0 ? DefaultPreviewSegments : config.Ui.PreviewSegments, 1, 100);
            var segmentDuration = Math.Clamp(config.Ui.PreviewSegmentDuration <= 0 ? DefaultPreviewSegmentDuration : config.Ui.PreviewSegmentDuration, 0.1, 30d);
            var preset = NormalizePreviewPreset(config.PreviewPreset);
            var includeAudio = string.Equals(config.PreviewAudio, "true", StringComparison.OrdinalIgnoreCase);
            var audioArg = includeAudio ? string.Empty : "-an";
            var excludeStart = ParsePreviewExclusion(config.Ui.PreviewExcludeStart, duration);
            var excludeEnd = ParsePreviewExclusion(config.Ui.PreviewExcludeEnd, duration);
            var usableStart = Math.Min(excludeStart, Math.Max(0, duration - 0.1));
            var usableEnd = Math.Max(usableStart, duration - excludeEnd);
            var usableDuration = usableEnd - usableStart;
            if (usableDuration <= 0)
                return false;

            var decodeArgs = GetFfmpegDecodeArgs();

            // If video is too short for all segments, use a single full-video preview
            if (usableDuration < segmentDuration * segmentCount)
            {
                var seekArgs = usableStart > 0 ? $"-ss {usableStart.ToString("F2", CultureInfo.InvariantCulture)}" : string.Empty;
                var durationArgs = usableDuration < duration ? $"-t {usableDuration.ToString("F2", CultureInfo.InvariantCulture)}" : string.Empty;
                await RunPreviewEncodeAsync(
                    ffmpegPath,
                    $"{decodeArgs} -v error -y {HwDevicePlaceholder} {seekArgs} -i \"{filePath}\" {durationArgs} -max_muxing_queue_size 1024 {VideoCodecPlaceholder} -vf \"{previewScale}{HwUploadPlaceholder}\" -profile:v high -level 4.2 {audioArg} \"{generatedPreviewPath}\"",
                    generatedPreviewPath,
                    TimeSpan.FromMinutes(5),
                    preset,
                    inputCount: 1,
                    ct);
                var committed = TryCommitGeneratedFile(generatedPreviewPath, previewPath, ct);
                if (!committed)
                    logger.LogWarning("Preview generation failed for video {VideoId} - output not created", videoId);
                return committed;
            }

            var interval = usableDuration / segmentCount;
            var seekTimes = new double[segmentCount];
            for (var i = 0; i < segmentCount; i++)
            {
                var seekTime = usableStart + interval * i + interval * 0.5;
                if (seekTime + segmentDuration > usableEnd) seekTime = usableEnd - segmentDuration;
                if (seekTime < usableStart) seekTime = usableStart;
                seekTimes[i] = seekTime;
            }

            // Silent previews (the default) are assembled by one ffmpeg process: each segment is a
            // separately-seeked input, spliced together by the concat filter and encoded once. That
            // replaces segmentCount process launches and segmentCount encoder sessions with one of
            // each - which also keeps a hardware encoder from opening a session per segment.
            //
            // With preview audio on, each segment would additionally have to contribute an audio
            // stream to the concat filter, and a source with no audio track makes the whole graph
            // fail. Those previews keep the per-segment route, where a segment that cannot be
            // produced is simply left out of the concatenation.
            if (!includeAudio)
            {
                var inputs = new StringBuilder();
                var filter = new StringBuilder();
                foreach (var seekTime in seekTimes)
                {
                    inputs.Append(" -ss ").Append(seekTime.ToString("F2", CultureInfo.InvariantCulture))
                          .Append(" -t ").Append(segmentDuration.ToString("F2", CultureInfo.InvariantCulture))
                          .Append(" -i \"").Append(filePath).Append('"');
                }
                for (var i = 0; i < segmentCount; i++)
                {
                    // setpts=PTS-STARTPTS rebases each segment to zero; without it concat inherits the
                    // source timestamps and the output carries huge gaps between segments.
                    filter.Append('[').Append(i.ToString(CultureInfo.InvariantCulture))
                          .Append(":v:0]").Append(previewScale)
                          .Append(",setsar=1,setpts=PTS-STARTPTS[v")
                          .Append(i.ToString(CultureInfo.InvariantCulture)).Append("];");
                }
                for (var i = 0; i < segmentCount; i++)
                    filter.Append("[v").Append(i.ToString(CultureInfo.InvariantCulture)).Append(']');
                // The graph ends at [spliced]; the tail is filled in per encoder, because a VAAPI
                // encode has to upload to a GPU surface first and a software encode must not.
                filter.Append("concat=n=").Append(segmentCount.ToString(CultureInfo.InvariantCulture))
                      .Append(":v=1:a=0[spliced];[spliced]null").Append(HwUploadPlaceholder).Append("[preview]");

                await RunPreviewEncodeAsync(
                    ffmpegPath,
                    $"{decodeArgs} -v error -y {HwDevicePlaceholder}{inputs} -max_muxing_queue_size 1024 -filter_complex \"{filter}\" -map \"[preview]\" {VideoCodecPlaceholder} -profile:v high -level 4.2 -an \"{generatedPreviewPath}\"",
                    generatedPreviewPath,
                    TimeSpan.FromMinutes(5),
                    preset,
                    inputCount: segmentCount,
                    ct);

                if (!TryCommitGeneratedFile(generatedPreviewPath, previewPath, ct))
                {
                    logger.LogWarning("Preview generation failed for video {VideoId} - output not created", videoId);
                    return false;
                }

                return true;
            }

            var chunkFiles = new List<string>();
            for (int i = 0; i < segmentCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var chunkPath = Path.Combine(tmpDir, $"chunk_{i:D3}.mp4");
                chunkFiles.Add(chunkPath);

                await RunPreviewEncodeAsync(
                    ffmpegPath,
                    $"{decodeArgs} -v error -y {HwDevicePlaceholder} -ss {seekTimes[i].ToString("F2", CultureInfo.InvariantCulture)} -i \"{filePath}\" -t {segmentDuration.ToString("F2", CultureInfo.InvariantCulture)} -max_muxing_queue_size 1024 {VideoCodecPlaceholder} -vf \"{previewScale}{HwUploadPlaceholder}\" -profile:v high -level 4.2 {audioArg} \"{chunkPath}\"",
                    chunkPath,
                    TimeSpan.FromSeconds(60),
                    preset,
                    inputCount: 1,
                    ct);
            }

            // Only concat chunks that actually exist AND are non-empty. A chunk encode that failed can
            // leave a missing or 0-byte file; feeding that to the concat demuxer fails the whole preview
            // with "moov atom not found / Invalid data found when processing input".
            var validChunks = chunkFiles
                .Where(f => File.Exists(f) && new FileInfo(f).Length > 0)
                .ToList();
            if (validChunks.Count == 0)
            {
                logger.LogWarning("Preview generation for video {VideoId} produced no usable chunks", videoId);
                return false;
            }

            // Create concat file — use forward slashes for FFmpeg compatibility on all platforms
            var concatListPath = Path.Combine(tmpDir, "concat.txt");
            var concatLines = validChunks.Select(f => $"file '{Path.GetFullPath(f).Replace('\\', '/')}'");
            await File.WriteAllTextAsync(concatListPath, string.Join("\n", concatLines), ct);

            // Concatenate chunks into final preview
            await RunFfmpegAsync(ffmpegPath,
                $"-v error -y -f concat -safe 0 -i \"{concatListPath}\" -c:v copy \"{generatedPreviewPath}\"",
                TimeSpan.FromSeconds(30), ct);

            if (!TryCommitGeneratedFile(generatedPreviewPath, previewPath, ct))
            {
                logger.LogWarning("Preview generation failed for video {VideoId} - output not created", videoId);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error generating preview for video {VideoId}", videoId);
            return false;
        }
        finally
        {
            if (semaphoreAcquired)
                sem.Release();
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
        }
    }

    internal static bool TryCommitGeneratedFile(
        string generatedPath,
        string destinationPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(generatedPath) || new FileInfo(generatedPath).Length == 0)
            return false;

        ct.ThrowIfCancellationRequested();
        File.Move(generatedPath, destinationPath, overwrite: true);
        return true;
    }

    // Sentinel token for the video codec args slot in preview encode templates. A plain
    // string replace is used instead of string.Format so that file paths containing literal
    // '{' or '}' characters don't get misinterpreted as format placeholders (FormatException).
    private const string VideoCodecPlaceholder = "__COVE_VCODEC__";

    // Some hardware encoders need more than a codec swap. VAAPI encodes from GPU surfaces, so it
    // needs a device on the input side and an hwupload at the end of the filter chain; the others
    // take system-memory frames as-is. Both are placeholders rather than interpolated up front
    // because RunPreviewEncodeAsync may fall back from the hardware encoder to libx264, and the
    // device and upload MUST disappear with it - a leftover hwupload fails a libx264 encode.
    private const string HwDevicePlaceholder = "__COVE_HWDEV__";
    private const string HwUploadPlaceholder = "__COVE_HWUPLOAD__";

    /// <summary>
    /// Filter-chain tail a preview encode needs for the chosen encoder. VAAPI consumes GPU surfaces,
    /// so frames must be converted and uploaded; every other encoder reads system memory and gets a
    /// plain pixel-format conversion instead. Both forms end the chain in 8-bit 4:2:0, which a 10-bit
    /// HEVC source would otherwise carry through into High 10 H.264 that Safari refuses to play.
    /// </summary>
    private static string PreviewUploadChain(string encoder)
        => encoder == "h264_vaapi" ? ",format=nv12,hwupload" : ",format=yuv420p";

    /// <param name="inputCount">
    /// How many inputs this command opens. A spliced preview seeks the source once per segment, so
    /// it decodes that many streams at once and must reserve that much of the decode budget - the
    /// setting is denominated in decode inputs, not in processes.
    /// </param>
    private async Task RunPreviewEncodeAsync(string ffmpegPath, string argsTemplate, string outputPath, TimeSpan timeout, string softwarePreset, int inputCount, CancellationToken ct)

    {
        var encoder = GetH264Encoder();

        // Fills in every encoder-dependent slot at once, so a hardware attempt and its libx264
        // retry each get a fully consistent command line.
        string Compose(string chosen) => argsTemplate
            .Replace(VideoCodecPlaceholder, FfmpegHwAccel.VideoEncodeArgs(chosen, PreviewCrf, softwarePreset), StringComparison.Ordinal)
            .Replace(HwDevicePlaceholder, FfmpegHwAccel.InputArgsForEncoder(chosen), StringComparison.Ordinal)
            .Replace(HwUploadPlaceholder, PreviewUploadChain(chosen), StringComparison.Ordinal);
        // Build the codec args per encoder family. libx264 honors -preset/-crf; the hardware encoders
        // need their own constant-quality knobs (NVENC/QSV/AMF ignore -crf, and a libx264 preset name
        // like "veryfast" is an invalid NVENC preset that aborts the encode).
        if (encoder != "libx264")
        {
            var hwArgs = Compose(encoder);
            bool ok;
            // Two separate budgets: the GPU's encode-session limit (shared with library conversion)
            // and Cove's decode-input budget. A preview needs one of each.
            using (await _hwEncodeSessionGate.AcquireAsync(ct))
            {
                await using var slots = await ffmpegConcurrency.AcquireAsync(inputCount, ct);
                ok = await TryRunFfmpegAsync(ffmpegPath, hwArgs, timeout, ct);
            }

            if (ok || ct.IsCancellationRequested)
                return;

            // A hardware encode that still fails (session exhaustion, driver/SDK mismatch, etc.) must not
            // leave a missing/empty chunk that later breaks the concat — fall back to the CPU encoder so
            // generation degrades gracefully instead of producing a broken preview.
            logger.LogDebug("Hardware encode ({Encoder}) failed for {Output}; falling back to libx264.", encoder, Path.GetFileName(outputPath));
        }

        await using var softwareSlots = await ffmpegConcurrency.AcquireAsync(inputCount, ct);
        await RunFfmpegAsync(ffmpegPath, Compose("libx264"), timeout, ct);
    }

    private static double ParsePreviewExclusion(string? value, double duration)
    {
        if (string.IsNullOrWhiteSpace(value) || duration <= 0)
            return 0d;

        var trimmed = value.Trim();
        if (trimmed.EndsWith('%'))
        {
            var percentText = trimmed[..^1].Trim();
            if (double.TryParse(percentText, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                return Math.Clamp(duration * (percent / 100d), 0d, duration);
        }

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? Math.Clamp(seconds, 0d, duration)
            : 0d;
    }

    private static string NormalizePreviewPreset(string? preset)
        => preset?.Trim().ToLowerInvariant() switch
        {
            "ultrafast" => "ultrafast",
            "veryfast" => "veryfast",
            "fast" => "fast",
            "medium" => "medium",
            "slow" => "slow",
            "slower" => "slower",
            "veryslow" => "veryslow",
            _ => PreviewPreset,
        };

    /// <summary>Generate a sprite sheet (JPEG grid) and VTT timeline file for a video.
    /// Frames are extracted by seeking, batched into a small number of ffmpeg invocations rather
    /// than one per frame — 3-6x faster than per-frame spawning, and far faster than decoding the
    /// whole file through an fps filter.</summary>
    public async Task GenerateVideoSpriteAsync(int videoId, CancellationToken ct = default)
    {
        await GenerateVideoSpriteCoreAsync(videoId, sourceFileId: null, overwrite: false, ct);
    }

    public Task<bool> RegenerateVideoSpriteAsync(int videoId, CancellationToken ct = default)
        => GenerateVideoSpriteCoreAsync(videoId, sourceFileId: null, overwrite: true, ct);

    public Task<bool> GenerateSpriteFromFileAsync(
        int videoId,
        int sourceFileId,
        bool overwrite,
        CancellationToken ct = default)
        => GenerateVideoSpriteCoreAsync(videoId, sourceFileId, overwrite, ct);

    private Task<bool> GenerateVideoSpriteCoreAsync(
        int videoId,
        int? sourceFileId,
        bool overwrite,
        CancellationToken ct)
        => _generatedAssetCoordinator.RunAsync(videoId, () => RunWithSpriteGenerationLockAsync(
            videoId,
            () => GenerateVideoSpriteLockedAsync(videoId, sourceFileId, overwrite, ct),
            ct), ct);

    internal static async Task<T> RunWithSpriteGenerationLockAsync<T>(
        int videoId,
        Func<Task<T>> operation,
        CancellationToken ct = default)
    {
        var gate = SpriteGenerationLocks[(int)((uint)videoId % (uint)SpriteGenerationLocks.Length)];
        await gate.WaitAsync(ct);
        try
        {
            return await operation();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> GenerateVideoSpriteLockedAsync(
        int videoId,
        int? sourceFileId,
        bool overwrite,
        CancellationToken ct)
    {
        var destinationSpritePath = GetSpritePath(videoId);
        var destinationVttPath = GetSpriteVttPath(videoId);
        if (!overwrite && File.Exists(destinationSpritePath) && File.Exists(destinationVttPath)) return true;

        var (filePath, duration) = await GetVideoFileInfoAsync(videoId, sourceFileId, ct);
        if (filePath == null || duration <= 0) return false;

        var ffmpegPath = GetCachedFfmpegPath();
        if (ffmpegPath == null) return false;

        var spriteDir = Path.GetDirectoryName(destinationSpritePath)!;
        Directory.CreateDirectory(spriteDir);
        var tmpDir = Path.Combine(config.GeneratedPath, "tmp", $"sprite_{videoId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var spritePath = Path.Combine(tmpDir, Path.GetFileName(destinationSpritePath));
        var vttPath = Path.Combine(tmpDir, Path.GetFileName(destinationVttPath));
        var sem = GetFfmpegSemaphore();
        var semaphoreAcquired = false;

        try
        {
            await sem.WaitAsync(ct);
            semaphoreAcquired = true;
            if (!overwrite && File.Exists(destinationSpritePath) && File.Exists(destinationVttPath)) return true;

            // Calculate grid dimensions
            var frameCount = Math.Min(SpriteFrameCount, Math.Max(1, (int)(duration / 2)));
            var cols = (int)Math.Ceiling(Math.Sqrt(frameCount));
            var rows = (int)Math.Ceiling((double)frameCount / cols);
            var interval = duration / frameCount;

            // Build timestamps for seek-based extraction (center of each interval)
            var timestamps = new double[frameCount];
            for (var i = 0; i < frameCount; i++)
                timestamps[i] = interval * (i + 0.5);

            var extracted = await VideoFrameBatchExtractor.ExtractAsync(
                ffmpegPath, filePath, timestamps, SpriteFrameSize, ffmpegConcurrency, logger, ct);

            if (extracted == null)
            {
                logger.LogWarning("Sprite generation failed for video {VideoId} - frame extraction returned null", videoId);
                return false;
            }

            // A handful of unreadable frames (a corrupt GOP, a damaged region) should not cost the
            // video its whole scrubbing preview, so gaps are filled from the nearest decoded frame.
            // Below the ratio threshold the sheet would be mostly filler and the video is reported
            // as failed instead.
            var frameSet = SpriteFrameGapFiller.Fill(extracted);
            if (frameSet.Frames is not { } frames)
            {
                foreach (var f in extracted) f?.Dispose();
                logger.LogWarning(
                    "Sprite generation failed for video {VideoId} - only {Decoded}/{Requested} frames could be decoded",
                    videoId, frameSet.DecodedCount, frameSet.RequestedCount);
                return false;
            }

            if (frameSet.SubstitutedCount > 0)
            {
                logger.LogInformation(
                    "Sprite for video {VideoId}: {Decoded}/{Requested} frames decoded, {Substituted} filled from neighbours",
                    videoId, frameSet.DecodedCount, frameSet.RequestedCount, frameSet.SubstitutedCount);
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
                logger.LogWarning("Sprite generation failed for video {VideoId}", videoId);
                return false;
            }

            await WriteSpriteVttAsync(spritePath, vttPath, frameCount, cols, rows, interval, ct, fw, fh, duration);
            CommitGeneratedSpriteFiles(spritePath, vttPath, destinationSpritePath, destinationVttPath, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error generating sprite for video {VideoId}", videoId);
            return false;
        }
        finally
        {
            if (semaphoreAcquired)
                sem.Release();
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
        }
    }

    internal static void CommitGeneratedSpriteFiles(
        string generatedSpritePath,
        string generatedVttPath,
        string destinationSpritePath,
        string destinationVttPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(generatedSpritePath)
            || new FileInfo(generatedSpritePath).Length == 0
            || !File.Exists(generatedVttPath)
            || new FileInfo(generatedVttPath).Length == 0)
        {
            throw new InvalidDataException("Generated sprite and VTT must both be present and non-empty before replacement.");
        }

        ct.ThrowIfCancellationRequested();

        var backupSuffix = $".backup.{Guid.NewGuid():N}";
        var spriteBackupPath = destinationSpritePath + backupSuffix;
        var vttBackupPath = destinationVttPath + backupSuffix;
        var spriteExisted = File.Exists(destinationSpritePath);
        var vttExisted = File.Exists(destinationVttPath);
        var replacementStarted = false;
        var cleanupBackups = false;

        try
        {
            if (spriteExisted)
                File.Copy(destinationSpritePath, spriteBackupPath);
            if (vttExisted)
                File.Copy(destinationVttPath, vttBackupPath);

            ct.ThrowIfCancellationRequested();
            File.Move(generatedSpritePath, destinationSpritePath, overwrite: true);
            replacementStarted = true;
            ct.ThrowIfCancellationRequested();
            File.Move(generatedVttPath, destinationVttPath, overwrite: true);
            ct.ThrowIfCancellationRequested();
            cleanupBackups = true;
        }
        catch (Exception commitException)
        {
            if (!replacementStarted)
            {
                cleanupBackups = true;
                throw;
            }

            Exception? rollbackException = null;
            try
            {
                RestoreGeneratedAsset(destinationSpritePath, spriteBackupPath, spriteExisted);
            }
            catch (Exception ex)
            {
                rollbackException = ex;
            }

            try
            {
                RestoreGeneratedAsset(destinationVttPath, vttBackupPath, vttExisted);
            }
            catch (Exception ex)
            {
                rollbackException = rollbackException == null
                    ? ex
                    : new AggregateException(rollbackException, ex);
            }

            if (rollbackException != null)
                throw new AggregateException("Sprite/VTT replacement and rollback both failed.", commitException, rollbackException);
            cleanupBackups = true;
            throw;
        }
        finally
        {
            // A failed rollback can leave these as the only recoverable copies of the prior pair.
            if (cleanupBackups)
            {
                TryDeleteFile(spriteBackupPath);
                TryDeleteFile(vttBackupPath);
            }
        }
    }

    private static void RestoreGeneratedAsset(string destinationPath, string backupPath, bool existed)
    {
        if (existed)
        {
            if (!File.Exists(backupPath))
                throw new IOException($"Generated asset backup is missing: {backupPath}");
            File.Move(backupPath, destinationPath, overwrite: true);
        }
        else if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of a private temporary or backup file.
        }
    }

    private async Task WriteSpriteVttAsync(string spritePath, string vttPath, int frameCount, int cols, int rows, double interval, CancellationToken ct, int? frameWidth = null, int? frameHeight = null, double? duration = null)
    {
        int thumbWidth;
        int thumbHeight;

        if (frameWidth.HasValue && frameHeight.HasValue)
        {
            thumbWidth = frameWidth.Value;
            thumbHeight = frameHeight.Value;
        }
        else
        {
            var spriteInfo = await SixLabors.ImageSharp.Image.IdentifyAsync(spritePath, ct);
            if (spriteInfo == null)
                return;

            thumbWidth = spriteInfo.Width / cols;
            thumbHeight = spriteInfo.Height / rows;
        }

        var effectiveDuration = duration ?? interval * frameCount;
        var vttBuilder = new StringBuilder();
        vttBuilder.AppendLine("WEBVTT");
        vttBuilder.AppendLine();

        var spriteFileName = Path.GetFileName(spritePath);
        for (int i = 0; i < frameCount; i++)
        {
            var startTime = i * interval;
            var endTime = Math.Min((i + 1) * interval, effectiveDuration);
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

    private static string FormatVttTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    /// <summary>The VR layout to flatten generated images with, or null for a flat video.</summary>
    private async Task<VrDescriptorDto?> GetVideoVrAsync(int videoId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var video = await db.Videos
            .AsNoTracking()
            .Where(video => video.Id == videoId && video.IsVr)
            .Select(video => new { video.VrProjection, video.VrFieldOfView, video.VrStereoMode, video.PrimaryFileId })
            .SingleOrDefaultAsync(ct);
        if (video == null)
            return null;
        var file = await db.VideoFiles
            .AsNoTracking()
            .Where(file => file.VideoId == videoId && file.Id == video.PrimaryFileId)
            .Select(file => new { file.Path, file.Width, file.Height })
            .SingleOrDefaultAsync(ct);
        return VrDescriptorDetector.Resolve(true, video.VrProjection, video.VrFieldOfView, video.VrStereoMode, file?.Path, file?.Width ?? 0, file?.Height ?? 0);
    }

    internal async Task<(string? FilePath, double Duration)> GetVideoFileInfoAsync(
        int videoId,
        int? sourceFileId,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

        var primaryFileId = await db.Videos
            .AsNoTracking()
            .Where(video => video.Id == videoId)
            .Select(video => video.PrimaryFileId)
            .SingleOrDefaultAsync(ct);
        if (sourceFileId.HasValue && sourceFileId != primaryFileId) return (null, 0);
        var selectedFileId = sourceFileId ?? primaryFileId;
        if (!selectedFileId.HasValue) return (null, 0);
        var videoFile = await db.VideoFiles
            .AsNoTracking()
            .SingleOrDefaultAsync(file => file.VideoId == videoId && file.Id == selectedFileId.Value, ct);

        if (videoFile == null) return (null, 0);

        var filePath = FilesystemPaths.ToNativePath(videoFile.Path);

        return File.Exists(filePath) ? (filePath, videoFile.Duration) : (null, 0);
    }

    private async Task RunFfmpegAsync(string ffmpegPath, string args, TimeSpan timeout, CancellationToken ct)
    {
        var result = await FfmpegProcessRunner.RunAsync(ffmpegPath, args, timeout, ct);
        if (result.TimedOut)
        {
            logger.LogWarning("FFmpeg timed out: {Args}", args[..Math.Min(200, args.Length)]);
            return;
        }

        if (result.ExitCode != 0)
            logger.LogWarning("FFmpeg failed (exit {Code}): {Error}", result.ExitCode, result.StandardError[..Math.Min(500, result.StandardError.Length)]);
    }

    private async Task<bool> TryRunFfmpegAsync(string ffmpegPath, string args, TimeSpan timeout, CancellationToken ct)
    {
        var result = await FfmpegProcessRunner.RunAsync(ffmpegPath, args, timeout, ct);
        if (result.TimedOut)
        {
            if (logger.IsEnabled(LogLevel.Trace))
                logger.LogTrace("FFmpeg timed out: {Args}", args[..Math.Min(200, args.Length)]);
            return false;
        }

        if (result.ExitCode == 0)
            return true;

        if (logger.IsEnabled(LogLevel.Trace))
            logger.LogTrace("FFmpeg failed (exit {Code}): {Error}", result.ExitCode, result.StandardError[..Math.Min(500, result.StandardError.Length)]);
        return false;
    }

    public string StartGenerateAllThumbnails()
    {
        return jobService.Enqueue("generate_thumbnails", "Generating thumbnails", async (progress, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

            var videoIds = await db.Videos
                .Where(video => string.IsNullOrEmpty(video.ImageBlobId))
                .Select(video => video.Id)
                .ToListAsync(ct);
            var total = videoIds.Count;
            var processed = 0;
            var generated = 0;
            var alreadyPresent = 0;
            var failed = 0;

            foreach (var videoId in videoIds)
            {
                ct.ThrowIfCancellationRequested();
                processed++;
                progress.Report((double)processed / total, $"Video {processed}/{total}");

                var thumbPath = GetThumbnailPath(videoId);
                if (File.Exists(thumbPath))
                {
                    alreadyPresent++;
                    continue;
                }

                await GenerateVideoThumbnailAsync(videoId, null, ct);
                if (File.Exists(thumbPath))
                    generated++;
                else
                    failed++;
            }

            logger.LogInformation(
                "Thumbnail generation finished: {Generated} generated, {AlreadyPresent} already present, {Failed} failed of {Total} videos",
                generated,
                alreadyPresent,
                failed,
                total);
        });
    }

    private string GetThumbnailPath(int videoId)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(videoId)));
        var subDir = hash[..2];
        return Path.Combine(ThumbnailDir, subDir, $"{videoId}.jpg");
    }

    private void DeleteFilesByPattern(string directory, string searchPattern)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var path in Directory.EnumerateFiles(directory, searchPattern))
            DeleteFileIfExists(path);
    }

    private void DeleteFileIfExists(string path)
    {
        if (!File.Exists(path))
            return;

        File.Delete(path);
        logger.LogDebug("Deleted generated asset at {Path}", path);
    }

    private string GetImageThumbnailBasePath(int imageId, int maxDimension)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(imageId)));
        var subDir = hash[..2];
        return Path.Combine(ImageThumbnailDir, subDir, $"{imageId}_m{maxDimension}_{ImageThumbnailCacheVersion}");
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

    /// <summary>
    /// Input-side decode arguments. Deliberately empty: frame extraction decodes on the CPU.
    ///
    /// Hardware decode loses here, and not marginally. Extraction seeks to a timestamp and decodes a
    /// handful of frames, so the cost is dominated by per-input setup - open, parse the index, seek -
    /// not by decoding. A hardware decoder pays device and surface setup on every input, and with 81
    /// separate seeks that fixed cost swamps the decode it saves. Measured on an RTX 3090 / 32-core
    /// host, 81-frame sprite extraction:
    ///
    ///   source            CPU     -hwaccel auto   cuda+sw scale   cuda+scale_cuda
    ///   1080p  6m09s      3.2s         10.6s            8.8s            12.2s
    ///   4K    20m57s     18.5s         43.8s           57.3s            30.1s
    ///   4K    41m57s     25.0s         42.3s           51.0s            28.7s
    ///
    /// Keeping the scale on the GPU does not rescue it. (The removed in-process decoder did benefit
    /// from hwaccel, because it opened one decoder and reused it across all 81 seeks; the CLI cannot
    /// reuse a decoder across seeks, so that amortization is not available here.)
    ///
    /// Hardware acceleration still pays for preview *encoding*, which is a single long encode per
    /// video rather than many short decodes - that path goes through <see cref="GetH264Encoder"/>.
    ///
    /// A power user can still force input arguments via the FfmpegInputArgs setting.
    /// </summary>
    private string GetFfmpegDecodeArgs()
    {
        return !string.IsNullOrWhiteSpace(config.FfmpegInputArgs) ? config.FfmpegInputArgs : string.Empty;
    }

    /// <summary>Get the H.264 encoder to use for generation, honoring the configured hardware
    /// acceleration preference. The probe result is cached, but the cache is keyed on the
    /// relevant settings (ffmpeg path + hardware-acceleration mode) so changing those in
    /// Settings takes effect immediately, without restarting Cove.</summary>
    private string GetH264Encoder()
    {
        var ffmpegPath = GetCachedFfmpegPath();
        if (ffmpegPath == null) return "libx264";

        // Re-probe whenever a setting that can change the outcome changes.
        var fingerprint = $"{ffmpegPath}|{config.HardwareAcceleration}";

        lock (_hwEncoderLock)
        {
            if (_hwEncoder != null && _hwEncoderFingerprint == fingerprint)
                return _hwEncoder;

            var encoder = ProbeH264Encoder(ffmpegPath);
            _hwEncoder = encoder;
            _hwEncoderFingerprint = fingerprint;
            return encoder;
        }
    }

    /// <summary>Pick the H.264 encoder for the current configuration, honoring a pinned hardware
    /// acceleration and falling back to libx264 if it cannot open a session. Shared with the live
    /// transcode path via <see cref="FfmpegHwAccel"/>.</summary>
    private string ProbeH264Encoder(string ffmpegPath)
        => FfmpegHwAccel.SelectH264Encoder(ffmpegPath, config.HardwareAcceleration, logger);
}
