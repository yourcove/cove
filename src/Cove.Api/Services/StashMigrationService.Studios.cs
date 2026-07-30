using Microsoft.Data.Sqlite;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

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

        var byId = rows.ToDictionary(r => r.Id);
        var ordered = TopologicalSort(rows.Select(r => r.Id).ToList(),
            id => byId[id].ParentId.HasValue ? [byId[id].ParentId!.Value] : (IEnumerable<int>)[]);

        _logger.LogDebug("Preparing to import {Total} studios", rows.Count);
        var idMap = new Dictionary<int, int>();
        var createdStudiosByStashId = new Dictionary<int, Studio>();
        const int StudioBatchSize = 500;
        var pendingStudios = new List<(int StashId, Studio Entity)>(StudioBatchSize);
        progress.Report(startProgress, "Importing studios...");
        _logger.LogDebug(
            "[StashTiming] phase=studios checkpoint=loaded rows={Rows} urlOwners={UrlOwners} aliasOwners={AliasOwners} remoteIdOwners={RemoteIdOwners} elapsedMs={ElapsedMilliseconds:F0}",
            rows.Count,
            urls.Count,
            aliases.Count,
            studioStashIds.Count,
            stopwatch.Elapsed.TotalMilliseconds);

        async Task FlushStudioBatchAsync()
        {
            if (pendingStudios.Count == 0)
                return;

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed importing studio batch with Stash IDs [{StashStudioIds}]",
                    string.Join(", ", pendingStudios.Select(static item => item.StashId)));
                throw;
            }

            foreach (var (stashId, entity) in pendingStudios)
                idMap[stashId] = entity.Id;

            pendingStudios.Clear();
            _db.ChangeTracker.Clear();

            ReportPhase(progress, startProgress, endProgress, idMap.Count, ordered.Count, $"Importing studios ({idMap.Count}/{ordered.Count})");
            _logger.LogDebug(
                "[StashTiming] phase=studios checkpoint=batch imported={Imported} total={Total} elapsedMs={ElapsedMilliseconds:F0}",
                idMap.Count,
                ordered.Count,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        foreach (var stashId in ordered)
        {
            var row = byId[stashId];
            var remoteIds = studioStashIds.GetValueOrDefault(stashId, [])
                .DistinctBy(s => (s.Ep, s.Rid))
                .Select(s => new StudioRemoteId { Endpoint = s.Ep, RemoteId = s.Rid })
                .ToList();
            var entity = new Studio
            {
                Name = row.Name,
                ParentId = row.ParentId.HasValue && idMap.TryGetValue(row.ParentId.Value, out var pId) ? pId : null,
                Parent = row.ParentId.HasValue && !idMap.ContainsKey(row.ParentId.Value) && createdStudiosByStashId.TryGetValue(row.ParentId.Value, out var parentStudio) ? parentStudio : null,
                Details = row.Details,
                Favorite = row.Favorite,
                Organized = false,
                ImageBlobId = GetBlobId(blobMap, row.ImageBlob),
                Urls = urls.GetValueOrDefault(stashId, []).Select(u => new StudioUrl { Url = u }).ToList(),
                Aliases = aliases.GetValueOrDefault(stashId, []).Select(a => new StudioAlias { Alias = a }).ToList(),
                RemoteIds = remoteIds,
                CreatedAt = ParseDateTime(row.CreatedAt),
                UpdatedAt = ParseDateTime(row.UpdatedAt),
            };
            _db.Studios.Add(entity);
            createdStudiosByStashId[stashId] = entity;
            pendingStudios.Add((stashId, entity));

            if (pendingStudios.Count >= StudioBatchSize)
                await FlushStudioBatchAsync();
        }

        await FlushStudioBatchAsync();
        await AddImportedOverallRatingsAsync(
            rows.Select(row => new ImportedRatingSeed(row.Id, row.Rating)),
            idMap,
            RatingHostType.Studio,
            ct);
        _logger.LogInformation("Imported {Count} studios in {Elapsed}", idMap.Count, stopwatch.Elapsed);
        return idMap;
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
