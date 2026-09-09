using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<PaginatedResponse<AiRunDto>> GetAiRunsAsync(
        AiRunTargetType? targetType = null,
        CancellationToken cancellationToken = default)
        => SendAsync<PaginatedResponse<AiRunDto>>(
            HttpMethod.Get,
            WithCacheNonce(targetType is null ? "/api/ai-runs" : $"/api/ai-runs?targetType={targetType}"),
            payload: null,
            cancellationToken);

    public Task<PaginatedResponse<EmbeddingDto>> GetEmbeddingsAsync(
        EmbeddingHostType? hostType = null,
        CancellationToken cancellationToken = default)
        => SendAsync<PaginatedResponse<EmbeddingDto>>(
            HttpMethod.Get,
            WithCacheNonce(hostType is null ? "/api/embeddings" : $"/api/embeddings?hostType={hostType}"),
            payload: null,
            cancellationToken);

    public Task<AiRunDto> GetAiRunAsync(
        int id,
        CancellationToken cancellationToken = default)
        => SendAsync<AiRunDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/ai-runs/{id}"),
            payload: null,
            cancellationToken);

    public Task<EmbeddingDto> GetEmbeddingAsync(
        int id,
        CancellationToken cancellationToken = default)
        => SendAsync<EmbeddingDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/embeddings/{id}"),
            payload: null,
            cancellationToken);

    public Task<IReadOnlyList<EmbeddingSearchResultDto>> SearchEmbeddingsAsync(
        EmbeddingSearchRequestDto request,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<EmbeddingSearchResultDto>>(
            HttpMethod.Post,
            "/api/embeddings/search",
            request,
            cancellationToken);

    public Task<AiDataPurgeResultDto> DeleteEmbeddingsAsync(
        AiDataSelectorDto selector,
        CancellationToken cancellationToken = default)
        => SendAsync<AiDataPurgeResultDto>(
            HttpMethod.Delete,
            "/api/embeddings",
            selector,
            cancellationToken);

    public Task<AiDataPurgeResultDto> PurgeAiDataAsync(
        AiDataPurgeRequestDto request,
        CancellationToken cancellationToken = default)
        => SendAsync<AiDataPurgeResultDto>(
            HttpMethod.Post,
            "/api/ai-data/purge",
            request,
            cancellationToken);

    public Task<AuditEventPageDto> GetAuditEventsAsync(
        string action,
        CancellationToken cancellationToken = default)
        => SendAsync<AuditEventPageDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/audit?action={Uri.EscapeDataString(action)}&perPage=200"),
            payload: null,
            cancellationToken);
}

public sealed record AuditEventPageDto(
    IReadOnlyList<AuditEventDto> Items,
    long TotalCount,
    int Page,
    int PerPage);
