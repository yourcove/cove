using Microsoft.AspNetCore.Mvc;
using Cove.Api.Services;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/stash-migration")]
public class StashMigrationController(StashMigrationService migrationService) : ControllerBase
{
    public record PreviewRequest(string StashDbPath);
    public record ImportRequest(string StashDbPath);

    [HttpPost("preview")]
    public async Task<ActionResult<StashPreviewResult>> Preview([FromBody] PreviewRequest req, CancellationToken ct)
    {
        var result = await migrationService.PreviewAsync(req.StashDbPath, ct);
        if (!result.IsValid)
            return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("import")]
    public async Task<ActionResult<StashImportResult>> Import([FromBody] ImportRequest req, CancellationToken ct)
    {
        try
        {
            // Use a non-request CT so client disconnect doesn't abort a long-running migration
            var result = await migrationService.ImportAsync(req.StashDbPath, CancellationToken.None);
            return Ok(result);
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
}
