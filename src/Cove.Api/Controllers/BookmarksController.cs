using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/me/bookmarks")]
[AllowWithoutPermission]
public class BookmarksController(CoveContext db, ICurrentPrincipalAccessor principalAccessor, IUserEngagementService engagement) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BookmarkDto>>> List(CancellationToken ct)
    {
        if (principalAccessor.Current?.UserId is not int userId)
            return Forbid();

        var rows = await db.UserBookmarks.AsNoTracking()
            .Where(bookmark => bookmark.UserId == userId)
            .OrderByDescending(bookmark => bookmark.CreatedAt)
            .ToListAsync(ct);

        var readableIdsByType = new Dictionary<AffinityHostType, HashSet<int>>();
        foreach (var group in rows.GroupBy(bookmark => bookmark.HostType))
            readableIdsByType[group.Key] = await GetReadableHostIdsAsync(group.Key, group.Select(bookmark => bookmark.HostId), ct);

        return Ok(rows
            .Where(bookmark => readableIdsByType.TryGetValue(bookmark.HostType, out var readableIds) && readableIds.Contains(bookmark.HostId))
            .Select(bookmark => new BookmarkDto(bookmark.HostType, bookmark.HostId, bookmark.CreatedAt.ToString("o")))
            .ToList());
    }

    [HttpPost("batch")]
    public async Task<ActionResult<IReadOnlyList<BookmarkStateDto>>> Batch([FromBody] BookmarkBatchRequestDto dto, CancellationToken ct)
    {
        if (principalAccessor.Current?.UserId is not int userId)
            return Forbid();
        if (!HasPermission(dto.HostType))
            return Forbid();

        var ids = dto.HostIds.Distinct().ToList();
        var readableIds = await GetReadableHostIdsAsync(dto.HostType, ids, ct);
        var rows = await db.UserBookmarks.AsNoTracking()
            .Where(bookmark => bookmark.UserId == userId && bookmark.HostType == dto.HostType && readableIds.Contains(bookmark.HostId))
            .ToDictionaryAsync(bookmark => bookmark.HostId, bookmark => bookmark.CreatedAt, ct);

        return Ok(ids.Select(id => new BookmarkStateDto(
            dto.HostType,
            id,
            readableIds.Contains(id) && rows.ContainsKey(id),
            rows.TryGetValue(id, out var createdAt) ? createdAt.ToString("o") : null)).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<BookmarkStateDto>> Toggle([FromBody] BookmarkToggleDto dto, CancellationToken ct)
    {
        if (principalAccessor.Current?.UserId is not int userId)
            return Forbid();
        if (!HasPermission(dto.HostType))
            return Forbid();
        if (!await HostExistsAsync(dto.HostType, dto.HostId, ct))
            return NotFound();

        var existing = await db.UserBookmarks.FirstOrDefaultAsync(
            bookmark => bookmark.UserId == userId && bookmark.HostType == dto.HostType && bookmark.HostId == dto.HostId,
            ct);

        if (dto.Saved)
        {
            if (existing is null)
            {
                existing = new UserBookmark
                {
                    UserId = userId,
                    HostType = dto.HostType,
                    HostId = dto.HostId,
                    CreatedAt = DateTime.UtcNow,
                };
                db.UserBookmarks.Add(existing);
            }
        }
        else if (existing is not null)
        {
            db.UserBookmarks.Remove(existing);
        }

        await db.SaveChangesAsync(ct);
        // Reflect the bookmark as a soft-positive engagement signal (creates the affinity row if needed).
        await engagement.SetBookmarkedAsync(dto.HostType, dto.HostId, dto.Saved, ct);
        return Ok(new BookmarkStateDto(dto.HostType, dto.HostId, dto.Saved, dto.Saved ? existing?.CreatedAt.ToString("o") : null));
    }

    private async Task<bool> HostExistsAsync(AffinityHostType hostType, int hostId, CancellationToken ct)
        => hostType switch
        {
            AffinityHostType.Video => await db.Videos.AsNoTracking().AnyAsync(item => item.Id == hostId, ct),
            AffinityHostType.Audio => await db.Audios.AsNoTracking().AnyAsync(item => item.Id == hostId, ct),
            AffinityHostType.Text => await db.TextDocuments.AsNoTracking().AnyAsync(item => item.Id == hostId, ct),
            AffinityHostType.Image => await db.Images.AsNoTracking().AnyAsync(item => item.Id == hostId, ct),
            AffinityHostType.Performer => await db.Performers.AsNoTracking().AnyAsync(item => item.Id == hostId, ct),
            AffinityHostType.Face => await db.Faces.AsNoTracking().AnyAsync(item => item.Id == hostId, ct),
            AffinityHostType.Tag => await db.Tags.AsNoTracking().AnyAsync(item => item.Id == hostId, ct),
            AffinityHostType.Studio => await db.Studios.AsNoTracking().AnyAsync(item => item.Id == hostId, ct),
            AffinityHostType.Gallery => await db.Galleries.AsNoTracking().AnyAsync(item => item.Id == hostId, ct),
            AffinityHostType.Group => await db.Groups.AsNoTracking().AnyAsync(item => item.Id == hostId, ct),
            AffinityHostType.Segment => await db.VisibleSegments().AsNoTracking().AnyAsync(item => item.Id == hostId, ct),
            _ => false,
        };

    private async Task<HashSet<int>> GetReadableHostIdsAsync(AffinityHostType hostType, IEnumerable<int> hostIds, CancellationToken ct)
    {
        var ids = hostIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
            return [];

        var readableIds = hostType switch
        {
            AffinityHostType.Video => await db.Videos.AsNoTracking().Where(item => ids.Contains(item.Id)).Select(item => item.Id).ToListAsync(ct),
            AffinityHostType.Audio => await db.Audios.AsNoTracking().Where(item => ids.Contains(item.Id)).Select(item => item.Id).ToListAsync(ct),
            AffinityHostType.Text => await db.TextDocuments.AsNoTracking().Where(item => ids.Contains(item.Id)).Select(item => item.Id).ToListAsync(ct),
            AffinityHostType.Image => await db.Images.AsNoTracking().Where(item => ids.Contains(item.Id)).Select(item => item.Id).ToListAsync(ct),
            AffinityHostType.Performer => await db.Performers.AsNoTracking().Where(item => ids.Contains(item.Id)).Select(item => item.Id).ToListAsync(ct),
            AffinityHostType.Face => await db.Faces.AsNoTracking().Where(item => ids.Contains(item.Id)).Select(item => item.Id).ToListAsync(ct),
            AffinityHostType.Tag => await db.Tags.AsNoTracking().Where(item => ids.Contains(item.Id)).Select(item => item.Id).ToListAsync(ct),
            AffinityHostType.Studio => await db.Studios.AsNoTracking().Where(item => ids.Contains(item.Id)).Select(item => item.Id).ToListAsync(ct),
            AffinityHostType.Gallery => await db.Galleries.AsNoTracking().Where(item => ids.Contains(item.Id)).Select(item => item.Id).ToListAsync(ct),
            AffinityHostType.Group => await db.Groups.AsNoTracking().Where(item => ids.Contains(item.Id)).Select(item => item.Id).ToListAsync(ct),
            AffinityHostType.Segment => await db.VisibleSegments().AsNoTracking().Where(item => ids.Contains(item.Id)).Select(item => item.Id).ToListAsync(ct),
            _ => [],
        };

        return readableIds.ToHashSet();
    }

    private bool HasPermission(AffinityHostType hostType)
    {
        var permission = hostType switch
        {
            AffinityHostType.Video => Permissions.VideosRead,
            AffinityHostType.Audio => Permissions.AudiosRead,
            AffinityHostType.Text => Permissions.TextsRead,
            AffinityHostType.Image => Permissions.ImagesRead,
            AffinityHostType.Performer => Permissions.PerformersRead,
            AffinityHostType.Face => Permissions.FacesRead,
            AffinityHostType.Tag => Permissions.TagsRead,
            AffinityHostType.Studio => Permissions.StudiosRead,
            AffinityHostType.Gallery => Permissions.GalleriesRead,
            AffinityHostType.Group => Permissions.GroupsRead,
            AffinityHostType.Segment => Permissions.SegmentsRead,
            _ => null,
        };
        return permission is not null && principalAccessor.Current?.Has(permission) == true;
    }
}

