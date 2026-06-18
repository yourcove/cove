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
    /// <summary>Map a configured hardware-acceleration preference to its H.264 encoder name,
    /// or null when no hardware encoder is pinned.</summary>
    public static string? PinnedH264Encoder(string? hwAccelPref) =>
        hwAccelPref?.Trim().ToLowerInvariant() switch
        {
            "nvenc" => "h264_nvenc",
            "qsv" => "h264_qsv",
            "vaapi" => "h264_vaapi",
            "amf" => "h264_amf",
            _ => null,
        };

    /// <summary>Pick the H.264 encoder for the current configuration. When the user has pinned a
    /// specific hardware acceleration, only that encoder is attempted (falling back to libx264 if
    /// it cannot open a session); otherwise the best available HW encoder is auto-detected. Every
    /// candidate is verified with a real test encode before being chosen.</summary>
    public static string SelectH264Encoder(string ffmpegPath, string? hwAccelPref, ILogger logger)
    {
        try
        {
            var listed = ListEncoders(ffmpegPath);

            // If the user explicitly selected a hardware acceleration, honor that choice rather
            // than silently substituting a different vendor's encoder.
            var pinned = PinnedH264Encoder(hwAccelPref);
            if (pinned != null)
            {
                if (listed.Contains(pinned, StringComparer.OrdinalIgnoreCase)
                    && ProbeEncoder(ffmpegPath, pinned, out _))
                {
                    logger.LogInformation("Using configured HW-accelerated H.264 encoder: {Encoder}", pinned);
                    return pinned;
                }

                logger.LogWarning(
                    "Configured HW encoder {Encoder} is unavailable on this system; falling back to libx264", pinned);
                return "libx264";
            }

            // Auto-detect: prefer NVENC > QSV > AMF > VideoToolbox. Verify each candidate with an
            // actual test encode; presence in the encoder list does not guarantee the runtime can
            // open a session (e.g. NVENC client-key mismatches with the installed driver).
            string[] hwEncoders = ["h264_nvenc", "h264_qsv", "h264_amf", "h264_videotoolbox"];
            foreach (var enc in hwEncoders)
            {
                if (!listed.Contains(enc, StringComparer.OrdinalIgnoreCase)) continue;
                if (!ProbeEncoder(ffmpegPath, enc, out var probeError))
                {
                    logger.LogDebug("Skipping {Encoder}: probe failed ({Error})", enc, probeError);
                    continue;
                }

                logger.LogInformation("Using HW-accelerated H.264 encoder: {Encoder}", enc);
                return enc;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to detect HW encoders, falling back to libx264");
        }

        logger.LogInformation("Using software H.264 encoder: libx264");
        return "libx264";
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

    public static IReadOnlyList<string> ListEncoders(string ffmpegPath)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -encoders",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
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
                Arguments = $"-hide_banner -v error -f lavfi -i color=size=64x64:rate=1:duration=0.1 -c:v {encoder} -frames:v 1 -f null -",
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
