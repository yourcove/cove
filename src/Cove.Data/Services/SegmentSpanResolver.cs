using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Cove.Data.Services;

public sealed class SegmentSpanResolver(CoveContext db, ICurrentPrincipalAccessor principalAccessor, IMemoryCache memoryCache)
{
    private const double BuiltInDefaultMergeGapSec = 8d;
    private const double BuiltInDefaultMinDurationSec = 10d;
    private const int DefaultSpanCacheMinutes = 5;
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> VideoCacheKeys = new();
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> ProfileCacheKeys = new();
    private static readonly SemaphoreSlim ProfileInitializationLock = new(1, 1);

    public async Task<VideoResolvedSpansDto> ResolveVideoAsync(int videoId, int? profileId, CancellationToken ct)
    {
        var profile = await ResolveProfileAsync(profileId, ct);
        var cacheKey = $"segment-spans:{videoId}:{profile.Id}:{profile.Version}";
        if (memoryCache.TryGetValue<VideoResolvedSpansDto>(cacheKey, out var cached) && cached is not null)
            return cached;

        var segments = await LoadVideoSegmentsAsync(videoId, ct);
        var rules = await LoadRulesAsync(profile.Id, ct);
        var spans = BuildVideoSpans(videoId, profile, segments, rules);
        var response = new VideoResolvedSpansDto(spans, profile.Id, profile.Version);

        memoryCache.Set(cacheKey, response, TimeSpan.FromMinutes(DefaultSpanCacheMinutes));
        RegisterCacheKey(videoId, profile.Id, cacheKey);

        return response;
    }

    public async Task<IReadOnlyList<(int VideoId, IReadOnlyList<ResolvedSpan> Spans)>> ResolveVideosBatchAsync(
        IReadOnlyList<int> videoIds, int profileId, CancellationToken ct)
    {
        if (videoIds.Count == 0) return [];

        var profile = await ResolveProfileAsync(profileId, ct);
        var rules = await LoadRulesAsync(profile.Id, ct);

        var results = new List<(int VideoId, IReadOnlyList<ResolvedSpan> Spans)>(videoIds.Count);
        var uncachedIds = new List<int>(videoIds.Count);

        foreach (var videoId in videoIds)
        {
            var cacheKey = $"segment-spans:{videoId}:{profile.Id}:{profile.Version}";
            if (memoryCache.TryGetValue<VideoResolvedSpansDto>(cacheKey, out var cached) && cached is not null)
            {
                results.Add((videoId, cached.Spans));
            }
            else
            {
                uncachedIds.Add(videoId);
            }
        }

        if (uncachedIds.Count == 0)
            return results;

        var allSegments = await db.VisibleSegments().AsNoTracking()
            .Include(segment => segment.Tag).ThenInclude(tag => tag!.TagGroup)
            .Where(s => s.HostType == SegmentHostType.Video && uncachedIds.Contains(s.HostId))
            .OrderBy(s => s.StartSec)
            .ThenBy(s => s.Id)
            .ToListAsync(ct);

        var segmentsByHost = allSegments.GroupBy(s => s.HostId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var videoId in uncachedIds)
        {
            var segs = segmentsByHost.TryGetValue(videoId, out var hostSegments) ? hostSegments : [];
            var spans = BuildVideoSpans(videoId, profile, segs.AsReadOnly(), rules);
            var response = new VideoResolvedSpansDto(spans, profile.Id, profile.Version);

            var cacheKey = $"segment-spans:{videoId}:{profile.Id}:{profile.Version}";
            memoryCache.Set(cacheKey, response, TimeSpan.FromMinutes(DefaultSpanCacheMinutes));
            RegisterCacheKey(videoId, profile.Id, cacheKey);

            results.Add((videoId, spans));
        }

        return results;
    }

    public async Task<IReadOnlyList<(int VideoId, IReadOnlyList<ResolvedSpan> Spans)>> QueryVideosBatchAsync(
        IReadOnlyList<int> videoIds, SegmentSpanQueryRequestDto queryRequest, CancellationToken ct)
    {
        if (videoIds.Count == 0 || queryRequest.Operands is null || queryRequest.Operands.Count == 0)
            return [];

        var profile = await ResolveProfileAsync(queryRequest.Profile, ct);
        var requestJson = JsonSerializer.Serialize(new { queryRequest.Operator, Operands = queryRequest.Operands, queryRequest.MergeGapSec, queryRequest.MinDurationSec });
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestJson))[..10]).ToLowerInvariant();

        var results = new List<(int VideoId, IReadOnlyList<ResolvedSpan> Spans)>(videoIds.Count);
        var uncachedIds = new List<int>(videoIds.Count);

        foreach (var videoId in videoIds)
        {
            var cacheKey = $"video-segment-query:{videoId}:{profile.Id}:{profile.Version}:{requestHash}";
            if (memoryCache.TryGetValue<IReadOnlyList<ResolvedSpan>>(cacheKey, out var cachedSpans) && cachedSpans is not null)
            {
                results.Add((videoId, cachedSpans));
            }
            else
            {
                uncachedIds.Add(videoId);
            }
        }

        if (uncachedIds.Count == 0)
            return results;

        var allSegments = await db.VisibleSegments().AsNoTracking()
            .Include(segment => segment.Tag).ThenInclude(tag => tag!.TagGroup)
            .Where(s => s.HostType == SegmentHostType.Video && uncachedIds.Contains(s.HostId))
            .OrderBy(s => s.StartSec)
            .ThenBy(s => s.Id)
            .ToListAsync(ct);

        var segmentsByHost = allSegments.GroupBy(s => s.HostId).ToDictionary(g => g.Key, g => g.ToList());
        var operators = queryRequest.Operator.Trim().ToLowerInvariant();

        foreach (var videoId in uncachedIds)
        {
            var segs = segmentsByHost.TryGetValue(videoId, out var hostSegments) ? hostSegments : [];

            var operandMatches = queryRequest.Operands.Select(operand => MatchOperand(segs, operand)).ToList();
            var intervalSets = operandMatches.Select(match => match.Intervals).ToList();
            List<IntervalAlgebra.Interval> result = operators switch
            {
                "union" => IntervalAlgebra.Union(intervalSets.SelectMany(set => set)),
                "intersection" => IntervalAlgebra.Intersection(intervalSets),
                "difference" when intervalSets.Count > 0 => intervalSets.Skip(1).Aggregate(intervalSets[0], (current, next) => IntervalAlgebra.Difference(current, next)),
                _ => throw new InvalidOperationException($"Unsupported span query operator '{queryRequest.Operator}'."),
            };

            var mergeGapSec = queryRequest.MergeGapSec ?? GetDefaultMergeGap(operators, profile.Id, videoId, segs);
            var minDurationSec = queryRequest.MinDurationSec ?? GetDefaultMinDuration(operators, profile.Id, videoId, segs);
            result = IntervalAlgebra.Filter(IntervalAlgebra.Merge(result, mergeGapSec), minDurationSec);

            List<ResolvedSpan> resolvedSpans;
            if (result.Count == 0)
            {
                resolvedSpans = [];
            }
            else
            {
                var contributingIds = new HashSet<int>(operandMatches.SelectMany(match => match.SegmentIds));
                resolvedSpans = new List<ResolvedSpan>(result.Count);
                foreach (var interval in result)
                {
                    var overlappingIds = segs
                        .Where(segment => contributingIds.Contains(segment.Id) && (segment.EndSec ?? segment.StartSec) > interval.Start && segment.StartSec < interval.End)
                        .Select(segment => segment.Id)
                        .OrderBy(id => id)
                        .ToList();

                    resolvedSpans.Add(new ResolvedSpan(
                        ResolvedSpanKeys.CreateDerivedQuery(operators, interval.Start, interval.End),
                        SegmentHostType.Video,
                        videoId,
                        interval.Start,
                        interval.End,
                        "derived",
                        operators,
                        null,
                        null,
                        null,
                        null,
                        false,
                        overlappingIds));
                }
            }

            var cacheKey = $"video-segment-query:{videoId}:{profile.Id}:{profile.Version}:{requestHash}";
            memoryCache.Set(cacheKey, (IReadOnlyList<ResolvedSpan>)resolvedSpans, TimeSpan.FromMinutes(DefaultSpanCacheMinutes));
            RegisterCacheKey(videoId, profile.Id, cacheKey);

            results.Add((videoId, resolvedSpans));
        }

        return results;
    }

    public async Task<IReadOnlyList<ResolvedSpan>> PreviewVideoAsync(int videoId, IReadOnlyList<SegmentDisplayRule> rules, CancellationToken ct)
    {
        var segments = await LoadVideoSegmentsAsync(videoId, ct);
        return BuildVideoSpans(videoId, new SegmentDisplayProfile { Id = 0, Name = "Preview", Version = 1 }, segments, rules);
    }

    public async Task<ResolvedSpanDetailDto?> GetSpanDetailAsync(int videoId, string spanKey, int? profileId, CancellationToken ct)
    {
        var resolved = await ResolveVideoAsync(videoId, profileId, ct);
        var span = resolved.Spans.FirstOrDefault(item => string.Equals(item.SpanKey, spanKey, StringComparison.Ordinal));
        var isDerivedQuerySpan = false;
        var segments = await LoadVideoSegmentsAsync(videoId, ct);
        if (span is null)
        {
            if (!ResolvedSpanKeys.TryParseDerivedQuery(spanKey, out var derivedKind, out var startSec, out var endSec))
                return null;

            var overlappingIds = segments
                .Where(segment => (segment.EndSec ?? segment.StartSec) > startSec && segment.StartSec < endSec)
                .Select(segment => segment.Id)
                .OrderBy(id => id)
                .ToList();

            span = new ResolvedSpan(
                spanKey,
                SegmentHostType.Video,
                videoId,
                startSec,
                endSec,
                "derived",
                derivedKind,
                null,
                null,
                null,
                null,
                false,
                overlappingIds);
            isDerivedQuerySpan = true;
        }

        var video = await db.Videos.AsNoTracking()
            .Where(item => item.Id == videoId)
            .Select(item => new { item.Id, item.Title })
            .FirstOrDefaultAsync(ct);

        if (isDerivedQuerySpan)
        {
            return new ResolvedSpanDetailDto(
                span,
                videoId,
                video?.Title,
                [new ResolvedSpanIntervalDto(span.StartSec, span.EndSec)],
                resolved.ProfileId,
                resolved.ProfileVersion);
        }

        var segmentMap = segments.ToDictionary(segment => segment.Id);
        var intervals = new List<IntervalAlgebra.Interval>();
        foreach (var segmentId in span.SegmentIds)
        {
            if (!segmentMap.TryGetValue(segmentId, out var segment))
                continue;

            var endSec = segment.EndSec ?? segment.StartSec;
            if (endSec > segment.StartSec)
                intervals.Add(new IntervalAlgebra.Interval(segment.StartSec, endSec));
        }

        if (intervals.Count == 0 && span.EndSec > span.StartSec)
            intervals.Add(new IntervalAlgebra.Interval(span.StartSec, span.EndSec));

        return new ResolvedSpanDetailDto(
            span,
            videoId,
            video?.Title,
            IntervalAlgebra.Union(intervals).Select(interval => new ResolvedSpanIntervalDto(interval.Start, interval.End)).ToList(),
            resolved.ProfileId,
            resolved.ProfileVersion);
    }

    public async Task<IReadOnlyList<ResolvedSpan>> QueryVideoAsync(int videoId, SegmentSpanQueryRequestDto request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var profile = await ResolveProfileAsync(request.Profile, ct);
        if (request.Operands is null || request.Operands.Count == 0)
            return [];

        // Cache derived query results per (video, profile version, request shape) to avoid
        // redundant interval algebra on repeated page navigations.
        var requestJson = JsonSerializer.Serialize(new { request.Operator, Operands = request.Operands, request.MergeGapSec, request.MinDurationSec });
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestJson))[..10]).ToLowerInvariant();
        var queryCacheKey = $"video-segment-query:{videoId}:{profile.Id}:{profile.Version}:{requestHash}";
        if (memoryCache.TryGetValue<IReadOnlyList<ResolvedSpan>>(queryCacheKey, out var cachedSpans) && cachedSpans is not null)
            return cachedSpans;

        var segments = await LoadVideoSegmentsAsync(videoId, ct);
        var operators = request.Operator.Trim().ToLowerInvariant();

        var operandMatches = request.Operands.Select(operand => MatchOperand(segments, operand)).ToList();
        var intervalSets = operandMatches.Select(match => match.Intervals).ToList();
        List<IntervalAlgebra.Interval> result = operators switch
        {
            "union" => IntervalAlgebra.Union(intervalSets.SelectMany(set => set)),
            "intersection" => IntervalAlgebra.Intersection(intervalSets),
            "difference" when intervalSets.Count > 0 => intervalSets.Skip(1).Aggregate(intervalSets[0], (current, next) => IntervalAlgebra.Difference(current, next)),
            _ => throw new InvalidOperationException($"Unsupported span query operator '{request.Operator}'."),
        };

        var mergeGapSec = request.MergeGapSec ?? GetDefaultMergeGap(operators, profile.Id, videoId, segments);
        var minDurationSec = request.MinDurationSec ?? GetDefaultMinDuration(operators, profile.Id, videoId, segments);
        result = IntervalAlgebra.Filter(IntervalAlgebra.Merge(result, mergeGapSec), minDurationSec);
        if (result.Count == 0)
        {
            memoryCache.Set(queryCacheKey, (IReadOnlyList<ResolvedSpan>)[], TimeSpan.FromMinutes(DefaultSpanCacheMinutes));
            RegisterCacheKey(videoId, profile.Id, queryCacheKey);
            return [];
        }

        var contributingIds = new HashSet<int>(operandMatches.SelectMany(match => match.SegmentIds));
        var spans = new List<ResolvedSpan>(result.Count);
        foreach (var interval in result)
        {
            var overlappingIds = segments
                .Where(segment => contributingIds.Contains(segment.Id) && (segment.EndSec ?? segment.StartSec) > interval.Start && segment.StartSec < interval.End)
                .Select(segment => segment.Id)
                .OrderBy(id => id)
                .ToList();

            spans.Add(new ResolvedSpan(
                ResolvedSpanKeys.CreateDerivedQuery(operators, interval.Start, interval.End),
                SegmentHostType.Video,
                videoId,
                interval.Start,
                interval.End,
                "derived",
                operators,
                null,
                null,
                null,
                null,
                false,
                overlappingIds));
        }

        memoryCache.Set(queryCacheKey, (IReadOnlyList<ResolvedSpan>)spans, TimeSpan.FromMinutes(DefaultSpanCacheMinutes));
        RegisterCacheKey(videoId, profile.Id, queryCacheKey);
        return spans;
    }

    public void EvictVideo(int videoId)
    {
        if (!VideoCacheKeys.TryRemove(videoId, out var keys))
            return;

        foreach (var key in keys.Keys)
            memoryCache.Remove(key);
    }

    public void EvictProfile(int profileId)
    {
        if (!ProfileCacheKeys.TryRemove(profileId, out var keys))
            return;

        foreach (var key in keys.Keys)
            memoryCache.Remove(key);

        memoryCache.Remove($"segment-display-rules:{profileId}");
    }

    public async Task<int> ResolveProfileIdAsync(int? profileId, CancellationToken ct)
    {
        var profile = await ResolveProfileAsync(profileId, ct);
        return profile.Id;
    }

    public async Task<SegmentDisplayProfile> EnsureDefaultProfileAsync(int? userId, CancellationToken ct)
    {
        await ProfileInitializationLock.WaitAsync(ct);
        try
        {
            await EnsureBuiltInProfilesLockedAsync(ct);

            // Prefer the user's own default if they have explicitly created one, but do NOT
            // auto-create a per-user "Default" copy — that produced a confusing duplicate profile
            // that reappeared every time it was deleted. Users without a personal profile fall back
            // to the shared global default and can create their own profile when they want to customize.
            if (userId.HasValue)
            {
                var userProfile = await db.SegmentDisplayProfiles.FirstOrDefaultAsync(
                    profile => profile.UserId == userId.Value && profile.IsDefault,
                    ct);
                if (userProfile is not null)
                    return userProfile;
            }

            var globalDefault = await db.SegmentDisplayProfiles.FirstOrDefaultAsync(
                profile => profile.UserId == null && profile.IsDefault,
                ct);
            if (globalDefault is not null)
                return globalDefault;

            throw new InvalidOperationException("No global segment display profile exists.");
        }
        finally
        {
            ProfileInitializationLock.Release();
        }
    }

    private async Task<SegmentDisplayProfile> ResolveProfileAsync(int? profileId, CancellationToken ct)
    {
        if (profileId.HasValue)
        {
            await EnsureBuiltInProfilesAsync(ct);

            var explicitProfile = await db.SegmentDisplayProfiles.AsNoTracking().FirstOrDefaultAsync(profile => profile.Id == profileId.Value, ct);
            if (explicitProfile is null)
                throw new InvalidOperationException($"Segment display profile {profileId.Value} was not found.");

            return explicitProfile;
        }

        return await EnsureDefaultProfileAsync(principalAccessor.Current?.UserId, ct);
    }

    private async Task<List<Segment>> LoadVideoSegmentsAsync(int videoId, CancellationToken ct)
    {
        var cacheKey = $"video-raw-segments:{videoId}";
        if (memoryCache.TryGetValue<List<Segment>>(cacheKey, out var cached) && cached is not null)
            return cached;

        var segments = await db.VisibleSegments().AsNoTracking()
            .Include(segment => segment.Tag).ThenInclude(tag => tag!.TagGroup)
            .Where(segment => segment.HostType == SegmentHostType.Video && segment.HostId == videoId)
            .OrderBy(segment => segment.StartSec)
            .ThenBy(segment => segment.Id)
            .ToListAsync(ct);

        memoryCache.Set(cacheKey, segments, TimeSpan.FromMinutes(5));
        RegisterVideoCacheKey(videoId, cacheKey);
        return segments;
    }

    private async Task<List<SegmentDisplayRule>> LoadRulesAsync(int profileId, CancellationToken ct)
    {
        var cacheKey = $"segment-display-rules:{profileId}";
        if (memoryCache.TryGetValue<List<SegmentDisplayRule>>(cacheKey, out var cached) && cached is not null)
            return cached;

        var rules = await db.SegmentDisplayRules.AsNoTracking()
            .Where(rule => rule.ProfileId == profileId)
            .OrderByDescending(rule => rule.TagId.HasValue)
            .ThenByDescending(rule => rule.Kind != null)
            .ThenByDescending(rule => rule.SourceKey != null)
            .ThenByDescending(rule => rule.TagCategory != null)
            .ThenByDescending(rule => rule.HostType.HasValue)
            .ThenByDescending(rule => rule.Priority ?? 0)
            .ThenBy(rule => rule.Id)
            .ToListAsync(ct);

        memoryCache.Set(cacheKey, rules, TimeSpan.FromMinutes(5));
        return rules;
    }

    private static List<ResolvedSpan> BuildVideoSpans(int videoId, SegmentDisplayProfile profile, IReadOnlyList<Segment> segments, IReadOnlyList<SegmentDisplayRule> rules)
    {
        var buckets = new Dictionary<SpanBucketKey, List<Segment>>();

        foreach (var segment in segments)
        {
            var rule = FindMatchingRule(segment, rules);
            if (rule?.Visible == false)
                continue;

            if (rule?.MinConfidence is float minConfidence && (!segment.Confidence.HasValue || segment.Confidence.Value < minConfidence))
                continue;

            var endSec = segment.EndSec ?? segment.StartSec;
            var durationSec = Math.Max(0, endSec - segment.StartSec);

            var segmentLabel = segment.Tag?.Name ?? segment.Title;
            var bucketKey = new SpanBucketKey(
                segment.SourceKey,
                segment.Kind,
                segment.TagId,
                segment.TagId is null ? segmentLabel : null,
                segment.Tag?.SegmentColorOverride ?? segment.Tag?.Color ?? rule?.ColorOverride ?? segment.ColorHint,
                segment.Tag?.SegmentLaneOverride ?? rule?.Lane,
                rule?.CollapseToInstant == true || durationSec <= 0,
                rule?.MergeGapSec ?? 0d,
                rule?.MinDurationSec ?? 0d,
                segment.TagId is null ? segmentLabel : null);

            if (!buckets.TryGetValue(bucketKey, out var bucket))
            {
                bucket = [];
                buckets[bucketKey] = bucket;
            }

            bucket.Add(segment);
        }

        var spans = new List<ResolvedSpan>();
        foreach (var pair in buckets)
        {
            var bucket = pair.Value;
            bucket.Sort(static (left, right) =>
            {
                var startComparison = left.StartSec.CompareTo(right.StartSec);
                return startComparison != 0 ? startComparison : left.Id.CompareTo(right.Id);
            });

            if (pair.Key.CollapsedToInstant)
            {
                foreach (var segment in bucket)
                {
                    spans.Add(new ResolvedSpan(
                        ResolvedSpanKeys.Create(videoId, profile.Id, pair.Key.SourceKey, pair.Key.Kind, pair.Key.TagId, segment.StartSec, segment.StartSec),
                        SegmentHostType.Video,
                        videoId,
                        segment.StartSec,
                        segment.StartSec,
                        pair.Key.SourceKey,
                        pair.Key.Kind,
                        pair.Key.TagId,
                        segment.Tag?.Name ?? pair.Key.Label,
                        pair.Key.ColorHint,
                        pair.Key.Lane,
                        true,
                        [segment.Id]));
                }

                continue;
            }

            var currentIds = new List<int>();
            var currentStart = bucket[0].StartSec;
            var currentEnd = bucket[0].EndSec ?? bucket[0].StartSec;
            currentIds.Add(bucket[0].Id);

            for (var index = 1; index < bucket.Count; index++)
            {
                var segment = bucket[index];
                var segmentEnd = segment.EndSec ?? segment.StartSec;
                if (segment.StartSec <= currentEnd + pair.Key.MergeGapSec)
                {
                    if (segmentEnd > currentEnd)
                        currentEnd = segmentEnd;

                    currentIds.Add(segment.Id);
                    continue;
                }

                AddResolvedSpan(spans, videoId, profile.Id, pair.Key, videoId, currentStart, currentEnd, bucket[0].Tag?.Name, pair.Key.Label, currentIds);
                currentStart = segment.StartSec;
                currentEnd = segmentEnd;
                currentIds = [segment.Id];
            }

            AddResolvedSpan(spans, videoId, profile.Id, pair.Key, videoId, currentStart, currentEnd, bucket[0].Tag?.Name, pair.Key.Label, currentIds);
        }

        spans.Sort(static (left, right) =>
        {
            var startComparison = left.StartSec.CompareTo(right.StartSec);
            if (startComparison != 0)
                return startComparison;

            var endComparison = left.EndSec.CompareTo(right.EndSec);
            return endComparison != 0 ? endComparison : string.CompareOrdinal(left.SpanKey, right.SpanKey);
        });

        return spans;
    }

    private static ResolvedSpan CreateResolvedSpan(int videoId, int profileId, SpanBucketKey key, int hostId, double startSec, double endSec, string? tagName, string? fallbackLabel, List<int> segmentIds)
        => new(
            ResolvedSpanKeys.Create(videoId, profileId, key.SourceKey, key.Kind, key.TagId, startSec, endSec),
            SegmentHostType.Video,
            hostId,
            startSec,
            endSec,
            key.SourceKey,
            key.Kind,
            key.TagId,
            tagName ?? fallbackLabel,
            key.ColorHint,
            key.Lane,
            false,
            segmentIds.OrderBy(id => id).ToList());

    private static void AddResolvedSpan(List<ResolvedSpan> spans, int videoId, int profileId, SpanBucketKey key, int hostId, double startSec, double endSec, string? tagName, string? fallbackLabel, List<int> segmentIds)
    {
        if (key.MinDurationSec > 0 && endSec - startSec < key.MinDurationSec)
            return;

        spans.Add(CreateResolvedSpan(videoId, profileId, key, hostId, startSec, endSec, tagName, fallbackLabel, segmentIds));
    }

    private static SegmentDisplayRule? FindMatchingRule(Segment segment, IReadOnlyList<SegmentDisplayRule> rules)
    {
        SegmentDisplayRule? best = null;
        var bestSpecificity = int.MinValue;
        var bestPriority = int.MinValue;

        foreach (var rule in rules)
        {
            if (rule.HostType.HasValue && rule.HostType.Value != segment.HostType)
                continue;

            if (rule.TagId.HasValue && rule.TagId.Value != segment.TagId)
                continue;

            if (rule.Kind != null && !string.Equals(rule.Kind, segment.Kind, StringComparison.OrdinalIgnoreCase))
                continue;

            if (rule.SourceKey != null && !MatchesRuleValue(rule.SourceKey, segment.SourceKey))
                continue;

            if (rule.TagCategory != null && !string.Equals(rule.TagCategory, GetTagCategory(segment.Tag), StringComparison.OrdinalIgnoreCase))
                continue;

            var specificity = GetSpecificity(rule);
            var priority = rule.Priority ?? 0;
            if (specificity > bestSpecificity || (specificity == bestSpecificity && priority > bestPriority))
            {
                best = rule;
                bestSpecificity = specificity;
                bestPriority = priority;
            }
        }

        return best;
    }

    private static int GetSpecificity(SegmentDisplayRule rule)
    {
        if (rule.TagId.HasValue)
            return 300 + BonusSpecificity(rule);
        if (rule.Kind != null)
            return 200 + BonusSpecificity(rule);
        if (rule.SourceKey != null)
            return 100 + BonusSpecificity(rule);
        return BonusSpecificity(rule);
    }

    private static int BonusSpecificity(SegmentDisplayRule rule)
    {
        var score = 0;
        if (rule.HostType.HasValue)
            score += 10;
        if (rule.TagCategory != null)
            score += 1;
        if (rule.SourceKey != null && !ContainsWildcards(rule.SourceKey))
            score += 5;
        return score;
    }

    private static bool MatchesRuleValue(string ruleValue, string? candidate)
    {
        if (candidate is null)
            return false;

        if (!ContainsWildcards(ruleValue))
            return string.Equals(ruleValue, candidate, StringComparison.OrdinalIgnoreCase);

        return LikePatternMatches(ruleValue, candidate);
    }

    private static bool ContainsWildcards(string value)
        => value.Contains('%', StringComparison.Ordinal) || value.Contains('_', StringComparison.Ordinal);

    private static bool LikePatternMatches(string pattern, string candidate)
    {
        return MatchAt(0, 0);

        bool MatchAt(int patternIndex, int candidateIndex)
        {
            while (patternIndex < pattern.Length)
            {
                var token = pattern[patternIndex];
                if (token == '%')
                {
                    patternIndex++;
                    if (patternIndex >= pattern.Length)
                        return true;

                    for (var nextCandidate = candidateIndex; nextCandidate <= candidate.Length; nextCandidate++)
                    {
                        if (MatchAt(patternIndex, nextCandidate))
                            return true;
                    }

                    return false;
                }

                if (candidateIndex >= candidate.Length)
                    return false;

                if (token != '_' && char.ToUpperInvariant(token) != char.ToUpperInvariant(candidate[candidateIndex]))
                    return false;

                patternIndex++;
                candidateIndex++;
            }

            return candidateIndex == candidate.Length;
        }
    }

    private static string? GetTagCategory(Tag? tag)
    {
        return tag?.TagGroup?.Name;
    }

    private static OperandMatch MatchOperand(IReadOnlyList<Segment> segments, SegmentSpanOperandDto operand)
    {
        var matchingSegments = new List<Segment>();
        foreach (var segment in segments)
        {
            if (operand.SourceKey != null && !string.Equals(operand.SourceKey, segment.SourceKey, StringComparison.OrdinalIgnoreCase))
                continue;

            if (operand.Kind != null && !string.Equals(operand.Kind, segment.Kind, StringComparison.OrdinalIgnoreCase))
                continue;

            if (operand.TagIds is { Count: > 0 } && !MatchesAnyRequestedTag(segment, operand.TagIds))
                continue;

            if (operand.RefIds is { Count: > 0 } && (!segment.RefId.HasValue || !operand.RefIds.Contains(segment.RefId.Value)))
                continue;

            if (operand.MinConfidence.HasValue && (!segment.Confidence.HasValue || segment.Confidence.Value < operand.MinConfidence.Value))
                continue;

            matchingSegments.Add(segment);
        }

        return new OperandMatch(
            IntervalAlgebra.Union(matchingSegments
                .Select(segment => new IntervalAlgebra.Interval(segment.StartSec, segment.EndSec ?? segment.StartSec))
                .Where(interval => interval.End > interval.Start)),
            matchingSegments.Select(segment => segment.Id).ToHashSet());
    }

    private static bool MatchesAnyRequestedTag(Segment segment, IReadOnlyCollection<int> requestedTagIds)
    {
        if (segment.TagId.HasValue && requestedTagIds.Contains(segment.TagId.Value))
            return true;

        if (segment.Payload?.RootElement.ValueKind != JsonValueKind.Object)
            return false;

        if (!segment.Payload.RootElement.TryGetProperty("secondaryTagIds", out var secondaryTagIds) || secondaryTagIds.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var value in secondaryTagIds.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var secondaryTagId) && requestedTagIds.Contains(secondaryTagId))
                return true;
        }

        return false;
    }

    private static double GetDefaultMergeGap(string @operator, int profileId, int videoId, IReadOnlyList<Segment> segments)
    {
        _ = profileId;
        _ = videoId;
        _ = segments;
        return @operator == "intersection" ? 0 : 0;
    }

    private static double GetDefaultMinDuration(string @operator, int profileId, int videoId, IReadOnlyList<Segment> segments)
    {
        _ = @operator;
        _ = profileId;
        _ = videoId;
        _ = segments;
        return 0;
    }

    private void RegisterCacheKey(int videoId, int profileId, string cacheKey)
    {
        VideoCacheKeys.GetOrAdd(videoId, static _ => new ConcurrentDictionary<string, byte>())[cacheKey] = 0;
        ProfileCacheKeys.GetOrAdd(profileId, static _ => new ConcurrentDictionary<string, byte>())[cacheKey] = 0;
    }

    private void RegisterVideoCacheKey(int videoId, string cacheKey)
    {
        VideoCacheKeys.GetOrAdd(videoId, static _ => new ConcurrentDictionary<string, byte>())[cacheKey] = 0;
    }

    private async Task EnsureBuiltInProfilesAsync(CancellationToken ct)
    {
        await ProfileInitializationLock.WaitAsync(ct);
        try
        {
            await EnsureBuiltInProfilesLockedAsync(ct);
        }
        finally
        {
            ProfileInitializationLock.Release();
        }
    }

    private async Task EnsureBuiltInProfilesLockedAsync(CancellationToken ct)
    {
        var changed = false;

        var rawProfile = await db.SegmentDisplayProfiles.FirstOrDefaultAsync(
            profile => profile.UserId == null && profile.IsSystem && profile.Name == "Raw",
            ct);
        if (rawProfile is null)
        {
            db.SegmentDisplayProfiles.Add(new SegmentDisplayProfile
            {
                Name = "Raw",
                Description = "Built-in raw segment display profile",
                UserId = null,
                IsSystem = true,
                IsDefault = false,
                Version = 1,
            });
            changed = true;
        }

        var defaultProfile = await db.SegmentDisplayProfiles.FirstOrDefaultAsync(
            profile => profile.UserId == null && profile.IsSystem && profile.Name == "Default",
            ct);
        if (defaultProfile is null)
        {
            defaultProfile = new SegmentDisplayProfile
            {
                Name = "Default",
                Description = "Built-in default segment display profile",
                UserId = null,
                IsSystem = true,
                IsDefault = true,
                Version = 1,
            };
            db.SegmentDisplayProfiles.Add(defaultProfile);
            db.SegmentDisplayRules.Add(CreateBuiltInDefaultRule(defaultProfile));
            changed = true;
        }
        else if (await EnsureDefaultRuleExistsAsync(defaultProfile, ct))
        {
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync(ct);
    }

    private async Task<bool> EnsureDefaultRuleExistsAsync(SegmentDisplayProfile profile, CancellationToken ct)
    {
        var hasRules = profile.Id > 0 && await db.SegmentDisplayRules.AnyAsync(rule => rule.ProfileId == profile.Id, ct);
        if (hasRules)
            return false;

        db.SegmentDisplayRules.Add(CreateBuiltInDefaultRule(profile));
        profile.Version++;
        return true;
    }

    private static SegmentDisplayRule CreateBuiltInDefaultRule(SegmentDisplayProfile profile)
        => new()
        {
            Profile = profile,
            HostType = SegmentHostType.Video,
            Visible = true,
            MinDurationSec = BuiltInDefaultMinDurationSec,
            MergeGapSec = BuiltInDefaultMergeGapSec,
            Priority = 1,
            UserId = profile.UserId,
        };

    private sealed record SpanBucketKey(
        string SourceKey,
        string? Kind,
        int? TagId,
        string? GroupKey,
        string? ColorHint,
        int? Lane,
        bool CollapsedToInstant,
        double MergeGapSec,
        double MinDurationSec,
        string? Label);

    private sealed record OperandMatch(List<IntervalAlgebra.Interval> Intervals, HashSet<int> SegmentIds);
}

