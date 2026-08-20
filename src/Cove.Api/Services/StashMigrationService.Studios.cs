using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data.Services;

namespace Cove.Api.Services;

public partial class StashMigrationService
{
    private async Task<Dictionary<int, int>> ImportStudiosAsync(SqliteConnection conn, Dictionary<string, string> blobMap, IJobProgress progress, double startProgress, double endProgress, CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var rows = new List<(int Id, string Name, int? ParentId, string? Details, int? Rating, bool Favorite, string? ImageBlob, string CreatedAt, string UpdatedAt)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, parent_id, details, rating, favorite, image_blob, created_at, updated_at FROM studios";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add((r.GetInt32(0), r.GetString(1), ReadIntNull(r, 2), ReadStringNull(r, 3),
                    ReadIntNull(r, 4), ReadBool(r, 5), ReadStringNull(r, 6), r.GetString(7), r.GetString(8)));
        }
        var urls = await ReadUrlsAsync(conn, "studio_urls", "studio_id", ct);
        var aliases = await ReadAliasesAsync(conn, "studio_aliases", "studio_id", ct);

        var studioStashIds = new Dictionary<int, List<(string Ep, string Rid)>>();
        if (await TableExistsAsync(conn, "studio_stash_ids", ct))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT studio_id, endpoint, stash_id FROM studio_stash_ids";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var sId = r.GetInt32(0);
                if (!studioStashIds.TryGetValue(sId, out var list)) studioStashIds[sId] = list = [];
                list.Add((r.GetString(1), r.GetString(2)));
            }
        }

        _logger.LogDebug("Preparing to import {Total} studios", rows.Count);
        progress.Report(startProgress, "Importing studios...");
        _logger.LogDebug(
            "[StashTiming] phase=studios checkpoint=loaded rows={Rows} urlOwners={UrlOwners} aliasOwners={AliasOwners} remoteIdOwners={RemoteIdOwners} elapsedMs={ElapsedMilliseconds:F0}",
            rows.Count,
            urls.Count,
            aliases.Count,
            studioStashIds.Count,
            stopwatch.Elapsed.TotalMilliseconds);

        var byId = rows.ToDictionary(row => row.Id);
        var identityKeys = rows.Select(row => EntityNameRules.StudioIdentityKey(row.Name)).ToHashSet(StringComparer.Ordinal);
        var existingCandidates = await _db.Studios
            .Include(studio => studio.Urls)
            .Include(studio => studio.Aliases)
            .Include(studio => studio.RemoteIds)
            .OrderBy(studio => studio.Id)
            .ToListAsync(ct);
        var existingByIdentity = new Dictionary<string, Studio>(StringComparer.Ordinal);
        foreach (var existing in existingCandidates)
        {
            var identityKey = EntityNameRules.StudioIdentityKey(existing.Name);
            if (!identityKeys.Contains(identityKey))
                continue;
            if (!existingByIdentity.TryAdd(identityKey, existing))
                throw new EntityNameConflictException(NameConflictEntityTypes.Studio);
        }

        var groups = rows
            .OrderBy(row => row.Id)
            .GroupBy(row => EntityNameRules.StudioIdentityKey(row.Name), StringComparer.Ordinal)
            .Select(group => (IdentityKey: group.Key, StashIds: (IReadOnlyList<int>)group.Select(row => row.Id).ToArray()))
            .ToArray();
        var idMap = new Dictionary<int, int>(rows.Count);
        const int StudioBatchSize = 500;
        var pendingStudios = new List<(IReadOnlyList<int> StashIds, Studio Entity)>();
        var pendingSourceCount = 0;

        async Task FlushStudioBatchAsync()
        {
            if (pendingStudios.Count == 0)
                return;

            try
            {
                _db.ChangeTracker.DetectChanges();
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                // RunMigrationPhaseAsync records the terminal phase failure. Keep this local context
                // diagnostic-only so a failed save does not emit the same exception at Error twice.
                _logger.LogDebug(
                    ex,
                    "Failed importing studio batch containing {StashStudioCount} records",
                    pendingStudios.Count);
                throw;
            }

            foreach (var (stashIds, entity) in pendingStudios)
                foreach (var stashId in stashIds)
                    idMap[stashId] = entity.Id;

            pendingStudios.Clear();
            pendingSourceCount = 0;
            _db.ChangeTracker.Clear();

            ReportPhase(progress, startProgress, endProgress, idMap.Count, rows.Count, $"Importing studios ({idMap.Count}/{rows.Count})");
            _logger.LogDebug(
                "[StashTiming] phase=studios checkpoint=batch imported={Imported} total={Total} elapsedMs={ElapsedMilliseconds:F0}",
                idMap.Count,
                rows.Count,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        void MergeImportedStudioMetadata(Studio entity, int stashId)
        {
            var row = byId[stashId];
            if (string.IsNullOrWhiteSpace(entity.Details) && !string.IsNullOrWhiteSpace(row.Details))
                entity.Details = row.Details;
            entity.Favorite |= row.Favorite;
            if (string.IsNullOrWhiteSpace(entity.ImageBlobId))
                entity.ImageBlobId = GetBlobId(blobMap, row.ImageBlob);

            var urlKeys = entity.Urls.Select(item => item.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var value in urls.GetValueOrDefault(stashId, []).Where(value => !string.IsNullOrWhiteSpace(value)))
                if (urlKeys.Add(value))
                    entity.Urls.Add(new StudioUrl { Url = value });
            var aliasKeys = entity.Aliases.Select(item => item.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var value in aliases.GetValueOrDefault(stashId, []).Where(value => !string.IsNullOrWhiteSpace(value)))
                if (aliasKeys.Add(value))
                    entity.Aliases.Add(new StudioAlias { Alias = value });
            var remoteKeys = entity.RemoteIds.Select(item => (item.Endpoint, item.RemoteId)).ToHashSet();
            foreach (var (endpoint, remoteId) in studioStashIds.GetValueOrDefault(stashId, []))
                if (remoteKeys.Add((endpoint, remoteId)))
                    entity.RemoteIds.Add(new StudioRemoteId { Endpoint = endpoint, RemoteId = remoteId });
        }

        foreach (var group in groups)
        {
            var firstRow = byId[group.StashIds[0]];
            Studio entity;
            if (existingByIdentity.TryGetValue(group.IdentityKey, out var existing))
            {
                entity = existing;
                if (_db.Entry(entity).State == EntityState.Detached)
                    _db.Studios.Attach(entity);
            }
            else
            {
                entity = new Studio
                {
                    Name = EntityNameRules.NormalizeCanonicalName(firstRow.Name),
                    Organized = false,
                    CreatedAt = ParseDateTime(firstRow.CreatedAt),
                    UpdatedAt = ParseDateTime(firstRow.UpdatedAt),
                };
                _db.Studios.Add(entity);
            }

            foreach (var stashId in group.StashIds)
                MergeImportedStudioMetadata(entity, stashId);
            pendingStudios.Add((group.StashIds, entity));
            pendingSourceCount += group.StashIds.Count;

            if (pendingSourceCount >= StudioBatchSize)
                await FlushStudioBatchAsync();
        }

        await FlushStudioBatchAsync();

        var targetIds = idMap.Values.Distinct().ToArray();
        var targetsById = await _db.Studios.Where(studio => targetIds.Contains(studio.Id)).ToDictionaryAsync(studio => studio.Id, ct);
        var parentByStudioId = await _db.Studios.AsNoTracking()
            .ToDictionaryAsync(studio => studio.Id, studio => studio.ParentId, ct);
        foreach (var group in groups)
        {
            var targetId = idMap[group.StashIds[0]];
            var target = targetsById[targetId];
            if (target.ParentId.HasValue)
                continue;
            foreach (var stashId in group.StashIds)
            {
                var parentStashId = byId[stashId].ParentId;
                if (!parentStashId.HasValue
                    || !idMap.TryGetValue(parentStashId.Value, out var parentTargetId)
                    || WouldCreateStudioParentCycle(targetId, parentTargetId, parentByStudioId))
                    continue;
                target.ParentId = parentTargetId;
                parentByStudioId[targetId] = parentTargetId;
                break;
            }
        }
        _db.ChangeTracker.DetectChanges();
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();

        await AddImportedOverallRatingsAsync(
            rows.OrderByDescending(row => row.Id).Select(row => new ImportedRatingSeed(row.Id, row.Rating)),
            idMap,
            RatingHostType.Studio,
            ct);
        _logger.LogInformation("Imported {Count} studios in {Elapsed}", idMap.Count, stopwatch.Elapsed);
        return idMap;
    }

    private static bool WouldCreateStudioParentCycle(
        int studioId,
        int proposedParentId,
        IReadOnlyDictionary<int, int?> parentByStudioId)
    {
        var visited = new HashSet<int>();
        var current = proposedParentId;
        while (true)
        {
            if (current == studioId || !visited.Add(current))
                return true;
            if (!parentByStudioId.TryGetValue(current, out var parentId) || !parentId.HasValue)
                return false;
            current = parentId.Value;
        }
    }

    private async Task<int> ImportStudioTagRelationshipsAsync(
        SqliteConnection conn,
        IReadOnlyDictionary<int, int> studioIdMap,
        IReadOnlyDictionary<int, int> tagIdMap,
        CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "studios_tags", ct))
        {
            _logger.LogDebug("No studios_tags table found; skipping studio-tag relationships");
            return 0;
        }

        const int RelationshipBatchSize = 5000;
        var relationships = new List<StudioTag>(RelationshipBatchSize);
        var mappedRelationships = new HashSet<(int StudioId, int TagId)>();
        var count = 0;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT studio_id, tag_id FROM studios_tags";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!studioIdMap.TryGetValue(reader.GetInt32(0), out var studioId)
                || !tagIdMap.TryGetValue(reader.GetInt32(1), out var tagId)
                || !mappedRelationships.Add((studioId, tagId)))
            {
                continue;
            }

            relationships.Add(new StudioTag { StudioId = studioId, TagId = tagId });
            if (relationships.Count < RelationshipBatchSize)
                continue;

            _db.Set<StudioTag>().AddRange(relationships);
            await _db.SaveChangesAsync(ct);
            count += relationships.Count;
            relationships.Clear();
            _db.ChangeTracker.Clear();
        }

        if (relationships.Count > 0)
        {
            _db.Set<StudioTag>().AddRange(relationships);
            await _db.SaveChangesAsync(ct);
            count += relationships.Count;
        }

        _logger.LogInformation("Imported {Count} studio-tag relationships", count);
        return count;
    }
}
