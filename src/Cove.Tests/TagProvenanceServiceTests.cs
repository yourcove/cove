using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Tests;

public sealed class TagProvenanceServiceTests
{
    [Fact]
    public async Task SyncTagSetAsync_AddsUserRowsForNewTagsAndDeletesOnlyMatchingSourceProvenance()
    {
        await using var context = CreateContext();

        var video = new Video { Title = "Tagged Video" };
        var manualTag = new Tag { Name = "Manual" };
        var keptTag = new Tag { Name = "Kept" };
        var addedTag = new Tag { Name = "Added" };

        context.AddRange(video, manualTag, keptTag, addedTag);
        await context.SaveChangesAsync();

        context.TagApplications.AddRange(
            new TagApplication
            {
                HostType = AffinityHostType.Video,
                HostId = video.Id,
                TagId = manualTag.Id,
                SourceKey = "ext:ai.tagging",
                SourceRunId = "run-1",
                ModelKey = "tagger-v1",
                Confidence = 0.91f,
            },
            new TagApplication
            {
                HostType = AffinityHostType.Video,
                HostId = video.Id,
                TagId = manualTag.Id,
                SourceKey = "user",
            },
            new TagApplication
            {
                HostType = AffinityHostType.Video,
                HostId = video.Id,
                TagId = keptTag.Id,
                SourceKey = "ext:ai.tagging",
                SourceRunId = "run-2",
                ModelKey = "tagger-v1",
                Confidence = 0.77f,
            });
        await context.SaveChangesAsync();

        ITagProvenanceService service = new TagProvenanceService(context);

        await service.SyncTagSetAsync(
            AffinityHostType.Video,
            video.Id,
            [manualTag.Id, keptTag.Id],
            [keptTag.Id, addedTag.Id]);
        await context.SaveChangesAsync();

        var applications = await context.TagApplications
            .Where(application => application.HostType == AffinityHostType.Video && application.HostId == video.Id)
            .OrderBy(application => application.TagId)
            .ThenBy(application => application.SourceKey)
            .ToListAsync();

        Assert.Contains(applications, application => application.TagId == manualTag.Id && application.SourceKey == "ext:ai.tagging");
        Assert.DoesNotContain(applications, application => application.TagId == manualTag.Id && application.SourceKey == "user");
        Assert.Contains(applications, application => application.TagId == keptTag.Id && application.SourceKey == "ext:ai.tagging");
        Assert.Contains(applications, application => application.TagId == addedTag.Id && application.SourceKey == "user");
        Assert.DoesNotContain(applications, application => application.TagId == keptTag.Id && application.SourceKey == "user");
    }

    [Fact]
    public async Task RecordAsync_UpdatesExistingConfidenceForMatchingSource()
    {
        await using var context = CreateContext();

        var image = new Image { Title = "Tagged Image" };
        var tag = new Tag { Name = "Action" };
        context.AddRange(image, tag);
        await context.SaveChangesAsync();

        ITagProvenanceService service = new TagProvenanceService(context);

        await service.RecordAsync(AffinityHostType.Image, image.Id, tag.Id, "ext:ai.tagging", "run-1", "tagger-v1", 0.41f);
        await service.RecordAsync(AffinityHostType.Image, image.Id, tag.Id, "ext:ai.tagging", "run-1", "tagger-v1", 0.83f);
        await context.SaveChangesAsync();

        var applications = await context.TagApplications
            .Where(application => application.HostType == AffinityHostType.Image && application.HostId == image.Id)
            .ToListAsync();

        var application = Assert.Single(applications);
        Assert.Equal(0.83f, application.Confidence);
    }

    [Fact]
    public async Task VideosController_CreateUpdateAndDelete_TracksUserTagProvenance()
    {
        await using var context = CreateContext();

        var firstTag = new Tag { Name = "First" };
        var secondTag = new Tag { Name = "Second" };
        context.AddRange(firstTag, secondTag);
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
            new EventBus(),
            new TagProvenanceService(context));

        var createResult = await controller.Create(
            new VideoCreateDto("Tagged Video", null, null, null, null, null, false, null, null, null, [firstTag.Id], null, null, null),
            CancellationToken.None);
        var created = Assert.IsType<CreatedAtActionResult>(createResult.Result);
        var createdVideo = Assert.IsType<VideoDto>(created.Value);

        var createdApplications = await context.TagApplications
            .Where(application => application.HostType == AffinityHostType.Video && application.HostId == createdVideo.Id)
            .ToListAsync();
        Assert.Single(createdApplications);
        Assert.Equal(firstTag.Id, createdApplications[0].TagId);
        Assert.Equal("user", createdApplications[0].SourceKey);

        var updateResult = await controller.Update(
            createdVideo.Id,
            new VideoUpdateDto(null, null, null, null, null, null, null, null, null, null, [secondTag.Id], null, null, null, null, null),
            CancellationToken.None);
        var updated = Assert.IsType<OkObjectResult>(updateResult.Result);
        Assert.IsType<VideoDto>(updated.Value);

        var updatedApplications = await context.TagApplications
            .Where(application => application.HostType == AffinityHostType.Video && application.HostId == createdVideo.Id)
            .ToListAsync();
        var updatedApplication = Assert.Single(updatedApplications);
        Assert.Equal(secondTag.Id, updatedApplication.TagId);
        Assert.Equal("user", updatedApplication.SourceKey);

        var deleteResult = await controller.Delete(createdVideo.Id, false, false, CancellationToken.None);
        Assert.IsType<NoContentResult>(deleteResult);
        Assert.Empty(await context.TagApplications.Where(application => application.HostType == AffinityHostType.Video && application.HostId == createdVideo.Id).ToListAsync());
    }

    [Fact]
    public async Task VideoMetadataApplyService_RecordsScraperTagProvenance()
    {
        await using var context = CreateContext();

        var video = new Video { Title = "Scraped Video" };
        context.Videos.Add(video);
        await context.SaveChangesAsync();

        var service = new VideoMetadataApplyService(context, new EventBus(), new NoOpVideoCoverService(), new TagProvenanceService(context));

        var applied = await service.ApplyAsync(
            video.Id,
            new ScrapedVideoDto
            {
                TagNames = ["Body"],
            },
            new DownloaderMetadataApplyOptions(CreateMissingTags: true),
            CancellationToken.None);

        Assert.True(applied);

        var application = await context.TagApplications.SingleAsync();
        var tag = await context.Tags.SingleAsync();

        Assert.Equal(AffinityHostType.Video, application.HostType);
        Assert.Equal(video.Id, application.HostId);
        Assert.Equal(tag.Id, application.TagId);
        Assert.Equal("scraper:local", application.SourceKey);
    }

    [Fact]
    public async Task FieldProvenanceService_RecordAsync_UpsertsMatchingSourceField()
    {
        await using var context = CreateContext();
        var video = new Video { Title = "Tracked Video" };
        context.Videos.Add(video);
        await context.SaveChangesAsync();

        var service = new FieldProvenanceService(context);
        await service.RecordAsync(AffinityHostType.Video, video.Id, "Title", "First Title", "scraper");
        await context.SaveChangesAsync();
        await service.RecordAsync(AffinityHostType.Video, video.Id, "title", "Second Title", "scraper");
        await context.SaveChangesAsync();

        var rows = await service.GetForHostAsync(AffinityHostType.Video, video.Id);
        var row = Assert.Single(rows);

        Assert.Equal("title", row.FieldKey);
        Assert.Equal("scraper:local", row.SourceKey);
        Assert.True(row.Value.HasValue);
        Assert.Equal("Second Title", row.Value.Value.GetString());
    }

    [Fact]
    public async Task VideoMetadataApplyService_RecordsScraperFieldProvenance()
    {
        await using var context = CreateContext();

        var video = new Video { Title = "Original Video" };
        context.Videos.Add(video);
        await context.SaveChangesAsync();

        var fieldProvenance = new FieldProvenanceService(context);
        var service = new VideoMetadataApplyService(context, new EventBus(), new NoOpVideoCoverService(), new TagProvenanceService(context), fieldProvenance);

        var applied = await service.ApplyAsync(
            video.Id,
            new ScrapedVideoDto
            {
                Title = "Scraped Title",
                Details = "Scraped details",
                Date = "2024-05-01",
                Urls = ["https://example.com/watch/field-provenance"],
                PerformerNames = ["Performer One"],
            },
            new DownloaderMetadataApplyOptions(CreateMissingPerformers: true),
            CancellationToken.None);

        Assert.True(applied);

        var rows = await fieldProvenance.GetForHostAsync(AffinityHostType.Video, video.Id);
        Assert.Contains(rows, row => row.FieldKey == "title" && row.Value.HasValue && row.Value.Value.GetString() == "Scraped Title");
        Assert.Contains(rows, row => row.FieldKey == "details" && row.Value.HasValue && row.Value.Value.GetString() == "Scraped details");
        Assert.Contains(rows, row => row.FieldKey == "date" && row.Value.HasValue && row.Value.Value.GetString() == "2024-05-01");
        var performers = Assert.Single(rows, row => row.FieldKey == "performers");
        Assert.True(performers.Value.HasValue);
        Assert.Contains(performers.Value.Value.EnumerateArray(), value => value.GetString() == "Performer One");
    }

    [Fact]
    public async Task GetLookupAsync_UsesSeparateContextWhenCallerHasActiveReader()
    {
        await using var callerContext = CreateContext();
        callerContext.AddRange(
            new Video { Id = 1, Title = "Caller Video" },
            new Tag { Id = 1, Name = "Action" },
            new AiRun
            {
                RunKey = "run-1",
                SourceKey = "ext:ai.tagging",
                TargetType = AiRunTargetType.Video,
                TargetId = 1,
                Status = AiRunStatus.Completed,
                StartedAt = DateTime.UtcNow.AddMinutes(-2),
                CompletedAt = DateTime.UtcNow.AddMinutes(-1),
            });
        await callerContext.SaveChangesAsync();

        await using var lookupContext = CreateContext();
        lookupContext.AddRange(
            new Video { Id = 1, Title = "Lookup Video" },
            new Tag { Id = 1, Name = "Action" },
            new TagApplication
            {
                HostType = AffinityHostType.Video,
                HostId = 1,
                TagId = 1,
                SourceKey = "ext:ai.tagging",
                SourceRunId = "run-1",
                ModelKey = "tagger-v1",
                Confidence = 0.91f,
            });
        await lookupContext.SaveChangesAsync();

        var scopeFactory = new FixedScopeFactory(lookupContext);
        var service = new TagProvenanceService(callerContext, scopeFactory);

        await using var reader = callerContext.AiRuns
            .AsNoTracking()
            .AsAsyncEnumerable()
            .GetAsyncEnumerator();

        Assert.True(await reader.MoveNextAsync());

        var fallbackLookup = await new TagProvenanceService(callerContext).GetLookupAsync(AffinityHostType.Video, 1, [1]);
        Assert.Empty(fallbackLookup);

        var lookup = await service.GetLookupAsync(AffinityHostType.Video, 1, [1]);

        Assert.True(scopeFactory.ScopeCreated);
        var provenance = Assert.Single(lookup[1]);
        Assert.Equal("ext:ai.tagging", provenance.SourceKey);
        Assert.Equal("tagger-v1", provenance.ModelKey);
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"tag-provenance-{Guid.NewGuid():N}")
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

    private sealed class FixedScopeFactory(CoveContext context) : IServiceScopeFactory
    {
        public bool ScopeCreated { get; private set; }

        public IServiceScope CreateScope()
        {
            ScopeCreated = true;
            return new FixedScope(context);
        }
    }

    private sealed class FixedScope(CoveContext context) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new ServiceCollection()
            .AddScoped<CoveContext>(_ => context)
            .BuildServiceProvider();

        public void Dispose()
        {
        }
    }

    private sealed class NoOpVideoCoverService : IVideoCoverService
    {
        public Task<bool> TryApplyRemoteCoverAsync(Video video, string? imageUrl, CancellationToken ct = default)
            => Task.FromResult(false);
    }
}

