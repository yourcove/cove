using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.DTOs;
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
    public async Task DeleteUnkeptDuplicateVideos_UsesTheConfiguredExecutionStrategyForItsClaimTransaction()
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
        var (search, _, keeper, _) = await SeedCompletedSearchAsync(db);

        var jobs = new CapturingJobService();
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateController(db, principalAccessor, memoryCache, jobs);
        commitAmbiguity.Arm();

        var result = await controller.DeleteUnkeptDuplicateVideos(
            search.Id,
            new DuplicateSearchDeleteRequestDto(DeleteGenerated: true),
            new AllowAllAuthorizationService(),
            CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var queued = Assert.IsType<BulkDeletionJobStart>(accepted.Value);
        Assert.Equal("captured-duplicate-deletion", queued.JobId);
        Assert.Equal(1, queued.ItemCount);
        Assert.Equal("captured-duplicate-deletion", await db.DuplicateSearches
            .Where(item => item.Id == search.Id)
            .Select(item => item.DeletionJobId)
            .SingleAsync());
        Assert.True(await db.DuplicateDeletionKeeperReservations
            .AnyAsync(item => item.SearchId == search.Id && item.VideoId == keeper.Id));
        Assert.Equal(1, commitAmbiguity.FailuresRaised);
    }

    [Fact]
    public async Task UpdateDuplicateSearchGroupDecision_UsesTheConfiguredExecutionStrategyForItsTransaction()
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
        var controller = CreateController(db, principalAccessor, memoryCache, new CapturingJobService());
        commitAmbiguity.Arm();

        var result = await controller.UpdateDuplicateSearchGroupDecision(
            search.Id,
            group.Id,
            new DuplicateSearchGroupDecisionDto([unwanted.Id]),
            CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        var decisions = await db.DuplicateSearchItems
            .Where(item => item.Group != null && item.Group.SearchId == search.Id)
            .ToDictionaryAsync(item => item.VideoId, item => item.Keep);
        Assert.False(decisions[keeper.Id]);
        Assert.True(decisions[unwanted.Id]);
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
        var controller = CreateController(db, principalAccessor, memoryCache, new CapturingJobService());

        var result = await controller.UpdateDuplicateSearchGroupDecision(
            search.Id,
            group.Id,
            new DuplicateSearchGroupDecisionDto([unwanted.Id]),
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
    public async Task KeeperDecisionReturnsSuccessWhenDeletionClaimsItsCommittedChoiceBeforeRetry()
    {
        var databaseName = $"keeper-claim-retry-{Guid.NewGuid():N}";
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
            await using var claimDb = new CoveContext(ordinaryOptions, principalAccessor);
            await claimDb.DuplicateSearches
                .Where(item => item.Id == search.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.DeletionJobId, "queued-deletion"));
        });
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateController(db, principalAccessor, memoryCache, new CapturingJobService());

        var result = await controller.UpdateDuplicateSearchGroupDecision(
            search.Id,
            group.Id,
            new DuplicateSearchGroupDecisionDto([unwanted.Id]),
            CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        db.ChangeTracker.Clear();
        var decisions = await db.DuplicateSearchItems
            .Where(item => item.GroupId == group.Id)
            .ToDictionaryAsync(item => item.VideoId, item => item.Keep);
        Assert.False(decisions[keeper.Id]);
        Assert.True(decisions[unwanted.Id]);
        Assert.Equal("queued-deletion", await db.DuplicateSearches
            .Where(item => item.Id == search.Id)
            .Select(item => item.DeletionJobId)
            .SingleAsync());
    }

    [Fact]
    public async Task ExhaustedClaimRetriesReleaseThePreEnqueueReservation()
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
        var (search, _, _, _) = await SeedCompletedSearchAsync(db);
        var jobs = new CapturingJobService();
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateController(db, principalAccessor, memoryCache, jobs);
        commitAmbiguity.Arm(failureCount: 4);

        await Assert.ThrowsAnyAsync<Exception>(() => controller.DeleteUnkeptDuplicateVideos(
            search.Id,
            new DuplicateSearchDeleteRequestDto(DeleteGenerated: true),
            new AllowAllAuthorizationService(),
            CancellationToken.None));

        db.ChangeTracker.Clear();
        Assert.Null(await db.DuplicateSearches
            .Where(item => item.Id == search.Id)
            .Select(item => item.DeletionJobId)
            .SingleAsync());
        Assert.False(await db.DuplicateDeletionKeeperReservations.AnyAsync(item => item.SearchId == search.Id));
        Assert.Equal(0, jobs.EnqueueCount);
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
            bulkDeletionJobService: new BulkDeletionJobService(
                jobs,
                null!,
                new CoveConfiguration { MaxParallelTasks = 1 }),
            duplicateSearchJobService: new DuplicateSearchJobService(db, jobs, null!));

    private static async Task<(
        DuplicateSearch Search,
        DuplicateSearchGroup Group,
        Video Keeper,
        Video Unwanted)> SeedCompletedSearchAsync(CoveContext db)
    {
        var keeper = new Video { Title = "Keeper" };
        var unwanted = new Video { Title = "Unwanted" };
        var group = new DuplicateSearchGroup
        {
            Position = 0,
            Items =
            [
                new DuplicateSearchItem { Video = keeper, Keep = true },
                new DuplicateSearchItem { Video = unwanted, Keep = false },
            ],
        };
        var search = new DuplicateSearch
        {
            OwnerKey = "user:1",
            Status = DuplicateSearchStatus.Completed,
            Groups = [group],
        };
        db.DuplicateSearches.Add(search);
        await db.SaveChangesAsync();
        return (search, group, keeper, unwanted);
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

    private sealed class CapturingJobService : IJobService
    {
        public int EnqueueCount { get; private set; }

        public string EnqueueOwned(
            JobOwner owner,
            string type,
            string description,
            Func<IJobProgress, CancellationToken, Task> work,
            string? resultUrl = null,
            bool exclusive = true)
        {
            EnqueueCount++;
            return "captured-duplicate-deletion";
        }

        public string Enqueue(
            string type,
            string description,
            Func<IJobProgress, CancellationToken, Task> work,
            bool exclusive = true)
        {
            EnqueueCount++;
            return "captured-global-duplicate-deletion";
        }

        public bool Cancel(string jobId) => false;
        public bool ReorderQueued(string jobId, string? beforeJobId) => false;
        public Cove.Core.Interfaces.JobInfo? GetJob(string jobId) => null;
        public IReadOnlyList<Cove.Core.Interfaces.JobInfo> GetAllJobs() => [];
        public IReadOnlyList<Cove.Core.Interfaces.JobInfo> GetJobHistory() => [];
    }
}
