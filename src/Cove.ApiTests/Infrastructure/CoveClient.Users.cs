using System.Net;
using System.Net.Http.Json;
using Cove.Core.Auth;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<UserDto> CreateUserAsync(
        CreateUserRequest user,
        CancellationToken cancellationToken = default)
        => SendAsync<UserDto>(HttpMethod.Post, "/api/users", user, cancellationToken);

    public Task<UserDto> GetUserAsync(
        int id,
        CancellationToken cancellationToken = default)
        => SendAsync<UserDto>(HttpMethod.Get, WithCacheNonce($"/api/users/{id}"), null, cancellationToken);

    public Task<UserDto> UpdateUserAsync(
        int id,
        UpdateUserRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync<UserDto>(HttpMethod.Put, $"/api/users/{id}", request, cancellationToken);

    public Task DeleteUserAsync(
        int id,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(HttpMethod.Delete, $"/api/users/{id}", new { }, cancellationToken);

    public Task<UserDto> SetUserRolesAsync(
        int id,
        IReadOnlyList<string> roles,
        CancellationToken cancellationToken = default)
        => SendAsync<UserDto>(HttpMethod.Post, $"/api/users/{id}/roles", new { roles }, cancellationToken);

    public Task ChangeUserPasswordAsync(
        int id,
        string newPassword,
        CancellationToken cancellationToken = default)
        => SendAsync<object>(HttpMethod.Post, $"/api/users/{id}/password", new { newPassword }, cancellationToken);

    public Task<InviteTokenDto> CreatePendingUserInviteAsync(
        CreateInviteRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync<InviteTokenDto>(HttpMethod.Post, "/api/users/invite", request, cancellationToken);

    public Task<InviteTokenDto> CreateUserInviteAsync(
        int id,
        CancellationToken cancellationToken = default)
        => SendAsync<InviteTokenDto>(HttpMethod.Post, $"/api/users/{id}/invite", new { }, cancellationToken);

    public Task UnlockUserAsync(
        int id,
        CancellationToken cancellationToken = default)
        => SendAsync<object>(HttpMethod.Post, $"/api/users/{id}/unlock", new { }, cancellationToken);

    public Task<IReadOnlyList<ExternalIdentityLinkDto>> GetUserExternalLinksAsync(
        int id,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<ExternalIdentityLinkDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/users/{id}/external-links"),
            null,
            cancellationToken);

    public Task RemoveUserExternalLinkAsync(
        int userId,
        int linkId,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(
            HttpMethod.Delete,
            $"/api/users/{userId}/external-links/{linkId}",
            new { },
            cancellationToken);

    public async Task<HttpStatusCode> TryRemoveUserExternalLinkStatusAsync(
        int userId,
        int linkId,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateHttpClient();
        using var response = await client.DeleteAsync(
            $"/api/users/{userId}/external-links/{linkId}",
            cancellationToken);
        return response.StatusCode;
    }

    public async Task<bool> TryLoginAsync(
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
        return response.StatusCode is HttpStatusCode.OK;
    }
}
