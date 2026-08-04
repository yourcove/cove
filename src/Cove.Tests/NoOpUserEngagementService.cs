using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using System.Text.Json;

namespace Cove.Tests;

internal sealed class NoOpUserEngagementService : IUserEngagementService
{
    public Task<UserEngagementSnapshot?> GetSnapshotAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken = default)
        => Task.FromResult<UserEngagementSnapshot?>(null);

    public Task<Dictionary<string, int>?> GetRatingsByAspectAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken = default)
        => Task.FromResult<Dictionary<string, int>?>([]);

    public Task<Dictionary<int, UserEngagementSnapshot>> GetSnapshotsAsync(AffinityHostType hostType, IEnumerable<int> hostIds, CancellationToken cancellationToken = default)
        => Task.FromResult(new Dictionary<int, UserEngagementSnapshot>());

    public Task<Dictionary<int, UserEngagementSnapshot>> GetVideoSnapshotsAsync(IEnumerable<int> videoIds, CancellationToken cancellationToken = default)
        => Task.FromResult(new Dictionary<int, UserEngagementSnapshot>());

    public Task<UserEngagementSnapshot?> SetFavoriteAsync(AffinityHostType hostType, int hostId, bool isFavorite, CancellationToken cancellationToken = default)
        => Task.FromResult<UserEngagementSnapshot?>(null);

    public Task<UserEngagementSnapshot?> SetBookmarkedAsync(AffinityHostType hostType, int hostId, bool saved, CancellationToken cancellationToken = default)
        => Task.FromResult<UserEngagementSnapshot?>(null);

    public Task<UserEngagementSnapshot?> SetRatingAsync(AffinityHostType hostType, int hostId, int? value, string aspect = "overall", CancellationToken cancellationToken = default)
        => Task.FromResult<UserEngagementSnapshot?>(null);

    public Task<bool> RecordInteractionAsync(InteractionHostType hostType, int hostId, InteractionKind kind, JsonElement? meta = null, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<bool> RecordPlaybackIntervalsAsync(PlaybackIntervalsRequestDto dto, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<IReadOnlyList<EngagementInteractionDto>> GetInteractionsAsync(InteractionHostType? hostType = null, int? hostId = null, int limit = 100, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<EngagementInteractionDto>>([]);

    public Task<UserEngagementSnapshot?> RecordVideoPlayAsync(int videoId, CancellationToken cancellationToken = default)
        => Task.FromResult<UserEngagementSnapshot?>(null);

    public Task<UserEngagementSnapshot?> DeleteVideoPlayAsync(int videoId, CancellationToken cancellationToken = default)
        => Task.FromResult<UserEngagementSnapshot?>(null);

    public Task<UserEngagementSnapshot?> ResetVideoPlayAsync(int videoId, CancellationToken cancellationToken = default)
        => Task.FromResult<UserEngagementSnapshot?>(null);

    public Task<UserEngagementSnapshot?> IncrementVideoLikeAsync(int videoId, CancellationToken cancellationToken = default)
        => Task.FromResult<UserEngagementSnapshot?>(null);

    public Task<UserEngagementSnapshot?> AddHistoricalVideoLikeAsync(int videoId, DateTime at, CancellationToken cancellationToken = default)
        => Task.FromResult<UserEngagementSnapshot?>(null);

    public Task<UserEngagementSnapshot?> DecrementVideoLikeAsync(int videoId, CancellationToken cancellationToken = default)
        => Task.FromResult<UserEngagementSnapshot?>(null);

    public Task<UserEngagementSnapshot?> ResetVideoLikeAsync(int videoId, CancellationToken cancellationToken = default)
        => Task.FromResult<UserEngagementSnapshot?>(null);

    public Task<UserEngagementSnapshot?> IncrementLikeAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken = default)
        => Task.FromResult<UserEngagementSnapshot?>(null);

    public Task<UserEngagementSnapshot?> AddHistoricalLikeAsync(AffinityHostType hostType, int hostId, DateTime at, CancellationToken cancellationToken = default)
        => Task.FromResult<UserEngagementSnapshot?>(null);

    public Task<UserEngagementSnapshot?> DecrementLikeAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken = default)
        => Task.FromResult<UserEngagementSnapshot?>(null);

    public Task<UserEngagementSnapshot?> ResetLikeAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken = default)
        => Task.FromResult<UserEngagementSnapshot?>(null);

    public Task<UserEngagementSnapshot?> IncrementImageLikeAsync(int imageId, CancellationToken cancellationToken = default)
        => Task.FromResult<UserEngagementSnapshot?>(null);

    public Task<UserEngagementSnapshot?> DecrementImageLikeAsync(int imageId, CancellationToken cancellationToken = default)
        => Task.FromResult<UserEngagementSnapshot?>(null);

    public Task<UserEngagementSnapshot?> ResetImageLikeAsync(int imageId, CancellationToken cancellationToken = default)
        => Task.FromResult<UserEngagementSnapshot?>(null);

    public Task<UserEngagementSnapshot?> ResetVideoActivityAsync(int videoId, CancellationToken cancellationToken = default)
        => Task.FromResult<UserEngagementSnapshot?>(null);

    public Task<UserEngagementSnapshot?> ResetActivityAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken = default)
        => Task.FromResult<UserEngagementSnapshot?>(null);

    public Task<int> ResetAllActivityAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    public Task<int> WipeAllEngagementAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    public Task<UserEngagementSnapshot?> SetVideoRatingAsync(int videoId, int? value, string aspect = "overall", CancellationToken cancellationToken = default)
        => Task.FromResult<UserEngagementSnapshot?>(null);

    public Task<VideoHistoryDto?> GetVideoHistoryAsync(int videoId, CancellationToken cancellationToken = default)
        => Task.FromResult<VideoHistoryDto?>(null);

    public Task<VideoHistoryDto?> GetHistoryAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken = default)
        => Task.FromResult<VideoHistoryDto?>(null);
}
