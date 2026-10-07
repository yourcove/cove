using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Controllers;

/// <summary>
/// Reads and deletes the shot boundaries of a video's files. Shots belong to files; without
/// <c>fileId</c> these endpoints act on the video's primary file. Writing is done by extensions
/// through <see cref="IVideoShotService"/>.
/// </summary>
[ApiController]
[Route("api/videos/{videoId:int}/shots")]
[RequiresPermission(Permissions.SegmentsRead)]
public class VideoShotsController(CoveContext db, IVideoShotService shots) : ControllerBase
{
    /// <summary>The set with its shots; 204 when the file has none, or the video has no primary file.</summary>
    [HttpGet]
    public async Task<ActionResult<VideoShotSetDto>> Get(int videoId, [FromQuery] int? fileId, CancellationToken ct)
    {
        var target = await ResolveFileAsync(videoId, fileId, ct);
        if (target.NotFound)
            return NotFound();
        if (target.FileId is not int resolvedFileId)
            return NoContent();

        var set = await shots.GetForFileAsync(resolvedFileId, ct);
        return set is null ? NoContent() : Ok(set);
    }

    /// <summary>Summaries, without shots, of the sets of every file of the video.</summary>
    [HttpGet("sets")]
    public async Task<ActionResult<IReadOnlyList<VideoShotSetDto>>> ListSets(int videoId, CancellationToken ct)
    {
        if (!await VideoExistsAsync(videoId, ct))
            return NotFound();
        return Ok(await shots.ListForVideoAsync(videoId, ct));
    }

    [HttpDelete]
    [RequiresPermission(Permissions.SegmentsDelete)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.SegmentsDelete, RouteValueName = "videoId")]
    public async Task<IActionResult> Delete(int videoId, [FromQuery] int? fileId, CancellationToken ct)
    {
        var target = await ResolveFileAsync(videoId, fileId, ct);
        if (target.NotFound || target.FileId is not int resolvedFileId)
            return NotFound();

        var result = await shots.DeleteAsync(new VideoShotDeleteRequest { FileId = resolvedFileId }, ct);
        return result.Status == VideoShotEditStatus.Deleted ? NoContent() : NotFound();
    }

    private sealed record FileTarget(bool NotFound, int? FileId);

    private async Task<FileTarget> ResolveFileAsync(int videoId, int? fileId, CancellationToken ct)
    {
        var video = await db.Videos
            .AsNoTracking()
            .Where(candidate => candidate.Id == videoId)
            .Select(candidate => new { candidate.PrimaryFileId })
            .FirstOrDefaultAsync(ct);
        if (video is null)
            return new FileTarget(true, null);
        if (fileId is null)
            return new FileTarget(false, video.PrimaryFileId);

        var belongs = await db.VideoFiles
            .AsNoTracking()
            .AnyAsync(file => file.Id == fileId && file.VideoId == videoId, ct);
        return belongs ? new FileTarget(false, fileId) : new FileTarget(true, null);
    }

    private Task<bool> VideoExistsAsync(int videoId, CancellationToken ct) =>
        db.Videos.AsNoTracking().AnyAsync(video => video.Id == videoId, ct);
}
