using System.Net;
using System.Reflection;
using Cove.Api.Controllers;
using Cove.Core.Interfaces;
using Cove.Api.Middleware;
using Cove.Core.Auth;
using Cove.Data.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Cove.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void HashPassword_uses_argon2id_and_verifies()
    {
        var hash = PasswordHasher.HashPassword("correct horse battery staple");

        Assert.StartsWith("$argon2id$", hash, StringComparison.Ordinal);
        Assert.True(PasswordHasher.Verify("correct horse battery staple", hash, PasswordHasher.Algorithm));
        Assert.False(PasswordHasher.Verify("wrong", hash, PasswordHasher.Algorithm));
        Assert.False(PasswordHasher.NeedsRehash(hash, PasswordHasher.Algorithm));
    }

    [Fact]
    public void Verify_supports_bcrypt_and_requests_upgrade()
    {
        var bcryptHash = BCrypt.Net.BCrypt.HashPassword("hunter2", workFactor: 4);

        Assert.True(PasswordHasher.Verify("hunter2", bcryptHash, "bcrypt"));
        Assert.True(PasswordHasher.NeedsRehash(bcryptHash, "bcrypt"));
    }
}

public class AuthDisabledRequestGuardTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.0.0.2", true)]
    [InlineData("172.16.0.5", true)]
    [InlineData("192.168.1.7", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    public void Trusts_expected_ipv4_ranges(string address, bool expected)
    {
        Assert.Equal(expected, AuthDisabledRequestGuard.IsTrustedLocalAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fd12:3456:789a::1", true)]
    [InlineData("2001:4860:4860::8888", false)]
    public void Trusts_expected_ipv6_ranges(string address, bool expected)
    {
        Assert.Equal(expected, AuthDisabledRequestGuard.IsTrustedLocalAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public void Uses_forwarded_for_only_from_configured_public_proxy()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.10");
        context.Request.Host = new HostString("cove.local");
        context.Request.Headers["X-Forwarded-For"] = "8.8.8.8";

        var trustedProxyConfig = new AuthConfig { KnownProxies = ["198.51.100.10"] };
        var untrustedProxyConfig = new AuthConfig();

        Assert.Equal(IPAddress.Parse("8.8.8.8"), AuthDisabledRequestGuard.GetEffectiveRemoteAddress(context, trustedProxyConfig));
        Assert.Equal(IPAddress.Parse("198.51.100.10"), AuthDisabledRequestGuard.GetEffectiveRemoteAddress(context, untrustedProxyConfig));
        Assert.False(AuthDisabledRequestGuard.IsTrustedLocalRequest(context, trustedProxyConfig));
        Assert.False(AuthDisabledRequestGuard.IsTrustedLocalRequest(context, untrustedProxyConfig));
    }

    [Fact]
    public void Uses_forwarded_for_from_trusted_local_proxy_without_known_proxy_configuration()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.10");
        context.Request.Host = new HostString("cove.local");
        context.Request.Headers["X-Forwarded-For"] = "8.8.8.8";

        Assert.Equal(IPAddress.Parse("8.8.8.8"), AuthDisabledRequestGuard.GetEffectiveRemoteAddress(context, new AuthConfig()));
        Assert.False(AuthDisabledRequestGuard.IsTrustedLocalRequest(context, new AuthConfig()));
    }

    [Fact]
    public void Supports_known_proxy_cidr_entries()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.50.25");
        context.Request.Host = new HostString("cove.local");
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.9";

        var config = new AuthConfig { KnownProxies = ["192.168.50.0/24"] };

        Assert.Equal(IPAddress.Parse("203.0.113.9"), AuthDisabledRequestGuard.GetEffectiveRemoteAddress(context, config));
    }

    [Fact]
    public void Trusted_host_allowlist_makes_public_request_trusted_regardless_of_ip()
    {
        // Mirrors a k8s/nginx-ingress deployment: the connection is the private
        // ingress pod, X-Forwarded-For carries the real public client, and the host
        // is a custom public FQDN that would never pass the built-in host check.
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.10");
        context.Request.Host = new HostString("cove.example.com");
        context.Request.Headers["X-Forwarded-For"] = "8.8.8.8";

        Assert.False(AuthDisabledRequestGuard.IsTrustedLocalRequest(context, new AuthConfig()));
        Assert.True(AuthDisabledRequestGuard.IsTrustedLocalRequest(
            context, new AuthConfig { TrustedHosts = ["cove.example.com"] }));
    }

    [Fact]
    public void Trusted_host_allowlist_honors_forwarded_host_header()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        context.Request.Host = new HostString("internal-service");
        context.Request.Headers["X-Forwarded-Host"] = "cove.example.com";

        Assert.True(AuthDisabledRequestGuard.IsTrustedLocalRequest(
            context, new AuthConfig { TrustedHosts = ["cove.example.com"] }));
    }

    [Theory]
    [InlineData("cove.example.com", "*.example.com", true)]
    [InlineData("a.b.example.com", "*.example.com", true)]
    [InlineData("example.com", "*.example.com", false)]
    [InlineData("cove.example.org", "*.example.com", false)]
    [InlineData("anything.test", "*", true)]
    [InlineData("cove.example.com", "other.example.com", false)]
    public void Trusted_host_allowlist_matches_exact_wildcard_and_star(string host, string entry, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        context.Request.Host = new HostString(host);

        Assert.Equal(expected, AuthDisabledRequestGuard.IsTrustedLocalRequest(
            context, new AuthConfig { TrustedHosts = [entry] }));
    }
}

public class AuthorizationSurfaceTests
{
    [Fact]
    public void Http_actions_declare_authorization_policy_or_explicit_anonymous_access()
    {
        var missing = typeof(CurrentPrincipalMiddleware).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any())
                .Where(method => !method.GetCustomAttributes<NonActionAttribute>(inherit: true).Any())
                .Where(method => !HasAuthMarker(type.GetCustomAttributes(inherit: true).OfType<Attribute>())
                    && !HasAuthMarker(method.GetCustomAttributes(inherit: true).OfType<Attribute>()))
                .Select(method => $"{type.Name}.{method.Name}"))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0, "HTTP actions without an authorization marker: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("GET", "/hubs/notifications", true)]
    [InlineData("POST", "/hubs/notifications", true)]
    [InlineData("GET", "/api/stream/video/1/hls/master.m3u8", true)]
    [InlineData("HEAD", "/api/stream/video/1/preview", true)]
    [InlineData("GET", "/api/audios/1/stream", true)]
    [InlineData("GET", "/api/texts/1/file", true)]
    [InlineData("GET", "/api/groups/2/image/front", true)]
    [InlineData("GET", "/api/auth/me", false)]
    [InlineData("GET", "/api/audios/1", false)]
    [InlineData("POST", "/api/stream/video/1", false)]
    public void Query_tokens_are_only_accepted_for_hubs_and_gettable_media_routes(string method, string path, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        Assert.Equal(expected, CurrentPrincipalMiddleware.AllowsQueryToken(context.Request));
    }

    [Theory]
    [InlineData("ai.faces", true)]
    [InlineData("plugin_1-2", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("../outside", false)]
    [InlineData("sub/plugin", false)]
    [InlineData("sub\\plugin", false)]
    public void Plugin_ids_reject_path_traversal_and_nested_paths(string pluginId, bool expected)
    {
        Assert.Equal(expected, PluginsController.IsSafePluginId(pluginId));
    }

    [Fact]
    public void Plugin_directory_resolution_stays_inside_plugin_root()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cove-plugin-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Assert.True(PluginsController.TryResolvePluginDirectory(root, "ai.faces", out var resolved));

            var rootFullPath = Path.GetFullPath(root);
            var expectedPrefix = rootFullPath.EndsWith(Path.DirectorySeparatorChar)
                ? rootFullPath
                : rootFullPath + Path.DirectorySeparatorChar;
            Assert.StartsWith(expectedPrefix, resolved, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

            Assert.False(PluginsController.TryResolvePluginDirectory(root, "..", out _));
            Assert.False(PluginsController.TryResolvePluginDirectory(root, "../outside", out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool HasAuthMarker(IEnumerable<Attribute> attributes)
        => attributes.Any(attribute =>
            attribute is RequiresPermissionAttribute
                or AllowWithoutPermissionAttribute
                or AllowAnonymousAttribute
                or AuthorizeAttribute);
}
