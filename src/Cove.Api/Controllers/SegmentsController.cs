using System.Text.Json;
using System.Linq.Expressions;
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
[Route("api/[controller]")]
[RequiresPermission(Permissions.SegmentsRead)]
public class SegmentsController(CoveContext db, SegmentSpanResolver spanResolver, IFieldProvenanceService? fieldProvenanceService = null) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PaginatedResponse<SegmentRecordDto>>> List(
        [FromQuery] string? q,
        [FromQuery] string? ids,
        [FromQuery] int? videoId,
        [FromQuery] string? videoIds,
        [FromQuery] string? videoTitle,
        [FromQuery] int? tagId,
        [FromQuery] string? tagIds,
        [FromQuery] string? kind,
        [FromQuery] string? sourceKey,
        [FromQuery] string? sourceCategory,
        [FromQuery] string? refIds,
        [FromQuery] string? performerIds,
        [FromQuery] bool? tagged,
        [FromQuery] float? minConfidence,
        [FromQuery] double? minDurationSec,
        [FromQuery] float? confidence,
        [FromQuery] float? confidence2,
        [FromQuery] string? confidenceModifier,
        [FromQuery] double? durationSec,
        [FromQuery] double? durationSec2,
        [FromQuery] string? durationModifier,
        [FromQuery] string? sort,
        [FromQuery] string? direction,
        [FromQuery] string? excludeVideoIds = null,
        [FromQuery] string? title = null,
        [FromQuery] string? titleModifier = null,
        [FromQuery] string? hostType = null,
        [FromQuery] string? sourceRunId = null,
        [FromQuery] string? sourceRunIdModifier = null,
        [FromQuery] string? colorHint = null,
        [FromQuery] string? colorHintModifier = null,
        [FromQuery] bool? hasImage = null,
        [FromQuery] bool? hasPayload = null,
        [FromQuery] double? startSec = null,
        [FromQuery] double? startSec2 = null,
        [FromQuery] string? startSecModifier = null,
        [FromQuery] double? endSec = null,
        [FromQuery] double? endSec2 = null,
        [FromQuery] string? endSecModifier = null,
        [FromQuery] string? createdAt = null,
        [FromQuery] string? createdAt2 = null,
        [FromQuery] string? createdAtModifier = null,
        [FromQuery] string? updatedAt = null,
        [FromQuery] string? updatedAt2 = null,
        [FromQuery] string? updatedAtModifier = null,
        [FromQuery] int page = 1,
        [FromQuery] int perPage = 48,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        perPage = Math.Clamp(perPage, 1, 250);
        var sortKey = NormalizeSort(sort);
        var descending = !string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase);

        var query =
            from segment in db.Segments.AsNoTracking()
            join video in db.Videos.AsNoTracking() on segment.HostId equals video.Id
            join tag in db.Tags.AsNoTracking() on segment.TagId equals tag.Id into tagJoin
            from tag in tagJoin.DefaultIfEmpty()
            join face in db.Faces.AsNoTracking() on segment.RefId equals (long?)face.Id into faceJoin
            from face in faceJoin.DefaultIfEmpty()
            join facePerformer in db.Performers.AsNoTracking() on face!.PerformerId equals (int?)facePerformer.Id into facePerformerJoin
            from facePerformer in facePerformerJoin.DefaultIfEmpty()
            join directPerformer in db.Performers.AsNoTracking() on segment.RefId equals (long?)directPerformer.Id into directPerformerJoin
            from directPerformer in directPerformerJoin.DefaultIfEmpty()
            where segment.HostType == SegmentHostType.Video
            select new SegmentLibraryRow
            {
                Segment = segment,
                VideoTitle = video.Title,
                TagName = tag != null ? tag.Name : null,
                RefLabel = face != null ? face.Label : segment.Kind != null && segment.Kind!.ToLower() == "performer" && directPerformer != null ? directPerformer!.Name : null,
                FaceId = face != null ? face!.Id : null,
                FacePerformerId = facePerformer != null ? facePerformer!.Id : null,
                DirectPerformerId = segment.Kind != null && segment.Kind!.ToLower() == "performer" && directPerformer != null ? directPerformer!.Id : null,
                PerformerId = segment.Kind != null && segment.Kind!.ToLower() == "performer" && directPerformer != null ? directPerformer!.Id : facePerformer != null ? facePerformer!.Id : null,
                PerformerName = segment.Kind != null && segment.Kind!.ToLower() == "performer" && directPerformer != null ? directPerformer!.Name : facePerformer != null ? facePerformer!.Name : null,
            };

        var parsedIds = ParseIdList(ids);
        if (parsedIds.Count > 0)
            query = query.Where(item => parsedIds.Contains(item.Segment.Id));

        var parsedVideoIds = ParseIdList(videoIds);
        var parsedExcludeVideoIds = ParseIdList(excludeVideoIds);
        if (videoId.HasValue)
            query = query.Where(item => item.Segment.HostId == videoId.Value);
        else if (parsedVideoIds.Count > 0)
            query = query.Where(item => parsedVideoIds.Contains(item.Segment.HostId));

        if (parsedExcludeVideoIds.Count > 0)
            query = query.Where(item => !parsedExcludeVideoIds.Contains(item.Segment.HostId));

        if (!string.IsNullOrWhiteSpace(videoTitle))
        {
            var normalizedVideoTitle = videoTitle.Trim().ToLowerInvariant();
            query = query.Where(item => item.VideoTitle != null && item.VideoTitle.ToLower().Contains(normalizedVideoTitle));
        }

        var parsedTagIds = ParseIdList(tagIds);
        if (tagId.HasValue)
            query = query.Where(item => item.Segment.TagId == tagId.Value);
        else if (parsedTagIds.Count > 0)
            query = query.Where(item => item.Segment.TagId.HasValue && parsedTagIds.Contains(item.Segment.TagId.Value));

        if (!string.IsNullOrWhiteSpace(kind))
        {
            var normalizedKind = kind.Trim().ToLowerInvariant();
            query = query.Where(item => item.Segment.Kind != null && item.Segment.Kind.ToLower().Contains(normalizedKind));
        }

        if (!string.IsNullOrWhiteSpace(sourceKey))
        {
            var normalizedSourceKey = sourceKey.Trim().ToLowerInvariant();
            query = query.Where(item => item.Segment.SourceKey.ToLower().Contains(normalizedSourceKey));
        }

        if (!string.IsNullOrWhiteSpace(sourceCategory))
        {
            var normalizedSourceCategory = sourceCategory.Trim().ToLowerInvariant();
            query = normalizedSourceCategory switch
            {
                "extensions" => query.Where(item => item.Segment.SourceKey.StartsWith("ext:")),
                "user" => query.Where(item => item.Segment.SourceKey == "user"),
                _ => query,
            };
        }

        var parsedRefIds = ParseLongIdList(refIds);
        if (parsedRefIds.Count > 0)
            query = query.Where(item => item.Segment.RefId.HasValue
                && parsedRefIds.Contains(item.Segment.RefId.Value)
                && item.Segment.Kind != null
                && item.Segment.Kind.ToLower() == "face");

        var parsedPerformerIds = ParseIdList(performerIds);
        if (parsedPerformerIds.Count > 0)
            query = query.Where(item =>
                (item.DirectPerformerId.HasValue && parsedPerformerIds.Contains(item.DirectPerformerId.Value)) ||
                (item.FacePerformerId.HasValue && parsedPerformerIds.Contains(item.FacePerformerId.Value)));

        if (tagged.HasValue)
            query = tagged.Value
                ? query.Where(item => item.Segment.TagId != null)
                : query.Where(item => item.Segment.TagId == null);

        if (minConfidence.HasValue)
            query = query.Where(item => item.Segment.Confidence.HasValue && item.Segment.Confidence.Value >= minConfidence.Value);

        if (confidence.HasValue)
            query = ApplyConfidenceCriterion(query, confidence.Value, confidence2, confidenceModifier);

        if (minDurationSec.HasValue)
            query = query.Where(item => ((item.Segment.EndSec ?? item.Segment.StartSec) - item.Segment.StartSec) >= minDurationSec.Value);

        if (durationSec.HasValue)
            query = ApplyDurationCriterion(query, durationSec.Value, durationSec2, durationModifier);

        query = ApplyStringCriterion(query, title, titleModifier, item => item.Segment.Title);
        query = ApplyStringCriterion(query, sourceRunId, sourceRunIdModifier, item => item.Segment.SourceRunId);
        query = ApplyStringCriterion(query, colorHint, colorHintModifier, item => item.Segment.ColorHint);

        if (!string.IsNullOrWhiteSpace(hostType) && Enum.TryParse<SegmentHostType>(hostType, true, out var parsedHostType))
            query = query.Where(item => item.Segment.HostType == parsedHostType);

        if (hasImage.HasValue)
            query = hasImage.Value
                ? query.Where(item => item.Segment.ImageBlobId != null && item.Segment.ImageBlobId != string.Empty)
                : query.Where(item => item.Segment.ImageBlobId == null || item.Segment.ImageBlobId == string.Empty);

        if (hasPayload.HasValue)
            query = hasPayload.Value
                ? query.Where(item => item.Segment.Payload != null)
                : query.Where(item => item.Segment.Payload == null);

        if (startSec.HasValue)
            query = ApplyDoubleCriterion(query, startSec.Value, startSec2, startSecModifier, item => item.Segment.StartSec);

        if (endSec.HasValue)
            query = ApplyDoubleCriterion(query, endSec.Value, endSec2, endSecModifier, item => item.Segment.EndSec ?? item.Segment.StartSec);

        if (TryParseDateTime(createdAt, out var createdAtValue))
            query = ApplyDateTimeCriterion(query, createdAtValue, createdAt2, createdAtModifier, item => item.Segment.CreatedAt);

        if (TryParseDateTime(updatedAt, out var updatedAtValue))
            query = ApplyDateTimeCriterion(query, updatedAtValue, updatedAt2, updatedAtModifier, item => item.Segment.UpdatedAt);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            var normalizedTerm = term.ToLowerInvariant();
            query = query.Where(item =>
                (item.Segment.Title != null && item.Segment.Title.ToLower().Contains(normalizedTerm)) ||
                (item.Segment.Kind != null && item.Segment.Kind.ToLower().Contains(normalizedTerm)) ||
                (item.TagName != null && item.TagName.ToLower().Contains(normalizedTerm)) ||
                (item.Segment.TagId.HasValue && db.Set<TagAlias>().Any(alias => alias.TagId == item.Segment.TagId.Value && alias.Alias.ToLower().Contains(normalizedTerm))) ||
                (item.RefLabel != null && item.RefLabel.ToLower().Contains(normalizedTerm)) ||
                (item.PerformerName != null && item.PerformerName.ToLower().Contains(normalizedTerm)) ||
                (item.FacePerformerId.HasValue && db.Set<PerformerAlias>().Any(alias => alias.PerformerId == item.FacePerformerId.Value && alias.Alias.ToLower().Contains(normalizedTerm))) ||
                (item.DirectPerformerId.HasValue && db.Set<PerformerAlias>().Any(alias => alias.PerformerId == item.DirectPerformerId.Value && alias.Alias.ToLower().Contains(normalizedTerm))) ||
                (item.VideoTitle != null && item.VideoTitle.ToLower().Contains(normalizedTerm)) ||
                item.Segment.SourceKey.ToLower().Contains(normalizedTerm));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var orderedQuery = ApplyOrdering(query, sortKey, descending);
        var items = await orderedQuery
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .ToListAsync(cancellationToken);

        return Ok(new PaginatedResponse<SegmentRecordDto>(items.Select(item => MapToDto(item)).ToList(), totalCount, page, perPage));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<SegmentRecordDto>> GetById(int id, CancellationToken cancellationToken)
    {
        var item = await (
            from segment in db.Segments.AsNoTracking()
            join video in db.Videos.AsNoTracking() on segment.HostId equals video.Id
            join tag in db.Tags.AsNoTracking() on segment.TagId equals tag.Id into tagJoin
            from tag in tagJoin.DefaultIfEmpty()
            join face in db.Faces.AsNoTracking() on segment.RefId equals (long?)face.Id into faceJoin
            from face in faceJoin.DefaultIfEmpty()
            join facePerformer in db.Performers.AsNoTracking() on face!.PerformerId equals (int?)facePerformer.Id into facePerformerJoin
            from facePerformer in facePerformerJoin.DefaultIfEmpty()
            join directPerformer in db.Performers.AsNoTracking() on segment.RefId equals (long?)directPerformer.Id into directPerformerJoin
            from directPerformer in directPerformerJoin.DefaultIfEmpty()
            where segment.HostType == SegmentHostType.Video && segment.Id == id
            select new SegmentLibraryRow
            {
                Segment = segment,
                VideoTitle = video.Title,
                TagName = tag != null ? tag.Name : null,
                RefLabel = face != null ? face.Label : segment.Kind != null && segment.Kind!.ToLower() == "performer" && directPerformer != null ? directPerformer!.Name : null,
                FaceId = face != null ? face!.Id : null,
                FacePerformerId = facePerformer != null ? facePerformer!.Id : null,
                DirectPerformerId = segment.Kind != null && segment.Kind!.ToLower() == "performer" && directPerformer != null ? directPerformer!.Id : null,
                PerformerId = segment.Kind != null && segment.Kind!.ToLower() == "performer" && directPerformer != null ? directPerformer!.Id : facePerformer != null ? facePerformer!.Id : null,
                PerformerName = segment.Kind != null && segment.Kind!.ToLower() == "performer" && directPerformer != null ? directPerformer!.Name : facePerformer != null ? facePerformer!.Name : null,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (item is null)
            return NotFound();

        var fieldProvenance = fieldProvenanceService == null
            ? null
            : await fieldProvenanceService.GetForHostAsync(AffinityHostType.Segment, item.Segment.Id, cancellationToken);

        return Ok(MapToDto(item, fieldProvenance));
    }

    [HttpPost("bulk/remove-tag")]
    [RequiresPermission(Permissions.SegmentsWrite)]
    public async Task<ActionResult<object>> RemoveTagFromSegments([FromBody] SegmentTagBulkRemoveRequest request, CancellationToken cancellationToken)
    {
        if (request.TagId <= 0)
            return BadRequest("A valid tag id is required.");

        var ids = request.Ids?.Where(id => id > 0).Distinct().ToArray() ?? [];
        if (ids.Length == 0)
            return BadRequest("At least one segment id is required.");

        var segments = await db.VisibleSegments()
            .Where(segment => ids.Contains(segment.Id) && segment.TagId == request.TagId)
            .ToListAsync(cancellationToken);

        var videoIds = segments
            .Where(segment => segment.HostType == SegmentHostType.Video)
            .Select(segment => segment.HostId)
            .Distinct()
            .ToArray();

        var now = DateTime.UtcNow;
        foreach (var segment in segments)
        {
            // A "tag" segment only exists to mark where that tag occurs — removing the tag
            // leaves nothing meaningful, so delete it rather than orphan a kind=tag row with no tag.
            if (string.Equals(segment.Kind?.Trim(), "tag", StringComparison.OrdinalIgnoreCase))
            {
                db.Segments.Remove(segment);
                continue;
            }

            segment.TagId = null;
            segment.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var videoId in videoIds)
            spanResolver.EvictVideo(videoId);

        return Ok(new { count = segments.Count });
    }

    [HttpGet("source-keys/distinct")]
    public async Task<ActionResult<IReadOnlyList<SegmentDistinctValueDto>>> DistinctSourceKeys(CancellationToken cancellationToken)
    {
        var values = await db.VisibleSegments().AsNoTracking()
            .Where(segment => segment.HostType == SegmentHostType.Video && !string.IsNullOrWhiteSpace(segment.SourceKey))
            .GroupBy(segment => segment.SourceKey)
            .Select(group => new
            {
                Value = group.Key,
                Count = group.Count(),
            })
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Value)
            .Take(200)
            .ToListAsync(cancellationToken);

        var items = values
            .Select(item => new SegmentDistinctValueDto(item.Value!, item.Count))
            .ToList();

        return Ok(items);
    }

    [HttpGet("kinds/distinct")]
    public async Task<ActionResult<IReadOnlyList<SegmentDistinctValueDto>>> DistinctKinds(CancellationToken cancellationToken)
    {
        var values = await db.VisibleSegments().AsNoTracking()
            .Where(segment => segment.HostType == SegmentHostType.Video && segment.Kind != null && segment.Kind != string.Empty)
            .GroupBy(segment => segment.Kind!)
            .Select(group => new
            {
                Value = group.Key,
                Count = group.Count(),
            })
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Value)
            .Take(200)
            .ToListAsync(cancellationToken);

        var items = values
            .Select(item => new SegmentDistinctValueDto(item.Value, item.Count))
            .ToList();

        return Ok(items);
    }

    private static SegmentRecordDto MapToDto(SegmentLibraryRow item, IReadOnlyList<FieldProvenanceDto>? fieldProvenance = null) => new(
        item.Segment.Id,
        item.Segment.HostType,
        item.Segment.HostId,
        item.VideoTitle,
        item.Segment.StartSec,
        item.Segment.EndSec,
        item.Segment.TagId,
        item.TagName,
        item.Segment.Kind,
        item.Segment.RefId,
        item.RefLabel,
        item.PerformerId,
        item.PerformerName,
        item.Segment.Payload != null ? item.Segment.Payload.RootElement.Clone() : (JsonElement?)null,
        item.Segment.SourceKey,
        item.Segment.SourceRunId,
        item.Segment.Confidence,
        item.Segment.Title,
        item.Segment.ColorHint,
        item.Segment.CreatedAt.ToString("o"),
        item.Segment.UpdatedAt.ToString("o"),
        fieldProvenance?.ToList());

    private static string NormalizeSort(string? sort)
    {
        if (string.IsNullOrWhiteSpace(sort))
            return "updated_at";

        return sort.Trim().ToLowerInvariant();
    }

    private static IQueryable<SegmentLibraryRow> ApplyConfidenceCriterion(IQueryable<SegmentLibraryRow> query, float value, float? value2, string? modifier)
    {
        return NormalizeCriterionModifier(modifier) switch
        {
            "NOT_EQUALS" => query.Where(item => !item.Segment.Confidence.HasValue || item.Segment.Confidence.Value != value),
            "LESS_THAN" => query.Where(item => item.Segment.Confidence.HasValue && item.Segment.Confidence.Value < value),
            "BETWEEN" when value2.HasValue => query.Where(item => item.Segment.Confidence.HasValue && item.Segment.Confidence.Value >= Math.Min(value, value2.Value) && item.Segment.Confidence.Value <= Math.Max(value, value2.Value)),
            "NOT_BETWEEN" when value2.HasValue => query.Where(item => !item.Segment.Confidence.HasValue || item.Segment.Confidence.Value < Math.Min(value, value2.Value) || item.Segment.Confidence.Value > Math.Max(value, value2.Value)),
            "EQUALS" => query.Where(item => item.Segment.Confidence.HasValue && item.Segment.Confidence.Value == value),
            _ => query.Where(item => item.Segment.Confidence.HasValue && item.Segment.Confidence.Value > value),
        };
    }

    private static IQueryable<SegmentLibraryRow> ApplyDurationCriterion(IQueryable<SegmentLibraryRow> query, double value, double? value2, string? modifier)
    {
        return NormalizeCriterionModifier(modifier) switch
        {
            "NOT_EQUALS" => query.Where(item => ((item.Segment.EndSec ?? item.Segment.StartSec) - item.Segment.StartSec) != value),
            "LESS_THAN" => query.Where(item => ((item.Segment.EndSec ?? item.Segment.StartSec) - item.Segment.StartSec) < value),
            "BETWEEN" when value2.HasValue => query.Where(item => ((item.Segment.EndSec ?? item.Segment.StartSec) - item.Segment.StartSec) >= Math.Min(value, value2.Value) && ((item.Segment.EndSec ?? item.Segment.StartSec) - item.Segment.StartSec) <= Math.Max(value, value2.Value)),
            "NOT_BETWEEN" when value2.HasValue => query.Where(item => ((item.Segment.EndSec ?? item.Segment.StartSec) - item.Segment.StartSec) < Math.Min(value, value2.Value) || ((item.Segment.EndSec ?? item.Segment.StartSec) - item.Segment.StartSec) > Math.Max(value, value2.Value)),
            "EQUALS" => query.Where(item => ((item.Segment.EndSec ?? item.Segment.StartSec) - item.Segment.StartSec) == value),
            _ => query.Where(item => ((item.Segment.EndSec ?? item.Segment.StartSec) - item.Segment.StartSec) > value),
        };
    }

    private static IQueryable<SegmentLibraryRow> ApplyDoubleCriterion(
        IQueryable<SegmentLibraryRow> query,
        double value,
        double? value2,
        string? modifier,
        Expression<Func<SegmentLibraryRow, double>> selector)
    {
        var normalized = NormalizeCriterionModifier(modifier);
        var upper = value2 ?? value;
        return normalized switch
        {
            "NOT_EQUALS" => WhereCompare(query, selector, value, ExpressionType.NotEqual),
            "LESS_THAN" => WhereCompare(query, selector, value, ExpressionType.LessThan),
            "BETWEEN" => WhereBetween(query, selector, Math.Min(value, upper), Math.Max(value, upper)),
            "NOT_BETWEEN" => WhereNotBetween(query, selector, Math.Min(value, upper), Math.Max(value, upper)),
            "EQUALS" => WhereCompare(query, selector, value, ExpressionType.Equal),
            _ => WhereCompare(query, selector, value, ExpressionType.GreaterThan),
        };
    }

    private static IQueryable<SegmentLibraryRow> ApplyDateTimeCriterion(
        IQueryable<SegmentLibraryRow> query,
        DateTime value,
        string? value2,
        string? modifier,
        Expression<Func<SegmentLibraryRow, DateTime>> selector)
    {
        _ = TryParseDateTime(value2, out var parsedValue2);
        var upper = parsedValue2 == default ? value : parsedValue2;
        return NormalizeCriterionModifier(modifier) switch
        {
            "NOT_EQUALS" => WhereCompare(query, selector, value, ExpressionType.NotEqual),
            "LESS_THAN" => WhereCompare(query, selector, value, ExpressionType.LessThan),
            "BETWEEN" => WhereBetween(query, selector, value < upper ? value : upper, value < upper ? upper : value),
            "NOT_BETWEEN" => WhereNotBetween(query, selector, value < upper ? value : upper, value < upper ? upper : value),
            "EQUALS" => WhereCompare(query, selector, value, ExpressionType.Equal),
            _ => WhereCompare(query, selector, value, ExpressionType.GreaterThan),
        };
    }

    private static IQueryable<SegmentLibraryRow> ApplyStringCriterion(
        IQueryable<SegmentLibraryRow> query,
        string? value,
        string? modifier,
        Expression<Func<SegmentLibraryRow, string?>> selector)
    {
        var normalized = NormalizeCriterionModifier(modifier);
        if (normalized is "IS_NULL" or "NOT_NULL")
        {
            var param = selector.Parameters[0];
            var nullCheck = normalized == "IS_NULL"
                ? Expression.Equal(selector.Body, Expression.Constant(null, typeof(string)))
                : Expression.NotEqual(selector.Body, Expression.Constant(null, typeof(string)));
            return query.Where(Expression.Lambda<Func<SegmentLibraryRow, bool>>(nullCheck, param));
        }

        if (string.IsNullOrWhiteSpace(value))
            return query;

        var normalizedValue = value.Trim().ToLowerInvariant();
        var parameter = selector.Parameters[0];
        var body = Expression.Coalesce(selector.Body, Expression.Constant(string.Empty));
        var lowered = Expression.Call(body, typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!);
        var constant = Expression.Constant(normalizedValue);
        var contains = Expression.Call(lowered, typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!, constant);
        var equals = Expression.Equal(lowered, constant);

        Expression predicate = normalized switch
        {
            "EQUALS" => equals,
            "NOT_EQUALS" => Expression.Not(equals),
            "EXCLUDES" => Expression.Not(contains),
            _ => contains,
        };

        return query.Where(Expression.Lambda<Func<SegmentLibraryRow, bool>>(predicate, parameter));
    }

    private static IQueryable<SegmentLibraryRow> WhereCompare<T>(IQueryable<SegmentLibraryRow> query, Expression<Func<SegmentLibraryRow, T>> selector, T value, ExpressionType comparison)
    {
        var predicate = Expression.MakeBinary(comparison, selector.Body, Expression.Constant(value));
        return query.Where(Expression.Lambda<Func<SegmentLibraryRow, bool>>(predicate, selector.Parameters[0]));
    }

    private static IQueryable<SegmentLibraryRow> WhereBetween<T>(IQueryable<SegmentLibraryRow> query, Expression<Func<SegmentLibraryRow, T>> selector, T lower, T upper)
    {
        var body = selector.Body;
        var predicate = Expression.AndAlso(
            Expression.GreaterThanOrEqual(body, Expression.Constant(lower)),
            Expression.LessThanOrEqual(body, Expression.Constant(upper)));
        return query.Where(Expression.Lambda<Func<SegmentLibraryRow, bool>>(predicate, selector.Parameters[0]));
    }

    private static IQueryable<SegmentLibraryRow> WhereNotBetween<T>(IQueryable<SegmentLibraryRow> query, Expression<Func<SegmentLibraryRow, T>> selector, T lower, T upper)
    {
        var body = selector.Body;
        var predicate = Expression.OrElse(
            Expression.LessThan(body, Expression.Constant(lower)),
            Expression.GreaterThan(body, Expression.Constant(upper)));
        return query.Where(Expression.Lambda<Func<SegmentLibraryRow, bool>>(predicate, selector.Parameters[0]));
    }

    private static bool TryParseDateTime(string? value, out DateTime parsed)
    {
        if (DateTime.TryParse(value, out parsed))
        {
            if (parsed.Kind == DateTimeKind.Unspecified)
                parsed = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return true;
        }

        parsed = default;
        return false;
    }

    private static string NormalizeCriterionModifier(string? modifier)
        => string.IsNullOrWhiteSpace(modifier) ? "GREATER_THAN" : modifier.Trim().ToUpperInvariant();

    private static List<int> ParseIdList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var parsed) ? parsed : (int?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .Distinct()
            .ToList();
    }

    private static List<long> ParseLongIdList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => long.TryParse(value, out var parsed) ? parsed : (long?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .Distinct()
            .ToList();
    }

    private static IOrderedQueryable<SegmentLibraryRow> ApplyOrdering(IQueryable<SegmentLibraryRow> query, string sort, bool descending)
    {
        return sort switch
        {
            "created_at" => OrderBy(query, item => item.Segment.CreatedAt, descending),
            "start_sec" => OrderBy(query, item => item.Segment.StartSec, descending),
            "end_sec" => OrderBy(query, item => item.Segment.EndSec ?? item.Segment.StartSec, descending),
            "duration" => OrderBy(query, item => (item.Segment.EndSec ?? item.Segment.StartSec) - item.Segment.StartSec, descending),
            "confidence" => OrderBy(query, item => item.Segment.Confidence ?? -1f, descending),
            "title" => OrderBy(query, item => item.Segment.Title ?? item.Segment.Kind ?? item.TagName ?? string.Empty, descending),
            "video_title" => OrderBy(query, item => item.VideoTitle ?? string.Empty, descending),
            "kind" => OrderBy(query, item => item.Segment.Kind ?? string.Empty, descending),
            "source_key" => OrderBy(query, item => item.Segment.SourceKey, descending),
            "tag_name" => OrderBy(query, item => item.TagName ?? string.Empty, descending),
            "performer" => OrderBy(query, item => item.PerformerName ?? string.Empty, descending),
            "ref" => OrderBy(query, item => item.RefLabel ?? item.PerformerName ?? string.Empty, descending),
            _ => OrderBy(query, item => item.Segment.UpdatedAt, descending),
        };
    }

    private static IOrderedQueryable<SegmentLibraryRow> OrderBy<T>(
        IQueryable<SegmentLibraryRow> query,
        Expression<Func<SegmentLibraryRow, T>> keySelector,
        bool descending)
    {
        return descending
            ? query.OrderByDescending(keySelector).ThenByDescending(item => item.Segment.Id)
            : query.OrderBy(keySelector).ThenBy(item => item.Segment.Id);
    }

    private sealed class SegmentLibraryRow
    {
        public required Segment Segment { get; init; }
        public string? VideoTitle { get; init; }
        public string? TagName { get; init; }
        public string? RefLabel { get; init; }
        public int? FaceId { get; init; }
        public int? FacePerformerId { get; init; }
        public int? DirectPerformerId { get; init; }
        public int? PerformerId { get; init; }
        public string? PerformerName { get; init; }
    }

    public sealed record SegmentTagBulkRemoveRequest(int TagId, IReadOnlyList<int>? Ids);

    // ===== Span Search =====

    [HttpPost("spans/search")]
    public async Task<ActionResult<SegmentSpanSearchResponseDto>> SearchSpans(
        [FromBody] SegmentSpanSearchRequestDto request,
        CancellationToken ct)
    {
        var page = Math.Max(1, request.Page ?? 1);
        var perPage = Math.Clamp(request.PerPage ?? 24, 1, 100);
        var sort = (request.Sort ?? "updated_at").Trim().ToLowerInvariant();
        var descending = !string.Equals(request.Direction, "asc", StringComparison.OrdinalIgnoreCase);

        // 1. Gather video IDs matching the scope filters.
        List<(int Id, string? Title, DateTimeOffset UpdatedAt)> videoList;
        if (request.VideoIds is { Length: > 0 })
        {
            var idSet = request.VideoIds.ToHashSet();
            var excludeSet = request.ExcludeVideoIds?.ToHashSet() ?? [];
            videoList = await db.Videos.AsNoTracking()
                .Where(s => idSet.Contains(s.Id) && !excludeSet.Contains(s.Id))
                .OrderBy(s => s.Id)
                .Select(s => new { s.Id, s.Title, s.UpdatedAt })
                .ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(s => (s.Id, (string?)s.Title, (DateTimeOffset)s.UpdatedAt)).ToList(), TaskContinuationOptions.ExecuteSynchronously);
        }
        else
        {
            var excludeSet = request.ExcludeVideoIds?.ToHashSet() ?? [];
            var videoQuery = db.Videos.AsNoTracking()
                .Where(s => !excludeSet.Contains(s.Id));

            if (!string.IsNullOrWhiteSpace(request.VideoTitle))
            {
                var titleTerm = request.VideoTitle.Trim();
                videoQuery = videoQuery.Where(s => s.Title != null && s.Title.Contains(titleTerm));
            }

            videoQuery = (sort, descending) switch
            {
                ("title", false) => videoQuery.OrderBy(s => s.Title),
                ("title", true) => videoQuery.OrderByDescending(s => s.Title),
                ("created_at", false) => videoQuery.OrderBy(s => s.CreatedAt),
                ("created_at", true) => videoQuery.OrderByDescending(s => s.CreatedAt),
                (_, false) => videoQuery.OrderBy(s => s.UpdatedAt),
                _ => videoQuery.OrderByDescending(s => s.UpdatedAt),
            };

            videoList = await videoQuery
                .Select(s => new { s.Id, s.Title, s.UpdatedAt })
                .ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(s => (s.Id, (string?)s.Title, (DateTimeOffset)s.UpdatedAt)).ToList(), TaskContinuationOptions.ExecuteSynchronously);
        }

        // 2. Resolve the active profile ID once.
        var profileId = await spanResolver.ResolveProfileIdAsync(request.Profile, ct);

        // 3. Build the derived query request if applicable.
        var derivedQueryRequest = request.DerivedQuery is { } dq
            ? new SegmentSpanQueryRequestDto(
                profileId,
                dq.Operator,
                dq.Operands,
                dq.MergeGapSec,
                dq.MinDurationSec)
            : null;

        // 4. Determine whether we can terminate early — only when sort is video-level and
        //    no segment-row-dependent filtering is needed. Video-level sorts mean the
        //    video query already produces rows in the correct final order.
        var canTerminateEarly = IsVideoLevelSort(sort) && !NeedsSegmentRows(request);
        var neededCount = page * perPage;

        var allItems = new List<SegmentSpanSearchResultItemDto>(videoList.Count * 2);
        const int batchSize = 16;
        var videoMap = videoList.ToDictionary(v => v.Id);

        for (var i = 0; i < videoList.Count; i += batchSize)
        {
            var batch = videoList.Skip(i).Take(batchSize).ToList();
            var batchVideoIds = batch.Select(v => v.Id).ToList();

            IReadOnlyList<(int VideoId, IReadOnlyList<ResolvedSpan> Spans)> batchResults;
            if (derivedQueryRequest is not null)
            {
                batchResults = await spanResolver.QueryVideosBatchAsync(batchVideoIds, derivedQueryRequest, ct);
            }
            else
            {
                batchResults = await spanResolver.ResolveVideosBatchAsync(batchVideoIds, profileId, ct);
            }

            foreach (var (videoId, spans) in batchResults)
            {
                if (!videoMap.TryGetValue(videoId, out var video))
                    continue;

                foreach (var span in spans)
                    allItems.Add(new SegmentSpanSearchResultItemDto(span, video.Id, video.Title, video.UpdatedAt.ToString("o"), profileId));
            }

            if (canTerminateEarly && allItems.Count >= neededCount && (i + batch.Count) < videoList.Count)
            {
                // We have enough items for the requested page. Return a conservative
                // totalCount so the frontend shows at least one more page when relevant.
                var conservativeTotal = Math.Max(allItems.Count, neededCount + 1);
                var offset = (page - 1) * perPage;
                var pageItems = allItems.Skip(offset).Take(perPage).ToList();
                return Ok(new SegmentSpanSearchResponseDto(pageItems, conservativeTotal, page, perPage));
            }
        }

        // 5. Full path: apply segment-level filtering and sorting.
        IReadOnlyDictionary<int, SegmentSearchRow> segmentRows = new Dictionary<int, SegmentSearchRow>();
        if (allItems.Count > 0 && NeedsSegmentRows(request))
        {
            segmentRows = await LoadSpanSegmentRowsAsync(allItems.SelectMany(item => item.Span.SegmentIds), ct);
            allItems = allItems
                .Where(item => MatchesSpanSearchRequest(item, request, segmentRows))
                .ToList();
        }

        if (IsSpanLevelSort(sort))
            allItems = ApplySpanOrdering(allItems, sort, descending, segmentRows).ToList();

        var totalCount = allItems.Count;
        var finalOffset = (page - 1) * perPage;
        var finalPageItems = allItems.Skip(finalOffset).Take(perPage).ToList();

        return Ok(new SegmentSpanSearchResponseDto(finalPageItems, totalCount, page, perPage));
    }

    private static bool IsVideoLevelSort(string? sort)
        => (sort ?? string.Empty).Trim().ToLowerInvariant() is "updated_at" or "created_at" or "title";

    private static bool NeedsSegmentRows(SegmentSpanSearchRequestDto request)
        => !string.IsNullOrWhiteSpace(request.Q)
            || !string.IsNullOrWhiteSpace(request.Title)
            || !string.IsNullOrWhiteSpace(request.Kind)
            || !string.IsNullOrWhiteSpace(request.SourceKey)
            || !string.IsNullOrWhiteSpace(request.SourceCategory)
            || !string.IsNullOrWhiteSpace(request.SourceRunId)
            || !string.IsNullOrWhiteSpace(request.ColorHint)
            || !string.IsNullOrWhiteSpace(request.HostType)
            || request.HasImage.HasValue
            || request.HasPayload.HasValue
            || request.StartSec.HasValue
            || request.EndSec.HasValue
            || !string.IsNullOrWhiteSpace(request.CreatedAt)
            || !string.IsNullOrWhiteSpace(request.UpdatedAt)
            || request.TagIds is { Length: > 0 }
            || request.RefIds is { Length: > 0 }
            || request.PerformerIds is { Length: > 0 }
            || request.Confidence.HasValue
            || request.DurationSec.HasValue
            || SortNeedsSegmentRows(request.Sort);

    private static bool SortNeedsSegmentRows(string? sort)
        => (sort ?? string.Empty).Trim().ToLowerInvariant() is "segment_confidence" or "confidence" or "segment_count" or "segment_created_at" or "segment_updated_at" or "source_run_id" or "segment_source_run_id" or "performer" or "segment_performer" or "ref" or "segment_ref" or "host_type" or "host_id";

    private static bool IsSpanLevelSort(string sort)
        => sort is "start_sec" or "span_start" or "end_sec" or "span_end" or "duration" or "span_duration" or "kind" or "segment_kind" or "source_key" or "segment_source_key" or "tag_name" or "segment_tag_name" or "segment_count" or "segment_confidence" or "confidence" or "segment_created_at" or "segment_updated_at" or "source_run_id" or "segment_source_run_id" or "performer" or "segment_performer" or "ref" or "segment_ref" or "host_title" or "host_type" or "host_id";

    private static IOrderedEnumerable<SegmentSpanSearchResultItemDto> ApplySpanOrdering(
        IEnumerable<SegmentSpanSearchResultItemDto> items,
        string sort,
        bool descending,
        IReadOnlyDictionary<int, SegmentSearchRow> segmentRows)
    {
        return sort switch
        {
            "start_sec" or "span_start" => OrderSpanBy(items, item => item.Span.StartSec, descending),
            "end_sec" or "span_end" => OrderSpanBy(items, item => item.Span.EndSec, descending),
            "duration" or "span_duration" => OrderSpanBy(items, item => item.Span.EndSec - item.Span.StartSec, descending),
            "kind" or "segment_kind" => OrderSpanBy(items, item => item.Span.Kind ?? string.Empty, descending),
            "source_key" or "segment_source_key" => OrderSpanBy(items, item => item.Span.SourceKey ?? string.Empty, descending),
            "tag_name" or "segment_tag_name" => OrderSpanBy(items, item => item.Span.TagName ?? string.Empty, descending),
            "segment_count" => OrderSpanBy(items, item => item.Span.SegmentIds.Count, descending),
            "segment_confidence" or "confidence" => OrderSpanBy(items, item => MaxSpanConfidence(item, segmentRows), descending),
            "segment_created_at" => OrderSpanBy(items, item => EarliestSpanDateKey(item, segmentRows, row => row.CreatedAt), descending),
            "segment_updated_at" => OrderSpanBy(items, item => LatestSpanDateKey(item, segmentRows, row => row.UpdatedAt), descending),
            "source_run_id" or "segment_source_run_id" => OrderSpanBy(items, item => SpanTextKey(item, segmentRows, row => row.SourceRunId), descending),
            "performer" or "segment_performer" => OrderSpanBy(items, item => SpanTextKey(item, segmentRows, row => row.PerformerName), descending),
            "ref" or "segment_ref" => OrderSpanBy(items, item => SpanTextKey(item, segmentRows, row => row.RefLabel ?? row.PerformerName), descending),
            "host_title" => OrderSpanBy(items, item => item.VideoTitle ?? string.Empty, descending),
            "host_type" => OrderSpanBy(items, item => item.Span.HostType.ToString(), descending),
            "host_id" => OrderSpanBy(items, item => item.Span.HostId, descending),
            _ => OrderSpanBy(items, item => item.VideoUpdatedAt ?? string.Empty, descending),
        };
    }

    private static IOrderedEnumerable<SegmentSpanSearchResultItemDto> OrderSpanBy<TKey>(
        IEnumerable<SegmentSpanSearchResultItemDto> items,
        Func<SegmentSpanSearchResultItemDto, TKey> keySelector,
        bool descending)
        => descending
            ? items.OrderByDescending(keySelector).ThenByDescending(item => item.VideoId).ThenByDescending(item => item.Span.StartSec)
            : items.OrderBy(keySelector).ThenBy(item => item.VideoId).ThenBy(item => item.Span.StartSec);

    private static float MaxSpanConfidence(SegmentSpanSearchResultItemDto item, IReadOnlyDictionary<int, SegmentSearchRow> segmentRows)
        => item.Span.SegmentIds
            .Select(id => segmentRows.TryGetValue(id, out var row) ? row.Confidence : null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty(-1f)
            .Max();

    private static DateTime EarliestSpanDateKey(SegmentSpanSearchResultItemDto item, IReadOnlyDictionary<int, SegmentSearchRow> segmentRows, Func<SegmentSearchRow, DateTime> selector)
        => SpanDateKey(item, segmentRows, selector, values => values.Min());

    private static DateTime LatestSpanDateKey(SegmentSpanSearchResultItemDto item, IReadOnlyDictionary<int, SegmentSearchRow> segmentRows, Func<SegmentSearchRow, DateTime> selector)
        => SpanDateKey(item, segmentRows, selector, values => values.Max());

    private static DateTime SpanDateKey(
        SegmentSpanSearchResultItemDto item,
        IReadOnlyDictionary<int, SegmentSearchRow> segmentRows,
        Func<SegmentSearchRow, DateTime> selector,
        Func<IReadOnlyList<DateTime>, DateTime> aggregate)
    {
        var values = item.Span.SegmentIds
            .Select(id => segmentRows.TryGetValue(id, out var row) ? selector(row) : (DateTime?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();

        if (values.Count == 0)
            return DateTime.MinValue;

        return aggregate(values);
    }

    private static string SpanTextKey(SegmentSpanSearchResultItemDto item, IReadOnlyDictionary<int, SegmentSearchRow> segmentRows, Func<SegmentSearchRow, string?> selector)
        => item.Span.SegmentIds
            .Select(id => segmentRows.TryGetValue(id, out var row) ? selector(row) : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Order(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? string.Empty;

    private async Task<Dictionary<int, SegmentSearchRow>> LoadSpanSegmentRowsAsync(IEnumerable<int> segmentIds, CancellationToken ct)
    {
        var ids = segmentIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
            return [];

        var rows = await (
            from segment in db.Segments.AsNoTracking()
            join tag in db.Tags.AsNoTracking() on segment.TagId equals tag.Id into tagJoin
            from tag in tagJoin.DefaultIfEmpty()
            join face in db.Faces.AsNoTracking() on segment.RefId equals (long?)face.Id into faceJoin
            from face in faceJoin.DefaultIfEmpty()
            join facePerformer in db.Performers.AsNoTracking() on face!.PerformerId equals (int?)facePerformer.Id into facePerformerJoin
            from facePerformer in facePerformerJoin.DefaultIfEmpty()
            join directPerformer in db.Performers.AsNoTracking() on segment.RefId equals (long?)directPerformer.Id into directPerformerJoin
            from directPerformer in directPerformerJoin.DefaultIfEmpty()
            where ids.Contains(segment.Id)
            select new SegmentSearchRow
            {
                Id = segment.Id,
                Title = segment.Title,
                HostType = segment.HostType,
                HostId = segment.HostId,
                StartSec = segment.StartSec,
                EndSec = segment.EndSec,
                SourceKey = segment.SourceKey,
                SourceRunId = segment.SourceRunId,
                Kind = segment.Kind,
                TagId = segment.TagId,
                TagName = tag != null ? tag.Name : null,
                RefId = segment.RefId,
                RefLabel = face != null ? face.Label : segment.Kind != null && segment.Kind!.ToLower() == "performer" && directPerformer != null ? directPerformer!.Name : null,
                FacePerformerId = facePerformer != null ? facePerformer!.Id : null,
                DirectPerformerId = segment.Kind != null && segment.Kind!.ToLower() == "performer" && directPerformer != null ? directPerformer!.Id : null,
                PerformerName = segment.Kind != null && segment.Kind!.ToLower() == "performer" && directPerformer != null ? directPerformer!.Name : facePerformer != null ? facePerformer!.Name : null,
                Confidence = segment.Confidence,
                ColorHint = segment.ColorHint,
                HasImage = segment.ImageBlobId != null && segment.ImageBlobId != string.Empty,
                HasPayload = segment.Payload != null,
                CreatedAt = segment.CreatedAt,
                UpdatedAt = segment.UpdatedAt,
            })
            .ToListAsync(ct);

        return rows.ToDictionary(row => row.Id);
    }

    private static bool MatchesSpanSearchRequest(SegmentSpanSearchResultItemDto item, SegmentSpanSearchRequestDto request, IReadOnlyDictionary<int, SegmentSearchRow> segmentRows)
    {
        var rows = item.Span.SegmentIds
            .Select(id => segmentRows.TryGetValue(id, out var row) ? row : null)
            .Where(row => row is not null)
            .Select(row => row!)
            .ToList();

        if (!MatchesStringCriterion(rows.Select(row => row.Title), request.Title, request.TitleModifier))
            return false;

        if (!MatchesHostTypeCriterion(item, rows, request.HostType))
            return false;

        if (!MatchesSourceCategory(item.Span.SourceKey, rows.Select(row => row.SourceKey), request.SourceCategory))
            return false;

        if (!MatchesStringCriterion(rows.Select(row => row.SourceRunId), request.SourceRunId, request.SourceRunIdModifier))
            return false;

        if (!MatchesStringCriterion(rows.Select(row => row.ColorHint), request.ColorHint, request.ColorHintModifier))
            return false;

        if (!MatchesBool(rows.Select(row => row.HasImage), request.HasImage))
            return false;

        if (!MatchesBool(rows.Select(row => row.HasPayload), request.HasPayload))
            return false;

        if (request.StartSec.HasValue && !MatchesNumberCriterion(item.Span.StartSec, request.StartSec.Value, request.StartSec2, request.StartSecModifier))
            return false;

        if (request.EndSec.HasValue && !MatchesNumberCriterion(item.Span.EndSec, request.EndSec.Value, request.EndSec2, request.EndSecModifier))
            return false;

        if (!MatchesDateCriterion(rows.Select(row => row.CreatedAt), request.CreatedAt, request.CreatedAt2, request.CreatedAtModifier))
            return false;

        if (!MatchesDateCriterion(rows.Select(row => row.UpdatedAt), request.UpdatedAt, request.UpdatedAt2, request.UpdatedAtModifier))
            return false;

        if (!string.IsNullOrWhiteSpace(request.Kind))
        {
            var kind = request.Kind.Trim();
            if (!EqualsIgnoreCase(item.Span.Kind, kind) && !rows.Any(row => EqualsIgnoreCase(row.Kind, kind)))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(request.SourceKey))
        {
            var sourceKey = request.SourceKey.Trim();
            if (!EqualsIgnoreCase(item.Span.SourceKey, sourceKey) && !rows.Any(row => EqualsIgnoreCase(row.SourceKey, sourceKey)))
                return false;
        }

        if (request.TagIds is { Length: > 0 })
        {
            var tagIds = request.TagIds.ToHashSet();
            if (!(item.Span.TagId.HasValue && tagIds.Contains(item.Span.TagId.Value)) && !rows.Any(row => row.TagId.HasValue && tagIds.Contains(row.TagId.Value)))
                return false;
        }

        if (request.RefIds is { Length: > 0 })
        {
            var refIds = request.RefIds.ToHashSet();
            if (!rows.Any(row => row.RefId.HasValue && refIds.Contains(row.RefId.Value)))
                return false;
        }

        if (request.PerformerIds is { Length: > 0 })
        {
            var performerIds = request.PerformerIds.ToHashSet();
            if (!rows.Any(row => (row.DirectPerformerId.HasValue && performerIds.Contains(row.DirectPerformerId.Value)) || (row.FacePerformerId.HasValue && performerIds.Contains(row.FacePerformerId.Value))))
                return false;
        }

        if (request.Confidence.HasValue && !rows.Any(row => row.Confidence.HasValue && MatchesNumberCriterion(row.Confidence.Value, request.Confidence.Value, request.Confidence2, request.ConfidenceModifier)))
            return false;

        if (request.DurationSec.HasValue && !MatchesNumberCriterion(item.Span.EndSec - item.Span.StartSec, request.DurationSec.Value, request.DurationSec2, request.DurationModifier))
            return false;

        if (!string.IsNullOrWhiteSpace(request.Q))
        {
            var term = request.Q.Trim();
            if (!ContainsIgnoreCase(item.VideoTitle, term)
                && !ContainsIgnoreCase(item.Span.SpanKey, term)
                && !ContainsIgnoreCase(item.Span.SourceKey, term)
                && !ContainsIgnoreCase(item.Span.Kind, term)
                && !ContainsIgnoreCase(item.Span.TagName, term)
                && !rows.Any(row => ContainsIgnoreCase(row.Title, term)
                    || ContainsIgnoreCase(row.SourceKey, term)
                    || ContainsIgnoreCase(row.Kind, term)
                    || ContainsIgnoreCase(row.TagName, term)
                    || ContainsIgnoreCase(row.RefLabel, term)
                    || ContainsIgnoreCase(row.PerformerName, term)))
                return false;
        }

        return true;
    }

    private static bool MatchesStringCriterion(IEnumerable<string?> actualValues, string? expected, string? modifier)
    {
        var normalized = NormalizeCriterionModifier(modifier);
        var values = actualValues.ToList();
        if (normalized == "IS_NULL")
            return values.Count == 0 || values.All(string.IsNullOrWhiteSpace);
        if (normalized == "NOT_NULL")
            return values.Any(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(expected))
            return true;

        var text = expected.Trim();
        return normalized switch
        {
            "EQUALS" => values.Any(value => EqualsIgnoreCase(value, text)),
            "NOT_EQUALS" => !values.Any(value => EqualsIgnoreCase(value, text)),
            "EXCLUDES" => !values.Any(value => ContainsIgnoreCase(value, text)),
            _ => values.Any(value => ContainsIgnoreCase(value, text)),
        };
    }

    private static bool MatchesHostTypeCriterion(SegmentSpanSearchResultItemDto item, IEnumerable<SegmentSearchRow> rows, string? hostType)
    {
        if (string.IsNullOrWhiteSpace(hostType))
            return true;

        if (!Enum.TryParse<SegmentHostType>(hostType.Trim(), true, out var parsed))
            return true;

        return item.Span.HostType == parsed || rows.Any(row => row.HostType == parsed);
    }

    private static bool MatchesSourceCategory(string? spanSourceKey, IEnumerable<string?> rowSourceKeys, string? sourceCategory)
    {
        if (string.IsNullOrWhiteSpace(sourceCategory))
            return true;

        var keys = rowSourceKeys.Append(spanSourceKey).Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        return sourceCategory.Trim().ToLowerInvariant() switch
        {
            "extensions" => keys.Any(value => value!.StartsWith("ext:", StringComparison.OrdinalIgnoreCase)),
            "user" => keys.Any(value => string.Equals(value, "user", StringComparison.OrdinalIgnoreCase)),
            _ => true,
        };
    }

    private static bool MatchesBool(IEnumerable<bool> actualValues, bool? expected)
        => !expected.HasValue || (expected.Value ? actualValues.Any(value => value) : actualValues.All(value => !value));

    private static bool MatchesDateCriterion(IEnumerable<DateTime> actualValues, string? expected, string? expected2, string? modifier)
    {
        if (!TryParseDateTime(expected, out var value))
            return true;

        _ = TryParseDateTime(expected2, out var parsedValue2);
        var upper = parsedValue2 == default ? value : parsedValue2;
        return actualValues.Any(actual => NormalizeCriterionModifier(modifier) switch
        {
            "NOT_EQUALS" => actual != value,
            "LESS_THAN" => actual < value,
            "BETWEEN" => actual >= (value < upper ? value : upper) && actual <= (value < upper ? upper : value),
            "NOT_BETWEEN" => actual < (value < upper ? value : upper) || actual > (value < upper ? upper : value),
            "EQUALS" => actual == value,
            _ => actual > value,
        });
    }

    private static bool MatchesNumberCriterion(double actual, double value, double? value2, string? modifier)
    {
        return NormalizeCriterionModifier(modifier) switch
        {
            "NOT_EQUALS" => actual != value,
            "LESS_THAN" => actual < value,
            "BETWEEN" when value2.HasValue => actual >= Math.Min(value, value2.Value) && actual <= Math.Max(value, value2.Value),
            "NOT_BETWEEN" when value2.HasValue => actual < Math.Min(value, value2.Value) || actual > Math.Max(value, value2.Value),
            "EQUALS" => actual == value,
            _ => actual > value,
        };
    }

    private static bool EqualsIgnoreCase(string? actual, string expected)
        => string.Equals(actual?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsIgnoreCase(string? actual, string expected)
        => !string.IsNullOrWhiteSpace(actual) && actual.Contains(expected, StringComparison.OrdinalIgnoreCase);

    private sealed class SegmentSearchRow
    {
        public int Id { get; init; }
        public string? Title { get; init; }
        public SegmentHostType HostType { get; init; }
        public int HostId { get; init; }
        public double StartSec { get; init; }
        public double? EndSec { get; init; }
        public string? SourceKey { get; init; }
        public string? SourceRunId { get; init; }
        public string? Kind { get; init; }
        public int? TagId { get; init; }
        public string? TagName { get; init; }
        public long? RefId { get; init; }
        public string? RefLabel { get; init; }
        public int? FacePerformerId { get; init; }
        public int? DirectPerformerId { get; init; }
        public string? PerformerName { get; init; }
        public float? Confidence { get; init; }
        public string? ColorHint { get; init; }
        public bool HasImage { get; init; }
        public bool HasPayload { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }
}
