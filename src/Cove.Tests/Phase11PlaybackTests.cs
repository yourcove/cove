using Cove.Api.Controllers;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Cove.Tests;

public sealed class Phase11PlaybackTests
{
    [Fact]
    public async Task PlaybackController_PersistsVideoIntervals()
    {
        await using var scope = await CreateContextAsync();
        scope.Context.Videos.Add(new Video { Title = "Playback Video" });
        await scope.Context.SaveChangesAsync();
        var videoId = await scope.Context.Videos.Select(video => video.Id).SingleAsync();

        scope.PrincipalAccessor.Set(CreatePrincipal(7));
        var controller = CreateController(scope.Context, scope.PrincipalAccessor);
        var sessionId = Guid.NewGuid();

        var result = await controller.RecordIntervals(new PlaybackIntervalsRequestDto(
            "video",
            videoId,
            sessionId,
            180.0,
            42.0,
            "paused",
            [new PlaybackIntervalInputDto(0.0, 30.0), new PlaybackIntervalInputDto(30.0, 42.0)]), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);

        var session = await scope.Context.PlaybackSessions.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(7, session.UserId);
        Assert.Equal(InteractionHostType.Video, session.HostType);
        Assert.Equal(videoId, session.HostId);
        Assert.Equal(sessionId, session.SessionId);
        Assert.Equal(42.0, session.TotalWatchedSec, precision: 5);
        Assert.Equal(42.0, session.LastPositionSec);
        Assert.Equal(PlaybackSessionState.Paused, session.State);

        var intervals = await scope.Context.PlaybackIntervals.IgnoreQueryFilters().OrderBy(interval => interval.StartSec).ToListAsync();
        Assert.Equal(2, intervals.Count);
        Assert.Equal((0.0, 30.0), (intervals[0].StartSec, intervals[0].EndSec));
        Assert.Equal((30.0, 42.0), (intervals[1].StartSec, intervals[1].EndSec));
    }

    [Fact]
    public async Task PlaybackController_PersistsGroupIntervalsForCompilationSession()
    {
        await using var scope = await CreateContextAsync();
        scope.Context.Groups.Add(new Group { Name = "Compilation playback" });
        await scope.Context.SaveChangesAsync();
        var groupId = await scope.Context.Groups.Select(group => group.Id).SingleAsync();

        scope.PrincipalAccessor.Set(CreatePrincipal(11));
        var controller = CreateController(scope.Context, scope.PrincipalAccessor);
        var sessionId = Guid.NewGuid();

        Assert.IsType<NoContentResult>(await controller.RecordIntervals(new PlaybackIntervalsRequestDto(
            "group",
            groupId,
            sessionId,
            90.0,
            12.0,
            "active",
            [new PlaybackIntervalInputDto(0.0, 12.0)]), CancellationToken.None));

        Assert.IsType<NoContentResult>(await controller.RecordIntervals(new PlaybackIntervalsRequestDto(
            "group",
            groupId,
            sessionId,
            90.0,
            27.0,
            "paused",
            [new PlaybackIntervalInputDto(12.0, 27.0)]), CancellationToken.None));

        var session = await scope.Context.PlaybackSessions.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(11, session.UserId);
        Assert.Equal(InteractionHostType.Group, session.HostType);
        Assert.Equal(groupId, session.HostId);
        Assert.Equal(sessionId, session.SessionId);
        Assert.Equal(27.0, session.TotalWatchedSec, precision: 5);
        Assert.Equal(27.0, session.LastPositionSec);
        Assert.Equal(PlaybackSessionState.Paused, session.State);

        var intervals = await scope.Context.PlaybackIntervals.IgnoreQueryFilters().OrderBy(interval => interval.StartSec).ToListAsync();
        Assert.Equal(2, intervals.Count);
        Assert.All(intervals, interval => Assert.Equal(InteractionHostType.Group, interval.HostType));
        Assert.Equal((0.0, 12.0), (intervals[0].StartSec, intervals[0].EndSec));
        Assert.Equal((12.0, 27.0), (intervals[1].StartSec, intervals[1].EndSec));
    }

    [Fact]
    public async Task PlaybackController_PersistsPlaybackContextOnSessionAndIntervals()
    {
        await using var scope = await CreateContextAsync();
        var video = new Video { Title = "Compilation Item" };
        var group = new Group { Name = "Compilation Context" };
        scope.Context.AddRange(video, group);
        await scope.Context.SaveChangesAsync();

        scope.PrincipalAccessor.Set(CreatePrincipal(12));
        var controller = CreateController(scope.Context, scope.PrincipalAccessor);
        var sessionId = Guid.NewGuid();
        var context = JsonSerializer.SerializeToElement(new { itemIndex = 3, source = "test" });

        Assert.IsType<NoContentResult>(await controller.RecordIntervals(new PlaybackIntervalsRequestDto(
            "group",
            group.Id,
            sessionId,
            90.0,
            15.0,
            "paused",
            [new PlaybackIntervalInputDto(5.0, 15.0)],
            Surface: "compilation",
            ScopeKey: $"group:{group.Id}",
            ParentHostType: "group",
            ParentHostId: group.Id,
            ItemHostType: "video",
            ItemHostId: video.Id,
            GroupItemId: 123,
            SegmentId: null,
            ClipStartSec: 5.0,
            ClipEndSec: 15.0,
            Autoplay: true,
            Muted: true,
            Fullscreen: false,
            PlaybackRate: 1.25,
            Route: $"/compilation/{group.Id}",
            RecommendationSource: "home",
            Context: context), CancellationToken.None));

        var session = await scope.Context.PlaybackSessions.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("compilation", session.Surface);
        Assert.Equal($"group:{group.Id}", session.ScopeKey);
        Assert.Equal(InteractionHostType.Group, session.ParentHostType);
        Assert.Equal(group.Id, session.ParentHostId);
        Assert.Equal(InteractionHostType.Video, session.ItemHostType);
        Assert.Equal(video.Id, session.ItemHostId);
        Assert.Equal(123, session.GroupItemId);
        Assert.Equal(5.0, session.ClipStartSec);
        Assert.Equal(15.0, session.ClipEndSec);
        Assert.True(session.Autoplay);
        Assert.True(session.Muted);
        Assert.False(session.Fullscreen);
        Assert.Equal(1.25, session.PlaybackRate);
        Assert.Equal($"/compilation/{group.Id}", session.Route);
        Assert.Equal("home", session.RecommendationSource);
        Assert.Equal(3, session.Context!.RootElement.GetProperty("itemIndex").GetInt32());

        var interval = await scope.Context.PlaybackIntervals.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("compilation", interval.Surface);
        Assert.Equal(InteractionHostType.Video, interval.ItemHostType);
        Assert.Equal(video.Id, interval.ItemHostId);
        Assert.Equal(123, interval.GroupItemId);
        Assert.Equal(1.25, interval.PlaybackRate);
        Assert.Equal("test", interval.Context!.RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public async Task SegmentPlayback_CreatesSegmentAffinityAndCompletion()
    {
        await using var scope = await CreateContextAsync();
        var video = new Video { Title = "Segment Host" };
        scope.Context.Videos.Add(video);
        await scope.Context.SaveChangesAsync();
        var segment = new Segment
        {
            HostType = SegmentHostType.Video,
            HostId = video.Id,
            SourceKey = "test",
            StartSec = 10,
            EndSec = 20,
            Title = "Tracked segment",
        };
        scope.Context.Segments.Add(segment);
        await scope.Context.SaveChangesAsync();

        scope.PrincipalAccessor.Set(CreatePrincipal(13, Permissions.SegmentsRead));
        var controller = CreateController(scope.Context, scope.PrincipalAccessor);

        Assert.IsType<NoContentResult>(await controller.RecordIntervals(new PlaybackIntervalsRequestDto(
            "segment",
            segment.Id,
            Guid.NewGuid(),
            120.0,
            20.0,
            "ended",
            [new PlaybackIntervalInputDto(10.0, 20.0)],
            Surface: "segmentDetail",
            ScopeKey: $"segment:{segment.Id}",
            ParentHostType: "video",
            ParentHostId: video.Id,
            ItemHostType: "video",
            ItemHostId: video.Id,
            SegmentId: segment.Id,
            ClipStartSec: 10.0,
            ClipEndSec: 20.0), CancellationToken.None));

        var affinity = await scope.Context.UserEntityAffinities.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(AffinityHostType.Segment, affinity.HostType);
        Assert.Equal(segment.Id, affinity.HostId);
        Assert.Equal(1, affinity.ViewCount);
        Assert.Equal(1, affinity.CompleteCount);
        Assert.Equal(10.0, affinity.TotalConsumedSec, precision: 5);
        Assert.Equal(10.0, affinity.LastPositionSec);
    }

    [Fact]
    public async Task SegmentFirstTouch_ConcurrentInteractionAndPlaybackShareOneAffinity()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"cove-phase11-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;

        try
        {
            int videoId;
            int segmentId;
            var setupPrincipalAccessor = new CurrentPrincipalAccessor();
            await using (var setupContext = new PlaybackTestContext(options, setupPrincipalAccessor))
            {
                await setupContext.Database.EnsureCreatedAsync();
                setupContext.Users.Add(new User
                {
                    Id = 29,
                    Username = "user-29",
                    PasswordHash = "test",
                });
                var video = new Video { Title = "Concurrent Segment Host" };
                setupContext.Videos.Add(video);
                await setupContext.SaveChangesAsync();
                videoId = video.Id;

                var segment = new Segment
                {
                    HostType = SegmentHostType.Video,
                    HostId = videoId,
                    SourceKey = "test",
                    StartSec = 10,
                    EndSec = 20,
                    Title = "Concurrent segment",
                };
                setupContext.Segments.Add(segment);
                await setupContext.SaveChangesAsync();
                segmentId = segment.Id;
            }

            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var interactionTask = Task.Run(async () =>
            {
                var principalAccessor = new CurrentPrincipalAccessor();
                principalAccessor.Set(CreatePrincipal(29, Permissions.SegmentsRead));
                await using var context = new PlaybackTestContext(options, principalAccessor);
                var controller = CreateEngagementController(context, principalAccessor);
                await gate.Task;
                return await controller.RecordInteraction(new EngagementInteractionWriteDto("segment", segmentId, "seek"), CancellationToken.None);
            });

            var playbackTask = Task.Run(async () =>
            {
                var principalAccessor = new CurrentPrincipalAccessor();
                principalAccessor.Set(CreatePrincipal(29, Permissions.SegmentsRead));
                await using var context = new PlaybackTestContext(options, principalAccessor);
                var controller = CreateController(context, principalAccessor);
                await gate.Task;
                return await controller.RecordIntervals(new PlaybackIntervalsRequestDto(
                    "segment",
                    segmentId,
                    Guid.NewGuid(),
                    120.0,
                    12.0,
                    "ended",
                    [new PlaybackIntervalInputDto(10.0, 12.0)],
                    Surface: "segmentDetail",
                    ScopeKey: $"segment:{segmentId}",
                    ParentHostType: "video",
                    ParentHostId: videoId,
                    ItemHostType: "video",
                    ItemHostId: videoId,
                    SegmentId: segmentId,
                    ClipStartSec: 10.0,
                    ClipEndSec: 20.0), CancellationToken.None);
            });

            gate.SetResult(true);

            var results = await Task.WhenAll(interactionTask, playbackTask);
            Assert.All(results, result => Assert.IsType<NoContentResult>(result));

            await using var verifyContext = new PlaybackTestContext(options, new CurrentPrincipalAccessor());
            var affinity = await verifyContext.UserEntityAffinities.IgnoreQueryFilters()
                .SingleAsync(row => row.UserId == 29 && row.HostType == AffinityHostType.Segment && row.HostId == segmentId);
            Assert.Equal(1, affinity.InteractionCount);
            Assert.Equal(1, affinity.SeekCount);
            Assert.Equal(1, affinity.PlayerControlCount);
            Assert.Equal(2.0, affinity.TotalConsumedSec, precision: 5);
            Assert.Equal(2.0, affinity.LastPositionSec);
            Assert.Equal(1, await verifyContext.UserEntityAffinities.IgnoreQueryFilters()
                .CountAsync(row => row.UserId == 29 && row.HostType == AffinityHostType.Segment && row.HostId == segmentId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task RichPlayerInteractions_AreAcceptedAndAggregated()
    {
        await using var scope = await CreateContextAsync();
        await AddUserAsync(scope, 14);
        scope.Context.Images.Add(new Image { Title = "Lightbox image" });
        await scope.Context.SaveChangesAsync();
        var imageId = await scope.Context.Images.Select(image => image.Id).SingleAsync();

        scope.PrincipalAccessor.Set(CreatePrincipal(14, Permissions.ImagesRead));
        var controller = CreateEngagementController(scope.Context, scope.PrincipalAccessor);

        Assert.IsType<NoContentResult>(await controller.RecordInteraction(new EngagementInteractionWriteDto("image", imageId, "fullscreen"), CancellationToken.None));
        Assert.IsType<NoContentResult>(await controller.RecordInteraction(new EngagementInteractionWriteDto("image", imageId, "slideshowDelay"), CancellationToken.None));
        Assert.IsType<NoContentResult>(await controller.RecordInteraction(new EngagementInteractionWriteDto("image", imageId, "zoom"), CancellationToken.None));

        var affinity = await scope.Context.UserEntityAffinities.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(3, affinity.InteractionCount);
        Assert.Equal(2, affinity.PlayerControlCount);
        Assert.Equal(1, affinity.ZoomCount);
        Assert.Equal(3, await scope.Context.Interactions.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task TrackingDisabled_ReturnsNoContentWithoutWritingInteractionsOrPlayback()
    {
        await using var scope = await CreateContextAsync();
        await AddUserAsync(scope, 21, "{\"tracking\":{\"enabled\":false}}");
        scope.Context.Videos.Add(new Video { Title = "Muted tracking" });
        await scope.Context.SaveChangesAsync();
        var videoId = await scope.Context.Videos.Select(video => video.Id).SingleAsync();

        scope.PrincipalAccessor.Set(CreatePrincipal(21));
        var playbackController = CreateController(scope.Context, scope.PrincipalAccessor);
        var engagementController = CreateEngagementController(scope.Context, scope.PrincipalAccessor);

        var interactionResult = await engagementController.RecordInteraction(
            new EngagementInteractionWriteDto("video", videoId, "pageVisit"),
            CancellationToken.None);
        var playbackResult = await playbackController.RecordIntervals(new PlaybackIntervalsRequestDto(
            "video",
            videoId,
            Guid.NewGuid(),
            120.0,
            40.0,
            "ended",
            [new PlaybackIntervalInputDto(0.0, 40.0)]), CancellationToken.None);

        Assert.IsType<NoContentResult>(interactionResult);
        Assert.IsType<NoContentResult>(playbackResult);
        Assert.Empty(await scope.Context.Interactions.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await scope.Context.PlaybackSessions.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await scope.Context.UserEntityAffinities.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task VideoPlayback_CountsViewAtMinViewSecondsWithoutCompletion()
    {
        await using var scope = await CreateContextAsync();
        await AddUserAsync(scope, 22);
        scope.Context.Videos.Add(new Video { Title = "Threshold view" });
        await scope.Context.SaveChangesAsync();
        var videoId = await scope.Context.Videos.Select(video => video.Id).SingleAsync();

        scope.PrincipalAccessor.Set(CreatePrincipal(22));
        var controller = CreateController(scope.Context, scope.PrincipalAccessor);

        Assert.IsType<NoContentResult>(await controller.RecordIntervals(new PlaybackIntervalsRequestDto(
            "video",
            videoId,
            Guid.NewGuid(),
            100.0,
            35.0,
            "ended",
            [new PlaybackIntervalInputDto(0.0, 35.0)]), CancellationToken.None));

        var affinity = await scope.Context.UserEntityAffinities.IgnoreQueryFilters().SingleAsync();
        var session = await scope.Context.PlaybackSessions.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(1, affinity.ViewCount);
        Assert.Equal(0, affinity.CompleteCount);
        Assert.True(session.CountsAsView);
        Assert.False(session.IsCompleted);
    }

    [Fact]
    public async Task ImageDetailDwell_CountsPageVisitAndViewForUser()
    {
        await using var scope = await CreateContextAsync();
        await AddUserAsync(scope, 23);
        scope.Context.Images.Add(new Image { Title = "Dwell image" });
        await scope.Context.SaveChangesAsync();
        var imageId = await scope.Context.Images.Select(image => image.Id).SingleAsync();

        scope.PrincipalAccessor.Set(CreatePrincipal(23, Permissions.ImagesRead));
        var playbackController = CreateController(scope.Context, scope.PrincipalAccessor);
        var engagementController = CreateEngagementController(scope.Context, scope.PrincipalAccessor);

        Assert.IsType<NoContentResult>(await engagementController.RecordInteraction(
            new EngagementInteractionWriteDto("image", imageId, "pageVisit"),
            CancellationToken.None));
        Assert.IsType<NoContentResult>(await playbackController.RecordIntervals(new PlaybackIntervalsRequestDto(
            "image",
            imageId,
            Guid.NewGuid(),
            6.0,
            6.0,
            "ended",
            [new PlaybackIntervalInputDto(0.0, 6.0)]), CancellationToken.None));

        var affinity = await scope.Context.UserEntityAffinities.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(AffinityHostType.Image, affinity.HostType);
        Assert.Equal(imageId, affinity.HostId);
        Assert.Equal(1, affinity.PageVisitCount);
        Assert.Equal(1, affinity.ViewCount);
    }

    [Fact]
    public async Task FinalLongLastSession_AwardsDerivedLikeAndInteraction()
    {
        await using var scope = await CreateContextAsync();
        await AddUserAsync(scope, 24);
        scope.Context.Videos.Add(new Video { Title = "Long session" });
        await scope.Context.SaveChangesAsync();
        var videoId = await scope.Context.Videos.Select(video => video.Id).SingleAsync();

        scope.PrincipalAccessor.Set(CreatePrincipal(24));
        var controller = CreateController(scope.Context, scope.PrincipalAccessor);

        // Watch 65s and end the player.
        Assert.IsType<NoContentResult>(await controller.RecordIntervals(new PlaybackIntervalsRequestDto(
            "video",
            videoId,
            Guid.NewGuid(),
            180.0,
            65.0,
            "ended",
            [new PlaybackIntervalInputDto(0.0, 65.0)]), CancellationToken.None));

        // Under the user-global session model the derived like is awarded when the session FINALIZES (a new
        // session begins after the idle timeout), not on the per-player "ended" — so none yet.
        Assert.Equal(0, (await scope.Context.UserEntityAffinities.IgnoreQueryFilters().SingleAsync()).DerivedLikeCount);

        // Simulate a 20-minute-long session that then went idle past the 30-min timeout, so the next activity
        // rolls over and finalizes it (awarding the derived like to the last entity).
        var userSession = await scope.Context.UserSessions.IgnoreQueryFilters().SingleAsync();
        userSession.StartedAt = DateTime.UtcNow.AddMinutes(-51);
        userSession.LastSeenAt = DateTime.UtcNow.AddMinutes(-31);
        await scope.Context.SaveChangesAsync();

        Assert.IsType<NoContentResult>(await controller.RecordIntervals(new PlaybackIntervalsRequestDto(
            "video",
            videoId,
            Guid.NewGuid(),
            180.0,
            70.0,
            "active",
            [new PlaybackIntervalInputDto(65.0, 70.0)]), CancellationToken.None));

        var affinity = await scope.Context.UserEntityAffinities.IgnoreQueryFilters().SingleAsync();
        var derivedLike = await scope.Context.Interactions.IgnoreQueryFilters().SingleAsync(interaction => interaction.Kind == InteractionKind.DerivedLike);
        Assert.Equal(1, affinity.DerivedLikeCount);
        Assert.Equal(InteractionHostType.Video, derivedLike.HostType);
        Assert.Equal(videoId, derivedLike.HostId);
        Assert.Equal(24, derivedLike.UserId);
    }

    private static PlaybackController CreateController(CoveContext context, CurrentPrincipalAccessor principalAccessor)
    {
        var engagementService = new UserEngagementService(context, principalAccessor);
        return new PlaybackController(engagementService, principalAccessor);
    }

    private static EntityEngagementController CreateEngagementController(CoveContext context, CurrentPrincipalAccessor principalAccessor)
    {
        var engagementService = new UserEngagementService(context, principalAccessor);
        return new EntityEngagementController(engagementService, principalAccessor);
    }

    private static CovePrincipal CreatePrincipal(int userId, params string[] permissions) => new()
    {
        UserId = userId,
        Username = $"user-{userId}",
        Kind = PrincipalKind.User,
        Roles = new HashSet<string>(),
        Permissions = new HashSet<string>
        {
            Permissions.VideosRead,
        }.Concat(permissions).ToHashSet(),
    };

    private static async Task AddUserAsync(TestContextScope scope, int userId, string? uiPreferencesJson = null)
    {
        scope.Context.Users.Add(new User
        {
            Id = userId,
            Username = $"user-{userId}",
            PasswordHash = "test",
            UiPreferencesJson = uiPreferencesJson,
        });
        await scope.Context.SaveChangesAsync();
    }

    private static async Task<TestContextScope> CreateContextAsync()
    {
        var principalAccessor = new CurrentPrincipalAccessor();
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;

        var context = new PlaybackTestContext(options, principalAccessor);
        await context.Database.EnsureCreatedAsync();
        return new TestContextScope(context, connection, principalAccessor);
    }

    private sealed class PlaybackTestContext(DbContextOptions<CoveContext> options, ICurrentPrincipalAccessor principalAccessor) : CoveContext(options, principalAccessor)
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
            PrincipalAccessor.Set(null);
        }
    }
}

