using Cove.Core.Interfaces;

namespace Cove.Api.Services;

/// <summary>
/// Finds the ffmpeg and ffprobe executables. This is the only place that decides which binary Cove
/// runs: a configured path that exists wins, then (for ffprobe) the one beside the configured ffmpeg,
/// then the first match on PATH. <see cref="FfmpegManagerService"/> writes the path it settles on at
/// startup back into the configuration, so after startup this is normally a single existence check.
/// </summary>
internal static class FfmpegExecutableLocator
{
    public static string FfmpegFileName => ExecutableFileName("ffmpeg");
    public static string FfprobeFileName => ExecutableFileName("ffprobe");

    public static string? FindFfmpeg(CoveConfiguration config) => FindFfmpeg(config.FfmpegPath);

    public static string? FindFfmpeg(string? configuredPath)
        => ExistingFile(configuredPath) ?? FindOnPath(FfmpegFileName);

    public static string? FindFfprobe(CoveConfiguration config)
        => ExistingFile(config.FfprobePath)
           ?? SiblingOf(config.FfmpegPath, FfprobeFileName)
           ?? FindOnPath(FfprobeFileName);

    /// <summary>The first directory on PATH that holds <paramref name="fileName"/>, or null.</summary>
    public static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;
            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is skipped rather than ending the search.
            }
        }

        return null;
    }

    private static string ExecutableFileName(string tool) => OperatingSystem.IsWindows() ? tool + ".exe" : tool;

    private static string? ExistingFile(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;

    private static string? SiblingOf(string? path, string fileName)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var directory = Path.GetDirectoryName(path);
        return string.IsNullOrWhiteSpace(directory) ? null : ExistingFile(Path.Combine(directory, fileName));
    }
}
