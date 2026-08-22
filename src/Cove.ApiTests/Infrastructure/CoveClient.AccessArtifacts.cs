using Cove.Api.Controllers;
using Cove.Core.Auth;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<ApiTokenIssued> CreateApiTokenAsync(
        string name,
        IReadOnlyList<string>? scope = null,
        DateTime? expiresAt = null,
        CancellationToken cancellationToken = default)
        => SendAsync<ApiTokenIssued>(
            HttpMethod.Post,
            "/api/apitokens",
            new ApiTokensController.CreateApiTokenRequest(name, scope?.ToArray(), expiresAt),
            cancellationToken);

    public Task<IReadOnlyList<ApiTokenDto>> GetApiTokensAsync(
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<ApiTokenDto>>(
            HttpMethod.Get,
            WithCacheNonce("/api/apitokens"),
            payload: null,
            cancellationToken);

    public Task RevokeApiTokenAsync(
        Guid id,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(
            HttpMethod.Delete,
            $"/api/apitokens/{id:D}",
            new { },
            cancellationToken);

    public Task<ShareLinkIssued> CreateShareLinkAsync(
        CreateShareLinkRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync<ShareLinkIssued>(
            HttpMethod.Post,
            "/api/share-links",
            request,
            cancellationToken);

    public Task<IReadOnlyList<ShareLinkDto>> GetShareLinksAsync(
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<ShareLinkDto>>(
            HttpMethod.Get,
            WithCacheNonce("/api/share-links"),
            payload: null,
            cancellationToken);

    public Task RevokeShareLinkAsync(
        Guid id,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(
            HttpMethod.Delete,
            $"/api/share-links/{id:D}",
            new { },
            cancellationToken);
}
