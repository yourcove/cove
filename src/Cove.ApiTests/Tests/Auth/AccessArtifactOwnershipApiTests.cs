using System.Net;
using System.Net.Http.Headers;
using AwesomeAssertions.Execution;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Auth;

[Collection(ApiTestLane1Collection.Name)]
public sealed class AccessArtifactOwnershipApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenAnotherUsersAccessArtifacts_WhenLimitedManagerRevokesTheirIds_ThenBothRemainUsableAndVisible()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var roleName = $"Access artifact manager {suffix}";
        await AsUser().CreateRoleAsync(new CreateRoleRequest(
            roleName,
            "Manages only its own API tokens and share links.",
            [Permissions.ApiTokensWrite, Permissions.ShareLinksWrite, Permissions.VideosRead]));
        var victimUsername = $"access-artifact-victim-{suffix}";
        var limitedUsername = $"access-artifact-attacker-{suffix}";
        const string victimPassword = "Access artifact victim password 123!";
        const string limitedPassword = "Access artifact attacker password 123!";
        await AsUser().CreateUserAsync(new CreateUserRequest(
            victimUsername,
            victimPassword,
            Roles: [roleName]));
        await AsUser().CreateUserAsync(new CreateUserRequest(
            limitedUsername,
            limitedPassword,
            Roles: [roleName]));
        using var victimSession = await AsUser().CreateAuthSessionAsync(victimUsername, victimPassword);
        using var limitedSession = await AsUser().CreateAuthSessionAsync(limitedUsername, limitedPassword);
        var video = await AsUser().CreateVideoAsync($"Access artifact target {suffix}");
        var apiToken = await victimSession.Client.CreateApiTokenAsync(
            $"Victim API token {suffix}",
            [Permissions.VideosRead],
            DateTime.UtcNow.AddHours(1));
        var shareLink = await victimSession.Client.CreateShareLinkAsync(new CreateShareLinkRequest(
            EntityKinds.Video,
            [video.Id.ToString()]));

        using var apiTokenClient = new HttpClient { BaseAddress = ApiUri };
        apiTokenClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.PlaintextToken);
        using var apiTokenBefore = await apiTokenClient.GetAsync(
            $"/api/videos/{video.Id}?apiTestNonce={Guid.NewGuid():N}");
        using var shareLinkClient = new HttpClient { BaseAddress = ApiUri };
        shareLinkClient.DefaultRequestHeaders.Add("X-Share-Token", shareLink.PlaintextToken);
        using var shareLinkBefore = await shareLinkClient.GetAsync(
            $"/api/videos/{video.Id}?apiTestNonce={Guid.NewGuid():N}");
        (await limitedSession.Client.GetApiTokensAsync()).Should().BeEmpty();
        (await limitedSession.Client.GetShareLinksAsync()).Should().BeEmpty();

        await limitedSession.Client.RevokeApiTokenAsync(apiToken.Id);
        await limitedSession.Client.RevokeShareLinkAsync(shareLink.Id);

        using var apiTokenAfter = await apiTokenClient.GetAsync(
            $"/api/videos/{video.Id}?apiTestNonce={Guid.NewGuid():N}");
        using var shareLinkAfter = await shareLinkClient.GetAsync(
            $"/api/videos/{video.Id}?apiTestNonce={Guid.NewGuid():N}");
        var victimTokens = await victimSession.Client.GetApiTokensAsync();
        var victimShareLinks = await victimSession.Client.GetShareLinksAsync();

        using var assertions = new AssertionScope();
        apiTokenBefore.StatusCode.Should().Be(HttpStatusCode.OK);
        shareLinkBefore.StatusCode.Should().Be(HttpStatusCode.OK);
        apiTokenAfter.StatusCode.Should().Be(HttpStatusCode.OK);
        shareLinkAfter.StatusCode.Should().Be(HttpStatusCode.OK);
        victimTokens.Should().ContainSingle(token => token.Id == apiToken.Id);
        victimShareLinks.Should().ContainSingle(link => link.Id == shareLink.Id && !link.Revoked);
    }

    [Fact]
    public async Task GivenOwnedAndMissingAccessArtifacts_WhenLimitedCreatorRevokesRepeatedly_ThenStateAndAuditsAreIdempotent()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var administrator = AsUser();
        var creatorRole = $"Access artifact creator role {suffix}";
        await administrator.CreateRoleAsync(new CreateRoleRequest(
            creatorRole,
            "Creates and revokes its own access artifacts.",
            [Permissions.ApiTokensWrite, Permissions.ShareLinksWrite, Permissions.VideosRead]));
        var creatorUsername = $"access-artifact-creator-{suffix}";
        const string creatorPassword = "Access artifact creator password 123!";
        await administrator.CreateUserAsync(new CreateUserRequest(
            creatorUsername,
            creatorPassword,
            Roles: [creatorRole]));
        using var creatorSession = await administrator.CreateAuthSessionAsync(creatorUsername, creatorPassword);
        var creator = creatorSession.Client;
        var video = await administrator.CreateVideoAsync($"Owned artifact target {suffix}");
        var apiToken = await creator.CreateApiTokenAsync(
            $"Owned API token {suffix}",
            [Permissions.VideosRead],
            DateTime.UtcNow.AddHours(1));
        var shareLink = await creator.CreateShareLinkAsync(new CreateShareLinkRequest(
            EntityKinds.Video,
            [video.Id.ToString()]));
        var missingApiTokenId = Guid.NewGuid();
        var missingShareLinkId = Guid.NewGuid();

        await creator.RevokeApiTokenAsync(apiToken.Id);
        await creator.RevokeApiTokenAsync(apiToken.Id);
        await creator.RevokeApiTokenAsync(missingApiTokenId);
        await creator.RevokeShareLinkAsync(shareLink.Id);
        await creator.RevokeShareLinkAsync(shareLink.Id);
        await creator.RevokeShareLinkAsync(missingShareLinkId);
        await WaitForAuditAsync(AuditActions.ApiTokenRevoke, apiToken.Id);
        await WaitForAuditAsync(AuditActions.ShareLinkRevoke, shareLink.Id);

        using var apiTokenClient = new HttpClient { BaseAddress = ApiUri };
        apiTokenClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.PlaintextToken);
        using var apiTokenAfter = await apiTokenClient.GetAsync($"/api/videos/{video.Id}?apiTestNonce={Guid.NewGuid():N}");
        using var shareLinkClient = new HttpClient { BaseAddress = ApiUri };
        shareLinkClient.DefaultRequestHeaders.Add("X-Share-Token", shareLink.PlaintextToken);
        using var shareLinkAfter = await shareLinkClient.GetAsync($"/api/videos/{video.Id}?apiTestNonce={Guid.NewGuid():N}");
        var tokenAudits = await administrator.GetAuditEventsAsync(AuditActions.ApiTokenRevoke);
        var shareAudits = await administrator.GetAuditEventsAsync(AuditActions.ShareLinkRevoke);

        using var assertions = new AssertionScope();
        apiTokenAfter.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        shareLinkAfter.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await creator.GetApiTokensAsync()).Should().NotContain(token => token.Id == apiToken.Id);
        (await creator.GetShareLinksAsync()).Should().ContainSingle(link => link.Id == shareLink.Id && link.Revoked);
        tokenAudits.Items.Should().ContainSingle(item => item.TargetId == apiToken.Id.ToString() && item.ActorUsername == creatorUsername && item.Outcome == AuditOutcomes.Success);
        tokenAudits.Items.Should().NotContain(item => item.TargetId == missingApiTokenId.ToString());
        shareAudits.Items.Should().ContainSingle(item => item.TargetId == shareLink.Id.ToString() && item.ActorUsername == creatorUsername && item.Outcome == AuditOutcomes.Success);
        shareAudits.Items.Should().NotContain(item => item.TargetId == missingShareLinkId.ToString());
    }

    [Fact]
    public async Task GivenForeignArtifacts_WhenUserAdministratorRevokesThem_ThenOnlyShareLinkIsRevoked()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var owner = AsUser();
        var victimRole = $"Access artifact victim role {suffix}";
        var administratorRole = $"Access artifact administrator role {suffix}";
        await owner.CreateRoleAsync(new CreateRoleRequest(
            victimRole,
            "Creates access artifacts.",
            [Permissions.ApiTokensWrite, Permissions.ShareLinksWrite, Permissions.VideosRead]));
        await owner.CreateRoleAsync(new CreateRoleRequest(
            administratorRole,
            "Administers users and share links.",
            [Permissions.ApiTokensWrite, Permissions.ShareLinksWrite, Permissions.UsersRead, Permissions.VideosRead]));
        var victimUsername = $"access-artifact-admin-victim-{suffix}";
        var administratorUsername = $"access-artifact-administrator-{suffix}";
        const string password = "Access artifact administration password 123!";
        await owner.CreateUserAsync(new CreateUserRequest(victimUsername, password, Roles: [victimRole]));
        await owner.CreateUserAsync(new CreateUserRequest(administratorUsername, password, Roles: [administratorRole]));
        using var victimSession = await owner.CreateAuthSessionAsync(victimUsername, password);
        using var administratorSession = await owner.CreateAuthSessionAsync(administratorUsername, password);
        var video = await owner.CreateVideoAsync($"Administrative artifact target {suffix}");
        var apiToken = await victimSession.Client.CreateApiTokenAsync(
            $"Administrative API token {suffix}",
            [Permissions.VideosRead],
            DateTime.UtcNow.AddHours(1));
        var shareLink = await victimSession.Client.CreateShareLinkAsync(new CreateShareLinkRequest(
            EntityKinds.Video,
            [video.Id.ToString()]));

        await administratorSession.Client.RevokeApiTokenAsync(apiToken.Id);
        await administratorSession.Client.RevokeShareLinkAsync(shareLink.Id);
        var shareAudit = await WaitForAuditAsync(AuditActions.ShareLinkRevoke, shareLink.Id);

        using var apiTokenClient = new HttpClient { BaseAddress = ApiUri };
        apiTokenClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.PlaintextToken);
        using var apiTokenAfter = await apiTokenClient.GetAsync($"/api/videos/{video.Id}?apiTestNonce={Guid.NewGuid():N}");
        using var shareLinkClient = new HttpClient { BaseAddress = ApiUri };
        shareLinkClient.DefaultRequestHeaders.Add("X-Share-Token", shareLink.PlaintextToken);
        using var shareLinkAfter = await shareLinkClient.GetAsync($"/api/videos/{video.Id}?apiTestNonce={Guid.NewGuid():N}");
        var tokenAudits = await owner.GetAuditEventsAsync(AuditActions.ApiTokenRevoke);

        using var assertions = new AssertionScope();
        apiTokenAfter.StatusCode.Should().Be(HttpStatusCode.OK);
        shareLinkAfter.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await victimSession.Client.GetApiTokensAsync()).Should().ContainSingle(token => token.Id == apiToken.Id);
        (await administratorSession.Client.GetShareLinksAsync()).Should().ContainSingle(link => link.Id == shareLink.Id && link.Revoked);
        tokenAudits.Items.Should().NotContain(item => item.TargetId == apiToken.Id.ToString());
        shareAudit.ActorUsername.Should().Be(administratorUsername);
        shareAudit.Outcome.Should().Be(AuditOutcomes.Success);
    }

    private async Task<AuditEventDto> WaitForAuditAsync(string action, Guid targetId)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var page = await AsUser().GetAuditEventsAsync(action);
            var matches = page.Items.Where(item => item.TargetId == targetId.ToString()).ToList();
            if (matches.Count == 1)
                return matches[0];
            if (matches.Count > 1)
                throw new InvalidOperationException($"Expected one {action} audit for the target, but found {matches.Count}.");
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException($"The {action} audit event was not persisted within two seconds.");
    }
}
