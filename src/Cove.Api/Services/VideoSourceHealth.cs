using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Cove.Api.Services;

/// <summary>Verdict from a metadata-only look at a video file, before any decoding is attempted.</summary>
internal readonly record struct VideoSourceVerdict(bool IsReadable, string? Reason)
{
    public static VideoSourceVerdict Readable { get; } = new(true, null);
    public static VideoSourceVerdict Unreadable(string reason) => new(false, reason);
}

/// <summary>
/// Cheap up-front check for sources that cannot produce generated assets, so that generation fails
/// fast with an accurate reason instead of spending tens of seconds per asset rediscovering it.
///
/// The signal is the container's average bitrate against the video stream's own declared bitrate.
/// An interrupted download leaves a container that still advertises the full duration while holding
/// only a fraction of the bytes, so the average collapses far below what the stream declares. On a
/// complete file the two agree closely - the average runs slightly *above* the video stream's rate
/// because it also counts audio and container overhead.
///
/// Measured on this library: 400 complete videos ranged 1.002-1.251, while 114 known-broken ones had
/// a median of 0.021 (most held 1-2% of their expected bytes). The threshold sits at half the lowest
/// observed complete file, so a complete video is never flagged by a wide margin; incomplete files
/// that fall above it are still caught later by extraction itself, just more slowly.
/// </summary>
internal static class VideoSourceHealth
{
    /// <summary>
    /// Below this ratio of (container average bitrate / video stream bitrate) a file is treated as
    /// truncated. Deliberately far under the lowest complete file observed (1.002) - a false positive
    /// silently denies a good video its assets, which is far worse than a slow failure on a bad one.
    /// </summary>
    internal const double TruncationRatioThreshold = 0.5;

    /// <summary>
    /// Classifies a probe result. Anything the probe cannot answer confidently - no stream bitrate,
    /// no duration, a probe that failed outright - is reported readable so generation proceeds
    /// normally. This check only ever *skips* work it is confident is doomed.
    /// </summary>
    internal static VideoSourceVerdict Classify(long fileSize, double declaredDuration, long streamBitRate)
    {
        if (fileSize <= 0 || declaredDuration <= 0 || streamBitRate <= 0)
            return VideoSourceVerdict.Readable;

        var averageBitRate = fileSize * 8d / declaredDuration;
        var ratio = averageBitRate / streamBitRate;
        if (ratio >= TruncationRatioThreshold)
            return VideoSourceVerdict.Readable;

        var expectedSize = streamBitRate * declaredDuration / 8d;
        return VideoSourceVerdict.Unreadable(
            $"Source file looks incomplete: {Describe(fileSize)} on disk but its {FormatDuration(declaredDuration)} " +
            $"at {streamBitRate / 1000:N0} kbps implies about {Describe((long)expectedSize)}. " +
            "Re-download or re-import the file.");
    }

    private static string Describe(long bytes)
        => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):N1} GB"
         : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):N0} MB"
         : $"{bytes / 1024d:N0} KB";

    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h{span.Minutes:D2}m"
            : $"{span.Minutes}m{span.Seconds:D2}s";
    }
}

public interface IVideoSourceHealthProbe
{
    /// <summary>
    /// Returns a human-readable reason when the source cannot produce assets, or null when
    /// generation should proceed. Never throws: an inconclusive probe means "proceed".
    /// </summary>
    Task<string?> GetUnreadableReasonAsync(string path, CancellationToken ct = default);
}

public sealed class VideoSourceHealthProbe(
    IMediaProbeService mediaProbe,
    ILogger<VideoSourceHealthProbe> logger) : IVideoSourceHealthProbe
{
    public async Task<string?> GetUnreadableReasonAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;

            var probe = await mediaProbe.ProbeAsync(path, ct);
            if (probe.Status != MediaProbeStatus.Success || probe.Json is not { } json)
                return null;

            if (!TryReadProbe(json, out var duration, out var streamBitRate))
                return null;

            var verdict = VideoSourceHealth.Classify(info.Length, duration, streamBitRate);
            if (verdict.IsReadable)
                return null;

            logger.LogInformation("Source health: {Path} looks incomplete - {Reason}", path, verdict.Reason);
            return verdict.Reason;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An inconclusive probe must never block generation.
            logger.LogDebug(ex, "Source health probe failed for {Path}; proceeding with generation", path);
            return null;
        }
    }

    /// <summary>
    /// MPEG-1 video carries bit_rate in its sequence header as 18 bits in units of 400 bit/s, and
    /// all ones means the stream is variable-rate (ISO/IEC 11172-2). ffprobe reports that sentinel
    /// verbatim, so an ordinary 900 kbps .mpg declares 262143 * 400 bit/s and looks truncated by a
    /// factor of a hundred. It is a declaration of "unknown", not a rate.
    /// </summary>
    private const long Mpeg1VariableBitRateSentinel = 262143L * 400;

    /// <summary>
    /// Reads the container duration and the FIRST video stream's own declared bitrate out of
    /// ffprobe's -show_format/-show_streams JSON. The stream bitrate is the part that matters and
    /// the part Cove does not persist - <see cref="Cove.Core.Entities.VideoFile.BitRate"/> holds
    /// the container average, which by definition already encodes the truncation and so cannot
    /// detect it.
    /// </summary>
    internal static bool TryReadProbe(string json, out double duration, out long videoStreamBitRate)
    {
        duration = 0;
        videoStreamBitRate = 0;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("format", out var format)
            && format.TryGetProperty("duration", out var durationValue)
            && double.TryParse(durationValue.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDuration))
        {
            duration = parsedDuration;
        }

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_type", out var codecType) || codecType.GetString() != "video")
                    continue;
                if (stream.TryGetProperty("bit_rate", out var bitRate)
                    && long.TryParse(bitRate.GetString(), out var parsedBitRate)
                    && parsedBitRate != Mpeg1VariableBitRateSentinel)
                    videoStreamBitRate = parsedBitRate;
                break;
            }
        }

        return duration > 0 && videoStreamBitRate > 0;
    }
}
