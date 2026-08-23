using System.Globalization;
using System.Net;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Enums;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Entities.Audios;

[Collection(ApiTestLane2Collection.Name)]
public sealed class AudioEngagementLifecycleApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/audios/{id:int}/history")]
    [CoversEndpoint("POST", "/api/audios/{id:int}/like")]
    [CoversEndpoint("POST", "/api/audios/{id:int}/like/historical")]
    [CoversEndpoint("DELETE", "/api/audios/{id:int}/like/history")]
    [CoversEndpoint("DELETE", "/api/audios/{id:int}/like")]
    [CoversEndpoint("POST", "/api/audios/{id:int}/like/reset")]
    public async Task GivenUserScopedAudioLikes_WhenHistoricalCurrentAndResetOperationsRun_ThenHistoryCountsPermissionsAndSortRemainExact()
    {
        var owner = AsUser();
        var eva = AsUser(ApiTestUsers.Eva);
        var anthony = AsUser(ApiTestUsers.Anthony);
        var suffix = Guid.NewGuid().ToString("N");
        var primary = await owner.CreateAudioAsync($"Primary like audio {suffix}");
        var secondary = await owner.CreateAudioAsync($"Secondary like audio {suffix}");
        var control = await owner.CreateAudioAsync($"Control like audio {suffix}");
        var now = DateTime.UtcNow;
        var historicalAt = new DateTime(
            now.Year,
            now.Month,
            now.Day,
            now.Hour,
            now.Minute,
            now.Second,
            DateTimeKind.Utc).AddDays(-1);

        (await eva.AddHistoricalAudioLikeAsync(primary, historicalAt)).Should().Be(1);
        var historical = await eva.GetAudioHistoryAsync(primary);
        (await anthony.IncrementAudioLikeAsync(primary)).Should().Be(1);

        var viewerUsername = $"audio-viewer-{Guid.NewGuid():N}";
        const string viewerPassword = "Audio viewer password 123!";
        var viewerUser = await owner.CreateUserAsync(new CreateUserRequest(
            viewerUsername,
            viewerPassword,
            Roles: [BuiltinRoles.Member]));
        var viewerHistoricalAt = historicalAt.AddHours(1);
        var viewerSessionId = Guid.NewGuid();
        using (var memberSession = await owner.CreateAuthSessionAsync(viewerUsername, viewerPassword))
        {
            (await memberSession.Client.AddHistoricalAudioLikeAsync(primary, viewerHistoricalAt)).Should().Be(1);
            await memberSession.Client.RecordAudioPlaybackAsync(primary, viewerSessionId, startSec: 1, endSec: 3);
        }
        _ = await owner.SetUserRolesAsync(viewerUser.Id, [BuiltinRoles.Viewer]);
        using var viewerSession = await owner.CreateAuthSessionAsync(viewerUsername, viewerPassword);
        var viewer = viewerSession.Client;
        var forbiddenViewerWrites = new Func<Task>[]
        {
            async () => _ = await viewer.IncrementAudioLikeAsync(primary),
            async () => _ = await viewer.AddHistoricalAudioLikeAsync(primary, historicalAt),
            () => viewer.DeleteHistoricalAudioLikeAsync(primary, viewerHistoricalAt),
            async () => _ = await viewer.DecrementAudioLikeAsync(primary),
            async () => _ = await viewer.ResetAudioLikeAsync(primary),
            () => viewer.ResetAudioActivityAsync(primary),
        };
        foreach (var forbiddenViewerWrite in forbiddenViewerWrites)
        {
            await forbiddenViewerWrite.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
            var viewerAfterForbiddenWrite = await viewer.GetAudioHistoryAsync(primary);
            viewerAfterForbiddenWrite.LikeHistory.Should().ContainSingle();
            DateTime.Parse(
                    viewerAfterForbiddenWrite.LikeHistory.Single(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind)
                .Should().Be(viewerHistoricalAt);
            viewerAfterForbiddenWrite.Sessions.Should().ContainSingle(session => session.SessionId == viewerSessionId);
            var viewerEngagement = await viewer.GetEntityEngagementAsync(AffinityHostType.Audio, primary.Id);
            viewerEngagement.LikeCount.Should().Be(1);
            viewerEngagement.PlayDuration.Should().Be(2);
        }
        (await eva.GetAudioHistoryAsync(primary)).LikeHistory.Should().ContainSingle();
        (await anthony.GetAudioHistoryAsync(primary)).LikeHistory.Should().ContainSingle();
        (await owner.GetAudioHistoryAsync(primary)).LikeHistory.Should().BeEmpty();

        await eva.DeleteHistoricalAudioLikeAsync(primary, historicalAt);
        (await eva.GetAudioHistoryAsync(primary)).LikeHistory.Should().BeEmpty();
        (await eva.GetEntityEngagementAsync(AffinityHostType.Audio, primary.Id)).LikeCount.Should().Be(0);
        (await anthony.GetAudioHistoryAsync(primary)).LikeHistory.Should().ContainSingle();
        var futureHistoricalLike = () => eva.AddHistoricalAudioLikeAsync(primary, DateTime.UtcNow.AddDays(1));
        await futureHistoricalLike.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        (await eva.GetAudioHistoryAsync(primary)).LikeHistory.Should().BeEmpty();

        (await eva.IncrementAudioLikeAsync(primary)).Should().Be(1);
        (await eva.IncrementAudioLikeAsync(primary)).Should().Be(2);
        (await eva.IncrementAudioLikeAsync(secondary)).Should().Be(1);
        (await anthony.IncrementAudioLikeAsync(secondary)).Should().Be(1);
        (await anthony.IncrementAudioLikeAsync(secondary)).Should().Be(2);
        var sortedByLikes = await eva.FindAudiosAsync(new FilteredQueryRequest<AudioFilter>
        {
            Ids = [primary.Id, secondary.Id, control.Id],
            FindFilter = new FindFilter
            {
                Page = 1,
                PerPage = 10,
                Sort = "like_counter",
                Direction = SortDirection.Desc,
            },
        });
        sortedByLikes.Items.Select(audio => audio.Id).Should().Equal(primary.Id, secondary.Id, control.Id);
        var anthonySortedByLikes = await anthony.FindAudiosAsync(SortRequest([primary.Id, secondary.Id, control.Id], "like_counter"));
        anthonySortedByLikes.Items.Select(audio => audio.Id).Should().Equal(secondary.Id, primary.Id, control.Id);

        (await eva.DecrementAudioLikeAsync(primary)).Should().Be(1);
        (await eva.GetAudioHistoryAsync(primary)).LikeHistory.Should().ContainSingle();
        (await eva.ResetAudioLikeAsync(primary)).Should().Be(0);

        var evaHistory = await eva.GetAudioHistoryAsync(primary);
        var anthonyHistory = await anthony.GetAudioHistoryAsync(primary);
        var ownerHistory = await owner.GetAudioHistoryAsync(primary);
        evaHistory.LikeHistory.Should().BeEmpty();
        anthonyHistory.LikeHistory.Should().ContainSingle();
        ownerHistory.LikeHistory.Should().BeEmpty();
        (await eva.GetEntityEngagementAsync(AffinityHostType.Audio, primary.Id)).LikeCount.Should().Be(0);
        (await anthony.GetEntityEngagementAsync(AffinityHostType.Audio, primary.Id)).LikeCount.Should().Be(1);
        (await owner.GetEntityEngagementAsync(AffinityHostType.Audio, primary.Id)).LikeCount.Should().Be(0);

        historical.LikeHistory.Should().ContainSingle();
        DateTime.Parse(
                historical.LikeHistory.Single(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind)
            .Should().Be(historicalAt);

        using var client = owner.CreateHttpClient();
        using var missingHistory = await client.GetAsync("/api/audios/2147483647/history");
        missingHistory.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var missingLike = await client.PostAsync("/api/audios/2147483647/like", content: null);
        missingLike.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/audios/{id:int}/activity/reset")]
    public async Task GivenAudioPlaybackAcrossUsers_WhenOneUserResetsActivity_ThenOtherActivityLikesAndAffinitySortsRemainScoped()
    {
        var owner = AsUser();
        var eva = AsUser(ApiTestUsers.Eva);
        var anthony = AsUser(ApiTestUsers.Anthony);
        var suffix = Guid.NewGuid().ToString("N");
        var longest = await owner.CreateAudioAsync($"Longest played audio {suffix}");
        var recent = await owner.CreateAudioAsync($"Recent played audio {suffix}");
        var control = await owner.CreateAudioAsync($"Unplayed audio {suffix}");

        await eva.RecordAudioPlaybackAsync(longest, Guid.NewGuid(), startSec: 1, endSec: 8);
        await eva.RecordAudioPlaybackAsync(recent, Guid.NewGuid(), startSec: 1, endSec: 4);
        await anthony.RecordAudioPlaybackAsync(recent, Guid.NewGuid(), startSec: 1, endSec: 9);
        await anthony.RecordAudioPlaybackAsync(longest, Guid.NewGuid(), startSec: 2, endSec: 6);
        (await eva.IncrementAudioLikeAsync(longest)).Should().Be(1);

        var byDuration = await eva.FindAudiosAsync(SortRequest([longest.Id, recent.Id, control.Id], "play_duration"));
        byDuration.Items.Select(audio => audio.Id).Should().Equal(longest.Id, recent.Id, control.Id);
        var anthonyByDuration = await anthony.FindAudiosAsync(SortRequest([longest.Id, recent.Id, control.Id], "play_duration"));
        anthonyByDuration.Items.Select(audio => audio.Id).Should().Equal(recent.Id, longest.Id, control.Id);
        var byLastPlayed = await eva.FindAudiosAsync(SortRequest([longest.Id, recent.Id, control.Id], "last_played_at"));
        byLastPlayed.Items.Take(2).Select(audio => audio.Id).Should().Equal(recent.Id, longest.Id);
        var anthonyByLastPlayed = await anthony.FindAudiosAsync(SortRequest([longest.Id, recent.Id, control.Id], "last_played_at"));
        anthonyByLastPlayed.Items.Take(2).Select(audio => audio.Id).Should().Equal(longest.Id, recent.Id);

        var evaBefore = await eva.GetAudioHistoryAsync(longest);
        var anthonyBefore = await anthony.GetAudioHistoryAsync(longest);
        evaBefore.Sessions.Should().ContainSingle();
        evaBefore.TotalDistinctWatchedSec.Should().Be(7);
        anthonyBefore.Sessions.Should().ContainSingle();
        anthonyBefore.TotalDistinctWatchedSec.Should().Be(4);

        await eva.ResetAudioActivityAsync(longest);

        var evaAfter = await eva.GetAudioHistoryAsync(longest);
        var anthonyAfter = await anthony.GetAudioHistoryAsync(longest);
        var recentAfter = await eva.GetAudioHistoryAsync(recent);
        var evaEngagement = await eva.GetEntityEngagementAsync(AffinityHostType.Audio, longest.Id);
        var anthonyEngagement = await anthony.GetEntityEngagementAsync(AffinityHostType.Audio, longest.Id);
        evaAfter.Sessions.Should().BeEmpty();
        evaAfter.TotalDistinctWatchedSec.Should().Be(0);
        evaAfter.LikeHistory.Should().ContainSingle("activity reset must preserve explicit likes");
        evaEngagement.PlayDuration.Should().Be(0);
        evaEngagement.LastPlayedAt.Should().BeNull();
        evaEngagement.LikeCount.Should().Be(1);
        anthonyAfter.Sessions.Should().ContainSingle();
        anthonyAfter.TotalDistinctWatchedSec.Should().Be(4);
        anthonyEngagement.PlayDuration.Should().Be(4);
        anthonyEngagement.LastPlayedAt.Should().NotBeNull();
        recentAfter.Sessions.Should().ContainSingle();
        recentAfter.TotalDistinctWatchedSec.Should().Be(3);
    }

    private static FilteredQueryRequest<AudioFilter> SortRequest(IReadOnlyList<int> ids, string sort)
        => new()
        {
            Ids = ids.ToList(),
            FindFilter = new FindFilter
            {
                Page = 1,
                PerPage = 10,
                Sort = sort,
                Direction = SortDirection.Desc,
            },
        };
}
