using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using GalleriesController = Cove.Api.Controllers.GalleriesController;
using VideosController = Cove.Api.Controllers.VideosController;

namespace Cove.Tests;

public sealed class Phase10TagProvenanceTests
{
    [Fact]
    public async Task VideosController_GetById_IncludesProvenance()
    {
        await using var context = CreateContext();

        var tag = new Tag { Name = "Detected" };
        var video = new Video { Title = "Video with provenance" };
        video.VideoTags.Add(new VideoTag { Video = video, Tag = tag });

        context.AddRange(tag, video);
        await context.SaveChangesAsync();

        context.TagApplications.Add(new TagApplication
        {
            HostType = AffinityHostType.Video,
            HostId = video.Id,
            TagId = tag.Id,
            SourceKey = "ext:ai.tagging",
            SourceRunId = "run-video",
            ModelKey = "model-video",
            Confidence = 0.82f,
        });
        await context.SaveChangesAsync();

        var controller = new VideosController(
            new VideoRepository(context),
            context,
            null!,
            null!,
            null!,
            new MemoryCache(new MemoryCacheOptions()),
            null!,
            null!,
            new NoOpUserEngagementService(),
            new CustomFieldService(context),
            new TagProvenanceService(context));

        var result = await controller.GetById(video.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<VideoDto>(ok.Value);
        var dtoTag = Assert.Single(dto.Tags);
        var provenance = Assert.Single(dtoTag.Provenance!);

        Assert.Equal("ext:ai.tagging", provenance.SourceKey);
        Assert.Equal("run-video", provenance.SourceRunId);
        Assert.Equal("model-video", provenance.ModelKey);
        Assert.Equal(0.82f, provenance.Confidence);
    }

    [Fact]
    public async Task GalleriesController_GetById_IncludesProvenance()
    {
        await using var context = CreateContext();

        var tag = new Tag { Name = "Generated" };
        var gallery = new Gallery { Title = "Gallery with provenance" };
        gallery.GalleryTags.Add(new GalleryTag { Gallery = gallery, Tag = tag });

        context.AddRange(tag, gallery);
        await context.SaveChangesAsync();

        context.TagApplications.Add(new TagApplication
        {
            HostType = AffinityHostType.Gallery,
            HostId = gallery.Id,
            TagId = tag.Id,
            SourceKey = "ext:ai.tagging",
            SourceRunId = "run-gallery",
            ModelKey = "model-gallery",
            Confidence = 0.67f,
        });
        await context.SaveChangesAsync();

        var controller = new GalleriesController(
            new GalleryRepository(context),
            context,
            new NoOpUserEngagementService(),
            new NoOpScanService(),
            new TagProvenanceService(context));

        var result = await controller.GetById(gallery.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<GalleryDto>(ok.Value);
        var dtoTag = Assert.Single(dto.Tags);
        var provenance = Assert.Single(dtoTag.Provenance!);

        Assert.Equal("ext:ai.tagging", provenance.SourceKey);
        Assert.Equal("run-gallery", provenance.SourceRunId);
        Assert.Equal("model-gallery", provenance.ModelKey);
        Assert.Equal(0.67f, provenance.Confidence);
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"phase10-tag-provenance-{Guid.NewGuid():N}")
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
