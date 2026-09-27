extern alias InstanceManager;

using System.Diagnostics;
using InstanceManagerService = InstanceManager::InstanceManagerService;

namespace Cove.Tests;

/// <summary>
/// The instance manager launches pg_ctl and a terminal with paths under an instance's home directory,
/// which the user chooses and which may contain spaces or quotes. Each path must reach the child
/// process as exactly one argument, unchanged.
/// </summary>
public class InstanceManagerProcessArgumentTests
{
    public static bool IsUnix => !OperatingSystem.IsWindows();

    private const string AwkwardDirectory = "my \"cove\" home";

    [Fact(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    public void PgCtlStop_PassesDataDirectoryAsOneArgument()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), AwkwardDirectory, "pgdata");

        var argv = RunRecorded(InstanceManagerService.CreatePgCtlStopStartInfo("/opt/pgsql/bin/pg_ctl", dataDir));

        Assert.Equal(["stop", "-D", dataDir, "-m", "fast", "-w", "-t", "30"], argv);
    }

    [Theory(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    [InlineData("x-terminal-emulator", "-e")]
    [InlineData("gnome-terminal", "--")]
    [InlineData("konsole", "-e")]
    [InlineData("xterm", "-e")]
    public void LinuxTerminal_PassesScriptPathAsOneArgument(string terminal, string separator)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), AwkwardDirectory, ".manager", "show-cove-logs.sh");

        var argv = RunRecorded(InstanceManagerService.CreateLinuxTerminalStartInfo(terminal, scriptPath));

        Assert.Equal([separator, scriptPath], argv);
    }

    [Fact(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    public void LinuxTerminal_PassesPlainScriptPathWithoutShellQuotes()
    {
        const string scriptPath = "/home/user/.manager/show-cove-logs.sh";

        var argv = RunRecorded(InstanceManagerService.CreateLinuxTerminalStartInfo("xterm", scriptPath));

        Assert.Equal(["-e", scriptPath], argv);
    }

    [Fact(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    public void MacTerminal_PassesScriptPathAsOneArgument()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), AwkwardDirectory, ".manager", "show-cove-logs.sh");

        var argv = RunRecorded(InstanceManagerService.CreateMacTerminalStartInfo(scriptPath));

        Assert.Equal(["-a", "Terminal", scriptPath], argv);
    }

    /// <summary>Starts <paramref name="startInfo"/> against the argv recorder instead of its real program.</summary>
    private static IReadOnlyList<string> RunRecorded(ProcessStartInfo startInfo)
    {
        using var recorder = new ArgvRecordingExecutable(name: Path.GetFileName(startInfo.FileName));
        startInfo.FileName = recorder.ExecutablePath;
        startInfo.WorkingDirectory = "";
        startInfo.UseShellExecute = false;

        using (var process = Process.Start(startInfo)!)
            Assert.True(process.WaitForExit(TimeSpan.FromSeconds(10)));

        return Assert.Single(recorder.Invocations);
    }
}
