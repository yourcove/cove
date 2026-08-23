using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions.Execution;
using Cove.Api.Controllers;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.Entities;

namespace Cove.ApiTests.Tests.Auth;

[Collection(ApiTestLane1Collection.Name)]
public sealed class AccessArtifactOwnershipApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/apitokens")]
    [CoversEndpoint("DELETE", "/api/apitokens/{id:guid}")]
    public async Task GivenScopedApiTokens_WhenCreatedDeniedAndRevoked_ThenSecretsPermissionsAuditsAndControlsAreExact()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var video = await AsUser().CreateVideoAsync($"API token target {suffix}");
        var expiresAt = DateTime.UtcNow.AddHours(1);
        expiresAt = expiresAt.AddTicks(-(expiresAt.Ticks % TimeSpan.TicksPerSecond));
        var createdAfter = DateTime.UtcNow;

        using var memberClient = AsUser(ApiTestUsers.Eva).CreateHttpClient();
        using var forbiddenCreate = await memberClient.PostAsJsonAsync(
            "/api/apitokens",
            new ApiTokensController.CreateApiTokenRequest(
                $"Forbidden API token {suffix}",
                [Permissions.VideosRead],
                expiresAt));

        var target = await AsUser().CreateApiTokenAsync(
            $"Target API token {suffix}",
            [Permissions.VideosRead],
            expiresAt);
        var control = await AsUser().CreateApiTokenAsync(
            $"Control API token {suffix}",
            [Permissions.VideosRead],
            expiresAt);
        var listedBefore = await AsUser().GetApiTokensAsync();
        var targetBefore = await GetWithApiTokenAsync(target.PlaintextToken, $"/api/videos/{video.Id}");
        var targetOutsideScope = await GetWithApiTokenAsync(target.PlaintextToken, "/api/users");
        var controlBefore = await GetWithApiTokenAsync(control.PlaintextToken, $"/api/videos/{video.Id}");

        using var forbiddenRevoke = await memberClient.DeleteAsync($"/api/apitokens/{target.Id:D}");
        var targetAfterForbidden = await GetWithApiTokenAsync(target.PlaintextToken, $"/api/videos/{video.Id}");

        await AsUser().RevokeApiTokenAsync(target.Id);
        await AsUser().RevokeApiTokenAsync(target.Id);
        await AsUser().RevokeApiTokenAsync(Guid.NewGuid());
        await WaitForAuditAsync(AuditActions.ApiTokenCreate, target.Id);
        await WaitForAuditAsync(AuditActions.ApiTokenCreate, control.Id);
        await WaitForAuditAsync(AuditActions.ApiTokenRevoke, target.Id);

        var listedAfter = await AsUser().GetApiTokensAsync();
        var targetAfterRevoke = await GetWithApiTokenAsync(target.PlaintextToken, $"/api/videos/{video.Id}");
        var controlAfterRevoke = await GetWithApiTokenAsync(control.PlaintextToken, $"/api/videos/{video.Id}");
        var createAudits = (await AsUser().GetAuditEventsAsync(AuditActions.ApiTokenCreate)).Items;
        var revokeAudits = (await AsUser().GetAuditEventsAsync(AuditActions.ApiTokenRevoke)).Items;

        using var assertions = new AssertionScope();
        forbiddenCreate.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        target.Id.Should().NotBeEmpty();
        target.Name.Should().Be($"Target API token {suffix}");
        Regex.IsMatch(target.PlaintextToken, $"^cove_pat_{target.Id:N}_[A-Za-z0-9_-]+$").Should().BeTrue();
        target.Prefix.Should().HaveLength(4);
        target.Scope.Should().Equal(Permissions.VideosRead);
        target.CreatedAt.Should().BeOnOrAfter(createdAfter);
        target.ExpiresAt.Should().Be(expiresAt);
        listedBefore.Should().HaveCount(2);
        var listedTarget = listedBefore.Should().ContainSingle(token => token.Id == target.Id).Which;
        listedTarget.Name.Should().Be(target.Name);
        listedTarget.Prefix.Should().Be(target.Prefix);
        listedTarget.Scope.Should().Equal(Permissions.VideosRead);
        listedTarget.CreatedAt.Should().BeCloseTo(target.CreatedAt, TimeSpan.FromMilliseconds(1));
        listedTarget.ExpiresAt.Should().Be(expiresAt);
        listedBefore.Should().ContainSingle(token => token.Id == control.Id);
        targetBefore.Should().Be(HttpStatusCode.OK);
        targetOutsideScope.Should().Be(HttpStatusCode.Forbidden);
        controlBefore.Should().Be(HttpStatusCode.OK);
        forbiddenRevoke.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        targetAfterForbidden.Should().Be(HttpStatusCode.OK);
        listedAfter.Should().ContainSingle().Which.Id.Should().Be(control.Id);
        targetAfterRevoke.Should().Be(HttpStatusCode.Unauthorized);
        controlAfterRevoke.Should().Be(HttpStatusCode.OK);
        createAudits.Should().HaveCount(2);
        createAudits.Select(audit => audit.TargetId).Should().BeEquivalentTo(
            target.Id.ToString(),
            control.Id.ToString());
        createAudits.Should().OnlyContain(audit =>
            audit.ActorUsername == ApiTestUsers.Owner
            && audit.ActorKind == "user"
            && audit.TargetKind == "api_token"
            && audit.Outcome == AuditOutcomes.Success);
        createAudits.Any(audit =>
            audit.Detail?.Contains(target.PlaintextToken, StringComparison.Ordinal) == true
            || audit.Detail?.Contains(control.PlaintextToken, StringComparison.Ordinal) == true).Should().BeFalse();
        revokeAudits.Should().ContainSingle();
        revokeAudits[0].TargetId.Should().Be(target.Id.ToString());
        revokeAudits[0].ActorUsername.Should().Be(ApiTestUsers.Owner);
        revokeAudits[0].TargetKind.Should().Be("api_token");
        revokeAudits[0].Outcome.Should().Be(AuditOutcomes.Success);
    }

    [Fact]
    public async Task GivenEntityScopedReadGrant_WhenApiTokenScopeIncludesOrExcludesRead_ThenGrantRespectsTokenScope()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var owner = AsUser();
        var role = await owner.CreateRoleAsync(new CreateRoleRequest(
            $"Scoped token entity grant role {suffix}",
            "Creates tokens and reads only explicitly granted entities.",
            [Permissions.ApiTokensWrite]));
        var video = await owner.CreateVideoAsync($"Scoped token entity grant target {suffix}");
        await owner.CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            role.Id,
            EntityKinds.Video,
            video.Id.ToString(),
            "allow",
            "read"));
        var username = $"scoped-token-entity-grant-{suffix}";
        const string password = "Scoped token entity grant password 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [role.Name]));
        using var session = await owner.CreateAuthSessionAsync(username, password);
        var inScope = await session.Client.CreateApiTokenAsync(
            $"In-scope entity grant token {suffix}",
            [Permissions.VideosRead],
            DateTime.UtcNow.AddHours(1));
        var outOfScope = await session.Client.CreateApiTokenAsync(
            $"Out-of-scope entity grant token {suffix}",
            [Permissions.ApiTokensWrite],
            DateTime.UtcNow.AddHours(1));

        var inScopeStatus = await GetWithApiTokenAsync(inScope.PlaintextToken, $"/api/videos/{video.Id}");
        var outOfScopeStatus = await GetWithApiTokenAsync(outOfScope.PlaintextToken, $"/api/videos/{video.Id}");

        using var assertions = new AssertionScope();
        inScopeStatus.Should().Be(HttpStatusCode.OK);
        outOfScopeStatus.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/share-links")]
    [CoversEndpoint("DELETE", "/api/share-links/{id:guid}")]
    public async Task GivenShareLinks_WhenCreatedDeniedAndRevoked_ThenScopePasswordAuditsAndControlsAreExact()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var targetVideo = await AsUser().CreateVideoAsync($"Share link target {suffix}");
        var controlVideo = await AsUser().CreateVideoAsync($"Share link control {suffix}");
        var hiddenVideo = await AsUser().CreateVideoAsync($"Share link hidden {suffix}");
        var restrictedRole = await AsUser().CreateRoleAsync(new CreateRoleRequest(
            $"Restricted share creator {suffix}",
            "Creates share links only for readable videos.",
            [Permissions.ShareLinksWrite, Permissions.VideosRead]));
        await AsUser().CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            restrictedRole.Id,
            EntityKinds.Video,
            hiddenVideo.Id.ToString(),
            "deny",
            "read"));
        var restrictedUsername = $"restricted-share-creator-{suffix}";
        const string restrictedPassword = "Restricted share creator password 123!";
        await AsUser().CreateUserAsync(new CreateUserRequest(
            restrictedUsername,
            restrictedPassword,
            Roles: [restrictedRole.Name]));
        using var restrictedSession = await AsUser().CreateAuthSessionAsync(restrictedUsername, restrictedPassword);
        using var restrictedClient = restrictedSession.Client.CreateHttpClient();
        using var deniedByScope = await restrictedClient.PostAsJsonAsync(
            "/api/share-links",
            new CreateShareLinkRequest(EntityKinds.Video, [hiddenVideo.Id.ToString()]));

        var expiresAt = DateTime.UtcNow.AddHours(1);
        expiresAt = expiresAt.AddTicks(-(expiresAt.Ticks % TimeSpan.TicksPerSecond));
        const string sharePassword = "Share link password 123!";
        var target = await AsUser().CreateShareLinkAsync(new CreateShareLinkRequest(
            " VIDEO ",
            [$" {targetVideo.Id} ", targetVideo.Id.ToString(), " "],
            expiresAt,
            sharePassword));
        var control = await AsUser().CreateShareLinkAsync(new CreateShareLinkRequest(
            EntityKinds.Video,
            [controlVideo.Id.ToString()]));
        var listedBefore = await AsUser().GetShareLinksAsync();

        using var memberClient = AsUser(ApiTestUsers.Eva).CreateHttpClient();
        using var forbiddenCreate = await memberClient.PostAsJsonAsync(
            "/api/share-links",
            new CreateShareLinkRequest(EntityKinds.Video, [controlVideo.Id.ToString()]));
        using var forbiddenRevoke = await memberClient.DeleteAsync($"/api/share-links/{target.Id:D}");

        var missingPassword = await GetWithShareLinkAsync(target.PlaintextToken, targetVideo.Id);
        var wrongPassword = await GetWithShareLinkAsync(target.PlaintextToken, targetVideo.Id, "wrong password");
        var targetBefore = await GetWithShareLinkAsync(target.PlaintextToken, targetVideo.Id, sharePassword);
        var unrelatedBefore = await GetWithShareLinkAsync(target.PlaintextToken, controlVideo.Id, sharePassword);
        var controlBefore = await GetWithShareLinkAsync(control.PlaintextToken, controlVideo.Id);

        await AsUser().RevokeShareLinkAsync(target.Id);
        await AsUser().RevokeShareLinkAsync(target.Id);
        await AsUser().RevokeShareLinkAsync(Guid.NewGuid());
        await WaitForAuditAsync(AuditActions.ShareLinkCreate, target.Id);
        await WaitForAuditAsync(AuditActions.ShareLinkCreate, control.Id);
        await WaitForAuditAsync(AuditActions.ShareLinkRevoke, target.Id);

        var listedAfter = await AsUser().GetShareLinksAsync();
        var targetAfter = await GetWithShareLinkAsync(target.PlaintextToken, targetVideo.Id, sharePassword);
        var controlAfter = await GetWithShareLinkAsync(control.PlaintextToken, controlVideo.Id);
        var createAudits = (await AsUser().GetAuditEventsAsync(AuditActions.ShareLinkCreate)).Items;
        var revokeAudits = (await AsUser().GetAuditEventsAsync(AuditActions.ShareLinkRevoke)).Items;

        using var assertions = new AssertionScope();
        deniedByScope.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await restrictedSession.Client.GetShareLinksAsync()).Should().BeEmpty();
        target.Id.Should().NotBeEmpty();
        Regex.IsMatch(target.PlaintextToken, $"^cove_share_{target.Id:N}_[A-Za-z0-9_-]+$").Should().BeTrue();
        target.EntityKind.Should().Be(EntityKinds.Video);
        target.EntityIds.Should().Equal(targetVideo.Id.ToString());
        target.ExpiresAt.Should().Be(expiresAt);
        target.HasPassword.Should().BeTrue();
        control.HasPassword.Should().BeFalse();
        listedBefore.Should().HaveCount(2);
        var listedTarget = listedBefore.Should().ContainSingle(link => link.Id == target.Id).Which;
        listedTarget.CreatedByUsername.Should().Be(ApiTestUsers.Owner);
        listedTarget.EntityKind.Should().Be(EntityKinds.Video);
        listedTarget.EntityIds.Should().Equal(targetVideo.Id.ToString());
        listedTarget.ExpiresAt.Should().Be(expiresAt);
        listedTarget.HasPassword.Should().BeTrue();
        listedTarget.Revoked.Should().BeFalse();
        listedBefore.Should().ContainSingle(link => link.Id == control.Id && !link.Revoked);
        forbiddenCreate.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        forbiddenRevoke.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        missingPassword.Should().Be(HttpStatusCode.Unauthorized);
        wrongPassword.Should().Be(HttpStatusCode.Unauthorized);
        targetBefore.Should().Be(HttpStatusCode.OK);
        unrelatedBefore.Should().Be(HttpStatusCode.NotFound);
        controlBefore.Should().Be(HttpStatusCode.OK);
        listedAfter.Should().HaveCount(2);
        listedAfter.Should().ContainSingle(link => link.Id == target.Id && link.Revoked);
        listedAfter.Should().ContainSingle(link => link.Id == control.Id && !link.Revoked);
        targetAfter.Should().Be(HttpStatusCode.Unauthorized);
        controlAfter.Should().Be(HttpStatusCode.OK);
        createAudits.Should().HaveCount(2);
        createAudits.Select(audit => audit.TargetId).Should().BeEquivalentTo(
            target.Id.ToString(),
            control.Id.ToString());
        createAudits.Should().OnlyContain(audit =>
            audit.ActorUsername == ApiTestUsers.Owner
            && audit.ActorKind == "user"
            && audit.TargetKind == "share_link"
            && audit.Outcome == AuditOutcomes.Success);
        createAudits.Any(audit =>
            audit.Detail?.Contains(target.PlaintextToken, StringComparison.Ordinal) == true
            || audit.Detail?.Contains(control.PlaintextToken, StringComparison.Ordinal) == true).Should().BeFalse();
        revokeAudits.Should().ContainSingle();
        revokeAudits[0].TargetId.Should().Be(target.Id.ToString());
        revokeAudits[0].ActorUsername.Should().Be(ApiTestUsers.Owner);
        revokeAudits[0].TargetKind.Should().Be("share_link");
        revokeAudits[0].Outcome.Should().Be(AuditOutcomes.Success);
    }

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

    private async Task<HttpStatusCode> GetWithApiTokenAsync(string token, string requestUri)
    {
        using var client = new HttpClient { BaseAddress = ApiUri };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.GetAsync($"{requestUri}{(requestUri.Contains('?') ? '&' : '?')}apiTestNonce={Guid.NewGuid():N}");
        return response.StatusCode;
    }
    private async Task<HttpStatusCode> GetWithShareLinkAsync(
        string token,
        int videoId,
        string? password = null)
    {
        using var client = new HttpClient { BaseAddress = ApiUri };
        client.DefaultRequestHeaders.Add("X-Share-Token", token);
        if (password is not null)
            client.DefaultRequestHeaders.Add("X-Share-Password", password);
        using var response = await client.GetAsync($"/api/videos/{videoId}?apiTestNonce={Guid.NewGuid():N}");
        return response.StatusCode;
    }
}
