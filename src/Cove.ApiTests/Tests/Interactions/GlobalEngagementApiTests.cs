using System.Globalization;
using System.Text.Json;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;

namespace Cove.ApiTests.Tests.Interactions;

[Collection(ApiTestLane2Collection.Name)]
public sealed class GlobalEngagementApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/engagement/batch")]
    [CoversEndpoint("POST", "/api/engagement/interactions")]
    [CoversEndpoint("GET", "/api/engagement/interactions")]
    public async Task GivenUserScopedSignals_WhenBatchAndInteractionsAreRead_ThenOrderingFilteringAndPermissionsAreExact()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var primaryVideo = await owner.CreateVideoAsync($"Primary engagement video {suffix}");
        var secondaryVideo = await owner.CreateVideoAsync($"Secondary engagement video {suffix}");
        var scopedRole = await owner.CreateRoleAsync(new CreateRoleRequest(
            $"Engagement reader {suffix}",
            "API-test role for entity-scoped engagement reads",
            [Permissions.VideosRead]));

        var primaryUsername = $"engagement-primary-{suffix}";
        var controlUsername = $"engagement-control-{suffix}";
        var noRoleUsername = $"engagement-no-role-{suffix}";
        const string password = "Global engagement 123!";
        await owner.CreateUserAsync(new CreateUserRequest(primaryUsername, password, Roles: [scopedRole.Name]));
        await owner.CreateUserAsync(new CreateUserRequest(controlUsername, password, Roles: [BuiltinRoles.Member]));
        await owner.CreateUserAsync(new CreateUserRequest(noRoleUsername, password, Roles: []));
        using var primarySession = await owner.CreateAuthSessionAsync(primaryUsername, password);
        using var controlSession = await owner.CreateAuthSessionAsync(controlUsername, password);
        using var noRoleSession = await owner.CreateAuthSessionAsync(noRoleUsername, password);
        var primary = primarySession.Client;
        var control = controlSession.Client;
        var noRole = noRoleSession.Client;

        _ = await primary.SetVideoFavoriteAsync(primaryVideo, isFavorite: true);
        _ = await primary.SetVideoRatingAsync(primaryVideo, 81);
        (await primary.IncrementVideoLikeAsync(primaryVideo)).Should().Be(1);
        _ = await primary.SetVideoRatingAsync(secondaryVideo, 42);
        _ = await control.SetVideoRatingAsync(primaryVideo, 17);
        (await control.IncrementVideoLikeAsync(primaryVideo)).Should().Be(1);
        (await control.IncrementVideoLikeAsync(primaryVideo)).Should().Be(2);

        var detailMeta = JsonSerializer.SerializeToElement(new { source = "detail" });
        var wallMeta = JsonSerializer.SerializeToElement(new { source = "wall" });
        await primary.RecordEngagementInteractionAsync(new EngagementInteractionWriteDto(
            "video",
            primaryVideo.Id,
            "openDetail",
            detailMeta));
        await primary.RecordEngagementInteractionAsync(new EngagementInteractionWriteDto(
            "video",
            secondaryVideo.Id,
            "pageVisit",
            wallMeta));
        await control.RecordEngagementInteractionAsync(new EngagementInteractionWriteDto(
            "video",
            primaryVideo.Id,
            "navigate"));

        var primaryInteractions = await primary.GetEngagementInteractionsAsync();
        primaryInteractions.Select(interaction => interaction.Kind).Should().Equal("pageVisit", "openDetail", "likeCount");
        primaryInteractions.Select(interaction => interaction.HostId).Should().Equal(secondaryVideo.Id, primaryVideo.Id, primaryVideo.Id);
        primaryInteractions.Select(interaction => interaction.Id).Should().Equal(primaryInteractions
            .OrderByDescending(interaction => DateTime.Parse(interaction.At, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
            .ThenByDescending(interaction => interaction.Id)
            .Select(interaction => interaction.Id));
        primaryInteractions[0].Meta!.Value.GetProperty("source").GetString().Should().Be("wall");
        primaryInteractions[1].Meta!.Value.GetProperty("source").GetString().Should().Be("detail");
        primaryInteractions[2].Meta.Should().BeNull();

        var primaryVideoInteractions = await primary.GetEngagementInteractionsAsync("video", primaryVideo.Id);
        primaryVideoInteractions.Select(interaction => interaction.Id).Should().Equal(primaryInteractions[1].Id, primaryInteractions[2].Id);
        (await primary.GetEngagementInteractionsAsync(limit: 1)).Should().ContainSingle().Which.Id.Should().Be(primaryInteractions[0].Id);
        var controlInteractions = await control.GetEngagementInteractionsAsync();
        controlInteractions.Select(interaction => interaction.Kind).Should().Equal("navigate", "likeCount", "likeCount");
        controlInteractions.Should().OnlyContain(interaction => interaction.HostId == primaryVideo.Id);
        (await noRole.GetEngagementInteractionsAsync()).Should().BeEmpty();

        var secondaryOverride = await owner.CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            scopedRole.Id,
            EntityKinds.Video,
            secondaryVideo.Id.ToString(CultureInfo.InvariantCulture),
            "deny",
            "read"));
        using var restrictedPrimarySession = await owner.CreateAuthSessionAsync(primaryUsername, password);
        var missingId = int.MaxValue;
        var batch = await restrictedPrimarySession.Client.GetEngagementBatchAsync(
            AffinityHostType.Video,
            [primaryVideo.Id, primaryVideo.Id, missingId, secondaryVideo.Id]);
        batch.Select(item => item.HostId).Should().Equal(primaryVideo.Id, missingId, secondaryVideo.Id);
        AssertSnapshot(batch[0], isFavorite: true, rating: 81, likeCount: 1, pageVisitCount: 0);
        AssertSnapshot(batch[1], isFavorite: false, rating: null, likeCount: 0, pageVisitCount: 0);
        AssertSnapshot(batch[2], isFavorite: false, rating: null, likeCount: 0, pageVisitCount: 0);
        var controlBatch = await control.GetEngagementBatchAsync(AffinityHostType.Video, [primaryVideo.Id, secondaryVideo.Id]);
        AssertSnapshot(controlBatch[0], isFavorite: false, rating: 17, likeCount: 2, pageVisitCount: 0);
        AssertSnapshot(controlBatch[1], isFavorite: false, rating: null, likeCount: 0, pageVisitCount: 0);
        await owner.DeleteEntityOverrideAsync(secondaryOverride.Id);
        using var restoredPrimarySession = await owner.CreateAuthSessionAsync(primaryUsername, password);
        var restoredBatch = await restoredPrimarySession.Client.GetEngagementBatchAsync(AffinityHostType.Video, [secondaryVideo.Id]);
        AssertSnapshot(restoredBatch.Should().ContainSingle().Which, isFavorite: false, rating: 42, likeCount: 0, pageVisitCount: 1);

        var forbiddenBatch = () => noRole.GetEngagementBatchAsync(AffinityHostType.Video, [primaryVideo.Id]);
        await forbiddenBatch.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        var forbiddenInteraction = () => noRole.RecordEngagementInteractionAsync(new EngagementInteractionWriteDto(
            "video",
            primaryVideo.Id,
            "openDetail"));
        await forbiddenInteraction.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await noRole.GetEngagementInteractionsAsync()).Should().BeEmpty();

        var missingInteraction = () => primary.RecordEngagementInteractionAsync(new EngagementInteractionWriteDto(
            "video",
            missingId,
            "openDetail"));
        await missingInteraction.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        var directLikeInteraction = () => primary.RecordEngagementInteractionAsync(new EngagementInteractionWriteDto(
            "video",
            primaryVideo.Id,
            "likeCount"));
        await directLikeInteraction.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        var invalidInteractionRead = () => primary.GetEngagementInteractionsAsync("unsupported", primaryVideo.Id);
        await invalidInteractionRead.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        (await primary.GetEngagementInteractionsAsync()).Select(interaction => interaction.Id)
            .Should().Equal(primaryInteractions.Select(interaction => interaction.Id));
    }

    [Fact]
    [CoversEndpoint("POST", "/api/engagement/activity/reset-all")]
    [CoversEndpoint("POST", "/api/engagement/wipe-all")]
    public async Task GivenImplicitAndExplicitSignals_WhenActivityIsResetAndEngagementIsWiped_ThenOnlyCurrentUsersImplicitStateIsRemoved()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var video = await owner.CreateVideoAsync($"Engagement reset video {suffix}");
        var primaryUsername = $"engagement-reset-primary-{suffix}";
        var controlUsername = $"engagement-reset-control-{suffix}";
        const string password = "Engagement reset 123!";
        await owner.CreateUserAsync(new CreateUserRequest(primaryUsername, password, Roles: [BuiltinRoles.Member]));
        await owner.CreateUserAsync(new CreateUserRequest(controlUsername, password, Roles: [BuiltinRoles.Member]));
        using var primarySession = await owner.CreateAuthSessionAsync(primaryUsername, password);
        using var controlSession = await owner.CreateAuthSessionAsync(controlUsername, password);
        var primary = primarySession.Client;
        var control = controlSession.Client;
        var primaryPlaybackSession = Guid.NewGuid();
        var controlPlaybackSession = Guid.NewGuid();

        _ = await primary.UpdateUiPreferencesAsync(new UserUiPreferencesDto(
            Theme: null,
            RatingSystemOptions: null,
            Tracking: new UserTrackingPreferencesDto(
                Enabled: true,
                MinViewSeconds: null,
                ViewCompletionRatio: null,
                MinImageDetailViewSeconds: null,
                MinDerivedLikeSessionSeconds: 0,
                SessionIdleTimeoutSec: 10),
            Videos: null,
            KeybindingOverrides: null));
        await SeedSignalsAsync(primary, video, primaryPlaybackSession, rating: 88, source: "primary");
        await SeedSignalsAsync(control, video, controlPlaybackSession, rating: 33, source: "control");
        await Task.Delay(TimeSpan.FromSeconds(11));
        await primary.RecordEngagementInteractionAsync(new EngagementInteractionWriteDto(
            "video",
            video.Id,
            "pageVisit",
            JsonSerializer.SerializeToElement(new { source = "session-rollover" })));
        var primaryBefore = await primary.GetVideoEngagementAsync(video);
        var controlBefore = await control.GetVideoEngagementAsync(video);
        primaryBefore.IsFavorite.Should().BeTrue();
        primaryBefore.Rating.Should().Be(88);
        primaryBefore.LikeCount.Should().Be(1);
        primaryBefore.PlayDuration.Should().Be(6);
        primaryBefore.ResumeTime.Should().Be(8);
        primaryBefore.PageVisitCount.Should().Be(2);
        primaryBefore.DerivedLikeCount.Should().Be(1);
        (await primary.GetVideoHistoryAsync(video)).Sessions.Should().ContainSingle(session => session.SessionId == primaryPlaybackSession);
        (await control.GetVideoHistoryAsync(video)).Sessions.Should().ContainSingle(session => session.SessionId == controlPlaybackSession);
        (await primary.GetEngagementInteractionsAsync()).Select(interaction => interaction.Kind).Should().Equal("pageVisit", "derivedLike", "pageVisit", "likeCount");
        (await control.GetEngagementInteractionsAsync()).Select(interaction => interaction.Kind).Should().Equal("pageVisit", "likeCount");

        (await primary.ResetAllEngagementActivityAsync()).Should().Be(1);
        var afterReset = await primary.GetVideoEngagementAsync(video);
        AssertExplicitSignals(afterReset, rating: 88);
        AssertClearedActivity(afterReset);
        afterReset.PageVisitCount.Should().Be(2);
        afterReset.DerivedLikeCount.Should().Be(1);
        (await primary.GetVideoHistoryAsync(video)).Sessions.Should().BeEmpty();
        (await primary.GetEngagementInteractionsAsync()).Select(interaction => interaction.Kind).Should().Equal("pageVisit", "derivedLike", "pageVisit", "likeCount");
        (await primary.GetVideoBookmarkAsync(video)).Saved.Should().BeTrue();
        AssertControlUnchanged(await control.GetVideoEngagementAsync(video), controlBefore);
        (await control.GetVideoHistoryAsync(video)).Sessions.Should().ContainSingle(session => session.SessionId == controlPlaybackSession);

        var replacementPlaybackSession = Guid.NewGuid();
        await primary.RecordVideoPlaybackAsync(video, replacementPlaybackSession);
        await primary.RecordEngagementInteractionAsync(new EngagementInteractionWriteDto(
            "video",
            video.Id,
            "openDetail",
            JsonSerializer.SerializeToElement(new { source = "after-reset" })));
        (await primary.GetEngagementInteractionsAsync()).Select(interaction => interaction.Kind).Should().Equal("openDetail", "pageVisit", "derivedLike", "pageVisit", "likeCount");

        (await primary.WipeAllEngagementAsync()).Should().Be(1);
        var afterWipe = await primary.GetVideoEngagementAsync(video);
        AssertExplicitSignals(afterWipe, rating: 88);
        AssertClearedActivity(afterWipe);
        afterWipe.PageVisitCount.Should().Be(0);
        afterWipe.DerivedLikeCount.Should().Be(0);
        (await primary.GetVideoHistoryAsync(video)).Sessions.Should().BeEmpty();
        var afterWipeInteractions = await primary.GetEngagementInteractionsAsync();
        afterWipeInteractions.Should().ContainSingle();
        afterWipeInteractions.Single().Kind.Should().Be("likeCount");
        afterWipeInteractions.Single().HostId.Should().Be(video.Id);
        (await primary.GetVideoBookmarkAsync(video)).Saved.Should().BeTrue();

        AssertControlUnchanged(await control.GetVideoEngagementAsync(video), controlBefore);
        (await control.GetVideoHistoryAsync(video)).Sessions.Should().ContainSingle(session => session.SessionId == controlPlaybackSession);
        (await control.GetEngagementInteractionsAsync()).Select(interaction => interaction.Kind).Should().Equal("pageVisit", "likeCount");
        (await control.GetVideoBookmarkAsync(video)).Saved.Should().BeTrue();
    }

    private static async Task SeedSignalsAsync(CoveClient client, VideoDto video, Guid sessionId, int rating, string source)
    {
        _ = await client.SetVideoFavoriteAsync(video, isFavorite: true);
        _ = await client.SetVideoRatingAsync(video, rating);
        _ = await client.SetVideoBookmarkAsync(video, isSaved: true);
        (await client.IncrementVideoLikeAsync(video)).Should().Be(1);
        await client.RecordVideoPlaybackAsync(video, sessionId);
        await client.RecordEngagementInteractionAsync(new EngagementInteractionWriteDto(
            "video",
            video.Id,
            "pageVisit",
            JsonSerializer.SerializeToElement(new { source })));
    }

    private static void AssertSnapshot(
        EntityEngagementDto actual,
        bool isFavorite,
        int? rating,
        int likeCount,
        int pageVisitCount)
    {
        actual.IsFavorite.Should().Be(isFavorite);
        actual.Rating.Should().Be(rating);
        actual.LikeCount.Should().Be(likeCount);
        actual.PageVisitCount.Should().Be(pageVisitCount);
        actual.PlayDuration.Should().Be(0);
        actual.PlayCount.Should().Be(0);
        actual.ResumeTime.Should().Be(0);
    }

    private static void AssertExplicitSignals(EntityEngagementDto actual, int rating)
    {
        actual.IsFavorite.Should().BeTrue();
        actual.Rating.Should().Be(rating);
        actual.LikeCount.Should().Be(1);
    }

    private static void AssertClearedActivity(EntityEngagementDto actual)
    {
        actual.PlayDuration.Should().Be(0);
        actual.PlayCount.Should().Be(0);
        actual.ResumeTime.Should().Be(0);
        actual.LastPlayedAt.Should().BeNull();
        actual.CompleteCount.Should().Be(0);
    }

    private static void AssertControlUnchanged(EntityEngagementDto actual, EntityEngagementDto expected)
    {
        actual.HostId.Should().Be(expected.HostId);
        actual.IsFavorite.Should().Be(expected.IsFavorite);
        actual.Rating.Should().Be(expected.Rating);
        actual.ResumeTime.Should().Be(expected.ResumeTime);
        actual.PlayDuration.Should().Be(expected.PlayDuration);
        actual.PlayCount.Should().Be(expected.PlayCount);
        actual.LastPlayedAt.Should().Be(expected.LastPlayedAt);
        actual.LikeCount.Should().Be(expected.LikeCount);
        actual.DerivedLikeCount.Should().Be(expected.DerivedLikeCount);
        actual.PageVisitCount.Should().Be(expected.PageVisitCount);
        actual.CompleteCount.Should().Be(expected.CompleteCount);
        actual.LastLikedAt.Should().Be(expected.LastLikedAt);
    }
}
