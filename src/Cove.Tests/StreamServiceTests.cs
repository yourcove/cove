using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Cove.Api.Services;
using Cove.Core.Interfaces;

namespace Cove.Tests;

public class StreamServiceTests
{
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
                await sprite.SaveAsJpegAsync(spritePath);
            }

            await File.WriteAllTextAsync(vttPath, """
                WEBVTT

                00:00:00.000 --> 00:00:05.000
                stashhash_sprite.jpg#xywh=0,0,160,90

                00:00:05.000 --> 00:00:10.000
                stashhash_sprite.jpg#xywh=160,0,160,90
                """);

            var service = new StreamService(null!, new FakeThumbnailService(timestampPath, animatedPath, spritePath, vttPath), null!);

            var screenshot = await service.GetVideoScreenshot(42, 6, CancellationToken.None);
            var segmentPreview = await service.GetSegmentAnimatedPreview(42, 6, CancellationToken.None);

            Assert.NotNull(screenshot);
            Assert.NotNull(segmentPreview);
            Assert.Equal("image/jpeg", screenshot.Value.contentType);
            Assert.Equal("image/jpeg", segmentPreview.Value.contentType);

            await using var stream = screenshot.Value.stream;
            using var image = await Image.LoadAsync<Rgba32>(stream);

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
