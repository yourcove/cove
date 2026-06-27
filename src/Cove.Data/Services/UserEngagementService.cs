using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data.Auth;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Text.Json;

namespace Cove.Data.Services;

public sealed class UserEngagementService(CoveContext db, ICurrentPrincipalAccessor principalAccessor) : IUserEngagementService
{
    private static readonly UserEngagementSnapshot EmptySnapshot = new(false, null, 0d, 0d, 0, null, 0, 0, 0, 0);
    private static readonly TrackingSettings DefaultTrackingSettings = new(true, 30, 0.9d, 5, 60, 120);

    private sealed record TrackingSettings(
        bool Enabled,
        int MinViewSeconds,
        double ViewCompletionRatio,
        int MinImageDetailViewSeconds,
        int MinDerivedLikeSessionSeconds,
        int SessionIdleTimeoutSec);

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
        var affinities = await db.UserEntityAffinities
            .Where(affinity => affinity.UserId == userId.Value && affinity.HostType == hostType && visibleIds.Contains(affinity.HostId))
            .ToDictionaryAsync(affinity => affinity.HostId, cancellationToken);

        var ratings = await db.Ratings
            .Where(rating => rating.UserId == userId.Value && rating.HostType == ratingHostType && rating.Aspect == "overall" && visibleIds.Contains(rating.HostId))
            .ToDictionaryAsync(rating => rating.HostId, cancellationToken);

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
        if (userId.HasValue)
        {
            var ratingHostType = ToRatingHostType(hostType);
            var existing = await db.Ratings.FirstOrDefaultAsync(
                rating => rating.UserId == userId.Value && rating.HostType == ratingHostType && rating.HostId == hostId && rating.Aspect == normalizedAspect,
                cancellationToken);

            if (!value.HasValue)
            {
                if (existing != null)
                    db.Ratings.Remove(existing);
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
            }
            else
            {
                existing.Value = Math.Clamp(value.Value, 0, 100);
            }
        }
        await db.SaveChangesAsync(cancellationToken);
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

    private async Task<UserEngagementSnapshot?> IncrementLikeAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken)
    {
        if (!await EntityExistsAsync(hostType, hostId, cancellationToken))
            return null;

        var now = DateTime.UtcNow;
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

        return (await GetSnapshotsAsync(hostType, [hostId], cancellationToken)).GetValueOrDefault(hostId) ?? EmptySnapshot;
    }

    private async Task<UserEngagementSnapshot?> DecrementLikeAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken)
    {
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

        return (await GetSnapshotsAsync(hostType, [hostId], cancellationToken)).GetValueOrDefault(hostId) ?? EmptySnapshot;
    }

    private async Task<UserEngagementSnapshot?> ResetLikeAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken)
    {
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

        return (await GetSnapshotsAsync(hostType, [hostId], cancellationToken)).GetValueOrDefault(hostId) ?? EmptySnapshot;
    }

    /// <summary>
    /// Record a batch of contiguous watched intervals for a playback session.
    /// Each call appends new PlaybackInterval rows, then recomputes per-session TotalWatchedSec
    /// from the full merged set — no guesswork from position deltas.
    /// Also updates UserEntityAffinity.TotalConsumedSec, LastPositionSec, and LastConsumedAt,
    /// and marks IsCompleted / CompleteCount when the session ends near the media tail.
    /// </summary>
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

        var session = await db.PlaybackSessions
            .Include(s => s.Intervals)
            .FirstOrDefaultAsync(
                s => s.UserId == userId.Value && s.SessionId == dto.SessionId,
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
                    INSERT INTO "PlaybackSessions"
                        ("UserId", "HostType", "HostId", "SessionId", "StartedAt", "LastSeenAt", "State",
                         "MediaDurationSec", "TotalWatchedSec", "IsCompleted", "CountsAsView", "DerivedLikeAwarded",
                         "CreatedAt", "UpdatedAt")
                    VALUES ({userId.Value}, {(int)hostType}, {dto.HostId}, {dto.SessionId}, {now}, {now}, {(int)PlaybackSessionState.Active},
                         {0d}, {0d}, {false}, {false}, {false}, {now}, {now})
                    ON CONFLICT ("UserId", "SessionId") DO UPDATE SET "LastSeenAt" = EXCLUDED."LastSeenAt"
                    """, cancellationToken);

                session = await db.PlaybackSessions
                    .Include(s => s.Intervals)
                    .FirstOrDefaultAsync(
                        s => s.UserId == userId.Value && s.SessionId == dto.SessionId,
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
                    StartedAt = now,
                    LastSeenAt = now,
                };
                db.PlaybackSessions.Add(session);
            }
        }

        ApplyPlaybackContext(session, dto, parentHostType, itemHostType);

        // Append new intervals (validate and clamp)
        var mediaDuration = dto.MediaDurationSec > 0 ? dto.MediaDurationSec : session.MediaDurationSec;
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
        }

        // Recompute TotalWatchedSec from the FULL merged interval set for this session
        // We need to flush the new intervals first so they appear in the in-memory list
        var allIntervals = session.Intervals
            .Concat(db.ChangeTracker.Entries<PlaybackInterval>()
                .Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Added && e.Entity.Session == session)
                .Select(e => e.Entity));
        var prevTotal = session.TotalWatchedSec;
        session.TotalWatchedSec = ComputeMergedWatchedSec(allIntervals);

        // Update session state fields
        session.State = state;
        session.LastSeenAt = now;
        if (mediaDuration > 0) session.MediaDurationSec = mediaDuration;
        if (dto.CurrentPositionSec >= 0) session.LastPositionSec = dto.CurrentPositionSec;

        var isFinalState = state is PlaybackSessionState.Ended or PlaybackSessionState.Abandoned;
        var wasCompleted = session.IsCompleted;
        var wasCountsAsView = session.CountsAsView;
        var clipDuration = dto.ClipStartSec.HasValue && dto.ClipEndSec.HasValue && dto.ClipEndSec.Value > dto.ClipStartSec.Value
            ? dto.ClipEndSec.Value - dto.ClipStartSec.Value
            : (double?)null;
        var completedByPosition = isFinalState
            && ((hostType == InteractionHostType.Video || hostType == InteractionHostType.Audio)
                && mediaDuration > 0
                && dto.CurrentPositionSec >= mediaDuration * tracking.ViewCompletionRatio
                || hostType == InteractionHostType.Segment
                && clipDuration.HasValue
                && session.TotalWatchedSec >= clipDuration.Value * tracking.ViewCompletionRatio);
        if (completedByPosition)
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
            InteractionHostType.Video => session.TotalWatchedSec >= tracking.MinViewSeconds || completedByPosition,
            InteractionHostType.Audio => session.TotalWatchedSec >= tracking.MinViewSeconds || completedByPosition,
            InteractionHostType.Segment => session.TotalWatchedSec >= tracking.MinViewSeconds || completedByPosition,
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
                var delta = session.TotalWatchedSec - prevTotal;
                if (delta > 0d)
                    affinity.TotalConsumedSec = Math.Max(0d, affinity.TotalConsumedSec + delta);

                if ((hostType == InteractionHostType.Video || hostType == InteractionHostType.Audio) && dto.CurrentPositionSec >= 0)
                    affinity.LastPositionSec = dto.CurrentPositionSec;
                else if (hostType == InteractionHostType.Segment && dto.CurrentPositionSec >= 0)
                    affinity.LastPositionSec = Math.Max(0d, dto.CurrentPositionSec - (dto.ClipStartSec ?? 0d));

                if (delta > 0d || countsAsView)
                    affinity.LastConsumedAt = now;

                if (!wasCompleted && session.IsCompleted)
                    affinity.CompleteCount++;

                if (!wasCountsAsView && session.CountsAsView)
                    affinity.ViewCount++;

                // Update video-level resume/duration cache
                if (hostType == InteractionHostType.Video)
                {
                    var video = await db.Videos.FirstOrDefaultAsync(sc => sc.Id == dto.HostId, cancellationToken);
                    if (video != null)
                    {
                    }
                }

                if (isFinalState
                    && !session.DerivedLikeAwarded
                    && session.TotalWatchedSec >= tracking.MinDerivedLikeSessionSeconds
                    && !await db.PlaybackSessions.AnyAsync(
                        other => other.UserId == userId.Value && other.Id != session.Id && other.StartedAt > session.StartedAt,
                        cancellationToken))
                {
                    affinity.DerivedLikeCount++;
                    session.DerivedLikeAwarded = true;
                    db.Interactions.Add(new Interaction
                    {
                        UserId = userId.Value,
                        HostType = hostType,
                        HostId = dto.HostId,
                        Kind = InteractionKind.DerivedLike,
                        At = now,
                    });
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
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

    public async Task<UserEngagementSnapshot?> SetVideoRatingAsync(int videoId, int? value, string aspect = "overall", CancellationToken cancellationToken = default)
    {
        var video = await db.Videos.FirstOrDefaultAsync(item => item.Id == videoId, cancellationToken);
        if (video is null)
            return null;

        var normalizedAspect = NormalizeAspect(aspect);

        var userId = principalAccessor.Current?.UserId;
        if (userId.HasValue)
        {
            var existing = await db.Ratings.FirstOrDefaultAsync(
                rating => rating.UserId == userId.Value && rating.HostType == RatingHostType.Video && rating.HostId == videoId && rating.Aspect == normalizedAspect,
                cancellationToken);

            if (!value.HasValue)
            {
                if (existing != null)
                    db.Ratings.Remove(existing);
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
            }
            else
            {
                existing.Value = Math.Clamp(value.Value, 0, 100);
            }
        }
        await db.SaveChangesAsync(cancellationToken);

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
            var likeHistory = hostType == AffinityHostType.Video
                ? await db.Set<VideoLikeHistory>()
                    .Where(history => history.VideoId == hostId)
                    .OrderByDescending(history => history.OccurredAt)
                    .Select(history => history.OccurredAt.ToString("o"))
                    .ToListAsync(cancellationToken)
                : new List<string>();
            var events = playHistory
                .Select(date => (At: date, Event: new InteractionEventDto("playStart", date)))
                .Concat(likeHistory.Select(date => (At: date, Event: new InteractionEventDto("likeCount", date))))
                .OrderByDescending(item => item.At, StringComparer.Ordinal)
                .Select(item => item.Event)
                .ToList();
            return new VideoHistoryDto(playHistory, likeHistory, events);
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
        var allTimeWatchedIntervals = allIntervals
            .Select(ToPlaybackIntervalDto)
            .ToList();
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
            Math.Clamp(preferences.SessionIdleTimeoutSec ?? DefaultTrackingSettings.SessionIdleTimeoutSec, 10, 86_400));
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
            case InteractionKind.Share:
                affinity.ShareCount++;
                break;
            case InteractionKind.Hide:
                affinity.HideCount++;
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
            session.Intervals
                .OrderBy(iv => iv.StartSec)
                .Select(ToPlaybackIntervalDto)
                .ToList());

    private static PlaybackIntervalDto ToPlaybackIntervalDto(PlaybackInterval iv)
        => new(iv.StartSec, iv.EndSec, iv.RecordedAt.ToString("o"));

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
