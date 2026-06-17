using Microsoft.Data.Sqlite;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using System.Text;

namespace Cove.Api.Services;

public partial class StashMigrationService
{
    private static readonly HashSet<string> SupportedCustomPerformerImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif",
        ".bmp",
        ".gif",
        ".heic",
        ".jpeg",
        ".jpg",
        ".jxl",
        ".png",
        ".svg",
        ".webp",
    };

    private async Task<Dictionary<int, int>> ImportPerformersAsync(SqliteConnection conn, Dictionary<string, string> blobMap, Dictionary<int, int> tagIdMap, IJobProgress progress, double startProgress, double endProgress, CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var rows = new List<(int Id, string Name, string? Disambiguation, string? Gender, string? Birthdate,
            string? Ethnicity, string? Country, string? EyeColor, string? HairColor, int? Height, int? Weight,
            string? Measurements, string? FakeTits, double? PenisLength, string? Circumcised,
            string? CareerLength, string? DeathDate,
            string? Tattoos, string? Piercings, bool Favorite, int? Rating, string? Details,
            string? ImageBlob, string CreatedAt, string UpdatedAt)>();
        var hasCareerLength = await ColumnExistsAsync(conn, "performers", "career_length", ct);
        await using (var cmd = conn.CreateCommand())
        {
            var careerLengthExpr = hasCareerLength ? "career_length" : "NULL";
            cmd.CommandText = @"SELECT id, name, disambiguation, gender, birthdate, ethnicity, country, eye_color,
                hair_color, height, weight, measurements, fake_tits, penis_length, circumcised, " + careerLengthExpr + @" AS career_length,
                death_date, tattoos, piercings, favorite, rating, details, image_blob, created_at, updated_at
                FROM performers";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add((r.GetInt32(0), r.GetString(1), ReadStringNull(r, 2), ReadStringNull(r, 3),
                    ReadStringNull(r, 4), ReadStringNull(r, 5), ReadStringNull(r, 6), ReadStringNull(r, 7),
                    ReadStringNull(r, 8), ReadIntNull(r, 9), ReadIntNull(r, 10), ReadStringNull(r, 11),
                    ReadStringNull(r, 12), r.IsDBNull(13) ? null : (double?)r.GetDouble(13),
                    ReadStringNull(r, 14), ReadStringNull(r, 15), ReadStringNull(r, 16),
                    ReadStringNull(r, 17), ReadStringNull(r, 18), ReadBool(r, 19), ReadIntNull(r, 20),
                    ReadStringNull(r, 21), ReadStringNull(r, 22), r.GetString(23), r.GetString(24)));
        }
        var urls = await ReadUrlsAsync(conn, "performer_urls", "performer_id", ct);
        var aliases = await ReadAliasesAsync(conn, "performer_aliases", "performer_id", ct);
        var performerTagMap = await ReadJunctionAsync(conn, "performers_tags", "performer_id", "tag_id", ct);

        var performerStashIds = new Dictionary<int, List<(string Ep, string Rid)>>();
        if (await TableExistsAsync(conn, "performer_stash_ids", ct))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT performer_id, endpoint, stash_id FROM performer_stash_ids";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var pId = r.GetInt32(0);
                if (!performerStashIds.TryGetValue(pId, out var list)) performerStashIds[pId] = list = [];
                list.Add((r.GetString(1), r.GetString(2)));
            }
        }

        var idMap = new Dictionary<int, int>(rows.Count);
        var customPerformerImageFiles = GetCustomPerformerImageFiles(_currentImportCustomPerformerImageLocation);
        var customPerformerImageBlobIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        const int PerformerBatchSize = 500;
        var pendingBatch = new List<(int StashId, Performer Entity)>(PerformerBatchSize);
        progress.Report(startProgress, "Importing performers...");
        if (!string.IsNullOrWhiteSpace(_currentImportCustomPerformerImageLocation)
            && customPerformerImageFiles.Count == 0
            && !Directory.Exists(_currentImportCustomPerformerImageLocation))
        {
            _logger.LogWarning(
                "Configured Stash custom performer image location does not exist: {Path}",
                _currentImportCustomPerformerImageLocation);
        }

        _logger.LogDebug(
            "[StashTiming] phase=performers checkpoint=loaded rows={Rows} urlOwners={UrlOwners} aliasOwners={AliasOwners} tagOwners={TagOwners} remoteIdOwners={RemoteIdOwners} elapsedMs={ElapsedMilliseconds:F0}",
            rows.Count,
            urls.Count,
            aliases.Count,
            performerTagMap.Count,
            performerStashIds.Count,
            stopwatch.Elapsed.TotalMilliseconds);

        async Task FlushPerformerBatchAsync()
        {
            if (pendingBatch.Count == 0)
                return;

            await _db.SaveChangesAsync(ct);
            foreach (var (stashId, entity) in pendingBatch)
                idMap[stashId] = entity.Id;

            pendingBatch.Clear();
            _db.ChangeTracker.Clear();
            ReportPhase(progress, startProgress, endProgress, idMap.Count, rows.Count, $"Importing performers ({idMap.Count}/{rows.Count})");
            _logger.LogDebug(
                "[StashTiming] phase=performers checkpoint=batch imported={Imported} total={Total} elapsedMs={ElapsedMilliseconds:F0}",
                idMap.Count,
                rows.Count,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        foreach (var row in rows)
        {
            var performerUrls = urls.GetValueOrDefault(row.Id, [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var performerAliases = aliases.GetValueOrDefault(row.Id, [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var performerTags = performerTagMap.GetValueOrDefault(row.Id, [])
                .Where(tagIdMap.ContainsKey)
                .Distinct()
                .Select(tagId => tagIdMap[tagId])
                .ToList();
            var performerRemoteIds = performerStashIds.GetValueOrDefault(row.Id, [])
                .DistinctBy(s => (s.Ep, s.Rid))
                .ToList();
            var (careerStart, careerEnd) = ParseCareerLength(row.CareerLength);
            var imageBlobId = GetBlobId(blobMap, row.ImageBlob);
            if (imageBlobId is null && string.IsNullOrWhiteSpace(row.ImageBlob))
            {
                imageBlobId = await TryImportCustomPerformerImageAsync(
                    customPerformerImageFiles,
                    customPerformerImageBlobIds,
                    row.Name,
                    ct);
            }

            var entity = new Performer
            {
                Name = row.Name,
                Disambiguation = row.Disambiguation,
                Gender = ParseGender(row.Gender),
                Birthdate = ParseDate(row.Birthdate),
                Ethnicity = row.Ethnicity,
                Country = row.Country,
                EyeColor = row.EyeColor,
                HairColor = row.HairColor,
                HeightCm = row.Height,
                Weight = row.Weight,
                Measurements = row.Measurements,
                FakeTits = row.FakeTits,
                PenisLength = row.PenisLength,
                Circumcised = ParseCircumcised(row.Circumcised),
                CareerStart = careerStart,
                CareerEnd = careerEnd,
                DeathDate = ParseDate(row.DeathDate),
                Tattoos = row.Tattoos,
                Piercings = row.Piercings,
                Favorite = row.Favorite,
                Details = row.Details,
                ImageBlobId = imageBlobId,
                Urls = performerUrls.Select(url => new PerformerUrl { Url = url }).ToList(),
                Aliases = performerAliases.Select(alias => new PerformerAlias { Alias = alias }).ToList(),
                PerformerTags = performerTags.Select(tagId => new PerformerTag { TagId = tagId }).ToList(),
                RemoteIds = performerRemoteIds.Select(remoteId => new PerformerRemoteId { Endpoint = remoteId.Ep, RemoteId = remoteId.Rid }).ToList(),
                CreatedAt = ParseDateTime(row.CreatedAt),
                UpdatedAt = ParseDateTime(row.UpdatedAt),
            };
            _db.Performers.Add(entity);
            pendingBatch.Add((row.Id, entity));

            if (pendingBatch.Count >= PerformerBatchSize)
                await FlushPerformerBatchAsync();
        }

        await FlushPerformerBatchAsync();
        await AddImportedOverallRatingsAsync(
            rows.Select(row => new ImportedRatingSeed(row.Id, row.Rating)),
            idMap,
            RatingHostType.Performer,
            ct);

        _logger.LogInformation("Imported {Count} performers in {Elapsed}", idMap.Count, stopwatch.Elapsed);
        return idMap;
    }

    private static IReadOnlyList<string> GetCustomPerformerImageFiles(string? customPerformerImageLocation)
    {
        if (string.IsNullOrWhiteSpace(customPerformerImageLocation) || !Directory.Exists(customPerformerImageLocation))
            return [];

        return Directory.EnumerateFiles(customPerformerImageLocation, "*", SearchOption.AllDirectories)
            .Where(path => SupportedCustomPerformerImageExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => Path.GetRelativePath(customPerformerImageLocation, path).Replace('\\', '/'), StringComparer.Ordinal)
            .ToList();
    }

    private async Task<string?> TryImportCustomPerformerImageAsync(
        IReadOnlyList<string> customPerformerImageFiles,
        Dictionary<string, string> customPerformerImageBlobIds,
        string performerName,
        CancellationToken ct)
    {
        if (customPerformerImageFiles.Count == 0 || string.IsNullOrWhiteSpace(performerName))
            return null;

        var fileIndex = (int)(ComputeStablePerformerImageHash(performerName) % (ulong)customPerformerImageFiles.Count);
        var sourcePath = customPerformerImageFiles[fileIndex];
        if (customPerformerImageBlobIds.TryGetValue(sourcePath, out var existingBlobId))
            return existingBlobId;

        try
        {
            await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            var contentType = DetectImageContentType(sourceStream);
            var blobId = await _blobService.StoreBlobAsync(sourceStream, contentType, ct);
            customPerformerImageBlobIds[sourcePath] = blobId;
            return blobId;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to import Stash custom performer image from {Path}", sourcePath);
            return null;
        }
    }

    private static ulong ComputeStablePerformerImageHash(string performerName)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offsetBasis;
        foreach (var value in Encoding.UTF8.GetBytes(performerName))
        {
            hash ^= value;
            hash *= prime;
        }

        return hash;
    }

    private async Task SaveImportedPerformerChildrenAsync(
        int performerId,
        IReadOnlyCollection<string> performerUrls,
        IReadOnlyCollection<string> performerAliases,
        IReadOnlyCollection<int> performerTags,
        IReadOnlyCollection<(string Ep, string Rid)> performerRemoteIds,
        CancellationToken ct)
    {
        if (performerUrls.Count > 0)
        {
            _db.ChangeTracker.Clear();
            _db.Set<PerformerUrl>().AddRange(performerUrls.Select(url => new PerformerUrl
            {
                PerformerId = performerId,
                Url = url,
            }));
            await _db.SaveChangesAsync(ct);
        }

        if (performerAliases.Count > 0)
        {
            _db.ChangeTracker.Clear();
            _db.Set<PerformerAlias>().AddRange(performerAliases.Select(alias => new PerformerAlias
            {
                PerformerId = performerId,
                Alias = alias,
            }));
            await _db.SaveChangesAsync(ct);
        }

        if (performerTags.Count > 0)
        {
            _db.ChangeTracker.Clear();
            _db.Set<PerformerTag>().AddRange(performerTags.Select(tagId => new PerformerTag
            {
                PerformerId = performerId,
                TagId = tagId,
            }));
            await _db.SaveChangesAsync(ct);
        }

        if (performerRemoteIds.Count > 0)
        {
            _db.ChangeTracker.Clear();
            _db.Set<PerformerRemoteId>().AddRange(performerRemoteIds.Select(remoteId => new PerformerRemoteId
            {
                PerformerId = performerId,
                Endpoint = remoteId.Ep,
                RemoteId = remoteId.Rid,
            }));
            await _db.SaveChangesAsync(ct);
        }

        _db.ChangeTracker.Clear();
    }
}