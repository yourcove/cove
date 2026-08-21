using Cove.ApiTests.Infrastructure;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Interactions;

[Collection(ApiTestLane1Collection.Name)]
public sealed class VideoEngagementLifecycleApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/videos/{id:int}/play")]
    [CoversEndpoint("DELETE", "/api/videos/{id:int}/play")]
    [CoversEndpoint("POST", "/api/videos/{id:int}/play/reset")]
    public async Task GivenTwoMembersPlayVideo_WhenOneDeletesAndResetsOwnHistory_ThenOtherMemberHistoryRemains()
    {
        var owner = AsUser();
        var eva = AsUser(ApiTestUsers.Eva);
        var anthony = AsUser(ApiTestUsers.Anthony);
        var video = await owner.CreateVideoAsync($"Video play lifecycle {Guid.NewGuid():N}");

        await eva.RecordVideoPlayAsync(video);
        await eva.RecordVideoPlayAsync(video);
        await anthony.RecordVideoPlayAsync(video);
        await anthony.RecordVideoPlayAsync(video);
        await eva.DeleteVideoPlayAsync(video);
        await eva.ResetVideoPlayAsync(video);

        var evaHistory = await eva.GetVideoHistoryAsync(video);
        var anthonyHistory = await anthony.GetVideoHistoryAsync(video);
        var evaEngagement = await eva.GetVideoEngagementAsync(video);
        var anthonyEngagement = await anthony.GetVideoEngagementAsync(video);

        evaHistory.PlayHistory.Should().BeEmpty();
        evaEngagement.PlayCount.Should().Be(0);
        anthonyHistory.PlayHistory.Should().HaveCount(2);
        anthonyEngagement.PlayCount.Should().Be(2);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/videos/{id:int}/like/historical")]
    [CoversEndpoint("DELETE", "/api/videos/{id:int}/like/history")]
    [CoversEndpoint("DELETE", "/api/videos/{id:int}/like")]
    [CoversEndpoint("POST", "/api/videos/{id:int}/like/reset")]
    public async Task GivenMemberLikeHistory_WhenHistoricalAndCurrentLikesAreRemoved_ThenOtherMemberLikesRemain()
    {
        var owner = AsUser();
        var eva = AsUser(ApiTestUsers.Eva);
        var anthony = AsUser(ApiTestUsers.Anthony);
        var video = await owner.CreateVideoAsync($"Video like lifecycle {Guid.NewGuid():N}");
        var now = DateTime.UtcNow;
        var historicalAt = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, DateTimeKind.Utc).AddDays(-1);

        (await eva.AddHistoricalVideoLikeAsync(video, historicalAt)).Should().Be(1);
        await anthony.IncrementVideoLikeAsync(video);
        await eva.DeleteHistoricalVideoLikeAsync(video, historicalAt);
        await eva.IncrementVideoLikeAsync(video);
        await eva.IncrementVideoLikeAsync(video);
        await eva.DecrementVideoLikeAsync(video);
        await eva.ResetVideoLikeAsync(video);

        var evaHistory = await eva.GetVideoHistoryAsync(video);
        var anthonyHistory = await anthony.GetVideoHistoryAsync(video);
        var evaEngagement = await eva.GetVideoEngagementAsync(video);
        var anthonyEngagement = await anthony.GetVideoEngagementAsync(video);

        evaHistory.LikeHistory.Should().BeEmpty();
        evaEngagement.LikeCount.Should().Be(0);
        anthonyHistory.LikeHistory.Should().ContainSingle();
        anthonyEngagement.LikeCount.Should().Be(1);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/videos/{id:int}/rating")]
    [CoversEndpoint("GET", "/api/videos/{id:int}/ratings")]
    [CoversEndpoint("DELETE", "/api/videos/{id:int}/rating")]
    public async Task GivenMemberAspectRatings_WhenSetReadAndCleared_ThenResponsesAndFreshReadsAreUserScoped()
    {
        var owner = AsUser();
        var eva = AsUser(ApiTestUsers.Eva);
        var anthony = AsUser(ApiTestUsers.Anthony);
        var video = await owner.CreateVideoAsync($"Video rating lifecycle {Guid.NewGuid():N}");

        var contentResponse = await eva.SetVideoRatingViaVideoAsync(video, 46, "content");
        var overallResponse = await eva.SetVideoRatingViaVideoAsync(video, 82, "overall");
        await anthony.SetVideoRatingViaVideoAsync(video, 71, "video_quality");
        var evaBeforeClear = await eva.GetVideoRatingsViaVideoAsync(video);
        var anthonyBeforeClear = await anthony.GetVideoRatingsViaVideoAsync(video);
        await eva.ClearVideoRatingViaVideoAsync(video, "content");
        var evaAfterClear = await eva.GetVideoRatingsViaVideoAsync(video);
        var anthonyAfterClear = await anthony.GetVideoRatingsViaVideoAsync(video);

        contentResponse.Should().Be(46);
        overallResponse.Should().Be(82);
        evaBeforeClear.Ratings.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["content"] = 46,
            ["overall"] = 82,
        });
        anthonyBeforeClear.Ratings.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["video_quality"] = 71,
        });
        evaAfterClear.Ratings.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["overall"] = 82,
        });
        anthonyAfterClear.Ratings.Should().BeEquivalentTo(anthonyBeforeClear.Ratings);
    }
}
