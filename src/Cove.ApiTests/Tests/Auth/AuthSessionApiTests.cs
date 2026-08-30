using System.Net;
using System.Text.Json;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.Entities.Auth;

namespace Cove.ApiTests.Tests.Auth;

public sealed class AuthSessionApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenTestSessionEndpoint_WhenResetTokenIsMissing_ThenEndpointIsHidden()
    {
        using var client = new HttpClient { BaseAddress = ApiUri };

        using var response = await client.PostAsync(
            $"/health/test-session/{ApiTestUsers.Eva}",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [CoversEndpoint("GET", "/api/auth/bootstrap-status")]
    [CoversEndpoint("GET", "/api/auth/external/providers")]
    [CoversEndpoint("GET", "/api/auth/external/links")]
    public async Task GivenProvisionedApi_WhenAuthStatusAndEmptyExternalStateAreRead_ThenTheyDescribeTheCurrentUserAndServer()
    {
        var owner = AsUser();

        var status = await owner.GetBootstrapStatusAsync(TestContext.Current.CancellationToken);
        var providers = await owner.GetExternalLoginProvidersAsync(TestContext.Current.CancellationToken);
        var links = await owner.GetExternalLinksAsync(TestContext.Current.CancellationToken);

        status.GetProperty("ownerExists").GetBoolean().Should().BeTrue();
        status.GetProperty("authEnabled").GetBoolean().Should().BeTrue();
        status.GetProperty("hasSetupToken").GetBoolean().Should().BeFalse();
        providers.ValueKind.Should().Be(JsonValueKind.Array);
        providers.GetArrayLength().Should().Be(0);
        links.ValueKind.Should().Be(JsonValueKind.Array);
        links.GetArrayLength().Should().Be(0);
    }

    [Fact]
    [CoversEndpoint("GET", "/api/auth/invite-info")]
    [CoversEndpoint("POST", "/api/auth/invite-redeem")]
    public async Task GivenPendingInvite_WhenAnonymousRecipientInspectsAndRedeemsIt_ThenAccountIsCreatedAndTheTokenCannotBeReused()
    {
        var owner = AsUser();
        var username = $"invite-redeem-{Guid.NewGuid():N}";
        const string password = "Invited password 123!";
        var invite = await owner.CreatePendingUserInviteAsync(new CreateInviteRequest(
            Username: username,
            DisplayName: "Invited API user",
            Roles: [BuiltinRoles.Member]), TestContext.Current.CancellationToken);

        var info = await owner.GetInviteInfoAsync(invite.Token, TestContext.Current.CancellationToken);
        using var redeemed = await owner.RedeemInviteAsync(invite.Token, password, username, TestContext.Current.CancellationToken);

        info.Valid.Should().BeTrue();
        info.UsernameRequired.Should().BeFalse();
        info.Username.Should().Be(username);
        info.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
        redeemed.Username.Should().Be(username);
        var redeemedUser = (await redeemed.Client.GetCurrentUserAsync(TestContext.Current.CancellationToken)).GetProperty("user");
        redeemedUser.GetProperty("username").GetString().Should().Be(username);
        redeemedUser.GetProperty("roles").EnumerateArray().Select(role => role.GetString()).Should().Contain(BuiltinRoles.Member);
        (await owner.GetUserAsync(int.Parse(redeemedUser.GetProperty("id").GetString()!), TestContext.Current.CancellationToken)).DisplayName.Should().Be("Invited API user");
        (await owner.TryRedeemInviteStatusAsync(invite.Token, password, username, TestContext.Current.CancellationToken)).Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/auth/login")]
    [CoversEndpoint("POST", "/api/auth/refresh")]
    [CoversEndpoint("POST", "/api/auth/logout")]
    [CoversEndpoint("POST", "/api/auth/revoke-sessions")]
    public async Task GivenMemberSessions_WhenTheyRotateLogoutAndRevoke_ThenOnlyThatMembersRefreshTokensAreInvalidated()
    {
        var owner = AsUser();
        (await owner.TryLoginStatusAsync("no-such-user", "wrong password", TestContext.Current.CancellationToken)).Should().Be(HttpStatusCode.Unauthorized);
        (await owner.TryLoginStatusAsync(ApiTestUsers.Eva, "wrong password", TestContext.Current.CancellationToken)).Should().Be(HttpStatusCode.Unauthorized);

        using var initial = await owner.CreateAuthSessionAsync(ApiTestUsers.Eva, ApiTestUsers.Password, TestContext.Current.CancellationToken);
        using var rotated = await owner.RefreshAuthSessionAsync(initial, TestContext.Current.CancellationToken);
        var refreshTokenRotated = !string.Equals(rotated.RefreshToken, initial.RefreshToken, StringComparison.Ordinal);
        refreshTokenRotated.Should().BeTrue();
        rotated.Username.Should().Be(ApiTestUsers.Eva);
        await rotated.Client.LogoutAuthSessionAsync(rotated, TestContext.Current.CancellationToken);
        (await owner.TryRefreshStatusAsync(rotated.RefreshToken, TestContext.Current.CancellationToken)).Should().Be(HttpStatusCode.Unauthorized);
        var loggedOutIdentity = () => rotated.Client.GetCurrentUserAsync();
        await loggedOutIdentity.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 401 (Unauthorized)*");

        using var revocable = await owner.CreateAuthSessionAsync(ApiTestUsers.Eva, ApiTestUsers.Password, TestContext.Current.CancellationToken);
        await revocable.Client.RevokeSessionsAsync(TestContext.Current.CancellationToken);
        (await owner.TryRefreshStatusAsync(revocable.RefreshToken, TestContext.Current.CancellationToken)).Should().Be(HttpStatusCode.Unauthorized);
        var revokedIdentity = () => revocable.Client.GetCurrentUserAsync();
        await revokedIdentity.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 401 (Unauthorized)*");
        (await owner.GetCurrentUserAsync(TestContext.Current.CancellationToken)).GetProperty("user").GetProperty("username").GetString().Should().Be(ApiTestUsers.Owner);
    }

    [Fact]
    [CoversEndpoint("PUT", "/api/auth/me/ui-preferences")]
    [CoversEndpoint("POST", "/api/auth/change-password")]
    public async Task GivenMember_WhenPreferencesAndPasswordAreChanged_ThenPreferencesPersistAndOnlyThatMembersOldSessionsAreRevoked()
    {
        var owner = AsUser();
        var eva = AsUser(ApiTestUsers.Eva);
        var preferences = new UserUiPreferencesDto(
            Theme: null,
            RatingSystemOptions: null,
            Tracking: null,
            Videos: new UserVideosPreferencesDto(IncludeCompilationGroups: true),
            KeybindingOverrides: new Dictionary<string, string> { ["playPause"] = "Space" });

        var updated = await eva.UpdateUiPreferencesAsync(preferences, TestContext.Current.CancellationToken);
        var currentUser = await eva.GetCurrentUserAsync(TestContext.Current.CancellationToken);
        updated.Should().BeEquivalentTo(preferences);
        currentUser.GetProperty("user").GetProperty("uiPreferences").GetProperty("videos").GetProperty("includeCompilationGroups").GetBoolean().Should().BeTrue();
        currentUser.GetProperty("user").GetProperty("uiPreferences").GetProperty("keybindingOverrides").GetProperty("playPause").GetString().Should().Be("Space");

        (await eva.TryChangeOwnPasswordStatusAsync("wrong password", "Replacement password 123!", TestContext.Current.CancellationToken)).Should().Be(HttpStatusCode.BadRequest);
        using var oldSession = await owner.CreateAuthSessionAsync(ApiTestUsers.Eva, ApiTestUsers.Password, TestContext.Current.CancellationToken);
        await oldSession.Client.ChangeOwnPasswordAsync(ApiTestUsers.Password, "Replacement password 123!", TestContext.Current.CancellationToken);
        (await owner.TryRefreshStatusAsync(oldSession.RefreshToken, TestContext.Current.CancellationToken)).Should().Be(HttpStatusCode.Unauthorized);
        (await owner.TryLoginStatusAsync(ApiTestUsers.Eva, ApiTestUsers.Password, TestContext.Current.CancellationToken)).Should().Be(HttpStatusCode.Unauthorized);
        using var replacement = await owner.CreateAuthSessionAsync(ApiTestUsers.Eva, "Replacement password 123!", TestContext.Current.CancellationToken);
        replacement.Username.Should().Be(ApiTestUsers.Eva);
        (await owner.GetCurrentUserAsync(TestContext.Current.CancellationToken)).GetProperty("user").GetProperty("username").GetString().Should().Be(ApiTestUsers.Owner);
    }
}
