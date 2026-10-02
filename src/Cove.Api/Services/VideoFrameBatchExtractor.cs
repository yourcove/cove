using System.Globalization;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Cove.Api.Services;

/// <summary>
/// Extracts frames at arbitrary timestamps by batching seeks into a small number of ffmpeg
/// invocations: one process per batch of N timestamps instead of one process per frame.
///
/// Each timestamp becomes its own <c>-ss T -i FILE</c> input pair, and each input is mapped to
/// its own single-frame output. FFmpeg then decodes the batch's inputs concurrently, which is
/// where the speedup comes from - a 4K 42-minute video drops from ~144s to ~23s for 81 frames.
///
/// The per-frame arguments are byte-identical to the one-process-per-frame path this replaces
/// (same <c>scale</c>, <c>-q:v</c> and <c>-pix_fmt</c>), so extracted frames - and therefore any
/// perceptual hash built from them - are unchanged. That was verified frame-by-frame before this
/// path was adopted; changing those arguments would silently invalidate every stored pHash.
///
/// Unlike the per-frame path, a frame that cannot be decoded yields a null slot rather than
/// failing the whole request, so callers can decide their own tolerance (a sprite can survive a
/// few unreadable frames; a pHash cannot).
/// </summary>
internal static class VideoFrameBatchExtractor
{
    /// <summary>Timestamps per ffmpeg invocation. Larger batches amortize process startup and let
    /// ffmpeg overlap more decodes, but every input repeats the source path on the command line.</summary>
    internal const int DefaultBatchSize = 24;

    /// <summary>Windows caps a command line at 32767 characters. Stay well under it: if a batch's
    /// arguments would exceed this, the batch is split. Long library paths are the usual cause.</summary>
    private const int MaxCommandLineLength = 24000;

    private static readonly TimeSpan BaseBatchTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PerFrameTimeout = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Extracts one frame per timestamp. The returned array always has one slot per requested
    /// timestamp; a slot is null when that frame could not be decoded. Returns null only when the
    /// extraction could not be attempted at all (no usable timestamps).
    /// </summary>
    /// <param name="scaleWidth">Target width; height follows the aspect ratio. Values &lt;= 0 keep the native size.</param>
    /// <param name="preFilter">An ffmpeg video filter applied before the scale, such as a VR reprojection. Null for none.</param>
    public static async Task<Image<Rgba32>?[]?> ExtractAsync(
        string ffmpegPath,
        string videoPath,
        IReadOnlyList<double> timestamps,
        int scaleWidth,
        FfmpegConcurrencyLimiter limiter,
        ILogger logger,
        CancellationToken ct,
        int batchSize = DefaultBatchSize,
        string? preFilter = null)
    {
        if (timestamps.Count == 0)
            return null;

        // A batch may not be wider than the whole decode budget, or it could never be admitted.
        batchSize = limiter.ClampBatchSize(batchSize);

        var tmpDir = Path.Combine(Path.GetTempPath(), $"cove_frames_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        var frames = new Image<Rgba32>?[timestamps.Count];
        var decoded = 0;

        try
        {
            foreach (var batch in PlanBatches(videoPath, tmpDir, timestamps.Count, scaleWidth, batchSize, preFilter))
            {
                ct.ThrowIfCancellationRequested();

                var args = BuildBatchArguments(videoPath, tmpDir, timestamps, batch.Start, batch.Count, scaleWidth, preFilter);
                var timeout = BaseBatchTimeout + PerFrameTimeout * batch.Count;

                // Held only for the decode itself. Loading the extracted frames afterwards is
                // ImageSharp work on already-written files and must not keep decode slots reserved.
                FfmpegProcessResult result;
                await using (await limiter.AcquireAsync(batch.Count, ct))
                    result = await FfmpegProcessRunner.RunAsync(ffmpegPath, args, timeout, ct);

                if (result.TimedOut)
                {
                    logger.LogDebug(
                        "Batched frame extraction timed out after {Timeout:F0}s on frames {Start}-{End} of {Path}",
                        timeout.TotalSeconds, batch.Start, batch.Start + batch.Count - 1, videoPath);
                }
                else if (result.ExitCode != 0)
                {
                    logger.LogDebug(
                        "Batched frame extraction reported exit {ExitCode} on frames {Start}-{End} of {Path}: {Error}",
                        result.ExitCode, batch.Start, batch.Start + batch.Count - 1, videoPath, Summarize(result.StandardError));
                }

                // Load whatever the batch did produce. A non-zero exit or a timeout still commonly
                // leaves most frames on disk - only the undecodable ones are missing - so the
                // per-frame outcome is read from the filesystem rather than inferred from the exit code.
                for (var offset = 0; offset < batch.Count; offset++)
                {
                    var index = batch.Start + offset;
                    var framePath = FramePath(tmpDir, index);
                    if (!File.Exists(framePath) || new FileInfo(framePath).Length == 0)
                        continue;

                    try
                    {
                        frames[index] = await Image.LoadAsync<Rgba32>(framePath, ct);
                        decoded++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogDebug(ex, "Could not load extracted frame {Index} of {Path}", index, videoPath);
                    }
                }
            }

            logger.LogTrace(
                "Batched frame extraction for {Path}: {Decoded}/{Requested} frames at scaleWidth={ScaleWidth}",
                videoPath, decoded, timestamps.Count, scaleWidth);

            return frames;
        }
        catch (OperationCanceledException)
        {
            DisposeFrames(frames);
            throw;
        }
        catch (Exception ex)
        {
            DisposeFrames(frames);
            logger.LogDebug(ex, "Batched frame extraction failed for {Path}", videoPath);
            return null;
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort */ }
        }
    }

    internal readonly record struct BatchPlan(int Start, int Count);

    /// <summary>Splits the timestamps into batches that each fit inside the command-line limit.</summary>
    internal static IEnumerable<BatchPlan> PlanBatches(
        string videoPath, string tmpDir, int timestampCount, int scaleWidth, int batchSize, string? preFilter = null)
    {
        var requested = Math.Max(1, batchSize);

        // Worst-case characters one input/output pair contributes, measured against the longest
        // frame index so the estimate never under-counts.
        var perFrame = PerFrameArgumentLength(videoPath, tmpDir, timestampCount, scaleWidth) + (preFilter?.Length + 1 ?? 0);
        var affordable = Math.Max(1, MaxCommandLineLength / Math.Max(1, perFrame));
        var effective = Math.Min(requested, affordable);

        for (var start = 0; start < timestampCount; start += effective)
            yield return new BatchPlan(start, Math.Min(effective, timestampCount - start));
    }

    private static int PerFrameArgumentLength(string videoPath, string tmpDir, int timestampCount, int scaleWidth)
    {
        var lastFramePath = FramePath(tmpDir, Math.Max(0, timestampCount - 1));
        // -ss <12 chars> -i "path"  +  -map N:v:0 -an -frames:v 1 [-vf "scale=W:-2"] -q:v 3 -pix_fmt yuvj420p "out"
        var inputLength = videoPath.Length + 28;
        var outputLength = lastFramePath.Length + 72 + (scaleWidth > 0 ? 22 : 0);
        return inputLength + outputLength;
    }

    private static string FramePath(string tmpDir, int index)
        => Path.Combine(tmpDir, $"frame_{index:D4}.jpg");

    /// <summary>
    /// Builds one ffmpeg invocation covering <paramref name="count"/> timestamps.
    /// The output options per frame mirror the historical single-frame command exactly so that
    /// frames (and pHashes derived from them) do not change.
    /// </summary>
    internal static IReadOnlyList<string> BuildBatchArguments(
        string videoPath,
        string tmpDir,
        IReadOnlyList<double> timestamps,
        int start,
        int count,
        int scaleWidth,
        string? preFilter = null)
    {
        var args = new List<string> { "-v", "error", "-y" };

        for (var offset = 0; offset < count; offset++)
        {
            // -ss before -i is an input-side seek: ffmpeg jumps to the nearest keyframe instead of
            // decoding from the start. The invariant culture is mandatory - a comma decimal
            // separator makes ffmpeg reject the option outright.
            var seconds = Math.Max(0, timestamps[start + offset]);
            args.AddRange(["-threads", "1", "-ss", seconds.ToString("F3", CultureInfo.InvariantCulture), "-i", videoPath]);
        }

        var filters = new List<string>();
        if (preFilter != null)
            filters.Add(preFilter);
        if (scaleWidth > 0)
            filters.Add("scale=" + scaleWidth.ToString(CultureInfo.InvariantCulture) + ":-2");

        for (var offset = 0; offset < count; offset++)
        {
            args.AddRange(["-map", offset.ToString(CultureInfo.InvariantCulture) + ":v:0", "-an", "-frames:v", "1"]);
            if (filters.Count > 0)
                args.AddRange(["-vf", string.Join(',', filters)]);
            // -threads 1 on the OUTPUT caps the mjpeg encoder. The -threads 1 before each input only
            // caps that input's decoder; each output encoder otherwise defaults to frame threading
            // across every core, so a 24-output batch spawned ~770 threads on a 32-core host. Capping
            // it brings that to ~40 with no measurable time cost - encoding a 160px JPEG is trivial -
            // and the written frames are byte-identical, which matters because pHashes are built
            // from them.
            // -pix_fmt yuvj420p forces full-range JPEG so the mjpeg encoder accepts limited-range
            // YUV sources instead of failing with "Non full-range YUV is non-standard".
            args.AddRange(["-threads", "1", "-q:v", "3", "-pix_fmt", "yuvj420p", FramePath(tmpDir, start + offset)]);
        }

        return args;
    }

    private static string Summarize(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return "(no output)";
        var firstLine = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? stderr;
        return firstLine.Length > 200 ? firstLine[..200] : firstLine;
    }

    private static void DisposeFrames(Image<Rgba32>?[] frames)
    {
        for (var index = 0; index < frames.Length; index++)
        {
            try { frames[index]?.Dispose(); } catch { /* best effort */ }
            frames[index] = null;
        }
    }
}
