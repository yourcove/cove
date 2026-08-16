using System.Net.Http.Headers;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<IReadOnlyList<ScraperSummaryDto>> GetScrapersAsync(CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<ScraperSummaryDto>>(HttpMethod.Get, WithCacheNonce("/api/system/scrapers"), null, cancellationToken);
    public Task<IReadOnlyList<ScraperSummaryDto>> ReloadScrapersAsync(CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<ScraperSummaryDto>>(HttpMethod.Post, "/api/system/scrapers/reload", new { }, cancellationToken);
    public Task<IReadOnlyList<ScraperSummaryDto>> MatchScrapersAsync(ScraperMatchUrlRequest request, CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<ScraperSummaryDto>>(HttpMethod.Post, "/api/system/scrapers/match-url", request, cancellationToken);
    public Task<JsonElement> ScrapeUrlAsync(ScrapeUrlRequest request, CancellationToken cancellationToken = default) => SendAsync<JsonElement>(HttpMethod.Post, "/api/system/scrapers/scrape-url", request, cancellationToken);
    public Task<JsonElement> ScrapeUrlAutoAsync(ScraperMatchUrlRequest request, CancellationToken cancellationToken = default) => SendAsync<JsonElement>(HttpMethod.Post, "/api/system/scrapers/scrape-url-auto", request, cancellationToken);
    public Task<JsonElement> ScrapeNameAsync(ScrapeNameRequest request, CancellationToken cancellationToken = default) => SendAsync<JsonElement>(HttpMethod.Post, "/api/system/scrapers/scrape-name", request, cancellationToken);
    public Task<JsonElement> ScrapeFragmentAsync(ScrapeFragmentRequest request, CancellationToken cancellationToken = default) => SendAsync<JsonElement>(HttpMethod.Post, "/api/system/scrapers/scrape-fragment", request, cancellationToken);

    public Task<IReadOnlyList<ScrapedPerformerDto>> ScrapePerformerNameAsync(ScrapeNameRequest request, CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<IReadOnlyList<ScrapedPerformerDto>>(
            HttpMethod.Post,
            "/api/system/scrapers/scrape-name",
            request,
            System.Net.HttpStatusCode.OK,
            cancellationToken);

    public Task<ScrapedPerformerDto> ScrapePerformerFragmentAsync(ScrapeFragmentRequest request, CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<ScrapedPerformerDto>(
            HttpMethod.Post,
            "/api/system/scrapers/scrape-fragment",
            request,
            System.Net.HttpStatusCode.OK,
            cancellationToken);
    public Task<MetadataServerValidationResultDto> ValidateMetadataServerAsync(MetadataServerDto metadataServer, CancellationToken cancellationToken = default) => SendAsync<MetadataServerValidationResultDto>(HttpMethod.Post, "/api/system/metadata-servers/validate", metadataServer, cancellationToken);

    public Task<CoveConfigDto> GetSystemConfigAsync(
        CancellationToken cancellationToken = default)
        => SendAsync<CoveConfigDto>(
            HttpMethod.Get,
            WithCacheNonce("/api/system/config"),
            payload: null,
            cancellationToken);

    public Task<CoveConfigDto> SaveSystemConfigAsync(
        CoveConfigDto config,
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<CoveConfigDto>(
            HttpMethod.Put,
            "/api/system/config",
            config,
            System.Net.HttpStatusCode.OK,
            cancellationToken);

    public Task<StatsDto> GetSystemStatsAsync(
        CancellationToken cancellationToken = default)
        => SendAsync<StatsDto>(
            HttpMethod.Get,
            WithCacheNonce("/api/system/stats"),
            payload: null,
            cancellationToken);

    public Task<SystemLogLevelStatus> GetSystemLogLevelAsync(
        CancellationToken cancellationToken = default)
        => SendAsync<SystemLogLevelStatus>(
            HttpMethod.Get,
            WithCacheNonce("/api/system/log-level"),
            payload: null,
            cancellationToken);

    public Task<FfmpegCapabilitiesResponse> GetFfmpegCapabilitiesAsync(
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<FfmpegCapabilitiesResponse>(
            HttpMethod.Get,
            WithCacheNonce("/api/system/ffmpeg-capabilities"),
            payload: null,
            System.Net.HttpStatusCode.OK,
            cancellationToken);

    public Task<SystemLogLevelStatus> SetSystemLogLevelAsync(
        string level,
        CancellationToken cancellationToken = default)
        => SendAsync<SystemLogLevelStatus>(
            HttpMethod.Patch,
            "/api/system/log-level",
            new { level },
            cancellationToken);

    public Task<RecomputeDerivedCountsResult> RecomputeDerivedCountsAsync(
        CancellationToken cancellationToken = default)
        => SendAsync<RecomputeDerivedCountsResult>(
            HttpMethod.Post,
            "/api/system/maintenance/recompute-derived-counts",
            payload: null,
            cancellationToken);

    public Task<SystemUiAssetUploadResult> UploadFaviconAsync(
        byte[] content,
        string fileName,
        string mediaType = "image/png",
        CancellationToken cancellationToken = default)
        => UploadSystemUiAssetAsync("/api/system/ui/favicon", content, fileName, mediaType, cancellationToken);

    public Task<SystemUiAssetUploadResult> UploadLogoAsync(
        byte[] content,
        string fileName,
        string mediaType = "image/png",
        CancellationToken cancellationToken = default)
        => UploadSystemUiAssetAsync("/api/system/ui/logo", content, fileName, mediaType, cancellationToken);

    public Task<IReadOnlyList<RoleDto>> GetRolesAsync(
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<RoleDto>>(
            HttpMethod.Get,
            WithCacheNonce("/api/roles"),
            payload: null,
            cancellationToken);

    public Task<RoleDto> UpdateRoleAsync(
        int roleId,
        UpdateRoleRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync<RoleDto>(HttpMethod.Put, $"/api/roles/{roleId}", request, cancellationToken);

    public Task<ContentRuleDto> CreateContentRuleAsync(
        CreateContentRuleRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync<ContentRuleDto>(HttpMethod.Post, "/api/content-rules", request, cancellationToken);

    public Task<EntityOverrideDto> CreateEntityOverrideAsync(
        CreateEntityOverrideRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync<EntityOverrideDto>(
            HttpMethod.Post,
            "/api/content-rules/overrides",
            request,
            cancellationToken);

    public Task<JsonElement> ReadEndpointAsync(
        ReadEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        var definition = ReadEndpointCatalog.Get(endpoint);

        return SendAsync<JsonElement>(
            HttpMethod.Get,
            WithCacheNonce(definition.RequestUri),
            payload: null,
            cancellationToken);
    }

    public Task<IReadOnlyList<DownloaderDescriptorDto>> GetDownloadersAsync(
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<DownloaderDescriptorDto>>(
            HttpMethod.Get,
            WithCacheNonce("/api/system/downloaders"),
            payload: null,
            cancellationToken);

    public Task<IReadOnlyList<DownloaderMatchDto>> MatchDownloaderAsync(
        Uri uri,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<DownloaderMatchDto>>(
            HttpMethod.Post,
            "/api/system/downloaders/match",
            new DownloaderMatchRequestDto(uri.AbsoluteUri),
            cancellationToken);

    public Task<DownloaderPreflightResponseDto> PreflightDownloadAsync(
        Uri uri,
        string entity,
        int? entityId = null,
        CancellationToken cancellationToken = default)
        => SendAsync<DownloaderPreflightResponseDto>(
            HttpMethod.Post,
            "/api/system/downloaders/preflight",
            new DownloaderPreflightRequestDto
            {
                Url = uri.AbsoluteUri,
                Entity = entity,
                EntityId = entityId,
            },
            cancellationToken);

    public async Task<string> StartTextDownloadAsync(
        string downloaderId,
        Uri uri,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Post,
            "/api/system/downloaders/download",
            new DownloaderStartRequestDto
            {
                DownloaderId = downloaderId,
                Url = uri.AbsoluteUri,
                Entity = "Text",
            },
            cancellationToken);
        return response.GetProperty("jobId").GetString()
            ?? throw new InvalidOperationException("The downloader response did not contain a job id.");
    }

    public Task<DownloaderBatchStartResponse> StartDownloaderBatchAsync(
        DownloaderBatchStartRequestDto request,
        CancellationToken cancellationToken = default)
        => SendAsync<DownloaderBatchStartResponse>(
            HttpMethod.Post,
            "/api/system/downloaders/download-batch",
            request,
            cancellationToken);

    public Task<JobInfo> GetJobAsync(
        string jobId,
        CancellationToken cancellationToken = default)
        => SendAsync<JobInfo>(
            HttpMethod.Get,
            WithCacheNonce($"/api/jobs/{Uri.EscapeDataString(jobId)}"),
            payload: null,
            cancellationToken);

    public async Task<JobInfo> WaitForTerminalJobAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        JobInfo? lastJob = null;
        try
        {
            while (true)
            {
                lastJob = await GetJobAsync(jobId, timeout.Token);
                if (lastJob.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
                    return lastJob;
                await Task.Delay(100, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Job '{jobId}' did not reach a terminal state within 30 seconds. "
                + $"Last status: {lastJob?.Status.ToString() ?? "unavailable"}; "
                + $"progress: {lastJob?.Progress.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"}; "
                + $"subtask: {lastJob?.SubTask ?? "unavailable"}.");
        }
    }

    private async Task<SystemUiAssetUploadResult> UploadSystemUiAssetAsync(
        string requestUri,
        byte[] content,
        string fileName,
        string mediaType,
        CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(file, "file", fileName);
        using var response = await _client.PostAsync(requestUri, form, cancellationToken);
        return await ApiResponse.ReadAsync<SystemUiAssetUploadResult>(response, $"POST {requestUri}", cancellationToken);
    }
}

public sealed record SystemLogLevelStatus(
    string Level,
    string ConfiguredLevel,
    DateTimeOffset? TraceExpiresAt);

public sealed record FfmpegCapabilitiesResponse(
    bool FfmpegFound,
    string? FfmpegPath,
    IReadOnlyList<string> Accelerators,
    IReadOnlyList<string> Decoders,
    DateTime ProbedAtUtc);

public sealed record SystemUiAssetUploadResult(string Path, string FileName);

public sealed record DownloaderBatchStartResponse(
    string? JobId,
    int QueuedCount,
    IReadOnlyList<DownloaderBatchStartIssueDto> Issues);
