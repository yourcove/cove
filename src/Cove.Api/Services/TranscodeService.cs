using Cove.Core.Interfaces;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Cove.Api.Services;

public interface ITranscodeService
{
    Task<Stream?> TranscodeToMp4Async(string inputPath, string? resolution, double startSeconds = 0, CancellationToken ct = default);
    Task<string?> GenerateHlsManifestAsync(int videoId, string inputPath, string? resolution, CancellationToken ct = default);
    Task<Stream?> GetHlsSegmentAsync(int videoId, string segment, CancellationToken ct = default);
    string[] GetAvailableResolutions(int sourceWidth, int sourceHeight);
}

public class TranscodeService : ITranscodeService
{
    private readonly CoveConfiguration _config;
    private readonly ILogger<TranscodeService> _logger;
    private readonly SemaphoreSlim _transcodeSemaphore = new(2); // Limit concurrent transcodes
    private string? _ffmpegPath;

    // Probed H.264 encoder, cached and re-evaluated whenever the relevant settings change.
    private string? _encoder;
    private string? _encoderFingerprint;
    private readonly object _encoderLock = new();

    // How long to wait for the first byte of transcoded output before treating the encode as failed.
    private static readonly TimeSpan FirstByteTimeout = TimeSpan.FromSeconds(25);

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

    public async Task<Stream?> TranscodeToMp4Async(string inputPath, string? resolution, double startSeconds = 0, CancellationToken ct = default)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null)
        {
            _logger.LogWarning("FFmpeg not found, cannot transcode");
            return null;
        }

        var encoder = GetH264Encoder(ffmpeg);
        const string outputContainer = "-movflags frag_keyframe+empty_moov -f mp4 pipe:1";

        await _transcodeSemaphore.WaitAsync(ct);
        var ownedByStream = false;
        try
        {
            var stream = await TrySpawnTranscodeAsync(
                ffmpeg, BuildEncodeArgs(ffmpeg, inputPath, resolution, startSeconds, encoder, outputContainer), encoder, inputPath, finalAttempt: encoder == "libx264", ct);

            // A hardware-encoder pipeline can fail at runtime even after probing OK — e.g. an NVENC
            // session-limit exhaustion (NV_ENC_ERR_OUT_OF_MEMORY) when previews are generating on the
            // same GPU, or a driver/NVENC-library mismatch (NV_ENC_ERR_INCOMPATIBLE_CLIENT_KEY). Whatever
            // the cause, fall back to libx264 so playback still works instead of returning an error.
            if (stream == null && encoder != "libx264" && !ct.IsCancellationRequested)
            {
                _logger.LogDebug("Live transcode with {Encoder} failed for {Input}; retrying with libx264.", encoder, inputPath);
                stream = await TrySpawnTranscodeAsync(
                    ffmpeg, BuildEncodeArgs(ffmpeg, inputPath, resolution, startSeconds, "libx264", outputContainer), "libx264", inputPath, finalAttempt: true, ct);
            }

            if (stream != null)
            {
                // Ownership of the semaphore passes to the stream wrapper, which releases it (and kills
                // the process) when the HTTP response finishes consuming the stream.
                ownedByStream = true;
                return stream;
            }

            _transcodeSemaphore.Release();
            return null;
        }
        catch
        {
            if (!ownedByStream) _transcodeSemaphore.Release();
            throw;
        }
    }

    /// <summary>
    /// Spawns an ffmpeg transcode and reads the first output chunk to confirm the pipeline actually
    /// produces data. A broken pipeline (e.g. the old NVENC "Impossible to convert between formats" when
    /// GPU surfaces meet a software filter, or an NVENC session-limit failure) exits immediately with no
    /// output; reading the first chunk catches that before the controller commits a 200 wrapping a dead
    /// stream. Returns a stream that owns the process + transcode semaphore (releasing both on dispose)
    /// on success, or null on failure — the caller decides whether to retry, release the semaphore, or
    /// surface the error. This method never releases the semaphore itself.
    /// </summary>
    private async Task<Stream?> TrySpawnTranscodeAsync(string ffmpeg, string args, string encoder, string inputPath, bool finalAttempt, CancellationToken ct)
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
        FfmpegProcessEnvironment.Apply(psi, ffmpeg);

        var process = Process.Start(psi);
        if (process == null)
            return null;

        // Drain stderr continuously into a capped buffer. This both prevents the stderr pipe from
        // filling (which would deadlock ffmpeg) and lets us report the real error if the encode fails.
        var stderr = new StringBuilder();
        _ = Task.Run(async () =>
        {
            try
            {
                var buffer = new char[4096];
                int n;
                while ((n = await process.StandardError.ReadAsync(buffer, ct)) > 0)
                    lock (stderr) { if (stderr.Length < 8192) stderr.Append(buffer, 0, n); }
            }
            catch { /* process exited / cancelled */ }
        }, ct);

        ct.Register(() => { try { process.Kill(true); } catch { } });

        var stdout = process.StandardOutput.BaseStream;
        var prefix = new byte[64 * 1024];
        int read;
        using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            readCts.CancelAfter(FirstByteTimeout);
            try
            {
                read = await stdout.ReadAsync(prefix, readCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(true); } catch { }
                if (finalAttempt)
                    _logger.LogWarning("Transcode produced no output within {Timeout}s for {Input} (encoder {Encoder}). ffmpeg: {Error}", FirstByteTimeout.TotalSeconds, inputPath, encoder, Tail(stderr));
                else if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Transcode produced no output within {Timeout}s for {Input} (encoder {Encoder}); fallback may be attempted. ffmpeg: {Error}", FirstByteTimeout.TotalSeconds, inputPath, encoder, Tail(stderr));
                return null;
            }
        }

        if (read == 0)
        {
            try { await process.WaitForExitAsync(ct); } catch { }
            var exit = process.HasExited ? process.ExitCode : -1;
            if (finalAttempt)
                _logger.LogWarning("Transcode failed (exit {Code}) for {Input} (encoder {Encoder}). ffmpeg: {Error}", exit, inputPath, encoder, Tail(stderr));
            else if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Transcode failed (exit {Code}) for {Input} (encoder {Encoder}); fallback may be attempted. ffmpeg: {Error}", exit, inputPath, encoder, Tail(stderr));
            try { process.Kill(true); } catch { }
            return null;
        }

        return new PrefixedReleasingStream(prefix, read, stdout, process, _transcodeSemaphore);
    }

    public async Task<string?> GenerateHlsManifestAsync(int videoId, string inputPath, string? resolution, CancellationToken ct = default)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null) return null;

        var outputDir = Path.Combine(_config.GeneratedPath ?? Path.GetTempPath(), "transcodes", "hls", videoId.ToString());
        Directory.CreateDirectory(outputDir);

        var manifestPath = Path.Combine(outputDir, $"{resolution ?? "original"}.m3u8");

        // If manifest already exists and is recent, return it
        if (File.Exists(manifestPath) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(manifestPath)).TotalHours < 24)
        {
            return await File.ReadAllTextAsync(manifestPath, ct);
        }

        var encoder = GetH264Encoder(ffmpeg);
        var segmentPath = Path.Combine(outputDir, $"{resolution ?? "original"}_%04d.ts");
        var args = BuildEncodeArgs(ffmpeg, inputPath, resolution, 0, encoder,
            $"-f hls -hls_time 6 -hls_list_size 0 -hls_segment_filename \"{segmentPath}\" \"{manifestPath}\"");

        await _transcodeSemaphore.WaitAsync(ct);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = args,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            FfmpegProcessEnvironment.Apply(psi, ffmpeg);

            using var process = Process.Start(psi);
            if (process == null) return null;

            // Drain stderr concurrently so a verbose/long encode can't fill the pipe buffer and
            // deadlock against our WaitForExit.
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("HLS generation failed (exit {Code}, encoder {Encoder}): {Stderr}",
                    process.ExitCode, encoder, stderr[..Math.Min(stderr.Length, 500)]);
                return null;
            }

            return File.Exists(manifestPath) ? await File.ReadAllTextAsync(manifestPath, ct) : null;
        }
        finally
        {
            _transcodeSemaphore.Release();
        }
    }

    public Task<Stream?> GetHlsSegmentAsync(int videoId, string segment, CancellationToken ct = default)
    {
        var segmentName = Path.GetFileName(segment);
        if (string.IsNullOrWhiteSpace(segmentName) || segmentName != segment) return Task.FromResult<Stream?>(null);

        var segmentPath = Path.Combine(_config.GeneratedPath ?? Path.GetTempPath(), "transcodes", "hls", videoId.ToString(), segment);

        if (!File.Exists(segmentPath)) return Task.FromResult<Stream?>(null);

        Stream stream = new FileStream(segmentPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Task.FromResult<Stream?>(stream);
    }

    /// <summary>
    /// Builds a coherent ffmpeg command for a software-decode → (software scale) → hardware-encode
    /// pipeline. Hardware <b>decode</b> is opt-in only (via LiveTranscodeInputArgs); the previous
    /// design forced GPU surfaces for NVENC decode (<c>-hwaccel_output_format cuda</c>) but left the
    /// scale filter and encoder in software, which cannot read GPU frames and aborted on every
    /// resolution change. Hardware <b>encode</b> (the expensive half) is selected by probe with a
    /// libx264 fallback, so a misconfigured GPU degrades gracefully instead of black-screening.
    /// </summary>
    private string BuildEncodeArgs(string ffmpeg, string inputPath, string? resolution, double startSeconds, string encoder, string outputContainerArgs)
    {
        var scaleChain = BuildScaleChain(resolution);
        var videoFilter = FfmpegHwAccel.VideoFilterForEncoder(encoder, scaleChain);

        // Input/decode args: software by default; honor an explicit override and add any encoder
        // device setup (e.g. the VAAPI render node).
        var decodeArgs = !string.IsNullOrWhiteSpace(_config.FfmpegInputArgs) ? _config.FfmpegInputArgs! : string.Empty;
        var inputArgs = Join(FfmpegHwAccel.InputArgsForEncoder(encoder), decodeArgs);

        // Encode args: full user override if provided, else encoder-correct constant-quality args.
        var encodeArgs = !string.IsNullOrWhiteSpace(_config.FfmpegOutputArgs)
            ? _config.FfmpegOutputArgs!
            : $"{FfmpegHwAccel.VideoEncodeArgs(encoder, 23, "veryfast")} -c:a aac -b:a 128k";

        var seekArgs = startSeconds > 0
            ? $"-ss {Math.Max(0, startSeconds).ToString("0.###", CultureInfo.InvariantCulture)}"
            : string.Empty;

        return Join(inputArgs, seekArgs, $"-i \"{inputPath}\"", videoFilter, encodeArgs, outputContainerArgs);
    }

    private static string BuildScaleChain(string? resolution)
    {
        if (resolution != null && ResolutionProfiles.TryGetValue(resolution, out var res))
            return $"scale={res.width}:{res.height}:force_original_aspect_ratio=decrease,scale=trunc(iw/2)*2:trunc(ih/2)*2";
        return string.Empty;
    }

    private static string Join(params string[] parts) =>
        string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string Tail(StringBuilder stderr)
    {
        lock (stderr)
        {
            var text = stderr.ToString();
            return text.Length > 500 ? text[^500..] : text;
        }
    }

    /// <summary>Resolve the H.264 encoder for live transcoding, honoring the configured hardware
    /// acceleration. Cached and re-probed only when ffmpeg path or the HW-accel setting changes, so
    /// a Settings change takes effect without a restart and without re-probing on every request.</summary>
    private string GetH264Encoder(string ffmpegPath)
    {
        var fingerprint = $"{ffmpegPath}|{_config.HardwareAcceleration}";
        lock (_encoderLock)
        {
            if (_encoder != null && _encoderFingerprint == fingerprint) return _encoder;
            _encoder = FfmpegHwAccel.SelectH264Encoder(ffmpegPath, _config.HardwareAcceleration, _logger);
            _encoderFingerprint = fingerprint;
            return _encoder;
        }
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
/// Wraps FFmpeg's stdout pipe, prepending a buffer of bytes already read for failure detection,
/// and ensures the FFmpeg process is killed and the transcode semaphore released when the stream
/// is disposed (i.e. after the HTTP response completes). Without the release the semaphore leaks.
/// </summary>
file sealed class PrefixedReleasingStream(byte[] prefix, int prefixLen, Stream inner, System.Diagnostics.Process process, SemaphoreSlim semaphore) : Stream
{
    private int _prefixPos;
    private int _disposed;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_prefixPos < prefixLen)
        {
            var n = Math.Min(count, prefixLen - _prefixPos);
            Array.Copy(prefix, _prefixPos, buffer, offset, n);
            _prefixPos += n;
            return n;
        }
        return inner.Read(buffer, offset, count);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_prefixPos < prefixLen)
        {
            var n = Math.Min(buffer.Length, prefixLen - _prefixPos);
            prefix.AsSpan(_prefixPos, n).CopyTo(buffer.Span);
            _prefixPos += n;
            return n;
        }
        return await inner.ReadAsync(buffer, ct);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { inner.Dispose(); } catch { }
            try { if (!process.HasExited) process.Kill(true); } catch { }
            try { process.Dispose(); } catch { }
            semaphore.Release();
        }
        base.Dispose(disposing);
    }
}
