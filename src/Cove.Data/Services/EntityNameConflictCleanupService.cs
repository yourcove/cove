using System.Data;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Services;

/// <summary>
/// Transactional executor for performer and studio compatibility conflicts. Every request is checked
/// against a fresh scan revision, performs explicit extension-FK repairs before deletion, and verifies
/// that no affected entity remains in a conflict before commit.
/// </summary>
public sealed class EntityNameConflictCleanupService(
    CoveContext db,
    EntityNameConflictScanner scanner,
    PerformerMergeService performerMergeService,
    StudioMergeService studioMergeService,
    IEntityExternalReferenceInspector? externalReferenceInspector = null,
    BlobReferenceTransactionCoordinator? blobReferenceTransactions = null)
{
    private sealed record ResolveOutcome(
        EntityNameConflictScanDto Scan,
        PerformerMergeResult? PerformerMerge,
        StudioMergeResult? StudioMerge);

    public async Task<EntityNameConflictScanDto> ResolveAsync(
        ResolveEntityNameConflictDto request,
        CancellationToken ct = default)
    {
        ResolveOutcome? outcome = null;
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
                outcome = await ResolveWithinTransactionAsync(request, ct);
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

        if (outcome!.PerformerMerge != null)
            performerMergeService.PublishCompletedMerge(outcome.PerformerMerge);
        if (outcome.StudioMerge != null)
            studioMergeService.PublishCompletedMerge(outcome.StudioMerge);
        return outcome.Scan;
    }

    public async Task<EntityNameConflictScanDto> ResolveBatchAsync(
        ResolveEntityNameConflictBatchDto request,
        CancellationToken ct = default)
    {
        ValidateEntityType(request.EntityType);
        var outcomes = new List<ResolveOutcome>();
        var attempt = 0;
        var executionStrategy = db.Database.CreateExecutionStrategy();
        await executionStrategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0)
            {
                db.ChangeTracker.Clear();
                outcomes.Clear();
            }
            var blobReferenceTransaction = blobReferenceTransactions == null
                ? null
                : await blobReferenceTransactions.BeginAsync(db, ct);
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var scan = await scanner.ScanAsync(request.EntityType, ct);
                ValidateBatchRequest(request, scan);
                foreach (var groupRequest in request.Groups)
                {
                    outcomes.Add(await ResolveWithinTransactionAsync(groupRequest, ct));
                    db.ChangeTracker.Clear();
                }
                if (outcomes[^1].Scan.UnresolvedGroupCount != 0)
                    throw new InvalidOperationException("The selected batch did not resolve every conflict. No changes were applied; refresh the scan and review the remaining groups.");
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

        foreach (var outcome in outcomes)
        {
            if (outcome.PerformerMerge != null)
                performerMergeService.PublishCompletedMerge(outcome.PerformerMerge);
            if (outcome.StudioMerge != null)
                studioMergeService.PublishCompletedMerge(outcome.StudioMerge);
        }
        return outcomes[^1].Scan;
    }

    private static void ValidateBatchRequest(
        ResolveEntityNameConflictBatchDto request,
        EntityNameConflictScanDto scan)
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
            if (!string.Equals(groupRequest.EntityType, request.EntityType, StringComparison.Ordinal)
                || !string.Equals(groupRequest.ExpectedRevision, group.Revision, StringComparison.Ordinal))
                throw new InvalidOperationException("A conflict group changed. Refresh the scan and review its selected actions before trying again.");
            if (groupRequest.SurvivorEntityId == null)
                throw new ArgumentException("Every batch group must include an explicit survivor.", nameof(request));
            var candidateIds = group.Candidates.Select(candidate => candidate.EntityId).ToHashSet();
            var resolutionIds = (groupRequest.Resolutions ?? []).Select(resolution => resolution.EntityId).ToArray();
            if (resolutionIds.Length != candidateIds.Count
                || resolutionIds.Distinct().Count() != resolutionIds.Length
                || !candidateIds.SetEquals(resolutionIds))
                throw new ArgumentException("Every batch group must include one explicit resolution for each candidate.", nameof(request));
        }
    }

    private static void ValidateEntityType(string entityType)
    {
        if (!NameConflictEntityTypes.IsSupported(entityType))
            throw new ArgumentException("The requested entity type does not have a cleanup policy.", nameof(entityType));
    }

    private async Task<ResolveOutcome> ResolveWithinTransactionAsync(
        ResolveEntityNameConflictDto request,
        CancellationToken ct)
    {
        if (!NameConflictEntityTypes.IsSupported(request.EntityType))
            throw new ArgumentException("The requested entity type does not have a cleanup policy.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.GroupKey) || string.IsNullOrWhiteSpace(request.ExpectedRevision))
            throw new ArgumentException("A conflict group key and scanned revision are required.", nameof(request));

        var scan = await scanner.ScanAsync(request.EntityType, ct);
        var group = scan.Groups.SingleOrDefault(candidate => candidate.Key == request.GroupKey)
            ?? throw new InvalidOperationException("The conflict group no longer exists. Refresh the scan.");
        if (!string.Equals(group.Revision, request.ExpectedRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("The conflict group changed. Refresh the scan before resolving it.");

        var candidateIds = group.Candidates.Select(candidate => candidate.EntityId).ToHashSet();
        var survivorId = request.SurvivorEntityId ?? group.RecommendedSurvivorEntityId;
        if (!candidateIds.Contains(survivorId))
            throw new ArgumentException("The selected survivor is not part of this conflict group.", nameof(request));

        var requestedById = (request.Resolutions ?? [])
            .GroupBy(resolution => resolution.EntityId)
            .ToDictionary(grouping => grouping.Key, grouping => grouping.ToArray());
        if (requestedById.Any(entry => !candidateIds.Contains(entry.Key) || entry.Value.Length != 1))
            throw new ArgumentException("Each conflict candidate can have at most one valid resolution.", nameof(request));
        if (requestedById.TryGetValue(survivorId, out var survivorResolution)
            && survivorResolution[0].Action != EntityNameConflictActions.Keep)
            throw new ArgumentException("The selected survivor must be kept.", nameof(request));

        var plans = new List<EntityNameConflictResolutionDto>();
        foreach (var candidate in group.Candidates.Where(candidate => candidate.EntityId != survivorId))
        {
            var resolution = requestedById.TryGetValue(candidate.EntityId, out var requested)
                ? requested[0]
                : new EntityNameConflictResolutionDto(candidate.EntityId, EntityNameConflictActions.MergeEntity);
            if (resolution.Action is not EntityNameConflictActions.MergeEntity and not EntityNameConflictActions.Rename)
                throw new ArgumentException("A non-survivor must be merged or renamed.", nameof(request));
            if (resolution.Action == EntityNameConflictActions.Rename)
                ValidateRename(request.EntityType, resolution);
            plans.Add(resolution);
        }

        var mergeIds = plans
            .Where(plan => plan.Action == EntityNameConflictActions.MergeEntity)
            .Select(plan => plan.EntityId)
            .Order()
            .ToArray();
        await ApplyExternalReferencePlanAsync(
            request.EntityType,
            survivorId,
            mergeIds,
            group,
            request.ExternalReferenceResolutions ?? [],
            ct);

        PerformerMergeResult? performerMerge = null;
        StudioMergeResult? studioMerge = null;
        if (mergeIds.Length > 0)
        {
            if (request.EntityType == NameConflictEntityTypes.Performer)
                performerMerge = await performerMergeService.MergeWithinTransactionAsync(survivorId, mergeIds, true, ct);
            else
                studioMerge = await studioMergeService.MergeWithinTransactionAsync(survivorId, mergeIds, true, ct);
        }

        var renames = plans.Where(plan => plan.Action == EntityNameConflictActions.Rename).ToArray();
        if (renames.Length > 0)
        {
            using var authorizationFilterSuppression = db.SuppressAuthorizationFilters();
            if (request.EntityType == NameConflictEntityTypes.Performer)
            {
                var entities = await db.Performers.IgnoreQueryFilters()
                    .Where(entity => renames.Select(rename => rename.EntityId).Contains(entity.Id))
                    .ToListAsync(ct);
                foreach (var rename in renames)
                {
                    var entity = entities.Single(candidate => candidate.Id == rename.EntityId);
                    entity.Name = EntityNameRules.NormalizeCanonicalName(rename.NewName);
                    entity.Disambiguation = EntityNameRules.NormalizeDisambiguation(rename.NewDisambiguation);
                }
            }
            else
            {
                var entities = await db.Studios.IgnoreQueryFilters()
                    .Where(entity => renames.Select(rename => rename.EntityId).Contains(entity.Id))
                    .ToListAsync(ct);
                foreach (var rename in renames)
                    entities.Single(candidate => candidate.Id == rename.EntityId).Name =
                        EntityNameRules.NormalizeCanonicalName(rename.NewName);
            }
            await db.SaveChangesAsync(ct);
        }

        db.ChangeTracker.Clear();
        var refreshed = await scanner.ScanAsync(request.EntityType, ct);
        var affectedIds = plans.Select(plan => plan.EntityId).Append(survivorId).ToHashSet();
        if (refreshed.Groups.Any(candidate => candidate.Candidates.Any(entity => affectedIds.Contains(entity.EntityId))))
            throw new EntityNameConflictException(request.EntityType);
        return new ResolveOutcome(refreshed, performerMerge, studioMerge);
    }

    private async Task ApplyExternalReferencePlanAsync(
        string entityType,
        int survivorId,
        int[] mergeIds,
        EntityNameConflictGroupDto group,
        IReadOnlyCollection<EntityExternalReferenceResolutionDto> requested,
        CancellationToken ct)
    {
        var required = group.Impacts
            .Where(impact => mergeIds.Contains(impact.EntityId))
            .SelectMany(impact => impact.ExternalReferences)
            .OrderBy(reference => reference.EntityId)
            .ThenBy(reference => reference.ReferenceKey, StringComparer.Ordinal)
            .ToArray();
        if (required.Any(reference => reference.AccessLimitation != null || reference.RowCount == null))
            throw new EntityMergeBlockedException(
                entityType,
                required.Sum(reference => reference.RowCount ?? 0),
                required.Select(reference => reference.EntityId).Distinct().Count(),
                hasUninspectableReferences: true);
        if (required.Length == 0)
        {
            if (requested.Count > 0)
                throw new ArgumentException("No non-core reference repairs are required for the selected merges.", nameof(requested));
            return;
        }
        if (externalReferenceInspector == null)
            throw new InvalidOperationException("Non-core reference repair is unavailable.");

        var requestByIdentity = requested
            .GroupBy(resolution => (resolution.EntityId, resolution.ReferenceKey))
            .ToDictionary(grouping => grouping.Key, grouping => grouping.ToArray());
        if (requestByIdentity.Count != required.Length
            || requestByIdentity.Any(entry => entry.Value.Length != 1)
            || required.Any(reference => !requestByIdentity.ContainsKey((reference.EntityId, reference.ReferenceKey))))
            throw new ArgumentException("Choose update or delete for every non-core reference attached to a merged entity.", nameof(requested));

        await externalReferenceInspector.ApplyResolutionsAsync(entityType, survivorId, requested, ct);
    }

    private static void ValidateRename(string entityType, EntityNameConflictResolutionDto resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution.NewName))
            throw new ArgumentException("A renamed entity must have a nonblank name.", nameof(resolution));
        if (entityType == NameConflictEntityTypes.Studio && resolution.NewDisambiguation != null)
            throw new ArgumentException("Studio identities do not use disambiguation.", nameof(resolution));
    }
}
