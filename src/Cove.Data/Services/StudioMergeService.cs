using System.Data;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Services;

public sealed record StudioMergeResult(int TargetId, IReadOnlyList<int> MergedSourceIds);

/// <summary>
/// Authoritative studio relationship-transfer implementation. Direct media and hierarchy references,
/// intrinsic metadata, list relationships, Cove-owned polymorphic metadata, and extension safeguards
/// are handled transactionally before source studios are deleted.
/// </summary>
public sealed class StudioMergeService(
    CoveContext db,
    IEventBus? eventBus = null,
    IEntityExternalReferenceInspector? externalReferenceInspector = null,
    BlobReferenceTransactionCoordinator? blobReferenceTransactions = null)
{
    public async Task<StudioMergeResult> MergeAsync(
        int targetId,
        IReadOnlyCollection<int> sourceIds,
        CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction != null)
            return await MergeWithinTransactionAsync(targetId, sourceIds, bypassStudioVisibility: false, ct);

        StudioMergeResult? result = null;
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
                result = await MergeWithinTransactionAsync(targetId, sourceIds, bypassStudioVisibility: false, ct);
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

    internal async Task<StudioMergeResult> MergeWithinTransactionAsync(
        int targetId,
        IReadOnlyCollection<int> sourceIds,
        bool bypassStudioVisibility,
        CancellationToken ct = default)
    {
        var requestedSourceIds = sourceIds
            .Where(id => id > 0 && id != targetId)
            .Distinct()
            .Order()
            .ToArray();
        var requestedIds = requestedSourceIds.Append(targetId).Distinct().ToArray();
        var query = bypassStudioVisibility ? db.Studios.IgnoreQueryFilters() : db.Studios;
        var studios = await query
            .Where(studio => requestedIds.Contains(studio.Id))
            .OrderBy(studio => studio.Id)
            .ToListAsync(ct);
        var target = studios.SingleOrDefault(studio => studio.Id == targetId)
            ?? throw new KeyNotFoundException($"Target studio {targetId} was not found.");
        var sources = studios.Where(studio => studio.Id != targetId).OrderBy(studio => studio.Id).ToArray();
        if (sources.Length == 0)
            return new StudioMergeResult(targetId, []);

        using var authorizationFilterSuppression = db.SuppressAuthorizationFilters();
        using var entityNameValidationSuppression = db.SuppressEntityNameValidation();
        var mergedSourceIds = sources.Select(source => source.Id).ToArray();
        var allIds = mergedSourceIds.Append(targetId).ToArray();

        await EnsureNoExternalReferencesAsync(mergedSourceIds, ct);
        MergeIntrinsicMetadata(target, sources);
        await TransferDirectStudioReferencesAsync(target, sources, allIds, ct);
        await TransferAliasesAsync(target, sources, allIds, ct);
        await TransferUrlsAsync(targetId, allIds, ct);
        await TransferRemoteIdsAsync(targetId, allIds, ct);
        await TransferTagsAsync(targetId, allIds, ct);
        await db.SaveChangesAsync(ct);

        await new EntityMergeMetadataService(db).TransferAsync(
            NameConflictEntityTypes.Studio,
            targetId,
            mergedSourceIds,
            ct);
        await db.SaveChangesAsync(ct);

        db.Studios.RemoveRange(sources);
        await db.SaveChangesAsync(ct);
        return new StudioMergeResult(targetId, mergedSourceIds);
    }

    internal void PublishCompletedMerge(StudioMergeResult result)
    {
        if (result.MergedSourceIds.Count == 0)
            return;
        eventBus?.Publish(new EntityEvent(EventType.StudioUpdated, "Studio", result.TargetId));
        foreach (var sourceId in result.MergedSourceIds)
            eventBus?.Publish(new EntityEvent(EventType.StudioDeleted, "Studio", sourceId));
    }

    private async Task EnsureNoExternalReferencesAsync(int[] sourceIds, CancellationToken ct)
    {
        if (externalReferenceInspector == null || sourceIds.Length == 0)
            return;
        var references = await externalReferenceInspector.InspectAsync(NameConflictEntityTypes.Studio, sourceIds, ct);
        if (references.Count == 0)
            return;
        throw new EntityMergeBlockedException(
            NameConflictEntityTypes.Studio,
            references.Sum(reference => reference.RowCount ?? 0),
            references.Select(reference => reference.EntityId).Distinct().Count(),
            references.Any(reference => reference.AccessLimitation != null));
    }

    private static void MergeIntrinsicMetadata(Studio target, IReadOnlyList<Studio> sources)
    {
        static string? FirstText(string? targetValue, IEnumerable<string?> sourceValues)
            => !string.IsNullOrWhiteSpace(targetValue)
                ? targetValue
                : sourceValues.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        target.Details = FirstText(target.Details, sources.Select(source => source.Details));
        target.ImageBlobId = FirstText(target.ImageBlobId, sources.Select(source => source.ImageBlobId));
        target.ImageOverrideBlobId = FirstText(target.ImageOverrideBlobId, sources.Select(source => source.ImageOverrideBlobId));
        target.SearchText = FirstText(target.SearchText, sources.Select(source => source.SearchText));
        target.Favorite |= sources.Any(source => source.Favorite);
        target.Organized |= sources.Any(source => source.Organized);
    }

    private async Task TransferDirectStudioReferencesAsync(
        Studio target,
        IReadOnlyList<Studio> sources,
        int[] allIds,
        CancellationToken ct)
    {
        var videos = await db.Videos.Where(row => row.StudioId != null && allIds.Contains(row.StudioId.Value)).ToListAsync(ct);
        foreach (var row in videos)
            row.StudioId = target.Id;
        var images = await db.Images.Where(row => row.StudioId != null && allIds.Contains(row.StudioId.Value)).ToListAsync(ct);
        foreach (var row in images)
            row.StudioId = target.Id;
        var galleries = await db.Galleries.Where(row => row.StudioId != null && allIds.Contains(row.StudioId.Value)).ToListAsync(ct);
        foreach (var row in galleries)
            row.StudioId = target.Id;
        var groups = await db.Groups.Where(row => row.StudioId != null && allIds.Contains(row.StudioId.Value)).ToListAsync(ct);
        foreach (var row in groups)
            row.StudioId = target.Id;
        var audios = await db.Audios.Where(row => row.StudioId != null && allIds.Contains(row.StudioId.Value)).ToListAsync(ct);
        foreach (var row in audios)
            row.StudioId = target.Id;
        var texts = await db.TextDocuments.Where(row => row.StudioId != null && allIds.Contains(row.StudioId.Value)).ToListAsync(ct);
        foreach (var row in texts)
            row.StudioId = target.Id;

        var hierarchyRows = await db.Studios
            .Where(studio => studio.Id == target.Id
                || studio.ParentId != null && allIds.Contains(studio.ParentId.Value))
            .ToListAsync(ct);
        var externalParentCandidates = new[] { target.ParentId }
            .Concat(sources.Select(source => source.ParentId))
            .Where(parentId => parentId != null && !allIds.Contains(parentId.Value))
            .Select(parentId => parentId!.Value)
            .Distinct()
            .ToArray();
        target.ParentId = externalParentCandidates.FirstOrDefault() is var parentId && parentId > 0
            ? parentId
            : null;
        foreach (var child in hierarchyRows.Where(studio => studio.Id != target.Id && !allIds.Contains(studio.Id)))
            child.ParentId = target.Id;

        // Merging an ancestor into its descendant can make the chosen target its own indirect parent.
        // Validate the proposed target chain against the remapped direct children and break only the
        // target's parent link if that would create a cycle.
        if (target.ParentId != null)
        {
            var parentById = await db.Studios.IgnoreQueryFilters().AsNoTracking()
                .Select(studio => new { studio.Id, studio.ParentId })
                .ToDictionaryAsync(studio => studio.Id, studio => studio.ParentId, ct);
            foreach (var child in hierarchyRows.Where(studio => studio.Id != target.Id && !allIds.Contains(studio.Id)))
                parentById[child.Id] = target.Id;
            var seen = new HashSet<int>();
            var cursor = target.ParentId;
            while (cursor is int current && seen.Add(current) && parentById.TryGetValue(current, out cursor))
            {
                if (current == target.Id || cursor == target.Id)
                {
                    target.ParentId = null;
                    break;
                }
            }
        }
    }

    private async Task TransferAliasesAsync(
        Studio target,
        IReadOnlyList<Studio> sources,
        int[] allIds,
        CancellationToken ct)
    {
        var aliases = await db.Set<StudioAlias>()
            .Where(alias => allIds.Contains(alias.StudioId))
            .OrderBy(alias => alias.StudioId == target.Id ? 0 : 1)
            .ThenBy(alias => alias.Id)
            .ToListAsync(ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var targetNameKey = EntityNameRules.NameKey(target.Name);
        var replacements = new List<string>();
        foreach (var value in aliases.Select(alias => alias.Alias).Concat(sources.Select(source => source.Name)))
        {
            var normalized = EntityNameRules.NormalizeDisambiguation(value);
            if (normalized == null)
                continue;
            var key = EntityNameRules.NameKey(normalized);
            if (key == targetNameKey || !seen.Add(key))
                continue;
            replacements.Add(normalized);
        }
        db.Set<StudioAlias>().RemoveRange(aliases);
        foreach (var value in replacements)
            db.Set<StudioAlias>().Add(new StudioAlias { StudioId = target.Id, Alias = value });
    }

    private async Task TransferUrlsAsync(int targetId, int[] allIds, CancellationToken ct)
    {
        var rows = await db.Set<StudioUrl>()
            .Where(row => allIds.Contains(row.StudioId))
            .OrderBy(row => row.StudioId == targetId ? 0 : 1)
            .ThenBy(row => row.Id)
            .ToListAsync(ct);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            row.Url = row.Url.Trim();
            if (row.Url.Length == 0 || !seen.Add(row.Url))
                db.Set<StudioUrl>().Remove(row);
            else
                row.StudioId = targetId;
        }
    }

    private async Task TransferRemoteIdsAsync(int targetId, int[] allIds, CancellationToken ct)
    {
        var rows = await db.Set<StudioRemoteId>()
            .Where(row => allIds.Contains(row.StudioId))
            .OrderBy(row => row.StudioId == targetId ? 0 : 1)
            .ThenBy(row => row.Id)
            .ToListAsync(ct);
        var seen = new HashSet<(string Endpoint, string RemoteId)>();
        foreach (var row in rows)
        {
            if (!seen.Add((row.Endpoint, row.RemoteId)))
                db.Set<StudioRemoteId>().Remove(row);
            else
                row.StudioId = targetId;
        }
    }

    private async Task TransferTagsAsync(int targetId, int[] allIds, CancellationToken ct)
    {
        var links = await db.Set<StudioTag>().Where(link => allIds.Contains(link.StudioId)).ToListAsync(ct);
        var targetTagIds = links.Where(link => link.StudioId == targetId).Select(link => link.TagId).ToHashSet();
        var sourceLinks = links.Where(link => link.StudioId != targetId).ToArray();
        foreach (var tagId in sourceLinks.Select(link => link.TagId).Distinct())
            if (targetTagIds.Add(tagId))
                db.Set<StudioTag>().Add(new StudioTag { StudioId = targetId, TagId = tagId });
        db.Set<StudioTag>().RemoveRange(sourceLinks);
    }
}
