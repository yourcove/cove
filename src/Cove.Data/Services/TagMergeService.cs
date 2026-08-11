using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Services;

public sealed record TagMergeResult(int TargetId, IReadOnlyList<int> MergedSourceIds);

/// <summary>
/// Authoritative relationship-transfer implementation for tag merges. The ordinary merge endpoint,
/// the 1.2 compatibility cleanup tool, and the future enforcement migration are required to follow
/// this contract; do not add a tag reference to Cove without updating this service, its impact scan,
/// and the conflict-resolution specification together.
/// </summary>
public sealed class TagMergeService(
    CoveContext db,
    IEventBus? eventBus = null,
    ISegmentSpanCacheInvalidator? spanCacheInvalidator = null,
    ITagExternalReferenceInspector? externalReferenceInspector = null,
    BlobReferenceTransactionCoordinator? blobReferenceTransactions = null)
{
    public async Task<TagMergeResult> MergeAsync(
        int targetId,
        IReadOnlyCollection<int> sourceIds,
        CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction != null)
            return await MergeWithinTransactionAsync(targetId, sourceIds, bypassTagVisibility: false, ct);

        TagMergeResult? result = null;
        var attempt = 0;
        var executionStrategy = db.Database.CreateExecutionStrategy();
        await executionStrategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0)
                db.ChangeTracker.Clear();
            var blobReferenceTransaction = blobReferenceTransactions == null
                ? null
                : await blobReferenceTransactions.BeginAsync(db, ct);
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                result = await MergeWithinTransactionAsync(targetId, sourceIds, bypassTagVisibility: false, ct);
                await transaction.CommitAsync(ct);
                if (blobReferenceTransaction != null)
                    await blobReferenceTransaction.CompleteAsync();
            }
            finally
            {
                if (blobReferenceTransaction != null)
                    await blobReferenceTransaction.DisposeAsync();
            }
        });

        PublishCompletedMerge(result!);
        return result!;
    }

    internal async Task<TagMergeResult> MergeWithinTransactionAsync(
        int targetId,
        IReadOnlyCollection<int> sourceIds,
        bool bypassTagVisibility,
        CancellationToken ct = default)
    {
        var requestedSourceIds = sourceIds
            .Where(id => id > 0 && id != targetId)
            .Distinct()
            .Order()
            .ToArray();
        var requestedIds = requestedSourceIds.Append(targetId).Distinct().ToArray();
        var requestedTags = bypassTagVisibility
            ? db.Tags.IgnoreQueryFilters()
            : db.Tags;
        var tags = await requestedTags
            .Where(tag => requestedIds.Contains(tag.Id))
            .OrderBy(tag => tag.Id)
            .ToListAsync(ct);
        var target = tags.SingleOrDefault(tag => tag.Id == targetId)
            ?? throw new KeyNotFoundException($"Target tag {targetId} was not found.");
        var sources = tags.Where(tag => tag.Id != targetId).OrderBy(tag => tag.Id).ToArray();
        if (sources.Length == 0)
            return new TagMergeResult(targetId, []);

        // The requested tags themselves are resolved under the caller's normal visibility rules for
        // ordinary merges. Once that authorization boundary has been established, the transfer must
        // see every attached row, including another user's engagement and relationships to content
        // hidden from the caller. The administrator cleanup path opts into tag visibility bypass too.
        using var authorizationFilterSuppression = db.SuppressAuthorizationFilters();
        using var tagNameValidationSuppression = db.SuppressTagNameValidation();

        var mergedSourceIds = sources.Select(tag => tag.Id).ToArray();
        var allIds = mergedSourceIds.Append(targetId).ToArray();
        var tagIdMap = mergedSourceIds.ToDictionary(id => id, _ => targetId);
        tagIdMap[targetId] = targetId;

        await EnsureNoExternalReferencesAsync(mergedSourceIds, ct);
        MergeIntrinsicMetadata(target, sources);
        await TransferEntityLinksAsync(targetId, allIds, ct);
        await TransferHierarchyAsync(targetId, allIds, tagIdMap, ct);
        await TransferAliasesAsync(target, sources, allIds, ct);
        await TransferRemoteIdsAsync(targetId, allIds, ct);
        await db.SaveChangesAsync(ct);

        await TransferTagApplicationsAsync(targetId, allIds, tagIdMap, ct);
        await TransferCustomFieldValuesAsync(targetId, allIds, tagIdMap, ct);
        await TransferFieldProvenanceAsync(targetId, allIds, ct);
        await TransferRatingsAsync(targetId, allIds, ct);
        await TransferBookmarksAsync(targetId, allIds, ct);
        await TransferAffinitiesAsync(targetId, allIds, ct);
        await TransferRoleOverridesAsync(targetId, allIds, ct);
        await db.SaveChangesAsync(ct);

        await TransferDirectReferencesAsync(targetId, allIds, tagIdMap, ct);
        await RewriteStoredJsonReferencesAsync(tagIdMap, ct);
        await db.SaveChangesAsync(ct);

        db.Tags.RemoveRange(sources);
        await db.SaveChangesAsync(ct);

        return new TagMergeResult(targetId, mergedSourceIds);
    }

    private async Task EnsureNoExternalReferencesAsync(int[] sourceIds, CancellationToken ct)
    {
        if (externalReferenceInspector == null || sourceIds.Length == 0)
            return;

        var references = await externalReferenceInspector.InspectAsync(sourceIds, ct);
        if (references.Count > 0)
            throw new TagMergeBlockedException(
                references.Sum(reference => reference.RowCount ?? 0),
                references.Select(reference => reference.TagId).Distinct().Count(),
                references.Any(reference => reference.AccessLimitation != null));
    }

    internal void PublishCompletedMerge(TagMergeResult result)
    {
        if (result.MergedSourceIds.Count == 0)
            return;

        spanCacheInvalidator?.InvalidateAll();
        eventBus?.Publish(new EntityEvent(EventType.TagUpdated, "Tag", result.TargetId));
        foreach (var sourceId in result.MergedSourceIds)
            eventBus?.Publish(new EntityEvent(EventType.TagDeleted, "Tag", sourceId));
    }

    private static void MergeIntrinsicMetadata(Tag target, IReadOnlyList<Tag> sources)
    {
        static string? FirstText(string? targetValue, IEnumerable<string?> sourceValues)
            => !string.IsNullOrWhiteSpace(targetValue)
                ? targetValue
                : sourceValues.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        target.SortName = FirstText(target.SortName, sources.Select(tag => tag.SortName));
        target.Description = FirstText(target.Description, sources.Select(tag => tag.Description));
        target.Color = FirstText(target.Color, sources.Select(tag => tag.Color));
        target.TagGroupId ??= sources.Select(tag => tag.TagGroupId).FirstOrDefault(value => value != null);
        target.Favorite |= sources.Any(tag => tag.Favorite);
        target.Organized |= sources.Any(tag => tag.Organized);
        target.MinOccurrenceSec ??= sources.Select(tag => tag.MinOccurrenceSec).FirstOrDefault(value => value != null);
        target.MinOccurrencePercent ??= sources.Select(tag => tag.MinOccurrencePercent).FirstOrDefault(value => value != null);
        target.ShowAsSegment ??= sources.Select(tag => tag.ShowAsSegment).FirstOrDefault(value => value != null);
        target.SegmentColorOverride = FirstText(target.SegmentColorOverride, sources.Select(tag => tag.SegmentColorOverride));
        target.SegmentLaneOverride ??= sources.Select(tag => tag.SegmentLaneOverride).FirstOrDefault(value => value != null);
        target.ImageBlobId = FirstText(target.ImageBlobId, sources.Select(tag => tag.ImageBlobId));
        target.ImageOverrideBlobId = FirstText(target.ImageOverrideBlobId, sources.Select(tag => tag.ImageOverrideBlobId));
        target.SearchText = FirstText(target.SearchText, sources.Select(tag => tag.SearchText));
    }

    private async Task TransferEntityLinksAsync(int targetId, int[] allIds, CancellationToken ct)
    {
        var videoLinks = await db.Set<VideoTag>().Where(link => allIds.Contains(link.TagId)).ToListAsync(ct);
        TransferLinks(videoLinks, targetId, link => link.TagId, link => link.VideoId, ownerId => new VideoTag { VideoId = ownerId, TagId = targetId });

        var audioLinks = await db.Set<AudioTag>().Where(link => allIds.Contains(link.TagId)).ToListAsync(ct);
        var affectedAudioIds = TransferLinks(audioLinks, targetId, link => link.TagId, link => link.AudioId, ownerId => new AudioTag { AudioId = ownerId, TagId = targetId });

        var textLinks = await db.Set<TextTag>().Where(link => allIds.Contains(link.TagId)).ToListAsync(ct);
        var affectedTextIds = TransferLinks(textLinks, targetId, link => link.TagId, link => link.TextDocumentId, ownerId => new TextTag { TextDocumentId = ownerId, TagId = targetId });

        var performerLinks = await db.Set<PerformerTag>().Where(link => allIds.Contains(link.TagId)).ToListAsync(ct);
        TransferLinks(performerLinks, targetId, link => link.TagId, link => link.PerformerId, ownerId => new PerformerTag { PerformerId = ownerId, TagId = targetId });

        var imageLinks = await db.Set<ImageTag>().Where(link => allIds.Contains(link.TagId)).ToListAsync(ct);
        TransferLinks(imageLinks, targetId, link => link.TagId, link => link.ImageId, ownerId => new ImageTag { ImageId = ownerId, TagId = targetId });

        var galleryLinks = await db.Set<GalleryTag>().Where(link => allIds.Contains(link.TagId)).ToListAsync(ct);
        TransferLinks(galleryLinks, targetId, link => link.TagId, link => link.GalleryId, ownerId => new GalleryTag { GalleryId = ownerId, TagId = targetId });

        var studioLinks = await db.Set<StudioTag>().Where(link => allIds.Contains(link.TagId)).ToListAsync(ct);
        TransferLinks(studioLinks, targetId, link => link.TagId, link => link.StudioId, ownerId => new StudioTag { StudioId = ownerId, TagId = targetId });

        var groupLinks = await db.Set<GroupTag>().Where(link => allIds.Contains(link.TagId)).ToListAsync(ct);
        TransferLinks(groupLinks, targetId, link => link.TagId, link => link.GroupId, ownerId => new GroupTag { GroupId = ownerId, TagId = targetId });

        await db.SaveChangesAsync(ct);

        if (affectedAudioIds.Count > 0)
        {
            var audios = await db.Audios.Where(audio => affectedAudioIds.Contains(audio.Id)).ToListAsync(ct);
            foreach (var audio in audios)
                audio.TagIds = await db.Set<AudioTag>().Where(link => link.AudioId == audio.Id).Select(link => link.TagId).OrderBy(id => id).ToArrayAsync(ct);
        }

        if (affectedTextIds.Count > 0)
        {
            var texts = await db.TextDocuments.Where(text => affectedTextIds.Contains(text.Id)).ToListAsync(ct);
            foreach (var text in texts)
                text.TagIds = await db.Set<TextTag>().Where(link => link.TextDocumentId == text.Id).Select(link => link.TagId).OrderBy(id => id).ToArrayAsync(ct);
        }
    }

    private HashSet<int> TransferLinks<TLink>(
        IReadOnlyCollection<TLink> links,
        int targetId,
        Func<TLink, int> getTagId,
        Func<TLink, int> getOwnerId,
        Func<int, TLink> createTargetLink)
        where TLink : class
    {
        var targetOwnerIds = links
            .Where(link => getTagId(link) == targetId)
            .Select(getOwnerId)
            .ToHashSet();
        var sourceLinks = links.Where(link => getTagId(link) != targetId).ToArray();
        foreach (var ownerId in sourceLinks.Select(getOwnerId).Distinct())
            if (targetOwnerIds.Add(ownerId))
                db.Set<TLink>().Add(createTargetLink(ownerId));
        db.Set<TLink>().RemoveRange(sourceLinks);
        return links.Select(getOwnerId).ToHashSet();
    }

    private async Task TransferHierarchyAsync(
        int targetId,
        int[] allIds,
        IReadOnlyDictionary<int, int> tagIdMap,
        CancellationToken ct)
    {
        var relations = await db.Set<TagParent>()
            .Where(relation => allIds.Contains(relation.ParentId) || allIds.Contains(relation.ChildId))
            .ToListAsync(ct);
        var sourceRelations = relations
            .Where(relation => relation.ParentId != targetId && tagIdMap.ContainsKey(relation.ParentId)
                || relation.ChildId != targetId && tagIdMap.ContainsKey(relation.ChildId))
            .ToArray();
        var kept = relations.Except(sourceRelations)
            .Select(relation => (relation.ParentId, relation.ChildId))
            .ToHashSet();
        foreach (var relation in sourceRelations)
        {
            var parentId = tagIdMap.GetValueOrDefault(relation.ParentId, relation.ParentId);
            var childId = tagIdMap.GetValueOrDefault(relation.ChildId, relation.ChildId);
            if (parentId != childId && kept.Add((parentId, childId)))
                db.Set<TagParent>().Add(new TagParent { ParentId = parentId, ChildId = childId });
        }
        db.Set<TagParent>().RemoveRange(sourceRelations);
    }

    private async Task TransferAliasesAsync(Tag target, IReadOnlyList<Tag> sources, int[] allIds, CancellationToken ct)
    {
        var aliases = await db.Set<TagAlias>()
            .Where(alias => allIds.Contains(alias.TagId))
            .OrderBy(alias => alias.TagId == target.Id ? 0 : 1)
            .ThenBy(alias => alias.Id)
            .ToListAsync(ct);
        var candidateValues = aliases.Select(alias => alias.Alias)
            .Concat(sources.Select(source => source.Name));
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var normalizedTargetName = TagNameRules.NormalizeCanonicalName(target.Name);
        var replacementValues = new List<string>();
        foreach (var value in candidateValues)
        {
            var normalized = TagNameRules.NormalizeAlias(value);
            if (normalized == null || TagNameRules.NamesEqual(normalized, normalizedTargetName))
                continue;
            if (kept.Add(TagNameRules.NamespaceKey(normalized)))
                replacementValues.Add(normalized);
        }

        db.Set<TagAlias>().RemoveRange(aliases);
        foreach (var value in replacementValues)
            db.Set<TagAlias>().Add(new TagAlias { TagId = target.Id, Alias = value });
    }

    private async Task TransferRemoteIdsAsync(int targetId, int[] allIds, CancellationToken ct)
    {
        var remoteIds = await db.Set<TagRemoteId>()
            .Where(remoteId => allIds.Contains(remoteId.TagId))
            .OrderBy(remoteId => remoteId.TagId == targetId ? 0 : 1)
            .ThenBy(remoteId => remoteId.Id)
            .ToListAsync(ct);
        var seen = new HashSet<(string Endpoint, string RemoteId)>();
        foreach (var remoteId in remoteIds)
        {
            if (!seen.Add((remoteId.Endpoint, remoteId.RemoteId)))
                db.Set<TagRemoteId>().Remove(remoteId);
            else
                remoteId.TagId = targetId;
        }
    }

    private async Task TransferTagApplicationsAsync(
        int targetId,
        int[] allIds,
        IReadOnlyDictionary<int, int> tagIdMap,
        CancellationToken ct)
    {
        var applications = await db.TagApplications
            .Where(application => allIds.Contains(application.TagId)
                || application.HostType == AffinityHostType.Tag && allIds.Contains(application.HostId))
            .ToListAsync(ct);
        var groups = applications.GroupBy(application => new
        {
            application.HostType,
            HostId = application.HostType == AffinityHostType.Tag ? tagIdMap.GetValueOrDefault(application.HostId, application.HostId) : application.HostId,
            application.ContextType,
            application.ContextId,
            TagId = tagIdMap.GetValueOrDefault(application.TagId, application.TagId),
            application.SourceKey,
            application.SourceRunId,
            application.ModelKey,
        });
        foreach (var group in groups)
        {
            var keeper = group
                .OrderByDescending(application => application.TagId == targetId
                    && (application.HostType != AffinityHostType.Tag || application.HostId == targetId))
                .ThenBy(application => application.Id)
                .First();
            keeper.TagId = group.Key.TagId;
            keeper.HostId = group.Key.HostId;
            keeper.Confidence = group.Max(application => application.Confidence);
            keeper.TotalDurationSec = group.Max(application => application.TotalDurationSec);
            keeper.HostDurationSec = group.Max(application => application.HostDurationSec);
            db.TagApplications.RemoveRange(group.Where(application => application.Id != keeper.Id));
        }
    }

    private async Task TransferCustomFieldValuesAsync(
        int targetId,
        int[] allIds,
        IReadOnlyDictionary<int, int> tagIdMap,
        CancellationToken ct)
    {
        var tagReferenceDefinitionIds = await db.CustomFieldDefinitions
            .Where(definition => definition.Type.ToLower() == CustomFieldTypes.Tag)
            .Select(definition => definition.Id)
            .ToArrayAsync(ct);
        var values = await db.CustomFieldValues
            .Where(value => value.EntityType.ToLower() == CustomFieldEntityTypes.Tag && allIds.Contains(value.EntityId)
                || tagReferenceDefinitionIds.Contains(value.DefinitionId) && value.IntegerValue != null && allIds.Contains(value.IntegerValue.Value))
            .ToListAsync(ct);

        var entityValues = values
            .Where(value => value.EntityType.Equals(CustomFieldEntityTypes.Tag, StringComparison.OrdinalIgnoreCase)
                && allIds.Contains(value.EntityId))
            .GroupBy(value => new { value.DefinitionId, EntityType = value.EntityType.ToLowerInvariant(), EntityId = targetId, value.Position });
        var removedIds = new HashSet<int>();
        foreach (var group in entityValues)
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
            && tagReferenceDefinitionIds.Contains(value.DefinitionId)
            && value.IntegerValue is int referenceId
            && tagIdMap.ContainsKey(referenceId)))
            value.IntegerValue = tagIdMap[value.IntegerValue!.Value];
    }

    private async Task TransferFieldProvenanceAsync(int targetId, int[] allIds, CancellationToken ct)
    {
        var rows = await db.FieldProvenance
            .Where(row => row.HostType == AffinityHostType.Tag && allIds.Contains(row.HostId))
            .ToListAsync(ct);
        foreach (var group in rows.GroupBy(row => new { row.HostType, HostId = targetId, row.FieldKey, row.SourceKey, row.SourceRunId, row.ModelKey }))
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
                // Leave an invalid legacy value to the scalar preservation path.
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
            // PostgreSQL validates jsonb, but preserve nonblank legacy/test-provider values rather
            // than discarding provenance merely because this helper cannot interpret them.
            return true;
        }
    }

    private async Task TransferRatingsAsync(int targetId, int[] allIds, CancellationToken ct)
    {
        var ratings = await db.Ratings
            .Where(rating => rating.HostType == RatingHostType.Tag && allIds.Contains(rating.HostId))
            .ToListAsync(ct);
        foreach (var group in ratings.GroupBy(rating => new { rating.UserId, rating.HostType, HostId = targetId, rating.Aspect }))
        {
            var keeper = group.OrderByDescending(rating => rating.HostId == targetId).ThenBy(rating => rating.Id).First();
            keeper.HostId = targetId;
            db.Ratings.RemoveRange(group.Where(rating => rating.Id != keeper.Id));
        }
    }

    private async Task TransferBookmarksAsync(int targetId, int[] allIds, CancellationToken ct)
    {
        var bookmarks = await db.UserBookmarks
            .Where(bookmark => bookmark.HostType == AffinityHostType.Tag && allIds.Contains(bookmark.HostId))
            .ToListAsync(ct);
        foreach (var group in bookmarks.GroupBy(bookmark => bookmark.UserId))
        {
            var targetBookmark = group.FirstOrDefault(bookmark => bookmark.HostId == targetId);
            var createdAt = group.Min(bookmark => bookmark.CreatedAt);
            db.UserBookmarks.RemoveRange(group.Where(bookmark => bookmark.HostId != targetId));
            if (targetBookmark != null)
                targetBookmark.CreatedAt = createdAt;
            else
                db.UserBookmarks.Add(new UserBookmark { UserId = group.Key, HostType = AffinityHostType.Tag, HostId = targetId, CreatedAt = createdAt });
        }
    }

    private async Task TransferAffinitiesAsync(int targetId, int[] allIds, CancellationToken ct)
    {
        var affinities = await db.UserEntityAffinities
            .Where(affinity => affinity.HostType == AffinityHostType.Tag && allIds.Contains(affinity.HostId))
            .ToListAsync(ct);
        foreach (var group in affinities.GroupBy(affinity => affinity.UserId))
        {
            var ordered = group.OrderByDescending(affinity => affinity.HostId == targetId).ThenBy(affinity => affinity.Id).ToArray();
            var keeper = ordered[0];
            var mostRecent = ordered.OrderByDescending(affinity => affinity.LastConsumedAt).ThenBy(affinity => affinity.Id).First();
            var deepest = ordered.OrderByDescending(affinity => affinity.MaxDwellSec).ThenBy(affinity => affinity.Id).First();
            keeper.HostId = targetId;
            keeper.IsFavorite = ordered.Any(affinity => affinity.IsFavorite);
            keeper.FavoritedAt = ordered.Where(affinity => affinity.FavoritedAt != null).Min(affinity => affinity.FavoritedAt);
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

    private async Task TransferRoleOverridesAsync(int targetId, int[] allIds, CancellationToken ct)
    {
        var idStrings = allIds.Select(id => id.ToString()).ToArray();
        var overrides = await db.RoleEntityOverrides
            .Where(row => row.EntityKind.ToLower() == EntityKinds.Tag && idStrings.Contains(row.EntityId))
            .ToListAsync(ct);
        foreach (var group in overrides.GroupBy(row => new { row.RoleId, EntityKind = row.EntityKind.ToLowerInvariant(), row.AppliesTo }))
        {
            var ordered = group.OrderByDescending(row => row.EntityId == targetId.ToString()).ThenBy(row => row.Id).ToArray();
            var keeper = ordered[0];
            keeper.EntityId = targetId.ToString();
            keeper.Effect = ordered.Any(row => row.Effect.Equals("deny", StringComparison.OrdinalIgnoreCase)) ? "deny" : "allow";
            db.RoleEntityOverrides.RemoveRange(ordered.Skip(1));
        }
    }

    private async Task TransferDirectReferencesAsync(
        int targetId,
        int[] allIds,
        IReadOnlyDictionary<int, int> tagIdMap,
        CancellationToken ct)
    {
        var segments = await db.Segments
            .Where(segment => segment.TagId != null && allIds.Contains(segment.TagId.Value))
            .ToListAsync(ct);
        foreach (var segment in segments)
            segment.TagId = targetId;

        var displayRules = await db.SegmentDisplayRules
            .Where(rule => rule.TagId != null && allIds.Contains(rule.TagId.Value))
            .ToListAsync(ct);
        foreach (var rule in displayRules)
            rule.TagId = targetId;

        var interactions = await db.Interactions
            .Where(row => row.HostType == InteractionHostType.Tag && allIds.Contains(row.HostId))
            .ToListAsync(ct);
        foreach (var row in interactions)
            row.HostId = targetId;

        await TransferPlaybackAsync(targetId, allIds, tagIdMap, ct);

        var groupItems = await db.GroupItems
            .Where(item => item.HostType.ToLower() == EntityKinds.Tag && allIds.Contains(item.HostId))
            .ToListAsync(ct);
        foreach (var item in groupItems)
            item.HostId = targetId;

        var userSessions = await db.UserSessions
            .Where(session => session.LastHostType == InteractionHostType.Tag && session.LastHostId != null && allIds.Contains(session.LastHostId.Value))
            .ToListAsync(ct);
        foreach (var session in userSessions)
            session.LastHostId = targetId;
    }

    private async Task TransferPlaybackAsync(
        int targetId,
        int[] allIds,
        IReadOnlyDictionary<int, int> tagIdMap,
        CancellationToken ct)
    {
        var sessions = await db.PlaybackSessions
            .Where(session => session.HostType == InteractionHostType.Tag && allIds.Contains(session.HostId)
                || session.ParentHostType == InteractionHostType.Tag && session.ParentHostId != null && allIds.Contains(session.ParentHostId.Value)
                || session.ItemHostType == InteractionHostType.Tag && session.ItemHostId != null && allIds.Contains(session.ItemHostId.Value))
            .ToListAsync(ct);
        var hostSessions = sessions.Where(session => session.HostType == InteractionHostType.Tag && allIds.Contains(session.HostId)).ToArray();
        var originalWatchedBySessionId = hostSessions.ToDictionary(session => session.Id, session => session.TotalWatchedSec);
        var duplicateSessionIds = new Dictionary<int, int>();
        var combinedSessionMemberIds = new Dictionary<int, int[]>();
        foreach (var group in hostSessions.Where(session => session.UserSessionId != null).GroupBy(session => new { session.UserId, session.UserSessionId }))
        {
            var ordered = group.OrderByDescending(session => session.HostId == targetId).ThenBy(session => session.Id).ToArray();
            var keeper = ordered[0];
            var latest = ordered.OrderByDescending(session => session.LastSeenAt).ThenBy(session => session.Id).First();
            var latestPosition = latest.LastPositionSec;
            var latestContext = latest.Context == null
                ? null
                : JsonDocument.Parse(latest.Context.RootElement.GetRawText());
            keeper.HostId = targetId;
            keeper.StartedAt = ordered.Min(session => session.StartedAt);
            keeper.LastSeenAt = ordered.Max(session => session.LastSeenAt);
            keeper.EndedAt = ordered.Max(session => session.EndedAt);
            keeper.State = ordered.Max(session => session.State);
            keeper.MediaDurationSec = ordered.Max(session => session.MediaDurationSec);
            keeper.LastPositionSec = latestPosition;
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
            keeper.Context = latestContext;
            if (ordered.Length > 1)
                combinedSessionMemberIds[keeper.Id] = ordered.Select(session => session.Id).ToArray();
            foreach (var duplicate in ordered.Skip(1))
                duplicateSessionIds[duplicate.Id] = keeper.Id;
        }

        foreach (var session in hostSessions.Where(session => session.UserSessionId == null))
            session.HostId = targetId;
        foreach (var session in sessions)
        {
            if (session.ParentHostType == InteractionHostType.Tag && session.ParentHostId is int parentId)
                session.ParentHostId = tagIdMap.GetValueOrDefault(parentId, parentId);
            if (session.ItemHostType == InteractionHostType.Tag && session.ItemHostId is int itemId)
                session.ItemHostId = tagIdMap.GetValueOrDefault(itemId, itemId);
        }

        var sessionIds = sessions.Select(session => session.Id).ToArray();
        var intervals = await db.PlaybackIntervals
            .Where(interval => sessionIds.Contains(interval.PlaybackSessionId)
                || interval.HostType == InteractionHostType.Tag && allIds.Contains(interval.HostId)
                || interval.ParentHostType == InteractionHostType.Tag && interval.ParentHostId != null && allIds.Contains(interval.ParentHostId.Value)
                || interval.ItemHostType == InteractionHostType.Tag && interval.ItemHostId != null && allIds.Contains(interval.ItemHostId.Value))
            .ToListAsync(ct);
        var intervalCoverageByOriginalSessionId = intervals
            .Where(interval => originalWatchedBySessionId.ContainsKey(interval.PlaybackSessionId))
            .GroupBy(interval => interval.PlaybackSessionId)
            .ToDictionary(group => group.Key, group => PlaybackIntervalMath.ComputeMergedWatchedSec(group));
        foreach (var interval in intervals)
        {
            if (duplicateSessionIds.TryGetValue(interval.PlaybackSessionId, out var keeperId))
                interval.PlaybackSessionId = keeperId;
            if (interval.HostType == InteractionHostType.Tag && allIds.Contains(interval.HostId))
                interval.HostId = targetId;
            if (interval.ParentHostType == InteractionHostType.Tag && interval.ParentHostId is int parentId)
                interval.ParentHostId = tagIdMap.GetValueOrDefault(parentId, parentId);
            if (interval.ItemHostType == InteractionHostType.Tag && interval.ItemHostId is int itemId)
                interval.ItemHostId = tagIdMap.GetValueOrDefault(itemId, itemId);
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

    private async Task RewriteStoredJsonReferencesAsync(IReadOnlyDictionary<int, int> tagIdMap, CancellationToken ct)
    {
        var fieldProvenance = await db.FieldProvenance
            .Where(row => row.ValueJson != null)
            .ToListAsync(ct);
        foreach (var row in fieldProvenance)
            row.ValueJson = TagReferenceJsonRewriter.RewriteFieldProvenanceValue(
                row.HostType,
                row.FieldKey,
                row.ValueJson,
                tagIdMap);

        var interactions = await db.Interactions
            .Where(interaction => interaction.Meta != null)
            .ToListAsync(ct);
        foreach (var interaction in interactions)
            interaction.Meta = TagReferenceJsonRewriter.Rewrite(interaction.Meta, tagIdMap);

        var playbackSessions = await db.PlaybackSessions
            .Where(session => session.Context != null)
            .ToListAsync(ct);
        foreach (var session in playbackSessions)
            session.Context = TagReferenceJsonRewriter.Rewrite(session.Context, tagIdMap);

        var playbackIntervals = await db.PlaybackIntervals
            .Where(interval => interval.Context != null)
            .ToListAsync(ct);
        foreach (var interval in playbackIntervals)
            interval.Context = TagReferenceJsonRewriter.Rewrite(interval.Context, tagIdMap);

        var payloadSegments = await db.Segments.Where(segment => segment.Payload != null).ToListAsync(ct);
        foreach (var segment in payloadSegments)
            segment.Payload = TagReferenceJsonRewriter.Rewrite(segment.Payload, tagIdMap);

        var savedFilters = await db.SavedFilters.Where(filter => filter.ObjectFilter != null).ToListAsync(ct);
        foreach (var filter in savedFilters)
            filter.ObjectFilter = TagReferenceJsonRewriter.Rewrite(
                filter.ObjectFilter,
                tagIdMap,
                filter.Mode.Equals(EntityKinds.Tag, StringComparison.OrdinalIgnoreCase)
                    || filter.Mode.Equals("tags", StringComparison.OrdinalIgnoreCase));

        var users = await db.Users.Where(user => user.UiPreferencesJson != null).ToListAsync(ct);
        foreach (var user in users)
            user.UiPreferencesJson = TagReferenceJsonRewriter.RewriteUserUiPreferences(
                user.UiPreferencesJson,
                tagIdMap);

        var groups = await db.Groups.Where(group => group.QueryJson != null).ToListAsync(ct);
        foreach (var group in groups)
            group.QueryJson = TagReferenceJsonRewriter.Rewrite(group.QueryJson, tagIdMap);

        var groupItems = await db.GroupItems.Where(item => item.SourceQueryJson != null).ToListAsync(ct);
        foreach (var item in groupItems)
            item.SourceQueryJson = TagReferenceJsonRewriter.Rewrite(item.SourceQueryJson, tagIdMap);

        var contentRules = await db.RoleContentRules.Where(rule => rule.ScopeValue != null).ToListAsync(ct);
        foreach (var rule in contentRules)
            rule.ScopeValue = TagReferenceJsonRewriter.RewriteRoleContentScope(
                rule.EntityKind,
                rule.ScopeKind,
                rule.ScopeValue,
                tagIdMap) ?? rule.ScopeValue;

        var shareLinks = await db.ShareLinks.Where(link => link.EntityKind.ToLower() == EntityKinds.Tag).ToListAsync(ct);
        foreach (var link in shareLinks)
            link.EntityIds = RewriteShareLinkIds(link.EntityIds, tagIdMap);
    }

    private static string RewriteShareLinkIds(string json, IReadOnlyDictionary<int, int> tagIdMap)
        => TagReferenceJsonRewriter.Rewrite($$"""{"tagIds":{{json}}}""", tagIdMap) is { } wrapped
            ? wrapped[10..^1]
            : json;

    private static int SumClamped(IEnumerable<int> values)
        => (int)Math.Clamp(values.Aggregate(0L, (sum, value) => sum + value), int.MinValue, int.MaxValue);
}
