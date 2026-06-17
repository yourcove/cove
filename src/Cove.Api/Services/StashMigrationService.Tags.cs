using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

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
            .Select(row => row.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var existingTags = await _db.Tags
            .Where(tag => rowNames.Contains(tag.Name))
            .ToListAsync(ct);
        var existingByName = existingTags.ToDictionary(tag => tag.Name, StringComparer.Ordinal);

        var idMap = new Dictionary<int, int>();
        const int TagBatchSize = 1000;
        const int TagParentBatchSize = 5000;
        var pendingTags = new List<(int StashId, Tag Entity)>(TagBatchSize);
        var pendingTagsByName = new Dictionary<string, Tag>(StringComparer.Ordinal);
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

            await _db.SaveChangesAsync(ct);
            foreach (var (stashId, entity) in pendingTags)
                idMap[stashId] = entity.Id;
            foreach (var entity in pendingTags.Select(tag => tag.Entity).DistinctBy(tag => tag.Name))
                existingByName[entity.Name] = entity;

            pendingTags.Clear();
            pendingTagsByName.Clear();
            _db.ChangeTracker.Clear();
            ReportPhase(progress, startProgress, endProgress, idMap.Count, ordered.Count, $"Importing tags ({idMap.Count}/{ordered.Count})");
            _logger.LogDebug(
                "[StashTiming] phase=tags checkpoint=batch imported={Imported} total={Total} elapsedMs={ElapsedMilliseconds:F0}",
                idMap.Count,
                ordered.Count,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        foreach (var stashId in ordered)
        {
            var row = byId[stashId];
            if (existingByName.TryGetValue(row.Name, out var existingTag))
            {
                idMap[stashId] = existingTag.Id;
                continue;
            }

            if (pendingTagsByName.TryGetValue(row.Name, out var pendingTag))
            {
                pendingTags.Add((stashId, pendingTag));
                continue;
            }

            var entity = new Tag
            {
                Name = row.Name,
                SortName = row.SortName,
                Description = row.Description,
                Favorite = row.Favorite,
                ImageBlobId = GetBlobId(blobMap, row.ImageBlob),
                Aliases = aliases.GetValueOrDefault(stashId, []).Select(a => new TagAlias { Alias = a }).ToList(),
                CreatedAt = ParseDateTime(row.CreatedAt),
                UpdatedAt = ParseDateTime(row.UpdatedAt),
            };
            _db.Tags.Add(entity);
            pendingTagsByName[row.Name] = entity;
            pendingTags.Add((stashId, entity));

            if (pendingTags.Count >= TagBatchSize)
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