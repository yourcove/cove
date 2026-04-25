using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public class PerformerFilterBehaviorTests
{
    [Fact]
    public async Task StudiosCriterion_IncludesAll_RequiresScenesFromAllSelectedStudios()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var alphaStudio = new Studio { Name = "Alpha" };
        var betaStudio = new Studio { Name = "Beta" };

        context.Performers.AddRange(
            CreatePerformer("both-studios", alphaStudio, betaStudio),
            CreatePerformer("alpha-only", alphaStudio),
            CreatePerformer("beta-only", betaStudio));
        await context.SaveChangesAsync();

        var repository = new PerformerRepository(context);
        var filter = new PerformerFilter
        {
            StudiosCriterion = new MultiIdCriterion
            {
                Value = [alphaStudio.Id, betaStudio.Id],
                Modifier = CriterionModifier.IncludesAll,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

        Assert.Equal(1, totalCount);
        Assert.Equal(["both-studios"], items.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task StudiosCriterion_ExcludedIds_RemovePerformersWithExcludedStudios()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var alphaStudio = new Studio { Name = "Alpha" };
        var betaStudio = new Studio { Name = "Beta" };

        context.Performers.AddRange(
            CreatePerformer("alpha-only", alphaStudio),
            CreatePerformer("alpha-and-beta", alphaStudio, betaStudio),
            CreatePerformer("beta-only", betaStudio));
        await context.SaveChangesAsync();

        var repository = new PerformerRepository(context);
        var filter = new PerformerFilter
        {
            StudiosCriterion = new MultiIdCriterion
            {
                Value = [alphaStudio.Id],
                Excludes = [betaStudio.Id],
                Modifier = CriterionModifier.Includes,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

        Assert.Equal(1, totalCount);
        Assert.Equal(["alpha-only"], items.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task StudiosCriterion_DepthIncludesChildStudios()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var parentStudio = new Studio { Name = "Parent" };
        var childStudio = new Studio { Name = "Child", Parent = parentStudio };
        var otherStudio = new Studio { Name = "Other" };

        context.Performers.AddRange(
            CreatePerformer("child-performer", childStudio),
            CreatePerformer("other-performer", otherStudio));
        await context.SaveChangesAsync();

        var repository = new PerformerRepository(context);
        var filter = new PerformerFilter
        {
            StudiosCriterion = new MultiIdCriterion
            {
                Value = [parentStudio.Id],
                Modifier = CriterionModifier.Includes,
                Depth = -1,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

        Assert.Equal(1, totalCount);
        Assert.Equal(["child-performer"], items.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task StudiosCriterion_IncludesAll_WithHierarchy_RequiresMatchPerSelectedStudioGroup()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var parentA = new Studio { Name = "Parent A" };
        var childA = new Studio { Name = "Child A", Parent = parentA };
        var parentB = new Studio { Name = "Parent B" };
        var childB = new Studio { Name = "Child B", Parent = parentB };

        context.Performers.AddRange(
            CreatePerformer("both-groups", childA, childB),
            CreatePerformer("only-first-group", childA),
            CreatePerformer("only-second-group", childB));
        await context.SaveChangesAsync();

        var repository = new PerformerRepository(context);
        var filter = new PerformerFilter
        {
            StudiosCriterion = new MultiIdCriterion
            {
                Value = [parentA.Id, parentB.Id],
                Modifier = CriterionModifier.IncludesAll,
                Depth = -1,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

        Assert.Equal(1, totalCount);
        Assert.Equal(["both-groups"], items.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task NameCriterion_FiltersByPerformerName()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        context.Performers.AddRange(
            new Performer { Name = "Alice Example" },
            new Performer { Name = "Beth Example" });
        await context.SaveChangesAsync();

        var repository = new PerformerRepository(context);
        var filter = new PerformerFilter
        {
            NameCriterion = new StringCriterion
            {
                Value = "alice",
                Modifier = CriterionModifier.Includes,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

        Assert.Equal(1, totalCount);
        Assert.Equal(["Alice Example"], items.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task SceneCountCriterion_IsNullAndNotNull_UsePresenceSemantics()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var alphaStudio = new Studio { Name = "Alpha" };
        context.Performers.AddRange(
            new Performer { Name = "No Scenes" },
            CreatePerformer("Has Scene", alphaStudio));
        await context.SaveChangesAsync();

        var repository = new PerformerRepository(context);

        var (nullItems, nullCount) = await repository.FindAsync(
            new PerformerFilter { SceneCountCriterion = new IntCriterion { Modifier = CriterionModifier.IsNull } },
            new FindFilter { Page = 1, PerPage = 20, Sort = "name" });
        var (notNullItems, notNullCount) = await repository.FindAsync(
            new PerformerFilter { SceneCountCriterion = new IntCriterion { Modifier = CriterionModifier.NotNull } },
            new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

        Assert.Equal(1, nullCount);
        Assert.Equal(["No Scenes"], nullItems.Select(performer => performer.Name ?? string.Empty).ToArray());
        Assert.Equal(1, notNullCount);
        Assert.Equal(["Has Scene"], notNullItems.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task StudioCountCriterion_CountsDistinctStudios()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var alphaStudio = new Studio { Name = "Alpha" };
        var betaStudio = new Studio { Name = "Beta" };
        context.Performers.AddRange(
            CreatePerformer("one-studio", alphaStudio),
            CreatePerformer("two-studios", alphaStudio, betaStudio, alphaStudio),
            CreatePerformerWithScene("no-studio", null));
        await context.SaveChangesAsync();

        var repository = new PerformerRepository(context);
        var filter = new PerformerFilter
        {
            StudioCountCriterion = new IntCriterion
            {
                Value = 2,
                Modifier = CriterionModifier.Equals,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

        Assert.Equal(1, totalCount);
        Assert.Equal(["two-studios"], items.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task RemoteIdCriterion_WithProviderUsesProviderSpecificNullChecks()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        context.Performers.AddRange(
            new Performer
            {
                Name = "Has PMVStash",
                RemoteIds = [new PerformerRemoteId { Endpoint = "PMVStash", RemoteId = "pmv-1" }],
            },
            new Performer
            {
                Name = "Has StashDB",
                RemoteIds = [new PerformerRemoteId { Endpoint = "StashDB", RemoteId = "stash-1" }],
            },
            new Performer { Name = "No Remote" });
        await context.SaveChangesAsync();

        var repository = new PerformerRepository(context);

        var (withProviderItems, withProviderCount) = await repository.FindAsync(
            new PerformerFilter
            {
                RemoteIdCriterion = new StringCriterion
                {
                    Value = "PMVStash",
                    Modifier = CriterionModifier.NotNull,
                },
            },
            new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

        var (withoutProviderItems, withoutProviderCount) = await repository.FindAsync(
            new PerformerFilter
            {
                RemoteIdCriterion = new StringCriterion
                {
                    Value = "PMVStash",
                    Modifier = CriterionModifier.IsNull,
                },
            },
            new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

        Assert.Equal(1, withProviderCount);
        Assert.Equal(["Has PMVStash"], withProviderItems.Select(performer => performer.Name ?? string.Empty).ToArray());
        Assert.Equal(2, withoutProviderCount);
        Assert.Equal(["Has StashDB", "No Remote"], withoutProviderItems.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task RemoteIdValueCriterion_FiltersByRemoteIdValue()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        context.Performers.AddRange(
            new Performer
            {
                Name = "Has PMV Value",
                RemoteIds = [new PerformerRemoteId { Endpoint = "PMVStash", RemoteId = "pmv-123" }],
            },
            new Performer
            {
                Name = "Has Different Value",
                RemoteIds = [new PerformerRemoteId { Endpoint = "PMVStash", RemoteId = "other-456" }],
            },
            new Performer { Name = "No Remote" });
        await context.SaveChangesAsync();

        var repository = new PerformerRepository(context);
        var filter = new PerformerFilter
        {
            RemoteIdValueCriterion = new StringCriterion
            {
                Value = "pmv-123",
                Modifier = CriterionModifier.Equals,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

        Assert.Equal(1, totalCount);
        Assert.Equal(["Has PMV Value"], items.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task CareerLengthCriterion_FiltersByComputedCareerYears()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        context.Performers.AddRange(
            new Performer
            {
                Name = "Long Career",
                CareerStart = new DateOnly(2010, 1, 1),
                CareerEnd = new DateOnly(2024, 1, 1),
            },
            new Performer
            {
                Name = "Short Career",
                CareerStart = new DateOnly(2021, 1, 1),
                CareerEnd = new DateOnly(2024, 1, 1),
            },
            new Performer { Name = "Unknown Career" });
        await context.SaveChangesAsync();

        var repository = new PerformerRepository(context);
        var filter = new PerformerFilter
        {
            CareerLengthCriterion = new IntCriterion
            {
                Value = 10,
                Modifier = CriterionModifier.GreaterThan,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

        Assert.Equal(1, totalCount);
        Assert.Equal(["Long Career"], items.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    private static Performer CreatePerformer(string name, params Studio[] studios)
    {
        var performer = new Performer { Name = name };

        foreach (var studio in studios)
        {
            var scene = new Scene
            {
                Title = $"{name}-{studio.Name}",
                Studio = studio,
            };

            var link = new ScenePerformer
            {
                Scene = scene,
                Performer = performer,
            };

            scene.ScenePerformers.Add(link);
            performer.ScenePerformers.Add(link);
        }

        return performer;
    }

    private static Performer CreatePerformerWithScene(string name, Studio? studio)
    {
        var performer = new Performer { Name = name };
        var scene = new Scene
        {
            Title = $"{name}-scene",
            Studio = studio,
        };

        var link = new ScenePerformer
        {
            Scene = scene,
            Performer = performer,
        };

        scene.ScenePerformers.Add(link);
        performer.ScenePerformers.Add(link);
        return performer;
    }

    private static async Task<TestContextScope> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;

        var context = new PerformerFilterTestContext(options);
        await context.Database.EnsureCreatedAsync();
        return new TestContextScope(context, connection);
    }

    private sealed class PerformerFilterTestContext(DbContextOptions<CoveContext> options) : CoveContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Scene>().Ignore(scene => scene.CustomFields);
            modelBuilder.Entity<Performer>().Ignore(performer => performer.CustomFields);
            modelBuilder.Entity<Tag>().Ignore(tag => tag.CustomFields);
            modelBuilder.Entity<Studio>().Ignore(studio => studio.CustomFields);
            modelBuilder.Entity<Gallery>().Ignore(gallery => gallery.CustomFields);
            modelBuilder.Entity<Image>().Ignore(image => image.CustomFields);
            modelBuilder.Entity<Group>().Ignore(group => group.CustomFields);
        }
    }

    private sealed class TestContextScope(CoveContext context, SqliteConnection connection) : IAsyncDisposable
    {
        public CoveContext Context { get; } = context;

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}