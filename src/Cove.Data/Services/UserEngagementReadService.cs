using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data.Auth;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Services;

/// <summary>
/// EF-backed implementation of <see cref="IUserEngagementReadService"/>. Queries with
/// <c>IgnoreQueryFilters()</c> and an explicit <c>userId</c> predicate so reads are correct regardless
/// of the ambient principal (e.g. background recompute jobs), rather than relying on the per-principal
/// global query filter on affinities/ratings.
/// </summary>
public sealed class UserEngagementReadService(CoveContext db) : IUserEngagementReadService
{
    private readonly CoveContext _db = db;

    public async Task<IReadOnlyList<UserEntityAffinity>> GetAffinitiesForUserAsync(
        int userId, AffinityHostType? hostType = null, int? skip = null, int? take = null, CancellationToken cancellationToken = default)
    {
        var query = _db.UserEntityAffinities.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.UserId == userId);
        if (hostType is { } ht)
            query = query.Where(a => a.HostType == ht);

        query = query.OrderByDescending(a => a.LastConsumedAt ?? a.LastInteractedAt ?? a.UpdatedAt);
        if (skip is { } s)
            query = query.Skip(s);
        if (take is { } t)
            query = query.Take(t);

        return await query.ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UserEntityAffinity>> GetAffinitiesForEntitiesAsync(
        int userId, AffinityHostType hostType, IReadOnlyCollection<int> hostIds, CancellationToken cancellationToken = default)
    {
        if (hostIds.Count == 0)
            return [];

        return await _db.UserEntityAffinities.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.UserId == userId && a.HostType == hostType && hostIds.Contains(a.HostId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Rating>> GetRatingsForUserAsync(
        int userId, RatingHostType? hostType = null, string? aspect = null, CancellationToken cancellationToken = default)
    {
        var query = _db.Ratings.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.UserId == userId);
        if (hostType is { } ht)
            query = query.Where(r => r.HostType == ht);
        if (!string.IsNullOrEmpty(aspect))
            query = query.Where(r => r.Aspect == aspect);

        return await query.ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Rating>> GetRatingsForEntitiesAsync(
        int userId, RatingHostType hostType, IReadOnlyCollection<int> hostIds, CancellationToken cancellationToken = default)
    {
        if (hostIds.Count == 0)
            return [];

        return await _db.Ratings.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.UserId == userId && r.HostType == hostType && hostIds.Contains(r.HostId))
            .ToListAsync(cancellationToken);
    }

    public async Task<UserRatingStats> GetRatingStatsAsync(
        int userId, RatingHostType? hostType = null, string aspect = "overall", CancellationToken cancellationToken = default)
    {
        var query = _db.Ratings.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.UserId == userId && r.Aspect == aspect);
        if (hostType is { } ht)
            query = query.Where(r => r.HostType == ht);

        // Ratings per user are bounded; compute stats in memory for provider-portable std-dev.
        var values = await query.Select(r => r.Value).ToListAsync(cancellationToken);
        if (values.Count == 0)
            return new UserRatingStats(0, 0, 0, 0, 0);

        var mean = values.Average();
        var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
        return new UserRatingStats(values.Count, mean, Math.Sqrt(variance), values.Min(), values.Max());
    }

    public async Task<IReadOnlyDictionary<int, double>> GetMediaDurationsAsync(
        AffinityHostType hostType, IReadOnlyCollection<int> hostIds, CancellationToken cancellationToken = default)
    {
        if (hostIds.Count == 0)
            return new Dictionary<int, double>();

        switch (hostType)
        {
            case AffinityHostType.Video:
                return await _db.Set<VideoFile>().AsNoTracking()
                    .Where(f => f.VideoId != null && hostIds.Contains(f.VideoId.Value) && f.Duration > 0)
                    .GroupBy(f => f.VideoId!.Value)
                    .Select(g => new { Id = g.Key, Duration = g.Max(f => f.Duration) })
                    .ToDictionaryAsync(x => x.Id, x => x.Duration, cancellationToken);
            case AffinityHostType.Audio:
                return await _db.Set<AudioFile>().AsNoTracking()
                    .Where(f => f.AudioId != null && hostIds.Contains(f.AudioId.Value) && f.Duration > 0)
                    .GroupBy(f => f.AudioId!.Value)
                    .Select(g => new { Id = g.Key, Duration = g.Max(f => f.Duration) })
                    .ToDictionaryAsync(x => x.Id, x => x.Duration, cancellationToken);
            default:
                return new Dictionary<int, double>();
        }
    }

    public async Task<int> GetDwellPositiveSecAsync(int userId, CancellationToken cancellationToken = default)
    {
        var raw = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.UiPreferencesJson)
            .FirstOrDefaultAsync(cancellationToken);
        var configured = UserService.ParseUiPreferences(raw)?.Tracking?.DwellPositiveSec;
        return Math.Clamp(configured ?? 25, 1, 86_400);
    }
}
