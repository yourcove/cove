using System.Text.Json;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<PerformerDto> CreatePerformerAsync(
        PerformerCreateDto performer,
        CancellationToken cancellationToken = default)
        => SendAsync<PerformerDto>(HttpMethod.Post, "/api/performers", performer, cancellationToken);

    public Task<PerformerDto> GetPerformerByIdAsync(
        int performerId,
        CancellationToken cancellationToken = default)
        => SendAsync<PerformerDto>(
            HttpMethod.Get,
            $"/api/performers/{performerId}?apiTestNonce={Guid.NewGuid():N}",
            payload: null,
            cancellationToken);

    public Task<PerformerDto> UpdatePerformerAsync(
        int performerId,
        object update,
        CancellationToken cancellationToken = default)
        => SendAsync<PerformerDto>(
            HttpMethod.Put,
            $"/api/performers/{performerId}",
            update,
            cancellationToken);

    public async Task DeletePerformerAsync(
        int performerId,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"/api/performers/{performerId}";
        using var response = await _client.DeleteAsync(requestUri, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.NoContent)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"DELETE {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
    }

    public async Task<IReadOnlyList<PerformerDto>> GetPerformersAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<PaginatedResponse<PerformerDto>>(
            HttpMethod.Get,
            $"/api/performers?perPage=250&apiTestNonce={Guid.NewGuid():N}",
            payload: null,
            cancellationToken);
        return result.Items;
    }

    public Task<PaginatedResponse<PerformerDto>> FindPerformersAsync(
        FilteredQueryRequest<PerformerFilter> request,
        CancellationToken cancellationToken = default)
        => SendAsync<PaginatedResponse<PerformerDto>>(
            HttpMethod.Post,
            "/api/performers/find",
            request,
            cancellationToken);

    public async Task<int> BulkUpdatePerformersAsync(
        BulkPerformerUpdateDto request,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(HttpMethod.Post, "/api/performers/bulk", request, cancellationToken);
        return response.GetProperty("updated").GetInt32();
    }

    public async Task<int> BulkDeletePerformersAsync(
        BatchDeleteDto request,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(HttpMethod.Delete, "/api/performers/bulk", request, cancellationToken);
        return response.GetProperty("deleted").GetInt32();
    }

    public Task<PaginatedResponse<GroupDto>> GetPerformerGroupsAsync(
        int performerId,
        int page = 1,
        int perPage = 18,
        CancellationToken cancellationToken = default)
        => SendAsync<PaginatedResponse<GroupDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/performers/{performerId}/groups?page={page}&perPage={perPage}"),
            payload: null,
            cancellationToken);

    public Task<PaginatedResponse<PerformerDto>> GetPerformerAppearsWithAsync(
        int performerId,
        int page = 1,
        int perPage = 18,
        CancellationToken cancellationToken = default)
        => SendAsync<PaginatedResponse<PerformerDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/performers/{performerId}/appears-with?page={page}&perPage={perPage}"),
            payload: null,
            cancellationToken);

    public async Task<PerformerDto> LinkTagToPerformerAsync(
        TagDetailDto tag,
        PerformerDto performer,
        CancellationToken cancellationToken = default)
    {
        var current = await GetPerformerByIdAsync(performer.Id, cancellationToken);
        var tagIds = current.Tags
            .Select(existingTag => existingTag.Id)
            .Append(tag.Id)
            .Distinct()
            .ToList();

        return await SendAsync<PerformerDto>(
            HttpMethod.Put,
            $"/api/performers/{performer.Id}",
            new { tagIds },
            cancellationToken);
    }
}
