using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Cove.Tests;

public class VideoMutationEventTests
{
    [Fact]
    public async Task BulkRelationshipUpdateTouchesParentOnlyWhenCollectionChanges()
    {
        var (db, principal) = CreateContext();
        await using (db)
        {
            var video = new Video
            {
                Title = "Bulk relationship update",
                VideoTags = [new VideoTag { TagId = 10 }],
            };
            db.Videos.Add(video);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var originalUpdatedAt = DateTime.UtcNow.AddDays(-1);
            video.UpdatedAt = originalUpdatedAt;
            db.Entry(video).Property(item => item.UpdatedAt).IsModified = false;
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var controller = CreateController(db, principal, new EventBus(), cache);
            var update = new BulkVideoUpdateDto
            {
                Ids = [video.Id],
                TagIds = [10],
                TagMode = BulkUpdateMode.Remove,
            };

            await controller.BulkUpdate(update, TestContext.Current.CancellationToken);

            Assert.Empty(video.VideoTags);
            await db.Entry(video).ReloadAsync(TestContext.Current.CancellationToken);
            Assert.True(video.UpdatedAt > originalUpdatedAt);
            var changedUpdatedAt = video.UpdatedAt;

            await controller.BulkUpdate(update, TestContext.Current.CancellationToken);

            await db.Entry(video).ReloadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(changedUpdatedAt, video.UpdatedAt);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SplitUsesExplicitMetadataAndRetainsLegacyDefaults(bool explicitMetadata, bool populated)
    {
        var (db, principal) = CreateContext();
        await using (db)
        {
            var source = new Video { Title = "Source", Code = "Old", Details = "Old details", Director = "Old director", Organized = true, IsVr = true };
            var folder = new Folder { Path = "/library" };
            var primary = new VideoFile { Basename = "primary.mp4", ParentFolder = folder, Video = source };
            var file = new VideoFile { Basename = "secondary.mp4", ParentFolder = folder, Video = source };
            source.PrimaryFile = primary;
            db.AddRange(source, folder, primary, file);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var sourceId = source.Id;
            var fileId = file.Id;
            var primaryId = primary.Id;
            var bus = new EventBus();
            var events = new List<EntityEvent>();
            using var subscription = bus.Subscribe<EntityEvent>(events.Add);
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var controller = CreateController(db, principal, bus, cache);
            var metadata = explicitMetadata ? (populated
                ? new VideoSplitMetadataDto(Title: "New", Code: "New code", Details: "New details", Director: "New director", Date: "2024-03", Urls: ["https://example.com/scene"])
                : new VideoSplitMetadataDto()) : null;

            var result = await controller.SplitFile(sourceId, new VideoSplitFileDto(fileId, Metadata: metadata), TestContext.Current.CancellationToken);

            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
            var created = await db.Videos.Include(video => video.Urls).SingleAsync(video => video.Id != sourceId, TestContext.Current.CancellationToken);
            Assert.Equal(explicitMetadata ? (populated ? "New" : null) : "Source", created.Title);
            Assert.Equal(explicitMetadata ? (populated ? "New code" : null) : "Old", created.Code);
            Assert.Equal(explicitMetadata ? (populated ? "New details" : null) : "Old details", created.Details);
            Assert.Equal(explicitMetadata ? (populated ? "New director" : null) : "Old director", created.Director);
            Assert.Equal(!explicitMetadata, created.Organized);
            Assert.Equal(!explicitMetadata, created.IsVr);
            Assert.Equal(fileId, created.PrimaryFileId);
            Assert.Equal(created.Id, (await db.VideoFiles.SingleAsync(item => item.Id == fileId)).VideoId);
            Assert.Equal(primaryId, (await db.Videos.SingleAsync(item => item.Id == sourceId)).PrimaryFileId);
            Assert.Equal(populated ? 1 : 0, created.Urls.Count);
            Assert.Equal(2, events.Count);
        }
    }

    [Fact]
    public async Task SplitRejectsPrimaryAndMissingFilesWithoutCreatingVideo()
    {
        var (db, principal) = CreateContext();
        await using (db)
        {
            var source = new Video { Title = "Source" };
            var primary = new VideoFile { Basename = "primary.mp4", ParentFolder = new Folder { Path = "/library" }, Video = source };
            source.PrimaryFile = primary;
            db.AddRange(source, primary);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var sourceId = source.Id;
            var primaryId = primary.Id;
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var controller = CreateController(db, principal, new EventBus(), cache);
            Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(await controller.SplitFile(sourceId, new VideoSplitFileDto(primaryId, Metadata: new()), TestContext.Current.CancellationToken));
            Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>(await controller.SplitFile(sourceId, new VideoSplitFileDto(int.MaxValue, Metadata: new()), TestContext.Current.CancellationToken));
            Assert.Equal(1, await db.Videos.CountAsync());
        }
    }

    [Fact]
    public async Task AssignFilePublishesUpdatesForPreviousAndNewOwners()
    {
        var (db, principal) = CreateContext();
        await using (db)
        {
            var previousOwner = new Video { Title = "Previous owner" };
            var newOwner = new Video { Title = "New owner" };
            var folder = new Folder { Path = "/library" };
            var primaryFile = new VideoFile { Basename = "primary.mp4", ParentFolder = folder, Video = previousOwner };
            var file = new VideoFile { Basename = "video.mp4", ParentFolder = folder, Video = previousOwner };
            previousOwner.PrimaryFile = primaryFile;
            db.AddRange(previousOwner, newOwner, folder, primaryFile, file);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var eventBus = new EventBus();
            var published = new List<EntityEvent>();
            using var subscription = eventBus.Subscribe<EntityEvent>(published.Add);
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var controller = CreateController(db, principal, eventBus, cache);

            await controller.AssignFile(newOwner.Id, new VideoAssignFileDto(file.Id), CancellationToken.None);

            Assert.Equal(
                [previousOwner.Id, newOwner.Id],
                published.Where(evt => evt.Type == EventType.VideoUpdated).Select(evt => evt.EntityId).Order().ToArray());
        }
    }

    [Fact]
    public async Task SettingRatingPublishesOneLifecycleEvent()
    {
        var (db, principal) = CreateContext();
        await using (db)
        {
            var video = new Video { Title = "Rated video" };
            db.Videos.Add(video);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var eventBus = new EventBus();
            var published = new List<EntityEvent>();
            using var subscription = eventBus.Subscribe<EntityEvent>(published.Add);
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var controller = CreateController(db, principal, eventBus, cache);

            await controller.SetRating(video.Id, new VideoRatingDto(75), CancellationToken.None);

            var evt = Assert.Single(published);
            Assert.Equal(EventType.RatingCreated, evt.Type);
        }
    }

    [Fact]
    public async Task MergePublishesTargetUpdateAndSourceDeletes()
    {
        var (db, principal) = CreateContext();
        await using (db)
        {
            var target = new Video { Title = "Target" };
            var firstSource = new Video { Title = "First source" };
            var secondSource = new Video { Title = "Second source" };
            db.Videos.AddRange(target, firstSource, secondSource);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var eventBus = new EventBus();
            var published = new List<EntityEvent>();
            using var subscription = eventBus.Subscribe<EntityEvent>(published.Add);
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var controller = CreateController(db, principal, eventBus, cache);

            await controller.MergeVideos(
                new VideoMergeDto(target.Id, [firstSource.Id, secondSource.Id]),
                CancellationToken.None);

            Assert.Collection(
                published,
                evt =>
                {
                    Assert.Equal(EventType.VideoUpdated, evt.Type);
                    Assert.Equal(target.Id, evt.EntityId);
                },
                evt =>
                {
                    Assert.Equal(EventType.VideoDeleted, evt.Type);
                    Assert.Equal(firstSource.Id, evt.EntityId);
                },
                evt =>
                {
                    Assert.Equal(EventType.VideoDeleted, evt.Type);
                    Assert.Equal(secondSource.Id, evt.EntityId);
                });
        }
    }

    [Fact]
    public async Task MergeWithNoPersistedSourcesPublishesNothing()
    {
        var (db, principal) = CreateContext();
        await using (db)
        {
            var target = new Video { Title = "Target" };
            db.Videos.Add(target);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var eventBus = new EventBus();
            var published = new List<EntityEvent>();
            using var subscription = eventBus.Subscribe<EntityEvent>(published.Add);
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var controller = CreateController(db, principal, eventBus, cache);

            await controller.MergeVideos(
                new VideoMergeDto(target.Id, [target.Id, 999]),
                CancellationToken.None);

            Assert.Empty(published);
        }
    }

    private static VideosController CreateController(
        CoveContext db,
        CurrentPrincipalAccessor principal,
        IEventBus eventBus,
        IMemoryCache cache)
    {
        var engagement = new UserEngagementService(db, principal, eventBus);
        return new VideosController(
            new VideoRepository(db),
            db,
            null!,
            null!,
            null!,
            cache,
            null!,
            null!,
            engagement,
            new CustomFieldService(db),
            eventBus,
            null,
            principal);
    }

    private static (CoveContext Context, CurrentPrincipalAccessor Principal) CreateContext()
    {
        var principal = new CurrentPrincipalAccessor();
        principal.Set(new CovePrincipal
        {
            UserId = 7,
            Username = "event-test",
            Kind = PrincipalKind.User,
            Roles = new HashSet<string>(),
            Permissions = new HashSet<string> { Permissions.VideosRead, Permissions.VideosWrite },
        });
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return (new CoveContext(options, principal), principal);
    }
}
