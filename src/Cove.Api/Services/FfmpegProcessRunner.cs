using System.Diagnostics;

namespace Cove.Api.Services;

internal readonly record struct FfmpegProcessResult(int ExitCode, string StandardError, bool TimedOut);

internal static class FfmpegProcessRunner
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    public static async Task<FfmpegProcessResult> RunAsync(
        string ffmpegPath,
        string arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        FfmpegProcessEnvironment.Apply(startInfo, ffmpegPath);
        using var process = new Process { StartInfo = startInfo };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            await ObserveExitAsync(process);
            await ObserveOutputAsync(stdoutTask, stderrTask);

            ct.ThrowIfCancellationRequested();
            return new FfmpegProcessResult(-1, CompletedOutput(stderrTask), TimedOut: true);
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        return new FfmpegProcessResult(process.ExitCode, stderrTask.Result, TimedOut: false);
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process either exited concurrently or could not be killed. Cleanup below remains bounded.
        }
    }

    private static async Task ObserveExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(CleanupTimeout);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            // Cleanup is best effort and must not delay cancellation indefinitely.
        }
    }

    private static async Task ObserveOutputAsync(Task<string> stdoutTask, Task<string> stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(CleanupTimeout);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException)
        {
            // Killing the process can close redirected pipes while their readers are completing.
        }
    }

    private static string CompletedOutput(Task<string> outputTask)
        => outputTask.IsCompletedSuccessfully ? outputTask.Result : string.Empty;
}
