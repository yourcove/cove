using System.Diagnostics;
using System.Text;

namespace Cove.Api.Services;

internal readonly record struct FfmpegProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>
/// The single owner of the child processes Cove's media services start (ffmpeg, ffprobe, and the
/// archive tool the ffmpeg download unpacks with).
///
/// Arguments are always a list, handed to the operating system one element per argument through
/// <see cref="ProcessStartInfo.ArgumentList"/>. Nothing is ever joined into a command-line string, so
/// a file name is passed through unchanged whatever characters it holds. The only text that is split
/// into arguments is administrator configuration, by <see cref="FfmpegArgumentTokenizer"/>.
///
/// Every entry point bounds the process: it is killed together with its children on timeout, on
/// cancellation, when the caller fails, and when a <see cref="FfmpegProcessHandle"/> is disposed; and
/// the <see cref="Process"/> is always disposed. Cleanup waits are themselves bounded, so cancelling
/// never hangs behind a child that will not die.
/// </summary>
internal static class FfmpegProcessRunner
{
    internal static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Runs ffmpeg or ffprobe to completion, capturing stdout and stderr.</summary>
    /// <returns>The exit code and output. On timeout the process tree is killed and the result has
    /// <see cref="FfmpegProcessResult.TimedOut"/> set; cancellation throws after the same cleanup.</returns>
    public static Task<FfmpegProcessResult> RunAsync(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
        => RunCoreAsync(ffmpegPath, arguments, timeout, applyFfmpegEnvironment: true, ct);

    /// <summary>
    /// <see cref="RunAsync"/> for a system tool rather than an ffmpeg binary: the library search path
    /// that lets a downloaded ffmpeg find its own shared libraries is not applied.
    /// </summary>
    public static Task<FfmpegProcessResult> RunSystemToolAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
        => RunCoreAsync(executable, arguments, timeout, applyFfmpegEnvironment: false, ct);

    /// <summary>
    /// Blocking form of <see cref="RunAsync"/> for the short capability probes that run inside
    /// synchronous encoder selection. Anything that can run for long belongs on the async form.
    /// </summary>
    public static FfmpegProcessResult Run(string ffmpegPath, IReadOnlyList<string> arguments, TimeSpan timeout)
        => RunAsync(ffmpegPath, arguments, timeout, CancellationToken.None).GetAwaiter().GetResult();

    private static async Task<FfmpegProcessResult> RunCoreAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        bool applyFfmpegEnvironment,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var process = Start(executable, arguments, redirectStandardOutput: true, applyFfmpegEnvironment);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        // The readers only stop for the caller's cancellation: after a timeout they drain what the killed
        // process left, which is usually the error worth reporting. StopAsync bounds that drain.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            // A grandchild that inherited the pipes can hold them open after ffmpeg exits; the timeout
            // still applies to draining them.
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            await StopAsync(process, stdoutTask, stderrTask);
            ct.ThrowIfCancellationRequested();
            return new FfmpegProcessResult(-1, CompletedOutput(stdoutTask), CompletedOutput(stderrTask), TimedOut: true);
        }
        catch
        {
            await StopAsync(process, stdoutTask, stderrTask);
            throw;
        }

        return new FfmpegProcessResult(process.ExitCode, stdoutTask.Result, stderrTask.Result, TimedOut: false);
    }

    /// <summary>
    /// Runs a long ffmpeg job (a full-file encode or decode) that writes <c>-progress pipe:1</c> key=value
    /// lines to stdout, handing each line to <paramref name="onProgressLine"/>. There is no overall timeout,
    /// because a large encode legitimately runs for hours; instead the process is killed when stdout goes
    /// quiet for <paramref name="stallTimeout"/>, which ffmpeg only does when it is hung. Only the tail of
    /// stderr is kept, since a long run with warnings can produce a lot of it.
    /// </summary>
    public static async Task<FfmpegProcessResult> RunWithProgressAsync(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        Action<string> onProgressLine,
        TimeSpan stallTimeout,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var process = Start(ffmpegPath, arguments, redirectStandardOutput: true, applyFfmpegEnvironment: true);
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var lastOutput = Environment.TickCount64;
        var stderrTail = new OutputTail(maxChars: 4000);
        var stderrTask = stderrTail.DrainAsync(process.StandardError, stallCts.Token);
        var stdoutTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(stallCts.Token) is { } line)
            {
                Interlocked.Exchange(ref lastOutput, Environment.TickCount64);
                onProgressLine(line);
            }
        }, CancellationToken.None);

        var stalled = false;
        var watchdog = Task.Run(async () =>
        {
            try
            {
                while (!process.HasExited)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stallCts.Token);
                    if (Environment.TickCount64 - Interlocked.Read(ref lastOutput) > stallTimeout.TotalMilliseconds)
                    {
                        stalled = true;
                        stallCts.Cancel();
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(stallCts.Token);
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(stallCts.Token);
        }
        catch (OperationCanceledException)
        {
            await StopAsync(process, stdoutTask, stderrTask);
            ct.ThrowIfCancellationRequested();
            return new FfmpegProcessResult(-1, string.Empty, stderrTail.ToString(), TimedOut: stalled);
        }
        catch
        {
            await StopAsync(process, stdoutTask, stderrTask);
            throw;
        }
        finally
        {
            stallCts.Cancel();
            await watchdog;
        }

        return new FfmpegProcessResult(process.ExitCode, string.Empty, stderrTail.ToString(), TimedOut: false);
    }

    /// <summary>
    /// Starts ffmpeg for a caller that consumes its output as it is produced (a live transcode stream,
    /// raw frames) or decides itself when to stop it (an HLS encode nobody is watching any more). The
    /// returned handle owns the process: cancelling <paramref name="ct"/> kills the process tree, and
    /// disposing the handle kills whatever is still running and releases the process.
    /// </summary>
    /// <param name="redirectStandardOutput">Whether stdout is captured for the caller to read. When it
    /// is, the caller must keep reading it or ffmpeg blocks on a full pipe.</param>
    /// <param name="maxStandardErrorChars">How much of the end of stderr is kept; stderr is always
    /// drained so it cannot fill and block ffmpeg.</param>
    public static FfmpegProcessHandle Start(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        bool redirectStandardOutput,
        CancellationToken ct,
        int maxStandardErrorChars = 8192)
    {
        ct.ThrowIfCancellationRequested();
        var process = Start(ffmpegPath, arguments, redirectStandardOutput, applyFfmpegEnvironment: true);
        return new FfmpegProcessHandle(process, maxStandardErrorChars, ct);
    }

    private static Process Start(string executable, IReadOnlyList<string> arguments, bool redirectStandardOutput, bool applyFfmpegEnvironment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = redirectStandardOutput,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument, nameof(arguments));
            startInfo.ArgumentList.Add(argument);
        }
        if (applyFfmpegEnvironment)
            FfmpegProcessEnvironment.Apply(startInfo, executable);

        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        return process;
    }

    private static async Task StopAsync(Process process, Task stdoutTask, Task stderrTask)
    {
        KillProcessTree(process);
        await ObserveExitAsync(process);
        await ObserveAsync(Task.WhenAll(stdoutTask, stderrTask));
    }

    internal static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The process either exited concurrently or could not be killed. Cleanup below remains bounded.
        }
    }

    internal static async Task ObserveExitAsync(Process process)
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

    internal static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.WaitAsync(CleanupTimeout);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            // Killing the process can close redirected pipes while their readers are completing.
        }
    }

    /// <summary>
    /// A readable, truncated rendering of an argument list for log messages only. It is never parsed
    /// back or used to start a process.
    /// </summary>
    internal static string Describe(IReadOnlyList<string> arguments, int maxChars = 200)
    {
        var text = string.Join(' ', arguments.Select(argument =>
            argument.Length == 0 || argument.Any(c => char.IsWhiteSpace(c) || c == '"')
                ? "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
                : argument));
        return text.Length > maxChars ? text[..maxChars] : text;
    }

    private static string CompletedOutput(Task<string> outputTask)
        => outputTask.IsCompletedSuccessfully ? outputTask.Result : string.Empty;
}

/// <summary>A running ffmpeg started by <see cref="FfmpegProcessRunner.Start"/>. See there for ownership.</summary>
internal sealed class FfmpegProcessHandle : IAsyncDisposable, IDisposable
{
    private readonly Process _process;
    private readonly OutputTail _stderr;
    private readonly Task _stderrDrain;
    private readonly CancellationTokenRegistration _cancellation;
    private int _disposed;

    internal FfmpegProcessHandle(Process process, int maxStandardErrorChars, CancellationToken ct)
    {
        _process = process;
        _stderr = new OutputTail(maxStandardErrorChars);
        _stderrDrain = _stderr.DrainAsync(process.StandardError, CancellationToken.None);
        _cancellation = ct.Register(static state => FfmpegProcessRunner.KillProcessTree((Process)state!), process);
    }

    /// <summary>ffmpeg's stdout. Only available when the handle was started with stdout redirected.</summary>
    public Stream StandardOutput => _process.StandardOutput.BaseStream;

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    /// <summary>The most recent stderr output, up to the size the handle was started with.</summary>
    public string StandardErrorTail => _stderr.ToString();

    public Task WaitForExitAsync(CancellationToken ct = default) => _process.WaitForExitAsync(ct);

    /// <summary>Waits (bounded) for stderr to be fully read after exit, then returns its tail.</summary>
    public async Task<string> ReadStandardErrorTailAsync()
    {
        await FfmpegProcessRunner.ObserveAsync(_stderrDrain);
        return _stderr.ToString();
    }

    /// <summary>Kills the process and its children. Safe to call at any time, including after exit.</summary>
    public void Kill() => FfmpegProcessRunner.KillProcessTree(_process);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _cancellation.DisposeAsync();
        Kill();
        await FfmpegProcessRunner.ObserveExitAsync(_process);
        await FfmpegProcessRunner.ObserveAsync(_stderrDrain);
        _process.Dispose();
    }

    /// <summary>
    /// Synchronous disposal for owners that can only dispose synchronously (a <see cref="Stream"/>
    /// wrapper). The process is killed and released without waiting for it to be reaped.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cancellation.Dispose();
        Kill();
        _process.Dispose();
    }
}

/// <summary>Keeps the last <c>maxChars</c> characters of a stream of text, readable while it is still being written.</summary>
internal sealed class OutputTail(int maxChars)
{
    private readonly StringBuilder _text = new();

    public async Task DrainAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                lock (_text)
                {
                    _text.Append(buffer, 0, read);
                    if (_text.Length > maxChars * 2)
                        _text.Remove(0, _text.Length - maxChars);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process was killed or released while its pipe was being read.
        }
    }

    public override string ToString()
    {
        lock (_text)
            return _text.Length > maxChars ? _text.ToString(_text.Length - maxChars, maxChars) : _text.ToString();
    }
}
