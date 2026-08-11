using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Data.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/entity-name-conflicts")]
[RequiresPermission(Permissions.EntityNameConflictsManage)]
public sealed class EntityNameConflictsController(
    EntityNameConflictScanner scanner,
    EntityNameConflictCleanupService cleanupService) : ControllerBase
{
    [HttpGet("summary")]
    public async Task<ActionResult<EntityNameConflictSummaryDto>> Summary(CancellationToken ct)
        => Ok(await scanner.ScanSummaryAsync(ct));

    [HttpGet("{entityType}")]
    public async Task<ActionResult<EntityNameConflictScanDto>> Scan(string entityType, CancellationToken ct)
    {
        if (!NameConflictEntityTypes.IsSupported(entityType))
            return BadRequest(new { message = "Entity type must be performer or studio." });
        return Ok(await scanner.ScanAsync(entityType, ct));
    }

    [HttpPost("resolve")]
    public async Task<ActionResult<EntityNameConflictScanDto>> Resolve(
        [FromBody] ResolveEntityNameConflictDto request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await cleanupService.ResolveAsync(request, ct));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (EntityMergeBlockedException exception)
        {
            return Conflict(new
            {
                code = "ENTITY_MERGE_EXTENSION_REFERENCES",
                message = exception.Message,
                exception.EntityType,
                exception.ReferenceCount,
                exception.AffectedEntityCount,
                exception.HasUninspectableReferences,
            });
        }
        catch (EntityNameConflictException exception)
        {
            return Conflict(new { code = "ENTITY_NAME_RENAME_CONFLICT", message = exception.Message, exception.EntityType });
        }
        catch (EntityExternalReferenceRepairException exception)
        {
            return Conflict(new { code = "ENTITY_EXTENSION_REFERENCE_REPAIR_FAILED", message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { code = "ENTITY_NAME_CONFLICT_CHANGED", message = exception.Message });
        }
    }

    [HttpPost("resolve-all")]
    public async Task<ActionResult<EntityNameConflictScanDto>> ResolveAll(
        [FromBody] ResolveAllEntityNameConflictsDto request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await cleanupService.ResolveAllRecommendedAsync(request.EntityType, request.ExpectedRevision, ct));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (EntityMergeBlockedException exception)
        {
            return Conflict(new
            {
                code = "ENTITY_MERGE_EXTENSION_REFERENCES",
                message = exception.Message,
                exception.EntityType,
                exception.ReferenceCount,
                exception.AffectedEntityCount,
                exception.HasUninspectableReferences,
            });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { code = "ENTITY_NAME_CONFLICT_CHANGED", message = exception.Message });
        }
    }
}
