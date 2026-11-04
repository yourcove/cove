using System.Text.Json;
using System.Globalization;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Cove.Data.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[RequiresPermission(Permissions.SegmentsRead)]
public class SegmentsController(CoveContext db, ISegmentRepository segments, SegmentSpanResolver spanResolver, IMemoryCache memoryCache, IFieldProvenanceService? fieldProvenanceService = null) : ControllerBase
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
        [FromQuery] int? seed = null,
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
        [FromQuery] int? tagDepth = null,
        [FromQuery] string? videoTagIds = null,
        [FromQuery] int? videoTagDepth = null,
        [FromQuery] bool includeAggregate = false,
        CancellationToken cancellationToken = default)
    {
        // The library only ever lists video-hosted segments; another host type matches nothing.
        if (!string.IsNullOrWhiteSpace(hostType)
            && (!Enum.TryParse<SegmentHostType>(hostType, true, out var parsedHostType) || parsedHostType != SegmentHostType.Video))
            return Ok(new PaginatedResponse<SegmentRecordDto>([], 0, Math.Max(page, 1), Math.Clamp(perPage, 1, ListPaging.MaxPerPage), includeAggregate ? 0 : null));

        // The flat query parameters are the original wire shape of the list; they map onto the same
        // SegmentFilter the POST /find body carries, so both go through one repository query.
        var filter = new SegmentFilter
        {
            Ids = IdList(ids),
            HostId = videoId,
            VideoIds = IdList(videoIds),
            ExcludeVideoIds = IdList(excludeVideoIds),
            VideoTagIds = IdList(videoTagIds),
            VideoTagDepth = videoTagDepth,
            TagIds = IdList(tagIds),
            TagsCriterion = tagId.HasValue ? new MultiIdCriterion { Value = [tagId.Value], Modifier = CriterionModifier.Includes, Depth = tagDepth } : null,
            KindCriterion = ContainsCriterion(kind),
            SourceKeyCriterion = ContainsCriterion(sourceKey),
            SourceCategory = sourceCategory,
            RefIds = ParseLongIdList(refIds) is { Count: > 0 } parsedRefIds ? parsedRefIds : null,
            PerformerIds = IdList(performerIds),
            Tagged = tagged,
            MinConfidence = minConfidence,
            MinDurationSec = minDurationSec,
            ConfidenceCriterion = DoubleCriterionOrNull(confidence, confidence2, confidenceModifier),
            DurationCriterion = DoubleCriterionOrNull(durationSec, durationSec2, durationModifier),
            TitleCriterion = StringCriterionOrNull(title, titleModifier),
            SourceRunIdCriterion = StringCriterionOrNull(sourceRunId, sourceRunIdModifier),
            ColorHintCriterion = StringCriterionOrNull(colorHint, colorHintModifier),
            VideoTitleCriterion = ContainsCriterion(videoTitle),
            HasImageCriterion = hasImage is { } hasImageValue ? new BoolCriterion { Value = hasImageValue } : null,
            HasPayloadCriterion = hasPayload is { } hasPayloadValue ? new BoolCriterion { Value = hasPayloadValue } : null,
            StartSecCriterion = DoubleCriterionOrNull(startSec, startSec2, startSecModifier),
            EndSecCriterion = DoubleCriterionOrNull(endSec, endSec2, endSecModifier),
            CreatedAtCriterion = TimestampCriterionOrNull(createdAt, createdAt2, createdAtModifier),
            UpdatedAtCriterion = TimestampCriterionOrNull(updatedAt, updatedAt2, updatedAtModifier),
        };
        var findFilter = new FindFilter
        {
            Q = q,
            Page = page,
            PerPage = perPage,
            Sort = sort,
            // The segment list has always defaulted to descending; only an explicit "asc" flips it.
            Direction = string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase) ? Cove.Core.Enums.SortDirection.Asc : Cove.Core.Enums.SortDirection.Desc,
            Seed = seed,
        };

        var (items, totalCount) = await segments.FindAsync(filter, findFilter, cancellationToken);
        double? aggregateDuration = includeAggregate
            ? (await segments.AggregateAsync(filter, findFilter, cancellationToken)).Duration
            : null;

        return Ok(new PaginatedResponse<SegmentRecordDto>(items.Select(item => MapToDto(item)).ToList(), totalCount, Math.Max(page, 1), Math.Clamp(perPage, 1, ListPaging.MaxPerPage), aggregateDuration));
    }

    /// <summary>
    /// The list as a body: the filter dialog's criteria as a <see cref="SegmentFilter"/> plus search, sort
    /// and paging in a <see cref="FindFilter"/>, the way <c>POST /api/videos/find</c> takes them. Filter
    /// expressions are not supported for segments.
    /// </summary>
    [HttpPost("find")]
    public async Task<ActionResult<PaginatedResponse<SegmentRecordDto>>> FindPost([FromBody] FilteredQueryRequest<SegmentFilter> req, CancellationToken cancellationToken)
    {
        if (req.FilterExpression is { Children.Count: > 0 })
            return BadRequest(new { message = "Filter expressions are not supported for segments." });

        var findFilter = req.FindFilter ?? new FindFilter();
        var filter = req.ObjectFilter ?? new SegmentFilter();
        if (req.Ids is { Count: > 0 })
            filter.Ids = req.Ids;

        var (items, totalCount) = await segments.FindAsync(filter, findFilter, cancellationToken);
        return Ok(new PaginatedResponse<SegmentRecordDto>(items.Select(item => MapToDto(item)).ToList(), totalCount, Math.Max(findFilter.Page, 1), Math.Clamp(findFilter.PerPage, 1, ListPaging.MaxPerPage), null));
    }

    [HttpPost("aggregate")]
    public async Task<ActionResult<SegmentAggregate>> Aggregate([FromBody] FilteredQueryRequest<SegmentFilter> req, CancellationToken cancellationToken)
    {
        if (req.FilterExpression is { Children.Count: > 0 })
            return BadRequest(new { message = "Filter expressions are not supported for segments." });

        var filter = req.ObjectFilter ?? new SegmentFilter();
        if (req.Ids is { Count: > 0 })
            filter.Ids = req.Ids;

        return Ok(await segments.AggregateAsync(filter, req.FindFilter, cancellationToken));
    }

    [HttpGet("{id:int}")]
    [AllowShareLinkAccess]
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
            select new SegmentLibraryItem
            {
                Segment = segment,
                VideoTitle = video.Title,
                TagName = tag != null ? tag.Name : null,
                RefLabel = face != null ? (facePerformer != null ? facePerformer!.Name : face.Label) : segment.Kind != null && segment.Kind!.ToLower() == "performer" && directPerformer != null ? directPerformer!.Name : null,
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
        var values = await db.VisibleSegments(SegmentHostType.Video).AsNoTracking()
            .Where(segment => !string.IsNullOrWhiteSpace(segment.SourceKey))
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
        var values = await db.VisibleSegments(SegmentHostType.Video).AsNoTracking()
            .Where(segment => segment.Kind != null && segment.Kind != string.Empty)
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

    private static SegmentRecordDto MapToDto(SegmentLibraryItem item, IReadOnlyList<FieldProvenanceDto>? fieldProvenance = null)
        => SegmentRecordDto.FromLibraryItem(item, fieldProvenance);

    internal static IQueryable<TRow> ApplyDateTimeCriterion<TRow>(
        IQueryable<TRow> query,
        DateTime value,
        string? value2,
        string? modifier,
        Expression<Func<TRow, DateTime>> selector)
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

    private static IQueryable<TRow> WhereCompare<TRow, T>(IQueryable<TRow> query, Expression<Func<TRow, T>> selector, T value, ExpressionType comparison)
    {
        var predicate = Expression.MakeBinary(comparison, selector.Body, Expression.Constant(value));
        return query.Where(Expression.Lambda<Func<TRow, bool>>(predicate, selector.Parameters[0]));
    }

    private static IQueryable<TRow> WhereBetween<TRow, T>(IQueryable<TRow> query, Expression<Func<TRow, T>> selector, T lower, T upper)
    {
        var body = selector.Body;
        var predicate = Expression.AndAlso(
            Expression.GreaterThanOrEqual(body, Expression.Constant(lower)),
            Expression.LessThanOrEqual(body, Expression.Constant(upper)));
        return query.Where(Expression.Lambda<Func<TRow, bool>>(predicate, selector.Parameters[0]));
    }

    private static IQueryable<TRow> WhereNotBetween<TRow, T>(IQueryable<TRow> query, Expression<Func<TRow, T>> selector, T lower, T upper)
    {
        var body = selector.Body;
        var predicate = Expression.OrElse(
            Expression.LessThan(body, Expression.Constant(lower)),
            Expression.GreaterThan(body, Expression.Constant(upper)));
        return query.Where(Expression.Lambda<Func<TRow, bool>>(predicate, selector.Parameters[0]));
    }

    internal static bool TryParseDateTime(string? value, out DateTime parsed)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
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

    private static List<int>? IdList(string? raw)
        => ParseIdList(raw) is { Count: > 0 } ids ? ids : null;

    private static StringCriterion? ContainsCriterion(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : new StringCriterion { Value = value.Trim(), Modifier = CriterionModifier.Includes };

    // A string parameter only filters with a modifier it understands; IS_NULL and NOT_NULL need no value.
    private static StringCriterion? StringCriterionOrNull(string? value, string? modifier)
    {
        var parsed = ParseModifier(modifier);
        if (parsed is CriterionModifier.IsNull or CriterionModifier.NotNull)
            return new StringCriterion { Value = string.Empty, Modifier = parsed.Value };
        if (string.IsNullOrWhiteSpace(value) || parsed is null)
            return null;
        return new StringCriterion { Value = value.Trim(), Modifier = parsed.Value };
    }

    // Number and timestamp parameters without a modifier have always meant "greater than".
    private static DoubleCriterion? DoubleCriterionOrNull(double? value, double? value2, string? modifier)
        => value.HasValue ? new DoubleCriterion { Value = value.Value, Value2 = value2, Modifier = ParseModifier(modifier) ?? CriterionModifier.GreaterThan } : null;

    private static TimestampCriterion? TimestampCriterionOrNull(string? value, string? value2, string? modifier)
        => string.IsNullOrWhiteSpace(value) ? null : new TimestampCriterion { Value = value, Value2 = value2, Modifier = ParseModifier(modifier) ?? CriterionModifier.GreaterThan };

    private static CriterionModifier? ParseModifier(string? modifier)
        => !string.IsNullOrWhiteSpace(modifier) && Enum.TryParse<CriterionModifier>(modifier.Replace("_", string.Empty), ignoreCase: true, out var parsed) ? parsed : null;

    public sealed record SegmentTagBulkRemoveRequest(int TagId, IReadOnlyList<int>? Ids);

    // ===== Span Search =====

    [HttpPost("spans/search")]
    public async Task<ActionResult<SegmentSpanSearchResponseDto>> SearchSpans(
        [FromBody] SegmentSpanSearchRequestDto request,
        CancellationToken ct)
    {
        using var relativeDates = RelativeDateEvaluation.Begin();
        request = ResolveRelativeTimestamps(request);
        request = await ExpandSpanTagFiltersAsync(request, ct);
        var page = Math.Max(1, request.Page ?? 1);
        var perPage = Math.Clamp(request.PerPage ?? 24, 1, 100);
        var sort = (request.Sort ?? "updated_at").Trim().ToLowerInvariant();
        var descending = !string.Equals(request.Direction, "asc", StringComparison.OrdinalIgnoreCase);

        // 1. Gather the in-scope videos (ordered for the active sort) plus the profile/derived query.
        var videoList = await BuildSpanVideoListAsync(request, sort, descending, ct);
        var profileId = await spanResolver.ResolveProfileIdAsync(request.Profile, ct);
        var derivedQueryRequest = BuildDerivedQueryRequest(request, profileId);
        var videoMap = videoList.ToDictionary(v => v.Id);

        // 2. Fast path: for a video-level sort with no segment-row-dependent filter (the common browse
        //    case) the videos are already in final order, so we resolve them in order and stop as soon
        //    as the requested page is filled — paying only the current page's cost. The exact total is
        //    fetched separately (spans/count, cached); here we report -1 (unknown) + HasMore. VideoIds
        //    scoping is excluded because that branch is ordered by id rather than the sort.
        var canTerminateEarly = request.VideoIds is not { Length: > 0 }
            && IsVideoLevelSort(sort)
            && !NeedsSegmentRows(request);

        if (canTerminateEarly)
        {
            var neededCount = page * perPage;
            var pageStart = (page - 1) * perPage;
            var pageItems = new List<SegmentSpanSearchResultItemDto>(perPage);
            var collected = 0;
            var earlyTerminated = false;

            for (var i = 0; i < videoList.Count && !earlyTerminated; i += SpanResolveBatchSize)
            {
                var batchVideoIds = videoList.Skip(i).Take(SpanResolveBatchSize).Select(v => v.Id).ToList();
                foreach (var (videoId, spans) in await ResolveSpanBatchAsync(batchVideoIds, profileId, derivedQueryRequest, ct))
                {
                    if (!videoMap.TryGetValue(videoId, out var video)) continue;
                    foreach (var span in spans)
                    {
                        if (collected >= pageStart && pageItems.Count < perPage)
                            pageItems.Add(new SegmentSpanSearchResultItemDto(span, video.Id, video.Title, video.UpdatedAt.ToString("o"), profileId));
                        collected++;
                        if (collected > neededCount) { earlyTerminated = true; break; }
                    }
                    if (earlyTerminated) break;
                }
            }

            // If we never crossed the page boundary we resolved the whole scope, so the count is exact and
            // free; otherwise it's unknown here and the spans/count endpoint supplies it.
            var exactTotal = earlyTerminated ? -1 : collected;
            return Ok(new SegmentSpanSearchResponseDto(await AttachSpanDetailsAsync(pageItems, derivedQueryRequest != null, ct), exactTotal, page, perPage, HasMore: earlyTerminated));
        }

        // 3. Full path (segment-row filters or span-level sort): resolve all in-scope spans, filter, sort,
        //    and page. The total is exact here because the whole matching set is materialized.
        var (allItems, segmentRows) = await ResolveAndFilterSpansAsync(videoList, request, profileId, derivedQueryRequest, videoMap, ct);

        if (IsSpanLevelSort(sort))
            allItems = ApplySpanOrdering(allItems, sort, descending, segmentRows, request.Seed).ToList();

        var totalCount = allItems.Count;
        var finalOffset = (page - 1) * perPage;
        var finalPageItems = allItems.Skip(finalOffset).Take(perPage).ToList();

        return Ok(new SegmentSpanSearchResponseDto(await AttachSpanDetailsAsync(finalPageItems, derivedQueryRequest != null, ct), totalCount, page, perPage, HasMore: finalOffset + finalPageItems.Count < totalCount));
    }

    /// <summary>
    /// Picks each span's cover segment and versions its picture. The cover is the span's earliest-starting
    /// segment, since hovering a span card plays the video from the span's start and that segment's cover
    /// frame is the closest to it. Its UpdatedAt versions the image, because choosing a cover touches the
    /// segment, not its video.
    /// </summary>
    private async Task<List<SegmentSpanSearchResultItemDto>> WithCoverVersionsAsync(List<SegmentSpanSearchResultItemDto> items, CancellationToken ct)
    {
        var spanSegmentIds = items.SelectMany(item => item.Span.SegmentIds).Distinct().ToList();
        if (spanSegmentIds.Count == 0) return items;

        var segmentsById = await db.Segments.AsNoTracking()
            .Where(segment => spanSegmentIds.Contains(segment.Id))
            .Select(segment => new { segment.Id, segment.StartSec, segment.UpdatedAt })
            .ToDictionaryAsync(segment => segment.Id, ct);
        return items
            .Select(item => item.Span.SegmentIds
                    .Where(segmentsById.ContainsKey)
                    .OrderBy(id => segmentsById[id].StartSec)
                    .ThenBy(id => id)
                    .Select(id => (int?)id)
                    .FirstOrDefault() is { } coverSegmentId
                ? item with
                {
                    CoverSegmentId = coverSegmentId,
                    CoverVersion = segmentsById[coverSegmentId].UpdatedAt.ToString("o"),
                }
                : item)
            .ToList();
    }

    private async Task<List<SegmentSpanSearchResultItemDto>> AttachSpanDetailsAsync(List<SegmentSpanSearchResultItemDto> items, bool derived, CancellationToken ct)
    {
        items = await AttachSpanReferencesAsync(await WithCoverVersionsAsync(items, ct), ct);
        return derived ? items : await AttachSpanConfidenceAsync(items, ct);
    }

    // Cards show a span's confidence the way the confidence sort reads it: the highest of its segments'. A
    // derived span gets none, since its segments come from operands that score different things, the
    // excluded operands of a difference included.
    private async Task<List<SegmentSpanSearchResultItemDto>> AttachSpanConfidenceAsync(List<SegmentSpanSearchResultItemDto> items, CancellationToken ct)
    {
        var segmentIds = items.SelectMany(item => item.Span.SegmentIds).Where(id => id > 0).Distinct().ToList();
        if (segmentIds.Count == 0)
            return items;

        var confidences = await db.Segments.AsNoTracking()
            .Where(segment => segmentIds.Contains(segment.Id) && segment.Confidence != null)
            .Select(segment => new { segment.Id, segment.Confidence })
            .ToDictionaryAsync(segment => segment.Id, segment => segment.Confidence!.Value, ct);
        if (confidences.Count == 0)
            return items;

        return items
            .Select(item => item with
            {
                Confidence = item.Span.SegmentIds.Select(id => confidences.TryGetValue(id, out var value) ? value : (float?)null).Max(),
            })
            .ToList();
    }

    // Face and performer spans carry only their segments' stored title, which goes stale when a face is
    // renamed or linked, so the returned page reads the current face and performer from each span's first
    // segment. A profile span's kind is its segments' kind; derived-query spans (kind = the operator) mix
    // several references and get none.
    private async Task<List<SegmentSpanSearchResultItemDto>> AttachSpanReferencesAsync(List<SegmentSpanSearchResultItemDto> items, CancellationToken ct)
    {
        static bool IsFace(string? kind) => string.Equals(kind, "face", StringComparison.OrdinalIgnoreCase);
        static bool IsPerformer(string? kind) => string.Equals(kind, "performer", StringComparison.OrdinalIgnoreCase);
        static bool HasReference(ResolvedSpan span) =>
            span.TagId is null && span.SegmentIds.Count > 0 && (IsFace(span.Kind) || IsPerformer(span.Kind));

        var segmentIds = items.Where(item => HasReference(item.Span)).Select(item => item.Span.SegmentIds[0]).Distinct().ToList();
        if (segmentIds.Count == 0)
            return items;

        var segmentRefs = (await db.Segments.AsNoTracking()
                .Where(segment => segmentIds.Contains(segment.Id) && segment.RefId != null && segment.RefId <= int.MaxValue)
                .Select(segment => new { segment.Id, segment.Kind, segment.RefId })
                .ToListAsync(ct))
            .Select(segment => (segment.Id, segment.Kind, RefId: (int)segment.RefId!.Value))
            .ToList();

        var faceIds = segmentRefs.Where(segment => IsFace(segment.Kind)).Select(segment => segment.RefId).Distinct().ToList();
        var faces = faceIds.Count == 0
            ? []
            : (await db.Faces.AsNoTracking()
                .Where(face => faceIds.Contains(face.Id))
                .Select(face => new { face.Id, face.Label, face.PrimarySourceKey, face.PerformerId })
                .ToListAsync(ct))
            .ToDictionary(face => face.Id, face => (face.Id, face.Label, face.PrimarySourceKey, face.PerformerId));

        var performerIds = faces.Values.Where(face => face.PerformerId != null).Select(face => face.PerformerId!.Value)
            .Concat(segmentRefs.Where(segment => IsPerformer(segment.Kind)).Select(segment => segment.RefId))
            .Distinct()
            .ToList();
        Dictionary<int, string> performerNames = performerIds.Count == 0
            ? []
            : await db.Performers.AsNoTracking()
                .Where(performer => performerIds.Contains(performer.Id))
                .Select(performer => new { performer.Id, performer.Name })
                .ToDictionaryAsync(performer => performer.Id, performer => performer.Name, ct);

        var references = new Dictionary<int, SegmentSpanReferenceDto>();
        foreach (var segment in segmentRefs)
        {
            var refId = segment.RefId;
            if (IsFace(segment.Kind) && faces.TryGetValue(refId, out var face))
            {
                references[segment.Id] = new SegmentSpanReferenceDto
                {
                    FaceId = face.Id,
                    FaceLabel = face.Label,
                    FaceSourceKey = face.PrimarySourceKey,
                    PerformerId = face.PerformerId,
                    PerformerName = face.PerformerId is { } linkedId ? performerNames.GetValueOrDefault(linkedId) : null,
                };
            }
            else if (IsPerformer(segment.Kind) && performerNames.TryGetValue(refId, out var performerName))
            {
                references[segment.Id] = new SegmentSpanReferenceDto { PerformerId = refId, PerformerName = performerName };
            }
        }

        return items
            .Select(item => HasReference(item.Span) && references.TryGetValue(item.Span.SegmentIds[0], out var reference)
                    ? item with { Reference = reference }
                    : item)
            .ToList();
    }

    private const int SpanResolveBatchSize = 400;

    /// <summary>
    /// Exact span total for a filter set. Resolving every in-scope video is unavoidable for an exact
    /// merged-span count, so the result is cached keyed by the filter set plus a cheap segments-table
    /// fingerprint — it is computed at most once per filter set and auto-invalidates on any segment change.
    /// </summary>
    [HttpPost("spans/count")]
    public async Task<ActionResult<SegmentSpanCountResponseDto>> CountSpans(
        [FromBody] SegmentSpanSearchRequestDto request,
        CancellationToken ct)
    {
        using var relativeDates = RelativeDateEvaluation.Begin();
        var hasRelativeDates = RelativeDateEvaluation.IsRelativeExpression(request.CreatedAt)
            || RelativeDateEvaluation.IsRelativeExpression(request.CreatedAt2)
            || RelativeDateEvaluation.IsRelativeExpression(request.UpdatedAt)
            || RelativeDateEvaluation.IsRelativeExpression(request.UpdatedAt2);
        request = ResolveRelativeTimestamps(request);
        request = await ExpandSpanTagFiltersAsync(request, ct);
        var sort = (request.Sort ?? "updated_at").Trim().ToLowerInvariant();
        var descending = !string.Equals(request.Direction, "asc", StringComparison.OrdinalIgnoreCase);

        var version = await GetSegmentsVersionAsync(ct);
        var cacheKey = $"spans-count:{version}:{BuildSpanCountKey(request)}";
        var canCache = request.VideoTagIds is not { Length: > 0 } && !hasRelativeDates;
        if (canCache && memoryCache.TryGetValue<SegmentSpanCountResponseDto>(cacheKey, out var cachedAggregate))
            return Ok(cachedAggregate);

        var videoList = await BuildSpanVideoListAsync(request, sort, descending, ct);
        var profileId = await spanResolver.ResolveProfileIdAsync(request.Profile, ct);
        var derivedQueryRequest = BuildDerivedQueryRequest(request, profileId);
        var videoMap = videoList.ToDictionary(v => v.Id);
        var (allItems, _) = await ResolveAndFilterSpansAsync(videoList, request, profileId, derivedQueryRequest, videoMap, ct);

        var aggregate = new SegmentSpanCountResponseDto(
            allItems.Count,
            allItems.Sum(item => Math.Max(0, item.Span.EndSec - item.Span.StartSec)));
        if (canCache)
            memoryCache.Set(cacheKey, aggregate, TimeSpan.FromMinutes(30));
        return Ok(aggregate);
    }

    private async Task<SegmentSpanSearchRequestDto> ExpandSpanTagFiltersAsync(
        SegmentSpanSearchRequestDto request,
        CancellationToken ct)
    {
        var result = request;
        if (request.TagDepth == -1 && request.TagIds is { Length: > 0 })
        {
            var expanded = await HierarchicalCriterionExpander.ExpandTagsAsync(db,
                new MultiIdCriterion { Value = request.TagIds.ToList(), Modifier = CriterionModifier.Includes, Depth = -1 }, ct);
            result = result with { TagIds = expanded.Criterion.Value.ToArray(), TagDepth = null };
        }
        if (request.VideoTagDepth == -1 && request.VideoTagIds is { Length: > 0 })
        {
            var expanded = await HierarchicalCriterionExpander.ExpandTagsAsync(db,
                new MultiIdCriterion { Value = request.VideoTagIds.ToList(), Modifier = CriterionModifier.Includes, Depth = -1 }, ct);
            result = result with { VideoTagIds = expanded.Criterion.Value.ToArray(), VideoTagDepth = null };
        }
        return result;
    }

    private static SegmentSpanQueryRequestDto? BuildDerivedQueryRequest(SegmentSpanSearchRequestDto request, int profileId)
        => request.DerivedQuery is { } dq
            ? new SegmentSpanQueryRequestDto(profileId, dq.Operator, dq.Operands, dq.MergeGapSec, dq.MinDurationSec)
            : null;

    private async Task<List<(int Id, string? Title, DateTimeOffset UpdatedAt)>> BuildSpanVideoListAsync(
        SegmentSpanSearchRequestDto request, string sort, bool descending, CancellationToken ct)
    {
        var videoQuery = db.Videos.AsNoTracking().AsQueryable();
        if (request.VideoIds is { Length: > 0 })
        {
            var idSet = request.VideoIds.ToHashSet();
            var excludeSet = request.ExcludeVideoIds?.ToHashSet() ?? [];
            videoQuery = videoQuery.Where(s => idSet.Contains(s.Id) && !excludeSet.Contains(s.Id));
            if (request.VideoTagIds is { Length: > 0 })
            {
                var tagIds = request.VideoTagIds.ToHashSet();
                var effectiveVideoTags = EffectiveHostTagQuery.ForHostType(db, AffinityHostType.Video);
                videoQuery = videoQuery.Where(video => effectiveVideoTags.Any(tag => tag.HostId == video.Id && tagIds.Contains(tag.TagId)));
            }
            var rows = await videoQuery
                .OrderBy(s => s.Id)
                .Select(s => new { s.Id, s.Title, s.UpdatedAt })
                .ToListAsync(ct);
            return rows.Select(s => (s.Id, (string?)s.Title, (DateTimeOffset)s.UpdatedAt)).ToList();
        }

        var excludeIds = request.ExcludeVideoIds?.ToHashSet() ?? [];
        videoQuery = videoQuery
            .Where(s => !excludeIds.Contains(s.Id))
            // Only videos that actually have video segments can produce spans. Restricting to them
            // (loss-free — empty videos always resolve to zero spans) avoids walking and resolving every
            // video in the library, which is the dominant cost when listing spans at scale.
            .Where(s => db.Segments.Any(seg => seg.HostType == SegmentHostType.Video && seg.HostId == s.Id));

        if (request.VideoTagIds is { Length: > 0 })
        {
            var tagIds = request.VideoTagIds.ToHashSet();
            var effectiveVideoTags = EffectiveHostTagQuery.ForHostType(db, AffinityHostType.Video);
            videoQuery = videoQuery.Where(video => effectiveVideoTags.Any(tag => tag.HostId == video.Id && tagIds.Contains(tag.TagId)));
        }

        if (!string.IsNullOrWhiteSpace(request.VideoTitle))
        {
            var titleTerm = request.VideoTitle.Trim();
            videoQuery = videoQuery.Where(s => s.Title != null && s.Title.Contains(titleTerm));
        }

        videoQuery = (sort, descending) switch
        {
            ("title", false) => videoQuery.OrderBy(s => NaturalSort.Key(s.Title)),
            ("title", true) => videoQuery.OrderByDescending(s => NaturalSort.Key(s.Title)),
            ("created_at", false) => videoQuery.OrderBy(s => s.CreatedAt),
            ("created_at", true) => videoQuery.OrderByDescending(s => s.CreatedAt),
            (_, false) => videoQuery.OrderBy(s => s.UpdatedAt),
            _ => videoQuery.OrderByDescending(s => s.UpdatedAt),
        };

        var ordered = await videoQuery.Select(s => new { s.Id, s.Title, s.UpdatedAt }).ToListAsync(ct);
        return ordered.Select(s => (s.Id, (string?)s.Title, (DateTimeOffset)s.UpdatedAt)).ToList();
    }

    private async Task<IReadOnlyList<(int VideoId, IReadOnlyList<ResolvedSpan> Spans)>> ResolveSpanBatchAsync(
        IReadOnlyList<int> batchVideoIds, int profileId, SegmentSpanQueryRequestDto? derivedQueryRequest, CancellationToken ct)
        => derivedQueryRequest is not null
            ? await spanResolver.QueryVideosBatchAsync(batchVideoIds, derivedQueryRequest, ct)
            : await spanResolver.ResolveVideosBatchAsync(batchVideoIds, profileId, ct);

    private async Task<(List<SegmentSpanSearchResultItemDto> Items, IReadOnlyDictionary<int, SegmentSearchRow> SegmentRows)> ResolveAndFilterSpansAsync(
        List<(int Id, string? Title, DateTimeOffset UpdatedAt)> videoList,
        SegmentSpanSearchRequestDto request,
        int profileId,
        SegmentSpanQueryRequestDto? derivedQueryRequest,
        Dictionary<int, (int Id, string? Title, DateTimeOffset UpdatedAt)> videoMap,
        CancellationToken ct)
    {
        var allItems = new List<SegmentSpanSearchResultItemDto>(videoList.Count * 2);
        for (var i = 0; i < videoList.Count; i += SpanResolveBatchSize)
        {
            var batchVideoIds = videoList.Skip(i).Take(SpanResolveBatchSize).Select(v => v.Id).ToList();
            foreach (var (videoId, spans) in await ResolveSpanBatchAsync(batchVideoIds, profileId, derivedQueryRequest, ct))
            {
                if (!videoMap.TryGetValue(videoId, out var video)) continue;
                foreach (var span in spans)
                    allItems.Add(new SegmentSpanSearchResultItemDto(span, video.Id, video.Title, video.UpdatedAt.ToString("o"), profileId));
            }
        }

        IReadOnlyDictionary<int, SegmentSearchRow> segmentRows = new Dictionary<int, SegmentSearchRow>();
        if (allItems.Count > 0 && NeedsSegmentRows(request))
        {
            segmentRows = await LoadSpanSegmentRowsAsync(allItems.SelectMany(item => item.Span.SegmentIds), ct);
            allItems = allItems.Where(item => MatchesSpanSearchRequest(item, request, segmentRows)).ToList();
        }

        return (allItems, segmentRows);
    }

    private async Task<string> GetSegmentsVersionAsync(CancellationToken ct)
    {
        // Cheap fingerprint of the segments table: count covers add/remove, max(updated_at) covers edits.
        var count = await db.Segments.CountAsync(ct);
        var maxUpdated = await db.Segments.MaxAsync(s => (DateTimeOffset?)s.UpdatedAt, ct);
        return $"{count}:{maxUpdated?.UtcTicks ?? 0}";
    }

    private static string BuildSpanCountKey(SegmentSpanSearchRequestDto request)
    {
        // The count depends only on the filter set + profile + derived query — never on page/sort/direction.
        var derived = request.DerivedQuery is { } dq
            ? $"{dq.Operator}|{string.Join(",", dq.Operands ?? [])}|{dq.MergeGapSec}|{dq.MinDurationSec}"
            : string.Empty;
        return string.Join("|",
            request.Profile, derived, request.Q, request.VideoTitle,
            request.VideoIds is null ? "" : string.Join(",", request.VideoIds),
            request.ExcludeVideoIds is null ? "" : string.Join(",", request.ExcludeVideoIds),
            request.VideoTagIds is null ? "" : string.Join(",", request.VideoTagIds),
            request.TagIds is null ? "" : string.Join(",", request.TagIds),
            request.Kind, request.SourceKey, request.SourceCategory,
            request.RefIds is null ? "" : string.Join(",", request.RefIds),
            request.PerformerIds is null ? "" : string.Join(",", request.PerformerIds),
            request.Confidence, request.Confidence2, request.ConfidenceModifier,
            request.DurationSec, request.DurationSec2, request.DurationModifier,
            request.Title, request.TitleModifier, request.HostType,
            request.SourceRunId, request.SourceRunIdModifier,
            request.ColorHint, request.ColorHintModifier, request.HasImage, request.HasPayload,
            request.StartSec, request.StartSec2, request.StartSecModifier,
            request.EndSec, request.EndSec2, request.EndSecModifier,
            request.CreatedAt, request.CreatedAt2, request.CreatedAtModifier,
            request.UpdatedAt, request.UpdatedAt2, request.UpdatedAtModifier);
    }

    private static SegmentSpanSearchRequestDto ResolveRelativeTimestamps(SegmentSpanSearchRequestDto request)
    {
        var created = ResolveRelativeTimestamp(request.CreatedAt, request.CreatedAt2);
        var updated = ResolveRelativeTimestamp(request.UpdatedAt, request.UpdatedAt2);
        return request with { CreatedAt = created.Value, CreatedAt2 = created.Value2, UpdatedAt = updated.Value, UpdatedAt2 = updated.Value2 };
    }

    internal static (string? Value, string? Value2) ResolveRelativeTimestamp(string? value, string? value2)
    {
        var criterion = RelativeDateEvaluation.Resolve(new TimestampCriterion
        {
            Value = value ?? "", Value2 = value2,
        });
        return (criterion.Value, criterion.Value2);
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
        => sort is "random" or "start_sec" or "span_start" or "end_sec" or "span_end" or "duration" or "span_duration" or "kind" or "segment_kind" or "source_key" or "segment_source_key" or "tag_name" or "segment_tag_name" or "segment_count" or "segment_confidence" or "confidence" or "segment_created_at" or "segment_updated_at" or "source_run_id" or "segment_source_run_id" or "performer" or "segment_performer" or "ref" or "segment_ref" or "host_title" or "host_type" or "host_id";

    private static IOrderedEnumerable<SegmentSpanSearchResultItemDto> ApplySpanOrdering(
        IEnumerable<SegmentSpanSearchResultItemDto> items,
        string sort,
        bool descending,
        IReadOnlyDictionary<int, SegmentSearchRow> segmentRows,
        int? seed)
    {
        return sort switch
        {
            "random" => OrderSpanBy(items, item => SeededRandomSpanKey(item, seed), descending),
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

    // Derived spans cut from one long segment all share their first segment id, so the key folds in every segment
    // id and the span's own position; it stays stable for a seed so pages of one shuffle line up.
    private static long SeededRandomSpanKey(SegmentSpanSearchResultItemDto item, int? seed)
    {
        var key = SeededRandomKey(item.VideoId, seed);
        foreach (var segmentId in item.Span.SegmentIds)
        {
            key = SeededRandomKey(segmentId, unchecked((int)key));
        }

        return SeededRandomKey(unchecked((int)Math.Round(item.Span.StartSec * 1000)), unchecked((int)key));
    }

    private static long SeededRandomKey(int id, int? seed)
    {
        var value = unchecked((uint)(id ^ (seed ?? 1)));
        value ^= value >> 16;
        value *= 0x7feb352d;
        value ^= value >> 15;
        value *= 0x846ca68b;
        value ^= value >> 16;
        return value;
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
                RefLabel = face != null ? (facePerformer != null ? facePerformer!.Name : face.Label) : segment.Kind != null && segment.Kind!.ToLower() == "performer" && directPerformer != null ? directPerformer!.Name : null,
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
            "INCLUDES" => values.Any(value => ContainsIgnoreCase(value, text)),
            "EXCLUDES" => !values.Any(value => ContainsIgnoreCase(value, text)),
            "MATCHES_REGEX" => values.Any(value => Regex.IsMatch(value ?? string.Empty, text, RegexOptions.IgnoreCase)),
            "NOT_MATCHES_REGEX" => !values.Any(value => Regex.IsMatch(value ?? string.Empty, text, RegexOptions.IgnoreCase)),
            _ => true,
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
