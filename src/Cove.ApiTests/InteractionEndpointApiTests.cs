using Cove.Api.Controllers;
using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
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
        var video = await AsUser().CreateVideoAsync(TestCatalog.Movies.TheFastAndTheFlirtatious.Title);

        var updated = await AsUser().SetVideoFavoriteAsync(video, isFavorite: true);

        updated.IsFavorite.Should().BeTrue();
        var engagement = await AsUser().GetVideoEngagementAsync(video);
        engagement.IsFavorite.Should().BeTrue();
    }

    [Fact]
    public async Task GivenPerformer_WhenMembersInteractWithPerformer_ThenEachMemberHasOwnEngagement()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .Build());

        // Act
        await AsUser(ApiTestUsers.Eva).SetPerformerRatingAsync(performer, 81);
        await AsUser(ApiTestUsers.Eva).SetPerformerRatingAsync(performer, 62, "face");
        await AsUser(ApiTestUsers.Eva).SetPerformerRatingAsync(performer, 43, "body");
        await AsUser(ApiTestUsers.Eva).SetPerformerRatingAsync(performer, 24, "voice");
        await AsUser(ApiTestUsers.Anthony).SetPerformerRatingAsync(performer, 92);
        await AsUser(ApiTestUsers.Anthony).SetPerformerRatingAsync(performer, 73, "face");
        await AsUser(ApiTestUsers.Anthony).SetPerformerRatingAsync(performer, 54, "body");
        await AsUser(ApiTestUsers.Anthony).SetPerformerRatingAsync(performer, 35, "voice");
        await AsUser(ApiTestUsers.Eva).SetPerformerFavoriteAsync(performer, isFavorite: true);
        await AsUser(ApiTestUsers.Anthony).SetPerformerFavoriteAsync(performer, isFavorite: false);
        await AsUser(ApiTestUsers.Eva).SetPerformerBookmarkAsync(performer, isSaved: false);
        await AsUser(ApiTestUsers.Anthony).SetPerformerBookmarkAsync(performer, isSaved: true);

        // Assert
        var evaRatings = await AsUser(ApiTestUsers.Eva).GetPerformerRatingsAsync(performer);
        evaRatings.Ratings.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["overall"] = 81,
            ["face"] = 62,
            ["body"] = 43,
            ["voice"] = 24,
        });
        var anthonyRatings = await AsUser(ApiTestUsers.Anthony).GetPerformerRatingsAsync(performer);
        anthonyRatings.Ratings.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["overall"] = 92,
            ["face"] = 73,
            ["body"] = 54,
            ["voice"] = 35,
        });
        (await AsUser(ApiTestUsers.Eva).GetPerformerEngagementAsync(performer)).IsFavorite.Should().BeTrue();
        (await AsUser(ApiTestUsers.Anthony).GetPerformerEngagementAsync(performer)).IsFavorite.Should().BeFalse();
        (await AsUser(ApiTestUsers.Eva).GetPerformerBookmarkAsync(performer)).Saved.Should().BeFalse();
        (await AsUser(ApiTestUsers.Anthony).GetPerformerBookmarkAsync(performer)).Saved.Should().BeTrue();
    }

    [Fact]
    [CoversEndpoints(typeof(PlaybackController))]
    public async Task GivenVideo_WhenPlaybackIsRecorded_ThenHistoryContainsSession()
    {
        var video = await AsUser().CreateVideoAsync(TestCatalog.Movies.RaidersOfTheLostCorset.Title);
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
        var video = await AsUser().CreateVideoAsync(TestCatalog.Movies.HotSinglesInYourDatabase.Title);

        var attempts = await AsUser().GetVideoScrapeAttemptsAsync(video);

        attempts.Should().BeEmpty();
    }

    [Fact]
    [CoversEndpoints(typeof(StreamController))]
    public async Task GivenVideoWithoutPreview_WhenPreviewStatusIsRead_ThenPreviewIsUnavailable()
    {
        var video = await AsUser().CreateVideoAsync(TestCatalog.Movies.NoShirtNoShoesNoAlibi.Title);

        var available = await AsUser().GetVideoPreviewAvailabilityAsync(video);

        available.Should().BeFalse();
    }
}
