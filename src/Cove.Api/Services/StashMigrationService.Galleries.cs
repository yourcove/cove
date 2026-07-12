using Microsoft.Data.Sqlite;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.Api.Services;

public partial class StashMigrationService
{
    private async Task<(int Count, Dictionary<int, int> GalleryFileIdMap, Dictionary<int, int> GalleryIdMap)> ImportGalleriesAsync(
        SqliteConnection conn,
        Dictionary<int, int> folderIdMap,
        Dictionary<int, int> studioIdMap,
        Dictionary<int, int> tagIdMap,
        Dictionary<int, int> performerIdMap,
        Dictionary<int, int> imageIdMap,
        IJobProgress progress,
        double startProgress,
        double endProgress,
        CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "galleries", ct))
        {
            _logger.LogInformation("No galleries table found, skipping");
            return (0, new Dictionary<int, int>(), new Dictionary<int, int>());
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var total = await CountAsync(conn, "galleries", ct);
        _logger.LogInformation("Importing {Total} galleries...", total);

        var galleryTagMap = await ReadJunctionAsync(conn, "galleries_tags", "gallery_id", "tag_id", ct);
        var galleryPerformerMap = await ReadJunctionAsync(conn, "performers_galleries", "gallery_id", "performer_id", ct);
        var galleryUrls = await ReadUrlsAsync(conn, "gallery_urls", "gallery_id", ct);

        var galleryToFile = new Dictionary<int, int>();
        if (await TableExistsAsync(conn, "galleries_files", ct))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT gallery_id, file_id FROM galleries_files WHERE [primary]=1";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                galleryToFile[r.GetInt32(0)] = r.GetInt32(1);
        }

        var galleryImages = new Dictionary<int, List<int>>();
        if (await TableExistsAsync(conn, "galleries_images", ct))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT gallery_id, image_id FROM galleries_images";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var gid = r.GetInt32(0);
                if (!galleryImages.TryGetValue(gid, out var list)) galleryImages[gid] = list = [];
                list.Add(r.GetInt32(1));
            }
        }

        var galleryChapters = new Dictionary<int, List<(string Title, int ImageIndex)>>();
        if (await TableExistsAsync(conn, "galleries_chapters", ct))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT gallery_id, title, image_index FROM galleries_chapters";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var gid = r.GetInt32(0);
                if (!galleryChapters.TryGetValue(gid, out var list)) galleryChapters[gid] = list = [];
                list.Add((r.GetString(1), ReadIntNull(r, 2) ?? 0));
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
        var stashFolderNames = new Dictionary<int, string>();
        if (await TableExistsAsync(conn, "folders", ct))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, path FROM folders";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var path = r.GetString(1);
                var name = GetLastPathSegment(path);
                stashFolderNames[r.GetInt32(0)] = string.IsNullOrWhiteSpace(name) ? path : name;
            }
        }

        var galleryRows = new List<(int StashId, int? FolderId, string? Title, string? Date, string? Details,
            int? StudioId, int? Rating, bool Organized, string CreatedAt, string UpdatedAt, string? Code, string? Photographer)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, folder_id, title, date, details, studio_id, rating, organized, created_at, updated_at, code, photographer FROM galleries";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                galleryRows.Add((r.GetInt32(0), ReadIntNull(r, 1), ReadStringNull(r, 2), ReadStringNull(r, 3),
                    ReadStringNull(r, 4), ReadIntNull(r, 5), ReadIntNull(r, 6), ReadBool(r, 7),
                    r.GetString(8), r.GetString(9), ReadStringNull(r, 10), ReadStringNull(r, 11)));
        }

        var count = 0;
        var galleryFileIdMap = new Dictionary<int, int>();
        var galleryIdMap = new Dictionary<int, int>();
        var pendingGalleryFiles = new List<(int StashFileId, GalleryFile FileEntity)>();
        var pendingGalleries = new List<(int StashId, Gallery Entity)>();
        const int GalleryBatchSize = 500;
        progress.Report(startProgress, "Importing galleries...");
        _logger.LogDebug(
            "[StashTiming] phase=galleries checkpoint=loaded rows={Rows} files={Files} tagOwners={TagOwners} performerOwners={PerformerOwners} urlOwners={UrlOwners} imageOwners={ImageOwners} chapterOwners={ChapterOwners} elapsedMs={ElapsedMilliseconds:F0}",
            galleryRows.Count,
            fileData.Count,
            galleryTagMap.Count,
            galleryPerformerMap.Count,
            galleryUrls.Count,
            galleryImages.Count,
            galleryChapters.Count,
            stopwatch.Elapsed.TotalMilliseconds);
        foreach (var row in galleryRows)
        {
            var stashId = row.StashId;

            var gallery = new Gallery
            {
                Title = ResolveImportedGalleryTitle(row.Title, row.FolderId, stashId, galleryToFile, fileData, stashFolderNames),
                Code = row.Code,
                Date = ParseDate(row.Date),
                Details = row.Details,
                Photographer = row.Photographer,
                Organized = row.Organized,
                FolderId = row.FolderId.HasValue && folderIdMap.TryGetValue(row.FolderId.Value, out var fid) ? fid : null,
                StudioId = row.StudioId.HasValue && studioIdMap.TryGetValue(row.StudioId.Value, out var sid) ? sid : null,
                CreatedAt = ParseDateTime(row.CreatedAt),
                UpdatedAt = ParseDateTime(row.UpdatedAt),
                Urls = galleryUrls.GetValueOrDefault(stashId, []).Select(u => new GalleryUrl { Url = u }).ToList(),
                // Dedupe on the mapped Cove id — distinct Stash ids can collapse to one Cove id
                // (e.g. merged tags/performers), which would otherwise be a duplicate composite key.
                GalleryTags = galleryTagMap.GetValueOrDefault(stashId, [])
                    .Where(tagIdMap.ContainsKey)
                    .Select(t => tagIdMap[t])
                    .Distinct()
                    .Select(tagId => new GalleryTag { TagId = tagId }).ToList(),
                GalleryPerformers = galleryPerformerMap.GetValueOrDefault(stashId, [])
                    .Where(performerIdMap.ContainsKey)
                    .Select(p => performerIdMap[p])
                    .Distinct()
                    .Select(performerId => new GalleryPerformer { PerformerId = performerId }).ToList(),
                Chapters = galleryChapters.GetValueOrDefault(stashId, [])
                    .Select(c => new GalleryChapter { Title = c.Title, ImageIndex = c.ImageIndex }).ToList(),
                ImageGalleries = galleryImages.GetValueOrDefault(stashId, [])
                    .Where(imageIdMap.ContainsKey)
                    .Select(imgId => imageIdMap[imgId])
                    .Distinct()
                    .Select(coveImageId => new ImageGallery { ImageId = coveImageId }).ToList(),
            };

            if (galleryToFile.TryGetValue(stashId, out var fileId) && fileData.TryGetValue(fileId, out var fd)
                && folderIdMap.TryGetValue(fd.FolderId, out var coveFolderId))
            {
                var galleryFile = new GalleryFile
                {
                    Basename = fd.Basename,
                    ParentFolderId = coveFolderId,
                    Size = fd.Size,
                    ModTime = fd.ModTime,
                    CreatedAt = fd.CreatedAt,
                    UpdatedAt = fd.ModTime,
                };

                gallery.Files.Add(galleryFile);
                pendingGalleryFiles.Add((fileId, galleryFile));
            }

            _db.Galleries.Add(gallery);
            pendingGalleries.Add((stashId, gallery));
            count++;
            if (count % GalleryBatchSize == 0)
            {
                await _db.SaveChangesAsync(ct);
                foreach (var (galleryStashId, galleryEntity) in pendingGalleries)
                    galleryIdMap[galleryStashId] = galleryEntity.Id;
                foreach (var (stashFileId, fileEntity) in pendingGalleryFiles)
                    galleryFileIdMap[stashFileId] = fileEntity.Id;
                pendingGalleries.Clear();
                pendingGalleryFiles.Clear();
                _db.ChangeTracker.Clear();
                ReportPhase(progress, startProgress, endProgress, count, total, $"Importing galleries ({count}/{total})");
                _logger.LogInformation("Imported {Count}/{Total} galleries...", count, total);
                _logger.LogDebug(
                    "[StashTiming] phase=galleries checkpoint=batch imported={Imported} total={Total} galleryFiles={GalleryFiles} elapsedMs={ElapsedMilliseconds:F0}",
                    count,
                    total,
                    galleryFileIdMap.Count,
                    stopwatch.Elapsed.TotalMilliseconds);
            }
        }
        await _db.SaveChangesAsync(ct);
        foreach (var (galleryStashId, galleryEntity) in pendingGalleries)
            galleryIdMap[galleryStashId] = galleryEntity.Id;
        foreach (var (stashFileId, fileEntity) in pendingGalleryFiles)
            galleryFileIdMap[stashFileId] = fileEntity.Id;
        _db.ChangeTracker.Clear();
        ReportPhase(progress, startProgress, endProgress, count, total, $"Importing galleries ({count}/{total})");
        await AddImportedOverallRatingsAsync(
            galleryRows.Select(row => new ImportedRatingSeed(row.StashId, row.Rating)),
            galleryIdMap,
            RatingHostType.Gallery,
            ct);
        _logger.LogInformation("Imported {Count} galleries in {Elapsed}", count, stopwatch.Elapsed);
        return (count, galleryFileIdMap, galleryIdMap);
    }

    private async Task<int> ImportVideoGalleryRelationshipsAsync(
        SqliteConnection conn,
        IReadOnlyDictionary<int, int> sceneIdMap,
        IReadOnlyDictionary<int, int> galleryIdMap,
        CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "scenes_galleries", ct))
        {
            _logger.LogInformation("No scenes_galleries table found, skipping video-gallery relationships");
            return 0;
        }

        const int RelationshipBatchSize = 5000;
        var relationships = new List<VideoGallery>(RelationshipBatchSize);
        var mappedRelationships = new HashSet<(int VideoId, int GalleryId)>();
        var count = 0;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT scene_id, gallery_id FROM scenes_galleries";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!sceneIdMap.TryGetValue(reader.GetInt32(0), out var videoId)
                || !galleryIdMap.TryGetValue(reader.GetInt32(1), out var galleryId)
                || !mappedRelationships.Add((videoId, galleryId)))
            {
                continue;
            }

            relationships.Add(new VideoGallery { VideoId = videoId, GalleryId = galleryId });
            if (relationships.Count < RelationshipBatchSize)
                continue;

            _db.Set<VideoGallery>().AddRange(relationships);
            await _db.SaveChangesAsync(ct);
            count += relationships.Count;
            relationships.Clear();
            _db.ChangeTracker.Clear();
        }

        if (relationships.Count > 0)
        {
            _db.Set<VideoGallery>().AddRange(relationships);
            await _db.SaveChangesAsync(ct);
            count += relationships.Count;
        }

        _logger.LogInformation("Imported {Count} video-gallery relationships", count);
        return count;
    }
}
