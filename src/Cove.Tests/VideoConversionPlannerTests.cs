using System.Diagnostics;
using Cove.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public class VideoConversionPlannerTests
{
    private static readonly VideoConversionSettings HevcMp4 = new(
        VideoConversionCodec.Hevc, VideoConversionContainer.Mp4, VideoConversionEffort.HighHardware,
        ReplaceOriginal: false);

    private static ProbedStream Video(string codec, string pixFmt = "yuv420p", int index = 0, bool attachedPicture = false)
        => new(index, "video", codec, pixFmt, 0, attachedPicture, null, null, null);

    private static ProbedStream Stream(int index, string type, string codec)
        => new(index, type, codec, null, 0, false, null, null, null);

    /// <summary>
    /// Only a remux into the container a file is already in is skipped before anything is measured. A
    /// file already in the target codec is not: whether re-encoding it pays depends on its footage, and
    /// the most over-encoded files in a library are often already in the codec you would pick.
    /// </summary>
    [Theory]
    [InlineData("/lib/a.mp4", VideoConversionCodec.Hevc, VideoConversionContainer.Mp4, false)]
    [InlineData("/lib/a.mkv", VideoConversionCodec.Hevc, VideoConversionContainer.Mp4, false)]
    [InlineData("/lib/a.mp4", VideoConversionCodec.Copy, VideoConversionContainer.Mp4, true)]
    [InlineData("/lib/a.m4v", VideoConversionCodec.Copy, VideoConversionContainer.Mp4, true)]
    [InlineData("/lib/a.avi", VideoConversionCodec.Copy, VideoConversionContainer.Mkv, false)]
    public void SkipReason_SkipsOnlyARemuxIntoTheSameContainer(
        string path, VideoConversionCodec target, VideoConversionContainer container, bool skipped)
    {
        var settings = HevcMp4 with { Codec = target, Container = container };

        Assert.Equal(skipped, VideoConversionPlanner.SkipReason(path, settings) is not null);
    }

    /// <summary>
    /// Regression: Build used to work out for itself whether the video could be copied, so a caller that
    /// had decided to re-encode an already-HEVC file still got "-c:v copy" - a plain remux that silently
    /// ignored the quality target and the frame-rate change. The caller's encoder now decides alone:
    /// given one it encodes, given none it copies.
    /// </summary>
    [Fact]
    public void Build_EncodesWhenGivenAnEncoderEvenIfTheCodecAlreadyMatches()
    {
        var source = new ProbedMedia(600, [Video("hevc"), Stream(1, "audio", "aac")]);

        var copied = VideoConversionPlanner.Build(source, "in.mp4", "out.mp4", HevcMp4, encoder: null, null);
        Assert.True(copied.CopiesVideo);
        Assert.Contains("-c:v copy", Line(copied), StringComparison.Ordinal);

        var encoded = VideoConversionPlanner.Build(
            source, "in.mp4", "out.mp4", HevcMp4, "hevc_nvenc", null, qualityLevel: 26.5, maxKbps: 150_000, outputFrameRate: 30);

        Assert.False(encoded.CopiesVideo);
        Assert.DoesNotContain("-c:v copy", Line(encoded), StringComparison.Ordinal);
        Assert.Contains("-c:v hevc_nvenc", Line(encoded), StringComparison.Ordinal);
        Assert.Contains("-cq 26.5", Line(encoded), StringComparison.Ordinal);
        Assert.Contains("fps=30", Line(encoded), StringComparison.Ordinal);
        Assert.Equal("hevc", encoded.ExpectedVideoCodec);
    }

    /// <summary>Changing codec without an encoder is a caller error, not a silent copy of the old codec.</summary>
    [Fact]
    public void Build_RefusesToChangeCodecWithoutAnEncoder()
    {
        var source = new ProbedMedia(600, [Video("h264")]);

        Assert.Throws<ArgumentException>(() => VideoConversionPlanner.Build(source, "in.mp4", "out.mp4", HevcMp4, encoder: null, null));
    }

    /// <summary>
    /// The frame rate is lowered by the fps filter, never -r. Measured on a 59.94 fps source, -r 30 passed
    /// its first frames through before dropping, leaving every later frame three source frames (about
    /// 50 ms) behind its timestamp while the copied audio stayed put.
    /// </summary>
    [Fact]
    public void Build_LowersTheFrameRateWithTheFpsFilter()
    {
        var source = new ProbedMedia(600,
            [new ProbedStream(0, "video", "h264", "yuv420p", 8, false, null, null, null, 20_000, 3840, 2160, 59.94)]);

        var plan = VideoConversionPlanner.Build(
            source, "/in.mp4", "/out.mp4", HevcMp4 with { OutputFrameRate = 30 }, "hevc_nvenc", null,
            qualityLevel: 26, maxKbps: 40_000, outputFrameRate: 30);

        Assert.Contains("-vf fps=30", Line(plan));
        Assert.DoesNotContain(" -r ", Line(plan));
        Assert.Contains("-fps_mode passthrough", Line(plan));
    }

    /// <summary>Without a frame-rate change the source's timestamps pass through untouched, so markers and sprites stay aligned.</summary>
    [Fact]
    public void Build_KeepsTheSourceTimestampsWhenTheFrameRateIsKept()
    {
        var source = new ProbedMedia(600,
            [new ProbedStream(0, "video", "h264", "yuv420p", 8, false, null, null, null, 20_000, 3840, 2160, 59.94)]);

        var plan = VideoConversionPlanner.Build(source, "/in.mp4", "/out.mp4", HevcMp4, "hevc_nvenc", null, qualityLevel: 26, maxKbps: 40_000);

        Assert.Contains("-fps_mode passthrough", Line(plan));
        Assert.DoesNotContain("fps=", Line(plan));
        Assert.DoesNotContain(" -r ", Line(plan));
    }

    [Fact]
    public void ProbedStreamReadsDimensionsAndFrameRate()
    {
        const string json = """
        {
          "streams": [{
            "index": 0, "codec_type": "video", "codec_name": "h264",
            "width": 3840, "height": 2160,
            "avg_frame_rate": "30000/1001", "bit_rate": "54828000"
          }],
          "format": { "duration": "624.0", "bit_rate": "55000000" }
        }
        """;

        var probed = ProbedMedia.Parse(json);
        Assert.NotNull(probed.Video);
        Assert.Equal(3840, probed.Video!.Width);
        Assert.Equal(2160, probed.Video.Height);
        Assert.Equal(29.97, probed.Video.FrameRate, 2);
    }

    [Fact]
    public void ChooseOutputPath_KeepsTheNameWhenOnlyTheExtensionChanges()
    {
        var source = Path.Combine("lib", "clip.mkv");

        Assert.Equal(Path.Combine("lib", "clip.mp4"), VideoConversionPlanner.ChooseOutputPath(source, HevcMp4, _ => false));
    }

    [Fact]
    public void ChooseOutputPath_LabelsTheCodecWhenTheNameIsTheOriginalsOrTaken()
    {
        var source = Path.Combine("lib", "clip.mp4");
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine("lib", "clip.hevc.mp4") };

        Assert.Equal(Path.Combine("lib", "clip.hevc.mp4"), VideoConversionPlanner.ChooseOutputPath(source, HevcMp4, _ => false));
        Assert.Equal(Path.Combine("lib", "clip.hevc-2.mp4"), VideoConversionPlanner.ChooseOutputPath(source, HevcMp4, taken.Contains));
        Assert.Equal(
            Path.Combine("lib", "clip.remux.mp4"),
            VideoConversionPlanner.ChooseOutputPath(source, HevcMp4 with { Codec = VideoConversionCodec.Copy }, _ => false));
    }

    [Fact]
    public void Build_Mp4TargetReencodesIncompatibleAudioDropsImageSubtitlesAndTagsHevc()
    {
        var source = new ProbedMedia(60,
        [
            Video("h264", attachedPicture: true, index: 0),
            Video("h264", index: 1),
            Stream(2, "audio", "pcm_s16le"),
            Stream(3, "audio", "aac"),
            Stream(4, "subtitle", "subrip"),
            Stream(5, "subtitle", "hdmv_pgs_subtitle"),
        ]);

        var plan = VideoConversionPlanner.Build(source, "in.mkv", "out.mp4", HevcMp4, "libx265", decodeInputArgs: null, qualityLevel: 24);

        Assert.False(plan.CopiesVideo);
        Assert.Equal("hevc", plan.ExpectedVideoCodec);
        Assert.Equal(2, plan.AudioStreamCount);
        Assert.Contains("-map 0:1 ", Line(plan)); // the real video, not the cover art
        Assert.DoesNotContain("-map 0:0 ", Line(plan));
        Assert.Contains("-c:a:0 aac -b:a:0 192k", Line(plan));
        Assert.Contains("-c:a:1 copy", Line(plan));
        Assert.Contains("-map 0:4 -c:s:0 mov_text", Line(plan));
        Assert.DoesNotContain("-map 0:5", Line(plan));
        Assert.Contains("-c:v libx265", Line(plan));
        Assert.Contains("-tag:v hvc1", Line(plan));
        Assert.Contains("-fps_mode passthrough", Line(plan));
        Assert.Contains("-f mp4 out.mp4", Line(plan));
        Assert.Equal(2, plan.Notes.Count);
    }

    [Fact]
    public void Build_CopiesTheVideoWhenTheSourceIsAlreadyInTheTargetCodec()
    {
        var source = new ProbedMedia(60, [Video("hevc"), Stream(1, "audio", "flac"), Stream(2, "subtitle", "hdmv_pgs_subtitle")]);

        var plan = VideoConversionPlanner.Build(source, "in.mkv", "out.mkv", HevcMp4 with { Container = VideoConversionContainer.Mkv }, encoder: null, decodeInputArgs: "-hwaccel cuda");

        Assert.True(plan.CopiesVideo);
        Assert.Contains("-c:v copy", Line(plan));
        Assert.Contains("-map 0:2 -c:s:0 copy", Line(plan));
        Assert.Contains("-map 0:t? -c:t copy", Line(plan));
        Assert.DoesNotContain("-hwaccel", Line(plan)); // nothing is decoded in a remux
        Assert.DoesNotContain("hvc1", Line(plan));
        Assert.Contains("-f matroska", Line(plan));
        Assert.Empty(plan.Notes);
    }

    [Fact]
    public void Build_RefusesToCopyAVideoCodecMp4CannotHold()
    {
        var source = new ProbedMedia(60, [Video("wmv3")]);

        var error = Assert.Throws<VideoConversionException>(() => VideoConversionPlanner.Build(
            source, "in.wmv", "out.mp4", HevcMp4 with { Codec = VideoConversionCodec.Copy }, encoder: null, decodeInputArgs: null));
        Assert.Contains("wmv3", error.Message);
    }

    [Fact]
    public void Build_KeepsTenBitAndTheColourDescriptionForHevc()
    {
        var source = new ProbedMedia(60, [new ProbedStream(0, "video", "vp9", "yuv420p10le", 0, false, "bt2020", "smpte2084", "bt2020nc")]);

        var plan = VideoConversionPlanner.Build(source, "in.webm", "out.mp4", HevcMp4, "hevc_nvenc", decodeInputArgs: null, qualityLevel: 26, maxKbps: 40_000);

        Assert.Contains("-pix_fmt p010le -profile:v main10", Line(plan));
        Assert.Contains("-color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc", Line(plan));
    }

    [Fact]
    public void ConversionVideoFilter_UploadsFramesOnlyForVaapi()
    {
        Assert.Equal(["-vf", "format=p010,hwupload"], FfmpegHwAccel.ConversionVideoFilter("hevc_vaapi", tenBit: true));
        Assert.Equal(["-vf", "format=nv12,hwupload"], FfmpegHwAccel.ConversionVideoFilter("h264_vaapi", tenBit: true));
        Assert.Empty(FfmpegHwAccel.ConversionVideoFilter("hevc_nvenc", tenBit: true));
    }

    /// <summary>
    /// A frame-rate change and the VAAPI upload must share one -vf, since a second would replace the
    /// first, and the rate change must come first so it runs before frames move to the GPU.
    /// </summary>
    [Fact]
    public void ConversionVideoFilter_PutsTheFrameRateChangeInTheSameChain()
    {
        Assert.Equal(["-vf", "fps=30"], FfmpegHwAccel.ConversionVideoFilter("hevc_nvenc", tenBit: false, frameRate: 30));
        Assert.Equal(["-vf", "fps=24,format=nv12,hwupload"], FfmpegHwAccel.ConversionVideoFilter("hevc_vaapi", tenBit: false, frameRate: 24));
    }

    [Fact]
    public void HardwareEncodersFor_LimitsAv1ToNvenc()
    {
        Assert.Equal(["av1_nvenc"], FfmpegHwAccel.HardwareEncodersFor(VideoConversionCodec.Av1).Select(item => item.Encoder));
        Assert.Equal(5, FfmpegHwAccel.HardwareEncodersFor(VideoConversionCodec.Hevc).Count);
    }

    [Fact]
    public void VerifyOutput_RejectsAWrongCodecMissingAudioOrDifferentLength()
    {
        var source = new ProbedMedia(100, [Video("h264"), Stream(1, "audio", "aac")]);
        var plan = new VideoConversionPlan([], CopiesVideo: false, ExpectedVideoCodec: "hevc", AudioStreamCount: 1, Notes: []);

        Assert.Null(VideoConversionPlanner.VerifyOutput(source, new ProbedMedia(100.4, [Video("hevc"), Stream(1, "audio", "aac")]), plan));
        Assert.NotNull(VideoConversionPlanner.VerifyOutput(source, new ProbedMedia(100, [Video("h264"), Stream(1, "audio", "aac")]), plan));
        Assert.NotNull(VideoConversionPlanner.VerifyOutput(source, new ProbedMedia(100, [Video("hevc")]), plan));
        Assert.NotNull(VideoConversionPlanner.VerifyOutput(source, new ProbedMedia(97, [Video("hevc"), Stream(1, "audio", "aac")]), plan));
    }

    [Theory]
    [InlineData("out_time_us=2500000", true, 2.5)]
    [InlineData("out_time_ms=1000000", true, 1)]
    [InlineData("out_time_us=N/A", false, 0)]
    [InlineData("progress=continue", false, 0)]
    public void TryParseProgressSeconds_ReadsFfmpegProgressLines(string line, bool parsed, double seconds)
    {
        Assert.Equal(parsed, VideoConversionPlanner.TryParseProgressSeconds(line, out var value));
        Assert.Equal(seconds, value);
    }

    [Fact]
    public void ProbedMedia_ParsesFfprobeJson()
    {
        const string json = """
            {"streams":[
              {"index":0,"codec_name":"mjpeg","codec_type":"video","disposition":{"attached_pic":1}},
              {"index":1,"codec_name":"hevc","codec_type":"video","pix_fmt":"yuv420p10le","color_primaries":"bt2020"},
              {"index":2,"codec_name":"aac","codec_type":"audio"}],
             "format":{"duration":"12.500000"}}
            """;

        var media = ProbedMedia.Parse(json);

        Assert.Equal(12.5, media.Duration);
        Assert.Equal(1, media.Video!.Index);
        Assert.True(VideoConversionPlanner.IsTenBit(media.Video));
        Assert.Single(media.Audio);
    }

    /// <summary>
    /// Runs the planner's command lines through a real ffmpeg: an MKV with PCM audio is re-encoded to an
    /// HEVC MP4 in software, checked the way the job checks it, and decode-verified.
    /// </summary>
    [Fact]
    public async Task RealFfmpeg_ConvertsVerifiesAndDecodeChecksASoftwareHevcEncode()
    {
        var ffmpeg = FfmpegExecutableLocator.FindFfmpeg((string?)null);
        Assert.SkipWhen(ffmpeg is null, "Requires ffmpeg on PATH.");
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg!)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        Assert.SkipWhen(!File.Exists(ffprobe), "Requires ffprobe next to ffmpeg.");
        Assert.SkipWhen(!FfmpegHwAccel.ListEncoders(ffmpeg!).Contains("libx265"), "Requires an ffmpeg build with libx265.");

        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"cove-convert-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "clip.mkv");
            var make = await FfmpegProcessRunner.RunAsync(ffmpeg!,
                ["-hide_banner", "-v", "error", "-f", "lavfi", "-i", "testsrc=size=160x120:rate=25:duration=3", "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
                 "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "pcm_s16le", "-shortest", sourcePath],
                TimeSpan.FromMinutes(1), ct);
            Assert.Equal(0, make.ExitCode);

            var source = ProbedMedia.Parse(await ProbeAsync(ffprobe, sourcePath, ct));
            var outputPath = VideoConversionPlanner.ChooseOutputPath(sourcePath, HevcMp4, File.Exists);
            Assert.Equal(Path.Combine(root, "clip.mp4"), outputPath);

            var plan = VideoConversionPlanner.Build(
                source, sourcePath, outputPath, HevcMp4 with { Effort = VideoConversionEffort.BalancedHardware }, "libx265", null, qualityLevel: 28);
            var positions = new List<double>();
            var encode = await FfmpegProcessRunner.RunWithProgressAsync(ffmpeg!, plan.Arguments,
                line => { if (VideoConversionPlanner.TryParseProgressSeconds(line, out var seconds)) positions.Add(seconds); },
                TimeSpan.FromMinutes(1), ct);
            Assert.True(encode.ExitCode == 0, encode.StandardError);
            Assert.NotEmpty(positions);

            var output = ProbedMedia.Parse(await ProbeAsync(ffprobe, outputPath, ct));
            Assert.Null(VideoConversionPlanner.VerifyOutput(source, output, plan));
            Assert.Equal("aac", output.Audio.Single().CodecName);

            var decode = await FfmpegProcessRunner.RunWithProgressAsync(ffmpeg!, VideoConversionPlanner.DecodeCheckArguments(outputPath, null),
                _ => { }, TimeSpan.FromMinutes(1), ct);
            Assert.Equal(0, decode.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(decode.StandardError), decode.StandardError);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Runs the quality search's own commands through a real ffmpeg: a sample is copied out, encoded at
    /// two quality levels with the frame rate lowered, and scored against its source. A higher-quality
    /// level must score higher, and a near-lossless one close to the top of the scale. This is also what
    /// proves the scoring log path survives filter-graph escaping on this platform.
    /// </summary>
    [Fact]
    public async Task RealFfmpeg_QualitySamplesScoreInTheRightOrder()
    {
        var ffmpeg = FfmpegExecutableLocator.FindFfmpeg((string?)null);
        Assert.SkipWhen(ffmpeg is null, "Requires ffmpeg on PATH.");
        Assert.SkipWhen(!FfmpegHwAccel.ListEncoders(ffmpeg!).Contains("libx265"), "Requires an ffmpeg build with libx265.");
        Assert.SkipWhen(!FfmpegHwAccel.HasQualityMeasurement(ffmpeg!), "Requires an ffmpeg build with libvmaf.");

        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"cove-quality-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.mkv");
            var make = await FfmpegProcessRunner.RunAsync(ffmpeg!,
                ["-hide_banner", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=50:duration=4", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "12", sourcePath],
                TimeSpan.FromMinutes(1), ct);
            Assert.Equal(0, make.ExitCode);

            var window = Assert.Single(VideoQualitySearch.SampleWindows(4));
            var clip = Path.Combine(root, "clip0.mkv");
            var take = await FfmpegProcessRunner.RunAsync(ffmpeg!,
                VideoConversionPlanner.SampleClipArguments(sourcePath, 0, window.Start, window.Length, clip), TimeSpan.FromMinutes(1), ct);
            Assert.True(take.ExitCode == 0, take.StandardError);

            async Task<double> ScoreAt(double level)
            {
                var encoded = Path.Combine(root, $"sample-{level}.mkv");
                var encode = await FfmpegProcessRunner.RunAsync(ffmpeg!,
                    VideoConversionPlanner.SampleEncodeArguments(clip, encoded, "libx265", level, VideoConversionEffort.BalancedSoftware, false, 0, 25, null),
                    TimeSpan.FromMinutes(1), ct);
                Assert.True(encode.ExitCode == 0, encode.StandardError);

                var log = Path.Combine(root, $"score-{level}.json");
                var score = await FfmpegProcessRunner.RunAsync(ffmpeg!,
                    VideoConversionPlanner.SampleScoreArguments(encoded, clip, 640, 360, 25, frameRateChanged: true, isVr: false, log),
                    TimeSpan.FromMinutes(1), ct);
                Assert.True(score.ExitCode == 0, score.StandardError);
                return VideoQualitySearch.ParsePsnrHvs(await File.ReadAllTextAsync(log, ct))!.Value;
            }

            var fine = await ScoreAt(8);
            var coarse = await ScoreAt(38);
            Assert.True(fine > coarse + 3, $"crf 8 scored {fine:0.00}, crf 38 scored {coarse:0.00}");
            Assert.True(fine > 45, $"a near-lossless sample scored only {fine:0.00}, which suggests the frames are misaligned");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RealFfmpeg_DecodeCheckReportsATruncatedFile()
    {
        var ffmpeg = FfmpegExecutableLocator.FindFfmpeg((string?)null);
        Assert.SkipWhen(ffmpeg is null, "Requires ffmpeg on PATH.");

        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"cove-convert-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "clip.mkv");
            var make = await FfmpegProcessRunner.RunAsync(ffmpeg!,
                ["-hide_banner", "-v", "error", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25:duration=4", "-c:v", "libx264", "-preset", "ultrafast", path],
                TimeSpan.FromMinutes(1), ct);
            Assert.Equal(0, make.ExitCode);

            // Cut the file off mid-stream and scribble over part of what is left.
            var bytes = await File.ReadAllBytesAsync(path, ct);
            var damaged = bytes[..(bytes.Length * 2 / 3)];
            for (var i = damaged.Length / 2; i < damaged.Length / 2 + 2000 && i < damaged.Length; i++)
                damaged[i] ^= 0x5A;
            await File.WriteAllBytesAsync(path, damaged, ct);

            var decode = await FfmpegProcessRunner.RunWithProgressAsync(ffmpeg!, VideoConversionPlanner.DecodeCheckArguments(path, null),
                _ => { }, TimeSpan.FromMinutes(1), ct);

            Assert.True(decode.ExitCode != 0 || !string.IsNullOrWhiteSpace(decode.StandardError));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SelectConversionEncoder_HonoursHardwareAccelerationOff()
    {
        var ffmpeg = FfmpegExecutableLocator.FindFfmpeg((string?)null);
        Assert.SkipWhen(ffmpeg is null, "Requires ffmpeg on PATH.");
        Assert.SkipWhen(!FfmpegHwAccel.ListEncoders(ffmpeg!).Contains("libx265"), "Requires an ffmpeg build with libx265.");

        Assert.Equal("libx265", FfmpegHwAccel.SelectConversionEncoder(ffmpeg!, VideoConversionCodec.Hevc, "off", preferHardware: true, NullLogger.Instance));
    }

    private static string Line(VideoConversionPlan plan) => string.Join(" ", plan.Arguments);

    private static async Task<string> ProbeAsync(string ffprobe, string path, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path })
            startInfo.ArgumentList.Add(arg);
        using var process = Process.Start(startInfo)!;
        var json = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        Assert.Equal(0, process.ExitCode);
        return json;
    }
}
