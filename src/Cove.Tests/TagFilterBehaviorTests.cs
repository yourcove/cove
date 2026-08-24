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
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new TagRepository(context);

        var (withProviderItems, withProviderCount) = await repository.FindAsync(new TagFilter
            {
                RemoteIdCriterion = new StringCriterion
                {
                    Value = "PMVStash",
                    Modifier = CriterionModifier.NotNull,
                },
            }, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken);

        var (withoutProviderItems, withoutProviderCount) = await repository.FindAsync(new TagFilter
            {
                RemoteIdCriterion = new StringCriterion
                {
                    Value = "PMVStash",
                    Modifier = CriterionModifier.IsNull,
                },
            }, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken);

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
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new TagRepository(context);
        var filter = new TagFilter
        {
            RemoteIdValueCriterion = new StringCriterion
            {
                Value = "pmv-123",
                Modifier = CriterionModifier.Equals,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken);

        Assert.Equal(1, totalCount);
        Assert.Equal(["Has PMV Value"], items.Select(tag => tag.Name).ToArray());
    }

    [Fact]
    public async Task VideoCountCriterion_CanIncludeDescendantTagCounts()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var (parent, child, grandchild) = await SeedTagHierarchyAsync(context);
        context.Videos.Add(new Video
        {
            Title = "grandchild video",
            VideoTags = [new VideoTag { TagId = grandchild.Id }],
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new TagRepository(context);
        var directFilter = new TagFilter
        {
            VideoCountCriterion = new IntCriterion
            {
                Modifier = CriterionModifier.GreaterThan,
                Value = 0,
            },
        };
        var includeChildrenFilter = new TagFilter
        {
            VideoCountCriterion = new IntCriterion
            {
                Modifier = CriterionModifier.GreaterThan,
                Value = 0,
            },
            VideoCountIncludesChildren = true,
        };

        var (directItems, directCount) = await repository.FindAsync(directFilter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken);
        var (aggregatedItems, aggregatedCount) = await repository.FindAsync(includeChildrenFilter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken);

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
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

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

        var (directItems, directCount) = await repository.FindAsync(directFilter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken);
        var (aggregatedItems, aggregatedCount) = await repository.FindAsync(includeChildrenFilter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken);

        Assert.Equal(1, directCount);
        Assert.Equal(["Child"], directItems.Select(tag => tag.Name).ToArray());
        Assert.Equal(2, aggregatedCount);
        Assert.Equal(["Child", "Parent"], aggregatedItems.Select(tag => tag.Name).ToArray());
    }

    [Fact]
    public async Task VideoCountCriterion_IncludingChildren_DeduplicatesEntitiesAcrossMultipleDescendants()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var parent = new Tag { Name = "Parent" };
        var childA = new Tag { Name = "Child A" };
        var childB = new Tag { Name = "Child B" };

        context.Tags.AddRange(parent, childA, childB);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Set<TagParent>().AddRange(
            new TagParent { ParentId = parent.Id, ChildId = childA.Id },
            new TagParent { ParentId = parent.Id, ChildId = childB.Id });
        context.Videos.Add(new Video
        {
            Title = "shared video",
            VideoTags = [new VideoTag { TagId = childA.Id }, new VideoTag { TagId = childB.Id }],
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new TagRepository(context);
        var filter = new TagFilter
        {
            VideoCountCriterion = new IntCriterion
            {
                Modifier = CriterionModifier.Equals,
                Value = 1,
            },
            VideoCountIncludesChildren = true,
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken);

        Assert.Equal(3, totalCount);
        Assert.Equal(["Child A", "Child B", "Parent"], items.Select(tag => tag.Name).ToArray());
    }

    [Fact]
    public async Task TagGroupsCriterion_FiltersIncludedAndExcludedGroups()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var actionGroup = new TagGroup { Name = "Action", SortOrder = 1 };
        var subjectGroup = new TagGroup { Name = "Subject", SortOrder = 2 };
        context.TagGroups.AddRange(actionGroup, subjectGroup);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Tags.AddRange(
            new Tag { Name = "Action Tag", TagGroupId = actionGroup.Id },
            new Tag { Name = "Subject Tag", TagGroupId = subjectGroup.Id },
            new Tag { Name = "Ungrouped Tag" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new TagRepository(context);
        var (includedItems, includedCount) = await repository.FindAsync(new TagFilter
            {
                TagGroupsCriterion = new MultiIdCriterion
                {
                    Modifier = CriterionModifier.Includes,
                    Value = [actionGroup.Id],
                },
            }, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken);

        var (excludedItems, excludedCount) = await repository.FindAsync(new TagFilter
            {
                TagGroupsCriterion = new MultiIdCriterion
                {
                    Excludes = [subjectGroup.Id],
                },
            }, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken);

        Assert.Equal(1, includedCount);
        Assert.Equal(["Action Tag"], includedItems.Select(tag => tag.Name).ToArray());
        Assert.Equal(2, excludedCount);
        Assert.Equal(["Action Tag", "Ungrouped Tag"], excludedItems.Select(tag => tag.Name).ToArray());
    }

    [Fact]
    public async Task TagGroupSort_OrdersByGroupSortOrderThenTagName()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var actionGroup = new TagGroup { Name = "Action", SortOrder = 1 };
        var subjectGroup = new TagGroup { Name = "Subject", SortOrder = 2 };
        context.TagGroups.AddRange(actionGroup, subjectGroup);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Tags.AddRange(
            new Tag { Name = "Zulu Action", TagGroupId = actionGroup.Id },
            new Tag { Name = "Alpha Action", TagGroupId = actionGroup.Id },
            new Tag { Name = "Subject Tag", TagGroupId = subjectGroup.Id },
            new Tag { Name = "Ungrouped Tag" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new TagRepository(context);
        var (items, totalCount) = await repository.FindAsync(null, new FindFilter { Page = 1, PerPage = 20, Sort = "tag_group" }, TestContext.Current.CancellationToken);

        Assert.Equal(4, totalCount);
        Assert.Equal(["Alpha Action", "Zulu Action", "Subject Tag", "Ungrouped Tag"], items.Select(tag => tag.Name).ToArray());
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

