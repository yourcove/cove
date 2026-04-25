using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/metadata")]
public class MetadataController(
    IScanService scanService,
    IJobService jobService,
    IThumbnailService thumbnailService,
    IFingerprintService fingerprintService,
    IServiceScopeFactory scopeFactory,
    CoveConfiguration config,
    ILogger<MetadataController> logger) : ControllerBase
{
    [HttpPost("scan")]
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
            Rescan = opts?.Rescan == true,
        });
        return Ok(new { jobId });
    }

    [HttpPost("generate")]
    public ActionResult<object> StartGenerate([FromBody] GenerateOptionsDto? opts)
    {
        var jobId = jobService.Enqueue("generate", "Generating content", async (progress, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var dbCtx = scope.ServiceProvider.GetRequiredService<CoveContext>();

            async Task UpsertFingerprintAsync(int fileId, string type, string value, CancellationToken token)
            {
                using var innerScope = scopeFactory.CreateScope();
                var innerDb = innerScope.ServiceProvider.GetRequiredService<CoveContext>();
                var existing = await innerDb.FileFingerprints
                    .FirstOrDefaultAsync(fp => fp.FileId == fileId && fp.Type == type, token);
                if (existing != null)
                    existing.Value = value;
                else
                    innerDb.FileFingerprints.Add(new FileFingerprint { FileId = fileId, Type = type, Value = value });
                await innerDb.SaveChangesAsync(token);
            }

            var selectedSceneIds = opts?.SceneIds;
            var hasSceneSelection = selectedSceneIds is { Count: > 0 };

            var scenes = hasSceneSelection
                ? await dbCtx.Scenes.Include(s => s.Files).ThenInclude(f => f.ParentFolder).Include(s => s.Files).ThenInclude(f => f.Fingerprints).Where(s => selectedSceneIds!.Contains(s.Id)).AsSplitQuery().ToListAsync(ct)
                : await dbCtx.Scenes.Include(s => s.Files).ThenInclude(f => f.ParentFolder).Include(s => s.Files).ThenInclude(f => f.Fingerprints).AsSplitQuery().ToListAsync(ct);

            // Filter by paths if specified
            if (opts?.Paths is { Count: > 0 } paths)
            {
                scenes = scenes.Where(s =>
                {
                    var file = s.Files.FirstOrDefault();
                    if (file == null) return false;
                    var filePath = Path.Combine(file.ParentFolder?.Path ?? "", file.Basename);
                    return paths.Any(p => filePath.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                }).ToList();
            }

            // Build work items (read-only snapshot) so we don't touch DbContext from parallel threads
            var workItems = scenes.Select(s =>
            {
                var file = s.Files.FirstOrDefault();
                return new
                {
                    Scene = s,
                    File = file,
                    Path = file != null ? Path.Combine(file.ParentFolder?.Path ?? "", file.Basename) : "",
                    HasPhash = file?.Fingerprints.Any(f => f.Type == "phash" && !string.IsNullOrWhiteSpace(f.Value)) ?? false,
                    HasMd5 = file?.Fingerprints.Any(f => f.Type == "md5" && !string.IsNullOrWhiteSpace(f.Value)) ?? false,
                };
            }).Where(w => w.File != null).ToList();

            var total = workItems.Count;
            var processed = 0;
            var maxParallel = config.MaxParallelTasks;
            var parallelism = maxParallel <= 0 ? Environment.ProcessorCount : Math.Max(1, maxParallel);

            await Parallel.ForEachAsync(workItems, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, async (item, token) =>
            {
                if (!System.IO.File.Exists(item.Path))
                {
                    Interlocked.Increment(ref processed);
                    return;
                }

                if (opts?.Thumbnails == true)
                {
                    if (opts?.Overwrite == true)
                    {
                        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(item.Scene.Id)));
                        var thumbPath = Path.Combine(config.GeneratedPath, "screenshots", hash[..2], $"{item.Scene.Id}.jpg");
                        if (System.IO.File.Exists(thumbPath)) System.IO.File.Delete(thumbPath);
                    }
                    await thumbnailService.GenerateSceneThumbnailAsync(item.Scene.Id, null, token);
                }

                if (opts?.Previews == true)
                {
                    if (opts?.Overwrite == true)
                    {
                        var previewPath = thumbnailService.GetPreviewPath(item.Scene.Id);
                        if (System.IO.File.Exists(previewPath)) System.IO.File.Delete(previewPath);
                    }
                    await thumbnailService.GenerateScenePreviewAsync(item.Scene.Id, token);
                }

                if (opts?.Sprites == true)
                {
                    if (opts?.Overwrite == true)
                    {
                        var spritePath = thumbnailService.GetSpritePath(item.Scene.Id);
                        var vttPath = thumbnailService.GetSpriteVttPath(item.Scene.Id);
                        if (System.IO.File.Exists(spritePath)) System.IO.File.Delete(spritePath);
                        if (System.IO.File.Exists(vttPath)) System.IO.File.Delete(vttPath);
                    }
                    await thumbnailService.GenerateSceneSpriteAsync(item.Scene.Id, token);
                }

                if (opts?.Phashes == true && (opts?.Overwrite == true || !item.HasPhash))
                {
                    var phash = await fingerprintService.ComputeVideoPhashAsync(item.Path, item.File!.Duration, token);
                    if (!string.IsNullOrWhiteSpace(phash))
                        await UpsertFingerprintAsync(item.File!.Id, "phash", phash, token);
                }

                if (opts?.Md5 == true && (opts?.Overwrite == true || !item.HasMd5))
                {
                    var md5 = await fingerprintService.ComputeMd5Async(item.Path, token);
                    if (!string.IsNullOrWhiteSpace(md5))
                        await UpsertFingerprintAsync(item.File!.Id, "md5", md5, token);
                }

                var current = Interlocked.Increment(ref processed);
                progress.Report((double)current / total, $"Generating ({current}/{total}) {item.Scene.Title ?? "Untitled"}");
            });

            if (!hasSceneSelection && (opts?.ImagePhashes == true || opts?.ImageThumbnails == true || opts?.Md5 == true))
            {
                var imageFiles = await dbCtx.ImageFiles
                    .Include(f => f.ParentFolder)
                    .Include(f => f.Fingerprints)
                    .ToListAsync(ct);

                if (opts?.Paths is { Count: > 0 } imagePaths)
                {
                    imageFiles = imageFiles.Where(imageFile =>
                    {
                        var imagePath = imageFile.ParentFolder != null
                            ? Path.Combine(imageFile.ParentFolder.Path, imageFile.Basename)
                            : imageFile.Basename;
                        return imagePaths.Any(p => imagePath.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                    }).ToList();
                }

                var imageTotal = imageFiles.Count;
                var imageProcessed = 0;

                await Parallel.ForEachAsync(imageFiles, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, async (imageFile, token) =>
                {
                    var imagePath = imageFile.ParentFolder != null
                        ? Path.Combine(imageFile.ParentFolder.Path, imageFile.Basename)
                        : imageFile.Basename;

                    if (System.IO.File.Exists(imagePath))
                    {
                        if (opts?.ImageThumbnails == true && imageFile.ImageId.HasValue)
                            await thumbnailService.GenerateImageThumbnailAsync(imageFile.ImageId.Value, ct: token);

                        var hasPhash = imageFile.Fingerprints.Any(fp => fp.Type == "phash" && !string.IsNullOrWhiteSpace(fp.Value));
                        if (opts?.ImagePhashes == true && (opts?.Overwrite == true || !hasPhash))
                        {
                            var phash = await fingerprintService.ComputeImagePhashAsync(imagePath, token);
                            if (!string.IsNullOrWhiteSpace(phash))
                                await UpsertFingerprintAsync(imageFile.Id, "phash", phash, token);
                        }

                        var hasMd5 = imageFile.Fingerprints.Any(fp => fp.Type == "md5" && !string.IsNullOrWhiteSpace(fp.Value));
                        if (opts?.Md5 == true && (opts?.Overwrite == true || !hasMd5))
                        {
                            var md5 = await fingerprintService.ComputeMd5Async(imagePath, token);
                            if (!string.IsNullOrWhiteSpace(md5))
                                await UpsertFingerprintAsync(imageFile.Id, "md5", md5, token);
                        }
                    }

                    var current = Interlocked.Increment(ref imageProcessed);
                    progress.Report((double)current / imageTotal, $"Generating image content ({current}/{imageTotal})");
                });
            }
        });

        return Ok(new { jobId });
    }

    [HttpPost("auto-tag")]
    public ActionResult<object> StartAutoTag([FromBody] AutoTagOptionsDto? opts)
    {
        var jobId = jobService.Enqueue("auto-tag", "Auto-tagging content", async (progress, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var dbCtx = scope.ServiceProvider.GetRequiredService<CoveContext>();

            // Load all performers, studios, tags for matching
            var performers = await dbCtx.Performers.AsNoTracking().ToListAsync(ct);
            var studios = await dbCtx.Studios.AsNoTracking().ToListAsync(ct);
            var tags = await dbCtx.Tags.Include(t => t.Aliases).AsNoTracking().ToListAsync(ct);

            // Filter to configured subsets if provided
            if (opts?.Performers?.Count > 0)
                performers = performers.Where(p => opts.Performers.Any(n => p.Name.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();
            if (opts?.Studios?.Count > 0)
                studios = studios.Where(s => opts.Studios.Any(n => s.Name.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();
            if (opts?.Tags?.Count > 0)
                tags = tags.Where(t => opts.Tags.Any(n => t.Name.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();

            var scenes = await dbCtx.Scenes
                .Include(s => s.Files).ThenInclude(f => f.ParentFolder)
                .Include(s => s.SceneTags)
                .Include(s => s.ScenePerformers)
                .ToListAsync(ct);

            var total = scenes.Count;
            var tagged = 0;

            for (var i = 0; i < scenes.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report((double)(i + 1) / total, $"Auto-tagging scenes ({i + 1}/{total})");

                var scene = scenes[i];
                var sceneFile = scene.Files.FirstOrDefault();
                if (sceneFile == null) continue;

                var path = Path.Combine(sceneFile.ParentFolder?.Path ?? "", sceneFile.Basename).ToLowerInvariant();
                var title = (scene.Title ?? "").ToLowerInvariant();
                var searchText = $"{path} {title}";

                // Match performers by name in file path/title
                var existingPerformerIds = scene.ScenePerformers.Select(sp => sp.PerformerId).ToHashSet();
                foreach (var performer in performers)
                {
                    if (existingPerformerIds.Contains(performer.Id)) continue;
                    if (searchText.Contains(performer.Name.ToLowerInvariant()))
                    {
                        scene.ScenePerformers.Add(new ScenePerformer { PerformerId = performer.Id, SceneId = scene.Id });
                        tagged++;
                    }
                }

                // Match studios by name in file path
                if (scene.StudioId == null)
                {
                    foreach (var studio in studios)
                    {
                        if (searchText.Contains(studio.Name.ToLowerInvariant()))
                        {
                            scene.StudioId = studio.Id;
                            tagged++;
                            break;
                        }
                    }
                }

                // Match tags by name or alias in file path/title
                var existingTagIds = scene.SceneTags.Select(st => st.TagId).ToHashSet();
                foreach (var tag in tags)
                {
                    if (existingTagIds.Contains(tag.Id)) continue;
                    var names = new List<string> { tag.Name.ToLowerInvariant() };
                    names.AddRange(tag.Aliases.Select(a => a.Alias.ToLowerInvariant()));

                    if (names.Any(n => searchText.Contains(n)))
                    {
                        scene.SceneTags.Add(new SceneTag { TagId = tag.Id, SceneId = scene.Id });
                        tagged++;
                    }
                }
            }

            await dbCtx.SaveChangesAsync(ct);
            logger.LogInformation("Auto-tag completed. {Tagged} associations created", tagged);
        }, exclusive: false);

        return Ok(new { jobId });
    }

    [HttpPost("clean")]
    public ActionResult<object> StartClean([FromBody] CleanOptionsDto? opts)
    {
        var jobId = jobService.Enqueue("clean", "Cleaning library", async (progress, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var dbCtx = scope.ServiceProvider.GetRequiredService<CoveContext>();

            var files = await dbCtx.Set<BaseFileEntity>()
                .Include(f => f.ParentFolder)
                .ToListAsync(ct);

            var total = files.Count;
            var cleaned = 0;

            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report((double)(i + 1) / total, $"Checking files ({i + 1}/{total})");

                var file = files[i];
                var filePath = Path.Combine(file.ParentFolder?.Path ?? "", file.Basename);

                if (opts?.Paths?.Count > 0 && !opts.Paths.Any(p => filePath.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (!System.IO.File.Exists(filePath))
                {
                    if (opts?.DryRun != true)
                    {
                        dbCtx.Set<BaseFileEntity>().Remove(file);
                        cleaned++;
                    }
                    else
                    {
                        logger.LogInformation("[Dry Run] Would remove: {Path}", filePath);
                        cleaned++;
                    }
                }
            }

            if (opts?.DryRun != true)
                await dbCtx.SaveChangesAsync(ct);

            logger.LogInformation("Clean completed. {Cleaned} missing files {Action}", cleaned, opts?.DryRun == true ? "found" : "removed");
        }, exclusive: false);

        return Ok(new { jobId });
    }

    [HttpPost("export")]
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

            if (opts?.IncludeScenes != false)
            {
                progress.Report(0.1, "Exporting scenes...");
                exportData["scenes"] = await dbCtx.Scenes
                    .Include(s => s.SceneTags).ThenInclude(st => st.Tag)
                    .Include(s => s.ScenePerformers).ThenInclude(sp => sp.Performer)
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
            var jsonOpts = new JsonSerializerOptions { WriteIndented = true, ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles };
            await System.IO.File.WriteAllTextAsync(exportFile, JsonSerializer.Serialize(exportData, jsonOpts), ct);

            logger.LogInformation("Export completed: {Path}", exportFile);
        }, exclusive: false);

        return Ok(new { jobId });
    }

    [HttpPost("import")]
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
            var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var importData = JsonSerializer.Deserialize<JsonElement>(json, jsonOpts);

            // Import tags first (no dependencies)
            if (importData.TryGetProperty("tags", out var tagsEl))
            {
                progress.Report(0.1, "Importing tags...");
                var importTags = JsonSerializer.Deserialize<List<Tag>>(tagsEl.GetRawText(), jsonOpts) ?? [];
                foreach (var tag in importTags)
                {
                    ct.ThrowIfCancellationRequested();
                    var existing = await dbCtx.Tags.FirstOrDefaultAsync(t => t.Name == tag.Name, ct);
                    if (existing != null)
                    {
                        if (overwrite) { existing.Description = tag.Description; existing.Favorite = tag.Favorite; existing.IgnoreAutoTag = tag.IgnoreAutoTag; }
                    }
                    else
                    {
                        dbCtx.Tags.Add(new Tag { Name = tag.Name, Description = tag.Description, Favorite = tag.Favorite, IgnoreAutoTag = tag.IgnoreAutoTag });
                    }
                }
                await dbCtx.SaveChangesAsync(ct);
            }

            // Import studios (may reference parent studios)
            if (importData.TryGetProperty("studios", out var studiosEl))
            {
                progress.Report(0.3, "Importing studios...");
                var importStudios = JsonSerializer.Deserialize<List<Studio>>(studiosEl.GetRawText(), jsonOpts) ?? [];
                foreach (var studio in importStudios)
                {
                    ct.ThrowIfCancellationRequested();
                    var existing = await dbCtx.Studios.FirstOrDefaultAsync(s => s.Name == studio.Name, ct);
                    if (existing != null)
                    {
                        if (overwrite) { existing.Details = studio.Details; }
                    }
                    else
                    {
                        dbCtx.Studios.Add(new Studio { Name = studio.Name, Details = studio.Details });
                    }
                }
                await dbCtx.SaveChangesAsync(ct);
            }

            // Import performers
            if (importData.TryGetProperty("performers", out var performersEl))
            {
                progress.Report(0.5, "Importing performers...");
                var importPerformers = JsonSerializer.Deserialize<List<Performer>>(performersEl.GetRawText(), jsonOpts) ?? [];
                foreach (var performer in importPerformers)
                {
                    ct.ThrowIfCancellationRequested();
                    var existing = await dbCtx.Performers.FirstOrDefaultAsync(p => p.Name == performer.Name && p.Disambiguation == performer.Disambiguation, ct);
                    if (existing != null)
                    {
                        if (overwrite)
                        {
                            existing.Gender = performer.Gender;
                            existing.Birthdate = performer.Birthdate;
                            existing.Ethnicity = performer.Ethnicity;
                            existing.Country = performer.Country;
                            existing.Details = performer.Details;
                        }
                    }
                    else
                    {
                        dbCtx.Performers.Add(new Performer
                        {
                            Name = performer.Name, Disambiguation = performer.Disambiguation,
                            Gender = performer.Gender, Birthdate = performer.Birthdate,
                            Ethnicity = performer.Ethnicity, Country = performer.Country,
                            Details = performer.Details, Favorite = performer.Favorite
                        });
                    }
                }
                await dbCtx.SaveChangesAsync(ct);
            }

            // Import groups
            if (importData.TryGetProperty("groups", out var groupsEl))
            {
                progress.Report(0.7, "Importing groups...");
                var importGroups = JsonSerializer.Deserialize<List<Group>>(groupsEl.GetRawText(), jsonOpts) ?? [];
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

            progress.Report(1.0, "Import completed");
            logger.LogInformation("Metadata import completed from: {Path}", filePath);
        }, exclusive: false);

        return Ok(new { jobId });
    }

    [HttpPost("clean-generated")]
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

            var dirs = new[] { "screenshots", "thumbnails", "previews", "sprites", "transcodes", "vtt" };
            var totalCleared = 0L;

            for (var i = 0; i < dirs.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report((double)(i + 1) / dirs.Length, $"Cleaning {dirs[i]}...");

                var dir = Path.Combine(generatedPath, dirs[i]);
                if (!Directory.Exists(dir)) continue;

                foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var fi = new FileInfo(file);
                    totalCleared += fi.Length;
                    fi.Delete();
                }
            }

            logger.LogInformation("Cleaned generated files. Freed {Size} bytes", totalCleared);
        }, exclusive: false);

        return Ok(new { jobId });
    }

    [HttpPost("identify")]
    public ActionResult<object> StartIdentify([FromBody] IdentifyOptionsDto? opts)
    {
        var jobId = jobService.Enqueue("identify", "Identifying scenes", async (progress, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var dbCtx = scope.ServiceProvider.GetRequiredService<CoveContext>();
            var metadataServerSvc = scope.ServiceProvider.GetService<MetadataServerService>();

            var scenes = opts?.SceneIds?.Count > 0
                ? await dbCtx.Scenes
                    .Include(s => s.Files).ThenInclude(f => f.Fingerprints)
                    .Include(s => s.SceneTags)
                    .Include(s => s.ScenePerformers)
                    .Include(s => s.RemoteIds)
                    .Include(s => s.Urls)
                    .Where(s => opts.SceneIds.Contains(s.Id)).AsSplitQuery().ToListAsync(ct)
                : await dbCtx.Scenes
                    .Include(s => s.Files).ThenInclude(f => f.Fingerprints)
                    .Include(s => s.SceneTags)
                    .Include(s => s.ScenePerformers)
                    .Include(s => s.RemoteIds)
                    .Include(s => s.Urls)
                    .AsSplitQuery().ToListAsync(ct);

            var identifyDefaults = config.Scraping.IdentifyDefaults;

            // Build import config from identify options
            var importConfig = new MetadataServerSceneImportRequestDto
            {
                SetCoverImage = opts?.SetCoverImage ?? true,
                SetTags = opts?.SetTags ?? true,
                SetPerformers = opts?.SetPerformers ?? true,
                SetStudio = opts?.SetStudio ?? true,
                OnlyExistingTags = !(opts?.CreateTags ?? identifyDefaults.CreateTags),
                OnlyExistingPerformers = !(opts?.CreatePerformers ?? identifyDefaults.CreatePerformers),
                OnlyExistingStudio = !(opts?.CreateStudios ?? identifyDefaults.CreateStudios),
                MarkOrganized = opts?.MarkOrganized ?? false,
            };

            var total = scenes.Count;
            for (var i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report((double)(i + 1) / total, $"Identifying scene {i + 1}/{total}");

                var scene = scenes[i];
                var fingerprints = scene.Files.SelectMany(f => f.Fingerprints).ToList();
                if (fingerprints.Count == 0) continue;

                // Attempt MetadataServer identification
                if (metadataServerSvc != null)
                {
                    try
                    {
                        var matches = await metadataServerSvc.SearchScenesAsync(scene, null, null, ct);
                        if (matches.Count > 0)
                        {
                            var rankedMatches = matches
                                .Select(match => new
                                {
                                    Match = match,
                                    DurationDifferenceSeconds = GetDurationDifferenceSeconds(scene, match),
                                    PhashDistance = GetBestPhashDistance(scene, match),
                                })
                                .Where(candidate => MeetsIdentifyAutoApplyThresholds(candidate.DurationDifferenceSeconds, candidate.PhashDistance, identifyDefaults))
                                .OrderByDescending(candidate => candidate.Match.MatchCount)
                                .ThenBy(candidate => candidate.PhashDistance ?? int.MaxValue)
                                .ThenBy(candidate => candidate.DurationDifferenceSeconds ?? double.MaxValue)
                                .Select(candidate => candidate.Match)
                                .ToList();

                            if (rankedMatches.Count == 0)
                                continue;

                            // Skip multiple matches if configured
                            if ((opts?.SkipMultipleMatches ?? true) && rankedMatches.Count > 1)
                                continue;

                            var best = rankedMatches[0];
                            await metadataServerSvc.MergeSceneAsync(scene, best.Endpoint, best.Id, importConfig, ct);
                            await dbCtx.SaveChangesAsync(ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "MetadataServer identify failed for scene {SceneId}", scene.Id);
                    }
                }
            }

            await dbCtx.SaveChangesAsync(ct);
            logger.LogInformation("Identify completed for {Count} scenes", total);
        }, exclusive: false);

        return Ok(new { jobId });
    }

    private static bool MeetsIdentifyAutoApplyThresholds(double? durationDifferenceSeconds, int? phashDistance, IdentifyDefaultsConfig identifyDefaults)
    {
        if (identifyDefaults.AutoApplyMaxDurationDifferenceSeconds is int maxDurationDifferenceSeconds)
        {
            if (!durationDifferenceSeconds.HasValue || durationDifferenceSeconds.Value > maxDurationDifferenceSeconds)
                return false;
        }

        if (identifyDefaults.AutoApplyMaxPhashDistance is int maxPhashDistance)
        {
            if (!phashDistance.HasValue || phashDistance.Value > maxPhashDistance)
                return false;
        }

        return true;
    }

    private static double? GetDurationDifferenceSeconds(Scene scene, MetadataServerSceneMatchDto match)
    {
        var localDuration = scene.Files.Select(file => (double?)file.Duration).Max();
        return localDuration.HasValue && match.Duration.HasValue
            ? Math.Abs(localDuration.Value - match.Duration.Value)
            : null;
    }

    private static int? GetBestPhashDistance(Scene scene, MetadataServerSceneMatchDto match)
    {
        var localPhashes = scene.Files
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
    public ActionResult<object> SyncFingerprints([FromBody] SyncFingerprintsOptionsDto? opts)
    {
        var sourceUrl = opts?.SourceUrl ?? "http://localhost:3000/graphql";
        var apiKey = opts?.ApiKey;

        var jobId = jobService.Enqueue("sync-fingerprints", "Syncing fingerprints from source instance", async (progress, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var dbCtx = scope.ServiceProvider.GetRequiredService<CoveContext>();
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);
            if (!string.IsNullOrEmpty(apiKey))
                httpClient.DefaultRequestHeaders.Add("ApiKey", apiKey);

            // Step 1: Fetch all fingerprints from the source instance, paging through results
            progress.Report(0, "Fetching fingerprints from source instance...");
            var oshashToPhash = new Dictionary<string, string>();
            var page = 1;
            var perPage = 100;
            var totalScenes = 0;
            var fetched = 0;

            do
            {
                ct.ThrowIfCancellationRequested();

                var graphqlQuery = new
                {
                    query = @"query FindScenes($filter: FindFilterType!) {
                        findScenes(filter: $filter) {
                            count
                            scenes {
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
                var data = doc.RootElement.GetProperty("data").GetProperty("findScenes");
                totalScenes = data.GetProperty("count").GetInt32();

                foreach (var scene in data.GetProperty("scenes").EnumerateArray())
                {
                    foreach (var file in scene.GetProperty("files").EnumerateArray())
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
                progress.Report(Math.Min(0.5, (double)fetched / Math.Max(totalScenes, 1)),
                    $"Fetched {Math.Min(fetched, totalScenes)}/{totalScenes} scenes from source...");
            }
            while (fetched < totalScenes);

            logger.LogInformation("Fetched {Count} oshashâ†’phash mappings from source instance", oshashToPhash.Count);

            // Step 2: Load all files with fingerprints from our DB
            progress.Report(0.5, "Loading local scene fingerprints...");
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
