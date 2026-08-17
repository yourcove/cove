using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[RequiresPermission(Permissions.StreamRead)]
[AllowShareLinkAccess]
public class StreamController(IStreamService streamService, IThumbnailService thumbnailService, ITranscodeService transcodeService, CoveContext db) : ControllerBase
{
    // Portrait-style headroom around a detection box. Long-standing default for face covers; callers
    // that must distinguish neighbouring detections pass a smaller context (see GetDetectionCrop).
    private const double DefaultDetectionCropContext = 1.8;

    // At or below this the crop is tight enough that frame alignment matters more than cost.
    private const double TightDetectionCropContext = 1.5;

    [HttpGet("video/{videoId:int}")]
    public async Task<IActionResult> StreamVideo(int videoId, CancellationToken ct)
    {
        var result = await streamService.GetVideoStream(videoId, ct);
        if (result == null) return NotFound();

        var (stream, contentType, fileSize) = result.Value;
        Response.Headers["Accept-Ranges"] = "bytes";

        if (fileSize.HasValue)
            return File(stream, contentType, enableRangeProcessing: true);

        return File(stream, contentType);
    }

    [HttpGet("video/{videoId:int}/screenshot")]
    public async Task<IActionResult> GetScreenshot(int videoId, [FromQuery] double? seconds, CancellationToken ct)
    {
        var result = await streamService.GetVideoScreenshot(videoId, seconds, ct);
        if (result == null) return NotFound();

        var (stream, contentType, useLongCache) = result.Value;
        Response.Headers["Cache-Control"] = useLongCache
            ? "public, max-age=86400"
            : "no-store, no-cache, max-age=0, must-revalidate";
        return File(stream, contentType);
    }

    [HttpGet("video/{videoId:int}/segment-preview")]
    public async Task<IActionResult> GetSegmentPreview(int videoId, [FromQuery] double seconds, CancellationToken ct)
    {
        var result = await streamService.GetSegmentAnimatedPreview(videoId, seconds, ct);
        if (result == null) return NotFound();

        var (stream, contentType, useLongCache) = result.Value;
        Response.Headers["Cache-Control"] = useLongCache
            ? "public, max-age=86400"
            : "no-store, no-cache, max-age=0, must-revalidate";
        return File(stream, contentType);
    }

    [HttpGet("video/{videoId:int}/preview")]
    public IActionResult GetPreview(int videoId)
    {
        var path = GetExistingPreviewPath(videoId);
        if (path == null) return NotFound();

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        SetPreviewHeaders();
        return File(stream, "video/mp4", enableRangeProcessing: true);
    }

    [HttpHead("video/{videoId:int}/preview")]
    public IActionResult HeadPreview(int videoId)
    {
        var path = GetExistingPreviewPath(videoId);
        if (path == null) return NotFound();

        SetPreviewHeaders();
        Response.ContentType = "video/mp4";
        Response.ContentLength = new FileInfo(path).Length;
        return Ok();
    }

    [HttpGet("video/{videoId:int}/preview/status")]
    public IActionResult GetPreviewStatus(int videoId)
    {
        return Ok(new { available = GetExistingPreviewPath(videoId) != null });
    }

    private string? GetExistingPreviewPath(int videoId)
    {
        var sourceVideoId = db is null ? videoId : ResolveSourceVideoId(videoId);
        if (!sourceVideoId.HasValue) return null;

        var path = thumbnailService.GetPreviewPath(sourceVideoId.Value);
        return System.IO.File.Exists(path) ? path : null;
    }

    private void SetPreviewHeaders()
    {
        Response.Headers["Cache-Control"] = "public, max-age=86400";
        Response.Headers["Accept-Ranges"] = "bytes";
    }

    [HttpGet("video/{videoId:int}/sprite")]
    public IActionResult GetSprite(int videoId)
    {
        var sourceVideoId = db is null ? videoId : ResolveSourceVideoId(videoId);
        if (!sourceVideoId.HasValue) return NotFound();

        var path = thumbnailService.GetSpritePath(sourceVideoId.Value);
        if (!System.IO.File.Exists(path)) return NotFound();

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        Response.Headers["Cache-Control"] = "public, max-age=86400";
        return File(stream, "image/jpeg");
    }

    [HttpGet("video/{videoId:int}/vtt/thumbs")]
    public IActionResult GetSpriteVtt(int videoId)
    {
        var sourceVideoId = db is null ? videoId : ResolveSourceVideoId(videoId);
        if (!sourceVideoId.HasValue) return NotFound();

        var path = thumbnailService.GetSpriteVttPath(sourceVideoId.Value);
        if (!System.IO.File.Exists(path)) return NotFound();

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        Response.Headers["Cache-Control"] = "public, max-age=86400";
        return File(stream, "text/vtt");
    }

    [HttpGet("image/{imageId:int}")]
    public async Task<IActionResult> GetImage(int imageId, CancellationToken ct)
    {
        var result = await thumbnailService.GetImageStreamAsync(imageId, ct);
        if (result == null) return NotFound();

        var (stream, contentType, supportsRangeRequests) = result.Value;

        Response.Headers["Cache-Control"] = "public, max-age=86400";

        if (supportsRangeRequests)
        {
            Response.Headers["Accept-Ranges"] = "bytes";
            return File(stream, contentType, enableRangeProcessing: true);
        }

        return File(stream, contentType);
    }

    [HttpGet("image/{imageId:int}/thumbnail")]
    public async Task<IActionResult> GetImageThumbnail(int imageId, [FromQuery] int? max, CancellationToken ct)
    {
        var result = await thumbnailService.GetImageThumbnailStreamAsync(imageId, max ?? 0, ct);
        if (result == null) return NotFound();

        var (stream, contentType, supportsRangeRequests) = result.Value;
        Response.Headers["Cache-Control"] = "public, max-age=86400";

        if (supportsRangeRequests)
        {
            Response.Headers["Accept-Ranges"] = "bytes";
            return File(stream, contentType, enableRangeProcessing: true);
        }

        return File(stream, contentType);
    }

    /// <summary>
    /// A crop of the frame around a detection. <paramref name="context"/> scales how much surrounding
    /// frame is included, as a multiple of the detection box: the default 1.8 is a portrait-style crop
    /// with headroom, suited to cover images. Callers that need to tell adjacent detections apart —
    /// picking one face out of several in the same shot — should ask for something near 1.0, since the
    /// extra context readily pulls a neighbouring face into the frame.
    /// </summary>
    [HttpGet("detection/{detectionId:int}/crop")]
    public async Task<IActionResult> GetDetectionCrop(int detectionId, [FromQuery] int? max, [FromQuery] double? context, CancellationToken ct)
    {
        var detection = await db.VisibleDetections()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == detectionId, ct);
        if (detection is null) return NotFound();

        if (detection.HostType == DetectionHostType.Video)
        {
            // Without an exact-timestamp frame the crop falls back to a sprite tile, which is both
            // low-resolution and sampled at a coarser interval than the detection — so the box no longer
            // lines up with where the face actually was. A large request needs the resolution; a tight
            // context needs the alignment, because there is no slack left to absorb the drift.
            var needsExactFrame = max.GetValueOrDefault(640) > 640
                || context.GetValueOrDefault(DefaultDetectionCropContext) < TightDetectionCropContext;
            if (needsExactFrame)
            {
                await EnsureHighResolutionDetectionFrameAsync(detection, ct);
            }

            var result = await streamService.GetVideoScreenshot(detection.HostId, detection.ObservedAtSec, ct);
            if (result is null) return NotFound();

            await using var stream = result.Value.stream;
            return await BuildDetectionCropResultAsync(detection, stream, max, context, ct);
        }

        if (detection.HostType == DetectionHostType.Image)
        {
            var result = await thumbnailService.GetImageStreamAsync(detection.HostId, ct);
            if (result is null) return NotFound();

            await using var stream = result.Value.stream;
            return await BuildDetectionCropResultAsync(detection, stream, max, context, ct);
        }

        return NotFound();
    }

    private async Task EnsureHighResolutionDetectionFrameAsync(Detection detection, CancellationToken ct)
    {
        if (!detection.ObservedAtSec.HasValue)
        {
            return;
        }

        var sourceVideoId = await ResolveSourceVideoIdAsync(detection.HostId, ct);
        if (!sourceVideoId.HasValue)
        {
            return;
        }

        var framePath = thumbnailService.GetTimestampedThumbnailPath(sourceVideoId.Value, detection.ObservedAtSec.Value);
        if (System.IO.File.Exists(framePath))
        {
            return;
        }

        await thumbnailService.GenerateVideoThumbnailAsync(sourceVideoId.Value, detection.ObservedAtSec.Value, ct);
    }

    [HttpGet("video/{videoId:int}/caption/{captionId:int}")]
    public async Task<IActionResult> GetCaption(int videoId, int captionId, CancellationToken ct)
    {
        var sourceVideoId = await ResolveSourceVideoIdAsync(videoId, ct);
        if (!sourceVideoId.HasValue) return NotFound();

        var caption = await db.VideoCaptions
            .Include(c => c.File)
            .FirstOrDefaultAsync(c => c.Id == captionId && c.File != null
                && db.Videos.Any(s => s.Id == sourceVideoId.Value && s.Files.Any(f => f.Id == c.FileId)), ct);

        if (caption?.File == null) return NotFound();

        var videoDir = Path.GetDirectoryName(caption.File.Path);
        if (videoDir == null) return NotFound();

        var captionPath = Path.Combine(videoDir, caption.Filename);
        if (!System.IO.File.Exists(captionPath)) return NotFound();

        var contentType = caption.CaptionType == "srt" ? "application/x-subrip" : "text/vtt";
        var stream = new FileStream(captionPath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        Response.Headers["Cache-Control"] = "public, max-age=3600";
        return File(stream, contentType);
    }

    [HttpGet("video/{videoId:int}/captions")]
    public async Task<IActionResult> GetCaptions(int videoId, CancellationToken ct)
    {
        var sourceVideoId = await ResolveSourceVideoIdAsync(videoId, ct);
        if (!sourceVideoId.HasValue) return NotFound();

        var video = await db.Videos
            .Include(s => s.Files).ThenInclude(f => f.Captions)
            .FirstOrDefaultAsync(s => s.Id == sourceVideoId.Value, ct);

        if (video == null) return NotFound();

        var captions = video.Files
            .SelectMany(f => f.Captions)
            .Select(c => new { c.Id, c.LanguageCode, c.CaptionType, c.Filename })
            .ToList();

        return Ok(captions);
    }

    // ===== Transcoding / HLS =====

    [HttpGet("video/{videoId:int}/transcode")]
    public async Task<IActionResult> TranscodeVideo(int videoId, [FromQuery] string? resolution, [FromQuery] double? start, CancellationToken ct)
    {
        var filePath = await GetVideoFilePathAsync(videoId, ct);
        if (filePath == null) return NotFound();

        var startSeconds = start.HasValue && double.IsFinite(start.Value) ? Math.Max(0, start.Value) : 0;
        var stream = await transcodeService.TranscodeToMp4Async(filePath, resolution, startSeconds, ct);
        // null = FFmpeg missing or the encode produced no output (e.g. a hardware-acceleration
        // pipeline that can't run on this host). Either way the server log has the FFmpeg error;
        // return a real failure status so the player doesn't sit on a dead 200 stream.
        if (stream == null) return StatusCode(503, "Transcoding failed — check the server log and your hardware-acceleration setting");

        Response.Headers["Accept-Ranges"] = "none";
        return File(stream, "video/mp4");
    }

    [HttpGet("video/{videoId:int}/hls/master.m3u8")]
    public async Task<IActionResult> GetHlsMasterPlaylist(int videoId, CancellationToken ct)
    {
        var sourceVideoId = await ResolveSourceVideoIdAsync(videoId, ct);
        if (!sourceVideoId.HasValue) return NotFound();

        var file = await db.VideoFiles.FirstOrDefaultAsync(f => f.VideoId == sourceVideoId.Value, ct);
        if (file == null) return NotFound();

        var resolutions = transcodeService.GetAvailableResolutions(file.Width, file.Height);
        if (resolutions.Length == 0)
            resolutions = ["original"];

        // Build master playlist
        var lines = new List<string> { "#EXTM3U" };
        foreach (var res in resolutions)
        {
            var bw = res switch { "240p" => 400000, "360p" => 800000, "480p" => 1200000, "720p" => 2500000, "1080p" => 5000000, "1440p" => 8000000, "4K" => 15000000, _ => 5000000 };
            lines.Add($"#EXT-X-STREAM-INF:BANDWIDTH={bw},RESOLUTION={GetResForLabel(res)},NAME=\"{res}\"");
            lines.Add(AppendMediaAuthQuery($"/api/stream/video/{videoId}/hls/{res}.m3u8"));
        }

        Response.Headers["Cache-Control"] = "no-cache";
        return Content(string.Join("\n", lines), "application/vnd.apple.mpegurl");
    }

    [HttpGet("video/{videoId:int}/hls/{profile}.m3u8")]
    public async Task<IActionResult> GetHlsPlaylist(int videoId, string profile, CancellationToken ct)
    {
        var filePath = await GetVideoFilePathAsync(videoId, ct);
        if (filePath == null) return NotFound();

        var resolution = profile == "original" ? null : profile;
        var manifest = await transcodeService.GenerateHlsManifestAsync(videoId, filePath, resolution, ct);
        if (manifest == null) return StatusCode(503, "HLS generation failed — FFmpeg not found or error occurred");

        manifest = RewriteHlsSegmentUrls(manifest, videoId, resolution ?? "original");

        Response.Headers["Cache-Control"] = "no-cache";
        return Content(manifest, "application/vnd.apple.mpegurl");
    }

    [HttpGet("video/{videoId:int}/hls/segment/{segment}")]
    public async Task<IActionResult> GetHlsSegment(int videoId, string segment, CancellationToken ct)
    {
        if (!await db.Videos.AsNoTracking().AnyAsync(video => video.Id == videoId, ct))
            return NotFound();

        var stream = await transcodeService.GetHlsSegmentAsync(videoId, segment, ct);
        if (stream == null) return NotFound();

        Response.Headers["Cache-Control"] = "public, max-age=86400";
        return File(stream, "video/mp2t");
    }

    [HttpGet("video/{videoId:int}/resolutions")]
    public async Task<IActionResult> GetAvailableResolutions(int videoId, CancellationToken ct)
    {
        var sourceVideoId = await ResolveSourceVideoIdAsync(videoId, ct);
        if (!sourceVideoId.HasValue) return NotFound();

        var file = await db.VideoFiles.FirstOrDefaultAsync(f => f.VideoId == sourceVideoId.Value, ct);
        if (file == null) return NotFound();

        return Ok(transcodeService.GetAvailableResolutions(file.Width, file.Height));
    }

    private async Task<string?> GetVideoFilePathAsync(int videoId, CancellationToken ct)
    {
        var sourceVideoId = await ResolveSourceVideoIdAsync(videoId, ct);
        if (!sourceVideoId.HasValue) return null;

        var videoFile = await db.VideoFiles
            .Include(f => f.ParentFolder)
            .FirstOrDefaultAsync(f => f.VideoId == sourceVideoId.Value, ct);

        if (videoFile == null) return null;

        var filePath = videoFile.ParentFolder != null
            ? Path.Combine(videoFile.ParentFolder.Path, videoFile.Basename)
            : videoFile.Basename;

        return System.IO.File.Exists(filePath) ? filePath : null;
    }

    private string RewriteHlsSegmentUrls(string manifest, int videoId, string profile)
    {
        var segmentPrefix = profile + "_";
        var apiPrefix = $"/api/stream/video/{videoId}/hls/segment/";
        var lines = manifest.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (line.StartsWith(segmentPrefix, StringComparison.Ordinal))
                lines[index] = AppendMediaAuthQuery(apiPrefix + line);
        }

        return string.Join("\n", lines);
    }

    private string AppendMediaAuthQuery(string url)
    {
        var query = Request.Query;
        var values = new List<string>();
        AddQueryValue(values, query, "access_token");
        AddQueryValue(values, query, "share_token");
        AddQueryValue(values, query, "share_password");
        if (values.Count == 0)
            return url;

        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return url + separator + string.Join('&', values);
    }

    private static void AddQueryValue(List<string> values, IQueryCollection query, string key)
    {
        if (!query.TryGetValue(key, out var rawValues))
            return;

        foreach (var rawValue in rawValues)
        {
            if (string.IsNullOrEmpty(rawValue))
                continue;

            values.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(rawValue)}");
        }
    }

    private int? ResolveSourceVideoId(int videoId)
        => db.Videos.AsNoTracking()
            .Where(video => video.Id == videoId)
            .Select(video => (int?)(video.ParentVideoId ?? video.Id))
            .FirstOrDefault();

    private async Task<int?> ResolveSourceVideoIdAsync(int videoId, CancellationToken ct)
    {
        var video = await db.Videos.AsNoTracking()
            .Where(item => item.Id == videoId)
            .Select(item => new { item.Id, item.ParentVideoId })
            .FirstOrDefaultAsync(ct);

        return video?.ParentVideoId ?? video?.Id;
    }

    private async Task<IActionResult> BuildDetectionCropResultAsync(Detection detection, Stream sourceStream, int? max, double? context, CancellationToken ct)
    {
        try
        {
            using var image = await SixLabors.ImageSharp.Image.LoadAsync<Rgba32>(sourceStream, ct);
            image.Mutate(static context => context.AutoOrient());

            var cropRect = BuildDetectionCropRectangle(image.Width, image.Height, detection, context);
            if (cropRect is null)
            {
                return NotFound();
            }

            image.Mutate(context => context.Crop(cropRect.Value));

            var maxDimension = Math.Clamp(max.GetValueOrDefault(640), 64, 2048);
            if (Math.Max(image.Width, image.Height) > maxDimension)
            {
                image.Mutate(context => context.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(maxDimension, maxDimension),
                }));
            }

            var output = new MemoryStream();
            await image.SaveAsJpegAsync(output, new JpegEncoder { Quality = 88 }, ct);
            output.Position = 0;

            Response.Headers["Cache-Control"] = "public, max-age=86400";
            return File(output, "image/jpeg");
        }
        catch when (!ct.IsCancellationRequested)
        {
            return NotFound();
        }
    }

    private static Rectangle? BuildDetectionCropRectangle(int imageWidth, int imageHeight, Detection detection, double? context = null)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || detection.W <= 0 || detection.H <= 0)
        {
            return null;
        }

        var normalized = detection.X >= 0
            && detection.Y >= 0
            && detection.X <= 1.000001f
            && detection.Y <= 1.000001f
            && detection.W <= 1.000001f
            && detection.H <= 1.000001f;

        var x = (double)detection.X;
        var y = (double)detection.Y;
        var width = (double)detection.W;
        var height = (double)detection.H;

        if (normalized)
        {
            x *= imageWidth;
            width *= imageWidth;
            y *= imageHeight;
            height *= imageHeight;
        }
        else if (detection.FrameWidth > 0 && detection.FrameHeight > 0)
        {
            x = x / detection.FrameWidth * imageWidth;
            width = width / detection.FrameWidth * imageWidth;
            y = y / detection.FrameHeight * imageHeight;
            height = height / detection.FrameHeight * imageHeight;
        }

        var left = Clamp((int)Math.Floor(x), 0, imageWidth - 1);
        var top = Clamp((int)Math.Floor(y), 0, imageHeight - 1);
        var right = Clamp((int)Math.Ceiling(x + width), left + 1, imageWidth);
        var bottom = Clamp((int)Math.Ceiling(y + height), top + 1, imageHeight);
        var boxWidth = Math.Max(1, right - left);
        var boxHeight = Math.Max(1, bottom - top);
        var contextScale = Math.Clamp(context ?? DefaultDetectionCropContext, 1.0, 4.0);
        var side = (int)Math.Ceiling(Math.Max(boxWidth, boxHeight) * contextScale);
        side = Math.Clamp(side, 1, Math.Min(imageWidth, imageHeight));

        // The upward nudge gives a portrait headroom, which only makes sense while there is surrounding
        // frame to spend on it. Fade it out with the context so a tight crop stays centred on the box
        // instead of sliding the subject downwards out of view.
        var headroomFactor = Math.Clamp(
            (contextScale - 1.0) / (DefaultDetectionCropContext - 1.0),
            0.0,
            1.0);
        var centerX = left + boxWidth / 2.0;
        var centerY = top + boxHeight / 2.0 - boxHeight * 0.1 * headroomFactor;
        var cropLeft = Clamp((int)Math.Round(centerX - side / 2.0), 0, Math.Max(0, imageWidth - side));
        var cropTop = Clamp((int)Math.Round(centerY - side / 2.0), 0, Math.Max(0, imageHeight - side));

        return new Rectangle(cropLeft, cropTop, side, side);
    }

    private static int Clamp(int value, int min, int max)
        => value < min ? min : value > max ? max : value;

    private static string GetResForLabel(string label) => label switch
    {
        "240p" => "426x240",
        "360p" => "640x360",
        "480p" => "854x480",
        "720p" => "1280x720",
        "1080p" => "1920x1080",
        "1440p" => "2560x1440",
        "4K" => "3840x2160",
        _ => "1920x1080"
    };
}
