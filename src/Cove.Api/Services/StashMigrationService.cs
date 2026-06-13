using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Data.Common;

namespace Cove.Api.Services;

// Stash calls them "scenes"; Cove's domain (and the UI/API contract) calls them "videos".
// The property is named Videos so it serializes to `videos`, matching the frontend's StashPreviewResult/StashImportResult.
// GeneratedContentFound/GeneratedPath report whether Stash's "generated" folder (sprites, previews,
// screenshots, vtt) was located via config.yml so the preview can tell the user up front that generated
// content will be migratable. GeneratedPath is the resolved path when known (even if the folder is missing).
public record StashPreviewResult(bool IsValid, string? Error, int Videos, int Performers, int Tags, int Studios, int Groups, int Images, int Galleries, bool GeneratedContentFound = false, string? GeneratedPath = null);
public record StashImportResult(int Videos, int Performers, int Tags, int Studios, int Groups, int Images, int Galleries);
public record StashPathMapping(string Source, string Target);
public record StashImportOptions(string? CoveGeneratedPath, bool MigrateGeneratedContent = true, IReadOnlyList<StashPathMapping>? PathMappings = null);

public sealed class StashMigrationInProgressException(string message) : InvalidOperationException(message);

public partial class StashMigrationService
{
    private readonly CoveContext _db;
    private readonly IBlobService _blobService;
    private readonly ConfigService _configService;
    private readonly CoveConfiguration _config;
    private readonly IJobService _jobService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StashMigrationService> _logger;
    private string? _currentImportCustomPerformerImageLocation;

    private sealed record SceneGeneratedData(string? Oshash, string? Md5, string? CoverBlobId);
    private sealed record ImportedRatingSeed(int StashId, int? Value);
    private sealed record ImportedAffinitySeed(int StashId, int LikeCount = 0, int ViewCount = 0, double? LastPositionSec = null, double TotalConsumedSec = 0, DateTime? LastConsumedAt = null);
    private sealed class ImportedAffinityAggregate
    {
        private DateTime? _lastPositionConsumedAt;

        public int HostId { get; init; }
        public int LikeCount { get; private set; }
        public int ViewCount { get; private set; }
        public double TotalConsumedSec { get; private set; }
        public double? LastPositionSec { get; private set; }
        public DateTime? LastConsumedAt { get; private set; }

        public void Add(ImportedAffinitySeed seed)
        {
            LikeCount += Math.Max(0, seed.LikeCount);
            ViewCount += Math.Max(0, seed.ViewCount);
            TotalConsumedSec += Math.Max(0, seed.TotalConsumedSec);

            if (!LastConsumedAt.HasValue || (seed.LastConsumedAt.HasValue && seed.LastConsumedAt.Value > LastConsumedAt.Value))
                LastConsumedAt = seed.LastConsumedAt;

            if (seed.LastPositionSec is not { } lastPositionSec)
                return;

            if (seed.LastConsumedAt.HasValue)
            {
                if (!_lastPositionConsumedAt.HasValue || seed.LastConsumedAt.Value >= _lastPositionConsumedAt.Value)
                {
                    LastPositionSec = lastPositionSec;
                    _lastPositionConsumedAt = seed.LastConsumedAt;
                }
            }
            else if (!_lastPositionConsumedAt.HasValue)
            {
                LastPositionSec = lastPositionSec;
            }
        }
    }
    private sealed record StashMetadataServerConfig(string Endpoint, string ApiKey, string Name, int MaxRequestsPerMinute);
    private sealed record StashConfigData(
        List<(string Path, bool ExcludeImage, bool ExcludeVideo)> Paths,
        string? GeneratedPath,
        string VideoFileNamingAlgorithm,
        string? BlobFilesPath,
        string? CustomPerformerImageLocation,
        List<StashMetadataServerConfig> MetadataServers);

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
    private const double SceneMarkersStart = 0.68;
    private const double SceneMarkersEnd = 0.72;
    private const double ImagesStart = 0.72;
    private const double ImagesEnd = 0.93;
    private const double GalleriesStart = 0.93;
    private const double GalleriesEnd = 0.97;
    private const double LibraryPathsStart = 0.97;
    private const double LibraryPathsEnd = 0.985;
    private const double GeneratedAssetsStart = 0.985;
    private const double GeneratedAssetsEnd = 1.0;

    public StashMigrationService(
        CoveContext db,
        IBlobService blobService,
        ConfigService configService,
        CoveConfiguration config,
        IJobService jobService,
        IServiceScopeFactory scopeFactory,
        ILogger<StashMigrationService> logger)
    {
        _db = db;
        _blobService = blobService;
        _configService = configService;
        _config = config;
        _jobService = jobService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    private async Task<int?> GetImportEngagementUserIdAsync(CancellationToken ct)
        => await _db.Users.AsNoTracking()
            .Where(user => user.IsSystem && user.IsActive && !user.IsLocked)
            .OrderBy(user => user.Id)
            .Select(user => (int?)user.Id)
            .FirstOrDefaultAsync(ct);

    private async Task AddImportedOverallRatingsAsync(
        IEnumerable<ImportedRatingSeed> seeds,
        IReadOnlyDictionary<int, int> idMap,
        RatingHostType hostType,
        CancellationToken ct)
    {
        var userId = await GetImportEngagementUserIdAsync(ct);
        if (userId is null)
            return;

        var engagementUserId = userId.Value;

        var ratingsByHostId = new Dictionary<int, int>();
        foreach (var seed in seeds)
        {
            if (!seed.Value.HasValue || !idMap.TryGetValue(seed.StashId, out var hostId))
                continue;

            ratingsByHostId[hostId] = seed.Value.Value;
        }

        foreach (var hostIdChunk in ratingsByHostId.Keys.Chunk(500))
        {
            var existingByHostId = await _db.ReadSet<Rating>()
                .Where(rating => rating.UserId == engagementUserId
                    && rating.HostType == hostType
                    && rating.Aspect == "overall"
                    && hostIdChunk.Contains(rating.HostId))
                .ToDictionaryAsync(rating => rating.HostId, ct);

            foreach (var hostId in hostIdChunk)
            {
                var value = ratingsByHostId[hostId];
                if (existingByHostId.TryGetValue(hostId, out var existingRating))
                {
                    existingRating.Value = value;
                    _db.Entry(existingRating).State = EntityState.Modified;
                    continue;
                }

                _db.Ratings.Add(new Rating
                {
                    UserId = engagementUserId,
                    HostType = hostType,
                    HostId = hostId,
                    Aspect = "overall",
                    Value = value,
                });
            }

            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
        }
    }

    private async Task AddImportedAffinitiesAsync(
        IEnumerable<ImportedAffinitySeed> seeds,
        IReadOnlyDictionary<int, int> idMap,
        AffinityHostType hostType,
        CancellationToken ct)
    {
        var userId = await GetImportEngagementUserIdAsync(ct);
        if (userId is null)
            return;

        var engagementUserId = userId.Value;

        var affinitiesByHostId = new Dictionary<int, ImportedAffinityAggregate>();
        foreach (var seed in seeds)
        {
            if (!idMap.TryGetValue(seed.StashId, out var hostId))
                continue;
            if (seed.LikeCount <= 0 && seed.ViewCount <= 0 && seed.TotalConsumedSec <= 0 && seed.LastPositionSec is null && seed.LastConsumedAt is null)
                continue;

            if (!affinitiesByHostId.TryGetValue(hostId, out var aggregate))
            {
                aggregate = new ImportedAffinityAggregate { HostId = hostId };
                affinitiesByHostId[hostId] = aggregate;
            }

            aggregate.Add(seed);
        }

        foreach (var hostIdChunk in affinitiesByHostId.Keys.Chunk(500))
        {
            var existingByHostId = await _db.ReadSet<UserEntityAffinity>()
                .Where(affinity => affinity.UserId == engagementUserId
                    && affinity.HostType == hostType
                    && hostIdChunk.Contains(affinity.HostId))
                .ToDictionaryAsync(affinity => affinity.HostId, ct);

            foreach (var hostId in hostIdChunk)
            {
                var aggregate = affinitiesByHostId[hostId];
                if (existingByHostId.TryGetValue(hostId, out var existingAffinity))
                {
                    var existingLastConsumedAt = existingAffinity.LastConsumedAt;
                    existingAffinity.LikeCount = Math.Max(existingAffinity.LikeCount, aggregate.LikeCount);
                    existingAffinity.ViewCount = Math.Max(existingAffinity.ViewCount, aggregate.ViewCount);
                    existingAffinity.TotalConsumedSec = Math.Max(existingAffinity.TotalConsumedSec, aggregate.TotalConsumedSec);
                    if (!existingAffinity.LastConsumedAt.HasValue || (aggregate.LastConsumedAt.HasValue && aggregate.LastConsumedAt.Value > existingAffinity.LastConsumedAt.Value))
                        existingAffinity.LastConsumedAt = aggregate.LastConsumedAt;
                    if (aggregate.LastPositionSec.HasValue
                        && (!existingLastConsumedAt.HasValue || !aggregate.LastConsumedAt.HasValue || aggregate.LastConsumedAt.Value >= existingLastConsumedAt.Value))
                    {
                        existingAffinity.LastPositionSec = aggregate.LastPositionSec;
                    }

                    _db.Entry(existingAffinity).State = EntityState.Modified;
                    continue;
                }

                _db.UserEntityAffinities.Add(new UserEntityAffinity
                {
                    UserId = engagementUserId,
                    HostType = hostType,
                    HostId = hostId,
                    LikeCount = aggregate.LikeCount,
                    ViewCount = aggregate.ViewCount,
                    TotalConsumedSec = aggregate.TotalConsumedSec,
                    LastPositionSec = aggregate.LastPositionSec,
                    LastConsumedAt = aggregate.LastConsumedAt,
                });
            }

            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
        }
    }
    public async Task<StashPreviewResult> PreviewAsync(string stashDbPath, CancellationToken ct = default)
    {
        if (!File.Exists(stashDbPath))
            return new StashPreviewResult(false, $"Database file not found: {stashDbPath}", 0, 0, 0, 0, 0, 0, 0);
        try
        {
            var cs = OpenReadOnly(stashDbPath);
            await using var conn = new SqliteConnection(cs);
            await conn.OpenAsync(ct);

            // Surface whether Stash's generated folder is reachable so the user knows, before committing to
            // the import, that generated content (sprites/previews/screenshots/vtt) will be migratable.
            var (generatedFound, generatedPath) = DetectStashGeneratedFolder(stashDbPath);

            return new StashPreviewResult(true, null,
                await CountAsync(conn, "scenes", ct),
                await CountAsync(conn, "performers", ct),
                await CountAsync(conn, "tags", ct),
                await CountAsync(conn, "studios", ct),
                await CountAsync(conn, "groups", ct),
                await CountAsync(conn, "images", ct),
                await CountAsync(conn, "galleries", ct),
                generatedFound,
                generatedPath);
        }
        catch (Exception ex)
        {
            return new StashPreviewResult(false, ex.Message, 0, 0, 0, 0, 0, 0, 0);
        }
    }

    // Locates Stash's "generated" folder the same way the import does: read config.yml sitting next to the
    // database, resolve its "generated:" path, and check that the directory exists on disk. Returns the
    // resolved path (when known, even if missing) plus whether it was actually found.
    private static (bool Found, string? Path) DetectStashGeneratedFolder(string stashDbPath)
    {
        var configDir = Path.GetDirectoryName(stashDbPath);
        if (string.IsNullOrEmpty(configDir))
            return (false, null);

        var configPath = Path.Combine(configDir, "config.yml");
        if (!File.Exists(configPath))
            return (false, null);

        var generatedPath = ParseStashConfig(configPath).GeneratedPath;
        if (string.IsNullOrWhiteSpace(generatedPath))
            return (false, null);

        return (Directory.Exists(generatedPath), generatedPath);
    }

    public Task<StashImportResult> ImportAsync(string stashDbPath, StashImportOptions? options = null, CancellationToken ct = default)
    {
        return RunImportAsync(stashDbPath, options, NullJobProgress.Instance, ct);
    }

    public string StartImport(string stashDbPath, StashImportOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(stashDbPath))
            throw new ArgumentException("Stash database path is required.", nameof(stashDbPath));

        options = NormalizeImportOptions(options);

        lock (ImportSync)
        {
            if (!string.IsNullOrWhiteSpace(activeImportJobId))
            {
                if (!string.IsNullOrWhiteSpace(activeImportJobId)
                    && string.Equals(activeImportPath, stashDbPath, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Stash migration already running for {Path}; joining existing import", stashDbPath);
                    return activeImportJobId;
                }

                throw new StashMigrationInProgressException($"A Stash migration is already running for {activeImportPath}.");
            }

            activeImportPath = stashDbPath;
            string? jobId = null;
            jobId = _jobService.Enqueue("stash-import", "Importing Stash library", async (progress, ct) =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
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
        options = NormalizeImportOptions(options);
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
        var sw = Stopwatch.StartNew();
        var dbSizeBytes = new FileInfo(stashDbPath).Length;
        _logger.LogInformation("Starting Stash migration from {Path} ({Size:N0} bytes)", stashDbPath, dbSizeBytes);

        await ApplyCoveGeneratedPathOverrideAsync(options.CoveGeneratedPath, ct);

        var configDir = Path.GetDirectoryName(stashDbPath)!;
        var configPath = Path.Combine(configDir, "config.yml");
        var stashConfig = File.Exists(configPath)
            ? ParseStashConfig(configPath)
            : new StashConfigData([], null, "OSHASH", null, null, []);
        stashConfig = ApplyStashConfigPathMappings(stashConfig, options.PathMappings ?? []);

        _currentImportCustomPerformerImageLocation = stashConfig.CustomPerformerImageLocation;
        try
        {
            var blobMap = await RunMigrationPhaseAsync(
                "blobs",
                sw,
                () => ImportBlobsAsync(conn, stashConfig.BlobFilesPath, progress, BlobsStart, BlobsEnd, ct));

            var folderIdMap = await RunBulkInsertPhaseAsync(
                "folders",
                sw,
                () => ImportFoldersAsync(conn, options.PathMappings ?? [], progress, FoldersStart, FoldersEnd, ct));
            _db.ChangeTracker.Clear();

            var studioIdMap = await RunBulkInsertPhaseAsync(
                "studios",
                sw,
                () => ImportStudiosAsync(conn, blobMap, progress, StudiosStart, StudiosEnd, ct));
            _db.ChangeTracker.Clear();

            var tagIdMap = await RunBulkInsertPhaseAsync(
                "tags",
                sw,
                () => ImportTagsAsync(conn, blobMap, progress, TagsStart, TagsEnd, ct));
            _db.ChangeTracker.Clear();

            var performerIdMap = await RunBulkInsertPhaseAsync(
                "performers",
                sw,
                () => ImportPerformersAsync(conn, blobMap, tagIdMap, progress, PerformersStart, PerformersEnd, ct));
            _db.ChangeTracker.Clear();

            var groupIdMap = await RunBulkInsertPhaseAsync(
                "groups",
                sw,
                () => ImportGroupsAsync(conn, blobMap, studioIdMap, progress, GroupsStart, GroupsEnd, ct));
            _db.ChangeTracker.Clear();

            var (sceneCount, sceneIdMap, sceneGeneratedMap) = await RunBulkInsertPhaseAsync(
                "scenes",
                sw,
                () => ImportScenesAsync(conn, blobMap, folderIdMap, studioIdMap, tagIdMap, performerIdMap, groupIdMap, progress, ScenesStart, ScenesEnd, ct));
            _db.ChangeTracker.Clear();

            await RunBulkInsertPhaseAsync(
                "scene markers",
                sw,
                () => ImportSceneMarkerSegmentsAsync(conn, sceneIdMap, tagIdMap, progress, SceneMarkersStart, SceneMarkersEnd, ct));
            _db.ChangeTracker.Clear();

            var imageIdMap = await RunBulkInsertPhaseAsync(
                "images",
                sw,
                () => ImportImagesAsync(conn, folderIdMap, studioIdMap, tagIdMap, performerIdMap, progress, ImagesStart, ImagesEnd, ct));
            _db.ChangeTracker.Clear();

            var (galleryCount, galleryFileIdMap) = await RunBulkInsertPhaseAsync(
                "galleries",
                sw,
                () => ImportGalleriesAsync(conn, folderIdMap, studioIdMap, tagIdMap, performerIdMap, imageIdMap, progress, GalleriesStart, GalleriesEnd, ct));
            _db.ChangeTracker.Clear();

            await RunMigrationPhaseAsync(
                "reconcile zip links",
                sw,
                () => ReconcileImportedZipLinksAsync(conn, folderIdMap, imageIdMap, galleryFileIdMap, ct));
            _db.ChangeTracker.Clear();

            await RunMigrationPhaseAsync(
                "stash config",
                sw,
                async () =>
                {
                    progress.Report(LibraryPathsStart, "Importing Stash config...");
                    await ImportStashConfigAsync(stashConfig, ct);
                    progress.Report(LibraryPathsEnd, "Stash config imported");
                });

            if (options.MigrateGeneratedContent)
            {
                await RunMigrationPhaseAsync(
                    "generated content",
                    sw,
                    () => CopyGeneratedContentAsync(stashConfig, sceneGeneratedMap, options, progress, GeneratedAssetsStart, GeneratedAssetsEnd, ct));
            }
            else
            {
                _logger.LogInformation("Skipping generated content migration for {Path}", stashDbPath);
                progress.Report(GeneratedAssetsEnd, "Skipping generated scene assets");
            }

            // Bulk insert paths can leave the denormalized summary/count columns (video durations &
            // resolutions, gallery image counts, studio/performer/tag rollups) unpopulated, which makes
            // count-based filters and sorts behave as if every row were zero. Recompute them once the
            // whole library is in place so those filters/sorts work on freshly imported data.
            await RunMigrationPhaseAsync(
                "recompute derived counts",
                sw,
                async () =>
                {
                    progress.Report(GeneratedAssetsEnd, "Recomputing counts and summaries...");
                    await _db.RecomputeAllDerivedCountsAsync(cancellationToken: ct);
                    progress.Report(1.0, "Counts and summaries recomputed");
                });

            _logger.LogInformation("Migration complete in {Elapsed}: {S} scenes, {P} performers, {T} tags, {St} studios, {G} groups, {I} images, {Ga} galleries",
                sw.Elapsed, sceneCount, performerIdMap.Count, tagIdMap.Count, studioIdMap.Count, groupIdMap.Count, imageIdMap.Count, galleryCount);

            return new StashImportResult(sceneCount, performerIdMap.Count, tagIdMap.Count, studioIdMap.Count, groupIdMap.Count, imageIdMap.Count, galleryCount);
        }
        finally
        {
            _currentImportCustomPerformerImageLocation = null;
        }
    }

    private async Task<T> RunBulkInsertPhaseAsync<T>(string phaseName, Stopwatch totalStopwatch, Func<Task<T>> action)
    {
        var previousAutoDetectChanges = _db.ChangeTracker.AutoDetectChangesEnabled;
        _db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            _logger.LogDebug("[StashTiming] phase={Phase} autoDetectChanges=false", phaseName);
            return await RunMigrationPhaseAsync(phaseName, totalStopwatch, action);
        }
        finally
        {
            _db.ChangeTracker.AutoDetectChangesEnabled = previousAutoDetectChanges;
        }
    }

    private async Task<T> RunMigrationPhaseAsync<T>(string phaseName, Stopwatch totalStopwatch, Func<Task<T>> action)
    {
        var phaseStopwatch = Stopwatch.StartNew();
        var phaseStart = totalStopwatch.Elapsed;
        _logger.LogInformation("Stash migration: starting phase {Phase} at +{Elapsed}", phaseName, phaseStart);
        _logger.LogDebug("[StashTiming] phase={Phase} event=start totalMs={TotalMilliseconds:F0}", phaseName, phaseStart.TotalMilliseconds);

        try
        {
            var result = await action();
            phaseStopwatch.Stop();
            _logger.LogInformation("Stash migration: finished phase {Phase} in {PhaseElapsed} (total {TotalElapsed})", phaseName, phaseStopwatch.Elapsed, totalStopwatch.Elapsed);
            _logger.LogDebug(
                "[StashTiming] phase={Phase} event=finish phaseMs={PhaseMilliseconds:F0} totalMs={TotalMilliseconds:F0}",
                phaseName,
                phaseStopwatch.Elapsed.TotalMilliseconds,
                totalStopwatch.Elapsed.TotalMilliseconds);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            phaseStopwatch.Stop();
            _logger.LogError(
                ex,
                "[Stash] Failed {Phase} after {PhaseElapsed} (total {TotalElapsed})",
                phaseName,
                phaseStopwatch.Elapsed,
                totalStopwatch.Elapsed);
            _logger.LogDebug(
                "[StashTiming] phase={Phase} event=fail phaseMs={PhaseMilliseconds:F0} totalMs={TotalMilliseconds:F0}",
                phaseName,
                phaseStopwatch.Elapsed.TotalMilliseconds,
                totalStopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    private async Task RunMigrationPhaseAsync(string phaseName, Stopwatch totalStopwatch, Func<Task> action)
    {
        await RunMigrationPhaseAsync(
            phaseName,
            totalStopwatch,
            async () =>
            {
                await action();
                return true;
            });
    }
}
