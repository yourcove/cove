using Cove.Core.Interfaces;

namespace Cove.Api.Services;

/// <summary>
/// The hardware-acceleration capabilities of the host's ffmpeg, as the settings UI and the encode/decode
/// paths should see them. <see cref="Accelerators"/> are the vendor accelerators whose H.264 encoder is
/// both built into ffmpeg AND verified by a real test-encode — i.e. options that will actually work, so
/// the UI never offers a dead choice. <see cref="Decoders"/> is the informational `ffmpeg -hwaccels` list.
/// </summary>
public record FfmpegCapabilities(
    bool FfmpegFound,
    string? FfmpegPath,
    IReadOnlyList<string> Accelerators,
    IReadOnlyList<string> Decoders,
    DateTime ProbedAtUtc);

public interface IFfmpegCapabilities
{
    /// <summary>Returns the cached capabilities, probing once per ffmpeg binary. Pass refresh=true to
    /// force a re-probe (e.g. after the user changes the ffmpeg path).</summary>
    FfmpegCapabilities Get(bool refresh = false);
}

/// <summary>
/// Probes and caches the host ffmpeg's hardware capabilities. Detection is real (test-encodes), so it is
/// done at most once per ffmpeg binary and reused. Registered as a singleton so the probe cost is paid
/// once for the whole process rather than per request.
/// </summary>
public class FfmpegCapabilitiesService(CoveConfiguration config, ILogger<FfmpegCapabilitiesService> logger) : IFfmpegCapabilities
{
    private readonly object _lock = new();
    private FfmpegCapabilities? _cached;
    private string? _cachedForPath;

    public FfmpegCapabilities Get(bool refresh = false)
    {
        var ffmpeg = FfmpegHwAccel.FindFfmpeg(config.FfmpegPath);
        lock (_lock)
        {
            if (!refresh && _cached != null && _cachedForPath == ffmpeg)
                return _cached;

            _cached = Probe(ffmpeg);
            _cachedForPath = ffmpeg;
            return _cached;
        }
    }

    private FfmpegCapabilities Probe(string? ffmpeg)
    {
        if (ffmpeg == null)
        {
            logger.LogWarning("FFmpeg not found; hardware acceleration is unavailable.");
            return new FfmpegCapabilities(false, null, [], [], DateTime.UtcNow);
        }

        var accelerators = FfmpegHwAccel.DetectAvailableAccelerators(ffmpeg, logger);
        var decoders = FfmpegHwAccel.ListHwaccelDecoders(ffmpeg);
        logger.LogInformation(
            "FFmpeg hardware capabilities probed: encoders=[{Encoders}], hwaccel-decoders=[{Decoders}]",
            string.Join(", ", accelerators), string.Join(", ", decoders));
        return new FfmpegCapabilities(true, ffmpeg, accelerators, decoders, DateTime.UtcNow);
    }
}
