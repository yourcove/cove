using Cove.Core.Entities;

namespace Cove.Core.Interfaces;

/// <summary>
/// Per-user rating distribution statistics, used to z-score ratings (adapts to generous vs stingy raters).
/// </summary>
public sealed record UserRatingStats(int Count, double Mean, double StdDev, int Min, int Max);

/// <summary>
/// Bulk, explicit-userId read access to a user's engagement signals (affinities + ratings) and
/// normalization statistics. Intended for extensions (recommendations, analytics) that need to read
/// many entities' signals at once for scoring. This is the unopinionated "raw reads + normalization"
/// layer; opinionated preference fusion lives in the consuming extension.
///
/// Unlike <see cref="IUserEngagementService"/> (single-entity, current-principal-scoped, lossy
/// snapshot), this service takes an explicit <c>userId</c> (so it works off-request, e.g. in background
/// jobs) and returns full entity rows including every interaction sub-count and all rating aspects.
/// </summary>
public interface IUserEngagementReadService
{
    /// <summary>All affinity rows for a user, newest activity first, optionally filtered by host type and paged.</summary>
    Task<IReadOnlyList<UserEntityAffinity>> GetAffinitiesForUserAsync(
        int userId, AffinityHostType? hostType = null, int? skip = null, int? take = null, CancellationToken cancellationToken = default);

    /// <summary>Affinity rows for a specific set of entities of one host type (e.g. a candidate set to score).</summary>
    Task<IReadOnlyList<UserEntityAffinity>> GetAffinitiesForEntitiesAsync(
        int userId, AffinityHostType hostType, IReadOnlyCollection<int> hostIds, CancellationToken cancellationToken = default);

    /// <summary>All rating rows for a user (including non-"overall" aspect ratings), optionally filtered.</summary>
    Task<IReadOnlyList<Rating>> GetRatingsForUserAsync(
        int userId, RatingHostType? hostType = null, string? aspect = null, CancellationToken cancellationToken = default);

    /// <summary>Rating rows for a specific set of entities of one host type.</summary>
    Task<IReadOnlyList<Rating>> GetRatingsForEntitiesAsync(
        int userId, RatingHostType hostType, IReadOnlyCollection<int> hostIds, CancellationToken cancellationToken = default);

    /// <summary>Distribution stats (count/mean/std/min/max) of a user's ratings for a given aspect, for normalization.</summary>
    Task<UserRatingStats> GetRatingStatsAsync(
        int userId, RatingHostType? hostType = null, string aspect = "overall", CancellationToken cancellationToken = default);

    /// <summary>
    /// Media duration (seconds) for a set of time-based entities (Video/Audio), so consumed-seconds can be
    /// converted to watch percent. Returns only ids that have a positive duration; other host types return empty.
    /// </summary>
    Task<IReadOnlyDictionary<int, double>> GetMediaDurationsAsync(
        AffinityHostType hostType, IReadOnlyCollection<int> hostIds, CancellationToken cancellationToken = default);

    /// <summary>The user's configured "settled-in" dwell threshold in seconds (a contiguous watch ≥ this counts
    /// as a positive dwell signal). From the user's engagement/tracking preferences; defaults to 25.</summary>
    Task<int> GetDwellPositiveSecAsync(int userId, CancellationToken cancellationToken = default);
}
