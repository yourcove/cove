using System.Text.Json;
using System.Text.Json.Nodes;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Services;

/// <summary>
/// Shared transfer rules for Cove-owned polymorphic metadata. Performer and studio merges call this
/// service so engagement, security, custom fields, provenance, saved filters, and playback history
/// are not deleted with a source entity.
/// </summary>
internal sealed class EntityMergeMetadataService(CoveContext db)
{
    public async Task TransferAsync(
        string entityType,
        int targetId,
        IReadOnlyCollection<int> sourceIds,
        CancellationToken ct)
    {
        var descriptor = Describe(entityType);
        var allIds = sourceIds.Append(targetId).Distinct().Order().ToArray();
        var idMap = allIds.ToDictionary(id => id, _ => targetId);

        await TransferTagApplicationsAsync(descriptor, targetId, allIds, ct);
        await TransferCustomFieldValuesAsync(descriptor, targetId, allIds, idMap, ct);
        await TransferFieldProvenanceAsync(descriptor, targetId, allIds, ct);
        await TransferRatingsAsync(descriptor, targetId, allIds, ct);
        await TransferBookmarksAsync(descriptor, targetId, allIds, ct);
        await TransferAffinitiesAsync(descriptor, targetId, allIds, ct);
        await TransferRoleOverridesAsync(descriptor, targetId, allIds, ct);
        await TransferDirectReferencesAsync(descriptor, targetId, allIds, idMap, ct);
        await TransferHistoricalRowsAsync(descriptor, targetId, allIds, ct);
        await RewriteStoredJsonReferencesAsync(descriptor, idMap, ct);
    }

    private async Task TransferTagApplicationsAsync(
        Descriptor descriptor,
        int targetId,
        int[] allIds,
        CancellationToken ct)
    {
        var applications = await db.TagApplications
            .Where(application => application.HostType == descriptor.AffinityHostType
                    && allIds.Contains(application.HostId)
                || application.ContextType != null
                    && application.ContextType.ToLower() == descriptor.EntityKind
                    && application.ContextId != null
                    && allIds.Contains(application.ContextId.Value))
            .ToListAsync(ct);
        var wasAlreadyMapped = applications.ToDictionary(
            application => application.Id,
            application => (application.HostType != descriptor.AffinityHostType
                    || application.HostId == targetId)
                && (application.ContextType == null
                    || !application.ContextType.Equals(descriptor.EntityKind, StringComparison.OrdinalIgnoreCase)
                    || application.ContextId == targetId));
        foreach (var application in applications)
        {
            if (application.HostType == descriptor.AffinityHostType && allIds.Contains(application.HostId))
                application.HostId = targetId;
            if (application.ContextType != null
                && application.ContextType.Equals(descriptor.EntityKind, StringComparison.OrdinalIgnoreCase)
                && application.ContextId is int contextId
                && allIds.Contains(contextId))
                application.ContextId = targetId;
        }
        foreach (var group in applications.GroupBy(application => new
        {
            application.HostType,
            application.HostId,
            application.ContextType,
            application.ContextId,
            application.TagId,
            application.SourceKey,
            application.SourceRunId,
            application.ModelKey,
        }))
        {
            var keeper = group
                .OrderByDescending(application => wasAlreadyMapped[application.Id])
                .ThenBy(application => application.Id)
                .First();
            keeper.Confidence = group.Max(application => application.Confidence);
            keeper.TotalDurationSec = group.Max(application => application.TotalDurationSec);
            keeper.HostDurationSec = group.Max(application => application.HostDurationSec);
            db.TagApplications.RemoveRange(group.Where(application => application.Id != keeper.Id));
        }
    }

    private async Task TransferCustomFieldValuesAsync(
        Descriptor descriptor,
        int targetId,
        int[] allIds,
        IReadOnlyDictionary<int, int> idMap,
        CancellationToken ct)
    {
        var referenceDefinitionIds = await db.CustomFieldDefinitions
            .Where(definition => definition.Type.ToLower() == descriptor.CustomFieldType)
            .Select(definition => definition.Id)
            .ToArrayAsync(ct);
        var values = await db.CustomFieldValues
            .Where(value => value.EntityType.ToLower() == descriptor.CustomEntityType && allIds.Contains(value.EntityId)
                || referenceDefinitionIds.Contains(value.DefinitionId)
                    && value.IntegerValue != null
                    && allIds.Contains(value.IntegerValue.Value))
            .ToListAsync(ct);

        var removedIds = new HashSet<int>();
        foreach (var group in values
            .Where(value => value.EntityType.Equals(descriptor.CustomEntityType, StringComparison.OrdinalIgnoreCase)
                && allIds.Contains(value.EntityId))
            .GroupBy(value => new { value.DefinitionId, EntityType = descriptor.CustomEntityType, EntityId = targetId, value.Position }))
        {
            var keeper = group.OrderByDescending(value => value.EntityId == targetId).ThenBy(value => value.Id).First();
            keeper.EntityId = targetId;
            foreach (var duplicate in group.Where(value => value.Id != keeper.Id))
            {
                removedIds.Add(duplicate.Id);
                db.CustomFieldValues.Remove(duplicate);
            }
        }

        foreach (var value in values.Where(value => !removedIds.Contains(value.Id)
            && referenceDefinitionIds.Contains(value.DefinitionId)
            && value.IntegerValue is int referenceId
            && idMap.ContainsKey(referenceId)))
            value.IntegerValue = idMap[value.IntegerValue!.Value];

        var affectedReferenceHosts = values
            .Where(value => !removedIds.Contains(value.Id)
                && referenceDefinitionIds.Contains(value.DefinitionId))
            .Select(value => new { value.DefinitionId, value.EntityType, value.EntityId })
            .Distinct()
            .ToArray();
        foreach (var host in affectedReferenceHosts)
        {
            var hostValues = await db.CustomFieldValues
                .Where(value => value.DefinitionId == host.DefinitionId
                    && value.EntityType == host.EntityType
                    && value.EntityId == host.EntityId)
                .OrderBy(value => value.Position)
                .ThenBy(value => value.Id)
                .ToListAsync(ct);
            var survivors = hostValues
                .Where(value => db.Entry(value).State != EntityState.Deleted)
                .ToList();
            foreach (var duplicateGroup in survivors
                .Where(value => value.IntegerValue != null)
                .GroupBy(value => value.IntegerValue!.Value)
                .Where(group => group.Count() > 1))
            {
                foreach (var duplicate in duplicateGroup.Skip(1))
                {
                    db.CustomFieldValues.Remove(duplicate);
                    survivors.Remove(duplicate);
                }
            }

            for (var position = 0; position < survivors.Count; position++)
                survivors[position].Position = position;
        }
    }

    private async Task TransferFieldProvenanceAsync(
        Descriptor descriptor,
        int targetId,
        int[] allIds,
        CancellationToken ct)
    {
        var rows = await db.FieldProvenance
            .Where(row => row.HostType == descriptor.AffinityHostType && allIds.Contains(row.HostId))
            .ToListAsync(ct);
        foreach (var group in rows.GroupBy(row => new
        {
            row.HostType,
            HostId = targetId,
            row.FieldKey,
            row.SourceKey,
            row.SourceRunId,
            row.ModelKey,
        }))
        {
            var keeper = group.OrderByDescending(row => row.HostId == targetId).ThenBy(row => row.Id).First();
            if (TryMergeSetValuedProvenance(group, targetId, out var mergedValueJson, out var mergedConfidence))
            {
                keeper.ValueJson = mergedValueJson;
                keeper.Confidence = mergedConfidence;
            }
            else if (!HasMeaningfulProvenanceValue(keeper.ValueJson))
            {
                var valueSource = group
                    .Where(row => HasMeaningfulProvenanceValue(row.ValueJson))
                    .OrderByDescending(row => row.HostId == targetId)
                    .ThenBy(row => row.HostId)
                    .ThenBy(row => row.Id)
                    .FirstOrDefault();
                if (valueSource != null)
                {
                    keeper.ValueJson = valueSource.ValueJson;
                    keeper.Confidence = valueSource.Confidence;
                }
            }
            keeper.HostId = targetId;
            db.FieldProvenance.RemoveRange(group.Where(row => row.Id != keeper.Id));
        }
    }

    private static bool TryMergeSetValuedProvenance(
        IEnumerable<FieldProvenance> rows,
        int targetId,
        out string? valueJson,
        out float? confidence)
    {
        var ordered = rows
            .OrderByDescending(row => row.HostId == targetId)
            .ThenBy(row => row.HostId)
            .ThenBy(row => row.Id)
            .ToArray();
        var fieldKey = ordered[0].FieldKey;
        if (!fieldKey.Equals("aliases", StringComparison.OrdinalIgnoreCase)
            && !fieldKey.Equals("remote_ids", StringComparison.OrdinalIgnoreCase))
        {
            valueJson = null;
            confidence = null;
            return false;
        }

        var merged = new JsonArray();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var contributorConfidences = new List<float?>();
        var foundArray = false;
        foreach (var row in ordered)
        {
            if (string.IsNullOrWhiteSpace(row.ValueJson))
                continue;
            try
            {
                using var document = JsonDocument.Parse(row.ValueJson);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    continue;
                foundArray = true;
                var contributed = false;
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    var node = JsonNode.Parse(element.GetRawText());
                    if (node == null || !identities.Add(node.ToJsonString()))
                        continue;
                    merged.Add(node);
                    contributed = true;
                }
                if (contributed)
                    contributorConfidences.Add(row.Confidence);
            }
            catch (JsonException)
            {
                // Preserve malformed legacy provider values through the scalar path.
            }
        }

        if (!foundArray)
        {
            valueJson = null;
            confidence = null;
            return false;
        }

        valueJson = merged.ToJsonString();
        confidence = contributorConfidences.Distinct().Count() == 1
            ? contributorConfidences[0]
            : null;
        return true;
    }

    private static bool HasMeaningfulProvenanceValue(string? valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
            return false;
        try
        {
            using var document = JsonDocument.Parse(valueJson);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => false,
                JsonValueKind.String => !string.IsNullOrWhiteSpace(document.RootElement.GetString()),
                JsonValueKind.Array => document.RootElement.GetArrayLength() > 0,
                JsonValueKind.Object => document.RootElement.EnumerateObject().Any(),
                _ => true,
            };
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private async Task TransferRatingsAsync(Descriptor descriptor, int targetId, int[] allIds, CancellationToken ct)
    {
        var ratings = await db.Ratings
            .Where(rating => rating.HostType == descriptor.RatingHostType && allIds.Contains(rating.HostId))
            .ToListAsync(ct);
        foreach (var group in ratings.GroupBy(rating => new { rating.UserId, rating.HostType, HostId = targetId, rating.Aspect }))
        {
            var keeper = group.OrderByDescending(rating => rating.HostId == targetId).ThenBy(rating => rating.Id).First();
            keeper.HostId = targetId;
            db.Ratings.RemoveRange(group.Where(rating => rating.Id != keeper.Id));
        }
    }

    private async Task TransferBookmarksAsync(Descriptor descriptor, int targetId, int[] allIds, CancellationToken ct)
    {
        var bookmarks = await db.UserBookmarks
            .Where(bookmark => bookmark.HostType == descriptor.AffinityHostType && allIds.Contains(bookmark.HostId))
            .ToListAsync(ct);
        foreach (var group in bookmarks.GroupBy(bookmark => bookmark.UserId))
        {
            var targetBookmark = group.FirstOrDefault(bookmark => bookmark.HostId == targetId);
            var createdAt = group.Min(bookmark => bookmark.CreatedAt);
            db.UserBookmarks.RemoveRange(group.Where(bookmark => bookmark.HostId != targetId));
            if (targetBookmark != null)
                targetBookmark.CreatedAt = createdAt;
            else
                db.UserBookmarks.Add(new UserBookmark
                {
                    UserId = group.Key,
                    HostType = descriptor.AffinityHostType,
                    HostId = targetId,
                    CreatedAt = createdAt,
                });
        }
    }

    private async Task TransferAffinitiesAsync(Descriptor descriptor, int targetId, int[] allIds, CancellationToken ct)
    {
        var affinities = await db.UserEntityAffinities
            .Where(affinity => affinity.HostType == descriptor.AffinityHostType && allIds.Contains(affinity.HostId))
            .ToListAsync(ct);
        foreach (var group in affinities.GroupBy(affinity => affinity.UserId))
        {
            var ordered = group.OrderByDescending(affinity => affinity.HostId == targetId).ThenBy(affinity => affinity.Id).ToArray();
            var keeper = ordered[0];
            var mostRecent = ordered.OrderByDescending(affinity => affinity.LastConsumedAt).ThenBy(affinity => affinity.Id).First();
            var deepest = ordered.OrderByDescending(affinity => affinity.MaxDwellSec).ThenBy(affinity => affinity.Id).First();
            keeper.HostId = targetId;
            keeper.IsFavorite = ordered.Any(affinity => affinity.IsFavorite);
            keeper.FavoritedAt = ordered.Min(affinity => affinity.FavoritedAt);
            keeper.IsBookmarked = ordered.Any(affinity => affinity.IsBookmarked);
            keeper.ViewCount = SumClamped(ordered.Select(affinity => affinity.ViewCount));
            keeper.CompleteCount = SumClamped(ordered.Select(affinity => affinity.CompleteCount));
            keeper.TotalConsumedSec = ordered.Sum(affinity => affinity.TotalConsumedSec);
            keeper.LastPositionSec = mostRecent.LastPositionSec;
            keeper.LastConsumedAt = ordered.Max(affinity => affinity.LastConsumedAt);
            keeper.MaxDwellSec = deepest.MaxDwellSec;
            keeper.MaxDwellStartSec = deepest.MaxDwellStartSec;
            keeper.LikeCount = SumClamped(ordered.Select(affinity => affinity.LikeCount));
            keeper.DerivedLikeCount = SumClamped(ordered.Select(affinity => affinity.DerivedLikeCount));
            keeper.PageVisitCount = SumClamped(ordered.Select(affinity => affinity.PageVisitCount));
            keeper.InteractionCount = SumClamped(ordered.Select(affinity => affinity.InteractionCount));
            keeper.LastInteractedAt = ordered.Max(affinity => affinity.LastInteractedAt);
            keeper.OpenDetailCount = SumClamped(ordered.Select(affinity => affinity.OpenDetailCount));
            keeper.OpenLightboxCount = SumClamped(ordered.Select(affinity => affinity.OpenLightboxCount));
            keeper.NavigateCount = SumClamped(ordered.Select(affinity => affinity.NavigateCount));
            keeper.PauseCount = SumClamped(ordered.Select(affinity => affinity.PauseCount));
            keeper.SeekCount = SumClamped(ordered.Select(affinity => affinity.SeekCount));
            keeper.PlayerControlCount = SumClamped(ordered.Select(affinity => affinity.PlayerControlCount));
            keeper.SearchInteractionCount = SumClamped(ordered.Select(affinity => affinity.SearchInteractionCount));
            keeper.FilterInteractionCount = SumClamped(ordered.Select(affinity => affinity.FilterInteractionCount));
            keeper.ZoomCount = SumClamped(ordered.Select(affinity => affinity.ZoomCount));
            db.UserEntityAffinities.RemoveRange(ordered.Skip(1));
        }
    }

    private async Task TransferRoleOverridesAsync(Descriptor descriptor, int targetId, int[] allIds, CancellationToken ct)
    {
        var idStrings = allIds.Select(id => id.ToString()).ToArray();
        var overrides = await db.RoleEntityOverrides
            .Where(row => row.EntityKind.ToLower() == descriptor.EntityKind && idStrings.Contains(row.EntityId))
            .ToListAsync(ct);
        foreach (var group in overrides.GroupBy(row => new { row.RoleId, EntityKind = descriptor.EntityKind, row.AppliesTo }))
        {
            var ordered = group.OrderByDescending(row => row.EntityId == targetId.ToString()).ThenBy(row => row.Id).ToArray();
            var keeper = ordered[0];
            keeper.EntityId = targetId.ToString();
            keeper.Effect = ordered.Any(row => row.Effect.Equals("deny", StringComparison.OrdinalIgnoreCase)) ? "deny" : "allow";
            db.RoleEntityOverrides.RemoveRange(ordered.Skip(1));
        }
    }

    private async Task TransferDirectReferencesAsync(
        Descriptor descriptor,
        int targetId,
        int[] allIds,
        IReadOnlyDictionary<int, int> idMap,
        CancellationToken ct)
    {
        var interactions = await db.Interactions
            .Where(row => row.HostType == descriptor.InteractionHostType && allIds.Contains(row.HostId))
            .ToListAsync(ct);
        foreach (var row in interactions)
            row.HostId = targetId;

        await TransferPlaybackAsync(descriptor, targetId, allIds, idMap, ct);

        var groupItemKind = descriptor.EntityType == NameConflictEntityTypes.Performer
            ? GroupItemKind.Performer
            : GroupItemKind.Studio;
        var groupItems = await db.GroupItems
            .Where(item => (item.HostType.ToLower() == descriptor.EntityKind || item.Kind == groupItemKind)
                && allIds.Contains(item.HostId))
            .ToListAsync(ct);
        foreach (var item in groupItems)
        {
            item.HostId = targetId;
            item.HostType = descriptor.EntityKind;
            item.Kind = groupItemKind;
        }

        var userSessions = await db.UserSessions
            .Where(session => session.LastHostType == descriptor.InteractionHostType
                && session.LastHostId != null
                && allIds.Contains(session.LastHostId.Value))
            .ToListAsync(ct);
        foreach (var session in userSessions)
            session.LastHostId = targetId;

        var referenceIds = allIds.Select(id => (long)id).ToArray();
        var segments = await db.Segments
            .Where(segment => segment.Kind != null
                && segment.Kind.ToLower() == descriptor.EntityKind
                && segment.RefId != null
                && referenceIds.Contains(segment.RefId.Value))
            .ToListAsync(ct);
        foreach (var segment in segments)
            segment.RefId = targetId;

        var detections = await db.Detections
            .Where(detection => detection.RefKind != null
                && detection.RefKind.ToLower() == descriptor.EntityKind
                && detection.RefId != null
                && referenceIds.Contains(detection.RefId.Value))
            .ToListAsync(ct);
        foreach (var detection in detections)
            detection.RefId = targetId;
    }

    private async Task TransferPlaybackAsync(
        Descriptor descriptor,
        int targetId,
        int[] allIds,
        IReadOnlyDictionary<int, int> idMap,
        CancellationToken ct)
    {
        var sessions = await db.PlaybackSessions
            .Where(session => session.HostType == descriptor.InteractionHostType && allIds.Contains(session.HostId)
                || session.ParentHostType == descriptor.InteractionHostType && session.ParentHostId != null && allIds.Contains(session.ParentHostId.Value)
                || session.ItemHostType == descriptor.InteractionHostType && session.ItemHostId != null && allIds.Contains(session.ItemHostId.Value))
            .ToListAsync(ct);
        var hostSessions = sessions
            .Where(session => session.HostType == descriptor.InteractionHostType && allIds.Contains(session.HostId))
            .ToArray();
        var originalWatchedBySessionId = hostSessions.ToDictionary(session => session.Id, session => session.TotalWatchedSec);
        var duplicateSessionIds = new Dictionary<int, int>();
        var combinedSessionMemberIds = new Dictionary<int, int[]>();
        foreach (var group in hostSessions
            .Where(session => session.UserSessionId != null)
            .GroupBy(session => new { session.UserId, session.UserSessionId }))
        {
            var ordered = group.OrderByDescending(session => session.HostId == targetId).ThenBy(session => session.Id).ToArray();
            var keeper = ordered[0];
            var latest = ordered.OrderByDescending(session => session.LastSeenAt).ThenBy(session => session.Id).First();
            keeper.HostId = targetId;
            keeper.StartedAt = ordered.Min(session => session.StartedAt);
            keeper.LastSeenAt = ordered.Max(session => session.LastSeenAt);
            keeper.EndedAt = ordered.Max(session => session.EndedAt);
            keeper.State = ordered.Max(session => session.State);
            keeper.MediaDurationSec = ordered.Max(session => session.MediaDurationSec);
            keeper.LastPositionSec = latest.LastPositionSec;
            keeper.TotalWatchedSec = ordered.Sum(session => session.TotalWatchedSec);
            keeper.IsCompleted = ordered.Any(session => session.IsCompleted);
            keeper.CountsAsView = ordered.Any(session => session.CountsAsView);
            keeper.DerivedLikeAwarded = ordered.Any(session => session.DerivedLikeAwarded);
            keeper.Surface = latest.Surface;
            keeper.ScopeKey = latest.ScopeKey;
            keeper.ParentHostType = latest.ParentHostType;
            keeper.ParentHostId = latest.ParentHostId;
            keeper.ItemHostType = latest.ItemHostType;
            keeper.ItemHostId = latest.ItemHostId;
            keeper.GroupItemId = latest.GroupItemId;
            keeper.SegmentId = latest.SegmentId;
            keeper.ClipStartSec = latest.ClipStartSec;
            keeper.ClipEndSec = latest.ClipEndSec;
            keeper.Autoplay = latest.Autoplay;
            keeper.Muted = latest.Muted;
            keeper.Fullscreen = latest.Fullscreen;
            keeper.PlaybackRate = latest.PlaybackRate;
            keeper.Route = latest.Route;
            keeper.Referrer = latest.Referrer;
            keeper.RecommendationSource = latest.RecommendationSource;
            keeper.Context = latest.Context == null
                ? null
                : JsonDocument.Parse(latest.Context.RootElement.GetRawText());
            if (ordered.Length > 1)
                combinedSessionMemberIds[keeper.Id] = ordered.Select(session => session.Id).ToArray();
            foreach (var duplicate in ordered.Skip(1))
                duplicateSessionIds[duplicate.Id] = keeper.Id;
        }

        foreach (var session in hostSessions.Where(session => session.UserSessionId == null))
            session.HostId = targetId;
        foreach (var session in sessions)
        {
            if (session.ParentHostType == descriptor.InteractionHostType && session.ParentHostId is int parentId)
                session.ParentHostId = idMap.GetValueOrDefault(parentId, parentId);
            if (session.ItemHostType == descriptor.InteractionHostType && session.ItemHostId is int itemId)
                session.ItemHostId = idMap.GetValueOrDefault(itemId, itemId);
        }

        var sessionIds = sessions.Select(session => session.Id).ToArray();
        var intervals = await db.PlaybackIntervals
            .Where(interval => sessionIds.Contains(interval.PlaybackSessionId)
                || interval.HostType == descriptor.InteractionHostType && allIds.Contains(interval.HostId)
                || interval.ParentHostType == descriptor.InteractionHostType && interval.ParentHostId != null && allIds.Contains(interval.ParentHostId.Value)
                || interval.ItemHostType == descriptor.InteractionHostType && interval.ItemHostId != null && allIds.Contains(interval.ItemHostId.Value))
            .ToListAsync(ct);
        var intervalCoverageByOriginalSessionId = intervals
            .Where(interval => originalWatchedBySessionId.ContainsKey(interval.PlaybackSessionId))
            .GroupBy(interval => interval.PlaybackSessionId)
            .ToDictionary(group => group.Key, group => PlaybackIntervalMath.ComputeMergedWatchedSec(group));
        foreach (var interval in intervals)
        {
            if (duplicateSessionIds.TryGetValue(interval.PlaybackSessionId, out var keeperId))
                interval.PlaybackSessionId = keeperId;
            if (interval.HostType == descriptor.InteractionHostType && allIds.Contains(interval.HostId))
                interval.HostId = targetId;
            if (interval.ParentHostType == descriptor.InteractionHostType && interval.ParentHostId is int parentId)
                interval.ParentHostId = idMap.GetValueOrDefault(parentId, parentId);
            if (interval.ItemHostType == descriptor.InteractionHostType && interval.ItemHostId is int itemId)
                interval.ItemHostId = idMap.GetValueOrDefault(itemId, itemId);
        }
        foreach (var (keeperId, memberIds) in combinedSessionMemberIds)
        {
            var mergedIntervals = intervals.Where(interval => interval.PlaybackSessionId == keeperId).ToArray();
            var unrepresentedWatchedSec = memberIds.Sum(sessionId => Math.Max(
                0d,
                originalWatchedBySessionId[sessionId]
                    - intervalCoverageByOriginalSessionId.GetValueOrDefault(sessionId)));
            sessions.Single(session => session.Id == keeperId).TotalWatchedSec =
                PlaybackIntervalMath.ComputeMergedWatchedSec(mergedIntervals) + unrepresentedWatchedSec;
        }
        db.PlaybackSessions.RemoveRange(sessions.Where(session => duplicateSessionIds.ContainsKey(session.Id)));
    }

    private async Task TransferHistoricalRowsAsync(Descriptor descriptor, int targetId, int[] allIds, CancellationToken ct)
    {
        var scrapeAttempts = await db.ScrapeAttempts
            .Where(attempt => attempt.EntityType.ToLower() == descriptor.EntityKind
                && attempt.EntityId != null
                && allIds.Contains(attempt.EntityId.Value))
            .ToListAsync(ct);
        foreach (var attempt in scrapeAttempts)
            attempt.EntityId = targetId;

        if (descriptor.EntityType != NameConflictEntityTypes.Performer)
            return;

        var embeddings = await db.Embeddings
            .Where(embedding => embedding.HostType == EmbeddingHostType.Performer && allIds.Contains(embedding.HostId))
            .ToListAsync(ct);
        foreach (var embedding in embeddings)
            embedding.HostId = targetId;

        var runs = await db.AiRuns
            .Where(run => run.TargetType == AiRunTargetType.Performer && allIds.Contains(run.TargetId))
            .ToListAsync(ct);
        foreach (var run in runs)
            run.TargetId = targetId;
    }

    private async Task RewriteStoredJsonReferencesAsync(
        Descriptor descriptor,
        IReadOnlyDictionary<int, int> idMap,
        CancellationToken ct)
    {
        var fieldProvenance = await db.FieldProvenance.Where(row => row.ValueJson != null).ToListAsync(ct);
        foreach (var row in fieldProvenance)
            row.ValueJson = EntityReferenceJsonRewriter.RewriteFieldProvenanceValue(
                descriptor.EntityType,
                row.FieldKey,
                row.ValueJson,
                idMap);

        var interactions = await db.Interactions.Where(interaction => interaction.Meta != null).ToListAsync(ct);
        foreach (var interaction in interactions)
            interaction.Meta = EntityReferenceJsonRewriter.Rewrite(descriptor.EntityType, interaction.Meta, idMap);

        var playbackSessions = await db.PlaybackSessions.Where(session => session.Context != null).ToListAsync(ct);
        foreach (var session in playbackSessions)
            session.Context = EntityReferenceJsonRewriter.Rewrite(descriptor.EntityType, session.Context, idMap);

        var playbackIntervals = await db.PlaybackIntervals.Where(interval => interval.Context != null).ToListAsync(ct);
        foreach (var interval in playbackIntervals)
            interval.Context = EntityReferenceJsonRewriter.Rewrite(descriptor.EntityType, interval.Context, idMap);

        var payloadSegments = await db.Segments.Where(segment => segment.Payload != null).ToListAsync(ct);
        foreach (var segment in payloadSegments)
            segment.Payload = EntityReferenceJsonRewriter.Rewrite(descriptor.EntityType, segment.Payload, idMap);

        var savedFilters = await db.SavedFilters.Where(filter => filter.ObjectFilter != null).ToListAsync(ct);
        foreach (var filter in savedFilters)
            filter.ObjectFilter = EntityReferenceJsonRewriter.Rewrite(
                descriptor.EntityType,
                filter.ObjectFilter,
                idMap,
                filter.Mode.Equals(descriptor.EntityKind, StringComparison.OrdinalIgnoreCase)
                    || filter.Mode.Equals($"{descriptor.EntityKind}s", StringComparison.OrdinalIgnoreCase));

        var users = await db.Users.Where(user => user.UiPreferencesJson != null).ToListAsync(ct);
        foreach (var user in users)
            user.UiPreferencesJson = EntityReferenceJsonRewriter.RewriteUserUiPreferences(
                descriptor.EntityType,
                user.UiPreferencesJson,
                idMap);

        var groups = await db.Groups.Where(group => group.QueryJson != null).ToListAsync(ct);
        foreach (var group in groups)
            group.QueryJson = EntityReferenceJsonRewriter.Rewrite(descriptor.EntityType, group.QueryJson, idMap);

        var groupItems = await db.GroupItems.Where(item => item.SourceQueryJson != null).ToListAsync(ct);
        foreach (var item in groupItems)
            item.SourceQueryJson = EntityReferenceJsonRewriter.Rewrite(descriptor.EntityType, item.SourceQueryJson, idMap);

        var contentRules = await db.RoleContentRules.Where(rule => rule.ScopeValue != null).ToListAsync(ct);
        foreach (var rule in contentRules)
            rule.ScopeValue = EntityReferenceJsonRewriter.RewriteRoleContentScope(
                descriptor.EntityType,
                rule.EntityKind,
                rule.ScopeKind,
                rule.ScopeValue,
                idMap) ?? rule.ScopeValue;

        var shareLinks = await db.ShareLinks.Where(link => link.EntityKind.ToLower() == descriptor.EntityKind).ToListAsync(ct);
        foreach (var link in shareLinks)
            link.EntityIds = RewriteShareLinkIds(descriptor.EntityType, link.EntityIds, idMap);

        // Scrape attempt payloads and AI/model metadata are provider- or extension-defined immutable
        // history. Their numeric fields are opaque; only the typed EntityId/HostId/TargetId columns
        // transferred above are known to contain Cove-local identifiers.
    }

    private static string RewriteShareLinkIds(
        string entityType,
        string json,
        IReadOnlyDictionary<int, int> idMap)
    {
        var property = entityType == NameConflictEntityTypes.Performer ? "performerIds" : "studioIds";
        var wrapped = EntityReferenceJsonRewriter.Rewrite(entityType, $"{{\"{property}\":{json}}}", idMap);
        var prefixLength = property.Length + 4;
        return wrapped == null ? json : wrapped[prefixLength..^1];
    }

    private static int SumClamped(IEnumerable<int> values)
        => (int)Math.Clamp(values.Aggregate(0L, (sum, value) => sum + value), int.MinValue, int.MaxValue);

    private static Descriptor Describe(string entityType)
        => entityType switch
        {
            NameConflictEntityTypes.Performer => new(
                entityType,
                EntityKinds.Performer,
                AffinityHostType.Performer,
                InteractionHostType.Performer,
                RatingHostType.Performer,
                CustomFieldEntityTypes.Performer,
                CustomFieldTypes.Performer),
            NameConflictEntityTypes.Studio => new(
                entityType,
                EntityKinds.Studio,
                AffinityHostType.Studio,
                InteractionHostType.Studio,
                RatingHostType.Studio,
                CustomFieldEntityTypes.Studio,
                CustomFieldTypes.Studio),
            _ => throw new ArgumentException("The requested entity type does not have merge metadata rules.", nameof(entityType)),
        };

    private sealed record Descriptor(
        string EntityType,
        string EntityKind,
        AffinityHostType AffinityHostType,
        InteractionHostType InteractionHostType,
        RatingHostType RatingHostType,
        string CustomEntityType,
        string CustomFieldType);
}
