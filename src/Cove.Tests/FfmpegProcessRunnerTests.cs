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
                arguments: string.Empty,
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
