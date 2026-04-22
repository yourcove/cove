using Microsoft.Data.Sqlite;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Cove.Api.Services;

public record StashPreviewResult(bool IsValid, string? Error, int Scenes, int Performers, int Tags, int Studios, int Groups, int Images, int Galleries);
public record StashImportResult(int Scenes, int Performers, int Tags, int Studios, int Groups, int Images, int Galleries);
public record StashImportOptions(string? GeneratedPath, bool MigrateGeneratedContent = true);

public sealed class StashMigrationInProgressException(string message) : InvalidOperationException(message);

public class StashMigrationService(CoveContext db, IBlobService blobService, ConfigService configService, CoveConfiguration config, IJobService jobService, IServiceScopeFactory scopeFactory, ILogger<StashMigrationService> logger)
{
    private sealed record SceneGeneratedData(string? Oshash, string? Md5, string? CoverBlobId);
    private sealed record StashConfigData(
        List<(string Path, bool ExcludeImage, bool ExcludeVideo)> Paths,
        string? GeneratedPath,
        string VideoFileNamingAlgorithm);

    private static readonly object ImportSync = new();
    private static readonly Queue<string> importResultOrder = new();
    private static readonly Dictionary<string, StashImportResult> importResults = [];
    private static string? activeImportPath;
    private static string? activeImportJobId;

    private const double BlobsStart = 0.02;
    private const double BlobsEnd = 0.08;
    private const double FoldersStart = 0.08;
    private const double FoldersEnd = 0.12;
    private const double StudiosStart = 0.12;
    private const double StudiosEnd = 0.18;
    private const double TagsStart = 0.18;
    private const double TagsEnd = 0.24;
    private const double PerformersStart = 0.24;
    private const double PerformersEnd = 0.34;
    private const double GroupsStart = 0.34;
    private const double GroupsEnd = 0.38;
    private const double ScenesStart = 0.38;
    private const double ScenesEnd = 0.68;
    private const double ImagesStart = 0.68;
    private const double ImagesEnd = 0.92;
    private const double GalleriesStart = 0.92;
    private const double GalleriesEnd = 0.97;
    private const double LibraryPathsStart = 0.97;
    private const double LibraryPathsEnd = 0.985;
    private const double GeneratedAssetsStart = 0.985;
    private const double GeneratedAssetsEnd = 1.0;

    public async Task<StashPreviewResult> PreviewAsync(string stashDbPath, CancellationToken ct = default)
    {
        if (!File.Exists(stashDbPath))
            return new StashPreviewResult(false, $"Database file not found: {stashDbPath}", 0, 0, 0, 0, 0, 0, 0);
        try
        {
            var cs = OpenReadOnly(stashDbPath);
            await using var conn = new SqliteConnection(cs);
            await conn.OpenAsync(ct);
            return new StashPreviewResult(true, null,
                await CountAsync(conn, "scenes", ct),
                await CountAsync(conn, "performers", ct),
                await CountAsync(conn, "tags", ct),
                await CountAsync(conn, "studios", ct),
                await CountAsync(conn, "groups", ct),
                await CountAsync(conn, "images", ct),
                await CountAsync(conn, "galleries", ct));
        }
        catch (Exception ex)
        {
            return new StashPreviewResult(false, ex.Message, 0, 0, 0, 0, 0, 0, 0);
        }
    }

    public Task<StashImportResult> ImportAsync(string stashDbPath, StashImportOptions? options = null, CancellationToken ct = default)
    {
        return RunImportAsync(stashDbPath, options, NullJobProgress.Instance, ct);
    }

    public string StartImport(string stashDbPath, StashImportOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(stashDbPath))
            throw new ArgumentException("Stash database path is required.", nameof(stashDbPath));

        options ??= new StashImportOptions(null, true);

        lock (ImportSync)
        {
            if (!string.IsNullOrWhiteSpace(activeImportJobId))
            {
                if (!string.Equals(activeImportPath, stashDbPath, StringComparison.OrdinalIgnoreCase))
                    throw new StashMigrationInProgressException($"A Stash migration is already running for {activeImportPath}.");

                logger.LogInformation("Stash migration already running for {Path}; joining existing import", stashDbPath);
                return activeImportJobId;
            }

            activeImportPath = stashDbPath;
            string? jobId = null;
            jobId = jobService.Enqueue("stash-import", "Importing Stash library", async (progress, ct) =>
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var scopedMigration = scope.ServiceProvider.GetRequiredService<StashMigrationService>();
                    var result = await scopedMigration.RunImportAsync(stashDbPath, options, progress, ct);

                    lock (ImportSync)
                    {
                        importResults[jobId!] = result;
                        importResultOrder.Enqueue(jobId!);
                        TrimImportResultsLocked();
                    }
                }
                finally
                {
                    lock (ImportSync)
                    {
                        if (string.Equals(activeImportJobId, jobId, StringComparison.OrdinalIgnoreCase))
                        {
                            activeImportJobId = null;
                            activeImportPath = null;
                        }
                    }
                }
            });

            activeImportJobId = jobId;
            return jobId;
        }
    }

    public StashImportResult? GetImportResult(string jobId)
    {
        lock (ImportSync)
        {
            return importResults.TryGetValue(jobId, out var result) ? result : null;
        }
    }

    public async Task<StashImportResult> RunImportAsync(string stashDbPath, StashImportOptions? options, IJobProgress progress, CancellationToken ct = default)
    {
        options ??= new StashImportOptions(null, true);
        progress.Report(0.01, "Opening Stash database...");
        var result = await ImportCoreAsync(stashDbPath, options, progress, ct);
        progress.Report(1.0, "Import complete");
        return result;
    }

    private async Task<StashImportResult> ImportCoreAsync(string stashDbPath, StashImportOptions options, IJobProgress progress, CancellationToken ct)
    {
        if (!File.Exists(stashDbPath))
            throw new FileNotFoundException($"Database file not found: {stashDbPath}", stashDbPath);

        await using var conn = new SqliteConnection(OpenReadOnly(stashDbPath));
        await conn.OpenAsync(ct);
        logger.LogInformation("Starting Stash migration from {Path}", stashDbPath);

        var blobMap = await ImportBlobsAsync(conn, progress, BlobsStart, BlobsEnd, ct);
        var folderIdMap = await ImportFoldersAsync(conn, progress, FoldersStart, FoldersEnd, ct);
        db.ChangeTracker.Clear();

        var studioIdMap = await ImportStudiosAsync(conn, blobMap, progress, StudiosStart, StudiosEnd, ct);
        db.ChangeTracker.Clear();

        var tagIdMap = await ImportTagsAsync(conn, blobMap, progress, TagsStart, TagsEnd, ct);
        db.ChangeTracker.Clear();

        var performerIdMap = await ImportPerformersAsync(conn, blobMap, tagIdMap, progress, PerformersStart, PerformersEnd, ct);
        db.ChangeTracker.Clear();

        var groupIdMap = await ImportGroupsAsync(conn, studioIdMap, progress, GroupsStart, GroupsEnd, ct);
        db.ChangeTracker.Clear();

        var (sceneCount, sceneGeneratedMap) = await ImportScenesAsync(conn, blobMap, folderIdMap, studioIdMap, tagIdMap, performerIdMap, groupIdMap, progress, ScenesStart, ScenesEnd, ct);
        db.ChangeTracker.Clear();

        var imageIdMap = await ImportImagesAsync(conn, folderIdMap, studioIdMap, tagIdMap, performerIdMap, progress, ImagesStart, ImagesEnd, ct);
        db.ChangeTracker.Clear();

        var galleryCount = await ImportGalleriesAsync(conn, folderIdMap, studioIdMap, tagIdMap, performerIdMap, imageIdMap, progress, GalleriesStart, GalleriesEnd, ct);
        db.ChangeTracker.Clear();

        progress.Report(LibraryPathsStart, "Importing library paths...");
        await ImportLibraryPathsAsync(stashDbPath, ct);
        progress.Report(LibraryPathsEnd, "Library paths imported");

        if (options.MigrateGeneratedContent)
        {
            await CopyGeneratedContentAsync(stashDbPath, sceneGeneratedMap, options, progress, GeneratedAssetsStart, GeneratedAssetsEnd, ct);
        }
        else
        {
            logger.LogInformation("Skipping generated content migration for {Path}", stashDbPath);
            progress.Report(GeneratedAssetsEnd, "Skipping generated scene assets");
        }

        logger.LogInformation("Migration complete: {S} scenes, {P} performers, {T} tags, {St} studios, {G} groups, {I} images, {Ga} galleries",
            sceneCount, performerIdMap.Count, tagIdMap.Count, studioIdMap.Count, groupIdMap.Count, imageIdMap.Count, galleryCount);

        return new StashImportResult(sceneCount, performerIdMap.Count, tagIdMap.Count, studioIdMap.Count, groupIdMap.Count, imageIdMap.Count, galleryCount);
    }

    // ── Blobs ─────────────────────────────────────────────────────────────────

    private async Task<Dictionary<string, string>> ImportBlobsAsync(SqliteConnection conn, IJobProgress progress, double startProgress, double endProgress, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var total = await CountAsync(conn, "blobs", ct);
        var processed = 0;
        progress.Report(startProgress, "Importing blobs...");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT checksum, blob FROM blobs WHERE blob IS NOT NULL";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            processed++;
            if (r.IsDBNull(1)) continue;
            var checksum = r.GetString(0);
            try
            {
                var bytes = (byte[])r.GetValue(1);
                using var ms = new MemoryStream(bytes);
                var contentType = DetectImageContentType(ms);
                ms.Position = 0;
                var blobId = await blobService.StoreBlobAsync(ms, contentType, ct);
                map[checksum] = blobId;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Blob {Checksum} import failed: {Err}", checksum, ex.Message);
            }

            if (processed % 100 == 0 || processed == total)
                ReportPhase(progress, startProgress, endProgress, processed, total, $"Importing blobs ({processed}/{total})");
        }
        logger.LogInformation("Imported {Count} blobs", map.Count);
        return map;
    }

    // ── Studios ───────────────────────────────────────────────────────────────

    private async Task<Dictionary<int, int>> ImportStudiosAsync(SqliteConnection conn, Dictionary<string, string> blobMap, IJobProgress progress, double startProgress, double endProgress, CancellationToken ct)
    {
        var rows = new List<(int Id, string Name, int? ParentId, string? Details, int? Rating, bool Favorite, bool IgnoreAutoTag, string? ImageBlob)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, parent_id, details, rating, favorite, ignore_auto_tag, image_blob FROM studios";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add((r.GetInt32(0), r.GetString(1), ReadIntNull(r, 2), ReadStringNull(r, 3),
                    ReadIntNull(r, 4), ReadBool(r, 5), ReadBool(r, 6), ReadStringNull(r, 7)));
        }
        var urls = await ReadUrlsAsync(conn, "studio_urls", "studio_id", ct);
        var aliases = await ReadAliasesAsync(conn, "studio_aliases", "studio_id", ct);

        // Studio stash IDs
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

        var idMap = new Dictionary<int, int>();
        progress.Report(startProgress, "Importing studios...");
        foreach (var stashId in ordered)
        {
            var row = byId[stashId];
            var entity = new Studio
            {
                Name = row.Name,
                ParentId = row.ParentId.HasValue && idMap.TryGetValue(row.ParentId.Value, out var pId) ? pId : null,
                Details = row.Details,
                Rating = row.Rating,
                Favorite = row.Favorite,
                IgnoreAutoTag = row.IgnoreAutoTag,
                Organized = false,
                ImageBlobId = GetBlobId(blobMap, row.ImageBlob),
                Urls = urls.GetValueOrDefault(stashId, []).Select(u => new StudioUrl { Url = u }).ToList(),
                Aliases = aliases.GetValueOrDefault(stashId, []).Select(a => new StudioAlias { Alias = a }).ToList(),
                RemoteIds = studioStashIds.GetValueOrDefault(stashId, [])
                    .Select(s => new StudioRemoteId { Endpoint = s.Ep, RemoteId = s.Rid }).ToList(),
            };
            db.Studios.Add(entity);
            await db.SaveChangesAsync(ct);
            idMap[stashId] = entity.Id;

            if (idMap.Count % 25 == 0 || idMap.Count == ordered.Count)
                ReportPhase(progress, startProgress, endProgress, idMap.Count, ordered.Count, $"Importing studios ({idMap.Count}/{ordered.Count})");
        }
        logger.LogInformation("Imported {Count} studios", idMap.Count);
        return idMap;
    }

    // ── Tags ──────────────────────────────────────────────────────────────────

    private async Task<Dictionary<int, int>> ImportTagsAsync(SqliteConnection conn, Dictionary<string, string> blobMap, IJobProgress progress, double startProgress, double endProgress, CancellationToken ct)
    {
        var rows = new List<(int Id, string Name, string? SortName, string? Description, bool Favorite, bool IgnoreAutoTag, string? ImageBlob)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, sort_name, description, favorite, ignore_auto_tag, image_blob FROM tags";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add((r.GetInt32(0), r.GetString(1), ReadStringNull(r, 2), ReadStringNull(r, 3),
                    ReadBool(r, 4), ReadBool(r, 5), ReadStringNull(r, 6)));
        }
        var aliases = await ReadAliasesAsync(conn, "tag_aliases", "tag_id", ct);

        // Parent relationships (child_id → parent stash IDs)
        var tagParents = new Dictionary<int, List<int>>();
        if (await TableExistsAsync(conn, "tags_relations", ct))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT parent_id, child_id FROM tags_relations";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var pId = r.GetInt32(0); var cId = r.GetInt32(1);
                if (!tagParents.TryGetValue(cId, out var list)) tagParents[cId] = list = [];
                list.Add(pId);
            }
        }

        var byId = rows.ToDictionary(r => r.Id);
        var ordered = TopologicalSort(rows.Select(r => r.Id).ToList(),
            id => tagParents.GetValueOrDefault(id, []));

        var idMap = new Dictionary<int, int>();
        progress.Report(startProgress, "Importing tags...");
        foreach (var stashId in ordered)
        {
            var row = byId[stashId];
            var entity = new Tag
            {
                Name = row.Name,
                SortName = row.SortName,
                Description = row.Description,
                Favorite = row.Favorite,
                IgnoreAutoTag = row.IgnoreAutoTag,
                ImageBlobId = GetBlobId(blobMap, row.ImageBlob),
                Aliases = aliases.GetValueOrDefault(stashId, []).Select(a => new TagAlias { Alias = a }).ToList(),
            };
            db.Tags.Add(entity);
            await db.SaveChangesAsync(ct);
            idMap[stashId] = entity.Id;

            if (idMap.Count % 50 == 0 || idMap.Count == ordered.Count)
                ReportPhase(progress, startProgress, endProgress, idMap.Count, ordered.Count, $"Importing tags ({idMap.Count}/{ordered.Count})");
        }

        // Add parent/child relations
        if (tagParents.Count > 0)
        {
            foreach (var (childStashId, parentStashIds) in tagParents)
            {
                if (!idMap.TryGetValue(childStashId, out var childCoveId)) continue;
                foreach (var parentStashId in parentStashIds)
                {
                    if (!idMap.TryGetValue(parentStashId, out var parentCoveId)) continue;
                    db.Set<TagParent>().Add(new TagParent { ParentId = parentCoveId, ChildId = childCoveId });
                }
            }
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("Imported {Count} tags", idMap.Count);
        return idMap;
    }

    // ── Performers ────────────────────────────────────────────────────────────

    private async Task<Dictionary<int, int>> ImportPerformersAsync(SqliteConnection conn, Dictionary<string, string> blobMap, Dictionary<int, int> tagIdMap, IJobProgress progress, double startProgress, double endProgress, CancellationToken ct)
    {
        var rows = new List<(int Id, string Name, string? Disambiguation, string? Gender, string? Birthdate,
            string? Ethnicity, string? Country, string? EyeColor, string? HairColor, int? Height, int? Weight,
            string? Measurements, string? FakeTits, double? PenisLength, string? Circumcised,
            string? CareerLength, string? DeathDate,
            string? Tattoos, string? Piercings, bool Favorite, int? Rating, string? Details,
            bool IgnoreAutoTag, string? ImageBlob)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT id, name, disambiguation, gender, birthdate, ethnicity, country, eye_color,
                hair_color, height, weight, measurements, fake_tits, penis_length, circumcised, career_length,
                death_date, tattoos, piercings, favorite, rating, details, ignore_auto_tag, image_blob
                FROM performers";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add((r.GetInt32(0), r.GetString(1), ReadStringNull(r, 2), ReadStringNull(r, 3),
                    ReadStringNull(r, 4), ReadStringNull(r, 5), ReadStringNull(r, 6), ReadStringNull(r, 7),
                    ReadStringNull(r, 8), ReadIntNull(r, 9), ReadIntNull(r, 10), ReadStringNull(r, 11),
                    ReadStringNull(r, 12), r.IsDBNull(13) ? null : (double?)r.GetDouble(13),
                    ReadStringNull(r, 14), ReadStringNull(r, 15), ReadStringNull(r, 16),
                    ReadStringNull(r, 17), ReadStringNull(r, 18), ReadBool(r, 19), ReadIntNull(r, 20),
                    ReadStringNull(r, 21), ReadBool(r, 22), ReadStringNull(r, 23)));
        }
        var urls = await ReadUrlsAsync(conn, "performer_urls", "performer_id", ct);
        var aliases = await ReadAliasesAsync(conn, "performer_aliases", "performer_id", ct);
        var performerTagMap = await ReadJunctionAsync(conn, "performers_tags", "performer_id", "tag_id", ct);

        // Performer stash IDs
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
        var batchEntities = new List<(int StashId, Performer Entity)>(200);
        const int PerformerBatchSize = 200;
        progress.Report(startProgress, "Importing performers...");
        foreach (var row in rows)
        {
            var (careerStart, careerEnd) = ParseCareerLength(row.CareerLength);
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
                Rating = row.Rating,
                Details = row.Details,
                IgnoreAutoTag = row.IgnoreAutoTag,
                ImageBlobId = GetBlobId(blobMap, row.ImageBlob),
                Urls = urls.GetValueOrDefault(row.Id, []).Select(u => new PerformerUrl { Url = u }).ToList(),
                Aliases = aliases.GetValueOrDefault(row.Id, []).Select(a => new PerformerAlias { Alias = a }).ToList(),
                PerformerTags = performerTagMap.GetValueOrDefault(row.Id, [])
                    .Where(tagIdMap.ContainsKey)
                    .Select(tagId => new PerformerTag { TagId = tagIdMap[tagId] }).ToList(),
                RemoteIds = performerStashIds.GetValueOrDefault(row.Id, [])
                    .Select(s => new PerformerRemoteId { Endpoint = s.Ep, RemoteId = s.Rid }).ToList(),
            };
            db.Performers.Add(entity);
            batchEntities.Add((row.Id, entity));

            if (batchEntities.Count >= PerformerBatchSize)
            {
                await db.SaveChangesAsync(ct);

                foreach (var (stashId, performer) in batchEntities)
                    idMap[stashId] = performer.Id;

                batchEntities.Clear();
                db.ChangeTracker.Clear();
                ReportPhase(progress, startProgress, endProgress, idMap.Count, rows.Count, $"Importing performers ({idMap.Count}/{rows.Count})");
            }
        }

        if (batchEntities.Count > 0)
        {
            await db.SaveChangesAsync(ct);

            foreach (var (stashId, performer) in batchEntities)
                idMap[stashId] = performer.Id;

            batchEntities.Clear();
            db.ChangeTracker.Clear();
            ReportPhase(progress, startProgress, endProgress, idMap.Count, rows.Count, $"Importing performers ({idMap.Count}/{rows.Count})");
        }
        logger.LogInformation("Imported {Count} performers", idMap.Count);
        return idMap;
    }

    // ── Groups ────────────────────────────────────────────────────────────────

    private async Task<Dictionary<int, int>> ImportGroupsAsync(SqliteConnection conn, Dictionary<int, int> studioIdMap, IJobProgress progress, double startProgress, double endProgress, CancellationToken ct)
    {
        var rows = new List<(int Id, string Name, string? Aliases, int? Duration, string? Date,
            int? Rating, int? StudioId, string? Director, string? Description)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, aliases, duration, date, rating, studio_id, director, description FROM groups";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add((r.GetInt32(0), r.GetString(1), ReadStringNull(r, 2), ReadIntNull(r, 3),
                    ReadStringNull(r, 4), ReadIntNull(r, 5), ReadIntNull(r, 6),
                    ReadStringNull(r, 7), ReadStringNull(r, 8)));
        }
        var urls = await ReadUrlsAsync(conn, "group_urls", "group_id", ct);

        var idMap = new Dictionary<int, int>(rows.Count);
        var batchEntities = new List<(int StashId, Cove.Core.Entities.Group Entity)>(100);
        const int GroupBatchSize = 100;
        progress.Report(startProgress, "Importing groups...");
        foreach (var row in rows)
        {
            var entity = new Cove.Core.Entities.Group
            {
                Name = row.Name,
                Aliases = row.Aliases,
                Duration = row.Duration,
                Date = ParseDate(row.Date),
                Rating = row.Rating,
                StudioId = row.StudioId.HasValue && studioIdMap.TryGetValue(row.StudioId.Value, out var sId) ? sId : null,
                Director = row.Director,
                Synopsis = row.Description,
                Urls = urls.GetValueOrDefault(row.Id, []).Select(u => new GroupUrl { Url = u }).ToList(),
            };
            db.Groups.Add(entity);
            batchEntities.Add((row.Id, entity));

            if (batchEntities.Count >= GroupBatchSize)
            {
                await db.SaveChangesAsync(ct);

                foreach (var (stashId, group) in batchEntities)
                    idMap[stashId] = group.Id;

                batchEntities.Clear();
                db.ChangeTracker.Clear();
                ReportPhase(progress, startProgress, endProgress, idMap.Count, rows.Count, $"Importing groups ({idMap.Count}/{rows.Count})");
            }
        }

        if (batchEntities.Count > 0)
        {
            await db.SaveChangesAsync(ct);

            foreach (var (stashId, group) in batchEntities)
                idMap[stashId] = group.Id;

            batchEntities.Clear();
            db.ChangeTracker.Clear();
            ReportPhase(progress, startProgress, endProgress, idMap.Count, rows.Count, $"Importing groups ({idMap.Count}/{rows.Count})");
        }
        logger.LogInformation("Imported {Count} groups", idMap.Count);
        return idMap;
    }

    // ── Scenes ────────────────────────────────────────────────────────────────

    private async Task<(int count, Dictionary<int, SceneGeneratedData> generatedMap)> ImportScenesAsync(SqliteConnection conn,
        Dictionary<string, string> blobMap,
        Dictionary<int, int> folderIdMap,
        Dictionary<int, int> studioIdMap, Dictionary<int, int> tagIdMap,
        Dictionary<int, int> performerIdMap, Dictionary<int, int> groupIdMap,
        IJobProgress progress,
        double startProgress,
        double endProgress,
        CancellationToken ct)
    {
        // Load scene rows
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

        // Load all supporting data up front
        var sceneTagMap = await ReadJunctionAsync(conn, "scenes_tags", "scene_id", "tag_id", ct);
        var scenePerformerMap = await ReadJunctionAsync(conn, "performers_scenes", "scene_id", "performer_id", ct);
        var sceneGroupMap = new Dictionary<int, List<(int GroupId, int Index)>>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT scene_id, group_id, scene_index FROM groups_scenes";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var sId = r.GetInt32(0); var gId = r.GetInt32(1); var idx = ReadIntNull(r, 2) ?? 0;
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

        // File data
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
                var sId = r.GetInt32(0); var fId = r.GetInt32(1);
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

        // Fingerprints (blob contains UTF-8 encoded hash string)
        var fingerprints = new Dictionary<int, List<(string Type, string Value)>>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT file_id, type, fingerprint FROM files_fingerprints";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var fId = r.GetInt32(0); var type = r.GetString(1);
                // fingerprint may be stored as BLOB (binary), TEXT, or INTEGER (phash is int64)
                var rawFp = r.GetValue(2);
                var value = rawFp switch
                {
                    byte[] fpBytes => Encoding.UTF8.GetString(fpBytes),
                    long l => l.ToString(),
                    _ => rawFp?.ToString() ?? string.Empty,
                };
                if (!fingerprints.TryGetValue(fId, out var list)) fingerprints[fId] = list = [];
                list.Add((type, value));
            }
        }

        // Import scenes in batches of 50
        int count = 0;
        var idMap = new Dictionary<int, int>();
        const int SceneBatchSize = 50;
        var pendingBatch = new List<(int StashId, Scene Entity)>(SceneBatchSize);
        progress.Report(startProgress, "Importing scenes...");

        void FlushSceneBatch()
        {
            foreach (var (stashId, entity) in pendingBatch)
                idMap[stashId] = entity.Id;
            pendingBatch.Clear();
        }

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
                Rating = row.Rating,
                StudioId = row.StudioId.HasValue && studioIdMap.TryGetValue(row.StudioId.Value, out var sId) ? sId : null,
                Organized = row.Organized,
                Code = row.Code,
                Director = row.Director,
                ResumeTime = row.ResumeTime,
                PlayDuration = row.PlayDuration,
                OCounter = oHistory.Count,
                PlayCount = viewHistory.Count,
                LastPlayedAt = importedLastPlayedAt ?? (viewHistory.Count > 0 ? viewHistory.Max() : null),
                CreatedAt = ParseDateTime(row.CreatedAt),
                UpdatedAt = ParseDateTime(row.UpdatedAt),
                Urls = sceneUrls.GetValueOrDefault(row.Id, []).Select(u => new SceneUrl { Url = u }).ToList(),
                SceneTags = sceneTagMap.GetValueOrDefault(row.Id, [])
                    .Where(tagIdMap.ContainsKey)
                    .Select(t => new SceneTag { TagId = tagIdMap[t] }).ToList(),
                ScenePerformers = scenePerformerMap.GetValueOrDefault(row.Id, [])
                    .Where(performerIdMap.ContainsKey)
                    .Select(p => new ScenePerformer { PerformerId = performerIdMap[p] }).ToList(),
                SceneGroups = sceneGroupMap.GetValueOrDefault(row.Id, [])
                    .Where(g => groupIdMap.ContainsKey(g.GroupId))
                    .Select(g => new SceneGroup { GroupId = groupIdMap[g.GroupId], SceneIndex = g.Index }).ToList(),
                OHistory = oHistory.Select(d => new SceneOHistory { OccurredAt = d }).ToList(),
                PlayHistory = viewHistory.Select(d => new ScenePlayHistory { PlayedAt = d }).ToList(),
                RemoteIds = sceneStashIds.GetValueOrDefault(row.Id, [])
                    .Select(s => new SceneRemoteId { Endpoint = s.Ep, RemoteId = s.Rid }).ToList(),
            };

            foreach (var fileId in sceneFiles.GetValueOrDefault(row.Id, []))
            {
                if (!fileData.TryGetValue(fileId, out var fd)) continue;
                if (!videoData.TryGetValue(fileId, out var vd)) continue;
                if (!folderIdMap.TryGetValue(fd.FolderId, out var coveFolderId)) continue;

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

            db.Scenes.Add(scene);
            pendingBatch.Add((row.Id, scene));
            count++;

            if (pendingBatch.Count >= SceneBatchSize)
            {
                await db.SaveChangesAsync(ct);
                FlushSceneBatch();
                db.ChangeTracker.Clear();
                ReportPhase(progress, startProgress, endProgress, count, sceneRows.Count, $"Importing scenes ({count}/{sceneRows.Count})");
                logger.LogInformation("Imported {Count}/{Total} scenes...", count, sceneRows.Count);
            }
        }
        if (pendingBatch.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            FlushSceneBatch();
            db.ChangeTracker.Clear();
            ReportPhase(progress, startProgress, endProgress, count, sceneRows.Count, $"Importing scenes ({count}/{sceneRows.Count})");
        }
        logger.LogInformation("Imported {Count} scenes", count);

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

        return (count, generatedMap);
    }

    // ── Folders ───────────────────────────────────────────────────────────────

    private async Task<Dictionary<int, int>> ImportFoldersAsync(SqliteConnection conn, IJobProgress progress, double startProgress, double endProgress, CancellationToken ct)
    {
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
        var fOrdered = TopologicalSort(folderData.Keys.ToList(),
            id => folderData[id].ParentId.HasValue ? [folderData[id].ParentId!.Value] : (IEnumerable<int>)[]);

        var allPaths = folderData.Values.Select(fd => fd.Path).ToList();
        var existingFoldersByPath = db.Folders
            .Where(f => allPaths.Contains(f.Path))
            .ToDictionary(f => f.Path, f => f.Id, StringComparer.OrdinalIgnoreCase);

        progress.Report(startProgress, "Importing folders...");

        foreach (var stashFolderId in fOrdered)
        {
            var fd = folderData[stashFolderId];
            if (existingFoldersByPath.TryGetValue(fd.Path, out var existingId))
            {
                folderIdMap[stashFolderId] = existingId;
                continue;
            }
            var folder = new Folder
            {
                Path = fd.Path,
                ParentFolderId = fd.ParentId.HasValue && folderIdMap.TryGetValue(fd.ParentId.Value, out var pfId) ? pfId : null,
                ModTime = fd.ModTime,
                CreatedAt = fd.CreatedAt,
                UpdatedAt = fd.ModTime,
            };
            db.Folders.Add(folder);
            await db.SaveChangesAsync(ct);
            folderIdMap[stashFolderId] = folder.Id;
            existingFoldersByPath[fd.Path] = folder.Id;

            if (folderIdMap.Count % 100 == 0 || folderIdMap.Count == fOrdered.Count)
                ReportPhase(progress, startProgress, endProgress, folderIdMap.Count, fOrdered.Count, $"Importing folders ({folderIdMap.Count}/{fOrdered.Count})");
        }
        logger.LogInformation("Imported {Count} folders", folderIdMap.Count);
        return folderIdMap;
    }

    // ── Images ────────────────────────────────────────────────────────────────

    private async Task<Dictionary<int, int>> ImportImagesAsync(
        SqliteConnection conn, Dictionary<int, int> folderIdMap,
        Dictionary<int, int> studioIdMap, Dictionary<int, int> tagIdMap,
        Dictionary<int, int> performerIdMap, IJobProgress progress, double startProgress, double endProgress, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "images", ct))
            return new Dictionary<int, int>();

        var total = await CountAsync(conn, "images", ct);
        logger.LogInformation("Importing {Total} images...", total);

        var imageTagMap = await ReadJunctionAsync(conn, "images_tags", "image_id", "tag_id", ct);
        var imagePerformerMap = await ReadJunctionAsync(conn, "performers_images", "image_id", "performer_id", ct);
        var imageUrls = await ReadUrlsAsync(conn, "image_urls", "image_id", ct);

        var imageToFile = new Dictionary<int, int>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT image_id, file_id FROM images_files WHERE [primary]=1";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                imageToFile[r.GetInt32(0)] = r.GetInt32(1);
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

        // Load all image rows in memory (212k is ok — each row is small)
        var imageRows = new List<(int StashId, string? Title, string? Code, string? Details, string? Photographer,
            int? Rating, bool Organized, int OCounter, int? StudioId, string? Date, string CreatedAt, string UpdatedAt)>();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, title, code, details, photographer, rating, organized, o_counter, studio_id, date, created_at, updated_at FROM images";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                imageRows.Add((r.GetInt32(0), ReadStringNull(r, 1), ReadStringNull(r, 2), ReadStringNull(r, 3),
                    ReadStringNull(r, 4), ReadIntNull(r, 5), ReadBool(r, 6), ReadIntNull(r, 7) ?? 0,
                    ReadIntNull(r, 8), ReadStringNull(r, 9), r.GetString(10), r.GetString(11)));
        }

        var idMap = new Dictionary<int, int>(imageRows.Count);
        const int BatchSize = 500;
        progress.Report(startProgress, "Importing images...");

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
                    Rating = row.Rating,
                    Organized = row.Organized,
                    OCounter = row.OCounter,
                    StudioId = row.StudioId.HasValue && studioIdMap.TryGetValue(row.StudioId.Value, out var sid) ? sid : null,
                    Date = ParseDate(row.Date),
                    CreatedAt = ParseDateTime(row.CreatedAt),
                    UpdatedAt = ParseDateTime(row.UpdatedAt),
                    Urls = imageUrls.GetValueOrDefault(stashId, []).Select(u => new ImageUrl { Url = u }).ToList(),
                    ImageTags = imageTagMap.GetValueOrDefault(stashId, [])
                        .Where(tagIdMap.ContainsKey)
                        .Select(t => new ImageTag { TagId = tagIdMap[t] }).ToList(),
                    ImagePerformers = imagePerformerMap.GetValueOrDefault(stashId, [])
                        .Where(performerIdMap.ContainsKey)
                        .Select(p => new ImagePerformer { PerformerId = performerIdMap[p] }).ToList(),
                };

                if (imageToFile.TryGetValue(stashId, out var fileId) && fileData.TryGetValue(fileId, out var fd)
                    && folderIdMap.TryGetValue(fd.FolderId, out var coveFolderId))
                {
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

                db.Images.Add(image);
                batchEntities.Add((stashId, image));
            }

            await db.SaveChangesAsync(ct);

            foreach (var (stashId, entity) in batchEntities)
                idMap[stashId] = entity.Id;

            db.ChangeTracker.Clear();
            ReportPhase(progress, startProgress, endProgress, idMap.Count, imageRows.Count, $"Importing images ({idMap.Count}/{imageRows.Count})");

            logger.LogInformation("Imported {Count}/{Total} images...", Math.Min(i + BatchSize, imageRows.Count), imageRows.Count);
        }

        logger.LogInformation("Imported {Count} images", idMap.Count);
        return idMap;
    }

    // ── Galleries ─────────────────────────────────────────────────────────────

    private async Task<int> ImportGalleriesAsync(
        SqliteConnection conn, Dictionary<int, int> folderIdMap,
        Dictionary<int, int> studioIdMap, Dictionary<int, int> tagIdMap,
        Dictionary<int, int> performerIdMap, Dictionary<int, int> imageIdMap,
        IJobProgress progress,
        double startProgress,
        double endProgress,
        CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "galleries", ct))
        {
            logger.LogInformation("No galleries table found, skipping");
            return 0;
        }

        var total = await CountAsync(conn, "galleries", ct);
        logger.LogInformation("Importing {Total} galleries...", total);

        var galleryTagMap = await ReadJunctionAsync(conn, "galleries_tags", "gallery_id", "tag_id", ct);
        var galleryPerformerMap = await ReadJunctionAsync(conn, "performers_galleries", "gallery_id", "performer_id", ct);
        var galleryUrls = await ReadUrlsAsync(conn, "gallery_urls", "gallery_id", ct);

        // galleries_files: gallery_id → file_id (primary)
        var galleryToFile = new Dictionary<int, int>();
        if (await TableExistsAsync(conn, "galleries_files", ct))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT gallery_id, file_id FROM galleries_files WHERE [primary]=1";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                galleryToFile[r.GetInt32(0)] = r.GetInt32(1);
        }

        // galleries_images: gallery_id → list of image_ids
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

        // galleries_chapters
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

        // files data (for zip-based galleries)
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

        // Load all gallery rows
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

        int count = 0;
        progress.Report(startProgress, "Importing galleries...");
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
                Rating = row.Rating,
                Organized = row.Organized,
                FolderId = row.FolderId.HasValue && folderIdMap.TryGetValue(row.FolderId.Value, out var fid) ? fid : null,
                StudioId = row.StudioId.HasValue && studioIdMap.TryGetValue(row.StudioId.Value, out var sid) ? sid : null,
                CreatedAt = ParseDateTime(row.CreatedAt),
                UpdatedAt = ParseDateTime(row.UpdatedAt),
                Urls = galleryUrls.GetValueOrDefault(stashId, []).Select(u => new GalleryUrl { Url = u }).ToList(),
                GalleryTags = galleryTagMap.GetValueOrDefault(stashId, [])
                    .Where(tagIdMap.ContainsKey)
                    .Select(t => new GalleryTag { TagId = tagIdMap[t] }).ToList(),
                GalleryPerformers = galleryPerformerMap.GetValueOrDefault(stashId, [])
                    .Where(performerIdMap.ContainsKey)
                    .Select(p => new GalleryPerformer { PerformerId = performerIdMap[p] }).ToList(),
                Chapters = galleryChapters.GetValueOrDefault(stashId, [])
                    .Select(c => new GalleryChapter { Title = c.Title, ImageIndex = c.ImageIndex }).ToList(),
                ImageGalleries = galleryImages.GetValueOrDefault(stashId, [])
                    .Where(imageIdMap.ContainsKey)
                    .Select(imgId => new ImageGallery { ImageId = imageIdMap[imgId] }).ToList(),
            };

            // Zip-based gallery file
            if (galleryToFile.TryGetValue(stashId, out var fileId) && fileData.TryGetValue(fileId, out var fd)
                && folderIdMap.TryGetValue(fd.FolderId, out var coveFolderId))
            {
                gallery.Files.Add(new GalleryFile
                {
                    Basename = fd.Basename,
                    ParentFolderId = coveFolderId,
                    Size = fd.Size,
                    ModTime = fd.ModTime,
                    CreatedAt = fd.CreatedAt,
                    UpdatedAt = fd.ModTime,
                });
            }

            db.Galleries.Add(gallery);
            count++;
            if (count % 100 == 0)
            {
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                ReportPhase(progress, startProgress, endProgress, count, total, $"Importing galleries ({count}/{total})");
                logger.LogInformation("Imported {Count}/{Total} galleries...", count, total);
            }
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        ReportPhase(progress, startProgress, endProgress, count, total, $"Importing galleries ({count}/{total})");
        logger.LogInformation("Imported {Count} galleries", count);
        return count;
    }

    // ── Library Paths ──────────────────────────────────────────────────────────

    private async Task ImportLibraryPathsAsync(string stashDbPath, CancellationToken ct)
    {
        try
        {
            var configDir = Path.GetDirectoryName(stashDbPath)!;
            var configPath = Path.Combine(configDir, "config.yml");
            if (!File.Exists(configPath))
            {
                logger.LogWarning("Stash config.yml not found at {Path}, skipping library path import", configPath);
                return;
            }

            var stashConfig = ParseStashConfig(configPath);
            var paths = stashConfig.Paths;
            if (paths.Count == 0)
            {
                logger.LogInformation("No library paths found in Stash config");
                return;
            }

            // Merge with existing Cove paths (deduplicate by path)
            var existingPaths = new HashSet<string>(
                config.CovePaths.Select(p => p.Path),
                StringComparer.OrdinalIgnoreCase);

            var dto = configService.GetConfig();
            foreach (var (path, excludeImage, excludeVideo) in paths)
            {
                if (existingPaths.Contains(path)) continue;
                dto.CovePaths.Add(new Cove.Core.DTOs.CovePathDto
                {
                    Path = path,
                    ExcludeImage = excludeImage,
                    ExcludeVideo = excludeVideo,
                    ExcludeAudio = false,
                });
            }
            await configService.SaveConfigAsync(dto);
            logger.LogInformation("Imported {Count} library paths from Stash config", paths.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to import library paths from Stash config");
        }
    }

    // ── Generated Content Copy ────────────────────────────────────────────────

    private async Task CopyGeneratedContentAsync(string stashDbPath, Dictionary<int, SceneGeneratedData> sceneGeneratedMap, StashImportOptions options, IJobProgress progress, double startProgress, double endProgress, CancellationToken ct)
    {
        try
        {
            progress.Report(startProgress, "Copying generated scene assets...");
            var configDir = Path.GetDirectoryName(stashDbPath)!;
            var configPath = Path.Combine(configDir, "config.yml");
            var stashConfig = File.Exists(configPath)
                ? ParseStashConfig(configPath)
                : new StashConfigData([], null, "OSHASH");
            var stashGeneratedPath = string.IsNullOrWhiteSpace(options.GeneratedPath)
                ? stashConfig.GeneratedPath
                : options.GeneratedPath;
            if (string.IsNullOrWhiteSpace(stashGeneratedPath) || !Directory.Exists(stashGeneratedPath))
            {
                logger.LogWarning("Stash generated path not found: {Path}", stashGeneratedPath);
                return;
            }

            var stashScreenshotsDir = Path.Combine(stashGeneratedPath, "screenshots");
            var stashVttDir = Path.Combine(stashGeneratedPath, "vtt");

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

            int sourceScreenshots = 0, migratedScreenshots = 0;
            int sourcePreviews = 0, migratedPreviews = 0;
            int sourceSprites = 0, migratedSprites = 0;
            int sourceVtts = 0, migratedVtts = 0;

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

                var previewHash = ResolveGeneratedHash(generatedData, stashConfig.VideoFileNamingAlgorithm, previewHashes);
                if (!string.IsNullOrWhiteSpace(previewHash))
                {
                    sourcePreviews++;
                    var srcPreviewPath = Path.Combine(stashScreenshotsDir, $"{previewHash}.mp4");
                    if (TryCopyGeneratedFile(srcPreviewPath, GetCoveScenePreviewPath(coveSceneId)))
                        migratedPreviews++;
                }

                var spriteHash = ResolveGeneratedHash(generatedData, stashConfig.VideoFileNamingAlgorithm, spriteHashes);
                if (!string.IsNullOrWhiteSpace(spriteHash))
                {
                    sourceSprites++;
                    var srcSpritePath = Path.Combine(stashVttDir, $"{spriteHash}_sprite.jpg");
                    if (TryCopyGeneratedFile(srcSpritePath, GetCoveSceneSpritePath(coveSceneId)))
                        migratedSprites++;
                }

                var vttHash = ResolveGeneratedHash(generatedData, stashConfig.VideoFileNamingAlgorithm, vttHashes);
                if (!string.IsNullOrWhiteSpace(vttHash))
                {
                    sourceVtts++;
                    var srcVttPath = Path.Combine(stashVttDir, $"{vttHash}_thumbs.vtt");
                    if (TryCopyGeneratedFile(srcVttPath, GetCoveSceneSpriteVttPath(coveSceneId)))
                        migratedVtts++;
                }

                if (processed % 25 == 0 || processed == totalScenes)
                    ReportPhase(progress, startProgress, endProgress, processed, totalScenes, $"Copying generated assets ({processed}/{totalScenes})");
            }

            logger.LogInformation(
                "Migrated generated scene assets from Stash: screenshots {MigratedScreenshots}/{SourceScreenshots}, previews {MigratedPreviews}/{SourcePreviews}, sprites {MigratedSprites}/{SourceSprites}, vtt {MigratedVtts}/{SourceVtts}",
                migratedScreenshots,
                sourceScreenshots,
                migratedPreviews,
                sourcePreviews,
                migratedSprites,
                sourceSprites,
                migratedVtts,
                sourceVtts);

            progress.Report(endProgress, "Generated scene assets copied");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to copy generated content");
        }
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static string OpenReadOnly(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString();

    private static async Task<int> CountAsync(SqliteConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM \"{table}\"";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=@name";
        cmd.Parameters.AddWithValue("@name", table);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection conn, string table, string column, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM pragma_table_info('{table}') WHERE name = @column";
        cmd.Parameters.AddWithValue("@column", column);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task<Dictionary<int, List<string>>> ReadUrlsAsync(SqliteConnection conn, string table, string fkCol, CancellationToken ct)
    {
        var result = new Dictionary<int, List<string>>();
        if (!await TableExistsAsync(conn, table, ct)) return result;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT \"{fkCol}\", url FROM \"{table}\" ORDER BY \"{fkCol}\", position";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = r.GetInt32(0);
            if (!result.TryGetValue(id, out var list)) result[id] = list = [];
            list.Add(r.GetString(1));
        }
        return result;
    }

    private static async Task<Dictionary<int, List<string>>> ReadAliasesAsync(SqliteConnection conn, string table, string fkCol, CancellationToken ct)
    {
        var result = new Dictionary<int, List<string>>();
        if (!await TableExistsAsync(conn, table, ct)) return result;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT \"{fkCol}\", alias FROM \"{table}\"";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = r.GetInt32(0);
            if (!result.TryGetValue(id, out var list)) result[id] = list = [];
            list.Add(r.GetString(1));
        }
        return result;
    }

    private static async Task<Dictionary<int, List<int>>> ReadJunctionAsync(SqliteConnection conn, string table, string fkA, string fkB, CancellationToken ct)
    {
        var result = new Dictionary<int, List<int>>();
        if (!await TableExistsAsync(conn, table, ct)) return result;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT \"{fkA}\", \"{fkB}\" FROM \"{table}\"";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var a = r.GetInt32(0); var b = r.GetInt32(1);
            if (!result.TryGetValue(a, out var list)) result[a] = list = [];
            list.Add(b);
        }
        return result;
    }

    private static async Task<Dictionary<int, List<DateTime>>> ReadDatesAsync(SqliteConnection conn, string table, string fkCol, string dateCol, CancellationToken ct)
    {
        var result = new Dictionary<int, List<DateTime>>();
        if (!await TableExistsAsync(conn, table, ct)) return result;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT \"{fkCol}\", \"{dateCol}\" FROM \"{table}\"";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = r.GetInt32(0); var s = ReadStringNull(r, 1);
            if (s == null) continue;
            if (!result.TryGetValue(id, out var list)) result[id] = list = [];
            list.Add(ParseDateTime(s));
        }
        return result;
    }

    private static List<int> TopologicalSort(List<int> ids, Func<int, IEnumerable<int>> getDeps)
    {
        var result = new List<int>(ids.Count);
        var visited = new HashSet<int>(ids.Count);
        var inProgress = new HashSet<int>();
        var idSet = new HashSet<int>(ids);

        void Visit(int id)
        {
            if (visited.Contains(id) || inProgress.Contains(id)) return;
            inProgress.Add(id);
            foreach (var dep in getDeps(id))
                if (idSet.Contains(dep)) Visit(dep);
            inProgress.Remove(id);
            visited.Add(id);
            result.Add(id);
        }
        foreach (var id in ids) Visit(id);
        return result;
    }

    private static string? ReadStringNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static int? ReadIntNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    private static bool ReadBool(SqliteDataReader r, int i) => !r.IsDBNull(i) && r.GetBoolean(i);

    private static DateOnly? ParseDate(string? s) =>
        s != null && DateOnly.TryParse(s, out var d) ? d : null;

    private static DateTime ParseDateTime(string s) =>
        DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : DateTime.UtcNow;

    private static DateTime? ParseDateTimeOrNull(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : ParseDateTime(s);

    private static string? ResolveImportedGalleryTitle(
        string? explicitTitle,
        int? folderId,
        int stashGalleryId,
        IReadOnlyDictionary<int, int> galleryToFile,
        IReadOnlyDictionary<int, (string Basename, int FolderId, long Size, DateTime ModTime, DateTime CreatedAt)> fileData,
        IReadOnlyDictionary<int, string> stashFolderNames)
    {
        if (!string.IsNullOrWhiteSpace(explicitTitle))
            return explicitTitle;

        if (galleryToFile.TryGetValue(stashGalleryId, out var fileId) && fileData.TryGetValue(fileId, out var file))
            return Path.GetFileNameWithoutExtension(file.Basename);

        if (folderId.HasValue && stashFolderNames.TryGetValue(folderId.Value, out var folderName) && !string.IsNullOrWhiteSpace(folderName))
            return folderName;

        return null;
    }

    private static string? GetBlobId(Dictionary<string, string> blobMap, string? checksum) =>
        checksum != null && blobMap.TryGetValue(checksum, out var id) ? id : null;

    private static string? GetFingerprintValue(List<(string Type, string Value)> fingerprints, string type) =>
        fingerprints.FirstOrDefault(fp => string.Equals(fp.Type, type, StringComparison.OrdinalIgnoreCase)).Value;

    private static string GetLastPathSegment(string path)
    {
        var normalizedPath = path.Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalizedPath))
            return path;

        var separatorIndex = normalizedPath.LastIndexOf('/');
        return separatorIndex >= 0 ? normalizedPath[(separatorIndex + 1)..] : normalizedPath;
    }

    private static string? TrimGeneratedSuffix(string? value, string suffix)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? value[..^suffix.Length]
            : value;
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

    private static void ReportPhase(IJobProgress progress, double startProgress, double endProgress, int completed, int total, string subTask)
    {
        var ratio = total <= 0 ? 1 : Math.Clamp((double)completed / total, 0, 1);
        progress.Report(startProgress + ((endProgress - startProgress) * ratio), subTask);
    }

    private static void TrimImportResultsLocked()
    {
        while (importResultOrder.Count > 20)
        {
            var oldestJobId = importResultOrder.Dequeue();
            importResults.Remove(oldestJobId);
        }
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

    private bool TryCopyGeneratedFile(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath)) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath, overwrite: true);
        return File.Exists(destinationPath);
    }

    private async Task<bool> TryWriteSceneScreenshotAsync(int sceneId, string blobId, CancellationToken ct)
    {
        try
        {
            var blob = await blobService.GetBlobAsync(blobId, ct);
            if (blob == null) return false;

            await using var blobStream = blob.Value.Stream;
            var destinationPath = GetCoveSceneThumbnailPath(sceneId);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            if (string.Equals(blob.Value.ContentType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
            {
                await using var jpegOutput = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await blobStream.CopyToAsync(jpegOutput, ct);
                return File.Exists(destinationPath);
            }

            await using var buffered = new MemoryStream();
            await blobStream.CopyToAsync(buffered, ct);
            buffered.Position = 0;

            using var image = await SixLabors.ImageSharp.Image.LoadAsync(buffered, ct);
            await using var convertedOutput = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await image.SaveAsync(convertedOutput, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 85 }, ct);
            return File.Exists(destinationPath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SixLabors.ImageSharp.InvalidImageContentException ex)
        {
            logger.LogWarning("Skipping corrupt scene screenshot for scene {SceneId} from blob {BlobId}: {Message}", sceneId, blobId, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to migrate scene screenshot for scene {SceneId} from blob {BlobId}", sceneId, blobId);
            return false;
        }
    }

    private string GetCoveSceneThumbnailPath(int sceneId)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(sceneId)));
        return Path.Combine(config.GeneratedPath, "screenshots", hash[..2], $"{sceneId}.jpg");
    }

    private string GetCoveScenePreviewPath(int sceneId)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(sceneId)));
        return Path.Combine(config.GeneratedPath, "previews", hash[..2], $"{sceneId}.mp4");
    }

    private string GetCoveSceneSpritePath(int sceneId)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(sceneId)));
        return Path.Combine(config.GeneratedPath, "vtt", hash[..2], $"{sceneId}_sprite.jpg");
    }

    private string GetCoveSceneSpriteVttPath(int sceneId)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(sceneId)));
        return Path.Combine(config.GeneratedPath, "vtt", hash[..2], $"{sceneId}_thumbs.vtt");
    }

    private static string DetectImageContentType(Stream stream)
    {
        var buf = new byte[4];
        _ = stream.Read(buf, 0, 4);
        return (buf[0], buf[1], buf[2], buf[3]) switch
        {
            (0xFF, 0xD8, _, _) => "image/jpeg",
            (0x89, 0x50, 0x4E, 0x47) => "image/png",
            (0x47, 0x49, 0x46, _) => "image/gif",
            (0x52, 0x49, 0x46, 0x46) => "image/webp",
            _ => "image/jpeg",
        };
    }

    private sealed class NullJobProgress : IJobProgress
    {
        public static readonly NullJobProgress Instance = new();

        public void Report(double progress, string? subTask = null)
        {
        }
    }

    private static GenderEnum? ParseGender(string? s) => s?.ToUpperInvariant() switch
    {
        "MALE" => GenderEnum.Male,
        "FEMALE" => GenderEnum.Female,
        "TRANSGENDER_MALE" => GenderEnum.TransgenderMale,
        "TRANSGENDER_FEMALE" => GenderEnum.TransgenderFemale,
        "INTERSEX" => GenderEnum.Intersex,
        "NON_BINARY" => GenderEnum.NonBinary,
        _ => null,
    };

    private static CircumcisedEnum? ParseCircumcised(string? s) => s?.ToUpperInvariant() switch
    {
        "CUT" => CircumcisedEnum.Cut,
        "UNCUT" => CircumcisedEnum.Uncut,
        _ => null,
    };

    private static (DateOnly? start, DateOnly? end) ParseCareerLength(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return (null, null);
        // Format: "2012 -" or "2012 - 2020" or "2012-2020"
        var parts = s.Split('-', StringSplitOptions.TrimEntries);
        DateOnly? start = null, end = null;
        if (parts.Length >= 1 && int.TryParse(parts[0].Trim(), out var sy) && sy > 1900 && sy < 2100)
            start = new DateOnly(sy, 1, 1);
        if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[1]) && int.TryParse(parts[1].Trim(), out var ey) && ey > 1900 && ey < 2100)
            end = new DateOnly(ey, 12, 31);
        return (start, end);
    }

    private static StashConfigData ParseStashConfig(string configPath)
    {
        var paths = new List<(string Path, bool ExcludeImage, bool ExcludeVideo)>();
        string? generatedPath = null;
        string? videoFileNamingAlgorithm = null;
        bool? calculateMd5 = null;

        try
        {
            var lines = File.ReadAllLines(configPath);
            bool inStashArray = false;
            string? currentPath = null;
            bool currentExcludeImage = false;
            bool currentExcludeVideo = false;

            foreach (var rawLine in lines)
            {
                // generated: value
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

                var md5Match = Regex.Match(rawLine, @"^calculate_md5:\s*(true|false)$", RegexOptions.IgnoreCase);
                if (md5Match.Success)
                {
                    calculateMd5 = string.Equals(md5Match.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (rawLine.TrimStart().StartsWith("stash:"))
                {
                    inStashArray = true;
                    continue;
                }

                // End of stash array (top-level key)
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

                if (!inStashArray) continue;

                var trimmed = rawLine.TrimStart();

                // New array entry
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

            // Flush last entry
            if (inStashArray && currentPath != null)
                paths.Add((currentPath, currentExcludeImage, currentExcludeVideo));
        }
        catch (Exception)
        {
            // Swallow parse errors
        }

        return new StashConfigData(
            paths,
            generatedPath,
            videoFileNamingAlgorithm
            ?? (calculateMd5 == true ? "MD5" : "OSHASH"));
    }
}
