using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Cove.Api.Imaging;

namespace Cove.Tests;

public class StreamControllerTests
{
    [Fact]
    public void HeadPreview_ReturnsNotFound_WhenPreviewFileIsMissing()
    {
        var controller = CreateController(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.mp4"));

        var result = controller.HeadPreview(123);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task HeadPreview_ReturnsVideoHeaders_WhenPreviewFileExists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cove-preview-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        try
        {
            var controller = CreateController(path);

            var result = controller.HeadPreview(123);

            Assert.IsType<OkResult>(result);
            Assert.Equal("video/mp4", controller.Response.ContentType);
            Assert.Equal(4, controller.Response.ContentLength);
            Assert.Equal("bytes", controller.Response.Headers["Accept-Ranges"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetPreviewStatus_ReturnsUnavailable_WhenPreviewFileIsMissing()
    {
        var controller = CreateController(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.mp4"));

        var result = controller.GetPreviewStatus(123);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.False((bool)ok.Value!.GetType().GetProperty("available")!.GetValue(ok.Value)!);
    }

    [Fact]
    public async Task GetHlsMasterPlaylist_AppendsMediaAuthQueryToVariantPlaylistUrls()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var video = new Video { Title = "HLS video" };
        var folder = new Folder { Path = Path.GetTempPath() };
        context.AddRange(video, folder);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var primaryFile = new VideoFile
        {
            VideoId = video.Id,
            ParentFolderId = folder.Id,
            Basename = "video.mp4",
            Width = 1920,
            Height = 1080,
        };
        context.VideoFiles.Add(primaryFile);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        video.PrimaryFileId = primaryFile.Id;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = CreateController(context, new FakeTranscodeService());
        SetQuery(controller, "?access_token=access token&share_token=share/token&ignored=true");

        var result = await controller.GetHlsMasterPlaylist(video.Id, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains($"/api/stream/video/{video.Id}/hls/720p.m3u8?fileId={primaryFile.Id}&access_token=access%20token&share_token=share%2Ftoken", content.Content);
        Assert.DoesNotContain("ignored=", content.Content);
    }

    [Fact]
    public async Task GetHlsPlaylist_AppendsMediaAuthQueryToSegmentUrls()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cove-hls-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var videoPath = Path.Combine(tempDir, "video.mp4");
        await File.WriteAllBytesAsync(videoPath, [1, 2, 3], TestContext.Current.CancellationToken);

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        try
        {
            var video = new Video { Title = "HLS media video" };
            var folder = new Folder { Path = tempDir };
            context.AddRange(video, folder);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            var primaryFile = new VideoFile
            {
                VideoId = video.Id,
                ParentFolderId = folder.Id,
                Basename = Path.GetFileName(videoPath),
                Width = 1920,
                Height = 1080,
            };
            context.VideoFiles.Add(primaryFile);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            video.PrimaryFileId = primaryFile.Id;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var controller = CreateController(context, new FakeTranscodeService());
            SetQuery(controller, "?access_token=access token&share_token=share/token&share_password=p@ss&ignored=true");

            var result = await controller.GetHlsPlaylist(video.Id, "original", CancellationToken.None);

            var content = Assert.IsType<ContentResult>(result);
            Assert.Contains($"/api/stream/video/{video.Id}/hls/segment/original_s0_0000.ts?fileId={primaryFile.Id}&access_token=access%20token&share_token=share%2Ftoken&share_password=p%40ss", content.Content);
            Assert.Contains($"/api/stream/video/{video.Id}/hls/segment/original_s0_0001.ts?fileId={primaryFile.Id}&access_token=access%20token&share_token=share%2Ftoken&share_password=p%40ss", content.Content);
            Assert.Contains("#EXTINF:4,", content.Content);
            Assert.DoesNotContain("ignored=", content.Content);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LibrarySvg_IsSandboxed_SoOpeningItDirectlyCannotRunScript(bool thumbnail)
    {
        var thumbnails = new FakeThumbnailService("missing.mp4")
        {
            ImageContentType = "image/svg+xml",
            ImageBytes = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray(),
        };
        var controller = new StreamController(null!, thumbnails, null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = thumbnail
            ? await controller.GetImageThumbnail(1, max: null, TestContext.Current.CancellationToken)
            : await controller.GetImage(1, TestContext.Current.CancellationToken);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("image/svg+xml", file.ContentType);
        var headers = controller.Response.Headers;
        Assert.Equal("default-src 'none'; img-src data:; style-src 'unsafe-inline'; sandbox", headers.ContentSecurityPolicy.ToString());
        Assert.Equal("nosniff", headers.XContentTypeOptions.ToString());
    }

    private static StreamController CreateController(string previewPath)
    {
        return new StreamController(null!, new FakeThumbnailService(previewPath), null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static StreamController CreateController(CoveContext context, ITranscodeService transcodeService)
    {
        return new StreamController(null!, new FakeThumbnailService("missing.mp4"), transcodeService, context)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    [Theory]
    // The default, loose crop used to settle for a stand-in; the tight one already made its frame.
    [InlineData(null)]
    [InlineData(1.0)]
    public async Task GetDetectionCrop_CutsFromTheDetectionsOwnFrame_NotAnotherMomentInItsSecond(double? context)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var video = new Video { Title = "Video with a face" };
        db.Videos.Add(video);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var detection = new Detection { HostType = DetectionHostType.Video, HostId = video.Id, ObservedAtSec = 12, X = 0.25f, Y = 0.25f, W = 0.5f, H = 0.5f, SourceKey = "test" };
        db.Detections.Add(detection);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var frames = new TemporaryFrameDirectory();
        var thumbnails = new FrameThumbnailService(frames.Path) { FrameColorToGenerate = new Rgba32(255, 0, 0) };
        // A frame from 12.9 s, written when frames shared a file per whole second (by "Set cover from frame").
        await WriteFrameAsync(Path.Combine(frames.Path, $"{video.Id}_t12.jpg"), new Rgba32(0, 255, 0));
        var streams = new FrameStreamService(thumbnails);
        var controller = new StreamController(streams, thumbnails, null!, db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = await controller.GetDetectionCrop(detection.Id, null, context, TestContext.Current.CancellationToken);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("public, max-age=86400", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal(new[] { (video.Id, 12.0) }, thumbnails.Generated);
        var crop = TestImages.Decode(file.FileStream);
        Assert.True(crop[crop.Width / 2, crop.Height / 2].R > 200, "The crop should come from the detection's own (red) frame.");
    }

    [Fact]
    public async Task GetDetectionCrop_WhenTheFrameCannotBeMade_CachesTheStandInBriefly()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var video = new Video { Title = "Video whose file is offline" };
        db.Videos.Add(video);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var detection = new Detection { HostType = DetectionHostType.Video, HostId = video.Id, ObservedAtSec = 12, X = 0.25f, Y = 0.25f, W = 0.5f, H = 0.5f, SourceKey = "test" };
        db.Detections.Add(detection);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var frames = new TemporaryFrameDirectory();
        var thumbnails = new FrameThumbnailService(frames.Path);
        var standIn = Path.Combine(frames.Path, "stand-in.jpg");
        await WriteFrameAsync(standIn, new Rgba32(0, 0, 255));
        var controller = new StreamController(new FrameStreamService(thumbnails) { StandInPath = standIn }, thumbnails, null!, db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = await controller.GetDetectionCrop(detection.Id, null, null, TestContext.Current.CancellationToken);

        Assert.IsType<FileStreamResult>(result);
        Assert.Equal("public, max-age=300", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task DetectionFrame_UsesAnExistingExactFrame_WithoutMakingItAgain()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var video = new Video { Title = "Video with a face" };
        db.Videos.Add(video);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var frames = new TemporaryFrameDirectory();
        var thumbnails = new FrameThumbnailService(frames.Path) { FrameColorToGenerate = new Rgba32(255, 0, 0) };
        await WriteFrameAsync(thumbnails.GetTimestampedThumbnailPath(video.Id, 12.25), new Rgba32(0, 0, 255));
        var detection = new Detection { HostType = DetectionHostType.Video, HostId = video.Id, ObservedAtSec = 12.25 };

        await using var frame = await DetectionFrame.OpenAsync(db, thumbnails, detection, TestContext.Current.CancellationToken);

        Assert.NotNull(frame);
        Assert.Empty(thumbnails.Generated);
    }

    [Fact]
    public async Task DetectionFrame_OnAClip_MakesTheFrameFromTheSourceVideo()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var source = new Video { Title = "Source" };
        db.Videos.Add(source);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var clip = new Video { Title = "Clip", ParentVideoId = source.Id, ClipStartSec = 100 };
        db.Videos.Add(clip);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var frames = new TemporaryFrameDirectory();
        var thumbnails = new FrameThumbnailService(frames.Path) { FrameColorToGenerate = new Rgba32(255, 0, 0) };
        // Detections on a clip use the source's timeline.
        var detection = new Detection { HostType = DetectionHostType.Video, HostId = clip.Id, ObservedAtSec = 130 };

        await using var frame = await DetectionFrame.OpenAsync(db, thumbnails, detection, TestContext.Current.CancellationToken);

        Assert.NotNull(frame);
        Assert.Equal(new[] { (source.Id, 130.0) }, thumbnails.Generated);
    }

    [Fact]
    public async Task GetDetectionCrop_WhenMakingTheFrameFails_FallsBackInsteadOfFailing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var video = new Video { Title = "Video in a read-only library" };
        db.Videos.Add(video);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var detection = new Detection { HostType = DetectionHostType.Video, HostId = video.Id, ObservedAtSec = 12, X = 0.25f, Y = 0.25f, W = 0.5f, H = 0.5f, SourceKey = "test" };
        db.Detections.Add(detection);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var frames = new TemporaryFrameDirectory();
        var thumbnails = new FrameThumbnailService(frames.Path) { GenerateFailure = new UnauthorizedAccessException("generated folder is read-only") };
        var standIn = Path.Combine(frames.Path, "stand-in.jpg");
        await WriteFrameAsync(standIn, new Rgba32(0, 0, 255));
        var controller = new StreamController(new FrameStreamService(thumbnails) { StandInPath = standIn }, thumbnails, null!, db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = await controller.GetDetectionCrop(detection.Id, null, null, TestContext.Current.CancellationToken);

        Assert.IsType<FileStreamResult>(result);
        Assert.Equal("public, max-age=300", controller.Response.Headers.CacheControl.ToString());
    }

    private static async Task WriteFrameAsync(string path, Rgba32 color)
        => await TestImages.WriteAsync(path, TestImages.Jpeg(160, 90, color));

    private sealed class TemporaryFrameDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cove-frames-{Guid.NewGuid():N}");
        public TemporaryFrameDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    /// <summary>Frames named like the real service; generating one paints it a solid colour, or makes nothing.</summary>
    private sealed class FrameThumbnailService(string directory) : IThumbnailService
    {
        public Rgba32? FrameColorToGenerate { get; init; }
        public Exception? GenerateFailure { get; init; }
        public List<(int VideoId, double Seconds)> Generated { get; } = [];
        public Task<string?> GetVideoThumbnailPathAsync(int videoId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetImageFilePathAsync(int imageId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageStreamAsync(int imageId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageThumbnailStreamAsync(int imageId, int maxDimension = 640, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetBlobImageThumbnailStreamAsync(string blobId, int maxDimension = 640, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteVideoGeneratedFilesAsync(int videoId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteImageGeneratedFilesAsync(int imageId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteBlobGeneratedFilesAsync(string blobId, CancellationToken ct = default) => Task.CompletedTask;
        public async Task GenerateVideoThumbnailAsync(int videoId, double? atSeconds = null, CancellationToken ct = default)
        {
            Generated.Add((videoId, atSeconds!.Value));
            if (GenerateFailure != null)
                throw GenerateFailure;
            if (FrameColorToGenerate is { } color)
                await WriteFrameAsync(GetTimestampedThumbnailPath(videoId, atSeconds.Value), color);
        }
        public Task<bool> GenerateImageThumbnailAsync(int imageId, int maxDimension = 640, bool overwrite = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task GenerateVideoPreviewAsync(int videoId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task GenerateVideoSpriteAsync(int videoId, CancellationToken ct = default) => throw new NotImplementedException();
        public string GetThumbnailPathForVideo(int videoId) => throw new NotImplementedException();
        public string GetTimestampedThumbnailPath(int videoId, double seconds) => System.IO.Path.Combine(directory, $"{videoId}_t{(long)Math.Round(seconds * 1000)}ms.jpg");
        public string GetPreviewPath(int videoId) => throw new NotImplementedException();
        public string GetSpritePath(int videoId) => System.IO.Path.Combine(directory, $"{videoId}_sprite.jpg");
        public string GetSpriteVttPath(int videoId) => System.IO.Path.Combine(directory, $"{videoId}_thumbs.vtt");
        public string StartGenerateAllThumbnails() => throw new NotImplementedException();
    }

    /// <summary>Screenshots the way the real service reads them: the exact frame, then a stand-in.</summary>
    private sealed class FrameStreamService(FrameThumbnailService thumbnails) : IStreamService
    {
        public string? StandInPath { get; init; }
        public Task<(Stream stream, string contentType, long? fileSize)?> GetVideoStream(int videoId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(Stream stream, string contentType, bool useLongCache)?> GetVideoScreenshot(int videoId, double? seconds, CancellationToken ct = default)
        {
            var exact = thumbnails.GetTimestampedThumbnailPath(videoId, seconds!.Value);
            var path = new[] { exact, StandInPath }.FirstOrDefault(candidate => candidate != null && File.Exists(candidate));
            return Task.FromResult<(Stream stream, string contentType, bool useLongCache)?>(path == null ? null : (File.OpenRead(path), "image/jpeg", true));
        }
    }

    private static CoveContext CreateContext(SqliteConnection connection)
        => new(new DbContextOptionsBuilder<CoveContext>().UseSqlite(connection).Options);

    private sealed class FakeTranscodeService : ITranscodeService
    {
        public Task<Stream?> TranscodeToMp4Async(string inputPath, string? resolution, double startSeconds = 0, CancellationToken ct = default)
            => Task.FromResult<Stream?>(null);

        public Task<string?> GenerateHlsManifestAsync(int videoId, string inputPath, string? resolution, double startSeconds, CancellationToken ct = default)
            => Task.FromResult<string?>("#EXTM3U\n#EXTINF:4,\noriginal_s0_0000.ts\n#EXTINF:4,\noriginal_s0_0001.ts\n");

        public Task<Stream?> GetHlsSegmentAsync(int videoId, string segment, CancellationToken ct = default)
            => Task.FromResult<Stream?>(null);

        public string[] GetAvailableResolutions(int sourceWidth, int sourceHeight) => ["720p"];
    }

    private static void SetQuery(StreamController controller, string queryString)
    {
        controller.ControllerContext.HttpContext.Request.QueryString = new QueryString(queryString);
    }

    private sealed class FakeThumbnailService(string previewPath) : IThumbnailService
    {
        /// <summary>Served for both the image and its thumbnail, as happens for an SVG source.</summary>
        public string? ImageContentType { get; init; }
        public byte[] ImageBytes { get; init; } = [];

        private Task<(Stream stream, string contentType, bool supportsRangeRequests)?> ServeImage()
            => ImageContentType is null
                ? throw new NotImplementedException()
                : Task.FromResult<(Stream stream, string contentType, bool supportsRangeRequests)?>((new MemoryStream(ImageBytes), ImageContentType, true));

        public Task<string?> GetVideoThumbnailPathAsync(int videoId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> GetImageFilePathAsync(int imageId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageStreamAsync(int imageId, CancellationToken ct = default) => ServeImage();
        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageThumbnailStreamAsync(int imageId, int maxDimension = 640, CancellationToken ct = default) => ServeImage();
        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetBlobImageThumbnailStreamAsync(string blobId, int maxDimension = 640, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteVideoGeneratedFilesAsync(int videoId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteImageGeneratedFilesAsync(int imageId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteBlobGeneratedFilesAsync(string blobId, CancellationToken ct = default) => Task.CompletedTask;
        public Task GenerateVideoThumbnailAsync(int videoId, double? atSeconds = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> GenerateImageThumbnailAsync(int imageId, int maxDimension = 640, bool overwrite = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task GenerateVideoPreviewAsync(int videoId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task GenerateVideoSpriteAsync(int videoId, CancellationToken ct = default) => throw new NotImplementedException();
        public string GetThumbnailPathForVideo(int videoId) => throw new NotImplementedException();
        public string GetTimestampedThumbnailPath(int videoId, double seconds) => throw new NotImplementedException();
        public string GetPreviewPath(int videoId) => previewPath;
        public string GetSpritePath(int videoId) => throw new NotImplementedException();
        public string GetSpriteVttPath(int videoId) => throw new NotImplementedException();
        public string StartGenerateAllThumbnails() => throw new NotImplementedException();
    }
}
