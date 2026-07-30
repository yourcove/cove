using System.Globalization;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Cove.Api.Services;

internal static class FfmpegProcessFrameExtractor
{
    private static readonly TimeSpan FrameExtractionTimeout = TimeSpan.FromSeconds(30);

    public static async Task<Image<Rgba32>[]?> ExtractFramesAsync(
        string ffmpegPath,
        string videoPath,
        IReadOnlyList<double> timestamps,
        int scaleWidth,
        ILogger logger,
        CancellationToken ct)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"cove_frames_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        var frames = new Image<Rgba32>?[timestamps.Count];
        var extracted = 0;
        logger.LogTrace(
            "FFmpeg frame extraction started for {Path}: frames={FrameCount}, scaleWidth={ScaleWidth}",
            videoPath,
            timestamps.Count,
            scaleWidth);

        try
        {
            for (var index = 0; index < timestamps.Count; index++)
            {
                ct.ThrowIfCancellationRequested();

                var framePath = Path.Combine(tmpDir, $"frame_{index:D3}.jpg");
                var timestamp = timestamps[index];

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = BuildExtractFrameArguments(videoPath, timestamp, scaleWidth, framePath),
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using var proc = System.Diagnostics.Process.Start(psi)!;
                var stderrTask = proc.StandardError.ReadToEndAsync(ct);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(FrameExtractionTimeout);
                try
                {
                    await proc.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Timeout OR outer cancellation (e.g. the batch was aborted). In BOTH cases the ffmpeg
                    // process is still running — Process.Dispose() does not stop it, so failing to Kill here
                    // leaks orphaned ffmpeg processes that keep pegging the CPU after the job ends (exactly
                    // the "lots of ffmpeg processes throttling my cpu" report). Always kill the tree and
                    // observe stderr so the reader task isn't left unobserved.
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    try { await stderrTask; } catch { }
                    DisposeFrames(frames);
                    if (ct.IsCancellationRequested)
                        throw;
                    logger.LogWarning("FFmpeg timed out extracting frame {Index} from {Path}", index, videoPath);
                    return null;
                }

                if (proc.ExitCode != 0 || !File.Exists(framePath))
                {
                    var err = await stderrTask;
                    logger.LogWarning("FFmpeg failed extracting frame {Index} from {Path}: {Error}", index, videoPath, err);
                    DisposeFrames(frames);
                    return null;
                }

                frames[index] = await Image.LoadAsync<Rgba32>(framePath, ct);
                extracted++;
            }

            logger.LogTrace(
                "FFmpeg frame extraction completed for {Path}: frames={FrameCount}",
                videoPath,
                extracted);
            return frames.Cast<Image<Rgba32>>().ToArray();
        }
        catch (OperationCanceledException)
        {
            DisposeFrames(frames);
            throw;
        }
        catch (Exception ex)
        {
            DisposeFrames(frames);
            logger.LogWarning(ex, "FFmpeg process frame extraction failed for {Path}", videoPath);
            return null;
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    /// <summary>Builds the single-frame extraction ffmpeg arguments. The seek time MUST be formatted with
    /// the invariant culture — on machines whose locale uses a comma decimal separator (de-DE, pt-BR, …) a
    /// default <c>{timestamp:F3}</c> emits "697,910", which ffmpeg rejects ("Invalid duration for option ss",
    /// exit -22), failing thumbnail/sprite generation for every video.
    /// -ss before -i = fast input-side seek; -threads 1 + -an keep each extraction to ~1 core so N parallel
    /// jobs don't each fan out and thrash the CPU; -pix_fmt yuvj420p forces full-range JPEG so the mjpeg
    /// encoder accepts limited-range YUV sources (exit 234).</summary>
    internal static string BuildExtractFrameArguments(string videoPath, double timestamp, int scaleWidth, string framePath)
        => $"-v error -threads 1 -ss {timestamp.ToString("F3", CultureInfo.InvariantCulture)} -i \"{videoPath}\" -an -vframes 1 -vf \"scale={scaleWidth}:-2\" -q:v 3 -pix_fmt yuvj420p -y \"{framePath}\"";

    private static void DisposeFrames(Image<Rgba32>?[] frames)
    {
        for (var index = 0; index < frames.Length; index++)
        {
            try { frames[index]?.Dispose(); } catch { }
            frames[index] = null;
        }
    }
}
