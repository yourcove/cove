using System.ComponentModel;
using System.Diagnostics;
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
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(2);

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

        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        FfmpegProcessEnvironment.Apply(startInfo, ffprobePath);
        using var process = new Process { StartInfo = startInfo };

        process.StartInfo.ArgumentList.Add("-v");
        process.StartInfo.ArgumentList.Add("error");
        process.StartInfo.ArgumentList.Add("-print_format");
        process.StartInfo.ArgumentList.Add("json");
        process.StartInfo.ArgumentList.Add("-show_error");
        process.StartInfo.ArgumentList.Add("-show_format");
        process.StartInfo.ArgumentList.Add("-show_streams");
        // Cove does not import embedded chapters. Asking the MOV demuxer to inspect them can turn a
        // dangling chapter-track reference into stderr that rejects otherwise valid audio metadata.
        process.StartInfo.ArgumentList.Add("-ignore_chapters");
        process.StartInfo.ArgumentList.Add("1");
        process.StartInfo.ArgumentList.Add(path);

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
        {
            _logger.LogWarning(ex, "Unable to start FFprobe at {FfprobePath}", ffprobePath);
            return MediaProbeResult.ToolUnavailable("FFprobe could not be started");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start FFprobe for {Path}", path);
            return MediaProbeResult.Failure(ex.Message);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            KillProcessTree(process);
            await AwaitProcessExitAsync(process);
            await ObserveOutputTasksAsync(outputTask, errorTask);
            return MediaProbeResult.Timeout($"FFprobe exceeded the {_timeout.TotalSeconds:N0}-second timeout");
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            await AwaitProcessExitAsync(process);
            await ObserveOutputTasksAsync(outputTask, errorTask);
            throw;
        }
        catch (Exception ex)
        {
            KillProcessTree(process);
            await AwaitProcessExitAsync(process);
            await ObserveOutputTasksAsync(outputTask, errorTask);
            _logger.LogWarning(ex, "FFprobe process failed for {Path}", path);
            return MediaProbeResult.Failure(ex.Message);
        }

        string json;
        string error;
        try
        {
            json = await outputTask;
            error = await errorTask;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed reading FFprobe output for {Path}", path);
            return MediaProbeResult.Failure(ex.Message);
        }
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
            return MediaProbeResult.Rejected(CondenseFailure(error));

        // At error log level ffprobe writes only structural/demuxing errors encountered during its
        // bounded metadata read. Treat those as an invalid input rather than silently persisting it.
        if (!string.IsNullOrWhiteSpace(error))
            return MediaProbeResult.Rejected(CondenseFailure(error));

        return MediaProbeResult.Succeeded(json);
    }

    private string? FindFfprobe()
    {
        if (_ffprobeResolved)
            return _cachedFfprobePath;

        lock (_resolveLock)
        {
            if (_ffprobeResolved)
                return _cachedFfprobePath;

            _cachedFfprobePath = ResolveFfprobePath();
            _ffprobeResolved = true;
            return _cachedFfprobePath;
        }
    }

    private string? ResolveFfprobePath()
    {
        if (!string.IsNullOrWhiteSpace(_config.FfprobePath) && File.Exists(_config.FfprobePath))
            return _config.FfprobePath;

        if (!string.IsNullOrWhiteSpace(_config.FfmpegPath))
        {
            var directory = Path.GetDirectoryName(_config.FfmpegPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var sibling = Path.Combine(directory, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
                if (File.Exists(sibling))
                    return sibling;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It exited between HasExited and Kill.
        }
        catch (Win32Exception)
        {
            // Best effort during timeout/cancellation cleanup.
        }
    }

    private static async Task AwaitProcessExitAsync(Process process)
    {
        using var cleanupCts = new CancellationTokenSource(CleanupTimeout);
        try
        {
            await process.WaitForExitAsync(cleanupCts.Token);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            // Cleanup is best effort and must never turn a bounded probe into an unbounded wait.
        }
    }

    private static async Task ObserveOutputTasksAsync(Task<string> outputTask, Task<string> errorTask)
    {
        var outputs = Task.WhenAll(outputTask, errorTask);
        var completed = await Task.WhenAny(outputs, Task.Delay(CleanupTimeout));
        if (completed != outputs)
            return;

        try
        {
            await outputs;
        }
        catch
        {
            // Cleanup path: output is intentionally discarded.
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
