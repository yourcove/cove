using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public class TagsControllerSegmentTests
{
    [Fact]
    public async Task TagDetail_WithRecursiveDepth_AggregatesDistinctDescendantUsageCounts()
    {
        await using var context = CreateContext();
        var parent = new Tag { Name = "Parent" };
        var child = new Tag { Name = "Child" };
        var image = new Image { Title = "Shared image" };
        context.AddRange(parent, child, image);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.AddRange(
            new TagParent { ParentId = parent.Id, ChildId = child.Id },
            new ImageTag { ImageId = image.Id, TagId = parent.Id },
            new ImageTag { ImageId = image.Id, TagId = child.Id });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = new TagsController(null!, context, new CustomFieldService(context), null!);
        var result = await controller.GetById(parent.Id, CancellationToken.None, -1);
        var detail = Assert.IsType<OkObjectResult>(result.Result).Value as TagDetailDto;

        Assert.NotNull(detail);
        Assert.Equal(1, detail!.ImageCount);
    }

    [Fact]
    public async Task TagDetail_IncludesRemoteIds()
    {
        await using var context = CreateContext();
        var tag = new Tag { Name = "Linked tag" };
        tag.RemoteIds.Add(new TagRemoteId
        {
            Endpoint = "https://stashdb.org/graphql",
            RemoteId = "remote-tag-1",
        });
        context.Tags.Add(tag);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();

        var controller = new TagsController(null!, context, new CustomFieldService(context), null!);

        var result = await controller.GetById(tag.Id, CancellationToken.None);
        var detail = Assert.IsType<OkObjectResult>(result.Result).Value as TagDetailDto;

        Assert.NotNull(detail);
        var remoteId = Assert.Single(detail.RemoteIds!);
        Assert.Equal("https://stashdb.org/graphql", remoteId.Endpoint);
        Assert.Equal("remote-tag-1", remoteId.RemoteId);
    }

    [Fact]
    public async Task TagDetail_UsesVideoSegmentCountsAndReturnsTagSegments()
    {
        await using var context = CreateContext();
        var tag = new Tag { Name = "Body" };
        var otherTag = new Tag { Name = "Other" };
        var video = new Video { Title = "Imported Video" };
        context.AddRange(tag, otherTag, video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Segments.AddRange(
            new Segment
            {
                HostType = SegmentHostType.Video,
                HostId = video.Id,
                TagId = tag.Id,
                Kind = "tag",
                SourceKey = "import:test",
                StartSec = 8.0,
                EndSec = 11.0,
                Title = "AI body",
            },
            new Segment
            {
                HostType = SegmentHostType.Video,
                HostId = video.Id,
                TagId = tag.Id,
                Kind = "tag",
                SourceKey = "user",
                StartSec = 15.0,
                EndSec = 18.0,
                Title = "Manual body",
            },
            new Segment
            {
                HostType = SegmentHostType.Image,
                HostId = 999,
                TagId = tag.Id,
                Kind = "tag",
                SourceKey = "import:image",
                StartSec = 0,
            },
            new Segment
            {
                HostType = SegmentHostType.Video,
                HostId = video.Id,
                TagId = otherTag.Id,
                Kind = "tag",
                SourceKey = "import:test",
                StartSec = 20.0,
                EndSec = 25.0,
            });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = new TagsController(null!, context, new CustomFieldService(context), null!);

        var detailResult = await controller.GetById(tag.Id, CancellationToken.None);
        var detail = Assert.IsType<OkObjectResult>(detailResult.Result).Value as TagDetailDto;
        Assert.NotNull(detail);
        Assert.Equal(2, detail!.SegmentCount);

        var segmentsResult = await controller.GetSegments(tag.Id, 100, CancellationToken.None);
        var segments = Assert.IsType<OkObjectResult>(segmentsResult.Result).Value as IReadOnlyList<TagSegmentWallDto>;
        Assert.NotNull(segments);
        Assert.Equal(2, segments!.Count);
        Assert.All(segments, segment =>
        {
            Assert.Equal(video.Id, segment.VideoId);
            Assert.Equal(video.Title, segment.VideoTitle);
        });
    }

    [Fact]
    public async Task TagDetail_UsesLiveUsageCountsInsteadOfStoredCounters()
    {
        await using var context = CreateContext();

        var tag = new Tag { Name = "Body" };
        var video = new Video { Title = "Imported Video" };
        var performer = new Performer { Name = "Performer" };
        var studio = new Studio { Name = "Studio" };
        var image = new Image { Title = "Image" };
        var gallery = new Gallery { Title = "Gallery" };
        var group = new Group { Name = "Group" };
        context.AddRange(tag, video, performer, studio, image, gallery, group);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.AddRange(
            new VideoTag { VideoId = video.Id, TagId = tag.Id },
            new ImageTag { ImageId = image.Id, TagId = tag.Id },
            new GalleryTag { GalleryId = gallery.Id, TagId = tag.Id },
            new GroupTag { GroupId = group.Id, TagId = tag.Id },
            new PerformerTag { PerformerId = performer.Id, TagId = tag.Id },
            new StudioTag { StudioId = studio.Id, TagId = tag.Id },
            new Segment
            {
                HostType = SegmentHostType.Video,
                HostId = video.Id,
                TagId = tag.Id,
                Kind = "tag",
                SourceKey = "user",
                StartSec = 1,
                EndSec = 2,
            });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var storedTag = await context.Tags.SingleAsync(candidate => candidate.Id == tag.Id, cancellationToken: TestContext.Current.CancellationToken);
        storedTag.VideoCount = 0;
        storedTag.ImageCount = 0;
        storedTag.GalleryCount = 0;
        storedTag.GroupCount = 0;
        storedTag.PerformerCount = 0;
        storedTag.StudioCount = 0;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = new TagsController(null!, context, new CustomFieldService(context), null!);

        var detailResult = await controller.GetById(tag.Id, CancellationToken.None);
        var detail = Assert.IsType<OkObjectResult>(detailResult.Result).Value as TagDetailDto;
        Assert.NotNull(detail);
        Assert.Equal(1, detail!.VideoCount);
        Assert.Equal(1, detail.ImageCount);
        Assert.Equal(1, detail.GalleryCount);
        Assert.Equal(1, detail.GroupCount);
        Assert.Equal(1, detail.PerformerCount);
        Assert.Equal(1, detail.StudioCount);
        Assert.Equal(1, detail.SegmentCount);
    }

    [Fact]
    public async Task TagSegmentCount_TracksVideoSegments()
    {
        await using var context = CreateContext();
        var tag = new Tag { Name = "Body" };
        var video = new Video { Title = "Imported Video" };
        context.AddRange(tag, video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var segment = new Segment
        {
            HostType = SegmentHostType.Video,
            HostId = video.Id,
            TagId = tag.Id,
            Kind = "tag",
            SourceKey = "user",
            StartSec = 12.0,
            EndSec = 20.0,
        };

        context.Segments.Add(segment);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var addedTag = await context.Tags.AsNoTracking().SingleAsync(candidate => candidate.Id == tag.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, addedTag.SegmentCount);

        context.Segments.Remove(segment);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var removedTag = await context.Tags.AsNoTracking().SingleAsync(candidate => candidate.Id == tag.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, removedTag.SegmentCount);
    }

    [Fact]
    public async Task TagDetail_RoundTripsPlayerBarOverrides()
    {
        await using var context = CreateContext();
        var tag = new Tag
        {
            Name = "Body",
            ShowAsSegment = true,
            SegmentColorOverride = "#44aaee",
            SegmentLaneOverride = 2,
        };
        context.Tags.Add(tag);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = new TagsController(null!, context, new CustomFieldService(context), null!);

        var detailResult = await controller.GetById(tag.Id, CancellationToken.None);
        var detailOk = Assert.IsType<OkObjectResult>(detailResult.Result);
        var detail = Assert.IsType<TagDetailDto>(detailOk.Value);
        Assert.True(detail.ShowAsSegment);
        Assert.Equal("#44aaee", detail.SegmentColorOverride);
        Assert.Equal(2, detail.SegmentLaneOverride);

        var updateResult = await controller.Update(tag.Id, new TagUpdateDto(
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            null,
            null), CancellationToken.None);
        var updateOk = Assert.IsType<OkObjectResult>(updateResult.Result);
        var updated = Assert.IsType<TagDetailDto>(updateOk.Value);
        Assert.False(updated.ShowAsSegment);
        Assert.Null(updated.SegmentColorOverride);
        Assert.Null(updated.SegmentLaneOverride);
    }

    [Fact]
    public async Task TagDetail_OrdersParentsAndChildrenByEffectiveSortName()
    {
        await using var context = CreateContext();

        var parent = new Tag { Name = "Parent" };
        var zebraChild = new Tag { Name = "Zebra Child" };
        var appleChild = new Tag { Name = "Apple Child" };
        var bravoSortChild = new Tag { Name = "Xylophone Child", SortName = "Bravo Child" };
        var deltaSortChild = new Tag { Name = "Antelope Child", SortName = "Delta Child" };

        var child = new Tag { Name = "Child" };
        var zebraParent = new Tag { Name = "Zebra Parent" };
        var appleParent = new Tag { Name = "Apple Parent" };
        var bravoSortParent = new Tag { Name = "Xylophone Parent", SortName = "Bravo Parent" };
        var deltaSortParent = new Tag { Name = "Antelope Parent", SortName = "Delta Parent" };

        context.Tags.AddRange(
            parent,
            zebraChild,
            appleChild,
            bravoSortChild,
            deltaSortChild,
            child,
            zebraParent,
            appleParent,
            bravoSortParent,
            deltaSortParent);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Set<TagParent>().AddRange(
            new TagParent { ParentId = parent.Id, ChildId = zebraChild.Id },
            new TagParent { ParentId = parent.Id, ChildId = appleChild.Id },
            new TagParent { ParentId = parent.Id, ChildId = bravoSortChild.Id },
            new TagParent { ParentId = parent.Id, ChildId = deltaSortChild.Id },
            new TagParent { ParentId = zebraParent.Id, ChildId = child.Id },
            new TagParent { ParentId = appleParent.Id, ChildId = child.Id },
            new TagParent { ParentId = bravoSortParent.Id, ChildId = child.Id },
            new TagParent { ParentId = deltaSortParent.Id, ChildId = child.Id });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = new TagsController(null!, context, new CustomFieldService(context), null!);

        var parentDetailResult = await controller.GetById(parent.Id, CancellationToken.None);
        var parentDetailOk = Assert.IsType<OkObjectResult>(parentDetailResult.Result);
        var parentDetail = Assert.IsType<TagDetailDto>(parentDetailOk.Value);
        Assert.Equal(
            [appleChild.Name, bravoSortChild.Name, deltaSortChild.Name, zebraChild.Name],
            parentDetail.Children.Select(tag => tag.Name).ToList());

        var childDetailResult = await controller.GetById(child.Id, CancellationToken.None);
        var childDetailOk = Assert.IsType<OkObjectResult>(childDetailResult.Result);
        var childDetail = Assert.IsType<TagDetailDto>(childDetailOk.Value);
        Assert.Equal(
            [appleParent.Name, bravoSortParent.Name, deltaSortParent.Name, zebraParent.Name],
            childDetail.Parents.Select(tag => tag.Name).ToList());
    }

    [Fact]
    public async Task GetSegmentTitles_UsesVideoSegmentTitles()
    {
        await using var context = CreateContext();
        context.Segments.AddRange(
            new Segment
            {
                HostType = SegmentHostType.Video,
                HostId = 1,
                SourceKey = "user",
                StartSec = 5.0,
                Title = "Manual body",
            },
            new Segment
            {
                HostType = SegmentHostType.Video,
                HostId = 2,
                SourceKey = "user",
                StartSec = 15.0,
                Title = "Manual body",
            },
            new Segment
            {
                HostType = SegmentHostType.Video,
                HostId = 3,
                SourceKey = "user",
                StartSec = 25.0,
                Title = "AI body",
            },
            new Segment
            {
                HostType = SegmentHostType.Image,
                HostId = 4,
                SourceKey = "user",
                StartSec = 0,
                Title = "Image-only title",
            },
            new Segment
            {
                HostType = SegmentHostType.Video,
                HostId = 5,
                SourceKey = "user",
                StartSec = 35.0,
                Title = null,
            });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = new TagsController(null!, context, new CustomFieldService(context), null!);

        var alphabeticalResult = await controller.GetSegmentTitles(null, null, CancellationToken.None);
        var alphabetical = Assert.IsType<OkObjectResult>(alphabeticalResult.Result).Value as List<string>;
        Assert.NotNull(alphabetical);
        Assert.Equal(["AI body", "Manual body"], alphabetical);

        var countedResult = await controller.GetSegmentTitles(null, "count", CancellationToken.None);
        var counted = Assert.IsType<OkObjectResult>(countedResult.Result).Value as List<string>;
        Assert.NotNull(counted);
        Assert.Equal("Manual body", counted![0]);
        Assert.DoesNotContain("Image-only title", counted);

        var filteredResult = await controller.GetSegmentTitles("manual", null, CancellationToken.None);
        var filtered = Assert.IsType<OkObjectResult>(filteredResult.Result).Value as List<string>;
        Assert.NotNull(filtered);
        Assert.Equal(["Manual body"], filtered);
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"tags-controller-segments-{Guid.NewGuid():N}")
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
