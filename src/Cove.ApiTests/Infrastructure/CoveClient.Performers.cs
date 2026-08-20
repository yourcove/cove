using Cove.Core.DTOs;

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
