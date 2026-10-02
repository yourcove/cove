using Cove.Api.Services;

namespace Cove.Tests;

/// <summary>
/// A conversion measures quality instead of predicting a bitrate: samples of each video are encoded at
/// a candidate setting, scored against the source, and the setting is searched until the score meets
/// the target. These tests pin the search against known score curves, and the commands against what
/// the calibration showed they must do.
/// </summary>
public class VideoQualitySearchTests
{
    private static readonly VideoQualityKnob Nvenc = FfmpegHwAccel.ConversionQualityKnob("hevc_nvenc");

    /// <summary>Runs the search against a score curve and returns it finished.</summary>
    private static VideoQualitySearchState Run(VideoQualityKnob knob, double target, Func<double, double> score)
    {
        var search = new VideoQualitySearchState(knob, target);
        while (search.Next() is { } level)
            search.Record(level, score(level), 1_000_000 / (1 + level));
        return search;
    }

    // ---- the targets, against the judged conversions they came from ----

    /// <summary>
    /// Both "just short of High" verdicts - 1080p flat at 45.50 and 8K VR at 45.54 - must fall below
    /// High, and the level judged acceptable for High (46.69) must meet it. The soft verdict at 44.27
    /// must fall below Balanced, which in turn sits under High.
    /// </summary>
    [Fact]
    public void TargetsSitBetweenTheJudgedVerdicts()
    {
        Assert.True(45.50 < VideoQualitySearch.HighTarget);
        Assert.True(45.54 < VideoQualitySearch.HighTarget);
        Assert.True(46.69 >= VideoQualitySearch.HighTarget);

        Assert.True(44.27 < VideoQualitySearch.BalancedTarget);
        Assert.True(VideoQualitySearch.BalancedTarget < VideoQualitySearch.HighTarget);

        Assert.Equal(VideoQualitySearch.HighTarget, VideoQualitySearch.Target(VideoConversionEffort.HighHardware));
        Assert.Equal(VideoQualitySearch.HighTarget, VideoQualitySearch.Target(VideoConversionEffort.HighSoftware));
        Assert.Equal(VideoQualitySearch.BalancedTarget, VideoQualitySearch.Target(VideoConversionEffort.BalancedHardware));
    }

    // ---- the search ----

    /// <summary>
    /// On a curve shaped like the one measured on 8K VR (about 0.56 dB per CQ step) the search lands on
    /// the highest level that still passes - the smallest file keeping the quality - in a few rounds.
    /// </summary>
    [Fact]
    public void SettlesOnTheHighestPassingLevelInAFewRounds()
    {
        // 60 - 0.55L = 46.5 at L = 24.55, so 24.5 is the highest passing half-step.
        var search = Run(Nvenc, 46.5, level => 60 - 0.55 * level);

        Assert.Equal(24.5, search.Best!.Value.Level);
        Assert.True(search.Rounds.Count <= 3, $"took {search.Rounds.Count} rounds");
    }

    /// <summary>
    /// The knob's expected slope only seeds the first step. A video whose score moves at twice or half
    /// that rate must still end on its own highest passing level, using the slope it measured.
    /// </summary>
    [Theory]
    [InlineData(1.1, 23.5)]
    [InlineData(0.28, 21.0)]
    public void AdaptsToTheSlopeItMeasures(double slope, double expected)
    {
        // The target is crossed 0.2 past the expected level, between half-steps, as a real curve would
        // be; putting it exactly on a step would test floating-point rounding instead of the search.
        var intercept = 46.5 + slope * (expected + 0.2);
        var search = Run(Nvenc, 46.5, level => intercept - slope * level);

        Assert.Equal(expected, search.Best!.Value.Level);
        Assert.True(search.Rounds.Count <= VideoQualitySearch.MaxRounds);
    }

    /// <summary>
    /// Regression from real videos: 6317 scored 46.38 at level 27 against a 46.5 target, and 7652 46.39
    /// at 23.5. The slope put the answer a fraction of a step away, the proposal snapped back onto the
    /// level already tried, and the search stopped with no passing level at all. It must step down.
    /// </summary>
    [Theory]
    [InlineData(27.0, 46.38)]
    [InlineData(23.5, 46.39)]
    public void AMissByLessThanAStepStillTriesTheNextLevel(double missedAt, double scoreThere)
    {
        // A curve through the observed miss at the measured slope of about 0.5 dB per level.
        var search = Run(Nvenc, 46.5, level => scoreThere + 0.5 * (missedAt - level));

        Assert.NotNull(search.Best);
        Assert.Equal(missedAt - Nvenc.Step, search.Best!.Value.Level);
    }

    [Fact]
    public void NeverScoresAWorsePassThanOneItAlreadyHas()
    {
        var search = Run(Nvenc, 46.5, level => 60 - 0.55 * level);

        var best = search.Best!.Value;
        Assert.True(best.Score >= 46.5);
        Assert.DoesNotContain(search.Rounds, round => round.Score >= 46.5 && round.Level > best.Level);
    }

    /// <summary>A source whose best achievable score is below target is reported as unreachable, not encoded anyway.</summary>
    [Fact]
    public void ReportsATargetNoLevelCanReach()
    {
        var search = Run(Nvenc, 46.5, level => 40 - 0.1 * level);

        Assert.Null(search.Best);
        Assert.True(search.Unreachable);
        Assert.Contains(search.Rounds, round => round.Level == Nvenc.Min);
    }

    [Fact]
    public void StopsAtTheSmallestSettingWhenEvenThatPasses()
    {
        var search = Run(Nvenc, 46.5, _ => 60);

        Assert.Equal(Nvenc.Max, search.Best!.Value.Level);
    }

    [Fact]
    public void NeverTriesTheSameLevelTwice()
    {
        var search = Run(Nvenc, 46.5, level => level < 25 ? 47 : 45);   // a cliff, not a slope

        Assert.Equal(search.Rounds.Count, search.Rounds.Select(round => round.Level).Distinct().Count());
        Assert.True(search.Rounds.Count <= VideoQualitySearch.MaxRounds);
    }

    [Fact]
    public void KnobSnapsToTheEncodersStepAndRange()
    {
        Assert.Equal(24.5, Nvenc.Snap(24.4));
        Assert.Equal(Nvenc.Min, Nvenc.Snap(-3));
        Assert.Equal(Nvenc.Max, Nvenc.Snap(99));
    }

    // ---- samples ----

    [Fact]
    public void SamplesAreSpreadThroughTheVideoAwayFromItsEnds()
    {
        var windows = VideoQualitySearch.SampleWindows(600);

        Assert.Equal(VideoQualitySearch.SampleCount, windows.Count);
        Assert.All(windows, window =>
        {
            Assert.Equal(VideoQualitySearch.SampleSeconds, window.Length);
            Assert.InRange(window.Start, 60 - VideoQualitySearch.SampleSeconds, 540);
        });
        Assert.Equal(windows.OrderBy(window => window.Start), windows);
    }

    [Fact]
    public void AShortVideoIsMeasuredWhole()
    {
        Assert.Equal([(0d, 12d)], VideoQualitySearch.SampleWindows(12));
        Assert.Empty(VideoQualitySearch.SampleWindows(0));
    }

    [Fact]
    public void PredictedSizeIncludesTheCopiedAudio()
    {
        Assert.Equal(6_000_000_000L, VideoQualitySearch.PredictBytes(10_000_000, 600, 0));
        Assert.Equal(6_000_000_000L + 128_000 / 8 * 600, VideoQualitySearch.PredictBytes(10_000_000, 600, 128));
        Assert.Equal(0, VideoQualitySearch.PredictBytes(0, 600, 128));
    }

    // ---- scoring ----

    /// <summary>
    /// The regression that made a lossless encode score 81: libvmaf pairs frames by timestamp, and a
    /// millisecond container shifted every third one. Both sides must be renumbered by frame index.
    /// </summary>
    [Fact]
    public void ScoringPairsFramesByIndexOnBothSides()
    {
        var graph = VideoQualitySearch.ScoreFilter(1920, 1080, 30, frameRateChanged: false, isVr: false, "/tmp/log.json");

        Assert.Equal(2, CountOf(graph, "settb=1/30,setpts=N,"));
        Assert.Contains("feature=name=psnr_hvs", graph);
        Assert.DoesNotContain("fps=", graph);
        Assert.DoesNotContain("crop=", graph);
    }

    /// <summary>
    /// A lowered frame rate: the reference takes the same fps filter the encode did, so both select the
    /// same frames. The encode side must not be filtered again.
    /// </summary>
    [Fact]
    public void ScoringGivesTheReferenceTheSameFrameRateChange()
    {
        var graph = VideoQualitySearch.ScoreFilter(1920, 1080, 30, frameRateChanged: true, isVr: false, "/tmp/log.json");

        Assert.StartsWith("[1:v]fps=30,", graph);
        Assert.Equal(1, CountOf(graph, "fps=30"));
    }

    [Fact]
    public void VrIsScoredOnTheLeftEye()
    {
        var graph = VideoQualitySearch.ScoreFilter(7680, 3840, 30, frameRateChanged: true, isVr: true, "/tmp/log.json");

        Assert.Equal(2, CountOf(graph, "crop=3840:3840:0:0,"));
    }

    [Fact]
    public void LogPathIsEscapedForTheFilterGraph()
        => Assert.Equal("C\\:/Temp/it\\'s/score.json", VideoQualitySearch.EscapeFilterPath(@"C:\Temp\it's\score.json"));

    [Fact]
    public void ParsesTheMeanPsnrHvsOfEveryFrame()
    {
        const string json = """{"frames":[{"metrics":{"psnr_hvs":46.0,"vmaf":98}},{"metrics":{"psnr_hvs":47.0}},{"metrics":{"psnr_hvs":45.5}}]}""";

        Assert.Equal(46.1667, VideoQualitySearch.ParsePsnrHvs(json)!.Value, 4);
    }

    /// <summary>The capability check runs ffmpeg; a path that cannot run must read as "cannot measure", not throw.</summary>
    [Fact]
    public void AnFfmpegThatCannotRunCannotMeasure()
        => Assert.False(FfmpegHwAccel.HasQualityMeasurement(Path.Combine(Path.GetTempPath(), $"no-ffmpeg-{Guid.NewGuid():N}.exe")));

    [Fact]
    public void AMeasurementWithoutPsnrHvsFailsVisibly()
        => Assert.Throws<VideoConversionException>(() => VideoQualitySearch.ParsePsnrHvs("""{"frames":[{"metrics":{"vmaf":98}}]}"""));

    /// <summary>
    /// A frame identical to its source scores infinity, written as null. Found on a real 720p video: it
    /// must be skipped, not crash the measurement, and not be mistaken for an ffmpeg that cannot measure.
    /// </summary>
    [Fact]
    public void FramesIdenticalToTheSourceAreLeftOut()
    {
        const string json = """{"frames":[{"metrics":{"psnr_hvs":46.0}},{"metrics":{"psnr_hvs":null}},{"metrics":{"psnr_hvs":47.0}}]}""";

        Assert.Equal(46.5, VideoQualitySearch.ParsePsnrHvs(json)!.Value, 6);
    }

    /// <summary>A sample that encoded perfectly - a black or still stretch - carries no information and is left out of the round.</summary>
    [Fact]
    public void APerfectSampleIsLeftOutOfTheRound()
    {
        Assert.Null(VideoQualitySearch.ParsePsnrHvs("""{"frames":[{"metrics":{"psnr_hvs":null}},{"metrics":{"psnr_hvs":null}}]}"""));

        Assert.Equal(46.0, VideoQualitySearch.RoundScore([45.0, null, 47.0]), 6);
        Assert.Equal(VideoQualitySearch.PerfectScore, VideoQualitySearch.RoundScore([null, null]));
    }

    // ---- encoder arguments ----

    /// <summary>
    /// NVENC in constant-quality mode must be given its peak rate explicitly: left to its default it
    /// capped 8K at about 51 Mbit/s, and every CQ from 18 to 24 produced the same file.
    /// </summary>
    [Fact]
    public void NvencConstantQualityCarriesAnExplicitPeakRate()
    {
        var args = string.Join(" ", FfmpegHwAccel.ConversionQualityArgs("hevc_nvenc", 26.5, VideoConversionEffort.HighHardware, tenBit: false, maxKbps: 150_000));

        Assert.Contains("-rc vbr -cq 26.5 -b:v 0", args);
        Assert.Contains("-maxrate 150000k -bufsize 300000k", args);
    }

    [Theory]
    [InlineData("libx265", "-crf 22")]
    [InlineData("libx264", "-crf 22")]
    [InlineData("libsvtav1", "-crf 22")]
    [InlineData("hevc_qsv", "-global_quality 22")]
    [InlineData("hevc_amf", "-qp_i 22 -qp_p 22 -qp_b 22")]
    [InlineData("hevc_vaapi", "-rc_mode CQP -qp 22")]
    [InlineData("hevc_videotoolbox", "-q:v 78")]   // its scale runs the other way: 100 - 22
    public void EachFamilyGetsItsOwnConstantQualityControl(string encoder, string expected)
    {
        var knob = FfmpegHwAccel.ConversionQualityKnob(encoder);
        var args = string.Join(" ", FfmpegHwAccel.ConversionQualityArgs(encoder, 22, VideoConversionEffort.HighHardware, tenBit: false, maxKbps: 50_000));

        Assert.Contains(expected, args);
        Assert.InRange(knob.Start, knob.Min, knob.Max);
    }

    // ---- sample commands ----

    [Fact]
    public void SampleClipCopiesTheWindowWithoutDecoding()
    {
        var args = string.Join(" ", VideoConversionPlanner.SampleClipArguments("/lib/in.mp4", 0, 120.5, 4, "/tmp/clip0.mkv"));

        Assert.Contains("-ss 120.5 -i /lib/in.mp4 -t 4", args);
        Assert.Contains("-map 0:0 -c copy", args);
        Assert.EndsWith("-f matroska /tmp/clip0.mkv", args);
    }

    /// <summary>A sample is encoded exactly as the whole video will be: same encoder, level and frame-rate filter.</summary>
    [Fact]
    public void SampleEncodeMatchesTheFullEncode()
    {
        var sample = string.Join(" ", VideoConversionPlanner.SampleEncodeArguments(
            "/tmp/clip0.mkv", "/tmp/s.mkv", "hevc_nvenc", 26.5, VideoConversionEffort.HighHardware, false, 150_000, 30, null));
        var quality = string.Join(" ", FfmpegHwAccel.ConversionQualityArgs("hevc_nvenc", 26.5, VideoConversionEffort.HighHardware, false, 150_000));

        Assert.Contains(quality, sample);
        Assert.Contains("-vf fps=30", sample);
        Assert.Contains("-map 0:v:0 -an", sample);
        Assert.Contains("-fps_mode passthrough", sample);
    }

    /// <summary>
    /// The process runner kills an ffmpeg whose stdout stays quiet for minutes as hung. A software sample
    /// of 8K footage can take that long, so both sample commands must report progress on stdout.
    /// </summary>
    [Fact]
    public void SampleCommandsReportProgressSoTheyAreNotMistakenForHung()
    {
        var encode = string.Join(" ", VideoConversionPlanner.SampleEncodeArguments(
            "/tmp/clip0.mkv", "/tmp/s.mkv", "libx265", 22, VideoConversionEffort.HighSoftware, false, 0, null, null));
        var score = string.Join(" ", VideoConversionPlanner.SampleScoreArguments("/tmp/s.mkv", "/tmp/clip0.mkv", 1920, 1080, 30, false, false, "/tmp/l.json"));

        Assert.Contains("-progress pipe:1", encode);
        Assert.Contains("-progress pipe:1", score);
    }

    // ---- savings ----

    [Fact]
    public void SavingIsTheFractionOfTheOriginalReclaimed()
    {
        Assert.Equal(0.70, VideoConversionPlanner.ProjectedSaving(1_000_000, 300_000), 3);
        Assert.Equal(0d, VideoConversionPlanner.ProjectedSaving(0, 300_000));
    }

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        for (var at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
