using System.Globalization;
using System.Security.Cryptography;
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
/// The per-frame arguments are byte-identical to a one-process-per-frame command, so batching does
/// not change the extracted frames. Sprite frames are JPEGs (<c>scale</c>, <c>-q:v</c> and
/// <c>-pix_fmt</c>). pHash frames use <c>exactFrames</c>: full-precision seeks and lossless BMP, as
/// Stash's phasher extracts them. Those arguments define every stored pHash, so changing them
/// silently invalidates the stored ones.
///
/// Unlike the per-frame path, a frame that cannot be decoded yields a null slot rather than
/// failing the whole request, so callers can decide their own tolerance (a sprite can survive a
/// few unreadable frames; a pHash cannot).
///
/// Callers that only need a representative picture near each timestamp (sprites) can ask for
/// keyframe seeks. An exact seek decodes every frame from the preceding keyframe up to the
/// timestamp and discards all but the last; on 8K VR with 0.5-4s GOPs that is 15-125 full-size
/// frames thrown away per thumbnail, and it made sprite generation CPU-bound (~600 CPU-seconds per
/// video). A keyframe seek decodes one frame: measured on 8 VR sources over a network share, sprites
/// went from 47s to 30s per video at 1/15 of the CPU. The frame shown is the keyframe at or before
/// the timestamp instead of the exact frame. pHash must never use this - its frames define the hash.
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
    /// <param name="keyframeSeek">
    /// Return the keyframe at or before each timestamp instead of the exact frame (see the class
    /// remarks). Frames that come back missing, or identical to another timestamp's frame - two
    /// requests that landed on the same keyframe because keyframes are sparse relative to the
    /// spacing, or a source whose container marks almost none - are re-extracted with exact seeks.
    /// </param>
    /// <param name="exactFrames">
    /// Extract each frame the way Stash's pHash does: seek to the timestamp at full precision rather
    /// than to the millisecond, and write the scaled frame losslessly as BMP instead of as JPEG. Both
    /// matter to a pHash: a seek rounded across a frame boundary can land on the other side of a scene
    /// cut, and the JPEG path's full-range conversion shifts the colours of limited-range sources.
    /// </param>
    public static async Task<Image<Rgba32>?[]?> ExtractAsync(
        string ffmpegPath,
        string videoPath,
        IReadOnlyList<double> timestamps,
        int scaleWidth,
        FfmpegConcurrencyLimiter limiter,
        ILogger logger,
        CancellationToken ct,
        int batchSize = DefaultBatchSize,
        string? preFilter = null,
        bool keyframeSeek = false,
        bool exactFrames = false)
    {
        if (timestamps.Count == 0)
            return null;

        // A batch may not be wider than the whole decode budget, or it could never be admitted.
        batchSize = limiter.ClampBatchSize(batchSize);

        var tmpDir = Path.Combine(Path.GetTempPath(), $"cove_frames_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        var frames = new Image<Rgba32>?[timestamps.Count];
        var fingerprints = new string?[timestamps.Count];

        try
        {
            var pass = new ExtractionPass(ffmpegPath, videoPath, timestamps, scaleWidth, preFilter, batchSize, limiter, logger, frames, fingerprints, exactFrames);
            await pass.RunAsync(Enumerable.Range(0, timestamps.Count).ToArray(), keyframeSeek, Path.Combine(tmpDir, "first"), ct);

            if (keyframeSeek)
            {
                var reseek = IndicesNeedingExactSeek(fingerprints);
                if (reseek.Count > 0)
                {
                    logger.LogInformation(
                        "Keyframe seeks for {Path} left {Count}/{Requested} frames missing or on a shared keyframe; re-extracting those at exact timestamps",
                        videoPath, reseek.Count, timestamps.Count);
                    foreach (var index in reseek)
                    {
                        frames[index]?.Dispose();
                        frames[index] = null;
                        fingerprints[index] = null;
                    }
                    await pass.RunAsync(reseek, keyframeSeek: false, Path.Combine(tmpDir, "exact"), ct);
                }
            }

            logger.LogTrace(
                "Batched frame extraction for {Path}: {Decoded}/{Requested} frames at scaleWidth={ScaleWidth}",
                videoPath, frames.Count(frame => frame != null), timestamps.Count, scaleWidth);

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

    /// <summary>
    /// Slots a keyframe pass did not settle: those with no frame, and every slot whose frame is
    /// byte-identical to another slot's. Encoding is deterministic, so identical output from two
    /// different timestamps means both seeks resolved to the same keyframe. (Genuinely identical
    /// footage - two black frames - is caught too; re-extracting it exactly costs little.)
    /// </summary>
    internal static IReadOnlyList<int> IndicesNeedingExactSeek(IReadOnlyList<string?> fingerprints)
    {
        var counts = fingerprints
            .Where(fingerprint => fingerprint != null)
            .GroupBy(fingerprint => fingerprint!)
            .ToDictionary(group => group.Key, group => group.Count());
        var result = new List<int>();
        for (var index = 0; index < fingerprints.Count; index++)
        {
            var fingerprint = fingerprints[index];
            if (fingerprint == null || counts[fingerprint] > 1)
                result.Add(index);
        }
        return result;
    }

    /// <summary>One request's shared state; each <see cref="RunAsync"/> fills the slots it is given.</summary>
    private sealed class ExtractionPass(
        string ffmpegPath,
        string videoPath,
        IReadOnlyList<double> timestamps,
        int scaleWidth,
        string? preFilter,
        int batchSize,
        FfmpegConcurrencyLimiter limiter,
        ILogger logger,
        Image<Rgba32>?[] frames,
        string?[] fingerprints,
        bool exactFrames)
    {
        public async Task RunAsync(IReadOnlyList<int> indices, bool keyframeSeek, string dir, CancellationToken ct)
        {
            Directory.CreateDirectory(dir);
            // Batches address frames by position within this pass; indices maps a position back to its slot.
            var subset = indices.Select(index => timestamps[index]).ToArray();

            foreach (var batch in PlanBatches(videoPath, dir, subset.Length, scaleWidth, batchSize, preFilter, keyframeSeek, exactFrames))
            {
                ct.ThrowIfCancellationRequested();

                var args = BuildBatchArguments(videoPath, dir, subset, batch.Start, batch.Count, scaleWidth, preFilter, keyframeSeek, exactFrames);
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
                    var position = batch.Start + offset;
                    var index = indices[position];
                    var framePath = FramePath(dir, position, exactFrames);
                    if (!File.Exists(framePath) || new FileInfo(framePath).Length == 0)
                        continue;

                    try
                    {
                        var bytes = await File.ReadAllBytesAsync(framePath, ct);
                        frames[index] = Image.Load<Rgba32>(bytes);
                        fingerprints[index] = Convert.ToHexString(SHA256.HashData(bytes));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogDebug(ex, "Could not load extracted frame {Index} of {Path}", index, videoPath);
                    }
                }
            }
        }
    }

    internal readonly record struct BatchPlan(int Start, int Count);

    /// <summary>Splits the timestamps into batches that each fit inside the command-line limit.</summary>
    internal static IEnumerable<BatchPlan> PlanBatches(
        string videoPath, string tmpDir, int timestampCount, int scaleWidth, int batchSize, string? preFilter = null,
        bool keyframeSeek = false, bool exactFrames = false)
    {
        var requested = Math.Max(1, batchSize);

        // Worst-case characters one input/output pair contributes, measured against the longest
        // frame index so the estimate never under-counts.
        var perFrame = PerFrameArgumentLength(videoPath, tmpDir, timestampCount, scaleWidth, exactFrames) + (preFilter?.Length + 1 ?? 0)
            + (keyframeSeek ? KeyframeSeekArguments.Sum(argument => argument.Length + 1) : 0);
        var affordable = Math.Max(1, MaxCommandLineLength / Math.Max(1, perFrame));
        var effective = Math.Min(requested, affordable);

        for (var start = 0; start < timestampCount; start += effective)
            yield return new BatchPlan(start, Math.Min(effective, timestampCount - start));
    }

    private static int PerFrameArgumentLength(string videoPath, string tmpDir, int timestampCount, int scaleWidth, bool exactFrames)
    {
        var lastFramePath = FramePath(tmpDir, Math.Max(0, timestampCount - 1), exactFrames);
        // -ss <12 chars> -i "path"  +  -map N:v:0 -an -frames:v 1 [-vf "scale=W:-2"] -q:v 3 -pix_fmt yuvj420p "out".
        // A full-precision seek takes up to 24 characters; the BMP output options are shorter than the JPEG ones.
        var inputLength = videoPath.Length + (exactFrames ? 40 : 28);
        var outputLength = lastFramePath.Length + 72 + (scaleWidth > 0 ? 22 : 0);
        return inputLength + outputLength;
    }

    // Decode only keyframes, and keep the first one the seek reaches instead of decoding on to the
    // exact timestamp. Both are needed: -skip_frame alone would wait for the next keyframe.
    private static readonly string[] KeyframeSeekArguments = ["-skip_frame", "nokey", "-noaccurate_seek"];

    private static string FramePath(string tmpDir, int index, bool exactFrames = false)
        => Path.Combine(tmpDir, $"frame_{index:D4}.{(exactFrames ? "bmp" : "jpg")}");

    /// <summary>
    /// A seek as Stash formats it: the shortest decimal that round-trips the value, as Go's
    /// <c>fmt.Sprint</c> writes it. ffmpeg reads the result to the microsecond.
    /// </summary>
    internal static string FormatExactSeek(double seconds)
    {
        var text = seconds.ToString("R", CultureInfo.InvariantCulture);
        // ffmpeg does not accept exponents; only timestamps below 0.0001 s would produce one.
        return text.Contains('E') ? seconds.ToString("0.####################", CultureInfo.InvariantCulture) : text;
    }

    /// <summary>
    /// Builds one ffmpeg invocation covering <paramref name="count"/> timestamps.
    /// The output options per frame mirror a single-frame command exactly, so batching does not change
    /// the frames: JPEG for sprites, lossless BMP for pHash (<paramref name="exactFrames"/>).
    /// </summary>
    internal static IReadOnlyList<string> BuildBatchArguments(
        string videoPath,
        string tmpDir,
        IReadOnlyList<double> timestamps,
        int start,
        int count,
        int scaleWidth,
        string? preFilter = null,
        bool keyframeSeek = false,
        bool exactFrames = false)
    {
        var args = new List<string> { "-v", "error", "-y" };

        for (var offset = 0; offset < count; offset++)
        {
            // -ss before -i is an input-side seek: ffmpeg jumps to the nearest keyframe instead of
            // decoding from the start. The invariant culture is mandatory - a comma decimal
            // separator makes ffmpeg reject the option outright.
            var seconds = Math.Max(0, timestamps[start + offset]);
            if (keyframeSeek)
                args.AddRange(KeyframeSeekArguments);
            var seek = exactFrames ? FormatExactSeek(seconds) : seconds.ToString("F3", CultureInfo.InvariantCulture);
            args.AddRange(["-threads", "1", "-ss", seek, "-i", videoPath]);
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
            // -threads 1 on the OUTPUT caps the frame encoder. The -threads 1 before each input only
            // caps that input's decoder; each output encoder otherwise defaults to frame threading
            // across every core, so a 24-output batch spawned ~770 threads on a 32-core host. Capping
            // it brings that to ~40 with no measurable time cost - encoding a 160px frame is trivial -
            // and the written frames are byte-identical.
            // -pix_fmt yuvj420p forces full-range JPEG so the mjpeg encoder accepts limited-range
            // YUV sources instead of failing with "Non full-range YUV is non-standard".
            if (exactFrames)
                args.AddRange(["-threads", "1", "-c:v", "bmp", FramePath(tmpDir, start + offset, exactFrames)]);
            else
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
