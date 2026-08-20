using Cove.Core.DTOs;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<IReadOnlyList<SegmentDisplayProfileDto>> GetSegmentDisplayProfilesAsync(CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<SegmentDisplayProfileDto>>(HttpMethod.Get, WithCacheNonce("/api/segment-display-profiles"), null, cancellationToken);
    public Task<SegmentDisplayProfileDto> GetSegmentDisplayProfileAsync(int id, CancellationToken cancellationToken = default) => SendAsync<SegmentDisplayProfileDto>(HttpMethod.Get, WithCacheNonce($"/api/segment-display-profiles/{id}"), null, cancellationToken);
    public Task<SegmentDisplayProfileDto> CreateSegmentDisplayProfileAsync(SegmentDisplayProfileCreateDto dto, CancellationToken cancellationToken = default) => SendAsync<SegmentDisplayProfileDto>(HttpMethod.Post, "/api/segment-display-profiles", dto, cancellationToken);
    public Task<SegmentDisplayProfileDto> UpdateSegmentDisplayProfileAsync(int id, SegmentDisplayProfileUpdateDto dto, CancellationToken cancellationToken = default) => SendAsync<SegmentDisplayProfileDto>(HttpMethod.Put, $"/api/segment-display-profiles/{id}", dto, cancellationToken);
    public Task<SegmentDisplayProfileDto> SetDefaultSegmentDisplayProfileAsync(int id, CancellationToken cancellationToken = default) => SendAsync<SegmentDisplayProfileDto>(HttpMethod.Put, $"/api/segment-display-profiles/{id}/default", new { }, cancellationToken);
    public Task DeleteSegmentDisplayProfileAsync(int id, CancellationToken cancellationToken = default) => SendForNoContentAsync(HttpMethod.Delete, $"/api/segment-display-profiles/{id}", new { }, cancellationToken);
    public Task<IReadOnlyList<SegmentDisplayRuleDto>> GetSegmentDisplayRulesAsync(int profileId, CancellationToken cancellationToken = default) => SendAsync<IReadOnlyList<SegmentDisplayRuleDto>>(HttpMethod.Get, WithCacheNonce($"/api/segment-display-profiles/{profileId}/rules"), null, cancellationToken);
    public Task<SegmentDisplayRuleDto> CreateSegmentDisplayRuleAsync(int profileId, SegmentDisplayRuleCreateDto dto, CancellationToken cancellationToken = default) => SendAsync<SegmentDisplayRuleDto>(HttpMethod.Post, $"/api/segment-display-profiles/{profileId}/rules", dto, cancellationToken);
    public Task BulkCreateSegmentDisplayRulesAsync(int profileId, List<SegmentDisplayRuleCreateDto> dtos, CancellationToken cancellationToken = default) => SendForNoContentAsync(HttpMethod.Post, $"/api/segment-display-profiles/{profileId}/rules/bulk", dtos, cancellationToken);
    public Task<SegmentDisplayRuleDto> UpdateSegmentDisplayRuleAsync(int profileId, int id, SegmentDisplayRuleUpdateDto dto, CancellationToken cancellationToken = default) => SendAsync<SegmentDisplayRuleDto>(HttpMethod.Put, $"/api/segment-display-profiles/{profileId}/rules/{id}", dto, cancellationToken);
    public Task DeleteSegmentDisplayRuleAsync(int profileId, int id, CancellationToken cancellationToken = default) => SendForNoContentAsync(HttpMethod.Delete, $"/api/segment-display-profiles/{profileId}/rules/{id}", new { }, cancellationToken);
    public Task<ResolvedSpanListDto> PreviewSegmentDisplayProfileAsync(SegmentDisplayProfilePreviewRequestDto dto, CancellationToken cancellationToken = default) => SendAsync<ResolvedSpanListDto>(HttpMethod.Post, "/api/segment-display-profiles/preview", dto, cancellationToken);
}
