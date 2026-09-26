using Microsoft.Extensions.Logging.Abstractions;
using Cove.Api.Services;
using Cove.Core.Interfaces;

namespace Cove.Tests;

/// <summary>
/// The raw FfmpegInputArgs/FfmpegOutputArgs settings reach ffmpeg split exactly as they always were, and
/// in the same places on the command line: input args before the input, output args replacing Cove's own
/// encode arguments.
/// </summary>
public class FfmpegConfiguredArgumentTests
{
    public static bool IsUnix => !OperatingSystem.IsWindows();

    [Fact(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    public async Task LiveTranscode_PlacesConfiguredInputAndOutputArguments()
    {
        using var ffmpeg = new ArgvRecordingExecutable(stdout: "fragment");
        var root = Path.Combine(Path.GetTempPath(), $"cove-transcode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "clip.mp4");
            await File.WriteAllBytesAsync(input, [0], TestContext.Current.CancellationToken);
            var service = new TranscodeService(
                new CoveConfiguration
                {
                    FfmpegPath = ffmpeg.ExecutablePath,
                    HardwareAcceleration = "off",
                    GeneratedPath = root,
                    FfmpegInputArgs = "-hwaccel cuda  -threads\t4",
                    FfmpegOutputArgs = "-c:v libx264 -vf \"scale=1280:-2, format=yuv420p\" -metadata title=\\\"x\\\"",
                },
                NullLogger<TranscodeService>.Instance);

            await using (var stream = await service.TranscodeToMp4Async(input, resolution: null, 12.5, TestContext.Current.CancellationToken))
                Assert.NotNull(stream);

            Assert.Equal(
            [
                "-hwaccel", "cuda", "-threads", "4",
                "-ss", "12.5", "-i", input,
                "-c:v", "libx264", "-vf", "scale=1280:-2, format=yuv420p", "-metadata", "title=\"x\"",
                "-movflags", "frag_keyframe+empty_moov", "-f", "mp4", "pipe:1",
            ], Assert.Single(ffmpeg.Invocations));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    public async Task LiveTranscode_WithoutOverridesUsesCovesOwnEncodeArguments()
    {
        using var ffmpeg = new ArgvRecordingExecutable(stdout: "fragment");
        var root = Path.Combine(Path.GetTempPath(), $"cove-transcode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "clip.mp4");
            await File.WriteAllBytesAsync(input, [0], TestContext.Current.CancellationToken);
            var service = new TranscodeService(
                new CoveConfiguration { FfmpegPath = ffmpeg.ExecutablePath, HardwareAcceleration = "off", GeneratedPath = root },
                NullLogger<TranscodeService>.Instance);

            await using (var stream = await service.TranscodeToMp4Async(input, "720p", 0, TestContext.Current.CancellationToken))
                Assert.NotNull(stream);

            Assert.Equal(
            [
                "-i", input,
                "-vf", "scale=1280:720:force_original_aspect_ratio=decrease,scale=trunc(iw/2)*2:trunc(ih/2)*2",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k",
                "-movflags", "frag_keyframe+empty_moov", "-f", "mp4", "pipe:1",
            ], Assert.Single(ffmpeg.Invocations));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Conversion_PlacesConfiguredInputArgumentsBeforeTheInput()
    {
        var source = new ProbedMedia(60, [new ProbedStream(0, "video", "h264", "yuv420p", 8, false, null, null, null)]);
        var settings = new VideoConversionSettings(VideoConversionCodec.Hevc, VideoConversionContainer.Mp4, VideoConversionEffort.BalancedSoftware, ReplaceOriginal: false);

        var plan = VideoConversionPlanner.Build(source, "/in.mp4", "/out.mp4", settings, "libx265", decodeInputArgs: "-hwaccel \"cuda\"\r\n", qualityLevel: 24);
        var decode = VideoConversionPlanner.DecodeCheckArguments("/out.mp4", "-hwaccel \"cuda\"");

        Assert.Equal(["-hwaccel", "cuda", "-i", "/in.mp4"], plan.Arguments.Skip(8).Take(4));
        Assert.Equal(["-hwaccel", "cuda", "-i", "/out.mp4"], decode.Skip(7).Take(4));
    }
}
