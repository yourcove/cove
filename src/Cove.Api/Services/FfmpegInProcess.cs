using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Cove.Api.Services;

/// <summary>
/// In-process FFmpeg frame extraction using FFmpeg.AutoGen.
/// Opens the video file once and seeks to multiple timestamps, avoiding
/// the overhead of spawning one FFmpeg process per frame.
/// Automatically uses hardware-accelerated decoding when available (7-9x faster),
/// falling back to CPU software decode if no hwaccel is available.
/// Thread-safe: each call creates its own decoder context.
/// </summary>
public static unsafe class FfmpegInProcess
{
    private static bool _initialized;
    private static readonly object InitLock = new();
    private static readonly AVIOInterruptCB_callback InterruptCallback = OnInterrupt;
    private static string? _lastAttemptedFfmpegPath;

    /// <summary>
    /// True if in-process FFmpeg bindings initialized and verified successfully.
    /// False means the installed FFmpeg libraries are incompatible with FFmpeg.AutoGen;
    /// callers should fall back to spawning the ffmpeg process.
    /// </summary>
    public static bool IsAvailable { get; private set; }

    // Preferred hwaccel order (best avg speedup first from benchmarks).
    // Probed once at init; null entries are removed if they fail to initialize.
    private static AVHWDeviceType[]? _availableHwAccels;
    private static readonly AVHWDeviceType[] HwAccelProbeOrder =
    [
        AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2,     // Windows (7.3x avg)
        AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA,       // NVIDIA (7.2x avg)
        AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,    // Windows (6.6x avg)
        AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN,     // Cross-platform (6.4x avg)
        AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI,      // Linux
        AVHWDeviceType.AV_HWDEVICE_TYPE_QSV,        // Intel
    ];

    private sealed class InterruptState(CancellationToken cancellationToken)
    {
        public CancellationToken CancellationToken { get; } = cancellationToken;

        public bool ShouldAbort()
            => CancellationToken.IsCancellationRequested;
    }

    /// <summary>
    /// Initializes FFmpeg.AutoGen bindings by locating the FFmpeg shared libraries.
    /// Uses the directory containing the configured ffmpeg binary, or falls back to PATH.
    /// Safe to call multiple times (idempotent).
    /// </summary>
    public static void EnsureInitialized(string? ffmpegPath, bool enableHwAccel = false)
    {
        if (_initialized && (IsAvailable || string.Equals(_lastAttemptedFfmpegPath, ffmpegPath, StringComparison.OrdinalIgnoreCase)))
            return;

        lock (InitLock)
        {
            if (_initialized && (IsAvailable || string.Equals(_lastAttemptedFfmpegPath, ffmpegPath, StringComparison.OrdinalIgnoreCase)))
                return;

            _lastAttemptedFfmpegPath = ffmpegPath;
            Exception? lastError = null;

            foreach (var libraryPath in GetLibraryPathCandidates(ffmpegPath))
            {
                try
                {
                    DynamicallyLoadedBindings.LibrariesPath = libraryPath ?? string.Empty;
                    Console.WriteLine(string.IsNullOrEmpty(libraryPath)
                        ? "[FfmpegInProcess] Trying shared libraries from the default runtime loader paths"
                        : $"[FfmpegInProcess] Trying shared libraries from: {libraryPath}");

                    Console.WriteLine("[FfmpegInProcess] Calling DynamicallyLoadedBindings.Initialize()...");
                    DynamicallyLoadedBindings.Initialize();
                    Console.WriteLine("[FfmpegInProcess] Bindings initialized successfully.");

                    var majorVer = (int)(ffmpeg.avformat_version() >> 16);

                    if (enableHwAccel)
                    {
                        Console.WriteLine("[FfmpegInProcess] Probing hwaccels...");
                        _availableHwAccels = ProbeHwAccels();
                        Console.WriteLine($"[FfmpegInProcess] In-process FFmpeg ready (libavformat major={majorVer}, hwAccels={string.Join(",", _availableHwAccels)})");
                    }
                    else
                    {
                        _availableHwAccels = [];
                        Console.WriteLine($"[FfmpegInProcess] In-process FFmpeg ready (libavformat major={majorVer}, Hardware Acceleration: Disabled)");
                    }

                    IsAvailable = true;
                    _initialized = true;
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    IsAvailable = false;
                    Console.WriteLine("[FfmpegInProcess] In-process FFmpeg initialization attempt failed.");
                    Console.WriteLine($"[FfmpegInProcess] Exception: {ex.GetType().Name}: {ex.Message}");
                    if (ex.InnerException != null)
                        Console.WriteLine($"[FfmpegInProcess] Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                }
            }

            _initialized = true;
            if (lastError != null)
            {
                Console.WriteLine("[FfmpegInProcess] In-process FFmpeg initialization failed for all candidate library paths.");
                Console.WriteLine($"[FfmpegInProcess] StackTrace: {lastError.StackTrace}");
            }
        }
    }

    private static IEnumerable<string?> GetLibraryPathCandidates(string? ffmpegPath)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        static bool AddCandidate(HashSet<string> seenSet, List<string?> candidates, string? candidate)
        {
            var normalized = candidate ?? string.Empty;
            if (!seenSet.Add(normalized))
                return false;

            candidates.Add(candidate);
            return true;
        }

        var candidates = new List<string?>();
        var binaryDir = !string.IsNullOrWhiteSpace(ffmpegPath) ? Path.GetDirectoryName(ffmpegPath) : null;

        if (HasCompanionLibraries(binaryDir))
            AddCandidate(seen, candidates, binaryDir);

        if (!string.IsNullOrWhiteSpace(binaryDir))
        {
            var parentDir = Directory.GetParent(binaryDir)?.FullName;
            if (!string.IsNullOrWhiteSpace(parentDir))
            {
                var siblingLibDir = Path.Combine(parentDir, "lib");
                if (HasCompanionLibraries(siblingLibDir))
                    AddCandidate(seen, candidates, siblingLibDir);
            }
        }

        foreach (var commonDir in GetCommonLibraryDirectories())
        {
            if (HasCompanionLibraries(commonDir))
                AddCandidate(seen, candidates, commonDir);
        }

        if (!OperatingSystem.IsWindows())
            AddCandidate(seen, candidates, null);

        if (!string.IsNullOrWhiteSpace(binaryDir))
            AddCandidate(seen, candidates, binaryDir);

        return candidates;
    }

    private static IEnumerable<string> GetCommonLibraryDirectories()
    {
        if (OperatingSystem.IsMacOS())
        {
            return
            [
                "/opt/homebrew/lib",
                "/usr/local/lib",
                "/usr/lib",
            ];
        }

        if (!OperatingSystem.IsWindows())
        {
            return
            [
                "/usr/local/lib",
                "/usr/lib",
                "/usr/lib64",
                "/lib",
                "/lib64",
                "/usr/lib/x86_64-linux-gnu",
                "/usr/lib/aarch64-linux-gnu",
                "/lib/x86_64-linux-gnu",
                "/lib/aarch64-linux-gnu",
            ];
        }

        return [];
    }

    private static bool HasCompanionLibraries(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return false;

        var patterns = OperatingSystem.IsWindows()
            ? new[] { "avcodec-*.dll", "avformat-*.dll", "avutil-*.dll", "swscale-*.dll" }
            : OperatingSystem.IsMacOS()
                ? new[] { "libavcodec*.dylib", "libavformat*.dylib", "libavutil*.dylib", "libswscale*.dylib" }
                : new[] { "libavcodec.so*", "libavformat.so*", "libavutil.so*", "libswscale.so*" };

        return patterns.All(pattern => Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Any());
    }

    /// <summary>
    /// Tests each hwaccel device type and returns the ones that successfully initialize.
    /// </summary>
    private static unsafe AVHWDeviceType[] ProbeHwAccels()
    {
        var available = new List<AVHWDeviceType>();
        foreach (var dt in HwAccelProbeOrder)
        {
            try
            {
                AVBufferRef* ctx = null;
                if (ffmpeg.av_hwdevice_ctx_create(&ctx, dt, null, null, 0) == 0)
                {
                    available.Add(dt);
                    ffmpeg.av_buffer_unref(&ctx);
                }
            }
            catch (NotSupportedException)
            {
                // Some platforms/runtimes do not support certain hwdevice APIs.
            }
            catch (EntryPointNotFoundException)
            {
                // Function not available in the loaded FFmpeg build.
            }
            catch (DllNotFoundException)
            {
                // Partial/shared-library loading issue; continue with CPU fallback.
            }
        }
        return available.ToArray();
    }

    /// <summary>
    /// Extracts frames from a video at the given timestamps, scaled to the specified width.
    /// Opens the file once, seeks to each timestamp in order, and decodes one frame per seek.
    /// Automatically uses hardware-accelerated decoding if available, falling back to CPU.
    /// </summary>
    /// <param name="videoPath">Path to the video file.</param>
    /// <param name="timestamps">Sorted array of timestamps in seconds to extract frames at.</param>
    /// <param name="scaleWidth">Target width for scaling (height is computed preserving aspect ratio, rounded to even). Values less than or equal to 0 preserve the native frame size.</param>
    /// <param name="threadCount">FFmpeg decoder thread count (1 = single-threaded, 0 = auto). Used for CPU fallback only.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Array of extracted frames, or null if extraction failed.</returns>
    public static unsafe Image<Rgba32>[]? ExtractFrames(
        string videoPath, double[] timestamps, int scaleWidth, int threadCount, CancellationToken ct = default)
    {
        // Try each available hwaccel first, then fall back to CPU
        var hwAccels = _availableHwAccels ?? [];
        foreach (var hwType in hwAccels)
        {
            var result = ExtractFramesCore(videoPath, timestamps, scaleWidth, threadCount, hwType, ct);
            if (result != null) return result;
        }

        // CPU fallback
        return ExtractFramesCore(videoPath, timestamps, scaleWidth, threadCount, null, ct);
    }

    private static unsafe int OnInterrupt(void* opaque)
    {
        if (opaque == null)
            return 0;

        try
        {
            var handle = GCHandle.FromIntPtr((IntPtr)opaque);
            return handle.Target is InterruptState state && state.ShouldAbort() ? 1 : 0;
        }
        catch
        {
            return 1;
        }
    }

    private static unsafe Image<Rgba32>[]? ExtractFramesCore(
        string videoPath, double[] timestamps, int scaleWidth, int threadCount,
        AVHWDeviceType? hwDeviceType, CancellationToken ct)
    {
        AVFormatContext* pFormatCtx = null;
        AVCodecContext* pCodecCtx = null;
        AVFrame* pFrame = null;
        AVFrame* pSwFrame = null;
        AVPacket* pPacket = null;
        AVBufferRef* hwDeviceCtx = null;
        GCHandle interruptHandle = default;

        var frames = new Image<Rgba32>[timestamps.Length];
        var extracted = 0;

        try
        {
            var interruptState = new InterruptState(ct);
            interruptHandle = GCHandle.Alloc(interruptState);

            // Init HW device if requested
            if (hwDeviceType.HasValue)
            {
                if (ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, hwDeviceType.Value, null, null, 0) < 0)
                    return null;
            }

            pFormatCtx = ffmpeg.avformat_alloc_context();
            if (pFormatCtx == null) return null;

            pFormatCtx->interrupt_callback = new AVIOInterruptCB
            {
                callback = InterruptCallback,
                opaque = (void*)GCHandle.ToIntPtr(interruptHandle),
            };

            var pFmtCtx = pFormatCtx;
            if (ffmpeg.avformat_open_input(&pFmtCtx, videoPath, null, null) != 0) return null;
            pFormatCtx = pFmtCtx;
            if (ffmpeg.avformat_find_stream_info(pFormatCtx, null) < 0) return null;

            var videoStreamIdx = -1;
            for (var s = 0; s < pFormatCtx->nb_streams; s++)
            {
                if (pFormatCtx->streams[s]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                { videoStreamIdx = s; break; }
            }
            if (videoStreamIdx < 0) return null;

            var pStream = pFormatCtx->streams[videoStreamIdx];
            var timeBase = pStream->time_base;
            if (timeBase.num <= 0 || timeBase.den <= 0)
                return null;

            var pCodec = ffmpeg.avcodec_find_decoder(pStream->codecpar->codec_id);
            if (pCodec == null) return null;
            pCodecCtx = ffmpeg.avcodec_alloc_context3(pCodec);
            ffmpeg.avcodec_parameters_to_context(pCodecCtx, pStream->codecpar);
            if (pCodecCtx == null) return null;

            if (hwDeviceCtx != null)
                pCodecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(hwDeviceCtx);
            else
                pCodecCtx->thread_count = threadCount;

            if (ffmpeg.avcodec_open2(pCodecCtx, pCodec, null) < 0) return null;

            pFrame = ffmpeg.av_frame_alloc();
            pSwFrame = ffmpeg.av_frame_alloc();
            pPacket = ffmpeg.av_packet_alloc();
            if (pFrame == null || pSwFrame == null || pPacket == null) return null;

            SwsContext* pSwsCtx = null;
            var lastSrcW = 0;
            var lastSrcH = 0;
            var lastSrcFmt = AVPixelFormat.AV_PIX_FMT_NONE;

            for (var i = 0; i < timestamps.Length; i++)
            {
                ct.ThrowIfCancellationRequested();

                var seconds = Math.Max(0, timestamps[i]);
                var targetTs = (long)Math.Round(seconds * timeBase.den / (double)timeBase.num);

                if (ffmpeg.av_seek_frame(pFormatCtx, videoStreamIdx, targetTs, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0)
                    return null;
                ffmpeg.avcodec_flush_buffers(pCodecCtx);
                ffmpeg.av_packet_unref(pPacket);
                ffmpeg.av_frame_unref(pFrame);
                ffmpeg.av_frame_unref(pSwFrame);

                var gotFrame = false;
                while (ffmpeg.av_read_frame(pFormatCtx, pPacket) >= 0)
                {
                    if (pPacket->stream_index != videoStreamIdx)
                    { ffmpeg.av_packet_unref(pPacket); continue; }
                    if (ffmpeg.avcodec_send_packet(pCodecCtx, pPacket) < 0)
                    { ffmpeg.av_packet_unref(pPacket); continue; }
                    while (true)
                    {
                        var receiveResult = ffmpeg.avcodec_receive_frame(pCodecCtx, pFrame);
                        if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveResult == ffmpeg.AVERROR_EOF)
                            break;
                        if (receiveResult < 0)
                        {
                            ffmpeg.av_frame_unref(pFrame);
                            return null;
                        }

                        var framePts = pFrame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
                            ? pFrame->best_effort_timestamp
                            : pFrame->pts;
                        if (framePts == ffmpeg.AV_NOPTS_VALUE || framePts >= targetTs || framePts < 0)
                        {
                            gotFrame = true;
                            break;
                        }

                        ffmpeg.av_frame_unref(pFrame);
                    }
                    ffmpeg.av_packet_unref(pPacket);
                    if (gotFrame) break;
                }
                if (!gotFrame) return null;

                // If HW frame, transfer to CPU memory
                AVFrame* srcFrame;
                if (pFrame->hw_frames_ctx != null)
                {
                    ffmpeg.av_frame_unref(pSwFrame);
                    if (ffmpeg.av_hwframe_transfer_data(pSwFrame, pFrame, 0) < 0) return null;
                    srcFrame = pSwFrame;
                }
                else
                {
                    srcFrame = pFrame;
                }

                var srcW = srcFrame->width;
                var srcH = srcFrame->height;
                var srcFmt = (AVPixelFormat)srcFrame->format;
                if (srcW <= 0 || srcH <= 0)
                    return null;
                var dstW = scaleWidth > 0 ? scaleWidth : srcW;
                var dstH = scaleWidth > 0
                    ? (int)Math.Round((double)srcH * dstW / srcW)
                    : srcH;
                if (scaleWidth > 0 && dstH % 2 != 0) dstH++;

                if (pSwsCtx == null || srcW != lastSrcW || srcH != lastSrcH || srcFmt != lastSrcFmt)
                {
                    if (pSwsCtx != null) ffmpeg.sws_freeContext(pSwsCtx);
                    pSwsCtx = ffmpeg.sws_getContext(srcW, srcH, srcFmt,
                        dstW, dstH, AVPixelFormat.AV_PIX_FMT_RGBA,
                        (int)SwsFlags.SWS_BILINEAR, null, null, null);
                    lastSrcW = srcW; lastSrcH = srcH; lastSrcFmt = srcFmt;
                }

                var rgbaSize = dstW * dstH * 4;
                var rgbaBuffer = (byte*)ffmpeg.av_malloc((ulong)rgbaSize);
                try
                {
                    var dstData = new byte*[] { rgbaBuffer, null, null, null };
                    var dstLinesize = new int[] { dstW * 4, 0, 0, 0 };
                    ffmpeg.sws_scale(pSwsCtx, srcFrame->data, srcFrame->linesize, 0, srcH, dstData, dstLinesize);

                    var pixels = new byte[rgbaSize];
                    Marshal.Copy((IntPtr)rgbaBuffer, pixels, 0, rgbaSize);
                    frames[i] = Image.LoadPixelData<Rgba32>(pixels, dstW, dstH);
                    extracted++;
                }
                finally
                {
                    ffmpeg.av_free(rgbaBuffer);
                }

                ffmpeg.av_frame_unref(pFrame);
                ffmpeg.av_frame_unref(pSwFrame);
            }

            if (pSwsCtx != null) ffmpeg.sws_freeContext(pSwsCtx);
            return extracted == timestamps.Length ? frames : null;
        }
        finally
        {
            if (extracted < timestamps.Length)
                foreach (var f in frames) f?.Dispose();
            if (pPacket != null) ffmpeg.av_packet_free(&pPacket);
            if (pSwFrame != null) ffmpeg.av_frame_free(&pSwFrame);
            if (pFrame != null) ffmpeg.av_frame_free(&pFrame);
            if (pCodecCtx != null) ffmpeg.avcodec_free_context(&pCodecCtx);
            if (pFormatCtx != null) ffmpeg.avformat_close_input(&pFormatCtx);
            if (hwDeviceCtx != null) ffmpeg.av_buffer_unref(&hwDeviceCtx);
            if (interruptHandle.IsAllocated) interruptHandle.Free();
        }
    }
}
