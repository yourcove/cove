using Microsoft.Extensions.Logging.Abstractions;
using Cove.Api.Services;
using Cove.Core.Interfaces;

namespace Cove.Tests;

/// <summary>
/// Media paths are arbitrary file names from a scanned library. On Linux a name may contain a double
/// quote and text that looks like an ffmpeg option; the path must still reach ffmpeg as exactly one
/// argument, unchanged.
/// </summary>
public class FfmpegPathArgumentTests
{
    public static bool IsUnix => !OperatingSystem.IsWindows();

    internal const string AwkwardFileName = "clip\" -f null -y \"x.mp4";

    [Fact(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    public async Task LiveTranscode_PassesInputPathAsOneArgument()
    {
        using var ffmpeg = new ArgvRecordingExecutable(stdout: "fragment");
        var root = Path.Combine(Path.GetTempPath(), $"cove-transcode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, AwkwardFileName);
            await File.WriteAllBytesAsync(input, [0], TestContext.Current.CancellationToken);
            var service = new TranscodeService(
                new CoveConfiguration { FfmpegPath = ffmpeg.ExecutablePath, HardwareAcceleration = "off", GeneratedPath = root },
                NullLogger<TranscodeService>.Instance);

            await using (var stream = await service.TranscodeToMp4Async(input, "480p", 0, TestContext.Current.CancellationToken))
                Assert.NotNull(stream);

            var argv = Assert.Single(ffmpeg.Invocations);
            AssertSingleInput(argv, input);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    public async Task MediaProbe_PassesThePathAsOneArgument()
    {
        using var ffprobe = new ArgvRecordingExecutable(name: "ffprobe", stdout: "{}");
        var root = Path.Combine(Path.GetTempPath(), $"cove-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, AwkwardFileName);
            await File.WriteAllBytesAsync(input, [0], TestContext.Current.CancellationToken);
            var service = new FfprobeMediaProbeService(
                new CoveConfiguration { FfprobePath = ffprobe.ExecutablePath },
                NullLogger<FfprobeMediaProbeService>.Instance);

            var result = await service.ProbeAsync(input, TestContext.Current.CancellationToken);

            Assert.Equal(MediaProbeStatus.Success, result.Status);
            var argv = Assert.Single(ffprobe.Invocations);
            Assert.Equal(input, argv[^1]);
            Assert.Equal(input, Assert.Single(argv, argument => argument.Contains("x.mp4", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ConversionAndFrameCommands_KeepEachPathWhole()
    {
        var input = "/library/" + AwkwardFileName;
        var output = "/library/out \"converted\".mp4";
        var source = new ProbedMedia(60, [new ProbedStream(0, "video", "h264", "yuv420p", 8, false, null, null, null)]);
        var settings = new VideoConversionSettings(VideoConversionCodec.Hevc, VideoConversionContainer.Mp4, VideoConversionEffort.BalancedSoftware, ReplaceOriginal: false);

        var plan = VideoConversionPlanner.Build(source, input, output, settings, "libx265", decodeInputArgs: null, qualityLevel: 24);
        AssertSingleInput(plan.Arguments, input);
        Assert.Equal(output, plan.Arguments[^1]);

        AssertSingleInput(VideoConversionPlanner.SampleClipArguments(input, 0, 1, 2, output), input);
        AssertSingleInput(VideoConversionPlanner.DecodeCheckArguments(input, null), input);
        AssertSingleInput(VideoFrameBatchExtractor.BuildBatchArguments(input, "/tmp/frames", [1], 0, 1, 160), input);
    }

    internal static void AssertSingleInput(IReadOnlyList<string> argv, string expectedPath)
    {
        var inputIndex = argv.ToList().IndexOf("-i");
        Assert.True(inputIndex >= 0, $"No -i in: {string.Join(" | ", argv)}");
        Assert.Equal(expectedPath, argv[inputIndex + 1]);
        Assert.Equal(expectedPath, Assert.Single(argv, argument => argument.Contains("x.mp4", StringComparison.Ordinal)));
    }
}
