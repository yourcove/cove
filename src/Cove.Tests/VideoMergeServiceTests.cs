using System.Reflection;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

/// <summary>
/// The shared merge behind both the library merge and the duplicate resolver: the default policy, the
/// two ways of handling files, and the timeline rule that decides where markers land.
/// </summary>
public sealed class VideoMergeServiceTests
{
    [Fact]
    public async Task DefaultPolicyFillsEmptyFieldsCarriesEngagementAndAttachesFiles()
    {
        await using var harness = await Harness.CreateAsync();
        var (kept, removed) = await harness.SeedPairAsync(keptDuration: 60, removedDuration: 90);
        await using (var db = harness.CreateContext())
        {
            db.AddRange(
                new Rating { UserId = 1, HostType = RatingHostType.Video, HostId = removed.Id, Value = 80 },
                new UserEntityAffinity { UserId = 1, HostType = AffinityHostType.Video, HostId = removed.Id, ViewCount = 3, IsFavorite = true },
                new UserEntityAffinity { UserId = 1, HostType = AffinityHostType.Video, HostId = kept.Id, ViewCount = 2 },
                new UserBookmark { UserId = 1, HostType = AffinityHostType.Video, HostId = removed.Id },
                new VideoPlayHistory { VideoId = removed.Id, PlayedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var result = await harness.MergeAsync(new VideoMergePlan(kept.Id, [removed.Id]));

        Assert.Equal(VideoMergeOutcome.Merged, result.Outcome);
        Assert.Equal([removed.Id], result.MergedVideoIds);
        Assert.Empty(result.TimelineKeptVideoIds);
        await using var verify = harness.CreateContext();
        Assert.False(await verify.Videos.AnyAsync(video => video.Id == removed.Id));
        var video = await verify.Videos.Include(item => item.Files).Include(item => item.PlayHistory).SingleAsync(item => item.Id == kept.Id);
        Assert.Equal("Kept title", video.Title);
        Assert.Equal("Details only the removed copy had", video.Details);
        Assert.Equal("Removed director", video.Director);
        Assert.True(video.Organized);
        Assert.Equal(["kept.mp4", "removed.mp4"], video.Files.Select(file => file.Basename).Order());
        Assert.Single(video.PlayHistory);
        var rating = Assert.Single(await verify.Ratings.ToListAsync());
        Assert.Equal(kept.Id, rating.HostId);
        var affinity = Assert.Single(await verify.UserEntityAffinities.ToListAsync());
        Assert.Equal(kept.Id, affinity.HostId);
        Assert.Equal(5, affinity.ViewCount);
        Assert.True(affinity.IsFavorite);
        Assert.Equal(kept.Id, Assert.Single(await verify.UserBookmarks.ToListAsync()).HostId);
        var provenance = await verify.FieldProvenance.Where(item => item.HostId == kept.Id).ToListAsync();
        Assert.Equal(["details", "director", "organized"], provenance.Select(item => item.FieldKey).Order());
        Assert.All(provenance, item => Assert.Equal(VideoMergeService.DefaultPolicySourceKey, item.SourceKey));
        var published = harness.Events.OfType<EntityEvent>().Select(item => (item.Type, item.EntityId)).ToArray();
        Assert.Contains((EventType.VideoUpdated, kept.Id), published);
        Assert.Contains((EventType.VideoDeleted, removed.Id), published);
    }

    [Fact]
    public async Task ExplicitChoicesOverrideTheDefaultAndAreRecordedAsManual()
    {
        await using var harness = await Harness.CreateAsync();
        var (kept, removed) = await harness.SeedPairAsync(keptDuration: 60, removedDuration: 60);

        var result = await harness.MergeAsync(new VideoMergePlan(kept.Id, [removed.Id], Metadata: new VideoMergeMetadataDto(
            Fields: new() { ["title"] = "source", ["details"] = "target" })));

        Assert.Equal(VideoMergeOutcome.Merged, result.Outcome);
        await using var verify = harness.CreateContext();
        var video = await verify.Videos.SingleAsync(item => item.Id == kept.Id);
        Assert.Equal("Removed title", video.Title);
        // "target" keeps the kept value even when it is empty; only unmentioned fields follow the default.
        Assert.Null(video.Details);
        Assert.Equal("Removed director", video.Director);
        var provenance = await verify.FieldProvenance.Where(item => item.HostId == kept.Id).ToDictionaryAsync(item => item.FieldKey, item => item.SourceKey);
        Assert.Equal(VideoMergeService.ChosenSourceKey, provenance["title"]);
        Assert.Equal(VideoMergeService.DefaultPolicySourceKey, provenance["director"]);
        Assert.DoesNotContain("details", provenance.Keys);
    }

    [Fact]
    public async Task RemoveModeLeavesFilesBehindAndKeepsTimelineItemsWhenRunningTimesDiffer()
    {
        await using var harness = await Harness.CreateAsync();
        var (kept, removed) = await harness.SeedPairAsync(keptDuration: 60, removedDuration: 90, phash: "00ff00ff00ff00ff");
        await harness.AddTimelineItemsAsync(removed.Id);

        var result = await harness.MergeAsync(new VideoMergePlan(kept.Id, [removed.Id], VideoMergeFileHandling.Remove));

        Assert.Equal(VideoMergeOutcome.Merged, result.Outcome);
        Assert.Equal([removed.Id], result.TimelineKeptVideoIds);
        await using var verify = harness.CreateContext();
        // The copy and its file survive for the caller's deletion pipeline; metadata already moved.
        var leftover = await verify.Videos.Include(item => item.Files).SingleAsync(item => item.Id == removed.Id);
        Assert.Equal(["removed.mp4"], leftover.Files.Select(file => file.Basename));
        var video = await verify.Videos.Include(item => item.Files).Include(item => item.GroupItems).SingleAsync(item => item.Id == kept.Id);
        Assert.Equal(["kept.mp4"], video.Files.Select(file => file.Basename));
        Assert.Equal("Details only the removed copy had", video.Details);
        Assert.Equal(kept.PrimaryFileId, video.PrimaryFileId);
        // The whole-video membership moved; the timed range and the user marker stayed on the copy's timeline.
        Assert.Equal([null], video.GroupItems.Select(item => item.StartSec));
        Assert.Equal(removed.Id, Assert.Single(await verify.Segments.ToListAsync()).HostId);
        Assert.Equal(removed.Id, Assert.Single(await verify.GroupItems.Where(item => item.StartSec != null).ToListAsync()).VideoId);
    }

    [Fact]
    public async Task RemoveModeMovesTimelineItemsWhenTheFilesAreEquivalent()
    {
        await using var harness = await Harness.CreateAsync();
        var (kept, removed) = await harness.SeedPairAsync(keptDuration: 60, removedDuration: 60.5, phash: "00ff00ff00ff00ff");
        await harness.AddTimelineItemsAsync(removed.Id);

        var result = await harness.MergeAsync(new VideoMergePlan(kept.Id, [removed.Id], VideoMergeFileHandling.Remove));

        Assert.Equal(VideoMergeOutcome.Merged, result.Outcome);
        Assert.Empty(result.TimelineKeptVideoIds);
        await using var verify = harness.CreateContext();
        Assert.Equal(kept.Id, Assert.Single(await verify.Segments.ToListAsync()).HostId);
        Assert.All(await verify.GroupItems.ToListAsync(), item => Assert.Equal(kept.Id, item.VideoId));
    }

    [Fact]
    public async Task RemoveModeFallsBackToRunningTimesWhenNoFingerprintIsStored()
    {
        await using var harness = await Harness.CreateAsync();
        var (kept, removed) = await harness.SeedPairAsync(keptDuration: 60, removedDuration: 60.5);
        await harness.AddTimelineItemsAsync(removed.Id);

        var result = await harness.MergeAsync(new VideoMergePlan(kept.Id, [removed.Id], VideoMergeFileHandling.Remove));

        Assert.Equal(VideoMergeOutcome.Merged, result.Outcome);
        Assert.Empty(result.TimelineKeptVideoIds);
        await using var verify = harness.CreateContext();
        Assert.Equal(kept.Id, Assert.Single(await verify.Segments.ToListAsync()).HostId);
    }

    [Fact]
    public async Task RemoveModeReportsKeptTimelinesOnlyWhenSomethingStayedBehind()
    {
        await using var harness = await Harness.CreateAsync();
        var (kept, removed) = await harness.SeedPairAsync(keptDuration: 60, removedDuration: 90);
        await using (var db = harness.CreateContext())
        {
            // A generated segment describes the departing file; it is neither carried nor reported.
            db.Segments.Add(new Segment { HostType = SegmentHostType.Video, HostId = removed.Id, StartSec = 3, SourceKey = "detector", Title = "Scene" });
            await db.SaveChangesAsync();
        }

        var result = await harness.MergeAsync(new VideoMergePlan(kept.Id, [removed.Id], VideoMergeFileHandling.Remove));

        Assert.Equal(VideoMergeOutcome.Merged, result.Outcome);
        Assert.Equal([removed.Id], result.MergedVideoIds);
        Assert.Empty(result.TimelineKeptVideoIds);
    }

    [Theory]
    [InlineData(60, "00ff00ff00ff00ff", 61, "00ff00ff00ff00fe", true)]
    [InlineData(60, "00ff00ff00ff00ff", 61.01, "00ff00ff00ff00ff", false)]
    [InlineData(60, "0000000000000000", 60, "00000000000001ff", false)]
    [InlineData(60, "0000000000000000", 60, "00000000000000ff", true)]
    [InlineData(60, null, 60, "00ff00ff00ff00ff", false)]
    public void EquivalenceNeedsBothHashesCloseAndTheSameLength(double sourceDuration, string? sourceHash, double targetDuration, string? targetHash, bool expected)
        => Assert.Equal(expected, VideoFileEquivalence.AreEquivalent(sourceDuration, sourceHash, targetDuration, targetHash));

    [Theory]
    [InlineData(60, null, 60.5, null, true)]
    [InlineData(60, null, 62, null, false)]
    [InlineData(0, null, 0, null, false)]
    [InlineData(60, "0000000000000000", 60, "00000000000001ff", false)]
    public void WithoutAHashTheRunningTimesDecide(double sourceDuration, string? sourceHash, double targetDuration, string? targetHash, bool expected)
        => Assert.Equal(expected, VideoFileEquivalence.AreEquivalentOrSameLength(sourceDuration, sourceHash, targetDuration, targetHash));

    [Fact]
    public async Task ChoicesWithSeveralRemovedVideosReadTheSourceSideAsTheFirstCopyWithAValue()
    {
        await using var harness = await Harness.CreateAsync();
        var (kept, removed) = await harness.SeedPairAsync(keptDuration: 60, removedDuration: 60);
        int secondId;
        await using (var db = harness.CreateContext())
        {
            var second = new Video { Title = "Second title", Code = "CODE-2", ImageBlobId = "cover-of-second", Files = [new VideoFile { ParentFolder = new Folder { Path = "/library/second" }, Basename = "second.mp4" }] };
            db.Videos.Add(second);
            await db.SaveChangesAsync();
            secondId = second.Id;
        }

        var result = await harness.MergeAsync(new VideoMergePlan(kept.Id, [secondId, removed.Id], Metadata: new VideoMergeMetadataDto(
            Fields: new() { ["title"] = "source", ["code"] = "source", ["cover"] = "source", ["details"] = "target" })));

        Assert.Equal(VideoMergeOutcome.Merged, result.Outcome);
        await using var verify = harness.CreateContext();
        var video = await verify.Videos.Include(item => item.Files).SingleAsync(item => item.Id == kept.Id);
        // Title comes from the lower id (the first copy with a title); code and cover from the only copy that has one.
        Assert.Equal("Removed title", video.Title);
        Assert.Equal("CODE-2", video.Code);
        Assert.Equal("cover-of-second", video.ImageBlobId);
        Assert.Null(video.Details);
        Assert.Equal(3, video.Files.Count);
        Assert.False(await verify.Videos.AnyAsync(item => item.Id == removed.Id || item.Id == secondId));
    }

    [Fact]
    public async Task AssessmentNamesCopiesWhoseTimelineItemsWouldNotFollow()
    {
        await using var harness = await Harness.CreateAsync();
        var (kept, removed) = await harness.SeedPairAsync(keptDuration: 60, removedDuration: 90);
        await harness.AddTimelineItemsAsync(removed.Id);

        await using var db = harness.CreateContext();
        var service = new VideoMergeService(db, new CustomFieldService(db), NoOp<IBlobService>.Create(), NoOp<IStreamService>.Create(), new EventBus());
        var assessment = Assert.Single(await service.AssessAsync(kept.Id, [removed.Id, kept.Id, 0], CancellationToken.None));

        Assert.Equal(removed.Id, assessment.VideoId);
        Assert.False(assessment.FilesEquivalent);
        // One user marker and one timed group item; the whole-video membership does not depend on the timeline.
        Assert.Equal(2, assessment.TimelineItemCount);
    }

    [Fact]
    public async Task AssessmentReportsEquivalentFilesSoMarkersFollow()
    {
        await using var harness = await Harness.CreateAsync();
        var (kept, removed) = await harness.SeedPairAsync(keptDuration: 60, removedDuration: 60.5, phash: "00ff00ff00ff00ff");
        await harness.AddTimelineItemsAsync(removed.Id);

        await using var db = harness.CreateContext();
        var service = new VideoMergeService(db, new CustomFieldService(db), NoOp<IBlobService>.Create(), NoOp<IStreamService>.Create(), new EventBus());
        var assessment = Assert.Single(await service.AssessAsync(kept.Id, [removed.Id], CancellationToken.None));

        Assert.True(assessment.FilesEquivalent);
        Assert.Equal(2, assessment.TimelineItemCount);
    }

    [Fact]
    public async Task ChoicesCanAddAUrlNextToTheOnesTheVideosAlreadyHave()
    {
        await using var harness = await Harness.CreateAsync();
        var (kept, removed) = await harness.SeedPairAsync(keptDuration: 60, removedDuration: 60);
        await using (var db = harness.CreateContext())
        {
            db.AddRange(
                new VideoUrl { VideoId = kept.Id, Url = "https://kept.example/a" },
                new VideoUrl { VideoId = removed.Id, Url = "https://removed.example/b" });
            await db.SaveChangesAsync();
        }

        var result = await harness.MergeAsync(new VideoMergePlan(kept.Id, [removed.Id], Metadata: new VideoMergeMetadataDto(
            Urls: ["https://kept.example/a", "https://removed.example/b", "https://example.test/added-in-review"])));

        Assert.Equal(VideoMergeOutcome.Merged, result.Outcome);
        await using var verify = harness.CreateContext();
        var urls = await verify.Set<VideoUrl>().Where(url => url.VideoId == kept.Id).Select(url => url.Url).OrderBy(url => url).ToListAsync();
        Assert.Equal(["https://example.test/added-in-review", "https://kept.example/a", "https://removed.example/b"], urls);
    }

    [Fact]
    public async Task MetadataChoicesAreRejectedWhenARemovedVideoIsMissing()
    {
        await using var harness = await Harness.CreateAsync();
        var (kept, removed) = await harness.SeedPairAsync(keptDuration: 60, removedDuration: 60);

        var result = await harness.MergeAsync(new VideoMergePlan(kept.Id, [removed.Id, removed.Id + 100], Metadata: new VideoMergeMetadataDto()));

        Assert.Equal(VideoMergeOutcome.InvalidMetadata, result.Outcome);
        await using var verify = harness.CreateContext();
        Assert.True(await verify.Videos.AnyAsync(video => video.Id == removed.Id));
    }

    [Fact]
    public async Task AttachModeCarriesTheCopysShotBoundariesWithItsFile()
    {
        await using var harness = await Harness.CreateAsync();
        var (kept, removed) = await harness.SeedPairAsync(keptDuration: 60, removedDuration: 90);
        var removedFileId = removed.Files.Single().Id;
        await harness.WriteShotsAsync(removedFileId, 90);

        await harness.MergeAsync(new VideoMergePlan(kept.Id, [removed.Id]));

        await using var verify = harness.CreateContext();
        var shots = new Cove.Data.Services.VideoShotService(verify);
        var set = Assert.Single(await shots.ListForVideoAsync(kept.Id, TestContext.Current.CancellationToken));
        Assert.Equal((removedFileId, false), (set.FileId, set.IsPrimaryFile));
        Assert.Null(await shots.GetForVideoAsync(kept.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoveModeLeavesShotBoundariesWithTheCopyUntilItsFileIsDeleted()
    {
        await using var harness = await Harness.CreateAsync();
        var (kept, removed) = await harness.SeedPairAsync(keptDuration: 60, removedDuration: 90);
        var removedFileId = removed.Files.Single().Id;
        await harness.WriteShotsAsync(removedFileId, 90);

        await harness.MergeAsync(new VideoMergePlan(kept.Id, [removed.Id], VideoMergeFileHandling.Remove));

        await using (var verify = harness.CreateContext())
        {
            Assert.Equal(removed.Id, Assert.Single(await new Cove.Data.Services.VideoShotService(verify).ListForVideoAsync(removed.Id, TestContext.Current.CancellationToken)).VideoId);
            verify.VideoFiles.Remove(await verify.VideoFiles.SingleAsync(file => file.Id == removedFileId, TestContext.Current.CancellationToken));
            await verify.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var afterDelete = harness.CreateContext();
        Assert.False(await afterDelete.VideoShotSets.AnyAsync(TestContext.Current.CancellationToken));
        Assert.False(await afterDelete.VideoShots.AnyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoveModeCarriesShotBoundariesOntoAnEquivalentKeptFile()
    {
        await using var harness = await Harness.CreateAsync();
        var (kept, removed) = await harness.SeedPairAsync(keptDuration: 60, removedDuration: 60, phash: "00ff00ff00ff00ff");
        await harness.WriteShotsAsync(removed.Files.Single().Id, 60);

        await harness.MergeAsync(new VideoMergePlan(kept.Id, [removed.Id], VideoMergeFileHandling.Remove));

        await using var verify = harness.CreateContext();
        var shots = new Cove.Data.Services.VideoShotService(verify);
        Assert.Equal(kept.Files.Single().Id, (await shots.GetForVideoAsync(kept.Id, TestContext.Current.CancellationToken))!.FileId);
        Assert.Empty(await shots.ListForVideoAsync(removed.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoveModeCarriesShotBoundariesFromAnyFileOfTheCopy()
    {
        await using var harness = await Harness.CreateAsync();
        var (kept, removed) = await harness.SeedPairAsync(keptDuration: 60, removedDuration: 90, phash: "00ff00ff00ff00ff");
        var secondFileId = await harness.AttachFileAsync(removed.Id, duration: 60, phash: "00ff00ff00ff00ff");
        await harness.WriteShotsAsync(secondFileId, 60);

        await harness.MergeAsync(new VideoMergePlan(kept.Id, [removed.Id], VideoMergeFileHandling.Remove));

        await using var verify = harness.CreateContext();
        Assert.Equal(kept.Files.Single().Id, (await new Cove.Data.Services.VideoShotService(verify).GetForVideoAsync(kept.Id, TestContext.Current.CancellationToken))!.FileId);
    }

    [Fact]
    public async Task RemoveModeLeavesShotBoundariesOfACopyWithoutFingerprints()
    {
        await using var harness = await Harness.CreateAsync();
        var (kept, removed) = await harness.SeedPairAsync(keptDuration: 60, removedDuration: 60);
        await harness.WriteShotsAsync(removed.Files.Single().Id, 60);

        await harness.MergeAsync(new VideoMergePlan(kept.Id, [removed.Id], VideoMergeFileHandling.Remove));

        await using var verify = harness.CreateContext();
        var shots = new Cove.Data.Services.VideoShotService(verify);
        Assert.Null(await shots.GetForVideoAsync(kept.Id, TestContext.Current.CancellationToken));
        Assert.Single(await shots.ListForVideoAsync(removed.Id, TestContext.Current.CancellationToken));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection _anchor;
        private readonly DbContextOptions<CoveContext> _options;

        private Harness(SqliteConnection anchor, DbContextOptions<CoveContext> options)
        {
            _anchor = anchor;
            _options = options;
        }

        public List<object> Events { get; } = [];

        public static async Task<Harness> CreateAsync()
        {
            var connectionString = $"Data Source=video-merge-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            var options = new DbContextOptionsBuilder<CoveContext>().UseSqlite(connectionString).Options;
            var harness = new Harness(anchor, options);
            await using var db = harness.CreateContext();
            await db.Database.EnsureCreatedAsync();
            if (!await db.Users.AnyAsync(user => user.Id == 1))
            {
                db.Users.Add(new Cove.Core.Entities.Auth.User { Id = 1, Username = "merge-tester", PasswordHash = "test" });
                await db.SaveChangesAsync();
            }
            return harness;
        }

        public CoveContext CreateContext() => new(_options);

        public async Task<VideoMergeResult> MergeAsync(VideoMergePlan plan)
        {
            await using var db = CreateContext();
            var eventBus = new EventBus();
            using var subscription = eventBus.Subscribe<EntityEvent>(Events.Add);
            var service = new VideoMergeService(
                db,
                new CustomFieldService(db),
                NoOp<IBlobService>.Create(),
                NoOp<IStreamService>.Create(),
                eventBus,
                fieldProvenanceService: new FieldProvenanceService(db));
            return await service.MergeAsync(plan, CancellationToken.None);
        }

        public async Task<(Video Kept, Video Removed)> SeedPairAsync(double keptDuration, double removedDuration, string? phash = null)
        {
            await using var db = CreateContext();
            var folder = new Folder { Path = "/library/merge" };
            var kept = new Video { Title = "Kept title", Files = [new VideoFile { ParentFolder = folder, Basename = "kept.mp4", Duration = keptDuration }] };
            var removed = new Video
            {
                Title = "Removed title",
                Details = "Details only the removed copy had",
                Director = "Removed director",
                Organized = true,
                Files = [new VideoFile { ParentFolder = folder, Basename = "removed.mp4", Duration = removedDuration }],
            };
            db.AddRange(kept, removed);
            await db.SaveChangesAsync();
            kept.PrimaryFileId = kept.Files.Single().Id;
            removed.PrimaryFileId = removed.Files.Single().Id;
            if (phash != null)
                db.FileFingerprints.AddRange(
                    new FileFingerprint { FileId = kept.PrimaryFileId.Value, Type = "phash", Value = phash },
                    new FileFingerprint { FileId = removed.PrimaryFileId.Value, Type = "phash", Value = phash });
            await db.SaveChangesAsync();
            return (kept, removed);
        }

        public async Task AddTimelineItemsAsync(int videoId)
        {
            await using var db = CreateContext();
            var group = new Group { Name = "Merge group" };
            db.Groups.Add(group);
            await db.SaveChangesAsync();
            db.AddRange(
                new GroupItem { GroupId = group.Id, Kind = GroupItemKind.Video, HostType = "video", HostId = videoId, VideoId = videoId },
                new GroupItem { GroupId = group.Id, Kind = GroupItemKind.Video, HostType = "video", HostId = videoId, VideoId = videoId, StartSec = 5, EndSec = 10 },
                new Segment { HostType = SegmentHostType.Video, HostId = videoId, StartSec = 12, SourceKey = "user", Title = "Marker" });
            await db.SaveChangesAsync();
        }

        public async Task<int> AttachFileAsync(int videoId, double duration, string phash)
        {
            await using var db = CreateContext();
            var file = new VideoFile { VideoId = videoId, ParentFolder = new Folder { Path = $"/library/merge/{Guid.NewGuid():N}" }, Basename = "second.mp4", Duration = duration };
            db.VideoFiles.Add(file);
            await db.SaveChangesAsync();
            db.FileFingerprints.Add(new FileFingerprint { FileId = file.Id, Type = "phash", Value = phash });
            await db.SaveChangesAsync();
            return file.Id;
        }

        public async Task WriteShotsAsync(int fileId, double duration)
        {
            await using var db = CreateContext();
            var result = await new Cove.Data.Services.VideoShotService(db).WriteSetAsync(new VideoShotSetWrite
            {
                FileId = fileId,
                SourceKey = "ext:ai.shots",
                DurationSec = duration,
                Shots =
                [
                    new VideoShotInput { StartSec = 0, EndSec = duration / 2 },
                    new VideoShotInput { StartSec = duration / 2, EndSec = duration },
                ],
            });
            Assert.Equal(VideoShotWriteOutcome.Written, result.Outcome);
        }

        public async ValueTask DisposeAsync() => await _anchor.DisposeAsync();
    }

    /// <summary>Implements a wide service interface whose behavior these tests never observe.</summary>
    private class NoOp<T> : DispatchProxy where T : class
    {
        public static T Create() => DispatchProxy.Create<T, NoOp<T>>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var returnType = targetMethod?.ReturnType;
            if (returnType is null || returnType == typeof(void))
                return null;
            if (returnType == typeof(Task))
                return Task.CompletedTask;
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = returnType.GetGenericArguments()[0];
                var result = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, [result]);
            }
            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }
}
