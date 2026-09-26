using Cove.Api.Services;
using Cove.Core.Interfaces;

namespace Cove.Tests;

public class FfmpegExecutableLocatorTests
{
    [Fact]
    public void ConfiguredPathsWinAndFfprobeIsFoundBesideFfmpeg()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cove-locator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var ffmpeg = Path.Combine(root, FfmpegExecutableLocator.FfmpegFileName);
            var ffprobe = Path.Combine(root, FfmpegExecutableLocator.FfprobeFileName);
            var otherProbe = Path.Combine(root, "custom-ffprobe");
            File.WriteAllText(ffmpeg, string.Empty);
            File.WriteAllText(ffprobe, string.Empty);
            File.WriteAllText(otherProbe, string.Empty);

            Assert.Equal(ffmpeg, FfmpegExecutableLocator.FindFfmpeg(new CoveConfiguration { FfmpegPath = ffmpeg }));
            Assert.Equal(ffprobe, FfmpegExecutableLocator.FindFfprobe(new CoveConfiguration { FfmpegPath = ffmpeg }));
            Assert.Equal(otherProbe, FfmpegExecutableLocator.FindFfprobe(new CoveConfiguration { FfmpegPath = ffmpeg, FfprobePath = otherProbe }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AMissingConfiguredPathFallsBackToPath()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"cove-missing-{Guid.NewGuid():N}", "ffmpeg");

        Assert.Equal(
            FfmpegExecutableLocator.FindOnPath(FfmpegExecutableLocator.FfmpegFileName),
            FfmpegExecutableLocator.FindFfmpeg(new CoveConfiguration { FfmpegPath = missing }));
    }
}
