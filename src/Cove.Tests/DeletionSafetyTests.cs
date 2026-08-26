using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;

namespace Cove.Tests;

public sealed class DeletionSafetyTests
{
    [Fact]
    public async Task WriterWaitsForAnActiveFileProducer()
    {
        var coordinator = new PhysicalFileAccessCoordinator();
        var reader = await coordinator.AcquireReadAsync(CancellationToken.None);
        var writerTask = coordinator.AcquireWriteAsync(CancellationToken.None).AsTask();

        await Task.Delay(50);
        Assert.False(writerTask.IsCompleted);

        reader.Dispose();
        using var writer = await writerTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DurableOutboxDeletesAnUnreferencedFileAndClearsItsRow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cove-outbox-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        try
        {
            await using var db = CreateContext();
            var context = new BulkDeletionExecutionContext();
            context.StagePhysicalFiles(db, [path]);
            await db.SaveChangesAsync();

            var service = new PhysicalFileDeletionService(db, new PhysicalFileAccessCoordinator());
            var result = await service.ProcessPendingAsync(context.PhysicalDeletionBatchId, 2, CancellationToken.None);

            Assert.Equal(1, result.Deleted);
            Assert.Equal(0, result.Failed);
            Assert.False(File.Exists(path));
            Assert.Empty(await db.PendingPhysicalFileDeletions.ToListAsync());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ConcurrentOutboxDrainsSerializeFetchDeleteAndAcknowledgement()
    {
        var databaseName = $"DeletionOutboxOverlap-{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite($"Data Source={databaseName};Mode=Memory;Cache=Shared")
            .Options;
        var path = Path.Combine(Path.GetTempPath(), $"cove-outbox-overlap-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        try
        {
            await using var anchor = new CoveContext(options);
            await anchor.Database.OpenConnectionAsync();
            await anchor.Database.EnsureCreatedAsync();
            var staged = new BulkDeletionExecutionContext();
            staged.StagePhysicalFiles(anchor, [path]);
            await anchor.SaveChangesAsync();

            await using var firstDb = new CoveContext(options);
            await using var secondDb = new CoveContext(options);
            var coordinator = new PhysicalFileAccessCoordinator();
            using var blocker = await coordinator.AcquireWriteAsync(CancellationToken.None);
            var first = new PhysicalFileDeletionService(firstDb, coordinator)
                .ProcessPendingAsync(staged.PhysicalDeletionBatchId, 2, CancellationToken.None);
            var second = new PhysicalFileDeletionService(secondDb, coordinator)
                .ProcessPendingAsync(batchId: null, 2, CancellationToken.None);
            await Task.Delay(50);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            blocker.Dispose();

            var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, results.Sum(result => result.Deleted));
            Assert.Equal(0, results.Sum(result => result.Failed));
            Assert.False(File.Exists(path));
            anchor.ChangeTracker.Clear();
            Assert.Empty(await anchor.PendingPhysicalFileDeletions.ToArrayAsync());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ReferenceAddedByAReaderBeforeWriterEntryProtectsTheFile()
    {
        var databaseName = $"DeletionReferenceRace-{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite($"Data Source={databaseName};Mode=Memory;Cache=Shared")
            .Options;
        var path = Path.Combine(Path.GetTempPath(), $"cove-reference-race-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var coordinator = new PhysicalFileAccessCoordinator();
        try
        {
            await using var anchor = new CoveContext(options);
            await anchor.Database.OpenConnectionAsync();
            await anchor.Database.EnsureCreatedAsync();
            var deletionContext = new BulkDeletionExecutionContext();
            deletionContext.StagePhysicalFiles(anchor, [path]);
            await anchor.SaveChangesAsync();

            var reader = await coordinator.AcquireReadAsync(CancellationToken.None);
            var service = new PhysicalFileDeletionService(anchor, coordinator);
            var deletionTask = service.ProcessPendingAsync(deletionContext.PhysicalDeletionBatchId, 1, CancellationToken.None);
            await Task.Delay(50);
            Assert.False(deletionTask.IsCompleted);

            await using (var producerDb = new CoveContext(options))
            {
                var folderPath = Path.GetDirectoryName(path)!;
                producerDb.ImageFiles.Add(new ImageFile
                {
                    Basename = Path.GetFileName(path),
                    ParentFolder = new Folder { Path = folderPath, ModTime = DateTime.UtcNow },
                    Path = path,
                    Size = 3,
                    ModTime = DateTime.UtcNow,
                });
                await producerDb.SaveChangesAsync();
            }
            reader.Dispose();

            var result = await deletionTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, result.Deleted);
            Assert.Equal(0, result.Failed);
            Assert.True(File.Exists(path));
            Assert.Empty(await anchor.PendingPhysicalFileDeletions.ToListAsync());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task DurableOutboxPreservesAReplacementAtTheSamePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cove-replacement-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        try
        {
            await using var db = CreateContext();
            var context = new BulkDeletionExecutionContext();
            context.StagePhysicalFiles(db, [path]);
            await db.SaveChangesAsync();

            await File.WriteAllBytesAsync(path, [9, 8, 7, 6]);
            var service = new PhysicalFileDeletionService(db, new PhysicalFileAccessCoordinator());
            var result = await service.ProcessPendingAsync(context.PhysicalDeletionBatchId, 2, CancellationToken.None);

            Assert.Equal(0, result.Deleted);
            Assert.Equal(0, result.Failed);
            Assert.Equal([9, 8, 7, 6], await File.ReadAllBytesAsync(path));
            Assert.Empty(await db.PendingPhysicalFileDeletions.ToListAsync());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task DurableOutboxPreservesAFileCreatedAfterAnAbsentPathWasStaged()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cove-late-file-{Guid.NewGuid():N}.jpg");
        try
        {
            await using var db = CreateContext();
            var context = new BulkDeletionExecutionContext();
            context.StagePhysicalFiles(db, [path]);
            await db.SaveChangesAsync();

            await File.WriteAllBytesAsync(path, [4, 5, 6]);
            var service = new PhysicalFileDeletionService(db, new PhysicalFileAccessCoordinator());
            var result = await service.ProcessPendingAsync(context.PhysicalDeletionBatchId, 2, CancellationToken.None);

            Assert.Equal(0, result.Deleted);
            Assert.Equal(0, result.Failed);
            Assert.Equal([4, 5, 6], await File.ReadAllBytesAsync(path));
            Assert.Empty(await db.PendingPhysicalFileDeletions.ToListAsync());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SingleImageDeletionDoesNotWaitForAnActiveProducerLease()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cove-single-delete-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var coordinator = new PhysicalFileAccessCoordinator();
        using var activeProducer = await coordinator.AcquireReadAsync(CancellationToken.None);
        try
        {
            await using var db = CreateContext();
            var image = new Image
            {
                Title = "Single deletion during scan",
                Files =
                [
                    new ImageFile
                    {
                        Basename = Path.GetFileName(path),
                        ParentFolder = new Folder { Path = Path.GetDirectoryName(path)!, ModTime = DateTime.UtcNow },
                        Path = path,
                        Size = 3,
                        ModTime = DateTime.UtcNow,
                    },
                ],
            };
            db.Images.Add(image);
            await db.SaveChangesAsync();
            var signal = new PhysicalFileDeletionRecoverySignal();
            var deletion = new ImageDeletionService(
                db,
                new CustomFieldService(db),
                new RecordingThumbnailService(),
                physicalFileDeletionRecoverySignal: signal);

            Assert.True(await deletion.DeleteAsync(image.Id, deleteFile: true, deleteGenerated: false)
                .WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(File.Exists(path));
            Assert.Single(await db.PendingPhysicalFileDeletions.ToListAsync());

            activeProducer.Dispose();
            var physicalResult = await new PhysicalFileDeletionService(db, coordinator)
                .ProcessPendingAsync(batchId: null, maxParallelism: 1, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, physicalResult.Deleted);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CaseInsensitivePathNormalizationIsInvariant()
    {
        Assert.Equal(
            PhysicalFileDeletionService.NormalizeCaseInsensitivePath("a/Folder/ß.jpg"),
            PhysicalFileDeletionService.NormalizeCaseInsensitivePath("a/folder/ß.JPG"));
    }

    [Fact]
    public async Task PerformerCleanupIncludesContextAndNestedSegmentDependencies()
    {
        await using var db = CreateContext();
        var performer = new Performer { Name = "Disposable performer" };
        var tag = new Tag { Name = "Disposable metadata tag" };
        var group = new Group { Name = "Segment container" };
        db.AddRange(performer, tag, group);
        await db.SaveChangesAsync();
        var segment = new Segment
        {
            HostType = SegmentHostType.Video,
            HostId = 777,
            StartSec = 1,
            Kind = "Performer",
            RefId = performer.Id,
            SourceKey = "test",
            ImageBlobId = "segment-blob",
        };
        db.Segments.Add(segment);
        await db.SaveChangesAsync();
        db.AddRange(
            new GroupItem { GroupId = group.Id, HostType = "segment", HostId = segment.Id, Kind = GroupItemKind.Segment },
            new TagApplication { HostType = AffinityHostType.Video, HostId = 777, ContextType = "performer", ContextId = performer.Id, TagId = tag.Id, SourceKey = "test" },
            new TagApplication { HostType = AffinityHostType.Segment, HostId = segment.Id, TagId = tag.Id, SourceKey = "test" },
            new FieldProvenance { HostType = AffinityHostType.Segment, HostId = segment.Id, FieldKey = "title", SourceKey = "test" },
            new Embedding
            {
                HostType = EmbeddingHostType.Segment,
                HostId = segment.Id,
                Kind = "test",
                Modality = EmbeddingModality.Visual,
                Dim = 1,
                Vector = new Vector(new float[] { 1f }),
                SourceKey = "test",
            },
            new Detection
            {
                HostType = DetectionHostType.Video,
                HostId = 777,
                Class = "person",
                RefKind = "Performer",
                RefId = performer.Id,
                SourceKey = "test",
            });
        await db.SaveChangesAsync();

        var cleanup = await new EntityHostDependencyService(db)
            .StageDeleteAsync(AffinityHostType.Performer, performer.Id, CancellationToken.None);
        db.Performers.Remove(performer);
        await db.SaveChangesAsync();

        Assert.Equal(["segment-blob"], cleanup.BlobIds);
        Assert.Equal([777], cleanup.SegmentVideoIds);
        Assert.False(await db.Segments.AnyAsync(item => item.Id == segment.Id));
        Assert.False(await db.GroupItems.AnyAsync(item => item.HostType == "segment" && item.HostId == segment.Id));
        Assert.False(await db.TagApplications.AnyAsync(item => item.ContextType == "performer" && item.ContextId == performer.Id));
        Assert.False(await db.TagApplications.AnyAsync(item => item.HostType == AffinityHostType.Segment && item.HostId == segment.Id));
        Assert.False(await db.FieldProvenance.AnyAsync(item => item.HostType == AffinityHostType.Segment && item.HostId == segment.Id));
        Assert.False(await db.Embeddings.AnyAsync(item => item.HostType == EmbeddingHostType.Segment && item.HostId == segment.Id));
        Assert.False(await db.Detections.AnyAsync(item => item.RefKind == "Performer" && item.RefId == performer.Id));
    }

    [Fact]
    public async Task HostCleanupIgnoresUnrelatedEmbeddingAndAiRunReadPermissions()
    {
        var principalAccessor = new CurrentPrincipalAccessor();
        await using var db = CreateContext(principalAccessor);
        var video = new Video { Title = "Authorized deletion target" };
        db.Videos.Add(video);
        await db.SaveChangesAsync();
        db.AddRange(
            new Embedding
            {
                HostType = EmbeddingHostType.Video,
                HostId = video.Id,
                Kind = "test",
                Modality = EmbeddingModality.Visual,
                Dim = 1,
                Vector = new Vector(new float[] { 1f }),
                SourceKey = "test",
            },
            new AiRun
            {
                RunKey = Guid.NewGuid().ToString("n"),
                SourceKey = "test",
                TargetType = AiRunTargetType.Video,
                TargetId = video.Id,
            });
        await db.SaveChangesAsync();
        principalAccessor.Set(new CovePrincipal
        {
            UserId = 7,
            Username = "video-deleter",
            Kind = PrincipalKind.User,
            Permissions = new HashSet<string> { Permissions.VideosRead, Permissions.VideosDelete },
            Roles = new HashSet<string>(),
        });

        await new EntityHostDependencyService(db)
            .StageDeleteAsync(AffinityHostType.Video, video.Id, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Empty(await db.Embeddings.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.AiRuns.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task ParentVideoDeletionCleansDescendantDependenciesAndPublishesTheirEvents()
    {
        await using var db = CreateContext();
        var root = new Video { Title = "Parent" };
        var child = new Video { Title = "Child", ParentVideo = root };
        var grandchild = new Video { Title = "Grandchild", ParentVideo = child };
        var tag = new Tag { Name = "Child dependency tag" };
        var group = new Group { Name = "Child dependency group" };
        var definition = new CustomFieldDefinition
        {
            Key = "deletion_fixture",
            Label = "Deletion fixture",
            EntityTypes = [CustomFieldEntityTypes.Video],
        };
        db.AddRange(root, child, grandchild, tag, group, definition);
        await db.SaveChangesAsync();
        db.AddRange(
            new GroupItem { GroupId = group.Id, HostType = "video", HostId = child.Id, Kind = GroupItemKind.Video },
            new TagApplication { HostType = AffinityHostType.Video, HostId = child.Id, TagId = tag.Id, SourceKey = "test" },
            new Segment { HostType = SegmentHostType.Video, HostId = child.Id, StartSec = 1, SourceKey = "test" },
            new Embedding
            {
                HostType = EmbeddingHostType.Video,
                HostId = child.Id,
                Kind = "test",
                Modality = EmbeddingModality.Visual,
                Dim = 1,
                Vector = new Vector(new float[] { 1f }),
                SourceKey = "test",
            },
            new AiRun { SourceKey = "test", TargetType = AiRunTargetType.Video, TargetId = child.Id },
            new CustomFieldValue
            {
                DefinitionId = definition.Id,
                EntityType = CustomFieldEntityTypes.Video,
                EntityId = child.Id,
                TextValue = "remove me",
            },
            new UserEntityAffinity { UserId = 17, HostType = AffinityHostType.Video, HostId = child.Id },
            new UserBookmark { UserId = 18, HostType = AffinityHostType.Video, HostId = grandchild.Id, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var eventBus = new EventBus();
        var deletedEventIds = new List<int>();
        using var subscription = eventBus.Subscribe<EntityEvent>(evt =>
        {
            if (evt.Type == EventType.VideoDeleted)
                deletedEventIds.Add(evt.EntityId);
        });
        var customFields = new CustomFieldService(db);
        var thumbnails = new RecordingThumbnailService();
        var blobs = new ReferenceAwareBlobService(db);
        var service = new BulkEntityDeletionService(
            db,
            customFields,
            new ImageDeletionService(db, customFields, thumbnails, blobService: blobs),
            thumbnails,
            blobs,
            eventBus);

        Assert.True(await service.DeleteAsync(
            BulkDeletionEntityKind.Video,
            root.Id,
            new BulkDeletionExecutionContext(),
            deleteFiles: false,
            deleteGenerated: false,
            CancellationToken.None));

        Assert.Empty(await db.Videos.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.GroupItems.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.TagApplications.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.Segments.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.Embeddings.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.AiRuns.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.CustomFieldValues.ToListAsync());
        Assert.Empty(await db.UserEntityAffinities.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.UserBookmarks.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(
            new[] { root.Id, child.Id, grandchild.Id }.Order(),
            deletedEventIds.Order());
    }

    [Fact]
    public async Task VideoDeletionCompletesPostCommitWorkAfterAnAmbiguousCommit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var commitAmbiguity = new CommitAmbiguityInterceptor();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IBlobReferenceCoordinator, BlobReferenceCoordinator>();
        services.AddScoped<BlobReferenceTransactionCoordinator>();
        services.AddScoped<BlobReferenceSaveChangesInterceptor>();
        services.AddDbContext<CoveContext>((provider, options) =>
            options.UseSqlite(connection)
                .ReplaceService<IExecutionStrategyFactory, TestRetryingExecutionStrategyFactory>()
                .AddInterceptors(
                    provider.GetRequiredService<BlobReferenceSaveChangesInterceptor>(),
                    commitAmbiguity));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        await db.Database.EnsureCreatedAsync();
        var video = new Video { Title = "Video with artwork", ImageBlobId = "video-artwork" };
        db.Videos.Add(video);
        await db.SaveChangesAsync();
        var thumbnails = new RecordingThumbnailService();
        var blobs = new ReferenceAwareBlobService(db);
        var customFields = new CustomFieldService(db);
        var eventBus = new EventBus();
        var deletedEventIds = new List<int>();
        using var subscription = eventBus.Subscribe<EntityEvent>(evt =>
        {
            if (evt.Type == EventType.VideoDeleted)
                deletedEventIds.Add(evt.EntityId);
        });
        var service = new BulkEntityDeletionService(
            db,
            customFields,
            new ImageDeletionService(db, customFields, thumbnails, blobService: blobs),
            thumbnails,
            blobs,
            eventBus,
            blobReferenceTransactions: scope.ServiceProvider.GetRequiredService<BlobReferenceTransactionCoordinator>());
        commitAmbiguity.Arm();

        Assert.True(await service.DeleteAsync(
            BulkDeletionEntityKind.Video,
            video.Id,
            new BulkDeletionExecutionContext(),
            deleteFiles: false,
            deleteGenerated: false,
            CancellationToken.None));

        Assert.Empty(await db.Videos.IgnoreQueryFilters().ToArrayAsync());
        Assert.Contains("video-artwork", blobs.DeletedBlobIds);
        Assert.Equal([video.Id], deletedEventIds);
        Assert.Equal(1, commitAmbiguity.FailuresRaised);
        Assert.Empty(await db.VideoDeletionCommitMarkers.ToArrayAsync());
    }

    [Fact]
    public async Task RolledBackVideoDeletionDoesNotUseAConcurrentRootDeletionAsCommitProof()
    {
        var databaseName = $"video-delete-rollback-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        var preCommitFailure = new PreCommitFailureInterceptor();
        var retryingOptions = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connectionString)
            .ReplaceService<IExecutionStrategyFactory, TestRetryingExecutionStrategyFactory>()
            .AddInterceptors(preCommitFailure)
            .Options;
        var ordinaryOptions = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connectionString)
            .Options;
        await using var db = new CoveContext(retryingOptions);
        await db.Database.EnsureCreatedAsync();
        var root = new Video { Title = "Deletion attempt that rolls back" };
        var child = new Video { Title = "Reparented during retry", ParentVideo = root };
        db.AddRange(root, child);
        await db.SaveChangesAsync();
        preCommitFailure.Arm(async () =>
        {
            await using var concurrentDb = new CoveContext(ordinaryOptions);
            var concurrentChild = await concurrentDb.Videos.SingleAsync(video => video.Id == child.Id);
            concurrentChild.ParentVideoId = null;
            await concurrentDb.SaveChangesAsync();
            concurrentDb.Videos.Remove(await concurrentDb.Videos.SingleAsync(video => video.Id == root.Id));
            await concurrentDb.SaveChangesAsync();
        });
        var eventBus = new EventBus();
        var deletedEventIds = new List<int>();
        using var subscription = eventBus.Subscribe<EntityEvent>(evt =>
        {
            if (evt.Type == EventType.VideoDeleted)
                deletedEventIds.Add(evt.EntityId);
        });
        var thumbnails = new RecordingThumbnailService();
        var blobs = new ReferenceAwareBlobService(db);
        var customFields = new CustomFieldService(db);
        var service = new BulkEntityDeletionService(
            db,
            customFields,
            new ImageDeletionService(db, customFields, thumbnails, blobService: blobs),
            thumbnails,
            blobs,
            eventBus);

        Assert.False(await service.DeleteAsync(
            BulkDeletionEntityKind.Video,
            root.Id,
            new BulkDeletionExecutionContext(),
            deleteFiles: false,
            deleteGenerated: true,
            CancellationToken.None));

        db.ChangeTracker.Clear();
        Assert.False(await db.Videos.IgnoreQueryFilters().AnyAsync(video => video.Id == root.Id));
        Assert.True(await db.Videos.IgnoreQueryFilters().AnyAsync(video => video.Id == child.Id));
        Assert.Empty(thumbnails.DeletedVideoIds);
        Assert.Empty(deletedEventIds);
        Assert.Empty(await db.VideoDeletionCommitMarkers.ToArrayAsync());
    }

    [Fact]
    public async Task VideoDeletionJobCollapsesSelectedDescendantsIntoTheirSelectedRoot()
    {
        await using var db = CreateContext();
        var root = new Video { Title = "Selected parent" };
        var child = new Video { Title = "Selected child", ParentVideo = root };
        var grandchild = new Video { Title = "Selected grandchild", ParentVideo = child };
        var independent = new Video { Title = "Independent selection" };
        db.AddRange(root, child, grandchild, independent);
        await db.SaveChangesAsync();

        var collapsed = await BulkDeletionJobService.CollapseSelectedVideoDescendantsAsync(
            db,
            [root.Id, child.Id, grandchild.Id, independent.Id],
            CancellationToken.None);

        Assert.Equal(new[] { root.Id, independent.Id }.Order(), collapsed.Order());
    }

    [Fact]
    public async Task GalleryDeletionRemovesFileRowsAndOnlyCleansUnsharedBlobs()
    {
        await using var db = CreateContext();
        var gallery = new Gallery
        {
            Title = "Disposable gallery",
            ImageBlobId = "shared-blob",
            BackImageBlobId = "gallery-only-blob",
            Files =
            [
                new GalleryFile
                {
                    Basename = "gallery.zip",
                    ParentFolder = new Folder { Path = "/isolated-gallery", ModTime = DateTime.UtcNow },
                    Path = "/isolated-gallery/gallery.zip",
                    ModTime = DateTime.UtcNow,
                },
            ],
        };
        db.AddRange(gallery, new Performer { Name = "Shared blob owner", ImageBlobId = "shared-blob" });
        await db.SaveChangesAsync();
        var thumbnails = new RecordingThumbnailService();
        var blobs = new ReferenceAwareBlobService(db);
        var customFields = new CustomFieldService(db);
        var imageDeletion = new ImageDeletionService(db, customFields, thumbnails, blobService: blobs);
        var service = new BulkEntityDeletionService(
            db,
            customFields,
            imageDeletion,
            thumbnails,
            blobs,
            new EventBus(),
            blobReferenceCounter: new TestBlobReferenceCounter(db));

        var deleted = await service.DeleteAsync(
            BulkDeletionEntityKind.Gallery,
            gallery.Id,
            new BulkDeletionExecutionContext(),
            deleteFiles: false,
            deleteGenerated: true,
            CancellationToken.None);

        Assert.True(deleted);
        Assert.False(await db.Galleries.AnyAsync(item => item.Id == gallery.Id));
        Assert.Empty(await db.GalleryFiles.ToListAsync());
        Assert.Equal(["gallery-only-blob"], blobs.DeletedBlobIds);
        Assert.Equal(["gallery-only-blob"], thumbnails.DeletedBlobIds);
    }

    [Fact]
    public async Task TagDeletionInvalidatesAffectedVideoSegmentCachesAfterCommit()
    {
        await using var db = CreateContext();
        var tag = new Tag { Name = "Timeline tag" };
        db.Tags.Add(tag);
        await db.SaveChangesAsync();
        db.Segments.Add(new Segment
        {
            HostType = SegmentHostType.Video,
            HostId = 314,
            StartSec = 0,
            TagId = tag.Id,
            SourceKey = "test",
        });
        await db.SaveChangesAsync();
        var invalidator = new RecordingSegmentInvalidator();
        var thumbnails = new RecordingThumbnailService();
        var blobs = new ReferenceAwareBlobService(db);
        var customFields = new CustomFieldService(db);
        var service = new BulkEntityDeletionService(
            db,
            customFields,
            new ImageDeletionService(db, customFields, thumbnails, blobService: blobs),
            thumbnails,
            blobs,
            new EventBus(),
            segmentSpanCacheInvalidator: invalidator);

        Assert.True(await service.DeleteAsync(
            BulkDeletionEntityKind.Tag,
            tag.Id,
            new BulkDeletionExecutionContext(),
            deleteFiles: false,
            deleteGenerated: true,
            CancellationToken.None));

        Assert.Equal([314], invalidator.VideoIds);
    }

    private static CoveContext CreateContext(ICurrentPrincipalAccessor? principalAccessor = null)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var context = new CoveContext(options, principalAccessor);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    private sealed class RecordingSegmentInvalidator : ISegmentSpanCacheInvalidator
    {
        public List<int> VideoIds { get; } = [];
        public void InvalidateVideo(int videoId) => VideoIds.Add(videoId);
        public void InvalidateAll() => throw new NotSupportedException();
    }

    private sealed class TestBlobReferenceCounter(CoveContext db) : IBlobReferenceCounter
    {
        public async Task<int> CountReferencesAsync(string blobId, int maximum, CancellationToken ct = default)
        {
            var count = await db.Performers.CountAsync(item => item.ImageBlobId == blobId || item.ImageOverrideBlobId == blobId, ct)
                + await db.Galleries.CountAsync(item => item.ImageBlobId == blobId || item.BackImageBlobId == blobId, ct);
            return Math.Min(maximum, count);
        }
    }

    private sealed class ReferenceAwareBlobService(CoveContext db) : IBlobService
    {
        public List<string> DeletedBlobIds { get; } = [];
        public Task<string> StoreBlobAsync(Stream data, string contentType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(Stream Stream, string ContentType)?> GetBlobAsync(string blobId, CancellationToken ct = default) => throw new NotSupportedException();
        public async Task DeleteBlobAsync(string blobId, CancellationToken ct = default)
        {
            var referenced = await db.Performers.AnyAsync(item => item.ImageBlobId == blobId || item.ImageOverrideBlobId == blobId, ct)
                || await db.Galleries.AnyAsync(item => item.ImageBlobId == blobId || item.BackImageBlobId == blobId, ct);
            if (!referenced)
                DeletedBlobIds.Add(blobId);
        }
    }

    private sealed class RecordingThumbnailService : IThumbnailService
    {
        public List<string> DeletedBlobIds { get; } = [];
        public List<int> DeletedVideoIds { get; } = [];
        public Task<string?> GetVideoThumbnailPathAsync(int videoId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetImageFilePathAsync(int imageId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageStreamAsync(int imageId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageThumbnailStreamAsync(int imageId, int maxDimension, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetBlobImageThumbnailStreamAsync(string blobId, int maxDimension, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteVideoGeneratedFilesAsync(int videoId, CancellationToken ct = default)
        {
            DeletedVideoIds.Add(videoId);
            return Task.CompletedTask;
        }
        public Task DeleteImageGeneratedFilesAsync(int imageId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteBlobGeneratedFilesAsync(string blobId, CancellationToken ct = default)
        {
            DeletedBlobIds.Add(blobId);
            return Task.CompletedTask;
        }
        public Task GenerateVideoThumbnailAsync(int videoId, double? atSeconds = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> GenerateImageThumbnailAsync(int imageId, int maxDimension = 640, bool overwrite = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task GenerateVideoPreviewAsync(int videoId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task GenerateSegmentAnimatedPreviewAsync(int videoId, double startSec, double? endSec = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task GenerateVideoSpriteAsync(int videoId, CancellationToken ct = default) => throw new NotSupportedException();
        public string GetThumbnailPathForVideo(int videoId) => throw new NotSupportedException();
        public string GetTimestampedThumbnailPath(int videoId, double seconds) => throw new NotSupportedException();
        public string GetSegmentAnimatedPreviewPath(int videoId, double seconds) => throw new NotSupportedException();
        public string GetPreviewPath(int videoId) => throw new NotSupportedException();
        public string GetSpritePath(int videoId) => throw new NotSupportedException();
        public string GetSpriteVttPath(int videoId) => throw new NotSupportedException();
        public string StartGenerateAllThumbnails() => throw new NotSupportedException();
    }
}
