using System.Reflection;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Tests;

[RequiresPostgres]
public sealed class DuplicateResolutionWorkerTests
{
    [Fact]
    public async Task MergeResolutionCarriesMetadataToTheKeeperAndRemovesTheDuplicate()
    {
        await using var harness = await Harness.CreateAsync();
        var (search, group, keeper, duplicate) = await harness.SeedAsync();

        var queued = await harness.QueueAsync(search.Id, group.Id, DuplicateResolutionService.MergeAction);
        Assert.Equal(1, queued.QueuedGroupCount);
        await harness.RunWorkerAsync();

        await using var db = harness.CreateContext();
        Assert.False(await db.Videos.AnyAsync(video => video.Id == duplicate.Id, cancellationToken: TestContext.Current.CancellationToken));
        var kept = await db.Videos
            .Include(video => video.VideoTags)
            .Include(video => video.Urls)
            .SingleAsync(video => video.Id == keeper.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Keeper title", kept.Title);
        Assert.Equal("Details only the duplicate had", kept.Details);
        Assert.Equal([harness.KeeperTagId, harness.DuplicateTagId], kept.VideoTags.Select(link => link.TagId).Order());
        Assert.Contains(kept.Urls, url => url.Url == "https://example.test/duplicate");
        var rating = Assert.Single(await db.Ratings.Where(item => item.HostType == RatingHostType.Video).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(keeper.Id, rating.HostId);
        var affinity = Assert.Single(await db.UserEntityAffinities.Where(item => item.HostType == AffinityHostType.Video).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(keeper.Id, affinity.HostId);
        Assert.Equal(5, affinity.ViewCount);
        Assert.True(affinity.IsFavorite);

        var resolved = await db.DuplicateSearchGroups.SingleAsync(item => item.Id == group.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(DuplicateGroupStatus.Resolved, resolved.Status);
        Assert.Equal(1, resolved.RemovedVideoCount);
        Assert.NotNull(resolved.ResolvedAt);
        Assert.Null((await db.DuplicateSearches.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).DeletionJobId);
        Assert.Empty(await db.DuplicateDeletionKeeperReservations.IgnoreQueryFilters().ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MergeResolutionHonoursTheReviewsFieldChoices()
    {
        await using var harness = await Harness.CreateAsync();
        var (search, group, keeper, duplicate) = await harness.SeedAsync();

        await harness.QueueAsync(search.Id, group.Id, DuplicateResolutionService.MergeAction, new VideoMergeMetadataDto(
            Fields: new() { ["title"] = "source", ["details"] = "target" },
            TagIds: [harness.DuplicateTagId]));
        await harness.RunWorkerAsync();

        await using var db = harness.CreateContext();
        Assert.False(await db.Videos.AnyAsync(video => video.Id == duplicate.Id, cancellationToken: TestContext.Current.CancellationToken));
        var kept = await db.Videos.Include(video => video.VideoTags).SingleAsync(video => video.Id == keeper.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Duplicate title", kept.Title);
        Assert.Null(kept.Details);
        Assert.Equal([harness.DuplicateTagId], kept.VideoTags.Select(link => link.TagId));
        Assert.Equal(DuplicateGroupStatus.Resolved, (await db.DuplicateSearchGroups.SingleAsync(item => item.Id == group.Id, cancellationToken: TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task UnreadableStoredChoicesFailTheGroupInsteadOfMergingByDefault()
    {
        await using var harness = await Harness.CreateAsync();
        var (search, group, keeper, duplicate) = await harness.SeedAsync();
        await harness.QueueAsync(search.Id, group.Id, DuplicateResolutionService.MergeAction, new VideoMergeMetadataDto(TagIds: []));
        await using (var db = harness.CreateContext())
            await db.DuplicateSearchGroups.Where(item => item.Id == group.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.MergeMetadataJson, "{not json"), cancellationToken: TestContext.Current.CancellationToken);

        await harness.RunWorkerAsync();

        await using var verify = harness.CreateContext();
        Assert.True(await verify.Videos.AnyAsync(video => video.Id == duplicate.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null((await verify.Videos.SingleAsync(video => video.Id == keeper.Id, cancellationToken: TestContext.Current.CancellationToken)).Details);
        var failed = await verify.DuplicateSearchGroups.SingleAsync(item => item.Id == group.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(DuplicateGroupStatus.Failed, failed.Status);
        Assert.Contains("choices", failed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AttachResolutionMovesTheDuplicatesFilesOntoTheKeeperAndReclaimsNothing()
    {
        await using var harness = await Harness.CreateAsync();
        var (search, group, keeper, duplicate) = await harness.SeedAsync();
        await using (var seed = harness.CreateContext())
        {
            await seed.VideoFiles
                .Where(file => file.VideoId == duplicate.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(file => file.Size, 4_096L), TestContext.Current.CancellationToken);
            seed.Segments.Add(new Segment { HostType = SegmentHostType.Video, HostId = duplicate.Id, SourceKey = "user", Title = "Duplicate marker", StartSec = 3 });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var queued = await harness.QueueAsync(search.Id, group.Id, DuplicateResolutionService.MergeAction, attachFiles: true);
        Assert.Equal(1, queued.QueuedGroupCount);
        await harness.RunWorkerAsync();

        await using var db = harness.CreateContext();
        Assert.False(await db.Videos.AnyAsync(video => video.Id == duplicate.Id, cancellationToken: TestContext.Current.CancellationToken));
        var kept = await db.Videos
            .Include(video => video.Files)
            .Include(video => video.VideoTags)
            .SingleAsync(video => video.Id == keeper.Id, cancellationToken: TestContext.Current.CancellationToken);
        // Both files now belong to the kept video, whose own file stays primary.
        Assert.Equal(["duplicate.mp4", "keeper.mp4"], kept.Files.Select(file => file.Basename).Order());
        Assert.Equal(keeper.PrimaryFileId, kept.PrimaryFileId);
        Assert.Equal("Details only the duplicate had", kept.Details);
        Assert.Equal([harness.KeeperTagId, harness.DuplicateTagId], kept.VideoTags.Select(link => link.TagId).Order());
        // The files came along, so the copy's marker did too; a removing merge would need equivalent files.
        var marker = Assert.Single(await db.Segments.Where(segment => segment.HostType == SegmentHostType.Video).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(keeper.Id, marker.HostId);

        var resolved = await db.DuplicateSearchGroups.SingleAsync(item => item.Id == group.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(DuplicateGroupStatus.Resolved, resolved.Status);
        Assert.True(resolved.AttachFiles);
        Assert.Equal(1, resolved.RemovedVideoCount);
        Assert.Equal(0, resolved.RemovedBytes);
        Assert.Null(resolved.Error);
        Assert.Null((await db.DuplicateSearches.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).DeletionJobId);
        Assert.Empty(await db.DuplicateDeletionKeeperReservations.IgnoreQueryFilters().ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AttachResolutionFailsTheGroupWhenTheReviewerMayNotDeleteACopy()
    {
        // An attaching merge deletes the copies' records itself, so the worker must make the check the
        // deletion pipeline makes for a removal.
        await using var harness = await Harness.CreateAsync(new DenyVideoDeletion());
        var (search, group, keeper, duplicate) = await harness.SeedAsync();
        var reviewer = new CovePrincipal
        {
            UserId = 1,
            Username = "duplicate-reviewer",
            Kind = PrincipalKind.User,
            Permissions = new HashSet<string> { "*" },
            Roles = new HashSet<string>(),
        };

        await harness.QueueAsync(search.Id, group.Id, DuplicateResolutionService.MergeAction, attachFiles: true, principal: reviewer);
        await harness.RunWorkerAsync();

        await using var db = harness.CreateContext();
        var failed = await db.DuplicateSearchGroups.SingleAsync(item => item.Id == group.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(DuplicateGroupStatus.Failed, failed.Status);
        Assert.Contains("permission", failed.Error, StringComparison.OrdinalIgnoreCase);
        // Nothing moved: the copy still exists with its own file and its own metadata.
        var copy = await db.Videos.Include(video => video.Files).SingleAsync(video => video.Id == duplicate.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["duplicate.mp4"], copy.Files.Select(file => file.Basename));
        Assert.Null((await db.Videos.SingleAsync(video => video.Id == keeper.Id, cancellationToken: TestContext.Current.CancellationToken)).Details);
    }

    [Fact]
    public async Task AttachIsIgnoredForAPlainRemoval()
    {
        await using var harness = await Harness.CreateAsync();
        var (search, group, keeper, duplicate) = await harness.SeedAsync();

        await harness.QueueAsync(search.Id, group.Id, DuplicateResolutionService.RemoveAction, attachFiles: true);
        await harness.RunWorkerAsync();

        await using var db = harness.CreateContext();
        Assert.False(await db.Videos.AnyAsync(video => video.Id == duplicate.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(["keeper.mp4"], await db.VideoFiles.IgnoreQueryFilters().Select(file => file.Basename).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.False((await db.DuplicateSearchGroups.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).AttachFiles);
    }

    [Fact]
    public async Task RemoveResolutionDiscardsTheDuplicatesMetadata()
    {
        await using var harness = await Harness.CreateAsync();
        var (search, group, keeper, duplicate) = await harness.SeedAsync();
        Assert.NotNull(duplicate.PrimaryFileId);

        await harness.QueueAsync(search.Id, group.Id, DuplicateResolutionService.RemoveAction);
        await harness.RunWorkerAsync();

        await using var db = harness.CreateContext();
        Assert.False(await db.Videos.AnyAsync(video => video.Id == duplicate.Id, cancellationToken: TestContext.Current.CancellationToken));
        var kept = await db.Videos.Include(video => video.VideoTags).SingleAsync(video => video.Id == keeper.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(kept.Details);
        Assert.Equal([harness.KeeperTagId], kept.VideoTags.Select(link => link.TagId));
        Assert.Equal(keeper.PrimaryFileId, kept.PrimaryFileId);
        Assert.Equal(["keeper.mp4"], await db.VideoFiles.IgnoreQueryFilters().Select(file => file.Basename).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(DuplicateGroupStatus.Resolved, (await db.DuplicateSearchGroups.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task AGroupWhoseKeeperDisappearedFailsWithoutRemovingAnything()
    {
        await using var harness = await Harness.CreateAsync();
        var (search, group, keeper, duplicate) = await harness.SeedAsync();
        await harness.QueueAsync(search.Id, group.Id, DuplicateResolutionService.RemoveAction);
        await using (var db = harness.CreateContext())
        {
            // The keeper is removed elsewhere after the group was queued; its item cascades away with it.
            db.Videos.Remove(await db.Videos.SingleAsync(video => video.Id == keeper.Id, cancellationToken: TestContext.Current.CancellationToken));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await harness.RunWorkerAsync();

        await using var verify = harness.CreateContext();
        Assert.True(await verify.Videos.AnyAsync(video => video.Id == duplicate.Id, cancellationToken: TestContext.Current.CancellationToken));
        var failed = await verify.DuplicateSearchGroups.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(DuplicateGroupStatus.Failed, failed.Status);
        Assert.Contains("keep", failed.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null((await verify.DuplicateSearches.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).DeletionJobId);
    }

    [Fact]
    public async Task GroupsQueuedWhileTheWorkerRunsAreResolvedByTheSameWorker()
    {
        await using var harness = await Harness.CreateAsync();
        var (search, group, _, duplicate) = await harness.SeedAsync();
        var second = await harness.AddGroupAsync(search.Id);
        await harness.QueueAsync(search.Id, group.Id, DuplicateResolutionService.RemoveAction);

        // A second request while the claim is held queues without starting another worker.
        var followUp = await harness.QueueAsync(search.Id, second.Group.Id, DuplicateResolutionService.RemoveAction);
        Assert.Equal(1, harness.Jobs.EnqueueCount);
        Assert.Equal(1, followUp.QueuedGroupCount);
        await harness.RunWorkerAsync();

        await using var db = harness.CreateContext();
        Assert.All(await db.DuplicateSearchGroups.ToListAsync(cancellationToken: TestContext.Current.CancellationToken), item => Assert.Equal(DuplicateGroupStatus.Resolved, item.Status));
        Assert.False(await db.Videos.AnyAsync(video => video.Id == duplicate.Id || video.Id == second.Duplicate.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null((await db.DuplicateSearches.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).DeletionJobId);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CovePostgresDatabase _database;
        private readonly ServiceProvider _provider;
        private readonly DbContextOptions<CoveContext> _options;

        private Harness(CovePostgresDatabase database, ServiceProvider provider, DbContextOptions<CoveContext> options)
        {
            _database = database;
            _provider = provider;
            _options = options;
        }

        public DuplicateSearchJobTests.CapturingJobService Jobs { get; } = new();
        public int KeeperTagId { get; private set; }
        public int DuplicateTagId { get; private set; }

        public static async Task<Harness> CreateAsync(IAuthorizationService? authorization = null)
        {
            var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
            var options = database.CreateOptions();
            var services = new ServiceCollection();
            services.AddScoped<ICurrentPrincipalAccessor, CurrentPrincipalAccessor>();
            services.AddScoped(provider => new CoveContext(options, provider.GetRequiredService<ICurrentPrincipalAccessor>()));
            services.AddScoped<CustomFieldService>();
            services.AddSingleton<IEventBus, EventBus>();
            services.AddSingleton(NoOp<IThumbnailService>.Create());
            services.AddSingleton(NoOp<IBlobService>.Create());
            services.AddScoped(provider => new ImageDeletionService(
                provider.GetRequiredService<CoveContext>(),
                provider.GetRequiredService<CustomFieldService>(),
                provider.GetRequiredService<IThumbnailService>()));
            services.AddScoped(provider => new BulkEntityDeletionService(
                provider.GetRequiredService<CoveContext>(),
                provider.GetRequiredService<CustomFieldService>(),
                provider.GetRequiredService<ImageDeletionService>(),
                provider.GetRequiredService<IThumbnailService>(),
                provider.GetRequiredService<IBlobService>(),
                provider.GetRequiredService<IEventBus>()));
            services.AddSingleton(NoOp<IStreamService>.Create());
            services.AddScoped(provider => new VideoMergeService(
                provider.GetRequiredService<CoveContext>(),
                provider.GetRequiredService<CustomFieldService>(),
                provider.GetRequiredService<IBlobService>(),
                provider.GetRequiredService<IStreamService>(),
                provider.GetRequiredService<IEventBus>()));
            if (authorization is not null)
                services.AddSingleton(authorization);
            var provider = services.BuildServiceProvider();
            return new Harness(database, provider, options);
        }

        public CoveContext CreateContext() => new(_options);

        public async Task<(DuplicateSearch Search, DuplicateSearchGroup Group, Video Keeper, Video Duplicate)> SeedAsync()
        {
            await using var db = CreateContext();
            var keeperTag = new Tag { Name = "Keeper tag" };
            var duplicateTag = new Tag { Name = "Duplicate tag" };
            // Real library videos own a file that is also their primary file; that mutual reference is what
            // makes removing a video and its files in one save order-sensitive.
            var folder = new Folder { Path = "/library/first" };
            var keeper = new Video { Title = "Keeper title", Files = [new VideoFile { ParentFolder = folder, Basename = "keeper.mp4" }] };
            var duplicate = new Video
            {
                Title = "Duplicate title",
                Details = "Details only the duplicate had",
                Urls = [new VideoUrl { Url = "https://example.test/duplicate" }],
                Files = [new VideoFile { ParentFolder = folder, Basename = "duplicate.mp4" }],
            };
            db.AddRange(keeperTag, duplicateTag, keeper, duplicate);
            if (!await db.Users.AnyAsync(user => user.Id == 1))
                db.Users.Add(new Cove.Core.Entities.Auth.User { Id = 1, Username = "duplicate-reviewer", PasswordHash = "test" });
            await db.SaveChangesAsync();
            await _database.SyncIdentitySequencesAsync();
            db.AddRange(
                new VideoTag { VideoId = keeper.Id, TagId = keeperTag.Id },
                new VideoTag { VideoId = duplicate.Id, TagId = duplicateTag.Id },
                new Rating { UserId = 1, HostType = RatingHostType.Video, HostId = duplicate.Id, Value = 80 },
                new UserEntityAffinity { UserId = 1, HostType = AffinityHostType.Video, HostId = duplicate.Id, ViewCount = 3 },
                new UserEntityAffinity { UserId = 1, HostType = AffinityHostType.Video, HostId = keeper.Id, ViewCount = 2, IsFavorite = true });
            await db.SaveChangesAsync();
            KeeperTagId = keeperTag.Id;
            DuplicateTagId = duplicateTag.Id;

            var group = new DuplicateSearchGroup
            {
                Position = 0,
                Items =
                [
                    new DuplicateSearchItem { VideoId = keeper.Id, Keep = true },
                    new DuplicateSearchItem { VideoId = duplicate.Id, Keep = false },
                ],
            };
            var search = new DuplicateSearch { Status = DuplicateSearchStatus.Completed, MatchType = "title", Groups = [group] };
            db.DuplicateSearches.Add(search);
            await db.SaveChangesAsync();
            return (search, group, keeper, duplicate);
        }

        public async Task<(DuplicateSearchGroup Group, Video Duplicate)> AddGroupAsync(Guid searchId)
        {
            await using var db = CreateContext();
            var folder = new Folder { Path = "/library/second" };
            var keeper = new Video { Title = "Second keeper", Files = [new VideoFile { ParentFolder = folder, Basename = "second-keeper.mp4" }] };
            var duplicate = new Video { Title = "Second duplicate", Files = [new VideoFile { ParentFolder = folder, Basename = "second-duplicate.mp4" }] };
            db.AddRange(keeper, duplicate);
            await db.SaveChangesAsync();
            var group = new DuplicateSearchGroup
            {
                SearchId = searchId,
                Position = 1,
                Items =
                [
                    new DuplicateSearchItem { VideoId = keeper.Id, Keep = true },
                    new DuplicateSearchItem { VideoId = duplicate.Id, Keep = false },
                ],
            };
            db.DuplicateSearchGroups.Add(group);
            await db.SaveChangesAsync();
            return (group, duplicate);
        }

        public async Task<DuplicateResolveResult> QueueAsync(Guid searchId, int groupId, string action, VideoMergeMetadataDto? metadata = null, bool attachFiles = false, CovePrincipal? principal = null)
        {
            await using var db = CreateContext();
            var service = new DuplicateResolutionService(
                db,
                Jobs,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                new CoveConfiguration { MaxParallelTasks = 1 });
            return await service.QueueAsync(searchId, [groupId], action, deleteFiles: false, deleteGenerated: false, principal, CancellationToken.None, metadata, attachFiles);
        }

        public Task RunWorkerAsync() => Jobs.Work!(new NullProgress(), CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await _database.DisposeAsync();
        }
    }

    /// <summary>Allows everything except deleting a particular video.</summary>
    private sealed class DenyVideoDeletion : IAuthorizationService
    {
        public AuthorizationResult Authorize(CovePrincipal? principal, string permission, EntityRef? entity = null)
            => permission == Permissions.VideosDelete && entity is not null
                ? AuthorizationResult.Deny("Deleting this video is not allowed.")
                : AuthorizationResult.Allow();

        public Task<AuthorizationResult> AuthorizeAsync(CovePrincipal? principal, string permission, EntityRef? entity, CancellationToken ct)
            => Task.FromResult(Authorize(principal, permission, entity));

        public void Require(CovePrincipal? principal, string permission, EntityRef? entity = null)
        {
        }

        public bool Has(CovePrincipal? principal, string permission) => true;
    }

    private sealed class NullProgress : IJobProgress
    {
        public void Report(double progress, string? subTask = null)
        {
        }
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
