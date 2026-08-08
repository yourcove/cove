using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;
using Cove.Data.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Tests.Integration;

public sealed class ExtensionAuthenticationSmokeTests
{
    [Fact]
    public async Task Middleware_extension_assertion_authenticates_through_the_host_principal_resolver()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<CoveConfiguration>().Auth.Enabled = true;
        factory.Services.GetRequiredService<ExtensionManager>()
            .Register(new AssertionMiddlewareExtension("integration-user"), "integration-test");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/auth/me");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("integration-user", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_authoritative_extension_assertion_does_not_replace_a_bearer_principal()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<CoveConfiguration>().Auth.Enabled = true;
        await SeedSecondIdentityAsync(factory);
        factory.Services.GetRequiredService<ExtensionManager>()
            .Register(new AssertionMiddlewareExtension(
                "integration-user-two",
                "integration-subject-two"), "integration-test");
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/api/auth/me");
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "integration-user",
            document.RootElement.GetProperty("user").GetProperty("username").GetString());
        Assert.Contains("\"hasPassword\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"isSystem\":", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authoritative_extension_assertion_replaces_a_stale_cookie_principal()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<CoveConfiguration>().Auth.Enabled = true;
        await SeedSecondIdentityAsync(factory);
        factory.Services.GetRequiredService<ExtensionManager>()
            .Register(new AssertionMiddlewareExtension(
                "integration-user-two",
                "integration-subject-two",
                authoritative: true), "integration-test");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "cove_access_token=integration-test-token");

        using var response = await client.GetAsync("/api/auth/me");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "integration-user-two",
            document.RootElement.GetProperty("user").GetProperty("username").GetString());
    }

    [Fact]
    public async Task Unlinked_authoritative_assertion_clears_a_stale_cookie_principal()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<CoveConfiguration>().Auth.Enabled = true;
        factory.Services.GetRequiredService<ExtensionManager>()
            .Register(new AssertionMiddlewareExtension(
                "unlinked-user",
                "unlinked-subject",
                authoritative: true), "integration-test");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "cove_access_token=integration-test-token");

        using var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authoritative_assertion_for_the_same_user_preserves_api_token_scope()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<CoveConfiguration>().Auth.Enabled = true;
        factory.Services.GetRequiredService<ExtensionManager>()
            .Register(new AssertionMiddlewareExtension(
                "integration-user",
                authoritative: true), "integration-test");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "integration-scoped-token");

        using var response = await client.GetAsync("/api/auth/me");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "apiToken",
            document.RootElement.GetProperty("user").GetProperty("kind").GetString());
        Assert.Equal(
                ["videos.read"],
            document.RootElement.GetProperty("permissions")
                .EnumerateArray()
                .Select(permission => permission.GetString()!)
                .ToArray());
    }

    [Fact]
    public async Task Authoritative_assertion_preserves_explicit_share_link_scope()
    {
        const string shareSecret = "integration-share-secret";
        var shareId = Guid.NewGuid();
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        var configuration = factory.Services.GetRequiredService<CoveConfiguration>();
        configuration.Auth.Enabled = true;
        configuration.Auth.AllowAnonymousShareLinks = true;
        await factory.WithDbContextAsync(async db =>
        {
            db.ShareLinks.Add(new ShareLink
            {
                Id = shareId,
                TokenHash = ShareLinkService.HashToken(shareSecret),
                EntityKind = EntityKinds.Video,
                EntityIds = "[\"1\"]",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        factory.Services.GetRequiredService<ExtensionManager>()
            .Register(new AssertionMiddlewareExtension(
                "unlinked-user",
                "unlinked-subject",
                authoritative: true), "integration-test");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            "X-Share-Token",
            $"cove_share_{shareId:N}_{shareSecret}");

        using var response = await client.GetAsync("/api/auth/me");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "shareLink",
            document.RootElement.GetProperty("user").GetProperty("kind").GetString());
        Assert.Equal(
            "Share link",
            document.RootElement.GetProperty("user").GetProperty("username").GetString());
    }

    [Fact]
    public async Task Login_provider_discovery_is_anonymous_and_returns_only_safe_extension_metadata()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<ExtensionManager>()
            .Register(new InteractiveLoginExtension(), "integration-test");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/auth/external/providers");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("Example SSO", body, StringComparison.Ordinal);
        Assert.Contains("/api/plugins/integration.login/start", body, StringComparison.Ordinal);
        Assert.DoesNotContain("attacker.example", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task External_login_ticket_redeems_through_the_standard_login_response_once()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        string binding;
        ExtensionLoginCompletion completion;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<IExtensionLoginSessionService>();
            var callback = new DefaultHttpContext();
            callback.Request.Scheme = "http";
            callback.Connection.RemoteIpAddress = IPAddress.Loopback;
            binding = sessions.BeginBrowserSession(callback);
            callback.Request.Headers.Cookie = $"cove_external_login_binding={binding}";
            completion = await sessions.CompleteAsync(
                callback,
                binding,
                Identity());
        }
        Assert.Equal(ExtensionLoginCompletionFailure.None, completion.Failure);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"cove_external_login_binding={binding}");
        using var first = await client.PostAsJsonAsync(
            "/api/auth/external/redeem",
            new { code = completion.Code });
        using var second = await client.PostAsJsonAsync(
            "/api/auth/external/redeem",
            new { code = completion.Code });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(first.Headers.CacheControl?.NoStore);
        Assert.Contains("integration-test-access", await first.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.True(second.Headers.CacheControl?.NoStore);
    }

    private sealed class AssertionMiddlewareExtension(
        string accountLabel,
        string subject = "integration-subject",
        bool authoritative = false) : IMiddlewareExtension
    {
        public string Id => $"integration.assertion.{accountLabel}";
        public string Name => "Integration assertion";
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;

        public void ConfigureServices(
            IServiceCollection services,
            ExtensionContext context)
        {
        }

        public Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            context.TrySetExtensionIdentityAssertion(new ExtensionIdentityAssertion(
                "integration.login",
                "integration-provider",
                subject,
                "integration-test",
                "Integration provider",
                accountLabel)
            {
                IsAuthoritative = authoritative,
            });
            return next(context);
        }
    }

    private static Task SeedSecondIdentityAsync(CoveWebApplicationFactory factory) =>
        factory.WithDbContextAsync(async db =>
        {
            db.Users.Add(new User
            {
                Id = CoveWebApplicationFactory.TestExternalUserId,
                Username = "integration-user-two",
                PasswordHash = "integration-test",
                PasswordAlgo = "integration-test",
                IsActive = true,
            });
            db.ExternalIdentityLinks.Add(new ExternalIdentityLink
            {
                UserId = CoveWebApplicationFactory.TestExternalUserId,
                ExtensionId = "integration.login",
                ProviderId = "integration-provider",
                Subject = "integration-subject-two",
                ProviderLabel = "Integration provider",
                AccountLabel = "integration-user-two",
            });
            await db.SaveChangesAsync();
        });

    private static ExtensionIdentityAssertion Identity() => new(
        "integration.login",
        "integration-provider",
        "integration-subject",
        "integration-test",
        "Integration provider",
        "integration-user");

    private sealed class InteractiveLoginExtension : IUIExtension
    {
        public string Id => "integration.login";
        public string Name => "Integration login";
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
        {
        }

        public UIManifest GetUIManifest() => new()
        {
            LoginMethods =
            [
                new("example", "Example SSO", "/api/plugins/integration.login/start"),
                new("unsafe", "Unsafe", "https://attacker.example/login"),
            ],
        };
    }
}
