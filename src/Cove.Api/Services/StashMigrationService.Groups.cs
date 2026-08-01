using Microsoft.Data.Sqlite;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.Api.Services;

public partial class StashMigrationService
{
    private async Task<Dictionary<int, int>> ImportGroupsAsync(SqliteConnection conn, Dictionary<string, string> blobMap, Dictionary<int, int> studioIdMap, IJobProgress progress, double startProgress, double endProgress, CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var hasFrontImageBlob = await ColumnExistsAsync(conn, "groups", "front_image_blob", ct);
        var hasBackImageBlob = await ColumnExistsAsync(conn, "groups", "back_image_blob", ct);
        var rows = new List<(int Id, string Name, string? Aliases, int? Duration, string? Date,
            int? Rating, int? StudioId, string? Director, string? Description, string? FrontImageBlob, string? BackImageBlob)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT id, name, aliases, duration, date, rating, studio_id, director, description,
                       {(hasFrontImageBlob ? "front_image_blob" : "NULL")} AS front_image_blob,
                       {(hasBackImageBlob ? "back_image_blob" : "NULL")} AS back_image_blob
                FROM groups
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add((r.GetInt32(0), r.GetString(1), ReadStringNull(r, 2), ReadIntNull(r, 3),
                    ReadStringNull(r, 4), ReadIntNull(r, 5), ReadIntNull(r, 6),
                    ReadStringNull(r, 7), ReadStringNull(r, 8), ReadStringNull(r, 9), ReadStringNull(r, 10)));
        }
        var urls = await ReadUrlsAsync(conn, "group_urls", "group_id", ct);
        var sceneCounts = await ReadGroupSceneCountsAsync(conn, ct);
        var importUnits = BuildGroupImportUnits(rows, urls, sceneCounts);

        var idMap = new Dictionary<int, int>(rows.Count);
        const int GroupBatchSize = 500;
        var batchEntities = new List<(IReadOnlyList<int> StashIds, Cove.Core.Entities.Group Entity)>(GroupBatchSize);
        progress.Report(startProgress, "Importing groups...");
        _logger.LogDebug(
            "[StashTiming] phase=groups checkpoint=loaded rows={Rows} units={Units} urlOwners={UrlOwners} elapsedMs={ElapsedMilliseconds:F0}",
            rows.Count,
            importUnits.Count,
            urls.Count,
            stopwatch.Elapsed.TotalMilliseconds);
        foreach (var unit in importUnits)
        {
            var entity = new Cove.Core.Entities.Group
            {
                Name = unit.Name,
                Aliases = unit.Aliases,
                Duration = unit.Duration,
                Date = ParseDate(unit.Date),
                StudioId = unit.StudioId.HasValue && studioIdMap.TryGetValue(unit.StudioId.Value, out var sId) ? sId : null,
                Director = unit.Director,
                Synopsis = unit.Description,
                FrontImageBlobId = GetBlobId(blobMap, unit.FrontImageBlob),
                BackImageBlobId = GetBlobId(blobMap, unit.BackImageBlob),
                Urls = unit.Urls.Select(u => new GroupUrl { Url = u }).ToList(),
            };
            _db.Groups.Add(entity);
            batchEntities.Add((unit.StashIds, entity));

            if (batchEntities.Count >= GroupBatchSize)
            {
                await _db.SaveChangesAsync(ct);

                foreach (var (stashIds, group) in batchEntities)
                {
                    foreach (var stashId in stashIds)
                        idMap[stashId] = group.Id;
                }

                batchEntities.Clear();
                _db.ChangeTracker.Clear();
                ReportPhase(progress, startProgress, endProgress, idMap.Count, rows.Count, $"Importing groups ({idMap.Count}/{rows.Count})");
                _logger.LogDebug(
                    "[StashTiming] phase=groups checkpoint=batch imported={Imported} total={Total} elapsedMs={ElapsedMilliseconds:F0}",
                    idMap.Count,
                    rows.Count,
                    stopwatch.Elapsed.TotalMilliseconds);
            }
        }

        if (batchEntities.Count > 0)
        {
            await _db.SaveChangesAsync(ct);

            foreach (var (stashIds, group) in batchEntities)
            {
                foreach (var stashId in stashIds)
                    idMap[stashId] = group.Id;
            }

            batchEntities.Clear();
            _db.ChangeTracker.Clear();
            ReportPhase(progress, startProgress, endProgress, idMap.Count, rows.Count, $"Importing groups ({idMap.Count}/{rows.Count})");
        }
        await AddImportedOverallRatingsAsync(
            rows.Select(row => new ImportedRatingSeed(row.Id, row.Rating)),
            idMap,
            RatingHostType.Group,
            ct);

        _logger.LogInformation("Imported {SourceCount} Stash groups into {GroupCount} Cove groups in {Elapsed}", idMap.Count, importUnits.Count, stopwatch.Elapsed);
        return idMap;
    }

    private async Task<int> ImportGroupTagRelationshipsAsync(
        SqliteConnection conn,
        IReadOnlyDictionary<int, int> groupIdMap,
        IReadOnlyDictionary<int, int> tagIdMap,
        CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "groups_tags", ct))
        {
            _logger.LogDebug("No groups_tags table found; skipping group-tag relationships");
            return 0;
        }

        const int RelationshipBatchSize = 5000;
        var relationships = new List<GroupTag>(RelationshipBatchSize);
        var mappedRelationships = new HashSet<(int GroupId, int TagId)>();
        var count = 0;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT group_id, tag_id FROM groups_tags";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!groupIdMap.TryGetValue(reader.GetInt32(0), out var groupId)
                || !tagIdMap.TryGetValue(reader.GetInt32(1), out var tagId)
                || !mappedRelationships.Add((groupId, tagId)))
            {
                continue;
            }

            relationships.Add(new GroupTag { GroupId = groupId, TagId = tagId });
            if (relationships.Count < RelationshipBatchSize)
                continue;

            _db.Set<GroupTag>().AddRange(relationships);
            await _db.SaveChangesAsync(ct);
            count += relationships.Count;
            relationships.Clear();
            _db.ChangeTracker.Clear();
        }

        if (relationships.Count > 0)
        {
            _db.Set<GroupTag>().AddRange(relationships);
            await _db.SaveChangesAsync(ct);
            count += relationships.Count;
        }

        _logger.LogInformation("Imported {Count} group-tag relationships", count);
        return count;
    }

    private async Task<int> ImportGroupRelationsAsync(
        SqliteConnection conn,
        IReadOnlyDictionary<int, int> groupIdMap,
        CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "groups_relations", ct))
        {
            _logger.LogDebug("No groups_relations table found; skipping group relations");
            return 0;
        }

        const int RelationshipBatchSize = 5000;
        var relationships = new List<GroupRelation>(RelationshipBatchSize);
        var mappedRelationships = new HashSet<(int ContainingGroupId, int SubGroupId)>();
        var count = 0;
        await using var cmd = conn.CreateCommand();
        // Distinct Stash groups can collapse to one Cove group. Process source IDs deterministically so
        // the lowest ordered source pair supplies order/description when multiple pairs map to one link.
        cmd.CommandText = "SELECT containing_id, sub_id, order_index, description FROM groups_relations ORDER BY containing_id, sub_id";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!groupIdMap.TryGetValue(reader.GetInt32(0), out var containingGroupId)
                || !groupIdMap.TryGetValue(reader.GetInt32(1), out var subGroupId)
                || containingGroupId == subGroupId
                || !mappedRelationships.Add((containingGroupId, subGroupId)))
            {
                continue;
            }

            relationships.Add(new GroupRelation
            {
                ContainingGroupId = containingGroupId,
                SubGroupId = subGroupId,
                OrderIndex = reader.GetInt32(2),
                Description = ReadStringNull(reader, 3),
            });
            if (relationships.Count < RelationshipBatchSize)
                continue;

            _db.Set<GroupRelation>().AddRange(relationships);
            await _db.SaveChangesAsync(ct);
            count += relationships.Count;
            relationships.Clear();
            _db.ChangeTracker.Clear();
        }

        if (relationships.Count > 0)
        {
            _db.Set<GroupRelation>().AddRange(relationships);
            await _db.SaveChangesAsync(ct);
            count += relationships.Count;
        }

        _logger.LogInformation("Imported {Count} group relations", count);
        return count;
    }

    private sealed record StashGroupImportUnit(
        IReadOnlyList<int> StashIds,
        string Name,
        string? Aliases,
        int? Duration,
        string? Date,
        int? Rating,
        int? StudioId,
        string? Director,
        string? Description,
        string? FrontImageBlob,
        string? BackImageBlob,
        IReadOnlyList<string> Urls);

    private static async Task<Dictionary<int, int>> ReadGroupSceneCountsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var result = new Dictionary<int, int>();
        if (!await TableExistsAsync(conn, "groups_scenes", ct))
            return result;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT group_id, COUNT(*) FROM groups_scenes GROUP BY group_id";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result[r.GetInt32(0)] = r.GetInt32(1);

        return result;
    }

    private static List<StashGroupImportUnit> BuildGroupImportUnits(
        IReadOnlyList<(int Id, string Name, string? Aliases, int? Duration, string? Date,
            int? Rating, int? StudioId, string? Director, string? Description, string? FrontImageBlob, string? BackImageBlob)> rows,
        IReadOnlyDictionary<int, List<string>> urls,
        IReadOnlyDictionary<int, int> sceneCounts)
    {
        var units = new List<StashGroupImportUnit>(rows.Count);
        foreach (var duplicateSet in rows.GroupBy(GetGroupDisplayKey))
        {
            var duplicateRows = duplicateSet.ToList();
            var sceneLinkedRows = duplicateRows
                .Where(row => sceneCounts.GetValueOrDefault(row.Id) > 0)
                .OrderByDescending(row => sceneCounts.GetValueOrDefault(row.Id))
                .ThenBy(row => row.Id)
                .ToList();
            var coverOnlyRows = duplicateRows
                .Where(row => sceneCounts.GetValueOrDefault(row.Id) == 0 && HasGroupImage(row))
                .OrderBy(row => row.Id)
                .ToList();

            if (sceneLinkedRows.Count != 1 || coverOnlyRows.Count == 0)
            {
                foreach (var row in duplicateRows)
                    units.Add(CreateGroupImportUnit([row], urls));
                continue;
            }

            var sceneLinkedRow = sceneLinkedRows[0];
            var mergedRowIds = coverOnlyRows.Select(row => row.Id).ToHashSet();
            mergedRowIds.Add(sceneLinkedRow.Id);
            units.Add(CreateGroupImportUnit(
                [sceneLinkedRow, .. coverOnlyRows],
                urls));

            foreach (var row in duplicateRows.Where(row => !mergedRowIds.Contains(row.Id)))
                units.Add(CreateGroupImportUnit([row], urls));
        }

        return units;
    }

    private static StashGroupImportUnit CreateGroupImportUnit(
        IReadOnlyList<(int Id, string Name, string? Aliases, int? Duration, string? Date,
            int? Rating, int? StudioId, string? Director, string? Description, string? FrontImageBlob, string? BackImageBlob)> rows,
        IReadOnlyDictionary<int, List<string>> urls)
    {
        var canonical = rows[0];
        return new StashGroupImportUnit(
            rows.Select(row => row.Id).ToArray(),
            canonical.Name,
            FirstNonWhite(rows.Select(row => row.Aliases)),
            canonical.Duration ?? FirstNonNull(rows.Select(row => row.Duration)),
            FirstNonWhite(rows.Select(row => row.Date)),
            canonical.Rating ?? FirstNonNull(rows.Select(row => row.Rating)),
            canonical.StudioId ?? FirstNonNull(rows.Select(row => row.StudioId)),
            FirstNonWhite(rows.Select(row => row.Director)),
            FirstNonWhite(rows.Select(row => row.Description)),
            rows.Select(row => row.FrontImageBlob).FirstOrDefault(blob => !string.IsNullOrWhiteSpace(blob)),
            rows.Select(row => row.BackImageBlob).FirstOrDefault(blob => !string.IsNullOrWhiteSpace(blob)),
            rows.SelectMany(row => urls.GetValueOrDefault(row.Id, []))
                .Distinct(StringComparer.Ordinal)
                .ToArray());
    }

    private static bool HasGroupImage(
        (int Id, string Name, string? Aliases, int? Duration, string? Date, int? Rating, int? StudioId, string? Director, string? Description, string? FrontImageBlob, string? BackImageBlob) row)
        => !string.IsNullOrWhiteSpace(row.FrontImageBlob) || !string.IsNullOrWhiteSpace(row.BackImageBlob);

    private static (string Name, string? Date) GetGroupDisplayKey(
        (int Id, string Name, string? Aliases, int? Duration, string? Date, int? Rating, int? StudioId, string? Director, string? Description, string? FrontImageBlob, string? BackImageBlob) row)
        => (NormalizeGroupNameKey(row.Name), NormalizeGroupDateKey(row.Date));

    private static string NormalizeGroupNameKey(string name)
        => string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim().ToUpperInvariant();

    private static string? NormalizeGroupDateKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateOnly.TryParse(value, out var date)
            ? date.ToString("yyyy-MM-dd")
            : value.Trim();
    }

    private static string? FirstNonWhite(IEnumerable<string?> values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static int? FirstNonNull(IEnumerable<int?> values)
        => values.FirstOrDefault(value => value.HasValue);
}
