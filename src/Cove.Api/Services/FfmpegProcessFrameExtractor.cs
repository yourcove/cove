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
                    // -ss before -i = fast input-side seek (near-constant cost regardless of video length).
                    // -threads 1 + -an keep each single-frame extraction to ~1 core so N parallel jobs (sized
                    // by MaxParallelTasks) don't each fan out to all cores and thrash the CPU. -pix_fmt yuvj420p
                    // forces full-range JPEG so the mjpeg encoder accepts limited-range YUV sources (exit 234).
                    Arguments = $"-v error -threads 1 -ss {timestamp:F3} -i \"{videoPath}\" -an -vframes 1 -vf \"scale={scaleWidth}:-2\" -q:v 3 -pix_fmt yuvj420p -y \"{framePath}\"",
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
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    logger.LogWarning("FFmpeg timed out extracting frame {Index} from {Path}", index, videoPath);
                    DisposeFrames(frames);
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

    private static void DisposeFrames(Image<Rgba32>?[] frames)
    {
        for (var index = 0; index < frames.Length; index++)
        {
            try { frames[index]?.Dispose(); } catch { }
            frames[index] = null;
        }
    }
}