using System.Net;
using System.Net.Http.Json;
using Cove.Core.Auth;
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
        factory.Services.GetRequiredService<ExtensionManager>()
            .Register(new AssertionMiddlewareExtension("integration-user"), "integration-test");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/auth/me");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("integration-user", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Built_in_bearer_authentication_takes_precedence_over_an_extension_assertion()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<ExtensionManager>()
            .Register(new AssertionMiddlewareExtension("missing-user"), "integration-test");
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/api/auth/me");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"hasPassword\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"isSystem\":", body, StringComparison.Ordinal);
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

    private sealed class AssertionMiddlewareExtension(string username) : IMiddlewareExtension
    {
        public string Id => $"integration.assertion.{username}";
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
                "integration-subject",
                "integration-test",
                "Integration provider",
                username));
            return next(context);
        }
    }

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
