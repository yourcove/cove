using Cove.Api.Controllers;
using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public class AuthLoginCurrentUserPayloadTests
{
    private static readonly object Payload = new { user = "eva" };

    [Fact]
    public async Task Returns_the_me_payload_for_the_issued_token()
    {
        var principal = CovePrincipal.Anonymous();

        var me = await AuthController.MePayloadForIssuedTokenAsync(
            extensionsMayAssertIdentity: false,
            () => Task.FromResult<CovePrincipal?>(principal),
            resolved => Task.FromResult(resolved == principal ? Payload : new object()),
            NullLogger.Instance);

        Assert.Same(Payload, me);
    }

    [Fact]
    public async Task Leaves_me_out_without_resolving_when_an_extension_may_assert_the_identity()
    {
        var resolved = false;

        var me = await AuthController.MePayloadForIssuedTokenAsync(
            extensionsMayAssertIdentity: true,
            () =>
            {
                resolved = true;
                return Task.FromResult<CovePrincipal?>(CovePrincipal.Anonymous());
            },
            _ => Task.FromResult(Payload),
            NullLogger.Instance);

        Assert.Null(me);
        Assert.False(resolved);
    }

    [Fact]
    public async Task Leaves_me_out_when_resolving_the_issued_token_throws()
    {
        var me = await AuthController.MePayloadForIssuedTokenAsync(
            extensionsMayAssertIdentity: false,
            () => throw new InvalidOperationException("token store unavailable"),
            _ => Task.FromResult(Payload),
            NullLogger.Instance);

        Assert.Null(me);
    }

    [Fact]
    public async Task Leaves_me_out_when_building_the_payload_throws()
    {
        var me = await AuthController.MePayloadForIssuedTokenAsync(
            extensionsMayAssertIdentity: false,
            () => Task.FromResult<CovePrincipal?>(CovePrincipal.Anonymous()),
            _ => throw new InvalidOperationException("grants unavailable"),
            NullLogger.Instance);

        Assert.Null(me);
    }

    [Fact]
    public async Task Leaves_me_out_when_the_issued_token_does_not_resolve()
    {
        var me = await AuthController.MePayloadForIssuedTokenAsync(
            extensionsMayAssertIdentity: false,
            () => Task.FromResult<CovePrincipal?>(null),
            _ => Task.FromResult(Payload),
            NullLogger.Instance);

        Assert.Null(me);
    }

    [Fact]
    public void Extensions_may_not_assert_the_identity_without_an_extension_manager()
    {
        Assert.False(AuthController.ExtensionsMayAssertIdentity(CreateContext(manager: null)));
    }

    [Fact]
    public void Extensions_may_not_assert_the_identity_when_no_middleware_extension_is_registered()
    {
        var manager = CreateManager();
        manager.Register(new PlainExtension(), "test");

        Assert.False(AuthController.ExtensionsMayAssertIdentity(CreateContext(manager)));
    }

    [Fact]
    public void Extensions_may_assert_the_identity_when_a_middleware_extension_is_enabled()
    {
        var manager = CreateManager();
        manager.Register(new PassThroughMiddlewareExtension(), "test");

        Assert.True(AuthController.ExtensionsMayAssertIdentity(CreateContext(manager)));
    }

    [Fact]
    public async Task Extensions_may_not_assert_the_identity_when_the_middleware_extension_is_disabled()
    {
        var manager = CreateManager();
        var extension = new PassThroughMiddlewareExtension();
        manager.Register(extension, "test");
        await manager.DisableExtensionAsync(extension.Id, TestContext.Current.CancellationToken);

        Assert.False(AuthController.ExtensionsMayAssertIdentity(CreateContext(manager)));
    }

    [Fact]
    public void Extensions_may_assert_the_identity_when_the_request_already_carries_an_assertion()
    {
        var context = CreateContext(manager: null);
        Assert.True(context.TrySetExtensionIdentityAssertion(new ExtensionIdentityAssertion(
            "test.login",
            "test-provider",
            "test-subject",
            "test",
            "Test provider",
            "test-account")));

        Assert.True(AuthController.ExtensionsMayAssertIdentity(context));
    }

    private static ExtensionManager CreateManager() => new(new ExtensionContext
    {
        Configuration = new ConfigurationBuilder().Build(),
        DataDirectory = Path.GetTempPath(),
        CoveVersion = "1.0.0",
    });

    private static DefaultHttpContext CreateContext(ExtensionManager? manager)
    {
        var services = new ServiceCollection();
        if (manager is not null)
            services.AddSingleton(manager);
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    private class PlainExtension : IExtension
    {
        public virtual string Id => "com.example.plain";
        public string Name => "Plain";
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
        {
        }
    }

    private sealed class PassThroughMiddlewareExtension : PlainExtension, IMiddlewareExtension
    {
        public override string Id => "com.example.pass-through-middleware";

        public Task InvokeAsync(HttpContext context, RequestDelegate next) => next(context);
    }
}
