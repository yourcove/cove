using Cove.Api.Controllers;
using Cove.ApiTests.Infrastructure;
using Xunit.Abstractions;

namespace Cove.ApiTests;

[Collection(ApiTestLane2Collection.Name)]
public sealed class InteractionEndpointApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoints(typeof(EntityEngagementController))]
    public async Task GivenVideo_WhenFavoriteIsSet_ThenEngagementIsFavorite()
    {
        var video = await AsUser().CreateVideoAsync("API test favorite video");

        var updated = await AsUser().SetVideoFavoriteAsync(video, isFavorite: true);

        updated.IsFavorite.Should().BeTrue();
        var engagement = await AsUser().GetVideoEngagementAsync(video);
        engagement.IsFavorite.Should().BeTrue();
    }

    [Fact]
    [CoversEndpoints(typeof(PlaybackController))]
    public async Task GivenVideo_WhenPlaybackIsRecorded_ThenHistoryContainsSession()
    {
        var video = await AsUser().CreateVideoAsync("API test playback video");
        var sessionId = Guid.NewGuid();

        await AsUser().RecordVideoPlaybackAsync(video, sessionId);

        var history = await AsUser().GetVideoHistoryAsync(video);
        history.Sessions.Should().NotBeNull();
        history.Sessions!.Should().ContainSingle(session => session.SessionId == sessionId);
        history.TotalDistinctWatchedSec.Should().Be(6);
    }

    [Fact]
    [CoversEndpoints(typeof(ScrapeAttemptsController))]
    public async Task GivenVideoWithoutScrapes_WhenScrapeAttemptsAreRead_ThenAttemptListIsEmpty()
    {
        var video = await AsUser().CreateVideoAsync("API test unscraped video");

        var attempts = await AsUser().GetVideoScrapeAttemptsAsync(video);

        attempts.Should().BeEmpty();
    }

    [Fact]
    [CoversEndpoints(typeof(StreamController))]
    public async Task GivenVideoWithoutPreview_WhenPreviewStatusIsRead_ThenPreviewIsUnavailable()
    {
        var video = await AsUser().CreateVideoAsync("API test preview status video");

        var available = await AsUser().GetVideoPreviewAvailabilityAsync(video);

        available.Should().BeFalse();
    }
}
