using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Cove.Api.Services;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Tests;

public class StreamServiceTests
{
    [Fact]
    public async Task GetVideoStream_OpensTheCanonicalStoredPathWithoutFolderNavigation()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-stream-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var videoPath = Path.Combine(tempRoot, "source.mp4");
            var bytes = new byte[] { 1, 2, 3, 4 };
            await File.WriteAllBytesAsync(videoPath, bytes, TestContext.Current.CancellationToken);

            var dbOptions = new DbContextOptionsBuilder<CoveContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var services = new ServiceCollection();
            services.AddSingleton(dbOptions);
            services.AddScoped<CoveContext>(_ => new CoveContext(dbOptions));

            await using var provider = services.BuildServiceProvider();
            int videoId;
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                var video = new Video { Title = "stream source" };
                video.Files.Add(new VideoFile
                {
                    Basename = Path.GetFileName(videoPath),
                    ParentFolder = new Folder { Path = tempRoot },
                    Format = "mp4",
                });
                db.Videos.Add(video);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                videoId = video.Id;
            }

            var service = new StreamService(provider.GetRequiredService<IServiceScopeFactory>(), null!, null!);

            var result = await service.GetVideoStream(videoId, TestContext.Current.CancellationToken);

            Assert.NotNull(result);
            await using var stream = result.Value.stream;
            using var content = new MemoryStream();
            await stream.CopyToAsync(content, TestContext.Current.CancellationToken);
            Assert.Equal(bytes, content.ToArray());
            Assert.Equal("video/mp4", result.Value.contentType);
            Assert.Equal(bytes.Length, result.Value.fileSize);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GetVideoScreenshot_UsesSpriteVttFrame_WhenTimestampThumbnailIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-stream-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var spritePath = Path.Combine(tempRoot, "42_sprite.jpg");
            var vttPath = Path.Combine(tempRoot, "42_thumbs.vtt");
            var timestampPath = Path.Combine(tempRoot, "42_t6.jpg");
            var animatedPath = Path.Combine(tempRoot, "42_t6.webp");

            using (var sprite = new Image<Rgba32>(320, 90))
            {
                var red = new Rgba32(255, 0, 0);
                var green = new Rgba32(0, 128, 0);
                sprite.ProcessPixelRows(accessor =>
                {
                    for (var y = 0; y < accessor.Height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (var x = 0; x < row.Length; x++)
                        {
                            row[x] = x < 160 ? red : green;
                        }
                    }
                });
                await sprite.SaveAsJpegAsync(spritePath, cancellationToken: TestContext.Current.CancellationToken);
            }

            await File.WriteAllTextAsync(vttPath, """
                WEBVTT

                00:00:00.000 --> 00:00:05.000
                stashhash_sprite.jpg#xywh=0,0,160,90

                00:00:05.000 --> 00:00:10.000
                stashhash_sprite.jpg#xywh=160,0,160,90
                """, TestContext.Current.CancellationToken);

            var service = new StreamService(null!, new FakeThumbnailService(timestampPath, animatedPath, spritePath, vttPath), null!);

            var screenshot = await service.GetVideoScreenshot(42, 6, CancellationToken.None);
            var segmentPreview = await service.GetSegmentAnimatedPreview(42, 6, CancellationToken.None);

            Assert.NotNull(screenshot);
            Assert.NotNull(segmentPreview);
            Assert.Equal("image/jpeg", screenshot.Value.contentType);
            Assert.Equal("image/jpeg", segmentPreview.Value.contentType);

            await using var stream = screenshot.Value.stream;
            using var image = await SixLabors.ImageSharp.Image.LoadAsync<Rgba32>(stream, TestContext.Current.CancellationToken);

            Assert.Equal(160, image.Width);
            Assert.Equal(90, image.Height);
            Assert.True(image[0, 0].G > image[0, 0].R);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class FakeThumbnailService(string timestampPath, string animatedPath, string spritePath, string vttPath) : IThumbnailService
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
        public string GetTimestampedThumbnailPath(int videoId, double seconds) => timestampPath;
        public string GetSegmentAnimatedPreviewPath(int videoId, double seconds) => animatedPath;
        public string GetPreviewPath(int videoId) => throw new NotImplementedException();
        public string GetSpritePath(int videoId) => spritePath;
        public string GetSpriteVttPath(int videoId) => vttPath;
        public string StartGenerateAllThumbnails() => throw new NotImplementedException();
    }
}
