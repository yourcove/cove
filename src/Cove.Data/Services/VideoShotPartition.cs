using Cove.Core.Common;
using Cove.Core.DTOs;

namespace Cove.Data.Services;

/// <summary>One validated shot of a partition.</summary>
internal sealed record PartitionShot(
    double StartSec,
    double EndSec,
    int? StartFrame,
    int? EndFrame,
    string? ShotType,
    string? TransitionIn);

/// <summary>A validated partition, or the reason it was rejected.</summary>
internal sealed record PartitionResult(IReadOnlyList<PartitionShot>? Shots, int? FrameCount, string? Error)
{
    public bool IsValid => Error is null;

    public static PartitionResult Invalid(string error) => new(null, null, error);
}

/// <summary>
/// Checks that a list of shots is a contiguous, gapless partition of <c>[0, duration]</c>, snapping
/// boundaries that are within <see cref="VideoShotRules.BoundaryToleranceSec"/> of where they belong.
/// Anything further off is rejected rather than repaired, so a caller's mistake never turns into a
/// silently different set.
/// </summary>
internal static class VideoShotPartition
{
    // Comparisons against a minimum length allow for the rounding of values that were themselves
    // snapped to millisecond boundaries.
    private const double Slack = 1e-6;

    public static PartitionResult Normalize(
        IReadOnlyList<VideoShotInput>? shots,
        double durationSec,
        double? fps,
        int? frameCount)
    {
        if (shots is null || shots.Count == 0)
            return PartitionResult.Invalid("A set needs at least one shot.");
        if (shots.Count > VideoShotRules.MaxShotsPerSet)
            return PartitionResult.Invalid($"A set may hold at most {VideoShotRules.MaxShotsPerSet} shots.");
        if (!double.IsFinite(durationSec) || durationSec <= 0)
            return PartitionResult.Invalid("The set's duration must be a positive number of seconds.");
        if (fps is { } rate && (!double.IsFinite(rate) || rate <= 0))
            return PartitionResult.Invalid("The frame rate must be a positive number.");
        if (frameCount is <= 0)
            return PartitionResult.Invalid("The frame count must be positive.");

        foreach (var shot in shots)
        {
            if (shot is null)
                return PartitionResult.Invalid("A shot is missing.");
            if (!double.IsFinite(shot.StartSec) || !double.IsFinite(shot.EndSec))
                return PartitionResult.Invalid("Shot times must be finite numbers.");
            if (shot.StartFrame.HasValue != shot.EndFrame.HasValue)
                return PartitionResult.Invalid("A shot must give both of its frames or neither.");
        }

        var ordered = shots.OrderBy(shot => shot.StartSec).ThenBy(shot => shot.EndSec).ToList();
        var withFrames = ordered[0].StartFrame.HasValue;
        if (ordered.Any(shot => shot.StartFrame.HasValue != withFrames))
            return PartitionResult.Invalid("Either every shot gives its frames or none does.");
        if (withFrames && fps is null)
            return PartitionResult.Invalid("Shots with frames need the set's frame rate.");

        var result = new List<PartitionShot>(ordered.Count);
        var previousEnd = 0.0;
        for (var index = 0; index < ordered.Count; index++)
        {
            var shot = ordered[index];
            var start = shot.StartSec;
            if (Math.Abs(start - previousEnd) > VideoShotRules.BoundaryToleranceSec)
            {
                return PartitionResult.Invalid(index == 0
                    ? $"The first shot starts at {start:0.###}s instead of 0."
                    : start > previousEnd
                        ? $"There is a gap before the shot starting at {start:0.###}s."
                        : $"The shot starting at {start:0.###}s overlaps the one before it.");
            }

            start = previousEnd;
            var end = shot.EndSec;
            if (index == ordered.Count - 1)
            {
                if (Math.Abs(end - durationSec) > VideoShotRules.BoundaryToleranceSec)
                    return PartitionResult.Invalid($"The last shot ends at {end:0.###}s instead of the set's duration, {durationSec:0.###}s.");
                end = durationSec;
            }

            if (end - start < VideoShotRules.MinShotDurationSec - Slack)
                return PartitionResult.Invalid($"The shot starting at {start:0.###}s is shorter than {VideoShotRules.MinShotDurationSec * 1000:0.#}ms.");

            if (!TryNormalizeLabel(shot.ShotType, out var shotType) || !TryNormalizeLabel(shot.TransitionIn, out var transitionIn))
                return PartitionResult.Invalid($"Shot labels may be at most {VideoShotRules.MaxLabelLength} characters.");

            result.Add(new PartitionShot(start, end, shot.StartFrame, shot.EndFrame, shotType, index == 0 ? null : transitionIn));
            previousEnd = end;
        }

        int? resultFrameCount = frameCount;
        if (withFrames)
        {
            var previousEndFrame = 0;
            foreach (var shot in result)
            {
                if (shot.StartFrame != previousEndFrame)
                    return PartitionResult.Invalid($"Shot frames are not contiguous at frame {shot.StartFrame}.");
                if (shot.EndFrame <= shot.StartFrame)
                    return PartitionResult.Invalid($"The shot starting at frame {shot.StartFrame} has no frames.");
                previousEndFrame = shot.EndFrame!.Value;
            }

            if (frameCount is { } count && count != previousEndFrame)
                return PartitionResult.Invalid($"The shots end at frame {previousEndFrame}, not at the frame count {count}.");
            resultFrameCount = previousEndFrame;
        }

        return new PartitionResult(result, resultFrameCount, null);
    }

    public static bool TryNormalizeLabel(string? value, out string? normalized)
    {
        normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return normalized is null || normalized.Length <= VideoShotRules.MaxLabelLength;
    }

    /// <summary>
    /// Whether an analysed duration plausibly belongs to a file of the given probed duration: it may
    /// run only slightly past the file's end, but may stop well short of it, because the probed duration
    /// is the container's and the video stream can end first. A file without a probed duration is not
    /// checked.
    /// </summary>
    public static bool DurationMatchesFile(double durationSec, double fileDurationSec)
    {
        if (!double.IsFinite(fileDurationSec) || fileDurationSec <= 0)
            return true;
        var overrun = Math.Max(VideoShotRules.DurationSanityMinSec, fileDurationSec * VideoShotRules.DurationSanityFraction);
        return durationSec <= fileDurationSec + overrun
            && durationSec >= fileDurationSec * VideoShotRules.DurationSanityShortestFraction;
    }

    /// <summary>How an analysed duration that <see cref="DurationMatchesFile"/> refused compares with the file's.</summary>
    public static string DescribeDurationMismatch(double durationSec, double fileDurationSec)
        => durationSec > fileDurationSec
            ? $"{durationSec:0.###}s, longer than the file's {fileDurationSec:0.###}s"
            : $"{durationSec:0.###}s, less than half of the file's {fileDurationSec:0.###}s";
}
