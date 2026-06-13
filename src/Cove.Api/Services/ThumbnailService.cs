using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Cove.Core.Entities;
using Cove.Core.Entities.Galleries.Zip;
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
    Task GenerateImageThumbnailAsync(int imageId, int maxDimension = 640, bool overwrite = false, CancellationToken ct = default);
    Task GenerateVideoPreviewAsync(int videoId, CancellationToken ct = default);
    Task GenerateSegmentAnimatedPreviewAsync(int videoId, double startSec, double? endSec = null, CancellationToken ct = default);
    Task GenerateVideoSpriteAsync(int videoId, CancellationToken ct = default);
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
    ILogger<ThumbnailService> logger) : IThumbnailService
{
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

    // Corrupt media can crash FFmpeg.AutoGen in-process and take down the API.
    // Keep thumbnail extraction out-of-process so failures stay isolated to ffmpeg.
    private static bool UseInProcessVideoFrameExtraction => false;

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
            var cachedStream = new FileStream(declaredThumbnailPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
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
            await source.Value.stream.DisposeAsync();
            var cachedStream = new FileStream(thumbnailPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            return (cachedStream, thumbnailOutput.ContentType, true);
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

                    var cachedStream = new FileStream(thumbnailPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                    return (cachedStream, thumbnailOutput.ContentType, true);
                }

                if (source.Value.stream.CanSeek)
                    source.Value.stream.Position = 0;
                logger.LogDebug("Skipping thumbnail generation for unsupported image format {ImageId}", imageId);
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
            logger.LogInformation("Skipping thumbnail generation for unsupported image format {ImageId}", imageId);
            return (source.Value.stream, sourceContentType, source.Value.supportsRangeRequests);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Falling back to original image stream for thumbnail {ImageId}", imageId);
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
            var cachedStream = new FileStream(cachedThumbnail.Value.path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
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

                var cachedStream = new FileStream(thumbnailPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                return (cachedStream, thumbnailOutput.ContentType, true);
            }

            if (source.Value.Stream.CanSeek)
                source.Value.Stream.Position = 0;
            logger.LogDebug("Skipping cached blob thumbnail generation for unsupported image format {BlobId}", blobId);
            return (source.Value.Stream, sourceContentType, source.Value.Stream.CanSeek);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Falling back to original blob stream for entity image thumbnail {BlobId}", blobId);
            if (source.Value.Stream.CanSeek)
                source.Value.Stream.Position = 0;
            return (source.Value.Stream, sourceContentType, source.Value.Stream.CanSeek);
        }
    }

    public async Task GenerateImageThumbnailAsync(int imageId, int maxDimension, bool overwrite, CancellationToken ct)
    {
        maxDimension = NormalizeImageThumbnailMaxDimension(maxDimension);

        var imageFile = await GetImageFileRecordAsync(imageId, ct);
        if (imageFile == null) return;

        var source = await OpenImageSourceStreamAsync(imageFile, ct);
        if (source == null)
            return;

        var effectiveContentType = await GetEffectiveImageContentTypeAsync(source.Value.stream, source.Value.contentType, ct);
        if (!CanGenerateImageThumbnail(effectiveContentType ?? source.Value.contentType))
            return;

        var thumbnailBasePath = GetImageThumbnailBasePath(imageId, maxDimension);
        var thumbnailOutput = GetImageThumbnailOutput(effectiveContentType ?? source.Value.contentType);
        var thumbnailPath = GetImageThumbnailPath(thumbnailBasePath, thumbnailOutput);
        if (!overwrite && IsImageThumbnailCurrent(thumbnailPath, imageFile.ModTime))
            return;

        await using (source.Value.stream)
        {
            if (!await TryGenerateImageThumbnailFileAsync(source.Value.stream, TryGetDirectImageSourcePath(imageFile), effectiveContentType ?? source.Value.contentType, thumbnailPath, imageFile.ModTime, maxDimension, thumbnailOutput, ct))
                logger.LogDebug("Skipping generated thumbnail for unsupported image format {ImageId}", imageId);
            else
                DeleteAlternateImageThumbnailVariants(thumbnailBasePath, thumbnailPath);
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
        catch (UnknownImageFormatException)
        {
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
                    : $"-v error -y -i \"{inputPath}\" -vf \"{scaleFilter}\" -frames:v 1 -q:v 3 -f image2 \"{tempOutputPath}\"";
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
                    : $"-v error -y -i \"{inputPath}\" -vf \"{scaleFilter}\" -frames:v 1 -q:v 3 \"{tempOutputPath}\"";
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

        var cachedModifiedAt = File.GetLastWriteTimeUtc(thumbnailPath);
        return cachedModifiedAt >= NormalizeUtc(sourceModifiedAt).AddSeconds(-1);
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
        var thumbPath = atSeconds.HasValue
            ? GetTimestampedThumbnailPath(videoId, atSeconds.Value)
            : GetThumbnailPath(videoId);

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
            .FirstOrDefaultAsync(f => f.VideoId == videoId, ct);

        if (videoFile == null) return;

        var filePath = videoFile.ParentFolder != null
            ? Path.Combine(videoFile.ParentFolder.Path, videoFile.Basename)
            : videoFile.Basename;

        if (!File.Exists(filePath)) return;

        var ffmpegPath = GetCachedFfmpegPath();
        if (ffmpegPath == null)
        {
            logger.LogWarning("FFmpeg not found. Cannot generate thumbnail for video {VideoId}", videoId);
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

            var tempPath = thumbPath + ".tmp.jpg";
            try
            {
                if (!await TryGenerateVideoThumbnailViaInProcessAsync(ffmpegPath, filePath, thumbPath, tempPath, seekSeconds, ct))
                {
                    var decodeArgs = GetFfmpegDecodeArgs();
                    var args = $"{decodeArgs} -v error -fflags +discardcorrupt -err_detect ignore_err -y -ss {seekSeconds:F2} -i \"{filePath}\" -vframes 1 -q:v 2 -f image2 \"{tempPath}\"";
                    if (!await TryRunFfmpegAsync(ffmpegPath, args, TimeSpan.FromSeconds(20), ct))
                    {
                        logger.LogWarning("FFmpeg failed for video {VideoId} thumbnail generation", videoId);
                        return;
                    }

                    if (File.Exists(tempPath))
                        File.Move(tempPath, thumbPath, overwrite: true);
                }
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
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task<bool> TryGenerateVideoThumbnailViaInProcessAsync(string ffmpegPath, string filePath, string thumbPath, string tempPath, double seekSeconds, CancellationToken ct)
    {
        if (!UseInProcessVideoFrameExtraction)
            return false;

        FfmpegInProcess.EnsureInitialized(ffmpegPath, config.EnableFfmpegHwAccel);
        if (!FfmpegInProcess.IsAvailable)
            return false;

        Image<Rgba32>[]? frames = null;
        try
        {
            frames = FfmpegInProcess.ExtractFrames(filePath, [seekSeconds], scaleWidth: 0, threadCount: 1, ct);
            if (frames == null || frames.Length == 0 || frames[0] == null)
                return false;

            await frames[0].SaveAsJpegAsync(tempPath, new JpegEncoder { Quality = VideoThumbnailQuality }, ct);
            if (!File.Exists(tempPath))
                return false;

            File.Move(tempPath, thumbPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "In-process thumbnail generation failed for {FilePath}; falling back to ffmpeg CLI", filePath);
            return false;
        }
        finally
        {
            if (frames != null)
            {
                foreach (var frame in frames)
                    frame?.Dispose();
            }
        }
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

    public async Task GenerateSegmentAnimatedPreviewAsync(int videoId, double startSec, double? endSec, CancellationToken ct)
    {
        var previewPath = GetSegmentAnimatedPreviewPath(videoId, startSec);
        if (File.Exists(previewPath)) return;

        var (filePath, duration) = await GetVideoFileInfoAsync(videoId, ct);
        if (filePath == null || duration <= 0) return;

        var ffmpegPath = GetCachedFfmpegPath();
        if (ffmpegPath == null)
        {
            logger.LogWarning("FFmpeg not found, cannot generate segment preview for video {VideoId}", videoId);
            return;
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
            if (File.Exists(previewPath)) return;

            var tempPath = previewPath + ".tmp.webp";
            try
            {
                var decodeArgs = GetFfmpegDecodeArgs();
                var args = $"{decodeArgs} -v error -y -ss {clampedStart:F2} -i \"{filePath}\" -t {previewDuration:F2} -vf \"fps={SegmentPreviewFps},scale={SegmentPreviewWidth}:-2:flags=lanczos\" -loop 0 -an -quality 75 -compression_level 4 \"{tempPath}\"";
                await RunFfmpegAsync(ffmpegPath, args, TimeSpan.FromSeconds(60), ct);

                if (File.Exists(tempPath))
                    File.Move(tempPath, previewPath, overwrite: true);
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
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>Generate a multi-segment video preview clip (mp4) for a video.</summary>
    public async Task GenerateVideoPreviewAsync(int videoId, CancellationToken ct)
    {
        var previewPath = GetPreviewPath(videoId);
        if (File.Exists(previewPath)) return;

        var (filePath, duration) = await GetVideoFileInfoAsync(videoId, ct);
        if (filePath == null || duration <= 0) return;

        var ffmpegPath = GetCachedFfmpegPath();
        if (ffmpegPath == null)
        {
            logger.LogWarning("FFmpeg not found, cannot generate preview for video {VideoId}", videoId);
            return;
        }

        var previewDir = Path.GetDirectoryName(previewPath)!;
        Directory.CreateDirectory(previewDir);

        var tmpDir = Path.Combine(config.GeneratedPath, "tmp", $"preview_{videoId}");
        Directory.CreateDirectory(tmpDir);

        var sem = GetFfmpegSemaphore();
        await sem.WaitAsync(ct);
        try
        {
            var segmentCount = Math.Clamp(config.Ui.PreviewSegments <= 0 ? DefaultPreviewSegments : config.Ui.PreviewSegments, 1, 100);
            var segmentDuration = Math.Clamp(config.Ui.PreviewSegmentDuration <= 0 ? DefaultPreviewSegmentDuration : config.Ui.PreviewSegmentDuration, 0.1, 30d);
            var preset = NormalizePreviewPreset(config.PreviewPreset);
            var audioArg = string.Equals(config.PreviewAudio, "true", StringComparison.OrdinalIgnoreCase) ? string.Empty : "-an";
            var excludeStart = ParsePreviewExclusion(config.Ui.PreviewExcludeStart, duration);
            var excludeEnd = ParsePreviewExclusion(config.Ui.PreviewExcludeEnd, duration);
            var usableStart = Math.Min(excludeStart, Math.Max(0, duration - 0.1));
            var usableEnd = Math.Max(usableStart, duration - excludeEnd);
            var usableDuration = usableEnd - usableStart;
            if (usableDuration <= 0)
                return;

            var decodeArgs = GetFfmpegDecodeArgs();

            // If video is too short for all segments, use a single full-video preview
            if (usableDuration < segmentDuration * segmentCount)
            {
                var seekArgs = usableStart > 0 ? $"-ss {usableStart.ToString("F2", CultureInfo.InvariantCulture)}" : string.Empty;
                var durationArgs = usableDuration < duration ? $"-t {usableDuration.ToString("F2", CultureInfo.InvariantCulture)}" : string.Empty;
                await RunPreviewEncodeAsync(
                    ffmpegPath,
                    $"{decodeArgs} -v error -y {seekArgs} -i \"{filePath}\" {durationArgs} -max_muxing_queue_size 1024 {{0}} -vf \"scale={PreviewWidth}:-2\" -pix_fmt yuv420p -profile:v high -level 4.2 -preset {preset} -crf {PreviewCrf} {audioArg} \"{previewPath}\"",
                    previewPath,
                    TimeSpan.FromMinutes(5),
                    ct);
                return;
            }

            var interval = usableDuration / segmentCount;
            var chunkFiles = new List<string>();

            for (int i = 0; i < segmentCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var seekTime = usableStart + interval * i + interval * 0.5;
                if (seekTime + segmentDuration > usableEnd) seekTime = usableEnd - segmentDuration;
                if (seekTime < usableStart) seekTime = usableStart;

                var chunkPath = Path.Combine(tmpDir, $"chunk_{i:D3}.mp4");
                chunkFiles.Add(chunkPath);

                await RunPreviewEncodeAsync(
                    ffmpegPath,
                    $"{decodeArgs} -v error -y -ss {seekTime.ToString("F2", CultureInfo.InvariantCulture)} -i \"{filePath}\" -t {segmentDuration.ToString("F2", CultureInfo.InvariantCulture)} -max_muxing_queue_size 1024 {{0}} -vf \"scale={PreviewWidth}:-2\" -pix_fmt yuv420p -profile:v high -level 4.2 -preset {preset} -crf {PreviewCrf} {audioArg} \"{chunkPath}\"",
                    chunkPath,
                    TimeSpan.FromSeconds(60),
                    ct);
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
                logger.LogWarning("Preview generation failed for video {VideoId} - output not created", videoId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error generating preview for video {VideoId}", videoId);
        }
        finally
        {
            sem.Release();
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
        }
    }

    private async Task RunPreviewEncodeAsync(string ffmpegPath, string argsTemplate, string outputPath, TimeSpan timeout, CancellationToken ct)
    {
        var encoder = GetH264Encoder();
        var args = string.Format(System.Globalization.CultureInfo.InvariantCulture, argsTemplate, $"-c:v {encoder}");
        await RunFfmpegAsync(ffmpegPath, args, timeout, ct);
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
    /// Uses in-process FFmpeg decoding with seek-based extraction — 5-17× faster than
    /// the fps filter approach which decodes the entire video.</summary>
    public async Task GenerateVideoSpriteAsync(int videoId, CancellationToken ct)
    {
        var spritePath = GetSpritePath(videoId);
        var vttPath = GetSpriteVttPath(videoId);
        if (File.Exists(spritePath) && File.Exists(vttPath)) return;

        var (filePath, duration) = await GetVideoFileInfoAsync(videoId, ct);
        if (filePath == null || duration <= 0) return;

        var ffmpegPath = GetCachedFfmpegPath();
        if (ffmpegPath == null) return;

        var spriteDir = Path.GetDirectoryName(spritePath)!;
        Directory.CreateDirectory(spriteDir);
        var sem = GetFfmpegSemaphore();
        await sem.WaitAsync(ct);

        try
        {
            // Calculate grid dimensions
            var frameCount = Math.Min(SpriteFrameCount, Math.Max(1, (int)(duration / 2)));
            var cols = (int)Math.Ceiling(Math.Sqrt(frameCount));
            var rows = (int)Math.Ceiling((double)frameCount / cols);
            var interval = duration / frameCount;

            if (await TryGenerateVideoSpriteViaInProcessAsync(ffmpegPath, filePath, spritePath, vttPath, frameCount, cols, rows, interval, duration, ct))
                return;

            logger.LogDebug("Falling back to ffmpeg CLI sprite generation for video {VideoId}", videoId);

            if (await TryGenerateVideoSpriteViaFfmpegAsync(ffmpegPath, filePath, spritePath, frameCount, cols, rows, duration, ct))
            {
                await WriteSpriteVttAsync(spritePath, vttPath, frameCount, cols, rows, interval, ct, duration: duration);
                return;
            }

            logger.LogDebug("Falling back to ffmpeg process frame extraction for sprite generation of video {VideoId}", videoId);

            // Build timestamps for seek-based extraction (center of each interval)
            var timestamps = new double[frameCount];
            for (var i = 0; i < frameCount; i++)
                timestamps[i] = interval * (i + 0.5);

            Image<Rgba32>[]? frames = null;
            frames = await FfmpegProcessFrameExtractor.ExtractFramesAsync(ffmpegPath, filePath, timestamps, SpriteFrameSize, logger, ct);

            if (frames == null)
            {
                logger.LogWarning("Sprite generation failed for video {VideoId} - frame extraction returned null", videoId);
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
                logger.LogWarning("Sprite generation failed for video {VideoId}", videoId);
                return;
            }

            await WriteSpriteVttAsync(spritePath, vttPath, frameCount, cols, rows, interval, ct, fw, fh, duration);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error generating sprite for video {VideoId}", videoId);
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task<bool> TryGenerateVideoSpriteViaInProcessAsync(string ffmpegPath, string filePath, string spritePath, string vttPath, int frameCount, int cols, int rows, double interval, double duration, CancellationToken ct)
    {
        if (!UseInProcessVideoFrameExtraction)
            return false;

        FfmpegInProcess.EnsureInitialized(ffmpegPath, config.EnableFfmpegHwAccel);
        if (!FfmpegInProcess.IsAvailable)
            return false;

        var timestamps = new double[frameCount];
        for (var i = 0; i < frameCount; i++)
            timestamps[i] = interval * (i + 0.5);

        var tempPath = spritePath + ".tmp.jpg";
        Image<Rgba32>[]? frames = null;
        try
        {
            frames = FfmpegInProcess.ExtractFrames(filePath, timestamps, SpriteFrameSize, threadCount: 1, ct);
            if (frames == null)
                return false;

            var frameWidth = frames[0].Width;
            var frameHeight = frames[0].Height;
            using var sheet = new Image<Rgba32>(frameWidth * cols, frameHeight * rows);
            for (var idx = 0; idx < frameCount; idx++)
            {
                var x = frameWidth * (idx % cols);
                var y = frameHeight * (idx / cols);
                sheet.Mutate(ctx => ctx.DrawImage(frames[idx], new Point(x, y), 1f));
            }

            await sheet.SaveAsJpegAsync(tempPath, new JpegEncoder { Quality = 75 }, ct);
            if (!File.Exists(tempPath))
                return false;

            File.Move(tempPath, spritePath, overwrite: true);
            await WriteSpriteVttAsync(spritePath, vttPath, frameCount, cols, rows, interval, ct, frameWidth, frameHeight, duration);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "In-process sprite generation failed for {FilePath}; falling back to ffmpeg CLI", filePath);
            return false;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }

            if (frames != null)
            {
                foreach (var frame in frames)
                    frame?.Dispose();
            }
        }
    }

    private async Task<bool> TryGenerateVideoSpriteViaFfmpegAsync(string ffmpegPath, string filePath, string spritePath, int frameCount, int cols, int rows, double duration, CancellationToken ct)
    {
        var tempPath = spritePath + ".tmp.jpg";
        try
        {
            var fps = frameCount / Math.Max(duration, 0.001d);
            var fpsText = fps.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);
            var decodeArgs = GetFfmpegDecodeArgs();
            var filter = $"fps={fpsText},scale={SpriteFrameSize}:-2,tile={cols}x{rows}:margin=0:padding=0";
            var args = $"{decodeArgs} -v error -fflags +discardcorrupt -err_detect ignore_err -y -i \"{filePath}\" -vf \"{filter}\" -frames:v 1 -q:v 3 -f image2 \"{tempPath}\"";
            var timeout = TimeSpan.FromSeconds(Math.Clamp(duration / 2d, 45d, 300d));
            if (!await TryRunFfmpegAsync(ffmpegPath, args, timeout, ct) || !File.Exists(tempPath))
                return false;

            File.Move(tempPath, spritePath, overwrite: true);
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

    private async Task<(string? filePath, double duration)> GetVideoFileInfoAsync(int videoId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

        var videoFile = await db.VideoFiles
            .Include(f => f.ParentFolder)
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.VideoId == videoId, ct);

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

    private async Task<bool> TryRunFfmpegAsync(string ffmpegPath, string args, TimeSpan timeout, CancellationToken ct)
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
            return false;
        }

        if (process.ExitCode == 0)
            return true;

        var stderr = await stderrTask;
        logger.LogWarning("FFmpeg failed (exit {Code}): {Error}", process.ExitCode, stderr[..Math.Min(500, stderr.Length)]);
        return false;
    }

    public string StartGenerateAllThumbnails()
    {
        return jobService.Enqueue("generate_thumbnails", "Generating thumbnails", async (progress, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

            var videoIds = await db.Videos.Select(s => s.Id).ToListAsync(ct);
            var total = videoIds.Count;
            var processed = 0;

            foreach (var videoId in videoIds)
            {
                ct.ThrowIfCancellationRequested();
                processed++;
                progress.Report((double)processed / total, $"Video {processed}/{total}");

                var thumbPath = GetThumbnailPath(videoId);
                if (File.Exists(thumbPath)) continue;

                await GenerateVideoThumbnailAsync(videoId, null, ct);
            }

            logger.LogInformation("Generated thumbnails for {Count} videos", total);
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

    private string GetFfmpegDecodeArgs()
    {
        // These extraction pipelines use software filters/output, so implicit hwaccel adds
        // costly hwdownload/format bridging and can be slower than plain CPU decode.
        if (!string.IsNullOrWhiteSpace(config.LiveTranscodeInputArgs))
            return config.LiveTranscodeInputArgs;

        if (!string.IsNullOrWhiteSpace(config.TranscodeInputArgs))
            return config.TranscodeInputArgs;

        return string.Empty;
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
            var listed = ListFfmpegEncoders(ffmpegPath);

            // Prefer NVENC > QSV > AMF > VideoToolbox. Verify each candidate with an actual
            // test encode; presence in the encoder list does not guarantee the runtime
            // can open a session (e.g. NVENC client-key mismatches with the installed driver).
            string[] hwEncoders = ["h264_nvenc", "h264_qsv", "h264_amf", "h264_videotoolbox"];
            foreach (var enc in hwEncoders)
            {
                if (!listed.Contains(enc, StringComparer.OrdinalIgnoreCase)) continue;
                if (!ProbeEncoder(ffmpegPath, enc, out var probeError))
                {
                    logger.LogDebug("Skipping {Encoder}: probe failed ({Error})", enc, probeError);
                    continue;
                }

                _hwEncoder = enc;
                logger.LogInformation("Using HW-accelerated H.264 encoder: {Encoder}", enc);
                return enc;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to detect HW encoders, falling back to libx264");
        }

        _hwEncoder = "libx264";
        logger.LogInformation("Using software H.264 encoder: libx264");
        return "libx264";
    }

    private static IReadOnlyList<string> ListFfmpegEncoders(string ffmpegPath)
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
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool ProbeEncoder(string ffmpegPath, string encoder, out string error)
    {
        error = string.Empty;
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-hide_banner -v error -f lavfi -i color=size=64x64:rate=1:duration=0.1 -c:v {encoder} -frames:v 1 -f null -",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        try
        {
            process.Start();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(10000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                error = "timed out";
                return false;
            }
            if (process.ExitCode == 0)
                return true;
            error = stderr.Length > 200 ? stderr[..200] : stderr;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

