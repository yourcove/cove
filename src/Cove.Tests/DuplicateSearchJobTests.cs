using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Cove.Api.Services;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cove.Tests;

public sealed class DuplicateSearchJobTests
{
    [Fact]
    [RequiresPostgres]
    public async Task StartPersistsOwnerScopeRulesAndQueuesDurableResultLink()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
        var jobs = new CapturingJobService();
        var service = new DuplicateSearchJobService(db, jobs, null!);

        var queued = await service.StartAsync(
            new JobOwner("user:29"),
            null,
            new DuplicateSearchStartRequest(
                "phash",
                8,
                10,
                IncludePaths: [" D:\\Library\\Sorted\\ ", "D:/Library/Sorted"],
                ExcludePaths: ["D:/Library/Sorted/Trash/"],
                MinimumDuration: 30,
                KeeperRules: [new("bitrate"), new("unknown"), new("bitrate"), new("codec", ["H.265", "avc1"])]),
            [3, 7, 11],
            CancellationToken.None);

        var search = await db.DuplicateSearches.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(queued.SearchId, search.Id);
        Assert.Equal("user:29", search.OwnerKey);
        Assert.Equal(DuplicateSearchStatus.Pending, search.Status);
        Assert.Equal(3, search.CandidateCount);
        Assert.Equal(["D:/Library/Sorted"], search.IncludePaths);
        Assert.Equal(["D:/Library/Sorted/Trash"], search.ExcludePaths);
        Assert.Equal(30, search.MinimumDuration);
        var rules = DuplicateKeeperRules.Deserialize(search.KeeperRulesJson);
        Assert.Equal(["bitrate", "codec"], rules.Select(rule => rule.Type));
        Assert.Equal(["hevc", "h264"], rules[1].Values);
        Assert.Equal("duplicate-search", jobs.Type);
        Assert.Equal($"/duplicates?search={search.Id:D}", jobs.ResultUrl);
    }

    [Fact]
    public void ScopePathNormalizationRetainsEveryDistinctPath()
    {
        var paths = Enumerable.Range(1, 125)
            .Select(index => $"/library/folder-{index}")
            .Append(" /library/folder-1/ ")
            .ToArray();

        var normalized = DuplicateSearchJobService.NormalizeScopePaths(paths);

        Assert.Equal(125, normalized.Length);
        Assert.Equal("/library/folder-125", normalized[^1]);
    }

    [Fact]
    [RequiresPostgres]
    public async Task StartClampsPathologicalPHashDistance()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
        var service = new DuplicateSearchJobService(db, new CapturingJobService(), null!);

        await service.StartAsync(
            new JobOwner("user:29"),
            null,
            new DuplicateSearchStartRequest("phash", 64, double.MaxValue),
            [3, 7],
            CancellationToken.None);

        Assert.Equal(DuplicateSearchJobService.MaximumPHashDistance, (await db.DuplicateSearches.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).Distance);
    }

    [Fact]
    public void ScopePathNormalizationRejectsAnOversizedAggregate()
    {
        var paths = Enumerable.Range(0, 257)
            .Select(index => $"/{index:D3}" + new string('x', DuplicateSearchLimits.MaximumFieldCharacters - 4))
            .ToArray();

        var error = Assert.Throws<InvalidOperationException>(() => DuplicateSearchJobService.NormalizeScopePaths(paths));

        Assert.Contains("scope paths", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("total limit", error.Message, StringComparison.OrdinalIgnoreCase);
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
    public void PhashGroupingOmitsPairsMarkedAsNotDuplicates()
    {
        var result = DuplicateSearchExecutionService.FindPhashGroupsForTests(
        [
            new DuplicatePHashCandidate(1, 0, 0),
            new DuplicatePHashCandidate(2, 0, 0),
        ], maxDistance: 2, maxDurationDifference: 10, ignored: new HashSet<(int, int)> { (1, 2) });

        Assert.Empty(result.Groups);
    }

    [Fact]
    public void BucketGroupingJoinsBucketsThatShareAVideo()
    {
        // An MD5 match and an OSHash match of overlapping videos are one duplicate set, not two groups.
        var groups = DuplicateSearchExecutionService.GroupBuckets([[1, 2], [2, 3], [4, 5]], new HashSet<(int, int)>());

        Assert.Equal([[1, 2, 3], [4, 5]], groups);
    }

    [Fact]
    public void BucketGroupingHonorsIgnoredPairsUnlessAThirdVideoConnectsThem()
    {
        var ignored = new HashSet<(int, int)> { (1, 2) };

        Assert.Empty(DuplicateSearchExecutionService.GroupBuckets([[1, 2]], ignored));
        var connected = Assert.Single(DuplicateSearchExecutionService.GroupBuckets([[1, 2, 3]], ignored));
        Assert.Equal([1, 2, 3], connected);
    }

    [Fact]
    public void TitleNormalizationCollapsesWhitespace()
        => Assert.Equal("A Title", DuplicateSearchExecutionService.NormalizeTitle("  A \t Title "));

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
    public void AResultWithMoreGroupsThanTheFormerLimitIsKept()
    {
        // Results are paged, so the number of groups a search may produce is not limited.
        var groups = DuplicateSearchExecutionService.SplitOversizedGroups(
            Enumerable.Range(0, 30_000).Select(index => new[] { 2 * index + 1, 2 * index + 2 }));

        Assert.Equal(30_000, groups.Count);
    }

    [Fact]
    public void KeyBucketsMatchKeysExactlyOrIgnoringCase()
    {
        var exact = DuplicateKeyCandidate.TakeBuckets(
        [
            DuplicateKeyCandidate.Create(1, "md5\nabc"),
            DuplicateKeyCandidate.Create(2, "md5\nABC"),
            DuplicateKeyCandidate.Create(3, "md5\nabc"),
            DuplicateKeyCandidate.Create(4, "oshash\nabc"),
        ]);
        Assert.Equal([1, 3], Assert.Single(exact));

        var ignoringCase = DuplicateKeyCandidate.TakeBuckets(
        [
            DuplicateKeyCandidate.CreateIgnoringCase(1, "Été à Paris"),
            DuplicateKeyCandidate.CreateIgnoringCase(2, "ÉTÉ À PARIS"),
            DuplicateKeyCandidate.CreateIgnoringCase(3, "Ete a Paris"),
            DuplicateKeyCandidate.CreateIgnoringCase(4, "été à paris"),
        ]);
        Assert.Equal([1, 2, 4], Assert.Single(ignoringCase));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    public void PhashGroupingFindsExactlyThePairsAnExhaustiveComparisonFinds(int maxDistance)
    {
        // Near-copies at every bit distance up to 18 and duration drifts on both sides of the tolerance.
        var random = new Random(maxDistance + 1);
        var candidates = new List<DuplicatePHashCandidate>();
        for (var id = 1; id <= 400; id++)
            candidates.Add(new DuplicatePHashCandidate(id, Math.Round(random.NextDouble() * 600, 3), (ulong)random.NextInt64(long.MinValue, long.MaxValue)));
        for (var id = 401; id <= 800; id++)
        {
            var source = candidates[random.Next(400)];
            var hash = source.Hash;
            for (var flip = 0; flip < id % 19; flip++)
                hash ^= 1UL << random.Next(64);
            candidates.Add(new DuplicatePHashCandidate(id, source.Duration + Math.Round(random.NextDouble() * 16 - 8, 3), hash));
        }
        // Two files of one video never match each other.
        candidates.Add(new DuplicatePHashCandidate(1, candidates[0].Duration, candidates[0].Hash));
        const double tolerance = 5;

        var expected = new DuplicateUnionFind();
        long expectedComparisons = 0;
        for (var left = 0; left < candidates.Count; left++)
        {
            for (var right = left + 1; right < candidates.Count; right++)
            {
                if (Math.Abs(candidates[left].Duration - candidates[right].Duration) > tolerance)
                    continue;
                expectedComparisons++;
                if (candidates[left].VideoId != candidates[right].VideoId
                    && System.Numerics.BitOperations.PopCount(candidates[left].Hash ^ candidates[right].Hash) <= maxDistance)
                    expected.Union(candidates[left].VideoId, candidates[right].VideoId);
            }
        }

        var result = DuplicateSearchExecutionService.FindPhashGroupsForTests(candidates, maxDistance, tolerance);

        Assert.NotEmpty(result.Groups);
        Assert.Equal(expected.ToGroups(), result.Groups);
        Assert.Equal(expectedComparisons, result.ComparisonCount);
    }

    [Fact]
    public void KeeperRulesRecordTheRuleThatSeparatedTheKeeper()
    {
        var facts = Facts(
            Fact(1, pixels: 1920 * 1080, bitRate: 8_000),
            Fact(2, pixels: 3840 * 2160, bitRate: 4_000),
            Fact(3, pixels: 3840 * 2160, bitRate: 6_000));

        var choice = DuplicateKeeperRules.Choose([1, 2, 3], facts, [new("resolution"), new("bitrate")]);

        Assert.Equal(3, choice.KeeperId);
        Assert.Equal("bitrate", choice.DecisionRule);
    }

    [Fact]
    public void KeeperRulesFallBackToTheLowestIdWhenEveryRuleTies()
    {
        var facts = Facts(Fact(9), Fact(4));

        var choice = DuplicateKeeperRules.Choose([9, 4], facts, [new("resolution"), new("metadata")]);

        Assert.Equal(4, choice.KeeperId);
        Assert.Equal(DuplicateKeeperRules.TieBreakRule, choice.DecisionRule);
    }

    [Fact]
    public void KeeperRulesPreferEarlierCodecsAndPaths()
    {
        var facts = Facts(
            Fact(1, codec: "h264", path: "D:/Downloads/a.mp4"),
            Fact(2, codec: "hevc", path: "D:/Downloads/b.mp4"),
            Fact(3, codec: "h264", path: "D:/Library/c.mp4"));

        Assert.Equal(2, DuplicateKeeperRules.Choose([1, 2, 3], facts, [new("codec", ["hevc", "h264"])]).KeeperId);
        Assert.Equal(3, DuplicateKeeperRules.Choose([1, 2, 3], facts, [new("path", ["D:\\Library"])]).KeeperId);
        var smallest = DuplicateKeeperRules.Choose([1, 2], Facts(Fact(1, size: 10), Fact(2, size: 5)), [new("size-smallest")]);
        Assert.Equal(2, smallest.KeeperId);
    }

    [Fact]
    public void KeeperRuleNormalizationDropsRulesThatCannotSeparateAnything()
    {
        var rules = DuplicateKeeperRules.Normalize([new("path", ["", "  "]), new("Resolution"), new("resolution"), new("nope")]);

        Assert.Equal(["resolution"], rules.Select(rule => rule.Type));
        Assert.Equal(DuplicateKeeperRules.Defaults, DuplicateKeeperRules.Deserialize("not json"));
    }

    [Fact]
    [RequiresPostgres]
    public async Task MissingKeeperMakesTheRemainingGroupIneligibleForDeletion()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Videos.Remove(keeper);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await DuplicateSearchJobService.EffectiveUnkeptVideoIds(db, search.Id).ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await db.Videos.AnyAsync(video => video.Id == unwanted.Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    [RequiresPostgres]
    public async Task SharedKeeperAllowsEveryOtherMemberOfAnOversizedGroupToBeDeleted()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
        var videos = Enumerable.Range(1, 101).Select(id => new Video { Title = $"Video {id}" }).ToArray();
        db.Videos.AddRange(videos);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var unwantedIds = await DuplicateSearchJobService.EffectiveUnkeptVideoIds(db, search.Id)
            .OrderBy(id => id)
            .ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(videos.Skip(1).Select(video => video.Id), unwantedIds);
    }

    [Fact]
    [RequiresPostgres]
    public async Task AVideoKeptByAnotherGroupIsNeverRemovedByThisGroup()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
        var (search, groups, videos) = await AddSearchAsync(db, [[(true, "a"), (false, "b")], [(false, "a"), (true, "c")]]);

        var removable = await DuplicateSearchJobService
            .EffectiveUnkeptVideoIds(db, search.Id, db.DuplicateSearchGroups.Where(group => group.Id == groups[1].Id))
            .ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(removable);
        Assert.Equal([videos["b"].Id], await DuplicateSearchJobService
            .EffectiveUnkeptVideoIds(db, search.Id, db.DuplicateSearchGroups.Where(group => group.Id == groups[0].Id))
            .ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    [RequiresPostgres]
    public async Task TheCrossGroupKeeperRuleIsTranslatedToAnExistenceCheckNotAListExclusion()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);

        // "NOT IN (kept list)" is only fast while the list fits in PostgreSQL's working memory; beyond that
        // it is rescanned for every row, and the summary of a search with a few hundred thousand groups times out.
        var sql = DuplicateSearchJobService.EffectiveUnkeptVideoIds(db, Guid.NewGuid()).ToQueryString();

        Assert.Contains("NOT EXISTS", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"NOT\s+IN\s*\(\s*SELECT", sql);
    }

    [Fact]
    [RequiresPostgres]
    public async Task KeeperReservationPreventsAnIndependentVideoDelete()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
        var keeper = new Video { Title = "Reserved keeper" };
        var search = CompletedSearch();
        db.AddRange(keeper, search);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.DuplicateDeletionKeeperReservations.Add(new DuplicateDeletionKeeperReservation
        {
            SearchId = search.Id,
            VideoId = keeper.Id,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        db.Videos.Remove(await db.Videos.SingleAsync(video => video.Id == keeper.Id, cancellationToken: TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [RequiresPostgres]
    public async Task QueuingReviewableGroupsStartsExactlyOneWorkerPerSearch()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
        var (search, groups, _) = await AddSearchAsync(db,
        [
            [(true, "a"), (false, "b")],
            [(true, "c"), (false, "d")],
            [(true, "e"), (false, "f")],
        ]);
        await db.DuplicateSearchGroups
            .Where(group => group.Id == groups[2].Id)
            .ExecuteUpdateAsync(update => update.SetProperty(group => group.Status, DuplicateGroupStatus.Ignored), cancellationToken: TestContext.Current.CancellationToken);
        var jobs = new CapturingJobService();
        var service = new DuplicateResolutionService(db, jobs, null!, new CoveConfiguration());

        var first = await service.QueueAsync(search.Id, [groups[0].Id, groups[2].Id], "merge", true, false, null, CancellationToken.None);
        var second = await service.QueueAsync(search.Id, [groups[1].Id], "remove", false, true, null, CancellationToken.None);

        Assert.Equal(1, first.QueuedGroupCount);
        Assert.Equal(1, second.QueuedGroupCount);
        Assert.Equal(1, jobs.EnqueueCount);
        Assert.Equal("duplicate-job", second.JobId);
        Assert.Equal("duplicate-job", (await db.DuplicateSearches.AsNoTracking().SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).DeletionJobId);
        var stored = await db.DuplicateSearchGroups.AsNoTracking().OrderBy(group => group.Position).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(
            [DuplicateGroupStatus.Queued, DuplicateGroupStatus.Queued, DuplicateGroupStatus.Ignored],
            stored.Select(group => group.Status));
        Assert.Equal("merge", stored[0].ResolutionAction);
        Assert.True(stored[0].DeleteFiles);
        Assert.Equal("remove", stored[1].ResolutionAction);
        Assert.True(stored[1].DeleteGenerated);
    }

    [Fact]
    [RequiresPostgres]
    public async Task LostWorkerReturnsItsGroupsToReviewWithAnExplanation()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
        var (search, groups, videos) = await AddSearchAsync(db, [[(true, "a"), (false, "b")], [(true, "c"), (false, "d")]]);
        await ClaimAsync(db, search, "lost-job", groups[0], DuplicateGroupStatus.Processing, videos["a"]);
        await db.DuplicateSearchGroups
            .Where(group => group.Id == groups[1].Id)
            .ExecuteUpdateAsync(update => update.SetProperty(group => group.Status, DuplicateGroupStatus.Queued), cancellationToken: TestContext.Current.CancellationToken);
        var jobs = new CapturingJobService
        {
            ReturnedJob = new JobInfo("lost-job", "duplicate-resolution", "Resolving", JobStatus.Failed, 1, null, DateTime.UtcNow, DateTime.UtcNow, "boom"),
        };
        var service = new DuplicateResolutionService(db, jobs, null!, new CoveConfiguration());

        Assert.True(await service.ReconcileLostWorkerAsync(await db.DuplicateSearches.SingleAsync(cancellationToken: TestContext.Current.CancellationToken), CancellationToken.None));

        db.ChangeTracker.Clear();
        Assert.Null((await db.DuplicateSearches.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).DeletionJobId);
        Assert.All(await db.DuplicateSearchGroups.ToListAsync(cancellationToken: TestContext.Current.CancellationToken), group =>
        {
            Assert.Equal(DuplicateGroupStatus.Failed, group.Status);
            Assert.Equal(DuplicateResolutionService.LostWorkerError, group.Error);
        });
        Assert.False(await db.DuplicateDeletionKeeperReservations.IgnoreQueryFilters().AnyAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    [RequiresPostgres]
    public async Task RunningOrPreEnqueueWorkersAreNotReconciled()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
        var (search, groups, videos) = await AddSearchAsync(db, [[(true, "a"), (false, "b")]]);
        await ClaimAsync(db, search, "running-job", groups[0], DuplicateGroupStatus.Processing, videos["a"]);
        var jobs = new CapturingJobService
        {
            ReturnedJob = new JobInfo("running-job", "duplicate-resolution", "Resolving", JobStatus.Running, 0.5, null, DateTime.UtcNow, null, null),
        };
        var service = new DuplicateResolutionService(db, jobs, null!, new CoveConfiguration());

        Assert.False(await service.ReconcileLostWorkerAsync(await db.DuplicateSearches.SingleAsync(cancellationToken: TestContext.Current.CancellationToken), CancellationToken.None));
        await db.DuplicateSearches.ExecuteUpdateAsync(update => update.SetProperty(item => item.DeletionJobId, DuplicateSearchDeletionClaim.Create()), cancellationToken: TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        Assert.False(await service.ReconcileLostWorkerAsync(await db.DuplicateSearches.SingleAsync(cancellationToken: TestContext.Current.CancellationToken), CancellationToken.None));

        Assert.Equal(DuplicateGroupStatus.Processing, (await db.DuplicateSearchGroups.AsNoTracking().SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).Status);
        Assert.True(await db.DuplicateDeletionKeeperReservations.IgnoreQueryFilters().AnyAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    [RequiresPostgres]
    public async Task CancellingAWorkerBeforeItRunsReturnsQueuedGroupsToReview()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
        var (search, groups, videos) = await AddSearchAsync(db, [[(true, "a"), (false, "b")]]);
        await ClaimAsync(db, search, "queued-job", groups[0], DuplicateGroupStatus.Queued, videos["a"]);
        var service = new DuplicateResolutionService(db, new CapturingJobService(), null!, new CoveConfiguration());

        Assert.Equal(1, await service.ReleaseCancelledWorkerAsync("queued-job", CancellationToken.None));

        db.ChangeTracker.Clear();
        Assert.Null((await db.DuplicateSearches.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).DeletionJobId);
        Assert.Equal(DuplicateGroupStatus.Unresolved, (await db.DuplicateSearchGroups.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).Status);
        Assert.True(await db.Videos.AnyAsync(video => video.Id == videos["b"].Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    [RequiresPostgres]
    public async Task StartupRecoveryInterruptsSearchesAndReturnsInFlightGroupsToReview()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
        var running = CompletedSearch();
        running.Status = DuplicateSearchStatus.Running;
        db.DuplicateSearches.Add(running);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var (search, groups, videos) = await AddSearchAsync(db, [[(true, "a"), (false, "b")], [(true, "c"), (false, "d")]]);
        await ClaimAsync(db, search, "lost-job", groups[0], DuplicateGroupStatus.Processing, videos["a"]);

        // PostgreSQL stores microseconds, so the timestamp must survive the round trip unchanged.
        var recoveredAt = new DateTime(DateTime.UtcNow.Ticks / 10 * 10, DateTimeKind.Utc);
        await DuplicateSearchRecoveryService.RecoverAsync(db, recoveredAt, CancellationToken.None);

        db.ChangeTracker.Clear();
        var interrupted = await db.DuplicateSearches.SingleAsync(item => item.Id == running.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(DuplicateSearchStatus.Interrupted, interrupted.Status);
        Assert.Equal(recoveredAt, interrupted.CompletedAt);
        Assert.Null((await db.DuplicateSearches.SingleAsync(item => item.Id == search.Id, cancellationToken: TestContext.Current.CancellationToken)).DeletionJobId);
        var recovered = await db.DuplicateSearchGroups.OrderBy(group => group.Position).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(DuplicateGroupStatus.Failed, recovered[0].Status);
        Assert.Equal(DuplicateSearchRecoveryService.InterruptedResolutionError, recovered[0].Error);
        Assert.Equal(DuplicateGroupStatus.Unresolved, recovered[1].Status);
        Assert.Empty(await db.DuplicateDeletionKeeperReservations.IgnoreQueryFilters().ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("remoteId")]
    [InlineData("fingerprint")]
    [InlineData("phash")]
    [RequiresPostgres]
    public async Task ExecutionRejectsOversizedMetadataAndRecordsFailure(string matchType)
    {
        var ct = TestContext.Current.CancellationToken;
        var commands = new CandidateCommandCapture();
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database, commands);
        var value = new string('a', DuplicateSearchLimits.MaximumFieldCharacters + 1000);
        var video = new Video { Title = matchType == "title" ? value : "candidate" };
        if (matchType == "remoteId") video.RemoteIds.Add(new VideoRemoteId { Endpoint = "endpoint", RemoteId = value });
        if (matchType is "fingerprint" or "phash")
        {
            var folder = new Folder { Path = "/duplicate-memory" };
            db.Folders.Add(folder);
            await db.SaveChangesAsync(ct);
            video.Files.Add(new VideoFile { ParentFolderId = folder.Id, Basename = "candidate.mp4", Fingerprints = [new FileFingerprint { Type = matchType == "phash" ? "phash" : "md5", Value = value }] });
        }
        var search = new DuplicateSearch { MatchType = matchType, ExpiresAt = DateTime.UtcNow.AddDays(1) };
        db.AddRange(video, search);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        var service = new DuplicateSearchExecutionService(db, new CapturingJobService(), new CoveConfiguration());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(search.Id, null, new SilentProgress(), ct));
        Assert.Contains("character field limit", error.Message);
        Assert.Contains(commands.Commands, sql => sql.Contains("substring(", StringComparison.OrdinalIgnoreCase) && sql.Contains("LIMIT"));
        db.ChangeTracker.Clear();
        var saved = await db.DuplicateSearches.SingleAsync(ct);
        Assert.Equal(DuplicateSearchStatus.Failed, saved.Status);
        Assert.Contains("character field limit", saved.Error);
        Assert.Empty(await db.DuplicateSearchGroups.ToListAsync(ct));
    }

    [Fact]
    [RequiresPostgres]
    public async Task ExplicitCandidateLimitStopsEnqueueBeforeRetainingOversizedSelection()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
        var service = new DuplicateSearchJobService(db, new CapturingJobService(), null!);
        var ids = Enumerable.Range(1, DuplicateSearchLimits.MaximumExplicitVideos + 1).ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(null, null, new DuplicateSearchStartRequest("title", 0, 0), ids, TestContext.Current.CancellationToken));
        Assert.Empty(await db.DuplicateSearches.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal([1], DuplicateSearchLimits.NormalizeIds(Enumerable.Repeat(1, ids.Length)));
    }

    [Fact]
    [RequiresPostgres]
    public async Task ScopedCandidateLoadingStreamsFilesAndPreservesPathDurationAndMetadataOnlyRules()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
        var includedFolder = new Folder { Path = "/library/keep" };
        var excludedFolder = new Folder { Path = "/library/trash" };
        db.Folders.AddRange(includedFolder, excludedFolder);
        await db.SaveChangesAsync(ct);
        var included = new Video
        {
            Title = "included",
            MaxDuration = 60,
            Files =
            [
                new VideoFile { ParentFolderId = includedFolder.Id, Basename = "included.mp4", Duration = 60 },
                new VideoFile { ParentFolderId = excludedFolder.Id, Basename = "also-excluded.mp4", Duration = 60 },
            ],
        };
        var excluded = new Video
        {
            Title = "excluded",
            MaxDuration = 60,
            Files = [new VideoFile { ParentFolderId = excludedFolder.Id, Basename = "excluded.mp4", Duration = 60 }],
        };
        var tooShort = new Video
        {
            Title = "short",
            MaxDuration = 10,
            Files = [new VideoFile { ParentFolderId = includedFolder.Id, Basename = "short.mp4", Duration = 10 }],
        };
        var metadataOnly = new Video { Title = "metadata only", MaxDuration = 60 };
        db.Videos.AddRange(included, excluded, tooShort, metadataOnly);
        await db.SaveChangesAsync(ct);

        var scoped = new DuplicateSearch
        {
            MatchType = "title",
            IncludePaths = ["/library"],
            ExcludePaths = ["/library/trash"],
            MinimumDuration = 30,
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        };
        db.DuplicateSearches.Add(scoped);
        await db.SaveChangesAsync(ct);
        var service = new DuplicateSearchExecutionService(db, new CapturingJobService(), new CoveConfiguration());
        await service.ExecuteAsync(scoped.Id, null, new SilentProgress(), ct);
        db.ChangeTracker.Clear();
        Assert.Equal(1, (await db.DuplicateSearches.SingleAsync(search => search.Id == scoped.Id, ct)).CandidateCount);

        var unscoped = new DuplicateSearch { MatchType = "title", ExpiresAt = DateTime.UtcNow.AddDays(1) };
        db.DuplicateSearches.Add(unscoped);
        await db.SaveChangesAsync(ct);
        await service.ExecuteAsync(unscoped.Id, null, new SilentProgress(), ct);
        db.ChangeTracker.Clear();
        Assert.Equal(4, (await db.DuplicateSearches.SingleAsync(search => search.Id == unscoped.Id, ct)).CandidateCount);
    }

    [Fact]
    [RequiresPostgres]
    public async Task KeeperFactsStreamFilesAndRejectOversizedRetainedPaths()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
        var folder = new Folder { Path = "/library/keeper" };
        db.Folders.Add(folder);
        await db.SaveChangesAsync(ct);
        var files = Enumerable.Range(0, 300)
            .Select(index => new VideoFile
            {
                ParentFolderId = folder.Id,
                Basename = $"candidate-{index:D3}.mp4",
                Width = index + 1,
                Height = 2,
                Size = 10,
                VideoCodec = index == 0 ? "hvc1" : "h264",
            })
            .ToArray();
        var video = new Video { Title = "keeper facts", Files = files };
        db.Videos.Add(video);
        await db.SaveChangesAsync(ct);
        video.PrimaryFileId = files[0].Id;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        var facts = Assert.Single(await DuplicateKeeperRules.LoadFactsAsync(db, [video.Id], [new("resolution"), new("path", ["keeper"])], ct)).Value;
        Assert.Equal(600, facts.Pixels);
        Assert.Equal(3_000, facts.TotalSize);
        Assert.Equal("hevc", facts.Codec);
        Assert.EndsWith("candidate-000.mp4", facts.Path, StringComparison.Ordinal);

        var oversizedFolder = new Folder { Path = "/" + new string('x', DuplicateSearchLimits.MaximumFieldCharacters + 1) };
        db.Folders.Add(oversizedFolder);
        await db.SaveChangesAsync(ct);
        var oversized = new Video
        {
            Title = "oversized keeper path",
            Files = [new VideoFile { ParentFolderId = oversizedFolder.Id, Basename = "candidate.mp4", Width = 1, Height = 1 }],
        };
        db.Videos.Add(oversized);
        await db.SaveChangesAsync(ct);
        oversized.PrimaryFileId = oversized.Files.Single().Id;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DuplicateKeeperRules.LoadFactsAsync(db, [oversized.Id], [new("path", ["keeper"])], ct));
        Assert.Contains("character field limit", error.Message);
    }

    [Fact]
    [RequiresPostgres]
    public async Task VisualSearchGroupsNearDuplicatesWithinTheToleranceAndScope()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        await using var db = CreateContext(database);
        var folder = new Folder { Path = "/visual" };
        var siblingFolder = new Folder { Path = "/visual-other" };
        db.Folders.AddRange(folder, siblingFolder);
        await db.SaveChangesAsync(ct);
        Video Visual(string name, Folder parent, double duration, string phash) => new()
        {
            Title = name,
            MaxDuration = duration,
            Files =
            [
                new VideoFile
                {
                    ParentFolderId = parent.Id,
                    Basename = name + ".mp4",
                    Duration = duration,
                    Fingerprints = [new FileFingerprint { Type = "phash", Value = phash }],
                },
            ],
        };
        var original = Visual("original", folder, 100, "ff00ff00ff00ff00");
        // Two bits away, written with a prefix and in upper case.
        var reencode = Visual("reencode", folder, 102, "0xFF00FF00FF00FF03");
        var outsideTolerance = Visual("longer", folder, 120, "ff00ff00ff00ff00");
        var different = Visual("different", folder, 100, "00ff00ff00ff00ff");
        var unparseable = Visual("unparseable", folder, 100, "not-a-hash");
        // "/visual-other" shares a prefix with "/visual" without being below it.
        var sibling = Visual("sibling", siblingFolder, 100, "ff00ff00ff00ff00");
        db.Videos.AddRange(original, reencode, outsideTolerance, different, unparseable, sibling);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        async Task<(DuplicateSearch Search, int[][] Groups)> SearchAsync(string[] includePaths, string[] excludePaths)
        {
            var search = new DuplicateSearch
            {
                MatchType = "phash",
                Distance = 4,
                DurationDifference = 5,
                IncludePaths = includePaths,
                ExcludePaths = excludePaths,
                ExpiresAt = DateTime.UtcNow.AddDays(1),
            };
            db.DuplicateSearches.Add(search);
            await db.SaveChangesAsync(ct);
            await new DuplicateSearchExecutionService(db, new CapturingJobService(), new CoveConfiguration())
                .ExecuteAsync(search.Id, null, new SilentProgress(), ct);
            db.ChangeTracker.Clear();
            var groups = await db.DuplicateSearchGroups
                .Where(group => group.SearchId == search.Id)
                .OrderBy(group => group.Position)
                .Select(group => group.Items.Select(item => item.VideoId).OrderBy(id => id).ToArray())
                .ToArrayAsync(ct);
            return (await db.DuplicateSearches.AsNoTracking().SingleAsync(item => item.Id == search.Id, ct), groups);
        }

        var wholeLibrary = await SearchAsync([], []);
        Assert.Equal(6, wholeLibrary.Search.CandidateCount);
        Assert.Equal([original.Id, reencode.Id, sibling.Id], Assert.Single(wholeLibrary.Groups));

        var oneFolder = await SearchAsync(["/visual"], []);
        Assert.Equal(5, oneFolder.Search.CandidateCount);
        Assert.Equal([original.Id, reencode.Id], Assert.Single(oneFolder.Groups));

        // A scope may name a single file, and an excluded folder removes its files from an included parent.
        var fileAndFolder = await SearchAsync(["/visual/original.mp4", "/visual-other"], []);
        Assert.Equal(2, fileAndFolder.Search.CandidateCount);
        Assert.Equal([original.Id, sibling.Id], Assert.Single(fileAndFolder.Groups));
        var excluded = await SearchAsync(["/visual", "/visual-other"], ["/visual-other"]);
        Assert.Equal(5, excluded.Search.CandidateCount);
        Assert.Equal([original.Id, reencode.Id], Assert.Single(excluded.Groups));

        db.DuplicateIgnoredPairs.AddRange(
            new DuplicateIgnoredPair { LowVideoId = Math.Min(original.Id, reencode.Id), HighVideoId = Math.Max(original.Id, reencode.Id) },
            new DuplicateIgnoredPair { LowVideoId = Math.Min(original.Id, sibling.Id), HighVideoId = Math.Max(original.Id, sibling.Id) });
        await db.SaveChangesAsync(ct);
        Assert.Equal([reencode.Id, sibling.Id], Assert.Single((await SearchAsync([], [])).Groups));
    }

    [Fact]
    [RequiresPostgres]
    public async Task ScopeMatchesANamedFileWithTheCaseRuleOfThePlatform()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        await using var db = CreateContext(database);
        var folder = new Folder { Path = "/Scope" };
        db.Folders.Add(folder);
        await db.SaveChangesAsync(ct);
        var video = new Video { Title = "scoped", Files = [new VideoFile { ParentFolderId = folder.Id, Basename = "Master.mkv" }] };
        db.Videos.Add(video);
        await db.SaveChangesAsync(ct);

        Task<int> CountAsync(string[] includes, string[] excludes, bool ignoreCase)
            => DuplicateSearchExecutionService.WhereInScope(db.VideoFiles, includes, [], excludes, [], ignoreCase).CountAsync(ct);

        Assert.Equal(1, await CountAsync(["/Scope/Master.mkv"], [], ignoreCase: false));
        Assert.Equal(0, await CountAsync(["/scope/master.MKV"], [], ignoreCase: false));
        Assert.Equal(1, await CountAsync(["/scope/master.MKV"], [], ignoreCase: true));
        Assert.Equal(1, await CountAsync([], ["/scope/master.MKV"], ignoreCase: false));
        Assert.Equal(0, await CountAsync([], ["/scope/master.MKV"], ignoreCase: true));
    }

    [Fact]
    [RequiresPostgres]
    public async Task VisualSearchComparesAcrossUnitsAndSavesGroupsAcrossBatches()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        await using var db = CreateContext(database);
        var folder = new Folder { Path = "/visual-scale" };
        db.Folders.Add(folder);
        await db.SaveChangesAsync(ct);
        var random = new Random(7);
        var videos = new List<Video>();
        void Add(double duration, ulong hash) => videos.Add(new Video
        {
            Title = $"video {videos.Count}",
            Files =
            [
                new VideoFile
                {
                    ParentFolderId = folder.Id,
                    Basename = $"{videos.Count}.mp4",
                    Duration = duration,
                    Fingerprints = [new FileFingerprint { Type = "phash", Value = hash.ToString("x16") }],
                },
            ],
        });
        ulong NextHash() => (ulong)random.NextInt64(long.MinValue, long.MaxValue);
        // Unmatched fingerprints first, so that once sorted by duration the 300 pairs that follow lie across
        // the boundary between the first comparison unit and the second, with one pair split by it.
        const int singles = 4_095;
        const int pairs = 300;
        const int oversized = 60;
        for (var index = 0; index < singles; index++)
            Add(10 * index, NextHash());
        for (var index = 0; index < pairs; index++)
        {
            var hash = NextHash();
            Add(100_000 + 10 * index, hash);
            Add(100_001 + 10 * index, hash);
        }
        var sharedHash = NextHash();
        for (var index = 0; index < oversized; index++)
            Add(200_000 + 0.01 * index, sharedHash);
        db.Videos.AddRange(videos);
        var search = new DuplicateSearch { MatchType = "phash", Distance = 4, DurationDifference = 5, ExpiresAt = DateTime.UtcNow.AddDays(1) };
        db.DuplicateSearches.Add(search);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        var progress = new UnitCountingProgress();

        await new DuplicateSearchExecutionService(db, new CapturingJobService(), new CoveConfiguration { MaxParallelTasks = 4 })
            .ExecuteAsync(search.Id, null, progress, ct);

        Assert.Equal(2, progress.DeclaredUnits);
        db.ChangeTracker.Clear();
        var saved = await db.DuplicateSearches.AsNoTracking().SingleAsync(item => item.Id == search.Id, ct);
        var groups = await db.DuplicateSearchGroups
            .Where(group => group.SearchId == search.Id)
            .OrderBy(group => group.Position)
            .Select(group => new { group.Position, VideoIds = group.Items.Select(item => item.VideoId).OrderBy(id => id).ToArray() })
            .ToArrayAsync(ct);
        // The 60 copies are one logical group, saved as two chunks that share its keeper.
        Assert.Equal(pairs + 2, saved.GroupCount);
        Assert.Equal(2 * pairs + oversized, saved.VideoCount);
        Assert.Equal(Enumerable.Range(0, pairs + 2), groups.Select(group => group.Position));
        var expectedPairs = Enumerable.Range(0, pairs).Select(index => new[] { videos[singles + 2 * index].Id, videos[singles + 2 * index + 1].Id });
        Assert.Equal(expectedPairs, groups.Take(pairs).Select(group => group.VideoIds));
        var chunks = groups.Skip(pairs).ToArray();
        Assert.Equal(videos.Skip(singles + 2 * pairs).Select(video => video.Id).Order(), chunks.SelectMany(chunk => chunk.VideoIds).Distinct().Order());
        Assert.Single(chunks[0].VideoIds.Intersect(chunks[1].VideoIds));
    }

    [Fact]
    [RequiresPostgres]
    public async Task VisualSearchFailsBeforeComparingWhenTheWindowIsTooLargeAndSaysWhatToChange()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        await using var db = CreateContext(database);
        var folder = new Folder { Path = "/visual-limit" };
        db.Folders.Add(folder);
        await db.SaveChangesAsync(ct);
        db.Videos.AddRange(Enumerable.Range(0, 3).Select(index => new Video
        {
            Title = $"video {index}",
            Files =
            [
                new VideoFile
                {
                    ParentFolderId = folder.Id,
                    Basename = $"{index}.mp4",
                    Duration = 60 + index,
                    Fingerprints = [new FileFingerprint { Type = "phash", Value = "ff00ff00ff00ff00" }],
                },
            ],
        }));
        var search = new DuplicateSearch { MatchType = "phash", Distance = 4, DurationDifference = 5, ExpiresAt = DateTime.UtcNow.AddDays(1) };
        db.DuplicateSearches.Add(search);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        var progress = new UnitCountingProgress();
        var service = new DuplicateSearchExecutionService(db, new CapturingJobService(), new CoveConfiguration()) { PHashComparisonLimit = 2 };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(search.Id, null, progress, ct));

        Assert.Contains("would compare 3 pairs of fingerprints, more than the limit of 2", error.Message);
        Assert.Contains("smaller duration tolerance", error.Message);
        Assert.Null(progress.DeclaredUnits);
        db.ChangeTracker.Clear();
        var saved = await db.DuplicateSearches.AsNoTracking().SingleAsync(ct);
        Assert.Equal(DuplicateSearchStatus.Failed, saved.Status);
        Assert.Equal(error.Message, saved.Error);
    }

    [Fact]
    [RequiresPostgres]
    public async Task TitleSearchMatchesIgnoringCaseAndWhitespaceIncludingNonAsciiLetters()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        await using var db = CreateContext(database);
        var first = new Video { Title = "Été  à Paris" };
        var second = new Video { Title = " ÉTÉ à\tparis " };
        var unaccented = new Video { Title = "Ete a Paris" };
        var blank = new Video { Title = "   " };
        var alsoBlank = new Video { Title = "\t" };
        db.Videos.AddRange(first, second, unaccented, blank, alsoBlank);
        var search = new DuplicateSearch { MatchType = "title", ExpiresAt = DateTime.UtcNow.AddDays(1) };
        db.DuplicateSearches.Add(search);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        await new DuplicateSearchExecutionService(db, new CapturingJobService(), new CoveConfiguration())
            .ExecuteAsync(search.Id, null, new SilentProgress(), ct);

        var group = Assert.Single(await db.DuplicateSearchGroups.Where(item => item.SearchId == search.Id).Include(item => item.Items).ToArrayAsync(ct));
        Assert.Equal(new[] { first.Id, second.Id }, group.Items.Select(item => item.VideoId).Order().ToArray());
    }

    [Theory]
    [InlineData("title")]
    [InlineData("remoteId")]
    [InlineData("fingerprint")]
    [RequiresPostgres]
    public async Task ExactMatchesCrossPagesAndCandidateChunksWithoutExpandingScope(string matchType)
    {
        var ct = TestContext.Current.CancellationToken;
        var commands = new CandidateCommandCapture();
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database, commands);
        var folder = new Folder { Path = "/duplicate-pages" };
        db.Folders.Add(folder);
        await db.SaveChangesAsync(ct);
        var videos = Enumerable.Range(0, DuplicateSearchLimits.PageSize + 2).Select(index =>
        {
            var key = index == 0 || index == DuplicateSearchLimits.PageSize + 1 ? "shared" : $"unique-{index}";
            var video = new Video { Title = key };
            if (matchType == "remoteId") video.RemoteIds.Add(new VideoRemoteId { Endpoint = "endpoint", RemoteId = key });
            if (matchType == "fingerprint") video.Files.Add(new VideoFile { ParentFolderId = folder.Id, Basename = $"{index}.mp4", Fingerprints = [new FileFingerprint { Type = "md5", Value = key }] });
            return video;
        }).ToArray();
        db.Videos.AddRange(videos);
        await db.SaveChangesAsync(ct);
        var ids = videos.Select(video => video.Id).ToArray();
        db.ChangeTracker.Clear();
        foreach (var includeLast in new[] { true, false })
        {
            var search = new DuplicateSearch { MatchType = matchType, ExpiresAt = DateTime.UtcNow.AddDays(1) };
            db.DuplicateSearches.Add(search);
            await db.SaveChangesAsync(ct);
            await new DuplicateSearchExecutionService(db, new CapturingJobService(), new CoveConfiguration())
                .ExecuteAsync(search.Id, includeLast ? ids : ids[..^1], new SilentProgress(), ct);
            var groups = await db.DuplicateSearchGroups.Where(group => group.SearchId == search.Id).Include(group => group.Items).ToArrayAsync(ct);
            if (includeLast) Assert.Equal(new[] { ids[0], ids[^1] }, Assert.Single(groups).Items.Select(item => item.VideoId).Order().ToArray());
            else Assert.Empty(groups);
            Assert.Equal(DuplicateSearchStatus.Completed, (await db.DuplicateSearches.AsNoTracking().SingleAsync(item => item.Id == search.Id, ct)).Status);
        }
        Assert.Contains(commands.Commands, sql => sql.Contains("substring(", StringComparison.OrdinalIgnoreCase) && sql.Contains("LIMIT") && sql.Contains(" > @"));
    }

    [Fact]
    [RequiresPostgres]
    public async Task CancellationDuringCandidateLoadingPersistsCancelledStatus()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
        using var cancellation = new CancellationTokenSource();
        db.Videos.Add(new Video { Title = "cancellation candidate" });
        var search = new DuplicateSearch { MatchType = "title", ExpiresAt = DateTime.UtcNow.AddDays(1) };
        db.DuplicateSearches.Add(search);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DuplicateSearchExecutionService(db, new CapturingJobService(), new CoveConfiguration())
            .ExecuteAsync(search.Id, null, new CancelLoadingProgress(cancellation), cancellation.Token));
        Assert.Equal(DuplicateSearchStatus.Cancelled, (await db.DuplicateSearches.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).Status);
    }

    private sealed class CancelLoadingProgress(CancellationTokenSource cancellation) : IJobProgress
    {
        public void Report(double progress, string? subTask = null)
        {
            if (subTask == "Comparing video titles") cancellation.Cancel();
        }
    }

    private sealed class CandidateCommandCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("LIMIT")) Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class UnitCountingProgress : IJobProgress
    {
        public int? DeclaredUnits { get; private set; }
        public void Report(double progress, string? subTask = null) { }
        public void DeclareUnitCount(int totalUnits) => DeclaredUnits = totalUnits;
    }

    private sealed class SilentProgress : IJobProgress
    {
        public void Report(double progress, string? subTask = null) { }
    }

    private static CoveContext CreateContext(CovePostgresDatabase database, DbCommandInterceptor? interceptor = null)
        => database.CreateContext(builder =>
        {
            builder.ReplaceService<IExecutionStrategyFactory, TestRetryingExecutionStrategyFactory>();
            if (interceptor is not null) builder.AddInterceptors(interceptor);
        });

    private static DuplicateSearch CompletedSearch() => new()
    {
        Status = DuplicateSearchStatus.Completed,
        MatchType = "phash",
        ExpiresAt = DateTime.UtcNow.AddDays(7),
    };

    /// <summary>Adds a completed search whose groups reference videos by label; a label reused across groups is one video.</summary>
    internal static async Task<(DuplicateSearch Search, DuplicateSearchGroup[] Groups, Dictionary<string, Video> Videos)> AddSearchAsync(
        CoveContext db,
        (bool Keep, string Label)[][] groups,
        string? ownerKey = null)
    {
        var videos = groups.SelectMany(group => group).Select(member => member.Label).Distinct()
            .ToDictionary(label => label, label => new Video { Title = $"Video {label}" });
        db.Videos.AddRange(videos.Values);
        await db.SaveChangesAsync();
        var search = CompletedSearch();
        search.OwnerKey = ownerKey;
        var entities = groups.Select((members, position) => new DuplicateSearchGroup
        {
            Position = position,
            DecisionSource = "auto",
            Items = members.Select(member => new DuplicateSearchItem { VideoId = videos[member.Label].Id, Keep = member.Keep }).ToList(),
        }).ToArray();
        foreach (var entity in entities)
            search.Groups.Add(entity);
        db.DuplicateSearches.Add(search);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (search, entities, videos);
    }

    private static async Task ClaimAsync(
        CoveContext db,
        DuplicateSearch search,
        string jobId,
        DuplicateSearchGroup group,
        DuplicateGroupStatus status,
        Video keeper)
    {
        await db.DuplicateSearches
            .Where(item => item.Id == search.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.DeletionJobId, jobId));
        await db.DuplicateSearchGroups
            .Where(item => item.Id == group.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, status));
        db.DuplicateDeletionKeeperReservations.Add(new DuplicateDeletionKeeperReservation { SearchId = search.Id, VideoId = keeper.Id });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static Dictionary<int, DuplicateKeeperFacts> Facts(params DuplicateKeeperFacts[] facts)
        => facts.ToDictionary(fact => fact.VideoId);

    private static DuplicateKeeperFacts Fact(
        int id,
        long pixels = 0,
        long bitRate = 0,
        long size = 0,
        string codec = "",
        string path = "")
        => new(id, pixels, bitRate, 0, 0, size, codec, path, 0, 0, false, DateTime.UnixEpoch);

    internal sealed class CapturingJobService : IJobService
    {
        public string? Type { get; private set; }
        public string? ResultUrl { get; private set; }
        public int EnqueueCount { get; private set; }
        public JobInfo? ReturnedJob { get; init; }
        public Func<IJobProgress, CancellationToken, Task>? Work { get; private set; }

        public string EnqueueOwned(JobOwner owner, string type, string description, Func<IJobProgress, CancellationToken, Task> work, string? resultUrl = null, bool exclusive = true)
            => Capture(type, resultUrl, work);

        public string EnqueueWithResult(string type, string description, Func<IJobProgress, CancellationToken, Task> work, string resultUrl, bool exclusive = true)
            => Capture(type, resultUrl, work);

        public string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
            => Capture(type, null, work);

        private string Capture(string type, string? resultUrl, Func<IJobProgress, CancellationToken, Task> work)
        {
            Type = type;
            ResultUrl = resultUrl;
            Work = work;
            EnqueueCount++;
            return "duplicate-job";
        }

        public bool Cancel(string jobId) => false;
        public bool ReorderQueued(string jobId, string? beforeJobId) => false;
        public JobInfo? GetJob(string jobId) => ReturnedJob?.Id == jobId ? ReturnedJob : null;
        public IReadOnlyList<JobInfo> GetAllJobs() => [];
        public IReadOnlyList<JobInfo> GetJobHistory() => [];
    }
}
