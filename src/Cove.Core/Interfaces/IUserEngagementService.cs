using Cove.Core.DTOs;
using Cove.Core.Entities;
using System.Text.Json;

namespace Cove.Core.Interfaces;

public sealed record UserEngagementSnapshot(
    bool IsFavorite,
    int? Rating,
    double ResumeTime,
    // Sum of per-session distinct watched deltas. This is not the raw all-interval total.
    double PlayDuration,
    int PlayCount,
    DateTime? LastPlayedAt,
    int LikeCount,
    int DerivedLikeCount,
    int PageVisitCount,
    int CompleteCount);

public interface IUserEngagementService
{
    Task<UserEngagementSnapshot?> GetSnapshotAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken = default);

    Task<Dictionary<string, int>?> GetRatingsByAspectAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken = default);

    Task<Dictionary<int, UserEngagementSnapshot>> GetSnapshotsAsync(AffinityHostType hostType, IEnumerable<int> hostIds, CancellationToken cancellationToken = default);

    Task<Dictionary<int, UserEngagementSnapshot>> GetVideoSnapshotsAsync(IEnumerable<int> videoIds, CancellationToken cancellationToken = default);

    Task<UserEngagementSnapshot?> SetFavoriteAsync(AffinityHostType hostType, int hostId, bool isFavorite, CancellationToken cancellationToken = default);

    Task<UserEngagementSnapshot?> SetBookmarkedAsync(AffinityHostType hostType, int hostId, bool saved, CancellationToken cancellationToken = default);

    Task<UserEngagementSnapshot?> SetRatingAsync(AffinityHostType hostType, int hostId, int? value, string aspect = "overall", CancellationToken cancellationToken = default);

    Task<bool> RecordInteractionAsync(InteractionHostType hostType, int hostId, InteractionKind kind, JsonElement? meta = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EngagementInteractionDto>> GetInteractionsAsync(InteractionHostType? hostType = null, int? hostId = null, int limit = 100, CancellationToken cancellationToken = default);

    Task<UserEngagementSnapshot?> RecordVideoPlayAsync(int videoId, CancellationToken cancellationToken = default);

    Task<UserEngagementSnapshot?> DeleteVideoPlayAsync(int videoId, CancellationToken cancellationToken = default);

    Task<UserEngagementSnapshot?> ResetVideoPlayAsync(int videoId, CancellationToken cancellationToken = default);

    Task<UserEngagementSnapshot?> IncrementVideoLikeAsync(int videoId, CancellationToken cancellationToken = default);

    Task<UserEngagementSnapshot?> DecrementVideoLikeAsync(int videoId, CancellationToken cancellationToken = default);

    Task<UserEngagementSnapshot?> ResetVideoLikeAsync(int videoId, CancellationToken cancellationToken = default);

    Task<UserEngagementSnapshot?> IncrementImageLikeAsync(int imageId, CancellationToken cancellationToken = default);

    Task<UserEngagementSnapshot?> DecrementImageLikeAsync(int imageId, CancellationToken cancellationToken = default);

    Task<UserEngagementSnapshot?> ResetImageLikeAsync(int imageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Record a batch of contiguous watched intervals for a playback session.
    /// Creates or updates the PlaybackSession, inserts PlaybackInterval rows, recomputes per-session
    /// TotalWatchedSec from the merged interval set, and updates UserEntityAffinity accordingly.
    /// </summary>
    Task<bool> RecordPlaybackIntervalsAsync(PlaybackIntervalsRequestDto dto, CancellationToken cancellationToken = default);

    Task<UserEngagementSnapshot?> ResetVideoActivityAsync(int videoId, CancellationToken cancellationToken = default);

    Task<UserEngagementSnapshot?> ResetActivityAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reset ALL watch data for the current user: deletes every playback session/interval and clears
    /// the watch-derived affinity metrics (consumed seconds, view/complete counts, resume position).
    /// Ratings, likes, favorites, and interactions are preserved. Returns the number of affinity rows affected.
    /// </summary>
    Task<int> ResetAllActivityAsync(CancellationToken cancellationToken = default);

    /// <summary>Wipe only the system-collected (implicit) engagement for the current user — playback
    /// sessions/intervals, user-global sessions, behavioral interactions, derived likes, watch time,
    /// view/complete counts, dwell, and page visits. Explicit signals (ratings, likes, favorites,
    /// bookmarks) are preserved. Used to clear data poisoned by a bug. Returns affinity rows affected.</summary>
    Task<int> WipeAllEngagementAsync(CancellationToken cancellationToken = default);

    Task<UserEngagementSnapshot?> SetVideoRatingAsync(int videoId, int? value, string aspect = "overall", CancellationToken cancellationToken = default);

    Task<VideoHistoryDto?> GetVideoHistoryAsync(int videoId, CancellationToken cancellationToken = default);

    Task<VideoHistoryDto?> GetHistoryAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken = default);
}
