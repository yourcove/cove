using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;

namespace Cove.Tests;

public sealed class DuplicateSearchControllerTests
{
    [Fact]
    public async Task UpdateDuplicateSearchGroupDecision_UsesTheConfiguredExecutionStrategyAndMarksTheChoiceManual()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var commitAmbiguity = new CommitAmbiguityInterceptor();
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .ReplaceService<IExecutionStrategyFactory, TestRetryingExecutionStrategyFactory>()
            .AddInterceptors(commitAmbiguity)
            .Options;
        var principalAccessor = CreatePrincipalAccessor();
        await using var db = new CoveContext(options, principalAccessor);
        await db.Database.EnsureCreatedAsync();
        var (search, group, keeper, unwanted) = await SeedCompletedSearchAsync(db);

        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateController(db, principalAccessor, memoryCache, new DuplicateSearchJobTests.CapturingJobService());
        commitAmbiguity.Arm();

        var result = await controller.UpdateDuplicateSearchGroupDecision(
            search.Id,
            group.Id,
            new DuplicateKeeperDecisionRequest([unwanted.Id]),
            CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        db.ChangeTracker.Clear();
        var decisions = await db.DuplicateSearchItems
            .Where(item => item.GroupId == group.Id)
            .ToDictionaryAsync(item => item.VideoId, item => item.Keep);
        Assert.False(decisions[keeper.Id]);
        Assert.True(decisions[unwanted.Id]);
        var stored = await db.DuplicateSearchGroups.SingleAsync();
        Assert.Equal("manual", stored.DecisionSource);
        Assert.Null(stored.DecisionRule);
        Assert.Equal(1, commitAmbiguity.FailuresRaised);
    }

    [Fact]
    public async Task KeeperDecisionRetryDoesNotOverwriteANewerChoice()
    {
        var databaseName = $"keeper-retry-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        var commitAmbiguity = new CommitAmbiguityInterceptor();
        var retryingOptions = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connectionString)
            .ReplaceService<IExecutionStrategyFactory, TestRetryingExecutionStrategyFactory>()
            .AddInterceptors(commitAmbiguity)
            .Options;
        var ordinaryOptions = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connectionString)
            .Options;
        var principalAccessor = CreatePrincipalAccessor();
        await using var db = new CoveContext(retryingOptions, principalAccessor);
        await db.Database.EnsureCreatedAsync();
        var (search, group, keeper, unwanted) = await SeedCompletedSearchAsync(db);

        commitAmbiguity.Arm(async () =>
        {
            await using var newerDb = new CoveContext(ordinaryOptions, principalAccessor);
            var newerGroup = await newerDb.DuplicateSearchGroups
                .Include(item => item.Items)
                .SingleAsync(item => item.Id == group.Id);
            foreach (var item in newerGroup.Items)
                item.Keep = item.VideoId == keeper.Id;
            newerGroup.LastDecisionOperationId = Guid.NewGuid();
            await newerDb.SaveChangesAsync();
        });
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateController(db, principalAccessor, memoryCache, new DuplicateSearchJobTests.CapturingJobService());

        var result = await controller.UpdateDuplicateSearchGroupDecision(
            search.Id,
            group.Id,
            new DuplicateKeeperDecisionRequest([unwanted.Id]),
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        db.ChangeTracker.Clear();
        var decisions = await db.DuplicateSearchItems
            .Where(item => item.GroupId == group.Id)
            .ToDictionaryAsync(item => item.VideoId, item => item.Keep);
        Assert.True(decisions[keeper.Id]);
        Assert.False(decisions[unwanted.Id]);
    }

    [Fact]
    public async Task KeeperDecisionReturnsSuccessWhenTheGroupIsQueuedAfterItsCommittedChoice()
    {
        var databaseName = $"keeper-queue-retry-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        var commitAmbiguity = new CommitAmbiguityInterceptor();
        var retryingOptions = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connectionString)
            .ReplaceService<IExecutionStrategyFactory, TestRetryingExecutionStrategyFactory>()
            .AddInterceptors(commitAmbiguity)
            .Options;
        var ordinaryOptions = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connectionString)
            .Options;
        var principalAccessor = CreatePrincipalAccessor();
        await using var db = new CoveContext(retryingOptions, principalAccessor);
        await db.Database.EnsureCreatedAsync();
        var (search, group, keeper, unwanted) = await SeedCompletedSearchAsync(db);
        commitAmbiguity.Arm(async () =>
        {
            await using var queueDb = new CoveContext(ordinaryOptions, principalAccessor);
            await queueDb.DuplicateSearchGroups
                .Where(item => item.Id == group.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, DuplicateGroupStatus.Queued));
        });
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateController(db, principalAccessor, memoryCache, new DuplicateSearchJobTests.CapturingJobService());

        var result = await controller.UpdateDuplicateSearchGroupDecision(
            search.Id,
            group.Id,
            new DuplicateKeeperDecisionRequest([unwanted.Id]),
            CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        db.ChangeTracker.Clear();
        var decisions = await db.DuplicateSearchItems
            .Where(item => item.GroupId == group.Id)
            .ToDictionaryAsync(item => item.VideoId, item => item.Keep);
        Assert.False(decisions[keeper.Id]);
        Assert.True(decisions[unwanted.Id]);
    }

    [Fact]
    public async Task KeeperDecisionIsRejectedOnceTheGroupIsQueued()
    {
        var principalAccessor = CreatePrincipalAccessor();
        var (connection, db) = await CreateDatabaseAsync(principalAccessor);
        await using var _ = connection;
        await using var __ = db;
        var (search, group, _, unwanted) = await SeedCompletedSearchAsync(db);
        await db.DuplicateSearchGroups.ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, DuplicateGroupStatus.Queued));
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateController(db, principalAccessor, memoryCache, new DuplicateSearchJobTests.CapturingJobService());

        var result = await controller.UpdateDuplicateSearchGroupDecision(search.Id, group.Id, new DuplicateKeeperDecisionRequest([unwanted.Id]), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task ResolveQueuesOnlyGroupsWithSomethingToRemoveAndStartsOneWorker()
    {
        var principalAccessor = CreatePrincipalAccessor();
        var (connection, db) = await CreateDatabaseAsync(principalAccessor);
        await using var _ = connection;
        await using var __ = db;
        var (search, groups, _) = await DuplicateSearchJobTests.AddSearchAsync(
            db,
            [[(true, "a"), (false, "b")], [(true, "c"), (true, "d")]],
            ownerKey: "user:1");
        var jobs = new DuplicateSearchJobTests.CapturingJobService();
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateController(db, principalAccessor, memoryCache, jobs);

        var result = await controller.ResolveDuplicateGroups(
            search.Id,
            new DuplicateResolveRequest(null, "merge", DeleteFiles: false, DeleteGenerated: true),
            CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        var queued = Assert.IsType<DuplicateResolveResult>(accepted.Value);
        Assert.Equal(1, queued.QueuedGroupCount);
        Assert.Equal(1, jobs.EnqueueCount);
        Assert.Equal(DuplicateResolutionService.WorkerJobType, jobs.Type);
        db.ChangeTracker.Clear();
        var stored = await db.DuplicateSearchGroups.OrderBy(group => group.Position).ToListAsync();
        Assert.Equal(DuplicateGroupStatus.Queued, stored[0].Status);
        Assert.Equal("merge", stored[0].ResolutionAction);
        Assert.Equal(DuplicateGroupStatus.Unresolved, stored[1].Status);
        Assert.Equal(groups[0].Id, stored[0].Id);
    }

    [Fact]
    public async Task ResolveRejectsUnknownActionsAndFileDeletionWithoutPermission()
    {
        var (connection, db) = await CreateDatabaseAsync(new CurrentPrincipalAccessor());
        await using var __ = connection;
        await using var ___ = db;
        var (search, _, _) = await DuplicateSearchJobTests.AddSearchAsync(db, [[(true, "a"), (false, "b")]], ownerKey: "user:1");
        var limited = new CurrentPrincipalAccessor();
        limited.Set(new CovePrincipal
        {
            UserId = 1,
            Username = "limited",
            Kind = PrincipalKind.User,
            Permissions = new HashSet<string> { Permissions.VideosRead, Permissions.VideosDelete },
            Roles = new HashSet<string>(),
        });
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateController(db, limited, memoryCache, new DuplicateSearchJobTests.CapturingJobService());

        Assert.IsType<BadRequestObjectResult>((await controller.ResolveDuplicateGroups(search.Id, new DuplicateResolveRequest(null, "shred"), CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>((await controller.ResolveDuplicateGroups(search.Id, new DuplicateResolveRequest(null, DeleteFiles: true), CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>((await controller.ResolveDuplicateGroups(search.Id, new DuplicateResolveRequest(null, "merge"), CancellationToken.None)).Result);
        Assert.Equal(DuplicateGroupStatus.Unresolved, (await db.DuplicateSearchGroups.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task IgnoringAGroupRecordsEveryPairAndRestoringRemovesThem()
    {
        var principalAccessor = CreatePrincipalAccessor();
        var (connection, db) = await CreateDatabaseAsync(principalAccessor);
        await using var _ = connection;
        await using var __ = db;
        var (search, groups, videos) = await DuplicateSearchJobTests.AddSearchAsync(
            db,
            [[(true, "a"), (false, "b"), (false, "c")]],
            ownerKey: "user:1");
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateController(db, principalAccessor, memoryCache, new DuplicateSearchJobTests.CapturingJobService());

        Assert.IsType<NoContentResult>(await controller.IgnoreDuplicateGroup(search.Id, groups[0].Id, CancellationToken.None));
        Assert.IsType<NoContentResult>(await controller.IgnoreDuplicateGroup(search.Id, groups[0].Id, CancellationToken.None));

        db.ChangeTracker.Clear();
        Assert.Equal(DuplicateGroupStatus.Ignored, (await db.DuplicateSearchGroups.SingleAsync()).Status);
        var ids = new[] { videos["a"].Id, videos["b"].Id, videos["c"].Id }.Order().ToArray();
        Assert.Equal(
            [(ids[0], ids[1]), (ids[0], ids[2]), (ids[1], ids[2])],
            (await db.DuplicateIgnoredPairs.OrderBy(pair => pair.LowVideoId).ThenBy(pair => pair.HighVideoId).ToListAsync())
                .Select(pair => (pair.LowVideoId, pair.HighVideoId)));

        Assert.IsType<NoContentResult>(await controller.RestoreIgnoredDuplicateGroup(search.Id, groups[0].Id, CancellationToken.None));
        db.ChangeTracker.Clear();
        Assert.Equal(DuplicateGroupStatus.Unresolved, (await db.DuplicateSearchGroups.SingleAsync()).Status);
        Assert.Empty(await db.DuplicateIgnoredPairs.ToListAsync());
    }

    [Fact]
    public async Task RestoringAnOverlappingGroupPreservesEarlierIgnoredPairs()
    {
        var principalAccessor = CreatePrincipalAccessor();
        var (connection, db) = await CreateDatabaseAsync(principalAccessor);
        await using var _ = connection;
        await using var __ = db;
        var (firstSearch, firstGroups, videos) = await DuplicateSearchJobTests.AddSearchAsync(
            db,
            [[(true, "a"), (false, "b")]],
            ownerKey: "user:1");
        var third = new Video { Title = "Video c" };
        db.Videos.Add(third);
        var secondSearch = new DuplicateSearch
        {
            OwnerKey = "user:1",
            Status = DuplicateSearchStatus.Completed,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            Groups =
            [
                new DuplicateSearchGroup
                {
                    Position = 0,
                    Items =
                    [
                        new DuplicateSearchItem { VideoId = videos["a"].Id, Keep = true },
                        new DuplicateSearchItem { VideoId = videos["b"].Id },
                        new DuplicateSearchItem { Video = third },
                    ],
                },
            ],
        };
        db.DuplicateSearches.Add(secondSearch);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateController(db, principalAccessor, memoryCache, new DuplicateSearchJobTests.CapturingJobService());

        Assert.IsType<NoContentResult>(await controller.IgnoreDuplicateGroup(firstSearch.Id, firstGroups[0].Id, CancellationToken.None));
        var originalPrincipal = principalAccessor.Current;
        principalAccessor.Set(new CovePrincipal
        {
            UserId = 1,
            Username = "duplicate-search-owner",
            Kind = PrincipalKind.User,
            Permissions = new HashSet<string> { Permissions.VideosWrite },
            Roles = new HashSet<string>(),
        });
        try
        {
            Assert.IsType<NoContentResult>(await controller.IgnoreDuplicateGroup(secondSearch.Id, secondSearch.Groups.Single().Id, CancellationToken.None));
            Assert.Equal(2, (await db.DuplicateIgnoredPairs.IgnoreQueryFilters().SingleAsync(pair =>
                pair.LowVideoId == videos["a"].Id && pair.HighVideoId == videos["b"].Id)).DecisionCount);
            Assert.IsType<NoContentResult>(await controller.RestoreIgnoredDuplicateGroup(secondSearch.Id, secondSearch.Groups.Single().Id, CancellationToken.None));
            Assert.IsType<NoContentResult>(await controller.RestoreIgnoredDuplicateGroup(secondSearch.Id, secondSearch.Groups.Single().Id, CancellationToken.None));
        }
        finally
        {
            principalAccessor.Set(originalPrincipal);
        }
        db.ChangeTracker.Clear();

        var remaining = await db.DuplicateIgnoredPairs.SingleAsync();
        Assert.Equal((videos["a"].Id, videos["b"].Id, 1), (remaining.LowVideoId, remaining.HighVideoId, remaining.DecisionCount));
        Assert.Equal(DuplicateGroupStatus.Ignored, (await db.DuplicateSearchGroups.SingleAsync(group => group.Id == firstGroups[0].Id)).Status);
    }

    [Fact]
    public async Task AutoSelectAppliesRulesButKeepsManualChoicesUnlessAskedToOverwrite()
    {
        var principalAccessor = CreatePrincipalAccessor();
        var (connection, db) = await CreateDatabaseAsync(principalAccessor);
        await using var _ = connection;
        await using var __ = db;
        var (search, groups, videos) = await DuplicateSearchJobTests.AddSearchAsync(
            db,
            [[(true, "a"), (false, "b")], [(true, "c"), (false, "d")]],
            ownerKey: "user:1");
        var folder = new Folder { Path = "/library" };
        db.Folders.Add(folder);
        await db.SaveChangesAsync();
        db.VideoFiles.AddRange(
            VideoFile(folder, videos["a"], 1280, 720),
            VideoFile(folder, videos["b"], 1920, 1080),
            VideoFile(folder, videos["c"], 1280, 720),
            VideoFile(folder, videos["d"], 1920, 1080));
        await db.SaveChangesAsync();
        await db.DuplicateSearchGroups
            .Where(group => group.Id == groups[1].Id)
            .ExecuteUpdateAsync(update => update.SetProperty(group => group.DecisionSource, "manual"));
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateController(db, principalAccessor, memoryCache, new DuplicateSearchJobTests.CapturingJobService());

        var result = await controller.AutoSelectDuplicateKeepers(search.Id, new DuplicateAutoSelectRequest([new("resolution")]), CancellationToken.None);

        var counts = Assert.IsType<DuplicateAutoSelectResult>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, counts.UpdatedGroupCount);
        Assert.Equal(1, counts.ChangedGroupCount);
        db.ChangeTracker.Clear();
        var keepers = await db.DuplicateSearchItems.Where(item => item.Keep).Select(item => item.VideoId).ToListAsync();
        Assert.Contains(videos["b"].Id, keepers);
        Assert.Contains(videos["c"].Id, keepers);
        Assert.Equal("resolution", (await db.DuplicateSearchGroups.SingleAsync(group => group.Id == groups[0].Id)).DecisionRule);

        await controller.AutoSelectDuplicateKeepers(search.Id, new DuplicateAutoSelectRequest([new("resolution")], OverwriteManual: true), CancellationToken.None);
        db.ChangeTracker.Clear();
        Assert.True(await db.DuplicateSearchItems.AnyAsync(item => item.VideoId == videos["d"].Id && item.Keep));
    }

    private static VideoFile VideoFile(Folder folder, Video video, int width, int height) => new()
    {
        ParentFolderId = folder.Id,
        VideoId = video.Id,
        Basename = $"{video.Title}.mp4",
        Path = $"/library/{video.Title}.mp4",
        Width = width,
        Height = height,
        Duration = 60,
        Size = width * height,
    };

    /// <summary>
    /// The principal accessor is async-local, so callers must set the principal in the test method itself;
    /// a value set inside this helper would not flow back to the caller.
    /// </summary>
    private static async Task<(SqliteConnection Connection, CoveContext Db)> CreateDatabaseAsync(CurrentPrincipalAccessor principalAccessor)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .ReplaceService<IExecutionStrategyFactory, TestRetryingExecutionStrategyFactory>()
            .Options;
        var db = new CoveContext(options, principalAccessor);
        await db.Database.EnsureCreatedAsync();
        return (connection, db);
    }

    private static VideosController CreateController(
        CoveContext db,
        CurrentPrincipalAccessor principalAccessor,
        MemoryCache memoryCache,
        IJobService jobs)
        => new(
            new VideoRepository(db),
            db,
            null!,
            null!,
            null!,
            memoryCache,
            null!,
            null!,
            new NoOpUserEngagementService(),
            new CustomFieldService(db),
            new EventBus(),
            principalAccessor: principalAccessor,
            duplicateSearchJobService: new DuplicateSearchJobService(db, jobs, null!),
            authorizationService: new AllowAllAuthorizationService(),
            duplicateResolutionService: new DuplicateResolutionService(db, jobs, null!, new CoveConfiguration { MaxParallelTasks = 1 }));

    private static async Task<(
        DuplicateSearch Search,
        DuplicateSearchGroup Group,
        Video Keeper,
        Video Unwanted)> SeedCompletedSearchAsync(CoveContext db)
    {
        var (search, groups, videos) = await DuplicateSearchJobTests.AddSearchAsync(db, [[(true, "keeper"), (false, "unwanted")]], ownerKey: "user:1");
        return (search, groups[0], videos["keeper"], videos["unwanted"]);
    }

    private static CurrentPrincipalAccessor CreatePrincipalAccessor()
    {
        var accessor = new CurrentPrincipalAccessor();
        accessor.Set(new CovePrincipal
        {
            UserId = 1,
            Username = "duplicate-search-owner",
            Kind = PrincipalKind.User,
            Permissions = new HashSet<string> { "*" },
            Roles = new HashSet<string>(),
        });
        return accessor;
    }

    private sealed class AllowAllAuthorizationService : IAuthorizationService
    {
        public AuthorizationResult Authorize(CovePrincipal? principal, string permission, EntityRef? entity = null)
            => AuthorizationResult.Allow();

        public Task<AuthorizationResult> AuthorizeAsync(
            CovePrincipal? principal,
            string permission,
            EntityRef? entity,
            CancellationToken ct)
            => Task.FromResult(AuthorizationResult.Allow());

        public void Require(CovePrincipal? principal, string permission, EntityRef? entity = null)
        {
        }

        public bool Has(CovePrincipal? principal, string permission) => true;
    }
}
