using Cove.Core.DTOs;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<StudioDto> CreateStudioAsync(
        string name,
        CancellationToken cancellationToken = default)
        => CreateStudioAsync(
            new StudioCreateDto(
                Name: name,
                ParentId: null,
                Rating: null,
                Favorite: false,
                Details: null,
                Organized: false,
                Urls: [],
                Aliases: [],
                TagIds: []),
            cancellationToken);

    public Task<StudioDto> CreateStudioAsync(
        StudioCreateDto studio,
        CancellationToken cancellationToken = default)
        => SendAsync<StudioDto>(HttpMethod.Post, "/api/studios", studio, cancellationToken);

    public Task<StudioDto> GetStudioByIdAsync(
        int studioId,
        CancellationToken cancellationToken = default)
        => SendAsync<StudioDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/studios/{studioId}"),
            payload: null,
            cancellationToken);

    public async Task<IReadOnlyList<StudioDto>> GetStudiosAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<PaginatedResponse<StudioDto>>(
            HttpMethod.Get,
            WithCacheNonce("/api/studios?perPage=250"),
            payload: null,
            cancellationToken);
        return result.Items;
    }

    public Task<StudioDto> UpdateStudioAsync(
        int studioId,
        object update,
        CancellationToken cancellationToken = default)
        => SendAsync<StudioDto>(
            HttpMethod.Put,
            $"/api/studios/{studioId}",
            update,
            cancellationToken);

    public async Task DeleteStudioAsync(
        int studioId,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"/api/studios/{studioId}";
        using var response = await _client.DeleteAsync(requestUri, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.NoContent)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"DELETE {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
    }
}
