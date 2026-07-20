using System.Net;
using System.Net.Sockets;
using Cove.Core.Interfaces;

namespace Cove.Api.Middleware;

public static class AuthDisabledRequestGuard
{
    public static bool IsTrustedLocalRequest(HttpContext context, AuthConfig authConfig)
    {
        var effectiveHost = GetEffectiveHost(context);

        // An operator can explicitly allowlist the hostname(s) they serve Cove on so
        // they can intentionally run with auth disabled behind a trusted reverse proxy
        // (e.g. nginx ingress) on a custom public domain. A matching host is trusted
        // regardless of client IP, since behind such a proxy the effective remote
        // address resolves to the real (public) client and would otherwise never pass.
        if (IsExplicitlyTrustedHost(effectiveHost, authConfig.TrustedHosts))
            return true;

        return IsTrustedLocalAddress(GetEffectiveRemoteAddress(context, authConfig))
            && IsTrustedLocalHost(effectiveHost);
    }

    public static IPAddress? GetEffectiveRemoteAddress(HttpContext context, AuthConfig authConfig)
    {
        var remoteAddress = NormalizeAddress(context.Connection.RemoteIpAddress);
        if (remoteAddress is null)
            return remoteAddress;

        if (!CanTrustForwardedHeaders(remoteAddress, authConfig.KnownProxies))
            return remoteAddress;

        return GetForwardedAddress(context) ?? remoteAddress;
    }

    private static bool CanTrustForwardedHeaders(IPAddress address, IEnumerable<string> knownProxies)
    {
        return IsKnownProxy(address, knownProxies) || IsTrustedLocalAddress(address);
    }

    private static IPAddress? GetForwardedAddress(HttpContext context)
    {
        foreach (var headerName in new[] { "CF-Connecting-IP", "True-Client-IP", "X-Real-IP" })
        {
            if (IPAddress.TryParse(context.Request.Headers[headerName].ToString().Trim(), out var headerAddress))
                return NormalizeAddress(headerAddress);
        }

        foreach (var forwardedEntry in context.Request.Headers["Forwarded"].ToString().Split(','))
        {
            foreach (var segment in forwardedEntry.Split(';'))
            {
                var trimmed = segment.Trim();
                if (!trimmed.StartsWith("for=", StringComparison.OrdinalIgnoreCase))
                    continue;

                var rawAddress = trimmed[4..].Trim().Trim('"');
                var bracketEnd = rawAddress.IndexOf(']');
                if (rawAddress.StartsWith("[", StringComparison.Ordinal) && bracketEnd > 0)
                    rawAddress = rawAddress[1..bracketEnd];
                else if (rawAddress.Count(ch => ch == ':') == 1)
                    rawAddress = rawAddress[..rawAddress.IndexOf(':')];

                if (IPAddress.TryParse(rawAddress, out var forwardedAddress))
                    return NormalizeAddress(forwardedAddress);
            }
        }

        var forwardedFor = context.Request.Headers["X-Forwarded-For"].ToString();
        foreach (var part in forwardedFor.Split(','))
        {
            if (IPAddress.TryParse(part.Trim(), out var forwardedAddress))
                return NormalizeAddress(forwardedAddress);
        }

        return null;
    }

    private static string? GetEffectiveHost(HttpContext context)
    {
        var forwardedHost = context.Request.Headers["X-Forwarded-Host"].ToString();
        if (!string.IsNullOrWhiteSpace(forwardedHost))
            return forwardedHost.Split(',')[0].Trim();

        foreach (var forwardedEntry in context.Request.Headers["Forwarded"].ToString().Split(','))
        {
            foreach (var segment in forwardedEntry.Split(';'))
            {
                var trimmed = segment.Trim();
                if (trimmed.StartsWith("host=", StringComparison.OrdinalIgnoreCase))
                    return trimmed[5..].Trim().Trim('"');
            }
        }

        return context.Request.Host.Host;
    }

    public static bool IsTrustedLocalAddress(IPAddress? address)
    {
        if (address is null)
            return false;

        address = NormalizeAddress(address);

        if (address is null)
            return false;

        if (IPAddress.IsLoopback(address))
            return true;

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsTrustedIpv4(address),
            AddressFamily.InterNetworkV6 => IsTrustedIpv6(address),
            _ => false,
        };
    }

    private static bool IsTrustedIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();

        return bytes[0] switch
        {
            10 => true,
            172 when bytes[1] is >= 16 and <= 31 => true,
            192 when bytes[1] == 168 => true,
            169 when bytes[1] == 254 => true,
            _ => false,
        };
    }

    private static bool IsTrustedIpv6(IPAddress address)
    {
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            return true;

        var bytes = address.GetAddressBytes();
        return (bytes[0] & 0xfe) == 0xfc;
    }

    private static bool IsExplicitlyTrustedHost(string? rawHost, IEnumerable<string>? trustedHosts)
    {
        if (trustedHosts is null)
            return false;

        var host = NormalizeHost(rawHost);
        if (string.IsNullOrEmpty(host))
            return false;

        foreach (var rawEntry in trustedHosts)
        {
            var entry = rawEntry?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(entry))
                continue;

            if (entry == "*")
                return true;

            if (entry.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = entry[1..]; // ".example.com"
                if (host.EndsWith(suffix, StringComparison.Ordinal) && host.Length > suffix.Length)
                    return true;
                continue;
            }

            if (string.Equals(host, entry, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string? NormalizeHost(string? rawHost)
    {
        if (string.IsNullOrWhiteSpace(rawHost))
            return null;

        var host = rawHost.Trim().Trim('[', ']').ToLowerInvariant();
        var colonIndex = host.LastIndexOf(':');
        if (colonIndex > -1 && host.Count(ch => ch == ':') == 1)
            host = host[..colonIndex];

        return host;
    }

    private static bool IsTrustedLocalHost(string? rawHost)
    {
        if (string.IsNullOrWhiteSpace(rawHost))
            return true;

        var host = NormalizeHost(rawHost);
        if (string.IsNullOrEmpty(host))
            return true;

        if (host is "localhost" or "0.0.0.0" or "::" or "::1")
            return true;

        if (host.EndsWith(".localhost", StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.Ordinal)
            || host.EndsWith(".home.arpa", StringComparison.Ordinal))
            return true;

        if (!host.Contains('.', StringComparison.Ordinal) && !host.Contains(':', StringComparison.Ordinal))
            return true;

        return IPAddress.TryParse(host, out var address) && IsTrustedLocalAddress(address);
    }

    /// <summary>Public entry for matching an address against an IP/CIDR allow-list
    /// (used by trusted reverse-proxy SSO in <see cref="CurrentPrincipalMiddleware"/>).</summary>
    public static bool MatchesProxyList(IPAddress address, IEnumerable<string> proxies) => IsKnownProxy(address, proxies);

    private static bool IsKnownProxy(IPAddress address, IEnumerable<string> knownProxies)
    {
        foreach (var rawEntry in knownProxies ?? [])
        {
            var entry = rawEntry.Trim();
            if (entry.Length == 0)
                continue;

            if (entry.Contains('/', StringComparison.Ordinal))
            {
                if (IsInCidr(address, entry))
                    return true;
                continue;
            }

            if (IPAddress.TryParse(entry, out var proxyAddress)
                && NormalizeAddress(proxyAddress)?.Equals(address) == true)
                return true;
        }

        return false;
    }

    private static bool IsInCidr(IPAddress address, string cidr)
    {
        var separator = cidr.IndexOf('/');
        if (separator <= 0 || separator == cidr.Length - 1)
            return false;

        if (!IPAddress.TryParse(cidr[..separator], out var networkAddress)
            || !int.TryParse(cidr[(separator + 1)..], out var prefixLength))
            return false;

        address = NormalizeAddress(address)!;
        networkAddress = NormalizeAddress(networkAddress)!;

        if (address.AddressFamily != networkAddress.AddressFamily)
            return false;

        var addressBytes = address.GetAddressBytes();
        var networkBytes = networkAddress.GetAddressBytes();
        var totalBits = addressBytes.Length * 8;
        if (prefixLength < 0 || prefixLength > totalBits)
            return false;

        var fullBytes = prefixLength / 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (addressBytes[i] != networkBytes[i])
                return false;
        }

        var remainingBits = prefixLength % 8;
        if (remainingBits == 0)
            return true;

        var mask = (byte)(0xff << (8 - remainingBits));
        return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }

    private static IPAddress? NormalizeAddress(IPAddress? address)
    {
        if (address is null)
            return null;

        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }
}