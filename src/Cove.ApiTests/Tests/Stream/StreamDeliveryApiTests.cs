using System.Net;
using System.Net.Http.Headers;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using SixLabors.ImageSharp;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Stream;

[Collection(ApiTestLane1Collection.Name)]
public sealed class StreamDeliveryApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/stream/video/{videoid:int}")]
    [CoversEndpoint("GET", "/api/stream/video/{videoid:int}/screenshot")]
    [CoversEndpoint("GET", "/api/stream/video/{videoid:int}/segment-preview")]
    [CoversEndpoint("GET", "/api/stream/video/{videoid:int}/preview")]
    [CoversEndpoint("HEAD", "/api/stream/video/{videoid:int}/preview")]
    [CoversEndpoint("GET", "/api/stream/video/{videoid:int}/sprite")]
    [CoversEndpoint("GET", "/api/stream/video/{videoid:int}/vtt/thumbs")]
    [CoversEndpoint("GET", "/api/stream/video/{videoid:int}/hls/master.m3u8")]
    public async Task GivenVideoSourcesAndGeneratedAssets_WhenStreamRoutesAreRead_ThenBytesRangesCachesAndPlaylistAreExact()
    {
        var owner = AsUser();
        var video = await owner.CreateVideoAsync($"Stream delivery {Guid.NewGuid():N}");
        var sourceBytes = "api-test-video-source"u8.ToArray();
        var previewBytes = "api-test-preview"u8.ToArray();
        var screenshotBytes = await CreateImageAsync("jpeg", 12, 8);
        var segmentPreviewBytes = await CreateImageAsync("webp", 10, 6);
        var spriteBytes = await CreateImageAsync("jpeg", 16, 9);
        var customScreenshot = ApiTestImages.BluePixelPng();
        var vtt = $"WEBVTT\n\n00:00:00.000 --> 00:00:05.000\n{video.Id}_sprite.jpg#xywh=0,0,16,9\n";
        var fileSystem = AsTestFileSystem();
        var sourcePath = fileSystem.CreateLibraryFile($"stream-{video.Id}.mp4", sourceBytes);
        fileSystem.CreateVideoPreview(video.Id, previewBytes);
        fileSystem.CreateVideoScreenshot(video.Id, 7, screenshotBytes);
        fileSystem.CreateVideoSegmentPreview(video.Id, 7, segmentPreviewBytes);
        fileSystem.CreateVideoSprite(video.Id, spriteBytes);
        fileSystem.CreateVideoSpriteVtt(video.Id, vtt);
        await AsDbUser().AttachStreamVideoFileAsync(video.Id, sourcePath, width: 1280, height: 720, duration: 12);
        await owner.UploadVideoImageAsync(video, customScreenshot);

        using var client = owner.CreateHttpClient();
        using var videoRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/stream/video/{video.Id}");
        videoRequest.Headers.Range = new RangeHeaderValue(1, 4);
        using var videoResponse = await client.SendAsync(videoRequest);
        (await videoResponse.Content.ReadAsByteArrayAsync()).Should().Equal(sourceBytes[1..5]);
        videoResponse.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        videoResponse.Content.Headers.ContentType?.MediaType.Should().Be("video/mp4");
        videoResponse.Content.Headers.ContentRange?.ToString().Should().Be($"bytes 1-4/{sourceBytes.Length}");
        videoResponse.Headers.AcceptRanges.Should().Equal("bytes");

        using var customResponse = await client.GetAsync($"/api/stream/video/{video.Id}/screenshot");
        customResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await customResponse.Content.ReadAsByteArrayAsync()).Should().Equal(customScreenshot);
        customResponse.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        customResponse.Headers.CacheControl.Should().NotBeNull();
        customResponse.Headers.CacheControl!.NoStore.Should().BeTrue();
        customResponse.Headers.CacheControl.NoCache.Should().BeTrue();
        customResponse.Headers.CacheControl.MustRevalidate.Should().BeTrue();
        customResponse.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.Zero);

        using var generatedResponse = await client.GetAsync($"/api/stream/video/{video.Id}/screenshot?seconds=7");
        generatedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await generatedResponse.Content.ReadAsByteArrayAsync()).Should().Equal(screenshotBytes);
        generatedResponse.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
        generatedResponse.Headers.CacheControl?.ToString().Should().Be("public, max-age=86400");

        using var segmentResponse = await client.GetAsync($"/api/stream/video/{video.Id}/segment-preview?seconds=7");
        segmentResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await segmentResponse.Content.ReadAsByteArrayAsync()).Should().Equal(segmentPreviewBytes);
        segmentResponse.Content.Headers.ContentType?.MediaType.Should().Be("image/webp");
        segmentResponse.Headers.CacheControl?.ToString().Should().Be("public, max-age=86400");

        using var previewRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/stream/video/{video.Id}/preview");
        previewRequest.Headers.Range = new RangeHeaderValue(2, 5);
        using var previewResponse = await client.SendAsync(previewRequest);
        previewResponse.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await previewResponse.Content.ReadAsByteArrayAsync()).Should().Equal(previewBytes[2..6]);
        previewResponse.Content.Headers.ContentType?.MediaType.Should().Be("video/mp4");
        previewResponse.Content.Headers.ContentRange?.ToString().Should().Be($"bytes 2-5/{previewBytes.Length}");
        previewResponse.Headers.AcceptRanges.Should().Equal("bytes");
        previewResponse.Headers.CacheControl?.ToString().Should().Be("public, max-age=86400");

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, $"/api/stream/video/{video.Id}/preview");
        using var headResponse = await client.SendAsync(headRequest);
        headResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        headResponse.Content.Headers.ContentType?.MediaType.Should().Be("video/mp4");
        headResponse.Content.Headers.ContentLength.Should().Be(previewBytes.Length);
        (await headResponse.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        headResponse.Headers.AcceptRanges.Should().Equal("bytes");
        headResponse.Headers.CacheControl?.ToString().Should().Be("public, max-age=86400");

        using var spriteResponse = await client.GetAsync($"/api/stream/video/{video.Id}/sprite");
        spriteResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await spriteResponse.Content.ReadAsByteArrayAsync()).Should().Equal(spriteBytes);
        spriteResponse.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
        spriteResponse.Headers.CacheControl?.ToString().Should().Be("public, max-age=86400");

        using var vttResponse = await client.GetAsync($"/api/stream/video/{video.Id}/vtt/thumbs");
        vttResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await vttResponse.Content.ReadAsStringAsync()).Should().Be(vtt);
        vttResponse.Content.Headers.ContentType?.MediaType.Should().Be("text/vtt");
        vttResponse.Headers.CacheControl?.ToString().Should().Be("public, max-age=86400");

        var propagatedQuery = $"access_token={Uri.EscapeDataString(owner.AccessToken)}";
        using var hlsResponse = await client.GetAsync(
            $"/api/stream/video/{video.Id}/hls/master.m3u8?{propagatedQuery}&ignored=secret");
        var playlist = await hlsResponse.Content.ReadAsStringAsync();
        hlsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        hlsResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.apple.mpegurl");
        hlsResponse.Headers.CacheControl?.ToString().Should().Be("no-cache");
        playlist.Should().StartWith("#EXTM3U\n");
        playlist.Should().Contain($"/api/stream/video/{video.Id}/hls/720p.m3u8?{propagatedQuery}");
        playlist.Should().Contain("#EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720,NAME=\"720p\"");
        playlist.Should().NotContain("ignored=");
    }

    [Fact]
    [CoversEndpoint("GET", "/api/stream/image/{imageid:int}")]
    [CoversEndpoint("GET", "/api/stream/detection/{detectionid:int}/crop")]
    public async Task GivenImageSourceAndDetection_WhenStreamRoutesAreRead_ThenRangeAndDecodableCropAreReturned()
    {
        var owner = AsUser();
        var image = await owner.CreateImageAsync($"Stream image {Guid.NewGuid():N}");
        var imageBytes = await CreateImageAsync("png", 160, 120);
        var imagePath = AsTestFileSystem().CreateLibraryFile($"stream-{image.Id}.png", imageBytes);
        await AsDbUser().AttachStreamImageFileAsync(image.Id, imagePath, width: 160, height: 120);
        var detection = await owner.CreateImageDetectionAsync(image, new DetectionCreateDto(
            ObservedAtSec: null,
            FrameWidth: 160,
            FrameHeight: 120,
            Class: "stream-crop",
            Score: 0.95f,
            X: 0.25f,
            Y: 0.2f,
            W: 0.5f,
            H: 0.5f,
            Extra: null,
            RefKind: null,
            RefId: null,
            GroupKey: null,
            SourceKey: "api-test",
            SourceRunId: null));

        using var client = owner.CreateHttpClient();
        using var imageRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/stream/image/{image.Id}");
        imageRequest.Headers.Range = new RangeHeaderValue(0, 7);
        using var imageResponse = await client.SendAsync(imageRequest);
        imageResponse.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await imageResponse.Content.ReadAsByteArrayAsync()).Should().Equal(imageBytes[..8]);
        imageResponse.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        imageResponse.Content.Headers.ContentRange?.ToString().Should().Be($"bytes 0-7/{imageBytes.Length}");
        imageResponse.Headers.AcceptRanges.Should().Equal("bytes");
        imageResponse.Headers.CacheControl?.ToString().Should().Be("public, max-age=86400");

        using var cropResponse = await client.GetAsync($"/api/stream/detection/{detection.Id}/crop?max=64");
        var cropBytes = await cropResponse.Content.ReadAsByteArrayAsync();
        cropResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        cropResponse.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
        cropResponse.Headers.CacheControl?.ToString().Should().Be("public, max-age=86400");
        cropBytes.Should().NotBeEmpty();
        using var crop = Image.Load(cropBytes);
        crop.Width.Should().Be(64);
        crop.Height.Should().Be(64);

        using var missingResponse = await client.GetAsync("/api/stream/image/2147483647");
        missingResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var noPermissionUsername = $"stream-no-permission-{Guid.NewGuid():N}";
        const string noPermissionPassword = "Stream password 123!";
        await owner.CreateUserAsync(new CreateUserRequest(noPermissionUsername, noPermissionPassword, Roles: []));
        using var noPermissionSession = await owner.CreateAuthSessionAsync(noPermissionUsername, noPermissionPassword);
        using var noPermissionClient = noPermissionSession.Client.CreateHttpClient();
        using var forbiddenResponse = await noPermissionClient.GetAsync($"/api/stream/image/{image.Id}");
        forbiddenResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static async Task<byte[]> CreateImageAsync(string format, int width, int height)
    {
        using var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height, new(40, 90, 180, 255));
        await using var output = new MemoryStream();
        switch (format)
        {
            case "jpeg":
                await image.SaveAsJpegAsync(output);
                break;
            case "png":
                await image.SaveAsPngAsync(output);
                break;
            case "webp":
                await image.SaveAsWebpAsync(output);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
        return output.ToArray();
    }
}
