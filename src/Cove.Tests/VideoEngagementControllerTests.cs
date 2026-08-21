using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Data.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Cove.Tests;

public class VideoEngagementControllerTests
{
    [Fact]
    public async Task DirectMediaLikes_RecordHistoryForImagesAudiosAndTexts()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;
        context.Images.Add(new Image { Title = "Liked Image" });
        context.Audios.Add(new Audio { Title = "Liked Audio" });
        context.TextDocuments.Add(new TextDocument { Title = "Liked Text" });
        context.Performers.Add(new Performer { Name = "Aggregate Performer" });
        await context.SaveChangesAsync();
        principalAccessor.Set(CreatePrincipal(7));
        var service = new UserEngagementService(context, principalAccessor);
        var historicalAt = new DateTime(2022, 7, 3, 12, 0, 0, DateTimeKind.Utc);

        foreach (var (hostType, hostId) in new[]
        {
            (AffinityHostType.Image, await context.Images.Select(item => item.Id).SingleAsync()),
            (AffinityHostType.Audio, await context.Audios.Select(item => item.Id).SingleAsync()),
            (AffinityHostType.Text, await context.TextDocuments.Select(item => item.Id).SingleAsync()),
        })
        {
            var snapshot = await service.IncrementLikeAsync(hostType, hostId, CancellationToken.None);
            Assert.Equal(1, snapshot!.LikeCount);
            snapshot = await service.AddHistoricalLikeAsync(hostType, hostId, historicalAt, CancellationToken.None);
            Assert.Equal(2, snapshot!.LikeCount);

            var history = await service.GetHistoryAsync(hostType, hostId, CancellationToken.None);
            Assert.Equal(2, history!.LikeHistory.Count);
            Assert.Contains(historicalAt.ToString("o"), history.LikeHistory);

            snapshot = await service.DeleteLikeAtAsync(hostType, hostId, historicalAt, CancellationToken.None);
            Assert.Equal(1, snapshot!.LikeCount);
            history = await service.GetHistoryAsync(hostType, hostId, CancellationToken.None);
            Assert.DoesNotContain(historicalAt.ToString("o"), history!.LikeHistory);
            Assert.Single(history.LikeHistory);
        }

        var performerId = await context.Performers.Select(item => item.Id).SingleAsync();
        Assert.Null(await service.IncrementLikeAsync(AffinityHostType.Performer, performerId, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteLikeAt_RemovesOnlyTheSelectedVideoLike()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;
        context.Videos.Add(new Video { Title = "Selected Like Video" });
        await context.SaveChangesAsync();
        var videoId = await context.Videos.Select(video => video.Id).SingleAsync();
        principalAccessor.Set(CreatePrincipal(7));
        var service = new UserEngagementService(context, principalAccessor);
        var firstAt = new DateTime(2022, 4, 3, 14, 30, 0, DateTimeKind.Utc);
        var selectedAt = firstAt.AddDays(1);
        await service.AddHistoricalVideoLikeAsync(videoId, firstAt);
        await service.AddHistoricalVideoLikeAsync(videoId, selectedAt);

        var snapshot = await service.DeleteLikeAtAsync(AffinityHostType.Video, videoId, selectedAt);

        Assert.Equal(1, snapshot!.LikeCount);
        var history = await service.GetVideoHistoryAsync(videoId);
        Assert.Equal(firstAt.ToString("o"), Assert.Single(history!.LikeHistory));
    }

    [Fact]
    public async Task AddHistoricalLike_UsesRequestedTimestampAndIncrementsCount()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;
        context.Videos.Add(new Video { Title = "Historical Like Video" });
        await context.SaveChangesAsync();
        var videoId = await context.Videos.Select(video => video.Id).SingleAsync();
        principalAccessor.Set(CreatePrincipal(7));
        var controller = CreateVideosController(context, principalAccessor);
        var historicalAt = new DateTime(2022, 4, 3, 14, 30, 0, DateTimeKind.Utc);

        var result = await controller.AddHistoricalLike(videoId, new HistoricalLikeDto(historicalAt), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(1, Assert.IsType<int>(ok.Value));
        var history = await new UserEngagementService(context, principalAccessor).GetVideoHistoryAsync(videoId);
        Assert.Equal(historicalAt.ToString("o"), Assert.Single(history!.LikeHistory));
    }

    [Fact]
    public async Task EngagementMutations_EmitTraceSummaries()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;

        context.Videos.Add(new Video { Title = "Traceable Video" });
        await context.SaveChangesAsync();
        var videoId = await context.Videos.Select(video => video.Id).SingleAsync();

        principalAccessor.Set(CreatePrincipal(7));
        var logger = new RecordingLogger<UserEngagementService>();
        var service = new UserEngagementService(context, principalAccessor, logger: logger);

        Assert.True(await service.RecordInteractionAsync(
            InteractionHostType.Video,
            videoId,
            InteractionKind.OpenDetail,
            cancellationToken: CancellationToken.None));
        Assert.NotNull(await service.SetFavoriteAsync(
            AffinityHostType.Video,
            videoId,
            true,
            CancellationToken.None));
        Assert.NotNull(await service.SetBookmarkedAsync(
            AffinityHostType.Video,
            videoId,
            true,
            CancellationToken.None));
        Assert.NotNull(await service.IncrementVideoLikeAsync(videoId, CancellationToken.None));
        Assert.NotNull(await service.SetVideoRatingAsync(
            videoId,
            80,
            cancellationToken: CancellationToken.None));
        Assert.True(await service.RecordPlaybackIntervalsAsync(new PlaybackIntervalsRequestDto(
            "video",
            videoId,
            Guid.NewGuid(),
            120,
            12,
            "paused",
            [new PlaybackIntervalInputDto(5, 12)],
            Surface: "videoDetail"), CancellationToken.None));

        Assert.Contains(logger.Entries, entry =>
            entry is { Level: LogLevel.Trace, EventId.Id: 2701 }
            && entry.Message.Contains($"Recorded OpenDetail interaction for Video {videoId}", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry =>
            entry is { Level: LogLevel.Trace, EventId.Id: 2702 }
            && entry.Message.Contains($"Set favorite for Video {videoId} to True", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry =>
            entry is { Level: LogLevel.Trace, EventId.Id: 2704 }
            && entry.Message.Contains($"Updated like count for Video {videoId}; operation=incremented, count=1", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry =>
            entry is { Level: LogLevel.Trace, EventId.Id: 2705 }
            && entry.Message.Contains($"Recorded playback for Video {videoId}; state=Paused, submittedIntervals=1, acceptedIntervals=1", StringComparison.Ordinal)
            && entry.Message.Contains("addedWatchedSeconds=7", StringComparison.Ordinal)
            && entry.Message.Contains("surface=videoDetail", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry =>
            entry is { Level: LogLevel.Trace, EventId.Id: 2703 }
            && entry.Message.Contains($"Set bookmark for Video {videoId} to True", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry =>
            entry is { Level: LogLevel.Trace, EventId.Id: 2706 }
            && entry.Message.Contains($"Applied RatingCreated rating for Video {videoId}; aspect=overall, value=80", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VideoActivityAndRating_AreScopedToCurrentUser()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;

        context.Users.AddRange(
            new User { Id = 7, Username = "user-7", PasswordHash = "test" },
            new User { Id = 9, Username = "user-9", PasswordHash = "test" });
        context.Videos.Add(new Video { Title = "Scoped Video" });
        await context.SaveChangesAsync();
        var videoId = await context.Videos.Select(video => video.Id).SingleAsync();

        var videosController = CreateVideosController(context, principalAccessor);
        var playbackController = CreatePlaybackController(context, principalAccessor);

        principalAccessor.Set(CreatePrincipal(7));
        var sessionId = Guid.NewGuid();

        // Record a play (view count)
        Assert.IsType<NoContentResult>(await videosController.RecordPlay(videoId, CancellationToken.None));

        // Send first set of intervals: watched 42.5–48.0 (5.5s), paused
        Assert.IsType<NoContentResult>(await playbackController.RecordIntervals(new PlaybackIntervalsRequestDto(
            "video", videoId, sessionId, 120.0, 48.0, "paused",
            [new PlaybackIntervalInputDto(42.5, 48.0)]), CancellationToken.None));

        // Send second set: watched 66.0–120.0 (54.0s), ended. Total distinct watched = 59.5s of 120s (49.6%),
        // above the 45% completion ratio → IsCompleted = true (coverage-based, independent of end position).
        Assert.IsType<NoContentResult>(await playbackController.RecordIntervals(new PlaybackIntervalsRequestDto(
            "video", videoId, sessionId, 120.0, 120.0, "ended",
            [new PlaybackIntervalInputDto(66.0, 120.0)]), CancellationToken.None));

        var incrementResult = await videosController.IncrementLike(videoId, CancellationToken.None);
        var incrementOk = Assert.IsType<OkObjectResult>(incrementResult.Result);
        Assert.Equal(1, Assert.IsType<int>(incrementOk.Value));

        var ratingResult = await videosController.SetRating(videoId, new VideoRatingDto(88), CancellationToken.None);
        var ratingOk = Assert.IsType<OkObjectResult>(ratingResult.Result);
        Assert.Equal(88, Assert.IsType<int>(ratingOk.Value));

        var audioRatingResult = await videosController.SetRating(videoId, new VideoRatingDto(35, "audio"), CancellationToken.None);
        var audioRatingOk = Assert.IsType<OkObjectResult>(audioRatingResult.Result);
        Assert.Equal(88, Assert.IsType<int>(audioRatingOk.Value));

        var ratingsResult = await videosController.GetRatings(videoId, CancellationToken.None);
        var ratingsOk = Assert.IsType<OkObjectResult>(ratingsResult.Result);
        var ratingsDto = Assert.IsType<EntityRatingsDto>(ratingsOk.Value);
        Assert.Equal(88, ratingsDto.Ratings["overall"]);
        Assert.Equal(35, ratingsDto.Ratings["audio"]);

        var userOneResult = await videosController.GetById(videoId, CancellationToken.None);
        var userOneOk = Assert.IsType<OkObjectResult>(userOneResult.Result);
        var userOneVideo = Assert.IsType<VideoDto>(userOneOk.Value);
        var userOneSnapshot = await new UserEngagementService(context, principalAccessor).GetSnapshotAsync(AffinityHostType.Video, videoId, CancellationToken.None);
        Assert.NotNull(userOneSnapshot);
        Assert.Equal(88, userOneSnapshot.Rating);
        Assert.Equal(0.0, userOneSnapshot.ResumeTime);
        Assert.Equal(59.5, userOneSnapshot.PlayDuration, precision: 5);  // 5.5 + 54.0
        Assert.Equal(2, userOneSnapshot.PlayCount);
        Assert.Equal(1, userOneSnapshot.LikeCount);

        var historyResult = await videosController.GetHistory(videoId, CancellationToken.None);
        var historyOk = Assert.IsType<OkObjectResult>(historyResult.Result);
        var history = Assert.IsType<VideoHistoryDto>(historyOk.Value);
        Assert.Single(history.PlayHistory);
        Assert.Single(history.LikeHistory);
        Assert.NotNull(history.AllTimeWatchedIntervals);
        Assert.Equal(2, history.AllTimeWatchedIntervals!.Count);
        Assert.Equal(42.5, history.AllTimeWatchedIntervals[0].StartSec);
        Assert.Equal(48.0, history.AllTimeWatchedIntervals[0].EndSec);
        Assert.Equal(66.0, history.AllTimeWatchedIntervals[1].StartSec);
        Assert.Equal(120.0, history.AllTimeWatchedIntervals[1].EndSec);
        Assert.Equal(59.5, history.TotalDistinctWatchedSec!.Value, precision: 5);
        Assert.NotNull(history.Sessions);
        var sessionHistory = Assert.Single(history.Sessions!);
        Assert.Equal(sessionId, sessionHistory.SessionId);
        Assert.True(sessionHistory.IsCompleted);
        Assert.Equal(59.5, sessionHistory.TotalWatchedSec, precision: 5);
        Assert.Equal(2, sessionHistory.Intervals.Count);

        context.ChangeTracker.Clear();
        principalAccessor.Set(CreatePrincipal(9));

        var userTwoResult = await videosController.GetById(videoId, CancellationToken.None);
        var userTwoOk = Assert.IsType<OkObjectResult>(userTwoResult.Result);
        var userTwoVideo = Assert.IsType<VideoDto>(userTwoOk.Value);
        var userTwoSnapshot = await new UserEngagementService(context, principalAccessor).GetSnapshotAsync(AffinityHostType.Video, videoId, CancellationToken.None);
        Assert.NotNull(userTwoSnapshot);
        Assert.Null(userTwoSnapshot.Rating);
        Assert.Equal(0d, userTwoSnapshot.ResumeTime);
        Assert.Equal(0d, userTwoSnapshot.PlayDuration);
        Assert.Equal(0, userTwoSnapshot.PlayCount);
        Assert.Equal(0, userTwoSnapshot.LikeCount);

        var userTwoRatingsResult = await videosController.GetRatings(videoId, CancellationToken.None);
        var userTwoRatingsOk = Assert.IsType<OkObjectResult>(userTwoRatingsResult.Result);
        var userTwoRatingsDto = Assert.IsType<EntityRatingsDto>(userTwoRatingsOk.Value);
        Assert.Empty(userTwoRatingsDto.Ratings);
        var userTwoHistory = await new UserEngagementService(context, principalAccessor).GetVideoHistoryAsync(videoId);
        Assert.Empty(userTwoHistory!.PlayHistory);

        var affinityRows = await context.UserEntityAffinities.IgnoreQueryFilters().ToListAsync();
        var affinity = Assert.Single(affinityRows);
        Assert.Equal(7, affinity.UserId);
        Assert.Equal(2, affinity.ViewCount);
        Assert.Equal(1, affinity.LikeCount);
        Assert.Equal(1, affinity.CompleteCount);
        Assert.Equal(59.5, affinity.TotalConsumedSec, precision: 5);

        var playHistoryRow = Assert.Single(await context.Set<VideoPlayHistory>().IgnoreQueryFilters().ToListAsync());
        Assert.Equal(7, playHistoryRow.UserId);

        var playbackSessions = await context.PlaybackSessions.IgnoreQueryFilters().ToListAsync();
        var playbackSession = Assert.Single(playbackSessions);
        Assert.Equal(7, playbackSession.UserId);
        Assert.Equal(InteractionHostType.Video, playbackSession.HostType);
        Assert.Equal(videoId, playbackSession.HostId);
        Assert.Equal(sessionId, playbackSession.SessionId);
        Assert.True(playbackSession.IsCompleted);
        Assert.Equal(59.5, playbackSession.TotalWatchedSec, precision: 5);
        Assert.Equal(120.0, playbackSession.LastPositionSec);

        var playbackIntervals = await context.PlaybackIntervals.IgnoreQueryFilters().OrderBy(iv => iv.StartSec).ToListAsync();
        Assert.Equal(2, playbackIntervals.Count);
        Assert.Equal(playbackSession.Id, playbackIntervals[0].PlaybackSessionId);
        Assert.Equal(7, playbackIntervals[0].UserId);
        Assert.Equal(42.5, playbackIntervals[0].StartSec);
        Assert.Equal(48.0, playbackIntervals[0].EndSec);
        Assert.Equal(66.0, playbackIntervals[1].StartSec);
        Assert.Equal(120.0, playbackIntervals[1].EndSec);

        var ratingRows = await context.Ratings.IgnoreQueryFilters().OrderBy(rating => rating.Aspect).ToListAsync();
        Assert.Equal(2, ratingRows.Count);
        Assert.Collection(
            ratingRows,
            rating =>
            {
                Assert.Equal(7, rating.UserId);
                Assert.Equal("audio", rating.Aspect);
                Assert.Equal(35, rating.Value);
            },
            rating =>
            {
                Assert.Equal(7, rating.UserId);
                Assert.Equal("overall", rating.Aspect);
                Assert.Equal(88, rating.Value);
            });
    }

    [Fact]
    public async Task OwnerlessSystemPlayHistory_IsNotExposedOrMutatedWithoutAPrincipal()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;
        context.Videos.Add(new Video { Title = "Ownerless system history" });
        await context.SaveChangesAsync();
        var videoId = await context.Videos.Select(video => video.Id).SingleAsync();
        var service = new UserEngagementService(context, principalAccessor);

        principalAccessor.Set(CovePrincipal.System());
        await service.RecordVideoPlayAsync(videoId);
        Assert.Single((await service.GetVideoHistoryAsync(videoId))!.PlayHistory);

        principalAccessor.Set(null);
        await service.RecordVideoPlayAsync(videoId);
        await service.DeleteVideoPlayAsync(videoId);
        await service.ResetVideoPlayAsync(videoId);

        Assert.Empty((await service.GetVideoHistoryAsync(videoId))!.PlayHistory);
        var ownerlessRow = Assert.Single(await context.Set<VideoPlayHistory>().IgnoreQueryFilters().ToListAsync());
        Assert.Null(ownerlessRow.UserId);

        principalAccessor.Set(new CovePrincipal
        {
            UserId = null,
            Username = "share-link",
            Kind = PrincipalKind.ShareLink,
            Roles = new HashSet<string>(),
            Permissions = new HashSet<string> { Permissions.VideosRead },
        });
        await service.RecordVideoPlayAsync(videoId);
        await service.DeleteVideoPlayAsync(videoId);
        await service.ResetVideoPlayAsync(videoId);
        Assert.Empty((await service.GetVideoHistoryAsync(videoId))!.PlayHistory);
        ownerlessRow = Assert.Single(await context.Set<VideoPlayHistory>().IgnoreQueryFilters().ToListAsync());
        Assert.Null(ownerlessRow.UserId);

        principalAccessor.Set(CovePrincipal.System());
        Assert.Single((await service.GetVideoHistoryAsync(videoId))!.PlayHistory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerGlobalActivityCleanup_RemovesOwnedAndLegacyPlayHistoryButPreservesOtherUsers(bool wipeAll)
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;
        context.Users.AddRange(
            new User { Id = 7, Username = "owner-7", PasswordHash = "test" },
            new User { Id = 9, Username = "user-9", PasswordHash = "test" });
        var video = new Video { Title = "Scoped cleanup" };
        context.Videos.Add(video);
        await context.SaveChangesAsync();
        context.Set<VideoPlayHistory>().AddRange(
            new VideoPlayHistory { VideoId = video.Id, UserId = 7, PlayedAt = DateTime.UtcNow.AddMinutes(-3) },
            new VideoPlayHistory { VideoId = video.Id, UserId = null, PlayedAt = DateTime.UtcNow.AddMinutes(-2) },
            new VideoPlayHistory { VideoId = video.Id, UserId = 9, PlayedAt = DateTime.UtcNow.AddMinutes(-1) });
        await context.SaveChangesAsync();
        principalAccessor.Set(CreatePrincipal(7, isOwner: true));
        var service = new UserEngagementService(context, principalAccessor);

        if (wipeAll)
            await service.WipeAllEngagementAsync();
        else
            await service.ResetAllActivityAsync();

        var remaining = Assert.Single(await context.Set<VideoPlayHistory>()
            .IgnoreQueryFilters()
            .ToListAsync());
        Assert.Equal(9, remaining.UserId);
    }

    private static VideosController CreateVideosController(CoveContext context, CurrentPrincipalAccessor principalAccessor)
    {
        var repository = new VideoRepository(context);
        var engagementService = new UserEngagementService(context, principalAccessor);
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        return new VideosController(repository, context, null!, null!, null!, memoryCache, null!, null!, engagementService, new CustomFieldService(context), new EventBus(), null, principalAccessor);
    }

    private static PlaybackController CreatePlaybackController(CoveContext context, CurrentPrincipalAccessor principalAccessor)
    {
        var engagementService = new UserEngagementService(context, principalAccessor);
        return new PlaybackController(engagementService, principalAccessor);
    }

    private static CovePrincipal CreatePrincipal(int userId, bool isOwner = false) => new()
    {
        UserId = userId,
        Username = $"user-{userId}",
        Kind = PrincipalKind.User,
        Roles = new HashSet<string>(),
        Permissions = isOwner
            ? new HashSet<string> { "*" }
            : new HashSet<string> { Permissions.VideosRead },
    };

    private static async Task<TestContextScope> CreateContextAsync()
    {
        var principalAccessor = new CurrentPrincipalAccessor();
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;

        var context = new VideoEngagementTestContext(options, principalAccessor);
        await context.Database.EnsureCreatedAsync();
        return new TestContextScope(context, connection, principalAccessor);
    }

    private sealed class VideoEngagementTestContext(DbContextOptions<CoveContext> options, ICurrentPrincipalAccessor principalAccessor) : CoveContext(options, principalAccessor)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

        }
    }

    private sealed class TestContextScope(CoveContext context, SqliteConnection connection, CurrentPrincipalAccessor principalAccessor) : IAsyncDisposable
    {
        public CoveContext Context { get; } = context;

        public CurrentPrincipalAccessor PrincipalAccessor { get; } = principalAccessor;

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, eventId, formatter(state, exception)));
    }
}
