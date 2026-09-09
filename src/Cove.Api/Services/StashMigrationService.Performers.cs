using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using Cove.Data.Services;
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
            string? CareerLength, string? CareerStart, string? CareerEnd, string? DeathDate,
            string? Tattoos, string? Piercings, bool Favorite, int? Rating, string? Details,
            string? ImageBlob, string CreatedAt, string UpdatedAt)>();
        var hasCareerLength = await ColumnExistsAsync(conn, "performers", "career_length", ct);
        var hasCareerStart = await ColumnExistsAsync(conn, "performers", "career_start", ct);
        var hasCareerEnd = await ColumnExistsAsync(conn, "performers", "career_end", ct);
        var hasBirthdatePrecision = await ColumnExistsAsync(conn, "performers", "birthdate_precision", ct);
        var hasDeathDatePrecision = await ColumnExistsAsync(conn, "performers", "death_date_precision", ct);
        var hasCareerStartPrecision = hasCareerStart && await ColumnExistsAsync(conn, "performers", "career_start_precision", ct);
        var hasCareerEndPrecision = hasCareerEnd && await ColumnExistsAsync(conn, "performers", "career_end_precision", ct);
        await using (var cmd = conn.CreateCommand())
        {
            var careerLengthExpr = hasCareerLength ? "career_length" : "NULL";
            var careerStartExpr = hasCareerStart ? PartialDateSql("career_start", hasCareerStartPrecision) : "NULL";
            var careerEndExpr = hasCareerEnd ? PartialDateSql("career_end", hasCareerEndPrecision) : "NULL";
            cmd.CommandText = @"SELECT id, name, disambiguation, gender, " + PartialDateSql("birthdate", hasBirthdatePrecision) + @" AS birthdate, ethnicity, country, eye_color,
                hair_color, height, weight, measurements, fake_tits, penis_length, circumcised, " + careerLengthExpr + @" AS career_length,
                " + careerStartExpr + @" AS career_start, " + careerEndExpr + @" AS career_end,
                " + PartialDateSql("death_date", hasDeathDatePrecision) + @" AS death_date, tattoos, piercings, favorite, rating, details, image_blob, created_at, updated_at
                FROM performers";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add((r.GetInt32(0), r.GetString(1), ReadStringNull(r, 2), ReadStringNull(r, 3),
                    ReadStringNull(r, 4), ReadStringNull(r, 5), ReadStringNull(r, 6), ReadStringNull(r, 7),
                    ReadStringNull(r, 8), ReadIntNull(r, 9), ReadIntNull(r, 10), ReadStringNull(r, 11),
                    ReadStringNull(r, 12), r.IsDBNull(13) ? null : (double?)r.GetDouble(13),
                    ReadStringNull(r, 14), ReadStringNull(r, 15), ReadStringNull(r, 16),
                    ReadStringNull(r, 17), ReadStringNull(r, 18), ReadStringNull(r, 19), ReadStringNull(r, 20),
                    ReadBool(r, 21), ReadIntNull(r, 22), ReadStringNull(r, 23), ReadStringNull(r, 24),
                    r.GetString(25), r.GetString(26)));
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

        var customPerformerImageFiles = GetCustomPerformerImageFiles(_currentImportCustomPerformerImageLocation);
        var customPerformerImageBlobIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var customPerformerImageFailureCount = 0;
        var failedCustomPerformerImageSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

        var identityKeys = rows
            .Select(row => EntityNameRules.PerformerIdentityKey(row.Name, row.Disambiguation))
            .ToHashSet(StringComparer.Ordinal);
        var existingCandidates = await _db.Performers
            .Include(performer => performer.Urls)
            .Include(performer => performer.Aliases)
            .Include(performer => performer.PerformerTags)
            .Include(performer => performer.RemoteIds)
            .OrderBy(performer => performer.Id)
            .ToListAsync(ct);
        var existingByIdentity = new Dictionary<string, Performer>(StringComparer.Ordinal);
        foreach (var existing in existingCandidates)
        {
            var identityKey = EntityNameRules.PerformerIdentityKey(existing.Name, existing.Disambiguation);
            if (!identityKeys.Contains(identityKey))
                continue;
            if (!existingByIdentity.TryAdd(identityKey, existing))
                throw new EntityNameConflictException(NameConflictEntityTypes.Performer);
        }

        var byId = rows.ToDictionary(row => row.Id);
        var groups = rows
            .OrderBy(row => row.Id)
            .GroupBy(row => EntityNameRules.PerformerIdentityKey(row.Name, row.Disambiguation), StringComparer.Ordinal)
            .Select(group => (IdentityKey: group.Key, StashIds: (IReadOnlyList<int>)group.Select(row => row.Id).ToArray()))
            .ToArray();
        var idMap = new Dictionary<int, int>(rows.Count);
        const int PerformerBatchSize = 500;
        var pendingBatch = new List<(IReadOnlyList<int> StashIds, Performer Entity)>();
        var pendingSourceCount = 0;

        async Task FlushPerformerBatchAsync()
        {
            if (pendingBatch.Count == 0)
                return;

            _db.ChangeTracker.DetectChanges();
            await _db.SaveChangesAsync(ct);
            foreach (var (stashIds, entity) in pendingBatch)
                foreach (var stashId in stashIds)
                    idMap[stashId] = entity.Id;

            pendingBatch.Clear();
            pendingSourceCount = 0;
            _db.ChangeTracker.Clear();
            ReportPhase(progress, startProgress, endProgress, idMap.Count, rows.Count, $"Importing performers ({idMap.Count}/{rows.Count})");
            _logger.LogDebug(
                "[StashTiming] phase=performers checkpoint=batch imported={Imported} total={Total} elapsedMs={ElapsedMilliseconds:F0}",
                idMap.Count,
                rows.Count,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        async Task MergeImportedPerformerMetadataAsync(Performer entity, int stashId)
        {
            var row = byId[stashId];
            static string? FirstText(string? current, string? incoming)
                => !string.IsNullOrWhiteSpace(current) ? current : incoming;

            entity.Gender ??= ParseGender(row.Gender);
            if (entity.Birthdate is null)
            {
                var date = ParsePartialDate(row.Birthdate);
                entity.Birthdate = date.Value;
                entity.BirthdatePrecision = date.Precision;
            }
            entity.Ethnicity = FirstText(entity.Ethnicity, row.Ethnicity);
            entity.Country = FirstText(entity.Country, row.Country);
            entity.EyeColor = FirstText(entity.EyeColor, row.EyeColor);
            entity.HairColor = FirstText(entity.HairColor, row.HairColor);
            entity.HeightCm ??= row.Height;
            entity.Weight ??= row.Weight;
            entity.Measurements = FirstText(entity.Measurements, row.Measurements);
            entity.FakeTits = FirstText(entity.FakeTits, row.FakeTits);
            entity.PenisLength ??= row.PenisLength;
            entity.Circumcised ??= ParseCircumcised(row.Circumcised);
            var (legacyCareerStart, legacyCareerEnd) = ParseCareerLength(row.CareerLength);
            if (entity.CareerStart is null)
            {
                var date = ParsePartialDate(row.CareerStart);
                entity.CareerStart = date.Value ?? legacyCareerStart;
                entity.CareerStartPrecision = date.Value.HasValue ? date.Precision : DatePrecision.Year;
            }
            if (entity.CareerEnd is null)
            {
                var date = ParsePartialDate(row.CareerEnd);
                entity.CareerEnd = date.Value ?? legacyCareerEnd;
                entity.CareerEndPrecision = date.Value.HasValue ? date.Precision : DatePrecision.Year;
            }
            if (entity.DeathDate is null)
            {
                var date = ParsePartialDate(row.DeathDate);
                entity.DeathDate = date.Value;
                entity.DeathDatePrecision = date.Precision;
            }
            entity.Tattoos = FirstText(entity.Tattoos, row.Tattoos);
            entity.Piercings = FirstText(entity.Piercings, row.Piercings);
            entity.Details = FirstText(entity.Details, row.Details);
            entity.Favorite |= row.Favorite;

            string? imageBlobId = null;
            if (string.IsNullOrWhiteSpace(entity.ImageBlobId))
            {
                imageBlobId = GetBlobId(blobMap, row.ImageBlob);
                if (imageBlobId is null && string.IsNullOrWhiteSpace(row.ImageBlob))
                {
                    var customImageImport = await TryImportCustomPerformerImageAsync(
                        customPerformerImageFiles,
                        customPerformerImageBlobIds,
                        row.Name,
                        ct);
                    imageBlobId = customImageImport.BlobId;
                    if (customImageImport.FailedSourcePath is not null)
                    {
                        customPerformerImageFailureCount++;
                        failedCustomPerformerImageSources.Add(customImageImport.FailedSourcePath);
                    }
                }
            }
            entity.ImageBlobId = FirstText(entity.ImageBlobId, imageBlobId);

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

            var urlKeys = entity.Urls.Select(item => item.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var value in performerUrls)
                if (urlKeys.Add(value))
                    entity.Urls.Add(new PerformerUrl { Url = value });
            var aliasKeys = entity.Aliases.Select(item => item.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var value in performerAliases)
                if (aliasKeys.Add(value))
                    entity.Aliases.Add(new PerformerAlias { Alias = value });
            var tagKeys = entity.PerformerTags.Select(item => item.TagId).ToHashSet();
            foreach (var tagId in performerTags)
                if (tagKeys.Add(tagId))
                    entity.PerformerTags.Add(new PerformerTag { TagId = tagId });
            var remoteKeys = entity.RemoteIds.Select(item => (item.Endpoint, item.RemoteId)).ToHashSet();
            foreach (var (endpoint, remoteId) in performerRemoteIds)
                if (remoteKeys.Add((endpoint, remoteId)))
                    entity.RemoteIds.Add(new PerformerRemoteId { Endpoint = endpoint, RemoteId = remoteId });
        }

        foreach (var group in groups)
        {
            var firstRow = byId[group.StashIds[0]];
            Performer entity;
            if (existingByIdentity.TryGetValue(group.IdentityKey, out var existing))
            {
                entity = existing;
                if (_db.Entry(entity).State == EntityState.Detached)
                    _db.Performers.Attach(entity);
            }
            else
            {
                entity = new Performer
                {
                    Name = EntityNameRules.NormalizeCanonicalName(firstRow.Name),
                    Disambiguation = EntityNameRules.NormalizeDisambiguation(firstRow.Disambiguation),
                    CreatedAt = ParseDateTime(firstRow.CreatedAt),
                    UpdatedAt = ParseDateTime(firstRow.UpdatedAt),
                };
                _db.Performers.Add(entity);
            }

            foreach (var stashId in group.StashIds)
                await MergeImportedPerformerMetadataAsync(entity, stashId);
            pendingBatch.Add((group.StashIds, entity));
            pendingSourceCount += group.StashIds.Count;

            if (pendingSourceCount >= PerformerBatchSize)
                await FlushPerformerBatchAsync();
        }

        await FlushPerformerBatchAsync();
        await AddImportedOverallRatingsAsync(
            rows.OrderByDescending(row => row.Id).Select(row => new ImportedRatingSeed(row.Id, row.Rating)),
            idMap,
            RatingHostType.Performer,
            ct);

        if (customPerformerImageFailureCount > 0)
        {
            _logger.LogWarning(
                "Failed to import custom images for {AffectedPerformerCount} performers from {FailedSourceCount} source files",
                customPerformerImageFailureCount,
                failedCustomPerformerImageSources.Count);
        }

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

    private async Task<(string? BlobId, string? FailedSourcePath)> TryImportCustomPerformerImageAsync(
        IReadOnlyList<string> customPerformerImageFiles,
        Dictionary<string, string> customPerformerImageBlobIds,
        string performerName,
        CancellationToken ct)
    {
        if (customPerformerImageFiles.Count == 0 || string.IsNullOrWhiteSpace(performerName))
            return (null, null);

        var fileIndex = (int)(ComputeStablePerformerImageHash(performerName) % (ulong)customPerformerImageFiles.Count);
        var sourcePath = customPerformerImageFiles[fileIndex];
        if (customPerformerImageBlobIds.TryGetValue(sourcePath, out var existingBlobId))
            return (existingBlobId, null);

        try
        {
            await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            var contentType = DetectImageContentType(sourceStream);
            var blobId = await _blobService.StoreBlobAsync(sourceStream, contentType, ct);
            customPerformerImageBlobIds[sourcePath] = blobId;
            return (blobId, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            TraceCustomPerformerImageImportFailure(_logger, ex, sourcePath);
            return (null, sourcePath);
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
