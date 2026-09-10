using System.Diagnostics;
using System.Globalization;
using Cove.Core.Common;
using Cove.Core.Services;
using Cove.Core.Interfaces;

namespace Cove.Api.Services;

/// <summary>Extracts temporary time-local visual fingerprints and review frames for an alignment request.</summary>
public sealed class VideoAlignmentExtractor(CoveConfiguration config)
{
    public const int AlgorithmVersion = 8;
    public const int Width = 96, Height = 54, MaximumSamples = 8192;
    private readonly SemaphoreSlim slots = new(2);

    public async Task<AutomaticVideoAlignment> FindAutomaticAlignmentAsync(List<AlignmentSample> source, List<AlignmentSample> target,
        double sourceDuration, double targetDuration, double step, CancellationToken ct)
    {
        await slots.WaitAsync(ct);
        try { return await Task.Run(() => VideoAlignment.FindAutomaticAlignment(source, target, sourceDuration, targetDuration, step, ct), ct); }
        finally { slots.Release(); }
    }

    public async Task<(double Step, List<AlignmentSample> Samples)> ExtractAsync(string path, double duration, CancellationToken ct)
    {
        if (!double.IsFinite(duration) || duration <= 0 || duration > 24 * 60 * 60)
            throw new InvalidOperationException("Alignment requires a known duration of at most 24 hours.");
        await slots.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            var token = timeout.Token;
            var windows = VideoAlignment.PlanSampleWindows(duration);
            var sampledDuration = windows.Sum(window => window.EndSec - window.StartSec);
            var step = Math.Max(.1, sampledDuration / (MaximumSamples - 1));
            var executable = ResolveFfmpeg();
            var samples = new List<AlignmentSample>();
            for (var windowIndex = 0; windowIndex < windows.Count; windowIndex++)
                samples.AddRange(await ExtractWindowAsync(executable, path, windows[windowIndex], windowIndex, step, token));
            if (samples.Count < 3)
                throw new InvalidOperationException("Could not extract enough video frames for alignment.");
            return (step, samples);
        }
        finally { slots.Release(); }
    }

    public async Task<string> ExtractColorFrameAsync(string path, double timeSec, CancellationToken ct)
    {
        await slots.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var token = timeout.Token;
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveFfmpeg(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[]
            {
                "-nostdin", "-v", "error", "-threads", "2", "-ss", Math.Max(0, timeSec).ToString("R", CultureInfo.InvariantCulture),
                "-i", path, "-map", "0:v:0", "-an", "-sn", "-frames:v", "1", "-vf", "scale=480:270:force_original_aspect_ratio=decrease:force_divisible_by=2", "-c:v", "mjpeg", "-q:v", "3", "-f", "image2pipe", "pipe:1",
            }) startInfo.ArgumentList.Add(arg);
            FfmpegProcessEnvironment.Apply(startInfo, startInfo.FileName);
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start alignment preview extraction.");
            var outputTask = ReadLimitedBytesAsync(process.StandardOutput.BaseStream, 1_000_000, token);
            var errorTask = ReadLimitedTextAsync(process.StandardError, 16_384, token);
            try
            {
                await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(token));
                var (bytes, exceededLimit) = await outputTask;
                var error = await errorTask;
                if (process.ExitCode != 0 || bytes.Length == 0 || exceededLimit)
                    throw new InvalidOperationException(exceededLimit ? "The alignment preview frame exceeded its size limit." : string.IsNullOrWhiteSpace(error) ? "Could not extract an alignment preview frame." : error);
                return Convert.ToBase64String(bytes);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Alignment preview extraction timed out."); }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
        }
        finally { slots.Release(); }
    }

    private static async Task<(byte[] Bytes, bool ExceededLimit)> ReadLimitedBytesAsync(Stream stream, int limit, CancellationToken ct)
    {
        await using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var exceeded = false;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            var writable = Math.Min(read, Math.Max(0, limit - (int)output.Length));
            if (writable > 0) await output.WriteAsync(buffer.AsMemory(0, writable), ct);
            if (writable < read) exceeded = true;
        }
        return (output.ToArray(), exceeded);
    }

    private static async Task<string> ReadLimitedTextAsync(StreamReader reader, int limit, CancellationToken ct)
    {
        var output = new System.Text.StringBuilder(Math.Min(limit, 1024));
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
            if (output.Length < limit) output.Append(buffer, 0, Math.Min(read, limit - output.Length));
        return output.ToString();
    }

    private string ResolveFfmpeg()
        => !string.IsNullOrWhiteSpace(config.FfmpegPath) && File.Exists(config.FfmpegPath)
            ? config.FfmpegPath
            : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
                .Select(directory => Path.Combine(directory, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"))
                .FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("FFmpeg is unavailable. Configure it in Settings before capturing a reference.");

    private static async Task<List<AlignmentSample>> ExtractWindowAsync(
        string executable, string path, AlignedRange window, int windowIndex, double step, CancellationToken token)
    {
        var frameCount = Math.Min(MaximumSamples, (int)Math.Ceiling((window.EndSec - window.StartSec) / step));
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            "-nostdin", "-v", "error", "-threads", "2",
            "-ss", window.StartSec.ToString("R", CultureInfo.InvariantCulture), "-i", path,
            "-map", "0:v:0", "-an", "-sn", "-t", (window.EndSec - window.StartSec).ToString("R", CultureInfo.InvariantCulture),
            "-vf", $"setpts=PTS-STARTPTS,fps=fps=1/{step.ToString("R", CultureInfo.InvariantCulture)}:start_time=0,scale={Width}:{Height},format=gray",
            "-frames:v", frameCount.ToString(CultureInfo.InvariantCulture), "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1",
        })
            startInfo.ArgumentList.Add(arg);
        FfmpegProcessEnvironment.Apply(startInfo, startInfo.FileName);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start alignment extraction.");
        var errors = Task.Run(async () =>
        {
            var buffer = new char[4096];
            while (await process.StandardError.ReadAsync(buffer, token) > 0) { }
        }, CancellationToken.None);
        var samples = new List<AlignmentSample>(frameCount);
        try
        {
            var frame = new byte[Width * Height];
            while (samples.Count < frameCount)
            {
                var filled = 0;
                while (filled < frame.Length)
                {
                    var read = await process.StandardOutput.BaseStream.ReadAsync(frame.AsMemory(filled), token);
                    if (read == 0) break;
                    filled += read;
                }
                if (filled == 0) break;
                if (filled != frame.Length) throw new InvalidOperationException("Incomplete alignment frame.");
                samples.Add(DescribeFrame(window.StartSec + samples.Count * step, frame) with { Window = windowIndex });
            }
            await process.WaitForExitAsync(token);
            await errors;
            if (process.ExitCode != 0) throw new InvalidOperationException("Could not extract video frames for alignment.");
            return samples;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            try { await errors; } catch (OperationCanceledException) { }
        }
    }

    public static AlignmentSample DescribeFrame(double time, byte[] frame)
    {
        ulong hash = 0;
        // Horizontal gradients tolerate brightness changes and ordinary encode/resolution differences.
        double Cell(int x, int y)
        {
            var x0 = x * Width / 9; var x1 = (x + 1) * Width / 9;
            var y0 = y * Height / 8; var y1 = (y + 1) * Height / 8;
            double total = 0;
            for (var row = y0; row < y1; row++)
            for (var col = x0; col < x1; col++) total += frame[row * Width + col];
            return total / ((x1 - x0) * (y1 - y0));
        }
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
        {
            if (Cell(x, y) > Cell(x + 1, y)) hash |= 1UL << (y * 8 + x);
        }
        var mean = frame.Average(b => (double)b);
        var contrast = Math.Sqrt(frame.Average(b => (b - mean) * (b - mean)));
        var descriptor = new byte[16 * 9];
        for (var y = 0; y < 9; y++)
        for (var x = 0; x < 16; x++)
        {
            double total = 0;
            for (var row = y * 6; row < (y + 1) * 6; row++)
            for (var col = x * 6; col < (x + 1) * 6; col++) total += frame[row * Width + col];
            descriptor[y * 16 + x] = (byte)Math.Clamp(128 + (total / 36 - mean) * 32 / Math.Max(8, contrast), 0, 255);
        }
        return new(time, hash, contrast, string.Empty, Convert.ToBase64String(descriptor));
    }

}
