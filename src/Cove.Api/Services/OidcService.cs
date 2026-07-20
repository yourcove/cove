using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Cove.Api.Services;

/// <summary>
/// Native OpenID Connect login (authorization code + PKCE, confidential client).
/// Maps the configured username claim to an existing Cove user; session issuance is
/// handled by the caller so it matches password login exactly.
/// </summary>
public sealed class OidcService
{
    private static readonly TimeSpan FlowTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RedeemTtl = TimeSpan.FromSeconds(60);

    private readonly CoveConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OidcService> _logger;

    private readonly ConcurrentDictionary<string, PendingFlow> _flows = new();
    private readonly ConcurrentDictionary<string, PendingRedeem> _redeems = new();

    private ConfigurationManager<OpenIdConnectConfiguration>? _discovery;
    private string? _discoveryAuthority;
    private readonly object _discoveryLock = new();

    private sealed record PendingFlow(string Nonce, string CodeVerifier, DateTime CreatedUtc);
    private sealed record PendingRedeem(TokenPair Pair, DateTime CreatedUtc);

    public OidcService(CoveConfiguration config, IHttpClientFactory httpClientFactory, ILogger<OidcService> logger)
    {
        _config = config;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool Enabled =>
        _config.Auth.OidcEnabled
        && !string.IsNullOrWhiteSpace(_config.Auth.OidcAuthority)
        && !string.IsNullOrWhiteSpace(_config.Auth.OidcClientId);

    public string ButtonLabel =>
        string.IsNullOrWhiteSpace(_config.Auth.OidcButtonLabel) ? "Single Sign-On" : _config.Auth.OidcButtonLabel;

    /// <summary>Builds the IdP authorize redirect and records the state/nonce/PKCE for the flow.</summary>
    public async Task<string> BuildAuthorizeUrlAsync(string redirectUri, CancellationToken ct)
    {
        var oidcConfig = await GetDiscoveryAsync(ct);

        var state = RandomToken();
        var nonce = RandomToken();
        var verifier = RandomToken() + RandomToken();
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        Sweep(_flows, FlowTtl, static flow => flow.CreatedUtc);
        _flows[state] = new PendingFlow(nonce, verifier, DateTime.UtcNow);

        var scopes = string.IsNullOrWhiteSpace(_config.Auth.OidcScopes) ? "openid profile email" : _config.Auth.OidcScopes;
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = _config.Auth.OidcClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = scopes,
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        return oidcConfig.AuthorizationEndpoint + "?" + string.Join("&",
            query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value ?? string.Empty)}"));
    }

    /// <summary>
    /// Completes the code exchange and returns the username asserted by the IdP,
    /// or null when anything about the flow or token fails validation.
    /// </summary>
    public async Task<string?> CompleteLoginAsync(string code, string state, string redirectUri, CancellationToken ct)
    {
        if (!_flows.TryRemove(state, out var flow) || DateTime.UtcNow - flow.CreatedUtc > FlowTtl)
        {
            _logger.LogWarning("OIDC callback with unknown or expired state");
            return null;
        }

        var oidcConfig = await GetDiscoveryAsync(ct);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = _config.Auth.OidcClientId ?? string.Empty,
            ["code_verifier"] = flow.CodeVerifier,
        };
        if (!string.IsNullOrWhiteSpace(_config.Auth.OidcClientSecret))
            form["client_secret"] = _config.Auth.OidcClientSecret;

        using var response = await _httpClientFactory.CreateClient("oidc")
            .PostAsync(oidcConfig.TokenEndpoint, new FormUrlEncodedContent(form), ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OIDC token endpoint returned {Status}: {Body}", (int)response.StatusCode, Truncate(payload));
            return null;
        }

        string? idToken;
        using (var doc = System.Text.Json.JsonDocument.Parse(payload))
        {
            idToken = doc.RootElement.TryGetProperty("id_token", out var el) ? el.GetString() : null;
        }
        if (string.IsNullOrWhiteSpace(idToken))
        {
            _logger.LogWarning("OIDC token response had no id_token");
            return null;
        }

        var handler = new JsonWebTokenHandler();
        var validation = await handler.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = oidcConfig.Issuer,
            ValidAudience = _config.Auth.OidcClientId,
            IssuerSigningKeys = oidcConfig.SigningKeys,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        });
        if (!validation.IsValid)
        {
            _logger.LogWarning(validation.Exception, "OIDC id_token failed validation");
            return null;
        }

        if (validation.Claims.TryGetValue("nonce", out var tokenNonce)
            && !string.Equals(tokenNonce?.ToString(), flow.Nonce, StringComparison.Ordinal))
        {
            _logger.LogWarning("OIDC id_token nonce mismatch");
            return null;
        }

        var usernameClaim = string.IsNullOrWhiteSpace(_config.Auth.OidcUsernameClaim)
            ? "preferred_username"
            : _config.Auth.OidcUsernameClaim;
        if (!validation.Claims.TryGetValue(usernameClaim, out var raw) || string.IsNullOrWhiteSpace(raw?.ToString()))
        {
            _logger.LogWarning("OIDC id_token missing username claim {Claim}", usernameClaim);
            return null;
        }

        return raw.ToString();
    }

    /// <summary>Stores an issued token pair under a one-time code the SPA redeems right after the redirect.</summary>
    public string StashRedeem(TokenPair pair)
    {
        Sweep(_redeems, RedeemTtl, static redeem => redeem.CreatedUtc);
        var code = RandomToken();
        _redeems[code] = new PendingRedeem(pair, DateTime.UtcNow);
        return code;
    }

    public TokenPair? TakeRedeem(string code)
    {
        if (!_redeems.TryRemove(code, out var redeem) || DateTime.UtcNow - redeem.CreatedUtc > RedeemTtl)
            return null;
        return redeem.Pair;
    }

    private async Task<OpenIdConnectConfiguration> GetDiscoveryAsync(CancellationToken ct)
    {
        var authority = (_config.Auth.OidcAuthority ?? string.Empty).TrimEnd('/');
        lock (_discoveryLock)
        {
            if (_discovery is null || !string.Equals(_discoveryAuthority, authority, StringComparison.Ordinal))
            {
                _discovery = new ConfigurationManager<OpenIdConnectConfiguration>(
                    authority + "/.well-known/openid-configuration",
                    new OpenIdConnectConfigurationRetriever(),
                    new HttpDocumentRetriever(_httpClientFactory.CreateClient("oidc")) { RequireHttps = authority.StartsWith("https", StringComparison.OrdinalIgnoreCase) });
                _discoveryAuthority = authority;
            }
        }
        return await _discovery.GetConfigurationAsync(ct);
    }

    private static void Sweep<T>(ConcurrentDictionary<string, T> store, TimeSpan ttl, Func<T, DateTime> created)
    {
        if (store.Count < 64) return;
        var cutoff = DateTime.UtcNow - ttl;
        foreach (var entry in store)
        {
            if (created(entry.Value) < cutoff)
                store.TryRemove(entry.Key, out _);
        }
    }

    private static string RandomToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300];
}
