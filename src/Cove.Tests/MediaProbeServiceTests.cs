using Cove.Api.Services;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public class MediaProbeServiceTests
{
    [Fact]
    public async Task ProbeAsync_UsesConfiguredExecutableAndPassesPathAsSingleArgument()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var scriptPath = Path.Combine(tempRoot, "fake ffprobe");
            await File.WriteAllTextAsync(scriptPath, "#!/bin/sh\nprintf '%s' '{\"streams\":[]}'\n", TestContext.Current.CancellationToken);
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var service = CreateService(scriptPath, TimeSpan.FromSeconds(2));

            var result = await service.ProbeAsync(Path.Combine(tempRoot, "media with spaces.mp4"), TestContext.Current.CancellationToken);

            Assert.Equal(MediaProbeStatus.Success, result.Status);
            Assert.Equal("{\"streams\":[]}", result.Json);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProbeAsync_IgnoresEmbeddedChapters()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var scriptPath = Path.Combine(tempRoot, "fake ffprobe");
            await File.WriteAllTextAsync(scriptPath, "#!/bin/sh\nwhile [ $# -gt 1 ]; do\n  if [ \"$1\" = '-ignore_chapters' ] && [ \"$2\" = '1' ]; then\n    printf '%s' '{\"streams\":[]}'\n    exit 0\n  fi\n  shift\ndone\nprintf '%s\\n' 'embedded chapters were not ignored' >&2\nexit 2\n", TestContext.Current.CancellationToken);
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var service = CreateService(scriptPath, TimeSpan.FromSeconds(2));

            var result = await service.ProbeAsync(Path.Combine(tempRoot, "media.m4a"), TestContext.Current.CancellationToken);

            Assert.Equal(MediaProbeStatus.Success, result.Status);
            Assert.Equal("{\"streams\":[]}", result.Json);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProbeAsync_RejectsStderrEvenWhenProbeOtherwiseSucceeds()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var scriptPath = Path.Combine(tempRoot, "fake ffprobe");
            await File.WriteAllTextAsync(scriptPath, "#!/bin/sh\nprintf '%s' '{\"streams\":[]}'\nprintf '%s\\n' 'unrelated structural error' >&2\n", TestContext.Current.CancellationToken);
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var service = CreateService(scriptPath, TimeSpan.FromSeconds(2));

            var result = await service.ProbeAsync(Path.Combine(tempRoot, "media.m4a"), TestContext.Current.CancellationToken);

            Assert.Equal(MediaProbeStatus.Invalid, result.Status);
            Assert.Null(result.Json);
            Assert.Equal("unrelated structural error", result.Reason);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProbeAsync_TimesOutAndTerminatesHungProcess()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var scriptPath = Path.Combine(tempRoot, "hanging-ffprobe");
            await File.WriteAllTextAsync(scriptPath, "#!/bin/sh\nsleep 30\n", TestContext.Current.CancellationToken);
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var service = CreateService(scriptPath, TimeSpan.FromMilliseconds(100));
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var result = await service.ProbeAsync(Path.Combine(tempRoot, "media.mp4"), TestContext.Current.CancellationToken);

            Assert.Equal(MediaProbeStatus.TimedOut, result.Status);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Probe cleanup took {stopwatch.Elapsed}");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static FfprobeMediaProbeService CreateService(string path, TimeSpan timeout)
    {
        return new FfprobeMediaProbeService(
            new CoveConfiguration { FfprobePath = path },
            NullLogger<FfprobeMediaProbeService>.Instance,
            timeout);
    }
}
