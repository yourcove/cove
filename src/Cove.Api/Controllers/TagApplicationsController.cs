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
            .ThenBy(item => item.Tag!.TagGroupId.HasValue ? 0 : 1)
            .ThenBy(item => item.Tag!.TagGroup != null ? item.Tag.TagGroup.SortOrder : int.MaxValue)
            .ThenBy(item => item.Tag!.TagGroup != null ? item.Tag.TagGroup.Name : null)
            .ThenBy(item => item.Tag!.SortName ?? item.Tag.Name)
            .ThenBy(item => item.TagId)
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

    /// <summary>
    /// "Report incorrect detection": removes the AI's host-level tag applications for one (host, tag)
    /// so a wrongly-derived tag drops off this host. Leaves the global threshold and timeline segments
    /// untouched. Use this only for genuine AI mistakes — the "tag is correct but too minor" case is a
    /// threshold adjustment, not a deletion.
    /// </summary>
    [HttpDelete("host/{hostType}/{hostId:int}/tag/{tagId:int}")]
    [RequiresPermission(Permissions.TagsWrite)]
    public async Task<IActionResult> DeleteForHostTag(string hostType, int hostId, int tagId, CancellationToken ct)
    {
        try
        {
            var deleted = await service.DeleteHostTagApplicationsAsync(hostType, hostId, tagId, ct);
            return deleted == 0 ? NotFound() : NoContent();
        }
        catch (TagApplicationValidationException ex)
        {
            return StatusCode(ex.StatusCode, new { message = ex.Message });
        }
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
            tag.MinOccurrencePercent,
            HasImage: tag.ImageOverrideBlobId != null || tag.ImageBlobId != null)
        {
            TagGroupSortOrder = tag.TagGroup?.SortOrder,
            SortName = tag.SortName,
        };
}
