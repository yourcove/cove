using System.Net;
using System.Net.Http.Json;
using Cove.Api.Controllers;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<DashboardDto> BootstrapDashboardAsync(
        DashboardBootstrapRequest request,
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<DashboardDto>(
            HttpMethod.Post,
            "/api/dashboards/bootstrap",
            request,
            HttpStatusCode.OK,
            cancellationToken);

    public Task<IReadOnlyList<DashboardSummaryDto>> GetDashboardsAsync(
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<IReadOnlyList<DashboardSummaryDto>>(
            HttpMethod.Get,
            WithCacheNonce("/api/dashboards"),
            payload: null,
            HttpStatusCode.OK,
            cancellationToken);

    public Task<DashboardDto> GetDashboardAsync(
        int id,
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<DashboardDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/dashboards/{id}"),
            payload: null,
            HttpStatusCode.OK,
            cancellationToken);

    public Task<DashboardDto> CreateDashboardAsync(
        DashboardCreateRequest request,
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<DashboardDto>(
            HttpMethod.Post,
            "/api/dashboards",
            request,
            HttpStatusCode.Created,
            cancellationToken);

    public Task<DashboardDto> UpdateDashboardAsync(
        int id,
        DashboardUpdateRequest request,
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<DashboardDto>(
            HttpMethod.Put,
            $"/api/dashboards/{id}",
            request,
            HttpStatusCode.OK,
            cancellationToken);

    public async Task<DashboardVersionConflictDto> UpdateDashboardExpectingConflictAsync(
        int id,
        DashboardUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"/api/dashboards/{id}";
        using var response = await _client.PutAsJsonAsync(requestUri, request, ApiJson.Options, cancellationToken);
        if (response.StatusCode is not HttpStatusCode.Conflict)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"PUT {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}); expected 409 (Conflict). Response: {body}");
        }

        return await response.Content.ReadFromJsonAsync<DashboardVersionConflictDto>(ApiJson.Options, cancellationToken)
            ?? throw new InvalidOperationException($"PUT {requestUri} returned an empty version-conflict response.");
    }

    public Task<HttpStatusCode> TryUpdateDashboardAsync(
        int id,
        DashboardUpdateRequest request,
        CancellationToken cancellationToken = default)
        => SendStatusAsync(HttpMethod.Put, $"/api/dashboards/{id}", request, cancellationToken);

    public Task<DashboardDto> DuplicateDashboardAsync(
        int id,
        DashboardDuplicateRequest request,
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<DashboardDto>(
            HttpMethod.Post,
            $"/api/dashboards/{id}/duplicate",
            request,
            HttpStatusCode.Created,
            cancellationToken);

    public Task<DashboardDto> SetDefaultDashboardAsync(
        int id,
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<DashboardDto>(
            HttpMethod.Put,
            $"/api/dashboards/{id}/default",
            payload: null,
            HttpStatusCode.OK,
            cancellationToken);

    public Task DeleteDashboardAsync(
        int id,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(HttpMethod.Delete, $"/api/dashboards/{id}", new { }, cancellationToken);

    public Task<HttpStatusCode> TryDeleteDashboardAsync(
        int id,
        CancellationToken cancellationToken = default)
        => SendStatusAsync(HttpMethod.Delete, $"/api/dashboards/{id}", payload: null, cancellationToken);
}
