using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Cove.Api.Services;
using Cove.Core.Interfaces;
using Cove.Data;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StreamController(IStreamService streamService, IThumbnailService thumbnailService, ITranscodeService transcodeService, CoveContext db) : ControllerBase
{
    [HttpGet("scene/{sceneId:int}")]
    public async Task<IActionResult> StreamScene(int sceneId, CancellationToken ct)
    {
        var result = await streamService.GetSceneStream(sceneId, ct);
        if (result == null) return NotFound();

        var (stream, contentType, fileSize) = result.Value;
        Response.Headers["Accept-Ranges"] = "bytes";

        if (fileSize.HasValue)
            return File(stream, contentType, enableRangeProcessing: true);

        return File(stream, contentType);
    }

    [HttpGet("scene/{sceneId:int}/screenshot")]
    public async Task<IActionResult> GetScreenshot(int sceneId, [FromQuery] double? seconds, CancellationToken ct)
    {
        var result = await streamService.GetSceneScreenshot(sceneId, seconds, ct);
        if (result == null) return NotFound();

        var (stream, contentType) = result.Value;
        Response.Headers["Cache-Control"] = "public, max-age=86400";
        return File(stream, contentType);
    }

    [HttpGet("scene/{sceneId:int}/preview")]
    public IActionResult GetPreview(int sceneId)
    {
        var path = thumbnailService.GetPreviewPath(sceneId);
        if (!System.IO.File.Exists(path)) return NotFound();

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        Response.Headers["Cache-Control"] = "public, max-age=86400";
        Response.Headers["Accept-Ranges"] = "bytes";
        return File(stream, "video/mp4", enableRangeProcessing: true);
    }

    [HttpGet("scene/{sceneId:int}/sprite")]
    public IActionResult GetSprite(int sceneId)
    {
        var path = thumbnailService.GetSpritePath(sceneId);
        if (!System.IO.File.Exists(path)) return NotFound();

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        Response.Headers["Cache-Control"] = "public, max-age=86400";
        return File(stream, "image/jpeg");
    }

    [HttpGet("scene/{sceneId:int}/vtt/thumbs")]
    public IActionResult GetSpriteVtt(int sceneId)
    {
        var path = thumbnailService.GetSpriteVttPath(sceneId);
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
    public async Task<IActionResult> GetImageThumbnail(int imageId, CancellationToken ct)
    {
        // For images, just serve the original file (they're already images)
        return await GetImage(imageId, ct);
    }

    [HttpGet("scene/{sceneId:int}/caption/{captionId:int}")]
    public async Task<IActionResult> GetCaption(int sceneId, int captionId, CancellationToken ct)
    {
        var caption = await db.VideoCaptions
            .Include(c => c.File)
            .FirstOrDefaultAsync(c => c.Id == captionId && c.File != null
                && db.Scenes.Any(s => s.Id == sceneId && s.Files.Any(f => f.Id == c.FileId)), ct);

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

    [HttpGet("scene/{sceneId:int}/captions")]
    public async Task<IActionResult> GetCaptions(int sceneId, CancellationToken ct)
    {
        var scene = await db.Scenes
            .Include(s => s.Files).ThenInclude(f => f.Captions)
            .FirstOrDefaultAsync(s => s.Id == sceneId, ct);

        if (scene == null) return NotFound();

        var captions = scene.Files
            .SelectMany(f => f.Captions)
            .Select(c => new { c.Id, c.LanguageCode, c.CaptionType, c.Filename })
            .ToList();

        return Ok(captions);
    }

    // ===== Transcoding / HLS =====

    [HttpGet("scene/{sceneId:int}/transcode")]
    public async Task<IActionResult> TranscodeScene(int sceneId, [FromQuery] string? resolution, CancellationToken ct)
    {
        var filePath = await GetSceneFilePathAsync(sceneId, ct);
        if (filePath == null) return NotFound();

        var stream = await transcodeService.TranscodeToMp4Async(filePath, resolution, ct);
        if (stream == null) return StatusCode(503, "Transcoding unavailable — FFmpeg not found");

        Response.Headers["Accept-Ranges"] = "none";
        return File(stream, "video/mp4");
    }

    [HttpGet("scene/{sceneId:int}/hls/master.m3u8")]
    public async Task<IActionResult> GetHlsMasterPlaylist(int sceneId, CancellationToken ct)
    {
        var file = await db.VideoFiles.FirstOrDefaultAsync(f => f.SceneId == sceneId, ct);
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
            lines.Add($"/api/stream/scene/{sceneId}/hls/{res}.m3u8");
        }

        Response.Headers["Cache-Control"] = "no-cache";
        return Content(string.Join("\n", lines), "application/vnd.apple.mpegurl");
    }

    [HttpGet("scene/{sceneId:int}/hls/{profile}.m3u8")]
    public async Task<IActionResult> GetHlsPlaylist(int sceneId, string profile, CancellationToken ct)
    {
        var filePath = await GetSceneFilePathAsync(sceneId, ct);
        if (filePath == null) return NotFound();

        var resolution = profile == "original" ? null : profile;
        var manifest = await transcodeService.GenerateHlsManifestAsync(sceneId, filePath, resolution, ct);
        if (manifest == null) return StatusCode(503, "HLS generation failed — FFmpeg not found or error occurred");

        // Rewrite segment paths to use API URLs
        manifest = manifest.Replace($"{resolution ?? "original"}_", $"/api/stream/scene/{sceneId}/hls/segment/{resolution ?? "original"}_");

        Response.Headers["Cache-Control"] = "no-cache";
        return Content(manifest, "application/vnd.apple.mpegurl");
    }

    [HttpGet("scene/{sceneId:int}/hls/segment/{segment}")]
    public async Task<IActionResult> GetHlsSegment(int sceneId, string segment, CancellationToken ct)
    {
        var stream = await transcodeService.GetHlsSegmentAsync(sceneId, segment, ct);
        if (stream == null) return NotFound();

        Response.Headers["Cache-Control"] = "public, max-age=86400";
        return File(stream, "video/mp2t");
    }

    [HttpGet("scene/{sceneId:int}/resolutions")]
    public async Task<IActionResult> GetAvailableResolutions(int sceneId, CancellationToken ct)
    {
        var file = await db.VideoFiles.FirstOrDefaultAsync(f => f.SceneId == sceneId, ct);
        if (file == null) return NotFound();

        return Ok(transcodeService.GetAvailableResolutions(file.Width, file.Height));
    }

    private async Task<string?> GetSceneFilePathAsync(int sceneId, CancellationToken ct)
    {
        var videoFile = await db.VideoFiles
            .Include(f => f.ParentFolder)
            .FirstOrDefaultAsync(f => f.SceneId == sceneId, ct);

        if (videoFile == null) return null;

        var filePath = videoFile.ParentFolder != null
            ? Path.Combine(videoFile.ParentFolder.Path, videoFile.Basename)
            : videoFile.Basename;

        return System.IO.File.Exists(filePath) ? filePath : null;
    }

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
