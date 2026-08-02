using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class EntityMergeEventTests
{
    [Fact]
    public async Task GroupReorderPublishesOneUpdatePerPersistedGroup()
    {
        await using var db = CreateContext();
        var first = new Group { Name = "First" };
        var second = new Group { Name = "Second" };
        db.Groups.AddRange(first, second);
        await db.SaveChangesAsync();

        var events = new List<EntityEvent>();
        var eventBus = new EventBus();
        using var subscription = eventBus.Subscribe<EntityEvent>(events.Add);
        var controller = new GroupsController(
            new GroupRepository(db),
            db,
            new NoOpUserEngagementService(),
            eventBus: eventBus);

        await controller.Reorder(new GroupItemsReorderDto([second.Id, first.Id]), CancellationToken.None);

        Assert.Equal(
            [(EventType.GroupUpdated, second.Id), (EventType.GroupUpdated, first.Id)],
            events.Select(evt => (evt.Type, evt.EntityId)).ToArray());
    }

    [Fact]
    public async Task GalleryChapterCreatePublishesGalleryUpdate()
    {
        await using var db = CreateContext();
        var gallery = new Gallery { Title = "Gallery" };
        db.Galleries.Add(gallery);
        await db.SaveChangesAsync();

        var events = new List<EntityEvent>();
        var eventBus = new EventBus();
        using var subscription = eventBus.Subscribe<EntityEvent>(events.Add);
        var controller = new GalleriesController(
            new GalleryRepository(db),
            db,
            new NoOpUserEngagementService(),
            null!,
            eventBus: eventBus);

        await controller.CreateChapter(
            gallery.Id,
            new GalleryChapterCreateDto("Chapter", 0),
            CancellationToken.None);

        var publishedEvent = Assert.Single(events);
        Assert.Equal(EventType.GalleryUpdated, publishedEvent.Type);
        Assert.Equal(gallery.Id, publishedEvent.EntityId);
    }

    [Fact]
    public async Task PerformerMergePublishesSurvivorUpdateAndActualSourceDeletes()
    {
        await using var db = CreateContext();
        var target = new Performer { Name = "Target" };
        var source = new Performer { Name = "Source" };
        db.Performers.AddRange(target, source);
        await db.SaveChangesAsync();

        var events = new List<EntityEvent>();
        var eventBus = new EventBus();
        using var subscription = eventBus.Subscribe<EntityEvent>(events.Add);
        var service = new PerformerMergeService(db, eventBus);

        var result = await service.MergeAsync(target.Id, [source.Id, source.Id, 999], CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(
            [(EventType.PerformerUpdated, target.Id), (EventType.PerformerDeleted, source.Id)],
            events.Select(evt => (evt.Type, evt.EntityId)).ToArray());
    }

    [Fact]
    public async Task PerformerMergeWithNoPersistedSourcesPublishesNothing()
    {
        await using var db = CreateContext();
        var target = new Performer { Name = "Target" };
        db.Performers.Add(target);
        await db.SaveChangesAsync();

        var events = new List<EntityEvent>();
        var eventBus = new EventBus();
        using var subscription = eventBus.Subscribe<EntityEvent>(events.Add);
        var service = new PerformerMergeService(db, eventBus);

        var result = await service.MergeAsync(target.Id, [target.Id, 999], CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(events);
    }

    [Fact]
    public async Task StudioMergePublishesSurvivorUpdateAndActualSourceDeletes()
    {
        await using var db = CreateContext();
        var target = new Studio { Name = "Target" };
        var source = new Studio { Name = "Source" };
        db.Studios.AddRange(target, source);
        await db.SaveChangesAsync();

        var events = new List<EntityEvent>();
        var eventBus = new EventBus();
        using var subscription = eventBus.Subscribe<EntityEvent>(events.Add);
        var controller = new StudiosController(
            new StudioRepository(db),
            null!,
            db,
            new NoOpUserEngagementService(),
            eventBus: eventBus);

        await controller.MergeStudios(
            new StudioMergeDto(target.Id, [target.Id, source.Id, source.Id, 999]),
            CancellationToken.None);

        Assert.Equal(
            [(EventType.StudioUpdated, target.Id), (EventType.StudioDeleted, source.Id)],
            events.Select(evt => (evt.Type, evt.EntityId)).ToArray());
    }

    [Fact]
    public async Task TagMergePublishesSurvivorUpdateAndActualSourceDeletes()
    {
        await using var db = CreateContext();
        var target = new Tag { Name = "Target" };
        var source = new Tag { Name = "Source" };
        db.Tags.AddRange(target, source);
        await db.SaveChangesAsync();

        var events = new List<EntityEvent>();
        var eventBus = new EventBus();
        using var subscription = eventBus.Subscribe<EntityEvent>(events.Add);
        var controller = new TagsController(
            new TagRepository(db),
            db,
            new CustomFieldService(db),
            new NoOpUserEngagementService(),
            eventBus: eventBus);

        await controller.MergeTags(
            new TagMergeDto(target.Id, [target.Id, source.Id, source.Id, 999]),
            CancellationToken.None);

        Assert.Equal(
            [(EventType.TagUpdated, target.Id), (EventType.TagDeleted, source.Id)],
            events.Select(evt => (evt.Type, evt.EntityId)).ToArray());
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var context = new CoveContext(options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }
}
