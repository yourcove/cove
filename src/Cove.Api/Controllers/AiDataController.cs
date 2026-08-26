using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.DTOs;

using Microsoft.AspNetCore.Mvc;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/ai-data")]
[RequiresPermission(Permissions.AiDataRead)]
public class AiDataController(
    AiDataPurgeService aiDataPurgeService,
    IAuditService auditService,
    ICurrentPrincipalAccessor principalAccessor) : ControllerBase
{
    [HttpGet("summary")]
    [RequiresUnscopedEntityAccess("read")]
    public async Task<ActionResult<AiDataSummaryDto>> Summary(
        [FromQuery] string? sourceKey,
        [FromQuery] string? sourceRunId,
        [FromQuery] string? model,
        [FromQuery] string? modality,
        [FromQuery] string? hostType,
        [FromQuery] int? hostId,
        [FromQuery] string? kinds,
        CancellationToken cancellationToken)
    {
        var selector = new AiDataSelectorDto(sourceKey, sourceRunId, model, modality, hostType, hostId, SplitKinds(kinds));
        return Ok(await aiDataPurgeService.GetSummaryAsync(selector, cancellationToken));
    }

    [HttpPost("purge")]
    [RequiresPermission(Permissions.AiDataClear)]
    [RequiresUnscopedEntityAccess("read")]
    [RequiresUnscopedEntityAccess("delete", ActionArgumentName = "request", SkipWhenPropertyTrue = "DryRun")]
    public async Task<ActionResult<AiDataPurgeResultDto>> Purge([FromBody] AiDataPurgeRequestDto request, CancellationToken cancellationToken)
    {
        var selector = request.ToSelectorDto();
        if (!AiDataPurgeService.TryValidateDestructiveSelector(selector, out var error))
        {
            return BadRequest(new { error });
        }

        var result = await aiDataPurgeService.PurgeAsync(selector, request.DryRun, cancellationToken);

        if (!request.DryRun)
        {
            var actor = principalAccessor.Current;
            var occurredAt = DateTime.UtcNow;
            await auditService.LogAsync(
                AuditActions.AiDataPurge,
                AuditOutcomes.Success,
                actor,
                targetKind: "ai_data",
                detail: new
                {
                    userId = actor?.UserId,
                    selector,
                    kindCounts = result.RemovedCounts,
                    timestampUtc = occurredAt,
                },
                ct: cancellationToken);
        }

        return Ok(result);
    }

    private static List<string>? SplitKinds(string? kinds)
    {
        if (string.IsNullOrWhiteSpace(kinds))
        {
            return null;
        }

        var parsed = kinds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        return parsed.Count == 0 ? null : parsed;
    }
}
