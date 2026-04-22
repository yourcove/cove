using Microsoft.AspNetCore.Mvc;
using Cove.Api.Services;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/stash-migration")]
public class StashMigrationController(StashMigrationService migrationService) : ControllerBase
{
    public record PreviewRequest(string StashDbPath);
    public record ImportRequest(string StashDbPath, string? GeneratedPath, bool MigrateGeneratedContent = true);

    [HttpPost("preview")]
    public async Task<ActionResult<StashPreviewResult>> Preview([FromBody] PreviewRequest req, CancellationToken ct)
    {
        var result = await migrationService.PreviewAsync(req.StashDbPath, ct);
        if (!result.IsValid)
            return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("import")]
    public ActionResult<object> Import([FromBody] ImportRequest req)
    {
        try
        {
            var jobId = migrationService.StartImport(req.StashDbPath, new StashImportOptions(req.GeneratedPath, req.MigrateGeneratedContent));
            return Accepted(new { jobId });
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
