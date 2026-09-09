using System.Net;
using System.Net.Http.Json;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<ScrapeAttemptDto> CreateScrapeAttemptAsync(
        CreateScrapeAttemptDto request,
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<ScrapeAttemptDto>(
            HttpMethod.Post,
            "/api/scrape-attempts",
            request,
            HttpStatusCode.Created,
            cancellationToken);

    public Task<ScrapeAttemptDto> GetScrapeAttemptAsync(
        Guid attemptId,
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<ScrapeAttemptDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/scrape-attempts/{attemptId:D}"),
            payload: null,
            HttpStatusCode.OK,
            cancellationToken);

    public Task<ResolveScrapeRelationsResultDto> ResolveScrapeRelationsAsync(
        ResolveScrapeRelationsRequestDto request,
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<ResolveScrapeRelationsResultDto>(
            HttpMethod.Post,
            "/api/scrape-attempts/resolve-relations",
            request,
            HttpStatusCode.OK,
            cancellationToken);

    public Task<ScrapeAttemptDto> ApplyScrapeAttemptAsync(
        Guid attemptId,
        ApplyVideoScrapeAttemptDto request,
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<ScrapeAttemptDto>(
            HttpMethod.Post,
            $"/api/scrape-attempts/{attemptId:D}/apply",
            request,
            HttpStatusCode.OK,
            cancellationToken);

    private async Task<T> SendForExpectedStatusAsync<T>(
        HttpMethod method,
        string requestUri,
        object? payload,
        HttpStatusCode expectedStatus,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, requestUri);
        if (payload is not null)
            request.Content = JsonContent.Create(payload, options: ApiJson.Options);

        using var response = await _client.SendAsync(request, cancellationToken);
        if (response.StatusCode != expectedStatus)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"{method} {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}); expected {(int)expectedStatus} ({expectedStatus}). Response: {body}");
        }

        return await ApiResponse.ReadAsync<T>(response, $"{method} {requestUri}", cancellationToken);
    }
}
