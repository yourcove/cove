using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public class TagFilterBehaviorTests
{
    [Fact]
    public async Task RemoteIdCriterion_WithProviderUsesProviderSpecificNullChecks()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        context.Tags.AddRange(
            new Tag
            {
                Name = "Has PMVStash",
                RemoteIds = [new TagRemoteId { Endpoint = "PMVStash", RemoteId = "pmv-1" }],
            },
            new Tag
            {
                Name = "Has StashDB",
                RemoteIds = [new TagRemoteId { Endpoint = "StashDB", RemoteId = "stash-1" }],
            },
            new Tag { Name = "No Remote" });
        await context.SaveChangesAsync();

        var repository = new TagRepository(context);

        var (withProviderItems, withProviderCount) = await repository.FindAsync(
            new TagFilter
            {
                RemoteIdCriterion = new StringCriterion
                {
                    Value = "PMVStash",
                    Modifier = CriterionModifier.NotNull,
                },
            },
            new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

        var (withoutProviderItems, withoutProviderCount) = await repository.FindAsync(
            new TagFilter
            {
                RemoteIdCriterion = new StringCriterion
                {
                    Value = "PMVStash",
                    Modifier = CriterionModifier.IsNull,
                },
            },
            new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

        Assert.Equal(1, withProviderCount);
        Assert.Equal(["Has PMVStash"], withProviderItems.Select(tag => tag.Name).ToArray());
        Assert.Equal(2, withoutProviderCount);
        Assert.Equal(["Has StashDB", "No Remote"], withoutProviderItems.Select(tag => tag.Name).ToArray());
    }

    [Fact]
    public async Task RemoteIdValueCriterion_FiltersByRemoteIdValue()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        context.Tags.AddRange(
            new Tag
            {
                Name = "Has PMV Value",
                RemoteIds = [new TagRemoteId { Endpoint = "PMVStash", RemoteId = "pmv-123" }],
            },
            new Tag
            {
                Name = "Has Different Value",
                RemoteIds = [new TagRemoteId { Endpoint = "PMVStash", RemoteId = "other-456" }],
            },
            new Tag { Name = "No Remote" });
        await context.SaveChangesAsync();

        var repository = new TagRepository(context);
        var filter = new TagFilter
        {
            RemoteIdValueCriterion = new StringCriterion
            {
                Value = "pmv-123",
                Modifier = CriterionModifier.Equals,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

        Assert.Equal(1, totalCount);
        Assert.Equal(["Has PMV Value"], items.Select(tag => tag.Name).ToArray());
    }

    [Fact]
    public async Task SceneCountCriterion_CanIncludeDescendantTagCounts()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var (parent, child, grandchild) = await SeedTagHierarchyAsync(context);
        context.Scenes.Add(new Scene
        {
            Title = "grandchild scene",
            SceneTags = [new SceneTag { TagId = grandchild.Id }],
        });
        await context.SaveChangesAsync();

        var repository = new TagRepository(context);
        var directFilter = new TagFilter
        {
            SceneCountCriterion = new IntCriterion
            {
                Modifier = CriterionModifier.GreaterThan,
                Value = 0,
            },
        };
        var includeChildrenFilter = new TagFilter
        {
            SceneCountCriterion = new IntCriterion
            {
                Modifier = CriterionModifier.GreaterThan,
                Value = 0,
            },
            SceneCountIncludesChildren = true,
        };

        var (directItems, directCount) = await repository.FindAsync(directFilter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" });
        var (aggregatedItems, aggregatedCount) = await repository.FindAsync(includeChildrenFilter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

        Assert.Equal(1, directCount);
        Assert.Equal(["Grandchild"], directItems.Select(tag => tag.Name).ToArray());
        Assert.Equal(3, aggregatedCount);
        Assert.Equal(["Child", "Grandchild", "Parent"], aggregatedItems.Select(tag => tag.Name).ToArray());
    }

    [Fact]
    public async Task PerformerCountCriterion_CanIncludeDescendantTagCounts()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var (parent, child, _) = await SeedTagHierarchyAsync(context);
        context.Performers.Add(new Performer
        {
            Name = "Tagged Performer",
            PerformerTags = [new PerformerTag { TagId = child.Id }],
        });
        await context.SaveChangesAsync();

        var repository = new TagRepository(context);
        var directFilter = new TagFilter
        {
            PerformerCountCriterion = new IntCriterion
            {
                Modifier = CriterionModifier.GreaterThan,
                Value = 0,
            },
        };
        var includeChildrenFilter = new TagFilter
        {
            PerformerCountCriterion = new IntCriterion
            {
                Modifier = CriterionModifier.GreaterThan,
                Value = 0,
            },
            PerformerCountIncludesChildren = true,
        };

        var (directItems, directCount) = await repository.FindAsync(directFilter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" });
        var (aggregatedItems, aggregatedCount) = await repository.FindAsync(includeChildrenFilter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

        Assert.Equal(1, directCount);
        Assert.Equal(["Child"], directItems.Select(tag => tag.Name).ToArray());
        Assert.Equal(2, aggregatedCount);
        Assert.Equal(["Child", "Parent"], aggregatedItems.Select(tag => tag.Name).ToArray());
    }

    [Fact]
    public async Task MarkerCountCriterion_CanIncludeDescendantTagCounts()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var (parent, child, _) = await SeedTagHierarchyAsync(context);
        var scene = new Scene { Title = "marker scene" };
        context.Scenes.Add(scene);
        await context.SaveChangesAsync();

        context.SceneMarkers.Add(new SceneMarker
        {
            Title = "child marker",
            Seconds = 1,
            SceneId = scene.Id,
            PrimaryTagId = child.Id,
        });
        await context.SaveChangesAsync();

        var repository = new TagRepository(context);
        var directFilter = new TagFilter
        {
            MarkerCountCriterion = new IntCriterion
            {
                Modifier = CriterionModifier.GreaterThan,
                Value = 0,
            },
        };
        var includeChildrenFilter = new TagFilter
        {
            MarkerCountCriterion = new IntCriterion
            {
                Modifier = CriterionModifier.GreaterThan,
                Value = 0,
            },
            MarkerCountIncludesChildren = true,
        };

        var (directItems, directCount) = await repository.FindAsync(directFilter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" });
        var (aggregatedItems, aggregatedCount) = await repository.FindAsync(includeChildrenFilter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

        Assert.Equal(1, directCount);
        Assert.Equal(["Child"], directItems.Select(tag => tag.Name).ToArray());
        Assert.Equal(2, aggregatedCount);
        Assert.Equal(["Child", "Parent"], aggregatedItems.Select(tag => tag.Name).ToArray());
    }

    [Fact]
    public async Task SceneCountCriterion_IncludingChildren_DeduplicatesEntitiesAcrossMultipleDescendants()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var parent = new Tag { Name = "Parent" };
        var childA = new Tag { Name = "Child A" };
        var childB = new Tag { Name = "Child B" };

        context.Tags.AddRange(parent, childA, childB);
        await context.SaveChangesAsync();

        context.Set<TagParent>().AddRange(
            new TagParent { ParentId = parent.Id, ChildId = childA.Id },
            new TagParent { ParentId = parent.Id, ChildId = childB.Id });
        context.Scenes.Add(new Scene
        {
            Title = "shared scene",
            SceneTags = [new SceneTag { TagId = childA.Id }, new SceneTag { TagId = childB.Id }],
        });
        await context.SaveChangesAsync();

        var repository = new TagRepository(context);
        var filter = new TagFilter
        {
            SceneCountCriterion = new IntCriterion
            {
                Modifier = CriterionModifier.Equals,
                Value = 1,
            },
            SceneCountIncludesChildren = true,
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

        Assert.Equal(3, totalCount);
        Assert.Equal(["Child A", "Child B", "Parent"], items.Select(tag => tag.Name).ToArray());
    }

    private static async Task<(Tag Parent, Tag Child, Tag Grandchild)> SeedTagHierarchyAsync(CoveContext context)
    {
        var parent = new Tag { Name = "Parent" };
        var child = new Tag { Name = "Child" };
        var grandchild = new Tag { Name = "Grandchild" };

        context.Tags.AddRange(parent, child, grandchild);
        await context.SaveChangesAsync();

        context.Set<TagParent>().AddRange(
            new TagParent { ParentId = parent.Id, ChildId = child.Id },
            new TagParent { ParentId = child.Id, ChildId = grandchild.Id });
        await context.SaveChangesAsync();

        return (parent, child, grandchild);
    }

    private static async Task<TestContextScope> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;

        var context = new TagFilterTestContext(options);
        await context.Database.EnsureCreatedAsync();
        return new TestContextScope(context, connection);
    }

    private sealed class TagFilterTestContext(DbContextOptions<CoveContext> options) : CoveContext(options)
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