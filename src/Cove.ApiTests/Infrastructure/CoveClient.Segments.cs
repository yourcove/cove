using Cove.Core.DTOs;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<VideoResolvedSpansDto> GetVideoResolvedSpansAsync(VideoDto video, int profileId, CancellationToken cancellationToken = default)
        => SendAsync<VideoResolvedSpansDto>(HttpMethod.Get, WithCacheNonce($"/api/videos/{video.Id}/segments/spans?profile={profileId}"), null, cancellationToken);

    public Task<ResolvedSpanListDto> QueryVideoResolvedSpansAsync(VideoDto video, SegmentSpanQueryRequestDto request, CancellationToken cancellationToken = default)
        => SendAsync<ResolvedSpanListDto>(HttpMethod.Post, $"/api/videos/{video.Id}/segments/spans/query", request, cancellationToken);

    public Task<ResolvedSpanDetailDto> GetVideoResolvedSpanDetailAsync(VideoDto video, string spanKey, int profileId, CancellationToken cancellationToken = default)
        => SendAsync<ResolvedSpanDetailDto>(HttpMethod.Get, WithCacheNonce($"/api/videos/{video.Id}/spans/{spanKey}?profile={profileId}"), null, cancellationToken);

    public Task<SegmentDto> GetVideoSegmentAsync(VideoDto video, int segmentId, CancellationToken cancellationToken = default)
        => SendAsync<SegmentDto>(HttpMethod.Get, WithCacheNonce($"/api/videos/{video.Id}/segments/{segmentId}"), null, cancellationToken);

    public Task<SegmentDto> UpdateVideoSegmentAsync(VideoDto video, int segmentId, SegmentUpdateDto request, CancellationToken cancellationToken = default)
        => SendAsync<SegmentDto>(HttpMethod.Put, $"/api/videos/{video.Id}/segments/{segmentId}", request, cancellationToken);

    public Task DeleteVideoSegmentAsync(VideoDto video, int segmentId, CancellationToken cancellationToken = default)
        => SendForNoContentAsync(HttpMethod.Delete, $"/api/videos/{video.Id}/segments/{segmentId}", new { }, cancellationToken);

    public Task<SegmentSpanSearchResponseDto> SearchResolvedSpansAsync(SegmentSpanSearchRequestDto request, CancellationToken cancellationToken = default)
        => SendAsync<SegmentSpanSearchResponseDto>(HttpMethod.Post, "/api/segments/spans/search", request, cancellationToken);

    public Task<SegmentSpanCountResponseDto> CountResolvedSpansAsync(SegmentSpanSearchRequestDto request, CancellationToken cancellationToken = default)
        => SendAsync<SegmentSpanCountResponseDto>(HttpMethod.Post, "/api/segments/spans/count", request, cancellationToken);
}
