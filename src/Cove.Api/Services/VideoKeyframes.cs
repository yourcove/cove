using System.Diagnostics;
using System.Globalization;

namespace Cove.Api.Services;

/// <summary>
/// Finds where a lossless cut can start. Without re-encoding, a kept part must begin on a keyframe, so a
/// requested start is moved back to the nearest keyframe at or before it (see
/// <see cref="VideoCut.SnapStartsToKeyframes"/>).
///
/// Only a short window before each cut is read, widening until a keyframe turns up, rather than listing
/// every keyframe in the file: that would read an 8K master end to end just to place a few cuts.
/// </summary>
public static class VideoKeyframes
{
    /// <summary>How far back to look, widening each time nothing is found. Keyframes are rarely more than 10 s apart.</summary>
    private static readonly double[] Windows = [10, 60, 300];

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>ffprobe arguments listing the packets of one stream between two raw file timestamps.</summary>
    public static string ProbeArguments(string path, int streamIndex, double fromFileTime, double toFileTime)
        => string.Create(CultureInfo.InvariantCulture,
            $"-v error -select_streams {streamIndex} -read_intervals {Math.Max(0, fromFileTime):0.######}%{toFileTime:0.######} ")
         + "-show_entries packet=pts_time,flags -of csv=p=0 \"" + path.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// The latest keyframe at or before <paramref name="fileTime"/> in ffprobe's <c>pts_time,flags</c>
    /// CSV, or null when the listing holds none. A hair of tolerance absorbs timestamp rounding, so a cut
    /// placed exactly on a keyframe stays on it.
    /// </summary>
    public static double? LatestAtOrBefore(string ffprobeCsv, double fileTime)
    {
        double? best = null;
        foreach (var line in ffprobeCsv.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(',');
            if (fields.Length < 2 || !fields[1].Contains('K', StringComparison.Ordinal))
                continue;
            if (!double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var pts))
                continue;
            if (pts <= fileTime + 0.0005 && (best is null || pts > best))
                best = pts;
        }
        return best;
    }

    /// <summary>
    /// The keyframe at or before <paramref name="timelineTime"/>, on the timeline (the file's start time
    /// removed). Returns 0 when nothing earlier exists, which is where every file's decoding starts.
    /// </summary>
    public static async Task<double> FindAtOrBeforeAsync(
        string ffprobe, string path, int streamIndex, double timelineTime, double startTime, CancellationToken ct)
    {
        if (timelineTime <= 0)
            return 0;

        var fileTime = timelineTime + startTime;
        foreach (var window in Windows.Append(fileTime))
        {
            var csv = await RunAsync(ffprobe, ProbeArguments(path, streamIndex, fileTime - window, fileTime + 0.001), ct);
            if (LatestAtOrBefore(csv, fileTime) is { } keyframe)
                return Math.Max(0, keyframe - startTime);
            if (window >= fileTime)
                break;
        }
        return 0;
    }

    private static async Task<string> RunAsync(string ffprobe, string arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(ffprobe, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        FfmpegProcessEnvironment.Apply(startInfo, ffprobe);
        using var process = Process.Start(startInfo)
            ?? throw new VideoConversionException("ffprobe could not be started to find where the cut can begin.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
                throw new VideoConversionException($"ffprobe could not read the file's keyframes: {(await stderr).Trim()}");
            return await stdout;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new VideoConversionException("Finding the file's keyframes took too long, so the cut was not made.");
        }
    }
}
