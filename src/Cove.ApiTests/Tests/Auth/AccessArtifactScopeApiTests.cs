using System.Net;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Auth;

[Collection(ApiTestLane1Collection.Name)]
public sealed class AccessArtifactScopeApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenLimitedUserApiTokens_WhenScopesAndLifecyclesAreExercised_ThenTokensNeverExpandOrBypassContentRules()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var hiddenTag = await owner.CreateTagAsync($"Token-hidden tag {suffix}", TestContext.Current.CancellationToken);
        var visible = await owner.CreateVideoAsync($"Token-visible video {suffix}", TestContext.Current.CancellationToken);
        var hidden = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Token-hidden video {suffix}")
            .WithTags([hiddenTag])
            .Build(), TestContext.Current.CancellationToken);
        var roleName = $"Scoped token role {suffix}";
        var role = await owner.CreateRoleAsync(new CreateRoleRequest(
            roleName,
            "Creates tokens constrained by user and content permissions.",
            [Permissions.ApiTokensWrite, Permissions.VideosRead]), TestContext.Current.CancellationToken);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            role.Id, EntityKinds.Video, "deny", "tag", $"{{\"tagId\":{hiddenTag.Id}}}", "read"), TestContext.Current.CancellationToken);

        var username = $"scoped-token-{suffix}";
        const string password = "Scoped token password 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]), TestContext.Current.CancellationToken);
        using var userSession = await owner.CreateAuthSessionAsync(username, password, TestContext.Current.CancellationToken);

        var escalation = () => userSession.Client.CreateApiTokenAsync(
            $"Forbidden escalation {suffix}",
            [Permissions.VideosRead, Permissions.UsersRead],
            DateTime.UtcNow.AddHours(1));
        await escalation.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        var blankScope = () => userSession.Client.CreateApiTokenAsync(
            $"Blank scope {suffix}", ["", "   "], DateTime.UtcNow.AddHours(1));
        await blankScope.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await userSession.Client.GetApiTokensAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();

        var issued = await userSession.Client.CreateApiTokenAsync($"Scoped token {suffix}", [Permissions.VideosRead], DateTime.UtcNow.AddHours(1), TestContext.Current.CancellationToken);
        var tokenUser = AsUser(issued);
        await tokenUser.AssertResponseAsync($"/api/videos/{visible.Id}", cancellationToken: TestContext.Current.CancellationToken);
        await tokenUser.AssertResponseAsync($"/api/videos/{hidden.Id}", HttpStatusCode.NotFound, TestContext.Current.CancellationToken);
        await tokenUser.AssertResponseAsync("/api/videos/2147483647", HttpStatusCode.NotFound, TestContext.Current.CancellationToken);
        await tokenUser.AssertResponseAsync("/api/users", HttpStatusCode.Forbidden, TestContext.Current.CancellationToken);

        await userSession.Client.RevokeApiTokenAsync(issued.Id, TestContext.Current.CancellationToken);
        await tokenUser.AssertResponseAsync($"/api/videos/{visible.Id}", HttpStatusCode.Unauthorized, TestContext.Current.CancellationToken);
        await tokenUser.AssertResponseAsync("/api/videos/2147483647", HttpStatusCode.Unauthorized, TestContext.Current.CancellationToken);

        var expired = await userSession.Client.CreateApiTokenAsync($"Expired token {suffix}", [Permissions.VideosRead], DateTime.UtcNow.AddMinutes(-1), TestContext.Current.CancellationToken);
        var expiredUser = AsUser(expired);
        await expiredUser.AssertResponseAsync($"/api/videos/{visible.Id}", HttpStatusCode.Unauthorized, TestContext.Current.CancellationToken);
        await expiredUser.AssertResponseAsync("/api/videos/2147483647", HttpStatusCode.Unauthorized, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GivenShareLinks_WhenViewingAndNonViewingRoutesAreRequested_ThenOnlyTheViewingBundleIsAvailable()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var target = await owner.CreateVideoAsync($"Shared target video {suffix}", TestContext.Current.CancellationToken);
        var unrelated = await owner.CreateVideoAsync($"Unrelated shared video {suffix}", TestContext.Current.CancellationToken);
        await owner.UploadVideoImageAsync(target, ApiTestImages.OnePixelPng(), cancellationToken: TestContext.Current.CancellationToken);
        const string password = "Scoped share password 123!";
        var share = await owner.CreateShareLinkAsync(new CreateShareLinkRequest(
            EntityKinds.Video, [target.Id.ToString()], DateTime.UtcNow.AddHours(1), password), TestContext.Current.CancellationToken);
        var viewer = AsShareLink(share, password);

        await viewer.AssertResponseAsync($"/api/videos/{target.Id}", cancellationToken: TestContext.Current.CancellationToken);
        await viewer.AssertResponseAsync($"/api/videos/{target.Id}/image", cancellationToken: TestContext.Current.CancellationToken);
        await viewer.AssertResponseAsync($"/api/videos/{unrelated.Id}", HttpStatusCode.NotFound, TestContext.Current.CancellationToken);
        await viewer.AssertResponseAsync("/api/videos/2147483647", HttpStatusCode.NotFound, TestContext.Current.CancellationToken);
        await viewer.AssertResponseAsync($"/api/videos/{target.Id}/history", HttpStatusCode.Forbidden, TestContext.Current.CancellationToken);
        await viewer.AssertResponseAsync(HttpMethod.Post, $"/api/videos/{target.Id}/like", HttpStatusCode.Forbidden, cancellationToken: TestContext.Current.CancellationToken);
        await viewer.AssertResponseAsync("/api/search/global?q=shared", HttpStatusCode.Forbidden, TestContext.Current.CancellationToken);
        await viewer.AssertResponseAsync(HttpMethod.Post, "/api/videos/aggregate", HttpStatusCode.Forbidden, new FilteredQueryRequest<VideoFilter> { Ids = [target.Id] }, TestContext.Current.CancellationToken);
        await viewer.AssertResponseAsync("/api/users", HttpStatusCode.Forbidden, TestContext.Current.CancellationToken);

        var group = await owner.CreateGroupAsync($"Shared group {suffix}", TestContext.Current.CancellationToken);
        await owner.CreateGroupItemAsync(group.Id, new GroupItemCreateDto(
            0, GroupItemKind.Video, target.Id, EntityKinds.Video, target.Id,
            null, null, null, null, null, null), TestContext.Current.CancellationToken);
        var groupShare = await owner.CreateShareLinkAsync(new CreateShareLinkRequest(
            EntityKinds.Group, [group.Id.ToString()]), TestContext.Current.CancellationToken);
        var groupViewer = AsShareLink(groupShare);
        (await groupViewer.GetGroupItemsPageAsync(group.Id, page: 1, perPage: 25, cancellationToken: TestContext.Current.CancellationToken)).Should().Match<PaginatedResponse<GroupItemDto>>(
            page => page.TotalCount == 1 && page.Items.Count == 1 && page.Items[0].HostId == target.Id);
        (await groupViewer.GetGroupPlaybackManifestAsync(group.Id, TestContext.Current.CancellationToken)).Items.Should()
            .ContainSingle(item => item.HostType == EntityKinds.Video && item.HostId == target.Id);

        var hiddenTag = await owner.CreateTagAsync($"Share-hidden tag {suffix}", TestContext.Current.CancellationToken);
        var hiddenChild = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Share-hidden child {suffix}")
            .WithTags([hiddenTag])
            .Build(), TestContext.Current.CancellationToken);
        await owner.CreateGroupItemAsync(group.Id, new GroupItemCreateDto(
            1, GroupItemKind.Video, hiddenChild.Id, EntityKinds.Video, hiddenChild.Id,
            null, null, null, null, null, null), TestContext.Current.CancellationToken);
        await owner.CreateGroupItemAsync(group.Id, new GroupItemCreateDto(
            2, GroupItemKind.VideoRange, unrelated.Id, EntityKinds.Video, unrelated.Id,
            1, 2, null, null, null, null), TestContext.Current.CancellationToken);
        var sharerRoleName = $"Limited sharer {suffix}";
        var sharerRole = await owner.CreateRoleAsync(new CreateRoleRequest(
            sharerRoleName,
            "Shares containers without expanding hidden children.",
            [Permissions.ShareLinksWrite, Permissions.GroupsRead, Permissions.VideosRead]), TestContext.Current.CancellationToken);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            sharerRole.Id, EntityKinds.Video, "deny", "tag", $"{{\"tagId\":{hiddenTag.Id}}}", "read"), TestContext.Current.CancellationToken);
        var sharerUsername = $"limited-sharer-{suffix}";
        const string sharerPassword = "Limited sharer password 123!";
        await owner.CreateUserAsync(new CreateUserRequest(sharerUsername, sharerPassword, Roles: [sharerRoleName]), TestContext.Current.CancellationToken);
        using var sharerSession = await owner.CreateAuthSessionAsync(sharerUsername, sharerPassword, TestContext.Current.CancellationToken);
        var limitedGroupShare = await sharerSession.Client.CreateShareLinkAsync(new CreateShareLinkRequest(
            EntityKinds.Group, [group.Id.ToString()]), TestContext.Current.CancellationToken);
        var limitedViewer = AsShareLink(limitedGroupShare);
        var limitedPage = await limitedViewer.GetGroupItemsPageAsync(group.Id, page: 1, perPage: 25, cancellationToken: TestContext.Current.CancellationToken);
        limitedPage.Items.Should().ContainSingle(item => item.HostId == target.Id);
        await limitedViewer.AssertResponseAsync($"/api/videos/{hiddenChild.Id}", HttpStatusCode.NotFound, TestContext.Current.CancellationToken);
        await limitedViewer.AssertResponseAsync($"/api/videos/{unrelated.Id}", HttpStatusCode.NotFound, TestContext.Current.CancellationToken);

        var audio = await owner.CreateAudioAsync($"Shared audio {suffix}", TestContext.Current.CancellationToken);
        var text = await owner.CreateTextAsync($"Shared text {suffix}", TestContext.Current.CancellationToken);
        var audioShare = await owner.CreateShareLinkAsync(new CreateShareLinkRequest(EntityKinds.Audio, [audio.Id.ToString()]), TestContext.Current.CancellationToken);
        var textShare = await owner.CreateShareLinkAsync(new CreateShareLinkRequest(EntityKinds.Text, [text.Id.ToString()]), TestContext.Current.CancellationToken);
        await AsShareLink(audioShare).AssertResponseAsync($"/api/audios/{audio.Id}", cancellationToken: TestContext.Current.CancellationToken);
        await AsShareLink(textShare).AssertResponseAsync($"/api/texts/{text.Id}", cancellationToken: TestContext.Current.CancellationToken);

        var performer = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Shared performer {suffix}").Build(), TestContext.Current.CancellationToken);
        var tag = await owner.CreateTagAsync($"Shared tag {suffix}", TestContext.Current.CancellationToken);
        var studio = await owner.CreateStudioAsync($"Shared studio {suffix}", TestContext.Current.CancellationToken);
        var performerShare = await owner.CreateShareLinkAsync(new CreateShareLinkRequest(EntityKinds.Performer, [performer.Id.ToString()]), TestContext.Current.CancellationToken);
        var tagShare = await owner.CreateShareLinkAsync(new CreateShareLinkRequest(EntityKinds.Tag, [tag.Id.ToString()]), TestContext.Current.CancellationToken);
        var studioShare = await owner.CreateShareLinkAsync(new CreateShareLinkRequest(EntityKinds.Studio, [studio.Id.ToString()]), TestContext.Current.CancellationToken);
        await AsShareLink(performerShare).AssertResponseAsync($"/api/performers/{performer.Id}", cancellationToken: TestContext.Current.CancellationToken);
        await AsShareLink(tagShare).AssertResponseAsync($"/api/tags/{tag.Id}", cancellationToken: TestContext.Current.CancellationToken);
        await AsShareLink(studioShare).AssertResponseAsync($"/api/studios/{studio.Id}", cancellationToken: TestContext.Current.CancellationToken);

        await owner.AssertResponseAsync(HttpMethod.Post, "/api/share-links", HttpStatusCode.Forbidden, new CreateShareLinkRequest(EntityKinds.Segment, ["1"]), TestContext.Current.CancellationToken);
        var dynamicGroup = await owner.CreateGroupAsync($"Dynamic shared group {suffix}", TestContext.Current.CancellationToken);
        await owner.UpdateGroupQueryAsync(dynamicGroup.Id, new GroupQueryUpdateDto(
            "filter", "{\"entityTypes\":[\"video\"],\"findFilters\":{}}"), TestContext.Current.CancellationToken);
        await owner.AssertResponseAsync(HttpMethod.Post, "/api/share-links", HttpStatusCode.Forbidden, new CreateShareLinkRequest(EntityKinds.Group, [dynamicGroup.Id.ToString()]), TestContext.Current.CancellationToken);

        await AsShareLink(share).AssertResponseAsync($"/api/videos/{target.Id}", HttpStatusCode.Unauthorized, TestContext.Current.CancellationToken);
        await AsShareLink(share, "wrong password").AssertResponseAsync("/api/videos/2147483647", HttpStatusCode.Unauthorized, TestContext.Current.CancellationToken);
        var expired = await owner.CreateShareLinkAsync(new CreateShareLinkRequest(
            EntityKinds.Video, [target.Id.ToString()], DateTime.UtcNow.AddMinutes(-1)), TestContext.Current.CancellationToken);
        await AsShareLink(expired).AssertResponseAsync($"/api/videos/{target.Id}", HttpStatusCode.Unauthorized, TestContext.Current.CancellationToken);
        await owner.RevokeShareLinkAsync(share.Id, TestContext.Current.CancellationToken);
        await viewer.AssertResponseAsync($"/api/videos/{target.Id}", HttpStatusCode.Unauthorized, TestContext.Current.CancellationToken);

        var audits = (await owner.GetAuditEventsAsync(AuditActions.ShareLinkAccess, TestContext.Current.CancellationToken)).Items;
        audits.Should().Contain(audit => audit.TargetId == groupShare.Id.ToString() && audit.Outcome == AuditOutcomes.Success);
        audits.Should().OnlyContain(audit =>
            !(audit.Detail ?? string.Empty).Contains(share.PlaintextToken, StringComparison.Ordinal)
            && !(audit.Detail ?? string.Empty).Contains(password, StringComparison.Ordinal));
    }
}
