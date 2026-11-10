using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Common;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Cove.Plugins;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(writerTask.IsCompleted);

        reader.Dispose();
        using var writer = await writerTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    [RequiresPostgres]
    public async Task DurableOutboxDeletesAnUnreferencedFileAndClearsItsRow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cove-outbox-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
        try
        {
            await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
            await using var db = CreateContext(database);
            var context = new BulkDeletionExecutionContext();
            context.StagePhysicalFiles(db, [path]);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var service = new PhysicalFileDeletionService(db, new PhysicalFileAccessCoordinator());
            var result = await service.ProcessPendingAsync(context.PhysicalDeletionBatchId, 2, CancellationToken.None);

            Assert.Equal(1, result.Deleted);
            Assert.Equal(0, result.Failed);
            Assert.False(File.Exists(path));
            Assert.Empty(await db.PendingPhysicalFileDeletions.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    [RequiresPostgres]
    public async Task ConcurrentOutboxDrainsSerializeFetchDeleteAndAcknowledgement()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var options = database.CreateOptions();
        var path = Path.Combine(Path.GetTempPath(), $"cove-outbox-overlap-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
        try
        {
            await using var anchor = new CoveContext(options);
            var staged = new BulkDeletionExecutionContext();
            staged.StagePhysicalFiles(anchor, [path]);
            await anchor.SaveChangesAsync(TestContext.Current.CancellationToken);

            await using var firstDb = new CoveContext(options);
            await using var secondDb = new CoveContext(options);
            var coordinator = new PhysicalFileAccessCoordinator();
            using var blocker = await coordinator.AcquireWriteAsync(CancellationToken.None);
            var first = new PhysicalFileDeletionService(firstDb, coordinator)
                .ProcessPendingAsync(staged.PhysicalDeletionBatchId, 2, CancellationToken.None);
            var second = new PhysicalFileDeletionService(secondDb, coordinator)
                .ProcessPendingAsync(batchId: null, 2, CancellationToken.None);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            blocker.Dispose();

            var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(1, results.Sum(result => result.Deleted));
            Assert.Equal(0, results.Sum(result => result.Failed));
            Assert.False(File.Exists(path));
            anchor.ChangeTracker.Clear();
            Assert.Empty(await anchor.PendingPhysicalFileDeletions.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    [RequiresPostgres]
    public async Task ReferenceAddedByAReaderBeforeWriterEntryProtectsTheFile()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var options = database.CreateOptions();
        var path = Path.Combine(Path.GetTempPath(), $"cove-reference-race-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
        var coordinator = new PhysicalFileAccessCoordinator();
        try
        {
            await using var anchor = new CoveContext(options);
            var deletionContext = new BulkDeletionExecutionContext();
            deletionContext.StagePhysicalFiles(anchor, [path]);
            await anchor.SaveChangesAsync(TestContext.Current.CancellationToken);

            var reader = await coordinator.AcquireReadAsync(CancellationToken.None);
            var service = new PhysicalFileDeletionService(anchor, coordinator);
            var deletionTask = service.ProcessPendingAsync(deletionContext.PhysicalDeletionBatchId, 1, CancellationToken.None);
            await Task.Delay(50, TestContext.Current.CancellationToken);
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
                await producerDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
            reader.Dispose();

            var result = await deletionTask.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(0, result.Deleted);
            Assert.Equal(0, result.Failed);
            Assert.True(File.Exists(path));
            Assert.Empty(await anchor.PendingPhysicalFileDeletions.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    [RequiresPostgres]
    public async Task DurableOutboxPreservesAReplacementAtTheSamePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cove-replacement-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
        try
        {
            await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
            await using var db = CreateContext(database);
            var context = new BulkDeletionExecutionContext();
            context.StagePhysicalFiles(db, [path]);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            await File.WriteAllBytesAsync(path, [9, 8, 7, 6], TestContext.Current.CancellationToken);
            var service = new PhysicalFileDeletionService(db, new PhysicalFileAccessCoordinator());
            var result = await service.ProcessPendingAsync(context.PhysicalDeletionBatchId, 2, CancellationToken.None);

            Assert.Equal(0, result.Deleted);
            Assert.Equal(0, result.Failed);
            Assert.Equal([9, 8, 7, 6], await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.Empty(await db.PendingPhysicalFileDeletions.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    [RequiresPostgres]
    public async Task DurableOutboxPreservesAFileCreatedAfterAnAbsentPathWasStaged()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cove-late-file-{Guid.NewGuid():N}.jpg");
        try
        {
            await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
            await using var db = CreateContext(database);
            var context = new BulkDeletionExecutionContext();
            context.StagePhysicalFiles(db, [path]);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            await File.WriteAllBytesAsync(path, [4, 5, 6], TestContext.Current.CancellationToken);
            var service = new PhysicalFileDeletionService(db, new PhysicalFileAccessCoordinator());
            var result = await service.ProcessPendingAsync(context.PhysicalDeletionBatchId, 2, CancellationToken.None);

            Assert.Equal(0, result.Deleted);
            Assert.Equal(0, result.Failed);
            Assert.Equal([4, 5, 6], await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.Empty(await db.PendingPhysicalFileDeletions.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    [RequiresPostgres]
    public async Task SingleImageDeletionDoesNotWaitForAnActiveProducerLease()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cove-single-delete-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
        var coordinator = new PhysicalFileAccessCoordinator();
        using var activeProducer = await coordinator.AcquireReadAsync(CancellationToken.None);
        try
        {
            await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
            await using var db = CreateContext(database);
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
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var signal = new PhysicalFileDeletionRecoverySignal();
            var deletion = new ImageDeletionService(
                db,
                new CustomFieldService(db),
                new RecordingThumbnailService(),
                physicalFileDeletionRecoverySignal: signal);

            Assert.True(await deletion.DeleteAsync(image.Id, deleteFile: true, deleteGenerated: false, ct: TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.True(File.Exists(path));
            Assert.Single(await db.PendingPhysicalFileDeletions.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));

            activeProducer.Dispose();
            var physicalResult = await new PhysicalFileDeletionService(db, coordinator)
                .ProcessPendingAsync(batchId: null, maxParallelism: 1, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
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

    // The outbox tests need a database; this keeps the identity check they rely on running without one, on the
    // forward-slash path form the outbox stores.
    [Fact]
    public async Task StagedFileIdentityDoesNotMatchAReplacementOrAFileCreatedLater()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cove-identity-{Guid.NewGuid():N}.jpg");
        var storedPath = FilesystemPaths.ToStoredPath(path);
        try
        {
            var absent = PhysicalFileIdentitySnapshot.Capture(storedPath);
            Assert.True(absent.Captured);
            Assert.False(absent.Exists);

            await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
            var staged = PhysicalFileIdentitySnapshot.Capture(storedPath);
            Assert.True(staged.Captured);
            Assert.True(staged.Exists);
            Assert.Equal(3, staged.Length);
            Assert.NotEqual(absent, staged);
            Assert.Equal(staged, PhysicalFileIdentitySnapshot.Capture(storedPath));

            await File.WriteAllBytesAsync(path, [9, 8, 7, 6], TestContext.Current.CancellationToken);
            Assert.NotEqual(staged, PhysicalFileIdentitySnapshot.Capture(storedPath));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    [RequiresPostgres]
    public async Task PerformerCleanupIncludesContextAndNestedSegmentDependencies()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
        var performer = new Performer { Name = "Disposable performer" };
        var tag = new Tag { Name = "Disposable metadata tag" };
        var group = new Group { Name = "Segment container" };
        db.AddRange(performer, tag, group);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var cleanup = await new EntityHostDependencyService(db)
            .StageDeleteAsync(AffinityHostType.Performer, performer.Id, CancellationToken.None);
        db.Performers.Remove(performer);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["segment-blob"], cleanup.BlobIds);
        Assert.Equal([777], cleanup.SegmentVideoIds);
        Assert.False(await db.Segments.AnyAsync(item => item.Id == segment.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await db.GroupItems.AnyAsync(item => item.HostType == "segment" && item.HostId == segment.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await db.TagApplications.AnyAsync(item => item.ContextType == "performer" && item.ContextId == performer.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await db.TagApplications.AnyAsync(item => item.HostType == AffinityHostType.Segment && item.HostId == segment.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await db.FieldProvenance.AnyAsync(item => item.HostType == AffinityHostType.Segment && item.HostId == segment.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await db.Embeddings.AnyAsync(item => item.HostType == EmbeddingHostType.Segment && item.HostId == segment.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await db.Detections.AnyAsync(item => item.RefKind == "Performer" && item.RefId == performer.Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    [RequiresPostgres]
    public async Task HostCleanupIgnoresUnrelatedEmbeddingAndAiRunReadPermissions()
    {
        var principalAccessor = new CurrentPrincipalAccessor();
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database, principalAccessor);
        var video = new Video { Title = "Authorized deletion target" };
        db.Videos.Add(video);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        principalAccessor.Set(new CovePrincipal
        {
            UserId = 7,
            Username = "video-deleter",
            Kind = PrincipalKind.User,
            Permissions = new HashSet<string> { Permissions.VideosRead, Permissions.VideosDelete },
            Roles = new HashSet<string>(),
        });
        // The deleter cannot read the embedding or the run, so cleanup must find them past the authorization filters.
        Assert.True(await db.Videos.AnyAsync(item => item.Id == video.Id, TestContext.Current.CancellationToken));
        Assert.False(await db.Embeddings.AnyAsync(TestContext.Current.CancellationToken));
        Assert.False(await db.AiRuns.AnyAsync(TestContext.Current.CancellationToken));
        Assert.Single(await db.Embeddings.IgnoreQueryFilters().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await db.AiRuns.IgnoreQueryFilters().ToListAsync(TestContext.Current.CancellationToken));

        await new EntityHostDependencyService(db)
            .StageDeleteAsync(AffinityHostType.Video, video.Id, CancellationToken.None);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await db.Embeddings.IgnoreQueryFilters().ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await db.AiRuns.IgnoreQueryFilters().ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    [RequiresPostgres]
    public async Task ParentVideoDeletionCleansDescendantDependenciesAndPublishesTheirEvents()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
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
        db.AddRange(
            root,
            child,
            grandchild,
            tag,
            group,
            definition,
            new User { Id = 17, Username = "user-17", PasswordHash = "test" },
            new User { Id = 18, Username = "user-18", PasswordHash = "test" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

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

        Assert.Empty(await db.Videos.IgnoreQueryFilters().ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await db.GroupItems.IgnoreQueryFilters().ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await db.TagApplications.IgnoreQueryFilters().ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await db.Segments.IgnoreQueryFilters().ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await db.Embeddings.IgnoreQueryFilters().ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await db.AiRuns.IgnoreQueryFilters().ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await db.CustomFieldValues.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await db.UserEntityAffinities.IgnoreQueryFilters().ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await db.UserBookmarks.IgnoreQueryFilters().ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(
            new[] { root.Id, child.Id, grandchild.Id }.Order(),
            deletedEventIds.Order());
    }

    [Fact]
    [RequiresPostgres]
    public async Task VideoDeletionRemovesVideosTogetherWithTheirPrimaryFiles()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
        var folder = new Folder { Path = "/library" };
        var root = new Video { Title = "Parent", Files = [new VideoFile { ParentFolder = folder, Basename = "parent.mp4" }] };
        var child = new Video { Title = "Child", ParentVideo = root, Files = [new VideoFile { ParentFolder = folder, Basename = "child.mp4" }] };
        var unrelated = new Video { Title = "Unrelated", Files = [new VideoFile { ParentFolder = folder, Basename = "unrelated.mp4" }] };
        db.AddRange(root, child, unrelated);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(root.PrimaryFileId);
        Assert.NotNull(child.PrimaryFileId);
        db.ChangeTracker.Clear();

        var customFields = new CustomFieldService(db);
        var thumbnails = new RecordingThumbnailService();
        var blobs = new ReferenceAwareBlobService(db);
        var service = new BulkEntityDeletionService(
            db,
            customFields,
            new ImageDeletionService(db, customFields, thumbnails, blobService: blobs),
            thumbnails,
            blobs,
            new EventBus());

        Assert.True(await service.DeleteAsync(
            BulkDeletionEntityKind.Video,
            root.Id,
            new BulkDeletionExecutionContext(),
            deleteFiles: false,
            deleteGenerated: false,
            CancellationToken.None));

        var remaining = Assert.Single(await db.Videos.IgnoreQueryFilters().ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(unrelated.Id, remaining.Id);
        Assert.Equal(["unrelated.mp4"], await db.VideoFiles.IgnoreQueryFilters().Select(file => file.Basename).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    [RequiresPostgres]
    public async Task VideoDeletionCompletesPostCommitWorkAfterAnAmbiguousCommit()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var commitAmbiguity = new CommitAmbiguityInterceptor();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IBlobReferenceCoordinator, BlobReferenceCoordinator>();
        services.AddScoped<BlobReferenceTransactionCoordinator>();
        services.AddScoped<BlobReferenceSaveChangesInterceptor>();
        services.AddDbContext<CoveContext>((provider, options) =>
            database.Configure(options)
                .ReplaceService<IExecutionStrategyFactory, TestRetryingExecutionStrategyFactory>()
                .AddInterceptors(
                    provider.GetRequiredService<BlobReferenceSaveChangesInterceptor>(),
                    commitAmbiguity));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var video = new Video { Title = "Video with artwork", ImageBlobId = "video-artwork" };
        db.Videos.Add(video);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
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

        Assert.Empty(await db.Videos.IgnoreQueryFilters().ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("video-artwork", blobs.DeletedBlobIds);
        Assert.Equal([video.Id], deletedEventIds);
        Assert.Equal(1, commitAmbiguity.FailuresRaised);
        Assert.Empty(await db.VideoDeletionCommitMarkers.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    [RequiresPostgres]
    public async Task RolledBackVideoDeletionDoesNotUseAConcurrentRootDeletionAsCommitProof()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var preCommitFailure = new PreCommitFailureInterceptor();
        var retryingOptions = database.CreateOptions(builder => builder
            .ReplaceService<IExecutionStrategyFactory, TestRetryingExecutionStrategyFactory>()
            .AddInterceptors(preCommitFailure));
        var ordinaryOptions = database.CreateOptions();
        await using var db = new CoveContext(retryingOptions);
        var root = new Video { Title = "Deletion attempt that rolls back" };
        var child = new Video { Title = "Reparented during retry", ParentVideo = root };
        db.AddRange(root, child);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
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
        Assert.False(await db.Videos.IgnoreQueryFilters().AnyAsync(video => video.Id == root.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await db.Videos.IgnoreQueryFilters().AnyAsync(video => video.Id == child.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(thumbnails.DeletedVideoIds);
        Assert.Empty(deletedEventIds);
        Assert.Empty(await db.VideoDeletionCommitMarkers.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    [RequiresPostgres]
    public async Task VideoDeletionJobCollapsesSelectedDescendantsIntoTheirSelectedRoot()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
        var root = new Video { Title = "Selected parent" };
        var child = new Video { Title = "Selected child", ParentVideo = root };
        var grandchild = new Video { Title = "Selected grandchild", ParentVideo = child };
        var independent = new Video { Title = "Independent selection" };
        db.AddRange(root, child, grandchild, independent);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var collapsed = await BulkDeletionJobService.CollapseSelectedVideoDescendantsAsync(
            db,
            [root.Id, child.Id, grandchild.Id, independent.Id],
            CancellationToken.None);

        Assert.Equal(new[] { root.Id, independent.Id }.Order(), collapsed.Order());
    }

    [Fact]
    [RequiresPostgres]
    public async Task GalleryDeletionRemovesFileRowsAndOnlyCleansUnsharedBlobs()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
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
        Assert.False(await db.Galleries.AnyAsync(item => item.Id == gallery.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await db.GalleryFiles.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(["gallery-only-blob"], blobs.DeletedBlobIds);
        Assert.Equal(["gallery-only-blob"], thumbnails.DeletedBlobIds);
    }

    [Fact]
    [RequiresPostgres]
    public async Task TagDeletionInvalidatesAffectedVideoSegmentCachesAfterCommit()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
        var tag = new Tag { Name = "Timeline tag" };
        db.Tags.Add(tag);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.Segments.Add(new Segment
        {
            HostType = SegmentHostType.Video,
            HostId = 314,
            StartSec = 0,
            TagId = tag.Id,
            SourceKey = "test",
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
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

    [Fact]
    [RequiresPostgres]
    public async Task TagDeletionReturnsAConflictBeforeRemovingATagWithRestrictedExtensionReferences()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
        var tag = new Tag { Name = "Extension-referenced tag" };
        db.Tags.Add(tag);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var customFields = new CustomFieldService(db);
        var thumbnails = new RecordingThumbnailService();
        var blobs = new ReferenceAwareBlobService(db);
        var service = new BulkEntityDeletionService(
            db,
            customFields,
            new ImageDeletionService(db, customFields, thumbnails, blobService: blobs),
            thumbnails,
            blobs,
            new EventBus(),
            tagExternalReferenceInspector: new StubTagExternalReferenceInspector(
            [
                new TagExternalReferenceDto(
                    tag.Id,
                    "foreign-key-fixture",
                    "public",
                    "extension_items",
                    "tag_id",
                    "restrict",
                    84)
                {
                    ExtensionId = "com.example.segment-studio",
                    ExtensionName = "Segment Studio",
                },
                new TagExternalReferenceDto(
                    tag.Id,
                    "second-foreign-key-fixture",
                    "public",
                    "extension_items_two",
                    "tag_id",
                    "restrict",
                    2)
                {
                    ExtensionId = "com.example.library-helper",
                    ExtensionName = "Library Helper",
                },
                new TagExternalReferenceDto(
                    tag.Id,
                    "duplicate-owner-foreign-key-fixture",
                    "public",
                    "extension_items_three",
                    "tag_id",
                    "restrict",
                    3)
                {
                    ExtensionId = "com.example.segment-studio",
                    ExtensionName = "Segment Studio",
                },
                new TagExternalReferenceDto(
                    tag.Id,
                    "unknown-foreign-key-fixture",
                    "public",
                    "unmapped_extension_items",
                    "tag_id",
                    "restrict",
                    1),
            ]));
        var controller = new Cove.Api.Controllers.TagsController(
            null!,
            db,
            customFields,
            null!,
            bulkEntityDeletionService: service);

        var result = await controller.Delete(tag.Id, CancellationToken.None);

        var conflict = Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(result);
        var response = System.Text.Json.JsonSerializer.Serialize(conflict.Value);
        using var responseDocument = System.Text.Json.JsonDocument.Parse(response);
        var responseRoot = responseDocument.RootElement;
        Assert.Equal("TAG_DELETE_EXTENSION_REFERENCES", responseRoot.GetProperty("code").GetString());
        Assert.Contains(
            "• Library Helper\n• Segment Studio\n• An unidentified extension",
            responseRoot.GetProperty("message").GetString(),
            StringComparison.Ordinal);
        Assert.True(responseRoot.GetProperty("HasUnknownExtensionOwners").GetBoolean());
        Assert.Contains(
            responseRoot.GetProperty("Extensions").EnumerateArray(),
            extension => extension.GetProperty("Id").GetString() == "com.example.segment-studio"
                && extension.GetProperty("Name").GetString() == "Segment Studio");
        Assert.Equal(2, responseRoot.GetProperty("Extensions").GetArrayLength());
        Assert.DoesNotContain("extension_items", response, StringComparison.Ordinal);
        Assert.True(await db.Tags.AnyAsync(item => item.Id == tag.Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TagReferenceOwnerResolutionRequiresAUniqueMappedExtensionAssembly()
    {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.Entity<OwnerResolverFixture>().ToTable("owned_fixture", "extension_schema");
        var model = modelBuilder.FinalizeModel();
        var first = new OwnerResolverDataExtension("com.example.first", "First Extension");

        var unique = PostgresTagExternalReferenceInspector.ResolveExtensionOwner(
            model,
            [first],
            "extension_schema",
            "owned_fixture");
        var ambiguous = PostgresTagExternalReferenceInspector.ResolveExtensionOwner(
            model,
            [first, new OwnerResolverDataExtension("com.example.second", "Second Extension")],
            "extension_schema",
            "owned_fixture");
        var unmapped = PostgresTagExternalReferenceInspector.ResolveExtensionOwner(
            model,
            [first],
            "extension_schema",
            "other_fixture");

        Assert.Equal(("com.example.first", "First Extension"), unique);
        Assert.Null(ambiguous);
        Assert.Null(unmapped);
    }

    [Theory]
    [InlineData(BulkDeletionEntityKind.Studio)]
    [InlineData(BulkDeletionEntityKind.Performer)]
    [InlineData(BulkDeletionEntityKind.Tag)]
    [InlineData(BulkDeletionEntityKind.Gallery)]
    [InlineData(BulkDeletionEntityKind.Group)]
    [InlineData(BulkDeletionEntityKind.Audio)]
    [InlineData(BulkDeletionEntityKind.Text)]
    [InlineData(BulkDeletionEntityKind.Image)]
    [RequiresPostgres]
    public async Task DeletionThatLosesARaceReportsNotFoundAndLeavesTheContextUsable(BulkDeletionEntityKind kind)
    {
        var concurrentDelete = new ConcurrentRowDeleteInterceptor();
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database, saveChangesInterceptor: concurrentDelete);
        var folder = new Folder { Path = "/race", ModTime = DateTime.UtcNow };
        var lost = CreateRaceTarget(kind, "lost", folder);
        var next = CreateRaceTarget(kind, "next", folder);
        db.AddRange(lost, next);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var (service, blobs) = CreateDeletionService(db);
        var context = new BulkDeletionExecutionContext();
        concurrentDelete.Arm(RaceTargetTable(kind), lost.Id);

        Assert.False(await service.DeleteAsync(
            kind,
            lost.Id,
            context,
            deleteFiles: true,
            deleteGenerated: true,
            CancellationToken.None,
            publishEvent: false));
        // Clean deletes a whole batch through one context, so the lost race must not fail the next deletion.
        Assert.True(await service.DeleteAsync(
            kind,
            next.Id,
            context,
            deleteFiles: true,
            deleteGenerated: true,
            CancellationToken.None,
            publishEvent: false));

        Assert.False(await RaceTargetExistsAsync(db, kind, lost.Id));
        Assert.False(await RaceTargetExistsAsync(db, kind, next.Id));
        Assert.Equal(kind == BulkDeletionEntityKind.Image ? [] : ["lost-artwork", "next-artwork"], blobs.DeletedBlobIds);
        // The winner owns the lost entity's file deletion; only the second deletion stages its own file.
        string[] expectedFiles = kind is BulkDeletionEntityKind.Audio or BulkDeletionEntityKind.Text or BulkDeletionEntityKind.Image
            ? ["/race/next"]
            : [];
        Assert.Equal(expectedFiles, context.GetPhysicalFiles());
        Assert.Equal(expectedFiles, await db.PendingPhysicalFileDeletions.Select(item => item.Path).ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    private static BaseEntity CreateRaceTarget(BulkDeletionEntityKind kind, string name, Folder folder) => kind switch
    {
        BulkDeletionEntityKind.Studio => new Studio { Name = name, ImageBlobId = $"{name}-artwork" },
        BulkDeletionEntityKind.Performer => new Performer { Name = name, ImageBlobId = $"{name}-artwork" },
        BulkDeletionEntityKind.Tag => new Tag { Name = name, ImageBlobId = $"{name}-artwork" },
        BulkDeletionEntityKind.Gallery => new Gallery { Title = name, ImageBlobId = $"{name}-artwork" },
        BulkDeletionEntityKind.Group => new Group { Name = name, FrontImageBlobId = $"{name}-artwork" },
        BulkDeletionEntityKind.Audio => new Audio
        {
            Title = name,
            ImageBlobId = $"{name}-artwork",
            Files = [new AudioFile { Basename = name, ParentFolder = folder, Path = $"/race/{name}", ModTime = DateTime.UtcNow }],
        },
        BulkDeletionEntityKind.Text => new TextDocument
        {
            Title = name,
            ImageBlobId = $"{name}-artwork",
            Files = [new TextFile { Basename = name, ParentFolder = folder, Path = $"/race/{name}", ModTime = DateTime.UtcNow }],
        },
        BulkDeletionEntityKind.Image => new Image
        {
            Title = name,
            Files = [new ImageFile { Basename = name, ParentFolder = folder, Path = $"/race/{name}", ModTime = DateTime.UtcNow }],
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static string RaceTargetTable(BulkDeletionEntityKind kind) => kind switch
    {
        BulkDeletionEntityKind.Studio => "studios",
        BulkDeletionEntityKind.Performer => "performers",
        BulkDeletionEntityKind.Tag => "tags",
        BulkDeletionEntityKind.Gallery => "galleries",
        BulkDeletionEntityKind.Group => "groups",
        BulkDeletionEntityKind.Audio => "audios",
        BulkDeletionEntityKind.Text => "text_documents",
        BulkDeletionEntityKind.Image => "images",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static Task<bool> RaceTargetExistsAsync(CoveContext db, BulkDeletionEntityKind kind, int id) => kind switch
    {
        BulkDeletionEntityKind.Studio => db.Studios.AsNoTracking().AnyAsync(item => item.Id == id),
        BulkDeletionEntityKind.Performer => db.Performers.AsNoTracking().AnyAsync(item => item.Id == id),
        BulkDeletionEntityKind.Tag => db.Tags.AsNoTracking().AnyAsync(item => item.Id == id),
        BulkDeletionEntityKind.Gallery => db.Galleries.AsNoTracking().AnyAsync(item => item.Id == id),
        BulkDeletionEntityKind.Group => db.Groups.AsNoTracking().AnyAsync(item => item.Id == id),
        BulkDeletionEntityKind.Audio => db.Audios.AsNoTracking().AnyAsync(item => item.Id == id),
        BulkDeletionEntityKind.Text => db.TextDocuments.AsNoTracking().AnyAsync(item => item.Id == id),
        BulkDeletionEntityKind.Image => db.Images.AsNoTracking().AnyAsync(item => item.Id == id),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    [Fact]
    [RequiresPostgres]
    public async Task DeletionWhoseFollowUpCountSaveConflictsStillReportsTheCommittedDelete()
    {
        var concurrentDelete = new ConcurrentRowDeleteInterceptor();
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database, saveChangesInterceptor: concurrentDelete);
        var tag = new Tag { Name = "Counted" };
        var performer = new Performer { Name = "Target", ImageBlobId = "followup-artwork" };
        performer.PerformerTags.Add(new PerformerTag { Tag = tag });
        var next = new Performer { Name = "Next" };
        db.AddRange(performer, next);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        // A stale count guarantees the follow-up save writes the tag row.
        await db.Database.ExecuteSqlAsync($"UPDATE tags SET \"PerformerCount\" = 5 WHERE \"Id\" = {tag.Id}", cancellationToken: TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var eventBus = new EventBus();
        var deletedEventIds = new List<int>();
        using var subscription = eventBus.Subscribe<EntityEvent>(evt =>
        {
            if (evt.Type == EventType.PerformerDeleted)
                deletedEventIds.Add(evt.EntityId);
        });
        var (service, blobs) = CreateDeletionService(db, eventBus);
        // The performer delete commits; the competing request removes the tag whose count the
        // follow-up save refreshes.
        concurrentDelete.Arm("tags", tag.Id, beforeSave: 2);

        Assert.True(await service.DeleteAsync(
            BulkDeletionEntityKind.Performer,
            performer.Id,
            new BulkDeletionExecutionContext(),
            deleteFiles: false,
            deleteGenerated: true,
            CancellationToken.None));

        // The conflicting count update must not be replayed by the next deletion through this context.
        Assert.True(await service.DeleteAsync(
            BulkDeletionEntityKind.Performer,
            next.Id,
            new BulkDeletionExecutionContext(),
            deleteFiles: false,
            deleteGenerated: true,
            CancellationToken.None));

        Assert.False(await db.Performers.AsNoTracking().AnyAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal([performer.Id, next.Id], deletedEventIds);
        Assert.Equal(["followup-artwork"], blobs.DeletedBlobIds);
    }

    [Theory]
    [InlineData(BulkDeletionEntityKind.Studio)]
    [InlineData(BulkDeletionEntityKind.Performer)]
    [InlineData(BulkDeletionEntityKind.Tag)]
    [InlineData(BulkDeletionEntityKind.Gallery)]
    [InlineData(BulkDeletionEntityKind.Group)]
    [InlineData(BulkDeletionEntityKind.Audio)]
    [InlineData(BulkDeletionEntityKind.Text)]
    [InlineData(BulkDeletionEntityKind.Image)]
    [RequiresPostgres]
    public async Task DeletionRetriesWhenAStagedTagApplicationDisappears(BulkDeletionEntityKind kind)
    {
        var concurrentDelete = new ConcurrentRowDeleteInterceptor();
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database, saveChangesInterceptor: concurrentDelete);
        var target = CreateRaceTarget(kind, "target", new Folder { Path = "/race", ModTime = DateTime.UtcNow });
        var applied = new Tag { Name = "Applied" };
        db.AddRange(target, applied);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var application = new TagApplication { HostType = Enum.Parse<AffinityHostType>(kind.ToString()), HostId = target.Id, TagId = applied.Id };
        db.TagApplications.Add(application);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var eventBus = new EventBus();
        var deletedEvents = new List<EntityEvent>();
        using var subscription = eventBus.Subscribe<EntityEvent>(evt =>
        {
            if (evt.Type.ToString().EndsWith("Deleted", StringComparison.Ordinal))
                deletedEvents.Add(evt);
        });
        var (service, blobs) = CreateDeletionService(db, eventBus);
        var context = new BulkDeletionExecutionContext();
        // Only a staged dependency disappears, as when a tagger removes it; the entity itself still exists.
        concurrentDelete.Arm("tag_applications", application.Id);

        Assert.True(await service.DeleteAsync(
            kind,
            target.Id,
            context,
            deleteFiles: true,
            deleteGenerated: true,
            CancellationToken.None));

        Assert.False(await RaceTargetExistsAsync(db, kind, target.Id));
        Assert.Empty(await db.TagApplications.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        // The retried deletion publishes its event once.
        Assert.Equal([target.Id], deletedEvents.Select(evt => evt.EntityId));
        Assert.Equal(kind == BulkDeletionEntityKind.Image ? [] : ["target-artwork"], blobs.DeletedBlobIds);
        // The retry stages the file once; the first attempt's staged deletion rolled back.
        string[] expectedFiles = kind is BulkDeletionEntityKind.Audio or BulkDeletionEntityKind.Text or BulkDeletionEntityKind.Image
            ? ["/race/target"]
            : [];
        Assert.Equal(expectedFiles, context.GetPhysicalFiles());
        Assert.Equal(expectedFiles, await db.PendingPhysicalFileDeletions.Select(item => item.Path).ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(BulkDeletionEntityKind.Audio)]
    [InlineData(BulkDeletionEntityKind.Text)]
    [InlineData(BulkDeletionEntityKind.Image)]
    [RequiresPostgres]
    public async Task DeletionRetriesWhenAScanRemovesItsFileRowFirst(BulkDeletionEntityKind kind)
    {
        var concurrentDelete = new ConcurrentRowDeleteInterceptor();
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database, saveChangesInterceptor: concurrentDelete);
        var target = CreateRaceTarget(kind, "target", new Folder { Path = "/race", ModTime = DateTime.UtcNow });
        db.Add(target);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var fileId = await db.Set<BaseFileEntity>().Select(file => file.Id).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var (service, _) = CreateDeletionService(db);
        var context = new BulkDeletionExecutionContext();
        concurrentDelete.Arm("files", fileId);

        Assert.True(await service.DeleteAsync(
            kind,
            target.Id,
            context,
            deleteFiles: true,
            deleteGenerated: true,
            CancellationToken.None,
            publishEvent: false));

        Assert.False(await RaceTargetExistsAsync(db, kind, target.Id));
        // The retry no longer sees the removed file row, so it leaves that path to whatever removed the row.
        Assert.Empty(context.GetPhysicalFiles());
        Assert.Empty(await db.PendingPhysicalFileDeletions.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    [RequiresPostgres]
    public async Task StudioDeletionRethrowsWhenItsDependentsKeepChanging()
    {
        var concurrentDelete = new ConcurrentRowDeleteInterceptor();
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database, saveChangesInterceptor: concurrentDelete);
        var tag = new Tag { Name = "Applied" };
        var studio = new Studio { Name = "Survivor" };
        var next = new Studio { Name = "Next" };
        db.AddRange(tag, studio, next);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var first = new TagApplication { HostType = AffinityHostType.Studio, HostId = studio.Id, TagId = tag.Id, SourceKey = "first" };
        var second = new TagApplication { HostType = AffinityHostType.Studio, HostId = studio.Id, TagId = tag.Id, SourceKey = "second" };
        db.TagApplications.AddRange(first, second);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var (service, _) = CreateDeletionService(db);
        // A staged dependency disappears before the first attempt and another before the retry.
        concurrentDelete.Arm("tag_applications", first.Id);
        concurrentDelete.Arm("tag_applications", second.Id);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.DeleteAsync(
            BulkDeletionEntityKind.Studio,
            studio.Id,
            new BulkDeletionExecutionContext(),
            deleteFiles: false,
            deleteGenerated: true,
            CancellationToken.None,
            publishEvent: false));
        // Both queued removals ran, so the second conflict came from the one retry.
        Assert.Empty(await db.TagApplications.AsNoTracking().ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        // The rejected delete must not be replayed by the next deletion through this context.
        Assert.True(await service.DeleteAsync(
            BulkDeletionEntityKind.Studio,
            next.Id,
            new BulkDeletionExecutionContext(),
            deleteFiles: false,
            deleteGenerated: true,
            CancellationToken.None,
            publishEvent: false));

        Assert.Equal([studio.Id], await db.Studios.AsNoTracking().Select(item => item.Id).ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    private static (BulkEntityDeletionService Service, ReferenceAwareBlobService Blobs) CreateDeletionService(
        CoveContext db,
        EventBus? eventBus = null)
    {
        var customFields = new CustomFieldService(db);
        var thumbnails = new RecordingThumbnailService();
        var blobs = new ReferenceAwareBlobService(db);
        var service = new BulkEntityDeletionService(
            db,
            customFields,
            new ImageDeletionService(db, customFields, thumbnails, blobService: blobs),
            thumbnails,
            blobs,
            eventBus ?? new EventBus());
        return (service, blobs);
    }

    [Theory]
    [InlineData(PostgresErrorCodes.RestrictViolation)]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation)]
    [RequiresPostgres]
    public async Task TagDeletionMapsLatePostgresReferenceFailuresToASafeConflict(string sqlState)
    {
        var saveFailure = new PostgresSaveFailureInterceptor(sqlState);
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database, saveChangesInterceptor: saveFailure);
        var tag = new Tag { Name = "Late extension reference" };
        db.Tags.Add(tag);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var customFields = new CustomFieldService(db);
        var thumbnails = new RecordingThumbnailService();
        var blobs = new ReferenceAwareBlobService(db);
        var service = new BulkEntityDeletionService(
            db,
            customFields,
            new ImageDeletionService(db, customFields, thumbnails, blobService: blobs),
            thumbnails,
            blobs,
            new EventBus(),
            tagExternalReferenceInspector: new StubTagExternalReferenceInspector([]));
        var controller = new Cove.Api.Controllers.TagsController(
            null!,
            db,
            customFields,
            null!,
            bulkEntityDeletionService: service);
        saveFailure.Arm();

        var result = await controller.Delete(tag.Id, CancellationToken.None);

        var conflict = Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(result);
        var response = System.Text.Json.JsonSerializer.Serialize(conflict.Value);
        Assert.Contains("TAG_DELETE_EXTENSION_REFERENCES", response, StringComparison.Ordinal);
        Assert.DoesNotContain(PostgresSaveFailureInterceptor.SensitiveDetail, response, StringComparison.Ordinal);
        db.ChangeTracker.Clear();
        Assert.True(await db.Tags.AnyAsync(item => item.Id == tag.Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    [RequiresPostgres]
    public async Task TagDeletionDoesNotHideAnUnrelatedPostgresFailure()
    {
        var saveFailure = new PostgresSaveFailureInterceptor(PostgresErrorCodes.SerializationFailure);
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database, saveChangesInterceptor: saveFailure);
        var tag = new Tag { Name = "Unrelated database failure" };
        db.Tags.Add(tag);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var customFields = new CustomFieldService(db);
        var thumbnails = new RecordingThumbnailService();
        var blobs = new ReferenceAwareBlobService(db);
        var service = new BulkEntityDeletionService(
            db,
            customFields,
            new ImageDeletionService(db, customFields, thumbnails, blobService: blobs),
            thumbnails,
            blobs,
            new EventBus(),
            tagExternalReferenceInspector: new StubTagExternalReferenceInspector([]));
        saveFailure.Arm();

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => service.DeleteAsync(
            BulkDeletionEntityKind.Tag,
            tag.Id,
            new BulkDeletionExecutionContext(),
            deleteFiles: false,
            deleteGenerated: true,
            CancellationToken.None));

        Assert.IsType<PostgresException>(exception.InnerException);
        db.ChangeTracker.Clear();
        Assert.True(await db.Tags.AnyAsync(item => item.Id == tag.Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    [RequiresPostgres]
    public async Task TagDeletionAllowsExtensionReferencesWithDatabaseManagedCleanup()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
        var tag = new Tag { Name = "Cascade-referenced tag" };
        db.Tags.Add(tag);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var customFields = new CustomFieldService(db);
        var thumbnails = new RecordingThumbnailService();
        var blobs = new ReferenceAwareBlobService(db);
        var service = new BulkEntityDeletionService(
            db,
            customFields,
            new ImageDeletionService(db, customFields, thumbnails, blobService: blobs),
            thumbnails,
            blobs,
            new EventBus(),
            tagExternalReferenceInspector: new StubTagExternalReferenceInspector(
            [
                new TagExternalReferenceDto(
                    tag.Id,
                    "foreign-key-fixture",
                    "public",
                    "extension_items",
                    "tag_id",
                    "cascade",
                    1),
            ]));

        Assert.True(await service.DeleteAsync(
            BulkDeletionEntityKind.Tag,
            tag.Id,
            new BulkDeletionExecutionContext(),
            deleteFiles: false,
            deleteGenerated: true,
            CancellationToken.None));
        Assert.False(await db.Tags.AnyAsync(item => item.Id == tag.Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    [RequiresPostgres]
    public async Task MissingTagDeletionSkipsExternalReferenceInspection()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
        var customFields = new CustomFieldService(db);
        var thumbnails = new RecordingThumbnailService();
        var blobs = new ReferenceAwareBlobService(db);
        var inspector = new StubTagExternalReferenceInspector(
        [
            new TagExternalReferenceDto(
                999,
                "foreign-key-fixture",
                "public",
                "extension_items",
                "tag_id",
                "restrict",
                null,
                TagExternalReferenceAccessLimitations.DatabasePermission),
        ]);
        var service = new BulkEntityDeletionService(
            db,
            customFields,
            new ImageDeletionService(db, customFields, thumbnails, blobService: blobs),
            thumbnails,
            blobs,
            new EventBus(),
            tagExternalReferenceInspector: inspector);

        Assert.False(await service.DeleteAsync(
            BulkDeletionEntityKind.Tag,
            999,
            new BulkDeletionExecutionContext(),
            deleteFiles: false,
            deleteGenerated: true,
            CancellationToken.None));
        Assert.Equal(0, inspector.InspectionCount);
    }

    [Fact]
    [RequiresPostgres]
    public async Task UninspectableTagReferencesExplainHowToRestoreAccess()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = CreateContext(database);
        var tag = new Tag { Name = "Uninspectable extension reference" };
        db.Tags.Add(tag);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var customFields = new CustomFieldService(db);
        var thumbnails = new RecordingThumbnailService();
        var blobs = new ReferenceAwareBlobService(db);
        var service = new BulkEntityDeletionService(
            db,
            customFields,
            new ImageDeletionService(db, customFields, thumbnails, blobService: blobs),
            thumbnails,
            blobs,
            new EventBus(),
            tagExternalReferenceInspector: new StubTagExternalReferenceInspector(
            [
                new TagExternalReferenceDto(
                    tag.Id,
                    "foreign-key-fixture",
                    "public",
                    "extension_items",
                    "tag_id",
                    "no action",
                    null,
                    TagExternalReferenceAccessLimitations.RowLevelSecurity)
                {
                    ExtensionId = "com.example.private-data",
                    ExtensionName = "Private Data",
                },
            ]));

        var exception = await Assert.ThrowsAsync<TagDeletionBlockedException>(() => service.DeleteAsync(
            BulkDeletionEntityKind.Tag,
            tag.Id,
            new BulkDeletionExecutionContext(),
            deleteFiles: false,
            deleteGenerated: true,
            CancellationToken.None));

        Assert.True(exception.HasUninspectableReferences);
        Assert.Contains("Restore access", exception.Message, StringComparison.Ordinal);
        Assert.Contains("• Private Data", exception.Message, StringComparison.Ordinal);
        Assert.True(await db.Tags.AnyAsync(item => item.Id == tag.Id, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static CoveContext CreateContext(
        CovePostgresDatabase database,
        ICurrentPrincipalAccessor? principalAccessor = null,
        SaveChangesInterceptor? saveChangesInterceptor = null)
    {
        var options = database.CreateOptions(builder =>
        {
            if (saveChangesInterceptor is not null)
                builder.AddInterceptors(saveChangesInterceptor);
        });
        return new CoveContext(options, principalAccessor);
    }

    private sealed class ConcurrentRowDeleteInterceptor : SaveChangesInterceptor
    {
        private readonly Queue<(string Table, int Id)?> _deletes = new();

        // Each call queues one deletion for a later SaveChanges call. beforeSave counts those calls from 1 after
        // any deletion already queued, so 2 targets CoveContext's follow-up derived-count save.
        public void Arm(string table, int id, int beforeSave = 1)
        {
            for (var save = 1; save < beforeSave; save++)
                _deletes.Enqueue(null);
            _deletes.Enqueue((table, id));
        }

        // Removes the row after the deletion service loaded it, as a competing request would.
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_deletes.TryDequeue(out var next) && next is { } target)
            {
#pragma warning disable EF1002 // The table name comes from the test itself, not from input.
                await eventData.Context!.Database.ExecuteSqlRawAsync($"DELETE FROM {target.Table} WHERE \"Id\" = {{0}}", [target.Id], cancellationToken);
#pragma warning restore EF1002
            }
            return result;
        }
    }

    private sealed class PostgresSaveFailureInterceptor(string sqlState) : SaveChangesInterceptor
    {
        public const string SensitiveDetail = "foreign key fixture detail";
        private bool _armed;

        public void Arm() => _armed = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_armed)
                return ValueTask.FromResult(result);

            _armed = false;
            throw new DbUpdateException(
                "Injected save failure",
                new PostgresException(SensitiveDetail, "ERROR", "ERROR", sqlState));
        }
    }

    private sealed class OwnerResolverFixture
    {
        public int Id { get; set; }
    }

    private sealed class OwnerResolverDataExtension(string id, string name) : IDataExtension
    {
        public string Id => id;
        public string Name => name;
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;
        public void ConfigureServices(IServiceCollection services, ExtensionContext context) { }
        public void ConfigureModel(ModelBuilder modelBuilder) { }
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

    private sealed class StubTagExternalReferenceInspector(IReadOnlyList<TagExternalReferenceDto> references)
        : ITagExternalReferenceInspector
    {
        public int InspectionCount { get; private set; }

        public Task<IReadOnlyList<TagExternalReferenceDto>> InspectAsync(
            IReadOnlyCollection<int> tagIds,
            CancellationToken ct = default)
        {
            InspectionCount++;
            return Task.FromResult(references);
        }

        public Task ApplyResolutionsAsync(
            int targetTagId,
            IReadOnlyCollection<TagExternalReferenceResolutionDto> resolutions,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    internal sealed class ReferenceAwareBlobService(CoveContext db) : IBlobService
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

    internal sealed class RecordingThumbnailService : IThumbnailService
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
        public Task GenerateVideoSpriteAsync(int videoId, CancellationToken ct = default) => throw new NotSupportedException();
        public string GetThumbnailPathForVideo(int videoId) => throw new NotSupportedException();
        public string GetTimestampedThumbnailPath(int videoId, double seconds) => throw new NotSupportedException();
        public string GetPreviewPath(int videoId) => throw new NotSupportedException();
        public string GetSpritePath(int videoId) => throw new NotSupportedException();
        public string GetSpriteVttPath(int videoId) => throw new NotSupportedException();
        public string StartGenerateAllThumbnails() => throw new NotSupportedException();
    }
}
