using Cove.Core.DTOs;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Services;

/// <summary>
/// Read-only Cove 1.2 compatibility scanner for the canonical identity constraints introduced in
/// Cove 1.3. Performer aliases and studio aliases intentionally do not participate.
/// </summary>
public sealed class EntityNameConflictScanner(
    CoveContext db,
    IEntityExternalReferenceInspector? externalReferenceInspector = null)
{
    private sealed record IdentityRow(
        int Id,
        string Name,
        string? Disambiguation,
        string NormalizedName,
        string? NormalizedDisambiguation,
        string IdentityKey);

    private sealed record CountRow(int EntityId, int Count);

    public Task<EntityNameConflictScanDto> ScanAsync(
        string entityType,
        CancellationToken ct = default)
        => ScanCoreAsync(entityType, includeImpacts: true, ct);

    internal Task<EntityNameConflictScanDto> ScanWithoutImpactsAsync(
        string entityType,
        CancellationToken ct = default)
        => ScanCoreAsync(entityType, includeImpacts: false, ct);

    public async Task<EntityNameConflictSummaryDto> ScanSummaryAsync(CancellationToken ct = default)
    {
        var performers = await ScanWithoutImpactsAsync(NameConflictEntityTypes.Performer, ct);
        var studios = await ScanWithoutImpactsAsync(NameConflictEntityTypes.Studio, ct);
        return new EntityNameConflictSummaryDto(
            performers.UnresolvedGroupCount,
            studios.UnresolvedGroupCount,
            DateTime.UtcNow);
    }

    private async Task<EntityNameConflictScanDto> ScanCoreAsync(
        string entityType,
        bool includeImpacts,
        CancellationToken ct)
    {
        if (!NameConflictEntityTypes.IsSupported(entityType))
            throw new ArgumentException("The requested entity type does not have a canonical-name compatibility policy.", nameof(entityType));

        IdentityRow[] rows;
        if (entityType == NameConflictEntityTypes.Performer)
        {
            var performers = await db.Performers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .OrderBy(entity => entity.Id)
                .Select(entity => new { entity.Id, entity.Name, entity.Disambiguation })
                .ToListAsync(ct);
            rows = performers
                .Select(entity => CreatePerformerRow(entity.Id, entity.Name, entity.Disambiguation))
                .ToArray();
        }
        else
        {
            var studios = await db.Studios
                .IgnoreQueryFilters()
                .AsNoTracking()
                .OrderBy(entity => entity.Id)
                .Select(entity => new { entity.Id, entity.Name })
                .ToListAsync(ct);
            rows = studios
                .Select(entity => CreateStudioRow(entity.Id, entity.Name))
                .ToArray();
        }

        var conflicts = rows
            .GroupBy(row => row.IdentityKey, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.OrderBy(row => row.Id).ToArray())
            .ToArray();
        var conflictIds = conflicts.SelectMany(group => group).Select(row => row.Id).Distinct().Order().ToArray();
        var impacts = includeImpacts
            ? await LoadImpactsAsync(entityType, rows, conflictIds, ct)
            : rows.Where(row => conflictIds.Contains(row.Id)).ToDictionary(
                row => row.Id,
                row => EmptyImpact(row));

        var groups = conflicts.Select(candidates => CreateGroup(entityType, candidates, impacts)).ToArray();
        return new EntityNameConflictScanDto(
            entityType,
            groups.Length,
            DateTime.UtcNow,
            EntityNameRules.ConflictScanRevision(groups.Select(group => new EntityNameGroupRevision(entityType, group.Key, group.Revision))),
            groups);
    }

    private static IdentityRow CreatePerformerRow(int id, string name, string? disambiguation)
    {
        var normalizedName = EntityNameRules.NormalizeCanonicalName(name);
        var normalizedDisambiguation = EntityNameRules.NormalizeDisambiguation(disambiguation);
        return new IdentityRow(
            id,
            name,
            disambiguation,
            normalizedName,
            normalizedDisambiguation,
            EntityNameRules.PerformerIdentityKey(normalizedName, normalizedDisambiguation));
    }

    private static IdentityRow CreateStudioRow(int id, string name)
    {
        var normalizedName = EntityNameRules.NormalizeCanonicalName(name);
        return new IdentityRow(
            id,
            name,
            null,
            normalizedName,
            null,
            EntityNameRules.StudioIdentityKey(normalizedName));
    }

    private static EntityNameConflictGroupDto CreateGroup(
        string entityType,
        IdentityRow[] candidates,
        IReadOnlyDictionary<int, EntityNameImpactDto> impacts)
    {
        var survivorId = candidates
            .OrderByDescending(candidate => impacts[candidate.Id].ReferenceCount)
            .ThenBy(candidate => candidate.Id)
            .First()
            .Id;
        var identityKey = candidates[0].IdentityKey;
        var externalRevision = candidates
            .SelectMany(candidate => impacts[candidate.Id].ExternalReferences)
            .Select(reference => new EntityExternalReferenceRevision(
                reference.EntityId,
                reference.ReferenceKey,
                reference.RowCount,
                reference.AccessLimitation));
        var key = EntityNameRules.ConflictGroupKey(entityType, identityKey);
        var revision = EntityNameRules.ConflictGroupRevision(
            entityType,
            identityKey,
            survivorId,
            candidates.Select(candidate => new EntityNameRevisionCandidate(
                candidate.Id,
                candidate.Name,
                candidate.Disambiguation,
                candidate.NormalizedName,
                candidate.NormalizedDisambiguation)),
            externalRevision);

        return new EntityNameConflictGroupDto(
            entityType,
            key,
            revision,
            candidates[0].NormalizedName,
            candidates[0].NormalizedDisambiguation,
            survivorId,
            candidates.Where(candidate => candidate.Id != survivorId).Select(candidate => candidate.Id).ToArray(),
            candidates.Select(candidate => new EntityNameConflictCandidateDto(
                candidate.Id,
                candidate.Name,
                candidate.Disambiguation,
                candidate.NormalizedName,
                candidate.NormalizedDisambiguation,
                candidate.Id == survivorId ? EntityNameConflictActions.Keep : EntityNameConflictActions.MergeEntity,
                candidate.Id == survivorId)).ToArray(),
            candidates.Select(candidate => impacts[candidate.Id]).ToArray());
    }

    private async Task<Dictionary<int, EntityNameImpactDto>> LoadImpactsAsync(
        string entityType,
        IReadOnlyCollection<IdentityRow> rows,
        int[] ids,
        CancellationToken ct)
    {
        var linked = ids.ToDictionary(id => id, _ => 0);
        var groups = ids.ToDictionary(id => id, _ => 0);
        var hierarchy = ids.ToDictionary(id => id, _ => 0);
        var faces = ids.ToDictionary(id => id, _ => 0);
        var ratings = ids.ToDictionary(id => id, _ => 0);
        var other = ids.ToDictionary(id => id, _ => 0);
        var extension = ids.ToDictionary(id => id, _ => 0);
        IReadOnlyList<EntityExternalReferenceDto> externalReferences = [];
        if (ids.Length == 0)
            return [];

        async Task AddCountsAsync(IQueryable<int> query, Dictionary<int, int> destination)
        {
            var counts = await query
                .Where(id => ids.Contains(id))
                .GroupBy(id => id)
                .Select(group => new CountRow(group.Key, group.Count()))
                .ToListAsync(ct);
            foreach (var count in counts)
                destination[count.EntityId] += count.Count;
        }

        if (entityType == NameConflictEntityTypes.Performer)
        {
            await AddCountsAsync(db.Set<VideoPerformer>().IgnoreQueryFilters().Select(link => link.PerformerId), linked);
            await AddCountsAsync(db.Set<ImagePerformer>().IgnoreQueryFilters().Select(link => link.PerformerId), linked);
            await AddCountsAsync(db.Set<GalleryPerformer>().IgnoreQueryFilters().Select(link => link.PerformerId), linked);
            await AddCountsAsync(db.Set<AudioPerformer>().IgnoreQueryFilters().Select(link => link.PerformerId), linked);
            await AddCountsAsync(db.Set<TextPerformer>().IgnoreQueryFilters().Select(link => link.PerformerId), linked);
            await AddCountsAsync(db.Faces.IgnoreQueryFilters().Where(face => face.PerformerId != null).Select(face => face.PerformerId!.Value), faces);
            await AddCountsAsync(db.Faces.IgnoreQueryFilters().Where(face => face.TopSuggestionPerformerId != null).Select(face => face.TopSuggestionPerformerId!.Value), faces);
            await AddCountsAsync(db.Faces.IgnoreQueryFilters().Where(face => face.TopSuggestionLocalPerformerId != null).Select(face => face.TopSuggestionLocalPerformerId!.Value), faces);
            await AddCountsAsync(db.FaceSuggestionDecisions.IgnoreQueryFilters().Select(decision => decision.PerformerId), faces);
            await AddCountsAsync(db.Set<PerformerAlias>().IgnoreQueryFilters().Select(row => row.PerformerId), other);
            await AddCountsAsync(db.Set<PerformerUrl>().IgnoreQueryFilters().Select(row => row.PerformerId), other);
            await AddCountsAsync(db.Set<PerformerRemoteId>().IgnoreQueryFilters().Select(row => row.PerformerId), other);
            await AddCountsAsync(db.Set<PerformerTag>().IgnoreQueryFilters().Select(row => row.PerformerId), other);
        }
        else
        {
            await AddCountsAsync(db.Videos.IgnoreQueryFilters().Where(row => row.StudioId != null).Select(row => row.StudioId!.Value), linked);
            await AddCountsAsync(db.Images.IgnoreQueryFilters().Where(row => row.StudioId != null).Select(row => row.StudioId!.Value), linked);
            await AddCountsAsync(db.Galleries.IgnoreQueryFilters().Where(row => row.StudioId != null).Select(row => row.StudioId!.Value), linked);
            await AddCountsAsync(db.Groups.IgnoreQueryFilters().Where(row => row.StudioId != null).Select(row => row.StudioId!.Value), linked);
            await AddCountsAsync(db.Audios.IgnoreQueryFilters().Where(row => row.StudioId != null).Select(row => row.StudioId!.Value), linked);
            await AddCountsAsync(db.TextDocuments.IgnoreQueryFilters().Where(row => row.StudioId != null).Select(row => row.StudioId!.Value), linked);
            await AddCountsAsync(db.Studios.IgnoreQueryFilters().Where(row => row.ParentId != null).Select(row => row.ParentId!.Value), hierarchy);
            await AddCountsAsync(db.Studios.IgnoreQueryFilters().Where(row => row.ParentId != null).Select(row => row.Id), hierarchy);
            await AddCountsAsync(db.Set<StudioAlias>().IgnoreQueryFilters().Select(row => row.StudioId), other);
            await AddCountsAsync(db.Set<StudioUrl>().IgnoreQueryFilters().Select(row => row.StudioId), other);
            await AddCountsAsync(db.Set<StudioRemoteId>().IgnoreQueryFilters().Select(row => row.StudioId), other);
            await AddCountsAsync(db.Set<StudioTag>().IgnoreQueryFilters().Select(row => row.StudioId), other);
        }

        var affinityType = entityType == NameConflictEntityTypes.Performer ? AffinityHostType.Performer : AffinityHostType.Studio;
        var interactionType = entityType == NameConflictEntityTypes.Performer ? InteractionHostType.Performer : InteractionHostType.Studio;
        var ratingType = entityType == NameConflictEntityTypes.Performer ? RatingHostType.Performer : RatingHostType.Studio;
        var entityKind = entityType;

        var groupItemKind = entityType == NameConflictEntityTypes.Performer
            ? GroupItemKind.Performer
            : GroupItemKind.Studio;
        await AddCountsAsync(db.GroupItems.IgnoreQueryFilters()
            .Where(item => item.HostType.ToLower() == entityKind || item.Kind == groupItemKind)
            .Select(item => item.HostId), groups);
        await AddCountsAsync(db.Ratings.IgnoreQueryFilters().Where(row => row.HostType == ratingType).Select(row => row.HostId), ratings);
        await AddCountsAsync(db.TagApplications.IgnoreQueryFilters().Where(row => row.HostType == affinityType).Select(row => row.HostId), other);
        await AddCountsAsync(db.TagApplications.IgnoreQueryFilters()
            .Where(row => row.ContextType != null
                && row.ContextType.ToLower() == entityKind
                && row.ContextId != null)
            .Select(row => row.ContextId!.Value), other);
        await AddCountsAsync(db.FieldProvenance.IgnoreQueryFilters().Where(row => row.HostType == affinityType).Select(row => row.HostId), other);
        await AddCountsAsync(db.UserBookmarks.IgnoreQueryFilters().Where(row => row.HostType == affinityType).Select(row => row.HostId), other);
        await AddCountsAsync(db.UserEntityAffinities.IgnoreQueryFilters().Where(row => row.HostType == affinityType).Select(row => row.HostId), other);
        await AddCountsAsync(db.Interactions.IgnoreQueryFilters().Where(row => row.HostType == interactionType).Select(row => row.HostId), other);
        await AddCountsAsync(db.PlaybackSessions.IgnoreQueryFilters().Where(row => row.HostType == interactionType).Select(row => row.HostId), other);
        await AddCountsAsync(db.PlaybackSessions.IgnoreQueryFilters().Where(row => row.ParentHostType == interactionType && row.ParentHostId != null).Select(row => row.ParentHostId!.Value), other);
        await AddCountsAsync(db.PlaybackSessions.IgnoreQueryFilters().Where(row => row.ItemHostType == interactionType && row.ItemHostId != null).Select(row => row.ItemHostId!.Value), other);
        await AddCountsAsync(db.PlaybackIntervals.IgnoreQueryFilters().Where(row => row.HostType == interactionType).Select(row => row.HostId), other);
        await AddCountsAsync(db.PlaybackIntervals.IgnoreQueryFilters().Where(row => row.ParentHostType == interactionType && row.ParentHostId != null).Select(row => row.ParentHostId!.Value), other);
        await AddCountsAsync(db.PlaybackIntervals.IgnoreQueryFilters().Where(row => row.ItemHostType == interactionType && row.ItemHostId != null).Select(row => row.ItemHostId!.Value), other);
        await AddCountsAsync(db.UserSessions.IgnoreQueryFilters().Where(row => row.LastHostType == interactionType && row.LastHostId != null).Select(row => row.LastHostId!.Value), other);
        await AddCountsAsync(db.ScrapeAttempts.IgnoreQueryFilters()
            .Where(row => row.EntityType.ToLower() == entityKind && row.EntityId != null)
            .Select(row => row.EntityId!.Value), other);
        await AddCountsAsync(db.Segments.IgnoreQueryFilters()
            .Where(row => row.Kind != null
                && row.Kind.ToLower() == entityKind
                && row.RefId != null
                && row.RefId >= int.MinValue
                && row.RefId <= int.MaxValue)
            .Select(row => (int)row.RefId!.Value), other);
        await AddCountsAsync(db.Detections.IgnoreQueryFilters()
            .Where(row => row.RefKind != null
                && row.RefKind.ToLower() == entityKind
                && row.RefId != null
                && row.RefId >= int.MinValue
                && row.RefId <= int.MaxValue)
            .Select(row => (int)row.RefId!.Value), other);

        if (entityType == NameConflictEntityTypes.Performer)
        {
            await AddCountsAsync(db.Embeddings.IgnoreQueryFilters()
                .Where(row => row.HostType == EmbeddingHostType.Performer)
                .Select(row => row.HostId), other);
            await AddCountsAsync(db.AiRuns.IgnoreQueryFilters()
                .Where(row => row.TargetType == AiRunTargetType.Performer)
                .Select(row => row.TargetId), other);
        }

        var idStrings = ids.Select(id => id.ToString()).ToArray();
        var roleOverrideCounts = await db.RoleEntityOverrides
            .IgnoreQueryFilters()
            .Where(row => row.EntityKind.ToLower() == entityKind && idStrings.Contains(row.EntityId))
            .GroupBy(row => row.EntityId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToListAsync(ct);
        foreach (var count in roleOverrideCounts)
            if (int.TryParse(count.Key, out var id) && other.ContainsKey(id))
                other[id] += count.Count;

        var customEntityType = entityType;
        await AddCountsAsync(db.CustomFieldValues.IgnoreQueryFilters()
            .Where(row => row.EntityType.ToLower() == customEntityType)
            .Select(row => row.EntityId), other);
        var referenceDefinitionIds = await db.CustomFieldDefinitions
            .IgnoreQueryFilters()
            .Where(definition => definition.Type.ToLower() == customEntityType)
            .Select(definition => definition.Id)
            .ToArrayAsync(ct);
        await AddCountsAsync(db.CustomFieldValues.IgnoreQueryFilters()
            .Where(row => referenceDefinitionIds.Contains(row.DefinitionId) && row.IntegerValue != null)
            .Select(row => row.IntegerValue!.Value), other);

        void AddJsonReferences(string? json, bool isEntityFilter = false, bool rootIsIdArray = false)
        {
            foreach (var entityId in EntityReferenceJsonRewriter.FindIds(entityType, json, isEntityFilter, rootIsIdArray))
                if (other.ContainsKey(entityId))
                    other[entityId]++;
        }

        var fieldProvenanceValues = await db.FieldProvenance.IgnoreQueryFilters().AsNoTracking()
            .Where(row => row.ValueJson != null)
            .Select(row => new { row.FieldKey, row.ValueJson })
            .ToListAsync(ct);
        foreach (var row in fieldProvenanceValues)
            foreach (var entityId in EntityReferenceJsonRewriter.FindFieldProvenanceIds(entityType, row.FieldKey, row.ValueJson))
                if (other.ContainsKey(entityId))
                    other[entityId]++;

        var interactionMetadata = await db.Interactions.IgnoreQueryFilters().AsNoTracking()
            .Where(row => row.Meta != null)
            .Select(row => row.Meta)
            .ToListAsync(ct);
        foreach (var metadata in interactionMetadata)
            AddJsonReferences(metadata?.RootElement.GetRawText());

        var playbackSessionContexts = await db.PlaybackSessions.IgnoreQueryFilters().AsNoTracking()
            .Where(row => row.Context != null)
            .Select(row => row.Context)
            .ToListAsync(ct);
        foreach (var context in playbackSessionContexts)
            AddJsonReferences(context?.RootElement.GetRawText());

        var playbackIntervalContexts = await db.PlaybackIntervals.IgnoreQueryFilters().AsNoTracking()
            .Where(row => row.Context != null)
            .Select(row => row.Context)
            .ToListAsync(ct);
        foreach (var context in playbackIntervalContexts)
            AddJsonReferences(context?.RootElement.GetRawText());

        var segmentPayloads = await db.Segments.IgnoreQueryFilters().AsNoTracking()
            .Where(row => row.Payload != null)
            .Select(row => row.Payload)
            .ToListAsync(ct);
        foreach (var payload in segmentPayloads)
            AddJsonReferences(payload?.RootElement.GetRawText());

        var savedFilters = await db.SavedFilters.IgnoreQueryFilters().AsNoTracking()
            .Where(row => row.ObjectFilter != null)
            .Select(row => new { row.Mode, row.ObjectFilter })
            .ToListAsync(ct);
        foreach (var filter in savedFilters)
            AddJsonReferences(
                filter.ObjectFilter,
                filter.Mode.Equals(entityKind, StringComparison.OrdinalIgnoreCase)
                    || filter.Mode.Equals($"{entityKind}s", StringComparison.OrdinalIgnoreCase));

        var userPreferences = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(row => row.UiPreferencesJson != null)
            .Select(row => row.UiPreferencesJson)
            .ToListAsync(ct);
        foreach (var preferences in userPreferences)
            foreach (var entityId in EntityReferenceJsonRewriter.FindUserUiPreferenceIds(entityType, preferences))
                if (other.ContainsKey(entityId))
                    other[entityId]++;

        var groupQueries = await db.Groups.IgnoreQueryFilters().AsNoTracking()
            .Where(row => row.QueryJson != null)
            .Select(row => row.QueryJson)
            .ToListAsync(ct);
        foreach (var query in groupQueries)
            AddJsonReferences(query);

        var groupItemQueries = await db.GroupItems.IgnoreQueryFilters().AsNoTracking()
            .Where(row => row.SourceQueryJson != null)
            .Select(row => row.SourceQueryJson)
            .ToListAsync(ct);
        foreach (var query in groupItemQueries)
            AddJsonReferences(query);

        var contentRules = await db.RoleContentRules.IgnoreQueryFilters().AsNoTracking()
            .Where(row => row.ScopeValue != null)
            .Select(row => new { row.EntityKind, row.ScopeKind, row.ScopeValue })
            .ToListAsync(ct);
        foreach (var rule in contentRules)
            foreach (var entityId in EntityReferenceJsonRewriter.FindRoleContentIds(
                entityType,
                rule.EntityKind,
                rule.ScopeKind,
                rule.ScopeValue))
                if (other.ContainsKey(entityId))
                    other[entityId]++;

        var shareLinks = await db.ShareLinks.IgnoreQueryFilters().AsNoTracking()
            .Where(row => row.EntityKind.ToLower() == entityKind)
            .Select(row => row.EntityIds)
            .ToListAsync(ct);
        foreach (var entityIds in shareLinks)
            AddJsonReferences(entityIds, rootIsIdArray: true);

        if (entityType == NameConflictEntityTypes.Performer)
        {
            var faceAssignments = await db.ExtensionData.IgnoreQueryFilters().AsNoTracking()
                .Where(row => row.ExtensionId == FacePerformerAssignmentData.ExtensionId
                    && row.Key.StartsWith(FacePerformerAssignmentData.KeyPrefix))
                .Select(row => new { row.Key, row.Value })
                .ToListAsync(ct);
            foreach (var row in faceAssignments)
            {
                var rowIds = new HashSet<int>(EntityReferenceJsonRewriter.FindIds(entityType, row.Value));
                if (FacePerformerAssignmentData.TryParseKey(row.Key, out var assignment))
                    rowIds.Add(assignment.PerformerId);
                foreach (var entityId in rowIds)
                    if (other.ContainsKey(entityId))
                        other[entityId]++;
            }
        }

        if (externalReferenceInspector != null)
        {
            externalReferences = await externalReferenceInspector.InspectAsync(entityType, ids, ct);
            foreach (var reference in externalReferences)
                if (extension.ContainsKey(reference.EntityId))
                    extension[reference.EntityId] = checked(extension[reference.EntityId] + (reference.RowCount ?? 0));
        }

        var byId = rows.ToDictionary(row => row.Id);
        return ids.ToDictionary(id => id, id =>
        {
            var referenceCount = (long)linked[id]
                + groups[id]
                + hierarchy[id]
                + faces[id]
                + ratings[id]
                + other[id]
                + extension[id];
            var row = byId[id];
            return new EntityNameImpactDto(
                id,
                row.Name,
                row.Disambiguation,
                linked[id],
                groups[id],
                hierarchy[id],
                faces[id],
                ratings[id],
                other[id],
                extension[id],
                externalReferences.Where(reference => reference.EntityId == id).ToArray(),
                referenceCount);
        });
    }

    private static EntityNameImpactDto EmptyImpact(IdentityRow row)
        => new(row.Id, row.Name, row.Disambiguation, 0, 0, 0, 0, 0, 0, 0, [], 0);
}
