using System.Diagnostics;
using System.Globalization;
using Cove.Api.Services;

namespace Cove.Tests;

/// <summary>
/// Cutting parts out of a video's timeline: the range arithmetic, how everything timed moves, and - with a
/// real ffmpeg - that the frames which come out are exactly the ones that were meant to be kept.
/// </summary>
public class VideoCutTests
{
    private static readonly VideoConversionSettings CopyMp4 = new(
        VideoConversionCodec.Copy, VideoConversionContainer.Mp4, VideoConversionEffort.HighHardware, ReplaceOriginal: false);

    private static TimeRange R(double start, double end) => new(start, end);

    // ---- what a removal keeps ----

    [Fact]
    public void RemovalsAreClampedMergedAndInverted()
    {
        var (kept, error) = VideoCut.KeepAfterRemoving([R(8, 9.5), R(-5, 2), R(3, 5), R(4, 6), R(58, 99)], 60);

        Assert.Null(error);
        Assert.Equal([R(2, 3), R(6, 8), R(9.5, 58)], kept!);
    }

    /// <summary>A sliver left between two removals is an accident of dragging, not something to write out.</summary>
    [Fact]
    public void SliversBetweenRemovalsAreDropped()
    {
        var (kept, _) = VideoCut.KeepAfterRemoving([R(0, 10), R(10.1, 20)], 30);

        Assert.Equal([R(20, 30)], kept!);
    }

    [Theory]
    [InlineData(0d, 60d, 60d, "entire video")]
    [InlineData(70d, 80d, 60d, "Nothing to cut")]
    [InlineData(5d, 5d, 60d, "must end after it starts")]
    [InlineData(5d, 10d, 0d, "length is unknown")]
    public void ImpossibleCutsAreRefusedWithAReason(double start, double end, double duration, string reason)
    {
        var (kept, error) = VideoCut.KeepAfterRemoving([R(start, end)], duration);

        Assert.Null(kept);
        Assert.Contains(reason, error);
    }

    // ---- lossless snapping ----

    /// <summary>
    /// A lossless cut can only start a kept part on a keyframe. Starts move BACK, so the cut keeps a
    /// little more than asked and never loses footage that was meant to stay; parts that grow into each
    /// other are joined.
    /// </summary>
    [Fact]
    public void LosslessStartsMoveBackToTheirKeyframeAndNeverForward()
    {
        double Keyframe(double t) => Math.Floor(t / 2) * 2;   // a keyframe every 2 s

        var snapped = VideoCut.SnapStartsToKeyframes([R(0, 3), R(5.5, 8), R(8.4, 12), R(13, 20)], Keyframe);

        // 5.5 and 8.4 fall back to 4 and 8, and 13 to 12 - which makes the last three parts continuous.
        Assert.Equal([R(0, 3), R(4, 20)], snapped);
        Assert.Equal(3 + 16, VideoCut.OutputDuration(snapped));
    }

    // ---- how timed items move ----

    // Kept [0,10] and [20,30]: the output is 20 s long, with the join at 10.
    private static readonly IReadOnlyList<TimeRange> TwoParts = [R(0, 10), R(20, 30)];

    [Theory]
    [InlineData(5d, 5d)]      // before the cut: unchanged
    [InlineData(25d, 15d)]    // after the cut: moves earlier by what was removed
    [InlineData(20d, 10d)]    // on the far edge of the cut: lands on the join
    public void KeptTimesMoveByWhatWasRemovedBeforeThem(double source, double output)
        => Assert.Equal(output, VideoCut.MapTime(source, TwoParts, towardsLater: true));

    [Fact]
    public void AnItemInsideARemovedPartIsGone()
        => Assert.Null(VideoCut.MapRange(12, 18, TwoParts));

    /// <summary>An item spanning a cut stays, joined across it: its two kept parts now play back to back.</summary>
    [Fact]
    public void AnItemSpanningACutIsJoinedAcrossIt()
        => Assert.Equal(R(5, 15), VideoCut.MapRange(5, 25, TwoParts));

    [Theory]
    [InlineData(12d, 25d, 10d, 15d)]   // starts inside the cut: begins at the join
    [InlineData(5d, 15d, 5d, 10d)]     // ends inside the cut: ends at the join
    public void AnItemHalfInsideACutKeepsItsKeptHalf(double start, double end, double mappedStart, double mappedEnd)
        => Assert.Equal(R(mappedStart, mappedEnd), VideoCut.MapRange(start, end, TwoParts));

    [Theory]
    [InlineData(15d, null)]   // an instant inside the cut is gone
    [InlineData(10d, 10d)]    // on a kept edge it stays
    [InlineData(27d, 17d)]
    public void AnInstantSurvivesOnlyIfItIsKept(double at, double? mapped)
        => Assert.Equal(mapped is { } m ? R(m, m) : null, VideoCut.MapRange(at, at, TwoParts));

    [Fact]
    public void OutputTimesTraceBackToTheirSource()
    {
        Assert.Equal(5, VideoCut.SourceTime(5, TwoParts));
        Assert.Equal(25, VideoCut.SourceTime(15, TwoParts));
    }

    /// <summary>
    /// Quality samples come only from footage that is kept - a removed scene must not decide the setting
    /// for the rest - and each lies inside one kept part, since a sample is a single clip of the source.
    /// </summary>
    [Fact]
    public void QualitySamplesComeOnlyFromKeptFootageAndNeverCrossAJoin()
    {
        IReadOnlyList<TimeRange> kept = [R(0, 100), R(400, 460), R(900, 1200)];

        var windows = VideoCut.SampleWindows(kept);

        Assert.Equal(VideoQualitySearch.SampleCount, windows.Count);
        Assert.All(windows, window => Assert.Contains(kept, range => window.Start >= range.Start && window.Start + window.Length <= range.End + 1e-9));
    }

    // ---- commands ----

    [Fact]
    public void ConcatListPlacesEachKeptPartAtItsFileTimestamps()
    {
        var list = VideoConversionPlanner.CutConcatList(@"C:\lib\it's.mp4", [R(0, 3.5), R(5, 8)], startTime: 1.25);

        Assert.Equal(
            "ffconcat version 1.0\n"
            + "file 'C:/lib/it'\\''s.mp4'\ninpoint 1.25\noutpoint 4.75\n"
            + "file 'C:/lib/it'\\''s.mp4'\ninpoint 6.25\noutpoint 9.25\n",
            list);
    }

    // ---- real ffmpeg ----

    /// <summary>
    /// A lossless cut, end to end: every frame that comes out is the exact source frame the snapped kept
    /// ranges say it should be, in order, and the result passes the job's own output check.
    /// </summary>
    [Fact]
    public async Task RealFfmpeg_LosslessCutKeepsExactlyTheSnappedFrames()
    {
        var (ffmpeg, ffprobe) = Tools();
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("cove-cut-");
        try
        {
            var source = await MakeSourceAsync(ffmpeg, root.FullName, "source.mp4", ct);
            var probed = await ProbeAsync(ffprobe, source, ct);
            var (kept, _) = VideoCut.KeepAfterRemoving([R(3.3, 5.5), R(8.2, 9.4)], probed.Duration);
            var snapped = new List<TimeRange>();
            foreach (var range in kept!)
            {
                var start = await VideoKeyframes.FindAtOrBeforeAsync(ffprobe, source, probed.Video!.Index, range.Start, probed.StartTime, ct);
                snapped.Add(range with { Start = start });
            }
            Assert.Equal([0d, 5d, 9d], snapped.Select(range => Math.Round(range.Start, 3)));

            var list = Path.Combine(root.FullName, "cut.ffconcat");
            await File.WriteAllTextAsync(list, VideoConversionPlanner.CutConcatList(source, snapped, probed.StartTime), ct);
            var output = Path.Combine(root.FullName, "cut.mp4");
            var plan = VideoConversionPlanner.Build(probed, source, output, CopyMp4, encoder: null, null, cut: new VideoCutPlan(snapped, list));
            var run = await FfmpegProcessRunner.RunAsync(ffmpeg, plan.Arguments, TimeSpan.FromMinutes(1), ct);
            Assert.True(run.ExitCode == 0, run.StandardError);

            Assert.Null(VideoConversionPlanner.VerifyOutput(probed, await ProbeAsync(ffprobe, output, ct), plan));
            var sourceFrames = await FrameHashesAsync(ffmpeg, source, ct);
            var expected = sourceFrames[0..33].Concat(sourceFrames[50..82]).Concat(sourceFrames[90..120]).ToList();
            Assert.Equal(expected, await FrameHashesAsync(ffmpeg, output, ct));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A re-encode joining several parts is frame-exact: each output frame is compared with the source
    /// frame that should be there. A slip of even one frame on this moving pattern drops PSNR far below
    /// what a faithful encode scores.
    /// </summary>
    [Fact]
    public async Task RealFfmpeg_ReencodedCutAcrossSeveralPartsIsFrameExact()
    {
        var (ffmpeg, ffprobe) = Tools();
        Assert.SkipWhen(!FfmpegHwAccel.ListEncoders(ffmpeg).Contains("libx264"), "Requires libx264.");
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("cove-cut-");
        try
        {
            var source = await MakeSourceAsync(ffmpeg, root.FullName, "source.mp4", ct);
            var probed = await ProbeAsync(ffprobe, source, ct);
            var (kept, _) = VideoCut.KeepAfterRemoving([R(3.3, 5.5), R(8.2, 9.4)], probed.Duration);
            var output = Path.Combine(root.FullName, "cut.mp4");
            var settings = CopyMp4 with { Codec = VideoConversionCodec.H264, Effort = VideoConversionEffort.BalancedSoftware };
            var plan = VideoConversionPlanner.Build(probed, source, output, settings, "libx264", null, qualityLevel: 12, cut: new VideoCutPlan(kept!));
            Assert.Contains("concat=n=3:v=1:a=1", plan.Arguments);
            var run = await FfmpegProcessRunner.RunAsync(ffmpeg, plan.Arguments, TimeSpan.FromMinutes(2), ct);
            Assert.True(run.ExitCode == 0, run.StandardError);

            Assert.Null(VideoConversionPlanner.VerifyOutput(probed, await ProbeAsync(ffprobe, output, ct), plan));
            Assert.Equal(8.6, plan.ExpectedDuration!.Value, 3);
            // Frames 0-32, 55-81 and 94-119 of the 10 fps source are the ones kept.
            var psnr = await PsnrAgainstAsync(ffmpeg, output, source,
                "select='between(n\\,0\\,32)+between(n\\,55\\,81)+between(n\\,94\\,119)',setpts=N/10/TB", ct);
            Assert.True(psnr > 38, $"Output frames matched the kept source frames at only {psnr:0.0} dB.");
            var misaligned = await PsnrAgainstAsync(ffmpeg, output, source,
                "select='between(n\\,0\\,32)+between(n\\,54\\,80)+between(n\\,93\\,118)',setpts=N/10/TB", ct);
            Assert.True(psnr > misaligned + 5, $"One frame off scored {misaligned:0.0} dB against {psnr:0.0} dB aligned.");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A cut can lower the frame rate in the same encode, whether it joins several parts or keeps one:
    /// the kept footage comes out at the new rate and still has the cut's length.
    /// </summary>
    [Theory]
    [InlineData(3.3, 5.5)]
    [InlineData(0, 2)]
    public async Task RealFfmpeg_ReencodedCutCanLowerTheFrameRate(double removeStart, double removeEnd)
    {
        var (ffmpeg, ffprobe) = Tools();
        Assert.SkipWhen(!FfmpegHwAccel.ListEncoders(ffmpeg).Contains("libx264"), "Requires libx264.");
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("cove-cut-");
        try
        {
            var source = await MakeSourceAsync(ffmpeg, root.FullName, "source.mp4", ct);
            var probed = await ProbeAsync(ffprobe, source, ct);
            var (kept, _) = VideoCut.KeepAfterRemoving([R(removeStart, removeEnd), R(8.2, 9.4)], probed.Duration);
            var output = Path.Combine(root.FullName, "cut.mp4");
            var settings = CopyMp4 with { Codec = VideoConversionCodec.H264, Effort = VideoConversionEffort.BalancedSoftware, OutputFrameRate = 5 };
            var plan = VideoConversionPlanner.Build(probed, source, output, settings, "libx264", null, qualityLevel: 20,
                outputFrameRate: 5, cut: new VideoCutPlan(kept!));
            var run = await FfmpegProcessRunner.RunAsync(ffmpeg, plan.Arguments, TimeSpan.FromMinutes(2), ct);
            Assert.True(run.ExitCode == 0, run.StandardError);

            var result = await ProbeAsync(ffprobe, output, ct);
            Assert.Null(VideoConversionPlanner.VerifyOutput(probed, result, plan));
            Assert.Equal(5, result.Video!.FrameRate, 2);
            Assert.Equal(VideoCut.OutputDuration(kept!) * 5, (await FrameHashesAsync(ffmpeg, output, ct)).Count, 1.0);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Trimming only the start or end re-encodes the video but copies the audio untouched.</summary>
    [Fact]
    public async Task RealFfmpeg_ReencodedSinglePartCopiesTheAudio()
    {
        var (ffmpeg, ffprobe) = Tools();
        Assert.SkipWhen(!FfmpegHwAccel.ListEncoders(ffmpeg).Contains("libx264"), "Requires libx264.");
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("cove-cut-");
        try
        {
            var source = await MakeSourceAsync(ffmpeg, root.FullName, "source.mp4", ct);
            var probed = await ProbeAsync(ffprobe, source, ct);
            var (kept, _) = VideoCut.KeepAfterRemoving([R(0, 2), R(11, 12)], probed.Duration);
            var output = Path.Combine(root.FullName, "cut.mp4");
            var settings = CopyMp4 with { Codec = VideoConversionCodec.H264, Effort = VideoConversionEffort.BalancedSoftware };
            var plan = VideoConversionPlanner.Build(probed, source, output, settings, "libx264", null, qualityLevel: 20, cut: new VideoCutPlan(kept!));

            Assert.Contains("-c:a:0 copy", plan.Arguments);
            Assert.DoesNotContain("concat", plan.Arguments);
            var run = await FfmpegProcessRunner.RunAsync(ffmpeg, plan.Arguments, TimeSpan.FromMinutes(2), ct);
            Assert.True(run.ExitCode == 0, run.StandardError);
            Assert.Null(VideoConversionPlanner.VerifyOutput(probed, await ProbeAsync(ffprobe, output, ct), plan));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A file whose timestamps start at 5 s. The concat points are raw file timestamps while the timeline
    /// counts from the file's start; the cut must still keep exactly the right frames.
    /// </summary>
    [Fact]
    public async Task RealFfmpeg_LosslessCutHonoursAFilesStartTime()
    {
        var (ffmpeg, ffprobe) = Tools();
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("cove-cut-");
        try
        {
            var plain = await MakeSourceAsync(ffmpeg, root.FullName, "plain.mp4", ct);
            var source = Path.Combine(root.FullName, "offset.mp4");
            var shift = await FfmpegProcessRunner.RunAsync(ffmpeg,
                $"-hide_banner -v error -y -i \"{plain}\" -c copy -output_ts_offset 5 \"{source}\"", TimeSpan.FromMinutes(1), ct);
            Assert.Equal(0, shift.ExitCode);
            var probed = await ProbeAsync(ffprobe, source, ct);
            Assert.InRange(probed.StartTime, 4.9, 5.1);

            var (kept, _) = VideoCut.KeepAfterRemoving([R(3.3, 5.5)], probed.Duration);
            var snapped = new List<TimeRange>();
            foreach (var range in kept!)
                snapped.Add(range with { Start = await VideoKeyframes.FindAtOrBeforeAsync(ffprobe, source, probed.Video!.Index, range.Start, probed.StartTime, ct) });
            var list = Path.Combine(root.FullName, "cut.ffconcat");
            await File.WriteAllTextAsync(list, VideoConversionPlanner.CutConcatList(source, snapped, probed.StartTime), ct);
            var output = Path.Combine(root.FullName, "cut.mp4");
            var plan = VideoConversionPlanner.Build(probed, source, output, CopyMp4, encoder: null, null, cut: new VideoCutPlan(snapped, list));
            var run = await FfmpegProcessRunner.RunAsync(ffmpeg, plan.Arguments, TimeSpan.FromMinutes(1), ct);
            Assert.True(run.ExitCode == 0, run.StandardError);

            var sourceFrames = await FrameHashesAsync(ffmpeg, plain, ct);
            Assert.Equal(sourceFrames[0..33].Concat(sourceFrames[50..120]).ToList(), await FrameHashesAsync(ffmpeg, output, ct));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    // ---- helpers ----

    private static (string Ffmpeg, string Ffprobe) Tools()
    {
        var ffmpeg = FfmpegHwAccel.FindFfmpeg(null);
        Assert.SkipWhen(ffmpeg is null, "Requires ffmpeg on PATH.");
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg!)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        Assert.SkipWhen(!File.Exists(ffprobe), "Requires ffprobe next to ffmpeg.");
        return (ffmpeg!, ffprobe);
    }

    /// <summary>12 s of a moving pattern at 10 fps with a keyframe every second, plus a tone.</summary>
    private static async Task<string> MakeSourceAsync(string ffmpeg, string directory, string name, CancellationToken ct)
    {
        var path = Path.Combine(directory, name);
        var make = await FfmpegProcessRunner.RunAsync(ffmpeg,
            "-hide_banner -v error -y -f lavfi -i testsrc2=size=320x180:rate=10:duration=12 -f lavfi -i sine=frequency=440:duration=12 "
            + $"-c:v libx264 -preset ultrafast -crf 16 -g 10 -keyint_min 10 -sc_threshold 0 -c:a aac -shortest \"{path}\"",
            TimeSpan.FromMinutes(1), ct);
        Assert.True(make.ExitCode == 0, make.StandardError);
        return path;
    }

    private static async Task<ProbedMedia> ProbeAsync(string ffprobe, string path, CancellationToken ct)
        => ProbedMedia.Parse(await CaptureAsync(ffprobe, $"-v quiet -print_format json -show_format -show_streams \"{path}\"", ct));

    private static async Task<List<string>> FrameHashesAsync(string ffmpeg, string path, CancellationToken ct)
        => [.. (await CaptureAsync(ffmpeg, $"-hide_banner -v error -i \"{path}\" -map 0:v:0 -f framemd5 -", ct))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#'))
            .Select(line => line.Split(',')[^1].Trim())];

    private static async Task<double> PsnrAgainstAsync(string ffmpeg, string distorted, string reference, string referenceFilter, CancellationToken ct)
    {
        var stderr = await CaptureStderrAsync(ffmpeg,
            $"-hide_banner -i \"{distorted}\" -i \"{reference}\" -filter_complex \"[1:v]{referenceFilter}[r];[0:v]setpts=N/10/TB[d];[d][r]psnr\" -f null -", ct);
        var marker = stderr.LastIndexOf("average:", StringComparison.Ordinal);
        Assert.True(marker >= 0, stderr);
        var value = stderr[(marker + 8)..].Split(' ', 2)[0];
        return value == "inf" ? 100 : double.Parse(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string> CaptureAsync(string exe, string args, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        Assert.True(process.ExitCode == 0, await stderr);
        return await stdout;
    }

    private static async Task<string> CaptureStderrAsync(string exe, string args, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        await stdout;
        return await stderr;
    }
}
