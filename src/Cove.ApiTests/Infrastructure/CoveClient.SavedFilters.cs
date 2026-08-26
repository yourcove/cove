using System.Net;
using System.Net.Http.Json;
using Cove.Api.Controllers;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<SavedFilterDto> GetSavedFilterAsync(
        int id,
        CancellationToken cancellationToken = default)
        => SendAsync<SavedFilterDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/savedfilters/{id}"),
            payload: null,
            cancellationToken);

    public Task<IReadOnlyList<SavedFilterDto>> GetSavedFiltersAsync(
        string? mode = null,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<SavedFilterDto>>(
            HttpMethod.Get,
            WithCacheNonce(mode is null
                ? "/api/savedfilters"
                : $"/api/savedfilters?mode={Uri.EscapeDataString(mode)}"),
            payload: null,
            cancellationToken);

    public async Task<SavedFilterDto> CreateSavedFilterAsync(
        SavedFilterCreateDto filter,
        CancellationToken cancellationToken = default)
    {
        const string requestUri = "/api/savedfilters";
        using var response = await _client.PostAsJsonAsync(requestUri, filter, ApiJson.Options, cancellationToken);
        if (response.StatusCode is not HttpStatusCode.Created)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"POST {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
        }

        return await ApiResponse.ReadAsync<SavedFilterDto>(response, $"POST {requestUri}", cancellationToken);
    }

    public async Task<SavedFilterDto> UpdateSavedFilterAsync(
        int id,
        SavedFilterUpdateDto filter,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"/api/savedfilters/{id}";
        using var response = await _client.PutAsJsonAsync(requestUri, filter, ApiJson.Options, cancellationToken);
        if (response.StatusCode is not HttpStatusCode.OK)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"PUT {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
        }

        return await ApiResponse.ReadAsync<SavedFilterDto>(response, $"PUT {requestUri}", cancellationToken);
    }

    public Task DeleteSavedFilterAsync(
        int id,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(HttpMethod.Delete, $"/api/savedfilters/{id}", new { }, cancellationToken);
}
