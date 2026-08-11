using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Data.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/tag-name-conflicts")]
[RequiresPermission(Permissions.TagNameConflictsManage)]
public sealed class TagNameConflictsController(
    TagNameConflictScanner scanner,
    TagNameConflictCleanupService cleanupService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<TagNameConflictScanDto>> Scan(CancellationToken ct)
        => Ok(await scanner.ScanAsync(ct));

    [HttpGet("summary")]
    public async Task<ActionResult<TagNameConflictSummaryDto>> Summary(CancellationToken ct)
        => Ok(await scanner.ScanSummaryAsync(ct));

    [HttpPost("resolve")]
    public async Task<ActionResult<TagNameConflictScanDto>> Resolve(
        [FromBody] ResolveTagNameConflictDto request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.GroupKey))
            return BadRequest(new { message = "A conflict group key is required." });
        if (string.IsNullOrWhiteSpace(request.ExpectedRevision))
            return BadRequest(new { message = "The scanned conflict-group revision is required." });

        try
        {
            return Ok(await cleanupService.ResolveAsync(
                request.GroupKey,
                request.ExpectedRevision,
                request.SurvivorTagId,
                request.Resolutions,
                request.ExternalReferenceResolutions,
                ct));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (TagMergeBlockedException exception)
        {
            return Conflict(new
            {
                code = "TAG_MERGE_EXTENSION_REFERENCES",
                message = exception.Message,
                exception.ReferenceCount,
                exception.AffectedTagCount,
                exception.HasUninspectableReferences,
            });
        }
        catch (TagNameConflictException exception)
        {
            return Conflict(new { code = "TAG_NAME_RENAME_CONFLICT", message = exception.Message });
        }
        catch (TagExternalReferenceRepairException exception)
        {
            return Conflict(new { code = "TAG_EXTENSION_REFERENCE_REPAIR_FAILED", message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { code = "TAG_NAME_CONFLICT_CHANGED", message = exception.Message });
        }
    }

    [HttpPost("resolve-all")]
    public async Task<ActionResult<TagNameConflictScanDto>> ResolveAll(
        [FromBody] ResolveAllTagNameConflictsDto request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ExpectedRevision))
            return BadRequest(new { message = "The scanned conflict-list revision is required." });

        try
        {
            return Ok(await cleanupService.ResolveAllRecommendedAsync(request.ExpectedRevision, ct));
        }
        catch (TagMergeBlockedException exception)
        {
            return Conflict(new
            {
                code = "TAG_MERGE_EXTENSION_REFERENCES",
                message = exception.Message,
                exception.ReferenceCount,
                exception.AffectedTagCount,
                exception.HasUninspectableReferences,
            });
        }
        catch (TagNameConflictException exception)
        {
            return Conflict(new { code = "TAG_NAME_RENAME_CONFLICT", message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { code = "TAG_NAME_CONFLICT_CHANGED", message = exception.Message });
        }
    }
}
