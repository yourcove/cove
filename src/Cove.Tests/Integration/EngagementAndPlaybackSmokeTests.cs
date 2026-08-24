using System.Net;
using System.Net.Http.Json;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests.Integration;

public sealed class EntityEngagementControllerSmokeTests
{
    [Fact]
    public async Task GetSnapshot_ReturnsOkForKnownVideo()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();

        var videoId = await factory.WithDbContextAsync(async db =>
        {
            var video = new Video { Title = "Engagement Video" };
            db.Videos.Add(video);
            await db.SaveChangesAsync();
            return video.Id;
        });

        using var client = factory.CreateAuthenticatedClient();
        var response = await client.GetAsync($"/api/engagement/video/{videoId}", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadApiJsonAsync<EntityEngagementDto>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(payload);
        Assert.Equal(videoId, payload.HostId);
    }
}

public sealed class PlaybackControllerSmokeTests
{
    [Fact]
    public async Task RecordIntervals_PersistsSessionAndIntervals()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();

        var videoId = await factory.WithDbContextAsync(async db =>
        {
            var video = new Video { Title = "Playback Video" };
            db.Videos.Add(video);
            await db.SaveChangesAsync();
            return video.Id;
        });

        using var client = factory.CreateAuthenticatedClient();
        var sessionId = Guid.NewGuid();

        var first = await client.PostAsJsonAsync("/api/playback/intervals", new PlaybackIntervalsRequestDto(
            "video",
            videoId,
            sessionId,
            180.0,
            12.0,
            "active",
            [new PlaybackIntervalInputDto(0.0, 12.0)]), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/playback/intervals", new PlaybackIntervalsRequestDto(
            "video",
            videoId,
            sessionId,
            180.0,
            27.0,
            "paused",
            [new PlaybackIntervalInputDto(12.0, 27.0)]), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        await factory.WithDbContextAsync(async db =>
        {
            var session = await db.PlaybackSessions.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(CoveWebApplicationFactory.TestUserId, session.UserId);
            Assert.Equal(InteractionHostType.Video, session.HostType);
            Assert.Equal(videoId, session.HostId);
            Assert.Equal(sessionId, session.SessionId);
            Assert.Equal(27.0, session.TotalWatchedSec, precision: 5);

            var intervals = await db.PlaybackIntervals.IgnoreQueryFilters()
                .OrderBy(interval => interval.StartSec)
                .ToListAsync();
            Assert.Equal(2, intervals.Count);
            Assert.Equal((0.0, 12.0), (intervals[0].StartSec, intervals[0].EndSec));
            Assert.Equal((12.0, 27.0), (intervals[1].StartSec, intervals[1].EndSec));
        });
    }

    [Fact]
    public async Task InteractionWriteRateLimit_BlocksThe241stRequestAcrossEndpoints()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();

        var videoId = await factory.WithDbContextAsync(async db =>
        {
            var video = new Video { Title = "Rate Limit Video" };
            db.Videos.Add(video);
            await db.SaveChangesAsync();
            return video.Id;
        });

        using var client = factory.CreateAuthenticatedClient();

        for (var attempt = 0; attempt < 240; attempt++)
        {
            var response = await client.PostAsJsonAsync("/api/engagement/interactions", new EngagementInteractionWriteDto(
                "video",
                videoId,
                "openDetail",
                null), cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var rateLimited = await client.PostAsJsonAsync("/api/playback/intervals", new PlaybackIntervalsRequestDto(
            "video",
            videoId,
            Guid.NewGuid(),
            180.0,
            12.0,
            "active",
            [new PlaybackIntervalInputDto(0.0, 12.0)]), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, rateLimited.StatusCode);
    }
}
