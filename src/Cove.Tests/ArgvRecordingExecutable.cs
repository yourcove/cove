using System.Text;

namespace Cove.Tests;

/// <summary>
/// A stand-in executable (a POSIX shell script) that records the exact argv it receives, one invocation
/// per call, so tests can check how arguments reach a child process without depending on a real ffmpeg.
/// It writes <paramref name="stdout"/> to standard output and, when asked, creates the file named by its
/// last argument, which is where ffmpeg writes its output.
/// </summary>
internal sealed class ArgvRecordingExecutable : IDisposable
{
    private const char InvocationSeparator = '\u001e';
    private readonly string _directory;
    private readonly string _logPath;

    public ArgvRecordingExecutable(string name = "ffmpeg", string stdout = "", bool touchLastArgument = false)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The argv recorder is a POSIX shell script.");

        _directory = Path.Combine(Path.GetTempPath(), $"cove-argv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _logPath = Path.Combine(_directory, "argv.log");
        ExecutablePath = Path.Combine(_directory, name);

        var script = new StringBuilder("#!/bin/sh\n");
        script.Append("log=").Append(ShellQuote(_logPath)).Append('\n');
        script.Append("for arg in \"$@\"; do printf '%s\\0' \"$arg\" >> \"$log\"; done\n");
        script.Append("printf '\\036' >> \"$log\"\n");
        if (touchLastArgument)
            script.Append("for last in \"$@\"; do :; done\n: > \"$last\"\n");
        if (stdout.Length > 0)
            script.Append("printf '%s' ").Append(ShellQuote(stdout)).Append('\n');

        File.WriteAllText(ExecutablePath, script.ToString());
        File.SetUnixFileMode(ExecutablePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public string ExecutablePath { get; }

    /// <summary>Every recorded invocation's argv (excluding argv[0]), oldest first.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Invocations
    {
        get
        {
            if (!File.Exists(_logPath))
                return [];
            return File.ReadAllText(_logPath)
                .Split(InvocationSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(invocation => (IReadOnlyList<string>)invocation.Split('\0')[..^1])
                .ToList();
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
