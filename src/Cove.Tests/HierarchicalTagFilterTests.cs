using Microsoft.EntityFrameworkCore;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;

namespace Cove.Tests;

public class HierarchicalTagFilterTests
{
    [Fact]
    public async Task VideoStudiosCriterion_WithSubStudios_MatchesChildStudio()
    {
        await using var context = CreateContext();
        var parent = new Studio { Name = "Parent" };
        var child = new Studio { Name = "Child", Parent = parent };
        context.Studios.AddRange(parent, child);
        await context.SaveChangesAsync();

        context.Videos.Add(new Video { Title = "child-video", StudioId = child.Id });
        await context.SaveChangesAsync();

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            StudiosCriterion = new MultiIdCriterion
            {
                Value = [parent.Id],
                Modifier = CriterionModifier.Includes,
                Depth = -1,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(1, totalCount);
        Assert.Equal(["child-video"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task VideoTagsCriterion_IncludesAll_WithSubTags_MatchesPerSelectedRoot()
    {
        await using var context = CreateContext();
        var (parentA, childA, parentB, childB) = await SeedTagHierarchyAsync(context);

        context.Videos.AddRange(
            CreateVideo("children-only-match", childA.Id, childB.Id),
            CreateVideo("missing-second-root", childA.Id),
            CreateVideo("root-and-child-match", parentA.Id, childB.Id));
        await context.SaveChangesAsync();

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            TagsCriterion = new MultiIdCriterion
            {
                Value = [parentA.Id, parentB.Id],
                Modifier = CriterionModifier.IncludesAll,
                Depth = -1,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 });
        var titles = items.Select(video => video.Title ?? string.Empty).OrderBy(title => title).ToArray();

        Assert.Equal(2, totalCount);
        Assert.Equal(["children-only-match", "root-and-child-match"], titles);
    }

    [Fact]
    public async Task VideoTagsCriterion_ExcludesList_RemovesVideosWithExcludedTag()
    {
        // Repro for the reported bug: excluding a tag still returned videos that had it. The filter UI
        // sends excluded ids in MultiIdCriterion.Excludes alongside an Includes modifier (not by flipping
        // the modifier), so the exclude-only case must still filter the results.
        await using var context = CreateContext();
        var keep = new Tag { Name = "Keep" };
        var excluded = new Tag { Name = "1F" };
        context.Tags.AddRange(keep, excluded);
        await context.SaveChangesAsync();

        context.Videos.AddRange(
            CreateVideo("has-excluded-tag", excluded.Id),
            CreateVideo("has-excluded-and-keep", keep.Id, excluded.Id),
            CreateVideo("clean", keep.Id),
            CreateVideo("untagged"));
        await context.SaveChangesAsync();

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            TagsCriterion = new MultiIdCriterion
            {
                Value = [],
                Modifier = CriterionModifier.Includes,
                Excludes = [excluded.Id],
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 });
        var titles = items.Select(video => video.Title ?? string.Empty).OrderBy(title => title).ToArray();

        Assert.Equal(2, totalCount);
        Assert.Equal(["clean", "untagged"], titles);
    }

    [Fact]
    public async Task VideoTagsCriterion_IncludeWithExclude_AppliesBothSides()
    {
        await using var context = CreateContext();
        var keep = new Tag { Name = "Keep" };
        var excluded = new Tag { Name = "1F" };
        context.Tags.AddRange(keep, excluded);
        await context.SaveChangesAsync();

        context.Videos.AddRange(
            CreateVideo("keep-only", keep.Id),
            CreateVideo("keep-but-excluded", keep.Id, excluded.Id),
            CreateVideo("excluded-only", excluded.Id),
            CreateVideo("untagged"));
        await context.SaveChangesAsync();

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            TagsCriterion = new MultiIdCriterion
            {
                Value = [keep.Id],
                Modifier = CriterionModifier.Includes,
                Excludes = [excluded.Id],
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 });
        var titles = items.Select(video => video.Title ?? string.Empty).OrderBy(title => title).ToArray();

        Assert.Equal(1, totalCount);
        Assert.Equal(["keep-only"], titles);
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

    [Fact]
    public async Task GalleryTagsCriterion_IncludesAll_WithSubTags_MatchesPerSelectedRoot()
    {
        await using var context = CreateContext();
        var (parentA, childA, parentB, childB) = await SeedTagHierarchyAsync(context);
        context.Galleries.AddRange(
            CreateGallery("children-only-match", childA.Id, childB.Id),
            CreateGallery("missing-second-root", childA.Id),
            CreateGallery("root-and-child-match", parentA.Id, childB.Id));
        await context.SaveChangesAsync();

        var repository = new GalleryRepository(context);
        var filter = new GalleryFilter
        {
            TagsCriterion = new MultiIdCriterion
            {
                Value = [parentA.Id, parentB.Id],
                Modifier = CriterionModifier.IncludesAll,
                Depth = -1,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 });
        var titles = items.Select(gallery => gallery.Title ?? string.Empty).OrderBy(title => title).ToArray();

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

    private static Video CreateVideo(string title, params int[] tagIds)
        => new()
        {
            Title = title,
            VideoTags = tagIds.Select(tagId => new VideoTag { TagId = tagId }).ToList(),
        };

    private static Image CreateImage(string title, params int[] tagIds)
        => new()
        {
            Title = title,
            ImageTags = tagIds.Select(tagId => new ImageTag { TagId = tagId }).ToList(),
        };

    private static Gallery CreateGallery(string title, params int[] tagIds)
        => new()
        {
            Title = title,
            GalleryTags = tagIds.Select(tagId => new GalleryTag { TagId = tagId }).ToList(),
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

        }
    }
}
