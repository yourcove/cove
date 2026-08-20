using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data.Services;

namespace Cove.Api.Services;

public partial class StashMigrationService
{
    private async Task<Dictionary<int, int>> ImportTagsAsync(SqliteConnection conn, Dictionary<string, string> blobMap, IJobProgress progress, double startProgress, double endProgress, CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var rows = new List<(int Id, string Name, string? SortName, string? Description, bool Favorite, string? ImageBlob, string CreatedAt, string UpdatedAt)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, sort_name, description, favorite, image_blob, created_at, updated_at FROM tags";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add((r.GetInt32(0), r.GetString(1), ReadStringNull(r, 2), ReadStringNull(r, 3),
                    ReadBool(r, 4), ReadStringNull(r, 5), r.GetString(6), r.GetString(7)));
        }
        var aliases = await ReadAliasesAsync(conn, "tag_aliases", "tag_id", ct);

        var tagStashIds = new Dictionary<int, List<(string Ep, string Rid)>>();
        if (await TableExistsAsync(conn, "tag_stash_ids", ct))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT tag_id, endpoint, stash_id FROM tag_stash_ids";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var tId = r.GetInt32(0);
                if (!tagStashIds.TryGetValue(tId, out var list)) tagStashIds[tId] = list = [];
                list.Add((r.GetString(1), r.GetString(2)));
            }
        }

        var tagParents = new Dictionary<int, List<int>>();
        if (await TableExistsAsync(conn, "tags_relations", ct))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT parent_id, child_id FROM tags_relations";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var pId = r.GetInt32(0);
                var cId = r.GetInt32(1);
                if (!tagParents.TryGetValue(cId, out var list)) tagParents[cId] = list = [];
                list.Add(pId);
            }
        }

        var byId = rows.ToDictionary(r => r.Id);
        var ordered = TopologicalSort(rows.Select(r => r.Id).ToList(), id => tagParents.GetValueOrDefault(id, []));
        var rowNames = rows
            .Select(row => TagNameRules.NormalizeCanonicalName(row.Name))
            .Distinct(TagNameRules.NamespaceComparer)
            .ToArray();
        var resolvedTags = await RelationNameResolver.ResolveTagsAsync(_db, rowNames, ct);
        var existingByName = resolvedTags
            .GroupBy(pair => TagNameRules.NamespaceKey(pair.Key), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(pair => pair.Value.Id).First().Value, StringComparer.Ordinal);
        var resolvedTagIds = existingByName.Values.Select(tag => tag.Id).Distinct().ToArray();
        if (resolvedTagIds.Length > 0)
            await _db.Set<TagRemoteId>().Where(remoteId => resolvedTagIds.Contains(remoteId.TagId)).LoadAsync(ct);

        var idMap = new Dictionary<int, int>();
        const int TagBatchSize = 1000;
        const int TagParentBatchSize = 5000;
        var pendingTags = new List<(IReadOnlyList<int> StashIds, Tag Entity)>();
        var pendingSourceCount = 0;
        progress.Report(startProgress, "Importing tags...");
        _logger.LogDebug(
            "[StashTiming] phase=tags checkpoint=loaded rows={Rows} aliases={AliasOwners} parentLinks={ParentLinks} elapsedMs={ElapsedMilliseconds:F0}",
            rows.Count,
            aliases.Count,
            tagParents.Sum(static item => item.Value.Count),
            stopwatch.Elapsed.TotalMilliseconds);

        async Task FlushTagBatchAsync()
        {
            if (pendingTags.Count == 0)
                return;

            // RunBulkInsertPhaseAsync disables automatic change detection. Detect once per batch so
            // metadata merged into existing tags and children appended after Add are persisted.
            _db.ChangeTracker.DetectChanges();
            await _db.SaveChangesAsync(ct);
            foreach (var (stashIds, entity) in pendingTags)
                foreach (var stashId in stashIds)
                    idMap[stashId] = entity.Id;

            pendingTags.Clear();
            pendingSourceCount = 0;
            _db.ChangeTracker.Clear();
            ReportPhase(progress, startProgress, endProgress, idMap.Count, ordered.Count, $"Importing tags ({idMap.Count}/{ordered.Count})");
            _logger.LogDebug(
                "[StashTiming] phase=tags checkpoint=batch imported={Imported} total={Total} elapsedMs={ElapsedMilliseconds:F0}",
                idMap.Count,
                ordered.Count,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        void MergeImportedMetadata(Tag entity, int stashId)
        {
            var row = byId[stashId];
            if (string.IsNullOrWhiteSpace(entity.SortName) && !string.IsNullOrWhiteSpace(row.SortName))
                entity.SortName = row.SortName;
            if (string.IsNullOrWhiteSpace(entity.Description) && !string.IsNullOrWhiteSpace(row.Description))
                entity.Description = row.Description;
            entity.Favorite |= row.Favorite;
            if (string.IsNullOrWhiteSpace(entity.ImageBlobId))
                entity.ImageBlobId = GetBlobId(blobMap, row.ImageBlob);

            var canonicalKey = TagNameRules.NamespaceKey(TagNameRules.NormalizeCanonicalName(entity.Name));
            var aliasKeys = entity.Aliases
                .Select(alias => TagNameRules.NormalizeAlias(alias.Alias))
                .Where(alias => alias != null)
                .Select(alias => TagNameRules.NamespaceKey(alias!))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var value in aliases.GetValueOrDefault(stashId, []))
            {
                var normalized = TagNameRules.NormalizeAlias(value);
                if (normalized == null)
                    continue;
                var aliasKey = TagNameRules.NamespaceKey(normalized);
                if (aliasKey != canonicalKey && aliasKeys.Add(aliasKey))
                    entity.Aliases.Add(new TagAlias { Alias = normalized });
            }

            var remoteIds = entity.RemoteIds
                .Select(remoteId => (remoteId.Endpoint, remoteId.RemoteId))
                .ToHashSet();
            foreach (var (endpoint, remoteId) in tagStashIds.GetValueOrDefault(stashId, []))
                if (remoteIds.Add((endpoint, remoteId)))
                    entity.RemoteIds.Add(new TagRemoteId { Endpoint = endpoint, RemoteId = remoteId });
        }

        var groups = ordered
            .GroupBy(
                stashId => TagNameRules.NamespaceKey(TagNameRules.NormalizeCanonicalName(byId[stashId].Name)),
                StringComparer.Ordinal)
            .Select(group => (NamespaceKey: group.Key, StashIds: (IReadOnlyList<int>)group.ToArray()))
            .ToArray();
        foreach (var group in groups)
        {
            var firstRow = byId[group.StashIds[0]];
            Tag entity;
            if (existingByName.TryGetValue(group.NamespaceKey, out var existingTag))
            {
                entity = existingTag;
                if (_db.Entry(entity).State == EntityState.Detached)
                    _db.Tags.Attach(entity);
            }
            else
            {
                entity = new Tag
                {
                    Name = TagNameRules.NormalizeCanonicalName(firstRow.Name),
                    CreatedAt = ParseDateTime(firstRow.CreatedAt),
                    UpdatedAt = ParseDateTime(firstRow.UpdatedAt),
                };
                _db.Tags.Add(entity);
            }

            foreach (var stashId in group.StashIds)
                MergeImportedMetadata(entity, stashId);
            pendingTags.Add((group.StashIds, entity));
            pendingSourceCount += group.StashIds.Count;

            if (pendingSourceCount >= TagBatchSize)
                await FlushTagBatchAsync();
        }

        await FlushTagBatchAsync();

        if (tagParents.Count > 0)
        {
            var parentPairs = new HashSet<(int ParentId, int ChildId)>();
            foreach (var (childStashId, parentStashIds) in tagParents)
            {
                if (!idMap.TryGetValue(childStashId, out var childCoveId)) continue;
                foreach (var parentStashId in parentStashIds)
                {
                    if (!idMap.TryGetValue(parentStashId, out var parentCoveId)) continue;
                    if (parentCoveId == childCoveId) continue;
                    parentPairs.Add((parentCoveId, childCoveId));
                }
            }

            var parentIds = parentPairs.Select(pair => pair.ParentId).Distinct().ToArray();
            var childIds = parentPairs.Select(pair => pair.ChildId).Distinct().ToArray();
            var existingParentPairs = parentPairs.Count == 0
                ? []
                : await _db.Set<TagParent>()
                    .Where(parent => parentIds.Contains(parent.ParentId) && childIds.Contains(parent.ChildId))
                    .Select(parent => new ValueTuple<int, int>(parent.ParentId, parent.ChildId))
                    .ToListAsync(ct);
            foreach (var existingPair in existingParentPairs)
                parentPairs.Remove(existingPair);

            var pendingParents = new List<TagParent>(TagParentBatchSize);
            async Task FlushTagParentsAsync()
            {
                if (pendingParents.Count == 0)
                    return;

                _db.Set<TagParent>().AddRange(pendingParents);
                await _db.SaveChangesAsync(ct);
                pendingParents.Clear();
                _db.ChangeTracker.Clear();
            }

            foreach (var (parentCoveId, childCoveId) in parentPairs)
            {
                pendingParents.Add(new TagParent { ParentId = parentCoveId, ChildId = childCoveId });
                if (pendingParents.Count >= TagParentBatchSize)
                    await FlushTagParentsAsync();
            }
            await FlushTagParentsAsync();
            _logger.LogDebug(
                "[StashTiming] phase=tags checkpoint=parents parentLinks={ParentLinks} elapsedMs={ElapsedMilliseconds:F0}",
                tagParents.Sum(static item => item.Value.Count),
                stopwatch.Elapsed.TotalMilliseconds);
        }

        _logger.LogInformation("Imported {Count} tags in {Elapsed}", idMap.Count, stopwatch.Elapsed);
        return idMap;
    }
}
