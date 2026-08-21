using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    internal Task<JsonElement> GetBootstrapStatusAsync(CancellationToken cancellationToken = default)
        => SendAnonymousAsync<JsonElement>(HttpMethod.Get, WithCacheNonce("/api/auth/bootstrap-status"), null, cancellationToken);

    internal Task<JsonElement> GetExternalLoginProvidersAsync(CancellationToken cancellationToken = default)
        => SendAnonymousAsync<JsonElement>(HttpMethod.Get, WithCacheNonce("/api/auth/external/providers"), null, cancellationToken);

    internal Task<JsonElement> GetExternalLinksAsync(CancellationToken cancellationToken = default)
        => SendAsync<JsonElement>(HttpMethod.Get, WithCacheNonce("/api/auth/external/links"), null, cancellationToken);

    internal async Task<InviteTokenInfoDto> GetInviteInfoAsync(string token, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { BaseAddress = BaseAddress };
        using var response = await client.GetAsync($"/api/auth/invite-info?token={Uri.EscapeDataString(token)}", cancellationToken);
        return await ApiResponse.ReadAsync<InviteTokenInfoDto>(response, "GET /api/auth/invite-info", cancellationToken);
    }

    internal async Task<CoveAuthSession> RedeemInviteAsync(
        string token,
        string password,
        string? username,
        CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { BaseAddress = BaseAddress };
        using var response = await client.PostAsJsonAsync(
            "/api/auth/invite-redeem",
            new { token, password, username },
            ApiJson.Options,
            cancellationToken);
        return await ReadSessionAsync(response, "POST /api/auth/invite-redeem", cancellationToken);
    }

    internal async Task<CoveAuthSession> CreateAuthSessionAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { BaseAddress = BaseAddress };
        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(username, password),
            ApiJson.Options,
            cancellationToken);
        return await ReadSessionAsync(response, "POST /api/auth/login", cancellationToken);
    }

    internal async Task<CoveAuthSession> RefreshAuthSessionAsync(
        CoveAuthSession session,
        CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { BaseAddress = BaseAddress };
        using var response = await client.PostAsJsonAsync(
            "/api/auth/refresh",
            new { refreshToken = session.RefreshToken },
            ApiJson.Options,
            cancellationToken);
        return await ReadSessionAsync(response, "POST /api/auth/refresh", cancellationToken);
    }

    internal async Task LogoutAuthSessionAsync(CoveAuthSession session, CancellationToken cancellationToken = default)
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/auth/logout",
            new { refreshToken = session.RefreshToken },
            ApiJson.Options,
            cancellationToken);
        _ = await ApiResponse.ReadAsync<JsonElement>(response, "POST /api/auth/logout", cancellationToken);
    }

    internal Task<JsonElement> RevokeSessionsAsync(CancellationToken cancellationToken = default)
        => SendAsync<JsonElement>(HttpMethod.Post, "/api/auth/revoke-sessions", new { }, cancellationToken);

    internal Task<UserUiPreferencesDto?> UpdateUiPreferencesAsync(
        UserUiPreferencesDto preferences,
        CancellationToken cancellationToken = default)
        => SendAsync<UserUiPreferencesDto?>(HttpMethod.Put, "/api/auth/me/ui-preferences", preferences, cancellationToken);

    internal Task<JsonElement> GetCurrentUserAsync(CancellationToken cancellationToken = default)
        => SendAsync<JsonElement>(HttpMethod.Get, WithCacheNonce("/api/auth/me"), null, cancellationToken);

    internal Task<JsonElement> ChangeOwnPasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
        => SendAsync<JsonElement>(HttpMethod.Post, "/api/auth/change-password", new { currentPassword, newPassword }, cancellationToken);

    internal Task<HttpStatusCode> TryLoginStatusAsync(string username, string password, CancellationToken cancellationToken = default)
        => SendAnonymousForStatusAsync(HttpMethod.Post, "/api/auth/login", new LoginRequest(username, password), cancellationToken);

    internal Task<HttpStatusCode> TryRefreshStatusAsync(string refreshToken, CancellationToken cancellationToken = default)
        => SendAnonymousForStatusAsync(HttpMethod.Post, "/api/auth/refresh", new { refreshToken }, cancellationToken);

    internal Task<HttpStatusCode> TryRedeemInviteStatusAsync(
        string token,
        string password,
        string? username,
        CancellationToken cancellationToken = default)
        => SendAnonymousForStatusAsync(HttpMethod.Post, "/api/auth/invite-redeem", new { token, password, username }, cancellationToken);

    internal async Task<HttpStatusCode> TryChangeOwnPasswordStatusAsync(
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/auth/change-password",
            new { currentPassword, newPassword },
            ApiJson.Options,
            cancellationToken);
        return response.StatusCode;
    }

    private async Task<HttpStatusCode> SendAnonymousForStatusAsync(
        HttpMethod method,
        string requestUri,
        object payload,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = BaseAddress };
        using var request = new HttpRequestMessage(method, requestUri)
        {
            Content = JsonContent.Create(payload, options: ApiJson.Options),
        };
        using var response = await client.SendAsync(request, cancellationToken);
        return response.StatusCode;
    }

    private async Task<T> SendAnonymousAsync<T>(
        HttpMethod method,
        string requestUri,
        object? payload,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = BaseAddress };
        using var request = new HttpRequestMessage(method, requestUri);
        if (payload is not null)
            request.Content = JsonContent.Create(payload, options: ApiJson.Options);
        using var response = await client.SendAsync(request, cancellationToken);
        return await ApiResponse.ReadAsync<T>(response, $"{method} {requestUri}", cancellationToken);
    }

    private async Task<CoveAuthSession> ReadSessionAsync(
        HttpResponseMessage response,
        string requestDescription,
        CancellationToken cancellationToken)
    {
        var login = await ApiResponse.ReadAsync<AuthSessionResponse>(response, requestDescription, cancellationToken);
        if (string.IsNullOrWhiteSpace(login.Token) || string.IsNullOrWhiteSpace(login.RefreshToken))
            throw new InvalidOperationException($"{requestDescription} did not return an authentication session.");
        return new CoveAuthSession(login.User.Username, login.Token, login.RefreshToken, BaseAddress);
    }

    private sealed record AuthSessionResponse(string Token, string RefreshToken, UserDto User);
}

internal sealed class CoveAuthSession : IDisposable
{
    private readonly CoveClient _client;

    internal CoveAuthSession(string username, string accessToken, string refreshToken, Uri baseAddress)
    {
        Username = username;
        RefreshToken = refreshToken;
        _client = new CoveClient(username, baseAddress, accessToken);
    }

    internal string Username { get; }

    internal string RefreshToken { get; }

    internal CoveClient Client => _client;

    public void Dispose() => _client.Dispose();
}
