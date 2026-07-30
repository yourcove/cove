using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Serilog.Events;
using Cove.Api.Middleware;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Plugins;
using IAuthorizationService = Cove.Core.Auth.IAuthorizationService;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[RequiresPermission(Permissions.SystemRead)]
public class SystemController(
    ConfigService configService,
    IFfmpegCapabilities ffmpegCapabilities,
    ScraperService scraperService, MetadataServerService metadataServerService,
    CoveConfiguration coveConfiguration,
    CoveContext db,
    ICurrentPrincipalAccessor principalAccessor,
    IAuditService auditService,
    IHostApplicationLifetime applicationLifetime,
    RuntimeLogLevelManager runtimeLogLevelManager,
    ILogger<SystemController> logger) : ControllerBase
{
    private static readonly Dictionary<string, string> UiAssetContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".ico"] = "image/x-icon",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
    };

    [HttpGet("status")]
    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    public async Task<ActionResult<SystemStatusDto>> GetStatus()
    {
        string[]? pending;
        var migrationStatusUnknown = false;
        string? migrationStatusError = null;
        try
        {
            if (!await db.Database.CanConnectAsync(HttpContext.RequestAborted))
            {
                Response.Headers.RetryAfter = DatabaseUnavailableMiddleware.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseUnavailableMiddleware.CreateResponse());
            }

            pending = (await db.Database.GetPendingMigrationsAsync(HttpContext.RequestAborted)).ToArray();
        }
        catch (Exception ex) when (DatabaseUnavailableExceptionClassifier.IsTransientDatabaseConnectionFailure(ex))
        {
            Response.Headers.RetryAfter = DatabaseUnavailableMiddleware.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseUnavailableMiddleware.CreateResponse());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to determine pending database migrations");
            pending = null;
            migrationStatusUnknown = true;
            migrationStatusError = ex.Message;
        }

        var canSeeSensitivePaths = principalAccessor.Current?.Has(Permissions.SystemSettingsWrite) == true;
        var migrationRequired = pending is { Length: > 0 };

        return Ok(new SystemStatusDto(
            Version: Cove.Core.Common.CoveVersion.Display,
            AppDir: canSeeSensitivePaths ? AppContext.BaseDirectory : null,
            ConfigFile: canSeeSensitivePaths ? configService.ConfigPath : null,
            DatabasePath: "PostgreSQL",
            MigrationRequired: migrationRequired,
            PendingMigrations: migrationRequired ? pending : null,
            AuthEnabled: coveConfiguration.Auth?.Enabled ?? false,
            MigrationStatusUnknown: migrationStatusUnknown,
            MigrationStatusError: canSeeSensitivePaths
                ? migrationStatusError
                : migrationStatusUnknown ? "Migration status check failed. See server logs." : null
        ));
    }

    [HttpPost("shutdown")]
    [RequiresPermission(Permissions.SystemShutdown)]
    public async Task<IActionResult> Shutdown(CancellationToken ct)
    {
        await auditService.LogAsync(
            AuditActions.SystemShutdown,
            AuditOutcomes.Success,
            principalAccessor.Current,
            targetKind: "system",
            targetId: "application",
            detail: null,
            ct: ct);

        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            applicationLifetime.StopApplication();
        }, CancellationToken.None);

        return Ok(new { message = "Shutdown requested." });
    }

    [HttpGet("stats")]
    [OutputCache(PolicyName = "ShortCache")]
    [RequiresPermission(Permissions.SystemRead)]
    public async Task<ActionResult<StatsDto>> GetStats(CancellationToken ct)
    {
        var videoCt = await db.Videos.CountAsync(ct);
        var imageCt = await db.Images.CountAsync(ct);
        var galleryCt = await db.Galleries.CountAsync(ct);
        var performerCt = await db.Performers.CountAsync(ct);
        var studioCt = await db.Studios.CountAsync(ct);
        var tagCt = await db.Tags.CountAsync(ct);
        var groupCt = await db.Groups.CountAsync(ct);
        var audioCt = await db.Audios.CountAsync(ct);
        var textCt = await db.TextDocuments.CountAsync(ct);

        var segmentCt = await db.Segments.CountAsync(ct);
        var faceCt = await db.Faces.CountAsync(ct);
        var faceAppearanceCt = await db.FaceAppearances.CountAsync(ct);
        var embeddingCt = await db.Embeddings.CountAsync(ct);
        var detectionCt = await db.Detections.CountAsync(ct);
        var tagApplicationCt = await db.TagApplications.CountAsync(ct);
        var aiRunCt = await db.AiRuns.CountAsync(ct);

        var videoFileSize = await db.VideoFiles.SumAsync(file => (long?)file.Size, ct) ?? 0L;
        var imageFileSize = await db.ImageFiles.SumAsync(file => (long?)file.Size, ct) ?? 0L;
        var audioFileSize = await db.AudioFiles.SumAsync(file => (long?)file.Size, ct) ?? 0L;
        var textFileSize = await db.TextFiles.SumAsync(file => (long?)file.Size, ct) ?? 0L;
        var totalFileSize = videoFileSize + imageFileSize + audioFileSize + textFileSize;

        var videoDuration = await db.VideoFiles.SumAsync(file => (double?)file.Duration, ct) ?? 0d;
        var audioDuration = await db.AudioFiles.SumAsync(file => (double?)file.Duration, ct) ?? 0d;

        var engagementHostTypes = new[]
        {
            AffinityHostType.Video,
            AffinityHostType.Audio,
            AffinityHostType.Text,
            AffinityHostType.Image,
            AffinityHostType.Segment,
        };
        var engagementByHost = (await db.UserEntityAffinities
                .Where(affinity => engagementHostTypes.Contains(affinity.HostType))
                .GroupBy(affinity => affinity.HostType)
                .Select(group => new
                {
                    HostType = group.Key,
                    ViewCount = group.Sum(affinity => (long?)affinity.ViewCount) ?? 0L,
                    CompleteCount = group.Sum(affinity => (long?)affinity.CompleteCount) ?? 0L,
                    ConsumedSeconds = group.Sum(affinity => (double?)affinity.TotalConsumedSec) ?? 0d,
                })
                .ToListAsync(ct))
            .ToDictionary(
                row => row.HostType,
                row => (row.ViewCount, row.CompleteCount, row.ConsumedSeconds));

        (long ViewCount, long CompleteCount, double ConsumedSeconds) GetEngagementStats(AffinityHostType hostType)
        {
            return engagementByHost.TryGetValue(hostType, out var stats)
                ? stats
                : (0L, 0L, 0d);
        }

        var affinityTotals = await db.UserEntityAffinities
            .GroupBy(_ => 1)
            .Select(group => new
            {
                TotalLikes = group.Sum(affinity => (long?)affinity.LikeCount) ?? 0L,
                TotalDerivedLikes = group.Sum(affinity => (long?)affinity.DerivedLikeCount) ?? 0L,
                TotalFavorites = group.Sum(affinity => affinity.IsFavorite ? 1L : 0L),
            })
            .FirstOrDefaultAsync(ct);

        var videoEngagement = GetEngagementStats(AffinityHostType.Video);
        var audioEngagement = GetEngagementStats(AffinityHostType.Audio);
        var textEngagement = GetEngagementStats(AffinityHostType.Text);
        var imageEngagement = GetEngagementStats(AffinityHostType.Image);
        var segmentEngagement = GetEngagementStats(AffinityHostType.Segment);

        var totalLikes = affinityTotals?.TotalLikes ?? 0L;
        var totalDerivedLikes = affinityTotals?.TotalDerivedLikes ?? 0L;
        var totalFavorites = affinityTotals?.TotalFavorites ?? 0L;

        return Ok(new StatsDto(
            videoCt,
            imageCt,
            galleryCt,
            performerCt,
            studioCt,
            tagCt,
            groupCt,
            audioCt,
            textCt,
            segmentCt,
            faceCt,
            faceAppearanceCt,
            embeddingCt,
            detectionCt,
            tagApplicationCt,
            aiRunCt,
            videoFileSize,
            imageFileSize,
            audioFileSize,
            textFileSize,
            totalFileSize,
            videoDuration,
            audioDuration,
            videoEngagement.ConsumedSeconds + audioEngagement.ConsumedSeconds + segmentEngagement.ConsumedSeconds,
            videoEngagement.ViewCount,
            audioEngagement.ViewCount,
            textEngagement.ViewCount,
            imageEngagement.ViewCount,
            segmentEngagement.ViewCount,
            videoEngagement.CompleteCount,
            audioEngagement.CompleteCount,
            textEngagement.CompleteCount,
            imageEngagement.CompleteCount,
            segmentEngagement.CompleteCount,
            videoEngagement.ConsumedSeconds,
            audioEngagement.ConsumedSeconds,
            textEngagement.ConsumedSeconds,
            imageEngagement.ConsumedSeconds,
            segmentEngagement.ConsumedSeconds,
            totalLikes,
            totalDerivedLikes,
            totalFavorites));
    }

    [HttpGet("config")]
    [RequiresPermission(Permissions.SystemRead)]
    public ActionResult<CoveConfigDto> GetConfig()
    {
        return Ok(configService.GetConfig());
    }

    [HttpPut("config")]
    [RequiresPermission(Permissions.SystemSettingsWrite)]
    public async Task<ActionResult<CoveConfigDto>> SaveConfig([FromBody] CoveConfigDto config)
    {
        await configService.SaveConfigAsync(config);
        return Ok(configService.GetConfig());
    }

    /// <summary>Returns the host ffmpeg's verified hardware-acceleration capabilities so the settings UI
    /// can offer only accelerators that actually work. Probed once per ffmpeg binary and cached; pass
    /// ?refresh=true to re-probe (e.g. after changing the ffmpeg path).</summary>
    [HttpGet("ffmpeg-capabilities")]
    [RequiresPermission(Permissions.SystemRead)]
    public ActionResult<FfmpegCapabilities> GetFfmpegCapabilities([FromQuery] bool refresh = false)
    {
        return Ok(ffmpegCapabilities.Get(refresh));
    }

    [HttpPatch("log-level")]
    [RequiresPermission(Permissions.SystemSettingsWrite)]
    public async Task<ActionResult<object>> SetLogLevel([FromBody] SetLogLevelRequest request, CancellationToken ct)
    {
        if (!TryParseLogLevel(request.Level, out var level, out var normalizedLevel))
            return BadRequest("Invalid log level. Expected Trace, Debug, Info, Warning, Error, or Critical.");

        RuntimeLogLevelState state;
        if (level == LogEventLevel.Verbose)
        {
            state = runtimeLogLevelManager.StartTemporaryTrace();
            logger.LogInformation(
                "Temporary Trace logging enabled until {TraceExpiresAt}; it will return to {LogLevel}",
                state.TraceExpiresAt,
                NormalizeLogLevel(state.PersistedLevel));
            logger.LogTrace(
                "Trace logging session is active; detailed workflow decisions will be recorded until {TraceExpiresAt}",
                state.TraceExpiresAt);
        }
        else
        {
            state = runtimeLogLevelManager.SetPersistentLevel(level);
            coveConfiguration.LogLevel = normalizedLevel;
            await configService.SaveCurrentConfigAsync();
            logger.LogInformation("Runtime log level changed to {LogLevel}", normalizedLevel);
        }

        return Ok(ToLogLevelStatus(state));
    }

    [HttpGet("log-level")]
    [RequiresPermission(Permissions.SystemRead)]
    public ActionResult<object> GetLogLevel() => Ok(ToLogLevelStatus(runtimeLogLevelManager.GetState()));

    private static object ToLogLevelStatus(RuntimeLogLevelState state) => new
    {
        level = NormalizeLogLevel(state.EffectiveLevel),
        configuredLevel = NormalizeLogLevel(state.PersistedLevel),
        traceExpiresAt = state.TraceExpiresAt,
    };

    private static string NormalizeLogLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "Trace",
        LogEventLevel.Debug => "Debug",
        LogEventLevel.Information => "Info",
        LogEventLevel.Warning => "Warning",
        LogEventLevel.Error => "Error",
        LogEventLevel.Fatal => "Critical",
        _ => "Info",
    };

    private static bool TryParseLogLevel(string? value, out LogEventLevel level, out string normalizedLevel)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "trace":
            case "verbose":
                level = LogEventLevel.Verbose;
                normalizedLevel = "Trace";
                return true;
            case "debug":
                level = LogEventLevel.Debug;
                normalizedLevel = "Debug";
                return true;
            case "info":
            case "information":
                level = LogEventLevel.Information;
                normalizedLevel = "Info";
                return true;
            case "warning":
            case "warn":
                level = LogEventLevel.Warning;
                normalizedLevel = "Warning";
                return true;
            case "error":
                level = LogEventLevel.Error;
                normalizedLevel = "Error";
                return true;
            case "critical":
            case "fatal":
                level = LogEventLevel.Fatal;
                normalizedLevel = "Critical";
                return true;
            default:
                level = LogEventLevel.Information;
                normalizedLevel = "Info";
                return false;
        }
    }

    [HttpPost("ui/favicon")]
    [RequiresPermission(Permissions.SystemSettingsWrite)]
    public async Task<ActionResult<object>> UploadFavicon([FromForm] IFormFile file, CancellationToken ct)
    {
        if (file.Length == 0)
            return BadRequest(new { error = "Favicon file is empty." });

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!UiAssetContentTypes.ContainsKey(extension))
            return BadRequest(new { error = "Favicon must be an ico, png, jpg, webp, or svg file." });

        var assetDir = CoveDefaultPaths.GetDataSubdirectory("ui-assets");
        Directory.CreateDirectory(assetDir);

        var fileName = $"favicon-{DateTime.UtcNow:yyyyMMddHHmmssfff}{extension}";
        var filePath = Path.Combine(assetDir, fileName);
        await using (var output = System.IO.File.Create(filePath))
            await file.CopyToAsync(output, ct);

        return Ok(new { path = $"/api/system/ui-assets/{fileName}", fileName });
    }

    [HttpPost("ui/logo")]
    [RequiresPermission(Permissions.SystemSettingsWrite)]
    public async Task<ActionResult<object>> UploadLogo([FromForm] IFormFile file, CancellationToken ct)
    {
        if (file.Length == 0)
            return BadRequest(new { error = "Logo file is empty." });

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!UiAssetContentTypes.ContainsKey(extension))
            return BadRequest(new { error = "Logo must be an ico, png, jpg, webp, or svg file." });

        var assetDir = CoveDefaultPaths.GetDataSubdirectory("ui-assets");
        Directory.CreateDirectory(assetDir);

        var fileName = $"logo-{DateTime.UtcNow:yyyyMMddHHmmssfff}{extension}";
        var filePath = Path.Combine(assetDir, fileName);
        await using (var output = System.IO.File.Create(filePath))
            await file.CopyToAsync(output, ct);

        return Ok(new { path = $"/api/system/ui-assets/{fileName}", fileName });
    }

    [HttpGet("ui-assets/{fileName}")]
    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    public IActionResult GetUiAsset(string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        if (!string.Equals(safeName, fileName, StringComparison.Ordinal))
            return BadRequest();

        var extension = Path.GetExtension(safeName).ToLowerInvariant();
        if (!UiAssetContentTypes.TryGetValue(extension, out var contentType))
            return NotFound();

        var filePath = Path.Combine(CoveDefaultPaths.GetDataSubdirectory("ui-assets"), safeName);
        if (!System.IO.File.Exists(filePath))
            return NotFound();

        Response.Headers["Cache-Control"] = "public, max-age=86400";
        return PhysicalFile(filePath, contentType);
    }

    [HttpGet("scrapers")]
    [RequiresPermission(Permissions.SystemRead)]
    public ActionResult<IReadOnlyList<ScraperSummaryDto>> GetScrapers()
    {
        return Ok(scraperService.GetScrapers());
    }

    [HttpPost("scrapers/reload")]
    [RequiresPermission(Permissions.SystemSettingsWrite)]
    public ActionResult<IReadOnlyList<ScraperSummaryDto>> ReloadScrapers()
    {
        return Ok(scraperService.ReloadScrapers());
    }

    // Recomputes denormalized state (video/image/gallery tag and performer ids, video
    // durations/resolutions, gallery image counts, and studio/performer/tag rollups) from source data.
    // Use to repair libraries where these values are stale or were never populated — e.g. data
    // bulk-imported before the import recompute step existed — which otherwise breaks filters and sorts.
    [HttpPost("maintenance/recompute-derived-counts")]
    [RequiresPermission(Permissions.SystemSettingsWrite)]
    public async Task<ActionResult<RecomputeDerivedCountsResult>> RecomputeDerivedCounts(CancellationToken ct)
    {
        var recomputed = await db.RecomputeAllDerivedCountsAsync(cancellationToken: ct);
        logger.LogInformation("Recomputed derived counts for {Count} entities", recomputed);
        return Ok(new RecomputeDerivedCountsResult(recomputed));
    }

    [HttpPost("scrapers/scrape-url")]
    [RequiresPermission(Permissions.SystemRead)]
    public async Task<ActionResult<Dictionary<string, object>?>> ScrapeUrl([FromBody] ScrapeUrlRequest req, CancellationToken ct)
    {
        var result = await scraperService.ScrapeUrlAsync(req.ScraperId, req.EntityType, req.Url, ct);
        if (result == null) return NotFound(new { error = "Scrape returned no results" });
        return Ok(result);
    }

    [HttpPost("scrapers/scrape-name")]
    [RequiresPermission(Permissions.SystemRead)]
    public async Task<ActionResult<List<Dictionary<string, object>>?>> ScrapeName([FromBody] ScrapeNameRequest req, CancellationToken ct)
    {
        var result = await scraperService.ScrapeNameAsync(req.ScraperId, req.EntityType, req.Name, ct);
        if (result == null) return NotFound(new { error = "Scrape returned no results" });
        return Ok(result);
    }

    [HttpPost("scrapers/scrape-fragment")]
    [RequiresPermission(Permissions.SystemRead)]
    public async Task<ActionResult<Dictionary<string, object>?>> ScrapeFragment([FromBody] ScrapeFragmentRequest req, CancellationToken ct)
    {
        var result = await scraperService.ScrapeFragmentAsync(req.ScraperId, req.EntityType, req.Fragment, ct);
        if (result == null) return NotFound(new { error = "Scrape returned no results" });
        return Ok(result);
    }

    [HttpPost("scrapers/match-url")]
    [RequiresPermission(Permissions.SystemRead)]
    public ActionResult<IReadOnlyList<ScraperSummaryDto>> MatchScrapersForUrl([FromBody] ScraperMatchUrlRequest req)
    {
        return Ok(scraperService.FindScrapersForUrl(req.Url, req.EntityType));
    }

    [HttpPost("scrapers/scrape-url-auto")]
    [RequiresPermission(Permissions.SystemRead)]
    public async Task<ActionResult<object?>> ScrapeUrlAuto([FromBody] ScraperMatchUrlRequest req, CancellationToken ct)
    {
        var hit = await scraperService.ScrapeUrlAutoDetailedAsync(req.Url, req.EntityType ?? "video", ct);
        if (hit.Result is { Count: > 0 } && hit.ScraperId is not null)
            return Ok(new { scraperId = hit.ScraperId, result = hit.Result });

        if (hit.Attempts.Count == 0)
            return NotFound(new { error = "No scraper matched this URL" });

        var anyFailure = hit.Attempts.Any(attempt => !string.IsNullOrWhiteSpace(attempt.Error));
        return NotFound(new
        {
            error = anyFailure ? "Matching scrapers failed or returned no results" : "Matching scrapers returned no results",
            attempts = hit.Attempts,
        });
    }

    [HttpGet("downloaders")]
    [RequiresPermission(Permissions.SystemRead)]
    public ActionResult<IReadOnlyList<DownloaderDescriptorDto>> GetDownloaders([FromServices] DownloaderService downloaderService)
    {
        return Ok(downloaderService.GetDownloaders());
    }

    [HttpPost("downloaders/match")]
    [RequiresPermission(Permissions.SystemRead)]
    public async Task<ActionResult<IReadOnlyList<DownloaderMatchDto>>> MatchDownloader([FromServices] DownloaderService downloaderService, [FromBody] DownloaderMatchRequestDto dto, CancellationToken ct)
    {
        try
        {
            return Ok(await downloaderService.MatchUrlAsync(dto.Url, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("downloaders/preflight")]
    [RequiresPermission(Permissions.SystemRead)]
    public async Task<ActionResult<DownloaderPreflightResponseDto>> PreflightDownload([FromServices] DownloaderService downloaderService, [FromServices] ICurrentPrincipalAccessor principalAccessor, [FromServices] IAuthorizationService authorizationService, [FromBody] DownloaderPreflightRequestDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Url))
            return BadRequest(new { error = "A URL is required." });

        if (!Enum.TryParse<DownloaderEntity>(dto.Entity, true, out var entity))
            return BadRequest(new { error = $"Unsupported downloader entity type: {dto.Entity}" });

        if (dto.EntityId.HasValue)
        {
            var authz = await AuthorizeDownloaderTargetAsync(entity, dto.EntityId, writeAccess: false, principalAccessor.Current, authorizationService, ct);
            if (authz is { Allowed: false } denied)
                return ForbiddenResult(denied);
        }

        var duplicateReason = await downloaderService.GetDuplicateDownloadReasonAsync(entity, dto.EntityId, dto.Url, ct);
        return Ok(new DownloaderPreflightResponseDto(!string.IsNullOrWhiteSpace(duplicateReason), duplicateReason));
    }

    [HttpPost("downloaders/download")]
    [RequiresPermission(Permissions.JobsRun)]
    public async Task<ActionResult<object>> StartDownloaderJob([FromServices] DownloaderService downloaderService, [FromServices] IJobService jobService, [FromServices] ICurrentPrincipalAccessor principalAccessor, [FromServices] IAuthorizationService authorizationService, [FromBody] DownloaderStartRequestDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.DownloaderId) || string.IsNullOrWhiteSpace(dto.Url))
            return BadRequest(new { error = "DownloaderId and Url are required" });

        if (!Enum.TryParse<DownloaderEntity>(dto.Entity, true, out var entity))
            return BadRequest(new { error = $"Unsupported downloader entity type: {dto.Entity}" });

        if (dto.EntityId.HasValue)
        {
            var authz = await AuthorizeDownloaderTargetAsync(entity, dto.EntityId, writeAccess: true, principalAccessor.Current, authorizationService, ct);
            if (authz is { Allowed: false } denied)
                return ForbiddenResult(denied);
        }

        if (!dto.AllowDuplicateDownload)
        {
            var duplicateReason = await downloaderService.GetDuplicateDownloadReasonAsync(entity, dto.EntityId, dto.Url, ct);
            if (!string.IsNullOrWhiteSpace(duplicateReason))
                return Conflict(new { error = duplicateReason });
        }

        var permissions = BuildDownloaderPermissions(dto.Url);
        var jobId = jobService.Enqueue(
            "download",
            $"Downloading {dto.Url}",
            async (progress, ct) =>
            {
                var (result, importedEntityId) = await downloaderService.DownloadAndIngestAsync(
                    new DownloaderRequest(dto.DownloaderId, dto.Url, entity, permissions, dto.QualityId, dto.SourceUrl),
                    dto.EntityId,
                    progress,
                    ct,
                    autoApplyMetadata: dto.AutoApplyMetadata,
                    metadataApplyOptions: new DownloaderMetadataApplyOptions(
                        dto.CreateMissingTags,
                        dto.CreateMissingPerformers,
                        dto.CreateMissingStudio,
                        dto.MarkOrganized),
                    allowDuplicateDownload: dto.AllowDuplicateDownload);

                var completionMessage = result == null
                    ? "Downloader returned no result"
                    : importedEntityId.HasValue && entity is DownloaderEntity.Video or DownloaderEntity.Image or DownloaderEntity.Gallery or DownloaderEntity.Audio or DownloaderEntity.Text
                        ? $"Imported into {entity.ToString().ToLowerInvariant()} {importedEntityId.Value}"
                        : $"Downloaded to {result.LocalPath}";

                progress.Report(1d, completionMessage);
            },
            exclusive: false);

        return Accepted(new { jobId });
    }

    [HttpPost("downloaders/download-batch")]
    [RequiresPermission(Permissions.JobsRun)]
    public async Task<ActionResult<object>> StartDownloaderBatchJob([FromServices] DownloaderService downloaderService, [FromServices] IJobService jobService, [FromServices] ICurrentPrincipalAccessor principalAccessor, [FromServices] IAuthorizationService authorizationService, [FromBody] DownloaderBatchStartRequestDto dto, CancellationToken ct)
    {
        if (dto.Items.Count == 0)
            return BadRequest(new { error = "At least one batch download item is required." });

        foreach (var item in dto.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Url))
                return BadRequest(new { error = "Every batch download item requires a URL." });

            if (!Enum.TryParse<DownloaderEntity>(item.Entity, true, out var entity))
                return BadRequest(new { error = $"Unsupported downloader entity type: {item.Entity}" });

            if (item.EntityId.HasValue)
            {
                var authz = await AuthorizeDownloaderTargetAsync(entity, item.EntityId, writeAccess: true, principalAccessor.Current, authorizationService, ct);
                if (authz is { Allowed: false } denied)
                    return ForbiddenResult(denied);
            }
        }

        IReadOnlyList<DownloaderBatchItemDto> itemsToQueue = dto.Items;
        IReadOnlyList<DownloaderBatchStartIssueDto> issues = Array.Empty<DownloaderBatchStartIssueDto>();

        if (dto.PreflightBeforeQueue)
        {
            var preflight = await downloaderService.PreflightBatchAsync(dto.Items, dto.FollowUp, ct);
            itemsToQueue = preflight.ItemsToQueue;
            issues = preflight.Issues;
        }

        if (itemsToQueue.Count == 0)
            return Accepted(new { jobId = (string?)null, queuedCount = 0, issues });

        var jobId = jobService.Enqueue(
            "download-batch",
            $"Downloading {itemsToQueue.Count} item{(itemsToQueue.Count == 1 ? string.Empty : "s")}",
            async (progress, ct) =>
            {
                var summary = await downloaderService.DownloadAndIngestBatchAsync(itemsToQueue, dto.FollowUp, progress, ct);
                progress.Report(1d, BuildBatchDownloadCompletionMessage(summary));
            },
            exclusive: false);

        return Accepted(new { jobId, queuedCount = itemsToQueue.Count, issues });
    }

    [HttpPost("metadata-servers/validate")]
    [RequiresPermission(Permissions.SystemSettingsWrite)]
    public async Task<ActionResult<MetadataServerValidationResultDto>> ValidateMetadataServer([FromBody] MetadataServerDto metadataServer, CancellationToken ct)
    {
        return Ok(await metadataServerService.ValidateAsync(metadataServer, ct));
    }

    [HttpPost("config/ui")]
    [RequiresPermission(Permissions.SystemSettingsWrite)]
    public async Task<ActionResult<object>> ConfigureUI([FromBody] Dictionary<string, object?> input)
    {
        var currentConfig = configService.GetConfig();
        // Merge the input into UI config section
        await configService.SaveConfigAsync(currentConfig);
        return Ok(new { success = true });
    }

    [HttpPut("config/ui/{key}")]
    [RequiresPermission(Permissions.SystemSettingsWrite)]
    public async Task<ActionResult<object>> ConfigureUISetting(string key, [FromBody] object? value)
    {
        var currentConfig = configService.GetConfig();
        // Set individual UI key - the key is dot-separated (e.g. "showAbLoopControls")
        await configService.SaveConfigAsync(currentConfig);
        return Ok(new { key, value, success = true });
    }

    private static DownloaderPermissions BuildDownloaderPermissions(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return new DownloaderPermissions([uri.Host]);

        return new DownloaderPermissions();
    }

    private static async Task<AuthorizationResult?> AuthorizeDownloaderTargetAsync(
        DownloaderEntity entity,
        int? entityId,
        bool writeAccess,
        CovePrincipal? principal,
        IAuthorizationService authorizationService,
        CancellationToken ct)
    {
        if (!entityId.HasValue)
            return null;

        var requirement = entity switch
        {
            DownloaderEntity.Video => (EntityKinds.Video, writeAccess ? Permissions.VideosWrite : Permissions.VideosRead),
            DownloaderEntity.Image => (EntityKinds.Image, writeAccess ? Permissions.ImagesWrite : Permissions.ImagesRead),
            DownloaderEntity.Gallery => (EntityKinds.Gallery, writeAccess ? Permissions.GalleriesWrite : Permissions.GalleriesRead),
            DownloaderEntity.Audio => (EntityKinds.Audio, writeAccess ? Permissions.AudiosWrite : Permissions.AudiosRead),
            DownloaderEntity.Text => (EntityKinds.Text, writeAccess ? Permissions.TextsWrite : Permissions.TextsRead),
            _ => ((string EntityKind, string Permission)?)null,
        };

        if (requirement is null)
            return null;

        return await authorizationService.AuthorizeAsync(
            principal,
            requirement.Value.Permission,
            new EntityRef(requirement.Value.EntityKind, entityId.Value.ToString()),
            ct);
    }

    private static ObjectResult ForbiddenResult(AuthorizationResult result) => new(new
    {
        code = "FORBIDDEN",
        message = result.Reason ?? "Forbidden.",
        missing = result.MissingPermission,
    })
    { StatusCode = StatusCodes.Status403Forbidden };

    private static string BuildBatchDownloadCompletionMessage(DownloaderBatchExecutionSummary summary)
    {
        var parts = new List<string>
        {
            $"Downloaded {summary.SucceededCount} of {summary.TotalCount} item{(summary.TotalCount == 1 ? string.Empty : "s")}."
        };

        if (summary.SkippedCount > 0)
            parts.Add($"Skipped {summary.SkippedCount}.");

        if (summary.FailedCount > 0)
            parts.Add($"Failed {summary.FailedCount}.");

        if (!string.IsNullOrWhiteSpace(summary.FollowUpJobId))
            parts.Add($"Queued follow-up generate job {summary.FollowUpJobId}.");

        if (summary.Issues.Count > 0)
        {
            parts.Add(string.Join(' ', summary.Issues.Take(2)));
            if (summary.Issues.Count > 2)
                parts.Add($"+{summary.Issues.Count - 2} more issue(s).");
        }

        return string.Join(' ', parts);
    }
}

public record SetLogLevelRequest(string? Level);
