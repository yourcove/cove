using System.Collections;
using System.Data.Common;
using System.Text.RegularExpressions;
using Cove.Api.Services;
using Cove.Core.Entities.Auth;
using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

[RequiresPostgres]
public sealed class CleanServiceMemoryTests
{
    [Theory]
    [InlineData("dry-run")]
    [InlineData("cancel")]
    [InlineData("delete")]
    public async Task InspectsBoundedPagesAndDefersDeletionUntilInspectionCompletes(string outcome)
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var guard = new InspectionGuard();
        var options = database.CreateOptions(builder => builder.AddInterceptors(guard));
        var services = new ServiceCollection();
        services.AddScoped(_ => new CoveContext(options));
        AddDeletionServices(services);
        await using var provider = services.BuildServiceProvider();
        await using (var db = new CoveContext(options))
        {
            db.Videos.AddRange(Enumerable.Range(0, 601).Select(index => new Video { Title = $"orphan {index}" }));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        guard.Enabled = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (outcome == "cancel") guard.AfterInspect = cancellation.Cancel;
        var jobs = new CapturingJobs(guard, cancellation.Token);
        var service = new CleanService(jobs, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CleanService>.Instance);
        service.StartClean(dryRun: outcome == "dry-run");
        if (outcome == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => jobs.Completion);
        else
        {
            await jobs.Completion;
            Assert.True(guard.Inspected >= 601);
        }
        guard.Enabled = false;
        await using var verify = new CoveContext(options);
        Assert.Equal(outcome == "delete" ? 0 : 601, await verify.Videos.CountAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CleanPreservesClipsAndScopeWhileRemovingMissingArchiveContentAcrossBatches(bool scoped)
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), $"cove-clean-mixed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var guard = new InspectionGuard { RequiredInspectionCount = 1203 };
            var options = database.CreateOptions(builder => builder.AddInterceptors(guard));
            var services = new ServiceCollection();
            services.AddScoped(_ => new CoveContext(options));
            AddDeletionServices(services);
            await using var provider = services.BuildServiceProvider();
            var selected = Path.Combine(root, "selected");
            int clipId;
            await using (var db = new CoveContext(options))
            {
                var physical = new Folder { Path = selected };
                var virtualFolder = new Folder { Path = "/virtual/archive-content" };
                var outside = new Folder { Path = root };
                db.Folders.AddRange(physical, virtualFolder, outside);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                var archive = new GalleryFile { ParentFolder = physical, Basename = "missing.zip" };
                db.Galleries.Add(new Gallery { Title = "archive", Files = [archive] });
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                db.Images.AddRange(Enumerable.Range(0, 601).Select(index => new Image
                {
                    Title = $"virtual image {index}",
                    Files = [new ImageFile { ParentFolder = virtualFolder, Basename = $"{index}.jpg", ZipFileId = archive.Id }],
                }));
                db.Images.AddRange(Enumerable.Range(0, 600).Select(index => new Image
                {
                    Title = $"outside image {index}", Files = [new ImageFile { ParentFolder = outside, Basename = $"{index}.jpg" }],
                }));
                await File.WriteAllBytesAsync(Path.Combine(root, "live.mp4"), [1], TestContext.Current.CancellationToken);
                var parent = new Video { Title = "live parent", Files = [new VideoFile { ParentFolder = outside, Basename = "live.mp4" }] };
                var clip = new Video { Title = "clip", ParentVideo = parent };
                db.Videos.AddRange(parent, clip);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                clipId = clip.Id;
                await db.Images.ExecuteUpdateAsync(setters => setters.SetProperty(image => image.FileCount, 99), TestContext.Current.CancellationToken);
            }
            guard.Enabled = true;
            var jobs = new CapturingJobs(guard, TestContext.Current.CancellationToken);
            new CleanService(jobs, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CleanService>.Instance)
                .StartClean(paths: scoped ? [selected] : null);
            await jobs.Completion;
            guard.Enabled = false;
            await using var verify = new CoveContext(options);
            Assert.Equal(scoped ? 600 : 0, await verify.Images.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(scoped ? 600 : 0, await verify.Images.CountAsync(image => image.FileCount == 1, TestContext.Current.CancellationToken));
            Assert.Equal(scoped ? 600 : 0, await verify.ImageFiles.CountAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await verify.Galleries.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await verify.GalleryFiles.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal(2, await verify.Videos.CountAsync(TestContext.Current.CancellationToken));
            Assert.True(await verify.Videos.AnyAsync(video => video.Id == clipId, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CleanRemovesOrphansThroughTheUserDeletionPath()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), $"cove-clean-deps-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var options = database.CreateOptions();
            var eventBus = new EventBus();
            var thumbnails = new DeletionSafetyTests.RecordingThumbnailService();
            var services = new ServiceCollection();
            services.AddScoped(_ => new CoveContext(options));
            AddDeletionServices(services, eventBus, thumbnails);
            await using var provider = services.BuildServiceProvider();
            var deletedEventIds = new List<int>();
            using var subscription = eventBus.Subscribe<EntityEvent>(evt =>
            {
                if (evt.Type == EventType.VideoDeleted)
                    deletedEventIds.Add(evt.EntityId);
            });
            int orphanId, liveId, clipId;
            await using (var db = new CoveContext(options))
            {
                await File.WriteAllBytesAsync(Path.Combine(root, "live.mp4"), [1], TestContext.Current.CancellationToken);
                var folder = new Folder { Path = root };
                var orphan = new Video { Title = "orphan", Files = [new VideoFile { ParentFolder = folder, Basename = "gone.mp4" }] };
                var clip = new Video { Title = "orphan clip", ParentVideo = orphan };
                var live = new Video { Title = "live", Files = [new VideoFile { ParentFolder = folder, Basename = "live.mp4" }] };
                var tag = new Tag { Name = "tag" };
                var definition = new CustomFieldDefinition { Key = "clean_fixture", Label = "Clean fixture", EntityTypes = [CustomFieldEntityTypes.Video] };
                db.AddRange(orphan, clip, live, tag, definition);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                (orphanId, liveId, clipId) = (orphan.Id, live.Id, clip.Id);
                foreach (var hostId in new[] { orphanId, clipId, liveId })
                {
                    db.AddRange(
                        new TagApplication { HostType = AffinityHostType.Video, HostId = hostId, TagId = tag.Id, SourceKey = "test" },
                        new Segment { HostType = SegmentHostType.Video, HostId = hostId, StartSec = 1, SourceKey = "test" },
                        new CustomFieldValue { DefinitionId = definition.Id, EntityType = CustomFieldEntityTypes.Video, EntityId = hostId, TextValue = "x" });
                }
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var jobs = new CapturingJobs(new InspectionGuard(), TestContext.Current.CancellationToken);
            new CleanService(jobs, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CleanService>.Instance).StartClean();
            await jobs.Completion;

            await using var verify = new CoveContext(options);
            Assert.Equal([liveId], await verify.Videos.Select(video => video.Id).ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal([liveId], await verify.TagApplications.Select(item => item.HostId).ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal([liveId], await verify.Segments.Select(item => item.HostId).ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal([liveId], await verify.CustomFieldValues.Select(item => item.EntityId).ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal(new[] { orphanId, clipId }.Order(), deletedEventIds.Order());
            Assert.Equal(new[] { orphanId, clipId }.Order(), thumbnails.DeletedVideoIds.Order());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CleanFailureDoesNotLeakIntoTheNextDeletionInTheBatch()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), $"cove-clean-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var failure = new ChosenImageDeleteFailure();
            var options = database.CreateOptions(builder => builder.AddInterceptors(failure));
            var services = new ServiceCollection();
            services.AddScoped(_ => new CoveContext(options));
            AddDeletionServices(services);
            await using var provider = services.BuildServiceProvider();
            await using (var db = new CoveContext(options))
            {
                var folder = new Folder { Path = root };
                db.AddRange(
                    new Image { Title = "first", Files = [new ImageFile { ParentFolder = folder, Basename = "first.jpg" }] },
                    new Image { Title = "second", Files = [new ImageFile { ParentFolder = folder, Basename = "second.jpg" }] });
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            failure.Armed = true;
            var jobs = new CapturingJobs(new InspectionGuard(), TestContext.Current.CancellationToken);
            new CleanService(jobs, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CleanService>.Instance).StartClean();
            await jobs.Completion;

            // The page's shared save fails, so its images are removed one at a time. The failed image stays for
            // the next clean; the other deletion must not replay its staged delete.
            await using var verify = new CoveContext(options);
            Assert.NotNull(failure.FailedImageId);
            Assert.Equal(2, failure.Failures);
            Assert.Equal([failure.FailedImageId.Value], await verify.Images.Select(image => image.Id).ToListAsync(TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CleanRemovesAFullPageOfOrphansWithTheCommandsOfAFewOrphans()
    {
        // A query, a save or a transaction that ran once per orphan would make the full page cost more.
        var few = await CountCleanCommandsAsync(orphans: 8);
        var page = await CountCleanCommandsAsync(orphans: CleanPlan.BatchSize);
        Assert.Equal(few, page);
    }

    [Fact]
    public async Task ASecondDeletionCompilesNoNewQueries()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var counting = false;
        var compilations = 0;
        // A cache of its own: the shared one is size-limited and drops new entries when the rest of the suite
        // has filled it, which would make the second save compile again for an unrelated reason.
        using var queryCache = new MemoryCache(new MemoryCacheOptions());
        var options = database.CreateOptions(builder => builder.UseMemoryCache(queryCache).LogTo(
            (eventId, _) => eventId == CoreEventId.QueryCompilationStarting,
            _ =>
            {
                if (counting)
                    Interlocked.Increment(ref compilations);
            }));
        await using var db = new CoveContext(options);
        var folder = new Folder { Path = "/library" };
        db.Images.AddRange(
            new Image { Title = "first", Files = [new ImageFile { ParentFolder = folder, Basename = "first.jpg" }] },
            new Image { Title = "second", Files = [new ImageFile { ParentFolder = folder, Basename = "second.jpg" }] });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var images = await db.Images.OrderBy(image => image.Id).ToListAsync(TestContext.Current.CancellationToken);

        db.Images.Remove(images[0]);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // The first save compiled every query a deletion's save runs. A save for another id must reuse them: an id
        // that ends up in a query's cache key instead of a parameter makes every save compile that query again.
        counting = true;
        db.Images.Remove(images[1]);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        counting = false;

        Assert.Equal(0, compilations);
        Assert.Equal(0, await db.Images.CountAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<(int Commands, int Transactions)> CountCleanCommandsAsync(int orphans)
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var counter = new CommandCounter();
        var options = database.CreateOptions(builder => builder.AddInterceptors(counter));
        var eventBus = new EventBus();
        var services = new ServiceCollection();
        services.AddScoped(_ => new CoveContext(options));
        AddDeletionServices(services, eventBus);
        await using var provider = services.BuildServiceProvider();
        var deletedImages = 0;
        using var subscription = eventBus.Subscribe<EntityEvent>(evt =>
        {
            if (evt.Type == EventType.ImageDeleted)
                Interlocked.Increment(ref deletedImages);
        });
        await using (var db = new CoveContext(options))
        {
            // Nothing exists below this folder, so every image is an orphan.
            var folder = new Folder { Path = Path.Combine(Path.GetTempPath(), $"cove-clean-count-{Guid.NewGuid():N}") };
            db.Images.AddRange(Enumerable.Range(0, orphans).Select(index => new Image
            {
                Title = $"orphan {index}", Files = [new ImageFile { ParentFolder = folder, Basename = $"{index}.jpg" }],
            }));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        counter.Enabled = true;
        var jobs = new CapturingJobs(new InspectionGuard(), TestContext.Current.CancellationToken);
        new CleanService(jobs, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CleanService>.Instance).StartClean();
        await jobs.Completion;
        counter.Enabled = false;

        await using var verify = new CoveContext(options);
        Assert.Equal(0, await verify.Images.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await verify.ImageFiles.CountAsync(TestContext.Current.CancellationToken));
        // Batching the save must not batch away the per-entity event.
        Assert.Equal(orphans, deletedImages);
        return (counter.Commands, counter.Transactions);
    }

    [Theory]
    [InlineData(BulkDeletionEntityKind.Image)]
    [InlineData(BulkDeletionEntityKind.Audio)]
    [InlineData(BulkDeletionEntityKind.Text)]
    [InlineData(BulkDeletionEntityKind.Gallery)]
    public async Task BatchDeletionGivesEachEntityWhatASingleDeletionGives(BulkDeletionEntityKind kind)
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), $"cove-batch-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var options = database.CreateOptions();
            var eventBus = new EventBus();
            var services = new ServiceCollection();
            services.AddScoped(_ => new CoveContext(options));
            AddDeletionServices(services, eventBus);
            await using var provider = services.BuildServiceProvider();
            var (hostType, ratingHostType, customFieldType, deletedEvent) = kind switch
            {
                BulkDeletionEntityKind.Image => (AffinityHostType.Image, RatingHostType.Image, CustomFieldEntityTypes.Image, EventType.ImageDeleted),
                BulkDeletionEntityKind.Audio => (AffinityHostType.Audio, RatingHostType.Audio, CustomFieldEntityTypes.Audio, EventType.AudioDeleted),
                BulkDeletionEntityKind.Text => (AffinityHostType.Text, RatingHostType.Text, CustomFieldEntityTypes.Text, EventType.TextDeleted),
                _ => (AffinityHostType.Gallery, RatingHostType.Gallery, CustomFieldEntityTypes.Gallery, EventType.GalleryDeleted),
            };
            var deletedEventIds = new List<int>();
            using var subscription = eventBus.Subscribe<EntityEvent>(evt =>
            {
                if (evt.Type == deletedEvent)
                    deletedEventIds.Add(evt.EntityId);
            });

            string[] names = ["first", "second", "kept"];
            int[] ids;
            await using (var db = new CoveContext(options))
            {
                var folder = new Folder { Path = root };
                var entities = new List<BaseEntity>();
                foreach (var name in names)
                {
                    await File.WriteAllBytesAsync(Path.Combine(root, $"{name}.bin"), [1], TestContext.Current.CancellationToken);
                    entities.Add(kind switch
                    {
                        BulkDeletionEntityKind.Image => new Image { Title = name, Files = [new ImageFile { ParentFolder = folder, Basename = $"{name}.bin" }] },
                        BulkDeletionEntityKind.Audio => new Audio { Title = name, Files = [new AudioFile { ParentFolder = folder, Basename = $"{name}.bin" }] },
                        BulkDeletionEntityKind.Text => new TextDocument { Title = name, Files = [new TextFile { ParentFolder = folder, Basename = $"{name}.bin" }] },
                        _ => new Gallery { Title = name, Files = [new GalleryFile { ParentFolder = folder, Basename = $"{name}.bin" }] },
                    });
                }
                var tag = new Tag { Name = "tag" };
                var definition = new CustomFieldDefinition { Key = "batch_fixture", Label = "Batch fixture", EntityTypes = [customFieldType] };
                db.AddRange(entities);
                var user = new User { Username = "rater", PasswordHash = "test" };
                db.AddRange(tag, definition, user);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                ids = entities.Select(entity => entity.Id).ToArray();
                foreach (var id in ids)
                {
                    db.AddRange(
                        new TagApplication { HostType = hostType, HostId = id, TagId = tag.Id, SourceKey = "test" },
                        new Rating { UserId = user.Id, HostType = ratingHostType, HostId = id, Value = 80 },
                        new CustomFieldValue { DefinitionId = definition.Id, EntityType = customFieldType, EntityId = id, TextValue = "x" });
                }
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            int[] expectedDeleted = [ids[0], ids[1]];
            int[] expectedKept = [ids[2]];
            var context = new BulkDeletionExecutionContext();
            using (var scope = provider.CreateScope())
            {
                var deletion = scope.ServiceProvider.GetRequiredService<BulkEntityDeletionService>();
                // An id that no longer exists is skipped and left out of the result, not treated as a failure.
                var deleted = await deletion.DeleteBatchAsync(kind, [ids[0], ids[1], int.MaxValue], context,
                    deleteFiles: true, deleteGenerated: true, TestContext.Current.CancellationToken);
                Assert.Equal(expectedDeleted, deleted.Order());
            }

            // A gallery's files are never deleted from disk with it; the other kinds get one durable outbox row each.
            string[] expectedOutbox = kind == BulkDeletionEntityKind.Gallery
                ? []
                : [.. names.Take(2).Select(name => FilesystemPaths.ToStoredPath(Path.Combine(root, $"{name}.bin"))).Order(StringComparer.Ordinal)];
            await using (var verify = new CoveContext(options))
            {
                Assert.Equal(expectedKept, await RemainingIdsAsync(verify, kind));
                Assert.Equal(expectedKept, await verify.TagApplications.Select(item => item.HostId).ToListAsync(TestContext.Current.CancellationToken));
                Assert.Equal(expectedKept, await verify.Ratings.Select(item => item.HostId).ToListAsync(TestContext.Current.CancellationToken));
                Assert.Equal(expectedKept, await verify.CustomFieldValues.Select(item => item.EntityId).ToListAsync(TestContext.Current.CancellationToken));
                Assert.Equal(1, await verify.Set<BaseFileEntity>().CountAsync(TestContext.Current.CancellationToken));
                var outbox = await verify.PendingPhysicalFileDeletions.ToListAsync(TestContext.Current.CancellationToken);
                Assert.Equal(expectedOutbox, outbox.Select(item => item.Path).Order(StringComparer.Ordinal));
                Assert.All(outbox, item => Assert.Equal(context.PhysicalDeletionBatchId, item.BatchId));
            }
            Assert.Equal(expectedDeleted, deletedEventIds.Order());
            // The files are only removed by the outbox pass, after its referenced-path check.
            Assert.All(names, name => Assert.True(File.Exists(Path.Combine(root, $"{name}.bin"))));

            if (kind != BulkDeletionEntityKind.Gallery)
            {
                using var scope = provider.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<BulkEntityDeletionService>()
                    .DeleteTrackedPhysicalFilesAsync(kind, context, maxParallelism: 1, TestContext.Current.CancellationToken);
                Assert.Equal(new BulkPhysicalDeletionResult(2, 0), result);
                Assert.Equal([false, false, true], names.Select(name => File.Exists(Path.Combine(root, $"{name}.bin"))));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Task<List<int>> RemainingIdsAsync(CoveContext db, BulkDeletionEntityKind kind) => kind switch
    {
        BulkDeletionEntityKind.Image => db.Images.Select(item => item.Id).ToListAsync(TestContext.Current.CancellationToken),
        BulkDeletionEntityKind.Audio => db.Audios.Select(item => item.Id).ToListAsync(TestContext.Current.CancellationToken),
        BulkDeletionEntityKind.Text => db.TextDocuments.Select(item => item.Id).ToListAsync(TestContext.Current.CancellationToken),
        _ => db.Galleries.Select(item => item.Id).ToListAsync(TestContext.Current.CancellationToken),
    };

    private sealed class CommandCounter : DbCommandInterceptor, IDbTransactionInterceptor
    {
        private int commands;
        private int transactions;
        public bool Enabled { get; set; }
        public int Commands => commands;
        public int Transactions => transactions;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Count(ref commands); return ValueTask.FromResult(result); }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Count(ref commands); return result; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Count(ref commands); return ValueTask.FromResult(result); }
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        { Count(ref commands); return result; }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        { Count(ref commands); return ValueTask.FromResult(result); }
        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        { Count(ref commands); return result; }
        public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        { Count(ref transactions); return Task.CompletedTask; }
        public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Count(ref transactions);

        private void Count(ref int counter)
        {
            if (Enabled)
                Interlocked.Increment(ref counter);
        }
    }

    private sealed class ChosenImageDeleteFailure : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public int? FailedImageId { get; private set; }
        public int Failures { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            // Fails every save that deletes the chosen image: first the page's shared save, then that image's own.
            if (Armed
                && eventData.Context!.ChangeTracker.Entries<Image>().FirstOrDefault(entry => entry.State == EntityState.Deleted
                    && (FailedImageId is null || entry.Entity.Id == FailedImageId)) is { } deleted)
            {
                FailedImageId = deleted.Entity.Id;
                Failures++;
                throw new DbUpdateException("Injected save failure");
            }
            return ValueTask.FromResult(result);
        }
    }

    private static void AddDeletionServices(IServiceCollection services, IEventBus? eventBus = null,
        IThumbnailService? thumbnails = null)
    {
        services.AddScoped<CustomFieldService>();
        services.AddScoped<IThumbnailService>(_ => thumbnails ?? new DeletionSafetyTests.RecordingThumbnailService());
        services.AddScoped<IBlobService>(provider => new DeletionSafetyTests.ReferenceAwareBlobService(provider.GetRequiredService<CoveContext>()));
        services.AddScoped<ImageDeletionService>(provider => new ImageDeletionService(provider.GetRequiredService<CoveContext>(),
            provider.GetRequiredService<CustomFieldService>(), provider.GetRequiredService<IThumbnailService>(),
            blobService: provider.GetRequiredService<IBlobService>()));
        services.AddSingleton(eventBus ?? new EventBus());
        services.AddScoped(provider => new BulkEntityDeletionService(provider.GetRequiredService<CoveContext>(),
            provider.GetRequiredService<CustomFieldService>(), provider.GetRequiredService<ImageDeletionService>(),
            provider.GetRequiredService<IThumbnailService>(), provider.GetRequiredService<IBlobService>(),
            provider.GetRequiredService<IEventBus>()));
    }

    private sealed class CapturingJobs(InspectionGuard guard, CancellationToken ct) : IJobService
    {
        public Task Completion { get; private set; } = Task.CompletedTask;
        public string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
        {
            Completion = work(new Progress(guard), ct);
            return "clean-test";
        }
        public bool Cancel(string id) => false;
        public bool ReorderQueued(string id, string? before) => false;
        public JobInfo? GetJob(string id) => null;
        public IReadOnlyList<JobInfo> GetAllJobs() => [];
        public IReadOnlyList<JobInfo> GetJobHistory() => [];
    }
    private sealed class Progress(InspectionGuard guard) : IJobProgress
    {
        public void Report(double progress, string? subTask = null) => guard.Inspect();
    }
    private sealed class InspectionGuard : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public int Inspected { get; private set; }
        private int unreadInspection;
        public int RequiredInspectionCount { get; init; } = 601;
        private bool deleting;
        public Action? AfterInspect { get; set; }
        public void Inspect() { Inspected += unreadInspection; unreadInspection = 0; AfterInspect?.Invoke(); }
        public void ReadRow()
        {
            unreadInspection++;
            Assert.True(unreadInspection <= 256, "Clean read more than one page of videos before inspecting them.");
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            CheckDeletion(command);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            // Npgsql sends a SaveChanges batch, deletions included, through a reader.
            CheckDeletion(command);
            return ValueTask.FromResult(result);
        }
        private void CheckDeletion(DbCommand command)
        {
            if (Enabled && DeleteStatement.IsMatch(command.CommandText))
            {
                Assert.True(Inspected >= RequiredInspectionCount, "Destination deletion began before inspection finished.");
                deleting = true;
            }
        }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<DbDataReader>(Enabled && !deleting && !command.CommandText.Contains("COUNT(", StringComparison.OrdinalIgnoreCase)
                && EntityTable.IsMatch(command.CommandText) && !FilesCollection.IsMatch(command.CommandText)
                ? new GuardedReader(result, this) : result);

        // A batch can start with another statement, so a deletion is any statement in it that starts with DELETE.
        private static readonly Regex DeleteStatement = new(@"(^|;)\s*DELETE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // Npgsql quotes an identifier only when it must, so these lower-case table names appear bare.
        private static readonly Regex EntityTable = new(@"\bFROM ""?(videos|images|galleries|audios|text_documents)""?\s", RegexOptions.CultureInvariant);
        // Split queries load an entity's files in a second query joined to the page. Its rows are the page's
        // files, not more entities, so only the page query itself is counted.
        private static readonly Regex FilesCollection = new(@"\bJOIN \(\s*SELECT\b[^()]*\bFROM ""?files""?\s", RegexOptions.CultureInvariant);
    }
    private sealed class GuardedReader(DbDataReader inner, InspectionGuard guard) : DbDataReader
    {
        public override bool Read() { var result = inner.Read(); if (result) guard.ReadRow(); return result; }
        public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
        { var result = await inner.ReadAsync(cancellationToken); if (result) guard.ReadRow(); return result; }
        public override bool NextResult() => inner.NextResult();
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => inner.NextResultAsync(cancellationToken);
        public override void Close() => inner.Close();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        // Npgsql reads most columns through these; the base versions would cast GetValue and lose provider conversions.
        public override T GetFieldValue<T>(int ordinal) => inner.GetFieldValue<T>(ordinal);
        public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) => inner.GetFieldValueAsync<T>(ordinal, cancellationToken);
        public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) => inner.IsDBNullAsync(ordinal, cancellationToken);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long offset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, offset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(int ordinal, long offset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, offset, buffer, bufferOffset, length);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override IEnumerator GetEnumerator() => ((IEnumerable)inner).GetEnumerator();
    }
}
