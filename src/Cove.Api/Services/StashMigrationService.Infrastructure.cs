using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Cove.Core.Common;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.Api.Services;

public partial class StashMigrationService
{
    private async Task<Dictionary<string, string>> ImportBlobsAsync(SqliteConnection conn, string? blobFilesPath, IJobProgress progress, double startProgress, double endProgress, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var total = await CountAsync(conn, "blobs", ct);
        var processed = 0;
        var inlineCount = 0;
        var fileCount = 0;
        var missingCount = 0;
        var failedCount = 0;
        var blobStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var normalizedBlobFilesPath = string.IsNullOrWhiteSpace(blobFilesPath) ? null : blobFilesPath.Trim();
        var hasBlobFilesPath = !string.IsNullOrWhiteSpace(normalizedBlobFilesPath) && Directory.Exists(normalizedBlobFilesPath);

        if (!string.IsNullOrWhiteSpace(normalizedBlobFilesPath) && !hasBlobFilesPath)
        {
            _logger.LogWarning("Configured Stash blob files path does not exist: {Path}", normalizedBlobFilesPath);
        }

        _logger.LogInformation("Importing {Total} blobs from {Source}", total, hasBlobFilesPath ? normalizedBlobFilesPath : "inline SQLite");
        _logger.LogDebug("[StashTiming] phase=blobs checkpoint=ready totalRows={Total} blobFilesPath={BlobFilesPath}", total, normalizedBlobFilesPath ?? "");
        progress.Report(startProgress, "Importing blobs...");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT checksum, blob FROM blobs";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            processed++;
            var checksum = r.GetString(0);
            try
            {
                if (!r.IsDBNull(1))
                {
                    var bytes = (byte[])r.GetValue(1);
                    using var ms = new MemoryStream(bytes, writable: false);
                    var contentType = DetectImageContentType(ms);
                    ms.Position = 0;
                    var blobId = await _blobService.StoreBlobAsync(ms, contentType, ct);
                    map[checksum] = blobId;
                    inlineCount++;
                }
                else if (hasBlobFilesPath && TryResolveStashBlobFilePath(normalizedBlobFilesPath!, checksum, out var sourcePath))
                {
                    await using var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1024 * 128, useAsync: true);
                    var contentType = DetectImageContentType(fs);
                    fs.Position = 0;
                    var blobId = await _blobService.StoreBlobAsync(fs, contentType, ct);
                    map[checksum] = blobId;
                    fileCount++;
                }
                else
                {
                    missingCount++;
                    TraceStashMissingBlobData(_logger, checksum);
                }
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogWarning(ex, "Blob {Checksum} import failed", checksum);
            }

            if (processed % 100 == 0 || processed == total)
            {
                ReportPhase(progress, startProgress, endProgress, processed, total, $"Importing blobs ({processed}/{total})");
                _logger.LogDebug(
                    "[StashTiming] phase=blobs checkpoint=batch processed={Processed} total={Total} imported={Imported} inline={Inline} files={Files} missing={Missing} failed={Failed} elapsedMs={ElapsedMilliseconds:F0}",
                    processed,
                    total,
                    map.Count,
                    inlineCount,
                    fileCount,
                    missingCount,
                    failedCount,
                    blobStopwatch.Elapsed.TotalMilliseconds);
            }
        }
        _logger.LogInformation(
            "Imported {Count} blobs in {Elapsed}: inline {Inline}, files {Files}, missing {Missing}, failed {Failed}",
            map.Count,
            blobStopwatch.Elapsed,
            inlineCount,
            fileCount,
            missingCount,
            failedCount);
        return map;
    }

    private async Task<Dictionary<int, int>> ImportFoldersAsync(SqliteConnection conn, IReadOnlyList<StashPathMapping> pathMappings, IJobProgress progress, double startProgress, double endProgress, CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var folderData = new Dictionary<int, (string Path, int? ParentId, DateTime ModTime, DateTime CreatedAt)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, path, parent_folder_id, mod_time, created_at FROM folders";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                folderData[r.GetInt32(0)] = (r.GetString(1), ReadIntNull(r, 2),
                    ParseDateTime(r.GetString(3)), ParseDateTime(r.GetString(4)));
        }

        var folderIdMap = new Dictionary<int, int>();
        var ordered = TopologicalSort(folderData.Keys.ToList(),
            id => folderData[id].ParentId.HasValue ? [folderData[id].ParentId!.Value] : (IEnumerable<int>)[]);

        var allPaths = folderData.Values
            .SelectMany(fd => GetImportedPathLookupCandidates(ApplyStashPathMappings(fd.Path, pathMappings) ?? fd.Path))
            .Distinct(FilesystemPaths.PathComparer)
            .ToList();
        // Group folders using the host filesystem's case sensitivity so that two folders differing only
        // by case (distinct on Linux, e.g. .../Weibtm and .../weibtm) are NOT collapsed into one cove
        // folder — which would make their identically-named files collide on (ParentFolderId, Basename).
        var existingFoldersByPath = _db.Folders
            .AsNoTracking()
            .Where(f => allPaths.Contains(f.Path))
            .AsEnumerable()
            .GroupBy(f => NormalizeImportedPath(f.Path), FilesystemPaths.PathComparer)
            .ToDictionary(group => group.Key, group => group.OrderBy(f => f.Id).First().Id, FilesystemPaths.PathComparer);

        progress.Report(startProgress, "Importing folders...");
        _logger.LogDebug(
            "[StashTiming] phase=folders checkpoint=loaded rows={Rows} pathCandidates={PathCandidates} existing={Existing} elapsedMs={ElapsedMilliseconds:F0}",
            folderData.Count,
            allPaths.Count,
            existingFoldersByPath.Count,
            stopwatch.Elapsed.TotalMilliseconds);

        const int FolderBatchSize = 1000;
        var pendingFolders = new List<(int StashId, string NormalizedPath, Folder Entity)>(FolderBatchSize);
        var createdFoldersByStashId = new Dictionary<int, Folder>();
        var createdFoldersByPath = new Dictionary<string, Folder>(FilesystemPaths.PathComparer);

        async Task FlushFolderBatchAsync()
        {
            if (pendingFolders.Count == 0)
                return;

            await _db.SaveChangesAsync(ct);
            foreach (var (stashId, normalizedPath, entity) in pendingFolders)
            {
                folderIdMap[stashId] = entity.Id;
                existingFoldersByPath[normalizedPath] = entity.Id;
            }

            pendingFolders.Clear();
            _db.ChangeTracker.Clear();
            ReportPhase(progress, startProgress, endProgress, folderIdMap.Count, ordered.Count, $"Importing folders ({folderIdMap.Count}/{ordered.Count})");
            _logger.LogDebug(
                "[StashTiming] phase=folders checkpoint=batch imported={Imported} total={Total} elapsedMs={ElapsedMilliseconds:F0}",
                folderIdMap.Count,
                ordered.Count,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        foreach (var stashFolderId in ordered)
        {
            var fd = folderData[stashFolderId];
            var normalizedPath = NormalizeImportedPath(ApplyStashPathMappings(fd.Path, pathMappings) ?? fd.Path);
            if (existingFoldersByPath.TryGetValue(normalizedPath, out var existingId))
            {
                folderIdMap[stashFolderId] = existingId;
                continue;
            }

            if (createdFoldersByPath.TryGetValue(normalizedPath, out var pendingFolder))
            {
                createdFoldersByStashId[stashFolderId] = pendingFolder;
                pendingFolders.Add((stashFolderId, normalizedPath, pendingFolder));
                if (pendingFolders.Count >= FolderBatchSize)
                    await FlushFolderBatchAsync();
                continue;
            }

            var folder = new Folder
            {
                Path = normalizedPath,
                ParentFolderId = fd.ParentId.HasValue && folderIdMap.TryGetValue(fd.ParentId.Value, out var pfId) ? pfId : null,
                ParentFolder = fd.ParentId.HasValue && !folderIdMap.ContainsKey(fd.ParentId.Value) && createdFoldersByStashId.TryGetValue(fd.ParentId.Value, out var parentFolder) ? parentFolder : null,
                ModTime = fd.ModTime,
                CreatedAt = fd.CreatedAt,
                UpdatedAt = fd.ModTime,
            };
            _db.Folders.Add(folder);
            createdFoldersByStashId[stashFolderId] = folder;
            createdFoldersByPath[normalizedPath] = folder;
            pendingFolders.Add((stashFolderId, normalizedPath, folder));

            if (pendingFolders.Count >= FolderBatchSize)
                await FlushFolderBatchAsync();
        }

        await FlushFolderBatchAsync();
        _logger.LogInformation("Imported {Count} folders in {Elapsed}", folderIdMap.Count, stopwatch.Elapsed);
        return folderIdMap;
    }

    private async Task ReconcileImportedZipLinksAsync(
        SqliteConnection conn,
        Dictionary<int, int> folderIdMap,
        Dictionary<int, int> imageIdMap,
        Dictionary<int, int> galleryFileIdMap,
        CancellationToken ct)
    {
        if (galleryFileIdMap.Count == 0 || !await TableExistsAsync(conn, "files", ct)
            || !await ColumnExistsAsync(conn, "files", "zip_file_id", ct))
        {
            return;
        }

        await ReconcileImportedFolderZipLinksAsync(conn, folderIdMap, galleryFileIdMap, ct);
        await ReconcileImportedImageFileZipLinksAsync(conn, folderIdMap, imageIdMap, galleryFileIdMap, ct);
    }

    private async Task ReconcileImportedFolderZipLinksAsync(
        SqliteConnection conn,
        Dictionary<int, int> folderIdMap,
        Dictionary<int, int> galleryFileIdMap,
        CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "folders", ct)
            || !await ColumnExistsAsync(conn, "folders", "zip_file_id", ct))
        {
            return;
        }

        var folderZipLinks = new List<(int StashFolderId, int StashZipFileId)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, zip_file_id FROM folders WHERE zip_file_id IS NOT NULL";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                folderZipLinks.Add((reader.GetInt32(0), reader.GetInt32(1)));
        }

        if (folderZipLinks.Count == 0) return;

        var targetFolderIds = folderZipLinks
            .Where(link => folderIdMap.ContainsKey(link.StashFolderId) && galleryFileIdMap.ContainsKey(link.StashZipFileId))
            .Select(link => folderIdMap[link.StashFolderId])
            .Distinct()
            .ToList();

        if (targetFolderIds.Count == 0) return;

        var foldersById = (await _db.Folders
            .Where(folder => targetFolderIds.Contains(folder.Id))
            .ToListAsync(ct))
            .ToDictionary(folder => folder.Id);

        var updated = 0;
        foreach (var (stashFolderId, stashZipFileId) in folderZipLinks)
        {
            if (!folderIdMap.TryGetValue(stashFolderId, out var coveFolderId)) continue;
            if (!galleryFileIdMap.TryGetValue(stashZipFileId, out var coveZipFileId)) continue;
            if (!foldersById.TryGetValue(coveFolderId, out var folder)) continue;
            if (folder.ZipFileId == coveZipFileId) continue;

            folder.ZipFileId = coveZipFileId;
            updated++;
        }

        if (updated > 0)
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Reconciled {Count} imported folder zip links", updated);
        }
    }

    private async Task ReconcileImportedImageFileZipLinksAsync(
        SqliteConnection conn,
        Dictionary<int, int> folderIdMap,
        Dictionary<int, int> imageIdMap,
        Dictionary<int, int> galleryFileIdMap,
        CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "images_files", ct))
        {
            return;
        }

        var sourceLinks = new List<(int StashImageId, string Basename, int ParentFolderId, int StashZipFileId)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT images_files.image_id, files.basename, files.parent_folder_id, files.zip_file_id
FROM images_files
JOIN files ON files.id = images_files.file_id
WHERE files.zip_file_id IS NOT NULL";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                sourceLinks.Add((
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3)));
            }
        }

        if (sourceLinks.Count == 0) return;

        // Reconcile in batches. Loading every imported ImageFile at once into a tracked dictionary and
        // committing them in a single SaveChanges exhausts memory and stalls for hours on large libraries
        // (millions of image files). Process the source links in fixed-size batches, loading/mutating/saving
        // only a bounded set at a time and clearing the change tracker between batches so it never grows.
        const int BatchSize = 2000;
        var updated = 0;

        for (var offset = 0; offset < sourceLinks.Count; offset += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = sourceLinks.GetRange(offset, Math.Min(BatchSize, sourceLinks.Count - offset));

            // Resolve this batch's links to Cove ids first, collecting only the image ids we must load.
            var resolved = new List<(int CoveImageId, int CoveParentFolderId, string Basename, int CoveZipFileId)>(batch.Count);
            foreach (var (stashImageId, basename, stashParentFolderId, stashZipFileId) in batch)
            {
                if (imageIdMap.TryGetValue(stashImageId, out var coveImageId)
                    && folderIdMap.TryGetValue(stashParentFolderId, out var coveParentFolderId)
                    && galleryFileIdMap.TryGetValue(stashZipFileId, out var coveZipFileId))
                {
                    resolved.Add((coveImageId, coveParentFolderId, basename, coveZipFileId));
                }
            }

            if (resolved.Count == 0) continue;

            var batchImageIds = resolved.Select(item => item.CoveImageId).Distinct().ToList();
            var imageFilesByKey = (await _db.ImageFiles
                .Where(file => file.ImageId.HasValue && batchImageIds.Contains(file.ImageId.Value))
                .ToListAsync(ct))
                .ToDictionary(file => GetImportedImageFileKey(file.ImageId ?? 0, file.ParentFolderId, file.Basename));

            var batchUpdated = 0;
            foreach (var (coveImageId, coveParentFolderId, basename, coveZipFileId) in resolved)
            {
                var key = GetImportedImageFileKey(coveImageId, coveParentFolderId, basename);
                if (imageFilesByKey.TryGetValue(key, out var imageFile) && imageFile.ZipFileId != coveZipFileId)
                {
                    imageFile.ZipFileId = coveZipFileId;
                    batchUpdated++;
                }
            }

            if (batchUpdated > 0)
            {
                await _db.SaveChangesAsync(ct);
                updated += batchUpdated;
            }

            _db.ChangeTracker.Clear();
        }

        if (updated > 0)
        {
            _logger.LogInformation("Reconciled {Count} imported image file zip links", updated);
        }
    }

    private static string GetImportedImageFileKey(int imageId, int parentFolderId, string basename)
        => $"{imageId}|{parentFolderId}|{basename}";

    private static string GetImportedBaseFileKey(int parentFolderId, string basename)
        => $"{parentFolderId}|{basename}";

    private async Task ImportStashConfigAsync(StashConfigData stashConfig, CancellationToken ct)
    {
        try
        {
            var dto = _configService.GetConfig();
            var (addedPaths, addedMetadataServers, updatedMetadataServers) = MergeStashConfigIntoCoveConfig(dto, stashConfig);
            if (addedPaths == 0 && addedMetadataServers == 0 && updatedMetadataServers == 0)
            {
                _logger.LogDebug("No Stash config paths or metadata servers required importing");
                return;
            }

            await _configService.SaveConfigAsync(dto);
            _logger.LogInformation(
                "Imported Stash config additions: {PathCount} library paths, {AddedMetadataServerCount} metadata servers added, {UpdatedMetadataServerCount} metadata servers updated",
                addedPaths,
                addedMetadataServers,
                updatedMetadataServers);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to import Stash config");
        }
    }

    private static (int AddedPaths, int AddedMetadataServers, int UpdatedMetadataServers) MergeStashConfigIntoCoveConfig(CoveConfigDto dto, StashConfigData stashConfig)
    {
        var addedPaths = 0;
        var addedMetadataServers = 0;
        var updatedMetadataServers = 0;

        var existingPaths = new HashSet<string>(dto.CovePaths.Select(path => path.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var (path, excludeImage, excludeVideo) in stashConfig.Paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !existingPaths.Add(path))
                continue;

            dto.CovePaths.Add(new CovePathDto
            {
                Path = path,
                ExcludeImage = excludeImage,
                ExcludeVideo = excludeVideo,
                ExcludeAudio = false,
                ExcludeText = false,
            });
            addedPaths++;
        }

        var metadataServers = dto.Scraping.MetadataServers;
        var metadataServerIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < metadataServers.Count; index++)
        {
            var endpoint = metadataServers[index].Endpoint;
            if (!string.IsNullOrWhiteSpace(endpoint))
                metadataServerIndexes[endpoint] = index;
        }

        foreach (var server in stashConfig.MetadataServers)
        {
            var endpoint = server.Endpoint.Trim();
            if (string.IsNullOrWhiteSpace(endpoint))
                continue;

            var normalizedServer = new MetadataServerDto
            {
                Endpoint = endpoint,
                ApiKey = server.ApiKey.Trim(),
                Name = string.IsNullOrWhiteSpace(server.Name) ? endpoint : server.Name.Trim(),
                MaxRequestsPerMinute = server.MaxRequestsPerMinute > 0 ? server.MaxRequestsPerMinute : 240,
            };

            if (metadataServerIndexes.TryGetValue(endpoint, out var existingIndex))
            {
                if (string.IsNullOrWhiteSpace(normalizedServer.ApiKey) && !string.IsNullOrWhiteSpace(metadataServers[existingIndex].ApiKey))
                {
                    normalizedServer = normalizedServer with { ApiKey = metadataServers[existingIndex].ApiKey };
                }

                if (metadataServers[existingIndex] == normalizedServer)
                    continue;

                metadataServers[existingIndex] = normalizedServer;
                updatedMetadataServers++;
                continue;
            }

            metadataServers.Add(normalizedServer);
            metadataServerIndexes[endpoint] = metadataServers.Count - 1;
            addedMetadataServers++;
        }

        return (addedPaths, addedMetadataServers, updatedMetadataServers);
    }

    private async Task ApplyCoveGeneratedPathOverrideAsync(string? coveGeneratedPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(coveGeneratedPath))
            return;

        var normalizedPath = coveGeneratedPath.Trim();
        var dto = _configService.GetConfig();
        if (string.Equals(dto.GeneratedPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
            return;

        await _configService.SaveConfigAsync(dto with { GeneratedPath = normalizedPath });
        _logger.LogInformation("Updated Cove generated path to {Path} before Stash import", normalizedPath);
    }

    private async Task CopyGeneratedContentAsync(StashConfigData stashConfig, Dictionary<int, SceneGeneratedData> sceneGeneratedMap, StashImportOptions options, IJobProgress progress, double startProgress, double endProgress, CancellationToken ct)
    {
        try
        {
            progress.Report(startProgress, "Copying generated scene assets...");
            // Writing the scene cover thumbnail only needs the imported cover blob and Cove's own
            // destination path — NOT Stash's generated folder. So a missing/absent Stash generated
            // directory must not stop cover thumbnails from being written; it only disables the
            // file-copy steps (previews/sprites/vtt and the legacy screenshot fallback) that read
            // from it.
            var stashGeneratedPath = stashConfig.GeneratedPath;
            var hasStashGenerated = !string.IsNullOrWhiteSpace(stashGeneratedPath) && Directory.Exists(stashGeneratedPath);
            if (!hasStashGenerated)
                _logger.LogWarning("Stash generated path not found: {Path} - migrating cover thumbnails from blobs only", stashGeneratedPath);

            var stashScreenshotsDir = hasStashGenerated ? Path.Combine(stashGeneratedPath!, "screenshots") : string.Empty;
            var stashVttDir = hasStashGenerated ? Path.Combine(stashGeneratedPath!, "vtt") : string.Empty;

            var previewHashes = Directory.Exists(stashScreenshotsDir)
                ? Directory.EnumerateFiles(stashScreenshotsDir, "*.mp4", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var spriteHashes = Directory.Exists(stashVttDir)
                ? Directory.EnumerateFiles(stashVttDir, "*_sprite.jpg", SearchOption.TopDirectoryOnly)
                    .Select(path => TrimGeneratedSuffix(Path.GetFileNameWithoutExtension(path), "_sprite"))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var vttHashes = Directory.Exists(stashVttDir)
                ? Directory.EnumerateFiles(stashVttDir, "*_thumbs.vtt", SearchOption.TopDirectoryOnly)
                    .Select(path => TrimGeneratedSuffix(Path.GetFileNameWithoutExtension(path), "_thumbs"))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Older Stash installs (or libraries where the "Migrate scene screenshots" task was never
            // run) keep the scene cover as a generated file at screenshots/<hash>.jpg rather than a
            // cover_blob. Index those so scenes without a cover blob still get a thumbnail. Exclude
            // "<hash>.thumb.jpg" sidecars — only the full-size "<hash>.jpg" is the cover.
            var legacyScreenshotHashes = Directory.Exists(stashScreenshotsDir)
                ? Directory.EnumerateFiles(stashScreenshotsDir, "*.jpg", SearchOption.TopDirectoryOnly)
                    .Where(path => !path.EndsWith(".thumb.jpg", StringComparison.OrdinalIgnoreCase))
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int sourceScreenshots = 0;
            int migratedScreenshots = 0;
            int sourcePreviews = 0;
            int migratedPreviews = 0;
            int sourceSprites = 0;
            int migratedSprites = 0;
            int sourceVtts = 0;
            int migratedVtts = 0;

            var processed = 0;
            var totalScenes = sceneGeneratedMap.Count;
            foreach (var (coveSceneId, generatedData) in sceneGeneratedMap)
            {
                ct.ThrowIfCancellationRequested();
                processed++;

                if (!string.IsNullOrWhiteSpace(generatedData.CoverBlobId))
                {
                    sourceScreenshots++;
                    if (await TryWriteSceneScreenshotAsync(coveSceneId, generatedData.CoverBlobId!, ct))
                        migratedScreenshots++;
                }
                else if (ResolveGeneratedHash(generatedData, stashConfig.VideoFileNamingAlgorithm, legacyScreenshotHashes) is { } legacyScreenshotHash)
                {
                    // No cover blob (legacy state, or the blob failed to import) — fall back to the
                    // generated screenshots/<hash>.jpg file. Stash screenshots are already JPEG, so a
                    // straight copy into Cove's screenshot path matches what the runtime serves.
                    sourceScreenshots++;
                    var srcScreenshotPath = Path.Combine(stashScreenshotsDir, $"{legacyScreenshotHash}.jpg");
                    if (TryCopyGeneratedFile(coveSceneId, "screenshot", srcScreenshotPath, GetCoveSceneThumbnailPath(coveSceneId)))
                        migratedScreenshots++;
                }

                var previewHash = ResolveGeneratedHash(generatedData, stashConfig.VideoFileNamingAlgorithm, previewHashes);
                if (!string.IsNullOrWhiteSpace(previewHash))
                {
                    sourcePreviews++;
                    var srcPreviewPath = Path.Combine(stashScreenshotsDir, $"{previewHash}.mp4");
                    if (TryCopyGeneratedFile(coveSceneId, "preview", srcPreviewPath, GetCoveScenePreviewPath(coveSceneId)))
                        migratedPreviews++;
                }

                var spriteHash = ResolveGeneratedHash(generatedData, stashConfig.VideoFileNamingAlgorithm, spriteHashes);
                if (!string.IsNullOrWhiteSpace(spriteHash))
                {
                    sourceSprites++;
                    var srcSpritePath = Path.Combine(stashVttDir, $"{spriteHash}_sprite.jpg");
                    if (TryCopyGeneratedFile(coveSceneId, "sprite", srcSpritePath, GetCoveSceneSpritePath(coveSceneId)))
                        migratedSprites++;
                }

                var vttHash = ResolveGeneratedHash(generatedData, stashConfig.VideoFileNamingAlgorithm, vttHashes);
                if (!string.IsNullOrWhiteSpace(vttHash))
                {
                    sourceVtts++;
                    var srcVttPath = Path.Combine(stashVttDir, $"{vttHash}_thumbs.vtt");
                    if (TryCopyGeneratedFile(coveSceneId, "VTT", srcVttPath, GetCoveSceneSpriteVttPath(coveSceneId)))
                        migratedVtts++;
                }

                if (processed % 25 == 0 || processed == totalScenes)
                    ReportPhase(progress, startProgress, endProgress, processed, totalScenes, $"Copying generated assets ({processed}/{totalScenes})");
            }

            _logger.LogInformation(
                "Migrated generated scene assets from Stash: screenshots {MigratedScreenshots}/{SourceScreenshots}, previews {MigratedPreviews}/{SourcePreviews}, sprites {MigratedSprites}/{SourceSprites}, vtt {MigratedVtts}/{SourceVtts}",
                migratedScreenshots,
                sourceScreenshots,
                migratedPreviews,
                sourcePreviews,
                migratedSprites,
                sourceSprites,
                migratedVtts,
                sourceVtts);

            var failedScreenshots = sourceScreenshots - migratedScreenshots;
            var failedPreviews = sourcePreviews - migratedPreviews;
            var failedSprites = sourceSprites - migratedSprites;
            var failedVtts = sourceVtts - migratedVtts;
            if (failedScreenshots + failedPreviews + failedSprites + failedVtts > 0)
            {
                _logger.LogWarning(
                    "Generated asset migration was incomplete: screenshots {FailedScreenshots}, previews {FailedPreviews}, sprites {FailedSprites}, VTTs {FailedVtts} could not be migrated",
                    failedScreenshots,
                    failedPreviews,
                    failedSprites,
                    failedVtts);
            }

            progress.Report(endProgress, "Generated scene assets copied");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to copy generated content");
        }
    }

    private static string? ResolveGeneratedHash(SceneGeneratedData generatedData, string preferredAlgorithm, HashSet<string> availableHashes)
    {
        if (availableHashes.Count == 0) return null;

        foreach (var candidate in EnumerateHashCandidates(generatedData, preferredAlgorithm))
        {
            if (!string.IsNullOrWhiteSpace(candidate) && availableHashes.Contains(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string?> EnumerateHashCandidates(SceneGeneratedData generatedData, string preferredAlgorithm)
    {
        if (string.Equals(preferredAlgorithm, "MD5", StringComparison.OrdinalIgnoreCase))
        {
            yield return generatedData.Md5;
            yield return generatedData.Oshash;
            yield break;
        }

        yield return generatedData.Oshash;
        yield return generatedData.Md5;
    }

    private bool TryCopyGeneratedFile(int sceneId, string assetType, string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
        {
            TraceGeneratedAssetMigrationFailure(_logger, null, assetType, sceneId, sourcePath);
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
            var copied = File.Exists(destinationPath);
            if (!copied)
                TraceGeneratedAssetMigrationFailure(_logger, null, assetType, sceneId, sourcePath);
            return copied;
        }
        catch (Exception ex)
        {
            TraceGeneratedAssetMigrationFailure(_logger, ex, assetType, sceneId, sourcePath);
            throw;
        }
    }

    private async Task<bool> TryWriteSceneScreenshotAsync(int sceneId, string blobId, CancellationToken ct)
    {
        try
        {
            var blob = await _blobService.GetBlobAsync(blobId, ct);
            if (blob == null)
            {
                TraceSceneScreenshotMigrationFailure(_logger, null, sceneId, blobId);
                return false;
            }

            await using var blobStream = blob.Value.Stream;
            var destinationPath = GetCoveSceneThumbnailPath(sceneId);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            if (string.Equals(blob.Value.ContentType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
            {
                await using var jpegOutput = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await blobStream.CopyToAsync(jpegOutput, ct);
                var written = File.Exists(destinationPath);
                if (!written)
                    TraceSceneScreenshotMigrationFailure(_logger, null, sceneId, blobId);
                return written;
            }

            await using var buffered = new MemoryStream();
            await blobStream.CopyToAsync(buffered, ct);
            buffered.Position = 0;

            using var image = await SixLabors.ImageSharp.Image.LoadAsync(buffered, ct);
            await using var convertedOutput = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await image.SaveAsync(convertedOutput, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 85 }, ct);
            var converted = File.Exists(destinationPath);
            if (!converted)
                TraceSceneScreenshotMigrationFailure(_logger, null, sceneId, blobId);
            return converted;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SixLabors.ImageSharp.InvalidImageContentException ex)
        {
            TraceSceneScreenshotMigrationFailure(_logger, ex, sceneId, blobId);
            return false;
        }
        catch (Exception ex)
        {
            TraceSceneScreenshotMigrationFailure(_logger, ex, sceneId, blobId);
            return false;
        }
    }

    private string GetCoveSceneThumbnailPath(int sceneId)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(sceneId)));
        return Path.Combine(_config.GeneratedPath, "screenshots", hash[..2], $"{sceneId}.jpg");
    }

    private string GetCoveScenePreviewPath(int sceneId)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(sceneId)));
        return Path.Combine(_config.GeneratedPath, "previews", hash[..2], $"{sceneId}.mp4");
    }

    private string GetCoveSceneSpritePath(int sceneId)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(sceneId)));
        return Path.Combine(_config.GeneratedPath, "vtt", hash[..2], $"{sceneId}_sprite.jpg");
    }

    private string GetCoveSceneSpriteVttPath(int sceneId)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(sceneId)));
        return Path.Combine(_config.GeneratedPath, "vtt", hash[..2], $"{sceneId}_thumbs.vtt");
    }

    private static string DetectImageContentType(Stream stream)
    {
        var originalPosition = stream.CanSeek ? stream.Position : 0;
        var buffer = new byte[256];
        var bytesRead = stream.Read(buffer, 0, buffer.Length);

        if (stream.CanSeek)
            stream.Position = originalPosition;

        if (bytesRead >= 4)
        {
            if (buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47)
                return "image/png";

            if (buffer[0] == 0xFF && buffer[1] == 0xD8 && buffer[2] == 0xFF)
                return "image/jpeg";

            if (buffer[0] == 0x47 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x38)
                return "image/gif";

            if (buffer[0] == 0x42 && buffer[1] == 0x4D)
                return "image/bmp";
        }

        if (bytesRead >= 12)
        {
            if (buffer[0] == 0x52 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x46
                && buffer[8] == 0x57 && buffer[9] == 0x45 && buffer[10] == 0x42 && buffer[11] == 0x50)
            {
                return "image/webp";
            }

            if (buffer[4] == 0x66 && buffer[5] == 0x74 && buffer[6] == 0x79 && buffer[7] == 0x70)
            {
                var brand = Encoding.ASCII.GetString(buffer, 8, 4);
                if (brand.StartsWith("avif", StringComparison.OrdinalIgnoreCase))
                    return "image/avif";
                if (brand.StartsWith("heic", StringComparison.OrdinalIgnoreCase))
                    return "image/heic";
            }
        }

        if (bytesRead >= 2 && buffer[0] == 0xFF && buffer[1] == 0x0A)
            return "image/jxl";

        if (bytesRead >= 8
            && buffer[0] == 0x00 && buffer[1] == 0x00 && buffer[2] == 0x00 && buffer[3] == 0x0C
            && buffer[4] == 0x4A && buffer[5] == 0x58 && buffer[6] == 0x4C && buffer[7] == 0x20)
        {
            return "image/jxl";
        }

        if (bytesRead > 0 && buffer[0] == 0x3C)
        {
            var head = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            if (head.Contains("<svg", StringComparison.OrdinalIgnoreCase))
                return "image/svg+xml";
        }

        return "image/jpeg";
    }

    private static StashConfigData ParseStashConfig(string configPath)
    {
        var paths = new List<(string Path, bool ExcludeImage, bool ExcludeVideo)>();
        var metadataServers = new List<StashMetadataServerConfig>();
        string? generatedPath = null;
        string? videoFileNamingAlgorithm = null;
        string? blobFilesPath = null;
        string? customPerformerImageLocation = null;
        bool? calculateMd5 = null;

        try
        {
            var lines = File.ReadAllLines(configPath);
            var inStashArray = false;
            var inStashBoxesArray = false;
            string? currentPath = null;
            var currentExcludeImage = false;
            var currentExcludeVideo = false;
            string? currentEndpoint = null;
            string? currentApiKey = null;
            string? currentName = null;
            var currentMaxRequestsPerMinute = 240;

            foreach (var rawLine in lines)
            {
                if (rawLine.Length > 0 && !char.IsWhiteSpace(rawLine[0]))
                {
                    if (inStashArray && !rawLine.StartsWith("stash:", StringComparison.OrdinalIgnoreCase))
                    {
                        if (currentPath != null)
                        {
                            paths.Add((currentPath, currentExcludeImage, currentExcludeVideo));
                            currentPath = null;
                        }

                        inStashArray = false;
                    }

                    if (inStashBoxesArray && !IsTopLevelSection(rawLine, "stash_boxes", "stashBoxes", "metadata_providers", "metadataProviders"))
                    {
                        if (!string.IsNullOrWhiteSpace(currentEndpoint))
                        {
                            metadataServers.Add(new StashMetadataServerConfig(
                                currentEndpoint,
                                currentApiKey ?? string.Empty,
                                string.IsNullOrWhiteSpace(currentName) ? currentEndpoint : currentName,
                                currentMaxRequestsPerMinute));
                        }

                        currentEndpoint = null;
                        currentApiKey = null;
                        currentName = null;
                        currentMaxRequestsPerMinute = 240;
                        inStashBoxesArray = false;
                    }
                }

                var genMatch = Regex.Match(rawLine, @"^generated:\s*(.+)$");
                if (genMatch.Success)
                {
                    generatedPath = genMatch.Groups[1].Value.Trim().Trim('"', '\'');
                    continue;
                }

                var algoMatch = Regex.Match(rawLine, @"^video_file_naming_algorithm:\s*(.+)$", RegexOptions.IgnoreCase);
                if (algoMatch.Success)
                {
                    videoFileNamingAlgorithm = algoMatch.Groups[1].Value.Trim().Trim('"', '\'');
                    continue;
                }

                var blobFilesMatch = Regex.Match(rawLine, @"^(blob_files|blob_files_path|blobs_path|blobFilesPath):\s*(.+)$", RegexOptions.IgnoreCase);
                if (blobFilesMatch.Success)
                {
                    blobFilesPath = blobFilesMatch.Groups[2].Value.Trim().Trim('"', '\'');
                    continue;
                }

                var customPerformerImageMatch = Regex.Match(rawLine, @"^custom_performer_image_location:\s*(.+)$", RegexOptions.IgnoreCase);
                if (customPerformerImageMatch.Success)
                {
                    customPerformerImageLocation = customPerformerImageMatch.Groups[1].Value.Trim().Trim('"', '\'');
                    continue;
                }

                var md5Match = Regex.Match(rawLine, @"^calculate_md5:\s*(true|false)$", RegexOptions.IgnoreCase);
                if (md5Match.Success)
                {
                    calculateMd5 = string.Equals(md5Match.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (rawLine.TrimStart().StartsWith("stash:"))
                {
                    inStashArray = true;
                    inStashBoxesArray = false;
                    continue;
                }

                if (IsTopLevelSection(rawLine, "stash_boxes", "stashBoxes", "metadata_providers", "metadataProviders"))
                {
                    inStashBoxesArray = true;
                    inStashArray = false;
                    continue;
                }

                if (inStashArray && rawLine.Length > 0 && !char.IsWhiteSpace(rawLine[0]) && !rawLine.TrimStart().StartsWith("-"))
                {
                    if (currentPath != null)
                    {
                        paths.Add((currentPath, currentExcludeImage, currentExcludeVideo));
                        currentPath = null;
                    }
                    inStashArray = false;
                    continue;
                }

                if (inStashArray)
                {
                    var trimmed = rawLine.TrimStart();
                    if (trimmed.StartsWith("- "))
                    {
                        if (currentPath != null)
                            paths.Add((currentPath, currentExcludeImage, currentExcludeVideo));
                        currentPath = null;
                        currentExcludeImage = false;
                        currentExcludeVideo = false;
                        trimmed = trimmed[2..].TrimStart();
                    }

                    var pathMatch = Regex.Match(trimmed, @"^path:\s*(.+)$");
                    if (pathMatch.Success)
                    {
                        currentPath = pathMatch.Groups[1].Value.Trim().Trim('"', '\'');
                        continue;
                    }

                    var exImgMatch = Regex.Match(trimmed, @"^excludeimage:\s*(true|false)$", RegexOptions.IgnoreCase);
                    if (exImgMatch.Success)
                    {
                        currentExcludeImage = string.Equals(exImgMatch.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    var exVidMatch = Regex.Match(trimmed, @"^excludevideo:\s*(true|false)$", RegexOptions.IgnoreCase);
                    if (exVidMatch.Success)
                    {
                        currentExcludeVideo = string.Equals(exVidMatch.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                }

                if (!inStashBoxesArray)
                    continue;

                var stashBoxLine = rawLine.TrimStart();
                if (stashBoxLine.StartsWith("- "))
                {
                    if (!string.IsNullOrWhiteSpace(currentEndpoint))
                    {
                        metadataServers.Add(new StashMetadataServerConfig(
                            currentEndpoint,
                            currentApiKey ?? string.Empty,
                            string.IsNullOrWhiteSpace(currentName) ? currentEndpoint : currentName,
                            currentMaxRequestsPerMinute));
                    }

                    currentEndpoint = null;
                    currentApiKey = null;
                    currentName = null;
                    currentMaxRequestsPerMinute = 240;
                    stashBoxLine = stashBoxLine[2..].TrimStart();
                }

                var endpointMatch = Regex.Match(stashBoxLine, @"^endpoint:\s*(.+)$", RegexOptions.IgnoreCase);
                if (endpointMatch.Success)
                {
                    currentEndpoint = endpointMatch.Groups[1].Value.Trim().Trim('"', '\'');
                    continue;
                }

                var apiKeyMatch = Regex.Match(stashBoxLine, @"^(api[_-]?key|apikey):\s*(.+)$", RegexOptions.IgnoreCase);
                if (apiKeyMatch.Success)
                {
                    currentApiKey = apiKeyMatch.Groups[2].Value.Trim().Trim('"', '\'');
                    continue;
                }

                var nameMatch = Regex.Match(stashBoxLine, @"^name:\s*(.+)$", RegexOptions.IgnoreCase);
                if (nameMatch.Success)
                {
                    currentName = nameMatch.Groups[1].Value.Trim().Trim('"', '\'');
                    continue;
                }

                var maxRequestsMatch = Regex.Match(stashBoxLine, @"^(max_requests_per_minute|maxRequestsPerMinute):\s*(-?\d+)$", RegexOptions.IgnoreCase);
                if (maxRequestsMatch.Success && int.TryParse(maxRequestsMatch.Groups[2].Value, out var maxRequestsPerMinute))
                {
                    currentMaxRequestsPerMinute = maxRequestsPerMinute;
                    continue;
                }
            }

            if (inStashArray && currentPath != null)
                paths.Add((currentPath, currentExcludeImage, currentExcludeVideo));

            if (inStashBoxesArray && !string.IsNullOrWhiteSpace(currentEndpoint))
            {
                metadataServers.Add(new StashMetadataServerConfig(
                    currentEndpoint,
                    currentApiKey ?? string.Empty,
                    string.IsNullOrWhiteSpace(currentName) ? currentEndpoint : currentName,
                    currentMaxRequestsPerMinute));
            }
        }
        catch (Exception)
        {
        }

        var configDirectory = Path.GetDirectoryName(configPath) ?? string.Empty;

        // Stash records absolute paths in config.yml that reflect wherever Stash itself ran
        // (e.g. "/root/.stash/blobs", "/root/.stash/generated"). When a user mounts their Stash
        // data directory into Cove at a different location, those absolute paths no longer resolve
        // even though the data sits right next to config.yml/stash-go.sqlite. When the configured
        // path is unset OR points somewhere that does not exist, fall back to Stash's default
        // "<config_dir>/<name>" layout if it exists on disk. This also covers "Migrate blobs to
        // filesystem" runs that leave no "blobs_path:" key. The directory-exists check keeps
        // inline-blob / generated-less libraries from adopting a bogus path (and, for blobs,
        // preserves the configured value so the "does not exist" warning still names it).
        var resolvedBlobFilesPath = ResolveConfigDirDefault(
            ResolveStashConfigPath(configDirectory, blobFilesPath), configDirectory, "blobs");
        var resolvedGeneratedPath = ResolveConfigDirDefault(
            ResolveStashConfigPath(configDirectory, generatedPath), configDirectory, "generated");

        return new StashConfigData(
            paths
                .Select(path => (ResolveStashConfigPath(configDirectory, path.Path) ?? path.Path, path.ExcludeImage, path.ExcludeVideo))
                .ToList(),
            resolvedGeneratedPath,
            videoFileNamingAlgorithm ?? (calculateMd5 == true ? "MD5" : "OSHASH"),
            resolvedBlobFilesPath,
            ResolveStashConfigPath(configDirectory, customPerformerImageLocation),
            metadataServers);

        static bool IsTopLevelSection(string rawLine, params string[] names)
        {
            if (rawLine.Length > 0 && char.IsWhiteSpace(rawLine[0]))
                return false;

            var trimmed = rawLine.TrimStart();
            return names.Any(name => trimmed.StartsWith($"{name}:", StringComparison.OrdinalIgnoreCase));
        }
    }

    // Prefers the path resolved from config.yml, but when that path is unset or does not exist on
    // disk, falls back to Stash's default "<config_dir>/<defaultFolderName>" location if it exists.
    // Returns the original resolved path otherwise so callers still log/report the configured value.
    private static string? ResolveConfigDirDefault(string? resolvedPath, string configDirectory, string defaultFolderName)
    {
        if (!string.IsNullOrWhiteSpace(resolvedPath) && Directory.Exists(resolvedPath))
            return resolvedPath;

        if (!string.IsNullOrEmpty(configDirectory))
        {
            var fallback = Path.Combine(configDirectory, defaultFolderName);
            if (Directory.Exists(fallback))
                return fallback;
        }

        return resolvedPath;
    }

    private static string? ResolveStashConfigPath(string configDirectory, string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        var trimmed = configuredPath.Trim();
        if (Path.IsPathRooted(trimmed))
            return Path.GetFullPath(trimmed);
        if (IsWindowsAbsolutePath(trimmed))
            return NormalizeImportedPath(trimmed);

        return Path.GetFullPath(Path.Combine(configDirectory, trimmed));
    }

    private static bool IsWindowsAbsolutePath(string path)
        => Regex.IsMatch(path, @"^[A-Za-z]:[\\/]")
            || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal);

    private static bool TryResolveStashBlobFilePath(string blobFilesPath, string checksum, out string sourcePath)
    {
        foreach (var candidate in EnumerateStashBlobPathCandidates(blobFilesPath, checksum))
        {
            if (File.Exists(candidate))
            {
                sourcePath = candidate;
                return true;
            }
        }

        var bucket = checksum.Length >= 2 ? checksum[..2] : checksum;
        var bucketPath = Path.Combine(blobFilesPath, bucket);
        if (Directory.Exists(bucketPath))
        {
            var match = Directory.EnumerateFiles(bucketPath, $"{checksum}.*", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (match is not null)
            {
                sourcePath = match;
                return true;
            }
        }

        if (checksum.Length >= 4)
        {
            var nestedBucketPath = Path.Combine(blobFilesPath, checksum[..2], checksum[2..4]);
            if (Directory.Exists(nestedBucketPath))
            {
                var match = Directory.EnumerateFiles(nestedBucketPath, $"{checksum}.*", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (match is not null)
                {
                    sourcePath = match;
                    return true;
                }
            }
        }

        if (Directory.Exists(blobFilesPath))
        {
            var match = Directory.EnumerateFiles(blobFilesPath, $"{checksum}.*", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (match is not null)
            {
                sourcePath = match;
                return true;
            }
        }

        sourcePath = string.Empty;
        return false;
    }

    private static IEnumerable<string> EnumerateStashBlobPathCandidates(string blobFilesPath, string checksum)
    {
        yield return Path.Combine(blobFilesPath, checksum);

        if (checksum.Length >= 2)
        {
            yield return Path.Combine(blobFilesPath, checksum[..2], checksum);
        }

        if (checksum.Length >= 4)
        {
            yield return Path.Combine(blobFilesPath, checksum[..2], checksum[2..4], checksum);
        }
    }
}
