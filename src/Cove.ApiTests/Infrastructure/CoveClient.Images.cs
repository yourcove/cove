using System.Text.Json;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<VideoHistoryDto> GetImageHistoryAsync(
        ImageDto image,
        CancellationToken cancellationToken = default)
        => SendAsync<VideoHistoryDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/images/{image.Id}/history"),
            payload: null,
            cancellationToken);

    public Task<int> AddHistoricalImageLikeAsync(
        ImageDto image,
        DateTime at,
        CancellationToken cancellationToken = default)
        => SendAsync<int>(
            HttpMethod.Post,
            $"/api/images/{image.Id}/like/historical",
            new HistoricalLikeDto(at),
            cancellationToken);

    public Task DeleteHistoricalImageLikeAsync(
        ImageDto image,
        DateTime at,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(
            HttpMethod.Delete,
            $"/api/images/{image.Id}/like/history?at={Uri.EscapeDataString(at.ToUniversalTime().ToString("O"))}",
            new { },
            cancellationToken);

    public Task<int> DecrementImageLikeAsync(
        ImageDto image,
        CancellationToken cancellationToken = default)
        => SendAsync<int>(HttpMethod.Delete, $"/api/images/{image.Id}/like", payload: null, cancellationToken);

    public Task<int> ResetImageLikeAsync(
        ImageDto image,
        CancellationToken cancellationToken = default)
        => SendAsync<int>(HttpMethod.Post, $"/api/images/{image.Id}/like/reset", payload: null, cancellationToken);

    public async Task<string> RescanImageAsync(
        ImageDto image,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Post,
            $"/api/images/{image.Id}/rescan",
            payload: null,
            cancellationToken);
        return response.GetProperty("jobId").GetString()
            ?? throw new InvalidOperationException($"POST /api/images/{image.Id}/rescan did not return a job id.");
    }

    public Task<EntityEngagementDto> SetImageRatingAsync(
        ImageDto image,
        int rating,
        CancellationToken cancellationToken = default)
        => SendAsync<EntityEngagementDto>(
            HttpMethod.Put,
            $"/api/engagement/{AffinityHostType.Image}/{image.Id}/rating",
            new VideoRatingDto(rating, "overall"),
            cancellationToken);

    public Task<ImageDto> UpdateImageAsync(
        int imageId,
        object update,
        CancellationToken cancellationToken = default)
        => SendAsync<ImageDto>(HttpMethod.Put, $"/api/images/{imageId}", update, cancellationToken);

    public Task<PaginatedResponse<ImageDto>> FindImagesAsync(
        FilteredQueryRequest<ImageFilter> request,
        CancellationToken cancellationToken = default)
        => SendAsync<PaginatedResponse<ImageDto>>(HttpMethod.Post, "/api/images/find", request, cancellationToken);

    public Task<ImageAggregate> AggregateImagesAsync(
        FilteredQueryRequest<ImageFilter> request,
        CancellationToken cancellationToken = default)
        => SendAsync<ImageAggregate>(HttpMethod.Post, "/api/images/aggregate", request, cancellationToken);

    public async Task<int> BulkUpdateImagesAsync(
        BulkImageUpdateDto request,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(HttpMethod.Post, "/api/images/bulk", request, cancellationToken);
        return response.GetProperty("updated").GetInt32();
    }

    public Task DeleteImageAsync(
        int imageId,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(HttpMethod.Delete, $"/api/images/{imageId}", new { }, cancellationToken);

    public Task<BulkDeletionJobStartResponse> BulkDeleteImagesAsync(
        BatchDeleteDto request,
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<BulkDeletionJobStartResponse>(
            HttpMethod.Delete,
            "/api/images/bulk",
            request,
            System.Net.HttpStatusCode.Accepted,
            cancellationToken);
}
