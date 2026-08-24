using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities.Auth;
using SixLabors.ImageSharp;
using EntityKinds = Cove.Core.Entities.EntityKinds;

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
        var video = await AsUser().CreateVideoAsync($"Stream delivery {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
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
        await AsDbUser().AttachStreamVideoFileAsync(video.Id, sourcePath, width: 1280, height: 720, duration: 12, cancellationToken: TestContext.Current.CancellationToken);
        await AsUser().UploadVideoImageAsync(video, customScreenshot, cancellationToken: TestContext.Current.CancellationToken);

        using var client = AsUser().CreateHttpClient();
        using var videoRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/stream/video/{video.Id}");
        videoRequest.Headers.Range = new RangeHeaderValue(1, 4);
        using var videoResponse = await client.SendAsync(videoRequest, TestContext.Current.CancellationToken);
        (await videoResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Equal(sourceBytes[1..5]);
        videoResponse.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        videoResponse.Content.Headers.ContentType?.MediaType.Should().Be("video/mp4");
        videoResponse.Content.Headers.ContentRange?.ToString().Should().Be($"bytes 1-4/{sourceBytes.Length}");
        videoResponse.Headers.AcceptRanges.Should().Equal("bytes");

        using var customResponse = await client.GetAsync($"/api/stream/video/{video.Id}/screenshot", TestContext.Current.CancellationToken);
        customResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await customResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Equal(customScreenshot);
        customResponse.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        customResponse.Headers.CacheControl.Should().NotBeNull();
        customResponse.Headers.CacheControl!.NoStore.Should().BeTrue();
        customResponse.Headers.CacheControl.NoCache.Should().BeTrue();
        customResponse.Headers.CacheControl.MustRevalidate.Should().BeTrue();
        customResponse.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.Zero);

        using var generatedResponse = await client.GetAsync($"/api/stream/video/{video.Id}/screenshot?seconds=7", TestContext.Current.CancellationToken);
        generatedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await generatedResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Equal(screenshotBytes);
        generatedResponse.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
        generatedResponse.Headers.CacheControl?.ToString().Should().Be("public, max-age=86400");

        using var segmentResponse = await client.GetAsync($"/api/stream/video/{video.Id}/segment-preview?seconds=7", TestContext.Current.CancellationToken);
        segmentResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await segmentResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Equal(segmentPreviewBytes);
        segmentResponse.Content.Headers.ContentType?.MediaType.Should().Be("image/webp");
        segmentResponse.Headers.CacheControl?.ToString().Should().Be("public, max-age=86400");

        using var previewRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/stream/video/{video.Id}/preview");
        previewRequest.Headers.Range = new RangeHeaderValue(2, 5);
        using var previewResponse = await client.SendAsync(previewRequest, TestContext.Current.CancellationToken);
        previewResponse.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await previewResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Equal(previewBytes[2..6]);
        previewResponse.Content.Headers.ContentType?.MediaType.Should().Be("video/mp4");
        previewResponse.Content.Headers.ContentRange?.ToString().Should().Be($"bytes 2-5/{previewBytes.Length}");
        previewResponse.Headers.AcceptRanges.Should().Equal("bytes");
        previewResponse.Headers.CacheControl?.ToString().Should().Be("public, max-age=86400");

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, $"/api/stream/video/{video.Id}/preview");
        using var headResponse = await client.SendAsync(headRequest, TestContext.Current.CancellationToken);
        headResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        headResponse.Content.Headers.ContentType?.MediaType.Should().Be("video/mp4");
        headResponse.Content.Headers.ContentLength.Should().Be(previewBytes.Length);
        (await headResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
        headResponse.Headers.AcceptRanges.Should().Equal("bytes");
        headResponse.Headers.CacheControl?.ToString().Should().Be("public, max-age=86400");

        using var spriteResponse = await client.GetAsync($"/api/stream/video/{video.Id}/sprite", TestContext.Current.CancellationToken);
        spriteResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await spriteResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Equal(spriteBytes);
        spriteResponse.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
        spriteResponse.Headers.CacheControl?.ToString().Should().Be("public, max-age=86400");

        using var vttResponse = await client.GetAsync($"/api/stream/video/{video.Id}/vtt/thumbs", TestContext.Current.CancellationToken);
        vttResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await vttResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be(vtt);
        vttResponse.Content.Headers.ContentType?.MediaType.Should().Be("text/vtt");
        vttResponse.Headers.CacheControl?.ToString().Should().Be("public, max-age=86400");

        var propagatedQuery = $"access_token={Uri.EscapeDataString(AsUser().AccessToken)}";
        using var hlsResponse = await client.GetAsync($"/api/stream/video/{video.Id}/hls/master.m3u8?{propagatedQuery}&ignored=secret", TestContext.Current.CancellationToken);
        var playlist = await hlsResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        hlsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        hlsResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.apple.mpegurl");
        hlsResponse.Headers.CacheControl?.ToString().Should().Be("no-cache");
        playlist.Should().StartWith("#EXTM3U\n");
        playlist.Should().Contain($"/api/stream/video/{video.Id}/hls/720p.m3u8?{propagatedQuery}");
        playlist.Should().Contain("#EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720,NAME=\"720p\"");
        playlist.Should().NotContain("ignored=");
    }

    [Fact]
    [CoversEndpoint("GET", "/api/stream/video/{videoid:int}/caption/{captionid:int}")]
    [CoversEndpoint("GET", "/api/stream/video/{videoid:int}/captions")]
    [CoversEndpoint("GET", "/api/stream/video/{videoid:int}/hls/segment/{segment}")]
    [CoversEndpoint("GET", "/api/stream/video/{videoid:int}/resolutions")]
    public async Task GivenVideoCaptionSidecarsAndCachedSegment_WhenEncoderFreeStreamRoutesAreRead_ThenVisibilityContentAndProfilesAreExact()
    {
        var video = await AsUser().CreateVideoAsync($"Stream caption video {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var otherVideo = await AsUser().CreateVideoAsync($"Other stream caption video {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var fileSystem = AsTestFileSystem();
        var sourcePath = fileSystem.CreateLibraryFile($"stream-caption-{video.Id}.mp4", "stream-caption-source"u8.ToArray());
        var vttFilename = $"stream-caption-{video.Id}.en.vtt";
        var srtFilename = $"stream-caption-{video.Id}.es.srt";
        const string vtt = "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nEnglish fixture caption\n";
        const string srt = "1\n00:00:00,000 --> 00:00:01,000\nSpanish fixture caption\n";
        fileSystem.CreateLibraryFile(vttFilename, Encoding.UTF8.GetBytes(vtt));
        var srtPath = fileSystem.CreateLibraryFile(srtFilename, Encoding.UTF8.GetBytes(srt));
        await AsDbUser().AttachStreamVideoFileAsync(video.Id, sourcePath, width: 1280, height: 720, duration: 12, cancellationToken: TestContext.Current.CancellationToken);
        var vttCaptionId = await AsDbUser().AttachStreamVideoCaptionAsync(video.Id, vttFilename, "en", "vtt", TestContext.Current.CancellationToken);
        var srtCaptionId = await AsDbUser().AttachStreamVideoCaptionAsync(video.Id, srtFilename, "es", "srt", TestContext.Current.CancellationToken);
        const string segment = "720p_0000.ts";
        var segmentBytes = "api-test-hls-segment"u8.ToArray();
        fileSystem.CreateGeneratedFile(
            Path.Combine("transcodes", "hls", video.Id.ToString(CultureInfo.InvariantCulture), segment),
            segmentBytes);

        var memberRole = (await AsUser().GetRolesAsync(TestContext.Current.CancellationToken))
            .Should().ContainSingle(role => role.Name == BuiltinRoles.Member).Which;
        var readDeny = await AsUser().CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            memberRole.Id,
            EntityKinds.Video,
            video.Id.ToString(CultureInfo.InvariantCulture),
            "deny",
            "read"), TestContext.Current.CancellationToken);
        using (var memberSession = await AsUser().CreateAuthSessionAsync(ApiTestUsers.Eva, ApiTestUsers.Password, TestContext.Current.CancellationToken))
        using (var memberClient = memberSession.Client.CreateHttpClient())
        {
            await AssertHiddenAsync(memberClient, $"/api/stream/video/{video.Id}/captions", "application/json");
            await AssertHiddenAsync(memberClient, $"/api/stream/video/{video.Id}/caption/{vttCaptionId}", "text/vtt");
            await AssertHiddenAsync(memberClient, $"/api/stream/video/{video.Id}/hls/segment/{segment}", "video/mp2t");
            await AssertHiddenAsync(memberClient, $"/api/stream/video/{video.Id}/resolutions", "application/json");
        }
        await AsUser().DeleteEntityOverrideAsync(readDeny.Id, TestContext.Current.CancellationToken);

        using var client = AsUser().CreateHttpClient();
        using var captionsResponse = await client.GetAsync($"/api/stream/video/{video.Id}/captions", TestContext.Current.CancellationToken);
        captionsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        captionsResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        var captions = await captionsResponse.Content.ReadFromJsonAsync<List<StreamCaptionResponse>>(cancellationToken: TestContext.Current.CancellationToken);
        captions.Should().NotBeNull();
        captions.Should().BeEquivalentTo(
        [
            new StreamCaptionResponse(vttCaptionId, "en", "vtt", vttFilename),
            new StreamCaptionResponse(srtCaptionId, "es", "srt", srtFilename),
        ]);

        using var vttResponse = await client.GetAsync($"/api/stream/video/{video.Id}/caption/{vttCaptionId}", TestContext.Current.CancellationToken);
        vttResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await vttResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be(vtt);
        vttResponse.Content.Headers.ContentType?.MediaType.Should().Be("text/vtt");
        vttResponse.Headers.CacheControl?.ToString().Should().Be("public, max-age=3600");

        using (var srtResponse = await client.GetAsync($"/api/stream/video/{video.Id}/caption/{srtCaptionId}", TestContext.Current.CancellationToken))
        {
            srtResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            (await srtResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be(srt);
            srtResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/x-subrip");
            srtResponse.Headers.CacheControl?.ToString().Should().Be("public, max-age=3600");
        }
        fileSystem.DeleteLibraryFile(srtPath);
        using var missingSidecarResponse = await client.GetAsync($"/api/stream/video/{video.Id}/caption/{srtCaptionId}", TestContext.Current.CancellationToken);
        missingSidecarResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var segmentResponse = await client.GetAsync($"/api/stream/video/{video.Id}/hls/segment/{segment}", TestContext.Current.CancellationToken);
        segmentResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await segmentResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Equal(segmentBytes);
        segmentResponse.Content.Headers.ContentType?.MediaType.Should().Be("video/mp2t");
        segmentResponse.Headers.CacheControl?.ToString().Should().Be("public, max-age=86400");

        using var resolutionsResponse = await client.GetAsync($"/api/stream/video/{video.Id}/resolutions", TestContext.Current.CancellationToken);
        resolutionsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        resolutionsResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        (await resolutionsResponse.Content.ReadFromJsonAsync<string[]>(cancellationToken: TestContext.Current.CancellationToken)).Should().Equal("240p", "360p", "480p", "720p");

        using var wrongParentCaptionResponse = await client.GetAsync($"/api/stream/video/{otherVideo.Id}/caption/{vttCaptionId}", TestContext.Current.CancellationToken);
        wrongParentCaptionResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var missingCaptionResponse = await client.GetAsync($"/api/stream/video/{video.Id}/caption/{int.MaxValue}", TestContext.Current.CancellationToken);
        missingCaptionResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var otherCaptionsResponse = await client.GetAsync($"/api/stream/video/{otherVideo.Id}/captions", TestContext.Current.CancellationToken);
        otherCaptionsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await otherCaptionsResponse.Content.ReadFromJsonAsync<List<StreamCaptionResponse>>(cancellationToken: TestContext.Current.CancellationToken)).Should().BeEmpty();
        using var missingCaptionsResponse = await client.GetAsync($"/api/stream/video/{int.MaxValue}/captions", TestContext.Current.CancellationToken);
        missingCaptionsResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var missingSegmentResponse = await client.GetAsync($"/api/stream/video/{video.Id}/hls/segment/missing.ts", TestContext.Current.CancellationToken);
        missingSegmentResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var otherSegmentResponse = await client.GetAsync($"/api/stream/video/{otherVideo.Id}/hls/segment/{segment}", TestContext.Current.CancellationToken);
        otherSegmentResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var missingResolutionsResponse = await client.GetAsync($"/api/stream/video/{int.MaxValue}/resolutions", TestContext.Current.CancellationToken);
        missingResolutionsResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var filelessResolutionsResponse = await client.GetAsync($"/api/stream/video/{otherVideo.Id}/resolutions", TestContext.Current.CancellationToken);
        filelessResolutionsResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var noPermissionUsername = $"stream-video-no-permission-{Guid.NewGuid():N}";
        const string noPermissionPassword = "Stream video password 123!";
        await AsUser().CreateUserAsync(new CreateUserRequest(noPermissionUsername, noPermissionPassword, Roles: []), TestContext.Current.CancellationToken);
        using var noPermissionSession = await AsUser().CreateAuthSessionAsync(noPermissionUsername, noPermissionPassword, TestContext.Current.CancellationToken);
        using var noPermissionClient = noPermissionSession.Client.CreateHttpClient();
        using var forbiddenResponse = await noPermissionClient.GetAsync($"/api/stream/video/{video.Id}/captions", TestContext.Current.CancellationToken);
        forbiddenResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    [CoversEndpoint("GET", "/api/stream/image/{imageid:int}")]
    [CoversEndpoint("GET", "/api/stream/image/{imageid:int}/thumbnail")]
    [CoversEndpoint("GET", "/api/stream/detection/{detectionid:int}/crop")]
    public async Task GivenImageSourceAndDetection_WhenStreamRoutesAreRead_ThenRangeAndDecodableCropAreReturned()
    {
        var image = await AsUser().CreateImageAsync($"Stream image {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var imageBytes = await CreateImageAsync("png", 160, 120);
        var imagePath = AsTestFileSystem().CreateLibraryFile($"stream-{image.Id}.png", imageBytes);
        await AsDbUser().AttachStreamImageFileAsync(image.Id, imagePath, width: 160, height: 120, cancellationToken: TestContext.Current.CancellationToken);
        var detection = await AsUser().CreateImageDetectionAsync(image, new DetectionCreateDto(
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
            SourceRunId: null), TestContext.Current.CancellationToken);

        var memberRole = (await AsUser().GetRolesAsync(TestContext.Current.CancellationToken))
            .Should().ContainSingle(role => role.Name == BuiltinRoles.Member).Which;
        var readDeny = await AsUser().CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            memberRole.Id,
            EntityKinds.Image,
            image.Id.ToString(CultureInfo.InvariantCulture),
            "deny",
            "read"), TestContext.Current.CancellationToken);
        using (var memberSession = await AsUser().CreateAuthSessionAsync(ApiTestUsers.Eva, ApiTestUsers.Password, TestContext.Current.CancellationToken))
        using (var memberClient = memberSession.Client.CreateHttpClient())
        {
            using var deniedImageResponse = await memberClient.GetAsync($"/api/stream/image/{image.Id}", TestContext.Current.CancellationToken);
            deniedImageResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            deniedImageResponse.Content.Headers.ContentType?.MediaType.Should().NotBe("image/png");

            using var deniedThumbnailResponse = await memberClient.GetAsync($"/api/stream/image/{image.Id}/thumbnail?max=64", TestContext.Current.CancellationToken);
            deniedThumbnailResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            deniedThumbnailResponse.Content.Headers.ContentType?.MediaType.Should().NotBe("image/png");
        }
        Directory.EnumerateFiles(AsTestFileSystem().GeneratedPath, "*", SearchOption.AllDirectories).Should().BeEmpty();
        await AsUser().DeleteEntityOverrideAsync(readDeny.Id, TestContext.Current.CancellationToken);

        using var client = AsUser().CreateHttpClient();
        using var imageRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/stream/image/{image.Id}");
        imageRequest.Headers.Range = new RangeHeaderValue(0, 7);
        using var imageResponse = await client.SendAsync(imageRequest, TestContext.Current.CancellationToken);
        imageResponse.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await imageResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Equal(imageBytes[..8]);
        imageResponse.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        imageResponse.Content.Headers.ContentRange?.ToString().Should().Be($"bytes 0-7/{imageBytes.Length}");
        imageResponse.Headers.AcceptRanges.Should().Equal("bytes");
        imageResponse.Headers.CacheControl?.ToString().Should().Be("public, max-age=86400");

        using var thumbnailResponse = await client.GetAsync($"/api/stream/image/{image.Id}/thumbnail?max=64", TestContext.Current.CancellationToken);
        var thumbnailBytes = await thumbnailResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        thumbnailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        thumbnailResponse.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        thumbnailResponse.Headers.AcceptRanges.Should().Equal("bytes");
        thumbnailResponse.Headers.CacheControl?.ToString().Should().Be("public, max-age=86400");
        using (var thumbnail = Image.Load(thumbnailBytes))
        {
            thumbnail.Width.Should().Be(64);
            thumbnail.Height.Should().Be(48);
        }

        using var thumbnailRangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/stream/image/{image.Id}/thumbnail?max=64");
        thumbnailRangeRequest.Headers.Range = new RangeHeaderValue(1, 4);
        using var thumbnailRangeResponse = await client.SendAsync(thumbnailRangeRequest, TestContext.Current.CancellationToken);
        thumbnailRangeResponse.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await thumbnailRangeResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Equal(thumbnailBytes[1..5]);
        thumbnailRangeResponse.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        thumbnailRangeResponse.Content.Headers.ContentRange?.ToString().Should().Be($"bytes 1-4/{thumbnailBytes.Length}");
        thumbnailRangeResponse.Headers.AcceptRanges.Should().Equal("bytes");
        thumbnailRangeResponse.Headers.CacheControl?.ToString().Should().Be("public, max-age=86400");

        using var cropResponse = await client.GetAsync($"/api/stream/detection/{detection.Id}/crop?max=64", TestContext.Current.CancellationToken);
        var cropBytes = await cropResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        cropResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        cropResponse.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
        cropResponse.Headers.CacheControl?.ToString().Should().Be("public, max-age=86400");
        cropBytes.Should().NotBeEmpty();
        using var crop = Image.Load(cropBytes);
        crop.Width.Should().Be(64);
        crop.Height.Should().Be(64);

        using var missingResponse = await client.GetAsync("/api/stream/image/2147483647", TestContext.Current.CancellationToken);
        missingResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var noPermissionUsername = $"stream-no-permission-{Guid.NewGuid():N}";
        const string noPermissionPassword = "Stream password 123!";
        await AsUser().CreateUserAsync(new CreateUserRequest(noPermissionUsername, noPermissionPassword, Roles: []), TestContext.Current.CancellationToken);
        using var noPermissionSession = await AsUser().CreateAuthSessionAsync(noPermissionUsername, noPermissionPassword, TestContext.Current.CancellationToken);
        using var noPermissionClient = noPermissionSession.Client.CreateHttpClient();
        using var forbiddenResponse = await noPermissionClient.GetAsync($"/api/stream/image/{image.Id}", TestContext.Current.CancellationToken);
        forbiddenResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static async Task AssertHiddenAsync(HttpClient client, string requestUri, string mediaType)
    {
        using var response = await client.GetAsync(requestUri);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().NotBe(mediaType);
    }

    private sealed record StreamCaptionResponse(int Id, string LanguageCode, string CaptionType, string Filename);

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
