using System.Globalization;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

/// <summary>
/// Answers the Segments list from the stored span index: filters, sorts, counts and pages a profile's
/// spans in the database. It reproduces what the list used to compute in memory after resolving every
/// video. A filter on the contributing segments ("any segment of the span ...") is answered from the
/// facts stored on the span row; where those only bound the answer (an exact confidence, a date inside
/// a range) the remaining spans' segments are read.
/// </summary>
public sealed class SegmentSpanIndexSearch(CoveContext db)
{
    public sealed record Page(List<SegmentSpanSearchResultItemDto> Items, int TotalCount, bool HasMore);

    /// <summary>
    /// Case-insensitive matching lower-cases the stored text in this collation. The database's own
    /// collation may be "C", whose lower() only folds ASCII and would stop a title with an accented or
    /// non-Latin capital from matching even its own text.
    /// </summary>
    private const string CaseFoldCollation = "und-x-icu";

    /// <summary>
    /// Kinds and source keys are identifiers compared with ASCII values, where folding A-Z is exact and
    /// much cheaper than full Unicode folding over millions of rows. Pinned so that it does not depend on
    /// the collation the database was created with.
    /// </summary>
    private const string AsciiFoldCollation = "C";

    /// <summary>From this many rows in, a page is counted first so it can be read from the nearer end.</summary>
    internal const int DeepPageOffset = 5_000;

    /// <summary>A stored span with its video; the shape every filter and sort is written against.</summary>
    public sealed class SpanRow
    {
        public required StoredSegmentSpan Span { get; init; }
        public required Video Video { get; init; }
    }

    public async Task<Page> SearchAsync(
        SegmentSpanSearchRequestDto request, int profileId, int page, int perPage, string sort, bool descending, CancellationToken ct)
    {
        var filtered = await BuildFilteredQueryAsync(request, profileId, ct);
        var videoScoped = request.VideoIds is { Length: > 0 };
        var spanLevelSort = IsSpanLevelSort(sort);
        IOrderedQueryable<SpanRow> Ordered(bool reverse) => spanLevelSort
            ? ApplySpanOrdering(filtered, sort, descending, request.Seed, reverse)
            : videoScoped
                // A search scoped to given videos lists them by id, whatever the requested sort.
                ? reverse
                    ? filtered.OrderByDescending(row => row.Video.Id).ThenByDescending(row => row.Span.Ordinal)
                    : filtered.OrderBy(row => row.Video.Id).ThenBy(row => row.Span.Ordinal)
                : ApplyVideoOrdering(filtered, sort, descending, reverse);

        // A page number past what an offset can hold is simply past the end.
        var offset = (int)Math.Min((long)(page - 1) * perPage, int.MaxValue - perPage - 1);

        // The database still walks past every row before the page, so a page in the far half is read
        // from the other end of the same order: the last page then costs what the first does. That
        // needs the total, which a deep page is worth counting for.
        int? knownTotal = offset >= DeepPageOffset ? await filtered.CountAsync(ct) : null;
        var fromEnd = knownTotal is { } counted && offset > counted / 2;
        var skip = offset;
        var take = perPage + 1;
        if (fromEnd)
        {
            var remaining = Math.Max(0, knownTotal!.Value - offset);
            take = Math.Min(perPage, remaining);
            skip = remaining - take;
        }

        var rows = take == 0
            ? []
            : await Ordered(reverse: fromEnd)
                .Skip(skip)
                .Take(take)
                .Select(row => new
                {
                    row.Span.SpanKey,
                    row.Span.VideoId,
                    row.Span.StartSec,
                    row.Span.EndSec,
                    row.Span.SourceKey,
                    row.Span.Kind,
                    row.Span.TagId,
                    row.Span.Label,
                    // The tag's current name, and only if this reader may see the tag.
                    TagName = db.Tags.Where(tag => tag.Id == row.Span.TagId).Select(tag => tag.Name).FirstOrDefault(),
                    row.Span.ColorHint,
                    row.Span.Lane,
                    row.Span.CollapsedToInstant,
                    row.Span.SegmentIds,
                    VideoTitle = row.Video.Title,
                    VideoUpdatedAt = row.Video.UpdatedAt,
                })
                .ToListAsync(ct);
        if (fromEnd)
            rows.Reverse();

        var hasMore = knownTotal is { } total ? offset + Math.Min(rows.Count, perPage) < total : rows.Count > perPage;
        var items = rows.Take(perPage)
            .Select(row => new SegmentSpanSearchResultItemDto(
                new ResolvedSpan(
                    row.SpanKey,
                    SegmentHostType.Video,
                    row.VideoId,
                    row.StartSec,
                    row.EndSec,
                    row.SourceKey,
                    row.Kind,
                    row.TagId,
                    row.TagId is null ? row.Label : row.TagName,
                    // A tag this reader may not see lends the span neither its name nor its colour and lane.
                    row.TagId is not null && row.TagName is null ? null : row.ColorHint,
                    row.TagId is not null && row.TagName is null ? null : row.Lane,
                    row.CollapsedToInstant,
                    row.SegmentIds),
                row.VideoId,
                row.VideoTitle,
                ((DateTimeOffset)row.VideoUpdatedAt).ToString("o"),
                profileId))
            .ToList();

        // Browsing in video order reports an unknown total (-1) while more pages follow and leaves the
        // exact figure to the count endpoint, as the list always has; every other search is exact.
        int totalCount;
        if (knownTotal is { } exact)
            totalCount = exact;
        else if (sort is "updated_at" or "created_at" or "title" && !videoScoped && !HasSpanFilters(request) && hasMore)
            totalCount = -1;
        else if (!hasMore && (items.Count > 0 || offset == 0))
            totalCount = offset + items.Count;
        else
            totalCount = await filtered.CountAsync(ct);

        return new Page(items, totalCount, hasMore);
    }

    public async Task<SegmentSpanCountResponseDto> CountAsync(SegmentSpanSearchRequestDto request, int profileId, CancellationToken ct)
    {
        var filtered = await BuildFilteredQueryAsync(request, profileId, ct);
        var aggregate = await filtered
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                Duration = group.Sum(row => row.Span.DurationSec > 0 ? row.Span.DurationSec : 0),
            })
            .FirstOrDefaultAsync(ct);
        return new SegmentSpanCountResponseDto(aggregate?.Count ?? 0, aggregate?.Duration ?? 0d);
    }

    /// <summary>Whether the request filters on the spans themselves rather than only on their videos.</summary>
    internal static bool HasSpanFilters(SegmentSpanSearchRequestDto request)
        => !string.IsNullOrWhiteSpace(request.Q)
            || !string.IsNullOrWhiteSpace(request.Title)
            || !string.IsNullOrWhiteSpace(request.Kind)
            || !string.IsNullOrWhiteSpace(request.SourceKey)
            || !string.IsNullOrWhiteSpace(request.SourceCategory)
            || !string.IsNullOrWhiteSpace(request.SourceRunId)
            || !string.IsNullOrWhiteSpace(request.ColorHint)
            || !string.IsNullOrWhiteSpace(request.HostType)
            || IsNullnessModifier(request.TitleModifier)
            || IsNullnessModifier(request.SourceRunIdModifier)
            || IsNullnessModifier(request.ColorHintModifier)
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
            || request.DurationSec.HasValue;

    internal static bool IsSpanLevelSort(string sort)
        => sort is "random" or "start_sec" or "span_start" or "end_sec" or "span_end" or "duration" or "span_duration"
            or "kind" or "segment_kind" or "source_key" or "segment_source_key" or "tag_name" or "segment_tag_name"
            or "segment_count" or "segment_confidence" or "confidence" or "segment_created_at" or "segment_updated_at"
            or "source_run_id" or "segment_source_run_id" or "performer" or "segment_performer" or "ref" or "segment_ref"
            or "host_title" or "host_type" or "host_id";

    private async Task<IQueryable<SpanRow>> BuildFilteredQueryAsync(
        SegmentSpanSearchRequestDto request, int profileId, CancellationToken ct)
    {
        var query =
            from video in BuildVideoQuery(request)
            join span in db.StoredSegmentSpans.AsNoTracking().Where(span => span.ProfileId == profileId)
                on video.Id equals span.VideoId
            select new SpanRow { Span = span, Video = video };

        query = ApplyTextCriterion(query, row => row.Span.Titles, request.Title, request.TitleModifier);
        query = ApplyTextCriterion(query, row => row.Span.SourceRunIds, request.SourceRunId, request.SourceRunIdModifier);
        query = ApplyTextCriterion(query, row => row.Span.ColorHints, request.ColorHint, request.ColorHintModifier);

        // Every stored span belongs to a video, so any other valid host type matches nothing.
        if (!string.IsNullOrWhiteSpace(request.HostType)
            && Enum.TryParse<SegmentHostType>(request.HostType.Trim(), true, out var hostType)
            && hostType != SegmentHostType.Video)
            query = query.Where(row => false);

        if (!string.IsNullOrWhiteSpace(request.SourceCategory))
        {
            query = request.SourceCategory.Trim().ToLowerInvariant() switch
            {
                "extensions" => query.Where(row => row.Span.SourceKey != null && EF.Functions.Collate(row.Span.SourceKey, AsciiFoldCollation).ToLower().StartsWith("ext:")),
                "user" => query.Where(row => row.Span.SourceKey != null && EF.Functions.Collate(row.Span.SourceKey, AsciiFoldCollation).ToLower() == "user"),
                _ => query,
            };
        }

        if (request.HasImage is { } hasImage)
            query = query.Where(row => row.Span.HasImage == hasImage);
        if (request.HasPayload is { } hasPayload)
            query = query.Where(row => row.Span.HasPayload == hasPayload);

        if (request.StartSec is { } startSec)
            query = ApplyNumberCriterion(query, row => row.Span.StartSec, startSec, request.StartSec2, request.StartSecModifier);
        if (request.EndSec is { } endSec)
            query = ApplyNumberCriterion(query, row => row.Span.EndSec, endSec, request.EndSec2, request.EndSecModifier);
        if (request.DurationSec is { } durationSec)
            query = ApplyNumberCriterion(query, row => row.Span.DurationSec, durationSec, request.DurationSec2, request.DurationModifier);

        query = ApplyCreatedAtCriterion(query, request.CreatedAt, request.CreatedAt2, request.CreatedAtModifier);
        query = ApplyUpdatedAtCriterion(query, request.UpdatedAt, request.UpdatedAt2, request.UpdatedAtModifier);

        if (!string.IsNullOrWhiteSpace(request.Kind))
        {
            var kind = request.Kind.Trim().ToLowerInvariant();
            query = System.Text.Ascii.IsValid(kind)
                ? query.Where(row => row.Span.Kind != null && EF.Functions.Collate(row.Span.Kind, AsciiFoldCollation).ToLower() == kind)
                : query.Where(row => row.Span.Kind != null
                    && EF.Functions.Collate(row.Span.Kind, CaseFoldCollation).ToLower() == EF.Functions.Collate(kind, CaseFoldCollation).ToLower());
        }

        if (!string.IsNullOrWhiteSpace(request.SourceKey))
        {
            var sourceKey = request.SourceKey.Trim().ToLowerInvariant();
            query = System.Text.Ascii.IsValid(sourceKey)
                ? query.Where(row => row.Span.SourceKey != null && EF.Functions.Collate(row.Span.SourceKey, AsciiFoldCollation).ToLower() == sourceKey)
                : query.Where(row => row.Span.SourceKey != null
                    && EF.Functions.Collate(row.Span.SourceKey, CaseFoldCollation).ToLower() == EF.Functions.Collate(sourceKey, CaseFoldCollation).ToLower());
        }

        if (request.TagIds is { Length: > 0 } tagIds)
            query = query.Where(row => row.Span.TagId != null && tagIds.Contains(row.Span.TagId.Value));

        if (request.RefIds is { Length: > 0 } refIds)
            query = query.Where(row => row.Span.RefIds.Any(refId => refIds.Contains(refId)));

        if (request.PerformerIds is { Length: > 0 } performerIds)
        {
            // A span shows a performer through a segment that references one of the performer's faces
            // or, on a performer-kind segment, the performer directly.
            var visiblePerformerIds = await db.Performers.AsNoTracking()
                .Where(performer => performerIds.Contains(performer.Id))
                .Select(performer => performer.Id)
                .ToListAsync(ct);
            var faceRefIds = await db.Faces.AsNoTracking()
                .Where(face => face.PerformerId != null && visiblePerformerIds.Contains(face.PerformerId.Value))
                .Select(face => (long)face.Id)
                .ToArrayAsync(ct);
            var directRefIds = visiblePerformerIds.Select(id => (long)id).ToArray();
            query = query.Where(row =>
                row.Span.RefIds.Any(refId => faceRefIds.Contains(refId))
                || (row.Span.Kind != null && EF.Functions.Collate(row.Span.Kind, AsciiFoldCollation).ToLower() == "performer"
                    && row.Span.RefIds.Any(refId => directRefIds.Contains(refId))));
        }

        if (request.Confidence is { } confidence)
            query = ApplyConfidenceCriterion(query, confidence, request.Confidence2, request.ConfidenceModifier);

        if (!string.IsNullOrWhiteSpace(request.Q))
            query = await ApplyTextSearchAsync(query, request.Q.Trim(), ct);

        return query;
    }

    private IQueryable<Video> BuildVideoQuery(SegmentSpanSearchRequestDto request)
    {
        var videos = db.Videos.AsNoTracking().AsQueryable();
        var excludeIds = request.ExcludeVideoIds ?? [];
        if (excludeIds.Length > 0)
            videos = videos.Where(video => !excludeIds.Contains(video.Id));

        if (request.VideoTagIds is { Length: > 0 } videoTagIds)
        {
            var effectiveVideoTags = EffectiveHostTagQuery.ForHostType(db, AffinityHostType.Video);
            videos = videos.Where(video => effectiveVideoTags.Any(tag => tag.HostId == video.Id && videoTagIds.Contains(tag.TagId)));
        }

        if (request.VideoIds is { Length: > 0 } videoIds)
            return videos.Where(video => videoIds.Contains(video.Id));

        if (!string.IsNullOrWhiteSpace(request.VideoTitle))
        {
            var titleTerm = request.VideoTitle.Trim();
            videos = videos.Where(video => video.Title != null && video.Title.Contains(titleTerm));
        }

        return videos;
    }

    private static IOrderedQueryable<SpanRow> ApplyVideoOrdering(IQueryable<SpanRow> query, string sort, bool descending, bool reverse)
    {
        var byVideo = (sort, descending ^ reverse) switch
        {
            ("title", false) => query.OrderBy(row => NaturalSort.Key(row.Video.Title)).ThenBy(row => row.Video.Id),
            ("title", true) => query.OrderByDescending(row => NaturalSort.Key(row.Video.Title)).ThenByDescending(row => row.Video.Id),
            ("created_at", false) => query.OrderBy(row => row.Video.CreatedAt).ThenBy(row => row.Video.Id),
            ("created_at", true) => query.OrderByDescending(row => row.Video.CreatedAt).ThenByDescending(row => row.Video.Id),
            (_, false) => query.OrderBy(row => row.Video.UpdatedAt).ThenBy(row => row.Video.Id),
            _ => query.OrderByDescending(row => row.Video.UpdatedAt).ThenByDescending(row => row.Video.Id),
        };

        // A video's own spans always run from its start to its end.
        return reverse ? byVideo.ThenByDescending(row => row.Span.Ordinal) : byVideo.ThenBy(row => row.Span.Ordinal);
    }

    private IOrderedQueryable<SpanRow> ApplySpanOrdering(IQueryable<SpanRow> query, string sort, bool descending, int? seed, bool reverse)
    {
        var mixSeed = seed ?? 1;
        return sort switch
        {
            // The stored key is fixed per span; mixing in the seed gives each seed its own stable shuffle.
            "random" => OrderSpansBy(query, row => (long)(row.Span.RandomKey ^ mixSeed) * 2654435761L % 4294967296L, descending, reverse),
            "start_sec" or "span_start" => OrderSpansBy(query, row => row.Span.StartSec, descending, reverse),
            "end_sec" or "span_end" => OrderSpansBy(query, row => row.Span.EndSec, descending, reverse),
            "duration" or "span_duration" => OrderSpansBy(query, row => row.Span.DurationSec, descending, reverse),
            "kind" or "segment_kind" => OrderSpansByText(query, row => row.Span.Kind ?? string.Empty, descending, reverse),
            "source_key" or "segment_source_key" => OrderSpansByText(query, row => row.Span.SourceKey ?? string.Empty, descending, reverse),
            "tag_name" or "segment_tag_name" => OrderSpansByText(query, row =>
                (row.Span.TagId == null
                    ? row.Span.Label
                    : db.Tags.Where(tag => tag.Id == row.Span.TagId).Select(tag => tag.Name).FirstOrDefault()) ?? string.Empty, descending, reverse),
            "segment_count" => OrderSpansBy(query, row => row.Span.SegmentCount, descending, reverse),
            "segment_confidence" or "confidence" => OrderSpansBy(query, row => row.Span.MaxConfidence ?? -1f, descending, reverse),
            "segment_created_at" => OrderSpansBy(query, row => row.Span.MinSegmentCreatedAt, descending, reverse),
            "segment_updated_at" => OrderSpansBy(query, row => row.Span.MaxSegmentUpdatedAt, descending, reverse),
            "source_run_id" or "segment_source_run_id" => OrderSpansByText(query, row =>
                row.Span.SourceRunIds.Length == 0 ? string.Empty : row.Span.SourceRunIds[0], descending, reverse),
            "performer" or "segment_performer" => OrderSpansByText(query, row =>
                (row.Span.Kind != null && EF.Functions.Collate(row.Span.Kind, AsciiFoldCollation).ToLower() == "performer"
                    ? db.Performers.Where(performer => row.Span.EntityRefIds.Contains(performer.Id)).Min(performer => performer.Name)
                    : null)
                ?? db.Faces.Where(face => row.Span.EntityRefIds.Contains(face.Id))
                    .Join(db.Performers, face => face.PerformerId, performer => (int?)performer.Id, (face, performer) => performer.Name)
                    .Min()
                ?? string.Empty, descending, reverse),
            "ref" or "segment_ref" => OrderSpansByText(query, row =>
                db.Faces.Where(face => row.Span.EntityRefIds.Contains(face.Id))
                    .Select(face => db.Performers.Where(performer => performer.Id == face.PerformerId).Select(performer => performer.Name).FirstOrDefault() ?? face.Label)
                    .Min()
                ?? (row.Span.Kind != null && EF.Functions.Collate(row.Span.Kind, AsciiFoldCollation).ToLower() == "performer"
                    ? db.Performers.Where(performer => row.Span.EntityRefIds.Contains(performer.Id)).Min(performer => performer.Name)
                    : null)
                ?? string.Empty, descending, reverse),
            "host_title" => OrderSpansByText(query, row => row.Video.Title ?? string.Empty, descending, reverse),
            "host_id" => OrderSpansBy(query, row => row.Span.VideoId, descending, reverse),
            // "host_type": every span is a video span, so only the tie-breaks order them.
            _ => OrderSpansBy(query, row => 0, descending, reverse),
        };
    }

    // Text keys sort in the natural collation the other lists use, not the database's byte order.
    private static IOrderedQueryable<SpanRow> OrderSpansByText(
        IQueryable<SpanRow> query, Expression<Func<SpanRow, string?>> key, bool descending, bool reverse)
        => OrderSpansBy(
            query,
            Expression.Lambda<Func<SpanRow, string?>>(Expression.Call(NaturalSortKeyMethod, key.Body), key.Parameters),
            descending,
            reverse);

    private static readonly System.Reflection.MethodInfo NaturalSortKeyMethod =
        typeof(NaturalSort).GetMethod(nameof(NaturalSort.Key))!;

    private static IOrderedQueryable<SpanRow> OrderSpansBy<TKey>(
        IQueryable<SpanRow> query, Expression<Func<SpanRow, TKey>> key, bool descending, bool reverse)
    {
        // Spans that still tie keep their order within the video in both directions; reading from the
        // far end flips every key, that one included.
        var byKey = descending ^ reverse
            ? query.OrderByDescending(key).ThenByDescending(row => row.Span.VideoId).ThenByDescending(row => row.Span.StartSec)
            : query.OrderBy(key).ThenBy(row => row.Span.VideoId).ThenBy(row => row.Span.StartSec);
        return reverse ? byKey.ThenByDescending(row => row.Span.Ordinal) : byKey.ThenBy(row => row.Span.Ordinal);
    }

    /// <summary>
    /// A text criterion over the distinct non-blank values the span's segments carry: a positive
    /// modifier passes when any value matches, a negative one when none does.
    /// </summary>
    private static IQueryable<SpanRow> ApplyTextCriterion(
        IQueryable<SpanRow> query, Expression<Func<SpanRow, string[]>> values, string? expected, string? modifier)
    {
        var normalized = NormalizeModifier(modifier);
        if (normalized == "IS_NULL")
            return query.Where(Compose(values, items => items.Length == 0));
        if (normalized == "NOT_NULL")
            return query.Where(Compose(values, items => items.Length > 0));
        if (string.IsNullOrWhiteSpace(expected))
            return query;

        var text = expected.Trim();
        // Folded by the database on both sides, so the term and the stored text follow the same rules.
        var lower = text;
        return normalized switch
        {
            "EQUALS" => query.Where(Compose(values, items => items.Any(item => EF.Functions.Collate(item, CaseFoldCollation).ToLower() == EF.Functions.Collate(lower, CaseFoldCollation).ToLower()))),
            "NOT_EQUALS" => query.Where(Compose(values, items => !items.Any(item => EF.Functions.Collate(item, CaseFoldCollation).ToLower() == EF.Functions.Collate(lower, CaseFoldCollation).ToLower()))),
            "INCLUDES" => query.Where(Compose(values, items => items.Any(item => EF.Functions.Collate(item, CaseFoldCollation).ToLower().Contains(EF.Functions.Collate(lower, CaseFoldCollation).ToLower())))),
            "EXCLUDES" => query.Where(Compose(values, items => !items.Any(item => EF.Functions.Collate(item, CaseFoldCollation).ToLower().Contains(EF.Functions.Collate(lower, CaseFoldCollation).ToLower())))),
            "MATCHES_REGEX" => query.Where(Compose(values, items => items.Any(item => Regex.IsMatch(EF.Functions.Collate(item, CaseFoldCollation), text, RegexOptions.IgnoreCase)))),
            "NOT_MATCHES_REGEX" => query.Where(Compose(values, items => !items.Any(item => Regex.IsMatch(EF.Functions.Collate(item, CaseFoldCollation), text, RegexOptions.IgnoreCase)))),
            _ => query,
        };
    }

    private static IQueryable<SpanRow> ApplyNumberCriterion(
        IQueryable<SpanRow> query, Expression<Func<SpanRow, double>> actual, double value, double? value2, string? modifier)
    {
        var low = value2 is { } second ? Math.Min(value, second) : value;
        var high = value2 is { } other ? Math.Max(value, other) : value;
        return NormalizeModifier(modifier) switch
        {
            "NOT_EQUALS" => query.Where(Compose(actual, number => number != value)),
            "LESS_THAN" => query.Where(Compose(actual, number => number < value)),
            "BETWEEN" when value2.HasValue => query.Where(Compose(actual, number => number >= low && number <= high)),
            "NOT_BETWEEN" when value2.HasValue => query.Where(Compose(actual, number => number < low || number > high)),
            "EQUALS" => query.Where(Compose(actual, number => number == value)),
            _ => query.Where(Compose(actual, number => number > value)),
        };
    }

    /// <summary>
    /// "Any segment's confidence satisfies the criterion", from the span's lowest and highest
    /// confidence. Those decide every comparison except an exact value or a range that lies strictly
    /// between them, for which the span's segments are read.
    /// </summary>
    private IQueryable<SpanRow> ApplyConfidenceCriterion(IQueryable<SpanRow> query, float value, float? value2, string? modifier)
    {
        var low = value2 is { } second ? Math.Min(value, second) : value;
        var high = value2 is { } other ? Math.Max(value, other) : value;
        return NormalizeModifier(modifier) switch
        {
            "NOT_EQUALS" => query.Where(row => row.Span.MinConfidence != null
                && (row.Span.MinConfidence != value || row.Span.MaxConfidence != value)),
            "LESS_THAN" => query.Where(row => row.Span.MinConfidence < value),
            "BETWEEN" when value2.HasValue => query.Where(row => row.Span.MinConfidence != null
                && ((row.Span.MinConfidence >= low && row.Span.MinConfidence <= high)
                    || (row.Span.MaxConfidence >= low && row.Span.MaxConfidence <= high)
                    || (row.Span.MinConfidence < low && row.Span.MaxConfidence > high
                        && db.Segments.Any(segment => row.Span.SegmentIds.Contains(segment.Id)
                            && segment.Confidence >= low && segment.Confidence <= high)))),
            "NOT_BETWEEN" when value2.HasValue => query.Where(row => row.Span.MinConfidence < low || row.Span.MaxConfidence > high),
            "EQUALS" => query.Where(row => row.Span.MinConfidence != null
                && (row.Span.MinConfidence == value
                    || row.Span.MaxConfidence == value
                    || (row.Span.MinConfidence < value && row.Span.MaxConfidence > value
                        && db.Segments.Any(segment => row.Span.SegmentIds.Contains(segment.Id) && segment.Confidence == value)))),
            _ => query.Where(row => row.Span.MaxConfidence > value),
        };
    }

    private IQueryable<SpanRow> ApplyCreatedAtCriterion(IQueryable<SpanRow> query, string? expected, string? expected2, string? modifier)
    {
        if (!TryParseDateRange(expected, expected2, out var value, out var low, out var high))
            return query;

        return NormalizeModifier(modifier) switch
        {
            "NOT_EQUALS" => query.Where(row => row.Span.MinSegmentCreatedAt != value || row.Span.MaxSegmentCreatedAt != value),
            "LESS_THAN" => query.Where(row => row.Span.MinSegmentCreatedAt < value),
            "BETWEEN" => query.Where(row =>
                (row.Span.MinSegmentCreatedAt >= low && row.Span.MinSegmentCreatedAt <= high)
                || (row.Span.MaxSegmentCreatedAt >= low && row.Span.MaxSegmentCreatedAt <= high)
                || (row.Span.MinSegmentCreatedAt < low && row.Span.MaxSegmentCreatedAt > high
                    && db.Segments.Any(segment => row.Span.SegmentIds.Contains(segment.Id)
                        && segment.CreatedAt >= low && segment.CreatedAt <= high))),
            "NOT_BETWEEN" => query.Where(row => row.Span.MinSegmentCreatedAt < low || row.Span.MaxSegmentCreatedAt > high),
            "EQUALS" => query.Where(row =>
                row.Span.MinSegmentCreatedAt == value
                || row.Span.MaxSegmentCreatedAt == value
                || (row.Span.MinSegmentCreatedAt < value && row.Span.MaxSegmentCreatedAt > value
                    && db.Segments.Any(segment => row.Span.SegmentIds.Contains(segment.Id) && segment.CreatedAt == value))),
            _ => query.Where(row => row.Span.MaxSegmentCreatedAt > value),
        };
    }

    private IQueryable<SpanRow> ApplyUpdatedAtCriterion(IQueryable<SpanRow> query, string? expected, string? expected2, string? modifier)
    {
        if (!TryParseDateRange(expected, expected2, out var value, out var low, out var high))
            return query;

        return NormalizeModifier(modifier) switch
        {
            "NOT_EQUALS" => query.Where(row => row.Span.MinSegmentUpdatedAt != value || row.Span.MaxSegmentUpdatedAt != value),
            "LESS_THAN" => query.Where(row => row.Span.MinSegmentUpdatedAt < value),
            "BETWEEN" => query.Where(row =>
                (row.Span.MinSegmentUpdatedAt >= low && row.Span.MinSegmentUpdatedAt <= high)
                || (row.Span.MaxSegmentUpdatedAt >= low && row.Span.MaxSegmentUpdatedAt <= high)
                || (row.Span.MinSegmentUpdatedAt < low && row.Span.MaxSegmentUpdatedAt > high
                    && db.Segments.Any(segment => row.Span.SegmentIds.Contains(segment.Id)
                        && segment.UpdatedAt >= low && segment.UpdatedAt <= high))),
            "NOT_BETWEEN" => query.Where(row => row.Span.MinSegmentUpdatedAt < low || row.Span.MaxSegmentUpdatedAt > high),
            "EQUALS" => query.Where(row =>
                row.Span.MinSegmentUpdatedAt == value
                || row.Span.MaxSegmentUpdatedAt == value
                || (row.Span.MinSegmentUpdatedAt < value && row.Span.MaxSegmentUpdatedAt > value
                    && db.Segments.Any(segment => row.Span.SegmentIds.Contains(segment.Id) && segment.UpdatedAt == value))),
            _ => query.Where(row => row.Span.MaxSegmentUpdatedAt > value),
        };
    }

    /// <summary>
    /// The free-text search: the video title, the span's own key, source, kind and tag, its segments'
    /// titles, and the face or performer its segments reference.
    /// </summary>
    private async Task<IQueryable<SpanRow>> ApplyTextSearchAsync(IQueryable<SpanRow> query, string term, CancellationToken ct)
    {
        var lower = term;

        // A reference is labelled by the face with that id (its performer's name when linked, else its
        // own label), or on a performer-kind segment by the performer with that id.
        var faceRefIds = await db.Faces.AsNoTracking()
            .Where(face =>
                EF.Functions.Collate(
                        db.Performers.Where(performer => performer.Id == face.PerformerId).Select(performer => performer.Name).FirstOrDefault() ?? face.Label ?? string.Empty,
                        CaseFoldCollation)
                    .ToLower().Contains(EF.Functions.Collate(lower, CaseFoldCollation).ToLower()))
            .Select(face => (long)face.Id)
            .ToArrayAsync(ct);
        var performerRefIds = await db.Performers.AsNoTracking()
            .Where(performer => EF.Functions.Collate(performer.Name, CaseFoldCollation).ToLower().Contains(EF.Functions.Collate(lower, CaseFoldCollation).ToLower()))
            .Select(performer => (long)performer.Id)
            .ToArrayAsync(ct);

        return query.Where(row =>
            (row.Video.Title != null && EF.Functions.Collate(row.Video.Title, CaseFoldCollation).ToLower().Contains(EF.Functions.Collate(lower, CaseFoldCollation).ToLower()))
            || EF.Functions.Collate(row.Span.SpanKey, CaseFoldCollation).ToLower().Contains(EF.Functions.Collate(lower, CaseFoldCollation).ToLower())
            || (row.Span.SourceKey != null && EF.Functions.Collate(row.Span.SourceKey, CaseFoldCollation).ToLower().Contains(EF.Functions.Collate(lower, CaseFoldCollation).ToLower()))
            || (row.Span.Kind != null && EF.Functions.Collate(row.Span.Kind, CaseFoldCollation).ToLower().Contains(EF.Functions.Collate(lower, CaseFoldCollation).ToLower()))
            || (row.Span.TagId == null && row.Span.Label != null && EF.Functions.Collate(row.Span.Label, CaseFoldCollation).ToLower().Contains(EF.Functions.Collate(lower, CaseFoldCollation).ToLower()))
            || db.Tags.Any(tag => tag.Id == row.Span.TagId && EF.Functions.Collate(tag.Name, CaseFoldCollation).ToLower().Contains(EF.Functions.Collate(lower, CaseFoldCollation).ToLower()))
            || row.Span.Titles.Any(title => EF.Functions.Collate(title, CaseFoldCollation).ToLower().Contains(EF.Functions.Collate(lower, CaseFoldCollation).ToLower()))
            || row.Span.RefIds.Any(refId => faceRefIds.Contains(refId))
            || (row.Span.Kind != null && EF.Functions.Collate(row.Span.Kind, AsciiFoldCollation).ToLower() == "performer"
                && row.Span.RefIds.Any(refId => performerRefIds.Contains(refId))));
    }

    private static bool TryParseDateRange(string? expected, string? expected2, out DateTime value, out DateTime low, out DateTime high)
    {
        low = high = default;
        if (!TryParseDateTime(expected, out value))
            return false;

        _ = TryParseDateTime(expected2, out var second);
        var upper = second == default ? value : second;
        low = value < upper ? value : upper;
        high = value < upper ? upper : value;
        return true;
    }

    private static bool TryParseDateTime(string? value, out DateTime parsed)
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

    private static string NormalizeModifier(string? modifier)
        => string.IsNullOrWhiteSpace(modifier) ? "GREATER_THAN" : modifier.Trim().ToUpperInvariant();

    private static bool IsNullnessModifier(string? modifier)
        => NormalizeModifier(modifier) is "IS_NULL" or "NOT_NULL";

    /// <summary>Applies <paramref name="predicate"/> to the value <paramref name="selector"/> picks from a row.</summary>
    private static Expression<Func<SpanRow, bool>> Compose<TValue>(
        Expression<Func<SpanRow, TValue>> selector, Expression<Func<TValue, bool>> predicate)
        => Expression.Lambda<Func<SpanRow, bool>>(
            new ParameterReplacer(predicate.Parameters[0], selector.Body).Visit(predicate.Body),
            selector.Parameters);

    private sealed class ParameterReplacer(ParameterExpression parameter, Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            => node == parameter ? replacement : base.VisitParameter(node);
    }
}
