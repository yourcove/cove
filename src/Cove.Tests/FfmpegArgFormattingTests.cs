using System.Globalization;
using Cove.Api.Services;

namespace Cove.Tests;

/// <summary>
/// Guards against the locale bug where ffmpeg seek/duration arguments were formatted with the current
/// culture: on a comma-decimal locale (de-DE, pt-BR, …) "-ss 697.91" became "-ss 697,91", which ffmpeg
/// rejects ("Invalid duration for option ss", exit -22), breaking generation for every video.
/// </summary>
public class FfmpegArgFormattingTests
{
    [Theory]
    [InlineData("de-DE")]   // comma decimal separator
    [InlineData("pt-BR")]   // comma decimal separator
    [InlineData("fr-FR")]   // comma decimal separator
    [InlineData("en-US")]   // period — control
    public void FrameExtractArgs_AlwaysUseInvariantDecimalSeparator(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            var args = VideoFrameBatchExtractor.BuildBatchArguments(
                "/media/clip.mp4", "/tmp/frames", [697.91, 1234.5], start: 0, count: 2, scaleWidth: 320);

            Assert.Contains("-ss 697.910", args);
            Assert.Contains("-ss 1234.500", args);
            Assert.DoesNotContain("697,91", args);
            Assert.DoesNotContain("1234,5", args);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// Each timestamp must get its own input and its own single-frame output. Without the per-input
    /// mapping, one input's frames would satisfy every output and the sprite/pHash would be built
    /// from the wrong frames — a failure that produces plausible-looking output, not an error.
    /// </summary>
    [Fact]
    public void FrameExtractArgs_MapEachInputToItsOwnSingleFrameOutput()
    {
        var args = VideoFrameBatchExtractor.BuildBatchArguments(
            "/media/clip.mp4", "/tmp/frames", [10, 20, 30], start: 0, count: 3, scaleWidth: 160);

        Assert.Equal(3, Occurrences(args, "-i \"/media/clip.mp4\""));
        Assert.Contains("-map 0:v:0", args);
        Assert.Contains("-map 1:v:0", args);
        Assert.Contains("-map 2:v:0", args);
        Assert.Equal(3, Occurrences(args, "-frames:v 1"));
        Assert.Contains("frame_0000.jpg", args);
        Assert.Contains("frame_0002.jpg", args);
    }

    /// <summary>
    /// The scale/quality/pixel-format triple defines the pixels a pHash is computed from. Changing
    /// any of them re-hashes the entire library without warning, so they are pinned here.
    /// </summary>
    [Fact]
    public void FrameExtractArgs_PinTheOutputFormatThatPhashesDependOn()
    {
        var args = VideoFrameBatchExtractor.BuildBatchArguments(
            "/media/clip.mp4", "/tmp/frames", [10], start: 0, count: 1, scaleWidth: 160);

        Assert.Contains("-vf \"scale=160:-2\"", args);
        Assert.Contains("-q:v 3", args);
        Assert.Contains("-pix_fmt yuvj420p", args);
        // Caps the per-output mjpeg encoder. Without it a 24-output batch spawns hundreds of
        // encoder threads and blows past whatever concurrency the user configured.
        Assert.Contains("-frames:v 1 -vf \"scale=160:-2\" -threads 1", args);
    }

    /// <summary>
    /// A VR sprite sheet flattens one eye before scaling; the reprojection goes ahead of the scale in
    /// the same filter chain, and callers that pass nothing (pHash) get the pinned scale-only chain.
    /// </summary>
    [Fact]
    public void FrameExtractArgs_PutThePreFilterAheadOfTheScale()
    {
        var args = VideoFrameBatchExtractor.BuildBatchArguments(
            "/media/vr.mp4", "/tmp/frames", [10], start: 0, count: 1, scaleWidth: 160,
            preFilter: "v360=input=he:output=flat:in_stereo=sbs:out_stereo=2d:h_fov=100:v_fov=67.67:w=160:h=90");

        Assert.Contains("-vf \"v360=input=he:output=flat:in_stereo=sbs:out_stereo=2d:h_fov=100:v_fov=67.67:w=160:h=90,scale=160:-2\"", args);
    }

    /// <summary>A non-positive scale width keeps the source resolution (used by thumbnails).</summary>
    [Fact]
    public void FrameExtractArgs_OmitScaleFilterWhenWidthIsNotPositive()
    {
        var args = VideoFrameBatchExtractor.BuildBatchArguments(
            "/media/clip.mp4", "/tmp/frames", [10], start: 0, count: 1, scaleWidth: 0);

        Assert.DoesNotContain("scale=", args);
    }

    /// <summary>
    /// Windows rejects command lines over 32767 characters. A library with long paths must still
    /// extract, so oversized batches are split rather than failing at process start.
    /// </summary>
    [Fact]
    public void BatchPlanner_SplitsBatchesThatWouldOverflowTheCommandLine()
    {
        var longPath = "/media/" + new string('x', 900) + "/clip.mp4";

        var plans = VideoFrameBatchExtractor
            .PlanBatches(longPath, "/tmp/frames", timestampCount: 81, scaleWidth: 160, batchSize: 24)
            .ToList();

        var timestamps = Enumerable.Range(0, 81).Select(i => (double)i).ToArray();
        Assert.All(plans, plan => Assert.True(
            VideoFrameBatchExtractor.BuildBatchArguments(
                longPath, "/tmp/frames", timestamps, plan.Start, plan.Count, 160).Length < 32767));
        Assert.Equal(81, plans.Sum(plan => plan.Count));
        Assert.True(
            plans.Max(plan => plan.Count) < VideoFrameBatchExtractor.DefaultBatchSize,
            "a 900-character source path should force batches smaller than the default");
    }

    /// <summary>Every timestamp is covered exactly once, in order, with no gaps or overlaps.</summary>
    [Fact]
    public void BatchPlanner_CoversEveryTimestampExactlyOnce()
    {
        var plans = VideoFrameBatchExtractor
            .PlanBatches("/media/clip.mp4", "/tmp/frames", timestampCount: 81, scaleWidth: 160, batchSize: 24)
            .ToList();

        var covered = plans.SelectMany(plan => Enumerable.Range(plan.Start, plan.Count)).ToList();
        Assert.Equal(Enumerable.Range(0, 81), covered);
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
