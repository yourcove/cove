using Cove.Core.DTOs;
using Cove.Core.Entities;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<EntityEngagementDto> GetPerformerEngagementAsync(
        PerformerDto performer,
        CancellationToken cancellationToken = default)
        => SendAsync<EntityEngagementDto>(
            HttpMethod.Get,
            $"/api/engagement/{AffinityHostType.Performer}/{performer.Id}",
            payload: null,
            cancellationToken);

    public Task<EntityEngagementDto> SetPerformerRatingAsync(
        PerformerDto performer,
        int rating,
        string aspect = "overall",
        CancellationToken cancellationToken = default)
        => SendAsync<EntityEngagementDto>(
            HttpMethod.Put,
            $"/api/engagement/{AffinityHostType.Performer}/{performer.Id}/rating",
            new VideoRatingDto(rating, aspect),
            cancellationToken);

    public Task<EntityRatingsDto> GetPerformerRatingsAsync(
        PerformerDto performer,
        CancellationToken cancellationToken = default)
        => SendAsync<EntityRatingsDto>(
            HttpMethod.Get,
            $"/api/engagement/{AffinityHostType.Performer}/{performer.Id}/ratings",
            payload: null,
            cancellationToken);

    public Task<EntityEngagementDto> SetPerformerFavoriteAsync(
        PerformerDto performer,
        bool isFavorite,
        CancellationToken cancellationToken = default)
        => SendAsync<EntityEngagementDto>(
            HttpMethod.Put,
            $"/api/engagement/{AffinityHostType.Performer}/{performer.Id}/favorite",
            new EntityFavoriteDto(isFavorite),
            cancellationToken);

    public Task<BookmarkStateDto> SetPerformerBookmarkAsync(
        PerformerDto performer,
        bool isSaved,
        CancellationToken cancellationToken = default)
        => SendAsync<BookmarkStateDto>(
            HttpMethod.Post,
            "/api/me/bookmarks",
            new BookmarkToggleDto(AffinityHostType.Performer, performer.Id, isSaved),
            cancellationToken);

    public async Task<BookmarkStateDto> GetPerformerBookmarkAsync(
        PerformerDto performer,
        CancellationToken cancellationToken = default)
    {
        var states = await SendAsync<IReadOnlyList<BookmarkStateDto>>(
            HttpMethod.Post,
            "/api/me/bookmarks/batch",
            new BookmarkBatchRequestDto(AffinityHostType.Performer, [performer.Id]),
            cancellationToken);
        return states.Single();
    }

    public Task<EntityEngagementDto> SetVideoFavoriteAsync(
        VideoDto video,
        bool isFavorite,
        CancellationToken cancellationToken = default)
        => SendAsync<EntityEngagementDto>(
            HttpMethod.Put,
            $"/api/engagement/video/{video.Id}/favorite",
            new EntityFavoriteDto(isFavorite),
            cancellationToken);

    public Task<int> IncrementVideoLikeAsync(
        VideoDto video,
        CancellationToken cancellationToken = default)
        => SendAsync<int>(
            HttpMethod.Post,
            $"/api/videos/{video.Id}/like",
            payload: null,
            cancellationToken);

    public Task<int> IncrementImageLikeAsync(
        ImageDto image,
        CancellationToken cancellationToken = default)
        => SendAsync<int>(
            HttpMethod.Post,
            $"/api/images/{image.Id}/like",
            payload: null,
            cancellationToken);

    public Task<EntityEngagementDto> SetVideoRatingAsync(
        VideoDto video,
        int rating,
        string aspect = "overall",
        CancellationToken cancellationToken = default)
        => SendAsync<EntityEngagementDto>(
            HttpMethod.Put,
            $"/api/engagement/{AffinityHostType.Video}/{video.Id}/rating",
            new VideoRatingDto(rating, aspect),
            cancellationToken);

    public Task<EntityRatingsDto> GetVideoRatingsAsync(
        VideoDto video,
        CancellationToken cancellationToken = default)
        => SendAsync<EntityRatingsDto>(
            HttpMethod.Get,
            $"/api/engagement/{AffinityHostType.Video}/{video.Id}/ratings",
            payload: null,
            cancellationToken);

    public Task<EntityEngagementDto> GetVideoEngagementAsync(
        VideoDto video,
        CancellationToken cancellationToken = default)
        => SendAsync<EntityEngagementDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/engagement/video/{video.Id}"),
            payload: null,
            cancellationToken);

    public Task RecordVideoPlaybackAsync(
        VideoDto video,
        Guid sessionId,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(
            HttpMethod.Post,
            "/api/playback/intervals",
            new PlaybackIntervalsRequestDto(
                HostType: "video",
                HostId: video.Id,
                SessionId: sessionId,
                MediaDurationSec: 20,
                CurrentPositionSec: 8,
                State: "paused",
                Intervals: [new PlaybackIntervalInputDto(2, 8)]),
            cancellationToken);

    public Task<VideoHistoryDto> GetVideoHistoryAsync(
        VideoDto video,
        CancellationToken cancellationToken = default)
        => SendAsync<VideoHistoryDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/videos/{video.Id}/history"),
            payload: null,
            cancellationToken);

    public Task<EntityEngagementDto> GetEntityEngagementAsync(
        AffinityHostType hostType,
        int hostId,
        CancellationToken cancellationToken = default)
        => SendAsync<EntityEngagementDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/engagement/{hostType}/{hostId}"),
            payload: null,
            cancellationToken);
}
