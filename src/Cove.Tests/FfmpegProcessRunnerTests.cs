using System.Diagnostics;
using Cove.Api.Services;

namespace Cove.Tests;

public class FfmpegProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_CancellationTerminatesTheProcessTreeBeforeReturning()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-ffmpeg-runner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var markerPath = Path.Combine(tempRoot, "processes.txt");
        var scriptPath = Path.Combine(tempRoot, "blocking-ffmpeg");
        var escapedMarkerPath = markerPath.Replace("\\", "\\\\").Replace("\"", "\\\"");
        await File.WriteAllTextAsync(scriptPath, $"#!/bin/sh\nsleep 600 &\nchild_pid=$!\nprintf '%s %s\\n' \"$$\" \"$child_pid\" > \"{escapedMarkerPath}\"\nwait \"$child_pid\"\n", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        int[] processIds = [];
        try
        {
            using var cancellation = new CancellationTokenSource();
            var runTask = FfmpegProcessRunner.RunAsync(
                scriptPath,
                arguments: [],
                timeout: TimeSpan.FromMinutes(1),
                cancellation.Token);

            await WaitUntilAsync(() => File.Exists(markerPath), TimeSpan.FromSeconds(5));
            processIds = (await File.ReadAllTextAsync(markerPath, TestContext.Current.CancellationToken))
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(int.Parse)
                .ToArray();
            Assert.Equal(2, processIds.Length);
            Assert.All(processIds, processId => Assert.True(IsRunning(processId)));

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
            await WaitUntilAsync(() => processIds.All(processId => !IsRunning(processId)), TimeSpan.FromSeconds(5));
            Assert.All(processIds, processId => Assert.False(IsRunning(processId)));
        }
        finally
        {
            foreach (var processId in processIds)
                KillIfRunning(processId);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_TimeoutKillsTheProcessTreeAndReportsTimedOut()
    {
        if (!OperatingSystem.IsLinux())
            return;

        await using var fixture = await BlockingProcessFixture.CreateAsync();
        var result = await FfmpegProcessRunner.RunAsync(
            fixture.ScriptPath, [], TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        var processIds = await fixture.ReadProcessIdsAsync();
        await WaitUntilAsync(() => processIds.All(processId => !IsRunning(processId)), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunAsync_TimeoutStillReportsWhatTheProcessWroteToStderr()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"cove-ffmpeg-runner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var script = Path.Combine(root, "stuck-ffmpeg");
            await File.WriteAllTextAsync(script, "#!/bin/sh\necho 'stuck on input' >&2\nexec sleep 600\n", TestContext.Current.CancellationToken);
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var result = await FfmpegProcessRunner.RunAsync(script, [], TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

            Assert.True(result.TimedOut);
            Assert.Contains("stuck on input", result.StandardError);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Start_DisposingTheHandleKillsTheProcessTree()
    {
        if (!OperatingSystem.IsLinux())
            return;

        await using var fixture = await BlockingProcessFixture.CreateAsync();
        int[] processIds;
        await using (FfmpegProcessRunner.Start(fixture.ScriptPath, [], redirectStandardOutput: true, TestContext.Current.CancellationToken))
        {
            processIds = await fixture.ReadProcessIdsAsync();
            Assert.All(processIds, processId => Assert.True(IsRunning(processId)));
        }

        await WaitUntilAsync(() => processIds.All(processId => !IsRunning(processId)), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Start_CancellationKillsTheProcessTreeWhileTheHandleIsHeld()
    {
        if (!OperatingSystem.IsLinux())
            return;

        await using var fixture = await BlockingProcessFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        await using var process = FfmpegProcessRunner.Start(fixture.ScriptPath, [], redirectStandardOutput: true, cancellation.Token);
        var processIds = await fixture.ReadProcessIdsAsync();

        cancellation.Cancel();

        await WaitUntilAsync(() => processIds.All(processId => !IsRunning(processId)), TimeSpan.FromSeconds(5));
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task RunAsync_PassesEachArgumentUnchangedAndReturnsOutput()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var recorder = new ArgvRecordingExecutable(stdout: "out");
        string[] arguments = ["-i", "a \"b\" c", "", "\\\"", "-y"];

        var result = await FfmpegProcessRunner.RunAsync(recorder.ExecutablePath, arguments, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("out", result.StandardOutput);
        Assert.Equal(arguments, Assert.Single(recorder.Invocations));
    }

    /// <summary>A shell script that starts a long-running child and records both process ids.</summary>
    private sealed class BlockingProcessFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _markerPath;
        private int[] _processIds = [];

        private BlockingProcessFixture(string root)
        {
            _root = root;
            _markerPath = Path.Combine(root, "processes.txt");
            ScriptPath = Path.Combine(root, "blocking-ffmpeg");
        }

        public string ScriptPath { get; }

        public static async Task<BlockingProcessFixture> CreateAsync()
        {
            if (OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("The fixture is a POSIX shell script.");
            var root = Path.Combine(Path.GetTempPath(), $"cove-ffmpeg-runner-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var fixture = new BlockingProcessFixture(root);
            var escapedMarkerPath = fixture._markerPath.Replace("\\", "\\\\").Replace("\"", "\\\"");
            await File.WriteAllTextAsync(fixture.ScriptPath, $"#!/bin/sh\nsleep 600 &\nchild_pid=$!\nprintf '%s %s\\n' \"$$\" \"$child_pid\" > \"{escapedMarkerPath}\"\nwait \"$child_pid\"\n");
            File.SetUnixFileMode(fixture.ScriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return fixture;
        }

        public async Task<int[]> ReadProcessIdsAsync()
        {
            await WaitUntilAsync(() => File.Exists(_markerPath) && new FileInfo(_markerPath).Length > 0, TimeSpan.FromSeconds(5));
            _processIds = (await File.ReadAllTextAsync(_markerPath))
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(int.Parse)
                .ToArray();
            Assert.Equal(2, _processIds.Length);
            return _processIds;
        }

        public ValueTask DisposeAsync()
        {
            foreach (var processId in _processIds)
                KillIfRunning(processId);
            Directory.Delete(_root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected process state was not observed before the test timeout.");
            await Task.Delay(50);
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void KillIfRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The test process already exited.
        }
    }
}
