using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using System.Text.Json;

namespace Cove.Data.Services;

public sealed partial class UserEngagementService(
    CoveContext db,
    ICurrentPrincipalAccessor principalAccessor,
    IEventBus? eventBus = null,
    ILogger<UserEngagementService>? logger = null) : IUserEngagementService
{
    private readonly ILogger<UserEngagementService> _logger = logger ?? NullLogger<UserEngagementService>.Instance;

    // Publish a rating-changed event (best-effort) so extensions/recommenders can refresh derived state. The
    // Entity payload carries userId + aspect + value (value null = cleared) so a handler knows WHO and WHAT changed.
    private void PublishRatingEvent(EventType type, AffinityHostType hostType, int hostId, int userId, string aspect, int? value)
        => eventBus?.Publish(new EntityEvent(type, hostType.ToString().ToLowerInvariant(), hostId,
            new Dictionary<string, object?> { ["userId"] = userId, ["aspect"] = aspect, ["value"] = value }));

    private static readonly UserEngagementSnapshot EmptySnapshot = new(false, null, 0d, 0d, 0, null, 0, 0, 0, 0);
    // ..., MinDerivedLikeSessionSeconds = 1200 (a session must be ≥20 min long to earn its derived like),
    // SessionIdleTimeoutSec = 1800 (a ≥30 min gap between events starts a new session),
    // DwellPositiveSec = 25 (a contiguous watch ≥ this counts as a "settled in" positive for the recommenders).
    private static readonly TrackingSettings DefaultTrackingSettings = new(true, 30, 0.45d, 5, 1200, 1800, 25);

    private sealed record TrackingSettings(
        bool Enabled,
        int MinViewSeconds,
        double ViewCompletionRatio,
        int MinImageDetailViewSeconds,
        int MinDerivedLikeSessionSeconds,
        int SessionIdleTimeoutSec,
        int DwellPositiveSec);

    public async Task<UserEngagementSnapshot?> GetSnapshotAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken = default)
    {
        if (!await EntityExistsAsync(hostType, hostId, cancellationToken))
            return null;

        var snapshots = await GetSnapshotsAsync(hostType, [hostId], cancellationToken);
        return snapshots.GetValueOrDefault(hostId) ?? EmptySnapshot;
    }

    public async Task<Dictionary<string, int>?> GetRatingsByAspectAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken = default)
    {
        if (!await EntityExistsAsync(hostType, hostId, cancellationToken))
            return null;

        var userId = principalAccessor.Current?.UserId;
        if (!userId.HasValue)
            return [];

        var ratingHostType = ToRatingHostType(hostType);
        var ratings = await db.Ratings
            .Where(rating => rating.UserId == userId.Value && rating.HostType == ratingHostType && rating.HostId == hostId)
            .OrderBy(rating => rating.Aspect)
            .ToListAsync(cancellationToken);

        return ratings.ToDictionary(rating => rating.Aspect, rating => rating.Value);
    }

    public async Task<Dictionary<int, UserEngagementSnapshot>> GetSnapshotsAsync(AffinityHostType hostType, IEnumerable<int> hostIds, CancellationToken cancellationToken = default)
    {
        var ids = hostIds.Distinct().ToArray();
        if (ids.Length == 0)
            return [];

        var visibleIds = await GetVisibleEntityIdsAsync(hostType, ids, cancellationToken);
        var visibleIdSet = visibleIds.ToHashSet();
        if (visibleIds.Length == 0)
            return ids.ToDictionary(id => id, _ => EmptySnapshot);

        var userId = principalAccessor.Current?.UserId;
        if (!userId.HasValue)
            return ids.ToDictionary(id => id, _ => EmptySnapshot);

        var ratingHostType = ToRatingHostType(hostType);
        // Group rather than ToDictionaryAsync-by-HostId: a database seeded by a Stash import or a plain-SQL
        // restore can be missing the user_entity_affinities (UserId,HostType,HostId) unique index and carry
        // DUPLICATE rows (which would make a keyed dictionary throw "same key already added"). The
        // AffinityDuplicateRepair migration cleans those up + (re)creates the index, but this read must never
        // 500 if it runs against a still-drifted DB. Keep the oldest row per host (matches the dedup's
        // keep-lowest-Id), newest rating per host.
        var affinities = (await db.UserEntityAffinities
            .Where(affinity => affinity.UserId == userId.Value && affinity.HostType == hostType && visibleIds.Contains(affinity.HostId))
            .ToListAsync(cancellationToken))
            .GroupBy(affinity => affinity.HostId)
            .ToDictionary(group => group.Key, group => group.OrderBy(affinity => affinity.Id).First());

        var ratings = (await db.Ratings
            .Where(rating => rating.UserId == userId.Value && rating.HostType == ratingHostType && rating.Aspect == "overall" && visibleIds.Contains(rating.HostId))
            .ToListAsync(cancellationToken))
            .GroupBy(rating => rating.HostId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(rating => rating.Id).First());

        return ids.ToDictionary(id => id, id => visibleIdSet.Contains(id)
            ? ToSnapshot(affinities.GetValueOrDefault(id), ratings.GetValueOrDefault(id))
            : EmptySnapshot);
    }

    public Task<Dictionary<int, UserEngagementSnapshot>> GetVideoSnapshotsAsync(IEnumerable<int> videoIds, CancellationToken cancellationToken = default)
        => GetSnapshotsAsync(AffinityHostType.Video, videoIds, cancellationToken);

    public async Task<UserEngagementSnapshot?> SetFavoriteAsync(AffinityHostType hostType, int hostId, bool isFavorite, CancellationToken cancellationToken = default)
    {
        if (!await EntityExistsAsync(hostType, hostId, cancellationToken))
            return null;

        var affinity = await GetOrCreateAffinityAsync(hostType, hostId, cancellationToken);
        if (affinity != null)
        {
            affinity.IsFavorite = isFavorite;
            affinity.FavoritedAt = isFavorite ? DateTime.UtcNow : null;
        }

        await MirrorLegacyFavoriteAsync(hostType, hostId, isFavorite, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        TraceFavoriteSet(hostType, hostId, isFavorite);
        return (await GetSnapshotsAsync(hostType, [hostId], cancellationToken)).GetValueOrDefault(hostId) ?? EmptySnapshot;
    }

    public async Task<UserEngagementSnapshot?> SetBookmarkedAsync(AffinityHostType hostType, int hostId, bool saved, CancellationToken cancellationToken = default)
    {
        if (!await EntityExistsAsync(hostType, hostId, cancellationToken))
            return null;

        var affinity = await GetOrCreateAffinityAsync(hostType, hostId, cancellationToken);
        if (affinity != null)
            affinity.IsBookmarked = saved;

        await db.SaveChangesAsync(cancellationToken);
        TraceBookmarkSet(hostType, hostId, saved);
        return (await GetSnapshotsAsync(hostType, [hostId], cancellationToken)).GetValueOrDefault(hostId) ?? EmptySnapshot;
    }

    public async Task<UserEngagementSnapshot?> SetRatingAsync(AffinityHostType hostType, int hostId, int? value, string aspect = "overall", CancellationToken cancellationToken = default)
    {
        if (hostType == AffinityHostType.Video)
            return await SetVideoRatingAsync(hostId, value, aspect, cancellationToken);

        if (!await EntityExistsAsync(hostType, hostId, cancellationToken))
            return null;

        var normalizedAspect = NormalizeAspect(aspect);

        var userId = principalAccessor.Current?.UserId;
        EventType? ratingEvent = null;
        if (userId.HasValue)
        {
            var ratingHostType = ToRatingHostType(hostType);
            var existing = await db.Ratings.FirstOrDefaultAsync(
                rating => rating.UserId == userId.Value && rating.HostType == ratingHostType && rating.HostId == hostId && rating.Aspect == normalizedAspect,
                cancellationToken);

            if (!value.HasValue)
            {
                if (existing != null) { db.Ratings.Remove(existing); ratingEvent = EventType.RatingDeleted; }
            }
            else if (existing == null)
            {
                db.Ratings.Add(new Rating
                {
                    UserId = userId.Value,
                    HostType = ratingHostType,
                    HostId = hostId,
                    Aspect = normalizedAspect,
                    Value = Math.Clamp(value.Value, 0, 100),
                });
                ratingEvent = EventType.RatingCreated;
            }
            else
            {
                existing.Value = Math.Clamp(value.Value, 0, 100);
                ratingEvent = EventType.RatingUpdated;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        if (ratingEvent is { } evt && userId is { } uid)
        {
            TraceRatingChanged(evt, hostType, hostId, normalizedAspect, value.HasValue ? Math.Clamp(value.Value, 0, 100) : null);
            PublishRatingEvent(evt, hostType, hostId, uid, normalizedAspect, value);
        }
        return (await GetSnapshotsAsync(hostType, [hostId], cancellationToken)).GetValueOrDefault(hostId) ?? EmptySnapshot;
    }

    public async Task<bool> RecordInteractionAsync(InteractionHostType hostType, int hostId, InteractionKind kind, JsonElement? meta = null, CancellationToken cancellationToken = default)
    {
        var userId = principalAccessor.Current?.UserId;
        if (!userId.HasValue)
            return false;

        var tracking = await GetTrackingSettingsAsync(userId.Value, cancellationToken);
        if (!tracking.Enabled)
            return true;

        var normalizedHostId = InteractionValueMapper.RequiresConcreteHost(hostType) ? hostId : 0;
        if (InteractionValueMapper.RequiresConcreteHost(hostType) && !await InteractionHostExistsAsync(hostType, normalizedHostId, cancellationToken))
            return false;

        var now = DateTime.UtcNow;
        if (TryMapAffinityHostType(hostType, out var affinityHostType))
        {
            var affinity = await GetOrCreateAffinityAsync(affinityHostType, normalizedHostId, cancellationToken);
            if (affinity != null)
            {
                ApplyInteractionAggregate(affinity, kind, now);
            }
        }

        // A page visit means the user is now looking at this entity — make it the user-global session's
        // most-recent ("finished on") entity, so a derived like can land on an image/text, not just played media.
        if (kind == InteractionKind.PageVisit && InteractionValueMapper.RequiresConcreteHost(hostType) && normalizedHostId > 0)
            await ResolveUserSessionAsync(userId.Value, hostType, normalizedHostId, now, tracking.MinDerivedLikeSessionSeconds, tracking.SessionIdleTimeoutSec, cancellationToken);

        db.Interactions.Add(new Interaction
        {
            UserId = userId.Value,
            HostType = hostType,
            HostId = normalizedHostId,
            Kind = kind,
            At = now,
            Meta = CloneJsonDocument(meta),
        });

        await db.SaveChangesAsync(cancellationToken);
        TraceInteractionRecorded(kind, hostType, normalizedHostId);
        return true;
    }

    public async Task<IReadOnlyList<EngagementInteractionDto>> GetInteractionsAsync(InteractionHostType? hostType = null, int? hostId = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        var userId = principalAccessor.Current?.UserId;
        if (!userId.HasValue)
            return [];

        var normalizedLimit = Math.Clamp(limit, 1, 500);
        var query = db.Interactions
            .Where(interaction => interaction.UserId == userId.Value);

        if (hostType.HasValue)
            query = query.Where(interaction => interaction.HostType == hostType.Value);

        if (hostId.HasValue)
            query = query.Where(interaction => interaction.HostId == hostId.Value);

        return await query
            .OrderByDescending(interaction => interaction.At)
            .ThenByDescending(interaction => interaction.Id)
            .Take(normalizedLimit)
            .Select(interaction => ToEngagementInteractionDto(interaction))
            .ToListAsync(cancellationToken);
    }

    public async Task<UserEngagementSnapshot?> RecordVideoPlayAsync(int videoId, CancellationToken cancellationToken = default)
    {
        var video = await db.Videos.FirstOrDefaultAsync(item => item.Id == videoId, cancellationToken);
        if (video is null)
            return null;

        var now = DateTime.UtcNow;
        var affinity = await GetOrCreateVideoAffinityAsync(videoId, cancellationToken);
        if (affinity != null)
        {
            affinity.ViewCount++;
            affinity.LastConsumedAt = now;
        }

        db.Set<VideoPlayHistory>().Add(new VideoPlayHistory { VideoId = videoId, PlayedAt = now });
        await db.SaveChangesAsync(cancellationToken);

        return await BuildVideoSnapshotAsync(videoId, video, affinity, cancellationToken);
    }

    public async Task<UserEngagementSnapshot?> DeleteVideoPlayAsync(int videoId, CancellationToken cancellationToken = default)
    {
        var video = await db.Videos.FirstOrDefaultAsync(item => item.Id == videoId, cancellationToken);
        if (video is null)
            return null;

        var affinity = await GetOrCreateVideoAffinityAsync(videoId, cancellationToken, createIfMissing: false);
        if (affinity != null)
        {
            affinity.ViewCount = Math.Max(0, affinity.ViewCount - 1);

            // Remove the most recent playback session for this user+video
            var lastPlaybackSession = await db.PlaybackSessions
                .Where(session => session.UserId == affinity.UserId && session.HostType == InteractionHostType.Video && session.HostId == videoId)
                .OrderByDescending(session => session.StartedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (lastPlaybackSession != null)
                db.PlaybackSessions.Remove(lastPlaybackSession);

            affinity.LastConsumedAt = await db.PlaybackSessions
                .Where(session => session.UserId == affinity.UserId && session.HostType == InteractionHostType.Video && session.HostId == videoId)
                .OrderByDescending(session => session.StartedAt)
                .Select(session => (DateTime?)session.StartedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        // Remove the most recent global play history entry for this video
        var lastPlayHistory = await db.Set<VideoPlayHistory>()
            .Where(h => h.VideoId == videoId)
            .OrderByDescending(h => h.PlayedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (lastPlayHistory != null)
            db.Set<VideoPlayHistory>().Remove(lastPlayHistory);
        await db.SaveChangesAsync(cancellationToken);

        return await BuildVideoSnapshotAsync(videoId, video, affinity, cancellationToken);
    }

    public async Task<UserEngagementSnapshot?> ResetVideoPlayAsync(int videoId, CancellationToken cancellationToken = default)
    {
        var video = await db.Videos.FirstOrDefaultAsync(item => item.Id == videoId, cancellationToken);
        if (video is null)
            return null;

        var affinity = await GetOrCreateVideoAffinityAsync(videoId, cancellationToken);
        if (affinity != null)
        {
            affinity.ViewCount = 0;
            affinity.CompleteCount = 0;
            affinity.TotalConsumedSec = 0;
            affinity.LastPositionSec = 0;
            affinity.LastConsumedAt = null;

            var playbackSessions = await db.PlaybackSessions
                .Where(session => session.UserId == affinity.UserId && session.HostType == InteractionHostType.Video && session.HostId == videoId)
                .ToListAsync(cancellationToken);
            db.PlaybackSessions.RemoveRange(playbackSessions);
        }

        var allPlayHistory = await db.Set<VideoPlayHistory>()
            .Where(h => h.VideoId == videoId)
            .ToListAsync(cancellationToken);
        db.Set<VideoPlayHistory>().RemoveRange(allPlayHistory);
        await db.SaveChangesAsync(cancellationToken);

        return await BuildVideoSnapshotAsync(videoId, video, affinity, cancellationToken);
    }

    public Task<UserEngagementSnapshot?> IncrementVideoLikeAsync(int videoId, CancellationToken cancellationToken = default)
        => IncrementLikeAsync(AffinityHostType.Video, videoId, cancellationToken);

    public Task<UserEngagementSnapshot?> AddHistoricalVideoLikeAsync(int videoId, DateTime at, CancellationToken cancellationToken = default)
        => AddHistoricalLikeAsync(AffinityHostType.Video, videoId, at, cancellationToken);

    public Task<UserEngagementSnapshot?> DecrementVideoLikeAsync(int videoId, CancellationToken cancellationToken = default)
        => DecrementLikeAsync(AffinityHostType.Video, videoId, cancellationToken);

    public Task<UserEngagementSnapshot?> ResetVideoLikeAsync(int videoId, CancellationToken cancellationToken = default)
        => ResetLikeAsync(AffinityHostType.Video, videoId, cancellationToken);

    public Task<UserEngagementSnapshot?> IncrementImageLikeAsync(int imageId, CancellationToken cancellationToken = default)
        => IncrementLikeAsync(AffinityHostType.Image, imageId, cancellationToken);

    public Task<UserEngagementSnapshot?> DecrementImageLikeAsync(int imageId, CancellationToken cancellationToken = default)
        => DecrementLikeAsync(AffinityHostType.Image, imageId, cancellationToken);

    public Task<UserEngagementSnapshot?> ResetImageLikeAsync(int imageId, CancellationToken cancellationToken = default)
        => ResetLikeAsync(AffinityHostType.Image, imageId, cancellationToken);

    public Task<UserEngagementSnapshot?> IncrementLikeAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken = default)
        => IncrementLikeCoreAsync(hostType, hostId, cancellationToken);

    public Task<UserEngagementSnapshot?> AddHistoricalLikeAsync(AffinityHostType hostType, int hostId, DateTime at, CancellationToken cancellationToken = default)
        => IncrementLikeCoreAsync(hostType, hostId, cancellationToken, at);

    public async Task<UserEngagementSnapshot?> DeleteLikeAtAsync(AffinityHostType hostType, int hostId, DateTime at, CancellationToken cancellationToken = default)
    {
        if (!IsDirectLikeHost(hostType))
            return null;
        if (!await EntityExistsAsync(hostType, hostId, cancellationToken))
            return null;

        var affinity = await GetOrCreateAffinityAsync(hostType, hostId, cancellationToken, createIfMissing: false);
        if (affinity != null)
        {
            var normalizedAt = at.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(at, DateTimeKind.Utc)
                : at.ToUniversalTime();
            var interactionHostType = ToInteractionHostType(hostType);
            var interaction = await db.Interactions
                .Where(item => item.UserId == affinity.UserId
                    && item.HostType == interactionHostType
                    && item.HostId == hostId
                    && item.Kind == InteractionKind.LikeCount
                    && item.At == normalizedAt)
                .OrderBy(item => item.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (interaction != null)
            {
                db.Interactions.Remove(interaction);
                affinity.LikeCount = Math.Max(0, affinity.LikeCount - 1);
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        TraceLikeCountChanged(hostType, hostId, "deleted from history", affinity?.LikeCount ?? 0);

        return (await GetSnapshotsAsync(hostType, [hostId], cancellationToken)).GetValueOrDefault(hostId) ?? EmptySnapshot;
    }

    private async Task<UserEngagementSnapshot?> IncrementLikeCoreAsync(
        AffinityHostType hostType,
        int hostId,
        CancellationToken cancellationToken,
        DateTime? at = null)
    {
        if (!IsDirectLikeHost(hostType))
            return null;
        if (!await EntityExistsAsync(hostType, hostId, cancellationToken))
            return null;

        var now = at ?? DateTime.UtcNow;
        var affinity = await GetOrCreateAffinityAsync(hostType, hostId, cancellationToken);
        if (affinity != null)
        {
            affinity.LikeCount++;
            db.Interactions.Add(new Interaction
            {
                UserId = affinity.UserId,
                HostType = ToInteractionHostType(hostType),
                HostId = hostId,
                Kind = InteractionKind.LikeCount,
                At = now,
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        TraceLikeCountChanged(hostType, hostId, "incremented", affinity?.LikeCount ?? 0);

        return (await GetSnapshotsAsync(hostType, [hostId], cancellationToken)).GetValueOrDefault(hostId) ?? EmptySnapshot;
    }

    public async Task<UserEngagementSnapshot?> DecrementLikeAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken = default)
    {
        if (!IsDirectLikeHost(hostType))
            return null;
        if (!await EntityExistsAsync(hostType, hostId, cancellationToken))
            return null;

        var interactionHostType = ToInteractionHostType(hostType);
        var affinity = await GetOrCreateAffinityAsync(hostType, hostId, cancellationToken, createIfMissing: false);
        if (affinity != null)
        {
            affinity.LikeCount = Math.Max(0, affinity.LikeCount - 1);
            var lastInteraction = await db.Interactions
                .Where(interaction => interaction.UserId == affinity.UserId && interaction.HostType == interactionHostType && interaction.HostId == hostId && interaction.Kind == InteractionKind.LikeCount)
                .OrderByDescending(interaction => interaction.At)
                .FirstOrDefaultAsync(cancellationToken);
            if (lastInteraction != null)
                db.Interactions.Remove(lastInteraction);
        }
        await db.SaveChangesAsync(cancellationToken);
        TraceLikeCountChanged(hostType, hostId, "decremented", affinity?.LikeCount ?? 0);

        return (await GetSnapshotsAsync(hostType, [hostId], cancellationToken)).GetValueOrDefault(hostId) ?? EmptySnapshot;
    }

    public async Task<UserEngagementSnapshot?> ResetLikeAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken = default)
    {
        if (!IsDirectLikeHost(hostType))
            return null;
        if (!await EntityExistsAsync(hostType, hostId, cancellationToken))
            return null;

        var interactionHostType = ToInteractionHostType(hostType);
        var affinity = await GetOrCreateAffinityAsync(hostType, hostId, cancellationToken);
        if (affinity != null)
        {
            affinity.LikeCount = 0;
            var interactions = await db.Interactions
                .Where(interaction => interaction.UserId == affinity.UserId && interaction.HostType == interactionHostType && interaction.HostId == hostId && interaction.Kind == InteractionKind.LikeCount)
                .ToListAsync(cancellationToken);
            db.Interactions.RemoveRange(interactions);
        }
        await db.SaveChangesAsync(cancellationToken);
        TraceLikeCountChanged(hostType, hostId, "reset", 0);

        return (await GetSnapshotsAsync(hostType, [hostId], cancellationToken)).GetValueOrDefault(hostId) ?? EmptySnapshot;
    }

    private static bool IsDirectLikeHost(AffinityHostType hostType)
        => hostType is AffinityHostType.Video or AffinityHostType.Image or AffinityHostType.Audio or AffinityHostType.Text;

    /// <summary>
    /// Record a batch of contiguous watched intervals for a playback session.
    /// Each call appends new PlaybackInterval rows, then recomputes per-session TotalWatchedSec
    /// from the full merged set — no guesswork from position deltas.
    /// Also updates UserEntityAffinity.TotalConsumedSec, LastPositionSec, and LastConsumedAt,
    /// and marks IsCompleted / CompleteCount when the session ends near the media tail.
    /// </summary>
    /// <summary>
    /// Resolve the user-global session this activity belongs to. A reload or a second device starts a fresh
    /// client SessionId, but if the user's last activity (any entity) was within the idle timeout this reuses
    /// the same server-resolved session (so reloads/devices don't fragment it); otherwise it finalizes the
    /// previous session (awarding its one derived like to the last entity) and starts a new one. Also records
    /// this entity as the session's most-recent ("finished on") entity.
    /// </summary>
    private async Task<UserSession> ResolveUserSessionAsync(int userId, InteractionHostType hostType, int hostId, DateTime now, double minDerivedLikeSeconds, int idleTimeoutSec, CancellationToken ct)
    {
        var recent = await db.UserSessions
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.LastSeenAt)
            .FirstOrDefaultAsync(ct);

        UserSession current;
        if (recent != null && now - recent.LastSeenAt <= TimeSpan.FromSeconds(idleTimeoutSec))
        {
            current = recent;
        }
        else
        {
            if (recent != null)
                await FinalizeUserSessionAsync(recent, minDerivedLikeSeconds, now, ct);
            current = new UserSession { UserId = userId, StartedAt = now, LastSeenAt = now };
            db.UserSessions.Add(current);
            await db.SaveChangesAsync(ct); // assign Id so PlaybackSession can key off it
        }

        current.LastSeenAt = now;
        if (InteractionValueMapper.RequiresConcreteHost(hostType) && hostId > 0)
        {
            current.LastHostType = hostType;
            current.LastHostId = hostId;
        }
        return current;
    }

    /// <summary>Award the single derived like for a finished user-session to the last entity the user engaged
    /// with (of any type), provided the session was long enough (its start-to-last-activity span ≥ the
    /// derived-like session-length threshold). Idempotent via <see cref="UserSession.DerivedLikeAwarded"/>.</summary>
    private async Task FinalizeUserSessionAsync(UserSession session, double minDerivedLikeSeconds, DateTime now, CancellationToken ct)
    {
        if (session.DerivedLikeAwarded || session.LastHostType is not { } lastHostType || session.LastHostId is not { } lastHostId)
            return;

        // The session must have lasted long enough (active span, gaps under the idle timeout) to earn a like.
        if ((session.LastSeenAt - session.StartedAt).TotalSeconds < minDerivedLikeSeconds)
            return;

        if (!TryMapAffinityHostType(lastHostType, out var affinityHostType))
            return;
        var affinity = await GetOrCreateAffinityAsync(affinityHostType, lastHostId, ct);
        if (affinity == null)
            return;

        affinity.DerivedLikeCount++;
        affinity.LastConsumedAt = now;
        session.DerivedLikeAwarded = true;
        db.Interactions.Add(new Interaction
        {
            UserId = session.UserId,
            HostType = lastHostType,
            HostId = lastHostId,
            Kind = InteractionKind.DerivedLike,
            At = now,
        });
    }

    public async Task<bool> RecordPlaybackIntervalsAsync(PlaybackIntervalsRequestDto dto, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await RecordPlaybackIntervalsCoreAsync(dto, cancellationToken);
            }
            catch (DbUpdateException ex) when (attempt == 0 && IsDuplicatePlaybackSessionInsert(ex))
            {
                db.ChangeTracker.Clear();
            }
        }

        return false;
    }

    private async Task<bool> RecordPlaybackIntervalsCoreAsync(PlaybackIntervalsRequestDto dto, CancellationToken cancellationToken)
    {
        if (!InteractionValueMapper.TryParseHostType(dto.HostType, out var hostType))
            return false;
        if (!await InteractionHostExistsAsync(hostType, dto.HostId, cancellationToken))
            return false;

        var userId = principalAccessor.Current?.UserId;
        if (!userId.HasValue)
            return false;

        var tracking = await GetTrackingSettingsAsync(userId.Value, cancellationToken);
        if (!tracking.Enabled)
            return true;

        var now = DateTime.UtcNow;
        if (!TryParseSessionState(dto.State, out var state))
            state = PlaybackSessionState.Active;
        var parentHostType = ParseOptionalHostType(dto.ParentHostType);
        var itemHostType = ParseOptionalHostType(dto.ItemHostType);

        // Resolve the user-global session this activity belongs to (server-authoritative, idle-timeout based,
        // cross-device). Reloads/devices reuse it instead of fragmenting; rollover finalizes the prior session
        // (awarding its one derived-like to the last entity the user engaged with).
        var userSession = await ResolveUserSessionAsync(userId.Value, hostType, dto.HostId, now, tracking.MinDerivedLikeSessionSeconds, tracking.SessionIdleTimeoutSec, cancellationToken);

        var session = await db.PlaybackSessions
            .Include(s => s.Intervals)
            .FirstOrDefaultAsync(
                s => s.UserId == userId.Value && s.HostType == hostType && s.HostId == dto.HostId && s.UserSessionId == userSession.Id,
                cancellationToken);

        var isRelational = db.Database.IsRelational();
        if (session == null)
        {
            // Create the session atomically. A find-then-Add pattern races with concurrent keepalives for
            // the same (UserId, SessionId) — both miss the lookup and both INSERT, violating the unique
            // index IX_PlaybackSessions_UserId_SessionId.
            //
            // We use ON CONFLICT DO UPDATE (not DO NOTHING). DO NOTHING leaves a window: when a concurrent
            // transaction has inserted the conflicting row but not yet committed, our INSERT's conflict
            // resolution does nothing and the immediately following SELECT can run on a snapshot that does
            // not yet see that uncommitted row — returning null and dropping us into the unprotected tracked
            // Add below, which then throws 23505 on SaveChanges. DO UPDATE forces a row lock on the
            // conflicting tuple: our statement blocks until the concurrent inserter commits, after which the
            // (no-op) UPDATE succeeds and the row is guaranteed visible to the re-query. Net effect: creation
            // is idempotent and the following SaveChanges only ever UPDATEs the row — no failed INSERT, no
            // duplicate-key exception (and no EF error log noise).
            if (isRelational)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO playback_sessions
                        ("UserId", "HostType", "HostId", "SessionId", "UserSessionId", "StartedAt", "LastSeenAt", "State",
                         "MediaDurationSec", "TotalWatchedSec", "IsCompleted", "CountsAsView", "DerivedLikeAwarded",
                         "CreatedAt", "UpdatedAt")
                    VALUES ({userId.Value}, {(int)hostType}, {dto.HostId}, {dto.SessionId}, {userSession.Id}, {now}, {now}, {(int)PlaybackSessionState.Active},
                         {0d}, {0d}, {false}, {false}, {false}, {now}, {now})
                    ON CONFLICT ("UserId", "HostType", "HostId", "UserSessionId") DO UPDATE SET "LastSeenAt" = EXCLUDED."LastSeenAt"
                    """, cancellationToken);

                session = await db.PlaybackSessions
                    .Include(s => s.Intervals)
                    .FirstOrDefaultAsync(
                        s => s.UserId == userId.Value && s.HostType == hostType && s.HostId == dto.HostId && s.UserSessionId == userSession.Id,
                        cancellationToken);
            }

            // Tracked-Add fallback. This is the ONLY non-idempotent create path, so we must never reach it on
            // a relational provider under concurrency: the upsert above guarantees the row exists and the
            // re-query returns it, so a relational miss here can only mean a genuine, unexpected SELECT
            // failure — in which case the outer RecordPlaybackIntervalsAsync retry (which catches the unique
            // violation, clears the change tracker and re-runs the whole flow) is the safety net. For
            // non-relational providers (in-memory/SQLite tests) there is no ON CONFLICT support and those
            // paths are single-threaded, so the race does not apply.
            if (session == null)
            {
                session = new PlaybackSession
                {
                    UserId = userId.Value,
                    HostType = hostType,
                    HostId = dto.HostId,
                    SessionId = dto.SessionId,
                    UserSessionId = userSession.Id,
                    StartedAt = now,
                    LastSeenAt = now,
                };
                db.PlaybackSessions.Add(session);
            }
        }

        ApplyPlaybackContext(session, dto, parentHostType, itemHostType);

        // Append new intervals (validate and clamp)
        var mediaDuration = dto.MediaDurationSec > 0 ? dto.MediaDurationSec : session.MediaDurationSec;
        var acceptedIntervalCount = 0;
        foreach (var incoming in dto.Intervals)
        {
            var start = Math.Max(0d, incoming.StartSec);
            var end = mediaDuration > 0 ? Math.Min(incoming.EndSec, mediaDuration) : incoming.EndSec;
            end = Math.Max(start, end);
            if (end <= start) continue;

            db.PlaybackIntervals.Add(new PlaybackInterval
            {
                Session = session,
                UserId = userId.Value,
                HostType = hostType,
                HostId = dto.HostId,
                StartSec = start,
                EndSec = end,
                RecordedAt = now,
                Surface = NormalizeOptionalText(dto.Surface, 64),
                ScopeKey = NormalizeOptionalText(dto.ScopeKey, 256),
                ParentHostType = parentHostType,
                ParentHostId = dto.ParentHostId,
                ItemHostType = itemHostType,
                ItemHostId = dto.ItemHostId,
                GroupItemId = dto.GroupItemId,
                SegmentId = dto.SegmentId,
                ClipStartSec = dto.ClipStartSec,
                ClipEndSec = dto.ClipEndSec,
                PlaybackRate = dto.PlaybackRate,
                Context = CloneJsonDocument(dto.Context),
            });
            acceptedIntervalCount++;
        }

        // Recompute TotalWatchedSec from the FULL merged interval set for this session
        // We need to flush the new intervals first so they appear in the in-memory list
        var allIntervals = session.Intervals
            .Concat(db.ChangeTracker.Entries<PlaybackInterval>()
                .Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Added && e.Entity.Session == session)
                .Select(e => e.Entity));
        var prevTotal = session.TotalWatchedSec;
        session.TotalWatchedSec = ComputeMergedWatchedSec(allIntervals);
        var addedWatchedSeconds = Math.Max(0d, session.TotalWatchedSec - prevTotal);
        var (dwellLen, dwellStart) = ComputeMaxDwell(allIntervals);

        // Update session state fields
        session.State = state;
        session.LastSeenAt = now;
        if (mediaDuration > 0) session.MediaDurationSec = mediaDuration;
        if (dto.CurrentPositionSec >= 0) session.LastPositionSec = dto.CurrentPositionSec;

        var isFinalState = state is PlaybackSessionState.Ended or PlaybackSessionState.Abandoned;
        var reachedMediaEnd = state is PlaybackSessionState.Ended
            && !dto.ClipStartSec.HasValue
            && !dto.ClipEndSec.HasValue
            && mediaDuration > 0d
            && dto.CurrentPositionSec >= Math.Max(0d, mediaDuration - 0.05d);
        var wasCompleted = session.IsCompleted;
        var wasCountsAsView = session.CountsAsView;
        var clipDuration = dto.ClipStartSec.HasValue && dto.ClipEndSec.HasValue && dto.ClipEndSec.Value > dto.ClipStartSec.Value
            ? dto.ClipEndSec.Value - dto.ClipStartSec.Value
            : (double?)null;
        var completedByCoverage = isFinalState
            && ((hostType == InteractionHostType.Video || hostType == InteractionHostType.Audio)
                && mediaDuration > 0
                && session.TotalWatchedSec >= mediaDuration * tracking.ViewCompletionRatio
                || hostType == InteractionHostType.Segment
                && clipDuration.HasValue
                && session.TotalWatchedSec >= clipDuration.Value * tracking.ViewCompletionRatio);
        if (completedByCoverage)
        {
            session.IsCompleted = true;
            session.EndedAt ??= now;
        }
        else if (isFinalState)
        {
            session.EndedAt ??= now;
        }

        var countsAsView = isFinalState && hostType switch
        {
            InteractionHostType.Image => session.TotalWatchedSec >= tracking.MinImageDetailViewSeconds,
            InteractionHostType.Text => session.TotalWatchedSec >= tracking.MinImageDetailViewSeconds,
            InteractionHostType.Video => session.TotalWatchedSec >= tracking.MinViewSeconds || completedByCoverage,
            InteractionHostType.Audio => session.TotalWatchedSec >= tracking.MinViewSeconds || completedByCoverage,
            InteractionHostType.Segment => session.TotalWatchedSec >= tracking.MinViewSeconds || completedByCoverage,
            _ => false,
        };

        if (countsAsView)
        {
            session.CountsAsView = true;
        }

        // Update affinity
        if (TryMapAffinityHostType(hostType, out var affinityHostType))
        {
            var affinity = await GetOrCreateAffinityAsync(affinityHostType, dto.HostId, cancellationToken);
            if (affinity != null)
            {
                if (addedWatchedSeconds > 0d)
                    affinity.TotalConsumedSec = Math.Max(0d, affinity.TotalConsumedSec + addedWatchedSeconds);

                // Track the user's deepest single dwell on this entity (longest contiguous watched run) and
                // where it occurred — accumulated as the max across all their sessions on it.
                if (dwellLen > affinity.MaxDwellSec)
                {
                    affinity.MaxDwellSec = dwellLen;
                    affinity.MaxDwellStartSec = dwellStart;
                }

                if ((hostType == InteractionHostType.Video || hostType == InteractionHostType.Audio) && dto.CurrentPositionSec >= 0)
                    affinity.LastPositionSec = reachedMediaEnd ? 0d : dto.CurrentPositionSec;
                else if (hostType == InteractionHostType.Segment && dto.CurrentPositionSec >= 0)
                    affinity.LastPositionSec = Math.Max(0d, dto.CurrentPositionSec - (dto.ClipStartSec ?? 0d));

                if (addedWatchedSeconds > 0d || countsAsView)
                    affinity.LastConsumedAt = now;

                if (!wasCompleted && session.IsCompleted)
                    affinity.CompleteCount++;

                if (!wasCountsAsView && session.CountsAsView)
                    affinity.ViewCount++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        TracePlaybackRecorded(
            hostType,
            dto.HostId,
            state,
            dto.Intervals.Count,
            acceptedIntervalCount,
            addedWatchedSeconds,
            session.TotalWatchedSec,
            session.Surface,
            session.CountsAsView,
            session.IsCompleted);
        return true;
    }

    private static InteractionHostType? ParseOptionalHostType(string? value)
        => InteractionValueMapper.TryParseHostType(value, out var hostType) && InteractionValueMapper.RequiresConcreteHost(hostType)
            ? hostType
            : null;

    private static string? NormalizeOptionalText(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static void ApplyPlaybackContext(
        PlaybackSession session,
        PlaybackIntervalsRequestDto dto,
        InteractionHostType? parentHostType,
        InteractionHostType? itemHostType)
    {
        session.Surface = NormalizeOptionalText(dto.Surface, 64);
        session.ScopeKey = NormalizeOptionalText(dto.ScopeKey, 256);
        session.ParentHostType = parentHostType;
        session.ParentHostId = dto.ParentHostId;
        session.ItemHostType = itemHostType;
        session.ItemHostId = dto.ItemHostId;
        session.GroupItemId = dto.GroupItemId;
        session.SegmentId = dto.SegmentId;
        session.ClipStartSec = dto.ClipStartSec;
        session.ClipEndSec = dto.ClipEndSec;
        session.Autoplay = dto.Autoplay;
        session.Muted = dto.Muted;
        session.Fullscreen = dto.Fullscreen;
        session.PlaybackRate = dto.PlaybackRate;
        session.Route = NormalizeOptionalText(dto.Route, 512);
        session.Referrer = NormalizeOptionalText(dto.Referrer, 512);
        session.RecommendationSource = NormalizeOptionalText(dto.RecommendationSource, 128);
        session.Context = CloneJsonDocument(dto.Context);
    }

    private static bool IsDuplicatePlaybackSessionInsert(DbUpdateException exception)
        => exception.InnerException is PostgresException postgresException
            && postgresException.SqlState == PostgresErrorCodes.UniqueViolation
            && string.Equals(postgresException.ConstraintName, "IX_PlaybackSessions_UserId_SessionId", StringComparison.Ordinal);

    private static double ComputeMergedWatchedSec(IEnumerable<PlaybackInterval> intervals)
    {
        var sorted = intervals.OrderBy(i => i.StartSec).ThenBy(i => i.EndSec).ToList();
        var total = 0d;
        var curStart = double.MinValue;
        var curEnd = double.MinValue;
        foreach (var iv in sorted)
        {
            if (iv.EndSec <= iv.StartSec) continue;
            if (iv.StartSec > curEnd)
            {
                total += Math.Max(0d, curEnd - curStart);
                curStart = iv.StartSec;
                curEnd = iv.EndSec;
            }
            else
            {
                curEnd = Math.Max(curEnd, iv.EndSec);
            }
        }
        total += Math.Max(0d, curEnd - curStart);
        return total;
    }

    /// <summary>The longest single contiguous watched run (merged) and where it starts — the user's deepest
    /// dwell. A long run is a strong "found a part worth staying on" signal; its start anchors future
    /// section-level attribution (the embeddings/tags/faces/audio present at that point in the media).</summary>
    private static (double lengthSec, double startSec) ComputeMaxDwell(IEnumerable<PlaybackInterval> intervals)
    {
        var sorted = intervals.OrderBy(i => i.StartSec).ThenBy(i => i.EndSec).ToList();
        double bestLen = 0d, bestStart = 0d;
        var curStart = double.MinValue;
        var curEnd = double.MinValue;
        void Close()
        {
            if (curEnd - curStart > bestLen) { bestLen = curEnd - curStart; bestStart = curStart; }
        }
        foreach (var iv in sorted)
        {
            if (iv.EndSec <= iv.StartSec) continue;
            if (iv.StartSec > curEnd)
            {
                Close();
                curStart = iv.StartSec;
                curEnd = iv.EndSec;
            }
            else
            {
                curEnd = Math.Max(curEnd, iv.EndSec);
            }
        }
        Close();
        return (Math.Max(0d, bestLen), bestLen > 0d ? bestStart : 0d);
    }

    private static bool TryParseSessionState(string? state, out PlaybackSessionState parsed)
    {
        parsed = PlaybackSessionState.Active;
        return (state?.Trim().ToLowerInvariant()) switch
        {
            "active" => Assign(PlaybackSessionState.Active, out parsed),
            "paused" => Assign(PlaybackSessionState.Paused, out parsed),
            "ended" => Assign(PlaybackSessionState.Ended, out parsed),
            "abandoned" => Assign(PlaybackSessionState.Abandoned, out parsed),
            _ => false,
        };

        static bool Assign(PlaybackSessionState val, out PlaybackSessionState p) { p = val; return true; }
    }

    public async Task<UserEngagementSnapshot?> ResetVideoActivityAsync(int videoId, CancellationToken cancellationToken = default)
        => await ResetActivityAsync(AffinityHostType.Video, videoId, cancellationToken);

    public async Task<UserEngagementSnapshot?> ResetActivityAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken = default)
    {
        if (!await EntityExistsAsync(hostType, hostId, cancellationToken))
            return null;

        var affinity = await GetOrCreateAffinityAsync(hostType, hostId, cancellationToken);
        if (affinity != null)
        {
            affinity.LastPositionSec = 0d;
            affinity.TotalConsumedSec = 0d;
            affinity.LastConsumedAt = null;

            var interactionHostType = ToInteractionHostType(hostType);
            var playbackSessions = await db.PlaybackSessions
                .Where(session => session.UserId == affinity.UserId && session.HostType == interactionHostType && session.HostId == hostId)
                .ToListAsync(cancellationToken);
            db.PlaybackSessions.RemoveRange(playbackSessions);
        }
        await db.SaveChangesAsync(cancellationToken);

        return (await GetSnapshotsAsync(hostType, [hostId], cancellationToken)).GetValueOrDefault(hostId) ?? EmptySnapshot;
    }

    public async Task<int> ResetAllActivityAsync(CancellationToken cancellationToken = default)
    {
        var userId = principalAccessor.Current?.UserId;
        if (!userId.HasValue)
            return 0;

        // Remove every playback session + interval + user-global session for this user.
        await db.Set<PlaybackInterval>().Where(i => i.UserId == userId.Value).ExecuteDeleteAsync(cancellationToken);
        await db.PlaybackSessions.Where(s => s.UserId == userId.Value).ExecuteDeleteAsync(cancellationToken);
        await db.UserSessions.Where(s => s.UserId == userId.Value).ExecuteDeleteAsync(cancellationToken);
        // continued below: clear watch-derived affinity metrics (ratings/likes/favorites kept)

        // Clear watch-derived metrics on every affinity row. Ratings/likes/favorites/interactions are kept.
        return await db.UserEntityAffinities
            .IgnoreQueryFilters()
            .Where(a => a.UserId == userId.Value)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(a => a.TotalConsumedSec, 0d)
                .SetProperty(a => a.LastPositionSec, (double?)null)
                .SetProperty(a => a.LastConsumedAt, (DateTime?)null)
                .SetProperty(a => a.ViewCount, 0)
                .SetProperty(a => a.CompleteCount, 0)
                .SetProperty(a => a.MaxDwellSec, 0d)
                .SetProperty(a => a.MaxDwellStartSec, 0d),
                cancellationToken);
    }

    public async Task<int> WipeAllEngagementAsync(CancellationToken cancellationToken = default)
    {
        var userId = principalAccessor.Current?.UserId;
        if (!userId.HasValue)
            return 0;

        // Wipe ONLY system-collected (implicit) engagement: playback sessions/intervals, user-global
        // sessions, behavioral interactions, derived likes, watch time, view/complete counts, dwell, and
        // page visits. Explicit signals the user set deliberately — ratings, likes ("orgasm count"),
        // favorites, and bookmarks/save-for-later — are PRESERVED. Used to clear data poisoned by a bug.
        await db.Set<PlaybackInterval>().Where(i => i.UserId == userId.Value).ExecuteDeleteAsync(cancellationToken);
        await db.PlaybackSessions.Where(s => s.UserId == userId.Value).ExecuteDeleteAsync(cancellationToken);
        await db.UserSessions.Where(s => s.UserId == userId.Value).ExecuteDeleteAsync(cancellationToken);

        // Delete every behavioral interaction except the explicit LikeCount ("orgasm count") events.
        await db.Interactions
            .Where(i => i.UserId == userId.Value && i.Kind != InteractionKind.LikeCount)
            .ExecuteDeleteAsync(cancellationToken);

        // Clear system-derived metrics on each affinity row; keep IsFavorite/FavoritedAt/IsBookmarked/LikeCount.
        return await db.UserEntityAffinities
            .IgnoreQueryFilters()
            .Where(a => a.UserId == userId.Value)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(a => a.ViewCount, 0)
                .SetProperty(a => a.CompleteCount, 0)
                .SetProperty(a => a.TotalConsumedSec, 0d)
                .SetProperty(a => a.LastPositionSec, (double?)null)
                .SetProperty(a => a.LastConsumedAt, (DateTime?)null)
                .SetProperty(a => a.MaxDwellSec, 0d)
                .SetProperty(a => a.MaxDwellStartSec, 0d)
                .SetProperty(a => a.DerivedLikeCount, 0)
                .SetProperty(a => a.PageVisitCount, 0)
                .SetProperty(a => a.InteractionCount, 0)
                .SetProperty(a => a.LastInteractedAt, (DateTime?)null)
                .SetProperty(a => a.OpenDetailCount, 0)
                .SetProperty(a => a.OpenLightboxCount, 0)
                .SetProperty(a => a.NavigateCount, 0)
                .SetProperty(a => a.PauseCount, 0)
                .SetProperty(a => a.SeekCount, 0)
                .SetProperty(a => a.PlayerControlCount, 0)
                .SetProperty(a => a.SearchInteractionCount, 0)
                .SetProperty(a => a.FilterInteractionCount, 0)
                .SetProperty(a => a.ZoomCount, 0),
                cancellationToken);
    }

    public async Task<UserEngagementSnapshot?> SetVideoRatingAsync(int videoId, int? value, string aspect = "overall", CancellationToken cancellationToken = default)
    {
        var video = await db.Videos.FirstOrDefaultAsync(item => item.Id == videoId, cancellationToken);
        if (video is null)
            return null;

        var normalizedAspect = NormalizeAspect(aspect);

        var userId = principalAccessor.Current?.UserId;
        EventType? ratingEvent = null;
        if (userId.HasValue)
        {
            var existing = await db.Ratings.FirstOrDefaultAsync(
                rating => rating.UserId == userId.Value && rating.HostType == RatingHostType.Video && rating.HostId == videoId && rating.Aspect == normalizedAspect,
                cancellationToken);

            if (!value.HasValue)
            {
                if (existing != null) { db.Ratings.Remove(existing); ratingEvent = EventType.RatingDeleted; }
            }
            else if (existing == null)
            {
                db.Ratings.Add(new Rating
                {
                    UserId = userId.Value,
                    HostType = RatingHostType.Video,
                    HostId = videoId,
                    Aspect = normalizedAspect,
                    Value = Math.Clamp(value.Value, 0, 100),
                });
                ratingEvent = EventType.RatingCreated;
            }
            else
            {
                existing.Value = Math.Clamp(value.Value, 0, 100);
                ratingEvent = EventType.RatingUpdated;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        if (ratingEvent is { } evt && userId is { } uid)
        {
            TraceRatingChanged(evt, AffinityHostType.Video, videoId, normalizedAspect, value.HasValue ? Math.Clamp(value.Value, 0, 100) : null);
            PublishRatingEvent(evt, AffinityHostType.Video, videoId, uid, normalizedAspect, value);
        }

        return await BuildVideoSnapshotAsync(videoId, video, null, cancellationToken);
    }

    public async Task<VideoHistoryDto?> GetVideoHistoryAsync(int videoId, CancellationToken cancellationToken = default)
        => await GetHistoryAsync(AffinityHostType.Video, videoId, cancellationToken);

    public async Task<VideoHistoryDto?> GetHistoryAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken = default)
    {
        if (!await EntityExistsAsync(hostType, hostId, cancellationToken))
            return null;

        var interactionHostType = ToInteractionHostType(hostType);

        var userId = principalAccessor.Current?.UserId;
        if (!userId.HasValue)
        {
            var playHistory = hostType == AffinityHostType.Video
                ? await db.Set<VideoPlayHistory>()
                    .Where(history => history.VideoId == hostId)
                    .OrderByDescending(history => history.PlayedAt)
                    .Select(history => history.PlayedAt.ToString("o"))
                    .ToListAsync(cancellationToken)
                : new List<string>();
            // Anonymous callers have no per-user like history; the legacy global (Stash-imported) like log
            // was removed, so only play history is available here.
            var events = playHistory
                .Select(date => new InteractionEventDto("playStart", date))
                .ToList();
            return new VideoHistoryDto(playHistory, [], events);
        }

        var interactions = await db.Interactions
            .Where(interaction => interaction.UserId == userId.Value && interaction.HostType == interactionHostType && interaction.HostId == hostId)
            .OrderByDescending(interaction => interaction.At)
            .ToListAsync(cancellationToken);
        var playbackSessions = await db.PlaybackSessions
            .Include(session => session.Intervals)
            .Where(session => session.UserId == userId.Value && session.HostType == interactionHostType && session.HostId == hostId)
            .OrderByDescending(session => session.StartedAt)
            .ToListAsync(cancellationToken);

        var playHistoryForUser = hostType == AffinityHostType.Video
            ? await db.Set<VideoPlayHistory>()
                .Where(history => history.VideoId == hostId)
                .OrderByDescending(history => history.PlayedAt)
                .Select(history => history.PlayedAt.ToString("o"))
                .ToListAsync(cancellationToken)
            : playbackSessions.Select(session => session.StartedAt.ToString("o")).ToList();
        var likeHistoryForUser = interactions
            .Where(interaction => interaction.Kind == InteractionKind.LikeCount)
            .Select(interaction => interaction.At.ToString("o"))
            .ToList();
        var eventsForUser = interactions
            .Select(ToInteractionEventDto)
            .ToList();
        var allIntervals = playbackSessions
            .SelectMany(session => session.Intervals)
            .OrderBy(iv => iv.StartSec)
            .ToList();
        var allTimeWatchedIntervals = MergeIntervalsForDisplay(allIntervals);
        var totalDistinctWatchedSec = ComputeMergedWatchedSec(allIntervals);
        var sessionsForUser = playbackSessions
            .Select(ToVideoPlaybackSessionDto)
            .ToList();
        return new VideoHistoryDto(playHistoryForUser, likeHistoryForUser, eventsForUser, allTimeWatchedIntervals, totalDistinctWatchedSec, sessionsForUser);
    }

    private Task<UserEntityAffinity?> GetOrCreateVideoAffinityAsync(int videoId, CancellationToken cancellationToken, bool createIfMissing = true)
        => GetOrCreateAffinityAsync(AffinityHostType.Video, videoId, cancellationToken, createIfMissing);

    private async Task<UserEntityAffinity?> GetOrCreateAffinityAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken, bool createIfMissing = true)
    {
        var userId = principalAccessor.Current?.UserId;
        if (!userId.HasValue)
            return null;

        var trackedAffinity = db.ChangeTracker.Entries<UserEntityAffinity>()
            .Where(entry => entry.State != EntityState.Deleted
                && entry.Entity.UserId == userId.Value
                && entry.Entity.HostType == hostType
                && entry.Entity.HostId == hostId)
            .Select(entry => entry.Entity)
            .FirstOrDefault();
        if (trackedAffinity != null)
            return trackedAffinity;

        var affinity = await db.UserEntityAffinities.FirstOrDefaultAsync(
            item => item.UserId == userId.Value && item.HostType == hostType && item.HostId == hostId,
            cancellationToken);

        if (affinity == null && createIfMissing)
        {
            if (db.Database.IsRelational())
            {
                var now = DateTime.UtcNow;
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO user_entity_affinities ("UserId", "HostType", "HostId", "IsFavorite", "CreatedAt", "UpdatedAt")
                    VALUES ({userId.Value}, {(int)hostType}, {hostId}, {false}, {now}, {now})
                    ON CONFLICT ("UserId", "HostType", "HostId") DO NOTHING
                    """, cancellationToken);

                affinity = await db.UserEntityAffinities.FirstOrDefaultAsync(
                    item => item.UserId == userId.Value && item.HostType == hostType && item.HostId == hostId,
                    cancellationToken);
            }
            else
            {
                affinity = new UserEntityAffinity
                {
                    UserId = userId.Value,
                    HostType = hostType,
                    HostId = hostId,
                };
                db.UserEntityAffinities.Add(affinity);
            }
        }

        return affinity;
    }

    private async Task<TrackingSettings> GetTrackingSettingsAsync(int userId, CancellationToken cancellationToken)
    {
        var rawPreferences = await db.Users
            .Where(user => user.Id == userId)
            .Select(user => user.UiPreferencesJson)
            .FirstOrDefaultAsync(cancellationToken);
        var preferences = UserService.ParseUiPreferences(rawPreferences)?.Tracking;
        if (preferences is null)
            return DefaultTrackingSettings;

        return new TrackingSettings(
            preferences.Enabled ?? DefaultTrackingSettings.Enabled,
            Math.Clamp(preferences.MinViewSeconds ?? DefaultTrackingSettings.MinViewSeconds, 0, 86_400),
            Math.Clamp(preferences.ViewCompletionRatio ?? DefaultTrackingSettings.ViewCompletionRatio, 0.01d, 1d),
            Math.Clamp(preferences.MinImageDetailViewSeconds ?? DefaultTrackingSettings.MinImageDetailViewSeconds, 0, 86_400),
            Math.Clamp(preferences.MinDerivedLikeSessionSeconds ?? DefaultTrackingSettings.MinDerivedLikeSessionSeconds, 0, 86_400),
            Math.Clamp(preferences.SessionIdleTimeoutSec ?? DefaultTrackingSettings.SessionIdleTimeoutSec, 10, 86_400),
            Math.Clamp(preferences.DwellPositiveSec ?? DefaultTrackingSettings.DwellPositiveSec, 1, 86_400));
    }

    private static bool TryMapAffinityHostType(InteractionHostType hostType, out AffinityHostType affinityHostType)
    {
        affinityHostType = hostType switch
        {
            InteractionHostType.Video => AffinityHostType.Video,
            InteractionHostType.Image => AffinityHostType.Image,
            InteractionHostType.Audio => AffinityHostType.Audio,
            InteractionHostType.Text => AffinityHostType.Text,
            InteractionHostType.Segment => AffinityHostType.Segment,
            InteractionHostType.Performer => AffinityHostType.Performer,
            InteractionHostType.Face => AffinityHostType.Face,
            InteractionHostType.Tag => AffinityHostType.Tag,
            InteractionHostType.Studio => AffinityHostType.Studio,
            InteractionHostType.Gallery => AffinityHostType.Gallery,
            InteractionHostType.Group => AffinityHostType.Group,
            _ => default,
        };
        return affinityHostType != default;
    }

    private static void ApplyInteractionAggregate(UserEntityAffinity affinity, InteractionKind kind, DateTime at)
    {
        affinity.InteractionCount++;
        affinity.LastInteractedAt = at;

        switch (kind)
        {
            case InteractionKind.PageVisit:
                affinity.PageVisitCount++;
                break;
            case InteractionKind.OpenDetail:
                affinity.OpenDetailCount++;
                break;
            case InteractionKind.OpenLightbox:
                affinity.OpenLightboxCount++;
                break;
            case InteractionKind.Navigate:
                affinity.NavigateCount++;
                break;
            case InteractionKind.Pause:
                affinity.PauseCount++;
                affinity.PlayerControlCount++;
                break;
            case InteractionKind.Seek:
                affinity.SeekCount++;
                affinity.PlayerControlCount++;
                break;
            case InteractionKind.Fullscreen:
            case InteractionKind.SlideshowDelay:
                affinity.PlayerControlCount++;
                break;
            case InteractionKind.SearchQuery:
            case InteractionKind.SearchSelect:
                affinity.SearchInteractionCount++;
                break;
            case InteractionKind.FilterApply:
            case InteractionKind.FilterClear:
                affinity.FilterInteractionCount++;
                break;
            case InteractionKind.Zoom:
                affinity.ZoomCount++;
                break;
        }
    }

    private static InteractionHostType ToInteractionHostType(AffinityHostType hostType) => hostType switch
    {
        AffinityHostType.Video => InteractionHostType.Video,
        AffinityHostType.Image => InteractionHostType.Image,
        AffinityHostType.Audio => InteractionHostType.Audio,
        AffinityHostType.Text => InteractionHostType.Text,
        AffinityHostType.Segment => InteractionHostType.Segment,
        AffinityHostType.Performer => InteractionHostType.Performer,
        AffinityHostType.Face => InteractionHostType.Face,
        AffinityHostType.Tag => InteractionHostType.Tag,
        AffinityHostType.Studio => InteractionHostType.Studio,
        AffinityHostType.Gallery => InteractionHostType.Gallery,
        AffinityHostType.Group => InteractionHostType.Group,
        _ => throw new ArgumentOutOfRangeException(nameof(hostType), hostType, null),
    };
    private async Task<UserEngagementSnapshot> BuildVideoSnapshotAsync(int videoId, Video video, UserEntityAffinity? affinity, CancellationToken cancellationToken)
    {
        var userId = principalAccessor.Current?.UserId;
        Rating? rating = null;
        if (userId.HasValue)
        {
            rating = await db.Ratings.FirstOrDefaultAsync(
                item => item.UserId == userId.Value && item.HostType == RatingHostType.Video && item.HostId == videoId && item.Aspect == "overall",
                cancellationToken);
        }

        affinity ??= await GetOrCreateVideoAffinityAsync(videoId, cancellationToken, createIfMissing: false);
        return ToSnapshot(affinity, rating);
    }

    private async Task<bool> EntityExistsAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken)
        => hostType switch
        {
            AffinityHostType.Video => await db.Videos.AnyAsync(video => video.Id == hostId, cancellationToken),
            AffinityHostType.Image => await db.Images.AnyAsync(image => image.Id == hostId, cancellationToken),
            AffinityHostType.Audio => await db.Audios.AnyAsync(audio => audio.Id == hostId, cancellationToken),
            AffinityHostType.Text => await db.TextDocuments.AnyAsync(text => text.Id == hostId, cancellationToken),
            AffinityHostType.Segment => await db.VisibleSegments().AnyAsync(segment => segment.Id == hostId, cancellationToken),
            AffinityHostType.Performer => await db.Performers.AnyAsync(performer => performer.Id == hostId, cancellationToken),
            AffinityHostType.Face => await db.Faces.AnyAsync(face => face.Id == hostId, cancellationToken),
            AffinityHostType.Tag => await db.Tags.AnyAsync(tag => tag.Id == hostId, cancellationToken),
            AffinityHostType.Studio => await db.Studios.AnyAsync(studio => studio.Id == hostId, cancellationToken),
            AffinityHostType.Gallery => await db.Galleries.AnyAsync(gallery => gallery.Id == hostId, cancellationToken),
            AffinityHostType.Group => await db.Groups.AnyAsync(group => group.Id == hostId, cancellationToken),
            _ => false,
        };

    private async Task<int[]> GetVisibleEntityIdsAsync(AffinityHostType hostType, int[] hostIds, CancellationToken cancellationToken)
        => hostType switch
        {
            AffinityHostType.Video => await db.Videos.Where(video => hostIds.Contains(video.Id)).Select(video => video.Id).ToArrayAsync(cancellationToken),
            AffinityHostType.Image => await db.Images.Where(image => hostIds.Contains(image.Id)).Select(image => image.Id).ToArrayAsync(cancellationToken),
            AffinityHostType.Audio => await db.Audios.Where(audio => hostIds.Contains(audio.Id)).Select(audio => audio.Id).ToArrayAsync(cancellationToken),
            AffinityHostType.Text => await db.TextDocuments.Where(text => hostIds.Contains(text.Id)).Select(text => text.Id).ToArrayAsync(cancellationToken),
            AffinityHostType.Segment => await db.VisibleSegments().Where(segment => hostIds.Contains(segment.Id)).Select(segment => segment.Id).ToArrayAsync(cancellationToken),
            AffinityHostType.Performer => await db.Performers.Where(performer => hostIds.Contains(performer.Id)).Select(performer => performer.Id).ToArrayAsync(cancellationToken),
            AffinityHostType.Face => await db.Faces.Where(face => hostIds.Contains(face.Id)).Select(face => face.Id).ToArrayAsync(cancellationToken),
            AffinityHostType.Tag => await db.Tags.Where(tag => hostIds.Contains(tag.Id)).Select(tag => tag.Id).ToArrayAsync(cancellationToken),
            AffinityHostType.Studio => await db.Studios.Where(studio => hostIds.Contains(studio.Id)).Select(studio => studio.Id).ToArrayAsync(cancellationToken),
            AffinityHostType.Gallery => await db.Galleries.Where(gallery => hostIds.Contains(gallery.Id)).Select(gallery => gallery.Id).ToArrayAsync(cancellationToken),
            AffinityHostType.Group => await db.Groups.Where(group => hostIds.Contains(group.Id)).Select(group => group.Id).ToArrayAsync(cancellationToken),
            _ => [],
        };

    private async Task<bool> InteractionHostExistsAsync(InteractionHostType hostType, int hostId, CancellationToken cancellationToken)
        => hostType switch
        {
            InteractionHostType.Video => await db.Videos.AnyAsync(video => video.Id == hostId, cancellationToken),
            InteractionHostType.Image => await db.Images.AnyAsync(image => image.Id == hostId, cancellationToken),
            InteractionHostType.Audio => await db.Audios.AnyAsync(audio => audio.Id == hostId, cancellationToken),
            InteractionHostType.Text => await db.TextDocuments.AnyAsync(text => text.Id == hostId, cancellationToken),
            InteractionHostType.Performer => await db.Performers.AnyAsync(performer => performer.Id == hostId, cancellationToken),
            InteractionHostType.Tag => await db.Tags.AnyAsync(tag => tag.Id == hostId, cancellationToken),
            InteractionHostType.Face => await db.Faces.AnyAsync(face => face.Id == hostId, cancellationToken),
            InteractionHostType.Segment => await db.VisibleSegments().AnyAsync(segment => segment.Id == hostId, cancellationToken),
            InteractionHostType.Studio => await db.Studios.AnyAsync(studio => studio.Id == hostId, cancellationToken),
            InteractionHostType.Gallery => await db.Galleries.AnyAsync(gallery => gallery.Id == hostId, cancellationToken),
            InteractionHostType.Group => await db.Groups.AnyAsync(group => group.Id == hostId, cancellationToken),
            InteractionHostType.Search => true,
            InteractionHostType.Collection => true,
            _ => false,
        };

    private async Task MirrorLegacyFavoriteAsync(AffinityHostType hostType, int hostId, bool isFavorite, CancellationToken cancellationToken)
    {
        switch (hostType)
        {
            case AffinityHostType.Performer:
                var performer = await db.Performers.FirstOrDefaultAsync(item => item.Id == hostId, cancellationToken);
                if (performer != null) performer.Favorite = isFavorite;
                break;
            case AffinityHostType.Tag:
                var tag = await db.Tags.FirstOrDefaultAsync(item => item.Id == hostId, cancellationToken);
                if (tag != null) tag.Favorite = isFavorite;
                break;
            case AffinityHostType.Studio:
                var studio = await db.Studios.FirstOrDefaultAsync(item => item.Id == hostId, cancellationToken);
                if (studio != null) studio.Favorite = isFavorite;
                break;
        }
    }
    private static RatingHostType ToRatingHostType(AffinityHostType hostType) => hostType switch
    {
        AffinityHostType.Video => RatingHostType.Video,
        AffinityHostType.Image => RatingHostType.Image,
        AffinityHostType.Audio => RatingHostType.Audio,
        AffinityHostType.Text => RatingHostType.Text,
        AffinityHostType.Segment => RatingHostType.Segment,
        AffinityHostType.Performer => RatingHostType.Performer,
        AffinityHostType.Face => RatingHostType.Face,
        AffinityHostType.Tag => RatingHostType.Tag,
        AffinityHostType.Studio => RatingHostType.Studio,
        AffinityHostType.Gallery => RatingHostType.Gallery,
        AffinityHostType.Group => RatingHostType.Group,
        _ => throw new ArgumentOutOfRangeException(nameof(hostType), hostType, null),
    };

    private static string NormalizeAspect(string? aspect)
    {
        var normalized = string.IsNullOrWhiteSpace(aspect) ? "overall" : aspect.Trim();
        return IsOverallAspect(normalized) ? "overall" : normalized;
    }

    private static bool IsOverallAspect(string aspect)
        => string.Equals(aspect, "overall", StringComparison.OrdinalIgnoreCase);

    private static InteractionEventDto ToInteractionEventDto(Interaction interaction)
        => new(
            InteractionValueMapper.ToName(interaction.Kind),
            interaction.At.ToString("o"),
            interaction.Meta == null ? null : interaction.Meta.RootElement.Clone());

    private static EngagementInteractionDto ToEngagementInteractionDto(Interaction interaction)
        => new(
            interaction.Id,
            InteractionValueMapper.ToName(interaction.HostType),
            InteractionValueMapper.RequiresConcreteHost(interaction.HostType) ? interaction.HostId : null,
            InteractionValueMapper.ToName(interaction.Kind),
            interaction.At.ToString("o"),
            interaction.Meta == null ? null : interaction.Meta.RootElement.Clone());

    private static VideoPlaybackSessionDto ToVideoPlaybackSessionDto(PlaybackSession session)
        => new(
            session.SessionId,
            session.StartedAt.ToString("o"),
            session.LastSeenAt.ToString("o"),
            session.EndedAt?.ToString("o"),
            session.State.ToString().ToLowerInvariant(),
            session.MediaDurationSec,
            session.TotalWatchedSec,
            session.LastPositionSec,
            session.IsCompleted,
            MergeIntervalsForDisplay(session.Intervals));

    /// <summary>Coalesce raw stored intervals into contiguous runs FOR DISPLAY only. Storage stays
    /// append-only/raw (the recording contract: each keepalive checkpoint writes a fresh ~10s row), so a single
    /// uninterrupted watch lands as many exactly-touching rows. Merging touching/overlapping rows here makes
    /// the playback history show one section per real watch run instead of 10s confetti (and drops the
    /// duplicate rows a keepalive+close double-flush can leave). Purely presentational — TotalWatchedSec and
    /// MaxDwellSec already merge on read. A small join tolerance absorbs rounding between adjacent chunks
    /// without bridging a real seek gap.</summary>
    private static List<PlaybackIntervalDto> MergeIntervalsForDisplay(IEnumerable<PlaybackInterval> intervals)
    {
        const double joinGapSec = 0.5;
        var sorted = intervals.Where(iv => iv.EndSec > iv.StartSec)
            .OrderBy(iv => iv.StartSec).ThenBy(iv => iv.EndSec).ToList();
        var merged = new List<PlaybackIntervalDto>();
        double curStart = 0, curEnd = 0; DateTime curRecorded = default; var open = false;
        foreach (var iv in sorted)
        {
            if (open && iv.StartSec <= curEnd + joinGapSec)
            {
                curEnd = Math.Max(curEnd, iv.EndSec);
                if (iv.RecordedAt > curRecorded) curRecorded = iv.RecordedAt;
            }
            else
            {
                if (open) merged.Add(new PlaybackIntervalDto(curStart, curEnd, curRecorded.ToString("o")));
                curStart = iv.StartSec; curEnd = iv.EndSec; curRecorded = iv.RecordedAt; open = true;
            }
        }
        if (open) merged.Add(new PlaybackIntervalDto(curStart, curEnd, curRecorded.ToString("o")));
        return merged;
    }

    private static JsonDocument? CloneJsonDocument(JsonElement? element)
        => element.HasValue ? JsonDocument.Parse(element.Value.GetRawText()) : null;

    private static UserEngagementSnapshot ToSnapshot(UserEntityAffinity? affinity, Rating? rating) => new(
        affinity?.IsFavorite ?? false,
        rating?.Value,
        affinity?.LastPositionSec ?? 0d,
        affinity?.TotalConsumedSec ?? 0d,
        affinity?.ViewCount ?? 0,
        affinity?.LastConsumedAt,
        affinity?.LikeCount ?? 0,
        affinity?.DerivedLikeCount ?? 0,
        affinity?.PageVisitCount ?? 0,
        affinity?.CompleteCount ?? 0);
}
