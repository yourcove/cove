using System.ComponentModel;
using Cove.Core.Interfaces;

namespace Cove.Api.Services;

public enum MediaProbeStatus
{
    Success,
    Invalid,
    Unavailable,
    TimedOut,
    Failed,
}

public sealed record MediaProbeResult(MediaProbeStatus Status, string? Json, string? Reason)
{
    public static MediaProbeResult Succeeded(string json) => new(MediaProbeStatus.Success, json, null);
    public static MediaProbeResult Rejected(string reason) => new(MediaProbeStatus.Invalid, null, reason);
    public static MediaProbeResult ToolUnavailable(string reason) => new(MediaProbeStatus.Unavailable, null, reason);
    public static MediaProbeResult Timeout(string reason) => new(MediaProbeStatus.TimedOut, null, reason);
    public static MediaProbeResult Failure(string reason) => new(MediaProbeStatus.Failed, null, reason);
}

public interface IMediaProbeService
{
    Task<MediaProbeResult> ProbeAsync(string path, CancellationToken ct = default);
}

/// <summary>
/// Runs a bounded ffprobe metadata read. Completeness is established by the scanner's quiet-period,
/// before/after stat checks, and cheap container-specific checks; reading every packet here would add
/// another full-library I/O pass and make large scans impractical.
/// </summary>
public sealed class FfprobeMediaProbeService : IMediaProbeService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly CoveConfiguration _config;
    private readonly ILogger<FfprobeMediaProbeService> _logger;
    private readonly TimeSpan _timeout;
    private readonly object _resolveLock = new();
    private string? _cachedFfprobePath;
    private bool _ffprobeResolved;

    public FfprobeMediaProbeService(
        CoveConfiguration config,
        ILogger<FfprobeMediaProbeService> logger)
        : this(config, logger, DefaultTimeout)
    {
    }

    internal FfprobeMediaProbeService(
        CoveConfiguration config,
        ILogger<FfprobeMediaProbeService> logger,
        TimeSpan timeout)
    {
        _config = config;
        _logger = logger;
        _timeout = timeout;
    }

    public async Task<MediaProbeResult> ProbeAsync(string path, CancellationToken ct = default)
    {
        var ffprobePath = FindFfprobe();
        if (ffprobePath == null)
            return MediaProbeResult.ToolUnavailable("FFprobe is unavailable");

        FfmpegProcessResult result;
        try
        {
            result = await FfmpegProcessRunner.RunAsync(ffprobePath,
            [
                "-v", "error", "-print_format", "json", "-show_error", "-show_format", "-show_streams",
                // Cove does not import embedded chapters. Asking the MOV demuxer to inspect them can turn a
                // dangling chapter-track reference into stderr that rejects otherwise valid audio metadata.
                "-ignore_chapters", "1",
                path,
            ], _timeout, ct);
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
        {
            _logger.LogWarning(ex, "Unable to start FFprobe at {FfprobePath}", ffprobePath);
            return MediaProbeResult.ToolUnavailable("FFprobe could not be started");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "FFprobe process failed for {Path}", path);
            return MediaProbeResult.Failure(ex.Message);
        }

        if (result.TimedOut)
            return MediaProbeResult.Timeout($"FFprobe exceeded the {_timeout.TotalSeconds:N0}-second timeout");

        var json = result.StandardOutput;
        var error = result.StandardError;
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
            return MediaProbeResult.Rejected(CondenseFailure(error));

        // FFprobe can report a MOV sample-count mismatch while still returning usable metadata for
        // a playable file. Keep rejecting every other structural or demuxing error.
        if (!string.IsNullOrWhiteSpace(error) && !ContainsOnlyWrongSampleCountDiagnostics(error))
            return MediaProbeResult.Rejected(CondenseFailure(error));

        return MediaProbeResult.Succeeded(json);
    }

    private static bool ContainsOnlyWrongSampleCountDiagnostics(string error)
    {
        var lines = error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 && lines.All(line => line.EndsWith("] wrong sample count", StringComparison.Ordinal));
    }

    private string? FindFfprobe()
    {
        if (_ffprobeResolved)
            return _cachedFfprobePath;

        lock (_resolveLock)
        {
            if (_ffprobeResolved)
                return _cachedFfprobePath;

            _cachedFfprobePath = FfmpegExecutableLocator.FindFfprobe(_config);
            _ffprobeResolved = true;
            return _cachedFfprobePath;
        }
    }

    internal static string CondenseFailure(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "FFprobe rejected the file";

        var firstLine = error
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine) ? "FFprobe rejected the file" : firstLine;
    }
}
