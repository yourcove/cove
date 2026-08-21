using System.Globalization;
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
        var evaBeforeDelete = await eva.GetVideoHistoryAsync(video);
        var anthonyBeforeDelete = await anthony.GetVideoHistoryAsync(video);
        await eva.DeleteVideoPlayAsync(video);
        var evaAfterDelete = await eva.GetVideoHistoryAsync(video);
        var anthonyAfterDelete = await anthony.GetVideoHistoryAsync(video);
        await eva.ResetVideoPlayAsync(video);

        var evaHistory = await eva.GetVideoHistoryAsync(video);
        var anthonyHistory = await anthony.GetVideoHistoryAsync(video);
        var ownerHistory = await owner.GetVideoHistoryAsync(video);
        var evaEngagement = await eva.GetVideoEngagementAsync(video);
        var anthonyEngagement = await anthony.GetVideoEngagementAsync(video);
        var ownerEngagement = await owner.GetVideoEngagementAsync(video);

        evaBeforeDelete.PlayHistory.Should().HaveCount(2);
        anthonyBeforeDelete.PlayHistory.Should().HaveCount(2);
        evaAfterDelete.PlayHistory.Should().ContainSingle();
        anthonyAfterDelete.PlayHistory.Should().HaveCount(2);
        evaHistory.PlayHistory.Should().BeEmpty();
        evaEngagement.PlayCount.Should().Be(0);
        anthonyHistory.PlayHistory.Should().HaveCount(2);
        anthonyEngagement.PlayCount.Should().Be(2);
        ownerHistory.PlayHistory.Should().BeEmpty();
        ownerEngagement.PlayCount.Should().Be(0);
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
        var evaHistorical = await eva.GetVideoHistoryAsync(video);
        await anthony.IncrementVideoLikeAsync(video);
        await eva.DeleteHistoricalVideoLikeAsync(video, historicalAt);
        var evaAfterHistoricalDelete = await eva.GetVideoHistoryAsync(video);
        var anthonyAfterHistoricalDelete = await anthony.GetVideoHistoryAsync(video);
        var evaEngagementAfterHistoricalDelete = await eva.GetVideoEngagementAsync(video);
        var anthonyEngagementAfterHistoricalDelete = await anthony.GetVideoEngagementAsync(video);
        await eva.IncrementVideoLikeAsync(video);
        await eva.IncrementVideoLikeAsync(video);
        var evaBeforeDecrement = await eva.GetVideoHistoryAsync(video);
        var evaEngagementBeforeDecrement = await eva.GetVideoEngagementAsync(video);
        await eva.DecrementVideoLikeAsync(video);
        var evaAfterDecrement = await eva.GetVideoHistoryAsync(video);
        var evaEngagementAfterDecrement = await eva.GetVideoEngagementAsync(video);
        await eva.ResetVideoLikeAsync(video);

        var evaHistory = await eva.GetVideoHistoryAsync(video);
        var anthonyHistory = await anthony.GetVideoHistoryAsync(video);
        var ownerHistory = await owner.GetVideoHistoryAsync(video);
        var evaEngagement = await eva.GetVideoEngagementAsync(video);
        var anthonyEngagement = await anthony.GetVideoEngagementAsync(video);
        var ownerEngagement = await owner.GetVideoEngagementAsync(video);

        evaHistorical.LikeHistory.Should().ContainSingle();
        DateTime.Parse(
                evaHistorical.LikeHistory.Single(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind)
            .Should().Be(historicalAt);
        evaAfterHistoricalDelete.LikeHistory.Should().BeEmpty();
        evaEngagementAfterHistoricalDelete.LikeCount.Should().Be(0);
        anthonyAfterHistoricalDelete.LikeHistory.Should().ContainSingle();
        anthonyEngagementAfterHistoricalDelete.LikeCount.Should().Be(1);
        evaBeforeDecrement.LikeHistory.Should().HaveCount(2);
        evaEngagementBeforeDecrement.LikeCount.Should().Be(2);
        evaAfterDecrement.LikeHistory.Should().ContainSingle();
        evaEngagementAfterDecrement.LikeCount.Should().Be(1);
        evaHistory.LikeHistory.Should().BeEmpty();
        evaEngagement.LikeCount.Should().Be(0);
        anthonyHistory.LikeHistory.Should().ContainSingle();
        anthonyEngagement.LikeCount.Should().Be(1);
        ownerHistory.LikeHistory.Should().BeEmpty();
        ownerEngagement.LikeCount.Should().Be(0);
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

        var overallResponse = await eva.SetVideoRatingViaVideoAsync(video, 82, "overall");
        var contentResponse = await eva.SetVideoRatingViaVideoAsync(video, 46, "content");
        var anthonyOverallResponse = await anthony.SetVideoRatingViaVideoAsync(video, 71, "overall");
        var anthonyQualityResponse = await anthony.SetVideoRatingViaVideoAsync(video, 64, "video_quality");
        var evaBeforeClear = await eva.GetVideoRatingsViaVideoAsync(video);
        var anthonyBeforeClear = await anthony.GetVideoRatingsViaVideoAsync(video);
        var ownerBeforeClear = await owner.GetVideoRatingsViaVideoAsync(video);
        await eva.ClearVideoRatingViaVideoAsync(video, "content");
        var evaAfterClear = await eva.GetVideoRatingsViaVideoAsync(video);
        var anthonyAfterClear = await anthony.GetVideoRatingsViaVideoAsync(video);

        overallResponse.Should().Be(82);
        contentResponse.Should().Be(82, "the legacy scalar response represents the overall rating");
        anthonyOverallResponse.Should().Be(71);
        anthonyQualityResponse.Should().Be(71, "the legacy scalar response represents the overall rating");
        evaBeforeClear.HostId.Should().Be(video.Id);
        evaBeforeClear.Ratings.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["content"] = 46,
            ["overall"] = 82,
        });
        anthonyBeforeClear.HostId.Should().Be(video.Id);
        anthonyBeforeClear.Ratings.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["overall"] = 71,
            ["video_quality"] = 64,
        });
        ownerBeforeClear.HostId.Should().Be(video.Id);
        ownerBeforeClear.Ratings.Should().BeEmpty();
        evaAfterClear.HostId.Should().Be(video.Id);
        evaAfterClear.Ratings.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["overall"] = 82,
        });
        anthonyAfterClear.HostId.Should().Be(video.Id);
        anthonyAfterClear.Ratings.Should().BeEquivalentTo(anthonyBeforeClear.Ratings);
    }
}
