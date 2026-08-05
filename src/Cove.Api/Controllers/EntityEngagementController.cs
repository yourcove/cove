using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/engagement")]
[AllowWithoutPermission]
public class EntityEngagementController(IUserEngagementService engagementService, ICurrentPrincipalAccessor principalAccessor) : ControllerBase
{
    [HttpGet("{hostType}/{hostId:int}")]
    public async Task<ActionResult<EntityEngagementDto>> Get(AffinityHostType hostType, int hostId, CancellationToken cancellationToken)
    {
        if (!HasPermission(hostType, write: false))
            return Forbid();

        var snapshot = await engagementService.GetSnapshotAsync(hostType, hostId, cancellationToken);
        return snapshot is null ? NotFound() : Ok(ToDto(hostId, snapshot));
    }

    [HttpGet("{hostType}/{hostId:int}/ratings")]
    public async Task<ActionResult<EntityRatingsDto>> GetRatings(AffinityHostType hostType, int hostId, CancellationToken cancellationToken)
    {
        if (!HasPermission(hostType, write: false))
            return Forbid();

        var ratings = await engagementService.GetRatingsByAspectAsync(hostType, hostId, cancellationToken);
        return ratings is null ? NotFound() : Ok(new EntityRatingsDto(hostId, ratings));
    }

    [HttpPost("batch")]
    public async Task<ActionResult<IReadOnlyList<EntityEngagementDto>>> Batch([FromBody] EntityEngagementBatchRequestDto dto, CancellationToken cancellationToken)
    {
        if (!HasPermission(dto.HostType, write: false))
            return Forbid();

        var snapshots = await engagementService.GetSnapshotsAsync(dto.HostType, dto.HostIds, cancellationToken);
        return Ok(dto.HostIds.Distinct().Select(hostId => ToDto(hostId, snapshots.GetValueOrDefault(hostId))).ToList());
    }

    [HttpPost("interactions")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("interactions")]
    public async Task<IActionResult> RecordInteraction([FromBody] EngagementInteractionWriteDto dto, CancellationToken cancellationToken)
    {
        if (principalAccessor.Current?.UserId is null)
            return Forbid();
        if (!InteractionValueMapper.TryParseHostType(dto.HostType, out var hostType))
            return BadRequest("Unsupported interaction host type.");
        if (!InteractionValueMapper.TryParseKind(dto.Kind, out var kind))
            return BadRequest("Unsupported interaction kind.");
        if (kind == InteractionKind.LikeCount)
            return BadRequest("Like count interactions must use a dedicated media like endpoint.");
        if (InteractionValueMapper.RequiresConcreteHost(hostType) && (!dto.HostId.HasValue || dto.HostId.Value <= 0))
            return BadRequest("This interaction host type requires a host id.");
        if (!HasInteractionPermission(hostType))
            return Forbid();

        var recorded = await engagementService.RecordInteractionAsync(
            hostType,
            dto.HostId ?? 0,
            kind,
            dto.Meta,
            cancellationToken);

        return recorded ? NoContent() : NotFound();
    }

    [HttpGet("interactions")]
    public async Task<ActionResult<IReadOnlyList<EngagementInteractionDto>>> GetInteractions([FromQuery] string? hostType, [FromQuery] int? hostId, [FromQuery] int limit = 100, CancellationToken cancellationToken = default)
    {
        if (principalAccessor.Current?.UserId is null)
            return Forbid();

        InteractionHostType? parsedHostType = null;
        if (!string.IsNullOrWhiteSpace(hostType))
        {
            if (!InteractionValueMapper.TryParseHostType(hostType, out var concreteHostType))
                return BadRequest("Unsupported interaction host type.");
            if (InteractionValueMapper.RequiresConcreteHost(concreteHostType) && hostId.GetValueOrDefault() <= 0)
                return BadRequest("This interaction host type requires a host id.");
            if (!HasInteractionPermission(concreteHostType))
                return Forbid();
            parsedHostType = concreteHostType;
        }

        return Ok(await engagementService.GetInteractionsAsync(parsedHostType, hostId, limit, cancellationToken));
    }

    [HttpPut("{hostType}/{hostId:int}/favorite")]
    public async Task<ActionResult<EntityEngagementDto>> SetFavorite(AffinityHostType hostType, int hostId, [FromBody] EntityFavoriteDto dto, CancellationToken cancellationToken)
    {
        if (principalAccessor.Current?.UserId is null)
            return Forbid();
        if (!HasPermission(hostType, write: false))
            return Forbid();

        var snapshot = await engagementService.SetFavoriteAsync(hostType, hostId, dto.IsFavorite, cancellationToken);
        return snapshot is null ? NotFound() : Ok(ToDto(hostId, snapshot));
    }

    [HttpPut("{hostType}/{hostId:int}/rating")]
    public async Task<ActionResult<EntityEngagementDto>> SetRating(AffinityHostType hostType, int hostId, [FromBody] VideoRatingDto dto, CancellationToken cancellationToken)
    {
        if (principalAccessor.Current?.UserId is null)
            return Forbid();
        if (!HasPermission(hostType, write: false))
            return Forbid();

        var snapshot = await engagementService.SetRatingAsync(hostType, hostId, dto.Value, dto.Aspect, cancellationToken);
        return snapshot is null ? NotFound() : Ok(ToDto(hostId, snapshot));
    }

    [HttpPost("activity/reset-all")]
    public async Task<ActionResult<object>> ResetAllActivity(CancellationToken cancellationToken)
    {
        if (principalAccessor.Current?.UserId is null)
            return Forbid();

        var count = await engagementService.ResetAllActivityAsync(cancellationToken);
        return Ok(new { reset = count });
    }

    [HttpPost("wipe-all")]
    public async Task<ActionResult<object>> WipeAllEngagement(CancellationToken cancellationToken)
    {
        if (principalAccessor.Current?.UserId is null)
            return Forbid();

        var count = await engagementService.WipeAllEngagementAsync(cancellationToken);
        return Ok(new { wiped = count });
    }

    private bool HasPermission(AffinityHostType hostType, bool write)
    {
        var permission = (hostType, write) switch
        {
            (AffinityHostType.Video, false) => Permissions.VideosRead,
            (AffinityHostType.Video, true) => Permissions.VideosWrite,
            (AffinityHostType.Audio, false) => Permissions.AudiosRead,
            (AffinityHostType.Audio, true) => Permissions.AudiosWrite,
            (AffinityHostType.Text, false) => Permissions.TextsRead,
            (AffinityHostType.Text, true) => Permissions.TextsWrite,
            (AffinityHostType.Image, false) => Permissions.ImagesRead,
            (AffinityHostType.Image, true) => Permissions.ImagesWrite,
            (AffinityHostType.Performer, false) => Permissions.PerformersRead,
            (AffinityHostType.Performer, true) => Permissions.PerformersWrite,
            (AffinityHostType.Face, false) => Permissions.FacesRead,
            (AffinityHostType.Face, true) => Permissions.FacesWrite,
            (AffinityHostType.Tag, false) => Permissions.TagsRead,
            (AffinityHostType.Tag, true) => Permissions.TagsWrite,
            (AffinityHostType.Studio, false) => Permissions.StudiosRead,
            (AffinityHostType.Studio, true) => Permissions.StudiosWrite,
            (AffinityHostType.Gallery, false) => Permissions.GalleriesRead,
            (AffinityHostType.Gallery, true) => Permissions.GalleriesWrite,
            (AffinityHostType.Group, false) => Permissions.GroupsRead,
            (AffinityHostType.Group, true) => Permissions.GroupsWrite,
            (AffinityHostType.Segment, false) => Permissions.SegmentsRead,
            (AffinityHostType.Segment, true) => Permissions.SegmentsWrite,
            _ => null,
        };

        return permission != null && principalAccessor.Current?.Has(permission) == true;
    }

    private bool HasInteractionPermission(InteractionHostType hostType)
    {
        var permission = hostType switch
        {
            InteractionHostType.Video => Permissions.VideosRead,
            InteractionHostType.Image => Permissions.ImagesRead,
            InteractionHostType.Audio => Permissions.AudiosRead,
            InteractionHostType.Text => Permissions.TextsRead,
            InteractionHostType.Performer => Permissions.PerformersRead,
            InteractionHostType.Face => Permissions.FacesRead,
            InteractionHostType.Tag => Permissions.TagsRead,
            InteractionHostType.Segment => Permissions.SegmentsRead,
            InteractionHostType.Studio => Permissions.StudiosRead,
            InteractionHostType.Gallery => Permissions.GalleriesRead,
            InteractionHostType.Group => Permissions.GroupsRead,
            InteractionHostType.Search => null,
            InteractionHostType.Collection => null,
            _ => null,
        };

        return permission == null || principalAccessor.Current?.Has(permission) == true;
    }

    private static EntityEngagementDto ToDto(int hostId, UserEngagementSnapshot? snapshot)
    {
        snapshot ??= new UserEngagementSnapshot(false, null, 0d, 0d, 0, null, 0, 0, 0, 0);
        return new EntityEngagementDto(
            hostId,
            snapshot.IsFavorite,
            snapshot.Rating,
            snapshot.ResumeTime,
            snapshot.PlayDuration,
            snapshot.PlayCount,
            snapshot.LastPlayedAt?.ToString("o"),
            snapshot.LikeCount,
            snapshot.DerivedLikeCount,
            snapshot.PageVisitCount,
            snapshot.CompleteCount,
            snapshot.LastLikedAt?.ToString("o"));
    }
}
