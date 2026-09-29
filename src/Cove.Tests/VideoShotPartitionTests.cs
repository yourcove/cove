using Cove.Core.Common;
using Cove.Core.DTOs;
using Cove.Data.Services;

namespace Cove.Tests;

/// <summary>
/// The partition every shot set must be: ordered, gapless, from 0 to the set's duration. Boundaries
/// within a millisecond are snapped into place; anything further off is rejected, never repaired.
/// </summary>
public sealed class VideoShotPartitionTests
{
    [Fact]
    public void ContiguousShotsArePassedThroughInOrder()
    {
        var result = VideoShotPartition.Normalize(
            [Shot(4, 10, "General", "Hard_Cut"), Shot(0, 4, "General", "New_Start")], 10, null, null);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal([(0d, 4d), (4d, 10d)], result.Shots!.Select(shot => (shot.StartSec, shot.EndSec)));
        Assert.Null(result.Shots![0].TransitionIn);
        Assert.Equal("Hard_Cut", result.Shots[1].TransitionIn);
    }

    [Fact]
    public void BoundariesWithinAMillisecondAreSnapped()
    {
        var result = VideoShotPartition.Normalize(
            [Shot(0.0004, 4.0003), Shot(4.0, 7.5), Shot(7.5006, 9.9995)], 10, null, null);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal([(0d, 4.0003), (4.0003, 7.5), (7.5, 10d)], result.Shots!.Select(shot => (shot.StartSec, shot.EndSec)));
    }

    [Theory]
    [InlineData(0.002, 4, 4, 10, "starts at")]
    [InlineData(0, 4, 4.01, 10, "gap")]
    [InlineData(0, 4, 3.9, 10, "overlaps")]
    [InlineData(0, 4, 4, 9.9, "last shot ends")]
    public void BoundariesFurtherOffAreRejected(double firstStart, double firstEnd, double secondStart, double secondEnd, string expected)
    {
        var result = VideoShotPartition.Normalize([Shot(firstStart, firstEnd), Shot(secondStart, secondEnd)], 10, null, null);

        Assert.False(result.IsValid);
        Assert.Contains(expected, result.Error);
    }

    [Fact]
    public void ShapeAndValuesAreChecked()
    {
        Assert.False(VideoShotPartition.Normalize([], 10, null, null).IsValid);
        Assert.False(VideoShotPartition.Normalize([Shot(0, 10)], double.NaN, null, null).IsValid);
        Assert.False(VideoShotPartition.Normalize([Shot(0, 10)], 0, null, null).IsValid);
        Assert.False(VideoShotPartition.Normalize([Shot(0, double.PositiveInfinity)], 10, null, null).IsValid);
        Assert.False(VideoShotPartition.Normalize([Shot(0, 10)], 10, 0, null).IsValid);
        Assert.False(VideoShotPartition.Normalize([Shot(0, 5), Shot(5, 5.0005), Shot(5.0005, 10)], 10, null, null).IsValid);

        var tooMany = Enumerable.Range(0, VideoShotRules.MaxShotsPerSet + 1)
            .Select(index => Shot(index * 0.01, (index + 1) * 0.01))
            .ToList();
        Assert.Contains("at most", VideoShotPartition.Normalize(tooMany, (VideoShotRules.MaxShotsPerSet + 1) * 0.01, null, null).Error);
    }

    [Fact]
    public void FramesMustBeCompleteContiguousAndMatchTheFrameCount()
    {
        var valid = VideoShotPartition.Normalize([Shot(0, 2, startFrame: 0, endFrame: 50), Shot(2, 4, startFrame: 50, endFrame: 100)], 4, 25, null);
        Assert.True(valid.IsValid, valid.Error);
        Assert.Equal(100, valid.FrameCount);

        Assert.Contains("every shot", VideoShotPartition.Normalize([Shot(0, 2, startFrame: 0, endFrame: 50), Shot(2, 4)], 4, 25, null).Error);
        Assert.Contains("frame rate", VideoShotPartition.Normalize([Shot(0, 4, startFrame: 0, endFrame: 100)], 4, null, null).Error);
        Assert.Contains("both", VideoShotPartition.Normalize([Shot(0, 4, startFrame: 0)], 4, 25, null).Error);
        Assert.Contains("contiguous", VideoShotPartition.Normalize([Shot(0, 2, startFrame: 0, endFrame: 50), Shot(2, 4, startFrame: 51, endFrame: 100)], 4, 25, null).Error);
        Assert.Contains("frame count", VideoShotPartition.Normalize([Shot(0, 4, startFrame: 0, endFrame: 100)], 4, 25, 101).Error);
    }

    [Fact]
    public void LabelsAreTrimmedAndLimited()
    {
        var result = VideoShotPartition.Normalize([Shot(0, 2, "  General ", "New_Start"), Shot(2, 4, " ", " Hard_Cut ")], 4, null, null);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal("General", result.Shots![0].ShotType);
        Assert.Null(result.Shots[0].TransitionIn);
        Assert.Null(result.Shots[1].ShotType);
        Assert.Equal("Hard_Cut", result.Shots[1].TransitionIn);

        var tooLong = new string('x', VideoShotRules.MaxLabelLength + 1);
        Assert.False(VideoShotPartition.Normalize([Shot(0, 4, tooLong)], 4, null, null).IsValid);
    }

    // The probed duration is the container's; the analysed one is the video stream's, which can end
    // well before audio that runs on, but never runs much past the container.
    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(100, 102, true)]
    [InlineData(100, 102.5, false)]
    [InlineData(1000, 1009, true)]
    [InlineData(1000, 1011, false)]
    [InlineData(100, 97, true)]
    [InlineData(100, 80, true)]
    [InlineData(100, 50, true)]
    [InlineData(100, 49.9, false)]
    [InlineData(0, 55, true)]
    public void AnalysedDurationMayFallShortOfTheFileButNotOverrunIt(double fileDuration, double analysedDuration, bool expected)
        => Assert.Equal(expected, VideoShotPartition.DurationMatchesFile(analysedDuration, fileDuration));

    private static VideoShotInput Shot(
        double start,
        double end,
        string? shotType = null,
        string? transitionIn = null,
        int? startFrame = null,
        int? endFrame = null)
        => new()
        {
            StartSec = start,
            EndSec = end,
            ShotType = shotType,
            TransitionIn = transitionIn,
            StartFrame = startFrame,
            EndFrame = endFrame,
        };
}
