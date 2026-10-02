using System.Net;
using System.Net.Http.Json;
using Cove.ApiTests.Infrastructure;
using Cove.Api.Controllers;

namespace Cove.ApiTests.Tests.Stream;

public sealed class VrStreamApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/stream/video/{videoid:int}/vr-card")]
    [CoversEndpoint("GET", "/api/stream/video/{videoid:int}/vr-preview")]
    public async Task GivenVrVideoWithGeneratedStereoAssets_WhenVrStreamRoutesAreRead_ThenOnlyExistingAssetsAreServed()
    {
        var video = await AsUser().CreateVideoAsync($"VR stream {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var flat = await AsUser().CreateVideoAsync($"Flat stream {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var fileSystem = AsTestFileSystem();
        using var client = AsUser().CreateHttpClient();

        // Nothing is generated on request: a video without generated stereo assets answers 404.
        using (var missingCard = await client.GetAsync($"/api/stream/video/{video.Id}/vr-card", TestContext.Current.CancellationToken))
            missingCard.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var missingPreview = await client.GetAsync($"/api/stream/video/{video.Id}/vr-preview", TestContext.Current.CancellationToken))
            missingPreview.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var cardBytes = "api-test-vr-card"u8.ToArray();
        var previewBytes = "api-test-vr-preview"u8.ToArray();
        fileSystem.CreateVideoVrCard(video.Id, cardBytes);
        fileSystem.CreateVideoVrPreview(video.Id, previewBytes);
        fileSystem.CreateVideoVrCard(flat.Id, cardBytes);

        using (var card = await client.GetAsync($"/api/stream/video/{video.Id}/vr-card", TestContext.Current.CancellationToken))
        {
            card.StatusCode.Should().Be(HttpStatusCode.OK);
            card.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
            card.Headers.CacheControl?.ToString().Should().Be("public, max-age=86400");
            (await card.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Equal(cardBytes);
        }

        using (var preview = await client.GetAsync($"/api/stream/video/{video.Id}/vr-preview", TestContext.Current.CancellationToken))
        {
            preview.StatusCode.Should().Be(HttpStatusCode.OK);
            preview.Content.Headers.ContentType?.MediaType.Should().Be("video/mp4");
            preview.Headers.AcceptRanges.Should().Equal("bytes");
            (await preview.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Equal(previewBytes);
        }

        // The routes serve whatever the generate job left; they do not re-check the video's VR flag.
        using (var flatCard = await client.GetAsync($"/api/stream/video/{flat.Id}/vr-card", TestContext.Current.CancellationToken))
            flatCard.StatusCode.Should().Be(HttpStatusCode.OK);

        var missingId = int.MaxValue - video.Id;
        using (var unknown = await client.GetAsync($"/api/stream/video/{missingId}/vr-preview", TestContext.Current.CancellationToken))
            unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [CoversEndpoint("GET", "/api/https")]
    [CoversEndpoint("GET", "/api/https/ca.crt")]
    public async Task GivenNoHttpsListener_WhenHttpsRoutesAreRead_ThenStatusIsDisabledAndNoAuthorityIsServed()
    {
        using var client = AsUser().CreateHttpClient();

        using var status = await client.GetAsync("/api/https", TestContext.Current.CancellationToken);
        status.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await status.Content.ReadFromJsonAsync<HttpsStatusDto>(ApiJson.Options, TestContext.Current.CancellationToken);
        dto.Should().NotBeNull();
        dto!.Enabled.Should().BeFalse();
        dto.Port.Should().BeNull();
        dto.HostNames.Should().BeEmpty();
        dto.CertificateAuthorityUrl.Should().BeNull();

        using var authority = await client.GetAsync("/api/https/ca.crt", TestContext.Current.CancellationToken);
        authority.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
