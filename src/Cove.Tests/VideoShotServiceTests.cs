using System.Data.Common;
using System.Text.Json;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Tests;

/// <summary>
/// Shot boundaries are stored per video file and written only through <see cref="VideoShotService"/>,
/// which keeps each file's set a valid partition, applies the write policy, and checks every edit
/// against the revision it started from.
/// </summary>
public sealed class VideoShotServiceTests
{
    [Fact]
    public async Task WritingAnAnalysisStoresTheSetWithItsProvenance()
    {
        await using var harness = await Harness.CreateAsync();
        var (videoId, fileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        await using var db = harness.CreateContext();

        var result = await new VideoShotService(db).WriteSetAsync(Analysis(fileId,
            Shot(0, 4, "General", "New_Start", 0, 100), Shot(4, 10, "Dissolve", "Hard_Cut", 100, 250)) with
        {
            SourceRunId = "run-1",
            Model = "omnishotcut",
            ModelVersion = "1.0",
            Mode = "clean_shot",
            DecodeBackend = "ffmpeg_cpu",
            Fps = 25,
            FrameCount = 250,
            Payload = JsonDocument.Parse("{\"label_counts\":{}}").RootElement,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(VideoShotWriteOutcome.Written, result.Outcome);
        Assert.False(result.ReplacedEditedSet);
        Assert.Null(result.Set!.Payload);
        Assert.Null((await harness.Service().GetSummariesForFilesAsync([fileId], TestContext.Current.CancellationToken))[fileId].Payload);
        var set = await new VideoShotService(harness.CreateContext()).GetForFileAsync(fileId, TestContext.Current.CancellationToken);
        Assert.NotNull(set);
        Assert.Equal(videoId, set.VideoId);
        Assert.True(set.IsPrimaryFile);
        Assert.False(set.IsStale);
        Assert.Equal(("ext:ai.shots", "run-1", "omnishotcut", "1.0", "clean_shot", "ffmpeg_cpu"),
            (set.SourceKey, set.SourceRunId, set.Model, set.ModelVersion, set.Mode, set.DecodeBackend));
        Assert.Equal((25d, 250, 10d, 2, 1), (set.Fps!.Value, set.FrameCount!.Value, set.DurationSec, set.ShotCount, set.Revision));
        Assert.Null(set.EditedAt);
        Assert.True(set.Payload!.Value.TryGetProperty("label_counts", out _));
        Assert.Equal([(0d, 4d, 0, 100, "General", (string?)null), (4d, 10d, 100, 250, "Dissolve", "Hard_Cut")],
            set.Shots!.Select(shot => (shot.StartSec, shot.EndSec, shot.StartFrame!.Value, shot.EndFrame!.Value, shot.ShotType, shot.TransitionIn)));
    }

    [Fact]
    public async Task SkipIfExistsLeavesAnyExistingSetAlone()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        await harness.WriteAsync(Analysis(fileId, Shot(0, 5), Shot(5, 10)));

        var result = await harness.WriteAsync(Analysis(fileId, Shot(0, 10)));

        Assert.Equal(VideoShotWriteOutcome.SkippedExisting, result.Outcome);
        Assert.Equal(2, result.Set!.ShotCount);
        Assert.Equal(2, (await harness.GetAsync(fileId))!.ShotCount);
    }

    [Fact]
    public async Task ReplaceOverwritesEvenAnEditedSetAndClearsTheEdit()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        var written = await harness.WriteAsync(Analysis(fileId, Shot(0, 5), Shot(5, 10)));
        var split = await harness.Service().SplitAsync(new VideoShotSplitRequest { FileId = fileId, AtSec = 7, Expected = Version(written.Set!) }, TestContext.Current.CancellationToken);
        Assert.NotNull(split.Set!.EditedAt);

        var result = await harness.WriteAsync(Analysis(fileId, Shot(0, 3), Shot(3, 10)) with { WriteMode = VideoShotWriteMode.Replace });

        Assert.Equal(VideoShotWriteOutcome.Replaced, result.Outcome);
        Assert.True(result.ReplacedEditedSet);
        var set = await harness.GetAsync(fileId);
        Assert.Equal(written.Set!.Id, set!.Id);
        Assert.Equal(3, set.Revision);
        Assert.Null(set.EditedAt);
        Assert.Equal([0d, 3d], set.Shots!.Select(shot => shot.StartSec));
    }

    [Fact]
    public async Task ConditionalReplaceNeedsTheExactSet()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        var written = await harness.WriteAsync(Analysis(fileId, Shot(0, 10)));
        var stale = Version(written.Set!) with { Revision = 99 };

        var conflict = await harness.WriteAsync(Analysis(fileId, Shot(0, 2), Shot(2, 10)) with { WriteMode = VideoShotWriteMode.Replace, Expected = stale });
        var replaced = await harness.WriteAsync(Analysis(fileId, Shot(0, 2), Shot(2, 10)) with { WriteMode = VideoShotWriteMode.Replace, Expected = Version(written.Set!) });
        var misuse = await harness.WriteAsync(Analysis(fileId, Shot(0, 10)) with { Expected = Version(written.Set!) });

        Assert.Equal(VideoShotWriteOutcome.Conflict, conflict.Outcome);
        Assert.Equal(written.Set!.Id, conflict.Set!.Id);
        Assert.Equal(VideoShotWriteOutcome.Replaced, replaced.Outcome);
        Assert.Equal(VideoShotWriteOutcome.Invalid, misuse.Outcome);
    }

    [Fact]
    public async Task AnInvalidWriteNeverTouchesTheExistingSet()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        await harness.WriteAsync(Analysis(fileId, Shot(0, 10)));

        var gap = await harness.WriteAsync(Analysis(fileId, Shot(0, 4), Shot(5, 10)) with { WriteMode = VideoShotWriteMode.Replace });
        var wrongFile = await harness.WriteAsync(Analysis(fileId, Shot(0, 60)) with { DurationSec = 60, WriteMode = VideoShotWriteMode.Replace });
        var noSource = await harness.WriteAsync(Analysis(fileId, Shot(0, 10)) with { SourceKey = " ", WriteMode = VideoShotWriteMode.Replace });
        var missingFile = await harness.WriteAsync(Analysis(987_654, Shot(0, 10)));

        Assert.Equal(VideoShotWriteOutcome.Invalid, gap.Outcome);
        Assert.Equal(VideoShotWriteOutcome.Invalid, wrongFile.Outcome);
        Assert.Contains("different file", wrongFile.Reason);
        Assert.Equal(VideoShotWriteOutcome.Invalid, noSource.Outcome);
        Assert.Equal(VideoShotWriteOutcome.FileNotFound, missingFile.Outcome);
        var set = await harness.GetAsync(fileId);
        Assert.Equal((1, 1), (set!.Revision, set.ShotCount));
    }

    [Fact]
    public async Task AnAnalysisMayEndBeforeTheFileButNotFarBeforeOrAfterIt()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 100, size: 1_000);

        // The video stream ends ten seconds before the audio, and so before the probed container.
        var shorterStream = await harness.WriteAsync(Analysis(fileId, Shot(0, 40), Shot(40, 90)));
        var farShorter = await harness.WriteAsync(Analysis(fileId, Shot(0, 45)) with { WriteMode = VideoShotWriteMode.Replace });
        var longer = await harness.WriteAsync(Analysis(fileId, Shot(0, 103)) with { WriteMode = VideoShotWriteMode.Replace });

        Assert.Equal(VideoShotWriteOutcome.Written, shorterStream.Outcome);
        Assert.Equal((VideoShotWriteOutcome.Invalid, VideoShotWriteOutcome.Invalid), (farShorter.Outcome, longer.Outcome));
        Assert.Contains("less than half of the file's 100s", farShorter.Reason);
        Assert.Contains("longer than the file's 100s", longer.Reason);
        var set = await harness.GetAsync(fileId);
        Assert.Equal((90d, 1), (set!.DurationSec, set.Revision));
    }

    [Fact]
    public async Task AFileWithoutAProbedDurationAcceptsAnyAnalysedDuration()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 0, size: 1_000);

        var result = await harness.WriteAsync(Analysis(fileId, Shot(0, 42)) with { DurationSec = 42 });

        Assert.Equal(VideoShotWriteOutcome.Written, result.Outcome);
    }

    [Fact]
    public async Task AnImportedEditedSetKeepsItsEditTime()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        var editedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

        await harness.WriteAsync(Analysis(fileId, Shot(0, 10)) with { SourceKey = "ext:segment-studio", EditedAt = editedAt });

        Assert.Equal(editedAt, (await harness.GetAsync(fileId))!.EditedAt);
    }

    [Fact]
    public async Task SplittingCutsTheContainingShotAndKeepsTheOthers()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        var written = await harness.WriteAsync(Analysis(fileId,
            Shot(0, 4, "General", null, 0, 100), Shot(4, 10, "General", "Hard_Cut", 100, 250)) with { Fps = 25 });
        var before = await harness.GetAsync(fileId);

        var result = await harness.Service().SplitAsync(new VideoShotSplitRequest { FileId = fileId, AtSec = 6, Expected = Version(written.Set!) }, TestContext.Current.CancellationToken);

        Assert.Equal(VideoShotEditStatus.Updated, result.Status);
        var shots = result.Set!.Shots!;
        Assert.Equal([(0d, 4d), (4d, 6d), (6d, 10d)], shots.Select(shot => (shot.StartSec, shot.EndSec)));
        Assert.Equal([(0, 100), (100, 150), (150, 250)], shots.Select(shot => (shot.StartFrame!.Value, shot.EndFrame!.Value)));
        Assert.Equal(before!.Shots![0], shots[0]);
        Assert.Equal(before.Shots[1] with { EndSec = 6, EndFrame = 150 }, shots[1]);
        Assert.Equal(("General", (string?)null), (shots[2].ShotType, shots[2].TransitionIn));
        Assert.Equal((2, 3), (result.Set.Revision, result.Set.ShotCount));
        Assert.NotNull(result.Set.EditedAt);
    }

    [Fact]
    public async Task SplittingAFileWithoutShotsDrawsAManualSetFirst()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);

        var result = await harness.Service().SplitAsync(new VideoShotSplitRequest { FileId = fileId, AtSec = 3 }, TestContext.Current.CancellationToken);

        Assert.Equal(VideoShotEditStatus.Updated, result.Status);
        Assert.Equal(("user", 1, 2), (result.Set!.SourceKey, result.Set.Revision, result.Set.ShotCount));
        Assert.Equal([(0d, 3d), (3d, 10d)], result.Set.Shots!.Select(shot => (shot.StartSec, shot.EndSec)));
        Assert.NotNull(result.Set.EditedAt);
    }

    [Fact]
    public async Task EditsAreRejectedOnAStaleOrMissingVersion()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        var written = await harness.WriteAsync(Analysis(fileId, Shot(0, 5), Shot(5, 10)));
        var service = harness.Service();

        var withoutVersion = await service.SplitAsync(new VideoShotSplitRequest { FileId = fileId, AtSec = 2 }, TestContext.Current.CancellationToken);
        var staleVersion = await service.MergeAsync(new VideoShotMergeRequest { FileId = fileId, CutSec = 5, Expected = Version(written.Set!) with { Revision = 7 } }, TestContext.Current.CancellationToken);
        var tooClose = await service.SplitAsync(new VideoShotSplitRequest { FileId = fileId, AtSec = 5.0005, Expected = Version(written.Set!) }, TestContext.Current.CancellationToken);
        var noCut = await service.MergeAsync(new VideoShotMergeRequest { FileId = fileId, CutSec = 3, Expected = Version(written.Set!) }, TestContext.Current.CancellationToken);

        Assert.Equal(VideoShotEditStatus.Conflict, withoutVersion.Status);
        Assert.Equal(VideoShotEditStatus.Conflict, staleVersion.Status);
        Assert.Equal(2, staleVersion.Set!.Shots!.Count);
        Assert.Equal(VideoShotEditStatus.Invalid, tooClose.Status);
        Assert.Equal(VideoShotEditStatus.Invalid, noCut.Status);
        Assert.Equal(1, (await harness.GetAsync(fileId))!.Revision);
    }

    [Fact]
    public async Task MergingAndMovingCutsKeepThePartition()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 12, size: 1_000);
        var written = await harness.WriteAsync(Analysis(fileId, Shot(0, 4), Shot(4, 8), Shot(8, 12)) with { DurationSec = 12 });
        var service = harness.Service();

        var merged = await service.MergeAsync(new VideoShotMergeRequest { FileId = fileId, CutSec = 4, Expected = Version(written.Set!) }, TestContext.Current.CancellationToken);
        var moved = await service.MoveCutAsync(new VideoShotMoveCutRequest { FileId = fileId, FromSec = 8, ToSec = 9.5, Expected = Version(merged.Set!) }, TestContext.Current.CancellationToken);
        var unchanged = await service.MoveCutAsync(new VideoShotMoveCutRequest { FileId = fileId, FromSec = 9.5, ToSec = 9.5, Expected = Version(moved.Set!) }, TestContext.Current.CancellationToken);
        var outOfRange = await service.MoveCutAsync(new VideoShotMoveCutRequest { FileId = fileId, FromSec = 9.5, ToSec = 12.5, Expected = Version(moved.Set!) }, TestContext.Current.CancellationToken);
        var mergeFirst = await service.MergeAsync(new VideoShotMergeRequest { FileId = fileId, CutSec = 0, Expected = Version(moved.Set!) }, TestContext.Current.CancellationToken);

        Assert.Equal([(0d, 8d), (8d, 12d)], merged.Set!.Shots!.Select(shot => (shot.StartSec, shot.EndSec)));
        Assert.Equal([(0d, 9.5), (9.5, 12d)], moved.Set!.Shots!.Select(shot => (shot.StartSec, shot.EndSec)));
        Assert.Equal(moved.Set.Revision, unchanged.Set!.Revision);
        Assert.Equal(VideoShotEditStatus.Invalid, outOfRange.Status);
        Assert.Equal(VideoShotEditStatus.Invalid, mergeFirst.Status);
        Assert.Equal(3, (await harness.GetAsync(fileId))!.Revision);
    }

    [Fact]
    public async Task ReplacingShotsStoresTheNewPartition()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        var written = await harness.WriteAsync(Analysis(fileId, Shot(0, 3), Shot(3, 6), Shot(6, 10)));
        var result = await harness.Service().ReplaceShotsAsync(new VideoShotReplaceRequest
        {
            FileId = fileId,
            Expected = Version(written.Set!),
            Shots = [Shot(0, 3, "General"), Shot(3, 5, transitionIn: "Hard_Cut"), Shot(5, 10)],
        }, TestContext.Current.CancellationToken);

        Assert.Equal(VideoShotEditStatus.Updated, result.Status);
        var after = result.Set!.Shots!;
        Assert.Equal([0d, 3d, 5d], after.Select(shot => shot.StartSec));
        Assert.Equal(["General", null, null], after.Select(shot => shot.ShotType));
        Assert.Equal([null, "Hard_Cut", null], after.Select(shot => shot.TransitionIn));
        Assert.Equal((2, 3), (result.Set.Revision, result.Set.ShotCount));

        var outside = await harness.Service().ReplaceShotsAsync(new VideoShotReplaceRequest
        {
            FileId = fileId,
            Expected = Version(result.Set),
            Shots = [Shot(0, 11)],
        }, TestContext.Current.CancellationToken);
        Assert.Equal(VideoShotEditStatus.Invalid, outside.Status);
    }

    [Fact]
    public async Task ReplacingShotsOnAFileWithoutASetCreatesOne()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);

        var result = await harness.Service().ReplaceShotsAsync(new VideoShotReplaceRequest
        {
            FileId = fileId,
            SourceKey = "ext:segment-studio",
            Shots = [Shot(0, 4), Shot(4, 10)],
        }, TestContext.Current.CancellationToken);

        Assert.Equal(VideoShotEditStatus.Updated, result.Status);
        Assert.Equal(("ext:segment-studio", 1, 10d), (result.Set!.SourceKey, result.Set.Revision, result.Set.DurationSec));
    }

    [Fact]
    public async Task ARecreatedSetDoesNotAcceptTheOldVersion()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        var first = await harness.WriteAsync(Analysis(fileId, Shot(0, 5), Shot(5, 10)));
        // PostgreSQL never reuses identity values; keep SQLite from reusing the deleted row id.
        var (_, otherFileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        await harness.WriteAsync(Analysis(otherFileId, Shot(0, 10)));

        var staleDelete = await harness.Service().DeleteAsync(new VideoShotDeleteRequest { FileId = fileId, Expected = Version(first.Set!) with { Revision = 2 } }, TestContext.Current.CancellationToken);
        var deleted = await harness.Service().DeleteAsync(new VideoShotDeleteRequest { FileId = fileId, Expected = Version(first.Set!) }, TestContext.Current.CancellationToken);
        var missing = await harness.Service().DeleteAsync(new VideoShotDeleteRequest { FileId = fileId }, TestContext.Current.CancellationToken);
        var second = await harness.WriteAsync(Analysis(fileId, Shot(0, 5), Shot(5, 10)));
        var merge = await harness.Service().MergeAsync(new VideoShotMergeRequest { FileId = fileId, CutSec = 5, Expected = Version(first.Set!) }, TestContext.Current.CancellationToken);

        Assert.Equal(VideoShotEditStatus.Conflict, staleDelete.Status);
        Assert.Equal(VideoShotEditStatus.Deleted, deleted.Status);
        Assert.Equal(VideoShotEditStatus.NotFound, missing.Status);
        Assert.Equal(1, second.Set!.Revision);
        Assert.NotEqual(first.Set!.Id, second.Set.Id);
        Assert.Equal(VideoShotEditStatus.Conflict, merge.Status);
    }

    [Fact]
    public async Task EditsAreCheckedAgainstTheSetsOwnDuration()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 10.4, size: 1_000);
        var written = await harness.WriteAsync(Analysis(fileId, Shot(0, 5), Shot(5, 10)));

        var result = await harness.Service().ReplaceShotsAsync(new VideoShotReplaceRequest
        {
            FileId = fileId,
            Expected = Version(written.Set!),
            Shots = [Shot(0, 2), Shot(2, 10)],
        }, TestContext.Current.CancellationToken);

        Assert.Equal(VideoShotEditStatus.Updated, result.Status);
        Assert.Equal(10d, result.Set!.DurationSec);
    }

    [Fact]
    public async Task VideoReadsFollowThePrimaryFileWithoutMappingTime()
    {
        await using var harness = await Harness.CreateAsync();
        var (videoId, firstFileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        var secondFileId = await harness.AttachFileAsync(videoId, duration: 10, size: 2_000);
        await harness.WriteAsync(Analysis(firstFileId, Shot(0, 5), Shot(5, 10)));
        await harness.WriteAsync(Analysis(secondFileId, Shot(0, 10)));

        var fromPrimary = await harness.Service().GetForVideoAsync(videoId, TestContext.Current.CancellationToken);
        await harness.SetPrimaryFileAsync(videoId, secondFileId);
        var afterSwitch = await harness.Service().GetForVideoAsync(videoId, TestContext.Current.CancellationToken);
        var sets = await harness.Service().ListForVideoAsync(videoId, TestContext.Current.CancellationToken);

        Assert.Equal(firstFileId, fromPrimary!.FileId);
        Assert.Equal(secondFileId, afterSwitch!.FileId);
        Assert.Equal(1, afterSwitch.ShotCount);
        Assert.Equal([(firstFileId, false), (secondFileId, true)], sets.Select(set => (set.FileId, set.IsPrimaryFile)));
        Assert.All(sets, set => Assert.Null(set.Shots));
    }

    [Fact]
    public async Task ASetIsStaleOnceItsFileChangesSize()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        await harness.WriteAsync(Analysis(fileId, Shot(0, 10)));

        await using (var db = harness.CreateContext())
        {
            await db.VideoFiles.Where(file => file.Id == fileId).ExecuteUpdateAsync(update => update.SetProperty(file => file.Size, 2_000L), TestContext.Current.CancellationToken);
        }

        Assert.True((await harness.GetAsync(fileId))!.IsStale);
        var summaries = await harness.Service().GetSummariesForFilesAsync([fileId, 424_242], TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(summaries).Value.IsStale);
    }

    [Fact]
    public async Task DeletingTheFileRemovesItsShots()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, removedFileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        var (_, deletedFileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        await harness.WriteAsync(Analysis(removedFileId, Shot(0, 5), Shot(5, 10)));
        await harness.WriteAsync(Analysis(deletedFileId, Shot(0, 10)));

        await using (var db = harness.CreateContext())
        {
            db.VideoFiles.Remove(await db.VideoFiles.SingleAsync(file => file.Id == removedFileId, TestContext.Current.CancellationToken));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await db.VideoFiles.Where(file => file.Id == deletedFileId).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        await using var verify = harness.CreateContext();
        Assert.Empty(await verify.VideoShotSets.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WritesJoinTheCallersTransactionWithoutSavingItsPendingChanges()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, committedFileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        var (_, rolledBackFileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);

        await using (var db = harness.CreateContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            var tag = db.Tags.Add(new Tag { Name = "saved with the shots" });
            Assert.Equal(VideoShotWriteOutcome.Written, (await new VideoShotService(db).WriteSetAsync(Analysis(committedFileId, Shot(0, 10)), TestContext.Current.CancellationToken)).Outcome);
            Assert.Equal(EntityState.Added, tag.State);
            Assert.Empty(db.ChangeTracker.Entries<VideoShotSet>());
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        await using (var db = harness.CreateContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await new VideoShotService(db).WriteSetAsync(Analysis(rolledBackFileId, Shot(0, 10)), TestContext.Current.CancellationToken);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        await using var verify = harness.CreateContext();
        Assert.True(await verify.Tags.AnyAsync(tag => tag.Name == "saved with the shots", TestContext.Current.CancellationToken));
        Assert.True(await verify.VideoShotSets.AnyAsync(set => set.FileId == committedFileId, TestContext.Current.CancellationToken));
        Assert.False(await verify.VideoShotSets.AnyAsync(set => set.FileId == rolledBackFileId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASetStoresEachBoundaryOnceAsCutsInItsOwnRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync();
        var (_, framedFileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        var (_, singleFileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        await harness.WriteAsync(Analysis(framedFileId,
            Shot(0, 4, "General", null, 0, 100), Shot(4, 6, null, "Dissolve", 100, 150), Shot(6, 10, "Credits", null, 150, 250)) with { Fps = 25 });
        await harness.WriteAsync(Analysis(singleFileId, Shot(0, 10)));

        await using var db = harness.CreateContext();
        var framed = await db.VideoShotSets.AsNoTracking().SingleAsync(set => set.FileId == framedFileId, ct);
        var single = await db.VideoShotSets.AsNoTracking().SingleAsync(set => set.FileId == singleFileId, ct);

        Assert.Equal((3, 250), (framed.ShotCount, framed.FrameCount!.Value));
        Assert.Equal([4d, 6d], framed.CutTimes);
        Assert.Equal([100, 150], framed.CutFrames!);
        Assert.Equal(new string?[] { "General", null, "Credits" }, framed.ShotTypes);
        Assert.Equal(new string?[] { "Dissolve", null }, framed.Transitions);
        Assert.Equal(1, single.ShotCount);
        Assert.Empty(single.CutTimes);
        Assert.Null(single.CutFrames);
        Assert.Equal(new string?[] { null }, single.ShotTypes);
        Assert.Empty(single.Transitions);
    }

    [Fact]
    public async Task AnEditThatLosesARaceChangesNothingAndReturnsTheCurrentSet()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        var written = await harness.WriteAsync(Analysis(fileId, Shot(0, 5), Shot(5, 10)));
        // Another writer changes the set between the edit's read and its update.
        await using var db = harness.CreateContext(new RacingWriteInterceptor(harness, "UPDATE"));

        var result = await new VideoShotService(db).SplitAsync(new VideoShotSplitRequest { FileId = fileId, AtSec = 7, Expected = Version(written.Set!) }, ct);

        Assert.Equal(VideoShotEditStatus.Conflict, result.Status);
        Assert.Equal((2, 2), (result.Set!.Revision, result.Set.Shots!.Count));
        var stored = await harness.GetAsync(fileId);
        Assert.Equal(2, stored!.Revision);
        Assert.Equal([0d, 5d], stored.Shots!.Select(shot => shot.StartSec));
    }

    [Fact]
    public async Task AReplaceWithoutAVersionRetriesAfterLosingARace()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        await harness.WriteAsync(Analysis(fileId, Shot(0, 5), Shot(5, 10)));
        await using var db = harness.CreateContext(new RacingWriteInterceptor(harness, "UPDATE"));

        var result = await new VideoShotService(db).WriteSetAsync(Analysis(fileId, Shot(0, 3), Shot(3, 10)) with { WriteMode = VideoShotWriteMode.Replace }, TestContext.Current.CancellationToken);

        Assert.Equal(VideoShotWriteOutcome.Replaced, result.Outcome);
        var stored = await harness.GetAsync(fileId);
        Assert.Equal(3, stored!.Revision);
        Assert.Equal([0d, 3d], stored.Shots!.Select(shot => shot.StartSec));
    }

    [Fact]
    public async Task CreatingASetThatLosesARaceFindsTheOtherSet()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync();
        var (_, analysedFileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        var (_, drawnFileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);

        await using var analysing = harness.CreateContext(new RacingWriteInterceptor(harness, "INSERT", analysedFileId));
        var skipped = await new VideoShotService(analysing).WriteSetAsync(Analysis(analysedFileId, Shot(0, 4), Shot(4, 10)), ct);
        await using var drawing = harness.CreateContext(new RacingWriteInterceptor(harness, "INSERT", drawnFileId));
        var split = await new VideoShotService(drawing).SplitAsync(new VideoShotSplitRequest { FileId = drawnFileId, AtSec = 2 }, ct);

        Assert.Equal((VideoShotWriteOutcome.SkippedExisting, 1), (skipped.Outcome, skipped.Set!.ShotCount));
        Assert.Equal(VideoShotEditStatus.Conflict, split.Status);
        Assert.Equal(1, split.Set!.ShotCount);
        Assert.Equal(1, (await harness.GetAsync(analysedFileId))!.ShotCount);
        Assert.Equal(1, (await harness.GetAsync(drawnFileId))!.ShotCount);
    }

    [Fact]
    public async Task AnEditThatCreatesASetReturnsItsStoredVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);

        var created = await harness.Service().SplitAsync(new VideoShotSplitRequest { FileId = fileId, AtSec = 3 }, ct);
        var next = await harness.Service().SplitAsync(new VideoShotSplitRequest { FileId = fileId, AtSec = 6, Expected = Version(created.Set!) }, ct);

        Assert.Equal((await harness.GetAsync(fileId))!.Id, created.Set!.Id);
        Assert.Equal((VideoShotEditStatus.Updated, 3), (next.Status, next.Set!.ShotCount));
    }

    [Fact]
    public async Task AWriteForAFileDeletedMeanwhileFindsNoFile()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync();
        var (_, analysedFileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        var (_, drawnFileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        Func<int, Func<CoveContext, Task>> deleteFile = id => other => other.VideoFiles.Where(file => file.Id == id).ExecuteDeleteAsync();

        await using var analysing = harness.CreateContext(new RacingWriteInterceptor(harness, "INSERT", race: deleteFile(analysedFileId)));
        var written = await new VideoShotService(analysing).WriteSetAsync(Analysis(analysedFileId, Shot(0, 10)), ct);
        await using var drawing = harness.CreateContext(new RacingWriteInterceptor(harness, "INSERT", race: deleteFile(drawnFileId)));
        var split = await new VideoShotService(drawing).SplitAsync(new VideoShotSplitRequest { FileId = drawnFileId, AtSec = 2 }, ct);

        Assert.Equal(VideoShotWriteOutcome.FileNotFound, written.Outcome);
        Assert.Equal(VideoShotEditStatus.NotFound, split.Status);
        await using var verify = harness.CreateContext();
        Assert.False(await verify.VideoShotSets.AnyAsync(ct));
    }

    [Fact]
    public async Task ACarryOverLeavesASetThatChangedWhileItMoved()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync();
        var (videoId, fromFileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        var toFileId = await harness.AttachFileAsync(videoId, duration: 10, size: 1_000);
        await harness.WriteAsync(Analysis(fromFileId, Shot(0, 5), Shot(5, 10)));
        await using var db = harness.CreateContext(new RacingWriteInterceptor(harness, "UPDATE"));

        var moved = await VideoShotService.MoveSetToFileAsync(db, fromFileId, toFileId, ct);

        Assert.False(moved);
        Assert.Equal(2, (await harness.GetAsync(fromFileId))!.Revision);
        Assert.Null(await harness.GetAsync(toFileId));
    }

    [Fact]
    public async Task AnEditChecksTheDatabaseEvenWhenTheCallerTracksAnOlderCopy()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        var written = await harness.WriteAsync(Analysis(fileId, Shot(0, 5), Shot(5, 10)));
        await using var db = harness.CreateContext();
        var tracked = await db.VideoShotSets.SingleAsync(set => set.FileId == fileId, ct);
        await harness.Service().SplitAsync(new VideoShotSplitRequest { FileId = fileId, AtSec = 7, Expected = Version(written.Set!) }, ct);

        // The caller's copy, and the version it passes, are a revision behind the database.
        var result = await new VideoShotService(db).MergeAsync(new VideoShotMergeRequest { FileId = fileId, CutSec = 5, Expected = Version(written.Set!) }, ct);

        Assert.Equal(VideoShotEditStatus.Conflict, result.Status);
        Assert.Equal((2, 3), (result.Set!.Revision, result.Set.ShotCount));
        Assert.Equal(EntityState.Unchanged, db.Entry(tracked).State);
        Assert.Single(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task AnEditLeavesTheCallersTrackedCopyAndPendingChangesAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        var written = await harness.WriteAsync(Analysis(fileId, Shot(0, 5), Shot(5, 10)));
        await using var db = harness.CreateContext();
        var tracked = await db.VideoShotSets.SingleAsync(set => set.FileId == fileId, ct);
        var tag = db.Tags.Add(new Tag { Name = "caller work" });

        var result = await new VideoShotService(db).MergeAsync(new VideoShotMergeRequest { FileId = fileId, CutSec = 5, Expected = Version(written.Set!) }, ct);

        Assert.Equal(VideoShotEditStatus.Updated, result.Status);
        Assert.Equal((EntityState.Unchanged, 1, 2), (db.Entry(tracked).State, tracked.Revision, tracked.ShotCount));
        Assert.Equal(EntityState.Added, tag.State);
        // The caller saves its own work, and its stale copy has nothing to save.
        Assert.Equal(1, await db.SaveChangesAsync(ct));
        await using var verify = harness.CreateContext();
        Assert.True(await verify.Tags.AnyAsync(saved => saved.Name == "caller work", ct));
        var set = await harness.GetAsync(fileId);
        Assert.Equal((2, 1), (set!.Revision, set.ShotCount));
    }

    [Fact]
    public async Task AnEditTimeGivenInLocalOrUnspecifiedTimeIsStoredInUtc()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, localFileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        var (_, unspecifiedFileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        var local = new DateTime(2026, 9, 1, 14, 0, 0, DateTimeKind.Local);
        var unspecified = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Unspecified);

        var fromLocal = await harness.WriteAsync(Analysis(localFileId, Shot(0, 10)) with { EditedAt = local });
        var fromUnspecified = await harness.WriteAsync(Analysis(unspecifiedFileId, Shot(0, 10)) with { EditedAt = unspecified });

        Assert.Equal((local.ToUniversalTime(), DateTimeKind.Utc), (fromLocal.Set!.EditedAt!.Value, fromLocal.Set.EditedAt.Value.Kind));
        Assert.Equal((DateTime.SpecifyKind(unspecified, DateTimeKind.Utc), DateTimeKind.Utc), (fromUnspecified.Set!.EditedAt!.Value, fromUnspecified.Set.EditedAt.Value.Kind));
    }

    [Fact]
    public async Task ACutWithFramesLandsOnAFrameAtThatFramesTime()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        var written = await harness.WriteAsync(Analysis(fileId, Shot(0, 4, startFrame: 0, endFrame: 100), Shot(4, 10, startFrame: 100, endFrame: 250)) with { Fps = 25 });
        var service = harness.Service();

        var split = await service.SplitAsync(new VideoShotSplitRequest { FileId = fileId, AtSec = 6.019, Expected = Version(written.Set!) }, ct);
        var moved = await service.MoveCutAsync(new VideoShotMoveCutRequest { FileId = fileId, FromSec = 6, ToSec = 7.03, Expected = Version(split.Set!) }, ct);
        var withinAFrame = await service.MoveCutAsync(new VideoShotMoveCutRequest { FileId = fileId, FromSec = 7.04, ToSec = 4.01, Expected = Version(moved.Set!) }, ct);

        Assert.Equal([(4d, 6d, 100, 150), (6d, 10d, 150, 250)],
            split.Set!.Shots!.Skip(1).Select(shot => (shot.StartSec, shot.EndSec, shot.StartFrame!.Value, shot.EndFrame!.Value)));
        Assert.Equal(7.04, moved.Set!.Shots![2].StartSec, 9);
        Assert.Equal((176, 176), (moved.Set.Shots[1].EndFrame!.Value, moved.Set.Shots[2].StartFrame!.Value));
        Assert.Equal(VideoShotEditStatus.Invalid, withinAFrame.Status);
    }

    [Fact]
    public async Task AReplacementSetsTheFrameCountFromItsOwnFrames()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        var written = await harness.WriteAsync(Analysis(fileId, Shot(0, 4, startFrame: 0, endFrame: 100), Shot(4, 10, startFrame: 100, endFrame: 250)) with { Fps = 25 });
        var service = harness.Service();

        var withoutFrames = await service.ReplaceShotsAsync(new VideoShotReplaceRequest { FileId = fileId, Expected = Version(written.Set!), Shots = [Shot(0, 4), Shot(4, 10)] }, ct);
        var framesBack = await service.ReplaceShotsAsync(new VideoShotReplaceRequest
        {
            FileId = fileId,
            Expected = Version(withoutFrames.Set!),
            Shots = [Shot(0, 4, startFrame: 0, endFrame: 100), Shot(4, 10, startFrame: 100, endFrame: 249)],
        }, ct);

        Assert.Equal((25d, (int?)null), (withoutFrames.Set!.Fps!.Value, withoutFrames.Set.FrameCount));
        Assert.All(withoutFrames.Set.Shots!, shot => Assert.Null(shot.StartFrame));
        Assert.Equal(VideoShotEditStatus.Updated, framesBack.Status);
        Assert.Equal(249, framesBack.Set!.FrameCount);
    }

    [Fact]
    public async Task APayloadIsMeasuredInUtf8AndMayNotHoldNul()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        // Fewer UTF-16 characters than the limit, but two UTF-8 bytes each.
        var wide = JsonDocument.Parse($"{{\"text\":\"{new string('é', 33_000)}\"}}").RootElement;
        var withNul = JsonDocument.Parse("{\"text\":\"a\\u0000b\"}").RootElement;

        var tooWide = await harness.WriteAsync(Analysis(fileId, Shot(0, 10)) with { Payload = wide });
        var nul = await harness.WriteAsync(Analysis(fileId, Shot(0, 10)) with { Payload = withNul });

        Assert.Equal((VideoShotWriteOutcome.Invalid, VideoShotWriteOutcome.Invalid), (tooWide.Outcome, nul.Outcome));
        Assert.Contains("UTF-8", tooWide.Reason);
        Assert.Contains("U+0000", nul.Reason);
    }

    [Fact]
    public async Task AReplacementWithAMissingShotIsInvalid()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);

        var result = await harness.Service().ReplaceShotsAsync(new VideoShotReplaceRequest { FileId = fileId, Shots = [Shot(0, 4), null!] }, TestContext.Current.CancellationToken);

        Assert.Equal(VideoShotEditStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task DeletingASetLeavesTheCallersPendingChangesAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync();
        var (_, fileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        var (_, emptyFileId) = await harness.SeedVideoAsync(duration: 10, size: 1_000);
        await harness.WriteAsync(Analysis(fileId, Shot(0, 10)));
        await using var db = harness.CreateContext();
        var tag = db.Tags.Add(new Tag { Name = "caller work" });
        var service = new VideoShotService(db);

        var missing = await service.DeleteAsync(new VideoShotDeleteRequest { FileId = emptyFileId }, ct);
        var deleted = await service.DeleteAsync(new VideoShotDeleteRequest { FileId = fileId }, ct);

        Assert.Equal((VideoShotEditStatus.NotFound, VideoShotEditStatus.Deleted, EntityState.Added), (missing.Status, deleted.Status, tag.State));
        await using var verify = harness.CreateContext();
        Assert.False(await verify.Tags.AnyAsync(saved => saved.Name == "caller work", ct));
        Assert.False(await verify.VideoShotSets.AnyAsync(ct));
    }

    [Fact]
    public void TheHostRegistersTheServiceForExtensionContainers()
    {
        var services = new ServiceCollection();
        services.AddCoveData("Host=127.0.0.1;Database=cove_unused", runBackgroundMaterializers: false);

        // Scoped host services are copied into every extension container, so an extension resolves
        // the same service over the same scoped context as the host.
        var registration = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IVideoShotService));
        Assert.Equal((ServiceLifetime.Scoped, typeof(VideoShotService)), (registration.Lifetime, registration.ImplementationType));
    }

    private static VideoShotSetWrite Analysis(int fileId, params VideoShotInput[] shots) => new()
    {
        FileId = fileId,
        SourceKey = "ext:ai.shots",
        DurationSec = shots.Max(shot => shot.EndSec),
        Shots = shots,
    };

    private static VideoShotInput Shot(double start, double end, string? shotType = null, string? transitionIn = null, int? startFrame = null, int? endFrame = null)
        => new() { StartSec = start, EndSec = end, ShotType = shotType, TransitionIn = transitionIn, StartFrame = startFrame, EndFrame = endFrame };

    private static VideoShotSetVersion Version(VideoShotSetDto set) => new() { SetId = set.Id, Revision = set.Revision };

    /// <summary>
    /// Before the first statement of the given kind on the shot sets, writes from another connection: an
    /// update bumps every set's revision, an insert gives the file a set of one shot, unless
    /// <paramref name="race"/> says what to write instead.
    /// </summary>
    private sealed class RacingWriteInterceptor(Harness harness, string statement, int? fileId = null, Func<CoveContext, Task>? race = null) : DbCommandInterceptor
    {
        private bool _raced;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await RaceAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await RaceAsync(command);
            return result;
        }

        private async Task RaceAsync(DbCommand command)
        {
            var text = command.CommandText.TrimStart();
            if (_raced || !text.StartsWith(statement, StringComparison.Ordinal) || !text.Contains("\"video_shot_sets\"", StringComparison.Ordinal))
                return;
            _raced = true;
            await using var other = harness.CreateContext();
            if (race is not null)
                await race(other);
            else if (fileId is { } id)
                await new VideoShotService(other).WriteSetAsync(Analysis(id, Shot(0, 10)));
            else
                await other.VideoShotSets.ExecuteUpdateAsync(update => update.SetProperty(set => set.Revision, set => set.Revision + 1));
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection _anchor;
        private readonly string _connectionString;

        private Harness(SqliteConnection anchor, string connectionString)
        {
            _anchor = anchor;
            _connectionString = connectionString;
        }

        public static async Task<Harness> CreateAsync()
        {
            var connectionString = $"Data Source=video-shots-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Foreign Keys=True";
            var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            var harness = new Harness(anchor, connectionString);
            await using var db = harness.CreateContext();
            await db.Database.EnsureCreatedAsync();
            return harness;
        }

        public CoveContext CreateContext(params IInterceptor[] interceptors)
        {
            var builder = new DbContextOptionsBuilder<CoveContext>().UseSqlite(_connectionString);
            if (interceptors.Length > 0)
                builder.AddInterceptors(interceptors);
            return new CoveContext(builder.Options);
        }

        public VideoShotService Service() => new(CreateContext());

        public async Task<VideoShotWriteResult> WriteAsync(VideoShotSetWrite write)
        {
            await using var db = CreateContext();
            return await new VideoShotService(db).WriteSetAsync(write);
        }

        public async Task<VideoShotSetDto?> GetAsync(int fileId)
        {
            await using var db = CreateContext();
            return await new VideoShotService(db).GetForFileAsync(fileId);
        }

        public async Task<(int VideoId, int FileId)> SeedVideoAsync(double duration, long size)
        {
            await using var db = CreateContext();
            var file = new VideoFile
            {
                ParentFolder = new Folder { Path = $"/library/shots/{Guid.NewGuid():N}" },
                Basename = "source.mp4",
                Duration = duration,
                Size = size,
            };
            var video = new Video { Title = "Shots", Files = [file] };
            db.Videos.Add(video);
            await db.SaveChangesAsync();
            return (video.Id, file.Id);
        }

        public async Task<int> AttachFileAsync(int videoId, double duration, long size)
        {
            await using var db = CreateContext();
            var file = new VideoFile
            {
                VideoId = videoId,
                ParentFolder = new Folder { Path = $"/library/shots/{Guid.NewGuid():N}" },
                Basename = "copy.mp4",
                Duration = duration,
                Size = size,
            };
            db.VideoFiles.Add(file);
            await db.SaveChangesAsync();
            return file.Id;
        }

        public async Task SetPrimaryFileAsync(int videoId, int fileId)
        {
            await using var db = CreateContext();
            var video = await db.Videos.SingleAsync(candidate => candidate.Id == videoId);
            video.PrimaryFileId = fileId;
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync() => await _anchor.DisposeAsync();
    }
}
