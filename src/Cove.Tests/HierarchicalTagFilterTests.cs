using Microsoft.EntityFrameworkCore;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;

namespace Cove.Tests;

public class HierarchicalTagFilterTests
{
    [Fact]
    public async Task SceneTagsCriterion_IncludesAll_WithSubTags_MatchesPerSelectedRoot()
    {
        await using var context = CreateContext();
        var (parentA, childA, parentB, childB) = await SeedTagHierarchyAsync(context);

        context.Scenes.AddRange(
            CreateScene("children-only-match", childA.Id, childB.Id),
            CreateScene("missing-second-root", childA.Id),
            CreateScene("root-and-child-match", parentA.Id, childB.Id));
        await context.SaveChangesAsync();

        var repository = new SceneRepository(context);
        var filter = new SceneFilter
        {
            TagsCriterion = new MultiIdCriterion
            {
                Value = [parentA.Id, parentB.Id],
                Modifier = CriterionModifier.IncludesAll,
                Depth = -1,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 });
        var titles = items.Select(scene => scene.Title ?? string.Empty).OrderBy(title => title).ToArray();

        Assert.Equal(2, totalCount);
        Assert.Equal(["children-only-match", "root-and-child-match"], titles);
    }

    [Fact]
    public async Task ImageTagsCriterion_IncludesAll_WithSubTags_MatchesPerSelectedRoot()
    {
        await using var context = CreateContext();
        var (parentA, childA, parentB, childB) = await SeedTagHierarchyAsync(context);

        context.Images.AddRange(
            CreateImage("children-only-match", childA.Id, childB.Id),
            CreateImage("missing-second-root", childA.Id),
            CreateImage("root-and-child-match", parentA.Id, childB.Id));
        await context.SaveChangesAsync();

        var repository = new ImageRepository(context);
        var filter = new ImageFilter
        {
            TagsCriterion = new MultiIdCriterion
            {
                Value = [parentA.Id, parentB.Id],
                Modifier = CriterionModifier.IncludesAll,
                Depth = -1,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 });
        var titles = items.Select(image => image.Title ?? string.Empty).OrderBy(title => title).ToArray();

        Assert.Equal(2, totalCount);
        Assert.Equal(["children-only-match", "root-and-child-match"], titles);
    }

    private static async Task<(Tag ParentA, Tag ChildA, Tag ParentB, Tag ChildB)> SeedTagHierarchyAsync(CoveContext context)
    {
        var parentA = new Tag { Name = "Parent A" };
        var childA = new Tag { Name = "Child A" };
        var parentB = new Tag { Name = "Parent B" };
        var childB = new Tag { Name = "Child B" };

        context.Tags.AddRange(parentA, childA, parentB, childB);
        await context.SaveChangesAsync();

        context.Set<TagParent>().AddRange(
            new TagParent { ParentId = parentA.Id, ChildId = childA.Id },
            new TagParent { ParentId = parentB.Id, ChildId = childB.Id });
        await context.SaveChangesAsync();

        return (parentA, childA, parentB, childB);
    }

    private static Scene CreateScene(string title, params int[] tagIds)
        => new()
        {
            Title = title,
            SceneTags = tagIds.Select(tagId => new SceneTag { TagId = tagId }).ToList(),
        };

    private static Image CreateImage(string title, params int[] tagIds)
        => new()
        {
            Title = title,
            ImageTags = tagIds.Select(tagId => new ImageTag { TagId = tagId }).ToList(),
        };

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"hierarchical-tag-filter-{Guid.NewGuid():N}")
            .Options;

        return new TestCoveContext(options);
    }

    private sealed class TestCoveContext(DbContextOptions<CoveContext> options) : CoveContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Scene>().Ignore(scene => scene.CustomFields);
            modelBuilder.Entity<Image>().Ignore(image => image.CustomFields);
            modelBuilder.Entity<Tag>().Ignore(tag => tag.CustomFields);
            modelBuilder.Entity<Studio>().Ignore(studio => studio.CustomFields);
            modelBuilder.Entity<Performer>().Ignore(performer => performer.CustomFields);
            modelBuilder.Entity<Gallery>().Ignore(gallery => gallery.CustomFields);
            modelBuilder.Entity<Group>().Ignore(group => group.CustomFields);
        }
    }
}