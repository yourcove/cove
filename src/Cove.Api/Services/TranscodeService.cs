using Cove.Core.Interfaces;
using System.Globalization;

namespace Cove.Api.Services;

public interface ITranscodeService
{
    Task<Stream?> TranscodeToMp4Async(string inputPath, string? resolution, double startSeconds = 0, CancellationToken ct = default);
    Task<string?> GenerateHlsManifestAsync(int videoId, string inputPath, string? resolution, double startSeconds, CancellationToken ct = default);
    Task<Stream?> GetHlsSegmentAsync(int videoId, string segment, CancellationToken ct = default);
    string[] GetAvailableResolutions(int sourceWidth, int sourceHeight);
}

public class TranscodeService : ITranscodeService
{
    private readonly CoveConfiguration _config;
    private readonly ILogger<TranscodeService> _logger;
    private readonly SemaphoreSlim _transcodeSemaphore = new(2); // Limit concurrent transcodes

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
        string[] outputContainer = ["-movflags", "frag_keyframe+empty_moov", "-f", "mp4", "pipe:1"];

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
    private async Task<Stream?> TrySpawnTranscodeAsync(string ffmpeg, IReadOnlyList<string> args, string encoder, string inputPath, bool finalAttempt, CancellationToken ct)
    {
        // The handle drains stderr into a capped buffer, which both keeps the pipe from filling (and
        // deadlocking ffmpeg) and lets us report the real error if the encode fails. Cancelling ct
        // kills the process tree.
        var process = FfmpegProcessRunner.Start(ffmpeg, args, redirectStandardOutput: true, ct);
        var handedOff = false;
        try
        {
            var stdout = process.StandardOutput;
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
                    process.Kill();
                    if (finalAttempt)
                        _logger.LogWarning("Transcode produced no output within {Timeout}s for {Input} (encoder {Encoder}). ffmpeg: {Error}", FirstByteTimeout.TotalSeconds, inputPath, encoder, Tail(process.StandardErrorTail));
                    else if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("Transcode produced no output within {Timeout}s for {Input} (encoder {Encoder}); fallback may be attempted. ffmpeg: {Error}", FirstByteTimeout.TotalSeconds, inputPath, encoder, Tail(process.StandardErrorTail));
                    return null;
                }
            }

            if (read == 0)
            {
                try { await process.WaitForExitAsync(ct); } catch (OperationCanceledException) { }
                var exit = process.HasExited ? process.ExitCode : -1;
                var error = Tail(await process.ReadStandardErrorTailAsync());
                if (finalAttempt)
                    _logger.LogWarning("Transcode failed (exit {Code}) for {Input} (encoder {Encoder}). ffmpeg: {Error}", exit, inputPath, encoder, error);
                else if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Transcode failed (exit {Code}) for {Input} (encoder {Encoder}); fallback may be attempted. ffmpeg: {Error}", exit, inputPath, encoder, error);
                return null;
            }

            handedOff = true;
            return new PrefixedReleasingStream(prefix, read, process, _transcodeSemaphore);
        }
        finally
        {
            if (!handedOff)
                await process.DisposeAsync();
        }
    }

    public async Task<string?> GenerateHlsManifestAsync(int videoId, string inputPath, string? resolution, double startSeconds, CancellationToken ct = default)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null) return null;

        var profile = resolution ?? "original";
        var startKey = HlsStartKey(startSeconds);
        var outputDir = HlsOutputDirectory(videoId, profile, startKey);
        var manifestPath = Path.Combine(outputDir, HlsManifestFileName);

        // A finished, recent playlist is served as-is. Read outside the job lock: segment requests
        // take that lock on every fetch and must not wait behind file I/O.
        TouchHlsOutput(outputDir);
        var cached = await TryReadCompletedHlsManifestAsync(manifestPath, ct);
        if (cached != null) return cached;

        HlsJob? job;
        lock (_hlsJobs)
        {
            if (!_hlsJobs.TryGetValue(manifestPath, out job))
            {
                // A new offset for the same profile supersedes the offsets a player left behind (every
                // seek is a new offset); their encodes are stopped so they release their slot now
                // instead of after the idle timeout. Stopped encodes stay resumable on disk.
                foreach (var other in _hlsJobs.Values)
                {
                    if (other.IsSibling(outputDir)) other.Cancel();
                }

                job = new HlsJob(outputDir, manifestPath);
                job.Completion = RunHlsJobAsync(job, ffmpeg, inputPath, resolution, profile, startKey, startSeconds);
                _hlsJobs[manifestPath] = job;
            }
        }

        // Return as soon as enough of the playlist exists for a player to start, or when the encode
        // finishes (short inputs never reach the segment threshold before ENDLIST is written).
        var deadline = DateTime.UtcNow + HlsFirstSegmentsTimeout;
        while (true)
        {
            job.Touch();
            var completed = job.Completion.IsCompleted;
            if (completed) await job.Completion;

            var manifest = await FileReadRace.TryReadAllTextAsync(manifestPath, ct, pathWasObserved: false);
            var segments = manifest == null ? 0 : CountHlsSegments(manifest);
            if (manifest != null && (segments >= HlsReadySegmentCount || manifest.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal)))
                return manifest;

            // A failed or stopped encode still leaves whatever it produced playable.
            if (completed) return segments > 0 ? manifest : null;

            if (DateTime.UtcNow >= deadline)
            {
                _logger.LogWarning("HLS playlist for {Input} ({Profile} from {Start}s) produced no segments within {Timeout}s.", inputPath, profile, startSeconds, HlsFirstSegmentsTimeout.TotalSeconds);
                return null;
            }

            await Task.WhenAny(job.Completion, Task.Delay(HlsPollInterval, ct));
            ct.ThrowIfCancellationRequested();
        }
    }

    public Task<Stream?> GetHlsSegmentAsync(int videoId, string segment, CancellationToken ct = default)
    {
        // Segment names carry their profile and start offset (see HlsSegmentPattern), which locates
        // the output directory without any extra query state on the segment URL.
        var match = HlsSegmentPattern.Match(segment);
        if (!match.Success) return Task.FromResult<Stream?>(null);

        var outputDir = HlsOutputDirectory(videoId, match.Groups["profile"].Value, match.Groups["start"].Value);
        var segmentPath = Path.Combine(outputDir, segment);
        var manifestPath = Path.Combine(outputDir, HlsManifestFileName);

        // A segment fetch is playback activity for that output: it keeps the idle watchdog from
        // stopping the encode and keeps the directory from being superseded while it is being played.
        TouchHlsOutput(outputDir);
        lock (_hlsJobs)
        {
            if (_hlsJobs.TryGetValue(manifestPath, out var job)) job.Touch();
        }

        if (!File.Exists(segmentPath)) return Task.FromResult<Stream?>(null);

        return Task.FromResult<Stream?>(FileReadRace.TryOpenRead(segmentPath, pathWasObserved: true));
    }

    /// <summary>Whether <paramref name="profile"/> names an HLS rendition Cove can produce: a ladder
    /// entry or "original" (source resolution).</summary>
    public static bool IsHlsProfile(string profile)
        => profile == "original" || ResolutionProfiles.ContainsKey(profile);

    // ----- HLS background encode -----

    private const int HlsSegmentSeconds = 6;
    private const string HlsManifestFileName = "index.m3u8";
    // Segments that must exist before a playlist is handed to the player while the encode continues.
    private const int HlsReadySegmentCount = 1;
    private static readonly TimeSpan HlsPollInterval = TimeSpan.FromMilliseconds(250);
    // Upper bound on how long a playlist request waits for the first segment (covers a slow software
    // encode and a job queued behind the transcode slots).
    private static readonly TimeSpan HlsFirstSegmentsTimeout = TimeSpan.FromSeconds(120);
    // An encode nobody has fetched a playlist or segment from for this long is stopped; a later request
    // resumes it from where it stopped instead of starting over.
    private static readonly TimeSpan HlsIdleTimeout = TimeSpan.FromSeconds(90);
    private static readonly System.Text.RegularExpressions.Regex HlsSegmentPattern =
        new(@"^(?<profile>original|\d{3,4}p|4K)_(?<start>s\d+(?:_\d{1,3})?)_\d{4,}\.ts$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private readonly Dictionary<string, HlsJob> _hlsJobs = new(StringComparer.Ordinal);
    // Last playlist/segment fetch per output directory, so a directory still being played (even after
    // its encode finished) is not deleted by a newer offset.
    private readonly Dictionary<string, DateTime> _hlsOutputLastAccess = new(StringComparer.Ordinal);

    private sealed class HlsJob(string outputDir, string manifestPath)
    {
        private readonly CancellationTokenSource _stop = new();
        private long _lastAccessTicks = DateTime.UtcNow.Ticks;

        public string OutputDir { get; } = outputDir;
        public string ManifestPath { get; } = manifestPath;
        public Task Completion { get; set; } = Task.CompletedTask;
        public volatile bool Failed;
        public CancellationToken StopToken => _stop.Token;

        public void Touch() => Volatile.Write(ref _lastAccessTicks, DateTime.UtcNow.Ticks);
        public TimeSpan Idle => TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Volatile.Read(ref _lastAccessTicks));
        public void Cancel() { try { _stop.Cancel(); } catch (ObjectDisposedException) { } }
        public bool IsSibling(string otherOutputDir)
            => !string.Equals(OutputDir, otherOutputDir, StringComparison.Ordinal)
               && string.Equals(Path.GetDirectoryName(OutputDir), Path.GetDirectoryName(otherOutputDir), StringComparison.Ordinal);
    }

    /// <summary>Start offsets are part of the output path and segment names: "s12_5" for 12.5 s.</summary>
    private static string HlsStartKey(double startSeconds)
        => "s" + Math.Max(0, startSeconds).ToString("0.###", CultureInfo.InvariantCulture).Replace('.', '_');

    private string HlsOutputDirectory(int videoId, string profile, string startKey)
        => Path.Combine(_config.GeneratedPath ?? Path.GetTempPath(), "transcodes", "hls", videoId.ToString(CultureInfo.InvariantCulture), profile, startKey);

    private void TouchHlsOutput(string outputDir)
    {
        lock (_hlsJobs) { _hlsOutputLastAccess[outputDir] = DateTime.UtcNow; }
    }

    /// <summary>Runs one HLS encode to completion in the background. The job holds a transcode slot
    /// for its whole life, so an encode that no client is consuming is stopped after
    /// <see cref="HlsIdleTimeout"/> (or when a newer offset supersedes it) instead of blocking live
    /// transcodes for the rest of the file. A stopped encode leaves its playlist without ENDLIST; the
    /// next job for the same output appends to it from the encoded position (FFmpeg
    /// <c>append_list</c>), so a playlist handed to a player only ever grows. Like the piped path, a
    /// hardware encoder that fails at runtime is retried with libx264.</summary>
    private async Task RunHlsJobAsync(HlsJob job, string ffmpeg, string inputPath, string? resolution, string profile, string startKey, double startSeconds)
    {
        await Task.Yield();
        var acquired = false;
        try
        {
            await _transcodeSemaphore.WaitAsync(job.StopToken);
            acquired = true;

            RetireSupersededHlsOutputs(job.OutputDir);

            var encoder = GetH264Encoder(ffmpeg);
            var outcome = await RunHlsEncodeAsync(job, ffmpeg, encoder, inputPath, resolution, profile, startKey, startSeconds);
            if (outcome == HlsEncodeOutcome.Failed && encoder != "libx264")
            {
                _logger.LogDebug("HLS encode with {Encoder} failed for {Input}; retrying with libx264.", encoder, inputPath);
                outcome = await RunHlsEncodeAsync(job, ffmpeg, "libx264", inputPath, resolution, profile, startKey, startSeconds);
            }
            job.Failed = outcome == HlsEncodeOutcome.Failed;
        }
        catch (OperationCanceledException) when (job.StopToken.IsCancellationRequested)
        {
            // Superseded while queued for a slot; nothing was started.
        }
        catch (Exception ex)
        {
            job.Failed = true;
            _logger.LogWarning(ex, "HLS generation crashed for {Input}", inputPath);
        }
        finally
        {
            if (acquired) _transcodeSemaphore.Release();
            lock (_hlsJobs)
            {
                if (_hlsJobs.TryGetValue(job.ManifestPath, out var current) && ReferenceEquals(current, job))
                    _hlsJobs.Remove(job.ManifestPath);
            }
        }
    }

    private enum HlsEncodeOutcome { Completed, Stopped, Failed }

    private async Task<HlsEncodeOutcome> RunHlsEncodeAsync(HlsJob job, string ffmpeg, string encoder, string inputPath, string? resolution, string profile, string startKey, double startSeconds)
    {
        // Resume only a playlist that is genuinely unfinished. A finished one that aged out of the
        // cache, or an unreadable one, is encoded again from scratch into an empty directory.
        var encodedSeconds = await HlsResumableSecondsAsync(job.ManifestPath);
        var resume = encodedSeconds > 0;
        if (!resume)
        {
            try { if (Directory.Exists(job.OutputDir)) Directory.Delete(job.OutputDir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Directory.CreateDirectory(job.OutputDir);

        var segmentPath = Path.Combine(job.OutputDir, $"{profile}_{startKey}_%04d.ts");
        // Segments can only split on keyframes, so force one every segment length; NVENC emits
        // plain I-frames for forced keyframes unless told to make them IDR, which the muxer needs.
        string[] keyframeArgs =
        [
            .. encoder == "h264_nvenc" ? ["-forced-idr", "1"] : Array.Empty<string>(),
            "-force_key_frames", string.Create(CultureInfo.InvariantCulture, $"expr:gte(t,n_forced*{HlsSegmentSeconds})"),
        ];
        // append_list continues the numbering and marks the first appended segment as a discontinuity
        // by itself; discont_start must not be added, as it rewrites the playlist header on every run.
        var flags = resume ? "temp_file+independent_segments+append_list" : "temp_file+independent_segments";
        var args = BuildEncodeArgs(ffmpeg, inputPath, resolution, startSeconds + encodedSeconds, encoder,
        [
            "-y", "-nostdin", "-sn", "-dn", .. keyframeArgs,
            "-f", "hls", "-hls_time", HlsSegmentSeconds.ToString(CultureInfo.InvariantCulture), "-hls_list_size", "0",
            "-hls_playlist_type", "event", "-hls_flags", flags, "-hls_segment_filename", segmentPath, job.ManifestPath,
        ]);

        // The handle drains stderr concurrently, so a verbose/long encode can't fill the pipe buffer
        // and deadlock against our exit wait.
        await using var process = FfmpegProcessRunner.Start(ffmpeg, args, redirectStandardOutput: false, CancellationToken.None);
        var exited = process.WaitForExitAsync();
        var stopped = false;
        while (!exited.IsCompleted)
        {
            if (job.StopToken.IsCancellationRequested || job.Idle > HlsIdleTimeout)
            {
                stopped = true;
                process.Kill();
                break;
            }
            await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(1)));
        }
        if (stopped)
        {
            // A resumed encode appends to the same playlist and segment names, so give the killed one a
            // (bounded) chance to exit first, and say so if it would not.
            try
            {
                await exited.WaitAsync(FfmpegProcessRunner.CleanupTimeout);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("HLS encode for {Input} did not exit within {Timeout}s of being stopped.", inputPath, FfmpegProcessRunner.CleanupTimeout.TotalSeconds);
            }
            _logger.LogDebug("HLS encode for {Input} ({Profile} from {Start}s) stopped {Reason}; resumable.", inputPath, profile, startSeconds,
                job.StopToken.IsCancellationRequested ? "because a newer offset superseded it" : $"after {HlsIdleTimeout.TotalSeconds}s idle");
            return HlsEncodeOutcome.Stopped;
        }
        if (process.ExitCode != 0)
        {
            var stderr = await process.ReadStandardErrorTailAsync();
            _logger.LogWarning("HLS generation failed (exit {Code}, encoder {Encoder}, resume {Resume}): {Stderr}",
                process.ExitCode, encoder, resume, stderr[^Math.Min(stderr.Length, 500)..]);
            return HlsEncodeOutcome.Failed;
        }
        return HlsEncodeOutcome.Completed;
    }

    private static async Task<string?> TryReadCompletedHlsManifestAsync(string manifestPath, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(manifestPath)) return null;
            if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(manifestPath)).TotalHours >= 24) return null;
            var manifest = await FileReadRace.TryReadAllTextAsync(manifestPath, ct, pathWasObserved: true);
            return manifest != null && manifest.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal) ? manifest : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Seconds of media in an unfinished playlist (sum of EXTINF), or 0 when there is nothing
    /// to resume from: no playlist, an unparsable one, or one that already ends with ENDLIST.</summary>
    private static async Task<double> HlsResumableSecondsAsync(string manifestPath)
    {
        try
        {
            var lines = await FileReadRace.TryReadAllLinesAsync(manifestPath);
            if (lines == null) return 0;
            var total = 0d;
            var segments = 0;
            foreach (var line in lines)
            {
                if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal)) return 0;
                if (!line.StartsWith("#EXTINF:", StringComparison.Ordinal)) continue;
                var value = line["#EXTINF:".Length..].TrimEnd(',');
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return 0;
                total += seconds;
                segments++;
            }
            return segments > 0 ? total : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>One start offset per profile is kept on disk. Offsets that are neither being encoded
    /// nor recently played are moved aside under the job lock (a cheap rename, so a concurrent job can
    /// not observe a half-deleted directory) and deleted outside it.</summary>
    private void RetireSupersededHlsOutputs(string keepOutputDir)
    {
        var profileDir = Path.GetDirectoryName(keepOutputDir);
        if (profileDir == null || !Directory.Exists(profileDir)) return;

        var retired = new List<string>();
        var cutoff = DateTime.UtcNow - HlsIdleTimeout;
        lock (_hlsJobs)
        {
            foreach (var dir in Directory.EnumerateDirectories(profileDir))
            {
                if (string.Equals(dir, keepOutputDir, StringComparison.Ordinal)) continue;
                if (Path.GetFileName(dir).StartsWith(".retired-", StringComparison.Ordinal)) { retired.Add(dir); continue; }
                if (_hlsJobs.ContainsKey(Path.Combine(dir, HlsManifestFileName))) continue;
                if (_hlsOutputLastAccess.TryGetValue(dir, out var lastAccess) && lastAccess > cutoff) continue;

                var target = Path.Combine(profileDir, $".retired-{Guid.NewGuid():N}");
                try { Directory.Move(dir, target); retired.Add(target); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                _hlsOutputLastAccess.Remove(dir);
            }
        }

        foreach (var dir in retired)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static int CountHlsSegments(string manifest)
    {
        var count = 0;
        foreach (var line in manifest.Split('\n'))
        {
            if (line.StartsWith("#EXTINF", StringComparison.Ordinal)) count++;
        }
        return count;
    }

    /// <summary>
    /// Builds a coherent ffmpeg command for a software-decode → (software scale) → hardware-encode
    /// pipeline. Hardware <b>decode</b> is opt-in only (via LiveTranscodeInputArgs); the previous
    /// design forced GPU surfaces for NVENC decode (<c>-hwaccel_output_format cuda</c>) but left the
    /// scale filter and encoder in software, which cannot read GPU frames and aborted on every
    /// resolution change. Hardware <b>encode</b> (the expensive half) is selected by probe with a
    /// libx264 fallback, so a misconfigured GPU degrades gracefully instead of black-screening.
    /// </summary>
    private IReadOnlyList<string> BuildEncodeArgs(string ffmpeg, string inputPath, string? resolution, double startSeconds, string encoder, IReadOnlyList<string> outputContainerArgs)
    {
        var scaleChain = BuildScaleChain(resolution);
        var videoFilter = FfmpegHwAccel.VideoFilterForEncoder(encoder, scaleChain);

        // Encode args: full user override if provided, else encoder-correct constant-quality args.
        IReadOnlyList<string> encodeArgs = !string.IsNullOrWhiteSpace(_config.FfmpegOutputArgs)
            ? FfmpegArgumentTokenizer.Split(_config.FfmpegOutputArgs)
            : [.. FfmpegHwAccel.VideoEncodeArgs(encoder, 23, "veryfast"), "-c:a", "aac", "-b:a", "128k"];

        string[] seekArgs = startSeconds > 0
            ? ["-ss", Math.Max(0, startSeconds).ToString("0.###", CultureInfo.InvariantCulture)]
            : [];

        // Input/decode args: software by default; add any encoder device setup (e.g. the VAAPI render
        // node) and honor an explicit override.
        return
        [
            .. FfmpegHwAccel.InputArgsForEncoder(encoder),
            .. FfmpegArgumentTokenizer.Split(_config.FfmpegInputArgs),
            .. seekArgs,
            "-i", inputPath,
            .. videoFilter,
            .. encodeArgs,
            .. outputContainerArgs,
        ];
    }

    private static string BuildScaleChain(string? resolution)
    {
        if (resolution != null && ResolutionProfiles.TryGetValue(resolution, out var res))
            return $"scale={res.width}:{res.height}:force_original_aspect_ratio=decrease,scale=trunc(iw/2)*2:trunc(ih/2)*2";
        return string.Empty;
    }

    private static string Tail(string stderr) => stderr.Length > 500 ? stderr[^500..] : stderr;

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
        var ffmpeg = FfmpegExecutableLocator.FindFfmpeg(_config);
        if (ffmpeg == null)
            _logger.LogWarning("FFmpeg not found in PATH or configured path");
        return ffmpeg;
    }
}

/// <summary>
/// Wraps FFmpeg's stdout pipe, prepending a buffer of bytes already read for failure detection,
/// and ensures the FFmpeg process is killed and the transcode semaphore released when the stream
/// is disposed (i.e. after the HTTP response completes). Without the release the semaphore leaks.
/// </summary>
file sealed class PrefixedReleasingStream(byte[] prefix, int prefixLen, FfmpegProcessHandle process, SemaphoreSlim semaphore) : Stream
{
    private int _prefixPos;
    private readonly Stream _inner = process.StandardOutput;
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
        return _inner.Read(buffer, offset, count);
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
        return await _inner.ReadAsync(buffer, ct);
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
            try { _inner.Dispose(); } catch (IOException) { }
            process.Dispose();
            semaphore.Release();
        }
        base.Dispose(disposing);
    }
}
