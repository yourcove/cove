using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/videos/{videoId:int}/segments")]
[RequiresPermission(Permissions.SegmentsRead)]
public class VideoSegmentsController(CoveContext db, SegmentSpanResolver spanResolver, IBlobService blobService, IFieldProvenanceService? fieldProvenanceService = null) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SegmentDto>>> GetByVideo(int videoId, CancellationToken ct)
    {
        if (!await VideoExistsAsync(videoId, ct)) return NotFound();

            var segments = await db.VisibleSegments(SegmentHostType.Video)
            .AsNoTracking()
            .Include(segment => segment.Tag)
            .Where(segment => segment.HostId == videoId)
            .OrderBy(segment => segment.StartSec)
            .ThenBy(segment => segment.Id)
            .ToListAsync(ct);

        return Ok(segments.Select(segment => MapToDto(segment)).ToList());
    }

    [HttpGet("spans")]
    public async Task<ActionResult<VideoResolvedSpansDto>> GetSpans(int videoId, [FromQuery] int? profile = null, CancellationToken ct = default)
    {
        if (!await VideoExistsAsync(videoId, ct))
            return NotFound();

        try
        {
            return Ok(await spanResolver.ResolveVideoAsync(videoId, profile, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("spans/query")]
    public async Task<ActionResult<ResolvedSpanListDto>> QuerySpans(int videoId, [FromBody] SegmentSpanQueryRequestDto request, CancellationToken ct)
    {
        if (!await VideoExistsAsync(videoId, ct))
            return NotFound();

        try
        {
            var spans = await spanResolver.QueryVideoAsync(videoId, request, ct);
            return Ok(new ResolvedSpanListDto(spans));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("/api/videos/{videoId:int}/spans/{spanKey}")]
    public async Task<ActionResult<ResolvedSpanDetailDto>> GetSpanDetail(int videoId, string spanKey, [FromQuery] int? profile = null, CancellationToken ct = default)
    {
        if (!await VideoExistsAsync(videoId, ct))
            return NotFound();

        try
        {
            var detail = await spanResolver.GetSpanDetailAsync(videoId, spanKey, profile, ct);
            return detail is null ? NotFound() : Ok(detail);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<SegmentDto>> GetById(int videoId, int id, CancellationToken ct)
    {
        var segment = await db.VisibleSegments(SegmentHostType.Video)
            .AsNoTracking()
            .Include(item => item.Tag)
            .FirstOrDefaultAsync(item => item.Id == id && item.HostId == videoId, ct);

        if (segment is null)
            return NotFound();

        return Ok(MapToDto(segment, await LoadSegmentFieldProvenanceAsync(segment.Id, ct)));
    }

    [HttpPost]
    [RequiresPermission(Permissions.SegmentsWrite)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.SegmentsWrite, RouteValueName = "videoId")]
    public async Task<ActionResult<SegmentDto>> Create(int videoId, [FromBody] SegmentCreateDto dto, CancellationToken ct)
    {
        if (!await VideoExistsAsync(videoId, ct)) return NotFound();
        if (dto.EndSec.HasValue && dto.EndSec.Value < dto.StartSec)
            return BadRequest("Segment end must be greater than or equal to the start.");
        if (RequiresTagButMissing(dto.Kind, dto.TagId))
            return BadRequest("A segment with kind 'tag' must reference a tag.");

        var segment = new Segment
        {
            HostType = SegmentHostType.Video,
            HostId = videoId,
            StartSec = dto.StartSec,
            EndSec = dto.EndSec,
            TagId = dto.TagId,
            Kind = dto.Kind,
            RefId = dto.RefId,
            Payload = ToDocument(dto.Payload),
            SourceKey = NormalizeSourceKey(dto.SourceKey),
            SourceRunId = dto.SourceRunId,
            Confidence = dto.Confidence,
            Title = dto.Title,
            ColorHint = dto.ColorHint,
        };

        db.Segments.Add(segment);
        await RecordManualSegmentFieldProvenanceAsync(segment, ct);
        await db.SaveChangesAsync(ct);
        spanResolver.EvictVideo(videoId);
        await LoadTagAsync(segment, ct);

        return CreatedAtAction(nameof(GetById), new { videoId, id = segment.Id }, MapToDto(segment));
    }

    [HttpPut("{id:int}")]
    [RequiresPermission(Permissions.SegmentsWrite)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.SegmentsWrite, RouteValueName = "videoId")]
    public async Task<ActionResult<SegmentDto>> Update(int videoId, int id, [FromBody] SegmentUpdateDto dto, CancellationToken ct)
    {
        if (dto.EndSec.HasValue && dto.EndSec.Value < dto.StartSec)
            return BadRequest("Segment end must be greater than or equal to the start.");
        if (RequiresTagButMissing(dto.Kind, dto.TagId))
            return BadRequest("A segment with kind 'tag' must reference a tag.");

        var segment = await db.Segments
            .Include(item => item.Tag)
            .FirstOrDefaultAsync(item => item.Id == id && item.HostType == SegmentHostType.Video && item.HostId == videoId, ct);
        if (segment is null) return NotFound();

        var originalStartSec = segment.StartSec;
        var originalEndSec = segment.EndSec;
        var originalTagId = segment.TagId;
        var originalKind = segment.Kind;
        var originalRefId = segment.RefId;
        var originalPayload = segment.Payload?.RootElement.GetRawText();
        var originalSourceKey = segment.SourceKey;
        var originalSourceRunId = segment.SourceRunId;
        var originalConfidence = segment.Confidence;
        var originalTitle = segment.Title;
        var originalColorHint = segment.ColorHint;

        segment.StartSec = dto.StartSec;
        segment.EndSec = dto.EndSec;
        segment.TagId = dto.TagId;
        segment.Kind = dto.Kind;
        segment.RefId = dto.RefId;
        segment.Payload = ToDocument(dto.Payload);
        segment.SourceKey = NormalizeSourceKey(dto.SourceKey);
        segment.SourceRunId = dto.SourceRunId;
        segment.Confidence = dto.Confidence;
        segment.Title = dto.Title;
        segment.ColorHint = dto.ColorHint;
        segment.Tag = null;

        var manualFields = new Dictionary<string, object?>();
        if (!originalStartSec.Equals(segment.StartSec)) manualFields["start_sec"] = segment.StartSec;
        if (originalEndSec != segment.EndSec) manualFields["end_sec"] = segment.EndSec;
        if (originalTagId != segment.TagId) manualFields["tag_id"] = segment.TagId;
        if (!string.Equals(originalKind, segment.Kind, StringComparison.Ordinal)) manualFields["kind"] = segment.Kind;
        if (originalRefId != segment.RefId) manualFields["ref_id"] = segment.RefId;
        var updatedPayload = segment.Payload?.RootElement.GetRawText();
        if (!string.Equals(originalPayload, updatedPayload, StringComparison.Ordinal)) manualFields["payload"] = dto.Payload;
        if (!string.Equals(originalSourceKey, segment.SourceKey, StringComparison.Ordinal)) manualFields["source_key"] = segment.SourceKey;
        if (!string.Equals(originalSourceRunId, segment.SourceRunId, StringComparison.Ordinal)) manualFields["source_run_id"] = segment.SourceRunId;
        if (originalConfidence != segment.Confidence) manualFields["confidence"] = segment.Confidence;
        if (!string.Equals(originalTitle, segment.Title, StringComparison.Ordinal)) manualFields["title"] = segment.Title;
        if (!string.Equals(originalColorHint, segment.ColorHint, StringComparison.Ordinal)) manualFields["color_hint"] = segment.ColorHint;
        await RecordManualSegmentFieldProvenanceAsync(segment.Id, manualFields, ct);

        await db.SaveChangesAsync(ct);
        spanResolver.EvictVideo(videoId);
        await LoadTagAsync(segment, ct);
        return Ok(MapToDto(segment, await LoadSegmentFieldProvenanceAsync(segment.Id, ct)));
    }

    [HttpDelete("{id:int}")]
    [RequiresPermission(Permissions.SegmentsDelete)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.SegmentsDelete, RouteValueName = "videoId")]
    public async Task<IActionResult> Delete(int videoId, int id, CancellationToken ct)
    {
        // VideoSegmentsController.Delete only deletes persisted raw Segment rows.
        // Derived spans are computed by SegmentSpanResolver and never reach this endpoint.
        var segment = await db.Segments
            .FirstOrDefaultAsync(item => item.Id == id && item.HostType == SegmentHostType.Video && item.HostId == videoId, ct);
        if (segment is null) return NotFound();

        if (!string.IsNullOrWhiteSpace(segment.ImageBlobId))
            await blobService.DeleteBlobAsync(segment.ImageBlobId, ct);

        db.Segments.Remove(segment);
        await db.SaveChangesAsync(ct);
        spanResolver.EvictVideo(videoId);
        return NoContent();
    }

    private Task<bool> VideoExistsAsync(int videoId, CancellationToken ct) =>
        db.Videos.AsNoTracking().AnyAsync(video => video.Id == videoId, ct);

    private async Task LoadTagAsync(Segment segment, CancellationToken ct)
    {
        if (segment.TagId.HasValue)
            await db.Entry(segment).Reference(item => item.Tag).LoadAsync(ct);
    }

    private static SegmentDto MapToDto(Segment segment, IReadOnlyList<FieldProvenanceDto>? fieldProvenance = null) => new(
        segment.Id,
        segment.HostType,
        segment.HostId,
        segment.StartSec,
        segment.EndSec,
        segment.TagId,
        segment.Tag?.Name,
        segment.Kind,
        segment.RefId,
        segment.Payload?.RootElement.Clone(),
        segment.SourceKey,
        segment.SourceRunId,
        segment.Confidence,
        segment.Title,
        segment.ColorHint,
        segment.CreatedAt.ToString("o"),
        segment.UpdatedAt.ToString("o"),
        fieldProvenance?.ToList());

    private async Task<IReadOnlyList<FieldProvenanceDto>?> LoadSegmentFieldProvenanceAsync(int segmentId, CancellationToken cancellationToken)
        => fieldProvenanceService == null
            ? null
            : await fieldProvenanceService.GetForHostAsync(AffinityHostType.Segment, segmentId, cancellationToken);

    private async Task RecordManualSegmentFieldProvenanceAsync(Segment segment, CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken);
        var fields = new Dictionary<string, object?>
        {
            ["start_sec"] = segment.StartSec,
            ["end_sec"] = segment.EndSec,
            ["tag_id"] = segment.TagId,
            ["kind"] = segment.Kind,
            ["ref_id"] = segment.RefId,
            ["payload"] = segment.Payload?.RootElement.Clone(),
            ["source_key"] = segment.SourceKey,
            ["source_run_id"] = segment.SourceRunId,
            ["confidence"] = segment.Confidence,
            ["title"] = segment.Title,
            ["color_hint"] = segment.ColorHint,
        };
        await RecordManualSegmentFieldProvenanceAsync(segment.Id, fields, cancellationToken);
    }

    private Task RecordManualSegmentFieldProvenanceAsync(int segmentId, IReadOnlyDictionary<string, object?> fields, CancellationToken cancellationToken)
        => fieldProvenanceService == null || fields.Count == 0
            ? Task.CompletedTask
            : fieldProvenanceService.RecordManyAsync(AffinityHostType.Segment, segmentId, fields, "user", cancellationToken: cancellationToken);

    private static JsonDocument? ToDocument(JsonElement? payload) =>
        payload.HasValue ? JsonDocument.Parse(payload.Value.GetRawText()) : null;

    private static string NormalizeSourceKey(string? sourceKey) =>
        string.IsNullOrWhiteSpace(sourceKey) ? "user" : sourceKey;

    // A "tag" segment is the timeline occurrence of a tag, so it must point at a real tag.
    // (The label-only variants were stale data from an older AI.Tagging extension.)
    private static bool RequiresTagButMissing(string? kind, int? tagId) =>
        string.Equals(kind?.Trim(), "tag", StringComparison.OrdinalIgnoreCase) && (tagId is null || tagId <= 0);
}
