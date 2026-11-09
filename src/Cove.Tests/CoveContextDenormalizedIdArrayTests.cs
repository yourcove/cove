using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Tests.Postgres;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Cove.Tests;

[RequiresPostgres]
public sealed class CoveContextDenormalizedIdArrayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChanges_NewMediaGraphs_StoreGeneratedPerformerAndTagIds(bool saveAsynchronously)
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var options = database.CreateOptions();

        await using var context = new CoveContext(options);

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
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        else
            context.SaveChanges();
        context.ChangeTracker.Clear();

        var savedVideo = await context.Videos.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var savedImage = await context.Images.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var savedGallery = await context.Galleries.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
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
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var options = database.CreateOptions();

        await using var context = new CoveContext(options);

        var existingPerformer = new Performer { Name = "Existing performer" };
        var existingTag = new Tag { Name = "Existing tag" };
        var video = new Video { Title = "Existing video" };
        video.VideoPerformers.Add(new VideoPerformer { Performer = existingPerformer });
        video.VideoTags.Add(new VideoTag { Tag = existingTag });
        context.Videos.Add(video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var performer = new Performer { Name = "New performer" };
        var tag = new Tag { Name = "New tag" };
        video.VideoPerformers.Add(new VideoPerformer { Performer = performer });
        video.VideoTags.Add(new VideoTag { Tag = tag });

        if (saveAsynchronously)
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        else
            context.SaveChanges();
        context.ChangeTracker.Clear();

        var saved = await context.Videos.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([existingPerformer.Id, performer.Id], saved.PerformerIds);
        Assert.Equal([existingTag.Id, tag.Id], saved.TagIds);
    }

    [Fact]
    public async Task SaveChanges_ReplacingRelationshipWithSameKey_PreservesDerivedArray()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var options = database.CreateOptions();

        int videoId;
        int performerId;
        int tagId;
        await using (var setup = new CoveContext(options))
        {
            var performer = new Performer { Name = "Existing performer" };
            var tag = new Tag { Name = "Existing tag" };
            var video = new Video { Title = "Existing video" };
            video.VideoPerformers.Add(new VideoPerformer { Performer = performer });
            video.VideoTags.Add(new VideoTag { Tag = tag });
            setup.Add(video);
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
            videoId = video.Id;
            performerId = performer.Id;
            tagId = tag.Id;
        }

        await using (var update = new CoveContext(options))
        {
            var video = await update.Videos
                .Include(entity => entity.VideoPerformers)
                .Include(entity => entity.VideoTags)
                .SingleAsync(entity => entity.Id == videoId, cancellationToken: TestContext.Current.CancellationToken);
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
            await update.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var verification = new CoveContext(options);
        var stored = await verification.Videos
            .Include(video => video.VideoPerformers)
            .Include(video => video.VideoTags)
            .SingleAsync(video => video.Id == videoId, cancellationToken: TestContext.Current.CancellationToken);
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
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var options = database.CreateOptions();

        await using var context = new CoveContext(options);

        var existingPerformer = new Performer { Name = "Existing performer" };
        var video = new Video { Title = "Existing video" };
        video.VideoPerformers.Add(new VideoPerformer { Performer = existingPerformer });
        context.Videos.Add(video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var scrapedPerformer = new Performer { Name = "Scraped performer" };
        var scrapedStudio = new Studio { Name = "Scraped studio" };
        video.Studio = scrapedStudio;
        video.VideoPerformers.Add(new VideoPerformer { Performer = scrapedPerformer });

        if (saveAsynchronously)
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        else
            context.SaveChanges();
        context.ChangeTracker.Clear();

        var savedStudio = await context.Studios.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, savedStudio.VideoCount);
        Assert.Equal(2, savedStudio.PerformerCount);
    }

    [Fact]
    public async Task RecomputeAllDerivedCountsAsync_RepairsStaleMediaIdArrays()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var options = database.CreateOptions();

        await using var context = new CoveContext(options);

        var performer = new Performer { Name = "Existing performer" };
        var tag = new Tag { Name = "Existing tag" };
        await context.AddRangeAsync(performer, tag);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

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
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await context.Videos
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.PerformerIds, [0])
                .SetProperty(item => item.TagIds, Array.Empty<int>()), cancellationToken: TestContext.Current.CancellationToken);
        await context.Images
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.PerformerIds, [0])
                .SetProperty(item => item.TagIds, Array.Empty<int>()), cancellationToken: TestContext.Current.CancellationToken);
        await context.Galleries
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.PerformerIds, [0])
                .SetProperty(item => item.TagIds, Array.Empty<int>()), cancellationToken: TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();

        await context.RecomputeAllDerivedCountsAsync(cancellationToken: TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();

        var repairedVideo = await context.Videos.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var repairedImage = await context.Images.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var repairedGallery = await context.Galleries.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
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
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var options = database.CreateOptions();
        await using var context = new CoveContext(options);

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
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        video.PerformerIds = [];
        video.TagIds = [];
        image.PerformerIds = [];
        image.TagIds = [];
        gallery.PerformerIds = [];
        gallery.TagIds = [];
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();

        var storedVideo = await context.Videos.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var storedImage = await context.Images.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var storedGallery = await context.Galleries.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
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
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var options = database.CreateOptions();

        int videoId;
        int firstPerformerId;
        await using (var setup = new CoveContext(options))
        {
            var firstPerformer = new Performer { Name = "First performer" };
            var video = new Video { Title = "Original title" };
            video.VideoPerformers.Add(new VideoPerformer { Performer = firstPerformer });
            setup.Add(video);
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
            videoId = video.Id;
            firstPerformerId = firstPerformer.Id;
        }

        await using var staleContext = new CoveContext(options);
        var staleVideo = await staleContext.Videos
            .Include(video => video.VideoPerformers)
            .SingleAsync(video => video.Id == videoId, cancellationToken: TestContext.Current.CancellationToken);

        int secondPerformerId;
        await using (var relationshipContext = new CoveContext(options))
        {
            var currentVideo = await relationshipContext.Videos
                .Include(video => video.VideoPerformers)
                .SingleAsync(video => video.Id == videoId, cancellationToken: TestContext.Current.CancellationToken);
            var secondPerformer = new Performer { Name = "Second performer" };
            currentVideo.VideoPerformers.Add(new VideoPerformer { Performer = secondPerformer });
            await relationshipContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            secondPerformerId = secondPerformer.Id;
        }

        staleVideo.Title = "Unrelated title update";
        staleContext.Videos.Update(staleVideo);
        await staleContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        staleContext.ChangeTracker.Clear();

        var stored = await staleContext.Videos
            .Include(video => video.VideoPerformers)
            .SingleAsync(video => video.Id == videoId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([firstPerformerId, secondPerformerId], stored.PerformerIds);
        Assert.Equal(
            [firstPerformerId, secondPerformerId],
            stored.VideoPerformers.Select(link => link.PerformerId).Order().ToArray());
    }

    [Fact]
    public async Task VideoRepository_UpdateAsync_RejectsDetachedEntities()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var options = database.CreateOptions();
        await using var context = new CoveContext(options);
        var repository = new VideoRepository(context);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.UpdateAsync(new Video { Id = 123, Title = "Detached video" }, TestContext.Current.CancellationToken));

        Assert.Contains("requires an entity tracked", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateScreenshot_OverlappingPerformerImport_PreservesDerivedState()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var options = database.CreateOptions();

        int videoId;
        int firstPerformerId;
        await using (var setup = new CoveContext(options))
        {
            var firstPerformer = new Performer { Name = "First performer" };
            var video = new Video { Title = "Original title" };
            video.VideoPerformers.Add(new VideoPerformer { Performer = firstPerformer });
            setup.Add(video);
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
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
        await WaitForSignalAsync(thumbnailService.GenerationStarted, screenshotRequest);

        int importedPerformerId;
        await using (var importContext = new CoveContext(options))
        {
            var currentVideo = await importContext.Videos
                .Include(video => video.VideoPerformers)
                .SingleAsync(video => video.Id == videoId, cancellationToken: TestContext.Current.CancellationToken);
            var importedPerformer = new Performer { Name = "Imported performer" };
            currentVideo.VideoPerformers.Add(new VideoPerformer { Performer = importedPerformer });
            await importContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            importedPerformerId = importedPerformer.Id;
        }

        thumbnailService.CompleteGeneration();
        Assert.IsType<OkObjectResult>(await screenshotRequest);
        screenshotContext.ChangeTracker.Clear();

        var stored = await screenshotContext.Videos
            .Include(video => video.VideoPerformers)
            .SingleAsync(video => video.Id == videoId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([firstPerformerId, importedPerformerId], stored.PerformerIds);
        Assert.Equal(
            [firstPerformerId, importedPerformerId],
            stored.VideoPerformers.Select(link => link.PerformerId).Order().ToArray());
    }

    [Fact]
    public async Task SaveChanges_ConcurrentRelationshipAdds_PreserveBothDerivedArrayValues()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var options = database.CreateOptions();

        int videoId;
        int secondPerformerId;
        int thirdPerformerId;
        await using (var setup = new CoveContext(options))
        {
            var firstPerformer = new Performer { Name = "First performer" };
            var secondPerformer = new Performer { Name = "Second performer" };
            var thirdPerformer = new Performer { Name = "Third performer" };
            var video = new Video { Title = "Concurrent relationship video" };
            video.VideoPerformers.Add(new VideoPerformer { Performer = firstPerformer });
            setup.AddRange(video, secondPerformer, thirdPerformer);
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
            videoId = video.Id;
            secondPerformerId = secondPerformer.Id;
            thirdPerformerId = thirdPerformer.Id;
        }

        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstSave = AddPerformerAsync(options, videoId, secondPerformerId, firstReady, start);
        var secondSave = AddPerformerAsync(options, videoId, thirdPerformerId, secondReady, start);
        await WaitForSignalAsync(Task.WhenAll(firstReady.Task, secondReady.Task), firstSave, secondSave);
        start.TrySetResult();
        await Task.WhenAll(firstSave, secondSave);

        await using var verification = new CoveContext(options);
        var stored = await verification.Videos
            .Include(video => video.VideoPerformers)
            .SingleAsync(video => video.Id == videoId, cancellationToken: TestContext.Current.CancellationToken);
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
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var options = database.CreateOptions();

        int videoId;
        await using (var setup = new CoveContext(options))
        {
            var video = new Video { Title = "Concurrent cover video", ImageBlobId = "old-cover" };
            setup.Add(video);
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
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
        await WaitForSignalAsync(Task.WhenAll(firstThumbnail.GenerationStarted, secondThumbnail.GenerationStarted), firstRequest, secondRequest);

        firstThumbnail.CompleteGeneration();
        Assert.IsType<OkObjectResult>(await firstRequest);
        secondThumbnail.CompleteGeneration();
        Assert.IsType<ConflictObjectResult>(await secondRequest);

        await using var verification = new CoveContext(options);
        var storedBlobId = await verification.Videos
            .Where(video => video.Id == videoId)
            .Select(video => video.ImageBlobId)
            .SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("new-cover-1", storedBlobId);
        Assert.Contains("old-cover", blobService.DeletedBlobIds);
        Assert.Contains("new-cover-2", blobService.DeletedBlobIds);
        Assert.DoesNotContain("new-cover-1", blobService.DeletedBlobIds);
    }

    [Fact]
    public async Task SetCoverFromFrame_CleanupFailure_IsReported()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var options = database.CreateOptions();

        int videoId;
        await using (var setup = new CoveContext(options))
        {
            var video = new Video { Title = "Cover cleanup video", ImageBlobId = "old-cover" };
            setup.Add(video);
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
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
        await WaitForSignalAsync(thumbnailService.GenerationStarted, request);
        thumbnailService.CompleteGeneration();

        var exception = await Assert.ThrowsAsync<IOException>(() => request);
        Assert.Contains("old-cover", exception.Message, StringComparison.Ordinal);

        context.ChangeTracker.Clear();
        var storedBlobId = await context.Videos
            .Where(video => video.Id == videoId)
            .Select(video => video.ImageBlobId)
            .SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("new-cover", storedBlobId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChanges_OutsideATransaction_ReleasesItsDerivedArrayLocks(bool saveAsynchronously)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        var (videoId, _, performerId) = await SeedVideoWithSparePerformerAsync(database, ct);

        await using var context = database.CreateContext();
        var video = await context.Videos.Include(entity => entity.VideoPerformers).SingleAsync(entity => entity.Id == videoId, ct);
        video.VideoPerformers.Add(new VideoPerformer { VideoId = videoId, PerformerId = performerId });
        if (saveAsynchronously)
            await context.SaveChangesAsync(ct);
        else
            context.SaveChanges();

        // Session-level locks outlive the save on the pooled connection, so a missed unlock would still show here.
        Assert.Equal(0, await CountDerivedArrayLocksAsync(database, ct));
    }

    [Fact]
    public async Task SaveChanges_FailingAfterTakingItsLocks_ReleasesThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        var (videoId, _, _) = await SeedVideoWithSparePerformerAsync(database, ct);

        await using var context = database.CreateContext();
        var video = await context.Videos.Include(entity => entity.VideoPerformers).SingleAsync(entity => entity.Id == videoId, ct);
        // No performer has this id, so the insert fails on its foreign key after the video's lock is taken.
        video.VideoPerformers.Add(new VideoPerformer { VideoId = videoId, PerformerId = int.MaxValue });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(ct));
        Assert.Equal(0, await CountDerivedArrayLocksAsync(database, ct));
    }

    [Fact]
    public async Task SaveChanges_InsideATransaction_HoldsTheDerivedArrayLockUntilCommit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        var (videoId, _, performerId) = await SeedVideoWithSparePerformerAsync(database, ct);

        await using var context = database.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var video = await context.Videos.Include(entity => entity.VideoPerformers).SingleAsync(entity => entity.Id == videoId, ct);
        video.VideoPerformers.Add(new VideoPerformer { VideoId = videoId, PerformerId = performerId });
        await context.SaveChangesAsync(ct);

        Assert.Equal(1, await CountDerivedArrayLocksAsync(database, ct, videoId, granted: true));
        await transaction.CommitAsync(ct);
        Assert.Equal(0, await CountDerivedArrayLocksAsync(database, ct));
    }

    [Fact]
    public async Task SaveChanges_WaitsForADerivedArrayLockHeldByAnotherConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        var (videoId, existingPerformerId, performerId) = await SeedVideoWithSparePerformerAsync(database, ct);

        // Stands in for another API process changing the same video's relationships.
        await using var otherProcess = await database.DataSource.OpenConnectionAsync(ct);
        await using (var hold = otherProcess.CreateCommand())
        {
            hold.CommandText = "SELECT pg_advisory_lock(@ns, @id)";
            hold.Parameters.AddWithValue("ns", VideoDerivedArrayLockNamespace);
            hold.Parameters.AddWithValue("id", videoId);
            await hold.ExecuteNonQueryAsync(ct);
        }

        await using var context = database.CreateContext();
        var video = await context.Videos.Include(entity => entity.VideoPerformers).SingleAsync(entity => entity.Id == videoId, ct);
        video.VideoPerformers.Add(new VideoPerformer { VideoId = videoId, PerformerId = performerId });
        var save = context.SaveChangesAsync(ct);

        await WaitForSignalAsync(WaitForWaitingDerivedArrayLockAsync(database, videoId, ct), save);
        Assert.False(save.IsCompleted);

        await using (var release = otherProcess.CreateCommand())
        {
            release.CommandText = "SELECT pg_advisory_unlock(@ns, @id)";
            release.Parameters.AddWithValue("ns", VideoDerivedArrayLockNamespace);
            release.Parameters.AddWithValue("id", videoId);
            await release.ExecuteNonQueryAsync(ct);
        }
        await save.WaitAsync(TimeSpan.FromSeconds(30), ct);

        context.ChangeTracker.Clear();
        var stored = await context.Videos.SingleAsync(entity => entity.Id == videoId, ct);
        Assert.Equal([existingPerformerId, performerId], stored.PerformerIds);
        Assert.Equal(0, await CountDerivedArrayLocksAsync(database, ct));
    }

    // CoveContext keys a video's derived-array lock as (0x434F5600 + 1, video id); images and galleries use + 2 and + 3.
    private const int VideoDerivedArrayLockNamespace = 0x434F5601;
    private const int GalleryDerivedArrayLockNamespace = 0x434F5603;

    private static async Task<(int VideoId, int ExistingPerformerId, int SparePerformerId)> SeedVideoWithSparePerformerAsync(
        CovePostgresDatabase database,
        CancellationToken ct)
    {
        await using var setup = database.CreateContext();
        var existingPerformer = new Performer { Name = "Existing performer" };
        var sparePerformer = new Performer { Name = "Spare performer" };
        var video = new Video { Title = "Locked video" };
        video.VideoPerformers.Add(new VideoPerformer { Performer = existingPerformer });
        setup.AddRange(video, sparePerformer);
        await setup.SaveChangesAsync(ct);
        return (video.Id, existingPerformer.Id, sparePerformer.Id);
    }

    // Counts derived-array advisory locks in this test's database. The count uses an unpooled connection: reusing a
    // pooled one resets it with DISCARD ALL, which would release a leaked session lock before it could be counted.
    private static async Task<long> CountDerivedArrayLocksAsync(
        CovePostgresDatabase database,
        CancellationToken ct,
        int? videoId = null,
        bool? granted = null)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false }.ConnectionString;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM pg_locks
            WHERE locktype = 'advisory' AND objsubid = 2
                AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                AND classid::bigint BETWEEN @firstNamespace AND @lastNamespace
                AND (@videoId::integer IS NULL OR (classid::bigint = @videoNamespace AND objid::bigint = @videoId::integer))
                AND (@granted::boolean IS NULL OR granted = @granted::boolean)
            """;
        command.Parameters.AddWithValue("firstNamespace", (long)VideoDerivedArrayLockNamespace);
        command.Parameters.AddWithValue("lastNamespace", (long)GalleryDerivedArrayLockNamespace);
        command.Parameters.AddWithValue("videoNamespace", (long)VideoDerivedArrayLockNamespace);
        command.Parameters.AddWithValue("videoId", (object?)videoId ?? DBNull.Value);
        command.Parameters.AddWithValue("granted", (object?)granted ?? DBNull.Value);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task WaitForWaitingDerivedArrayLockAsync(CovePostgresDatabase database, int videoId, CancellationToken ct)
    {
        while (await CountDerivedArrayLocksAsync(database, ct, videoId, granted: false) == 0)
            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
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
            .SingleAsync(entity => entity.Id == videoId, TestContext.Current.CancellationToken);
        ready.TrySetResult();
        await start.Task;
        video.VideoPerformers.Add(new VideoPerformer
        {
            VideoId = videoId,
            PerformerId = performerId,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    // Waits for code under test to reach a signal. If that code ends first, its own exception fails the test
    // instead of leaving it waiting, and the timeout turns a hang into a failure.
    private static async Task WaitForSignalAsync(Task signal, params Task[] work)
    {
        var finished = await Task.WhenAny([signal, .. work])
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        if (finished == signal)
            return;
        await finished;
        throw new InvalidOperationException("The code under test finished without reaching the signal the test waits for.");
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
        public Task<bool> GenerateImageThumbnailAsync(int imageId, int maxDimension = 640, bool overwrite = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task GenerateVideoPreviewAsync(int videoId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task GenerateVideoSpriteAsync(int videoId, CancellationToken ct = default) => throw new NotSupportedException();
        public string GetThumbnailPathForVideo(int videoId) => throw new NotSupportedException();
        public string GetTimestampedThumbnailPath(int videoId, double seconds) => throw new NotSupportedException();
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
