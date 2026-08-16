using System.Net;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities.Auth;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Interactions;

[Collection(ApiTestLane1Collection.Name)]
public sealed class VideoActivityResetApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/videos/{id:int}/activity/reset")]
    public async Task GivenUserScopedPlayback_WhenVideoActivityIsReset_ThenLikesAndOtherActivityRemainExact()
    {
        var primary = await AsUser().CreateVideoAsync($"Activity reset {Guid.NewGuid():N}");
        var control = await AsUser().CreateVideoAsync($"Activity reset control {Guid.NewGuid():N}");
        var viewerUsername = $"activity-reset-viewer-{Guid.NewGuid():N}";
        const string viewerPassword = "Activity reset viewer password 123!";
        await AsUser().CreateUserAsync(new CreateUserRequest(
            viewerUsername,
            viewerPassword,
            Roles: [BuiltinRoles.Viewer]));
        using var viewerSession = await AsUser().CreateAuthSessionAsync(viewerUsername, viewerPassword);

        await AsUser(ApiTestUsers.Eva).RecordVideoPlaybackAsync(primary, Guid.NewGuid());
        await AsUser(ApiTestUsers.Anthony).RecordVideoPlaybackAsync(primary, Guid.NewGuid());
        await AsUser(ApiTestUsers.Eva).RecordVideoPlaybackAsync(control, Guid.NewGuid());
        await viewerSession.Client.RecordVideoPlaybackAsync(primary, Guid.NewGuid());
        await AsUser(ApiTestUsers.Eva).IncrementVideoLikeAsync(primary);

        var evaBefore = await AsUser(ApiTestUsers.Eva).GetVideoHistoryAsync(primary);
        var anthonyBefore = await AsUser(ApiTestUsers.Anthony).GetVideoHistoryAsync(primary);
        var viewerBefore = await viewerSession.Client.GetVideoHistoryAsync(primary);
        evaBefore.Sessions.Should().ContainSingle();
        anthonyBefore.Sessions.Should().ContainSingle();
        viewerBefore.Sessions.Should().ContainSingle();

        await viewerSession.Client.Invoking(client => client.ResetVideoActivityAsync(primary))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        (await viewerSession.Client.GetVideoHistoryAsync(primary)).Sessions.Should().ContainSingle();

        await AsUser(ApiTestUsers.Eva).ResetVideoActivityAsync(primary);

        var evaAfter = await AsUser(ApiTestUsers.Eva).GetVideoHistoryAsync(primary);
        var anthonyAfter = await AsUser(ApiTestUsers.Anthony).GetVideoHistoryAsync(primary);
        var viewerAfter = await viewerSession.Client.GetVideoHistoryAsync(primary);
        var controlAfter = await AsUser(ApiTestUsers.Eva).GetVideoHistoryAsync(control);
        var evaEngagement = await AsUser(ApiTestUsers.Eva).GetVideoEngagementAsync(primary);
        var anthonyEngagement = await AsUser(ApiTestUsers.Anthony).GetVideoEngagementAsync(primary);

        evaAfter.Sessions.Should().BeEmpty();
        evaAfter.TotalDistinctWatchedSec.Should().Be(0);
        evaAfter.LikeHistory.Should().ContainSingle("activity reset must preserve explicit likes");
        evaEngagement.PlayDuration.Should().Be(0);
        evaEngagement.LastPlayedAt.Should().BeNull();
        evaEngagement.LikeCount.Should().Be(1);
        anthonyAfter.Sessions.Should().BeEquivalentTo(anthonyBefore.Sessions);
        anthonyAfter.TotalDistinctWatchedSec.Should().Be(anthonyBefore.TotalDistinctWatchedSec);
        anthonyEngagement.PlayDuration.Should().Be(anthonyBefore.TotalDistinctWatchedSec);
        viewerAfter.Sessions.Should().BeEquivalentTo(viewerBefore.Sessions);
        viewerAfter.TotalDistinctWatchedSec.Should().Be(viewerBefore.TotalDistinctWatchedSec);
        controlAfter.Sessions.Should().ContainSingle();
        controlAfter.TotalDistinctWatchedSec.Should().Be(6);

        using var httpClient = AsUser(ApiTestUsers.Eva).CreateHttpClient();
        using var missing = await httpClient.PostAsync(
            "/api/videos/2147483647/activity/reset",
            content: null);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
