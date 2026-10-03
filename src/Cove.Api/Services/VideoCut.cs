namespace Cove.Api.Services;

/// <summary>A span of a video's timeline, in seconds.</summary>
public sealed record TimeRange(double Start, double End)
{
    public double Length => End - Start;
}

/// <summary>
/// The arithmetic of cutting parts out of a video's timeline, kept free of I/O so every rule can be
/// tested on its own.
///
/// A cut is described by what it keeps: an ordered list of non-overlapping source ranges, played back
/// to back in the output. Everything that knows a time on the old timeline - markers, segments, clips,
/// timed group items, quality samples - is translated through <see cref="MapTime"/> and
/// <see cref="MapRange"/>.
/// </summary>
public static class VideoCut
{
    /// <summary>
    /// Kept pieces shorter than this are dropped rather than written: a sliver left between two removals
    /// is almost always an accident of dragging, and a fraction of a second of video is not worth a join.
    /// </summary>
    public const double MinimumKeptSeconds = 0.25;

    /// <summary>
    /// What remains of a <paramref name="duration"/>-second timeline once <paramref name="remove"/> is taken
    /// out. Removals may overlap, touch, run past either end or come in any order; they are clamped and
    /// merged first. Returns null with a reason when the request cannot be honoured.
    /// </summary>
    public static (IReadOnlyList<TimeRange>? Kept, string? Error) KeepAfterRemoving(IEnumerable<TimeRange> remove, double duration)
    {
        if (!double.IsFinite(duration) || duration <= 0)
            return (null, "The video's length is unknown, so it cannot be cut.");

        var removals = new List<TimeRange>();
        foreach (var range in remove)
        {
            if (!double.IsFinite(range.Start) || !double.IsFinite(range.End))
                return (null, "Cut times must be finite numbers of seconds.");
            if (range.End <= range.Start)
                return (null, $"A cut must end after it starts ({range.Start:0.###}s to {range.End:0.###}s does not).");
            var start = Math.Max(0, range.Start);
            var end = Math.Min(duration, range.End);
            if (end > start)
                removals.Add(new TimeRange(start, end));
        }
        if (removals.Count == 0)
            return (null, "Nothing to cut: no part of the requested ranges falls within the video.");

        var merged = Merge(removals);
        var kept = new List<TimeRange>();
        var cursor = 0d;
        foreach (var removal in merged)
        {
            if (removal.Start - cursor >= MinimumKeptSeconds)
                kept.Add(new TimeRange(cursor, removal.Start));
            cursor = removal.End;
        }
        if (duration - cursor >= MinimumKeptSeconds)
            kept.Add(new TimeRange(cursor, duration));

        return kept.Count == 0
            ? (null, "That would cut the entire video. Delete the video instead if none of it should be kept.")
            : (kept, null);
    }

    /// <summary>
    /// The kept ranges a lossless cut can actually produce. Without re-encoding, a kept part must start
    /// on a keyframe - there is nothing before it to decode from - so each start moves back to the
    /// nearest keyframe at or before it. Moving back only ever keeps a little more than asked, never cuts
    /// something that was meant to stay. Ranges that grow into each other are joined.
    /// </summary>
    public static IReadOnlyList<TimeRange> SnapStartsToKeyframes(IReadOnlyList<TimeRange> kept, Func<double, double> keyframeAtOrBefore)
    {
        var snapped = kept
            .Select(range => new TimeRange(Math.Min(range.Start, Math.Max(0, keyframeAtOrBefore(range.Start))), range.End))
            .ToList();
        return Merge(snapped);
    }

    /// <summary>Total length of the output.</summary>
    public static double OutputDuration(IReadOnlyList<TimeRange> kept) => kept.Sum(range => range.Length);

    /// <summary>
    /// Where a source time lands on the output timeline. A time inside a removed part lands on the cut
    /// itself: on the join where the next kept part begins when <paramref name="towardsLater"/>, or where
    /// the previous kept part ends otherwise. Null when there is nothing on that side to land on.
    /// </summary>
    public static double? MapTime(double sourceTime, IReadOnlyList<TimeRange> kept, bool towardsLater)
    {
        var offset = 0d;
        TimeRange? previous = null;
        var previousOffset = 0d;
        foreach (var range in kept)
        {
            if (sourceTime < range.Start)
            {
                if (towardsLater)
                    return offset;
                return previous is null ? null : previousOffset + previous.Length;
            }
            if (sourceTime <= range.End)
                return offset + (sourceTime - range.Start);
            previous = range;
            previousOffset = offset;
            offset += range.Length;
        }
        return towardsLater ? null : offset;
    }

    /// <summary>
    /// Where a timed item lands. An item entirely inside a removed part is gone (null). One that spans a
    /// cut stays, joined across it: its kept parts are now contiguous, so it covers them as a single span.
    /// An instant (<paramref name="end"/> equal to <paramref name="start"/>) survives only if it is kept.
    /// </summary>
    public static TimeRange? MapRange(double start, double end, IReadOnlyList<TimeRange> kept)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || end < start)
            return null;

        if (end == start)
        {
            var point = kept.Any(range => start >= range.Start && start <= range.End) ? MapTime(start, kept, towardsLater: true) : null;
            return point is { } at ? new TimeRange(at, at) : null;
        }

        // Nothing of the item survives unless some kept range overlaps it.
        if (!kept.Any(range => range.Start < end && range.End > start))
            return null;

        var mappedStart = MapTime(start, kept, towardsLater: true);
        var mappedEnd = MapTime(end, kept, towardsLater: false);
        return mappedStart is { } s && mappedEnd is { } e && e >= s ? new TimeRange(s, e) : null;
    }

    /// <summary>The source time an output time came from - the inverse of <see cref="MapTime"/> on kept content.</summary>
    public static double SourceTime(double outputTime, IReadOnlyList<TimeRange> kept)
    {
        var offset = 0d;
        foreach (var range in kept)
        {
            if (outputTime <= offset + range.Length)
                return range.Start + Math.Max(0, outputTime - offset);
            offset += range.Length;
        }
        return kept.Count == 0 ? 0 : kept[^1].End;
    }

    /// <summary>
    /// Sample windows for the quality search, placed on the OUTPUT timeline so removed footage never
    /// informs the setting, then moved to where they come from in the source. A window that would cross a
    /// join is shortened to stay inside one kept part, since a sample is a single clip of the source.
    /// </summary>
    public static IReadOnlyList<(double Start, double Length)> SampleWindows(IReadOnlyList<TimeRange> kept)
    {
        var windows = new List<(double Start, double Length)>();
        foreach (var (outputStart, length) in VideoQualitySearch.SampleWindows(OutputDuration(kept)))
        {
            var sourceStart = SourceTime(outputStart, kept);
            var piece = kept.First(range => sourceStart >= range.Start && sourceStart <= range.End);
            var fit = Math.Min(length, piece.End - sourceStart);
            if (fit < length && piece.Length >= length)
            {
                // Not enough left before the join: slide back so the whole window fits in this part.
                sourceStart = piece.End - length;
                fit = length;
            }
            if (fit > 0.1)
                windows.Add((sourceStart, fit));
        }
        return windows;
    }

    private static List<TimeRange> Merge(IEnumerable<TimeRange> ranges)
    {
        var merged = new List<TimeRange>();
        foreach (var range in ranges.OrderBy(range => range.Start))
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, range.End) };
            else
                merged.Add(range);
        }
        return merged;
    }
}
