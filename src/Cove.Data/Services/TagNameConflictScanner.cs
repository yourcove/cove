using Cove.Core.DTOs;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Services;

/// <summary>
/// Read-only compatibility scanner for the shared tag-name/alias namespace planned for Cove 1.3.0.
/// Keep grouping and survivor selection here; cleanup and upgrade preflight both consume this result.
/// </summary>
public sealed class TagNameConflictScanner(
    CoveContext db,
    ITagExternalReferenceInspector? externalReferenceInspector = null)
{
    private sealed record Claim(
        int TagId,
        string TagName,
        string ClaimType,
        int? AliasId,
        string OriginalValue,
        string? NormalizedValue,
        string? NamespaceKey,
        bool IsWhitespaceOnlyCanonicalName = false)
    {
        public TagNameClaimIdentity Identity => new(TagId, AliasId);
        public TagNamePolicyClaim ToPolicyClaim() => new(TagId, ClaimType, AliasId);
    }

    private sealed record CountRow(int TagId, int Count);

    private sealed record ConflictGroup(
        string Key,
        string NormalizedName,
        List<string> Kinds,
        List<Claim> Claims);

    public Task<TagNameConflictScanDto> ScanAsync(CancellationToken ct = default)
        => ScanCoreAsync(includeImpacts: true, ct);

    internal Task<TagNameConflictScanDto> ScanWithoutImpactsAsync(CancellationToken ct = default)
        => ScanCoreAsync(includeImpacts: false, ct);

    public async Task<TagNameConflictSummaryDto> ScanSummaryAsync(CancellationToken ct = default)
    {
        var scan = await ScanWithoutImpactsAsync(ct);
        return new TagNameConflictSummaryDto(scan.UnresolvedGroupCount, scan.ScannedAtUtc);
    }

    private async Task<TagNameConflictScanDto> ScanCoreAsync(bool includeImpacts, CancellationToken ct)
    {
        var tags = await db.Tags
            .IgnoreQueryFilters()
            .AsNoTracking()
            .OrderBy(tag => tag.Id)
            // Keep the compatibility scan usable before the 1.3 enforcement migration adds the
            // persisted NamespaceKey column. Every projected field exists at the 1.2 checkpoint.
            .Select(tag => new Tag
            {
                Id = tag.Id,
                Name = tag.Name,
                SortName = tag.SortName,
                Description = tag.Description,
                Color = tag.Color,
                TagGroupId = tag.TagGroupId,
                Favorite = tag.Favorite,
                Organized = tag.Organized,
                MinOccurrenceSec = tag.MinOccurrenceSec,
                MinOccurrencePercent = tag.MinOccurrencePercent,
                ShowAsSegment = tag.ShowAsSegment,
                SegmentColorOverride = tag.SegmentColorOverride,
                SegmentLaneOverride = tag.SegmentLaneOverride,
                ImageBlobId = tag.ImageBlobId,
                ImageOverrideBlobId = tag.ImageOverrideBlobId,
                SearchText = tag.SearchText,
            })
            .ToListAsync(ct);
        var tagNames = tags.ToDictionary(tag => tag.Id, tag => tag.Name);
        var aliases = await db.Set<TagAlias>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .OrderBy(alias => alias.Id)
            .Select(alias => new TagAlias
            {
                Id = alias.Id,
                TagId = alias.TagId,
                Alias = alias.Alias,
            })
            .ToListAsync(ct);

        var claims = new List<Claim>(tags.Count + aliases.Count);
        foreach (var tag in tags)
        {
            var normalized = TagNameRules.NormalizeCanonicalName(tag.Name);
            claims.Add(new Claim(
                tag.Id,
                tag.Name,
                TagNameClaimTypes.CanonicalName,
                null,
                tag.Name,
                normalized,
                TagNameRules.NamespaceKey(normalized),
                string.IsNullOrWhiteSpace(tag.Name)));
        }

        foreach (var alias in aliases)
        {
            if (!tagNames.TryGetValue(alias.TagId, out var tagName))
                continue;

            var normalized = TagNameRules.NormalizeAlias(alias.Alias);
            claims.Add(new Claim(
                alias.TagId,
                tagName,
                TagNameClaimTypes.Alias,
                alias.Id,
                alias.Alias,
                normalized,
                normalized == null ? null : TagNameRules.NamespaceKey(normalized)));
        }

        var groups = new List<ConflictGroup>();
        foreach (var namespaceClaims in claims
            .Where(claim => claim.NamespaceKey != null)
            .GroupBy(claim => claim.NamespaceKey!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var orderedClaims = namespaceClaims
                .OrderBy(claim => claim.TagId)
                .ThenBy(claim => claim.ClaimType == TagNameClaimTypes.CanonicalName ? 0 : 1)
                .ThenBy(claim => claim.AliasId)
                .ToList();
            var ownerIds = orderedClaims.Select(claim => claim.TagId).Distinct().Order().ToArray();
            var canonicalClaims = orderedClaims.Where(claim => claim.ClaimType == TagNameClaimTypes.CanonicalName).ToList();
            var aliasClaims = orderedClaims.Where(claim => claim.ClaimType == TagNameClaimTypes.Alias).ToList();
            var kinds = new List<string>();

            if (canonicalClaims.Select(claim => claim.TagId).Distinct().Count() > 1)
                kinds.Add(TagNameConflictKinds.CanonicalNameCollision);
            if (canonicalClaims.Any(canonical => aliasClaims.Any(alias => alias.TagId != canonical.TagId)))
                kinds.Add(TagNameConflictKinds.NameAliasCollision);
            if (aliasClaims.Select(claim => claim.TagId).Distinct().Count() > 1)
                kinds.Add(TagNameConflictKinds.AliasOwnershipCollision);
            if (canonicalClaims.Any(canonical => aliasClaims.Any(alias => alias.TagId == canonical.TagId)))
                kinds.Add(TagNameConflictKinds.RedundantSelfAlias);
            if (aliasClaims.GroupBy(claim => claim.TagId).Any(group => group.Count() > 1))
                kinds.Add(TagNameConflictKinds.DuplicateAlias);
            if (canonicalClaims.Any(claim => claim.IsWhitespaceOnlyCanonicalName))
                kinds.Add(TagNameConflictKinds.WhitespaceOnlyCanonicalName);
            if (string.Equals(namespaceClaims.Key, TagNameRules.NamespaceKey(TagNameRules.EmptyCanonicalName), StringComparison.Ordinal)
                && ownerIds.Length > 1)
                kinds.Add(TagNameConflictKinds.EmptyNameCollision);

            if (kinds.Count == 0)
                continue;

            var normalizedName = string.Equals(namespaceClaims.Key, TagNameRules.NamespaceKey(TagNameRules.EmptyCanonicalName), StringComparison.Ordinal)
                ? TagNameRules.EmptyCanonicalName
                : canonicalClaims.FirstOrDefault()?.NormalizedValue ?? orderedClaims[0].NormalizedValue!;
            groups.Add(new ConflictGroup(
                TagNameRules.NamespaceGroupKey(namespaceClaims.Key),
                normalizedName,
                kinds,
                orderedClaims));
        }

        var blankAliases = claims
            .Where(claim => claim.ClaimType == TagNameClaimTypes.Alias && claim.NormalizedValue == null)
            .OrderBy(claim => claim.TagId)
            .ThenBy(claim => claim.AliasId)
            .ToList();
        if (blankAliases.Count > 0)
        {
            groups.Add(new ConflictGroup(
                TagNameRules.BlankAliasGroupKey,
                "<blank alias>",
                [TagNameConflictKinds.BlankAlias],
                blankAliases));
        }

        var conflictTagIds = groups
            .SelectMany(group => group.Claims)
            .Select(claim => claim.TagId)
            .Distinct()
            .Order()
            .ToArray();
        var impacts = includeImpacts
            ? await LoadImpactsAsync(tags, conflictTagIds, ct)
            : tags
                .Where(tag => conflictTagIds.Contains(tag.Id))
                .ToDictionary(tag => tag.Id, tag => new TagNameImpactDto(
                    tag.Id,
                    tag.Name,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    [],
                    0));

        var resultGroups = groups
            .OrderBy(group => group.NormalizedName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => CreateGroupDto(group, impacts))
            .ToList();

        var scanRevision = TagNameRules.ConflictScanRevision(resultGroups.Select(group =>
            new TagNameGroupRevision(group.Key, group.Revision)));
        return new TagNameConflictScanDto(resultGroups.Count, DateTime.UtcNow, scanRevision, resultGroups);
    }

    private static TagNameConflictGroupDto CreateGroupDto(
        ConflictGroup group,
        IReadOnlyDictionary<int, TagNameImpactDto> impacts)
    {
        var ownerIds = group.Claims.Select(claim => claim.TagId).Distinct().Order().ToArray();
        var recommendation = TagNameResolutionPolicy.Recommend(
            group.Claims.Select(claim => claim.ToPolicyClaim()).ToArray(),
            isBlankAliasGroup: group.Kinds.Contains(TagNameConflictKinds.BlankAlias),
            referenceCounts: ownerIds.ToDictionary(tagId => tagId, tagId => impacts[tagId].ReferenceCount));
        var revisionClaims = group.Claims.Select(claim => new TagNameRevisionClaim(
            claim.TagId,
            claim.TagName,
            claim.ClaimType,
            claim.AliasId,
            claim.OriginalValue,
            claim.NormalizedValue));
        var revisionExternalReferences = ownerIds
            .SelectMany(tagId => impacts[tagId].ExternalReferences)
            .Select(reference => new TagExternalReferenceRevision(
                reference.TagId,
                reference.ReferenceKey,
                reference.RowCount,
                reference.AccessLimitation));

        return new TagNameConflictGroupDto(
            group.Key,
            TagNameRules.ConflictGroupRevision(
                revisionClaims,
                recommendation.SurvivorTagId,
                revisionExternalReferences),
            group.NormalizedName,
            group.Kinds,
            recommendation.MergeTagIds.Count > 0,
            ownerIds.Length > 1,
            recommendation.SurvivorTagId,
            recommendation.MergeTagIds,
            recommendation.RemoveAliasIds,
            group.Claims.Select(claim => new TagNameClaimDto(
                claim.TagId,
                claim.TagName,
                claim.ClaimType,
                claim.AliasId,
                claim.OriginalValue,
                claim.NormalizedValue,
                recommendation.Claims[claim.Identity].Action,
                recommendation.Claims[claim.Identity].IsSurvivingClaim)).ToList(),
            ownerIds.Select(tagId => impacts[tagId]).ToList());
    }

    private async Task<Dictionary<int, TagNameImpactDto>> LoadImpactsAsync(
        IReadOnlyCollection<Tag> tags,
        int[] tagIds,
        CancellationToken ct)
    {
        var taggedEntities = tagIds.ToDictionary(tagId => tagId, _ => 0);
        var segments = tagIds.ToDictionary(tagId => tagId, _ => 0);
        var parents = tagIds.ToDictionary(tagId => tagId, _ => 0);
        var children = tagIds.ToDictionary(tagId => tagId, _ => 0);
        var ratings = tagIds.ToDictionary(tagId => tagId, _ => 0);
        var otherMetadata = tagIds.ToDictionary(tagId => tagId, _ => 0);
        var extensionMetadata = tagIds.ToDictionary(tagId => tagId, _ => 0);
        IReadOnlyList<TagExternalReferenceDto> externalReferences = [];
        if (tagIds.Length == 0)
            return new Dictionary<int, TagNameImpactDto>();

        async Task AddCountsAsync(IQueryable<int> query, Dictionary<int, int> destination)
        {
            var rows = await query
                .Where(tagId => tagIds.Contains(tagId))
                .GroupBy(tagId => tagId)
                .Select(group => new CountRow(group.Key, group.Count()))
                .ToListAsync(ct);
            foreach (var row in rows)
                destination[row.TagId] += row.Count;
        }

        await AddCountsAsync(db.Set<VideoTag>().IgnoreQueryFilters().Select(link => link.TagId), taggedEntities);
        await AddCountsAsync(db.Set<AudioTag>().IgnoreQueryFilters().Select(link => link.TagId), taggedEntities);
        await AddCountsAsync(db.Set<TextTag>().IgnoreQueryFilters().Select(link => link.TagId), taggedEntities);
        await AddCountsAsync(db.Set<PerformerTag>().IgnoreQueryFilters().Select(link => link.TagId), taggedEntities);
        await AddCountsAsync(db.Set<ImageTag>().IgnoreQueryFilters().Select(link => link.TagId), taggedEntities);
        await AddCountsAsync(db.Set<GalleryTag>().IgnoreQueryFilters().Select(link => link.TagId), taggedEntities);
        await AddCountsAsync(db.Set<StudioTag>().IgnoreQueryFilters().Select(link => link.TagId), taggedEntities);
        await AddCountsAsync(db.Set<GroupTag>().IgnoreQueryFilters().Select(link => link.TagId), taggedEntities);

        await AddCountsAsync(db.Segments.IgnoreQueryFilters().Where(segment => segment.TagId != null).Select(segment => segment.TagId!.Value), segments);
        var segmentPayloads = await db.Segments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(segment => segment.Payload != null)
            .Select(segment => new { segment.TagId, segment.Payload })
            .ToListAsync(ct);
        foreach (var segment in segmentPayloads)
        {
            foreach (var tagId in TagReferenceJsonRewriter.FindTagIds(segment.Payload?.RootElement.GetRawText()))
            {
                if (segments.ContainsKey(tagId) && segment.TagId != tagId)
                    segments[tagId]++;
            }
        }
        await AddCountsAsync(db.Set<TagParent>().IgnoreQueryFilters().Select(relation => relation.ChildId), parents);
        await AddCountsAsync(db.Set<TagParent>().IgnoreQueryFilters().Select(relation => relation.ParentId), children);
        await AddCountsAsync(db.Ratings.IgnoreQueryFilters().Where(rating => rating.HostType == RatingHostType.Tag).Select(rating => rating.HostId), ratings);

        await AddCountsAsync(db.Set<TagAlias>().IgnoreQueryFilters().Select(alias => alias.TagId), otherMetadata);
        await AddCountsAsync(db.Set<TagRemoteId>().IgnoreQueryFilters().Select(remoteId => remoteId.TagId), otherMetadata);
        await AddCountsAsync(db.SegmentDisplayRules.IgnoreQueryFilters().Where(rule => rule.TagId != null).Select(rule => rule.TagId!.Value), otherMetadata);
        await AddCountsAsync(db.TagApplications.IgnoreQueryFilters().Select(application => application.TagId), otherMetadata);
        await AddCountsAsync(db.TagApplications.IgnoreQueryFilters().Where(application => application.HostType == AffinityHostType.Tag).Select(application => application.HostId), otherMetadata);
        await AddCountsAsync(db.CustomFieldValues.IgnoreQueryFilters().Where(value => value.EntityType.ToLower() == CustomFieldEntityTypes.Tag).Select(value => value.EntityId), otherMetadata);
        await AddCountsAsync(
            from value in db.CustomFieldValues.IgnoreQueryFilters()
            join definition in db.CustomFieldDefinitions.IgnoreQueryFilters() on value.DefinitionId equals definition.Id
            where definition.Type.ToLower() == CustomFieldTypes.Tag && value.IntegerValue != null
            select value.IntegerValue!.Value,
            otherMetadata);
        await AddCountsAsync(db.FieldProvenance.IgnoreQueryFilters().Where(value => value.HostType == AffinityHostType.Tag).Select(value => value.HostId), otherMetadata);
        await AddCountsAsync(db.UserBookmarks.IgnoreQueryFilters().Where(value => value.HostType == AffinityHostType.Tag).Select(value => value.HostId), otherMetadata);
        await AddCountsAsync(db.UserEntityAffinities.IgnoreQueryFilters().Where(value => value.HostType == AffinityHostType.Tag).Select(value => value.HostId), otherMetadata);
        await AddCountsAsync(db.Interactions.IgnoreQueryFilters().Where(value => value.HostType == InteractionHostType.Tag).Select(value => value.HostId), otherMetadata);
        await AddCountsAsync(db.PlaybackSessions.IgnoreQueryFilters().Where(value => value.HostType == InteractionHostType.Tag).Select(value => value.HostId), otherMetadata);
        await AddCountsAsync(db.PlaybackSessions.IgnoreQueryFilters().Where(value => value.ParentHostType == InteractionHostType.Tag && value.ParentHostId != null).Select(value => value.ParentHostId!.Value), otherMetadata);
        await AddCountsAsync(db.PlaybackSessions.IgnoreQueryFilters().Where(value => value.ItemHostType == InteractionHostType.Tag && value.ItemHostId != null).Select(value => value.ItemHostId!.Value), otherMetadata);
        await AddCountsAsync(db.PlaybackIntervals.IgnoreQueryFilters().Where(value => value.HostType == InteractionHostType.Tag).Select(value => value.HostId), otherMetadata);
        await AddCountsAsync(db.PlaybackIntervals.IgnoreQueryFilters().Where(value => value.ParentHostType == InteractionHostType.Tag && value.ParentHostId != null).Select(value => value.ParentHostId!.Value), otherMetadata);
        await AddCountsAsync(db.PlaybackIntervals.IgnoreQueryFilters().Where(value => value.ItemHostType == InteractionHostType.Tag && value.ItemHostId != null).Select(value => value.ItemHostId!.Value), otherMetadata);
        await AddCountsAsync(db.GroupItems.IgnoreQueryFilters().Where(value => value.HostType.ToLower() == CustomFieldEntityTypes.Tag).Select(value => value.HostId), otherMetadata);
        await AddCountsAsync(db.UserSessions.IgnoreQueryFilters().Where(value => value.LastHostType == InteractionHostType.Tag && value.LastHostId != null).Select(value => value.LastHostId!.Value), otherMetadata);

        var roleOverrides = await db.RoleEntityOverrides
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(value => value.EntityKind.ToLower() == CustomFieldEntityTypes.Tag)
            .Select(value => value.EntityId)
            .ToListAsync(ct);
        foreach (var entityId in roleOverrides)
            if (int.TryParse(entityId, out var tagId) && otherMetadata.ContainsKey(tagId))
                otherMetadata[tagId]++;

        void AddJsonReferences(string? json, bool isTagFilter = false, bool rootIsTagIdArray = false)
        {
            foreach (var tagId in TagReferenceJsonRewriter.FindTagIds(json, isTagFilter, rootIsTagIdArray))
                if (otherMetadata.ContainsKey(tagId))
                    otherMetadata[tagId]++;
        }

        var fieldProvenanceValues = await db.FieldProvenance
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(row => row.ValueJson != null)
            .Select(row => new { row.HostType, row.FieldKey, row.ValueJson })
            .ToListAsync(ct);
        foreach (var row in fieldProvenanceValues)
            foreach (var tagId in TagReferenceJsonRewriter.FindFieldProvenanceTagIds(
                row.HostType,
                row.FieldKey,
                row.ValueJson))
                if (otherMetadata.ContainsKey(tagId))
                    otherMetadata[tagId]++;

        var interactionMetadata = await db.Interactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(interaction => interaction.Meta != null)
            .Select(interaction => interaction.Meta)
            .ToListAsync(ct);
        foreach (var metadata in interactionMetadata)
            AddJsonReferences(metadata?.RootElement.GetRawText());

        var playbackSessionContexts = await db.PlaybackSessions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(session => session.Context != null)
            .Select(session => session.Context)
            .ToListAsync(ct);
        foreach (var context in playbackSessionContexts)
            AddJsonReferences(context?.RootElement.GetRawText());

        var playbackIntervalContexts = await db.PlaybackIntervals
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(interval => interval.Context != null)
            .Select(interval => interval.Context)
            .ToListAsync(ct);
        foreach (var context in playbackIntervalContexts)
            AddJsonReferences(context?.RootElement.GetRawText());

        var savedFilters = await db.SavedFilters
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(filter => filter.ObjectFilter != null)
            .Select(filter => new { filter.Mode, filter.ObjectFilter })
            .ToListAsync(ct);
        foreach (var filter in savedFilters)
            AddJsonReferences(
                filter.ObjectFilter,
                filter.Mode.Equals(EntityKinds.Tag, StringComparison.OrdinalIgnoreCase)
                    || filter.Mode.Equals("tags", StringComparison.OrdinalIgnoreCase));

        var userUiPreferences = await db.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(user => user.UiPreferencesJson != null)
            .Select(user => user.UiPreferencesJson)
            .ToListAsync(ct);
        foreach (var preferences in userUiPreferences)
            foreach (var tagId in TagReferenceJsonRewriter.FindUserUiPreferenceTagIds(preferences))
                if (otherMetadata.ContainsKey(tagId))
                    otherMetadata[tagId]++;

        var groupQueries = await db.Groups.IgnoreQueryFilters().AsNoTracking()
            .Where(group => group.QueryJson != null)
            .Select(group => group.QueryJson)
            .ToListAsync(ct);
        foreach (var query in groupQueries)
            AddJsonReferences(query);

        var groupItemQueries = await db.GroupItems.IgnoreQueryFilters().AsNoTracking()
            .Where(item => item.SourceQueryJson != null)
            .Select(item => item.SourceQueryJson)
            .ToListAsync(ct);
        foreach (var query in groupItemQueries)
            AddJsonReferences(query);

        var contentRuleScopes = await db.RoleContentRules.IgnoreQueryFilters().AsNoTracking()
            .Select(rule => new { rule.EntityKind, rule.ScopeKind, rule.ScopeValue })
            .ToListAsync(ct);
        foreach (var scope in contentRuleScopes)
            foreach (var tagId in TagReferenceJsonRewriter.FindRoleContentTagIds(
                scope.EntityKind,
                scope.ScopeKind,
                scope.ScopeValue))
                if (otherMetadata.ContainsKey(tagId))
                    otherMetadata[tagId]++;

        var shareLinkEntityIds = await db.ShareLinks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(value => value.EntityKind.ToLower() == CustomFieldEntityTypes.Tag)
            .Select(value => value.EntityIds)
            .ToListAsync(ct);
        foreach (var serializedIds in shareLinkEntityIds)
            AddJsonReferences(serializedIds, rootIsTagIdArray: true);

        if (externalReferenceInspector != null)
        {
            externalReferences = await externalReferenceInspector.InspectAsync(tagIds, ct);
            foreach (var reference in externalReferences)
                if (extensionMetadata.ContainsKey(reference.TagId))
                    extensionMetadata[reference.TagId] = checked(
                        extensionMetadata[reference.TagId] + (reference.RowCount ?? 0));
        }

        var referenceCounts = tagIds.ToDictionary(tagId => tagId, tagId =>
            (long)taggedEntities[tagId]
            + segments[tagId]
            + parents[tagId]
            + children[tagId]
            + ratings[tagId]
            + otherMetadata[tagId]
            + extensionMetadata[tagId]);
        var tagsById = tags.Where(tag => tagIds.Contains(tag.Id)).ToDictionary(tag => tag.Id);
        foreach (var tagId in tagIds)
            otherMetadata[tagId] += CountIntrinsicMetadata(tagsById[tagId]);

        return tagIds.ToDictionary(tagId => tagId, tagId =>
        {
            var tag = tagsById[tagId];
            return new TagNameImpactDto(
                tagId,
                tag.Name,
                taggedEntities[tagId],
                segments[tagId],
                parents[tagId],
                children[tagId],
                ratings[tagId],
                otherMetadata[tagId],
                extensionMetadata[tagId],
                externalReferences.Where(reference => reference.TagId == tagId).ToArray(),
                referenceCounts[tagId]);
        });
    }

    private static int CountIntrinsicMetadata(Tag tag)
    {
        var count = 0;
        if (!string.IsNullOrWhiteSpace(tag.SortName)) count++;
        if (!string.IsNullOrWhiteSpace(tag.Description)) count++;
        if (!string.IsNullOrWhiteSpace(tag.Color)) count++;
        if (tag.TagGroupId != null) count++;
        if (tag.Favorite) count++;
        if (tag.Organized) count++;
        if (tag.MinOccurrenceSec != null) count++;
        if (tag.MinOccurrencePercent != null) count++;
        if (tag.ShowAsSegment != null) count++;
        if (!string.IsNullOrWhiteSpace(tag.SegmentColorOverride)) count++;
        if (tag.SegmentLaneOverride != null) count++;
        if (!string.IsNullOrWhiteSpace(tag.ImageBlobId)) count++;
        if (!string.IsNullOrWhiteSpace(tag.ImageOverrideBlobId)) count++;
        if (!string.IsNullOrWhiteSpace(tag.SearchText)) count++;
        return count;
    }
}
