using System.Data;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Services;

public sealed record PerformerMergeResult(int TargetId, IReadOnlyList<int> MergedSourceIds);

/// <summary>
/// Authoritative performer relationship-transfer implementation. The target name and disambiguation
/// remain its identity; other canonical metadata keeps the target value and fills gaps in ascending
/// source-ID order; list relationships are unioned; Cove-owned
/// polymorphic metadata is transferred by <see cref="EntityMergeMetadataService"/>.
/// </summary>
public sealed class PerformerMergeService(
    CoveContext db,
    IEventBus? eventBus = null,
    IEntityExternalReferenceInspector? externalReferenceInspector = null,
    BlobReferenceTransactionCoordinator? blobReferenceTransactions = null)
    : IPerformerMergeService
{
    public async Task<Performer?> MergeAsync(
        int targetId,
        IReadOnlyCollection<int> sourceIds,
        CancellationToken ct = default)
    {
        PerformerMergeResult result;
        if (db.Database.CurrentTransaction != null)
        {
            result = await MergeWithinTransactionAsync(targetId, sourceIds, bypassPerformerVisibility: false, ct);
        }
        else
        {
            PerformerMergeResult? completed = null;
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
                    completed = await MergeWithinTransactionAsync(targetId, sourceIds, bypassPerformerVisibility: false, ct);
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
            result = completed!;
            PublishCompletedMerge(result);
        }

        return await LoadWithRelationsAsync(result.TargetId, bypassVisibility: false, ct);
    }

    internal async Task<PerformerMergeResult> MergeWithinTransactionAsync(
        int targetId,
        IReadOnlyCollection<int> sourceIds,
        bool bypassPerformerVisibility,
        CancellationToken ct = default)
    {
        var requestedSourceIds = sourceIds
            .Where(id => id > 0 && id != targetId)
            .Distinct()
            .Order()
            .ToArray();
        var requestedIds = requestedSourceIds.Append(targetId).Distinct().ToArray();
        var query = bypassPerformerVisibility ? db.Performers.IgnoreQueryFilters() : db.Performers;
        var performers = await query
            .Where(performer => requestedIds.Contains(performer.Id))
            .OrderBy(performer => performer.Id)
            .ToListAsync(ct);
        var target = performers.SingleOrDefault(performer => performer.Id == targetId);
        if (target == null)
            return new PerformerMergeResult(targetId, []);
        var sources = performers.Where(performer => performer.Id != targetId).OrderBy(performer => performer.Id).ToArray();
        if (sources.Length == 0)
            return new PerformerMergeResult(targetId, []);

        using var authorizationFilterSuppression = db.SuppressAuthorizationFilters();
        using var entityNameValidationSuppression = db.SuppressEntityNameValidation();
        var mergedSourceIds = sources.Select(source => source.Id).ToArray();
        var allIds = mergedSourceIds.Append(targetId).ToArray();

        await EnsureNoExternalReferencesAsync(mergedSourceIds, ct);
        MergeIntrinsicMetadata(target, sources);
        await TransferLinksAsync(targetId, allIds, ct);
        await TransferAliasesAsync(target, sources, allIds, ct);
        await TransferUrlsAsync(targetId, allIds, ct);
        await TransferRemoteIdsAsync(targetId, allIds, ct);
        await TransferFacesAsync(target, mergedSourceIds, ct);
        await TransferFaceSuggestionDecisionsAsync(targetId, allIds, ct);
        await TransferFacePerformerAssignmentsAsync(targetId, allIds, ct);
        await db.SaveChangesAsync(ct);
        await RefreshAudioAndTextPerformerArraysAsync(allIds, ct);

        await new EntityMergeMetadataService(db).TransferAsync(
            NameConflictEntityTypes.Performer,
            targetId,
            mergedSourceIds,
            ct);
        await db.SaveChangesAsync(ct);

        db.Performers.RemoveRange(sources);
        await db.SaveChangesAsync(ct);
        return new PerformerMergeResult(targetId, mergedSourceIds);
    }

    internal void PublishCompletedMerge(PerformerMergeResult result)
    {
        if (result.MergedSourceIds.Count == 0)
            return;
        eventBus?.Publish(new EntityEvent(EventType.PerformerUpdated, "Performer", result.TargetId));
        foreach (var sourceId in result.MergedSourceIds)
            eventBus?.Publish(new EntityEvent(EventType.PerformerDeleted, "Performer", sourceId));
    }

    private async Task EnsureNoExternalReferencesAsync(int[] sourceIds, CancellationToken ct)
    {
        if (externalReferenceInspector == null || sourceIds.Length == 0)
            return;
        var references = await externalReferenceInspector.InspectAsync(NameConflictEntityTypes.Performer, sourceIds, ct);
        if (references.Count == 0)
            return;
        throw new EntityMergeBlockedException(
            NameConflictEntityTypes.Performer,
            references.Sum(reference => reference.RowCount ?? 0),
            references.Select(reference => reference.EntityId).Distinct().Count(),
            references.Any(reference => reference.AccessLimitation != null));
    }

    private static void MergeIntrinsicMetadata(Performer target, IReadOnlyList<Performer> sources)
    {
        static string? FirstText(string? targetValue, IEnumerable<string?> sourceValues)
            => !string.IsNullOrWhiteSpace(targetValue)
                ? targetValue
                : sourceValues.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        target.Gender ??= sources.Select(source => source.Gender).FirstOrDefault(value => value != null);
        if (target.Birthdate is null && sources.FirstOrDefault(source => source.Birthdate.HasValue) is { } birthdateSource)
        {
            target.Birthdate = birthdateSource.Birthdate;
            target.BirthdatePrecision = birthdateSource.BirthdatePrecision;
        }
        if (target.DeathDate is null && sources.FirstOrDefault(source => source.DeathDate.HasValue) is { } deathDateSource)
        {
            target.DeathDate = deathDateSource.DeathDate;
            target.DeathDatePrecision = deathDateSource.DeathDatePrecision;
        }
        target.Ethnicity = FirstText(target.Ethnicity, sources.Select(source => source.Ethnicity));
        target.Country = FirstText(target.Country, sources.Select(source => source.Country));
        target.EyeColor = FirstText(target.EyeColor, sources.Select(source => source.EyeColor));
        target.HairColor = FirstText(target.HairColor, sources.Select(source => source.HairColor));
        target.HeightCm ??= sources.Select(source => source.HeightCm).FirstOrDefault(value => value != null);
        target.Weight ??= sources.Select(source => source.Weight).FirstOrDefault(value => value != null);
        target.Measurements = FirstText(target.Measurements, sources.Select(source => source.Measurements));
        target.FakeTits = FirstText(target.FakeTits, sources.Select(source => source.FakeTits));
        target.PenisLength ??= sources.Select(source => source.PenisLength).FirstOrDefault(value => value != null);
        target.Circumcised ??= sources.Select(source => source.Circumcised).FirstOrDefault(value => value != null);
        if (target.CareerStart is null && sources.FirstOrDefault(source => source.CareerStart.HasValue) is { } careerStartSource)
        {
            target.CareerStart = careerStartSource.CareerStart;
            target.CareerStartPrecision = careerStartSource.CareerStartPrecision;
        }
        if (target.CareerEnd is null && sources.FirstOrDefault(source => source.CareerEnd.HasValue) is { } careerEndSource)
        {
            target.CareerEnd = careerEndSource.CareerEnd;
            target.CareerEndPrecision = careerEndSource.CareerEndPrecision;
        }
        target.Tattoos = FirstText(target.Tattoos, sources.Select(source => source.Tattoos));
        target.Piercings = FirstText(target.Piercings, sources.Select(source => source.Piercings));
        target.Details = FirstText(target.Details, sources.Select(source => source.Details));
        target.ImageBlobId = FirstText(target.ImageBlobId, sources.Select(source => source.ImageBlobId));
        target.ImageOverrideBlobId = FirstText(target.ImageOverrideBlobId, sources.Select(source => source.ImageOverrideBlobId));
        target.SearchText = FirstText(target.SearchText, sources.Select(source => source.SearchText));
        target.Favorite |= sources.Any(source => source.Favorite);
    }

    private async Task TransferLinksAsync(int targetId, int[] allIds, CancellationToken ct)
    {
        var videos = await db.Set<VideoPerformer>().Where(link => allIds.Contains(link.PerformerId)).ToListAsync(ct);
        TransferLinks(videos, targetId, link => link.PerformerId, link => link.VideoId, ownerId => new VideoPerformer { VideoId = ownerId, PerformerId = targetId });
        var images = await db.Set<ImagePerformer>().Where(link => allIds.Contains(link.PerformerId)).ToListAsync(ct);
        TransferLinks(images, targetId, link => link.PerformerId, link => link.ImageId, ownerId => new ImagePerformer { ImageId = ownerId, PerformerId = targetId });
        var galleries = await db.Set<GalleryPerformer>().Where(link => allIds.Contains(link.PerformerId)).ToListAsync(ct);
        TransferLinks(galleries, targetId, link => link.PerformerId, link => link.GalleryId, ownerId => new GalleryPerformer { GalleryId = ownerId, PerformerId = targetId });
        var audios = await db.Set<AudioPerformer>().Where(link => allIds.Contains(link.PerformerId)).ToListAsync(ct);
        TransferLinks(audios, targetId, link => link.PerformerId, link => link.AudioId, ownerId => new AudioPerformer { AudioId = ownerId, PerformerId = targetId });
        var texts = await db.Set<TextPerformer>().Where(link => allIds.Contains(link.PerformerId)).ToListAsync(ct);
        TransferLinks(texts, targetId, link => link.PerformerId, link => link.TextDocumentId, ownerId => new TextPerformer { TextDocumentId = ownerId, PerformerId = targetId });
        var tags = await db.Set<PerformerTag>().Where(link => allIds.Contains(link.PerformerId)).ToListAsync(ct);
        TransferLinks(tags, targetId, link => link.PerformerId, link => link.TagId, ownerId => new PerformerTag { TagId = ownerId, PerformerId = targetId });
    }

    private void TransferLinks<TLink>(
        IReadOnlyCollection<TLink> links,
        int targetId,
        Func<TLink, int> getEntityId,
        Func<TLink, int> getOwnerId,
        Func<int, TLink> createTargetLink)
        where TLink : class
    {
        var targetOwnerIds = links.Where(link => getEntityId(link) == targetId).Select(getOwnerId).ToHashSet();
        var sourceLinks = links.Where(link => getEntityId(link) != targetId).ToArray();
        foreach (var ownerId in sourceLinks.Select(getOwnerId).Distinct())
            if (targetOwnerIds.Add(ownerId))
                db.Set<TLink>().Add(createTargetLink(ownerId));
        db.Set<TLink>().RemoveRange(sourceLinks);
    }

    private async Task TransferAliasesAsync(
        Performer target,
        IReadOnlyList<Performer> sources,
        int[] allIds,
        CancellationToken ct)
    {
        var aliases = await db.Set<PerformerAlias>()
            .Where(alias => allIds.Contains(alias.PerformerId))
            .OrderBy(alias => alias.PerformerId == target.Id ? 0 : 1)
            .ThenBy(alias => alias.Id)
            .ToListAsync(ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var targetNameKey = EntityNameRules.NameKey(target.Name);
        var values = aliases.Select(alias => alias.Alias).Concat(sources.Select(source => source.Name));
        var replacements = new List<string>();
        foreach (var value in values)
        {
            var normalized = EntityNameRules.NormalizeDisambiguation(value);
            if (normalized == null)
                continue;
            var key = EntityNameRules.NameKey(normalized);
            if (key == targetNameKey || !seen.Add(key))
                continue;
            replacements.Add(normalized);
        }
        db.Set<PerformerAlias>().RemoveRange(aliases);
        foreach (var value in replacements)
            db.Set<PerformerAlias>().Add(new PerformerAlias { PerformerId = target.Id, Alias = value });
    }

    private async Task TransferUrlsAsync(int targetId, int[] allIds, CancellationToken ct)
    {
        var rows = await db.Set<PerformerUrl>()
            .Where(row => allIds.Contains(row.PerformerId))
            .OrderBy(row => row.PerformerId == targetId ? 0 : 1)
            .ThenBy(row => row.Id)
            .ToListAsync(ct);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            row.Url = row.Url.Trim();
            if (row.Url.Length == 0 || !seen.Add(row.Url))
                db.Set<PerformerUrl>().Remove(row);
            else
                row.PerformerId = targetId;
        }
    }

    private async Task TransferRemoteIdsAsync(int targetId, int[] allIds, CancellationToken ct)
    {
        var rows = await db.Set<PerformerRemoteId>()
            .Where(row => allIds.Contains(row.PerformerId))
            .OrderBy(row => row.PerformerId == targetId ? 0 : 1)
            .ThenBy(row => row.Id)
            .ToListAsync(ct);
        var seen = new HashSet<(string Endpoint, string RemoteId)>();
        foreach (var row in rows)
        {
            if (!seen.Add((row.Endpoint, row.RemoteId)))
                db.Set<PerformerRemoteId>().Remove(row);
            else
                row.PerformerId = targetId;
        }
    }

    private async Task TransferFacesAsync(Performer target, int[] sourceIds, CancellationToken ct)
    {
        var faces = await db.Faces
            .Where(face => face.PerformerId != null && sourceIds.Contains(face.PerformerId.Value)
                || face.TopSuggestionPerformerId != null && sourceIds.Contains(face.TopSuggestionPerformerId.Value)
                || face.TopSuggestionLocalPerformerId != null && sourceIds.Contains(face.TopSuggestionLocalPerformerId.Value))
            .ToListAsync(ct);
        foreach (var face in faces)
        {
            if (face.PerformerId != null && sourceIds.Contains(face.PerformerId.Value))
                face.PerformerId = target.Id;
            if ((face.TopSuggestionPerformerId != null && sourceIds.Contains(face.TopSuggestionPerformerId.Value))
                || (face.TopSuggestionLocalPerformerId != null && sourceIds.Contains(face.TopSuggestionLocalPerformerId.Value)))
            {
                // This is a materialized projection, not the authoritative suggestion evidence.
                // Source-specific names, URLs, image flags and confidence cannot safely be retargeted;
                // clear the complete projection so the background materializer recomputes it.
                face.TopSuggestionPerformerId = null;
                face.TopSuggestionLocalPerformerId = null;
                face.TopSuggestionPerformerName = null;
                face.TopSuggestionConfidence = null;
                face.TopSuggestionCoverImageUrl = null;
                face.TopSuggestionExternalUrl = null;
                face.TopSuggestionLocalPerformerHasImage = false;
                face.TopSuggestionLocalPerformerIsLocalOnly = false;
                face.TopSuggestionComputedAt = null;
            }
        }
    }

    private async Task TransferFaceSuggestionDecisionsAsync(int targetId, int[] allIds, CancellationToken ct)
    {
        var decisions = await db.FaceSuggestionDecisions
            .Where(decision => allIds.Contains(decision.PerformerId))
            .ToListAsync(ct);
        foreach (var group in decisions.GroupBy(decision => new { decision.FaceId, decision.UserId }))
        {
            var keeper = group
                .OrderByDescending(decision => decision.PerformerId == targetId)
                .ThenBy(decision => decision.Id)
                .First();
            keeper.PerformerId = targetId;
            db.FaceSuggestionDecisions.RemoveRange(group.Where(decision => decision.Id != keeper.Id));
        }
    }

    private async Task TransferFacePerformerAssignmentsAsync(int targetId, int[] allIds, CancellationToken ct)
    {
        var rows = await db.ExtensionData
            .Where(row => row.ExtensionId == FacePerformerAssignmentData.ExtensionId
                && row.Key.StartsWith(FacePerformerAssignmentData.KeyPrefix))
            .ToListAsync(ct);
        var parsed = rows
            .Select(row => FacePerformerAssignmentData.TryParseKey(row.Key, out var assignment)
                ? (Row: row, Assignment: (FacePerformerAssignmentData.Assignment?)assignment)
                : (Row: row, Assignment: null))
            .Where(item => item.Assignment is { } assignment && allIds.Contains(assignment.PerformerId))
            .Select(item => (item.Row, Assignment: item.Assignment!.Value))
            .ToArray();
        var idMap = allIds.ToDictionary(id => id, _ => targetId);

        foreach (var group in parsed.GroupBy(item => FacePerformerAssignmentData.BuildKey(
            item.Assignment with { PerformerId = targetId })))
        {
            var keeper = group
                .OrderByDescending(item => item.Assignment.PerformerId == targetId)
                .ThenBy(item => item.Assignment.PerformerId)
                .ThenBy(item => item.Row.Key, StringComparer.Ordinal)
                .First();
            var rewrittenValue = EntityReferenceJsonRewriter.Rewrite(
                NameConflictEntityTypes.Performer,
                keeper.Row.Value,
                idMap) ?? keeper.Row.Value;
            var updatedAt = group.Max(item => item.Row.UpdatedAt);

            if (keeper.Row.Key == group.Key)
            {
                keeper.Row.Value = rewrittenValue;
                keeper.Row.UpdatedAt = updatedAt;
                db.ExtensionData.RemoveRange(group.Where(item => item.Row != keeper.Row).Select(item => item.Row));
            }
            else
            {
                db.ExtensionData.RemoveRange(group.Select(item => item.Row));
                db.ExtensionData.Add(new ExtensionData
                {
                    ExtensionId = FacePerformerAssignmentData.ExtensionId,
                    Key = group.Key,
                    Value = rewrittenValue,
                    UpdatedAt = updatedAt,
                });
            }
        }
    }

    private async Task RefreshAudioAndTextPerformerArraysAsync(int[] allIds, CancellationToken ct)
    {
        var audioIds = await db.Set<AudioPerformer>()
            .Where(link => allIds.Contains(link.PerformerId))
            .Select(link => link.AudioId)
            .Distinct()
            .ToArrayAsync(ct);
        var audios = await db.Audios.Where(audio => audioIds.Contains(audio.Id)).ToListAsync(ct);
        foreach (var audio in audios)
            audio.PerformerIds = await db.Set<AudioPerformer>()
                .Where(link => link.AudioId == audio.Id)
                .Select(link => link.PerformerId)
                .OrderBy(id => id)
                .ToArrayAsync(ct);

        var textIds = await db.Set<TextPerformer>()
            .Where(link => allIds.Contains(link.PerformerId))
            .Select(link => link.TextDocumentId)
            .Distinct()
            .ToArrayAsync(ct);
        var texts = await db.TextDocuments.Where(text => textIds.Contains(text.Id)).ToListAsync(ct);
        foreach (var text in texts)
            text.PerformerIds = await db.Set<TextPerformer>()
                .Where(link => link.TextDocumentId == text.Id)
                .Select(link => link.PerformerId)
                .OrderBy(id => id)
                .ToArrayAsync(ct);
        if (audios.Count > 0 || texts.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    private Task<Performer?> LoadWithRelationsAsync(int id, bool bypassVisibility, CancellationToken ct)
    {
        var query = bypassVisibility ? db.Performers.IgnoreQueryFilters() : db.Performers;
        return query
            .Include(performer => performer.Urls)
            .Include(performer => performer.Aliases)
            .Include(performer => performer.RemoteIds)
            .Include(performer => performer.PerformerTags)
            .Include(performer => performer.VideoPerformers)
            .Include(performer => performer.ImagePerformers)
            .Include(performer => performer.GalleryPerformers)
            .FirstOrDefaultAsync(performer => performer.Id == id, ct);
    }
}
