using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Common;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Events;
using Cove.Core.Interfaces;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[RequiresPermission(Permissions.GroupsRead)]
public class GroupsController(IGroupRepository groupRepo, Data.CoveContext db, IUserEngagementService engagementService, CustomFieldService? customFields = null, DynamicGroupResolver? dynamicGroups = null, ICurrentPrincipalAccessor? principalAccessor = null, IFieldProvenanceService? fieldProvenanceService = null, IEventBus? eventBus = null) : ControllerBase
{
    private static readonly string[] DefaultAllowedHostTypes = ["video", "image", "audio", "text", "group", "performer", "studio", "tag", "gallery", "face", "segment"];
    private readonly CustomFieldService _customFields = customFields ?? new CustomFieldService(db);

    [HttpGet]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<PaginatedResponse<GroupDto>>> Find(
        [FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int perPage = 25,
        [FromQuery] string? sort = null, [FromQuery] string? direction = null,
        [FromQuery] int? seed = null,
        [FromQuery] string? name = null, [FromQuery] int? rating = null,
        [FromQuery] int? studioId = null, [FromQuery] string? tagIds = null,
        [FromQuery] string? kind = null,
        CancellationToken ct = default)
    {
        var filter = new GroupFilter
        {
            Name = name,
            Rating = rating,
            StudioId = studioId,
            TagIds = QueryParsing.ParseIntList(tagIds)?.ToList(),
            KindCriterion = string.IsNullOrWhiteSpace(kind) ? null : new StringCriterion { Value = kind.Trim(), Modifier = CriterionModifier.Equals },
        };
        var findFilter = new FindFilter
        {
            Q = q, Page = page, PerPage = perPage, Sort = sort,
            Direction = direction == "desc" ? SortDirection.Desc : SortDirection.Asc,
            Seed = seed,
        };

        var (items, totalCount) = await groupRepo.FindAsync(filter, findFilter, ct);
        var customFieldValues = await _customFields.GetValuesAsync(CustomFieldEntityTypes.Group, items.Select(item => item.Id), ct);
        var dynamicCounts = await GetDynamicCountsAsync(items, ct);
        var dtos = items.Select(group => MapToDto(group, GetCustomFields(customFieldValues, group.Id), dynamicCounts.GetValueOrDefault(group.Id))).ToList();
        return Ok(new PaginatedResponse<GroupDto>(dtos, totalCount, page, perPage));
    }

    [HttpPost("find")]
    public async Task<ActionResult<PaginatedResponse<GroupDto>>> FindPost([FromBody] FilteredQueryRequest<GroupFilter> req, CancellationToken ct)
    {
        var findFilter = req.FindFilter ?? new FindFilter();
        var filter = req.ObjectFilter ?? new GroupFilter();
        var (items, totalCount) = await groupRepo.FindAsync(filter, findFilter, ct);
        var customFieldValues = await _customFields.GetValuesAsync(CustomFieldEntityTypes.Group, items.Select(item => item.Id), ct);
        var dynamicCounts = await GetDynamicCountsAsync(items, ct);
        var dtos = items.Select(group => MapToDto(group, GetCustomFields(customFieldValues, group.Id), dynamicCounts.GetValueOrDefault(group.Id))).ToList();
        return Ok(new PaginatedResponse<GroupDto>(dtos, totalCount, findFilter.Page, findFilter.PerPage));
    }

    [HttpGet("{id:int}")]
    [AllowShareLinkAccess]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<GroupDto>> GetById(int id, CancellationToken ct)
    {
        var group = await groupRepo.GetByIdWithRelationsAsync(id, ct);
        if (group == null) return NotFound();
        return Ok(await MapToDetailDtoAsync(group, ct));
    }

    [HttpPost]
    [RequiresPermission(Permissions.GroupsWrite)]
    public async Task<ActionResult<GroupDto>> Create([FromBody] GroupCreateDto dto, CancellationToken ct)
    {
        var group = new Group
        {
            Name = dto.Name, Aliases = dto.Aliases,
            Date = ParseDate(dto.Date), StudioId = dto.StudioId,
            Director = dto.Director, Synopsis = dto.Description,
            Kind = dto.Kind ?? GroupKind.Static,
            QuerySourceKey = dto.Kind == GroupKind.Dynamic ? NormalizeOptionalText(dto.QuerySourceKey) : null,
            QueryJson = dto.Kind == GroupKind.Dynamic ? NormalizeOptionalText(dto.QueryJson) : null,
            ShowInVideoLists = dto.ShowInVideoLists ?? false,
            AllowedHostTypes = NormalizeAllowedHostTypes(dto.AllowedHostTypes ?? DeriveAllowedHostTypes(dto.Kind ?? GroupKind.Static, dto.QuerySourceKey, dto.QueryJson)),
            SortOrder = dto.SortOrder ?? 0,
        };
        if (dto.Urls?.Count > 0) group.Urls = dto.Urls.Select(u => new GroupUrl { Url = u }).ToList();
        if (dto.TagIds?.Count > 0) group.GroupTags = dto.TagIds.Select(id => new GroupTag { TagId = id }).ToList();

        group = await groupRepo.AddAsync(group, ct);
        if (dto.CustomFields != null)
            await _customFields.SaveValuesAsync(CustomFieldEntityTypes.Group, group.Id, dto.CustomFields, ct);
        if (dto.Rating.HasValue)
            await engagementService.SetRatingAsync(AffinityHostType.Group, group.Id, dto.Rating, cancellationToken: ct);
        var result = await groupRepo.GetByIdWithRelationsAsync(group.Id, ct);
        return CreatedAtAction(nameof(GetById), new { id = group.Id }, await MapToDetailDtoAsync(result!, ct));
    }

    [HttpPut("{id:int}")]
    [RequiresPermission(Permissions.GroupsWrite)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsWrite)]
    public async Task<ActionResult<GroupDto>> Update(int id, [FromBody] GroupUpdateDto dto, CancellationToken ct)
    {
        var group = await groupRepo.GetByIdWithRelationsAsync(id, ct);
        if (group == null) return NotFound();
        var clearFields = dto.ClearFields?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        if (dto.Name != null) group.Name = dto.Name;
        if (dto.Aliases != null) group.Aliases = dto.Aliases;
        if (dto.Date != null) group.Date = ParseDate(dto.Date);
        if (dto.StudioId.HasValue) group.StudioId = dto.StudioId;
        if (dto.Director != null) group.Director = dto.Director;
        if (dto.Description != null) group.Synopsis = dto.Description;
        if (clearFields.Contains("aliases")) group.Aliases = null;
        if (clearFields.Contains("date")) group.Date = null;
        if (clearFields.Contains("studioId")) group.StudioId = null;
        if (clearFields.Contains("director")) group.Director = null;
        if (clearFields.Contains("description")) group.Synopsis = null;
        if (dto.Kind.HasValue)
        {
            group.Kind = dto.Kind.Value;
            if (group.Kind == GroupKind.Static)
            {
                group.QuerySourceKey = null;
                group.QueryJson = null;
                group.LastResolvedAt = null;
                group.CachedItemCount = null;
            }
        }
        if (dto.QuerySourceKey != null) group.QuerySourceKey = group.Kind == GroupKind.Dynamic ? NormalizeOptionalText(dto.QuerySourceKey) : null;
        if (dto.QueryJson != null) group.QueryJson = group.Kind == GroupKind.Dynamic ? NormalizeOptionalText(dto.QueryJson) : null;
        if (dto.ShowInVideoLists.HasValue) group.ShowInVideoLists = dto.ShowInVideoLists.Value;
        if (dto.AllowedHostTypes != null)
            group.AllowedHostTypes = NormalizeAllowedHostTypes(dto.AllowedHostTypes);
        else if (group.Kind == GroupKind.Dynamic && string.Equals(group.QuerySourceKey, DynamicGroupResolver.FilterSourceKey, StringComparison.OrdinalIgnoreCase) && dto.QueryJson != null)
            group.AllowedHostTypes = NormalizeAllowedHostTypes(ParseFilterDynamicGroupEntityTypes(group.QueryJson));
        if (dto.SortOrder.HasValue) group.SortOrder = dto.SortOrder.Value;

        if (dto.Urls != null)
        {
            group.Urls.Clear();
            group.Urls = dto.Urls.Select(u => new GroupUrl { Url = u, GroupId = id }).ToList();
        }
        if (dto.TagIds != null)
        {
            group.GroupTags.Clear();
            group.GroupTags = dto.TagIds.Select(tid => new GroupTag { TagId = tid, GroupId = id }).ToList();
        }
        await groupRepo.UpdateAsync(group, ct);
        if (dto.CustomFields != null)
            await _customFields.SaveValuesAsync(CustomFieldEntityTypes.Group, id, dto.CustomFields, ct);
        if (dto.Rating.HasValue)
            await engagementService.SetRatingAsync(AffinityHostType.Group, id, dto.Rating, cancellationToken: ct);
        var updated = await groupRepo.GetByIdWithRelationsAsync(id, ct);
        return Ok(await MapToDetailDtoAsync(updated!, ct));
    }

    [HttpDelete("{id:int}")]
    [RequiresPermission(Permissions.GroupsDelete)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsDelete)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var g = await groupRepo.GetByIdAsync(id, ct);
        if (g == null) return NotFound();
        if (DynamicGroupResolver.IsProtectedBuiltInGroup(g.QuerySourceKey))
            return Conflict(new { error = "This is a built-in group and cannot be deleted." });
        await _customFields.DeleteValuesForEntityAsync(CustomFieldEntityTypes.Group, id, ct);
        await groupRepo.DeleteAsync(id, ct);
        return NoContent();
    }

    // ===== Bulk Update =====

    [HttpPost("bulk")]
    [RequiresPermission(Permissions.GroupsWrite)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsWrite, ActionArgumentName = "dto", PropertyName = "Ids")]
    public async Task<IActionResult> BulkUpdate([FromBody] BulkGroupUpdateDto dto, CancellationToken ct)
    {
        var groups = await db.Groups
            .Include(g => g.GroupTags)
            .Where(g => dto.Ids.Contains(g.Id))
            .ToListAsync(ct);
        var clearFields = dto.ClearFields?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        foreach (var g in groups)
        {
            if (clearFields.Contains("studioId")) g.StudioId = null;
            if (clearFields.Contains("date")) g.Date = null;
            if (clearFields.Contains("director")) g.Director = null;
            if (clearFields.Contains("description") || clearFields.Contains("synopsis")) g.Synopsis = null;
            if (dto.StudioId.HasValue) g.StudioId = dto.StudioId;
            if (dto.Date != null) g.Date = ParseDate(dto.Date);
            if (dto.Director != null) g.Director = dto.Director;
            if (dto.Description != null) g.Synopsis = dto.Description;

            if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Set)
            {
                g.GroupTags.Clear();
                g.GroupTags = dto.TagIds.Select(tid => new GroupTag { TagId = tid, GroupId = g.Id }).ToList();
            }
            else if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Add)
            {
                var existing = g.GroupTags.Select(gt => gt.TagId).ToHashSet();
                foreach (var tid in dto.TagIds.Where(t => !existing.Contains(t)))
                    g.GroupTags.Add(new GroupTag { TagId = tid, GroupId = g.Id });
            }
            else if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Remove)
            {
                g.GroupTags = g.GroupTags.Where(gt => !dto.TagIds.Contains(gt.TagId)).ToList();
            }
        }

        await db.SaveChangesAsync(ct);
        if (dto.Rating.HasValue)
        {
            foreach (var group in groups)
                await engagementService.SetRatingAsync(AffinityHostType.Group, group.Id, dto.Rating, cancellationToken: ct);
        }
        return Ok(new BulkUpdateResult(groups.Select(group => group.Id).ToList()));
    }

    [HttpDelete("bulk")]
    [RequiresPermission(Permissions.GroupsDelete)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsDelete, ActionArgumentName = "dto", PropertyName = "Ids")]
    public async Task<IActionResult> BulkDelete([FromBody] BatchDeleteDto dto, CancellationToken ct)
    {
        var ids = dto.Ids.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return Ok(new BulkDeleteWithSkippedResult([], 0));

        var groups = await db.Groups.Where(group => ids.Contains(group.Id)).ToListAsync(ct);
        var deletable = groups.Where(group => !DynamicGroupResolver.IsProtectedBuiltInGroup(group.QuerySourceKey)).ToList();
        var skipped = groups.Count - deletable.Count;
        foreach (var group in deletable)
            await _customFields.DeleteValuesForEntityAsync(CustomFieldEntityTypes.Group, group.Id, ct);
        db.Groups.RemoveRange(deletable);
        await db.SaveChangesAsync(ct);
        return Ok(new BulkDeleteWithSkippedResult(deletable.Select(group => group.Id).ToList(), skipped));
    }

    [HttpPut("reorder")]
    [RequiresPermission(Permissions.GroupsWrite)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsWrite, ActionArgumentName = "dto", PropertyName = "Ids")]
    public async Task<IActionResult> Reorder([FromBody] GroupItemsReorderDto dto, CancellationToken ct)
    {
        if (dto.Ids.Count == 0)
            return BadRequest("Reorder payload must contain at least one group.");

        var duplicateIds = dto.Ids.GroupBy(id => id).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        if (duplicateIds.Count > 0)
            return BadRequest("Reorder payload must not contain duplicate groups.");

        var ids = dto.Ids.Where(id => id > 0).ToArray();
        var groups = await db.Groups.Where(group => ids.Contains(group.Id)).ToListAsync(ct);
        if (groups.Count != ids.Length)
            return BadRequest("Reorder payload contains a group that was not found.");

        var insertIndex = Math.Max(0, dto.StartIndex);
        for (var index = 0; index < ids.Length; index++)
        {
            var group = groups.First(item => item.Id == ids[index]);
            group.SortOrder = insertIndex + index;
        }

        await db.SaveChangesAsync(ct);
        foreach (var id in ids)
            PublishGroupUpdate(id);
        return Ok();
    }

    [HttpGet("dynamic-sources")]
    public ActionResult<IReadOnlyList<DynamicGroupSourceDto>> GetDynamicSources()
        => Ok(dynamicGroups?.GetSources() ?? []);

    [HttpPut("{id:int}/query")]
    [RequiresPermission(Permissions.GroupsWrite)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsWrite)]
    public async Task<IActionResult> UpdateQuery(int id, [FromBody] GroupQueryUpdateDto dto, CancellationToken ct)
    {
        var group = await db.Groups.FirstOrDefaultAsync(group => group.Id == id, ct);
        if (group == null) return NotFound();

        group.Kind = GroupKind.Dynamic;
        group.QuerySourceKey = NormalizeOptionalText(dto.QuerySourceKey);
        group.QueryJson = NormalizeOptionalText(dto.QueryJson);
        var derivedAllowedHostTypes = DeriveAllowedHostTypes(group.Kind, group.QuerySourceKey, group.QueryJson);
        if (derivedAllowedHostTypes != null)
            group.AllowedHostTypes = NormalizeAllowedHostTypes(derivedAllowedHostTypes);
        if (dto.CacheTtlSec.HasValue) group.CacheTtlSec = Math.Max(0, dto.CacheTtlSec.Value);
        group.LastResolvedAt = null;
        group.CachedItemCount = null;
        await db.SaveChangesAsync(ct);
        PublishGroupUpdate(group.Id);
        return Ok();
    }

    [HttpPost("{id:int}/snapshot")]
    [RequiresPermission(Permissions.GroupsWrite)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsWrite)]
    public async Task<IActionResult> Snapshot(int id, CancellationToken ct)
    {
        if (dynamicGroups is null) return NotFound();
        var wasDynamic = await db.Groups.AsNoTracking().AnyAsync(group => group.Id == id && group.Kind != GroupKind.Static, ct);
        await dynamicGroups.SnapshotAsync(id, ct);
        if (wasDynamic)
            PublishGroupUpdate(id);
        return Ok();
    }

    [HttpGet("{id:int}/subgroups")]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<List<GroupDto>>> GetSubGroups(int id, CancellationToken ct)
    {
        var relations = await db.Set<GroupRelation>()
            .Where(r => r.ContainingGroupId == id)
            .OrderBy(r => r.OrderIndex)
            .Include(r => r.SubGroup!).ThenInclude(g => g.Urls)
            .Include(r => r.SubGroup!).ThenInclude(g => g.GroupTags).ThenInclude(gt => gt.Tag)
            .Include(r => r.SubGroup!).ThenInclude(g => g.GroupItems)
            .ToListAsync(ct);
        var groups = relations.Where(r => r.SubGroup != null).Select(r => r.SubGroup!).ToList();
        var customFieldValues = await _customFields.GetValuesAsync(CustomFieldEntityTypes.Group, groups.Select(group => group.Id), ct);
        var dynamicCounts = await GetDynamicCountsAsync(groups, ct);
        return Ok(groups.Select(group => MapToDto(group, GetCustomFields(customFieldValues, group.Id), dynamicCounts.GetValueOrDefault(group.Id))).ToList());
    }

    [HttpGet("{id:int}/containinggroups")]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<List<GroupDto>>> GetContainingGroups(int id, CancellationToken ct)
    {
        var relations = await db.Set<GroupRelation>()
            .Where(r => r.SubGroupId == id)
            .OrderBy(r => r.OrderIndex)
            .Include(r => r.ContainingGroup!).ThenInclude(g => g.Urls)
            .Include(r => r.ContainingGroup!).ThenInclude(g => g.GroupTags).ThenInclude(gt => gt.Tag)
            .Include(r => r.ContainingGroup!).ThenInclude(g => g.GroupItems)
            .ToListAsync(ct);
        var groups = relations.Where(r => r.ContainingGroup != null).Select(r => r.ContainingGroup!).ToList();
        var customFieldValues = await _customFields.GetValuesAsync(CustomFieldEntityTypes.Group, groups.Select(group => group.Id), ct);
        var dynamicCounts = await GetDynamicCountsAsync(groups, ct);
        return Ok(groups.Select(group => MapToDto(group, GetCustomFields(customFieldValues, group.Id), dynamicCounts.GetValueOrDefault(group.Id))).ToList());
    }

    [HttpPost("{id:int}/subgroups")]
    [RequiresPermission(Permissions.GroupsWrite)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsWrite)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsWrite, ActionArgumentName = "dto", PropertyName = "SubGroupId")]
    public async Task<IActionResult> AddSubGroup(int id, [FromBody] AddSubGroupDto dto, CancellationToken ct)
    {
        var group = await groupRepo.GetByIdAsync(id, ct);
        if (group == null) return NotFound();

        if (id == dto.SubGroupId)
            return BadRequest(new { message = "A group cannot contain itself." });

        var subGroup = await groupRepo.GetByIdAsync(dto.SubGroupId, ct);
        if (subGroup == null) return NotFound("Sub-group not found");

        // Built-in/system-managed dynamic groups (Save for Later, Watch History, Continue Watching)
        // resolve their items from a query and cannot be deleted, so they must not participate in
        // parent/child relations on either side.
        if (DynamicGroupResolver.IsProtectedBuiltInGroup(group.QuerySourceKey))
            return BadRequest(new { message = "Built-in groups cannot contain sub-groups." });
        if (DynamicGroupResolver.IsProtectedBuiltInGroup(subGroup.QuerySourceKey))
            return BadRequest(new { message = "Built-in groups cannot be added as a sub-group." });

        // Reject a direct cycle (the prospective sub-group already contains this group).
        var wouldCreateCycle = await db.Set<GroupRelation>()
            .AnyAsync(r => r.ContainingGroupId == dto.SubGroupId && r.SubGroupId == id, ct);
        if (wouldCreateCycle)
            return BadRequest(new { message = "That group already contains this group; the relationship would create a cycle." });

        var existing = await db.Set<GroupRelation>()
            .Where(r => r.ContainingGroupId == id)
            .ToListAsync(ct);

        if (existing.Any(r => r.SubGroupId == dto.SubGroupId))
            return Conflict("Sub-group already exists");

        var maxOrder = existing.Count > 0 ? existing.Max(r => r.OrderIndex) + 1 : 0;
        db.Set<GroupRelation>().Add(new GroupRelation
        {
            ContainingGroupId = id,
            SubGroupId = dto.SubGroupId,
            OrderIndex = dto.OrderIndex ?? maxOrder,
            Description = dto.Description,
        });
        await db.SaveChangesAsync(ct);
        PublishGroupUpdate(id);
        return Ok();
    }

    [HttpDelete("{id:int}/subgroups/{subGroupId:int}")]
    [RequiresPermission(Permissions.GroupsWrite)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsWrite)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsWrite, RouteValueName = "subGroupId")]
    public async Task<IActionResult> RemoveSubGroup(int id, int subGroupId, CancellationToken ct)
    {
        var relation = await db.Set<GroupRelation>()
            .FirstOrDefaultAsync(r => r.ContainingGroupId == id && r.SubGroupId == subGroupId, ct);
        if (relation == null) return NotFound();
        db.Set<GroupRelation>().Remove(relation);
        await db.SaveChangesAsync(ct);
        PublishGroupUpdate(id);
        return NoContent();
    }

    [HttpPut("{id:int}/subgroups/reorder")]
    [RequiresPermission(Permissions.GroupsWrite)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsWrite)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsWrite, ActionArgumentName = "dto", PropertyName = "SubGroupIds")]
    public async Task<IActionResult> ReorderSubGroups(int id, [FromBody] ReorderSubGroupsDto dto, CancellationToken ct)
    {
        var relations = await db.Set<GroupRelation>()
            .Where(r => r.ContainingGroupId == id)
            .ToListAsync(ct);

        for (var i = 0; i < dto.SubGroupIds.Count; i++)
        {
            var rel = relations.FirstOrDefault(r => r.SubGroupId == dto.SubGroupIds[i]);
            if (rel != null) rel.OrderIndex = i;
        }
        await db.SaveChangesAsync(ct);
        if (relations.Count > 0)
            PublishGroupUpdate(id);
        return Ok();
    }

    private void PublishGroupUpdate(int id)
        => eventBus?.Publish(new EntityEvent(EventType.GroupUpdated, "Group", id));

    private async Task<GroupDto> MapToDetailDtoAsync(Group group, CancellationToken ct)
    {
        var customFieldValues = await _customFields.GetValuesAsync(CustomFieldEntityTypes.Group, group.Id, ct);
        var dynamicCounts = await GetDynamicCountsAsync([group], ct);
        var fieldProvenance = fieldProvenanceService == null
            ? null
            : (await fieldProvenanceService.GetForHostAsync(AffinityHostType.Group, group.Id, ct)).ToList();
        return MapToDto(group, customFieldValues, dynamicCounts.GetValueOrDefault(group.Id), fieldProvenance);
    }

    private GroupDto MapToDto(Group g, Dictionary<string, object>? customFieldValues = null, GroupItemTypeCounts? dynamicCounts = null, List<FieldProvenanceDto>? fieldProvenance = null)
    {
        var counts = dynamicCounts ?? CountStaticItems(g);
        var itemCount = g.Kind == GroupKind.Dynamic ? dynamicCounts?.ItemCount ?? g.CachedItemCount ?? counts.ItemCount : g.GroupItems.Count;

        return new GroupDto(
            g.Id, g.Name, g.Aliases, g.Date?.ToString("yyyy-MM-dd"),
            g.StudioId, g.Studio?.Name, g.Director, g.Synopsis,
            g.Urls.Select(u => u.Url).ToList(),
            g.GroupTags.Where(gt => gt.Tag != null).Select(gt => TagDtoMapping.MapTagDto(gt.Tag!)).ToList(),
            counts.VideoCount,
            itemCount,
            g.GroupItems.Any(item => item.Kind == GroupItemKind.VideoRange),
            g.SubGroupRelations?.Count ?? 0,
            g.ContainingGroupRelations?.Count ?? 0,
            customFieldValues,
            g.CreatedAt.ToString("o"), g.UpdatedAt.ToString("o"),
            ResolveFrontImagePath(g),
            g.BackImageBlobId != null ? EntityImageUrls.GroupBack(ControllerContext.HttpContext, g.Id, g.UpdatedAt) : null,
            g.Kind,
            g.QuerySourceKey,
            g.QueryJson,
            g.LastResolvedAt?.ToString("o"),
            g.CachedItemCount,
            g.ShowInVideoLists,
            g.AllowedHostTypes,
            g.SortOrder,
            counts.ImageCount,
            counts.AudioCount,
            counts.TextCount,
            counts.GalleryCount,
            counts.PerformerCount,
            counts.StudioCount,
            counts.TagItemCount,
            counts.FaceCount,
                counts.SegmentCount,
                fieldProvenance);
    }

    private string? ResolveFrontImagePath(Group group)
        => group.FrontImageBlobId != null || group.GroupItems.Any(item => item.ImageId.HasValue || item.VideoId.HasValue)
            ? EntityImageUrls.GroupFront(ControllerContext.HttpContext, group.Id, group.UpdatedAt)
            : null;

    private async Task<IReadOnlyDictionary<int, GroupItemTypeCounts>> GetDynamicCountsAsync(IReadOnlyCollection<Group> groups, CancellationToken ct)
    {
        var dynamicGroupsToCount = groups.Where(group => group.Kind == GroupKind.Dynamic).ToList();
        if (dynamicGroupsToCount.Count == 0)
            return new Dictionary<int, GroupItemTypeCounts>();

        var result = new Dictionary<int, GroupItemTypeCounts>();
        foreach (var group in dynamicGroupsToCount.Where(group => string.Equals(group.QuerySourceKey, DynamicGroupResolver.FilterSourceKey, StringComparison.OrdinalIgnoreCase)))
        {
            if (dynamicGroups is null)
            {
                result[group.Id] = new GroupItemTypeCounts(ItemCount: group.CachedItemCount ?? 0);
                continue;
            }

            var counts = await dynamicGroups.CountByKindAsync(group, forceRefresh: false, ct);
            result[group.Id] = CountDynamicKinds(counts);
        }

        foreach (var group in dynamicGroupsToCount.Where(group => string.Equals(group.QuerySourceKey, DynamicGroupResolver.ContinueWatchingSourceKey, StringComparison.OrdinalIgnoreCase)))
            result[group.Id] = new GroupItemTypeCounts(VideoCount: group.CachedItemCount ?? 0, ItemCount: group.CachedItemCount ?? 0);

        if (principalAccessor?.Current?.UserId is not int userId)
            return result;

        if (dynamicGroupsToCount.Any(group => string.Equals(group.QuerySourceKey, DynamicGroupResolver.SaveForLaterSourceKey, StringComparison.OrdinalIgnoreCase)))
        {
            var bookmarkCounts = await db.UserBookmarks.AsNoTracking()
                .Where(bookmark => bookmark.UserId == userId)
                .GroupBy(bookmark => bookmark.HostType)
                .Select(group => new { HostType = group.Key, Count = group.Count() })
                .ToListAsync(ct);
            var counts = CountForAffinityRows(bookmarkCounts.Select(row => (row.HostType, row.Count)));
            foreach (var group in dynamicGroupsToCount.Where(group => string.Equals(group.QuerySourceKey, DynamicGroupResolver.SaveForLaterSourceKey, StringComparison.OrdinalIgnoreCase)))
                result[group.Id] = counts;
        }

        if (dynamicGroupsToCount.Any(group => string.Equals(group.QuerySourceKey, DynamicGroupResolver.WatchHistorySourceKey, StringComparison.OrdinalIgnoreCase)))
        {
            var historyCounts = await db.UserEntityAffinities.AsNoTracking()
                .Where(affinity => affinity.UserId == userId && affinity.LastConsumedAt != null)
                .GroupBy(affinity => affinity.HostType)
                .Select(group => new { HostType = group.Key, Count = group.Count() })
                .ToListAsync(ct);
            var counts = CountForAffinityRows(historyCounts.Select(row => (row.HostType, row.Count)));
            foreach (var group in dynamicGroupsToCount.Where(group => string.Equals(group.QuerySourceKey, DynamicGroupResolver.WatchHistorySourceKey, StringComparison.OrdinalIgnoreCase)))
                result[group.Id] = counts;
        }

        return result;
    }

    private static GroupItemTypeCounts CountStaticItems(Group group)
    {
        static int CountHosts(IEnumerable<GroupItem> items, string hostType)
            => items
                .Where(item => string.Equals(item.HostType, hostType, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.HostId > 0 ? item.HostId : item.VideoId ?? item.ImageId ?? item.ChildGroupId ?? 0)
                .Where(id => id > 0)
                .Distinct()
                .Count();

        var videoCount = group.GroupItems
            .Where(item => item.Kind is GroupItemKind.Video or GroupItemKind.VideoRange || string.Equals(item.HostType, "video", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.VideoId ?? item.HostId)
            .Where(id => id > 0)
            .Distinct()
            .Count();

        return new GroupItemTypeCounts(
            VideoCount: videoCount,
            ImageCount: CountHosts(group.GroupItems, "image"),
            AudioCount: CountHosts(group.GroupItems, "audio"),
            TextCount: CountHosts(group.GroupItems, "text"),
            GalleryCount: CountHosts(group.GroupItems, "gallery"),
            PerformerCount: CountHosts(group.GroupItems, "performer"),
            StudioCount: CountHosts(group.GroupItems, "studio"),
            TagItemCount: CountHosts(group.GroupItems, "tag"),
            FaceCount: CountHosts(group.GroupItems, "face"),
            SegmentCount: CountHosts(group.GroupItems, "segment"),
            ItemCount: group.GroupItems.Count);
    }

    private static GroupItemTypeCounts CountForAffinityRows(IEnumerable<(AffinityHostType HostType, int Count)> rows)
    {
        var counts = rows.ToDictionary(row => row.HostType, row => row.Count);
        return new GroupItemTypeCounts(
            VideoCount: counts.GetValueOrDefault(AffinityHostType.Video),
            ImageCount: counts.GetValueOrDefault(AffinityHostType.Image),
            AudioCount: counts.GetValueOrDefault(AffinityHostType.Audio),
            TextCount: counts.GetValueOrDefault(AffinityHostType.Text),
            GalleryCount: counts.GetValueOrDefault(AffinityHostType.Gallery),
            PerformerCount: counts.GetValueOrDefault(AffinityHostType.Performer),
            StudioCount: counts.GetValueOrDefault(AffinityHostType.Studio),
            TagItemCount: counts.GetValueOrDefault(AffinityHostType.Tag),
            FaceCount: counts.GetValueOrDefault(AffinityHostType.Face),
            ItemCount: counts.Values.Sum());
    }

    private static GroupItemTypeCounts CountForEntityType(string entityType, int count) => entityType switch
    {
        "image" or "images" => new GroupItemTypeCounts(ImageCount: count, ItemCount: count),
        "audio" or "audios" => new GroupItemTypeCounts(AudioCount: count, ItemCount: count),
        "text" or "texts" => new GroupItemTypeCounts(TextCount: count, ItemCount: count),
        "gallery" or "galleries" => new GroupItemTypeCounts(GalleryCount: count, ItemCount: count),
        "performer" or "performers" => new GroupItemTypeCounts(PerformerCount: count, ItemCount: count),
        "studio" or "studios" => new GroupItemTypeCounts(StudioCount: count, ItemCount: count),
        "tag" or "tags" => new GroupItemTypeCounts(TagItemCount: count, ItemCount: count),
        "face" or "faces" => new GroupItemTypeCounts(FaceCount: count, ItemCount: count),
        "segment" or "segments" => new GroupItemTypeCounts(SegmentCount: count, ItemCount: count),
        _ => new GroupItemTypeCounts(VideoCount: count, ItemCount: count),
    };

    private static GroupItemTypeCounts CountDynamicItems(IReadOnlyCollection<GroupItemDto> items)
    {
        int CountKind(params GroupItemKind[] kinds)
            => items.Count(item => kinds.Contains(item.Kind));

        return new GroupItemTypeCounts(
            VideoCount: CountKind(GroupItemKind.Video, GroupItemKind.VideoRange),
            ImageCount: CountKind(GroupItemKind.Image),
            AudioCount: CountKind(GroupItemKind.Audio),
            TextCount: CountKind(GroupItemKind.Text),
            GalleryCount: CountKind(GroupItemKind.Gallery),
            PerformerCount: CountKind(GroupItemKind.Performer),
            StudioCount: CountKind(GroupItemKind.Studio),
            TagItemCount: CountKind(GroupItemKind.Tag),
            FaceCount: CountKind(GroupItemKind.Face),
            SegmentCount: CountKind(GroupItemKind.Segment),
            ItemCount: items.Count);
    }

    private static GroupItemTypeCounts CountDynamicKinds(IReadOnlyDictionary<GroupItemKind, int> counts)
    {
        int CountKind(params GroupItemKind[] kinds) => kinds.Sum(kind => counts.GetValueOrDefault(kind));

        return new GroupItemTypeCounts(
            VideoCount: CountKind(GroupItemKind.Video, GroupItemKind.VideoRange),
            ImageCount: CountKind(GroupItemKind.Image),
            AudioCount: CountKind(GroupItemKind.Audio),
            TextCount: CountKind(GroupItemKind.Text),
            GalleryCount: CountKind(GroupItemKind.Gallery),
            PerformerCount: CountKind(GroupItemKind.Performer),
            StudioCount: CountKind(GroupItemKind.Studio),
            TagItemCount: CountKind(GroupItemKind.Tag),
            FaceCount: CountKind(GroupItemKind.Face),
            SegmentCount: CountKind(GroupItemKind.Segment),
            ItemCount: counts.Values.Sum());
    }

    private static IReadOnlyList<string> ParseFilterDynamicGroupEntityTypes(string? queryJson)
    {
        if (string.IsNullOrWhiteSpace(queryJson)) return ["video"];
        try
        {
            using var document = JsonDocument.Parse(queryJson);
            if (document.RootElement.TryGetProperty("entityTypes", out var entityTypes) && entityTypes.ValueKind == JsonValueKind.Array)
            {
                var values = entityTypes.EnumerateArray()
                    .Where(element => element.ValueKind == JsonValueKind.String)
                    .Select(element => NormalizeEntityTypeName(element.GetString()))
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (values.Count > 0)
                    return values;
            }

            return document.RootElement.TryGetProperty("entityType", out var entityType) && entityType.ValueKind == JsonValueKind.String
                ? [NormalizeEntityTypeName(entityType.GetString())]
                : ["video"];
        }
        catch (JsonException)
        {
            return ["video"];
        }
    }

    private static string NormalizeEntityTypeName(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "video" : value.Trim().ToLowerInvariant();
        return normalized.EndsWith('s') ? normalized[..^1] : normalized;
    }

    private static Dictionary<string, object>? GetCustomFields(IReadOnlyDictionary<int, Dictionary<string, object>> lookup, int id)
        => lookup.TryGetValue(id, out var values) && values.Count > 0 ? values : null;

    private static DateOnly? ParseDate(string? date) => DateOnly.TryParse(date, out var d) ? d : null;

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IEnumerable<string>? DeriveAllowedHostTypes(GroupKind kind, string? querySourceKey, string? queryJson)
        => kind == GroupKind.Dynamic && string.Equals(querySourceKey, DynamicGroupResolver.FilterSourceKey, StringComparison.OrdinalIgnoreCase)
            ? ParseFilterDynamicGroupEntityTypes(queryJson)
            : null;

    private static List<string> NormalizeAllowedHostTypes(IEnumerable<string>? hostTypes)
    {
        var values = (hostTypes ?? DefaultAllowedHostTypes)
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return values.Count > 0 ? values : [.. DefaultAllowedHostTypes];
    }

    private sealed record GroupItemTypeCounts(
        int VideoCount = 0,
        int ImageCount = 0,
        int AudioCount = 0,
        int TextCount = 0,
        int GalleryCount = 0,
        int PerformerCount = 0,
        int StudioCount = 0,
        int TagItemCount = 0,
        int FaceCount = 0,
        int SegmentCount = 0,
        int ItemCount = 0);
}
