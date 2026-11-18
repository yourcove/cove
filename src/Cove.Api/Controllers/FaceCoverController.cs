using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/faces")]
[RequiresPermission(Permissions.FacesRead)]
public class FaceCoverController(FaceCoverService faceCoverService) : ControllerBase
{
    private const int MaxDetectionIds = 50;

    /// <summary>
    /// Where the face's images come from: the state of its stored cover, and for each requested detection of the
    /// face the video or image, the moment, and the face segment that contains it.
    /// </summary>
    [HttpGet("{id:int}/image-sources")]
    public async Task<ActionResult<FaceImageSourcesDto>> GetImageSources(
        int id,
        [FromQuery] int[]? detectionIds,
        CancellationToken cancellationToken)
    {
        if (detectionIds is { Length: > MaxDetectionIds })
            return BadRequest(new { error = $"At most {MaxDetectionIds} detections can be described at once." });

        var sources = await faceCoverService.DescribeAsync(id, detectionIds ?? [], cancellationToken);
        return sources is null ? NotFound() : Ok(sources);
    }

    /// <summary>Makes a crop of one of the face's own detections its cover, and records that detection as the cover's source.</summary>
    [HttpPost("{id:int}/cover/detection")]
    [RequiresPermission(Permissions.FacesWrite)]
    [RequiresEntityAccess(EntityKinds.Face, Permissions.FacesWrite)]
    public async Task<IActionResult> SetCoverFromDetection(
        int id,
        [FromBody] FaceCoverFromDetectionDto request,
        CancellationToken cancellationToken)
        => await faceCoverService.SetFromDetectionAsync(id, request.DetectionId, cancellationToken) switch
        {
            FaceCoverChangeOutcome.Changed => NoContent(),
            FaceCoverChangeOutcome.FaceNotFound => NotFound(new { error = "Face was not found." }),
            FaceCoverChangeOutcome.DetectionNotOnFace => BadRequest(new { error = "That detection does not belong to this face." }),
            _ => Conflict(new { error = "The frame for that detection could not be read, so the cover was not changed." }),
        };
}
