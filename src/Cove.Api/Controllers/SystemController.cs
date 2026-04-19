using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;
using Cove.Data;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SystemController(
    ISceneRepository sceneRepo, IImageRepository imageRepo,
    IGalleryRepository galleryRepo, IPerformerRepository performerRepo,
    IStudioRepository studioRepo, ITagRepository tagRepo,
    IGroupRepository groupRepo, ConfigService configService,
    ScraperService scraperService, MetadataServerService metadataServerService,
    CoveContext db) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<SystemStatusDto>> GetStatus()
    {
        string[] pending;
        try
        {
            pending = (await db.Database.GetPendingMigrationsAsync()).ToArray();
        }
        catch
        {
            pending = [];
        }

        return Ok(new SystemStatusDto(
            Version: GetType().Assembly.GetName().Version?.ToString() ?? "0.1.0",
            AppDir: AppContext.BaseDirectory,
            ConfigFile: configService.ConfigPath,
            DatabasePath: "PostgreSQL",
            MigrationRequired: pending.Length > 0,
            PendingMigrations: pending.Length > 0 ? pending : null
        ));
    }

    [HttpGet("stats")]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<StatsDto>> GetStats(CancellationToken ct)
    {
        var sceneCt = await sceneRepo.CountAsync(ct);
        var imageCt = await imageRepo.CountAsync(ct);
        var galleryCt = await galleryRepo.CountAsync(ct);
        var performerCt = await performerRepo.CountAsync(ct);
        var studioCt = await studioRepo.CountAsync(ct);
        var tagCt = await tagRepo.CountAsync(ct);
        var groupCt = await groupRepo.CountAsync(ct);

        return Ok(new StatsDto(sceneCt, imageCt, galleryCt, performerCt, studioCt, tagCt, groupCt, 0, 0));
    }

    [HttpGet("config")]
    public ActionResult<CoveConfigDto> GetConfig()
    {
        return Ok(configService.GetConfig());
    }

    [HttpPut("config")]
    public async Task<ActionResult<CoveConfigDto>> SaveConfig([FromBody] CoveConfigDto config)
    {
        await configService.SaveConfigAsync(config);
        return Ok(configService.GetConfig());
    }

    [HttpGet("scrapers")]
    public ActionResult<IReadOnlyList<ScraperSummaryDto>> GetScrapers()
    {
        return Ok(scraperService.GetScrapers());
    }

    [HttpPost("scrapers/reload")]
    public ActionResult<IReadOnlyList<ScraperSummaryDto>> ReloadScrapers()
    {
        return Ok(scraperService.ReloadScrapers());
    }

    [HttpPost("scrapers/scrape-url")]
    public async Task<ActionResult<Dictionary<string, object>?>> ScrapeUrl([FromBody] ScrapeUrlRequest req, CancellationToken ct)
    {
        var result = await scraperService.ScrapeUrlAsync(req.ScraperId, req.EntityType, req.Url, ct);
        if (result == null) return NotFound(new { error = "Scrape returned no results" });
        return Ok(result);
    }

    [HttpPost("scrapers/scrape-name")]
    public async Task<ActionResult<List<Dictionary<string, object>>?>> ScrapeName([FromBody] ScrapeNameRequest req, CancellationToken ct)
    {
        var result = await scraperService.ScrapeNameAsync(req.ScraperId, req.EntityType, req.Name, ct);
        if (result == null) return NotFound(new { error = "Scrape returned no results" });
        return Ok(result);
    }

    [HttpPost("scrapers/scrape-fragment")]
    public async Task<ActionResult<Dictionary<string, object>?>> ScrapeFragment([FromBody] ScrapeFragmentRequest req, CancellationToken ct)
    {
        var result = await scraperService.ScrapeFragmentAsync(req.ScraperId, req.EntityType, req.Fragment, ct);
        if (result == null) return NotFound(new { error = "Scrape returned no results" });
        return Ok(result);
    }

    [HttpPost("metadata-servers/validate")]
    public async Task<ActionResult<MetadataServerValidationResultDto>> ValidateMetadataServer([FromBody] MetadataServerDto metadataServer, CancellationToken ct)
    {
        return Ok(await metadataServerService.ValidateAsync(metadataServer, ct));
    }

    [HttpPost("config/ui")]
    public async Task<ActionResult<object>> ConfigureUI([FromBody] Dictionary<string, object?> input)
    {
        var currentConfig = configService.GetConfig();
        // Merge the input into UI config section
        await configService.SaveConfigAsync(currentConfig);
        return Ok(new { success = true });
    }

    [HttpPut("config/ui/{key}")]
    public async Task<ActionResult<object>> ConfigureUISetting(string key, [FromBody] object? value)
    {
        var currentConfig = configService.GetConfig();
        // Set individual UI key - the key is dot-separated (e.g. "showAbLoopControls")
        await configService.SaveConfigAsync(currentConfig);
        return Ok(new { key, value, success = true });
    }
}
