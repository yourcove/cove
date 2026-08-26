using System.Diagnostics;
using Cove.Api.Services;

namespace Cove.Tests;

public sealed class FfmpegProcessEnvironmentTests
{
    [Fact]
    public void Apply_OnLinux_PrependsExecutableDirectoryAndPreservesInheritedPath()
    {
        var executableDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cove-managed-ffmpeg"));
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["LD_LIBRARY_PATH"] = "/existing/one:/existing/two";

        FfmpegProcessEnvironment.Apply(startInfo, Path.Combine(executableDirectory, "ffmpeg"));

        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(
                $"{executableDirectory}{Path.PathSeparator}/existing/one{Path.PathSeparator}/existing/two",
                startInfo.Environment["LD_LIBRARY_PATH"]);
        }
        else
        {
            Assert.Equal("/existing/one:/existing/two", startInfo.Environment["LD_LIBRARY_PATH"]);
        }
    }

    [Fact]
    public void Apply_OnLinux_PromotesExistingExecutableDirectoryWithoutDuplicatingIt()
    {
        var executableDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cove-managed-ffmpeg"));
        var inherited = $"/existing{Path.PathSeparator}{executableDirectory}";
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["LD_LIBRARY_PATH"] = inherited;

        FfmpegProcessEnvironment.Apply(startInfo, Path.Combine(executableDirectory, "ffmpeg"));

        if (OperatingSystem.IsLinux())
            Assert.Equal($"{executableDirectory}{Path.PathSeparator}/existing", startInfo.Environment["LD_LIBRARY_PATH"]);
        else
            Assert.Equal(inherited, startInfo.Environment["LD_LIBRARY_PATH"]);
    }

    [Fact]
    public void Apply_OnLinux_PreservesEmptyAndWhitespaceComponentsExactly()
    {
        var executableDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cove-managed-ffmpeg"));
        const string inherited = "/existing:: /spaced path/ :/tail:";
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["LD_LIBRARY_PATH"] = inherited;

        FfmpegProcessEnvironment.Apply(startInfo, Path.Combine(executableDirectory, "ffmpeg"));

        if (OperatingSystem.IsLinux())
            Assert.Equal($"{executableDirectory}{Path.PathSeparator}{inherited}", startInfo.Environment["LD_LIBRARY_PATH"]);
        else
            Assert.Equal(inherited, startInfo.Environment["LD_LIBRARY_PATH"]);
    }
}
