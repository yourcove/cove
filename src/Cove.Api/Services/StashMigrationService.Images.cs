using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.Api.Services;

public partial class StashMigrationService
{
    private async Task<Dictionary<int, int>> ImportImagesAsync(
        SqliteConnection conn,
        Dictionary<int, int> folderIdMap,
        Dictionary<int, int> studioIdMap,
        Dictionary<int, int> tagIdMap,
        Dictionary<int, int> performerIdMap,
        IJobProgress progress,
        double startProgress,
        double endProgress,
        CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "images", ct))
            return new Dictionary<int, int>();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var total = await CountAsync(conn, "images", ct);
        _logger.LogDebug("Preparing to import {Total} images", total);

        var imageTagMap = await ReadJunctionAsync(conn, "images_tags", "image_id", "tag_id", ct);
        var imagePerformerMap = await ReadJunctionAsync(conn, "performers_images", "image_id", "performer_id", ct);
        var imageUrls = await ReadUrlsAsync(conn, "image_urls", "image_id", ct);

        var imageToFiles = new Dictionary<int, List<int>>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT image_id, file_id, [primary] FROM images_files ORDER BY image_id, [primary] DESC, file_id";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var imageId = r.GetInt32(0);
                if (!imageToFiles.TryGetValue(imageId, out var fileIds))
                    imageToFiles[imageId] = fileIds = [];
                fileIds.Add(r.GetInt32(1));
            }
        }

        var imageFileData = new Dictionary<int, (string Format, int Width, int Height)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT file_id, format, width, height FROM image_files";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                imageFileData[r.GetInt32(0)] = (ReadStringNull(r, 1) ?? string.Empty, ReadIntNull(r, 2) ?? 0, ReadIntNull(r, 3) ?? 0);
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

        var imageRows = new List<(int StashId, string? Title, string? Code, string? Details, string? Photographer,
            int? Rating, bool Organized, int LikeCounter, int? StudioId, string? Date, string CreatedAt, string UpdatedAt)>();

        await using (var cmd = conn.CreateCommand())
        {
            var legacyLikeCounterColumn = "o" + "_counter";
            cmd.CommandText = $"SELECT id, title, code, details, photographer, rating, organized, {legacyLikeCounterColumn}, studio_id, date, created_at, updated_at FROM images";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                imageRows.Add((r.GetInt32(0), ReadStringNull(r, 1), ReadStringNull(r, 2), ReadStringNull(r, 3),
                    ReadStringNull(r, 4), ReadIntNull(r, 5), ReadBool(r, 6), ReadIntNull(r, 7) ?? 0,
                    ReadIntNull(r, 8), ReadStringNull(r, 9), r.GetString(10), r.GetString(11)));
        }

        var idMap = new Dictionary<int, int>(imageRows.Count);
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
            StringComparer.OrdinalIgnoreCase);
        var skippedDuplicateFiles = 0;
        const int BatchSize = 500;
        progress.Report(startProgress, "Importing images...");
        _logger.LogDebug(
            "[StashTiming] phase=images checkpoint=loaded rows={Rows} files={Files} imageFiles={ImageFiles} tagOwners={TagOwners} performerOwners={PerformerOwners} urlOwners={UrlOwners} elapsedMs={ElapsedMilliseconds:F0}",
            imageRows.Count,
            fileData.Count,
            imageFileData.Count,
            imageTagMap.Count,
            imagePerformerMap.Count,
            imageUrls.Count,
            stopwatch.Elapsed.TotalMilliseconds);

        for (int i = 0; i < imageRows.Count; i += BatchSize)
        {
            var batch = imageRows.Skip(i).Take(BatchSize).ToList();
            var batchEntities = new List<(int StashId, Image Entity)>(batch.Count);

            foreach (var row in batch)
            {
                var stashId = row.StashId;
                var image = new Image
                {
                    Title = row.Title,
                    Code = row.Code,
                    Details = row.Details,
                    Photographer = row.Photographer,
                    Organized = row.Organized,
                    StudioId = row.StudioId.HasValue && studioIdMap.TryGetValue(row.StudioId.Value, out var sid) ? sid : null,
                    Date = ParseDate(row.Date),
                    CreatedAt = ParseDateTime(row.CreatedAt),
                    UpdatedAt = ParseDateTime(row.UpdatedAt),
                    Urls = imageUrls.GetValueOrDefault(stashId, []).Select(u => new ImageUrl { Url = u }).ToList(),
                    // Dedupe on the mapped Cove id — distinct Stash ids can collapse to one Cove id
                    // (e.g. merged tags/performers), which would otherwise be a duplicate composite key.
                    ImageTags = imageTagMap.GetValueOrDefault(stashId, [])
                        .Where(tagIdMap.ContainsKey)
                        .Select(t => tagIdMap[t])
                        .Distinct()
                        .Select(tagId => new ImageTag { TagId = tagId }).ToList(),
                    ImagePerformers = imagePerformerMap.GetValueOrDefault(stashId, [])
                        .Where(performerIdMap.ContainsKey)
                        .Select(p => performerIdMap[p])
                        .Distinct()
                        .Select(performerId => new ImagePerformer { PerformerId = performerId }).ToList(),
                };

                if (imageToFiles.TryGetValue(stashId, out var fileIds))
                {
                    foreach (var fileId in fileIds.Distinct())
                    {
                        if (!fileData.TryGetValue(fileId, out var fd) || !folderIdMap.TryGetValue(fd.FolderId, out var coveFolderId))
                            continue;

                        var fileKey = GetImportedBaseFileKey(coveFolderId, fd.Basename);
                        if (!existingFileKeys.Add(fileKey))
                        {
                            skippedDuplicateFiles++;
                            TraceSkippedDuplicateFile(_logger, "image", fd.Basename, coveFolderId);
                            continue;
                        }

                        var imgFile = new ImageFile
                        {
                            Basename = fd.Basename,
                            ParentFolderId = coveFolderId,
                            Size = fd.Size,
                            ModTime = fd.ModTime,
                            CreatedAt = fd.CreatedAt,
                            UpdatedAt = fd.ModTime,
                        };
                        if (imageFileData.TryGetValue(fileId, out var ifd))
                        {
                            imgFile.Format = ifd.Format;
                            imgFile.Width = ifd.Width;
                            imgFile.Height = ifd.Height;
                        }
                        image.Files.Add(imgFile);
                    }
                }

                _db.Images.Add(image);
                batchEntities.Add((stashId, image));
            }

            await _db.SaveChangesAsync(ct);

            foreach (var (stashId, entity) in batchEntities)
                idMap[stashId] = entity.Id;

            _db.ChangeTracker.Clear();
            ReportPhase(progress, startProgress, endProgress, idMap.Count, imageRows.Count, $"Importing images ({idMap.Count}/{imageRows.Count})");

            _logger.LogDebug("Imported {Count}/{Total} images", Math.Min(i + BatchSize, imageRows.Count), imageRows.Count);
            _logger.LogDebug(
                "[StashTiming] phase=images checkpoint=batch imported={Imported} total={Total} skippedDuplicateFiles={SkippedDuplicateFiles} elapsedMs={ElapsedMilliseconds:F0}",
                idMap.Count,
                imageRows.Count,
                skippedDuplicateFiles,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        await AddImportedOverallRatingsAsync(
            imageRows.Select(row => new ImportedRatingSeed(row.StashId, row.Rating)),
            idMap,
            RatingHostType.Image,
            ct);
        await AddImportedAffinitiesAsync(
            imageRows.Select(row => new ImportedAffinitySeed(row.StashId, LikeCount: row.LikeCounter)),
            idMap,
            AffinityHostType.Image,
            ct);

        if (skippedDuplicateFiles > 0)
            _logger.LogWarning("Skipped {Count} duplicate image files because a file with the same folder/basename was already imported", skippedDuplicateFiles);

        _logger.LogInformation("Imported {Count} images in {Elapsed}", idMap.Count, stopwatch.Elapsed);
        return idMap;
    }
}
