using Cove.Core.Interfaces;
using System.Diagnostics;

namespace Cove.Api.Services;

public interface ITranscodeService
{
    Task<Stream?> TranscodeToMp4Async(string inputPath, string? resolution, CancellationToken ct = default);
    Task<string?> GenerateHlsManifestAsync(int sceneId, string inputPath, string? resolution, CancellationToken ct = default);
    Task<Stream?> GetHlsSegmentAsync(int sceneId, string segment, CancellationToken ct = default);
    string[] GetAvailableResolutions(int sourceWidth, int sourceHeight);
}

public class TranscodeService : ITranscodeService
{
    private readonly CoveConfiguration _config;
    private readonly ILogger<TranscodeService> _logger;
    private readonly SemaphoreSlim _transcodeSemaphore = new(2); // Limit concurrent transcodes
    private string? _ffmpegPath;

    private static readonly Dictionary<string, (int width, int height)> ResolutionProfiles = new()
    {
        ["240p"] = (426, 240),
        ["360p"] = (640, 360),
        ["480p"] = (854, 480),
        ["720p"] = (1280, 720),
        ["1080p"] = (1920, 1080),
        ["1440p"] = (2560, 1440),
        ["4K"] = (3840, 2160),
    };

    public TranscodeService(CoveConfiguration config, ILogger<TranscodeService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public string[] GetAvailableResolutions(int sourceWidth, int sourceHeight)
    {
        var maxSize = _config.MaxStreamingTranscodeSize > 0 ? _config.MaxStreamingTranscodeSize : sourceHeight;
        return ResolutionProfiles
            .Where(kv => kv.Value.height <= sourceHeight && kv.Value.height <= maxSize)
            .Select(kv => kv.Key)
            .ToArray();
    }

    public async Task<Stream?> TranscodeToMp4Async(string inputPath, string? resolution, CancellationToken ct = default)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null)
        {
            _logger.LogWarning("FFmpeg not found, cannot transcode");
            return null;
        }

        var scaleFilter = "";
        if (resolution != null && ResolutionProfiles.TryGetValue(resolution, out var res))
        {
            scaleFilter = $"-vf scale={res.width}:{res.height}:force_original_aspect_ratio=decrease,scale=trunc(iw/2)*2:trunc(ih/2)*2";
        }

        var hwAccel = GetHwAccelArgs();
        var outputArgs = _config.LiveTranscodeOutputArgs ?? "-c:v libx264 -preset veryfast -crf 23 -c:a aac -b:a 128k";

        var args = $"{hwAccel} -i \"{inputPath}\" {scaleFilter} {outputArgs} -movflags frag_keyframe+empty_moov -f mp4 pipe:1";

        await _transcodeSemaphore.WaitAsync(ct);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = Process.Start(psi);
            if (process == null)
            {
                _transcodeSemaphore.Release();
                return null;
            }

            // Read stderr in background for logging
            _ = Task.Run(async () =>
            {
                var stderr = await process.StandardError.ReadToEndAsync(ct);
                if (!string.IsNullOrWhiteSpace(stderr))
                    _logger.LogDebug("[Transcode] {Stderr}", stderr[..Math.Min(stderr.Length, 500)]);
            }, ct);

            ct.Register(() => { try { process.Kill(); } catch { } });

            // Wrap the stdout stream so the semaphore is released when the caller
            // finishes consuming the stream (after the HTTP response completes).
            return new SemaphoreReleasingStream(process.StandardOutput.BaseStream, process, _transcodeSemaphore);
        }
        catch
        {
            _transcodeSemaphore.Release();
            throw;
        }
    }

    public async Task<string?> GenerateHlsManifestAsync(int sceneId, string inputPath, string? resolution, CancellationToken ct = default)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null) return null;

        var outputDir = Path.Combine(_config.GeneratedPath ?? Path.GetTempPath(), "transcodes", "hls", sceneId.ToString());
        Directory.CreateDirectory(outputDir);

        var manifestPath = Path.Combine(outputDir, $"{resolution ?? "original"}.m3u8");

        // If manifest already exists and is recent, return it
        if (File.Exists(manifestPath) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(manifestPath)).TotalHours < 24)
        {
            return await File.ReadAllTextAsync(manifestPath, ct);
        }

        var scaleFilter = "";
        if (resolution != null && ResolutionProfiles.TryGetValue(resolution, out var res))
        {
            scaleFilter = $"-vf scale={res.width}:{res.height}:force_original_aspect_ratio=decrease,scale=trunc(iw/2)*2:trunc(ih/2)*2";
        }

        var hwAccel = GetHwAccelArgs();
        var segmentPath = Path.Combine(outputDir, $"{resolution ?? "original"}_%04d.ts");

        var args = $"{hwAccel} -i \"{inputPath}\" {scaleFilter} -c:v libx264 -preset veryfast -crf 23 -c:a aac -b:a 128k " +
                   $"-f hls -hls_time 6 -hls_list_size 0 -hls_segment_filename \"{segmentPath}\" \"{manifestPath}\"";

        await _transcodeSemaphore.WaitAsync(ct);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync(ct);
                _logger.LogWarning("HLS generation failed with exit code {Code}: {Stderr}", process.ExitCode, stderr[..Math.Min(stderr.Length, 500)]);
                return null;
            }

            return File.Exists(manifestPath) ? await File.ReadAllTextAsync(manifestPath, ct) : null;
        }
        finally
        {
            _transcodeSemaphore.Release();
        }
    }

    public Task<Stream?> GetHlsSegmentAsync(int sceneId, string segment, CancellationToken ct = default)
    {
        var segmentPath = Path.Combine(_config.GeneratedPath ?? Path.GetTempPath(), "transcodes", "hls", sceneId.ToString(), segment);

        if (!File.Exists(segmentPath)) return Task.FromResult<Stream?>(null);

        // Validate segment name is safe (no directory traversal)
        var segmentName = Path.GetFileName(segment);
        if (segmentName != segment) return Task.FromResult<Stream?>(null);

        Stream stream = new FileStream(segmentPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Task.FromResult<Stream?>(stream);
    }

    private string GetHwAccelArgs()
    {
        return _config.TranscodeHardwareAcceleration?.ToLowerInvariant() switch
        {
            "nvenc" => "-hwaccel cuda -hwaccel_output_format cuda",
            "vaapi" => "-hwaccel vaapi -hwaccel_device /dev/dri/renderD128",
            "qsv" => "-hwaccel qsv",
            _ => _config.LiveTranscodeInputArgs ?? ""
        };
    }

    private string? FindFfmpeg()
    {
        if (_ffmpegPath != null) return _ffmpegPath;

        if (!string.IsNullOrEmpty(_config.FfmpegPath) && File.Exists(_config.FfmpegPath))
        {
            _ffmpegPath = _config.FfmpegPath;
            return _ffmpegPath;
        }

        // Search PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (File.Exists(candidate))
            {
                _ffmpegPath = candidate;
                return _ffmpegPath;
            }
        }

        _logger.LogWarning("FFmpeg not found in PATH or configured path");
        return null;
    }
}

/// <summary>
/// Wraps a stream so that when it is disposed the associated FFmpeg process
/// is killed and the transcode semaphore is released.  Without this, the
/// semaphore leaks each time a transcode stream finishes.
/// </summary>
file sealed class SemaphoreReleasingStream(Stream inner, System.Diagnostics.Process process, SemaphoreSlim semaphore) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            try { if (!process.HasExited) process.Kill(true); } catch { }
            try { process.Dispose(); } catch { }
            semaphore.Release();
        }
        base.Dispose(disposing);
    }
}
