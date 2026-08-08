using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Cove.Tests;

public sealed class ExtensionAuthenticationAssertionTests
{
    [Fact]
    public void First_valid_extension_assertion_wins_for_the_request()
    {
        var context = new DefaultHttpContext();
        var first = new ExtensionIdentityAssertion(
            "com.example.forward-auth",
            "proxy-authority",
            "alice-subject",
            "trusted-proxy",
            "Forward auth",
            "alice");
        var second = new ExtensionIdentityAssertion(
            "com.example.other-auth",
            "other-authority",
            "mallory-subject",
            "other",
            "Other",
            "mallory");

        Assert.True(context.TrySetExtensionIdentityAssertion(first));
        Assert.False(context.TrySetExtensionIdentityAssertion(second));
        Assert.True(context.TryGetExtensionIdentityAssertion(out var actual));
        Assert.Equal(first, actual);
    }

    [Theory]
    [InlineData("", "provider", "subject", "trusted-proxy", "Provider")]
    [InlineData("com.example.forward-auth", "", "subject", "trusted-proxy", "Provider")]
    [InlineData("com.example.forward-auth", "provider", "", "trusted-proxy", "Provider")]
    [InlineData("com.example.forward-auth", "provider", "subject", "", "Provider")]
    [InlineData("com.example.forward-auth", "provider", "subject", "trusted-proxy", "")]
    public void Invalid_extension_assertions_are_rejected(
        string extensionId,
        string providerId,
        string subject,
        string method,
        string providerLabel)
    {
        var context = new DefaultHttpContext();

        Assert.False(context.TrySetExtensionIdentityAssertion(
            new ExtensionIdentityAssertion(extensionId, providerId, subject, method, providerLabel)));
        Assert.False(context.TryGetExtensionIdentityAssertion(out _));
    }

    [Fact]
    public void Opaque_subject_is_preserved_exactly()
    {
        var context = new DefaultHttpContext();

        Assert.True(context.TrySetExtensionIdentityAssertion(new ExtensionIdentityAssertion(
            "com.example.forward-auth",
            "provider",
            " subject-with-spaces ",
            "oidc",
            "Provider")));
        Assert.True(context.TryGetExtensionIdentityAssertion(out var assertion));
        Assert.Equal(" subject-with-spaces ", assertion.Subject);
    }

    [Fact]
    public void Legacy_username_assertions_fail_closed()
    {
        var context = new DefaultHttpContext();

#pragma warning disable CS0618
        Assert.False(context.TrySetExtensionUserAssertion(
            new ExtensionUserAssertion("com.example.legacy", "alice", "legacy")));
        Assert.False(context.TryGetExtensionUserAssertion(out _));
#pragma warning restore CS0618
        Assert.False(context.TryGetExtensionIdentityAssertion(out _));
    }
}

public sealed class ExtensionLoginMethodTests
{
    [Fact]
    public void Login_methods_are_stamped_filtered_and_ordered_by_the_host()
    {
        var manager = CreateManager();
        manager.Register(new LoginMethodExtension(
            "z.extension",
            new ExtensionLoginMethod("secondary", "Secondary", "/api/plugins/z.extension/login", 20),
            new ExtensionLoginMethod("unsafe", "Unsafe", "https://attacker.example/login", 1)), "local");
        manager.Register(new LoginMethodExtension(
            "a.extension",
            new ExtensionLoginMethod(
                "primary",
                "Primary",
                "/api/plugins/a.extension/login",
                10,
                LinkStartUrl: "/api/plugins/a.extension/link")), "local");

        var methods = manager.GetExtensionLoginMethods();

        Assert.Collection(
            methods,
            method =>
            {
                Assert.Equal("primary", method.Id);
                Assert.Equal("a.extension", method.ExtensionId);
                Assert.Equal("/api/plugins/a.extension/login", method.StartUrl);
                Assert.Equal("/api/plugins/a.extension/link", method.LinkStartUrl);
                Assert.True(method.ShowOnLoginPage);
            },
            method =>
            {
                Assert.Equal("secondary", method.Id);
                Assert.Equal("z.extension", method.ExtensionId);
                Assert.Equal("/api/plugins/z.extension/login", method.StartUrl);
            });
    }

    [Fact]
    public void Link_only_authentication_method_is_preserved_for_the_account_page()
    {
        var manager = CreateManager();
        manager.Register(new LoginMethodExtension(
            "example.extension",
            new ExtensionLoginMethod(
                "transparent",
                "Trusted proxy",
                "/api/plugins/example.extension/start",
                LinkStartUrl: "/api/plugins/example.extension/link")
            {
                ShowOnLoginPage = false,
            }), "local");

        var method = Assert.Single(manager.GetExtensionLoginMethods());

        Assert.False(method.ShowOnLoginPage);
        Assert.NotNull(method.LinkStartUrl);
    }

    [Fact]
    public void Duplicate_login_method_ids_from_one_extension_are_ignored_after_the_first()
    {
        var manager = CreateManager();
        manager.Register(new LoginMethodExtension(
            "example.extension",
            new ExtensionLoginMethod("oidc", "First", "/api/plugins/example.extension/first", 10),
            new ExtensionLoginMethod("oidc", "Second", "/api/plugins/example.extension/second", 20)), "local");

        var method = Assert.Single(manager.GetExtensionLoginMethods());

        Assert.Equal("First", method.Label);
        Assert.Equal("/api/plugins/example.extension/first", method.StartUrl);
    }

    private static ExtensionManager CreateManager() => new(new ExtensionContext
    {
        Configuration = new ConfigurationBuilder().Build(),
        DataDirectory = Path.GetTempPath(),
        CoveVersion = "1.0.0",
    });

    private sealed class LoginMethodExtension(
        string id,
        params ExtensionLoginMethod[] methods) : IUIExtension
    {
        public string Id => id;
        public string Name => id;
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;

        public void ConfigureServices(
            Microsoft.Extensions.DependencyInjection.IServiceCollection services,
            ExtensionContext context)
        {
        }

        public UIManifest GetUIManifest() => new()
        {
            LoginMethods = [.. methods],
        };
    }
}
