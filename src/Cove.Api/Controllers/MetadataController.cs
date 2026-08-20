using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Common;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/metadata")]
public class MetadataController(
    IScanService scanService,
    IJobService jobService,
    GenerateJobService generateJobService,
    ICleanService cleanService,
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    CoveConfiguration config,
    IEventBus eventBus,
    ILogger<MetadataController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions MetadataExportJsonOptions = new(CoveJson.Default)
    {
        WriteIndented = true,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };

    [HttpPost("scan")]
    [RequiresPermission(Permissions.LibraryScan)]
    public ActionResult<object> StartScan([FromBody] ScanOptionsDto? opts)
    {
        var enableAllGenerators = opts?.ScanGenerators == true;
        var jobId = scanService.StartScan(new ScanOperationOptions
        {
            Paths = opts?.Paths,
            GenerateCovers = enableAllGenerators || opts?.ScanGenerateCovers == true,
            GeneratePreviews = enableAllGenerators || opts?.ScanGeneratePreviews == true,
            GenerateSprites = enableAllGenerators || opts?.ScanGenerateSprites == true,
            GeneratePhashes = enableAllGenerators || opts?.ScanGeneratePhashes == true,
            GenerateMd5 = enableAllGenerators || opts?.ScanGenerateMd5 == true,
            GenerateImageThumbnails = enableAllGenerators || opts?.ScanGenerateThumbnails == true,
            GenerateImagePhashes = enableAllGenerators || opts?.ScanGenerateImagePhashes == true,
            GenerateAudioPhashes = enableAllGenerators || opts?.ScanGenerateAudioPhashes == true,
            GenerateTextPhashes = enableAllGenerators || opts?.ScanGenerateTextPhashes == true,
            Rescan = opts?.Rescan == true,
        });
        return Ok(new { jobId });
    }

    /// <summary>
    /// Lists folders the user may target for a selective scan/generate. With no <paramref name="path"/>
    /// it returns the configured library roots; otherwise it returns the immediate subfolders of the
    /// given path. The path MUST be at or below a configured library root — anything else is rejected,
    /// so the folder picker can never drill outside the library.
    /// </summary>
    [HttpGet("library-folders")]
    [RequiresPermission(Permissions.LibraryScan, Permissions.FilesRead, Mode = PermissionMode.Any)]
    public ActionResult<List<LibraryFolderDto>> GetLibraryFolders([FromQuery] string? path, [FromQuery] bool probeChildren = true)
    {
        var roots = config.CovePaths
            .Select(covePath => covePath.Path)
            .Where(rootPath => !string.IsNullOrWhiteSpace(rootPath))
            .Select(rootPath => CanonicalizePath(rootPath!))
            .Where(rootPath => rootPath.Length > 0)
            .Distinct(PathComparer)
            .ToList();

        if (string.IsNullOrWhiteSpace(path))
        {
            return Ok(roots
                .OrderBy(root => root, PathComparer)
                .Select(root => new LibraryFolderDto(root, root, !probeChildren || SafeHasSubdirectories(root)))
                .ToList());
        }

        var requested = CanonicalizePath(path);
        var isLogicallyContained = roots.Any(root => IsAtOrUnderPath(requested, root));
        var physicalRoots = roots
            .Select(ResolvePhysicalPath)
            .Where(root => root.Length > 0)
            .ToList();
        var physicalRequested = ResolvePhysicalPath(requested);
        if (requested.Length == 0 || !isLogicallyContained || physicalRequested.Length == 0 || !physicalRoots.Any(root => IsAtOrUnderPath(physicalRequested, root)))
            return StatusCode(StatusCodes.Status403Forbidden, new { code = "OUTSIDE_LIBRARY", message = "Path is not within a configured library folder." });

        if (!Directory.Exists(requested))
            return Ok(new List<LibraryFolderDto>());

        try
        {
            return Ok(Directory.GetDirectories(requested)
                .Select(CanonicalizePath)
                .Select(dir => new { Logical = dir, Physical = ResolveChildPhysicalPath(dir, physicalRequested) })
                .Where(dir => dir.Logical.Length > 0 && dir.Physical.Length > 0
                    && physicalRoots.Any(root => IsAtOrUnderPath(dir.Physical, root)))
                .Select(dir => dir.Logical)
                .OrderBy(dir => dir, PathComparer)
                .Select(dir => new LibraryFolderDto(dir[(dir.LastIndexOf('/') + 1)..], dir, !probeChildren || SafeHasSubdirectories(dir)))
                .ToList());
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            logger.LogWarning(ex, "Failed to list subfolders of {Path}", requested);
            return Ok(new List<LibraryFolderDto>());
        }
    }

    [HttpGet("filesystem-policy")]
    [RequiresPermission(Permissions.LibraryScan, Permissions.FilesRead, Permissions.GroupsRead, Mode = PermissionMode.Any)]
    public ActionResult<object> GetFilesystemPolicy()
        => Ok(new { caseSensitive = FilesystemPaths.PathComparison == StringComparison.Ordinal });

    private static string CanonicalizePath(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())).Replace('\\', '/'); }
        catch { return string.Empty; }
    }

    private static string ResolvePhysicalPath(string path)
    {
        try
        {
            var pending = Path.GetFullPath(path);
            for (var linkCount = 0; linkCount <= 63; linkCount++)
            {
                var pathRoot = Path.GetPathRoot(pending);
                if (string.IsNullOrEmpty(pathRoot)) return string.Empty;
                var segments = Path.GetRelativePath(pathRoot, pending)
                    .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
                var current = pathRoot;
                var followedLink = false;
                for (var index = 0; index < segments.Length; index++)
                {
                    if (segments[index] == ".") continue;
                    current = Path.Combine(current, segments[index]);
                    var directory = new DirectoryInfo(current);
                    if (directory.LinkTarget == null) continue;

                    var target = directory.ResolveLinkTarget(returnFinalTarget: false)?.FullName;
                    if (string.IsNullOrEmpty(target)) return string.Empty;
                    pending = Path.GetFullPath(segments[(index + 1)..].Aggregate(target, (currentPath, segment) => Path.Combine(currentPath, segment)));
                    followedLink = true;
                    break;
                }

                if (!followedLink) return CanonicalizePath(current);
            }
            return string.Empty;
        }
        catch { return string.Empty; }
    }

    private static string ResolveChildPhysicalPath(string logicalChild, string physicalParent)
    {
        try
        {
            var child = new DirectoryInfo(logicalChild);
            return child.LinkTarget != null
                ? ResolvePhysicalPath(logicalChild)
                : CanonicalizePath(Path.Combine(physicalParent, child.Name));
        }
        catch { return string.Empty; }
    }

    private static StringComparer PathComparer => FilesystemPaths.PathComparer;

    private static StringComparison PathComparison => FilesystemPaths.PathComparison;

    // Segment-aware containment check so "/library" does not match "/library-other".
    private static bool IsAtOrUnderPath(string candidate, string root)
        => candidate.Length > 0 && root.Length > 0
            && (candidate.Equals(root, PathComparison)
            || candidate.StartsWith(root.EndsWith('/') ? root : root + "/", PathComparison));

    private static bool SafeHasSubdirectories(string dir)
    {
        try { return Directory.Exists(dir) && Directory.EnumerateDirectories(dir).Any(); }
        catch { return false; }
    }

    [HttpPost("generate")]
    [RequiresPermission(Permissions.JobsRun)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosWrite, ActionArgumentName = "opts", PropertyName = "VideoIds")]
    public ActionResult<object> StartGenerate([FromBody] GenerateOptionsDto? opts)
    {
        var options = opts ?? new GenerateOptionsDto();
        if (GenerateJobService.RequiresPathsForExplicitNonVideoWork(options))
        {
            return BadRequest(new
            {
                error = "Non-video generate options require paths when videoIds are supplied. Provide paths for image, gallery, audio, or text work, or run those options without videoIds."
            });
        }

        return Ok(new { jobId = generateJobService.Start(options) });
    }

    [HttpPost("clean")]
    [RequiresPermission(Permissions.LibraryClean)]
    public ActionResult<object> StartClean([FromBody] CleanOptionsDto? opts)
    {
        // Delegate to the zip-aware CleanService. The previous inline implementation flat-listed
        // BaseFileEntity rows and removed any whose Path did not exist on disk — but zip-gallery
        // images have a synthetic Path (".../foo.zip#virtual/img.jpg") that never exists as a
        // standalone file, so it deleted every zip-internal image (the "757479 missing files
        // removed" reports) while leaving orphaned parent entities that scan then skipped.
        // CleanService resolves each file's containing archive via ZipFileId, so zip contents are
        // only removed when the archive itself is gone.
        var jobId = cleanService.StartClean(opts?.DryRun == true);
        return Ok(new { jobId });
    }

    [HttpPost("export")]
    [RequiresPermission(Permissions.SystemBackup)]
    public ActionResult<object> StartExport([FromBody] ExportOptionsDto? opts)
    {
        var jobId = jobService.Enqueue("export", "Exporting metadata", async (progress, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var dbCtx = scope.ServiceProvider.GetRequiredService<CoveContext>();

            var exportPath = Path.Combine(config.GeneratedPath ?? Path.GetTempPath(), "export");
            Directory.CreateDirectory(exportPath);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var exportFile = Path.Combine(exportPath, $"cove-export-{timestamp}.json");

            var exportData = new Dictionary<string, object>();

            if (opts?.IncludeVideos != false)
            {
                progress.Report(0.1, "Exporting videos...");
                exportData["videos"] = await dbCtx.Videos
                    .Include(s => s.VideoTags).ThenInclude(st => st.Tag)
                    .Include(s => s.VideoPerformers).ThenInclude(sp => sp.Performer)
                    .Include(s => s.Studio)
                    .Include(s => s.Files).ThenInclude(f => f.Fingerprints)
                    .AsNoTracking()
                    .AsSplitQuery()
                    .ToListAsync(ct);
            }

            if (opts?.IncludePerformers != false)
            {
                progress.Report(0.3, "Exporting performers...");
                exportData["performers"] = await dbCtx.Performers.AsNoTracking().ToListAsync(ct);
            }

            if (opts?.IncludeStudios != false)
            {
                progress.Report(0.5, "Exporting studios...");
                exportData["studios"] = await dbCtx.Studios.AsNoTracking().ToListAsync(ct);
            }

            if (opts?.IncludeTags != false)
            {
                progress.Report(0.6, "Exporting tags...");
                exportData["tags"] = await dbCtx.Tags.AsNoTracking().ToListAsync(ct);
            }

            if (opts?.IncludeGalleries != false)
            {
                progress.Report(0.7, "Exporting galleries...");
                exportData["galleries"] = await dbCtx.Galleries.AsNoTracking().ToListAsync(ct);
            }

            if (opts?.IncludeGroups != false)
            {
                progress.Report(0.8, "Exporting groups...");
                exportData["groups"] = await dbCtx.Groups.AsNoTracking().ToListAsync(ct);
            }

            progress.Report(0.9, "Writing export file...");
            await System.IO.File.WriteAllTextAsync(exportFile, JsonSerializer.Serialize(exportData, MetadataExportJsonOptions), ct);

            logger.LogInformation("Export completed: {Path}", exportFile);
        }, exclusive: false);

        return Ok(new { jobId });
    }

    [HttpPost("import")]
    [RequiresPermission(Permissions.SystemRestore)]
    public ActionResult<object> StartImport([FromBody] ImportOptionsDto? opts)
    {
        var filePath = opts?.FilePath;
        if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
            return BadRequest(new { error = "Import file path is required and must exist" });

        var overwrite = opts?.DuplicateHandling ?? false;

        var jobId = jobService.Enqueue("import", "Importing metadata", async (progress, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var dbCtx = scope.ServiceProvider.GetRequiredService<CoveContext>();

            progress.Report(0.05, "Reading import file...");
            var json = await System.IO.File.ReadAllTextAsync(filePath, ct);
            var importData = JsonSerializer.Deserialize<JsonElement>(json, CoveJson.Default);
            var importTags = ReadImportEntities<Tag>(importData, "tags");
            var importStudios = ReadImportEntities<Studio>(importData, "studios");
            var importPerformers = ReadImportEntities<Performer>(importData, "performers");
            var importGroups = ReadImportEntities<Group>(importData, "groups");

            // An import can touch several entity kinds. Keep all staged saves atomic so a later
            // identity conflict or malformed relationship cannot leave an unretryable partial job.
            await dbCtx.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                dbCtx.ChangeTracker.Clear();
                await using var transaction = await dbCtx.Database.BeginTransactionAsync(ct);

                // Import tags first (no dependencies)
                if (importTags.Count > 0)
                {
                    progress.Report(0.1, "Importing tags...");
                    var normalizedNames = importTags
                        .Select(tag => TagNameRules.NormalizeCanonicalName(tag.Name))
                        .ToArray();
                    var tagLookup = await RelationNameResolver.ResolveTagsAsync(dbCtx, normalizedNames, ct);
                    foreach (var tag in importTags)
                    {
                        ct.ThrowIfCancellationRequested();
                        var normalizedName = TagNameRules.NormalizeCanonicalName(tag.Name);
                        tagLookup.TryGetValue(normalizedName, out var existing);
                        if (existing != null)
                        {
                            if (overwrite) { existing.Description = tag.Description; existing.Favorite = tag.Favorite; }
                        }
                        else
                        {
                            var created = new Tag { Name = normalizedName, Description = tag.Description, Favorite = tag.Favorite };
                            dbCtx.Tags.Add(created);
                            tagLookup[normalizedName] = created;
                        }
                    }
                    await dbCtx.SaveChangesAsync(ct);
                }

                // Import studios (may reference parent studios)
                if (importStudios.Count > 0)
                {
                    progress.Report(0.3, "Importing studios...");
                    await ImportStudiosAsync(dbCtx, importStudios, overwrite, ct);
                }

                // Import performers
                if (importPerformers.Count > 0)
                {
                    progress.Report(0.5, "Importing performers...");
                    await ImportPerformersAsync(dbCtx, importPerformers, overwrite, ct);
                }

                // Import groups
                if (importGroups.Count > 0)
                {
                    progress.Report(0.7, "Importing groups...");
                    foreach (var group in importGroups)
                    {
                        ct.ThrowIfCancellationRequested();
                        var existing = await dbCtx.Groups.FirstOrDefaultAsync(g => g.Name == group.Name, ct);
                        if (existing != null)
                        {
                            if (overwrite) { existing.Director = group.Director; existing.Synopsis = group.Synopsis; }
                        }
                        else
                        {
                            dbCtx.Groups.Add(new Group { Name = group.Name, Director = group.Director, Synopsis = group.Synopsis, Duration = group.Duration });
                        }
                    }
                    await dbCtx.SaveChangesAsync(ct);
                }

                await transaction.CommitAsync(ct);
            });

            progress.Report(1.0, "Import completed");
            logger.LogInformation("Metadata import completed from: {Path}", filePath);
        }, exclusive: false);

        return Ok(new { jobId });
    }

    internal static List<TEntity> ReadImportEntities<TEntity>(JsonElement root, string propertyName)
        where TEntity : class
        => root.TryGetProperty(propertyName, out var value)
            ? JsonSerializer.Deserialize<List<TEntity>>(value.GetRawText(), CoveJson.Default) ?? []
            : [];

    internal static Dictionary<string, TEntity> BuildUniqueImportIdentityLookup<TEntity>(
        IEnumerable<TEntity> candidates,
        Func<TEntity, string> identitySelector,
        IReadOnlySet<string> requestedKeys,
        string entityType)
        where TEntity : class
    {
        var result = new Dictionary<string, TEntity>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var key = identitySelector(candidate);
            if (!requestedKeys.Contains(key))
                continue;
            if (!result.TryAdd(key, candidate))
                throw new EntityNameConflictException(entityType);
        }

        return result;
    }

    internal static async Task ImportStudiosAsync(
        CoveContext db,
        IReadOnlyCollection<Studio> imported,
        bool overwrite,
        CancellationToken ct)
    {
        var groups = imported
            .GroupBy(studio => EntityNameRules.StudioIdentityKey(studio.Name), StringComparer.Ordinal)
            .ToArray();
        var requestedKeys = groups.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var existingByIdentity = BuildUniqueImportIdentityLookup(
            await db.Studios.ToListAsync(ct),
            studio => EntityNameRules.StudioIdentityKey(studio.Name),
            requestedKeys,
            NameConflictEntityTypes.Studio);

        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var source = group.Last();
            if (existingByIdentity.TryGetValue(group.Key, out var existing))
            {
                if (overwrite)
                    ApplyImportedStudioMetadata(existing, source);
                continue;
            }

            var created = new Studio
            {
                Name = EntityNameRules.NormalizeCanonicalName(group.First().Name),
            };
            ApplyImportedStudioMetadata(created, source);
            db.Studios.Add(created);
            existingByIdentity[group.Key] = created;
        }
        await db.SaveChangesAsync(ct);
    }

    internal static async Task ImportPerformersAsync(
        CoveContext db,
        IReadOnlyCollection<Performer> imported,
        bool overwrite,
        CancellationToken ct)
    {
        var groups = imported
            .GroupBy(
                performer => EntityNameRules.PerformerIdentityKey(performer.Name, performer.Disambiguation),
                StringComparer.Ordinal)
            .ToArray();
        var requestedKeys = groups.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var existingByIdentity = BuildUniqueImportIdentityLookup(
            await db.Performers.ToListAsync(ct),
            performer => EntityNameRules.PerformerIdentityKey(performer.Name, performer.Disambiguation),
            requestedKeys,
            NameConflictEntityTypes.Performer);

        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var source = group.Last();
            if (existingByIdentity.TryGetValue(group.Key, out var existing))
            {
                if (overwrite)
                    ApplyImportedPerformerMetadata(existing, source);
                continue;
            }

            var first = group.First();
            var created = new Performer
            {
                Name = EntityNameRules.NormalizeCanonicalName(first.Name),
                Disambiguation = EntityNameRules.NormalizeDisambiguation(first.Disambiguation),
            };
            ApplyImportedPerformerMetadata(created, source);
            db.Performers.Add(created);
            existingByIdentity[group.Key] = created;
        }
        await db.SaveChangesAsync(ct);
    }

    internal static void ApplyImportedStudioMetadata(Studio target, Studio source)
    {
        target.Details = source.Details;
        target.Favorite = source.Favorite;
        target.Organized = source.Organized;
    }

    internal static void ApplyImportedPerformerMetadata(Performer target, Performer source)
    {
        target.Gender = source.Gender;
        target.Birthdate = source.Birthdate;
        target.DeathDate = source.DeathDate;
        target.Ethnicity = source.Ethnicity;
        target.Country = source.Country;
        target.EyeColor = source.EyeColor;
        target.HairColor = source.HairColor;
        target.HeightCm = source.HeightCm;
        target.Weight = source.Weight;
        target.Measurements = source.Measurements;
        target.FakeTits = source.FakeTits;
        target.PenisLength = source.PenisLength;
        target.Circumcised = source.Circumcised;
        target.CareerStart = source.CareerStart;
        target.CareerEnd = source.CareerEnd;
        target.Tattoos = source.Tattoos;
        target.Piercings = source.Piercings;
        target.Details = source.Details;
        target.Favorite = source.Favorite;
    }

    [HttpPost("clean-generated")]
    [RequiresPermission(Permissions.SystemSettingsWrite)]
    public ActionResult<object> CleanGenerated()
    {
        var jobId = jobService.Enqueue("clean-generated", "Cleaning generated files", async (progress, ct) =>
        {
            var generatedPath = config.GeneratedPath;
            if (string.IsNullOrEmpty(generatedPath) || !Directory.Exists(generatedPath))
            {
                logger.LogWarning("Generated path not configured or does not exist");
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var dbCtx = scope.ServiceProvider.GetRequiredService<CoveContext>();

            // Only delete generated artifacts whose owning entity no longer exists. A blind directory
            // wipe permanently destroys in-use video covers/previews/sprites/VTT — those are NOT
            // regenerated on demand (unlike image thumbnails), so wiping them left users with missing
            // video thumbnails for videos that still exist. Load the live entity ids and keep any file
            // that still belongs to one.
            var liveVideoIds = new HashSet<int>(await dbCtx.Videos.Select(v => v.Id).ToListAsync(ct));
            var liveImageIds = new HashSet<int>(await dbCtx.Images.Select(i => i.Id).ToListAsync(ct));

            var dirs = new[] { "screenshots", "thumbnails", "previews", "sprites", "transcodes", "vtt", "segment-previews" };
            var totalCleared = 0L;
            var deleted = 0;
            var kept = 0;

            for (var i = 0; i < dirs.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report((double)(i + 1) / dirs.Length, $"Cleaning {dirs[i]}...");

                var dir = Path.Combine(generatedPath, dirs[i]);
                if (!Directory.Exists(dir)) continue;

                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();

                    // Generated filenames are prefixed with the owning entity's integer id, delimited by
                    // '.', '_' or '-' (e.g. "<videoId>.jpg", "<videoId>_sprite.jpg", "<imageId>_m320_3").
                    // Files with no leading integer id (e.g. blob-keyed thumbnails under entity-blobs/)
                    // are kept — deleting them is harmless (they regenerate on demand) but they can't be
                    // matched to a live entity here, so err toward keeping. Only delete when the parsed id
                    // is absent from every live entity set.
                    var id = ParseLeadingEntityId(Path.GetFileName(file));
                    if (id is int entityId && !liveVideoIds.Contains(entityId) && !liveImageIds.Contains(entityId))
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            var len = fi.Length;
                            fi.Delete();
                            totalCleared += len;
                            deleted++;
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to delete orphaned generated file {File}", file);
                        }
                    }
                    else
                    {
                        kept++;
                    }
                }
            }

            logger.LogInformation("Cleaned generated files. Deleted {Deleted} orphaned files ({Size} bytes); kept {Kept} in-use files", deleted, totalCleared, kept);
        }, exclusive: false);

        return Ok(new { jobId });
    }

    // Parses the leading integer entity id from a generated filename, requiring the digits to be
    // followed by a '.', '_' or '-' delimiter (or end of name) so partial/hex-prefixed names like
    // "12ab.jpg" or a hex blob id are not misread as an entity id.
    private static int? ParseLeadingEntityId(string fileName)
    {
        var end = 0;
        while (end < fileName.Length && char.IsAsciiDigit(fileName[end]))
            end++;
        if (end == 0)
            return null;
        if (end < fileName.Length && fileName[end] is not ('.' or '_' or '-'))
            return null;
        return int.TryParse(fileName.AsSpan(0, end), out var id) ? id : null;
    }

    [HttpPost("identify")]
    [RequiresPermission(Permissions.LibraryIdentify)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosWrite, ActionArgumentName = "opts", PropertyName = "VideoIds")]
    public ActionResult<object> StartIdentify([FromBody] IdentifyOptionsDto? opts)
    {
        var jobId = jobService.Enqueue("identify", "Identifying videos", async (progress, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var dbCtx = scope.ServiceProvider.GetRequiredService<CoveContext>();
            var metadataServerSvc = scope.ServiceProvider.GetService<MetadataServerService>();
            var scraperSvc = scope.ServiceProvider.GetService<ScraperService>();
            var scrapeAttemptSvc = scope.ServiceProvider.GetService<ScrapeAttemptService>();

            var videoIds = opts?.VideoIds?.Count > 0
                ? await dbCtx.Videos
                    .Where(video => opts.VideoIds.Contains(video.Id))
                    .Select(video => video.Id)
                    .ToListAsync(ct)
                : await dbCtx.Videos
                    .Select(video => video.Id)
                    .ToListAsync(ct);

            var identifyDefaults = config.Scraping.IdentifyDefaults;
            var sourceEndpoints = ResolveIdentifyMetadataServerEndpoints(opts?.Sources, config.Scraping.MetadataServers);
            var sourceOrder = sourceEndpoints?
                .Select((endpoint, index) => new { endpoint, index })
                .ToDictionary(item => item.endpoint, item => item.index, StringComparer.OrdinalIgnoreCase);

            // Which URL-capable video scrapers are enabled as identify sources (null = all eligible;
            // empty = the caller selected only metadata servers, so the scraper stage is skipped).
            var enabledScraperIds = ResolveIdentifyScraperIds(opts?.Sources, scraperSvc);

            // Build import config from identify options
            var importConfig = new MetadataServerVideoImportRequestDto
            {
                SetCoverImage = opts?.SetCoverImage ?? true,
                SetTags = opts?.SetTags ?? true,
                SetPerformers = opts?.SetPerformers ?? true,
                SetStudio = opts?.SetStudio ?? true,
                OnlyExistingTags = !(opts?.CreateTags ?? identifyDefaults.CreateTags),
                OnlyExistingPerformers = !(opts?.CreatePerformers ?? identifyDefaults.CreatePerformers),
                OnlyExistingStudio = !(opts?.CreateStudios ?? identifyDefaults.CreateStudios),
                MarkOrganized = opts?.MarkOrganized ?? false,
                FieldStrategies = opts?.FieldStrategies,
                PerformerGenders = opts?.PerformerGenders,
                SkipSingleNamePerformers = opts?.SkipSingleNamePerformers ?? true,
            };

            var total = videoIds.Count;
            var identifiedCount = 0;
            var warningCount = 0;
            for (var i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report((double)(i + 1) / total, $"Identifying video {i + 1}/{total}");

                dbCtx.ChangeTracker.Clear();
                metadataServerSvc?.ResetTrackedIdentityState();
                var video = await dbCtx.Videos
                    .Include(entity => entity.Files).ThenInclude(file => file.Fingerprints)
                    .Include(entity => entity.VideoTags)
                    .Include(entity => entity.VideoPerformers)
                    .Include(entity => entity.RemoteIds)
                    .Include(entity => entity.Urls)
                    .AsSplitQuery()
                    .SingleAsync(entity => entity.Id == videoIds[i], ct);
                var fingerprints = video.Files.SelectMany(f => f.Fingerprints).ToList();
                var identified = false;

                // Attempt MetadataServer identification (needs fingerprints to auto-match a candidate).
                if (fingerprints.Count > 0 && metadataServerSvc != null && (sourceEndpoints == null || sourceEndpoints.Count > 0))
                {
                    try
                    {
                        IReadOnlyList<MetadataServerVideoMatchDto> matches;
                        if (sourceEndpoints == null)
                        {
                            matches = await metadataServerSvc.SearchVideosAsync(video, null, null, null, ct);
                        }
                        else
                        {
                            var orderedMatches = new List<MetadataServerVideoMatchDto>();
                            foreach (var endpoint in sourceEndpoints)
                            {
                                orderedMatches.AddRange(await metadataServerSvc.SearchVideosAsync(video, null, endpoint, null, ct));
                            }
                            matches = orderedMatches;
                        }

                        logger.LogTrace(
                            "Identify video {VideoId}: metadata servers returned {MatchCount} candidate match(es)",
                            video.Id, matches.Count);

                        if (matches.Count > 0)
                        {
                            // Evaluate every candidate once, capturing its scores and whether it cleared
                            // the auto-apply thresholds (and which guard rejected it). This is purely for
                            // diagnostics; the ranking/selection below is unchanged.
                            var evaluatedCandidates = matches
                                .Select(match =>
                                {
                                    var durationDifferenceSeconds = GetDurationDifferenceSeconds(video, match);
                                    var phashDistance = GetBestPhashDistance(video, match);
                                    var passed = MeetsIdentifyAutoApplyThresholds(match.MatchCount, durationDifferenceSeconds, phashDistance, identifyDefaults, out var failureReason);
                                    return new
                                    {
                                        Match = match,
                                        DurationDifferenceSeconds = durationDifferenceSeconds,
                                        PhashDistance = phashDistance,
                                        Passed = passed,
                                        FailureReason = failureReason,
                                    };
                                })
                                .ToList();

                            foreach (var candidate in evaluatedCandidates)
                            {
                                logger.LogTrace(
                                    "Identify video {VideoId}: candidate {CandidateId} '{CandidateTitle}' from {Endpoint} ({ServerName}) - matchCount={MatchCount}, durationDiff={DurationDiff}, phashDistance={PhashDistance} => {Result}; failureReason={FailureReason}",
                                    video.Id,
                                    candidate.Match.Id,
                                    candidate.Match.Title,
                                    candidate.Match.Endpoint,
                                    candidate.Match.MetadataServerName,
                                    candidate.Match.MatchCount,
                                    candidate.DurationDifferenceSeconds,
                                    candidate.PhashDistance,
                                    candidate.Passed ? "PASSED" : "FAILED",
                                    candidate.FailureReason);
                            }

                            var rankedMatches = evaluatedCandidates
                                .Where(candidate => candidate.Passed)
                                .OrderBy(candidate => sourceOrder != null && sourceOrder.TryGetValue(candidate.Match.Endpoint, out var index) ? index : int.MaxValue)
                                .ThenByDescending(candidate => candidate.Match.MatchCount)
                                .ThenBy(candidate => candidate.PhashDistance ?? int.MaxValue)
                                .ThenBy(candidate => candidate.DurationDifferenceSeconds ?? double.MaxValue)
                                .ToList();

                            if (rankedMatches.Count == 0)
                            {
                                logger.LogTrace(
                                    "Identify video {VideoId}: {MatchCount} candidate(s) returned, 0 passed thresholds",
                                    video.Id, matches.Count);
                                continue;
                            }

                            // Skip multiple matches only when explicitly requested. By default we
                            // apply the top-ranked candidate rather than skipping the whole video.
                            if ((opts?.SkipMultipleMatches ?? false) && rankedMatches.Count > 1)
                            {
                                logger.LogTrace(
                                    "Identify video {VideoId}: skipping because {PassedCount} candidates passed thresholds and SkipMultipleMatches is enabled",
                                    video.Id, rankedMatches.Count);
                                continue;
                            }

                            var bestCandidate = rankedMatches[0];
                            var best = bestCandidate.Match;
                            logger.LogTrace(
                                "Identify video {VideoId}: selected candidate {CandidateId} '{CandidateTitle}' from {Endpoint} ({ServerName}) - matchCount={MatchCount}, durationDiff={DurationDiff}, phashDistance={PhashDistance} (best of {PassedCount} passing of {TotalCount} returned)",
                                video.Id,
                                best.Id,
                                best.Title,
                                best.Endpoint,
                                best.MetadataServerName,
                                best.MatchCount,
                                bestCandidate.DurationDifferenceSeconds,
                                bestCandidate.PhashDistance,
                                rankedMatches.Count,
                                matches.Count);

                            var imported = await metadataServerSvc.MergeVideoWithWarningsAsync(video, best.Endpoint, best.Id, importConfig, ct);
                            if (imported.Imported)
                            {
                                await dbCtx.SaveChangesAsync(ct);
                                if (imported.Warnings.Count > 0)
                                {
                                    warningCount++;
                                    logger.LogWarning(
                                        "MetadataServer identify partially updated video {VideoId}: {Warnings}",
                                        video.Id,
                                        string.Join(" ", imported.Warnings));
                                }
                                eventBus.Publish(new EntityEvent(EventType.VideoUpdated, "Video", video.Id));
                                identified = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "MetadataServer identify failed for video {VideoId}", video.Id);
                        dbCtx.ChangeTracker.Clear();
                        metadataServerSvc.ResetTrackedIdentityState();
                        video = await dbCtx.Videos
                            .Include(entity => entity.Files).ThenInclude(file => file.Fingerprints)
                            .Include(entity => entity.VideoTags)
                            .Include(entity => entity.VideoPerformers)
                            .Include(entity => entity.RemoteIds)
                            .Include(entity => entity.Urls)
                            .AsSplitQuery()
                            .SingleAsync(entity => entity.Id == video.Id, ct);
                    }
                }

                // Attempt scraper identification from the video's existing URL(s). The URL is the
                // identity, so unlike metadata servers this needs no fingerprint match and also runs
                // for fingerprint-less videos. Skipped if a metadata server already identified this one.
                if (!identified && scraperSvc != null && scrapeAttemptSvc != null
                    && (enabledScraperIds == null || enabledScraperIds.Count > 0))
                {
                    try
                    {
                        identified = await TryScraperIdentifyVideoAsync(
                            video,
                            enabledScraperIds,
                            opts,
                            identifyDefaults,
                            scraperSvc,
                            scrapeAttemptSvc,
                            ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Scraper identify failed for video {VideoId}", video.Id);
                        dbCtx.ChangeTracker.Clear();
                        metadataServerSvc?.ResetTrackedIdentityState();
                    }
                }

                if (identified)
                {
                    await dbCtx.SaveChangesAsync(ct);
                    identifiedCount++;
                }
            }

            await dbCtx.SaveChangesAsync(ct);
            if (warningCount > 0)
                progress.Report(1d, $"Identified {identifiedCount} videos; {warningCount} saved with skipped conflicting tag names or aliases. See server logs for details.");
            logger.LogInformation(
                "Identify completed: {Identified} identified, {Unmatched} unmatched of {Total} videos",
                identifiedCount,
                total - identifiedCount,
                total);
        }, exclusive: false);

        return Ok(new { jobId });
    }

    private static List<string>? ResolveIdentifyMetadataServerEndpoints(List<string>? sources, IReadOnlyList<MetadataServerInstance> metadataServers)
    {
        if (sources == null || sources.Count == 0)
            return null;

        var endpoints = new List<string>();
        foreach (var source in sources.Select(source => source.Trim()).Where(source => source.Length > 0))
        {
            var server = metadataServers.FirstOrDefault(candidate =>
                string.Equals(candidate.Endpoint, source, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.Name, source, StringComparison.OrdinalIgnoreCase));

            if (server == null)
                continue;

            if (!endpoints.Contains(server.Endpoint, StringComparer.OrdinalIgnoreCase))
                endpoints.Add(server.Endpoint);
        }

        return endpoints;
    }

    // Resolves which URL-capable video scrapers are enabled as identify sources. Returns null when no
    // explicit sources were given (all scrapers eligible), or the set of scraper ids named in the
    // sources list (empty when the caller selected only metadata servers, so the scraper stage skips).
    private static HashSet<string>? ResolveIdentifyScraperIds(List<string>? sources, ScraperService? scraperSvc)
    {
        if (scraperSvc == null)
            return [];

        var videoScrapers = scraperSvc.GetScrapers()
            .Where(scraper => string.Equals(scraper.EntityType, EntityKinds.Video, StringComparison.OrdinalIgnoreCase)
                && scraper.SupportedScrapes.Any(kind => string.Equals(kind, "URL", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (sources == null || sources.Count == 0)
            return null;

        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources.Select(source => source.Trim()).Where(source => source.Length > 0))
        {
            var match = videoScrapers.FirstOrDefault(scraper =>
                string.Equals(scraper.Id, source, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scraper.Name, source, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                enabled.Add(match.Id);
        }

        return enabled;
    }

    // Tries each URL-matching, enabled scraper for the video's URLs in priority order, applying the
    // first that returns data (honoring the identify options), and returns whether one was applied.
    private async Task<bool> TryScraperIdentifyVideoAsync(
        Video video,
        HashSet<string>? enabledScraperIds,
        IdentifyOptionsDto? opts,
        IdentifyDefaultsConfig identifyDefaults,
        ScraperService scraperSvc,
        ScrapeAttemptService scrapeAttemptSvc,
        CancellationToken ct)
    {
        var urls = video.Urls
            .Select(item => item.Url)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .ToList();

        foreach (var url in urls)
        {
            foreach (var candidate in scraperSvc.FindScrapersForUrl(url, EntityKinds.Video))
            {
                if (enabledScraperIds != null && !enabledScraperIds.Contains(candidate.Id))
                    continue;

                var attempt = await scrapeAttemptSvc.CreateAttemptAsync(
                    new CreateScrapeAttemptDto(candidate.Id, EntityKinds.Video, video.Id, "url", url, null, null),
                    ct);

                if (attempt.Status != ScrapeAttemptStatuses.Success)
                    continue;

                var applied = await scrapeAttemptSvc.ApplyAttemptAsync(attempt.Id, BuildScraperIdentifyApplyDto(opts, identifyDefaults), ct);
                if (applied == null)
                    continue;

                logger.LogTrace(
                    "Identify video {VideoId}: applied scraper {ScraperId} from URL {Url}",
                    video.Id, candidate.Id, url);
                return true;
            }
        }

        return false;
    }

    // Translates the identify options into a scraper apply plan: per-field overwrite/merge/ignore plus
    // the create-missing / mark-organized toggles, mirroring the metadata-server import config.
    private static ApplyVideoScrapeAttemptDto BuildScraperIdentifyApplyDto(IdentifyOptionsDto? opts, IdentifyDefaultsConfig identifyDefaults)
    {
        var strategies = opts?.FieldStrategies;
        string Strategy(string key) => strategies != null && strategies.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim().ToLowerInvariant()
            : "merge";

        static string ModeFor(string strategy) => strategy switch
        {
            "ignore" => "skip",
            "overwrite" => "replace",
            _ => "merge",
        };

        var replaceFields = new List<string>();
        foreach (var field in new[] { "title", "code", "details", "director", "date" })
        {
            if (Strategy(field) == "overwrite")
                replaceFields.Add(field);
        }
        if (opts?.SetCoverImage ?? true)
            replaceFields.Add("image");

        var collectionModes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["urls"] = ModeFor(Strategy("urls")),
            ["tags"] = (opts?.SetTags ?? true) ? ModeFor(Strategy("tags")) : "skip",
            ["performers"] = (opts?.SetPerformers ?? true) ? ModeFor(Strategy("performers")) : "skip",
            ["studio"] = (opts?.SetStudio ?? true) ? ModeFor(Strategy("studio")) : "skip",
        };

        return new ApplyVideoScrapeAttemptDto(
            ReplaceFields: replaceFields,
            CollectionModes: collectionModes,
            CreateMissingTags: opts?.CreateTags ?? identifyDefaults.CreateTags,
            CreateMissingPerformers: opts?.CreatePerformers ?? identifyDefaults.CreatePerformers,
            CreateMissingStudio: opts?.CreateStudios ?? identifyDefaults.CreateStudios,
            MarkOrganized: opts?.MarkOrganized ?? false);
    }

    private static bool MeetsIdentifyAutoApplyThresholds(int matchCount, double? durationDifferenceSeconds, int? phashDistance, IdentifyDefaultsConfig identifyDefaults)
        => MeetsIdentifyAutoApplyThresholds(matchCount, durationDifferenceSeconds, phashDistance, identifyDefaults, out _);

    // Same threshold logic, but also reports which specific guard rejected the candidate so the
    // identify loop can log it. The boolean result is identical to the parameterless overload.
    private static bool MeetsIdentifyAutoApplyThresholds(int matchCount, double? durationDifferenceSeconds, int? phashDistance, IdentifyDefaultsConfig identifyDefaults, out string? failureReason)
    {
        failureReason = null;

        // Primary signal: require enough matching fingerprint submissions. MatchCount already
        // counts oshash, md5, and phash (incl. close phash) matches, so this works for metadata
        // servers that don't publish phashes.
        if (identifyDefaults.AutoApplyMinFingerprintMatches is int minFingerprintMatches)
        {
            if (matchCount < minFingerprintMatches)
            {
                failureReason = $"matchCount {matchCount} < AutoApplyMinFingerprintMatches {minFingerprintMatches}";
                return false;
            }
        }

        // Secondary guard: only reject when both durations are known and disagree by more than the
        // tolerance. A missing duration must never block a match that cleared the fingerprint bar.
        if (identifyDefaults.AutoApplyMaxDurationDifferenceSeconds is int maxDurationDifferenceSeconds)
        {
            if (durationDifferenceSeconds.HasValue && durationDifferenceSeconds.Value > maxDurationDifferenceSeconds)
            {
                failureReason = $"durationDiff {durationDifferenceSeconds.Value:0.##}s > AutoApplyMaxDurationDifferenceSeconds {maxDurationDifferenceSeconds}";
                return false;
            }
        }

        // Optional phash tightness guard: only applies when a phash distance is actually computable.
        if (identifyDefaults.AutoApplyMaxPhashDistance is int maxPhashDistance)
        {
            if (phashDistance.HasValue && phashDistance.Value > maxPhashDistance)
            {
                failureReason = $"phashDistance {phashDistance.Value} > AutoApplyMaxPhashDistance {maxPhashDistance}";
                return false;
            }
        }

        return true;
    }

    private static double? GetDurationDifferenceSeconds(Video video, MetadataServerVideoMatchDto match)
    {
        var localDuration = video.Files.Select(file => (double?)file.Duration).Max();
        return localDuration.HasValue && match.Duration.HasValue
            ? Math.Abs(localDuration.Value - match.Duration.Value)
            : null;
    }

    private static int? GetBestPhashDistance(Video video, MetadataServerVideoMatchDto match)
    {
        var localPhashes = video.Files
            .SelectMany(file => file.Fingerprints)
            .Where(fingerprint => string.Equals(fingerprint.Type, "phash", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(fingerprint.Value))
            .Select(fingerprint => fingerprint.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var remotePhashes = match.Fingerprints
            .Where(fingerprint => string.Equals(fingerprint.Algorithm, "PHASH", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(fingerprint.Hash))
            .Select(fingerprint => fingerprint.Hash)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (localPhashes.Count == 0 || remotePhashes.Count == 0)
            return null;

        int? bestDistance = null;
        foreach (var localPhash in localPhashes)
        {
            foreach (var remotePhash in remotePhashes)
            {
                var distance = MetadataServerService.ComputePhashHammingDistance(localPhash, remotePhash);
                bestDistance = bestDistance.HasValue ? Math.Min(bestDistance.Value, distance) : distance;
            }
        }

        return bestDistance;
    }

    [HttpPost("sync-fingerprints")]
    [RequiresPermission(Permissions.LibraryScan)]
    public ActionResult<object> SyncFingerprints([FromBody] SyncFingerprintsOptionsDto? opts)
    {
        var sourceUrl = opts?.SourceUrl ?? "http://localhost:3000/graphql";
        var apiKey = opts?.ApiKey;

        var jobId = jobService.Enqueue("sync-fingerprints", "Syncing fingerprints from source instance", async (progress, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var dbCtx = scope.ServiceProvider.GetRequiredService<CoveContext>();
            // Use the pooled factory rather than `new HttpClient()` to avoid socket exhaustion.
            using var httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);
            if (!string.IsNullOrEmpty(apiKey))
                httpClient.DefaultRequestHeaders.Add("ApiKey", apiKey);

            // Step 1: Fetch all fingerprints from the source instance, paging through results
            progress.Report(0, "Fetching fingerprints from source instance...");
            var oshashToPhash = new Dictionary<string, string>();
            var page = 1;
            var perPage = 100;
            var totalVideos = 0;
            var fetched = 0;

            do
            {
                ct.ThrowIfCancellationRequested();

                var graphqlQuery = new
                {
                    query = @"query FindVideos($filter: FindFilterType!) {
                        findVideos(filter: $filter) {
                            count
                            videos {
                                files {
                                    fingerprints {
                                        type
                                        value
                                    }
                                }
                            }
                        }
                    }",
                    variables = new
                    {
                        filter = new { page, per_page = perPage, sort = "id", direction = "ASC" }
                    }
                };

                var jsonPayload = JsonSerializer.Serialize(graphqlQuery);
                var response = await httpClient.PostAsync(
                    sourceUrl,
                    new StringContent(jsonPayload, Encoding.UTF8, "application/json"),
                    ct);

                response.EnsureSuccessStatusCode();
                var responseJson = await response.Content.ReadAsStringAsync(ct);

                using var doc = JsonDocument.Parse(responseJson);
                var data = doc.RootElement.GetProperty("data").GetProperty("findVideos");
                totalVideos = data.GetProperty("count").GetInt32();

                foreach (var video in data.GetProperty("videos").EnumerateArray())
                {
                    foreach (var file in video.GetProperty("files").EnumerateArray())
                    {
                        string? oshash = null;
                        string? phash = null;

                        foreach (var fp in file.GetProperty("fingerprints").EnumerateArray())
                        {
                            var type = fp.GetProperty("type").GetString();
                            var value = fp.GetProperty("value").GetString();
                            if (type == "oshash") oshash = value;
                            else if (type == "phash") phash = value;
                        }

                        if (oshash != null && phash != null)
                            oshashToPhash.TryAdd(oshash, phash);
                    }
                }

                fetched += perPage;
                page++;
                progress.Report(Math.Min(0.5, (double)fetched / Math.Max(totalVideos, 1)),
                    $"Fetched {Math.Min(fetched, totalVideos)}/{totalVideos} videos from source...");
            }
            while (fetched < totalVideos);

            logger.LogInformation("Fetched {Count} oshashâ†’phash mappings from source instance", oshashToPhash.Count);

            // Step 2: Load all files with fingerprints from our DB
            progress.Report(0.5, "Loading local video fingerprints...");
            var localFiles = await dbCtx.Set<BaseFileEntity>()
                .Include(f => f.Fingerprints)
                .ToListAsync(ct);

            var updated = 0;
            var created = 0;
            var total = localFiles.Count;

            for (var i = 0; i < localFiles.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = localFiles[i];
                var localOshash = file.Fingerprints.FirstOrDefault(f => f.Type == "oshash")?.Value;
                if (localOshash == null) continue;

                // Normalize oshash to padded format for lookup (Go uses %016x, local may be unpadded)
                var normalizedLocal = localOshash.PadLeft(16, '0');
                if (!oshashToPhash.TryGetValue(normalizedLocal, out var sourcePhash))
                {
                    // Also try with the raw value for backward compatibility
                    if (!oshashToPhash.TryGetValue(localOshash, out sourcePhash))
                        continue;
                }

                // Also fix the local oshash to padded format if it's not already
                if (localOshash.Length < 16)
                {
                    var oshashFp = file.Fingerprints.First(f => f.Type == "oshash");
                    oshashFp.Value = normalizedLocal;
                }

                var existingPhash = file.Fingerprints.FirstOrDefault(f => f.Type == "phash");
                if (existingPhash != null)
                {
                    if (existingPhash.Value != sourcePhash)
                    {
                        existingPhash.Value = sourcePhash;
                        updated++;
                    }
                }
                else
                {
                    file.Fingerprints.Add(new FileFingerprint { FileId = file.Id, Type = "phash", Value = sourcePhash });
                    created++;
                }

                if ((i + 1) % 100 == 0)
                    progress.Report(0.5 + 0.5 * ((double)(i + 1) / total),
                        $"Processing files ({i + 1}/{total})...");
            }

            await dbCtx.SaveChangesAsync(ct);
            logger.LogInformation("Fingerprint sync completed. {Updated} updated, {Created} created from {Total} source mappings",
                updated, created, oshashToPhash.Count);
        }, exclusive: false);

        return Ok(new { jobId });
    }
}
