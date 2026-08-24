using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class DuplicateSearchJobTests
{
    [Fact]
    public async Task StartPersistsOwnerAndQueuesDurableResultLink()
    {
        await using var db = CreateContext();
        var jobs = new CapturingJobService();
        var service = new DuplicateSearchJobService(
            db,
            jobs,
            null!);
        var owner = new JobOwner("user:29");

        var queued = await service.StartAsync(
            owner,
            null,
            new DuplicateSearchRequestDto("phash", 8, 10),
            [3, 7, 11],
            CancellationToken.None);

        var search = await db.DuplicateSearches.SingleAsync();
        Assert.Equal(queued.SearchId, search.Id);
        Assert.Equal("user:29", search.OwnerKey);
        Assert.Equal(DuplicateSearchStatus.Pending, search.Status);
        Assert.Equal(3, search.CandidateCount);
        Assert.Equal("duplicate-search", jobs.Type);
        Assert.Equal($"/duplicates?search={search.Id:D}", jobs.ResultUrl);
    }

    [Fact]
    public void PhashGroupingSkipsCandidatesOutsideTheDurationWindow()
    {
        var result = DuplicateSearchExecutionService.FindPhashGroupsForTests(
        [
            new DuplicatePHashCandidate(1, 0, 0),
            new DuplicatePHashCandidate(2, 5, 1),
            new DuplicatePHashCandidate(3, 100, 0),
        ], maxDistance: 1, maxDurationDifference: 10);

        Assert.Equal(1, result.ComparisonCount);
        var group = Assert.Single(result.Groups);
        Assert.Equal([1, 2], group);
    }

    [Fact]
    public void PhashGroupingCollapsesTransitiveMatchesIntoOneConnectedGroup()
    {
        var result = DuplicateSearchExecutionService.FindPhashGroupsForTests(
        [
            new DuplicatePHashCandidate(1, 0, 0b00),
            new DuplicatePHashCandidate(2, 0, 0b01),
            new DuplicatePHashCandidate(3, 0, 0b11),
        ], maxDistance: 1, maxDurationDifference: 10);

        var group = Assert.Single(result.Groups);
        Assert.Equal([1, 2, 3], group);
    }

    [Fact]
    public void OversizedGroupsShareOneKeeperAcrossBoundedChunks()
    {
        var groups = DuplicateSearchExecutionService.SplitOversizedGroups(
            [Enumerable.Range(1, 101)],
            maximumGroupSize: 50);

        Assert.Equal([50, 50, 3], groups.Select(group => group.Length).ToArray());
        Assert.All(groups, group => Assert.Contains(1, group));
        Assert.Equal(Enumerable.Range(1, 101), groups.SelectMany(group => group).Distinct().OrderBy(id => id));
        Assert.All(groups, group => Assert.InRange(group.Length, 2, 50));
    }

    [Fact]
    public void PersistedGroupLimitRejectsAHazardousResultGraph()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            DuplicateSearchExecutionService.SplitOversizedGroups(
                [[1, 2], [3, 4], [5, 6], [7, 8]],
                maximumGroupSize: 50,
                maximumGroupCount: 3));

        Assert.Contains("more than 3 duplicate groups", exception.Message);
    }

    [Fact]
    public async Task MissingKeeperMakesTheRemainingGroupIneligibleForDeletion()
    {
        await using var db = CreateContext();
        var keeper = new Video { Title = "Keeper" };
        var unwanted = new Video { Title = "Unwanted" };
        var search = CompletedSearch();
        search.Groups.Add(new DuplicateSearchGroup
        {
            Position = 0,
            Items =
            [
                new DuplicateSearchItem { Video = keeper, Keep = true },
                new DuplicateSearchItem { Video = unwanted, Keep = false },
            ],
        });
        db.DuplicateSearches.Add(search);
        await db.SaveChangesAsync();

        db.Videos.Remove(keeper);
        await db.SaveChangesAsync();

        Assert.Empty(await DuplicateSearchJobService.EffectiveUnkeptVideoIds(db, search.Id).ToArrayAsync());
        Assert.True(await db.Videos.AnyAsync(video => video.Id == unwanted.Id));
    }

    [Fact]
    public async Task SharedKeeperAllowsEveryOtherMemberOfAnOversizedGroupToBeDeleted()
    {
        await using var db = CreateContext();
        var videos = Enumerable.Range(1, 101).Select(id => new Video { Title = $"Video {id}" }).ToArray();
        db.Videos.AddRange(videos);
        await db.SaveChangesAsync();
        var search = CompletedSearch();
        var groups = DuplicateSearchExecutionService.SplitOversizedGroups(
            [videos.Select(video => video.Id)],
            maximumGroupSize: 50);
        for (var position = 0; position < groups.Count; position++)
        {
            search.Groups.Add(new DuplicateSearchGroup
            {
                Position = position,
                Items = groups[position]
                    .Select(id => new DuplicateSearchItem { VideoId = id, Keep = id == videos[0].Id })
                    .ToList(),
            });
        }
        db.DuplicateSearches.Add(search);
        await db.SaveChangesAsync();

        var unwantedIds = await DuplicateSearchJobService.EffectiveUnkeptVideoIds(db, search.Id)
            .OrderBy(id => id)
            .ToArrayAsync();

        Assert.Equal(videos.Skip(1).Select(video => video.Id), unwantedIds);
    }

    [Fact]
    public async Task KeeperReservationPreventsAnIndependentVideoDelete()
    {
        await using var db = CreateContext();
        var keeper = new Video { Title = "Reserved keeper" };
        var search = CompletedSearch();
        db.AddRange(keeper, search);
        await db.SaveChangesAsync();
        db.DuplicateDeletionKeeperReservations.Add(new DuplicateDeletionKeeperReservation
        {
            SearchId = search.Id,
            VideoId = keeper.Id,
        });
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        db.Videos.Remove(await db.Videos.SingleAsync(video => video.Id == keeper.Id));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task TerminalFailedDeletionReleasesClaimAndKeeperReservationWhenWorkRemains()
    {
        await using var db = CreateContext();
        var (search, keeper, _) = await AddClaimedSearchAsync(db, "failed-delete-job");
        var jobs = new CapturingJobService
        {
            ReturnedJob = new JobInfo(
                "failed-delete-job",
                "video-deletion",
                "Deleting duplicate videos",
                JobStatus.Failed,
                1,
                null,
                DateTime.UtcNow.AddSeconds(-1),
                DateTime.UtcNow,
                "A deletion failed."),
        };
        var service = new DuplicateSearchJobService(db, jobs, null!);

        Assert.True(await service.ReconcileTerminalDeletionAsync(search, CancellationToken.None));

        Assert.Null((await db.DuplicateSearches.AsNoTracking().SingleAsync(item => item.Id == search.Id)).DeletionJobId);
        Assert.False(await db.DuplicateDeletionKeeperReservations.AnyAsync(item => item.VideoId == keeper.Id));
    }

    [Fact]
    public async Task TerminalCompletedDeletionRetainsClaimWhenNoWorkRemains()
    {
        await using var db = CreateContext();
        var (search, keeper, unwanted) = await AddClaimedSearchAsync(db, "completed-delete-job");
        db.Videos.Remove(unwanted);
        await db.SaveChangesAsync();
        var jobs = new CapturingJobService
        {
            ReturnedJob = new JobInfo(
                "completed-delete-job",
                "video-deletion",
                "Deleting duplicate videos",
                JobStatus.Completed,
                1,
                null,
                DateTime.UtcNow.AddSeconds(-1),
                DateTime.UtcNow,
                null),
        };
        var service = new DuplicateSearchJobService(db, jobs, null!);

        Assert.False(await service.ReconcileTerminalDeletionAsync(search, CancellationToken.None));

        Assert.Equal("completed-delete-job", (await db.DuplicateSearches.AsNoTracking().SingleAsync(item => item.Id == search.Id)).DeletionJobId);
        Assert.False(await db.DuplicateDeletionKeeperReservations.AnyAsync(item => item.VideoId == keeper.Id));
    }

    [Fact]
    public async Task ActiveDeletionKeepsClaimAndKeeperReservation()
    {
        await using var db = CreateContext();
        var (search, keeper, _) = await AddClaimedSearchAsync(db, "active-delete-job");
        var jobs = new CapturingJobService
        {
            ReturnedJob = new JobInfo(
                "active-delete-job",
                "video-deletion",
                "Deleting duplicate videos",
                JobStatus.Running,
                0.5,
                "Deleting",
                DateTime.UtcNow,
                null,
                null),
        };
        var service = new DuplicateSearchJobService(db, jobs, null!);

        Assert.False(await service.ReconcileTerminalDeletionAsync(search, CancellationToken.None));

        Assert.Equal("active-delete-job", (await db.DuplicateSearches.AsNoTracking().SingleAsync(item => item.Id == search.Id)).DeletionJobId);
        Assert.True(await db.DuplicateDeletionKeeperReservations.AnyAsync(item => item.VideoId == keeper.Id));
    }

    [Fact]
    public async Task CancellingAQueuedDeletionReleasesItsClaimAndKeeperReservation()
    {
        await using var db = CreateContext();
        var (search, keeper, unwanted) = await AddClaimedSearchAsync(db, "queued-delete-job");
        var service = new DuplicateSearchJobService(db, new CapturingJobService(), null!);

        Assert.Equal(1, await service.ReleaseCancelledPendingDeletionAsync("queued-delete-job", CancellationToken.None));

        Assert.Null((await db.DuplicateSearches.AsNoTracking().SingleAsync(item => item.Id == search.Id)).DeletionJobId);
        Assert.False(await db.DuplicateDeletionKeeperReservations.AnyAsync(item => item.VideoId == keeper.Id));
        Assert.True(await db.Videos.AnyAsync(item => item.Id == unwanted.Id));
    }

    [Fact]
    public async Task StartupRecoveryReleasesOnlyIncompleteDeletionClaims()
    {
        await using var db = CreateContext();
        var interrupted = CompletedSearch();
        interrupted.Status = DuplicateSearchStatus.Running;
        interrupted.DeletionJobId = DuplicateSearchDeletionClaim.Create();
        var (incomplete, _, _) = await AddClaimedSearchAsync(db, "lost-delete-job");
        var (finished, _, finishedUnwanted) = await AddClaimedSearchAsync(db, "finished-delete-job");
        db.DuplicateSearches.Add(interrupted);
        await db.SaveChangesAsync();
        db.Videos.Remove(finishedUnwanted);
        await db.SaveChangesAsync();

        var recoveredAt = DateTime.UtcNow;
        await DuplicateSearchRecoveryService.RecoverAsync(db, recoveredAt, CancellationToken.None);

        var recoveredInterrupted = await db.DuplicateSearches.AsNoTracking().SingleAsync(item => item.Id == interrupted.Id);
        Assert.Equal(DuplicateSearchStatus.Interrupted, recoveredInterrupted.Status);
        Assert.Equal(recoveredAt, recoveredInterrupted.CompletedAt);
        Assert.Null(recoveredInterrupted.DeletionJobId);
        Assert.Null((await db.DuplicateSearches.AsNoTracking().SingleAsync(item => item.Id == incomplete.Id)).DeletionJobId);
        Assert.Equal("finished-delete-job", (await db.DuplicateSearches.AsNoTracking().SingleAsync(item => item.Id == finished.Id)).DeletionJobId);
        Assert.Empty(await db.DuplicateDeletionKeeperReservations.ToArrayAsync());
    }

    [Fact]
    public async Task StartClampsPathologicalPHashDistance()
    {
        await using var db = CreateContext();
        var jobs = new CapturingJobService();
        var service = new DuplicateSearchJobService(db, jobs, null!);

        await service.StartAsync(
            new JobOwner("user:29"),
            null,
            new DuplicateSearchRequestDto("phash", 64, double.MaxValue),
            [3, 7],
            CancellationToken.None);

        Assert.Equal(DuplicateSearchJobService.MaximumPHashDistance, (await db.DuplicateSearches.SingleAsync()).Distance);
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var context = new CoveContext(options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    private static DuplicateSearch CompletedSearch() => new()
    {
        Status = DuplicateSearchStatus.Completed,
        MatchType = "phash",
        ExpiresAt = DateTime.UtcNow.AddDays(7),
    };

    private static async Task<(DuplicateSearch Search, Video Keeper, Video Unwanted)> AddClaimedSearchAsync(
        CoveContext db,
        string deletionJobId)
    {
        var keeper = new Video { Title = $"Keeper for {deletionJobId}" };
        var unwanted = new Video { Title = $"Unwanted for {deletionJobId}" };
        var search = CompletedSearch();
        search.DeletionJobId = deletionJobId;
        search.Groups.Add(new DuplicateSearchGroup
        {
            Position = 0,
            Items =
            [
                new DuplicateSearchItem { Video = keeper, Keep = true },
                new DuplicateSearchItem { Video = unwanted, Keep = false },
            ],
        });
        db.DuplicateSearches.Add(search);
        await db.SaveChangesAsync();
        db.DuplicateDeletionKeeperReservations.Add(new DuplicateDeletionKeeperReservation
        {
            SearchId = search.Id,
            VideoId = keeper.Id,
        });
        await db.SaveChangesAsync();
        return (search, keeper, unwanted);
    }

    private sealed class CapturingJobService : IJobService
    {
        public string? Type { get; private set; }
        public string? ResultUrl { get; private set; }
        public JobInfo? ReturnedJob { get; init; }

        public string EnqueueOwned(JobOwner owner, string type, string description, Func<IJobProgress, CancellationToken, Task> work, string? resultUrl = null, bool exclusive = true)
        {
            Type = type;
            ResultUrl = resultUrl;
            return "duplicate-job";
        }

        public string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
            => "duplicate-job";
        public bool Cancel(string jobId) => false;
        public bool ReorderQueued(string jobId, string? beforeJobId) => false;
        public JobInfo? GetJob(string jobId) => ReturnedJob?.Id == jobId ? ReturnedJob : null;
        public IReadOnlyList<JobInfo> GetAllJobs() => [];
        public IReadOnlyList<JobInfo> GetJobHistory() => [];
    }
}
