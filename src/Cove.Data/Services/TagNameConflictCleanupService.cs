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
    BlobReferenceTransactionCoordinator? blobReferenceTransactions = null)
{
    private const int ResolveAllSafetyLimit = 10_000;
    private sealed record PlannedAction(string Action, string? NewValue = null);
    private sealed record ResolveOutcome(TagMergeResult? Merge, TagNameConflictScanDto Scan);
    private sealed record ResolveAllOutcome(IReadOnlyList<TagMergeResult> Merges, TagNameConflictScanDto Scan);

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
    {
        var outcome = await ExecuteTransactionAsync(async () =>
        {
            var scan = await scanner.ScanAsync(ct);
            var group = scan.Groups.SingleOrDefault(candidate => candidate.Key == groupKey)
                ?? throw new InvalidOperationException("The conflict group changed. Refresh the scan and try again.");
            if (expectedRevision != null
                && !string.Equals(group.Revision, expectedRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("The conflict group changed. Refresh the scan and review its claims before trying again.");
            var merge = await ResolveGroupAsync(group, survivorTagId, resolutions, ct);
            return new ResolveOutcome(merge, await scanner.ScanAsync(ct));
        }, ct);

        if (outcome.Merge != null)
            mergeService.PublishCompletedMerge(outcome.Merge);
        return outcome.Scan;
    }

    public async Task<TagNameConflictScanDto> ResolveAllRecommendedAsync(CancellationToken ct = default)
        => await ResolveAllRecommendedAsync(null, ct);

    public async Task<TagNameConflictScanDto> ResolveAllRecommendedAsync(
        string? expectedRevision,
        CancellationToken ct = default)
    {
        var outcome = await ExecuteTransactionAsync(async () =>
        {
            var attemptMerges = new List<TagMergeResult>();
            var scan = await scanner.ScanAsync(ct);
            if (expectedRevision != null
                && !string.Equals(scan.Revision, expectedRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("The conflict scan changed. Refresh it and review all recommended actions before trying again.");

            for (var operation = 0; operation < ResolveAllSafetyLimit; operation++)
            {
                var group = scan.Groups.FirstOrDefault();
                if (group == null)
                    return new ResolveAllOutcome(attemptMerges, await scanner.ScanAsync(ct));

                var merge = await ResolveGroupAsync(group, group.RecommendedSurvivorTagId, null, ct);
                if (merge != null)
                    attemptMerges.Add(merge);
                scan = await scanner.ScanAsync(ct);
            }

            throw new InvalidOperationException("Conflict cleanup did not converge before the safety limit.");
        }, ct);

        foreach (var merge in outcome.Merges)
            mergeService.PublishCompletedMerge(merge);
        return outcome.Scan;
    }

    private async Task<TagMergeResult?> ResolveGroupAsync(
        TagNameConflictGroupDto group,
        int? requestedSurvivorTagId,
        IReadOnlyCollection<TagNameClaimResolutionDto>? requestedResolutions,
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
