using Microsoft.AspNetCore.Mvc;
using Cove.Api.Services;
using Cove.Core.Auth;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/stash-migration")]
[RequiresPermission(Permissions.ImportStash)]
public class StashMigrationController(StashMigrationService migrationService) : ControllerBase
{
    public record PreviewRequest(string StashDbPath);
    public record PathMappingRequest(string Source, string Target);
    public record ImportRequest(string StashDbPath, string? GeneratedPath, bool MigrateGeneratedContent = true, IReadOnlyList<PathMappingRequest>? PathMappings = null);

    [HttpPost("preview")]
    public async Task<ActionResult<StashPreviewResult>> Preview([FromBody] PreviewRequest req, CancellationToken ct)
    {
        var result = await migrationService.PreviewAsync(req.StashDbPath, ct);
        if (!result.IsValid)
            return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("import")]
    [RequiresUnscopedEntityAccess("read")]
    [RequiresUnscopedEntityAccess("write")]
    public async Task<ActionResult<object>> Import([FromBody] ImportRequest req, CancellationToken ct)
    {
        try
        {
            var pathMappings = req.PathMappings?
                .Select(mapping => new StashPathMapping(mapping.Source, mapping.Target))
                .ToArray();
            var jobId = await migrationService.StartImportAsync(
                req.StashDbPath,
                new StashImportOptions(req.GeneratedPath, req.MigrateGeneratedContent, pathMappings),
                ct);
            return Accepted(new { jobId });
        }
        catch (StashMigrationOwnerRequiredException ex)
        {
            return Conflict(new { code = "OWNER_REQUIRED", error = ex.Message });
        }
        catch (StashMigrationInProgressException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("import/{jobId}")]
    public ActionResult<StashImportResult> GetImportResult(string jobId)
    {
        var result = migrationService.GetImportResult(jobId);
        return result != null ? Ok(result) : NotFound();
    }
}
