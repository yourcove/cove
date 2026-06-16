using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using Scene = Cove.Core.Entities.Video;
using SceneUrl = Cove.Core.Entities.VideoUrl;
using SceneTag = Cove.Core.Entities.VideoTag;
using ScenePerformer = Cove.Core.Entities.VideoPerformer;
using SceneLikeHistory = Cove.Core.Entities.VideoLikeHistory;
using ScenePlayHistory = Cove.Core.Entities.VideoPlayHistory;
using SceneRemoteId = Cove.Core.Entities.VideoRemoteId;

namespace Cove.Api.Services;

public partial class StashMigrationService
{
    private async Task<(int count, Dictionary<int, int> sceneIdMap, Dictionary<int, SceneGeneratedData> generatedMap)> ImportScenesAsync(
        SqliteConnection conn,
        Dictionary<string, string> blobMap,
        Dictionary<int, int> folderIdMap,
        Dictionary<int, int> studioIdMap,
        Dictionary<int, int> tagIdMap,
        Dictionary<int, int> performerIdMap,
        Dictionary<int, int> groupIdMap,
        IJobProgress progress,
        double startProgress,
        double endProgress,
        CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var sceneRows = new List<(int Id, string? Title, string? Details, string? Date, int? Rating,
            int? StudioId, bool Organized, string? Code, string? Director,
            double ResumeTime, double PlayDuration, string CreatedAt, string UpdatedAt, string? CoverBlob, string? LastPlayedAt)>();
        var hasSceneCoverBlob = await ColumnExistsAsync(conn, "scenes", "cover_blob", ct);
        var hasSceneLastPlayedAt = await ColumnExistsAsync(conn, "scenes", "last_played_at", ct);
        await using (var cmd = conn.CreateCommand())
        {
            var coverBlobExpr = hasSceneCoverBlob ? "cover_blob" : "NULL";
            var lastPlayedAtExpr = hasSceneLastPlayedAt ? "last_played_at" : "NULL";
            cmd.CommandText = $@"SELECT id, title, details, date, rating, studio_id, organized, code, director,
                resume_time, play_duration, created_at, updated_at, {coverBlobExpr} AS cover_blob, {lastPlayedAtExpr} AS last_played_at FROM scenes";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                sceneRows.Add((r.GetInt32(0), ReadStringNull(r, 1), ReadStringNull(r, 2), ReadStringNull(r, 3),
                    ReadIntNull(r, 4), ReadIntNull(r, 5), ReadBool(r, 6), ReadStringNull(r, 7),
                    ReadStringNull(r, 8), r.GetDouble(9), r.GetDouble(10), r.GetString(11), r.GetString(12),
                    ReadStringNull(r, 13), ReadStringNull(r, 14)));
        }

        var sceneTagMap = await ReadJunctionAsync(conn, "scenes_tags", "scene_id", "tag_id", ct);
        var scenePerformerMap = await ReadJunctionAsync(conn, "performers_scenes", "scene_id", "performer_id", ct);
        var sceneGroupMap = new Dictionary<int, List<(int GroupId, int Index)>>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT scene_id, group_id, scene_index FROM groups_scenes";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var sId = r.GetInt32(0);
                var gId = r.GetInt32(1);
                var idx = ReadIntNull(r, 2) ?? 0;
                if (!sceneGroupMap.TryGetValue(sId, out var list)) sceneGroupMap[sId] = list = [];
                list.Add((gId, idx));
            }
        }
        var sceneUrls = await ReadUrlsAsync(conn, "scene_urls", "scene_id", ct);
        var sceneODates = await ReadDatesAsync(conn, "scenes_o_dates", "scene_id", "o_date", ct);
        var sceneViewDates = await ReadDatesAsync(conn, "scenes_view_dates", "scene_id", "view_date", ct);

        var sceneStashIds = new Dictionary<int, List<(string Ep, string Rid)>>();
        if (await TableExistsAsync(conn, "scene_stash_ids", ct))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT scene_id, endpoint, stash_id FROM scene_stash_ids";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var sId = r.GetInt32(0);
                if (!sceneStashIds.TryGetValue(sId, out var list)) sceneStashIds[sId] = list = [];
                list.Add((r.GetString(1), r.GetString(2)));
            }
        }

        var sceneFiles = new Dictionary<int, List<int>>();
        var scenePrimaryFileMap = new Dictionary<int, int>();
        var hasScenePrimaryColumn = await ColumnExistsAsync(conn, "scenes_files", "primary", ct);
        await using (var cmd = conn.CreateCommand())
        {
            var primaryExpr = hasScenePrimaryColumn ? "[primary]" : "0";
            cmd.CommandText = $"SELECT scene_id, file_id, {primaryExpr} AS [primary] FROM scenes_files ORDER BY scene_id, [primary] DESC, file_id";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var sId = r.GetInt32(0);
                var fId = r.GetInt32(1);
                if (!sceneFiles.TryGetValue(sId, out var list)) sceneFiles[sId] = list = [];
                list.Add(fId);
                var isPrimary = !r.IsDBNull(2) && r.GetBoolean(2);
                if (isPrimary || !scenePrimaryFileMap.ContainsKey(sId))
                    scenePrimaryFileMap[sId] = fId;
            }
        }

        var fileData = new Dictionary<int, (string Basename, int FolderId, long Size, DateTime ModTime, DateTime CreatedAt)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, basename, parent_folder_id, size, mod_time, created_at FROM files";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                fileData[r.GetInt32(0)] = (r.GetString(1), r.GetInt32(2), r.GetInt64(3),
                    ParseDateTime(r.GetString(4)), ParseDateTime(r.GetString(5)));
        }

        var videoData = new Dictionary<int, (double Duration, string VideoCodec, string Format, string AudioCodec, int Width, int Height, double FrameRate, long BitRate, bool Interactive, int? InteractiveSpeed)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT file_id, duration, video_codec, format, audio_codec, width, height, frame_rate, bit_rate, interactive, interactive_speed FROM video_files";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                videoData[r.GetInt32(0)] = (r.GetDouble(1), r.GetString(2), r.GetString(3), r.GetString(4),
                    r.GetInt32(5), r.GetInt32(6), r.GetDouble(7), r.GetInt64(8), ReadBool(r, 9), ReadIntNull(r, 10));
        }

        var fingerprints = new Dictionary<int, List<(string Type, string Value)>>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT file_id, type, fingerprint FROM files_fingerprints";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var fId = r.GetInt32(0);
                var type = r.GetString(1);
                var rawFp = r.GetValue(2);
                var value = NormalizeImportedFingerprintValue(type, rawFp);
                if (!fingerprints.TryGetValue(fId, out var list)) fingerprints[fId] = list = [];
                list.Add((type, value));
            }
        }

        var count = 0;
        var skippedFailedScenes = 0;
        var idMap = new Dictionary<int, int>();
        const int SceneBatchSize = 250;
        var pendingBatch = new List<(int StashId, Scene Entity)>(SceneBatchSize);
        _logger.LogInformation("Importing {Total} scenes...", sceneRows.Count);
        progress.Report(startProgress, "Importing scenes...");
        _logger.LogDebug(
            "[StashTiming] phase=scenes checkpoint=loaded rows={Rows} files={Files} videos={Videos} fingerprints={FingerprintOwners} tagOwners={TagOwners} performerOwners={PerformerOwners} groupOwners={GroupOwners} elapsedMs={ElapsedMilliseconds:F0}",
            sceneRows.Count,
            fileData.Count,
            videoData.Count,
            fingerprints.Count,
            sceneTagMap.Count,
            scenePerformerMap.Count,
            sceneGroupMap.Count,
            stopwatch.Elapsed.TotalMilliseconds);

        void FlushSceneBatch()
        {
            foreach (var (stashId, entity) in pendingBatch)
                idMap[stashId] = entity.Id;
            pendingBatch.Clear();
        }

        // Persist the pending batch. If the bulk insert fails (a single malformed scene can take
        // the whole SaveChanges down), retry the batch one scene at a time on a clean tracker so a
        // single bad item is skipped and logged instead of aborting the entire migration.
        async Task SaveSceneBatchAsync()
        {
            if (pendingBatch.Count == 0) return;
            try
            {
                await _db.SaveChangesAsync(ct);
                FlushSceneBatch();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var batch = pendingBatch.ToList();
                pendingBatch.Clear();
                _db.ChangeTracker.Clear();
                _logger.LogWarning(ex,
                    "[Stash] Scene batch insert failed; retrying {Count} scenes individually", batch.Count);

                foreach (var (stashId, entity) in batch)
                {
                    try
                    {
                        _db.Videos.Add(entity);
                        await _db.SaveChangesAsync(ct);
                        idMap[stashId] = entity.Id;
                    }
                    catch (Exception sceneEx) when (sceneEx is not OperationCanceledException)
                    {
                        skippedFailedScenes++;
                        count--; // was counted when added to the batch, but did not import
                        _logger.LogWarning(sceneEx,
                            "[Stash] Skipped scene stashId={StashId} after a per-scene insert error", stashId);
                    }
                    finally
                    {
                        _db.ChangeTracker.Clear();
                    }
                }
            }
        }

        // Guard the unique (ParentFolderId, Basename) index: if two scene files resolve to the same
        // folder+basename (e.g. genuine duplicates), skip the second instead of letting SaveChanges
        // throw and abort the entire import. Keys are compared case-sensitively to match the index.
        var candidateParentFolderIds = fileData.Values
            .Where(file => folderIdMap.ContainsKey(file.FolderId))
            .Select(file => folderIdMap[file.FolderId])
            .Distinct()
            .ToList();
        var existingFileKeys = new HashSet<string>(
            await _db.Set<BaseFileEntity>()
                .AsNoTracking()
                .Where(file => candidateParentFolderIds.Contains(file.ParentFolderId))
                .Select(file => GetImportedBaseFileKey(file.ParentFolderId, file.Basename))
                .ToListAsync(ct),
            StringComparer.Ordinal);
        var seenFileKeys = new HashSet<string>(StringComparer.Ordinal);
        var skippedDuplicateFiles = 0;

        foreach (var row in sceneRows)
        {
            var oHistory = sceneODates.GetValueOrDefault(row.Id, []);
            var viewHistory = sceneViewDates.GetValueOrDefault(row.Id, []);
            var importedLastPlayedAt = ParseDateTimeOrNull(row.LastPlayedAt);

            var scene = new Scene
            {
                Title = row.Title,
                Details = row.Details,
                Date = ParseDate(row.Date),
                StudioId = row.StudioId.HasValue && studioIdMap.TryGetValue(row.StudioId.Value, out var sId) ? sId : null,
                Organized = row.Organized,
                Code = row.Code,
                Director = row.Director,
                CreatedAt = ParseDateTime(row.CreatedAt),
                UpdatedAt = ParseDateTime(row.UpdatedAt),
                Urls = sceneUrls.GetValueOrDefault(row.Id, []).Select(u => new SceneUrl { Url = u }).ToList(),
                // Dedupe on the mapped Cove id: a scene can list the same Stash id twice, and two
                // distinct Stash ids can collapse to one Cove id (e.g. merged tags/performers).
                // Either case yields a duplicate composite key that EF rejects when the graph is
                // attached, which would otherwise abort the whole scenes phase.
                VideoTags = sceneTagMap.GetValueOrDefault(row.Id, [])
                    .Where(tagIdMap.ContainsKey)
                    .Select(t => tagIdMap[t])
                    .Distinct()
                    .Select(tagId => new SceneTag { TagId = tagId }).ToList(),
                VideoPerformers = scenePerformerMap.GetValueOrDefault(row.Id, [])
                    .Where(performerIdMap.ContainsKey)
                    .Select(p => performerIdMap[p])
                    .Distinct()
                    .Select(performerId => new ScenePerformer { PerformerId = performerId }).ToList(),
                GroupItems = sceneGroupMap.GetValueOrDefault(row.Id, [])
                    .Where(g => groupIdMap.ContainsKey(g.GroupId))
                    .DistinctBy(g => groupIdMap[g.GroupId])
                    .Select(g => new GroupItem
                    {
                        GroupId = groupIdMap[g.GroupId],
                        OrderIndex = g.Index,
                        Kind = GroupItemKind.Video,
                    }).ToList(),
                LikeHistory = oHistory.Select(d => new SceneLikeHistory { OccurredAt = d }).ToList(),
                PlayHistory = viewHistory.Select(d => new ScenePlayHistory { PlayedAt = d }).ToList(),
                RemoteIds = sceneStashIds.GetValueOrDefault(row.Id, [])
                    .Select(s => new SceneRemoteId { Endpoint = s.Ep, RemoteId = s.Rid }).ToList(),
            };

            foreach (var fileId in sceneFiles.GetValueOrDefault(row.Id, []))
            {
                if (!fileData.TryGetValue(fileId, out var fd)) continue;
                if (!videoData.TryGetValue(fileId, out var vd)) continue;
                if (!folderIdMap.TryGetValue(fd.FolderId, out var coveFolderId)) continue;

                var fileKey = GetImportedBaseFileKey(coveFolderId, fd.Basename);
                if (existingFileKeys.Contains(fileKey) || !seenFileKeys.Add(fileKey))
                {
                    skippedDuplicateFiles++;
                    continue;
                }

                scene.Files.Add(new VideoFile
                {
                    Basename = fd.Basename,
                    ParentFolderId = coveFolderId,
                    Size = fd.Size,
                    ModTime = fd.ModTime,
                    CreatedAt = fd.CreatedAt,
                    UpdatedAt = fd.ModTime,
                    Duration = vd.Duration,
                    VideoCodec = vd.VideoCodec,
                    Format = vd.Format,
                    AudioCodec = vd.AudioCodec,
                    Width = vd.Width,
                    Height = vd.Height,
                    FrameRate = vd.FrameRate,
                    BitRate = vd.BitRate,
                    Interactive = vd.Interactive,
                    InteractiveSpeed = vd.InteractiveSpeed,
                    Fingerprints = fingerprints.GetValueOrDefault(fileId, [])
                        .Select(fp => new FileFingerprint { Type = fp.Type, Value = fp.Value }).ToList(),
                });
            }

            try
            {
                _db.Videos.Add(scene);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Don't let a single bad scene abort the whole import. The pending batch is
                // re-saved on a clean tracker below, so prior good scenes are not lost.
                skippedFailedScenes++;
                _logger.LogWarning(ex, "[Stash] Skipped scene stashId={StashId} during add", row.Id);
                continue;
            }
            pendingBatch.Add((row.Id, scene));
            count++;

            if (pendingBatch.Count >= SceneBatchSize)
            {
                await SaveSceneBatchAsync();
                _db.ChangeTracker.Clear();
                ReportPhase(progress, startProgress, endProgress, count, sceneRows.Count, $"Importing scenes ({count}/{sceneRows.Count})");
                _logger.LogInformation("Imported {Count}/{Total} scenes...", count, sceneRows.Count);
                _logger.LogDebug(
                    "[StashTiming] phase=scenes checkpoint=batch imported={Imported} total={Total} elapsedMs={ElapsedMilliseconds:F0}",
                    count,
                    sceneRows.Count,
                    stopwatch.Elapsed.TotalMilliseconds);
            }
        }

        if (pendingBatch.Count > 0)
        {
            await SaveSceneBatchAsync();
            _db.ChangeTracker.Clear();
            ReportPhase(progress, startProgress, endProgress, count, sceneRows.Count, $"Importing scenes ({count}/{sceneRows.Count})");
        }
        await AddImportedOverallRatingsAsync(
            sceneRows.Select(row => new ImportedRatingSeed(row.Id, row.Rating)),
            idMap,
            RatingHostType.Video,
            ct);

        var sceneAffinitySeeds = new List<ImportedAffinitySeed>(sceneRows.Count);
        foreach (var row in sceneRows)
        {
            var likeHistory = sceneODates.GetValueOrDefault(row.Id, []);
            var viewHistory = sceneViewDates.GetValueOrDefault(row.Id, []);
            var lastConsumedAt = ParseDateTimeOrNull(row.LastPlayedAt) ?? (viewHistory.Count > 0 ? viewHistory.Max() : null);
            sceneAffinitySeeds.Add(new ImportedAffinitySeed(
                row.Id,
                LikeCount: likeHistory.Count,
                ViewCount: viewHistory.Count,
                LastPositionSec: row.ResumeTime > 0 ? row.ResumeTime : null,
                TotalConsumedSec: row.PlayDuration,
                LastConsumedAt: lastConsumedAt));
        }
        await AddImportedAffinitiesAsync(sceneAffinitySeeds, idMap, AffinityHostType.Video, ct);

        _logger.LogInformation("Imported {Count} scenes in {Elapsed}", count, stopwatch.Elapsed);
        if (skippedDuplicateFiles > 0)
            _logger.LogWarning("Skipped {Count} duplicate scene files because a file with the same folder/basename was already imported", skippedDuplicateFiles);
        if (skippedFailedScenes > 0)
            _logger.LogWarning("[Stash] Skipped {Count} scenes that failed to import; the rest of the migration completed", skippedFailedScenes);

        var generatedMap = new Dictionary<int, SceneGeneratedData>();
        foreach (var row in sceneRows)
        {
            if (!idMap.TryGetValue(row.Id, out var coveId)) continue;
            if (!scenePrimaryFileMap.TryGetValue(row.Id, out var primaryFileId))
            {
                var fileIds = sceneFiles.GetValueOrDefault(row.Id, []);
                if (fileIds.Count == 0) continue;
                primaryFileId = fileIds[0];
            }

            var primaryFingerprints = fingerprints.GetValueOrDefault(primaryFileId, []);
            generatedMap[coveId] = new SceneGeneratedData(
                GetFingerprintValue(primaryFingerprints, "oshash"),
                GetFingerprintValue(primaryFingerprints, "md5"),
                GetBlobId(blobMap, row.CoverBlob));
        }

        return (count, idMap, generatedMap);
    }

    private async Task<int> ImportSceneMarkerSegmentsAsync(
        SqliteConnection conn,
        Dictionary<int, int> sceneIdMap,
        Dictionary<int, int> tagIdMap,
        IJobProgress progress,
        double startProgress,
        double endProgress,
        CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "scene_markers", ct))
        {
            progress.Report(endProgress, "No scene markers to import");
            return 0;
        }

        var total = await CountAsync(conn, "scene_markers", ct);
        if (total == 0)
        {
            progress.Report(endProgress, "No scene markers to import");
            return 0;
        }

        var hasEndSeconds = await ColumnExistsAsync(conn, "scene_markers", "end_seconds", ct);
        var markerRows = new List<(int Id, string Title, double Seconds, double? EndSeconds, int? PrimaryTagId, int? SceneId, string CreatedAt, string UpdatedAt)>();
        await using (var cmd = conn.CreateCommand())
        {
            var endSecondsExpr = hasEndSeconds ? "end_seconds" : "NULL";
            cmd.CommandText = $@"SELECT id, title, seconds, {endSecondsExpr} AS end_seconds, primary_tag_id, scene_id, created_at, updated_at
                FROM scene_markers
                ORDER BY scene_id, seconds, id";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                markerRows.Add((
                    r.GetInt32(0),
                    r.GetString(1),
                    r.GetDouble(2),
                    r.IsDBNull(3) ? null : r.GetDouble(3),
                    ReadIntNull(r, 4),
                    ReadIntNull(r, 5),
                    r.GetString(6),
                    r.GetString(7)));
            }
        }

        var markerTagMap = await TableExistsAsync(conn, "scene_markers_tags", ct)
            ? await ReadJunctionAsync(conn, "scene_markers_tags", "scene_marker_id", "tag_id", ct)
            : new Dictionary<int, List<int>>();

        var legacyAiTagIds = await GetLegacyAiMarkerTagIdsAsync(conn, ct);
        const int MarkerBatchSize = 200;

        var processed = 0;
        var pending = 0;
        var imported = 0;
        var skippedLegacyAi = 0;
        progress.Report(startProgress, "Importing scene marker segments...");

        foreach (var row in markerRows)
        {
            processed++;
            if (!row.PrimaryTagId.HasValue || !row.SceneId.HasValue)
                continue;

            var markerTagIds = markerTagMap.GetValueOrDefault(row.Id, []);
            var allTagIds = new HashSet<int>(markerTagIds) { row.PrimaryTagId.Value };
            if (allTagIds.Any(legacyAiTagIds.Contains))
            {
                skippedLegacyAi++;
                continue;
            }

            if (!sceneIdMap.TryGetValue(row.SceneId.Value, out var coveSceneId))
                continue;
            if (!tagIdMap.TryGetValue(row.PrimaryTagId.Value, out var covePrimaryTagId))
                continue;

            var secondaryTagIds = markerTagIds
                .Where(tagId => tagId != row.PrimaryTagId.Value && tagIdMap.ContainsKey(tagId))
                .Select(tagId => tagIdMap[tagId])
                .Distinct()
                .ToArray();

            _db.Segments.Add(new Segment
            {
                HostType = SegmentHostType.Video,
                HostId = coveSceneId,
                StartSec = row.Seconds,
                EndSec = row.EndSeconds,
                TagId = covePrimaryTagId,
                Kind = "tag",
                RefId = row.Id,
                Payload = secondaryTagIds.Length > 0 ? JsonSerializer.SerializeToDocument(new { secondaryTagIds }) : null,
                SourceKey = "user",
                Title = string.IsNullOrWhiteSpace(row.Title) ? null : row.Title,
                CreatedAt = ParseDateTime(row.CreatedAt),
                UpdatedAt = ParseDateTime(row.UpdatedAt),
            });

            imported++;
            pending++;

            if (pending >= MarkerBatchSize)
            {
                await _db.SaveChangesAsync(ct);
                _db.ChangeTracker.Clear();
                pending = 0;
            }

            if (processed % MarkerBatchSize == 0)
                ReportPhase(progress, startProgress, endProgress, processed, markerRows.Count, $"Importing scene marker segments ({processed}/{markerRows.Count})");
        }

        if (pending > 0)
        {
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
        }

        ReportPhase(progress, startProgress, endProgress, processed, markerRows.Count, $"Importing scene marker segments ({processed}/{markerRows.Count})");
        _logger.LogInformation("Imported {Count} scene marker segments and skipped {Skipped} legacy AI markers", imported, skippedLegacyAi);
        return imported;
    }

}
