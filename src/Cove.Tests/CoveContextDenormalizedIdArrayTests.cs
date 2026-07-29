using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Cove.Tests;

public sealed class CoveContextDenormalizedIdArrayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChanges_NewMediaGraphs_StoreGeneratedPerformerAndTagIds(bool saveAsynchronously)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new CoveContext(options);
        await context.Database.EnsureCreatedAsync();

        var performer = new Performer { Name = "New performer" };
        var tag = new Tag { Name = "New tag" };
        var video = new Video { Title = "New video" };
        video.VideoPerformers.Add(new VideoPerformer { Performer = performer });
        video.VideoTags.Add(new VideoTag { Tag = tag });
        var image = new Image { Title = "New image" };
        image.ImagePerformers.Add(new ImagePerformer { Performer = performer });
        image.ImageTags.Add(new ImageTag { Tag = tag });
        var gallery = new Gallery { Title = "New gallery" };
        gallery.GalleryPerformers.Add(new GalleryPerformer { Performer = performer });
        gallery.GalleryTags.Add(new GalleryTag { Tag = tag });
        context.AddRange(video, image, gallery);

        if (saveAsynchronously)
            await context.SaveChangesAsync();
        else
            context.SaveChanges();
        context.ChangeTracker.Clear();

        var savedVideo = await context.Videos.SingleAsync();
        var savedImage = await context.Images.SingleAsync();
        var savedGallery = await context.Galleries.SingleAsync();
        Assert.Equal([performer.Id], savedVideo.PerformerIds);
        Assert.Equal([tag.Id], savedVideo.TagIds);
        Assert.Equal([performer.Id], savedImage.PerformerIds);
        Assert.Equal([tag.Id], savedImage.TagIds);
        Assert.Equal([performer.Id], savedGallery.PerformerIds);
        Assert.Equal([tag.Id], savedGallery.TagIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChanges_ExistingVideoWithNewRelationships_ReplacesTemporaryIds(bool saveAsynchronously)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new CoveContext(options);
        await context.Database.EnsureCreatedAsync();

        var existingPerformer = new Performer { Name = "Existing performer" };
        var existingTag = new Tag { Name = "Existing tag" };
        var video = new Video { Title = "Existing video" };
        video.VideoPerformers.Add(new VideoPerformer { Performer = existingPerformer });
        video.VideoTags.Add(new VideoTag { Tag = existingTag });
        context.Videos.Add(video);
        await context.SaveChangesAsync();

        var performer = new Performer { Name = "New performer" };
        var tag = new Tag { Name = "New tag" };
        video.VideoPerformers.Add(new VideoPerformer { Performer = performer });
        video.VideoTags.Add(new VideoTag { Tag = tag });

        if (saveAsynchronously)
            await context.SaveChangesAsync();
        else
            context.SaveChanges();
        context.ChangeTracker.Clear();

        var saved = await context.Videos.SingleAsync();
        Assert.Equal([existingPerformer.Id, performer.Id], saved.PerformerIds);
        Assert.Equal([existingTag.Id, tag.Id], saved.TagIds);
    }

    [Fact]
    public async Task SaveChanges_ReplacingRelationshipWithSameKey_PreservesDerivedArray()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;

        int videoId;
        int performerId;
        int tagId;
        await using (var setup = new CoveContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            var performer = new Performer { Name = "Existing performer" };
            var tag = new Tag { Name = "Existing tag" };
            var video = new Video { Title = "Existing video" };
            video.VideoPerformers.Add(new VideoPerformer { Performer = performer });
            video.VideoTags.Add(new VideoTag { Tag = tag });
            setup.Add(video);
            await setup.SaveChangesAsync();
            videoId = video.Id;
            performerId = performer.Id;
            tagId = tag.Id;
        }

        await using (var update = new CoveContext(options))
        {
            var video = await update.Videos
                .Include(entity => entity.VideoPerformers)
                .Include(entity => entity.VideoTags)
                .SingleAsync(entity => entity.Id == videoId);
            video.VideoPerformers.Clear();
            video.VideoPerformers =
            [
                new VideoPerformer { VideoId = videoId, PerformerId = performerId },
            ];
            video.VideoTags.Clear();
            video.VideoTags =
            [
                new VideoTag { VideoId = videoId, TagId = tagId },
            ];
            await update.SaveChangesAsync();
        }

        await using var verification = new CoveContext(options);
        var stored = await verification.Videos
            .Include(video => video.VideoPerformers)
            .Include(video => video.VideoTags)
            .SingleAsync(video => video.Id == videoId);
        Assert.Equal([performerId], stored.VideoPerformers.Select(link => link.PerformerId).ToArray());
        Assert.Equal([performerId], stored.PerformerIds);
        Assert.Equal([tagId], stored.VideoTags.Select(link => link.TagId).ToArray());
        Assert.Equal([tagId], stored.TagIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChanges_ExistingVideoAssignedNewStudio_RefreshesGeneratedStudioCounts(bool saveAsynchronously)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new CoveContext(options);
        await context.Database.EnsureCreatedAsync();

        var existingPerformer = new Performer { Name = "Existing performer" };
        var video = new Video { Title = "Existing video" };
        video.VideoPerformers.Add(new VideoPerformer { Performer = existingPerformer });
        context.Videos.Add(video);
        await context.SaveChangesAsync();

        var scrapedPerformer = new Performer { Name = "Scraped performer" };
        var scrapedStudio = new Studio { Name = "Scraped studio" };
        video.Studio = scrapedStudio;
        video.VideoPerformers.Add(new VideoPerformer { Performer = scrapedPerformer });

        if (saveAsynchronously)
            await context.SaveChangesAsync();
        else
            context.SaveChanges();
        context.ChangeTracker.Clear();

        var savedStudio = await context.Studios.SingleAsync();
        Assert.Equal(1, savedStudio.VideoCount);
        Assert.Equal(2, savedStudio.PerformerCount);
    }

    [Fact]
    public async Task RecomputeAllDerivedCountsAsync_RepairsStaleMediaIdArrays()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new CoveContext(options);
        await context.Database.EnsureCreatedAsync();

        var performer = new Performer { Name = "Existing performer" };
        var tag = new Tag { Name = "Existing tag" };
        await context.AddRangeAsync(performer, tag);
        await context.SaveChangesAsync();

        var video = new Video { Title = "Existing video" };
        video.VideoPerformers.Add(new VideoPerformer { Performer = performer });
        video.VideoTags.Add(new VideoTag { Tag = tag });
        var image = new Image { Title = "Existing image" };
        image.ImagePerformers.Add(new ImagePerformer { Performer = performer });
        image.ImageTags.Add(new ImageTag { Tag = tag });
        var gallery = new Gallery { Title = "Existing gallery" };
        gallery.GalleryPerformers.Add(new GalleryPerformer { Performer = performer });
        gallery.GalleryTags.Add(new GalleryTag { Tag = tag });
        context.AddRange(video, image, gallery);
        await context.SaveChangesAsync();

        await context.Videos
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.PerformerIds, [0])
                .SetProperty(item => item.TagIds, Array.Empty<int>()));
        await context.Images
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.PerformerIds, [0])
                .SetProperty(item => item.TagIds, Array.Empty<int>()));
        await context.Galleries
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.PerformerIds, [0])
                .SetProperty(item => item.TagIds, Array.Empty<int>()));
        context.ChangeTracker.Clear();

        await context.RecomputeAllDerivedCountsAsync();
        context.ChangeTracker.Clear();

        var repairedVideo = await context.Videos.SingleAsync();
        var repairedImage = await context.Images.SingleAsync();
        var repairedGallery = await context.Galleries.SingleAsync();
        Assert.Equal([performer.Id], repairedVideo.PerformerIds);
        Assert.Equal([tag.Id], repairedVideo.TagIds);
        Assert.Equal([performer.Id], repairedImage.PerformerIds);
        Assert.Equal([tag.Id], repairedImage.TagIds);
        Assert.Equal([performer.Id], repairedGallery.PerformerIds);
        Assert.Equal([tag.Id], repairedGallery.TagIds);
    }

    [Fact]
    public async Task SaveChanges_DirectMediaArrayWritesWithoutLinkChanges_AreIgnored()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new CoveContext(options);
        await context.Database.EnsureCreatedAsync();

        var performer = new Performer { Name = "Existing performer" };
        var tag = new Tag { Name = "Existing tag" };
        var video = new Video { Title = "Existing video" };
        video.VideoPerformers.Add(new VideoPerformer { Performer = performer });
        video.VideoTags.Add(new VideoTag { Tag = tag });
        var image = new Image { Title = "Existing image" };
        image.ImagePerformers.Add(new ImagePerformer { Performer = performer });
        image.ImageTags.Add(new ImageTag { Tag = tag });
        var gallery = new Gallery { Title = "Existing gallery" };
        gallery.GalleryPerformers.Add(new GalleryPerformer { Performer = performer });
        gallery.GalleryTags.Add(new GalleryTag { Tag = tag });
        context.AddRange(video, image, gallery);
        await context.SaveChangesAsync();

        video.PerformerIds = [];
        video.TagIds = [];
        image.PerformerIds = [];
        image.TagIds = [];
        gallery.PerformerIds = [];
        gallery.TagIds = [];
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var storedVideo = await context.Videos.SingleAsync();
        var storedImage = await context.Images.SingleAsync();
        var storedGallery = await context.Galleries.SingleAsync();
        Assert.Equal([performer.Id], storedVideo.PerformerIds);
        Assert.Equal([tag.Id], storedVideo.TagIds);
        Assert.Equal([performer.Id], storedImage.PerformerIds);
        Assert.Equal([tag.Id], storedImage.TagIds);
        Assert.Equal([performer.Id], storedGallery.PerformerIds);
        Assert.Equal([tag.Id], storedGallery.TagIds);
    }

    [Fact]
    public async Task StaleVideoUpdate_PreservesPerformerArrayAndAuthoritativeJoin()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;

        int videoId;
        int firstPerformerId;
        await using (var setup = new CoveContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            var firstPerformer = new Performer { Name = "First performer" };
            var video = new Video { Title = "Original title" };
            video.VideoPerformers.Add(new VideoPerformer { Performer = firstPerformer });
            setup.Add(video);
            await setup.SaveChangesAsync();
            videoId = video.Id;
            firstPerformerId = firstPerformer.Id;
        }

        await using var staleContext = new CoveContext(options);
        var staleVideo = await staleContext.Videos
            .Include(video => video.VideoPerformers)
            .SingleAsync(video => video.Id == videoId);

        int secondPerformerId;
        await using (var relationshipContext = new CoveContext(options))
        {
            var currentVideo = await relationshipContext.Videos
                .Include(video => video.VideoPerformers)
                .SingleAsync(video => video.Id == videoId);
            var secondPerformer = new Performer { Name = "Second performer" };
            currentVideo.VideoPerformers.Add(new VideoPerformer { Performer = secondPerformer });
            await relationshipContext.SaveChangesAsync();
            secondPerformerId = secondPerformer.Id;
        }

        staleVideo.Title = "Unrelated title update";
        staleContext.Videos.Update(staleVideo);
        await staleContext.SaveChangesAsync();
        staleContext.ChangeTracker.Clear();

        var stored = await staleContext.Videos
            .Include(video => video.VideoPerformers)
            .SingleAsync(video => video.Id == videoId);
        Assert.Equal([firstPerformerId, secondPerformerId], stored.PerformerIds);
        Assert.Equal(
            [firstPerformerId, secondPerformerId],
            stored.VideoPerformers.Select(link => link.PerformerId).Order().ToArray());
    }

    [Fact]
    public async Task VideoRepository_UpdateAsync_RejectsDetachedEntities()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new CoveContext(options);
        await context.Database.EnsureCreatedAsync();
        var repository = new VideoRepository(context);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.UpdateAsync(new Video { Id = 123, Title = "Detached video" }));

        Assert.Contains("requires an entity tracked", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateScreenshot_OverlappingPerformerImport_PreservesDerivedState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;

        int videoId;
        int firstPerformerId;
        await using (var setup = new CoveContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            var firstPerformer = new Performer { Name = "First performer" };
            var video = new Video { Title = "Original title" };
            video.VideoPerformers.Add(new VideoPerformer { Performer = firstPerformer });
            setup.Add(video);
            await setup.SaveChangesAsync();
            videoId = video.Id;
            firstPerformerId = firstPerformer.Id;
        }

        await using var screenshotContext = new CoveContext(options);
        var thumbnailService = new BlockingThumbnailService();
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var controller = new VideosController(
            new VideoRepository(screenshotContext),
            screenshotContext,
            null!,
            thumbnailService,
            null!,
            memoryCache,
            null!,
            null!,
            null!,
            null!,
            new EventBus());

        var screenshotRequest = controller.GenerateScreenshot(videoId, null, CancellationToken.None);
        await thumbnailService.GenerationStarted;

        int importedPerformerId;
        await using (var importContext = new CoveContext(options))
        {
            var currentVideo = await importContext.Videos
                .Include(video => video.VideoPerformers)
                .SingleAsync(video => video.Id == videoId);
            var importedPerformer = new Performer { Name = "Imported performer" };
            currentVideo.VideoPerformers.Add(new VideoPerformer { Performer = importedPerformer });
            await importContext.SaveChangesAsync();
            importedPerformerId = importedPerformer.Id;
        }

        thumbnailService.CompleteGeneration();
        Assert.IsType<OkObjectResult>(await screenshotRequest);
        screenshotContext.ChangeTracker.Clear();

        var stored = await screenshotContext.Videos
            .Include(video => video.VideoPerformers)
            .SingleAsync(video => video.Id == videoId);
        Assert.Equal([firstPerformerId, importedPerformerId], stored.PerformerIds);
        Assert.Equal(
            [firstPerformerId, importedPerformerId],
            stored.VideoPerformers.Select(link => link.PerformerId).Order().ToArray());
    }

    [Fact]
    public async Task SaveChanges_ConcurrentRelationshipAdds_PreserveBothDerivedArrayValues()
    {
        var databaseName = $"concurrent-derived-arrays-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared;Default Timeout=30";
        await using var keeperConnection = new SqliteConnection(connectionString);
        await keeperConnection.OpenAsync();
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connectionString)
            .Options;

        int videoId;
        int secondPerformerId;
        int thirdPerformerId;
        await using (var setup = new CoveContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            var firstPerformer = new Performer { Name = "First performer" };
            var secondPerformer = new Performer { Name = "Second performer" };
            var thirdPerformer = new Performer { Name = "Third performer" };
            var video = new Video { Title = "Concurrent relationship video" };
            video.VideoPerformers.Add(new VideoPerformer { Performer = firstPerformer });
            setup.AddRange(video, secondPerformer, thirdPerformer);
            await setup.SaveChangesAsync();
            videoId = video.Id;
            secondPerformerId = secondPerformer.Id;
            thirdPerformerId = thirdPerformer.Id;
        }

        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstSave = AddPerformerAsync(options, videoId, secondPerformerId, firstReady, start);
        var secondSave = AddPerformerAsync(options, videoId, thirdPerformerId, secondReady, start);
        await Task.WhenAll(firstReady.Task, secondReady.Task);
        start.TrySetResult();
        await Task.WhenAll(firstSave, secondSave);

        await using var verification = new CoveContext(options);
        var stored = await verification.Videos
            .Include(video => video.VideoPerformers)
            .SingleAsync(video => video.Id == videoId);
        var authoritativeIds = stored.VideoPerformers
            .Select(link => link.PerformerId)
            .Order()
            .ToArray();
        Assert.Equal(3, authoritativeIds.Length);
        Assert.Equal(authoritativeIds, stored.PerformerIds);
    }

    [Fact]
    public async Task SetCoverFromFrame_ConcurrentRequests_KeepWinnerAndDeleteLosingBlob()
    {
        var databaseName = $"concurrent-cover-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared;Default Timeout=30";
        await using var keeperConnection = new SqliteConnection(connectionString);
        await keeperConnection.OpenAsync();
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connectionString)
            .Options;

        int videoId;
        await using (var setup = new CoveContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            var video = new Video { Title = "Concurrent cover video", ImageBlobId = "old-cover" };
            setup.Add(video);
            await setup.SaveChangesAsync();
            videoId = video.Id;
        }

        await using var firstContext = new CoveContext(options);
        await using var secondContext = new CoveContext(options);
        var firstThumbnail = new BlockingThumbnailService();
        var secondThumbnail = new BlockingThumbnailService();
        var blobService = new RecordingBlobService();
        var streamService = new StaticScreenshotStreamService();
        using var firstCache = new MemoryCache(new MemoryCacheOptions());
        using var secondCache = new MemoryCache(new MemoryCacheOptions());
        var firstController = CreateCoverController(
            firstContext,
            firstThumbnail,
            firstCache,
            blobService,
            streamService);
        var secondController = CreateCoverController(
            secondContext,
            secondThumbnail,
            secondCache,
            blobService,
            streamService);

        var firstRequest = firstController.SetCoverFromFrame(videoId, null, CancellationToken.None);
        var secondRequest = secondController.SetCoverFromFrame(videoId, null, CancellationToken.None);
        await Task.WhenAll(firstThumbnail.GenerationStarted, secondThumbnail.GenerationStarted);

        firstThumbnail.CompleteGeneration();
        Assert.IsType<OkObjectResult>(await firstRequest);
        secondThumbnail.CompleteGeneration();
        Assert.IsType<ConflictObjectResult>(await secondRequest);

        await using var verification = new CoveContext(options);
        var storedBlobId = await verification.Videos
            .Where(video => video.Id == videoId)
            .Select(video => video.ImageBlobId)
            .SingleAsync();
        Assert.Equal("new-cover-1", storedBlobId);
        Assert.Contains("old-cover", blobService.DeletedBlobIds);
        Assert.Contains("new-cover-2", blobService.DeletedBlobIds);
        Assert.DoesNotContain("new-cover-1", blobService.DeletedBlobIds);
    }

    [Fact]
    public async Task SetCoverFromFrame_CleanupFailure_IsReported()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;

        int videoId;
        await using (var setup = new CoveContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            var video = new Video { Title = "Cover cleanup video", ImageBlobId = "old-cover" };
            setup.Add(video);
            await setup.SaveChangesAsync();
            videoId = video.Id;
        }

        await using var context = new CoveContext(options);
        var thumbnailService = new BlockingThumbnailService();
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var controller = CreateCoverController(
            context,
            thumbnailService,
            memoryCache,
            new FailingDeleteBlobService(),
            new StaticScreenshotStreamService());

        var request = controller.SetCoverFromFrame(videoId, null, CancellationToken.None);
        await thumbnailService.GenerationStarted;
        thumbnailService.CompleteGeneration();

        var exception = await Assert.ThrowsAsync<IOException>(() => request);
        Assert.Contains("old-cover", exception.Message, StringComparison.Ordinal);

        context.ChangeTracker.Clear();
        var storedBlobId = await context.Videos
            .Where(video => video.Id == videoId)
            .Select(video => video.ImageBlobId)
            .SingleAsync();
        Assert.Equal("new-cover", storedBlobId);
    }

    private static async Task AddPerformerAsync(
        DbContextOptions<CoveContext> options,
        int videoId,
        int performerId,
        TaskCompletionSource ready,
        TaskCompletionSource start)
    {
        await using var context = new CoveContext(options);
        var video = await context.Videos
            .Include(entity => entity.VideoPerformers)
            .SingleAsync(entity => entity.Id == videoId);
        ready.TrySetResult();
        await start.Task;
        video.VideoPerformers.Add(new VideoPerformer
        {
            VideoId = videoId,
            PerformerId = performerId,
        });
        await context.SaveChangesAsync();
    }

    private static VideosController CreateCoverController(
        CoveContext context,
        IThumbnailService thumbnailService,
        IMemoryCache memoryCache,
        IBlobService blobService,
        IStreamService streamService) =>
        new(
            new VideoRepository(context),
            context,
            null!,
            thumbnailService,
            null!,
            memoryCache,
            blobService,
            streamService,
            null!,
            null!,
            new EventBus());

    private sealed class BlockingThumbnailService : IThumbnailService
    {
        private readonly TaskCompletionSource _generationStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _generationMayComplete =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task GenerationStarted => _generationStarted.Task;
        public void CompleteGeneration() => _generationMayComplete.TrySetResult();

        public async Task GenerateVideoThumbnailAsync(int videoId, double? atSeconds = null, CancellationToken ct = default)
        {
            _generationStarted.TrySetResult();
            await _generationMayComplete.Task.WaitAsync(ct);
        }

        public Task<string?> GetVideoThumbnailPathAsync(int videoId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetImageFilePathAsync(int imageId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageStreamAsync(int imageId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageThumbnailStreamAsync(int imageId, int maxDimension = 640, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetBlobImageThumbnailStreamAsync(string blobId, int maxDimension = 640, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteVideoGeneratedFilesAsync(int videoId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteImageGeneratedFilesAsync(int imageId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteBlobGeneratedFilesAsync(string blobId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task GenerateImageThumbnailAsync(int imageId, int maxDimension = 640, bool overwrite = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task GenerateVideoPreviewAsync(int videoId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task GenerateSegmentAnimatedPreviewAsync(int videoId, double startSec, double? endSec = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task GenerateVideoSpriteAsync(int videoId, CancellationToken ct = default) => throw new NotSupportedException();
        public string GetThumbnailPathForVideo(int videoId) => throw new NotSupportedException();
        public string GetTimestampedThumbnailPath(int videoId, double seconds) => throw new NotSupportedException();
        public string GetSegmentAnimatedPreviewPath(int videoId, double seconds) => throw new NotSupportedException();
        public string GetPreviewPath(int videoId) => throw new NotSupportedException();
        public string GetSpritePath(int videoId) => throw new NotSupportedException();
        public string GetSpriteVttPath(int videoId) => throw new NotSupportedException();
        public string StartGenerateAllThumbnails() => throw new NotSupportedException();
    }

    private sealed class StaticScreenshotStreamService : IStreamService
    {
        public Task<(Stream stream, string contentType, long? fileSize)?> GetVideoStream(
            int videoId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<(Stream stream, string contentType, bool useLongCache)?> GetVideoScreenshot(
            int videoId,
            double? seconds,
            CancellationToken ct = default) =>
            Task.FromResult<(Stream, string, bool)?>(
                (new MemoryStream([1, 2, 3], writable: false), "image/jpeg", false));

        public Task<(Stream stream, string contentType, bool useLongCache)?> GetSegmentAnimatedPreview(
            int videoId,
            double seconds,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingBlobService : IBlobService
    {
        private int _storedBlobCount;

        public IReadOnlyCollection<string> DeletedBlobIds
        {
            get
            {
                lock (_deletedBlobIds)
                    return _deletedBlobIds.ToArray();
            }
        }

        private readonly List<string> _deletedBlobIds = [];

        public Task<string> StoreBlobAsync(
            Stream data,
            string contentType,
            CancellationToken ct = default) =>
            Task.FromResult($"new-cover-{Interlocked.Increment(ref _storedBlobCount)}");

        public Task<(Stream Stream, string ContentType)?> GetBlobAsync(
            string blobId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteBlobAsync(string blobId, CancellationToken ct = default)
        {
            lock (_deletedBlobIds)
                _deletedBlobIds.Add(blobId);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingDeleteBlobService : IBlobService
    {
        public Task<string> StoreBlobAsync(
            Stream data,
            string contentType,
            CancellationToken ct = default) =>
            Task.FromResult("new-cover");

        public Task<(Stream Stream, string ContentType)?> GetBlobAsync(
            string blobId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteBlobAsync(string blobId, CancellationToken ct = default) =>
            Task.FromException(new IOException($"Failed to delete {blobId}"));
    }
}
