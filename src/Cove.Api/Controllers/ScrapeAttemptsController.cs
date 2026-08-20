using System.Globalization;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using IAuthorizationService = Cove.Core.Auth.IAuthorizationService;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/scrape-attempts")]
[RequiresPermission(Permissions.VideosScrape, Permissions.VideosWrite, Permissions.AudiosWrite, Permissions.TextsWrite, Permissions.ImagesWrite, Permissions.GalleriesWrite, Permissions.GroupsWrite, Mode = PermissionMode.Any)]
public class ScrapeAttemptsController(ScrapeAttemptService scrapeAttemptService, ICurrentPrincipalAccessor principalAccessor, IAuthorizationService authorizationService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ScrapeAttemptDto>>> List([FromQuery] string? entityType, [FromQuery] int? entityId, [FromQuery] int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entityType) || !entityId.HasValue)
            return BadRequest(new { error = "entityType and entityId are required." });

        var authorizationError = await AuthorizeEntityAsync(entityType, entityId.Value, write: false, ct);
        if (authorizationError != null)
            return authorizationError;

        return Ok(await scrapeAttemptService.ListAttemptsAsync(entityType, entityId, limit, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ScrapeAttemptDto>> Get(Guid id, CancellationToken ct)
    {
        var attempt = await scrapeAttemptService.GetAttemptAsync(id, ct);
        if (attempt == null)
            return NotFound();

        var authorizationError = await AuthorizeAttemptAsync(attempt, write: false, ct);
        if (authorizationError != null)
            return authorizationError;

        return Ok(attempt);
    }

    [HttpPost]
    public async Task<ActionResult<ScrapeAttemptDto>> Create([FromBody] CreateScrapeAttemptDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.EntityType) || dto.EntityId == null)
            return BadRequest(new { error = "EntityType and EntityId are required." });

        var authorizationError = await AuthorizeEntityAsync(dto.EntityType, dto.EntityId.Value, write: false, ct);
        if (authorizationError != null)
            return authorizationError;

        var attempt = await scrapeAttemptService.CreateAttemptAsync(dto, ct);
        return CreatedAtAction(nameof(Get), new { id = attempt.Id }, attempt);
    }

    [HttpPost("resolve-relations")]
    public async Task<ActionResult<ResolveScrapeRelationsResultDto>> ResolveRelations([FromBody] ResolveScrapeRelationsRequestDto dto, CancellationToken ct)
    {
        try
        {
            return Ok(await scrapeAttemptService.ResolveRelationsAsync(dto, ct));
        }
        catch (EntityNameConflictException exception)
        {
            return Conflict(new { code = "ENTITY_NAME_CONFLICT", message = exception.Message, exception.EntityType });
        }
    }

    [HttpPost("{id:guid}/apply")]
    public async Task<ActionResult<ScrapeAttemptDto>> Apply(Guid id, [FromBody] ApplyVideoScrapeAttemptDto dto, CancellationToken ct)
    {
        var existingAttempt = await scrapeAttemptService.GetAttemptAsync(id, ct);
        if (existingAttempt == null)
            return NotFound();

        var authorizationError = await AuthorizeAttemptAsync(existingAttempt, write: true, ct);
        if (authorizationError != null)
            return authorizationError;

        try
        {
            var attempt = await scrapeAttemptService.ApplyAttemptAsync(id, dto, ct);
            return attempt == null ? NotFound() : Ok(attempt);
        }
        catch (EntityNameConflictException exception)
        {
            return Conflict(new { code = "ENTITY_NAME_CONFLICT", message = exception.Message, exception.EntityType });
        }
    }

    private async Task<ObjectResult?> AuthorizeAttemptAsync(ScrapeAttemptDto attempt, bool write, CancellationToken ct)
    {
        if (!attempt.EntityId.HasValue)
            return BadRequest(new { error = "Scrape attempt is not attached to an entity." });

        return await AuthorizeEntityAsync(attempt.EntityType, attempt.EntityId.Value, write, ct);
    }

    private async Task<ObjectResult?> AuthorizeEntityAsync(string entityType, int entityId, bool write, CancellationToken ct)
    {
        if (!TryGetAttemptPermissions(entityType, write, out var entityKind, out var permissions))
            return BadRequest(new { error = $"Scrape attempts are not supported for entity type '{entityType}'." });

        AuthorizationResult? denied = null;
        foreach (var permission in permissions)
        {
            var result = await authorizationService.AuthorizeAsync(
                principalAccessor.Current,
                permission,
                new EntityRef(entityKind, entityId.ToString(CultureInfo.InvariantCulture)),
                ct);

            if (result.Allowed)
                return null;

            denied = result;
        }

        return denied == null
            ? BadRequest(new { error = $"Scrape attempts are not supported for entity type '{entityType}'." })
            : ForbiddenResult(denied.Value);
    }

    private static bool TryGetAttemptPermissions(string entityType, bool write, out string entityKind, out IReadOnlyList<string> permissions)
    {
        entityKind = entityType.Trim().ToLowerInvariant();
        switch (entityKind)
        {
            case EntityKinds.Video:
                permissions = write
                    ? [Permissions.VideosWrite]
                    : [Permissions.VideosScrape, Permissions.VideosWrite];
                return true;
            case EntityKinds.Audio:
                permissions = [Permissions.AudiosWrite];
                return true;
            case EntityKinds.Text:
                permissions = [Permissions.TextsWrite];
                return true;
            case EntityKinds.Image:
                permissions = [Permissions.ImagesWrite];
                return true;
            case EntityKinds.Gallery:
                permissions = [Permissions.GalleriesWrite];
                return true;
            case EntityKinds.Group:
                permissions = [Permissions.GroupsWrite];
                return true;
            default:
                permissions = [];
                return false;
        }
    }

    private static ObjectResult ForbiddenResult(AuthorizationResult result) => new(new
    {
        code = "FORBIDDEN",
        message = result.Reason ?? "Forbidden.",
        missing = result.MissingPermission,
    })
    { StatusCode = StatusCodes.Status403Forbidden };
}
