using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cove.ApiTests.Infrastructure;
using Cove.ApiTests.Tests.Harness;

namespace Cove.ApiTests.Tests.Auth;

[Collection(SelfHostedApiTestCollection.Name)]
public sealed class FirstRunAuthenticationApiTests
{
    [Fact]
    [CoversEndpoint("POST", "/api/auth/bootstrap-owner")]
    public async Task GivenFreshServer_WhenOwnerIsBootstrapped_ThenTheFirstOwnerSessionIsIssuedOnce()
    {
        await using var server = await CoveApiServer.StartAsync(TestContext.Current.CancellationToken);
        using var browser = new HttpClient { BaseAddress = server.BaseAddress };

        var before = await GetBootstrapStatusAsync(browser, TestContext.Current.CancellationToken);
        before.GetProperty("ownerExists").GetBoolean().Should().BeFalse();
        before.GetProperty("hasSetupToken").GetBoolean().Should().BeFalse();

        var username = $"first-owner-{Guid.NewGuid():N}";
        var login = await PostForJsonAsync(browser, "/api/auth/bootstrap-owner", new { username, password = ApiTestUsers.Password }, HttpStatusCode.OK, TestContext.Current.CancellationToken);
        AssertOwnerLogin(login, username);
        await AssertAuthenticatedOwnerAsync(server.BaseAddress, login.GetProperty("token").GetString()!, username, TestContext.Current.CancellationToken);

        var duplicate = await PostForJsonAsync(browser, "/api/auth/bootstrap-owner", new { username = $"other-{Guid.NewGuid():N}", password = ApiTestUsers.Password }, HttpStatusCode.Conflict, TestContext.Current.CancellationToken);
        duplicate.GetProperty("code").GetString().Should().Be("OWNER_EXISTS");

        var after = await GetBootstrapStatusAsync(browser, TestContext.Current.CancellationToken);
        after.GetProperty("ownerExists").GetBoolean().Should().BeTrue();
        after.GetProperty("hasSetupToken").GetBoolean().Should().BeFalse();
    }

    [Fact]
    [CoversEndpoint("POST", "/api/auth/setup-token-redeem")]
    public async Task GivenProvisionedSetupToken_WhenItIsRedeemed_ThenTokenFirstOwnerSetupIsOneTime()
    {
        await using var server = await CoveApiServer.StartAsync(TestContext.Current.CancellationToken);
        var setupToken = await server.DbUser.CreateSetupTokenAsync(TestContext.Current.CancellationToken);
        using var browser = new HttpClient { BaseAddress = server.BaseAddress };

        var before = await GetBootstrapStatusAsync(browser, TestContext.Current.CancellationToken);
        before.GetProperty("ownerExists").GetBoolean().Should().BeFalse();
        before.GetProperty("hasSetupToken").GetBoolean().Should().BeTrue();

        var blockedBootstrap = await PostForJsonAsync(browser, "/api/auth/bootstrap-owner", new { username = "blocked-owner", password = ApiTestUsers.Password }, HttpStatusCode.Forbidden, TestContext.Current.CancellationToken);
        blockedBootstrap.GetProperty("code").GetString().Should().Be("SETUP_TOKEN_REQUIRED");

        var invalid = await PostForJsonAsync(browser, "/api/auth/setup-token-redeem", new { token = $"invalid-{Guid.NewGuid():N}", password = ApiTestUsers.Password, username = "invalid-owner" }, HttpStatusCode.Gone, TestContext.Current.CancellationToken);
        invalid.GetProperty("code").GetString().Should().Be("TOKEN_EXPIRED");
        (await GetBootstrapStatusAsync(browser, TestContext.Current.CancellationToken)).GetProperty("ownerExists").GetBoolean().Should().BeFalse();

        var username = $"token-owner-{Guid.NewGuid():N}";
        var login = await PostForJsonAsync(browser, "/api/auth/setup-token-redeem", new { token = setupToken, password = ApiTestUsers.Password, username }, HttpStatusCode.OK, TestContext.Current.CancellationToken);
        AssertOwnerLogin(login, username);
        await AssertAuthenticatedOwnerAsync(server.BaseAddress, login.GetProperty("token").GetString()!, username, TestContext.Current.CancellationToken);

        var reused = await PostForJsonAsync(browser, "/api/auth/setup-token-redeem", new { token = setupToken, password = ApiTestUsers.Password, username }, HttpStatusCode.Gone, TestContext.Current.CancellationToken);
        reused.GetProperty("code").GetString().Should().Be("TOKEN_EXPIRED");

        var after = await GetBootstrapStatusAsync(browser, TestContext.Current.CancellationToken);
        after.GetProperty("ownerExists").GetBoolean().Should().BeTrue();
        after.GetProperty("hasSetupToken").GetBoolean().Should().BeFalse();
    }

    [Fact]
    [CoversEndpoint("POST", "/api/system/shutdown")]
    public async Task GivenBootstrappedOwner_WhenShutdownIsRequested_ThenAnonymousCallIsDeniedAndTheHostExits()
    {
        await using var server = await CoveApiServer.StartAsync(TestContext.Current.CancellationToken);
        using var browser = new HttpClient { BaseAddress = server.BaseAddress };
        var username = $"shutdown-owner-{Guid.NewGuid():N}";
        var login = await PostForJsonAsync(browser, "/api/auth/bootstrap-owner", new { username, password = ApiTestUsers.Password }, HttpStatusCode.OK, TestContext.Current.CancellationToken);

        (await PostForStatusAsync(server.BaseAddress, "/api/system/shutdown", TestContext.Current.CancellationToken))
            .Should().Be(HttpStatusCode.Unauthorized);
        (await GetBootstrapStatusAsync(browser, TestContext.Current.CancellationToken)).GetProperty("ownerExists").GetBoolean()
            .Should().BeTrue();

        var result = await PostAuthenticatedForJsonAsync(server.BaseAddress, login.GetProperty("token").GetString()!, "/api/system/shutdown", HttpStatusCode.OK, TestContext.Current.CancellationToken);
        result.GetProperty("message").GetString().Should().Be("Shutdown requested.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.WaitForExitAsync(timeout.Token);
    }

    private static async Task<JsonElement> GetBootstrapStatusAsync(
        HttpClient browser,
        CancellationToken cancellationToken = default)
    {
        using var response = await browser.GetAsync(
            $"/api/auth/bootstrap-status?nonce={Guid.NewGuid():N}",
            cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(
            ApiJson.Options,
            cancellationToken);
    }

    private static async Task<JsonElement> PostForJsonAsync(
        HttpClient browser,
        string route,
        object body,
        HttpStatusCode expectedStatus,
        CancellationToken cancellationToken = default)
    {
        using var response = await browser.PostAsJsonAsync(
            route,
            body,
            ApiJson.Options,
            cancellationToken);
        response.StatusCode.Should().Be(expectedStatus);
        return await response.Content.ReadFromJsonAsync<JsonElement>(
            ApiJson.Options,
            cancellationToken);
    }

    private static async Task<HttpStatusCode> PostForStatusAsync(
        Uri baseAddress,
        string route,
        CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { BaseAddress = baseAddress };
        using var response = await client.PostAsync(route, content: null, cancellationToken);
        return response.StatusCode;
    }

    private static async Task<JsonElement> PostAuthenticatedForJsonAsync(
        Uri baseAddress,
        string accessToken,
        string route,
        HttpStatusCode expectedStatus,
        CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { BaseAddress = baseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.PostAsync(route, content: null, cancellationToken);
        response.StatusCode.Should().Be(expectedStatus);
        return await response.Content.ReadFromJsonAsync<JsonElement>(
            ApiJson.Options,
            cancellationToken);
    }

    private static void AssertOwnerLogin(JsonElement login, string username)
    {
        login.GetProperty("token").GetString().Should().NotBeNullOrWhiteSpace();
        login.GetProperty("refreshToken").GetString().Should().NotBeNullOrWhiteSpace();
        login.GetProperty("username").GetString().Should().Be(username);
        login.GetProperty("user").GetProperty("username").GetString().Should().Be(username);
        login.GetProperty("user").GetProperty("roles")
            .EnumerateArray()
            .Select(role => role.GetString())
            .Should().Equal("Owner");
    }

    private static async Task AssertAuthenticatedOwnerAsync(
        Uri baseAddress,
        string accessToken,
        string username,
        CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { BaseAddress = baseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.GetAsync(
            $"/api/auth/me?nonce={Guid.NewGuid():N}",
            cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var current = await response.Content.ReadFromJsonAsync<JsonElement>(
            ApiJson.Options,
            cancellationToken);
        current.GetProperty("user").GetProperty("username").GetString().Should().Be(username);
    }
}
