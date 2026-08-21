using System.Text.Json;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;

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

    public Task<StudioDto> GetStudioByIdAtDepthAsync(
        int studioId,
        int depth,
        CancellationToken cancellationToken = default)
        => SendAsync<StudioDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/studios/{studioId}?depth={depth}"),
            payload: null,
            cancellationToken);

    public async Task<IReadOnlyList<StudioDto>> GetStudiosAsync(
        CancellationToken cancellationToken = default)
        => await GetStudiosAsync(sort: null, direction: null, cancellationToken);

    public async Task<IReadOnlyList<StudioDto>> GetStudiosAsync(
        string? sort,
        string? direction,
        CancellationToken cancellationToken = default)
    {
        var query = "/api/studios?perPage=250";
        if (!string.IsNullOrWhiteSpace(sort))
            query += $"&sort={Uri.EscapeDataString(sort)}";
        if (!string.IsNullOrWhiteSpace(direction))
            query += $"&direction={Uri.EscapeDataString(direction)}";
        var result = await SendAsync<PaginatedResponse<StudioDto>>(
            HttpMethod.Get,
            WithCacheNonce(query),
            payload: null,
            cancellationToken);
        return result.Items;
    }

    public Task<PaginatedResponse<StudioDto>> FindStudiosAsync(
        FilteredQueryRequest<StudioFilter> request,
        CancellationToken cancellationToken = default)
        => SendAsync<PaginatedResponse<StudioDto>>(
            HttpMethod.Post,
            "/api/studios/find",
            request,
            cancellationToken);

    public async Task<int> BulkUpdateStudiosAsync(
        BulkStudioUpdateDto request,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Post,
            "/api/studios/bulk",
            request,
            cancellationToken);
        return response.GetProperty("updated").GetInt32();
    }

    public async Task<int> BulkDeleteStudiosAsync(
        BatchDeleteDto request,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Delete,
            "/api/studios/bulk",
            request,
            cancellationToken);
        return response.GetProperty("deleted").GetInt32();
    }

    public Task<StudioDto> MergeStudiosAsync(
        StudioDto target,
        IReadOnlyCollection<StudioDto> sources,
        CancellationToken cancellationToken = default)
        => SendAsync<StudioDto>(
            HttpMethod.Post,
            "/api/studios/merge",
            new StudioMergeDto(target.Id, sources.Select(source => source.Id).ToList()),
            cancellationToken);

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
