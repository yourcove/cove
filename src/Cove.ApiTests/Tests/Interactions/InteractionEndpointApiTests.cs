using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;

namespace Cove.ApiTests.Tests.Interactions;

[Collection(ApiTestLane2Collection.Name)]
public sealed class InteractionEndpointApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("PUT", "/api/engagement/{hostType}/{hostId:int}/favorite")]
    [CoversEndpoint("GET", "/api/engagement/{hostType}/{hostId:int}")]
    public async Task GivenVideo_WhenFavoriteIsSet_ThenEngagementIsFavorite()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync(TestCatalog.Movies.TheFastAndTheFlirtatious.Title, TestContext.Current.CancellationToken);

        // Act
        var updated = await AsUser().SetVideoFavoriteAsync(video, isFavorite: true, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        updated.IsFavorite.Should().BeTrue();
        var engagement = await AsUser().GetVideoEngagementAsync(video, TestContext.Current.CancellationToken);
        engagement.IsFavorite.Should().BeTrue();
    }

    [Fact]
    [CoversEndpoint("PUT", "/api/engagement/{hostType}/{hostId:int}/rating")]
    [CoversEndpoint("GET", "/api/engagement/{hostType}/{hostId:int}/ratings")]
    [CoversEndpoint("POST", "/api/me/bookmarks")]
    [CoversEndpoint("POST", "/api/me/bookmarks/batch")]
    public async Task GivenPerformer_WhenMembersInteractWithPerformer_ThenEachMemberHasOwnEngagement()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .Build(), TestContext.Current.CancellationToken);

        // Act
        await AsUser(ApiTestUsers.Eva).SetPerformerRatingAsync(performer, 81, cancellationToken: TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).SetPerformerRatingAsync(performer, 62, "face", TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).SetPerformerRatingAsync(performer, 43, "body", TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).SetPerformerRatingAsync(performer, 24, "voice", TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Anthony).SetPerformerRatingAsync(performer, 92, cancellationToken: TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Anthony).SetPerformerRatingAsync(performer, 73, "face", TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Anthony).SetPerformerRatingAsync(performer, 54, "body", TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Anthony).SetPerformerRatingAsync(performer, 35, "voice", TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).SetPerformerFavoriteAsync(performer, isFavorite: true, cancellationToken: TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Anthony).SetPerformerFavoriteAsync(performer, isFavorite: false, cancellationToken: TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).SetPerformerBookmarkAsync(performer, isSaved: false, cancellationToken: TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Anthony).SetPerformerBookmarkAsync(performer, isSaved: true, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var evaRatings = await AsUser(ApiTestUsers.Eva).GetPerformerRatingsAsync(performer, TestContext.Current.CancellationToken);
        evaRatings.Ratings.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["overall"] = 81,
            ["face"] = 62,
            ["body"] = 43,
            ["voice"] = 24,
        });
        var anthonyRatings = await AsUser(ApiTestUsers.Anthony).GetPerformerRatingsAsync(performer, TestContext.Current.CancellationToken);
        anthonyRatings.Ratings.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["overall"] = 92,
            ["face"] = 73,
            ["body"] = 54,
            ["voice"] = 35,
        });
        (await AsUser(ApiTestUsers.Eva).GetPerformerEngagementAsync(performer, TestContext.Current.CancellationToken)).IsFavorite.Should().BeTrue();
        (await AsUser(ApiTestUsers.Anthony).GetPerformerEngagementAsync(performer, TestContext.Current.CancellationToken)).IsFavorite.Should().BeFalse();
        (await AsUser(ApiTestUsers.Eva).GetPerformerBookmarkAsync(performer, TestContext.Current.CancellationToken)).Saved.Should().BeFalse();
        (await AsUser(ApiTestUsers.Anthony).GetPerformerBookmarkAsync(performer, TestContext.Current.CancellationToken)).Saved.Should().BeTrue();
    }

    [Fact]
    public async Task GivenCanonicalMovie_WhenMembersRateVideo_ThenEachMemberHasOwnRatings()
    {
        // Arrange
        var movie = TestCatalog.Movies.TheFastAndTheFlirtatious;
        var performers = await Task.WhenAll(movie.Cast.Select(performer =>
            AsUser().CreatePerformerAsync(
                new PerformerBuilder()
                    .WithName(performer.Name)
                    .WithDetails(performer.Description)
                    .Build())));
        var tags = await Task.WhenAll(movie.Tags.Select(tag =>
            AsUser().CreateTagAsync(
                new TagBuilder()
                    .WithName(tag.Name)
                    .WithDescription(tag.Description)
                    .Build())));
        var created = await AsUser().CreateVideoAsync(new VideoBuilder()
                .WithTitle(movie.Title)
                .WithPerformers(performers)
                .WithTags(tags)
                .Build(), TestContext.Current.CancellationToken);

        // Act
        await AsUser(ApiTestUsers.Eva).SetVideoRatingAsync(created, 91, cancellationToken: TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).SetVideoRatingAsync(created, 82, "audio", TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).SetVideoRatingAsync(created, 73, "video_quality", TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).SetVideoRatingAsync(created, 64, "content", TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).SetVideoRatingAsync(created, 55, "performers", TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Anthony).SetVideoRatingAsync(created, 46, cancellationToken: TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Anthony).SetVideoRatingAsync(created, 37, "audio", TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Anthony).SetVideoRatingAsync(created, 28, "video_quality", TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Anthony).SetVideoRatingAsync(created, 19, "content", TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Anthony).SetVideoRatingAsync(created, 10, "performers", TestContext.Current.CancellationToken);

        // Assert
        var video = await AsUser().GetVideoByIdAsync(created.Id, TestContext.Current.CancellationToken);
        video.Performers.Select(performer => performer.Name).Should().BeEquivalentTo(movie.Cast.Select(performer => performer.Name));
        video.Tags.Select(tag => tag.Name).Should().BeEquivalentTo(movie.Tags.Select(tag => tag.Name));
        (await AsUser(ApiTestUsers.Eva).GetVideoRatingsAsync(video, TestContext.Current.CancellationToken)).Ratings.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["overall"] = 91,
            ["audio"] = 82,
            ["video_quality"] = 73,
            ["content"] = 64,
            ["performers"] = 55,
        });
        (await AsUser(ApiTestUsers.Anthony).GetVideoRatingsAsync(video, TestContext.Current.CancellationToken)).Ratings.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["overall"] = 46,
            ["audio"] = 37,
            ["video_quality"] = 28,
            ["content"] = 19,
            ["performers"] = 10,
        });
    }

    [Fact]
    [CoversEndpoint("POST", "/api/playback/intervals")]
    [CoversEndpoint("GET", "/api/videos/{id:int}/history")]
    public async Task GivenVideo_WhenPlaybackIsRecorded_ThenHistoryContainsSession()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync(TestCatalog.Movies.RaidersOfTheLostCorset.Title, TestContext.Current.CancellationToken);
        var sessionId = Guid.NewGuid();

        // Act
        await AsUser().RecordVideoPlaybackAsync(video, sessionId, TestContext.Current.CancellationToken);

        // Assert
        var history = await AsUser().GetVideoHistoryAsync(video, TestContext.Current.CancellationToken);
        history.Sessions.Should().NotBeNull();
        history.Sessions!.Should().ContainSingle(session => session.SessionId == sessionId);
        history.TotalDistinctWatchedSec.Should().Be(6);
    }

    [Fact]
    [CoversEndpoint("GET", "/api/scrape-attempts")]
    public async Task GivenVideoWithoutScrapes_WhenScrapeAttemptsAreRead_ThenAttemptListIsEmpty()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync(TestCatalog.Movies.HotSinglesInYourDatabase.Title, TestContext.Current.CancellationToken);

        // Act
        var attempts = await AsUser().GetVideoScrapeAttemptsAsync(video, TestContext.Current.CancellationToken);

        // Assert
        attempts.Should().BeEmpty();
    }

    [Fact]
    [CoversEndpoint("GET", "/api/stream/video/{videoId:int}/preview/status")]
    public async Task GivenVideoWithoutPreview_WhenPreviewStatusIsRead_ThenPreviewIsUnavailable()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync(TestCatalog.Movies.NoShirtNoShoesNoAlibi.Title, TestContext.Current.CancellationToken);

        // Act
        var available = await AsUser().GetVideoPreviewAvailabilityAsync(video, TestContext.Current.CancellationToken);

        // Assert
        available.Should().BeFalse();
    }
}
