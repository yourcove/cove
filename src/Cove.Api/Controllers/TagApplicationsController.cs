using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/tagapplications")]
[RequiresPermission(Permissions.TagsRead)]
public sealed class TagApplicationsController(TagApplicationService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TagApplicationDto>>> List(
        [FromQuery] string? hostType,
        [FromQuery] int? hostId,
        [FromQuery] string? contextType,
        [FromQuery] int? contextId,
        CancellationToken ct)
    {
        var applications = await service.BuildQuery(hostType, hostId, contextType, contextId)
            .OrderBy(item => item.ContextType)
            .ThenBy(item => item.ContextId)
            .ThenBy(item => item.Tag!.Name)
            .ToListAsync(ct);

        return Ok(applications.Select(Map).ToList());
    }

    [HttpPost]
    [RequiresPermission(Permissions.TagsWrite)]
    public async Task<ActionResult<TagApplicationDto>> Create([FromBody] TagApplicationCreateDto dto, CancellationToken ct)
    {
        try
        {
            var application = await service.AddAsync(dto, ct);
            return CreatedAtAction(nameof(List), new { hostType = application.HostType.ToString(), hostId = application.HostId }, Map(application));
        }
        catch (TagApplicationValidationException ex)
        {
            return StatusCode(ex.StatusCode, new { message = ex.Message });
        }
    }

    [HttpDelete("{id:int}")]
    [RequiresPermission(Permissions.TagsWrite)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var application = await service.DeleteAsync(id, ct);
        return application == null ? NotFound() : NoContent();
    }

    public static TagApplicationDto Map(TagApplication application)
    {
        var tag = application.Tag ?? new Tag { Id = application.TagId, Name = $"Tag #{application.TagId}" };
        return new TagApplicationDto(
            application.Id,
            application.HostType.ToString().ToLowerInvariant(),
            application.HostId,
            application.ContextType,
            application.ContextId,
            MapTag(tag),
            application.SourceKey,
            string.IsNullOrWhiteSpace(application.SourceRunId) ? null : application.SourceRunId,
            string.IsNullOrWhiteSpace(application.ModelKey) ? null : application.ModelKey,
            application.Confidence,
            application.TotalDurationSec,
            application.HostDurationSec,
            application.CreatedAt.ToString("o"));
    }

    private static TagDto MapTag(Tag tag)
        => new(
            tag.Id,
            tag.Name,
            tag.Description,
            tag.Favorite,
            tag.Aliases.Select(alias => alias.Alias).ToList(),
            tag.ShowAsSegment,
            tag.SegmentColorOverride,
            tag.SegmentLaneOverride,
            null,
            tag.Color,
            tag.TagGroupId,
            tag.TagGroup?.Name,
            tag.TagGroup?.Color,
            tag.MinOccurrenceSec,
            tag.MinOccurrencePercent);
}