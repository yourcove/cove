using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Tests.Postgres;
using IVideoFileMaintenanceService = Cove.Plugins.IVideoFileMaintenanceService;
using VideoFileOperationResult = Cove.Plugins.VideoFileOperationResult;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Tests;

/// <summary>The "files" duplicate search, which reviews the files attached to a single video.</summary>
[RequiresPostgres]
public sealed class DuplicateFileReviewTests
{
    [Fact]
    public async Task FilesSearchGroupsTheFilesOfEachVideoAndKeepsTheHighestResolution()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        await using var db = database.CreateContext();

        var folder = new Folder { Path = "/library" };
        var upgraded = new Video
        {
            Title = "upgraded",
            Files =
            [
                new VideoFile { ParentFolder = folder, Basename = "upgraded-1080p.mp4", Width = 1920, Height = 1080, Duration = 600 },
                new VideoFile { ParentFolder = folder, Basename = "upgraded-2160p.mp4", Width = 3840, Height = 2160, Duration = 600 },
            ],
        };
        var single = new Video { Title = "single", Files = [new VideoFile { ParentFolder = folder, Basename = "single.mp4", Width = 1920, Height = 1080 }] };
        var keptTogether = new Video
        {
            Title = "kept together",
            Files =
            [
                new VideoFile { ParentFolder = folder, Basename = "vr.mp4", Width = 3840, Height = 1920 },
                new VideoFile { ParentFolder = folder, Basename = "flat.mp4", Width = 1920, Height = 1080 },
            ],
        };
        db.Videos.AddRange(upgraded, single, keptTogether);
        await db.SaveChangesAsync(ct);
        var togetherIds = keptTogether.Files.Select(file => file.Id).Order().ToArray();
        db.DuplicateIgnoredFilePairs.Add(new DuplicateIgnoredFilePair { LowFileId = togetherIds[0], HighFileId = togetherIds[1] });
        var search = new DuplicateSearch { MatchType = DuplicateSearchJobService.FilesMatchType, ExpiresAt = DateTime.UtcNow.AddDays(1) };
        db.DuplicateSearches.Add(search);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        var service = new DuplicateSearchExecutionService(db, new DuplicateSearchJobTests.CapturingJobService(), new CoveConfiguration());
        await service.ExecuteAsync(search.Id, null, new SilentProgress(), ct);
        db.ChangeTracker.Clear();

        var saved = await db.DuplicateSearches.SingleAsync(ct);
        Assert.Equal(DuplicateSearchStatus.Completed, saved.Status);
        Assert.Equal(1, saved.GroupCount);
        Assert.Equal(2, saved.VideoCount);
        var group = await db.DuplicateSearchGroups.Include(item => item.FileItems).Include(item => item.Items).SingleAsync(ct);
        Assert.Empty(group.Items);
        Assert.Equal("resolution", group.DecisionRule);
        Assert.All(group.FileItems, item => Assert.Equal(upgraded.Id, item.VideoId));
        var fourK = upgraded.Files.Single(file => file.Height == 2160).Id;
        Assert.Equal([fourK], group.FileItems.Where(item => item.Keep).Select(item => item.FileId));
        Assert.Equal(2, group.FileItems.Count);
    }

    [Fact]
    public async Task FileGroupsAreListedAndDecidedByFileWhileMergeIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        var principalAccessor = CreatePrincipalAccessor();
        await using var db = new CoveContext(database.CreateOptions(), principalAccessor);
        var (search, group, video, primary, other) = await SeedFileGroupAsync(db, primaryIsKept: true, ownerKey: "user:1");

        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var jobs = new DuplicateSearchJobTests.CapturingJobService();
        var controller = CreateController(db, principalAccessor, memoryCache, jobs);

        var page = Assert.IsType<DuplicateGroupPage>(Assert.IsType<OkObjectResult>(
            (await controller.GetDuplicateSearchGroups(search.Id, ct: ct)).Result).Value);
        var view = Assert.Single(page.Items);
        Assert.Equal("unresolved", view.Status);
        Assert.Equal([video.Id], view.Videos.Select(item => item.Id));
        Assert.Empty(view.KeepVideoIds);
        Assert.Equal(new[] { primary.Id, other.Id }.Order(), view.FileIds);
        Assert.Equal([primary.Id], view.KeepFileIds);
        Assert.Equal(other.Size, view.ReclaimableBytes);

        var summary = Assert.IsType<DuplicateSearchSummary>(Assert.IsType<OkObjectResult>(
            (await controller.GetDuplicateSearch(search.Id, ct)).Result).Value);
        Assert.Equal(1, summary.Counts.Unresolved);
        Assert.Equal(1, summary.RemovableVideoCount);
        Assert.Equal(other.Size, summary.ReclaimableBytes);

        Assert.IsType<NoContentResult>(await controller.UpdateDuplicateSearchGroupDecision(
            search.Id, group.Id, new DuplicateKeeperDecisionRequest([], [other.Id]), ct));
        db.ChangeTracker.Clear();
        var decisions = await db.DuplicateSearchFileItems.Where(item => item.GroupId == group.Id).ToDictionaryAsync(item => item.FileId, item => item.Keep, ct);
        Assert.False(decisions[primary.Id]);
        Assert.True(decisions[other.Id]);

        Assert.IsType<BadRequestObjectResult>(await controller.UpdateDuplicateSearchGroupDecision(
            search.Id, group.Id, new DuplicateKeeperDecisionRequest([video.Id]), ct));

        var merge = await controller.ResolveDuplicateGroups(search.Id, new DuplicateResolveRequest(null, "merge"), ct);
        Assert.IsType<BadRequestObjectResult>(merge.Result);
        Assert.Equal(0, jobs.EnqueueCount);

        var remove = await controller.ResolveDuplicateGroups(search.Id, new DuplicateResolveRequest(null, "remove"), ct);
        var accepted = Assert.IsType<DuplicateResolveResult>(Assert.IsType<AcceptedResult>(remove.Result).Value);
        Assert.Equal(1, accepted.QueuedGroupCount);
    }

    [Fact]
    public async Task KeepingBothFilesIsRememberedForLaterFilesSearchesAndCanBeUndone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        var principalAccessor = CreatePrincipalAccessor();
        await using var db = new CoveContext(database.CreateOptions(), principalAccessor);
        var (search, group, _, primary, other) = await SeedFileGroupAsync(db, primaryIsKept: true, ownerKey: "user:1");
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateController(db, principalAccessor, memoryCache, new DuplicateSearchJobTests.CapturingJobService());

        Assert.IsType<NoContentResult>(await controller.IgnoreDuplicateGroup(search.Id, group.Id, ct));
        db.ChangeTracker.Clear();
        var pair = Assert.Single(await db.DuplicateIgnoredFilePairs.ToListAsync(ct));
        Assert.Equal((Math.Min(primary.Id, other.Id), Math.Max(primary.Id, other.Id)), (pair.LowFileId, pair.HighFileId));
        Assert.Empty(await db.DuplicateIgnoredPairs.ToListAsync(ct));

        Assert.IsType<NoContentResult>(await controller.RestoreIgnoredDuplicateGroup(search.Id, group.Id, ct));
        db.ChangeTracker.Clear();
        Assert.Empty(await db.DuplicateIgnoredFilePairs.ToListAsync(ct));
    }

    [Fact]
    public async Task ResolvingAFileGroupRemovesTheUnkeptFileAndNeverTheVideo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WorkerHarness.CreateAsync();
        var (search, group, video, primary, other) = await harness.SeedAsync(primaryIsKept: true);

        await harness.QueueAndRunAsync(search.Id, group.Id);

        await using var db = harness.CreateContext();
        var resolved = await db.DuplicateSearchGroups.SingleAsync(item => item.Id == group.Id, ct);
        Assert.Equal(DuplicateGroupStatus.Resolved, resolved.Status);
        Assert.Equal(1, resolved.RemovedVideoCount);
        Assert.Equal(other.Size, resolved.RemovedBytes);
        Assert.True(await db.Videos.AnyAsync(item => item.Id == video.Id, ct));
        Assert.Equal([primary.Id], await db.VideoFiles.Where(file => file.VideoId == video.Id).Select(file => file.Id).ToListAsync(ct));
        Assert.Empty(harness.Maintenance.PrimaryChanges);
        Assert.Equal([(other.Id, false)], harness.Maintenance.Deletions);
        Assert.Empty(await db.DuplicateDeletionKeeperReservations.IgnoreQueryFilters().ToListAsync(ct));
    }

    [Fact]
    public async Task KeepingANonPrimaryFileMakesItPrimaryBeforeTheOldPrimaryIsRemoved()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WorkerHarness.CreateAsync();
        var (search, group, video, primary, other) = await harness.SeedAsync(primaryIsKept: false);

        await harness.QueueAndRunAsync(search.Id, group.Id);

        await using var db = harness.CreateContext();
        Assert.Equal(DuplicateGroupStatus.Resolved, (await db.DuplicateSearchGroups.SingleAsync(item => item.Id == group.Id, ct)).Status);
        Assert.Equal([(video.Id, other.Id)], harness.Maintenance.PrimaryChanges);
        Assert.Equal([(primary.Id, false)], harness.Maintenance.Deletions);
        var stored = await db.Videos.SingleAsync(item => item.Id == video.Id, ct);
        Assert.Equal(other.Id, stored.PrimaryFileId);
        Assert.Equal([other.Id], await db.VideoFiles.Where(file => file.VideoId == video.Id).Select(file => file.Id).ToListAsync(ct));
    }

    [Fact]
    public async Task AKeptFileThatIsNotTheSameFootageFailsTheGroupWithoutRemovingAnything()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await WorkerHarness.CreateAsync();
        harness.Maintenance.RefusePrimaryChange = "The replacement file is not the same footage as the current primary file.";
        var (search, group, video, primary, other) = await harness.SeedAsync(primaryIsKept: false);

        await harness.QueueAndRunAsync(search.Id, group.Id);

        await using var db = harness.CreateContext();
        var failed = await db.DuplicateSearchGroups.SingleAsync(item => item.Id == group.Id, ct);
        Assert.Equal(DuplicateGroupStatus.Failed, failed.Status);
        Assert.Contains("not the same footage", failed.Error);
        Assert.Contains("Nothing was removed", failed.Error);
        Assert.Empty(harness.Maintenance.Deletions);
        Assert.Equal(
            new[] { primary.Id, other.Id }.Order(),
            await db.VideoFiles.Where(file => file.VideoId == video.Id).Select(file => file.Id).OrderBy(id => id).ToListAsync(ct));
        Assert.Equal(primary.Id, (await db.Videos.SingleAsync(item => item.Id == video.Id, ct)).PrimaryFileId);
    }

    private static async Task<(DuplicateSearch Search, DuplicateSearchGroup Group, Video Video, VideoFile Primary, VideoFile Other)> SeedFileGroupAsync(
        CoveContext db,
        bool primaryIsKept,
        string? ownerKey = null)
    {
        var folder = new Folder { Path = "/library/upgrades" };
        var primary = new VideoFile { ParentFolder = folder, Basename = "clip-1080p.mp4", Width = 1920, Height = 1080, Duration = 600, Size = 1_000 };
        var other = new VideoFile { ParentFolder = folder, Basename = "clip-2160p.mp4", Width = 3840, Height = 2160, Duration = 600, Size = 4_000 };
        var video = new Video { Title = "Upgraded clip", Files = [primary, other] };
        db.Videos.Add(video);
        await db.SaveChangesAsync();
        video.PrimaryFileId = primary.Id;
        await db.SaveChangesAsync();

        var group = new DuplicateSearchGroup
        {
            Position = 0,
            DecisionSource = "auto",
            FileItems =
            [
                new DuplicateSearchFileItem { FileId = primary.Id, VideoId = video.Id, Keep = primaryIsKept },
                new DuplicateSearchFileItem { FileId = other.Id, VideoId = video.Id, Keep = !primaryIsKept },
            ],
        };
        var search = new DuplicateSearch
        {
            OwnerKey = ownerKey,
            MatchType = DuplicateSearchJobService.FilesMatchType,
            Status = DuplicateSearchStatus.Completed,
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            Groups = [group],
        };
        db.DuplicateSearches.Add(search);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (search, group, video, primary, other);
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

    private sealed class WorkerHarness : IAsyncDisposable
    {
        private readonly CovePostgresDatabase _database;
        private readonly ServiceProvider _provider;
        private readonly DbContextOptions<CoveContext> _options;

        private WorkerHarness(CovePostgresDatabase database, ServiceProvider provider, DbContextOptions<CoveContext> options, RecordingFileMaintenance maintenance)
        {
            _database = database;
            _provider = provider;
            _options = options;
            Maintenance = maintenance;
        }

        public RecordingFileMaintenance Maintenance { get; }

        public static async Task<WorkerHarness> CreateAsync()
        {
            var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
            var options = database.CreateOptions();
            var maintenance = new RecordingFileMaintenance(options);
            var services = new ServiceCollection();
            services.AddScoped<ICurrentPrincipalAccessor, CurrentPrincipalAccessor>();
            services.AddScoped(provider => new CoveContext(options, provider.GetRequiredService<ICurrentPrincipalAccessor>()));
            services.AddSingleton<IVideoFileMaintenanceService>(maintenance);
            return new WorkerHarness(database, services.BuildServiceProvider(), options, maintenance);
        }

        public CoveContext CreateContext() => new(_options);

        public async Task<(DuplicateSearch Search, DuplicateSearchGroup Group, Video Video, VideoFile Primary, VideoFile Other)> SeedAsync(bool primaryIsKept)
        {
            await using var db = CreateContext();
            return await SeedFileGroupAsync(db, primaryIsKept);
        }

        public async Task QueueAndRunAsync(Guid searchId, int groupId)
        {
            var jobs = new DuplicateSearchJobTests.CapturingJobService();
            await using var db = CreateContext();
            var service = new DuplicateResolutionService(db, jobs, _provider.GetRequiredService<IServiceScopeFactory>(), new CoveConfiguration { MaxParallelTasks = 1 });
            var queued = await service.QueueAsync(searchId, [groupId], DuplicateResolutionService.RemoveAction, deleteFiles: false, deleteGenerated: false, principal: null, CancellationToken.None);
            Assert.Equal(1, queued.QueuedGroupCount);
            await jobs.Work!(new SilentProgress(), CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await _database.DisposeAsync();
        }
    }

    /// <summary>Applies primary changes and file removals to the database and records them, like the real service minus hashing and disk work.</summary>
    private sealed class RecordingFileMaintenance(DbContextOptions<CoveContext> options) : IVideoFileMaintenanceService
    {
        public string? RefusePrimaryChange { get; set; }
        public List<(int VideoId, int FileId)> PrimaryChanges { get; } = [];
        public List<(int FileId, bool DeleteFromDisk)> Deletions { get; } = [];

        public async Task<VideoFileOperationResult> MakePrimaryWhenSameContentAsync(int videoId, int fileId, CancellationToken ct)
        {
            if (RefusePrimaryChange is not null)
                return new(false, RefusePrimaryChange);
            await using var db = new CoveContext(options);
            var video = await db.Videos.SingleAsync(item => item.Id == videoId, ct);
            video.PrimaryFileId = fileId;
            await db.SaveChangesAsync(ct);
            PrimaryChanges.Add((videoId, fileId));
            return new(true, null);
        }

        public async Task<VideoFileOperationResult> DeleteFileAsync(int fileId, bool deleteFromDisk, CancellationToken ct)
        {
            await using var db = new CoveContext(options);
            if (await db.Videos.AnyAsync(item => item.PrimaryFileId == fileId, ct))
                return new(false, "This file is the primary file of a video, so it was kept.");
            db.VideoFiles.Remove(await db.VideoFiles.SingleAsync(file => file.Id == fileId, ct));
            await db.SaveChangesAsync(ct);
            Deletions.Add((fileId, deleteFromDisk));
            return new(true, null);
        }
    }

    private sealed class SilentProgress : IJobProgress
    {
        public void Report(double progress, string? subTask = null) { }
    }

    private sealed class AllowAllAuthorizationService : IAuthorizationService
    {
        public AuthorizationResult Authorize(CovePrincipal? principal, string permission, EntityRef? entity = null)
            => AuthorizationResult.Allow();

        public Task<AuthorizationResult> AuthorizeAsync(CovePrincipal? principal, string permission, EntityRef? entity, CancellationToken ct)
            => Task.FromResult(AuthorizationResult.Allow());

        public void Require(CovePrincipal? principal, string permission, EntityRef? entity = null)
        {
        }

        public bool Has(CovePrincipal? principal, string permission) => true;
    }
}
