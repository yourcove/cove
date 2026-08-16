using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<IReadOnlyList<JobInfo>> GetJobHistoryAsync(
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<JobInfo>>(
            HttpMethod.Get,
            WithCacheNonce("/api/jobs/history"),
            payload: null,
            cancellationToken);

    public Task<string> StartLibraryScanJobAsync(CancellationToken cancellationToken = default)
        => StartJobAsync("/api/jobs/scan", cancellationToken);

    public Task<string> StartThumbnailGenerationJobAsync(CancellationToken cancellationToken = default)
        => StartJobAsync("/api/jobs/generate-thumbnails", cancellationToken);

    public Task<string> StartVideoPhashGenerationJobAsync(CancellationToken cancellationToken = default)
        => StartJobAsync("/api/jobs/generate-video-phashes", cancellationToken);

    public Task<string> StartImagePhashGenerationJobAsync(CancellationToken cancellationToken = default)
        => StartJobAsync("/api/jobs/generate-image-phashes", cancellationToken);

    public Task<string> StartLibraryCleanJobAsync(
        bool dryRun,
        CancellationToken cancellationToken = default)
        => StartJobAsync($"/api/jobs/clean?dryRun={dryRun.ToString().ToLowerInvariant()}", cancellationToken);

    private async Task<string> StartJobAsync(
        string requestUri,
        CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsync(requestUri, content: null, cancellationToken);
        if (response.StatusCode is not HttpStatusCode.Accepted)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"POST {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ApiJson.Options, cancellationToken);
        return payload.GetProperty("jobId").GetString()
            ?? throw new InvalidOperationException($"POST {requestUri} did not return a job id.");
    }
}
