using System.Text.Json;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/groups/{groupId:int}")]
[RequiresPermission(Permissions.GroupsRead)]
[RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsRead, RouteValueName = "groupId")]
public class GroupItemsController(CoveContext db, SegmentSpanResolver spanResolver, DynamicGroupResolver? dynamicGroups = null, IEventBus? eventBus = null) : ControllerBase
{
    [HttpGet("items")]
    [AllowShareLinkAccess]
    public async Task<ActionResult<IReadOnlyList<GroupItemDto>>> List(int groupId, CancellationToken ct)
    {
        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(item => item.Id == groupId, ct);
        if (group is null)
            return NotFound();

        if (group.Kind == GroupKind.Dynamic && dynamicGroups is not null)
            return Ok(await dynamicGroups.ResolveDtosAsync(groupId, forceRefresh: false, ct));

        var items = await db.GroupItems.AsNoTracking()
            .Include(item => item.Video).ThenInclude(video => video!.Files)
            .Include(item => item.Image)
            .Include(item => item.ChildGroup)
            .Where(item => item.GroupId == groupId)
            .OrderBy(item => item.OrderIndex)
            .ThenBy(item => item.Id)
            .ToListAsync(ct);

        return Ok(items.Select(MapItem).ToList());
    }

    [HttpGet("items/page")]
    [AllowShareLinkAccess]
    public async Task<ActionResult<PaginatedResponse<GroupItemDto>>> ListPage(
        int groupId,
        [FromQuery] int page = 1,
        [FromQuery] int perPage = 40,
        [FromQuery] string? sort = null,
        [FromQuery] string? direction = null,
        [FromQuery] string? q = null,
        CancellationToken ct = default)
    {
        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(item => item.Id == groupId, ct);
        if (group is null)
            return NotFound();

        var findFilter = new FindFilter
        {
            Page = page,
            PerPage = perPage,
            Sort = sort,
            Q = q,
            Direction = string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase) ? SortDirection.Desc : SortDirection.Asc,
        };

        if (group.Kind != GroupKind.Dynamic)
        {
            var staticQuery = db.GroupItems.AsNoTracking()
                .Include(item => item.Video).ThenInclude(video => video!.Files)
                .Include(item => item.Image)
                .Include(item => item.ChildGroup)
                .Where(item => item.GroupId == groupId);

            if (!string.IsNullOrWhiteSpace(findFilter.Q))
            {
                var searchText = findFilter.Q.Trim();
                staticQuery = staticQuery.Where(item =>
                    (item.Title != null && EF.Functions.ILike(item.Title, $"%{searchText}%"))
                    || (item.Video != null && item.Video.Title != null && EF.Functions.ILike(item.Video.Title, $"%{searchText}%"))
                    || (item.Image != null && item.Image.Title != null && EF.Functions.ILike(item.Image.Title, $"%{searchText}%"))
                    || (item.ChildGroup != null && EF.Functions.ILike(item.ChildGroup.Name, $"%{searchText}%")));
            }

            var totalCount = await staticQuery.CountAsync(ct);
            var desc = findFilter.Direction == SortDirection.Desc;
            staticQuery = (findFilter.Sort ?? "order") switch
            {
                "title" => desc
                    ? staticQuery.OrderByDescending(item => item.Title ?? item.Video!.Title ?? item.Image!.Title ?? item.ChildGroup!.Name).ThenByDescending(item => item.Id)
                    : staticQuery.OrderBy(item => item.Title ?? item.Video!.Title ?? item.Image!.Title ?? item.ChildGroup!.Name).ThenBy(item => item.Id),
                "kind" => desc
                    ? staticQuery.OrderByDescending(item => item.Kind).ThenByDescending(item => item.OrderIndex)
                    : staticQuery.OrderBy(item => item.Kind).ThenBy(item => item.OrderIndex),
                "created_at" => desc
                    ? staticQuery.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
                    : staticQuery.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id),
                _ => desc
                    ? staticQuery.OrderByDescending(item => item.OrderIndex).ThenByDescending(item => item.Id)
                    : staticQuery.OrderBy(item => item.OrderIndex).ThenBy(item => item.Id),
            };

            var safePage = Math.Max(1, findFilter.Page);
            var infinitePageSize = findFilter.PerPage <= 0;
            var safePerPage = infinitePageSize ? totalCount : Math.Clamp(findFilter.PerPage, 1, 1000);
            var itemsQuery = staticQuery.Skip(infinitePageSize ? 0 : (safePage - 1) * safePerPage);
            if (!infinitePageSize)
                itemsQuery = itemsQuery.Take(safePerPage);

            var items = await itemsQuery.ToListAsync(ct);

            return Ok(new PaginatedResponse<GroupItemDto>(items.Select(MapItem).ToList(), totalCount, safePage, infinitePageSize ? 0 : safePerPage));
        }

        if (dynamicGroups is null)
            return Ok(new PaginatedResponse<GroupItemDto>([], 0, Math.Max(1, page), Math.Clamp(perPage, 1, 1000)));

        return Ok(await dynamicGroups.ResolvePageDtosAsync(groupId, findFilter, forceRefresh: false, ct));
    }

    [HttpPost("items")]
    [RequiresPermission(Permissions.GroupsWrite)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsWrite, RouteValueName = "groupId")]
    public async Task<ActionResult<GroupItemDto>> Create(int groupId, [FromBody] GroupItemCreateDto dto, CancellationToken ct)
    {
        var group = await GetGroupForStaticItemWriteAsync(groupId, ct);
        if (group is null)
            return NotFound();
        if (group.Kind == GroupKind.Dynamic)
            return BadRequest("Dynamic groups resolve their items from a query. Snapshot the group before editing items.");

        var host = await ResolveCreateHostAsync(groupId, dto, ct);
        if (host.Error is not null)
            return BadRequest(host.Error);
        if (!GroupAllowsHost(group, host.HostType))
            return BadRequest($"This group does not allow {host.HostType} items.");

        var validationError = ValidateItemRange(dto.Kind, dto.StartSec, dto.EndSec);
        if (validationError is not null)
            return BadRequest(validationError);

        var siblings = await db.GroupItems
            .Where(item => item.GroupId == groupId)
            .OrderBy(item => item.OrderIndex)
            .ThenBy(item => item.Id)
            .ToListAsync(ct);
        var insertIndex = Math.Clamp(dto.OrderIndex, 0, siblings.Count);
        foreach (var sibling in siblings.Where(item => item.OrderIndex >= insertIndex))
            sibling.OrderIndex++;

        var item = new GroupItem
        {
            GroupId = groupId,
            OrderIndex = insertIndex,
            Kind = dto.Kind,
            HostType = host.HostType,
            HostId = host.HostId,
            VideoId = host.VideoId,
            ImageId = host.ImageId,
            ChildGroupId = host.ChildGroupId,
            StartSec = dto.Kind == GroupItemKind.Video ? null : dto.StartSec,
            EndSec = dto.Kind == GroupItemKind.Video ? null : dto.EndSec,
            Title = NormalizeOptionalText(dto.Title) ?? host.DisplayTitle,
            Notes = NormalizeOptionalText(dto.Notes),
            SourceSpanKey = NormalizeOptionalText(dto.SourceSpanKey),
            SourceProfileId = dto.SourceProfileId,
            SourceQueryJson = NormalizeOptionalText(dto.SourceQueryJson),
        };

        db.GroupItems.Add(item);
        await db.SaveChangesAsync(ct);
        PublishGroupUpdate(groupId);
        await LoadItemReferencesAsync(item, ct);

        return CreatedAtAction(nameof(List), new { groupId }, MapItem(item));
    }

    [HttpPut("items/{id:int}")]
    [RequiresPermission(Permissions.GroupsWrite)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsWrite, RouteValueName = "groupId")]
    public async Task<ActionResult<GroupItemDto>> Update(int groupId, int id, [FromBody] GroupItemUpdateDto dto, CancellationToken ct)
    {
        var group = await GetGroupForStaticItemWriteAsync(groupId, ct);
        if (group is null)
            return NotFound();
        if (group.Kind == GroupKind.Dynamic)
            return BadRequest("Dynamic groups resolve their items from a query. Snapshot the group before editing items.");

        var item = await db.GroupItems
            .Include(entry => entry.Video)
            .FirstOrDefaultAsync(entry => entry.GroupId == groupId && entry.Id == id, ct);
        if (item is null)
            return NotFound();

        var validationError = ValidateItemRange(dto.Kind, dto.StartSec, dto.EndSec);
        if (validationError is not null)
            return BadRequest(validationError);

        var siblings = await db.GroupItems
            .Where(entry => entry.GroupId == groupId && entry.Id != id)
            .OrderBy(entry => entry.OrderIndex)
            .ThenBy(entry => entry.Id)
            .ToListAsync(ct);
        ApplyOrder(siblings, item, dto.OrderIndex);

        item.Kind = dto.Kind;
        item.StartSec = dto.Kind == GroupItemKind.Video ? null : dto.StartSec;
        item.EndSec = dto.Kind == GroupItemKind.Video ? null : dto.EndSec;
        item.Title = NormalizeOptionalText(dto.Title);
        item.Notes = NormalizeOptionalText(dto.Notes);

        await db.SaveChangesAsync(ct);
        PublishGroupUpdate(groupId);
        return Ok(MapItem(item));
    }

    [HttpDelete("items/{id:int}")]
    [RequiresPermission(Permissions.GroupsWrite)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsWrite, RouteValueName = "groupId")]
    public async Task<IActionResult> Delete(int groupId, int id, CancellationToken ct)
    {
        var group = await GetGroupForStaticItemWriteAsync(groupId, ct);
        if (group is null)
            return NotFound();
        if (group.Kind == GroupKind.Dynamic)
            return BadRequest("Dynamic groups resolve their items from a query. Snapshot the group before editing items.");

        var item = await db.GroupItems.FirstOrDefaultAsync(entry => entry.GroupId == groupId && entry.Id == id, ct);
        if (item is null)
            return NotFound();

        db.GroupItems.Remove(item);
        await ReindexItemsAsync(groupId, [item.Id], ct);
        await db.SaveChangesAsync(ct);
        PublishGroupUpdate(groupId);
        return NoContent();
    }

    [HttpPost("items/remove-hosts")]
    [RequiresPermission(Permissions.GroupsWrite)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsWrite, RouteValueName = "groupId")]
    public async Task<IActionResult> RemoveHosts(int groupId, [FromBody] GroupItemsRemoveHostsDto dto, CancellationToken ct)
    {
        var group = await GetGroupForStaticItemWriteAsync(groupId, ct);
        if (group is null)
            return NotFound();
        if (group.Kind == GroupKind.Dynamic)
            return BadRequest("Dynamic groups resolve their items from a query. Snapshot the group before editing items.");

        var hostIds = dto.HostIds.Where(id => id > 0).Distinct().ToArray();
        if (hostIds.Length == 0)
            return Ok(new { removed = 0 });

        var hostType = NormalizeHostType(null, dto.Kind);
        var items = await db.GroupItems
            .Where(item => item.GroupId == groupId && item.Kind == dto.Kind && item.HostType.ToLower() == hostType && hostIds.Contains(item.HostId))
            .ToListAsync(ct);

        if (items.Count == 0)
            return Ok(new { removed = 0 });

        var removedItemIds = items.Select(item => item.Id).ToArray();
        db.GroupItems.RemoveRange(items);
        await ReindexItemsAsync(groupId, removedItemIds, ct);
        await db.SaveChangesAsync(ct);
        PublishGroupUpdate(groupId);
        return Ok(new { removed = items.Count });
    }

    [HttpPut("items/reorder")]
    [RequiresPermission(Permissions.GroupsWrite)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsWrite, RouteValueName = "groupId")]
    public async Task<IActionResult> Reorder(int groupId, [FromBody] GroupItemsReorderDto dto, CancellationToken ct)
    {
        var group = await GetGroupForStaticItemWriteAsync(groupId, ct);
        if (group is null)
            return NotFound();
        if (group.Kind == GroupKind.Dynamic)
            return BadRequest("Dynamic groups resolve their items from a query. Snapshot the group before editing items.");

        var items = await db.GroupItems
            .Where(item => item.GroupId == groupId)
            .OrderBy(item => item.OrderIndex)
            .ThenBy(item => item.Id)
            .ToListAsync(ct);
        if (dto.Ids.Count == 0)
            return BadRequest("Reorder payload must contain at least one group item.");

        var duplicateIds = dto.Ids.GroupBy(id => id).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        if (duplicateIds.Count > 0)
            return BadRequest("Reorder payload must not contain duplicate group items.");

        var expectedIds = items.Select(item => item.Id).ToHashSet();
        var actualIds = dto.Ids.OrderBy(id => id).ToList();
        if (actualIds.Any(id => !expectedIds.Contains(id)))
            return BadRequest("Reorder payload contains a group item that is not in this group.");

        var orderedItems = items.ToList();
        if (dto.Ids.Count != items.Count)
        {
            var movingIds = dto.Ids.ToHashSet();
            orderedItems = orderedItems.Where(item => !movingIds.Contains(item.Id)).ToList();
            var movingItems = dto.Ids.Select(id => items.First(item => item.Id == id)).ToList();
            var insertIndex = Math.Clamp(dto.StartIndex, 0, orderedItems.Count);
            orderedItems.InsertRange(insertIndex, movingItems);
        }
        else
        {
            orderedItems = dto.Ids.Select(id => items.First(item => item.Id == id)).ToList();
        }

        for (var index = 0; index < orderedItems.Count; index++)
            orderedItems[index].OrderIndex = index;

        await db.SaveChangesAsync(ct);
        if (items.Count > 0)
            PublishGroupUpdate(groupId);
        return Ok();
    }

    [HttpPost("items/from-spans")]
    [RequiresPermission(Permissions.GroupsWrite)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsWrite, RouteValueName = "groupId")]
    public async Task<ActionResult<IReadOnlyList<GroupItemDto>>> CreateFromSpans(int groupId, [FromBody] GroupItemsFromSpansDto dto, CancellationToken ct)
    {
        var group = await GetGroupForStaticItemWriteAsync(groupId, ct);
        if (group is null)
            return NotFound();
        if (group.Kind == GroupKind.Dynamic)
            return BadRequest("Dynamic groups resolve their items from a query. Snapshot the group before editing items.");
        if (!GroupAllowsHost(group, "video"))
            return BadRequest("This group does not allow video items.");
        if (dto.Spans.Count == 0)
            return Ok(Array.Empty<GroupItemDto>());

        var items = await db.GroupItems
            .Where(item => item.GroupId == groupId)
            .OrderBy(item => item.OrderIndex)
            .ThenBy(item => item.Id)
            .ToListAsync(ct);
        var nextOrderIndex = items.Count;
        var createdItems = new List<GroupItem>();

        foreach (var spanInput in dto.Spans)
        {
            if (spanInput.DerivedQuery is { } derivedQuery)
            {
                if (!spanInput.VideoId.HasValue)
                    return BadRequest("VideoId is required when snapshotting a derived query span.");

                var derivedSpans = await spanResolver.QueryVideoAsync(spanInput.VideoId.Value, new SegmentSpanQueryRequestDto(
                    spanInput.ProfileId,
                    derivedQuery.Operator,
                    derivedQuery.Operands,
                    derivedQuery.MergeGapSec,
                    derivedQuery.MinDurationSec), ct);

                var matchingSpans = !string.IsNullOrWhiteSpace(spanInput.SpanKey)
                    ? derivedSpans.Where(span => string.Equals(span.SpanKey, spanInput.SpanKey, StringComparison.Ordinal)).ToList()
                    : derivedSpans.ToList();

                if (matchingSpans.Count == 0)
                    return BadRequest($"Derived span '{spanInput.SpanKey ?? "<query>"}' was not found.");

                var sourceQueryJson = JsonSerializer.Serialize(derivedQuery);
                foreach (var span in matchingSpans)
                {
                    createdItems.Add(new GroupItem
                    {
                        GroupId = groupId,
                        OrderIndex = nextOrderIndex++,
                        Kind = GroupItemKind.VideoRange,
                        HostType = "video",
                        HostId = spanInput.VideoId.Value,
                        VideoId = spanInput.VideoId.Value,
                        StartSec = span.StartSec,
                        EndSec = span.EndSec,
                        Title = NormalizeOptionalText(spanInput.Title) ?? span.TagName ?? span.Kind,
                        SourceSpanKey = span.SpanKey,
                        SourceProfileId = spanInput.ProfileId,
                        SourceQueryJson = sourceQueryJson,
                        SnapshotAt = DateTime.UtcNow,
                    });
                }

                continue;
            }

            GroupItem item;
            if (!string.IsNullOrWhiteSpace(spanInput.SpanKey))
            {
                if (!spanInput.VideoId.HasValue)
                    return BadRequest("VideoId is required when snapshotting a resolved span.");

                var detail = await spanResolver.GetSpanDetailAsync(spanInput.VideoId.Value, spanInput.SpanKey, spanInput.ProfileId, ct);
                if (detail is null)
                    return BadRequest($"Resolved span '{spanInput.SpanKey}' was not found.");

                item = new GroupItem
                {
                    GroupId = groupId,
                    OrderIndex = nextOrderIndex++,
                    Kind = GroupItemKind.VideoRange,
                    HostType = "video",
                    HostId = detail.VideoId,
                    VideoId = detail.VideoId,
                    StartSec = detail.Span.StartSec,
                    EndSec = detail.Span.EndSec,
                    Title = NormalizeOptionalText(spanInput.Title) ?? detail.Span.TagName ?? detail.Span.Kind ?? detail.VideoTitle,
                    SourceSpanKey = detail.Span.SpanKey,
                    SourceProfileId = detail.ProfileId,
                    SourceQueryJson = null,
                    SnapshotAt = DateTime.UtcNow,
                };
            }
            else
            {
                if (!spanInput.VideoId.HasValue)
                    return BadRequest("VideoId is required when creating a group item from manual span input.");
                if (!await VideoExistsAsync(spanInput.VideoId.Value, ct))
                    return BadRequest("Video was not found.");

                var kind = spanInput.StartSec.HasValue || spanInput.EndSec.HasValue ? GroupItemKind.VideoRange : GroupItemKind.Video;
                var validationError = ValidateItemRange(kind, spanInput.StartSec, spanInput.EndSec);
                if (validationError is not null)
                    return BadRequest(validationError);

                item = new GroupItem
                {
                    GroupId = groupId,
                    OrderIndex = nextOrderIndex++,
                    Kind = kind,
                    HostType = "video",
                    HostId = spanInput.VideoId.Value,
                    VideoId = spanInput.VideoId.Value,
                    StartSec = kind == GroupItemKind.Video ? null : spanInput.StartSec,
                    EndSec = kind == GroupItemKind.Video ? null : spanInput.EndSec,
                    Title = NormalizeOptionalText(spanInput.Title),
                    SourceProfileId = spanInput.ProfileId,
                    SourceQueryJson = null,
                    SnapshotAt = DateTime.UtcNow,
                };
            }

            createdItems.Add(item);
        }

        db.GroupItems.AddRange(createdItems);
        await db.SaveChangesAsync(ct);
        if (createdItems.Count > 0)
            PublishGroupUpdate(groupId);

        foreach (var item in createdItems)
            await LoadItemReferencesAsync(item, ct);

        return Ok(createdItems.Select(MapItem).ToList());
    }

    private void PublishGroupUpdate(int groupId)
        => eventBus?.Publish(new EntityEvent(EventType.GroupUpdated, "Group", groupId));

    [HttpGet("playback-manifest")]
    [AllowShareLinkAccess]
    [RequiresPermission(Permissions.StreamRead)]
    public async Task<ActionResult<GroupPlaybackManifestDto>> GetPlaybackManifest(int groupId, CancellationToken ct)
    {
        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(item => item.Id == groupId, ct);
        if (group is null)
            return NotFound();

        if (group.Kind == GroupKind.Dynamic && dynamicGroups is not null)
        {
            var resolved = await dynamicGroups.ResolveAsync(groupId, forceRefresh: false, ct);
            var playable = resolved
                .Where(IsPlayableManifestItem)
                .ToList();
            var segmentIds = playable.Where(IsSegmentManifestItem).Select(item => item.HostId).Distinct().ToArray();
            var segments = await db.VisibleSegments().AsNoTracking()
                .Include(segment => segment.Tag)
                .Where(segment => segmentIds.Contains(segment.Id))
                .ToDictionaryAsync(segment => segment.Id, ct);
            var videoIds = playable.Select(ResolveVideoId)
                .Concat(segments.Values.Where(segment => segment.HostType == SegmentHostType.Video).Select(segment => (int?)segment.HostId))
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .ToArray();
            var audioIds = playable.Where(IsAudioManifestItem).Select(item => item.HostId)
                .Concat(segments.Values.Where(segment => segment.HostType == SegmentHostType.Audio).Select(segment => segment.HostId))
                .Distinct()
                .ToArray();
            var imageIds = playable.Select(ResolveImageId)
                .Concat(segments.Values.Where(segment => segment.HostType == SegmentHostType.Image).Select(segment => (int?)segment.HostId))
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .ToArray();
            var textIds = playable.Where(IsTextManifestItem).Select(item => item.HostId).Distinct().ToArray();
            var videos = await db.Videos.AsNoTracking()
                .Where(video => videoIds.Contains(video.Id))
                .ToDictionaryAsync(video => video.Id, ct);
            var audios = await db.Audios.AsNoTracking()
                .Include(audio => audio.Files)
                .Where(audio => audioIds.Contains(audio.Id))
                .ToDictionaryAsync(audio => audio.Id, ct);
            var images = await db.Images.AsNoTracking()
                .Include(image => image.Files)
                .Where(image => imageIds.Contains(image.Id))
                .ToDictionaryAsync(image => image.Id, ct);
            var texts = await db.TextDocuments.AsNoTracking()
                .Include(text => text.Files)
                .Where(text => textIds.Contains(text.Id))
                .ToDictionaryAsync(text => text.Id, ct);

            var dynamicManifest = playable
                .Select((item, index) => BuildManifestItem(item, -(index + 1), videos, audios, images, texts, segments))
                .Where(item => item is not null)
                .Select(item => item!)
                .ToList();

            return Ok(new GroupPlaybackManifestDto(dynamicManifest));
        }

        var items = (await db.GroupItems.AsNoTracking()
            .Where(item => item.GroupId == groupId)
            .OrderBy(item => item.OrderIndex)
            .ThenBy(item => item.Id)
            .ToListAsync(ct))
            .Where(IsPlayableManifestItem)
            .ToList();

        var staticSegmentIds = items.Where(IsSegmentManifestItem).Select(item => item.HostId).Distinct().ToArray();
        var staticSegments = await db.VisibleSegments().AsNoTracking()
            .Include(segment => segment.Tag)
            .Where(segment => staticSegmentIds.Contains(segment.Id))
            .ToDictionaryAsync(segment => segment.Id, ct);
        var staticVideoIds = items.Select(ResolveVideoId)
            .Concat(staticSegments.Values.Where(segment => segment.HostType == SegmentHostType.Video).Select(segment => (int?)segment.HostId))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        var staticAudioIds = items.Where(IsAudioManifestItem).Select(item => item.HostId)
            .Concat(staticSegments.Values.Where(segment => segment.HostType == SegmentHostType.Audio).Select(segment => segment.HostId))
            .Distinct()
            .ToArray();
        var staticImageIds = items.Select(ResolveImageId)
            .Concat(staticSegments.Values.Where(segment => segment.HostType == SegmentHostType.Image).Select(segment => (int?)segment.HostId))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        var staticTextIds = items.Where(IsTextManifestItem).Select(item => item.HostId).Distinct().ToArray();
        var staticVideos = await db.Videos.AsNoTracking()
            .Where(video => staticVideoIds.Contains(video.Id))
            .ToDictionaryAsync(video => video.Id, ct);
        var staticAudios = await db.Audios.AsNoTracking()
            .Include(audio => audio.Files)
            .Where(audio => staticAudioIds.Contains(audio.Id))
            .ToDictionaryAsync(audio => audio.Id, ct);
        var staticImages = await db.Images.AsNoTracking()
            .Include(image => image.Files)
            .Where(image => staticImageIds.Contains(image.Id))
            .ToDictionaryAsync(image => image.Id, ct);
        var staticTexts = await db.TextDocuments.AsNoTracking()
            .Include(text => text.Files)
            .Where(text => staticTextIds.Contains(text.Id))
            .ToDictionaryAsync(text => text.Id, ct);

        var manifest = items
            .Select(item => BuildManifestItem(item, item.Id, staticVideos, staticAudios, staticImages, staticTexts, staticSegments))
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();

        return Ok(new GroupPlaybackManifestDto(manifest));
    }

    private static bool IsPlayableManifestItem(GroupItem item)
        => IsVideoManifestItem(item) || IsAudioManifestItem(item) || IsImageManifestItem(item) || IsTextManifestItem(item) || IsSegmentManifestItem(item);

    private static bool IsPlayableManifestItem(DynamicGroupResolvedItem item)
        => IsVideoManifestItem(item) || IsAudioManifestItem(item) || IsImageManifestItem(item) || IsTextManifestItem(item) || IsSegmentManifestItem(item);

    private static bool IsVideoManifestItem(DynamicGroupResolvedItem item)
        => (item.Kind == GroupItemKind.Video || item.Kind == GroupItemKind.VideoRange)
            && (item.VideoId.HasValue || (string.Equals(item.HostType, "video", StringComparison.OrdinalIgnoreCase) && item.HostId > 0));

    private static bool IsAudioManifestItem(DynamicGroupResolvedItem item)
        => (item.Kind == GroupItemKind.Audio || string.Equals(item.HostType, "audio", StringComparison.OrdinalIgnoreCase))
            && item.HostId > 0;

    private static bool IsImageManifestItem(DynamicGroupResolvedItem item)
        => (item.Kind == GroupItemKind.Image || string.Equals(item.HostType, "image", StringComparison.OrdinalIgnoreCase))
            && (item.ImageId.HasValue || item.HostId > 0);

    private static bool IsTextManifestItem(DynamicGroupResolvedItem item)
        => (item.Kind == GroupItemKind.Text || string.Equals(item.HostType, "text", StringComparison.OrdinalIgnoreCase))
            && item.HostId > 0;

    private static bool IsSegmentManifestItem(DynamicGroupResolvedItem item)
        => (item.Kind == GroupItemKind.Segment || string.Equals(item.HostType, "segment", StringComparison.OrdinalIgnoreCase))
            && item.HostId > 0;

    private static int? ResolveVideoId(DynamicGroupResolvedItem item)
        => item.VideoId ?? (string.Equals(item.HostType, "video", StringComparison.OrdinalIgnoreCase) && item.HostId > 0 ? item.HostId : null);

    private static int? ResolveImageId(DynamicGroupResolvedItem item)
        => item.ImageId ?? (string.Equals(item.HostType, "image", StringComparison.OrdinalIgnoreCase) && item.HostId > 0 ? item.HostId : null);

    private static bool IsVideoManifestItem(GroupItem item)
        => (item.Kind == GroupItemKind.Video || item.Kind == GroupItemKind.VideoRange)
            && (item.VideoId.HasValue || (string.Equals(item.HostType, "video", StringComparison.OrdinalIgnoreCase) && item.HostId > 0));

    private static bool IsAudioManifestItem(GroupItem item)
        => (item.Kind == GroupItemKind.Audio || string.Equals(item.HostType, "audio", StringComparison.OrdinalIgnoreCase))
            && item.HostId > 0;

    private static bool IsImageManifestItem(GroupItem item)
        => (item.Kind == GroupItemKind.Image || string.Equals(item.HostType, "image", StringComparison.OrdinalIgnoreCase))
            && (item.ImageId.HasValue || item.HostId > 0);

    private static bool IsTextManifestItem(GroupItem item)
        => (item.Kind == GroupItemKind.Text || string.Equals(item.HostType, "text", StringComparison.OrdinalIgnoreCase))
            && item.HostId > 0;

    private static bool IsSegmentManifestItem(GroupItem item)
        => (item.Kind == GroupItemKind.Segment || string.Equals(item.HostType, "segment", StringComparison.OrdinalIgnoreCase))
            && item.HostId > 0;

    private static int? ResolveVideoId(GroupItem item)
        => item.VideoId ?? (string.Equals(item.HostType, "video", StringComparison.OrdinalIgnoreCase) && item.HostId > 0 ? item.HostId : null);

    private static int? ResolveImageId(GroupItem item)
        => item.ImageId ?? (string.Equals(item.HostType, "image", StringComparison.OrdinalIgnoreCase) && item.HostId > 0 ? item.HostId : null);

    private static GroupPlaybackManifestItemDto? BuildManifestItem(
        GroupItem item,
        int manifestItemId,
        IReadOnlyDictionary<int, Video> videos,
        IReadOnlyDictionary<int, Audio> audios,
        IReadOnlyDictionary<int, Image> images,
        IReadOnlyDictionary<int, TextDocument> texts,
        IReadOnlyDictionary<int, Segment> segments)
    {
        if (IsVideoManifestItem(item))
        {
            var videoId = ResolveVideoId(item);
            if (!videoId.HasValue)
                return null;

            videos.TryGetValue(videoId.Value, out var video);
            var startSec = item.Kind == GroupItemKind.VideoRange ? item.StartSec ?? 0 : 0;
            var endSec = item.Kind == GroupItemKind.VideoRange ? item.EndSec : null;
            return BuildVideoManifestItem(manifestItemId, "video", videoId.Value, videoId.Value, video, startSec, endSec, item.Title ?? video?.Title);
        }

        if (IsAudioManifestItem(item) && audios.TryGetValue(item.HostId, out var audio))
        {
            return BuildAudioManifestItem(manifestItemId, "audio", audio.Id, audio, item.StartSec ?? 0, item.EndSec, item.Title);
        }

        if (IsImageManifestItem(item))
        {
            var imageId = ResolveImageId(item);
            if (imageId.HasValue && images.TryGetValue(imageId.Value, out var image))
                return BuildImageManifestItem(manifestItemId, "image", image.Id, image, item.Title);
        }

        if (IsTextManifestItem(item) && texts.TryGetValue(item.HostId, out var text))
        {
            return BuildTextManifestItem(manifestItemId, "text", text.Id, text, item.Title);
        }

        if (IsSegmentManifestItem(item) && segments.TryGetValue(item.HostId, out var segment))
        {
            return BuildSegmentManifestItem(manifestItemId, segment, item.Title, videos, audios, images);
        }

        return null;
    }

    private static GroupPlaybackManifestItemDto? BuildManifestItem(
        DynamicGroupResolvedItem item,
        int manifestItemId,
        IReadOnlyDictionary<int, Video> videos,
        IReadOnlyDictionary<int, Audio> audios,
        IReadOnlyDictionary<int, Image> images,
        IReadOnlyDictionary<int, TextDocument> texts,
        IReadOnlyDictionary<int, Segment> segments)
    {
        if (IsVideoManifestItem(item))
        {
            var videoId = ResolveVideoId(item);
            if (!videoId.HasValue)
                return null;

            videos.TryGetValue(videoId.Value, out var video);
            var startSec = item.Kind == GroupItemKind.VideoRange ? item.StartSec ?? 0 : 0;
            var endSec = item.Kind == GroupItemKind.VideoRange ? item.EndSec : null;
            return BuildVideoManifestItem(manifestItemId, "video", videoId.Value, videoId.Value, video, startSec, endSec, item.Title ?? video?.Title);
        }

        if (IsAudioManifestItem(item) && audios.TryGetValue(item.HostId, out var audio))
        {
            return BuildAudioManifestItem(manifestItemId, "audio", audio.Id, audio, item.StartSec ?? 0, item.EndSec, item.Title);
        }

        if (IsImageManifestItem(item))
        {
            var imageId = ResolveImageId(item);
            if (imageId.HasValue && images.TryGetValue(imageId.Value, out var image))
                return BuildImageManifestItem(manifestItemId, "image", image.Id, image, item.Title);
        }

        if (IsTextManifestItem(item) && texts.TryGetValue(item.HostId, out var text))
        {
            return BuildTextManifestItem(manifestItemId, "text", text.Id, text, item.Title);
        }

        if (IsSegmentManifestItem(item) && segments.TryGetValue(item.HostId, out var segment))
        {
            return BuildSegmentManifestItem(manifestItemId, segment, item.Title, videos, audios, images);
        }

        return null;
    }

    private static GroupPlaybackManifestItemDto BuildVideoManifestItem(
        int manifestItemId,
        string hostType,
        int hostId,
        int videoId,
        Video? video,
        double startSec,
        double? endSec,
        string? title,
        int? segmentId = null)
    {
        double? durationSec = endSec.HasValue
            ? Math.Max(0, endSec.Value - startSec)
            : video?.MaxDuration > 0
                ? video.MaxDuration
                : null;

        return new GroupPlaybackManifestItemDto(
            GroupItemId: manifestItemId,
            HostType: hostType,
            HostId: hostId,
            VideoId: videoId,
            AudioId: null,
            ImageId: null,
            TextId: null,
            SegmentId: segmentId,
            VideoTitle: video?.Title,
            Src: $"/api/stream/video/{videoId}",
            StartSec: startSec,
            EndSec: endSec,
            DurationSec: durationSec,
            DisplayDurationSec: null,
            PosterPath: $"/api/stream/video/{videoId}/screenshot",
            Title: title,
            Format: null,
            HasVideoTrack: false);
    }

    private static GroupPlaybackManifestItemDto BuildAudioManifestItem(
        int manifestItemId,
        string hostType,
        int hostId,
        Audio audio,
        double startSec,
        double? endSec,
        string? title,
        int? segmentId = null)
    {
        var file = audio.Files
            .OrderByDescending(file => file.Duration)
            .ThenBy(file => file.Id)
            .FirstOrDefault();
        double? duration = endSec.HasValue
            ? Math.Max(0, endSec.Value - startSec)
            : file?.Duration > 0
                ? file.Duration
                : audio.MaxDuration > 0
                    ? audio.MaxDuration
                    : null;

        return new GroupPlaybackManifestItemDto(
            GroupItemId: manifestItemId,
            HostType: hostType,
            HostId: hostId,
            VideoId: null,
            AudioId: audio.Id,
            ImageId: null,
            TextId: null,
            SegmentId: segmentId,
            VideoTitle: null,
            Src: $"/api/audios/{audio.Id}/stream",
            StartSec: startSec,
            EndSec: endSec,
            DurationSec: duration,
            DisplayDurationSec: null,
            PosterPath: null,
            Title: title ?? audio.Title ?? audio.MinPath ?? $"Audio {audio.Id}",
            Format: file?.Format,
            HasVideoTrack: file?.HasVideoTrack ?? audio.HasVideoFiles);
    }

    private static GroupPlaybackManifestItemDto BuildImageManifestItem(
        int manifestItemId,
        string hostType,
        int hostId,
        Image image,
        string? title,
        int? segmentId = null)
    {
        var file = image.Files
            .OrderByDescending(file => (long)file.Width * file.Height)
            .ThenBy(file => file.Id)
            .FirstOrDefault();

        return new GroupPlaybackManifestItemDto(
            GroupItemId: manifestItemId,
            HostType: hostType,
            HostId: hostId,
            VideoId: null,
            AudioId: null,
            ImageId: image.Id,
            TextId: null,
            SegmentId: segmentId,
            VideoTitle: null,
            Src: $"/api/stream/image/{image.Id}",
            StartSec: 0,
            EndSec: null,
            DurationSec: null,
            DisplayDurationSec: null,
            PosterPath: $"/api/stream/image/{image.Id}/thumbnail",
            Title: title ?? image.Title ?? image.MinPath ?? $"Image {image.Id}",
            Format: file?.Format,
            HasVideoTrack: false);
    }

    private static GroupPlaybackManifestItemDto BuildTextManifestItem(
        int manifestItemId,
        string hostType,
        int hostId,
        TextDocument text,
        string? title)
    {
        var file = text.Files
            .OrderByDescending(file => file.WordCount ?? 0)
            .ThenBy(file => file.Id)
            .FirstOrDefault();

        return new GroupPlaybackManifestItemDto(
            GroupItemId: manifestItemId,
            HostType: hostType,
            HostId: hostId,
            VideoId: null,
            AudioId: null,
            ImageId: null,
            TextId: text.Id,
            SegmentId: null,
            VideoTitle: null,
            Src: $"/api/texts/{text.Id}/file",
            StartSec: 0,
            EndSec: null,
            DurationSec: null,
            DisplayDurationSec: null,
            PosterPath: text.ImageBlobId is null ? null : $"/api/texts/{text.Id}/image",
            Title: title ?? text.Title ?? text.MinPath ?? $"Text {text.Id}",
            Format: file?.Format,
            HasVideoTrack: false);
    }

    private static GroupPlaybackManifestItemDto? BuildSegmentManifestItem(
        int manifestItemId,
        Segment segment,
        string? title,
        IReadOnlyDictionary<int, Video> videos,
        IReadOnlyDictionary<int, Audio> audios,
        IReadOnlyDictionary<int, Image> images)
    {
        var segmentTitle = title ?? SegmentTitle(segment);
        return segment.HostType switch
        {
            SegmentHostType.Video when videos.TryGetValue(segment.HostId, out var video) => BuildVideoManifestItem(
                manifestItemId,
                "segment",
                segment.Id,
                segment.HostId,
                video,
                segment.StartSec,
                segment.EndSec,
                segmentTitle,
                segment.Id),
            SegmentHostType.Audio when audios.TryGetValue(segment.HostId, out var audio) => BuildAudioManifestItem(
                manifestItemId,
                "segment",
                segment.Id,
                audio,
                segment.StartSec,
                segment.EndSec,
                segmentTitle,
                segment.Id),
            SegmentHostType.Image when images.TryGetValue(segment.HostId, out var image) => BuildImageManifestItem(
                manifestItemId,
                "segment",
                segment.Id,
                image,
                segmentTitle,
                segment.Id),
            _ => null,
        };
    }

    private static string SegmentTitle(Segment segment)
        => segment.Title ?? segment.Tag?.Name ?? segment.Kind ?? $"Segment {segment.Id}";

    private Task<bool> GroupExistsAsync(int groupId, CancellationToken ct)
        => db.Groups.AsNoTracking().AnyAsync(group => group.Id == groupId, ct);

    private Task<Group?> GetGroupForStaticItemWriteAsync(int groupId, CancellationToken ct)
        => db.Groups.AsNoTracking().FirstOrDefaultAsync(group => group.Id == groupId, ct);

    private Task<bool> VideoExistsAsync(int videoId, CancellationToken ct)
        => db.Videos.AsNoTracking().AnyAsync(video => video.Id == videoId, ct);

    private Task<bool> ImageExistsAsync(int imageId, CancellationToken ct)
        => db.Images.AsNoTracking().AnyAsync(image => image.Id == imageId, ct);

    private Task<string?> PerformerNameAsync(int performerId, CancellationToken ct)
        => db.Performers.AsNoTracking()
            .Where(performer => performer.Id == performerId)
            .Select(performer => performer.Name)
            .FirstOrDefaultAsync(ct);

    private Task<string?> StudioNameAsync(int studioId, CancellationToken ct)
        => db.Studios.AsNoTracking()
            .Where(studio => studio.Id == studioId)
            .Select(studio => studio.Name)
            .FirstOrDefaultAsync(ct);

    private Task<string?> TagNameAsync(int tagId, CancellationToken ct)
        => db.Tags.AsNoTracking()
            .Where(tag => tag.Id == tagId)
            .Select(tag => tag.Name)
            .FirstOrDefaultAsync(ct);

    private Task<string?> FaceLabelAsync(int faceId, CancellationToken ct)
        => db.Faces.AsNoTracking()
            .Where(face => face.Id == faceId)
            .Select(face => face.Label ?? $"Face {face.Id}")
            .FirstOrDefaultAsync(ct);

    private Task<string?> SegmentTitleAsync(int segmentId, CancellationToken ct)
        => db.VisibleSegments().AsNoTracking()
            .Where(segment => segment.Id == segmentId)
            .Select(segment => segment.Title ?? (segment.Tag != null ? segment.Tag.Name : null) ?? segment.Kind ?? $"Segment {segment.Id}")
            .FirstOrDefaultAsync(ct);

    private Task<string?> GalleryTitleAsync(int galleryId, CancellationToken ct)
        => db.Galleries.AsNoTracking()
            .Where(gallery => gallery.Id == galleryId)
            .Select(gallery => gallery.Title ?? $"Gallery {gallery.Id}")
            .FirstOrDefaultAsync(ct);

    private Task<string?> AudioTitleAsync(int audioId, CancellationToken ct)
        => db.Audios.AsNoTracking()
            .Where(audio => audio.Id == audioId)
            .Select(audio => audio.Title)
            .FirstOrDefaultAsync(ct);

    private Task<string?> TextTitleAsync(int textDocumentId, CancellationToken ct)
        => db.TextDocuments.AsNoTracking()
            .Where(text => text.Id == textDocumentId)
            .Select(text => text.Title)
            .FirstOrDefaultAsync(ct);

    private async Task LoadItemReferencesAsync(GroupItem item, CancellationToken ct)
    {
        if (item.VideoId.HasValue)
        {
            await db.Entry(item).Reference(entry => entry.Video).LoadAsync(ct);
            if (item.Video is not null)
                await db.Entry(item.Video).Collection(video => video.Files).LoadAsync(ct);
        }
        if (item.ImageId.HasValue)
            await db.Entry(item).Reference(entry => entry.Image).LoadAsync(ct);
        if (item.ChildGroupId.HasValue)
            await db.Entry(item).Reference(entry => entry.ChildGroup).LoadAsync(ct);
    }

    private async Task<GroupItemHostResolution> ResolveCreateHostAsync(int groupId, GroupItemCreateDto dto, CancellationToken ct)
    {
        var hostType = NormalizeHostType(dto.HostType, dto.Kind);
        var hostId = dto.HostId ?? dto.VideoId;
        if (!hostId.HasValue)
            return GroupItemHostResolution.Fail("Group item host id is required.");

        if (hostType == "video")
        {
            if (!await VideoExistsAsync(hostId.Value, ct))
                return GroupItemHostResolution.Fail("Video was not found.");
            return new GroupItemHostResolution(hostType, hostId.Value, hostId.Value, null, null, null, null);
        }

        if (hostType == "image")
        {
            if (!await ImageExistsAsync(hostId.Value, ct))
                return GroupItemHostResolution.Fail("Image was not found.");
            return new GroupItemHostResolution(hostType, hostId.Value, null, hostId.Value, null, null, null);
        }

        if (hostType == "gallery")
        {
            var title = await GalleryTitleAsync(hostId.Value, ct);
            if (title is null)
                return GroupItemHostResolution.Fail("Gallery was not found.");
            return new GroupItemHostResolution(hostType, hostId.Value, null, null, null, NormalizeOptionalText(title), null);
        }

        if (hostType == "audio")
        {
            var title = await AudioTitleAsync(hostId.Value, ct);
            if (title is null)
                return GroupItemHostResolution.Fail("Audio was not found.");
            return new GroupItemHostResolution(hostType, hostId.Value, null, null, null, NormalizeOptionalText(title), null);
        }

        if (hostType == "text")
        {
            var title = await TextTitleAsync(hostId.Value, ct);
            if (title is null)
                return GroupItemHostResolution.Fail("Text document was not found.");
            return new GroupItemHostResolution(hostType, hostId.Value, null, null, null, NormalizeOptionalText(title), null);
        }

        if (hostType == "performer")
        {
            var name = await PerformerNameAsync(hostId.Value, ct);
            if (name is null)
                return GroupItemHostResolution.Fail("Performer was not found.");
            return new GroupItemHostResolution(hostType, hostId.Value, null, null, null, NormalizeOptionalText(name), null);
        }

        if (hostType == "studio")
        {
            var name = await StudioNameAsync(hostId.Value, ct);
            if (name is null)
                return GroupItemHostResolution.Fail("Studio was not found.");
            return new GroupItemHostResolution(hostType, hostId.Value, null, null, null, NormalizeOptionalText(name), null);
        }

        if (hostType == "tag")
        {
            var name = await TagNameAsync(hostId.Value, ct);
            if (name is null)
                return GroupItemHostResolution.Fail("Tag was not found.");
            return new GroupItemHostResolution(hostType, hostId.Value, null, null, null, NormalizeOptionalText(name), null);
        }

        if (hostType == "face")
        {
            var label = await FaceLabelAsync(hostId.Value, ct);
            if (label is null)
                return GroupItemHostResolution.Fail("Face was not found.");
            return new GroupItemHostResolution(hostType, hostId.Value, null, null, null, NormalizeOptionalText(label), null);
        }

        if (hostType == "segment")
        {
            var title = await SegmentTitleAsync(hostId.Value, ct);
            if (title is null)
                return GroupItemHostResolution.Fail("Segment was not found.");
            return new GroupItemHostResolution(hostType, hostId.Value, null, null, null, NormalizeOptionalText(title), null);
        }

        if (hostType == "group")
        {
            if (hostId.Value == groupId)
                return GroupItemHostResolution.Fail("A group item cannot point to its containing group.");
            if (!await GroupExistsAsync(hostId.Value, ct))
                return GroupItemHostResolution.Fail("Child group was not found.");
            return new GroupItemHostResolution(hostType, hostId.Value, null, null, hostId.Value, null, null);
        }

        return GroupItemHostResolution.Fail($"Group items do not support host type '{hostType}'.");
    }

    private async Task ReindexItemsAsync(int groupId, int[] excludedItemIds, CancellationToken ct)
    {
        var query = db.GroupItems.Where(item => item.GroupId == groupId);
        if (excludedItemIds.Length > 0)
            query = query.Where(item => !excludedItemIds.Contains(item.Id));

        var items = await query
            .OrderBy(item => item.OrderIndex)
            .ThenBy(item => item.Id)
            .ToListAsync(ct);

        for (var index = 0; index < items.Count; index++)
            items[index].OrderIndex = index;
    }

    private static void ApplyOrder(List<GroupItem> siblings, GroupItem item, int desiredIndex)
    {
        var ordered = siblings.OrderBy(entry => entry.OrderIndex).ThenBy(entry => entry.Id).ToList();
        var insertIndex = Math.Clamp(desiredIndex, 0, ordered.Count);
        ordered.Insert(insertIndex, item);
        for (var index = 0; index < ordered.Count; index++)
            ordered[index].OrderIndex = index;
    }

    private static string? ValidateItemRange(GroupItemKind kind, double? startSec, double? endSec)
    {
        if (kind != GroupItemKind.VideoRange)
            return null;

        if (!startSec.HasValue || !endSec.HasValue)
            return "Video range items require both StartSec and EndSec.";
        if (endSec.Value < startSec.Value)
            return "Group item end must be greater than or equal to the start.";
        return null;
    }

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? VideoTitle(Video? video)
        => !string.IsNullOrWhiteSpace(video?.Title)
            ? video.Title
            : video?.Files.OrderBy(file => file.Id).FirstOrDefault()?.Basename;

    private static string NormalizeHostType(string? hostType, GroupItemKind kind)
    {
        if (!string.IsNullOrWhiteSpace(hostType))
            return hostType.Trim().ToLowerInvariant();

        return kind switch
        {
            GroupItemKind.Image => "image",
            GroupItemKind.Gallery => "gallery",
            GroupItemKind.Group => "group",
            GroupItemKind.Audio => "audio",
            GroupItemKind.Text => "text",
            GroupItemKind.Performer => "performer",
            GroupItemKind.Studio => "studio",
            GroupItemKind.Tag => "tag",
            GroupItemKind.Face => "face",
            GroupItemKind.Segment => "segment",
            _ => "video",
        };
    }

    private static bool GroupAllowsHost(Group group, string hostType)
    {
        if (group.AllowedHostTypes.Count == 0)
            return string.Equals(hostType, "video", StringComparison.OrdinalIgnoreCase);

        return group.AllowedHostTypes.Contains(hostType, StringComparer.OrdinalIgnoreCase);
    }

    private static GroupItemDto MapItem(GroupItem item) => new(
        item.Id,
        item.GroupId,
        item.OrderIndex,
        item.Kind,
        item.VideoId,
        VideoTitle(item.Video),
        item.HostType,
        item.HostId,
        item.ImageId,
        item.Image?.Title,
        item.ChildGroupId,
        item.ChildGroup?.Name,
        item.StartSec,
        item.EndSec,
        item.Title,
        item.Notes,
        item.SourceSpanKey,
        item.SourceProfileId,
        item.SourceQueryJson,
        item.SnapshotAt?.ToString("o"),
        item.CreatedAt.ToString("o"),
        item.UpdatedAt.ToString("o"));

    private sealed record GroupItemHostResolution(
        string HostType,
        int HostId,
        int? VideoId,
        int? ImageId,
        int? ChildGroupId,
        string? DisplayTitle,
        string? Error)
    {
        public static GroupItemHostResolution Fail(string error) => new(string.Empty, 0, null, null, null, null, error);
    }
}
