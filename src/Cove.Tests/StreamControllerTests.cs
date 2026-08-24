using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.Entities;
using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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
        context.VideoFiles.Add(new VideoFile
        {
            VideoId = video.Id,
            ParentFolderId = folder.Id,
            Basename = "video.mp4",
            Width = 1920,
            Height = 1080,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = CreateController(context, new FakeTranscodeService());
        SetQuery(controller, "?access_token=access token&share_token=share/token&ignored=true");

        var result = await controller.GetHlsMasterPlaylist(video.Id, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains($"/api/stream/video/{video.Id}/hls/720p.m3u8?access_token=access%20token&share_token=share%2Ftoken", content.Content);
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
            context.VideoFiles.Add(new VideoFile
            {
                VideoId = video.Id,
                ParentFolderId = folder.Id,
                Basename = Path.GetFileName(videoPath),
                Width = 1920,
                Height = 1080,
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var controller = CreateController(context, new FakeTranscodeService());
            SetQuery(controller, "?access_token=access token&share_token=share/token&share_password=p@ss&ignored=true");

            var result = await controller.GetHlsPlaylist(video.Id, "original", CancellationToken.None);

            var content = Assert.IsType<ContentResult>(result);
            Assert.Contains($"/api/stream/video/{video.Id}/hls/segment/original_000.ts?access_token=access%20token&share_token=share%2Ftoken&share_password=p%40ss", content.Content);
            Assert.Contains($"/api/stream/video/{video.Id}/hls/segment/original_001.ts?access_token=access%20token&share_token=share%2Ftoken&share_password=p%40ss", content.Content);
            Assert.Contains("#EXTINF:4,", content.Content);
            Assert.DoesNotContain("ignored=", content.Content);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
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

    private static CoveContext CreateContext(SqliteConnection connection)
        => new(new DbContextOptionsBuilder<CoveContext>().UseSqlite(connection).Options);

    private sealed class FakeTranscodeService : ITranscodeService
    {
        public Task<Stream?> TranscodeToMp4Async(string inputPath, string? resolution, double startSeconds = 0, CancellationToken ct = default)
            => Task.FromResult<Stream?>(null);

        public Task<string?> GenerateHlsManifestAsync(int videoId, string inputPath, string? resolution, CancellationToken ct = default)
            => Task.FromResult<string?>("#EXTM3U\n#EXTINF:4,\noriginal_000.ts\n#EXTINF:4,\noriginal_001.ts\n");

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
        public Task<string?> GetVideoThumbnailPathAsync(int videoId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> GetImageFilePathAsync(int imageId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageStreamAsync(int imageId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageThumbnailStreamAsync(int imageId, int maxDimension = 640, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetBlobImageThumbnailStreamAsync(string blobId, int maxDimension = 640, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteVideoGeneratedFilesAsync(int videoId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteImageGeneratedFilesAsync(int imageId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteBlobGeneratedFilesAsync(string blobId, CancellationToken ct = default) => Task.CompletedTask;
        public Task GenerateVideoThumbnailAsync(int videoId, double? atSeconds = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> GenerateImageThumbnailAsync(int imageId, int maxDimension = 640, bool overwrite = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task GenerateVideoPreviewAsync(int videoId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task GenerateSegmentAnimatedPreviewAsync(int videoId, double startSec, double? endSec = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task GenerateVideoSpriteAsync(int videoId, CancellationToken ct = default) => throw new NotImplementedException();
        public string GetThumbnailPathForVideo(int videoId) => throw new NotImplementedException();
        public string GetTimestampedThumbnailPath(int videoId, double seconds) => throw new NotImplementedException();
        public string GetSegmentAnimatedPreviewPath(int videoId, double seconds) => throw new NotImplementedException();
        public string GetPreviewPath(int videoId) => previewPath;
        public string GetSpritePath(int videoId) => throw new NotImplementedException();
        public string GetSpriteVttPath(int videoId) => throw new NotImplementedException();
        public string StartGenerateAllThumbnails() => throw new NotImplementedException();
    }
}
