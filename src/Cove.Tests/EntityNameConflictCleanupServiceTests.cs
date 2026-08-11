using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class EntityNameConflictCleanupServiceTests
{
    [Fact]
    public async Task ResolveAsync_MergesRecommendedPerformerAndRefreshesScan()
    {
        await using var db = CreateContext();
        var target = new Performer { Name = "Alpha" };
        var source = new Performer { Name = " alpha ", Details = "Transferred detail" };
        db.Performers.AddRange(target, source);
        using (db.SuppressEntityNameValidation())
            await db.SaveChangesAsync();
        var scanner = new EntityNameConflictScanner(db);
        var group = Assert.Single((await scanner.ScanAsync(NameConflictEntityTypes.Performer)).Groups);
        var cleanup = CreateCleanup(db, scanner);

        var refreshed = await cleanup.ResolveAsync(new ResolveEntityNameConflictDto(
            NameConflictEntityTypes.Performer,
            group.Key,
            group.Revision));

        Assert.Equal(0, refreshed.UnresolvedGroupCount);
        Assert.False(await db.Performers.AnyAsync(performer => performer.Id == source.Id));
        Assert.Equal("Transferred detail", (await db.Performers.SingleAsync()).Details);
    }

    [Fact]
    public async Task ResolveAsync_CanRenameOnePerformerInsteadOfMergingIt()
    {
        await using var db = CreateContext();
        var first = new Performer { Name = "Alpha", Disambiguation = "One" };
        var second = new Performer { Name = " alpha ", Disambiguation = " one " };
        db.Performers.AddRange(first, second);
        using (db.SuppressEntityNameValidation())
            await db.SaveChangesAsync();
        var scanner = new EntityNameConflictScanner(db);
        var group = Assert.Single((await scanner.ScanAsync(NameConflictEntityTypes.Performer)).Groups);
        var cleanup = CreateCleanup(db, scanner);

        var refreshed = await cleanup.ResolveAsync(new ResolveEntityNameConflictDto(
            NameConflictEntityTypes.Performer,
            group.Key,
            group.Revision,
            first.Id,
            [
                new EntityNameConflictResolutionDto(first.Id, EntityNameConflictActions.Keep),
                new EntityNameConflictResolutionDto(second.Id, EntityNameConflictActions.Rename, " Alpha ", "Two"),
            ]));

        Assert.Equal(0, refreshed.UnresolvedGroupCount);
        Assert.Equal(2, await db.Performers.CountAsync());
        var renamed = await db.Performers.SingleAsync(performer => performer.Id == second.Id);
        Assert.Equal("Alpha", renamed.Name);
        Assert.Equal("Two", renamed.Disambiguation);
    }

    [Fact]
    public async Task ResolveAsync_CanChooseDifferentStudioSurvivor()
    {
        await using var db = CreateContext();
        var lowerId = new Studio { Name = "Studio", Details = "Lower" };
        var higherId = new Studio { Name = " studio ", Details = "Chosen" };
        db.Studios.AddRange(lowerId, higherId);
        using (db.SuppressEntityNameValidation())
            await db.SaveChangesAsync();
        var scanner = new EntityNameConflictScanner(db);
        var group = Assert.Single((await scanner.ScanAsync(NameConflictEntityTypes.Studio)).Groups);
        var cleanup = CreateCleanup(db, scanner);

        await cleanup.ResolveAsync(new ResolveEntityNameConflictDto(
            NameConflictEntityTypes.Studio,
            group.Key,
            group.Revision,
            higherId.Id));

        var survivor = await db.Studios.SingleAsync();
        Assert.Equal(higherId.Id, survivor.Id);
        Assert.Equal("Chosen", survivor.Details);
    }

    [Fact]
    public async Task ResolveAllRecommendedAsync_CleansEveryGroup()
    {
        await using var db = CreateContext();
        db.Performers.AddRange(
            new Performer { Name = "Alpha" },
            new Performer { Name = "alpha" },
            new Performer { Name = "Beta", Disambiguation = "One" },
            new Performer { Name = " beta ", Disambiguation = "one" });
        using (db.SuppressEntityNameValidation())
            await db.SaveChangesAsync();
        var scanner = new EntityNameConflictScanner(db);
        var initial = await scanner.ScanAsync(NameConflictEntityTypes.Performer);
        var cleanup = CreateCleanup(db, scanner);

        var refreshed = await cleanup.ResolveAllRecommendedAsync(
            NameConflictEntityTypes.Performer,
            initial.Revision);

        Assert.Equal(0, refreshed.UnresolvedGroupCount);
        Assert.Equal(2, await db.Performers.CountAsync());
    }

    [Fact]
    public async Task ResolveAsync_RequiresExplicitActionForEveryExtensionReference()
    {
        await using var db = CreateContext();
        var target = new Studio { Name = "Studio" };
        var source = new Studio { Name = "studio" };
        db.Studios.AddRange(target, source);
        using (db.SuppressEntityNameValidation())
            await db.SaveChangesAsync();
        var inspector = new StatefulExternalReferenceInspector(source.Id, 2);
        var scanner = new EntityNameConflictScanner(db, inspector);
        var group = Assert.Single((await scanner.ScanAsync(NameConflictEntityTypes.Studio)).Groups);
        var cleanup = CreateCleanup(db, scanner, inspector);

        await Assert.ThrowsAsync<ArgumentException>(() => cleanup.ResolveAsync(new ResolveEntityNameConflictDto(
            NameConflictEntityTypes.Studio,
            group.Key,
            group.Revision,
            target.Id)));

        var reference = Assert.Single(group.Impacts, impact => impact.EntityId == source.Id).ExternalReferences.Single();
        var refreshed = await cleanup.ResolveAsync(new ResolveEntityNameConflictDto(
            NameConflictEntityTypes.Studio,
            group.Key,
            group.Revision,
            target.Id,
            ExternalReferenceResolutions:
            [
                new EntityExternalReferenceResolutionDto(
                    source.Id,
                    reference.ReferenceKey,
                    EntityExternalReferenceActions.UpdateToSurvivor),
            ]));

        Assert.Equal(0, refreshed.UnresolvedGroupCount);
        Assert.True(inspector.Applied);
    }

    [Fact]
    public async Task ResolveAsync_RejectsStaleGroupRevision()
    {
        await using var db = CreateContext();
        db.Studios.AddRange(new Studio { Name = "Studio" }, new Studio { Name = "studio" });
        using (db.SuppressEntityNameValidation())
            await db.SaveChangesAsync();
        var scanner = new EntityNameConflictScanner(db);
        var group = Assert.Single((await scanner.ScanAsync(NameConflictEntityTypes.Studio)).Groups);
        var cleanup = CreateCleanup(db, scanner);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.ResolveAsync(new ResolveEntityNameConflictDto(
            NameConflictEntityTypes.Studio,
            group.Key,
            "stale-revision")));
    }

    private static EntityNameConflictCleanupService CreateCleanup(
        CoveContext db,
        EntityNameConflictScanner scanner,
        IEntityExternalReferenceInspector? inspector = null)
    {
        var eventBus = new EventBus();
        var performerMerge = new PerformerMergeService(db, eventBus, inspector);
        var studioMerge = new StudioMergeService(db, eventBus, inspector);
        return new EntityNameConflictCleanupService(
            db,
            scanner,
            performerMerge,
            studioMerge,
            inspector);
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

    private sealed class StatefulExternalReferenceInspector(int sourceId, int count)
        : IEntityExternalReferenceInspector
    {
        public bool Applied { get; private set; }

        public Task<IReadOnlyList<EntityExternalReferenceDto>> InspectAsync(
            string entityType,
            IReadOnlyCollection<int> entityIds,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EntityExternalReferenceDto>>(
                !Applied && entityIds.Contains(sourceId)
                    ?
                    [
                        new EntityExternalReferenceDto(
                            sourceId,
                            "fixture-reference",
                            "public",
                            "extension_fixture",
                            "entity_id",
                            "restrict",
                            count),
                    ]
                    : []);

        public Task ApplyResolutionsAsync(
            string entityType,
            int targetEntityId,
            IReadOnlyCollection<EntityExternalReferenceResolutionDto> resolutions,
            CancellationToken ct = default)
        {
            Assert.Single(resolutions);
            Applied = true;
            return Task.CompletedTask;
        }
    }
}
