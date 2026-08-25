using System.Data;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Services;

/// <summary>
/// Transactional compatibility cleanup for the future shared tag namespace. Every operation starts
/// from a fresh scan, so stale browser state cannot merge a group whose membership has changed.
/// </summary>
public sealed class TagNameConflictCleanupService(
    CoveContext db,
    TagNameConflictScanner scanner,
    TagMergeService mergeService,
    BlobReferenceTransactionCoordinator? blobReferenceTransactions = null,
    ITagExternalReferenceInspector? externalReferenceInspector = null)
{
    private sealed record PlannedAction(string Action, string? NewValue = null);
    private sealed record ResolveOutcome(TagMergeResult? Merge, TagNameConflictScanDto Scan);
    private sealed record ResolveBatchOutcome(IReadOnlyList<TagMergeResult> Merges, TagNameConflictScanDto Scan);

    public async Task<TagNameConflictScanDto> ResolveAsync(
        string groupKey,
        int? survivorTagId,
        CancellationToken ct = default)
        => await ResolveAsync(groupKey, null, survivorTagId, null, ct);

    public async Task<TagNameConflictScanDto> ResolveAsync(
        string groupKey,
        int? survivorTagId,
        IReadOnlyCollection<TagNameClaimResolutionDto>? resolutions,
        CancellationToken ct = default)
        => await ResolveAsync(groupKey, null, survivorTagId, resolutions, ct);

    public async Task<TagNameConflictScanDto> ResolveAsync(
        string groupKey,
        string? expectedRevision,
        int? survivorTagId,
        IReadOnlyCollection<TagNameClaimResolutionDto>? resolutions,
        CancellationToken ct = default)
        => await ResolveAsync(
            groupKey,
            expectedRevision,
            survivorTagId,
            resolutions,
            null,
            ct);

    public async Task<TagNameConflictScanDto> ResolveAsync(
        string groupKey,
        string? expectedRevision,
        int? survivorTagId,
        IReadOnlyCollection<TagNameClaimResolutionDto>? resolutions,
        IReadOnlyCollection<TagExternalReferenceResolutionDto>? externalReferenceResolutions,
        CancellationToken ct = default)
    {
        var outcome = await ExecuteTransactionAsync(async () =>
        {
            var scan = await scanner.ScanAsync(ct);
            var group = scan.Groups.SingleOrDefault(candidate => candidate.Key == groupKey)
                ?? throw new InvalidOperationException("The conflict group changed. Refresh the scan and try again.");
            if (expectedRevision != null
                && !string.Equals(group.Revision, expectedRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("The conflict group changed. Refresh the scan and review its claims before trying again.");
            var merge = await ResolveGroupAsync(
                group,
                survivorTagId,
                resolutions,
                externalReferenceResolutions,
                ct);
            return new ResolveOutcome(merge, await scanner.ScanAsync(ct));
        }, ct);

        if (outcome.Merge != null)
            mergeService.PublishCompletedMerge(outcome.Merge);
        return outcome.Scan;
    }

    public async Task<TagNameConflictScanDto> ResolveBatchAsync(
        ResolveTagNameConflictBatchDto request,
        CancellationToken ct = default)
    {
        var outcome = await ExecuteTransactionAsync(async () =>
        {
            var scan = await scanner.ScanAsync(ct);
            ValidateBatchRequest(request, scan);

            var merges = new List<TagMergeResult>();
            var groupsByKey = scan.Groups.ToDictionary(group => group.Key, StringComparer.Ordinal);
            foreach (var groupRequest in request.Groups)
            {
                var merge = await ResolveGroupAsync(
                    groupsByKey[groupRequest.GroupKey],
                    groupRequest.SurvivorTagId,
                    groupRequest.Resolutions,
                    groupRequest.ExternalReferenceResolutions,
                    ct);
                if (merge != null)
                    merges.Add(merge);
                db.ChangeTracker.Clear();
            }

            var refreshed = await scanner.ScanAsync(ct);
            if (refreshed.UnresolvedGroupCount != 0)
                throw new InvalidOperationException("The selected batch did not resolve every conflict. No changes were applied; refresh the scan and review the remaining groups.");
            return new ResolveBatchOutcome(merges, refreshed);
        }, ct);

        foreach (var merge in outcome.Merges)
            mergeService.PublishCompletedMerge(merge);
        return outcome.Scan;
    }

    private static void ValidateBatchRequest(
        ResolveTagNameConflictBatchDto request,
        TagNameConflictScanDto scan)
    {
        if (string.IsNullOrWhiteSpace(request.ExpectedRevision)
            || !string.Equals(request.ExpectedRevision, scan.Revision, StringComparison.Ordinal))
            throw new InvalidOperationException("The conflict scan changed. Refresh it and review the selected actions before trying again.");
        if (request.Groups.Count == 0)
            throw new ArgumentException("At least one conflict group is required.", nameof(request));

        var requestedByKey = request.Groups
            .GroupBy(group => group.GroupKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var currentKeys = scan.Groups.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        if (requestedByKey.Any(entry => string.IsNullOrWhiteSpace(entry.Key) || entry.Value.Length != 1)
            || !currentKeys.SetEquals(requestedByKey.Keys))
            throw new InvalidOperationException("The batch must contain every current conflict group exactly once. Refresh the scan and try again.");

        foreach (var group in scan.Groups)
        {
            var groupRequest = requestedByKey[group.Key][0];
            if (string.IsNullOrWhiteSpace(groupRequest.ExpectedRevision)
                || !string.Equals(groupRequest.ExpectedRevision, group.Revision, StringComparison.Ordinal))
                throw new InvalidOperationException("A conflict group changed. Refresh the scan and review its selected actions before trying again.");
            if (groupRequest.SurvivorTagId == null)
                throw new ArgumentException("Every batch group must include an explicit survivor.", nameof(request));

            var survivingClaims = group.Kinds.Contains(TagNameConflictKinds.BlankAlias)
                ? []
                : group.Claims
                    .Where(claim => claim.TagId == groupRequest.SurvivorTagId)
                    .OrderBy(claim => claim.ClaimType == TagNameClaimTypes.CanonicalName ? 0 : 1)
                    .ThenBy(claim => claim.AliasId)
                    .Take(1)
                    .ToHashSet();
            var requiredClaims = group.Claims
                .Where(claim => !survivingClaims.Contains(claim))
                .Select(claim => (claim.TagId, claim.AliasId))
                .ToHashSet();
            var requestedClaims = (groupRequest.Resolutions ?? [])
                .Select(resolution => (resolution.TagId, resolution.AliasId))
                .ToArray();
            if (requestedClaims.Length != requiredClaims.Count
                || requestedClaims.Distinct().Count() != requestedClaims.Length
                || !requiredClaims.SetEquals(requestedClaims))
                throw new ArgumentException("Every batch group must include one explicit resolution for each non-surviving claim.", nameof(request));
        }

        var mergeSourceIds = request.Groups
            .SelectMany(group => group.Resolutions ?? [])
            .Where(resolution => resolution.Action == TagNameConflictActions.MergeTag)
            .Select(resolution => resolution.TagId)
            .ToHashSet();
        var linkedMergeSource = scan.Groups
            .SelectMany(group => group.Claims
                .Where(claim => mergeSourceIds.Contains(claim.TagId))
                .Select(claim => (claim.TagId, group.Key)))
            .Distinct()
            .GroupBy(entry => entry.TagId)
            .FirstOrDefault(group => group.Select(entry => entry.Key).Distinct(StringComparer.Ordinal).Count() > 1);
        if (linkedMergeSource != null)
            throw new ArgumentException(
                "A tag selected for merging participates in more than one conflict group. Resolve those linked groups individually, refresh the scan, and then apply the remaining batch.",
                nameof(request));
    }

    private async Task<TagMergeResult?> ResolveGroupAsync(
        TagNameConflictGroupDto group,
        int? requestedSurvivorTagId,
        IReadOnlyCollection<TagNameClaimResolutionDto>? requestedResolutions,
        IReadOnlyCollection<TagExternalReferenceResolutionDto>? requestedExternalReferenceResolutions,
        CancellationToken ct)
    {
        var ownerIds = group.Claims.Select(claim => claim.TagId).Distinct().Order().ToArray();
        if (ownerIds.Length == 0)
            throw new InvalidOperationException("The conflict group no longer has any tag claims.");

        var policyClaims = group.Claims
            .Select(claim => new TagNamePolicyClaim(claim.TagId, claim.ClaimType, claim.AliasId))
            .ToArray();
        var recommendation = TagNameResolutionPolicy.Recommend(
            policyClaims,
            group.Kinds.Contains(TagNameConflictKinds.BlankAlias),
            requestedSurvivorTagId ?? group.RecommendedSurvivorTagId);
        var claimsByIdentity = group.Claims.ToDictionary(
            claim => new TagNameClaimIdentity(claim.TagId, claim.AliasId));
        var actions = recommendation.Claims.ToDictionary(
            entry => entry.Key,
            entry => new PlannedAction(entry.Value.Action));

        if (requestedResolutions != null)
        {
            var duplicate = requestedResolutions
                .GroupBy(resolution => new TagNameClaimIdentity(resolution.TagId, resolution.AliasId))
                .FirstOrDefault(grouping => grouping.Count() > 1);
            if (duplicate != null)
                throw new ArgumentException("A claim can have only one resolution action.", nameof(requestedResolutions));

            foreach (var requested in requestedResolutions)
            {
                var identity = new TagNameClaimIdentity(requested.TagId, requested.AliasId);
                if (!claimsByIdentity.TryGetValue(identity, out var claim))
                    throw new ArgumentException("A requested claim no longer belongs to this conflict group.", nameof(requestedResolutions));
                if (recommendation.Claims[identity].IsSurvivingClaim)
                    throw new ArgumentException("The surviving claim cannot also be resolved.", nameof(requestedResolutions));

                ValidateRequestedAction(claim, requested);
                actions[identity] = new PlannedAction(requested.Action, requested.NewValue);
            }
        }

        var inconsistentMergeOwner = actions
            .GroupBy(entry => entry.Key.TagId)
            .FirstOrDefault(owner => owner.Any(entry => entry.Value.Action == TagNameConflictActions.MergeTag)
                && owner.Any(entry => entry.Value.Action != TagNameConflictActions.MergeTag));
        if (inconsistentMergeOwner != null)
            throw new ArgumentException(
                "Merging a tag must be selected for every claim owned by that tag.",
                nameof(requestedResolutions));

        var mergeTagIds = actions
            .Where(entry => entry.Value.Action == TagNameConflictActions.MergeTag)
            .Select(entry => entry.Key.TagId)
            .Where(tagId => tagId != recommendation.SurvivorTagId)
            .Distinct()
            .Order()
            .ToArray();
        if (actions.Any(entry => entry.Key.TagId == recommendation.SurvivorTagId
            && entry.Value.Action == TagNameConflictActions.MergeTag))
            throw new ArgumentException("The selected survivor cannot be merged into itself.", nameof(requestedResolutions));

        ValidateCompletePlan(group, recommendation, actions, mergeTagIds);
        var externalReferenceResolutions = ValidateExternalReferenceResolutions(
            group,
            recommendation.SurvivorTagId,
            mergeTagIds,
            requestedExternalReferenceResolutions);

        var survivingTagIds = ownerIds.Except(mergeTagIds).ToArray();
        var survivingTags = await db.Tags
            .IgnoreQueryFilters()
            .Where(tag => survivingTagIds.Contains(tag.Id))
            .ToDictionaryAsync(tag => tag.Id, ct);
        var requestedAliasIds = actions.Keys
            .Where(identity => identity.AliasId != null && !mergeTagIds.Contains(identity.TagId))
            .Select(identity => identity.AliasId!.Value)
            .Distinct()
            .ToArray();
        var aliases = await db.Set<TagAlias>()
            .Where(alias => requestedAliasIds.Contains(alias.Id))
            .ToDictionaryAsync(alias => alias.Id, ct);

        foreach (var (identity, planned) in actions)
        {
            if (mergeTagIds.Contains(identity.TagId))
                continue;

            var claim = claimsByIdentity[identity];
            if (claim.ClaimType == TagNameClaimTypes.CanonicalName)
            {
                if (planned.Action == TagNameConflictActions.Rename)
                    survivingTags[identity.TagId].Name = TagNameRules.NormalizeCanonicalName(planned.NewValue);
                continue;
            }

            if (identity.AliasId is not int aliasId || !aliases.TryGetValue(aliasId, out var alias))
                throw new InvalidOperationException("The conflict group changed. Refresh the scan and try again.");
            if (planned.Action == TagNameConflictActions.RemoveAlias)
                db.Set<TagAlias>().Remove(alias);
            else if (planned.Action == TagNameConflictActions.Rename)
                alias.Alias = TagNameRules.NormalizeAlias(planned.NewValue)!;
        }

        // Apply explicit alias removals and renames before merging. The merge service may rebuild
        // the survivor's alias rows while deduplicating them, so waiting until afterward would make
        // the scanned alias identifiers stale in otherwise valid mixed-action plans.
        await db.SaveChangesAsync(ct);

        if (externalReferenceResolutions.Count > 0)
        {
            if (externalReferenceInspector == null)
                throw new InvalidOperationException("Non-core tag-reference repair is unavailable.");
            await externalReferenceInspector.ApplyResolutionsAsync(
                recommendation.SurvivorTagId,
                externalReferenceResolutions,
                ct);
        }

        TagMergeResult? merge = null;
        if (mergeTagIds.Length > 0)
            merge = await mergeService.MergeWithinTransactionAsync(
                recommendation.SurvivorTagId,
                mergeTagIds,
                bypassTagVisibility: true,
                ct);

        foreach (var tagId in group.Claims
            .Where(claim => claim.ClaimType == TagNameClaimTypes.CanonicalName && !mergeTagIds.Contains(claim.TagId))
            .Select(claim => claim.TagId)
            .Distinct())
            survivingTags[tagId].Name = TagNameRules.NormalizeCanonicalName(survivingTags[tagId].Name);

        var survivingAliasClaim = group.Claims.SingleOrDefault(claim =>
            claim.ClaimType == TagNameClaimTypes.Alias
            && recommendation.Claims[new TagNameClaimIdentity(claim.TagId, claim.AliasId)].IsSurvivingClaim);
        if (survivingAliasClaim != null)
        {
            var namespaceKey = TagNameRules.NamespaceKey(survivingAliasClaim.NormalizedValue!);
            var survivorAliases = await db.Set<TagAlias>()
                .Where(alias => alias.TagId == recommendation.SurvivorTagId)
                .ToListAsync(ct);
            var survivingAlias = survivorAliases.FirstOrDefault(alias =>
                TagNameRules.NormalizeAlias(alias.Alias) is { } normalized
                && TagNameRules.NamespaceKey(normalized) == namespaceKey)
                ?? throw new InvalidOperationException("The conflict group changed. Refresh the scan and try again.");
            survivingAlias.Alias = TagNameRules.NormalizeAlias(survivingAlias.Alias)!;
        }
        await db.SaveChangesAsync(ct);

        var refreshed = await scanner.ScanWithoutImpactsAsync(ct);
        if (refreshed.Groups.Any(candidate => candidate.Key == group.Key))
            throw new InvalidOperationException("The selected actions did not resolve every claim in this conflict group.");

        return merge;
    }

    private static void ValidateRequestedAction(
        TagNameClaimDto claim,
        TagNameClaimResolutionDto requested)
    {
        var allowed = claim.ClaimType == TagNameClaimTypes.CanonicalName
            ? requested.Action is TagNameConflictActions.MergeTag or TagNameConflictActions.Rename
            : requested.Action is TagNameConflictActions.MergeTag or TagNameConflictActions.RemoveAlias or TagNameConflictActions.Rename;
        if (!allowed)
            throw new ArgumentException("The requested action is not valid for this claim.", nameof(requested));

        if (requested.Action != TagNameConflictActions.Rename)
            return;
        if (requested.NewValue == null)
            throw new ArgumentException("A new value is required when renaming a claim.", nameof(requested));
        if (claim.ClaimType == TagNameClaimTypes.Alias && TagNameRules.NormalizeAlias(requested.NewValue) == null)
            throw new ArgumentException("A renamed alias cannot be blank. Remove the alias instead.", nameof(requested));
    }

    private static void ValidateCompletePlan(
        TagNameConflictGroupDto group,
        TagNameResolutionRecommendation recommendation,
        IReadOnlyDictionary<TagNameClaimIdentity, PlannedAction> actions,
        IReadOnlyCollection<int> mergeTagIds)
    {
        foreach (var claim in group.Claims)
        {
            var identity = new TagNameClaimIdentity(claim.TagId, claim.AliasId);
            if (recommendation.Claims[identity].IsSurvivingClaim || mergeTagIds.Contains(claim.TagId))
                continue;

            var action = actions[identity].Action;
            if (claim.ClaimType == TagNameClaimTypes.CanonicalName
                && action != TagNameConflictActions.Rename)
                throw new ArgumentException("Every non-surviving tag name must be merged or renamed.", nameof(actions));
            if (claim.ClaimType == TagNameClaimTypes.Alias
                && action is not TagNameConflictActions.RemoveAlias and not TagNameConflictActions.Rename)
                throw new ArgumentException("Every non-surviving alias must be merged, removed, or renamed.", nameof(actions));
        }
    }

    private static IReadOnlyList<TagExternalReferenceResolutionDto> ValidateExternalReferenceResolutions(
        TagNameConflictGroupDto group,
        int survivorTagId,
        IReadOnlyCollection<int> mergeTagIds,
        IReadOnlyCollection<TagExternalReferenceResolutionDto>? requestedResolutions)
    {
        // Older callers may omit the optional repair list. Only an explicitly reviewed group request
        // may authorize generic database repair.
        if (requestedResolutions == null)
            return [];

        var required = group.Impacts
            .Where(impact => mergeTagIds.Contains(impact.TagId))
            .SelectMany(impact => impact.ExternalReferences)
            .OrderBy(reference => reference.TagId)
            .ThenBy(reference => reference.ReferenceKey, StringComparer.Ordinal)
            .ToArray();
        var requested = requestedResolutions
            .OrderBy(resolution => resolution.TagId)
            .ThenBy(resolution => resolution.ReferenceKey, StringComparer.Ordinal)
            .ToArray();

        if (required.Any(reference => reference.AccessLimitation != null || reference.RowCount == null))
            throw new TagExternalReferenceRepairException(
                "A non-core table cannot be inspected or repaired safely because of row-level security or database permissions. Use the owning extension or a database administrator before merging this tag.");

        if (requested.GroupBy(resolution => (resolution.TagId, resolution.ReferenceKey)).Any(grouping => grouping.Count() > 1))
            throw new ArgumentException(
                "A non-core reference location can have only one repair action per source tag.",
                nameof(requestedResolutions));
        if (requested.Any(resolution => resolution.TagId == survivorTagId
            || !mergeTagIds.Contains(resolution.TagId)))
            throw new ArgumentException(
                "Non-core reference repairs can be selected only for tags being merged.",
                nameof(requestedResolutions));
        if (requested.Any(resolution => resolution.Action is not TagExternalReferenceActions.UpdateToSurvivor
            and not TagExternalReferenceActions.DeleteRows))
            throw new ArgumentException(
                "The requested non-core reference action is not valid.",
                nameof(requestedResolutions));

        var requiredIdentities = required
            .Select(reference => (reference.TagId, reference.ReferenceKey))
            .ToHashSet();
        var requestedIdentities = requested
            .Select(resolution => (resolution.TagId, resolution.ReferenceKey))
            .ToHashSet();
        if (!requiredIdentities.SetEquals(requestedIdentities))
            throw new ArgumentException(
                "Choose an update or delete action for every non-core reference on each tag being merged, then try again.",
                nameof(requestedResolutions));

        return requested;
    }

    private async Task<TResult> ExecuteTransactionAsync<TResult>(Func<Task<TResult>> operation, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction != null)
            return await operation();

        var attempt = 0;
        var executionStrategy = db.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0)
                db.ChangeTracker.Clear();
            var blobReferenceTransaction = blobReferenceTransactions == null
                ? null
                : await blobReferenceTransactions.BeginAsync(db, ct);
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var result = await operation();
                await transaction.CommitAsync(ct);
                if (blobReferenceTransaction != null)
                    await blobReferenceTransaction.CompleteAsync();
                return result;
            }
            finally
            {
                if (blobReferenceTransaction != null)
                    await blobReferenceTransaction.DisposeAsync();
            }
        });
    }
}
