using Cove.Api.Controllers;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public class EntityDetailCountControllerTests
{
    [Fact]
    public async Task PerformerDetail_UsesLiveUsageCountsInsteadOfStoredCounters()
    {
        var principalAccessor = new CurrentPrincipalAccessor();
        principalAccessor.Set(new CovePrincipal
        {
            UserId = 1,
            Username = "performer-count-user",
            Kind = PrincipalKind.User,
            Permissions = new HashSet<string> { "*" },
            Roles = new HashSet<string>(),
        });
        await using var context = CreateContext(principalAccessor);

        var performer = new Performer { Name = "Performer" };
        var video = new Video { Title = "Video" };
        var image = new Image { Title = "Image" };
        var gallery = new Gallery { Title = "Gallery" };
        var group = new Group { Name = "Group" };
        context.AddRange(performer, video, image, gallery, group);
        await context.SaveChangesAsync();

        context.AddRange(
            new VideoPerformer { VideoId = video.Id, PerformerId = performer.Id },
            new UserEntityAffinity { UserId = 1, HostType = AffinityHostType.Video, HostId = video.Id, LikeCount = 3 },
            new UserEntityAffinity { UserId = 2, HostType = AffinityHostType.Video, HostId = video.Id, LikeCount = 7 },
            new ImagePerformer { ImageId = image.Id, PerformerId = performer.Id },
            new GalleryPerformer { GalleryId = gallery.Id, PerformerId = performer.Id },
            new GroupItem
            {
                GroupId = group.Id,
                Kind = GroupItemKind.Video,
                HostType = "video",
                HostId = video.Id,
                VideoId = video.Id,
            });
        await context.SaveChangesAsync();

        var storedPerformer = await context.Performers.SingleAsync(candidate => candidate.Id == performer.Id);
        storedPerformer.VideoCount = 0;
        storedPerformer.ImageCount = 0;
        storedPerformer.GalleryCount = 0;
        await context.SaveChangesAsync();

        var controller = new PerformersController(
            new PerformerRepository(context),
            null!,
            null!,
            context,
            null!,
            null!);

        var detailResult = await controller.GetById(performer.Id, CancellationToken.None);
        var detail = Assert.IsType<OkObjectResult>(detailResult.Result).Value as PerformerDto;
        Assert.NotNull(detail);
        Assert.Equal(1, detail!.VideoCount);
        Assert.Equal(1, detail.ImageCount);
        Assert.Equal(1, detail.GalleryCount);
        Assert.Equal(1, detail.GroupCount);
        Assert.Equal(3, detail.LikeCount);

        var listResult = await controller.Find(null, page: 1, perPage: 10, ct: CancellationToken.None);
        var list = Assert.IsType<PaginatedResponse<PerformerDto>>(Assert.IsType<OkObjectResult>(listResult.Result).Value);
        Assert.Equal(3, Assert.Single(list.Items).LikeCount);
    }

    [Fact]
    public async Task PerformerDetail_ReturnsTagsSortedByEffectiveSortName()
    {
        await using var context = CreateContext();

        var performer = new Performer { Name = "Performer" };
        var zulu = new Tag { Name = "Zulu" };
        var alpha = new Tag { Name = "Alpha" };
        var bravoSort = new Tag { Name = "Xylophone", SortName = "Bravo" };
        var deltaSort = new Tag { Name = "Antelope", SortName = "Delta" };
        context.AddRange(performer, zulu, alpha, bravoSort, deltaSort);
        await context.SaveChangesAsync();

        context.AddRange(
            new PerformerTag { PerformerId = performer.Id, TagId = zulu.Id },
            new PerformerTag { PerformerId = performer.Id, TagId = alpha.Id },
            new PerformerTag { PerformerId = performer.Id, TagId = bravoSort.Id },
            new PerformerTag { PerformerId = performer.Id, TagId = deltaSort.Id });
        await context.SaveChangesAsync();

        var controller = new PerformersController(
            new PerformerRepository(context),
            null!,
            null!,
            context,
            null!,
            null!);

        var detailResult = await controller.GetById(performer.Id, CancellationToken.None);
        var detail = Assert.IsType<OkObjectResult>(detailResult.Result).Value as PerformerDto;

        Assert.NotNull(detail);
        Assert.Equal(["Alpha", "Xylophone", "Antelope", "Zulu"], detail!.Tags.Select(tag => tag.Name).ToArray());
    }

    [Fact]
    public async Task StudioDetail_UsesLiveUsageCountsInsteadOfStoredCounters()
    {
        await using var context = CreateContext();

        var studio = new Studio { Name = "Studio" };
        var childStudio = new Studio { Name = "Child", Parent = studio };
        var video = new Video { Title = "Video", Studio = studio };
        var image = new Image { Title = "Image", Studio = studio };
        var gallery = new Gallery { Title = "Gallery", Studio = studio };
        var group = new Group { Name = "Group", Studio = studio };
        var performerA = new Performer { Name = "A" };
        var performerB = new Performer { Name = "B" };
        context.AddRange(studio, childStudio, video, image, gallery, group, performerA, performerB);
        await context.SaveChangesAsync();

        context.AddRange(
            new VideoPerformer { VideoId = video.Id, PerformerId = performerA.Id },
            new VideoPerformer { VideoId = video.Id, PerformerId = performerB.Id });
        await context.SaveChangesAsync();

        var storedStudio = await context.Studios.SingleAsync(candidate => candidate.Id == studio.Id);
        storedStudio.VideoCount = 0;
        storedStudio.ImageCount = 0;
        storedStudio.GalleryCount = 0;
        storedStudio.GroupCount = 0;
        storedStudio.PerformerCount = 0;
        storedStudio.ChildStudioCount = 0;
        await context.SaveChangesAsync();

        var controller = new StudiosController(
            new StudioRepository(context),
            null!,
            context,
            null!);

        var detailResult = await controller.GetById(studio.Id, CancellationToken.None);
        var detail = Assert.IsType<OkObjectResult>(detailResult.Result).Value as StudioDto;
        Assert.NotNull(detail);
        Assert.Equal(1, detail!.VideoCount);
        Assert.Equal(1, detail.ImageCount);
        Assert.Equal(1, detail.GalleryCount);
        Assert.Equal(1, detail.GroupCount);
        Assert.Equal(2, detail.PerformerCount);
        Assert.Equal(1, detail.ChildStudioCount);

        var childVideo = new Video { Title = "Child Video", StudioId = childStudio.Id };
        context.Videos.Add(childVideo);
        await context.SaveChangesAsync();
        context.Set<VideoPerformer>().Add(new VideoPerformer { VideoId = childVideo.Id, PerformerId = performerA.Id });
        await context.SaveChangesAsync();

        var recursiveResult = await controller.GetById(studio.Id, CancellationToken.None, -1);
        var recursiveDetail = Assert.IsType<OkObjectResult>(recursiveResult.Result).Value as StudioDto;
        Assert.NotNull(recursiveDetail);
        Assert.Equal(2, recursiveDetail!.VideoCount);
        Assert.Equal(2, recursiveDetail.PerformerCount);
    }

    [Fact]
    public async Task GalleryListAndDetail_UseLiveUsageCountsInsteadOfStoredCounters()
    {
        await using var context = CreateContext();

        var studio = new Studio { Name = "Studio" };
        var performer = new Performer { Name = "Performer" };
        var gallery = new Gallery { Title = "Gallery", Studio = studio };
        var imageA = new Image { Title = "Image A" };
        var imageB = new Image { Title = "Image B" };
        var video = new Video { Title = "Video" };
        context.AddRange(studio, performer, gallery, imageA, imageB, video);
        await context.SaveChangesAsync();

        context.AddRange(
            new ImageGallery { GalleryId = gallery.Id, ImageId = imageA.Id },
            new ImageGallery { GalleryId = gallery.Id, ImageId = imageB.Id },
            new VideoGallery { GalleryId = gallery.Id, VideoId = video.Id },
            new GalleryPerformer { GalleryId = gallery.Id, PerformerId = performer.Id });
        await context.SaveChangesAsync();

        var storedGallery = await context.Galleries.SingleAsync(candidate => candidate.Id == gallery.Id);
        storedGallery.ImageCount = 0;
        storedGallery.VideoCount = 0;
        await context.SaveChangesAsync();

        var controller = new GalleriesController(
            new GalleryRepository(context),
            context,
            new NoOpUserEngagementService(),
            new NoOpScanService())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var listResult = await controller.Find(q: null, page: 1, perPage: 10, sort: "title", direction: "asc", ct: CancellationToken.None);
        var list = Assert.IsType<PaginatedResponse<GalleryDto>>(Assert.IsType<OkObjectResult>(listResult.Result).Value);
        var listGallery = Assert.Single(list.Items);
        Assert.Equal(2, listGallery.ImageCount);
        Assert.Equal(1, listGallery.VideoCount);
        Assert.Equal(studio.Id, listGallery.StudioId);
        Assert.Equal("Studio", listGallery.StudioName);
        var listPerformer = Assert.Single(listGallery.Performers);
        Assert.Equal(performer.Id, listPerformer.Id);
        Assert.Equal("Performer", listPerformer.Name);
        Assert.NotNull(listGallery.CoverPath);

        var detailResult = await controller.GetById(gallery.Id, CancellationToken.None);
        var detail = Assert.IsType<GalleryDto>(Assert.IsType<OkObjectResult>(detailResult.Result).Value);
        Assert.Equal(2, detail.ImageCount);
        Assert.Equal(1, detail.VideoCount);
        Assert.NotNull(detail.CoverPath);
    }

    private static CoveContext CreateContext(ICurrentPrincipalAccessor? principalAccessor = null)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"entity-detail-counts-{Guid.NewGuid():N}")
            .Options;

        return new TestCoveContext(options, principalAccessor);
    }

    private sealed class TestCoveContext(DbContextOptions<CoveContext> options, ICurrentPrincipalAccessor? principalAccessor = null) : CoveContext(options, principalAccessor)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
        }
    }
}
