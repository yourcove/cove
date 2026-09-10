using System.Numerics;

namespace Cove.Core.Services;

public record AlignmentSample(double TimeSec, ulong Hash, double Contrast, string Thumbnail, string Descriptor = "", int Window = 0);
public record AlignmentAnchor(double SourceSec, double TargetSec, int Section);
public record AlignedRange(double StartSec, double EndSec);
public record AutomaticVideoAlignment(List<AlignmentAnchor> Anchors, bool InternalEditsDetected);

/// <summary>Conservative visual correspondence proposals. Gaps stay unmapped; proposals require review.</summary>
public static class VideoAlignment
{
    private record MatchCandidate(double SourceSec, double TargetSec, int SourceWindow, int TargetWindow);

    public const double SampleWindowSeconds = 15;
    public const int SampleWindowCount = 6;
    public const double ContinuousSamplingSeconds = 180;

    public static List<AlignedRange> PlanSampleWindows(double duration)
    {
        if (!double.IsFinite(duration) || duration <= 0) return [];
        if (duration <= ContinuousSamplingSeconds) return [new(0, duration)];
        var starts = Enumerable.Range(0, SampleWindowCount)
            .Select(index => index * (duration - SampleWindowSeconds) / (SampleWindowCount - 1d));
        return starts.Select(start => Math.Clamp(start, 0, duration - SampleWindowSeconds))
            .Distinct().Order().Select(start => new AlignedRange(start, start + SampleWindowSeconds)).ToList();
    }

    public static List<AlignmentAnchor> FindAnchors(IReadOnlyList<AlignmentSample> source,
        IReadOnlyList<AlignmentSample> target, double sampleStep, CancellationToken ct = default)
    {
        var candidates = new List<MatchCandidate>();
        var targetDescriptors = target.Select(s => string.IsNullOrEmpty(s.Descriptor) ? null : Convert.FromBase64String(s.Descriptor)).ToArray();
        var hasTargetDescriptors = targetDescriptors.All(d => d is not null);
        foreach (var sample in source.Where(s => s.Contrast >= 8))
        {
            ct.ThrowIfCancellationRequested();
            var descriptor = !hasTargetDescriptors || string.IsNullOrEmpty(sample.Descriptor) ? null : Convert.FromBase64String(sample.Descriptor);
            AlignmentSample? best = null;
            var bestDistance = double.PositiveInfinity;
            var distances = new double[target.Count];
            Array.Fill(distances, double.PositiveInfinity);
            for (var index = 0; index < target.Count; index++)
            {
                var other = target[index];
                if (other.Contrast < 8) continue;
                var hamming = BitOperations.PopCount(sample.Hash ^ other.Hash);
                double distance;
                var otherDescriptor = targetDescriptors[index];
                if (descriptor is not null && otherDescriptor is not null && descriptor.Length == otherDescriptor.Length)
                {
                    if (hamming > 20) continue;
                    double squared = 0;
                    for (var pixel = 0; pixel < descriptor.Length; pixel++)
                    { var difference = descriptor[pixel] - otherDescriptor[pixel]; squared += difference * difference; }
                    distance = Math.Sqrt(squared / descriptor.Length) / 255;
                }
                else distance = hamming / 64d;
                distances[index] = distance;
                if (distance < bestDistance) { best = other; bestDistance = distance; }
            }
            if (best is null || bestDistance > (descriptor is null ? 10 / 64d : .08)) continue;
            // Nearby frames can legitimately look identical. Distant repeated scenes cannot anchor a map.
            var ambiguityLimit = descriptor is null ? bestDistance + 4 / 64d : Math.Max(bestDistance * 1.5, bestDistance + .001);
            var ambiguous = target.Where((s, index) => Math.Abs(s.TimeSec - best.TimeSec) > Math.Max(2, sampleStep * 2)
                && distances[index] < ambiguityLimit).Any();
            if (!ambiguous) candidates.Add(new(sample.TimeSec, best.TimeSec, sample.Window, best.Window));
        }

        var result = new List<AlignmentAnchor>();
        var resultSourceWindows = new List<int>();
        var resultTargetWindows = new List<int>();
        var resultCoversOverlap = new List<bool>();
        var sourceCoverage = source.GroupBy(sample => sample.Window).ToDictionary(group => group.Key,
            group => new AlignedRange(group.Min(sample => sample.TimeSec), group.Max(sample => sample.TimeSec)));
        var targetCoverage = target.GroupBy(sample => sample.Window).ToDictionary(group => group.Key,
            group => new AlignedRange(group.Min(sample => sample.TimeSec), group.Max(sample => sample.TimeSec)));
        var run = new List<MatchCandidate>();
        var section = 0;
        void Flush()
        {
            if (run.Count >= 3 && run[^1].SourceSec - run[0].SourceSec >= Math.Max(4, sampleStep * 4))
            {
                var offset = ((run[0].TargetSec - run[0].SourceSec) + (run[^1].TargetSec - run[^1].SourceSec)) / 2;
                var sourceRange = sourceCoverage[run[0].SourceWindow];
                var targetRange = targetCoverage[run[0].TargetWindow];
                var overlapStart = Math.Max(sourceRange.StartSec, targetRange.StartSec - offset);
                var overlapEnd = Math.Min(sourceRange.EndSec, targetRange.EndSec - offset);
                var edgeTolerance = Math.Max(sampleStep * 1.01, (overlapEnd - overlapStart) * .4);
                var coversOverlap = overlapEnd >= overlapStart
                    && run[0].SourceSec <= overlapStart + edgeTolerance
                    && run[^1].SourceSec >= overlapEnd - edgeTolerance;
                foreach (var anchor in run)
                {
                    result.Add(new(anchor.SourceSec, anchor.TargetSec, section));
                    resultSourceWindows.Add(anchor.SourceWindow);
                    resultTargetWindows.Add(anchor.TargetWindow);
                    resultCoversOverlap.Add(coversOverlap);
                }
                section++;
            }
            run.Clear();
        }
        foreach (var candidate in candidates)
        {
            if (run.Count > 0)
            {
                var previous = run[^1];
                var ds = candidate.SourceSec - previous.SourceSec;
                var dt = candidate.TargetSec - previous.TargetSec;
                var crossedSampleWindow = candidate.SourceWindow != previous.SourceWindow
                    || candidate.TargetWindow != previous.TargetWindow;
                if (crossedSampleWindow) Flush();
                else
                {
                    if (dt == 0 && ds <= Math.Max(2, sampleStep * 2)) continue;
                    var offsetChange = Math.Abs((candidate.TargetSec - candidate.SourceSec)
                        - (previous.TargetSec - previous.SourceSec));
                    if (dt <= 0 || ds > Math.Max(1.01, sampleStep * 2.01)
                        || offsetChange > sampleStep * .75 + ds * .03) Flush();
                }
            }
            run.Add(candidate);
        }
        Flush();
        // Seeked windows can start on slightly different timestamp grids after a re-encode. Join only
        // adjacent windows whose independently matched runs cover all comparable sampled footage.
        if (result.Count > 0)
        {
            var mergedSection = 0;
            var previous = result[0];
            result[0] = previous with { Section = mergedSection };
            for (var index = 1; index < result.Count; index++)
            {
                var current = result[index];
                if (current.Section != previous.Section)
                {
                    var offsetChange = Math.Abs((current.TargetSec - current.SourceSec)
                        - (previous.TargetSec - previous.SourceSec));
                    var sameSparseTimeline = resultSourceWindows[index] == resultSourceWindows[index - 1] + 1
                        && resultTargetWindows[index] == resultTargetWindows[index - 1] + 1
                        && resultCoversOverlap[index - 1] && resultCoversOverlap[index]
                        && offsetChange <= Math.Max(.15, sampleStep * 1.01);
                    if (!sameSparseTimeline) mergedSection++;
                }
                result[index] = current with { Section = mergedSection };
                previous = current;
            }
        }
        return result;
    }

    public static AutomaticVideoAlignment FindAutomaticAlignment(IReadOnlyList<AlignmentSample> source,
        IReadOnlyList<AlignmentSample> target, double sourceDuration, double targetDuration, double sampleStep,
        CancellationToken ct = default)
    {
        var raw = FindAnchors(source, target, sampleStep, ct);
        if (raw.Count == 0) return new([], false);

        var evidence = raw.GroupBy(anchor => anchor.Section).Select(section =>
        {
            var anchors = section.OrderBy(anchor => anchor.SourceSec).ToList();
            var offsets = anchors.Select(anchor => anchor.TargetSec - anchor.SourceSec).Order().ToArray();
            return new
            {
                Anchors = anchors,
                Offset = offsets[offsets.Length / 2],
                Start = anchors[0].SourceSec,
                End = anchors[^1].SourceSec,
                Windows = anchors.Select(anchor => source.First(sample => sample.TimeSec == anchor.SourceSec).Window).Distinct().ToArray(),
            };
        }).OrderBy(section => section.Offset).ToList();

        var offsetTolerance = Math.Max(1, sampleStep * 2.1);
        var clusters = new List<List<int>>();
        foreach (var index in Enumerable.Range(0, evidence.Count))
        {
            var cluster = clusters.FirstOrDefault(candidate =>
            {
                var weightedOffset = candidate.Sum(item => evidence[item].Offset * evidence[item].Anchors.Count)
                    / candidate.Sum(item => evidence[item].Anchors.Count);
                return Math.Abs(evidence[index].Offset - weightedOffset) <= offsetTolerance;
            });
            if (cluster is null) clusters.Add([index]);
            else cluster.Add(index);
        }

        var dominant = clusters.MaxBy(cluster => cluster.Sum(index => evidence[index].Anchors.Count))!;
        var dominantCount = dominant.Sum(index => evidence[index].Anchors.Count);
        var sourceStart = source.Min(sample => sample.TimeSec);
        var sampledSpan = source.Max(sample => sample.TimeSec) + sampleStep - sourceStart;
        // A short false match near an edge is harmless. Any supported contradictory interior sample,
        // or a competing offset sustained over time, indicates an edit that one mapping cannot represent.
        var competing = clusters.Any(cluster =>
        {
            if (cluster == dominant || cluster.Sum(index => evidence[index].Anchors.Count) < 8) return false;
            var start = cluster.Min(index => evidence[index].Start);
            var end = cluster.Max(index => evidence[index].End);
            var interior = start > sourceStart + sampledSpan * .05 && end < sourceStart + sampledSpan * .95;
            return interior || end - start >= sampledSpan * .05;
        });
        if (competing) return new([], true);

        var dominantOffsets = dominant.SelectMany(index => evidence[index].Anchors)
            .Select(anchor => anchor.TargetSec - anchor.SourceSec).Order().ToArray();
        var offset = dominantOffsets[dominantOffsets.Length / 2];
        var timeTolerance = Math.Max(.75, sampleStep * 1.1);
        var sourceWindows = source.GroupBy(sample => sample.Window).OrderBy(window => window.Key).ToArray();
        foreach (var window in sourceWindows.Skip(1).Take(Math.Max(0, sourceWindows.Length - 2)))
        {
            var checks = window.Where(sample => sample.Contrast >= 8).Select(sample =>
            {
                var candidates = target.Where(candidate => Math.Abs(candidate.TimeSec - (sample.TimeSec + offset)) <= timeTolerance).ToArray();
                return candidates.Length == 0 ? (Sample: sample, Comparable: false, Matches: false)
                    : (Sample: sample, Comparable: true, Matches: candidates.Any(candidate => SamplesVisuallyMatch(sample, candidate)));
            }).Where(check => check.Comparable).OrderBy(check => check.Sample.TimeSec).ToArray();
            if (checks.Length < 8) return new([], false);
            if (checks.Count(check => check.Matches) < checks.Length * .6) return new([], true);
            double? unmatchedStart = null;
            foreach (var check in checks)
            {
                if (!check.Matches) unmatchedStart ??= check.Sample.TimeSec;
                else unmatchedStart = null;
                if (unmatchedStart.HasValue && check.Sample.TimeSec - unmatchedStart.Value + sampleStep >= Math.Max(4, sampleStep * 4))
                    return new([], true);
            }
        }

        var orderedDominant = dominant.Select(index => evidence[index]).OrderBy(section => section.Start).ToArray();
        for (var index = 1; index < orderedDominant.Length; index++)
        {
            var previous = orderedDominant[index - 1];
            var current = orderedDominant[index];
            var sharesWindow = previous.Windows.Intersect(current.Windows).Any();
            if (sharesWindow && (current.Start - previous.End > Math.Max(4, sampleStep * 4)
                || Math.Abs(current.Offset - previous.Offset) > Math.Max(.6, sampleStep * 1.1)))
                return new([], true);
        }

        var sourceEnd = sourceDuration;
        var targetStart = target.Min(sample => sample.TimeSec);
        var targetEnd = targetDuration;
        var evidenceStart = dominant.Min(index => evidence[index].Start);
        var evidenceEnd = dominant.Max(index => evidence[index].End);
        if (dominantCount < 12 || evidenceEnd - evidenceStart < (sourceEnd - sourceStart) * .55)
            return new([], false);

        var mappedStart = Math.Max(sourceStart, targetStart - offset);
        var mappedEnd = Math.Min(sourceEnd, targetEnd - offset);
        if (mappedEnd - mappedStart < Math.Max(4, sampleStep * 4)) return new([], false);
        return new([new(mappedStart, mappedStart + offset, 0), new(mappedEnd, mappedEnd + offset, 0)], false);
    }

    private static bool SamplesVisuallyMatch(AlignmentSample source, AlignmentSample target)
    {
        if (source.Contrast < 8 || target.Contrast < 8) return false;
        var hamming = BitOperations.PopCount(source.Hash ^ target.Hash);
        if (hamming > 28) return false;
        if (string.IsNullOrEmpty(source.Descriptor) || string.IsNullOrEmpty(target.Descriptor)) return hamming <= 10;
        var sourceDescriptor = Convert.FromBase64String(source.Descriptor);
        var targetDescriptor = Convert.FromBase64String(target.Descriptor);
        if (sourceDescriptor.Length != targetDescriptor.Length) return false;
        double squared = 0;
        for (var index = 0; index < sourceDescriptor.Length; index++)
        {
            var difference = sourceDescriptor[index] - targetDescriptor[index];
            squared += difference * difference;
        }
        return Math.Sqrt(squared / sourceDescriptor.Length) / 255 <= .15;
    }

    public static string? Validate(IReadOnlyList<AlignmentAnchor> anchors, double sourceDuration, double targetDuration)
    {
        if (anchors.Count is < 2 or > 8192) return "Provide between 2 and 8192 anchors.";
        if (!double.IsFinite(sourceDuration) || !double.IsFinite(targetDuration) || sourceDuration <= 0 || targetDuration <= 0)
            return "Both reference durations must be known.";
        foreach (var a in anchors)
            if (!double.IsFinite(a.SourceSec) || !double.IsFinite(a.TargetSec)
                || a.SourceSec < 0 || a.TargetSec < 0 || a.SourceSec > sourceDuration || a.TargetSec > targetDuration || a.Section < 0)
                return "Anchor times must be finite and within their reference files.";
        double previousEnd = -1, previousTargetEnd = -1;
        foreach (var section in anchors.GroupBy(a => a.Section).OrderBy(g => g.Min(a => a.SourceSec)))
        {
            var points = section.OrderBy(a => a.SourceSec).ToArray();
            if (points.Length < 2) return "Each matching section needs at least two anchors.";
            if (points[0].SourceSec <= previousEnd || points[0].TargetSec <= previousTargetEnd)
                return "Matching sections must be ordered and must not overlap.";
            for (var i = 1; i < points.Length; i++)
                if (points[i].SourceSec <= points[i - 1].SourceSec || points[i].TargetSec <= points[i - 1].TargetSec)
                    return "Anchor times must increase in both files.";
            previousEnd = points[^1].SourceSec;
            previousTargetEnd = points[^1].TargetSec;
        }
        return null;
    }

    public static AlignedRange? MapRange(double start, double end, IReadOnlyList<AlignmentAnchor> anchors)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end < start) return null;
        foreach (var group in anchors.GroupBy(a => a.Section))
        {
            var points = group.OrderBy(a => a.SourceSec).ToArray();
            if (points.Length < 2 || start < points[0].SourceSec || end > points[^1].SourceSec) continue;
            double Map(double time)
            {
                for (var i = 1; i < points.Length; i++)
                    if (time <= points[i].SourceSec)
                    {
                        var a = points[i - 1]; var b = points[i];
                        return a.TargetSec + (time - a.SourceSec) / (b.SourceSec - a.SourceSec) * (b.TargetSec - a.TargetSec);
                    }
                return points[^1].TargetSec;
            }
            return new(Map(start), Map(end));
        }
        return null;
    }
}
