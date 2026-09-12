using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

/// <summary>One ordered criterion used to choose which member of a duplicate group to keep.</summary>
public sealed record DuplicateKeeperRule(string Type, IReadOnlyList<string>? Values = null);

/// <summary>The attributes keeper rules compare for one video.</summary>
internal sealed record DuplicateKeeperFacts(
    int VideoId,
    long Pixels,
    long BitRate,
    double FrameRate,
    double Duration,
    long TotalSize,
    string Codec,
    string Path,
    int MetadataScore,
    int EngagementScore,
    bool Organized,
    DateTime CreatedAt);

internal sealed record DuplicateKeeperChoice(int KeeperId, string DecisionRule);

/// <summary>
/// Lexicographic keeper selection. Each rule narrows the candidates to those sharing its best value;
/// the rule that first narrows the group to a single video is recorded as the reason. When every rule
/// ties, the lowest video id wins so repeated evaluations stay deterministic.
/// </summary>
internal static class DuplicateKeeperRules
{
    public const string TieBreakRule = "tiebreak";
    private const int MaximumRules = 16;
    private const int MaximumRuleValues = 32;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly IReadOnlySet<string> KnownTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "resolution",
        "bitrate",
        "framerate",
        "duration",
        "size-largest",
        "size-smallest",
        "codec",
        "metadata",
        "engagement",
        "organized",
        "date-oldest",
        "date-newest",
        "path",
    };

    public static readonly IReadOnlyList<DuplicateKeeperRule> Defaults =
    [
        new("resolution"),
        new("duration"),
        new("bitrate"),
        new("metadata"),
        new("engagement"),
        new("date-oldest"),
    ];

    /// <summary>Drops unknown or repeated rules and bounds values so a stored rule list is always safe to evaluate.</summary>
    public static IReadOnlyList<DuplicateKeeperRule> Normalize(IEnumerable<DuplicateKeeperRule>? rules)
    {
        if (rules is null)
            return Defaults;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<DuplicateKeeperRule>();
        foreach (var rule in rules)
        {
            var type = rule?.Type?.Trim().ToLowerInvariant();
            if (type is null || !KnownTypes.Contains(type) || !seen.Add(type))
                continue;
            IReadOnlyList<string>? values = null;
            if (type is "codec" or "path")
            {
                values = (rule!.Values ?? [])
                    .Select(value => value?.Trim() ?? string.Empty)
                    .Where(value => value.Length > 0)
                    .Select(value => value.Length <= DuplicateSearchMemoryBudget.MaximumFieldCharacters
                        ? value
                        : throw new InvalidOperationException($"Duplicate search metadata exceeds the {DuplicateSearchMemoryBudget.MaximumFieldCharacters:N0}-character field limit. Shorten the affected metadata and try again."))
                    .Select(value => type == "codec" ? NormalizeCodec(value) : value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(MaximumRuleValues)
                    .ToArray();
                // A preference rule without preferences cannot separate anything.
                if (values.Count == 0)
                    continue;
            }
            normalized.Add(new DuplicateKeeperRule(type, values));
            if (normalized.Count == MaximumRules)
                break;
        }
        return normalized;
    }

    public static string Serialize(IReadOnlyList<DuplicateKeeperRule> rules)
        => JsonSerializer.Serialize(rules, JsonOptions);

    public static IReadOnlyList<DuplicateKeeperRule> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Defaults;
        try
        {
            return Normalize(JsonSerializer.Deserialize<List<DuplicateKeeperRule>>(json, JsonOptions));
        }
        catch (JsonException)
        {
            return Defaults;
        }
    }

    public static DuplicateKeeperChoice Choose(
        IReadOnlyCollection<int> videoIds,
        IReadOnlyDictionary<int, DuplicateKeeperFacts> facts,
        IReadOnlyList<DuplicateKeeperRule> rules)
    {
        if (videoIds.Count == 0)
            throw new ArgumentException("A duplicate group needs at least one video.", nameof(videoIds));

        // Videos without facts (deleted mid-search) can only win the final tie-break.
        var candidates = videoIds.Distinct().Where(facts.ContainsKey).Order().ToList();
        if (candidates.Count == 0)
            return new DuplicateKeeperChoice(videoIds.Min(), TieBreakRule);
        if (candidates.Count == 1)
            return new DuplicateKeeperChoice(candidates[0], TieBreakRule);

        foreach (var rule in rules)
        {
            var scored = candidates.Select(id => (Id: id, Score: Score(rule, facts[id]))).ToArray();
            var best = scored.Max(item => item.Score);
            var narrowed = scored.Where(item => item.Score == best).Select(item => item.Id).ToList();
            if (narrowed.Count == candidates.Count)
                continue;
            candidates = narrowed;
            if (candidates.Count == 1)
                return new DuplicateKeeperChoice(candidates[0], rule.Type);
        }

        return new DuplicateKeeperChoice(candidates.Min(), TieBreakRule);
    }

    /// <summary>A comparable score where a larger value is always preferred.</summary>
    private static double Score(DuplicateKeeperRule rule, DuplicateKeeperFacts facts) => rule.Type switch
    {
        "resolution" => facts.Pixels,
        "bitrate" => facts.BitRate,
        // Container frame rates are reported with float noise (29.97 vs 29.970029).
        "framerate" => Math.Round(facts.FrameRate, 2),
        // Probed durations of identical content differ by container overhead; whole seconds avoid noise.
        "duration" => Math.Round(facts.Duration),
        "size-largest" => facts.TotalSize,
        "size-smallest" => -facts.TotalSize,
        "codec" => -PreferenceIndex(rule.Values!, value => string.Equals(value, facts.Codec, StringComparison.OrdinalIgnoreCase)),
        "path" => -PreferenceIndex(rule.Values!, value => facts.Path.Contains(value.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)),
        "metadata" => facts.MetadataScore,
        "engagement" => facts.EngagementScore,
        "organized" => facts.Organized ? 1 : 0,
        "date-oldest" => -facts.CreatedAt.Ticks,
        "date-newest" => facts.CreatedAt.Ticks,
        _ => 0,
    };

    private static int PreferenceIndex(IReadOnlyList<string> values, Func<string, bool> matches)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (matches(values[index]))
                return index;
        }
        return values.Count;
    }

    public static string NormalizeCodec(string? value)
    {
        var codec = (value ?? string.Empty).Trim().ToLowerInvariant().Replace(".", "").Replace("_", "").Replace("-", "");
        return codec switch
        {
            "h265" or "hvc1" or "hev1" or "x265" => "hevc",
            "avc" or "avc1" or "x264" => "h264",
            "vp09" or "vp90" => "vp9",
            "av01" => "av1",
            _ => codec,
        };
    }

    public static async Task<Dictionary<int, DuplicateKeeperFacts>> LoadFactsAsync(
        CoveContext db,
        IReadOnlyCollection<int> videoIds,
        IReadOnlyList<DuplicateKeeperRule> rules,
        CancellationToken ct)
        => await LoadFactsAsync(db, videoIds, rules, new DuplicateSearchMemoryBudget(), ct);

    internal static async Task<Dictionary<int, DuplicateKeeperFacts>> LoadFactsAsync(
        CoveContext db,
        IReadOnlyCollection<int> videoIds,
        IReadOnlyList<DuplicateKeeperRule> rules,
        DuplicateSearchMemoryBudget memoryBudget,
        CancellationToken ct)
    {
        var result = new Dictionary<int, DuplicateKeeperFacts>();
        var needsMetadata = rules.Any(rule => rule.Type == "metadata");
        var needsEngagement = rules.Any(rule => rule.Type == "engagement");
        foreach (var chunk in videoIds.Distinct().Chunk(2_000))
        {
            var rows = await db.Videos
                .AsNoTracking()
                .Where(video => chunk.Contains(video.Id))
                .Select(video => new
                {
                    video.Id,
                    video.Organized,
                    video.CreatedAt,
                    video.MaxBitRate,
                    video.MaxFrameRate,
                    video.MaxDuration,
                    video.PrimaryFileId,
                })
                .ToListAsync(ct);
            var filesByVideoId = rows.ToDictionary(row => row.Id, row => new KeeperFileAccumulator(row.PrimaryFileId));
            var fileQuery = db.VideoFiles
                .AsNoTracking()
                .Where(file => file.VideoId.HasValue && chunk.Contains(file.VideoId.Value))
                .OrderBy(file => file.Id)
                .Select(file => new
                {
                    file.Id,
                    VideoId = file.VideoId!.Value,
                    file.Width,
                    file.Height,
                    VideoCodec = file.VideoCodec != null && file.VideoCodec.Length > DuplicateSearchMemoryBudget.MaximumFieldCharacters
                        ? file.VideoCodec.Substring(0, DuplicateSearchMemoryBudget.MaximumFieldCharacters + 1)
                        : file.VideoCodec,
                    Path = file.Path.Length > DuplicateSearchMemoryBudget.MaximumFieldCharacters
                        ? file.Path.Substring(0, DuplicateSearchMemoryBudget.MaximumFieldCharacters + 1)
                        : file.Path,
                    file.Size,
                })
                .AsAsyncEnumerable();
            await foreach (var file in fileQuery.WithCancellation(ct))
                filesByVideoId[file.VideoId].Add(file.Id, file.Width, file.Height, file.VideoCodec, file.Path, file.Size);

            var metadata = needsMetadata
                ? await db.Videos
                    .AsNoTracking()
                    .Where(video => chunk.Contains(video.Id))
                    .Select(video => new
                    {
                        video.Id,
                        Score = (video.Title != null && video.Title != "" ? 1 : 0)
                            + (video.Details != null && video.Details != "" ? 1 : 0)
                            + (video.Date != null ? 1 : 0)
                            + (video.StudioId != null ? 1 : 0)
                            + (video.Code != null && video.Code != "" ? 1 : 0)
                            + (video.Director != null && video.Director != "" ? 1 : 0)
                            + (video.ImageBlobId != null ? 1 : 0)
                            + video.VideoTags.Count()
                            + video.VideoPerformers.Count()
                            + video.VideoGalleries.Count()
                            + video.Urls.Count()
                            + video.RemoteIds.Count()
                            + video.GroupItems.Count(),
                    })
                    .ToDictionaryAsync(row => row.Id, row => row.Score, ct)
                : [];

            var engagement = new Dictionary<int, int>();
            if (needsEngagement)
            {
                var affinities = await db.UserEntityAffinities
                    .AsNoTracking()
                    .Where(affinity => affinity.HostType == AffinityHostType.Video && chunk.Contains(affinity.HostId))
                    .GroupBy(affinity => affinity.HostId)
                    .Select(group => new
                    {
                        HostId = group.Key,
                        Score = group.Sum(affinity => affinity.ViewCount + affinity.LikeCount + (affinity.IsFavorite ? 1 : 0)),
                    })
                    .ToListAsync(ct);
                foreach (var row in affinities)
                    engagement[row.HostId] = row.Score;
                var ratings = await db.Ratings
                    .AsNoTracking()
                    .Where(rating => rating.HostType == RatingHostType.Video && chunk.Contains(rating.HostId))
                    .GroupBy(rating => rating.HostId)
                    .Select(group => new { HostId = group.Key, Count = group.Count() })
                    .ToListAsync(ct);
                foreach (var row in ratings)
                    engagement[row.HostId] = engagement.GetValueOrDefault(row.HostId) + row.Count;
            }

            foreach (var row in rows)
            {
                var files = filesByVideoId[row.Id];
                var codec = NormalizeCodec(files.Codec);
                var path = (files.Path ?? string.Empty).Replace('\\', '/');
                memoryBudget.ReserveKeeperFact(codec.Length + path.Length, Math.Max(codec.Length, path.Length));
                result[row.Id] = new DuplicateKeeperFacts(
                    row.Id,
                    files.MaximumPixels,
                    row.MaxBitRate,
                    row.MaxFrameRate,
                    row.MaxDuration,
                    files.TotalSize,
                    codec,
                    path,
                    metadata.GetValueOrDefault(row.Id),
                    engagement.GetValueOrDefault(row.Id),
                    row.Organized,
                    row.CreatedAt);
            }
        }
        return result;
    }

    private sealed class KeeperFileAccumulator(int? primaryFileId)
    {
        private bool hasSelection;
        private bool selectionIsPrimary;
        private long selectionPixels;

        public long MaximumPixels { get; private set; }
        public long TotalSize { get; private set; }
        public string? Codec { get; private set; }
        public string? Path { get; private set; }

        public void Add(int id, int width, int height, string? codec, string path, long size)
        {
            var pixels = (long)width * height;
            MaximumPixels = Math.Max(MaximumPixels, pixels);
            TotalSize += size;
            var isPrimary = id == primaryFileId;
            if (!hasSelection || (isPrimary && !selectionIsPrimary) || (isPrimary == selectionIsPrimary && pixels > selectionPixels))
            {
                hasSelection = true;
                selectionIsPrimary = isPrimary;
                selectionPixels = pixels;
                Codec = codec;
                Path = path;
            }
        }
    }
}
