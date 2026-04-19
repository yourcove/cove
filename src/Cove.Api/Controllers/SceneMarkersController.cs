using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/scenes/{sceneId:int}/markers")]
public class SceneMarkersController(ISceneMarkerRepository markerRepo, ISceneRepository sceneRepo, CoveContext db) : ControllerBase
{
    /// <summary>Returns random scene markers for a wall/discovery view.</summary>
    [HttpGet("/api/markers/wall")]
    public async Task<ActionResult<List<SceneMarkerWallDto>>> MarkerWall([FromQuery] string? q, [FromQuery] int? tagId, [FromQuery] int count = 24, CancellationToken ct = default)
    {
        var query = db.SceneMarkers
            .Include(m => m.PrimaryTag)
            .Include(m => m.Scene).ThenInclude(s => s!.Files).ThenInclude(f => f.ParentFolder)
            .Include(m => m.SceneMarkerTags).ThenInclude(mt => mt.Tag)
            .AsNoTracking();

        if (!string.IsNullOrEmpty(q))
            query = query.Where(m => EF.Functions.ILike(m.Title, $"%{q}%"));
        if (tagId.HasValue)
            query = query.Where(m => m.PrimaryTagId == tagId.Value || m.SceneMarkerTags.Any(mt => mt.TagId == tagId.Value));

        var markers = await query.OrderBy(_ => EF.Functions.Random()).Take(count).ToListAsync(ct);
        return Ok(markers.Select(m => new SceneMarkerWallDto(
            m.Id, m.Title, m.Seconds, m.EndSeconds, m.PrimaryTagId, m.PrimaryTag?.Name ?? "",
            m.SceneId, m.Scene?.Title ?? "", m.Scene?.Files.FirstOrDefault()?.Path ?? "",
            m.SceneMarkerTags.Select(mt => new TagSummaryDto(mt.TagId, mt.Tag?.Name ?? "")).ToList()
        )).ToList());
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SceneMarkerSummaryDto>>> GetByScene(int sceneId, CancellationToken ct)
    {
        var markers = await markerRepo.GetBySceneIdAsync(sceneId, ct);
        return Ok(markers.Select(MapToDto).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<SceneMarkerSummaryDto>> GetById(int sceneId, int id, CancellationToken ct)
    {
        var marker = await markerRepo.GetByIdAsync(id, ct);
        if (marker == null || marker.SceneId != sceneId) return NotFound();
        return Ok(MapToDto(marker));
    }

    [HttpPost]
    public async Task<ActionResult<SceneMarkerSummaryDto>> Create(int sceneId, [FromBody] SceneMarkerCreateDto dto, CancellationToken ct)
    {
        var scene = await sceneRepo.GetByIdAsync(sceneId, ct);
        if (scene == null) return NotFound();

        var marker = new SceneMarker
        {
            Title = dto.Title, Seconds = dto.Seconds, EndSeconds = dto.EndSeconds,
            PrimaryTagId = dto.PrimaryTagId, SceneId = sceneId
        };
        if (dto.TagIds?.Count > 0)
            marker.SceneMarkerTags = dto.TagIds.Select(tid => new SceneMarkerTag { TagId = tid }).ToList();

        marker = await markerRepo.AddAsync(marker, ct);
        return CreatedAtAction(nameof(GetById), new { sceneId, id = marker.Id }, MapToDto(marker));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<SceneMarkerSummaryDto>> Update(int sceneId, int id, [FromBody] SceneMarkerUpdateDto dto, CancellationToken ct)
    {
        var marker = await markerRepo.GetByIdAsync(id, ct);
        if (marker == null || marker.SceneId != sceneId) return NotFound();

        if (dto.Title != null) marker.Title = dto.Title;
        if (dto.Seconds.HasValue) marker.Seconds = dto.Seconds.Value;
        if (dto.EndSeconds.HasValue) marker.EndSeconds = dto.EndSeconds;
        if (dto.PrimaryTagId.HasValue) marker.PrimaryTagId = dto.PrimaryTagId.Value;

        await markerRepo.UpdateAsync(marker, ct);
        return Ok(MapToDto(marker));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int sceneId, int id, CancellationToken ct)
    {
        var marker = await markerRepo.GetByIdAsync(id, ct);
        if (marker == null || marker.SceneId != sceneId) return NotFound();
        await markerRepo.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("/api/markers/bulk")]
    public async Task<ActionResult<List<SceneMarkerSummaryDto>>> BulkUpdate([FromBody] BulkSceneMarkerUpdateDto dto, CancellationToken ct)
    {
        var markers = await db.SceneMarkers
            .Include(m => m.PrimaryTag)
            .Include(m => m.SceneMarkerTags)
            .Where(m => dto.Ids.Contains(m.Id))
            .ToListAsync(ct);

        foreach (var marker in markers)
        {
            if (dto.PrimaryTagId.HasValue) marker.PrimaryTagId = dto.PrimaryTagId.Value;
            if (dto.TagIds != null)
            {
                var mode = dto.TagMode?.ToUpperInvariant() ?? "SET";
                if (mode == "SET")
                {
                    marker.SceneMarkerTags.Clear();
                    marker.SceneMarkerTags = dto.TagIds.Select(tid => new SceneMarkerTag { TagId = tid, SceneMarkerId = marker.Id }).ToList();
                }
                else if (mode == "ADD")
                {
                    var existing = marker.SceneMarkerTags.Select(mt => mt.TagId).ToHashSet();
                    foreach (var tid in dto.TagIds.Where(tid => !existing.Contains(tid)))
                        marker.SceneMarkerTags.Add(new SceneMarkerTag { TagId = tid, SceneMarkerId = marker.Id });
                }
                else if (mode == "REMOVE")
                {
                    var toRemove = marker.SceneMarkerTags.Where(mt => dto.TagIds.Contains(mt.TagId)).ToList();
                    foreach (var mt in toRemove) marker.SceneMarkerTags.Remove(mt);
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return Ok(markers.Select(MapToDto).ToList());
    }

    [HttpPost("/api/markers/destroy")]
    public async Task<IActionResult> DestroyBatch([FromBody] BatchDeleteDto dto, CancellationToken ct)
    {
        var markers = await db.SceneMarkers.Where(m => dto.Ids.Contains(m.Id)).ToListAsync(ct);
        db.SceneMarkers.RemoveRange(markers);
        await db.SaveChangesAsync(ct);
        return Ok(new { deleted = markers.Count });
    }

    private static SceneMarkerSummaryDto MapToDto(SceneMarker m) => new(
        m.Id, m.Title, m.Seconds, m.EndSeconds, m.PrimaryTagId, m.PrimaryTag?.Name ?? "");
}

// DTOs for scene markers - use Cove.Core.DTOs versions instead
// (SceneMarkerCreateDto and SceneMarkerUpdateDto are in Cove.Core.DTOs.DTOs)
