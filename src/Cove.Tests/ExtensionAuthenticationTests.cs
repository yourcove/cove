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
        var first = new ExtensionUserAssertion(
            "com.example.forward-auth",
            "alice",
            "trusted-proxy");
        var second = new ExtensionUserAssertion(
            "com.example.other-auth",
            "mallory",
            "other");

        Assert.True(context.TrySetExtensionUserAssertion(first));
        Assert.False(context.TrySetExtensionUserAssertion(second));
        Assert.True(context.TryGetExtensionUserAssertion(out var actual));
        Assert.Equal(first, actual);
    }

    [Theory]
    [InlineData("", "alice", "trusted-proxy")]
    [InlineData("com.example.forward-auth", "", "trusted-proxy")]
    [InlineData("com.example.forward-auth", "alice", "")]
    public void Invalid_extension_assertions_are_rejected(
        string extensionId,
        string username,
        string method)
    {
        var context = new DefaultHttpContext();

        Assert.False(context.TrySetExtensionUserAssertion(
            new ExtensionUserAssertion(extensionId, username, method)));
        Assert.False(context.TryGetExtensionUserAssertion(out _));
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
            new ExtensionLoginMethod("primary", "Primary", "/api/plugins/a.extension/login", 10)), "local");

        var methods = manager.GetExtensionLoginMethods();

        Assert.Collection(
            methods,
            method =>
            {
                Assert.Equal("primary", method.Id);
                Assert.Equal("a.extension", method.ExtensionId);
                Assert.Equal("/api/plugins/a.extension/login", method.StartUrl);
            },
            method =>
            {
                Assert.Equal("secondary", method.Id);
                Assert.Equal("z.extension", method.ExtensionId);
                Assert.Equal("/api/plugins/z.extension/login", method.StartUrl);
            });
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
