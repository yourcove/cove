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
    public async Task AssignFilePublishesUpdatesForPreviousAndNewOwners()
    {
        var (db, principal) = CreateContext();
        await using (db)
        {
            var previousOwner = new Video { Title = "Previous owner" };
            var newOwner = new Video { Title = "New owner" };
            var folder = new Folder { Path = "/library" };
            var file = new VideoFile { Basename = "video.mp4", ParentFolder = folder, Video = previousOwner };
            db.AddRange(previousOwner, newOwner, folder, file);
            await db.SaveChangesAsync();

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
            await db.SaveChangesAsync();

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
            await db.SaveChangesAsync();

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
            await db.SaveChangesAsync();

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
