using Cove.Core.Services;

namespace Cove.Tests;

public class VideoAlignmentTests
{
    [Fact]
    public void FindsAnIntroOffsetAndDoesNotExtrapolate()
    {
        var source = Samples(0);
        var target = Samples(12);
        var anchors = VideoAlignment.FindAnchors(source, target, 1, TestContext.Current.CancellationToken);
        Assert.True(anchors.Count >= 3);
        Assert.All(anchors, a => Assert.Equal(12, a.TargetSec - a.SourceSec));
        Assert.Null(VideoAlignment.MapRange(0, 2, anchors));
        Assert.Equal(32, VideoAlignment.MapRange(20, 25, anchors)!.StartSec);
    }

    [Fact]
    public void DeletedPassageCreatesSeparateSections()
    {
        var source = Samples(0);
        var target = source.Where(s => s.TimeSec < 35 || s.TimeSec >= 55)
            .Select(s => s with { TimeSec = s.TimeSec >= 55 ? s.TimeSec - 20 : s.TimeSec }).ToList();
        var anchors = VideoAlignment.FindAnchors(source, target, 1, TestContext.Current.CancellationToken);
        Assert.True(anchors.Select(a => a.Section).Distinct().Count() >= 2);
        Assert.Null(VideoAlignment.MapRange(30, 60, anchors));
        Assert.NotNull(VideoAlignment.MapRange(60, 70, anchors));
    }

    [Fact]
    public void RepeatedOrBlankFramesDoNotEstablishAlignment()
    {
        var repeated = Enumerable.Range(0, 50).Select(i => new AlignmentSample(i, 123UL, 40, "")).ToList();
        Assert.Empty(VideoAlignment.FindAnchors(repeated, repeated, 1, TestContext.Current.CancellationToken));
        var blank = Samples(0).Select(s => s with { Contrast = 0 }).ToList();
        Assert.Empty(VideoAlignment.FindAnchors(blank, blank, 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ShortDeletedPassageIsNotInterpolatedAsMatchingContent()
    {
        var source = Samples(0);
        var target = source.Where(s => s.TimeSec < 20 || s.TimeSec >= 22)
            .Select(s => s with { TimeSec = s.TimeSec >= 22 ? s.TimeSec - 2 : s.TimeSec }).ToList();
        var anchors = VideoAlignment.FindAnchors(source, target, 1, TestContext.Current.CancellationToken);
        Assert.True(anchors.Select(a => a.Section).Distinct().Count() >= 2);
        Assert.Null(VideoAlignment.MapRange(20, 21, anchors));
    }

    [Fact]
    public void ShortInsertedPassageSplitsMatchingSections()
    {
        var source = Samples(0);
        var target = source.Select(s => s with { TimeSec = s.TimeSec >= 20 ? s.TimeSec + 2 : s.TimeSec }).ToList();
        var anchors = VideoAlignment.FindAnchors(source, target, 1, TestContext.Current.CancellationToken);
        Assert.Null(VideoAlignment.MapRange(19, 21, anchors));
        Assert.NotNull(VideoAlignment.MapRange(25, 30, anchors));
    }

    [Fact]
    public void ManualAnchorsHandleSpeedChangesAndRejectInvalidMaps()
    {
        AlignmentAnchor[] anchors = [new(10, 20, 0), new(110, 124, 0)];
        var mapped = VideoAlignment.MapRange(35, 60, anchors)!;
        Assert.Equal(46, mapped.StartSec, 6);
        Assert.Equal(72, mapped.EndSec, 6);
        Assert.Null(VideoAlignment.Validate(anchors, 120, 130));
        Assert.NotNull(VideoAlignment.Validate([new(10, 20, 0), new(20, 10, 0)], 120, 130));
        Assert.NotNull(VideoAlignment.Validate([new(double.NaN, 10, 0), new(20, 30, 0)], 120, 130));
        Assert.NotNull(VideoAlignment.Validate([new(10, 10, 0)], 120, 130));
        Assert.Null(VideoAlignment.MapRange(5, 10, anchors));
    }

    [Fact]
    public void SpatialDescriptorsDistinguishFramesWithSimilarGradientHashes()
    {
        var random = new Random(9);
        var source = Enumerable.Range(0, 30).Select(i =>
        {
            var descriptor = new byte[144]; random.NextBytes(descriptor);
            return new AlignmentSample(i, 0, 40, "", Convert.ToBase64String(descriptor));
        }).ToList();
        var target = source.Select(s => s with { TimeSec = s.TimeSec + 6 }).ToList();
        var anchors = VideoAlignment.FindAnchors(source, target, 1, TestContext.Current.CancellationToken);
        Assert.True(anchors.Count >= 3);
        Assert.All(anchors, a => Assert.Equal(6, a.TargetSec - a.SourceSec));
    }

    [Fact]
    public void MatchingObservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => VideoAlignment.FindAnchors(Samples(0), Samples(6), 1, cancellation.Token));
    }

    [Fact]
    public void SparseWindowsEstablishOneConsistentOffsetAcrossUnexaminedIntervals()
    {
        var source = Samples(0).Where(s => s.TimeSec <= 12 || s.TimeSec is >= 45 and <= 55 || s.TimeSec >= 90)
            .Select(s => s with { Window = s.TimeSec <= 12 ? 0 : s.TimeSec <= 55 ? 1 : 2 }).ToList();
        var target = source.Select(s => s with { TimeSec = s.TimeSec + 12 }).ToList();
        var anchors = VideoAlignment.FindAnchors(source, target, 1, TestContext.Current.CancellationToken);
        Assert.NotEmpty(anchors);
        Assert.Single(anchors.Select(a => a.Section).Distinct());
        Assert.Equal(42, VideoAlignment.MapRange(30, 35, anchors)!.StartSec);
    }

    [Fact]
    public void SparseWindowsJoinDespiteLowContrastEdgesAndFrameGridJitter()
    {
        var random = new Random(211);
        var source = Enumerable.Range(0, 3).SelectMany(window => Enumerable.Range(0, 30).Select(index =>
            new AlignmentSample(window * 1_000 + index, (ulong)random.NextInt64(), index is >= 5 and <= 24 ? 40 : 0, "", Window: window))).ToList();
        double[] jitter = [0, .3, -.2];
        var target = source.Select(sample => sample with { TimeSec = sample.TimeSec + 6 + jitter[sample.Window] }).ToList();

        var anchors = VideoAlignment.FindAnchors(source, target, .5, TestContext.Current.CancellationToken);

        Assert.Single(anchors.Select(anchor => anchor.Section).Distinct());
        Assert.NotNull(VideoAlignment.MapRange(100, 900, anchors));
    }

    [Fact]
    public void AutomaticAlignmentReducesAConsistentSampledOffsetToTimelineBoundaries()
    {
        var random = new Random(312);
        var source = Enumerable.Range(0, 6).SelectMany(window => Enumerable.Range(0, 12).Select(index =>
            new AlignmentSample(window * 400 + index, (ulong)random.NextInt64(), 40, "", Window: window))).ToList();
        var target = source.Select(sample => sample with { TimeSec = sample.TimeSec + 6 }).ToList();

        var result = VideoAlignment.FindAutomaticAlignment(source, target, 2_012, 2_018, 1, TestContext.Current.CancellationToken);

        Assert.False(result.InternalEditsDetected);
        Assert.Equal(2, result.Anchors.Count);
        Assert.Equal(6, result.Anchors[0].TargetSec - result.Anchors[0].SourceSec);
        Assert.NotNull(VideoAlignment.MapRange(200, 1800, result.Anchors));
    }

    [Fact]
    public void AutomaticAlignmentRejectsMultipleSustainedOffsetsAsInternalEdits()
    {
        var random = new Random(418);
        var source = Enumerable.Range(0, 6).SelectMany(window => Enumerable.Range(0, 12).Select(index =>
            new AlignmentSample(window * 400 + index, (ulong)random.NextInt64(), 40, "", Window: window))).ToList();
        double[] offsets = [0, 0, -8, -8, -20, -20];
        var target = source.Select(sample => sample with { TimeSec = sample.TimeSec + offsets[sample.Window] }).OrderBy(sample => sample.TimeSec).ToList();

        var result = VideoAlignment.FindAutomaticAlignment(source, target, 2_012, 1_992, 1, TestContext.Current.CancellationToken);

        Assert.True(result.InternalEditsDetected);
        Assert.Empty(result.Anchors);
    }

    [Fact]
    public void AutomaticAlignmentUsesDirectChecksWhenOpeningSamplesAreAmbiguous()
    {
        var random = new Random(519);
        var source = Enumerable.Range(0, 6).SelectMany(window => Enumerable.Range(0, 12).Select(index =>
            new AlignmentSample(window * 400 + index, (ulong)random.NextInt64(), 40, "", Window: window))).ToList();
        var target = source.Select(sample => sample with { TimeSec = sample.TimeSec + 6 })
            .Concat(source.Where(sample => sample.Window == 0).Select(sample => sample with { TimeSec = sample.TimeSec + 1 }))
            .OrderBy(sample => sample.TimeSec).ToList();

        var result = VideoAlignment.FindAutomaticAlignment(source, target, 2_012, 2_018, 1, TestContext.Current.CancellationToken);

        Assert.False(result.InternalEditsDetected);
        Assert.Equal(2, result.Anchors.Count);
        Assert.Equal(6, result.Anchors[0].TargetSec - result.Anchors[0].SourceSec);
    }

    [Fact]
    public void AutomaticAlignmentRejectsOneContradictoryInteriorWindow()
    {
        var random = new Random(620);
        var source = Enumerable.Range(0, 6).SelectMany(window => Enumerable.Range(0, 12).Select(index =>
            new AlignmentSample(window * 400 + index, (ulong)random.NextInt64(), 40, "", Window: window))).ToList();
        var target = source.Select(sample => sample with { TimeSec = sample.TimeSec + (sample.Window == 3 ? 26 : 6) }).ToList();

        var result = VideoAlignment.FindAutomaticAlignment(source, target, 2_012, 2_038, 1, TestContext.Current.CancellationToken);

        Assert.True(result.InternalEditsDetected);
        Assert.Empty(result.Anchors);
    }

    [Fact]
    public void AutomaticAlignmentRejectsOneUnmatchedInteriorWindow()
    {
        var random = new Random(671);
        var source = Enumerable.Range(0, 6).SelectMany(window => Enumerable.Range(0, 12).Select(index =>
            new AlignmentSample(window * 400 + index, (ulong)random.NextInt64(), 40, "", Window: window))).ToList();
        var target = source.Where(sample => sample.Window != 3).Select(sample => sample with { TimeSec = sample.TimeSec + 6 }).ToList();
        target.AddRange(Enumerable.Range(0, 12).Select(index =>
            new AlignmentSample(1_206 + index, (ulong)random.NextInt64(), 40, "", Window: 3)));
        target = target.OrderBy(sample => sample.TimeSec).ToList();

        var result = VideoAlignment.FindAutomaticAlignment(source, target, 2_012, 2_018, 1, TestContext.Current.CancellationToken);

        Assert.True(result.InternalEditsDetected);
        Assert.Empty(result.Anchors);
    }

    [Fact]
    public void AutomaticAlignmentRejectsAContradictoryTailWithinAnInteriorWindow()
    {
        var random = new Random(682);
        var source = Enumerable.Range(0, 6).SelectMany(window => Enumerable.Range(0, 30).Select(index =>
            new AlignmentSample(window * 400 + index * .5, (ulong)random.NextInt64(), 40, "", Window: window))).ToList();
        var target = source.Select(sample => sample.Window == 3 && sample.TimeSec >= 1_207
            ? sample with { TimeSec = sample.TimeSec + 6, Hash = (ulong)random.NextInt64() }
            : sample with { TimeSec = sample.TimeSec + 6 }).OrderBy(sample => sample.TimeSec).ToList();

        var result = VideoAlignment.FindAutomaticAlignment(source, target, 2_015, 2_021, .5, TestContext.Current.CancellationToken);

        Assert.True(result.InternalEditsDetected);
        Assert.Empty(result.Anchors);
    }

    [Fact]
    public void AutomaticAlignmentRejectsASustainedOffsetChangeInOneContinuousWindow()
    {
        var random = new Random(793);
        var source = Enumerable.Range(0, 240).Select(index =>
            new AlignmentSample(index * .5, (ulong)random.NextInt64(), 40, "")).ToList();
        var target = source.Select(sample => sample with { TimeSec = sample.TimeSec + (sample.TimeSec < 60 ? 6 : 7) }).ToList();

        var result = VideoAlignment.FindAutomaticAlignment(source, target, 120, 127, .5, TestContext.Current.CancellationToken);

        Assert.True(result.InternalEditsDetected);
        Assert.Empty(result.Anchors);
    }

    [Fact]
    public void AutomaticAlignmentRefinesAConstantOffsetToAFrameAtTenHertz()
    {
        const double sourceDuration = 2_000;
        const double targetDuration = 2_013;
        const double expectedOffset = 6.02;
        static ulong Hash(long frame)
        {
            var value = (ulong)frame + 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
        var source = VideoAlignment.PlanSampleWindows(sourceDuration).SelectMany((window, windowIndex) =>
            Enumerable.Range(0, (int)Math.Ceiling((window.EndSec - window.StartSec) / .1)).Select(index =>
            {
                var time = window.StartSec + index * .1;
                return new AlignmentSample(time, Hash((long)Math.Round(time * 10)), 40, "", Window: windowIndex);
            })).ToList();
        var target = VideoAlignment.PlanSampleWindows(targetDuration).SelectMany((window, windowIndex) =>
            Enumerable.Range(0, (int)Math.Ceiling((window.EndSec - window.StartSec) / .1)).Select(index =>
            {
                var time = window.StartSec + index * .1;
                var expectedSourceTime = time - expectedOffset;
                var nearest = source.MinBy(sample => Math.Abs(sample.TimeSec - expectedSourceTime));
                var hash = nearest is not null && Math.Abs(nearest.TimeSec - expectedSourceTime) <= .051
                    ? nearest.Hash : Hash((long)Math.Round(time * 10)) ^ ulong.MaxValue;
                return new AlignmentSample(time, hash, 40, "", Window: windowIndex);
            })).ToList();

        var result = VideoAlignment.FindAutomaticAlignment(source, target, sourceDuration, targetDuration, .1, TestContext.Current.CancellationToken);

        Assert.False(result.InternalEditsDetected);
        Assert.Equal(2, result.Anchors.Count);
        Assert.InRange(result.Anchors[0].TargetSec - result.Anchors[0].SourceSec, expectedOffset - .06, expectedOffset + .06);
    }

    [Fact]
    public void AutomaticAlignmentRejectsAnObservedUnmatchedInteriorGap()
    {
        var source = Samples(0);
        var target = source.Where(sample => sample.TimeSec < 40 || sample.TimeSec > 60).ToList();

        var result = VideoAlignment.FindAutomaticAlignment(source, target, 101, 101, 1, TestContext.Current.CancellationToken);

        Assert.True(result.InternalEditsDetected);
        Assert.Empty(result.Anchors);
    }

    [Fact]
    public void AutomaticAlignmentClampsBoundaryAnchorsToFractionalDurations()
    {
        var random = new Random(721);
        var source = Enumerable.Range(0, 71).Select(index =>
            new AlignmentSample(index * .5, (ulong)random.NextInt64(), 40, "")).ToList();
        var target = source.Select(sample => sample with { TimeSec = sample.TimeSec + 6 }).ToList();

        var result = VideoAlignment.FindAutomaticAlignment(source, target, 35.2, 41.2, .5, TestContext.Current.CancellationToken);

        Assert.False(result.InternalEditsDetected);
        Assert.Equal(35.2, result.Anchors[^1].SourceSec);
        Assert.Equal(41.2, result.Anchors[^1].TargetSec, 6);
        Assert.Null(VideoAlignment.Validate(result.Anchors, 35.2, 41.2));
    }

    [Fact]
    public void AutomaticAlignmentDoesNotTreatIdenticalLowInformationSamplesAsIdentity()
    {
        var source = Enumerable.Range(0, 30).Select(index => new AlignmentSample(index, 0, 0, "")).ToList();

        var result = VideoAlignment.FindAutomaticAlignment(source, source, 30, 30, 1, TestContext.Current.CancellationToken);

        Assert.False(result.InternalEditsDetected);
        Assert.Empty(result.Anchors);
    }

    [Fact]
    public void SparseWindowsSplitWhenAHiddenEditChangesTheObservedOffset()
    {
        var random = new Random(73);
        var first = Enumerable.Range(0, 10).Select(i => new AlignmentSample(i, (ulong)random.NextInt64(), 40, "", Window: 0)).ToList();
        var second = Enumerable.Range(0, 10).Select(i => new AlignmentSample(20_000 + i, (ulong)random.NextInt64(), 40, "", Window: 1)).ToList();
        var source = first.Concat(second).ToList();
        var target = first.Select(sample => sample with { TimeSec = sample.TimeSec + 8 })
            .Concat(second.Select(sample => sample with { TimeSec = sample.TimeSec + 28 })).ToList();
        var anchors = VideoAlignment.FindAnchors(source, target, 1, TestContext.Current.CancellationToken);
        Assert.Equal(2, anchors.Select(anchor => anchor.Section).Distinct().Count());
        Assert.Null(VideoAlignment.MapRange(5, 20_005, anchors));
    }

    [Fact]
    public void DenseSampledMismatchRemainsAnUnresolvedGapEvenWhenOffsetsResume()
    {
        var source = Samples(0);
        var target = source.Where(sample => sample.TimeSec <= 20 || sample.TimeSec >= 80).ToList();
        var anchors = VideoAlignment.FindAnchors(source, target, 1, TestContext.Current.CancellationToken);
        Assert.Equal(2, anchors.Select(anchor => anchor.Section).Distinct().Count());
        Assert.Null(VideoAlignment.MapRange(40, 50, anchors));
    }

    [Fact]
    public void SparseMatchingDoesNotBridgeAContradictorySampledWindow()
    {
        var random = new Random(91);
        var source = Enumerable.Range(0, 5).SelectMany(window => Enumerable.Range(0, 10)
            .Select(i => new AlignmentSample(window * 1_000 + i, (ulong)random.NextInt64(), 40, "", Window: window))).ToList();
        var target = source.Where(sample => sample.Window != 2).Select(sample => sample with { TimeSec = sample.TimeSec + 8 }).ToList();
        target.AddRange(Enumerable.Range(0, 10)
            .Select(i => new AlignmentSample(2_008 + i, (ulong)random.NextInt64(), 40, "", Window: 2)));
        target = target.OrderBy(sample => sample.TimeSec).ToList();
        var anchors = VideoAlignment.FindAnchors(source, target, 1, TestContext.Current.CancellationToken);
        Assert.Equal(2, anchors.Select(anchor => anchor.Section).Distinct().Count());
        Assert.Null(VideoAlignment.MapRange(1_005, 3_005, anchors));
    }

    [Fact]
    public void IsolatedSparseCandidatesDoNotEstablishAContinuousTimeline()
    {
        ulong[] hashes = [0xFFFF, 0xFFFF0000, 0xFFFF00000000, 0xFFFF000000000000, 0xAAAAAAAAAAAAAAAA];
        var source = hashes.Select((hash, window) => new AlignmentSample(window * 1_000, hash, 40, "", Window: window)).ToList();
        var target = source.Select(sample => sample with { TimeSec = sample.TimeSec + 8 }).ToList();
        Assert.Empty(VideoAlignment.FindAnchors(source, target, 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void SparseMatchingDoesNotBridgeAnObservedMismatchAtAWindowEdge()
    {
        var random = new Random(117);
        var first = Enumerable.Range(0, 30)
            .Select(i => new AlignmentSample(i, (ulong)random.NextInt64(), 40, "", Window: 0)).ToList();
        var second = Enumerable.Range(0, 30)
            .Select(i => new AlignmentSample(100 + i, (ulong)random.NextInt64(), 40, "", Window: 1)).ToList();
        var source = first.Concat(second).ToList();
        var target = first.Take(10).Select(sample => sample with { TimeSec = sample.TimeSec + 8 })
            .Concat(Enumerable.Range(10, 20)
                .Select(i => new AlignmentSample(i + 8, (ulong)random.NextInt64(), 40, "", Window: 0)))
            .Concat(second.Select(sample => sample with { TimeSec = sample.TimeSec + 8 }))
            .OrderBy(sample => sample.TimeSec).ToList();
        var anchors = VideoAlignment.FindAnchors(source, target, 1, TestContext.Current.CancellationToken);
        Assert.Equal(2, anchors.Select(anchor => anchor.Section).Distinct().Count());
        Assert.Null(VideoAlignment.MapRange(5, 105, anchors));
    }

    [Fact]
    public void LongVideosDistributeShortWindowsAcrossTheTimelineWhileShortVideosRemainContinuous()
    {
        Assert.Equal([new AlignedRange(0, 120)], VideoAlignment.PlanSampleWindows(120));
        var fourMinuteWindows = VideoAlignment.PlanSampleWindows(240);
        Assert.Equal(6, fourMinuteWindows.Count);
        Assert.All(fourMinuteWindows.Zip(fourMinuteWindows.Skip(1)),
            pair => Assert.True(pair.First.EndSec <= pair.Second.StartSec));
        var windows = VideoAlignment.PlanSampleWindows(1200);
        Assert.Equal(6, windows.Count);
        Assert.Equal(90, windows.Sum(window => window.EndSec - window.StartSec));
        Assert.Equal(new AlignedRange(0, 15), windows.First());
        Assert.Equal(new AlignedRange(1185, 1200), windows.Last());
    }

    private static List<AlignmentSample> Samples(double offset)
    {
        var random = new Random(42);
        return Enumerable.Range(1, 100).Select(i => new AlignmentSample(i + offset,
            (ulong)random.NextInt64() | ((ulong)random.Next(2) << 63), 40, "")).ToList();
    }
}
