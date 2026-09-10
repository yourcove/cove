using System.Text.Json;
using Cove.Api.Controllers;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<JsonElement> GetVideoAlignmentsAsync(int videoId, CancellationToken ct)
        => SendAsync<JsonElement>(HttpMethod.Get, $"/api/videos/{videoId}/alignments", null, ct);
    public Task<JsonElement> AnalyzeVideoAlignmentAsync(int videoId, AnalyzeVideoAlignment pair, CancellationToken ct)
        => SendAsync<JsonElement>(HttpMethod.Post, $"/api/videos/{videoId}/alignments/analyze", pair, ct);
    public Task<JsonElement> PreviewVideoAlignmentAsync(int videoId, ReviewVideoAlignment review, CancellationToken ct)
        => SendAsync<JsonElement>(HttpMethod.Post, $"/api/videos/{videoId}/alignments/preview", review, ct);
    public Task<JsonElement> AssessVideoAlignmentAsync(int videoId, int targetFileId, CancellationToken ct)
        => SendAsync<JsonElement>(HttpMethod.Post, $"/api/videos/{videoId}/alignments/assess", new AnalyzeVideoAlignment(0, targetFileId), ct);
    public Task<JsonElement> ApplyVideoAlignmentAsync(int videoId, Cove.Core.DTOs.VideoSetPrimaryFileDto request, CancellationToken ct)
        => SendAsync<JsonElement>(HttpMethod.Post, $"/api/videos/{videoId}/alignments/apply", request, ct);
}
