using Microsoft.Extensions.Logging;

namespace Cove.Api.Services;

/// <summary>
/// Shared FFmpeg encoder-probing and hardware-acceleration helpers used by both the live
/// transcode path (<see cref="TranscodeService"/>) and the generate/preview path
/// (<see cref="ThumbnailService"/>).
///
/// Guiding principle is resilience. Hardware <b>encoding</b> is the expensive win and is always
/// verified with a real test-encode before use — presence in <c>-encoders</c> does not guarantee
/// the runtime can open a session (e.g. an NVENC driver/SDK mismatch). If the pinned encoder
/// cannot open we fall back to libx264 rather than producing a dead stream. Hardware <b>decoding</b>
/// is left opt-in (via the *InputArgs config) because forcing GPU surfaces requires every
/// downstream filter and the encoder to be GPU-aware; mixing a GPU decode (e.g.
/// <c>-hwaccel_output_format cuda</c>) with a software <c>scale</c> filter or libx264 is the classic
/// "Impossible to convert between the formats" failure.
/// </summary>
internal static class FfmpegHwAccel
{
    /// <summary>The hardware accelerators Cove understands, paired with their H.264 encoder, in
    /// auto-detect preference order. This single list is the source of truth for: the values the
    /// settings UI can offer, capability detection, pinned-encoder lookup, and auto-detection.</summary>
    public static readonly IReadOnlyList<(string Accelerator, string Encoder)> HardwareEncoders =
    [
        ("nvenc", "h264_nvenc"),
        ("qsv", "h264_qsv"),
        ("vaapi", "h264_vaapi"),
        ("amf", "h264_amf"),
        ("videotoolbox", "h264_videotoolbox"),
    ];

    /// <summary>Map a pinned accelerator name to its H.264 encoder, or null when not a specific
    /// hardware accelerator (i.e. "off", "auto", "none", empty, or unknown).</summary>
    public static string? PinnedH264Encoder(string? hwAccelPref)
    {
        var pref = hwAccelPref?.Trim().ToLowerInvariant();
        foreach (var (accel, encoder) in HardwareEncoders)
            if (accel == pref) return encoder;
        return null;
    }

    /// <summary>True when the policy means "use no hardware acceleration at all".</summary>
    public static bool IsHardwareAccelerationOff(string? hwAccelPref) =>
        string.Equals(hwAccelPref?.Trim(), "off", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pick the H.264 encoder for the current configuration. "off" forces libx264; a pinned
    /// accelerator is honored (falling back to libx264 if it cannot open a session); "auto"/"none"/empty
    /// auto-detects the best available. Every candidate is verified with a real test encode first.</summary>
    public static string SelectH264Encoder(string ffmpegPath, string? hwAccelPref, ILogger logger)
    {
        if (IsHardwareAccelerationOff(hwAccelPref))
        {
            logger.LogInformation("Hardware acceleration is off; using software H.264 encoder: libx264");
            return "libx264";
        }

        try
        {
            var listed = ListEncoders(ffmpegPath);

            // If the user pinned a specific accelerator, honor that choice rather than silently
            // substituting a different vendor's encoder.
            var pinned = PinnedH264Encoder(hwAccelPref);
            if (pinned != null)
            {
                if (!listed.Contains(pinned, StringComparer.OrdinalIgnoreCase))
                {
                    logger.LogWarning(
                        "Configured HW encoder {Encoder} is not built into this ffmpeg ({FfmpegPath}); falling back to libx264.",
                        pinned, ffmpegPath);
                    return "libx264";
                }

                if (!ProbeEncoder(ffmpegPath, pinned, out var pinnedError))
                {
                    logger.LogWarning(
                        "Configured HW encoder {Encoder} is present but a test encode failed ({Error}); falling back to libx264.",
                        pinned, pinnedError);
                    return "libx264";
                }

                logger.LogInformation("Using configured HW-accelerated H.264 encoder: {Encoder}", pinned);
                return pinned;
            }

            // Auto-detect in preference order. Verify each candidate with an actual test encode;
            // presence in the encoder list does not guarantee the runtime can open a session (e.g.
            // an NVENC driver/library mismatch).
            foreach (var (accel, enc) in HardwareEncoders)
            {
                if (!listed.Contains(enc, StringComparer.OrdinalIgnoreCase)) continue;
                if (!ProbeEncoder(ffmpegPath, enc, out var probeError))
                {
                    logger.LogTrace("Skipping {Encoder} ({Accel}): probe failed ({Error})", enc, accel, probeError);
                    continue;
                }

                logger.LogInformation("Auto-selected HW-accelerated H.264 encoder: {Encoder} ({Accel})", enc, accel);
                return enc;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to detect HW encoders, falling back to libx264");
        }

        logger.LogInformation("No usable hardware encoder; using software H.264 encoder: libx264");
        return "libx264";
    }

    /// <summary>Returns the accelerator names (nvenc/qsv/vaapi/amf/videotoolbox) whose H.264 encoder is
    /// both built into this ffmpeg AND passes a real test-encode. This is exactly what the settings UI
    /// should offer the user — never a vendor option their hardware/build can't actually run.</summary>
    public static IReadOnlyList<string> DetectAvailableAccelerators(string ffmpegPath, ILogger logger)
    {
        var available = new List<string>();
        try
        {
            var listed = ListEncoders(ffmpegPath);
            foreach (var (accel, encoder) in HardwareEncoders)
            {
                if (!listed.Contains(encoder, StringComparer.OrdinalIgnoreCase)) continue;
                if (ProbeEncoder(ffmpegPath, encoder, out var err))
                    available.Add(accel);
                else
                    logger.LogTrace("HW accelerator {Accel} ({Encoder}) present but test-encode failed: {Error}", accel, encoder, err);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to detect available hardware encoders");
        }
        return available;
    }

    /// <summary>Lists the hardware decode methods this ffmpeg build advertises via `ffmpeg -hwaccels`
    /// (cuda, vaapi, qsv, dxva2, d3d11va, vulkan, videotoolbox, …). Informational — not test-verified.</summary>
    public static IReadOnlyList<string> ListHwaccelDecoders(string ffmpegPath)
    {
        try
        {
            var output = RunFfmpegInfoQuery(ffmpegPath, "-hide_banner -hwaccels");

            // Output is a header line "Hardware acceleration methods:" followed by one method per line.
            return output
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.EndsWith(':') && !l.Contains(' '))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Build the <c>-c:v ...</c> video-encode arguments for the chosen encoder at a given
    /// quality. The hardware encoders ignore libx264's <c>-crf</c>/<c>-preset</c> vocabulary — worse,
    /// a libx264 preset name like <c>veryfast</c> is an <i>invalid</i> NVENC preset that aborts the
    /// encode — so the correct constant-quality knob is selected per encoder family. <paramref name="quality"/>
    /// follows the libx264 CRF convention (lower = better) and is mapped to each encoder's scale.</summary>
    public static string VideoEncodeArgs(string encoder, int quality, string softwarePreset)
    {
        return encoder switch
        {
            // Constant-quality VBR with no bitrate cap. No explicit preset: the libx264 preset
            // vocabulary is not valid here, and NVENC's default preset is a sane medium.
            "h264_nvenc" => $"-c:v h264_nvenc -rc vbr -cq {quality} -b:v 0",
            "h264_qsv" => $"-c:v h264_qsv -global_quality {quality}",
            "h264_amf" => $"-c:v h264_amf -rc cqp -qp_i {quality} -qp_p {quality} -qp_b {quality}",
            // VAAPI encodes from GPU surfaces, so the caller must also upload frames
            // (see VideoFilterForEncoder) and set a -vaapi_device on the input.
            "h264_vaapi" => $"-c:v h264_vaapi -rc_mode CQP -qp {quality}",
            "h264_videotoolbox" => $"-c:v h264_videotoolbox -q:v {Math.Clamp(65 - quality, 1, 100)}",
            _ => $"-c:v libx264 -preset {softwarePreset} -crf {quality}",
        };
    }

    /// <summary>Returns extra input-side arguments required by the chosen encoder (e.g. the VAAPI
    /// device), or an empty string. Decoding stays in software by default; only the encoder's own
    /// device setup is added here.</summary>
    public static string InputArgsForEncoder(string encoder) =>
        encoder == "h264_vaapi" ? "-vaapi_device /dev/dri/renderD128" : string.Empty;

    /// <summary>Builds the <c>-vf</c> argument for a software-decoded → hardware-encoded pipeline.
    /// <paramref name="scaleChain"/> is the software filter chain (may be empty). VAAPI requires the
    /// frames to be uploaded to a GPU surface before the encoder can consume them; the other HW
    /// encoders accept system-memory frames directly.</summary>
    public static string VideoFilterForEncoder(string encoder, string scaleChain)
    {
        var chain = scaleChain;
        if (encoder == "h264_vaapi")
            chain = string.IsNullOrEmpty(chain) ? "format=nv12,hwupload" : $"{chain},format=nv12,hwupload";

        return string.IsNullOrEmpty(chain) ? string.Empty : $"-vf \"{chain}\"";
    }

    /// <summary>Resolve the ffmpeg executable: the configured path if it exists, otherwise search PATH.
    /// Centralizes the lookup that several services otherwise duplicate.</summary>
    public static string? FindFfmpeg(string? configuredPath)
    {
        if (!string.IsNullOrEmpty(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Runs a quick ffmpeg build-info query (e.g. <c>-encoders</c>/<c>-hwaccels</c>) and returns
    /// stdout. The process is always disposed and is killed if it overruns the timeout, so a stuck child
    /// is never orphaned (matching <see cref="ProbeEncoder"/>'s cleanup).</summary>
    private static string RunFfmpegInfoQuery(string ffmpegPath, string arguments, int timeoutMs = 5000)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
        return output;
    }

    /// <summary>Returns the set of encoder NAMES available in this ffmpeg build.</summary>
    public static IReadOnlyCollection<string> ListEncoders(string ffmpegPath)
    {
        var output = RunFfmpegInfoQuery(ffmpegPath, "-hide_banner -encoders");

        // `ffmpeg -encoders` prints one encoder per row as: " V....D h264_nvenc   NVIDIA NVENC H.264 encoder".
        // The encoder NAME is the second whitespace-delimited token on rows whose first token is the
        // 6-character capability-flags column. Parse the names out so callers can do an exact-name
        // membership test. (The earlier code kept whole lines and checked Contains(name), which never
        // matched a bare name — so every hardware encoder looked "unavailable" and silently fell back
        // to libx264 even when it worked.)
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length == 6 && parts[1] != "=")
                names.Add(parts[1]);
        }
        return names;
    }

    /// <summary>Verify an encoder can actually open a session by encoding a single synthetic frame.</summary>
    public static bool ProbeEncoder(string ffmpegPath, string encoder, out string error)
    {
        error = string.Empty;
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpegPath,
                // 256x256, not 64x64: some NVENC generations reject very small frames, which would make a
                // perfectly working encoder fail the probe. This size is comfortably above all encoders' minimums.
                Arguments = $"-hide_banner -v error -f lavfi -i color=size=256x256:rate=1:duration=0.1 -c:v {encoder} -frames:v 1 -f null -",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        try
        {
            process.Start();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(10000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                error = "timed out";
                return false;
            }
            if (process.ExitCode == 0)
                return true;
            error = stderr.Length > 200 ? stderr[..200] : stderr;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
