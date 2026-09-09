using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using Cove.Core.Common;
using Cove.Core.Interfaces;
using Cove.Data;

namespace Cove.Api.Services;

public class StreamService(IServiceScopeFactory scopeFactory, IThumbnailService thumbnailService, IBlobService blobService, IMemoryCache? memoryCache = null) : IStreamService
{
    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".mkv"] = "video/x-matroska",
        [".avi"] = "video/x-msvideo",
        [".webm"] = "video/webm",
        [".mov"] = "video/quicktime",
        [".wmv"] = "video/x-ms-wmv",
        [".flv"] = "video/x-flv",
        [".m4v"] = "video/x-m4v",
        [".mpg"] = "video/mpeg",
        [".mpeg"] = "video/mpeg",
        [".ts"] = "video/mp2t",
        [".rmvb"] = "application/vnd.rn-realmedia-vbr",
        [".rm"] = "application/vnd.rn-realmedia",
    };

    public async Task<(Stream stream, string contentType, long? fileSize)?> GetVideoStream(int videoId, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

        var sourceVideoId = await ResolveSourceVideoIdAsync(db, videoId, ct);
        if (!sourceVideoId.HasValue) return null;

        var videoFile = await db.VideoFiles.FirstOrDefaultAsync(f => f.VideoId == sourceVideoId.Value, ct);

        if (videoFile == null) return null;

        var filePath = FilesystemPaths.ToNativePath(videoFile.Path);

        if (!File.Exists(filePath)) return null;

        var ext = Path.GetExtension(filePath);
        var contentType = MimeTypes.GetValueOrDefault(ext, "application/octet-stream");

        // FileShare.Delete lets the user delete the source file while the player still holds this stream
        // open (e.g. "delete from the video detail page"). Without it, Windows raises a sharing violation
        // ("being used by another process"). The audio stream already does this. On Windows the file is
        // unlinked once the last handle closes (when the player connection ends); on POSIX it unlinks
        // immediately and the open handle keeps serving until closed.
        var stream = FileReadRace.TryOpenRead(
            filePath,
            FileShare.Read | FileShare.Delete,
            pathWasObserved: true);
        return stream == null ? null : (stream, contentType, stream.Length);
    }

    public async Task<(Stream stream, string contentType, bool useLongCache)?> GetVideoScreenshot(int videoId, double? seconds, CancellationToken ct = default)
    {
        using var scope = scopeFactory?.CreateScope();
        var db = scope?.ServiceProvider.GetService<CoveContext>();
        var videoSource = db is null ? new VideoSource(videoId, null) : await ResolveVideoSourceAsync(db, videoId, ct);
        if (videoSource is null) return null;

        var sourceVideoId = videoSource.Value.SourceVideoId;
        var effectiveSeconds = seconds ?? videoSource.Value.ClipStartSec;

        // For timestamped thumbnails, only serve from cache — never generate on demand.
        // Thumbnail generation is exclusively the job of the generate task.
        if (effectiveSeconds.HasValue)
        {
            var tsPath = thumbnailService.GetTimestampedThumbnailPath(sourceVideoId, effectiveSeconds.Value);
            if (File.Exists(tsPath))
            {
                var timestampStream = FileReadRace.TryOpenRead(tsPath, bufferSize: 8192, pathWasObserved: true);
                if (timestampStream != null)
                    return (timestampStream, "image/jpeg", true);
            }

            var spriteFrame = await TryOpenSpriteFrameAsync(sourceVideoId, effectiveSeconds.Value, ct);
            if (spriteFrame != null) return spriteFrame;
        }

        var customCoverBlobId = db is null
            ? null
            : await db.Videos
                .AsNoTracking()
                .Where(video => video.Id == videoId)
                .Select(video => video.ImageBlobId)
                .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrWhiteSpace(customCoverBlobId))
        {
            var customCover = await blobService.GetBlobAsync(customCoverBlobId, ct);
            if (customCover != null)
            {
                return (customCover.Value.Stream, customCover.Value.ContentType, false);
            }
        }

        // Default cover thumbnail (no timestamp) — also only served from cache
        var thumbPath = await thumbnailService.GetVideoThumbnailPathAsync(sourceVideoId, ct);
        if (thumbPath == null) return null;

        var defaultStream = FileReadRace.TryOpenRead(thumbPath, bufferSize: 8192, pathWasObserved: true);
        return defaultStream == null ? null : (defaultStream, "image/jpeg", true);
    }

    public async Task<(Stream stream, string contentType, bool useLongCache)?> GetSegmentAnimatedPreview(int videoId, double seconds, CancellationToken ct = default)
    {
        using var scope = scopeFactory?.CreateScope();
        var db = scope?.ServiceProvider.GetService<CoveContext>();
        var sourceVideoId = db is null ? videoId : await ResolveSourceVideoIdAsync(db, videoId, ct);
        if (!sourceVideoId.HasValue) return null;

        var previewPath = thumbnailService.GetSegmentAnimatedPreviewPath(sourceVideoId.Value, seconds);
        if (!File.Exists(previewPath))
            return await TryOpenSpriteFrameAsync(sourceVideoId.Value, seconds, ct);

        var previewStream = FileReadRace.TryOpenRead(previewPath, bufferSize: 8192, pathWasObserved: true);
        if (previewStream == null)
            return await TryOpenSpriteFrameAsync(sourceVideoId.Value, seconds, ct);

        return (previewStream, "image/webp", true);
    }

    private static async Task<int?> ResolveSourceVideoIdAsync(CoveContext db, int videoId, CancellationToken ct)
        => (await ResolveVideoSourceAsync(db, videoId, ct))?.SourceVideoId;

    private static async Task<VideoSource?> ResolveVideoSourceAsync(CoveContext db, int videoId, CancellationToken ct)
    {
        var video = await db.Videos.AsNoTracking()
            .Where(item => item.Id == videoId)
            .Select(item => new { item.Id, item.ParentVideoId, item.ClipStartSec })
            .FirstOrDefaultAsync(ct);

        return video is null
            ? null
            : new VideoSource(video.ParentVideoId ?? video.Id, video.ClipStartSec);
    }

    private readonly record struct VideoSource(int SourceVideoId, double? ClipStartSec);

    private async Task<(Stream stream, string contentType, bool useLongCache)?> TryOpenSpriteFrameAsync(int videoId, double seconds, CancellationToken ct)
    {
        var vttPath = thumbnailService.GetSpriteVttPath(videoId);
        var spritePath = thumbnailService.GetSpritePath(videoId);
        if (!File.Exists(vttPath) || !File.Exists(spritePath)) return null;

        var frame = await FindSpriteFrameAsync(vttPath, seconds, ct);
        if (frame == null) return null;

        await using var spriteStream = FileReadRace.TryOpenRead(spritePath, bufferSize: 8192, pathWasObserved: true);
        if (spriteStream == null) return null;

        using var image = await Image.LoadAsync(spriteStream, ct);
        var bounds = new Rectangle(0, 0, image.Width, image.Height);
        var crop = Rectangle.Intersect(bounds, frame.Value.Bounds);
        if (crop.Width <= 0 || crop.Height <= 0) return null;

        image.Mutate(context => context.Crop(crop));

        var output = new MemoryStream();
        await image.SaveAsync(output, new JpegEncoder { Quality = 85 }, ct);
        output.Position = 0;
        return (output, "image/jpeg", true);
    }

    private async Task<SpriteFrame?> FindSpriteFrameAsync(string vttPath, double seconds, CancellationToken ct)
    {
        var frames = await LoadSpriteFramesAsync(vttPath, ct);
        if (frames.Count == 0) return null;

        SpriteFrame? previousFrame = null;

        foreach (var frame in frames)
        {
            if (seconds >= frame.StartSeconds && seconds < frame.EndSeconds)
                return frame;

            if (seconds < frame.StartSeconds)
                return previousFrame ?? frame;

            previousFrame = frame;
        }

        return previousFrame;
    }

    private async Task<IReadOnlyList<SpriteFrame>> LoadSpriteFramesAsync(string vttPath, CancellationToken ct)
    {
        var fileInfo = new FileInfo(vttPath);
        if (!fileInfo.Exists) return [];

        DateTime lastWriteTimeUtc;
        long length;
        try
        {
            lastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
            length = fileInfo.Length;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        catch (UnauthorizedAccessException ex) when (FileReadRace.IsWindowsDeletionRace(ex, vttPath))
        {
            return [];
        }

        var cacheKey = $"{nameof(StreamService)}:sprite-vtt:{vttPath}";
        if (memoryCache != null
            && memoryCache.TryGetValue(cacheKey, out SpriteFrameCache? cached)
            && cached is not null
            && cached.LastWriteTimeUtc == lastWriteTimeUtc
            && cached.Length == length)
        {
            return cached.Frames;
        }

        var lines = await FileReadRace.TryReadAllLinesAsync(vttPath, ct, pathWasObserved: true);
        if (lines == null) return [];
        var frames = ParseSpriteFrames(lines);
        memoryCache?.Set(
            cacheKey,
            new SpriteFrameCache(lastWriteTimeUtc, length, frames),
            new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(20),
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2),
            });

        return frames;
    }

    private static IReadOnlyList<SpriteFrame> ParseSpriteFrames(string[] lines)
    {
        var frames = new List<SpriteFrame>();

        for (var i = 0; i < lines.Length; i++)
        {
            var timing = lines[i].Trim();
            var separatorIndex = timing.IndexOf("-->", StringComparison.Ordinal);
            if (separatorIndex < 0) continue;

            if (!TryParseVttTime(timing[..separatorIndex], out var startSeconds)) continue;
            if (!TryParseVttTime(timing[(separatorIndex + 3)..], out var endSeconds)) continue;

            var bounds = TryParseSpriteBounds(lines, i + 1);
            if (bounds == null) continue;

            frames.Add(new SpriteFrame(startSeconds, endSeconds, bounds.Value));
        }

        return frames;
    }

    private static Rectangle? TryParseSpriteBounds(string[] lines, int startIndex)
    {
        for (var i = startIndex; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line.Contains("-->", StringComparison.Ordinal)) return null;

            var xywhIndex = line.IndexOf("#xywh=", StringComparison.OrdinalIgnoreCase);
            if (xywhIndex < 0) continue;

            var rectText = line[(xywhIndex + "#xywh=".Length)..];
            var parts = rectText.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 4) return null;

            return int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
                && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
                && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
                && width > 0
                && height > 0
                    ? new Rectangle(x, y, width, height)
                    : null;
        }

        return null;
    }

    private static bool TryParseVttTime(string value, out double seconds)
    {
        var token = value.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token))
        {
            seconds = 0;
            return false;
        }

        var parts = token.Replace(',', '.').Split(':');
        if (parts.Length is < 2 or > 3)
        {
            seconds = 0;
            return false;
        }

        var hours = 0;
        var minutesIndex = 0;
        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours))
            {
                seconds = 0;
                return false;
            }

            minutesIndex = 1;
        }

        if (!int.TryParse(parts[minutesIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(parts[minutesIndex + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var secondsPart))
        {
            seconds = 0;
            return false;
        }

        seconds = hours * 3600d + minutes * 60d + secondsPart;
        return true;
    }

    private readonly record struct SpriteFrame(double StartSeconds, double EndSeconds, Rectangle Bounds);
    private sealed record SpriteFrameCache(DateTime LastWriteTimeUtc, long Length, IReadOnlyList<SpriteFrame> Frames);
}
