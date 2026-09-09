using System.Diagnostics;

namespace Cove.Api.Services;

internal static class FfmpegProcessEnvironment
{
    internal static void Apply(ProcessStartInfo startInfo, string executablePath)
    {
        if (!OperatingSystem.IsLinux())
            return;

        var executableDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        if (string.IsNullOrWhiteSpace(executableDirectory))
            return;

        var hasInheritedLibraryPath = startInfo.Environment.TryGetValue("LD_LIBRARY_PATH", out var inheritedLibraryPath);
        var inheritedDirectories = hasInheritedLibraryPath
            ? inheritedLibraryPath!.Split(Path.PathSeparator, StringSplitOptions.None)
            : [];
        var remainingDirectories = inheritedDirectories
            .Where(directory => !string.Equals(directory, executableDirectory, StringComparison.Ordinal))
            .ToArray();
        startInfo.Environment["LD_LIBRARY_PATH"] = !hasInheritedLibraryPath || remainingDirectories.Length == 0
            ? executableDirectory
            : $"{executableDirectory}{Path.PathSeparator}{string.Join(Path.PathSeparator, remainingDirectories)}";
    }
}
