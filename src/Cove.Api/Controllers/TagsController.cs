using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Interfaces;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TagsController(ITagRepository tagRepo, Data.CoveContext db) : ControllerBase
{
    private sealed record TagUsageCounts(
        int SceneCount,
        int SceneMarkerCount,
        int ImageCount,
        int GalleryCount,
        int GroupCount,
        int PerformerCount,
        int StudioCount)
    {
        public int TotalUsageCount => SceneCount + SceneMarkerCount + ImageCount + GalleryCount + GroupCount + PerformerCount + StudioCount;
    }

    private sealed record GraphRelation(int ParentId, int ChildId);

    [HttpGet]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<PaginatedResponse<TagListDto>>> Find(
        [FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int perPage = 25,
        [FromQuery] string? sort = null, [FromQuery] string? direction = null,
        [FromQuery] int? seed = null,
        [FromQuery] string? name = null, [FromQuery] bool? favorite = null,
        CancellationToken ct = default)
    {
        var filter = new TagFilter { Name = name, Favorite = favorite };
        var findFilter = new FindFilter
        {
            Q = q, Page = page, PerPage = perPage, Sort = sort,
            Direction = direction == "desc" ? SortDirection.Desc : SortDirection.Asc,
            Seed = seed,
        };

        var (items, totalCount) = await tagRepo.FindAsync(filter, findFilter, ct);
        var dtos = await MapTagListDtos(items, ct);
        return Ok(new PaginatedResponse<TagListDto>(dtos, totalCount, page, perPage));
    }

    [HttpPost("find")]
    public async Task<ActionResult<PaginatedResponse<TagListDto>>> FindPost([FromBody] FilteredQueryRequest<TagFilter> req, CancellationToken ct)
    {
        var findFilter = req.FindFilter ?? new FindFilter();
        var filter = req.ObjectFilter ?? new TagFilter();
        var (items, totalCount) = await tagRepo.FindAsync(filter, findFilter, ct);
        var dtos = await MapTagListDtos(items, ct);
        return Ok(new PaginatedResponse<TagListDto>(dtos, totalCount, findFilter.Page, findFilter.PerPage));
    }

    [HttpPost("graph")]
    public async Task<ActionResult<TagGraphResponseDto>> Graph([FromBody] FilteredQueryRequest<TagFilter> req, CancellationToken ct)
    {
        const int graphNodeLimit = 5000;

        var requestFindFilter = req.FindFilter ?? new FindFilter();
        var graphFindFilter = new FindFilter
        {
            Q = requestFindFilter.Q,
            Sort = requestFindFilter.Sort,
            Direction = requestFindFilter.Direction,
            Seed = requestFindFilter.Seed,
            Page = 1,
            PerPage = Math.Clamp(requestFindFilter.PerPage > 0 ? requestFindFilter.PerPage : graphNodeLimit, 1, graphNodeLimit),
        };

        var filter = req.ObjectFilter ?? new TagFilter();
        var (items, totalCount) = await tagRepo.FindAsync(filter, graphFindFilter, ct);
        if (items.Count == 0)
            return Ok(new TagGraphResponseDto([], [], totalCount));

        var ids = items.Select(tag => tag.Id).ToList();
        var parentIdsByTagId = ids.ToDictionary(id => id, _ => new List<int>());
        var childIdsByTagId = ids.ToDictionary(id => id, _ => new List<int>());
        var usageCountsByTagId = await GetTagUsageCountsAsync(ids, ct);

        var relations = await db.Set<TagParent>()
            .AsNoTracking()
            .Where(relation => ids.Contains(relation.ParentId) && ids.Contains(relation.ChildId))
            .Select(relation => new GraphRelation(relation.ParentId, relation.ChildId))
            .ToListAsync(ct);

        foreach (var relation in relations)
        {
            childIdsByTagId[relation.ParentId].Add(relation.ChildId);
            parentIdsByTagId[relation.ChildId].Add(relation.ParentId);
        }

        var graphItems = items
            .Select(tag =>
            {
                var usageCounts = usageCountsByTagId.GetValueOrDefault(tag.Id) ?? new TagUsageCounts(0, 0, 0, 0, 0, 0, 0);

                return new TagGraphNodeDto(
                    tag.Id,
                    tag.Name,
                    tag.Favorite,
                    tag.Description,
                    tag.ImageBlobId != null ? EntityImageUrls.Tag(tag.Id, tag.UpdatedAt) : null,
                    parentIdsByTagId[tag.Id],
                    childIdsByTagId[tag.Id],
                    usageCounts.TotalUsageCount,
                    usageCounts.SceneCount,
                    usageCounts.SceneMarkerCount,
                    usageCounts.ImageCount,
                    usageCounts.GalleryCount,
                    usageCounts.GroupCount,
                    usageCounts.PerformerCount,
                    usageCounts.StudioCount);
            })
            .ToList();

        var graphLinks = relations
            .Select(relation => new TagGraphLinkDto(relation.ParentId, relation.ChildId))
            .ToList();

        return Ok(new TagGraphResponseDto(graphItems, graphLinks, totalCount));
    }

    [HttpGet("{id:int}")]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<TagDetailDto>> GetById(int id, CancellationToken ct)
    {
        var tag = await tagRepo.GetByIdWithRelationsAsync(id, ct);
        if (tag == null) return NotFound();

        // Use explicit COUNT queries — navigation properties aren't loaded for join tables
        var sceneCount = await db.Set<SceneTag>().CountAsync(st => st.TagId == id, ct);
        var performerCount = await db.Set<PerformerTag>().CountAsync(pt => pt.TagId == id, ct);
        var imageCount = await db.Set<ImageTag>().CountAsync(it => it.TagId == id, ct);
        var galleryCount = await db.Set<GalleryTag>().CountAsync(gt => gt.TagId == id, ct);
        var studioCount = await db.Set<StudioTag>().CountAsync(st => st.TagId == id, ct);
        var groupCount = await db.Set<GroupTag>().CountAsync(gt => gt.TagId == id, ct);
        var markerCount = await db.SceneMarkers.CountAsync(m => m.PrimaryTagId == id, ct)
            + await db.Set<SceneMarkerTag>().CountAsync(mt => mt.TagId == id, ct);

        return Ok(MapToDetailDto(tag, sceneCount, performerCount, imageCount, galleryCount, studioCount, groupCount, markerCount));
    }

    [HttpPost]
    public async Task<ActionResult<TagDetailDto>> Create([FromBody] TagCreateDto dto, CancellationToken ct)
    {
        var existing = await tagRepo.GetByNameAsync(dto.Name, ct);
        if (existing != null) return Conflict(new { message = $"Tag '{dto.Name}' already exists" });

        var tag = new Tag
        {
            Name = dto.Name, SortName = dto.SortName, Description = dto.Description,
            Favorite = dto.Favorite, IgnoreAutoTag = dto.IgnoreAutoTag
        };
        if (dto.Aliases?.Count > 0) tag.Aliases = dto.Aliases.Select(a => new TagAlias { Alias = a }).ToList();
        if (dto.ParentIds?.Count > 0) tag.ParentRelations = dto.ParentIds.Select(pid => new TagParent { ParentId = pid }).ToList();
        if (dto.ChildIds?.Count > 0) tag.ChildRelations = dto.ChildIds.Select(cid => new TagParent { ChildId = cid }).ToList();

        tag = await tagRepo.AddAsync(tag, ct);
        var result = await tagRepo.GetByIdWithRelationsAsync(tag.Id, ct);
        return CreatedAtAction(nameof(GetById), new { id = tag.Id }, MapToDetailDto(result!));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<TagDetailDto>> Update(int id, [FromBody] TagUpdateDto dto, CancellationToken ct)
    {
        var tag = await tagRepo.GetByIdWithRelationsAsync(id, ct);
        if (tag == null) return NotFound();

        if (dto.Name != null) tag.Name = dto.Name;
        if (dto.SortName != null) tag.SortName = dto.SortName;
        if (dto.Description != null) tag.Description = dto.Description;
        if (dto.Favorite.HasValue) tag.Favorite = dto.Favorite.Value;
        if (dto.IgnoreAutoTag.HasValue) tag.IgnoreAutoTag = dto.IgnoreAutoTag.Value;

        if (dto.Aliases != null)
        {
            tag.Aliases.Clear();
            tag.Aliases = dto.Aliases.Select(a => new TagAlias { Alias = a, TagId = id }).ToList();
        }
        if (dto.ParentIds != null)
        {
            tag.ParentRelations.Clear();
            tag.ParentRelations = dto.ParentIds.Select(pid => new TagParent { ParentId = pid, ChildId = id }).ToList();
        }
        if (dto.ChildIds != null)
        {
            tag.ChildRelations.Clear();
            tag.ChildRelations = dto.ChildIds.Select(cid => new TagParent { ParentId = id, ChildId = cid }).ToList();
        }
        if (dto.CustomFields != null) tag.CustomFields = dto.CustomFields;

        await tagRepo.UpdateAsync(tag, ct);
        var updated = await tagRepo.GetByIdWithRelationsAsync(id, ct);
        return Ok(MapToDetailDto(updated!));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var tag = await tagRepo.GetByIdAsync(id, ct);
        if (tag == null) return NotFound();
        await tagRepo.DeleteAsync(id, ct);
        return NoContent();
    }

    private static TagDetailDto MapToDetailDto(Tag t, int sceneCount = 0, int performerCount = 0, int imageCount = 0, int galleryCount = 0, int studioCount = 0, int groupCount = 0, int markerCount = 0) => new(
        t.Id, t.Name, t.SortName, t.Description, t.Favorite, t.IgnoreAutoTag,
        t.Aliases.Select(a => a.Alias).ToList(),
        t.ParentRelations.Where(pr => pr.Parent != null).Select(pr => new TagDto(pr.Parent!.Id, pr.Parent.Name, pr.Parent.Description, pr.Parent.Favorite, pr.Parent.IgnoreAutoTag, [])).ToList(),
        t.ChildRelations.Where(cr => cr.Child != null).Select(cr => new TagDto(cr.Child!.Id, cr.Child.Name, cr.Child.Description, cr.Child.Favorite, cr.Child.IgnoreAutoTag, [])).ToList(),
        sceneCount, performerCount, imageCount, galleryCount,
        studioCount, groupCount, markerCount,
        t.CustomFields,
        t.CreatedAt.ToString("o"), t.UpdatedAt.ToString("o")
    );

    private async Task<List<TagListDto>> MapTagListDtos(IReadOnlyList<Tag> items, CancellationToken ct)
    {
        if (items.Count == 0) return [];

        var ids = items.Select(t => t.Id).ToList();
        var usageCountsByTagId = await GetTagUsageCountsAsync(ids, ct);

        return items.Select(t =>
        {
            var usageCounts = usageCountsByTagId.GetValueOrDefault(t.Id) ?? new TagUsageCounts(0, 0, 0, 0, 0, 0, 0);

            return new TagListDto(
                t.Id,
                t.Name,
                t.Description,
                t.Favorite,
                t.IgnoreAutoTag,
                t.Aliases.Select(a => a.Alias).ToList(),
                usageCounts.SceneCount,
                usageCounts.SceneMarkerCount,
                usageCounts.ImageCount,
                usageCounts.GalleryCount,
                usageCounts.GroupCount,
                usageCounts.PerformerCount,
                usageCounts.StudioCount,
                t.ImageBlobId != null ? EntityImageUrls.Tag(t.Id, t.UpdatedAt) : null);
        }).ToList();
    }

    private async Task<Dictionary<int, TagUsageCounts>> GetTagUsageCountsAsync(IReadOnlyList<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];

        var sceneCounts = await db.Set<SceneTag>().Where(sceneTag => ids.Contains(sceneTag.TagId))
            .GroupBy(sceneTag => sceneTag.TagId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var primaryMarkerCounts = await db.SceneMarkers.Where(marker => ids.Contains(marker.PrimaryTagId))
            .GroupBy(marker => marker.PrimaryTagId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var secondaryMarkerCounts = await db.Set<SceneMarkerTag>().Where(markerTag => ids.Contains(markerTag.TagId))
            .GroupBy(markerTag => markerTag.TagId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var imageCounts = await db.Set<ImageTag>().Where(imageTag => ids.Contains(imageTag.TagId))
            .GroupBy(imageTag => imageTag.TagId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var galleryCounts = await db.Set<GalleryTag>().Where(galleryTag => ids.Contains(galleryTag.TagId))
            .GroupBy(galleryTag => galleryTag.TagId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var groupCounts = await db.Set<GroupTag>().Where(groupTag => ids.Contains(groupTag.TagId))
            .GroupBy(groupTag => groupTag.TagId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var performerCounts = await db.Set<PerformerTag>().Where(performerTag => ids.Contains(performerTag.TagId))
            .GroupBy(performerTag => performerTag.TagId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var studioCounts = await db.Set<StudioTag>().Where(studioTag => ids.Contains(studioTag.TagId))
            .GroupBy(studioTag => studioTag.TagId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);

        return ids.Distinct().ToDictionary(
            id => id,
            id => new TagUsageCounts(
                sceneCounts.GetValueOrDefault(id, 0),
                primaryMarkerCounts.GetValueOrDefault(id, 0) + secondaryMarkerCounts.GetValueOrDefault(id, 0),
                imageCounts.GetValueOrDefault(id, 0),
                galleryCounts.GetValueOrDefault(id, 0),
                groupCounts.GetValueOrDefault(id, 0),
                performerCounts.GetValueOrDefault(id, 0),
                studioCounts.GetValueOrDefault(id, 0)));
    }

    // ===== Bulk Operations =====

    [HttpPost("bulk")]
    public async Task<IActionResult> BulkUpdate([FromBody] BulkTagUpdateDto dto, CancellationToken ct)
    {
        var tags = await db.Tags
            .Include(t => t.ParentRelations)
            .Include(t => t.ChildRelations)
            .AsSplitQuery()
            .Where(t => dto.Ids.Contains(t.Id))
            .ToListAsync(ct);

        foreach (var tag in tags)
        {
            if (dto.Description != null) tag.Description = dto.Description;
            if (dto.Favorite.HasValue) tag.Favorite = dto.Favorite.Value;
            if (dto.IgnoreAutoTag.HasValue) tag.IgnoreAutoTag = dto.IgnoreAutoTag.Value;

            var parentIds = dto.ParentIds?
                .Where(parentId => parentId != tag.Id)
                .Distinct()
                .ToList();
            if (parentIds != null && dto.ParentMode == BulkUpdateMode.Set)
            {
                tag.ParentRelations.Clear();
                tag.ParentRelations = parentIds
                    .Select(parentId => new TagParent { ParentId = parentId, ChildId = tag.Id })
                    .ToList();
            }
            else if (parentIds != null && dto.ParentMode == BulkUpdateMode.Add)
            {
                var existingParentIds = tag.ParentRelations.Select(relation => relation.ParentId).ToHashSet();
                foreach (var parentId in parentIds.Where(parentId => !existingParentIds.Contains(parentId)))
                    tag.ParentRelations.Add(new TagParent { ParentId = parentId, ChildId = tag.Id });
            }
            else if (parentIds != null && dto.ParentMode == BulkUpdateMode.Remove)
            {
                tag.ParentRelations = tag.ParentRelations
                    .Where(relation => !parentIds.Contains(relation.ParentId))
                    .ToList();
            }

            var childIds = dto.ChildIds?
                .Where(childId => childId != tag.Id)
                .Distinct()
                .ToList();
            if (childIds != null && dto.ChildMode == BulkUpdateMode.Set)
            {
                tag.ChildRelations.Clear();
                tag.ChildRelations = childIds
                    .Select(childId => new TagParent { ParentId = tag.Id, ChildId = childId })
                    .ToList();
            }
            else if (childIds != null && dto.ChildMode == BulkUpdateMode.Add)
            {
                var existingChildIds = tag.ChildRelations.Select(relation => relation.ChildId).ToHashSet();
                foreach (var childId in childIds.Where(childId => !existingChildIds.Contains(childId)))
                    tag.ChildRelations.Add(new TagParent { ParentId = tag.Id, ChildId = childId });
            }
            else if (childIds != null && dto.ChildMode == BulkUpdateMode.Remove)
            {
                tag.ChildRelations = tag.ChildRelations
                    .Where(relation => !childIds.Contains(relation.ChildId))
                    .ToList();
            }
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { updated = tags.Count });
    }

    [HttpDelete("bulk")]
    public async Task<IActionResult> BulkDelete([FromBody] BatchDeleteDto dto, CancellationToken ct)
    {
        var tags = await db.Tags.Where(t => dto.Ids.Contains(t.Id)).ToListAsync(ct);
        if (tags.Count == 0)
            return Ok(new { deleted = 0 });

        db.Tags.RemoveRange(tags);
        await db.SaveChangesAsync(ct);
        return Ok(new { deleted = tags.Count });
    }

    // ===== Merge =====

    [HttpPost("merge")]
    public async Task<ActionResult<TagDetailDto>> MergeTags([FromBody] TagMergeDto dto, CancellationToken ct)
    {
        var target = await tagRepo.GetByIdWithRelationsAsync(dto.TargetId, ct);
        if (target == null) return NotFound("Target tag not found");

        var sources = await db.Tags
            .Include(t => t.Aliases)
            .Include(t => t.SceneTags)
            .Include(t => t.PerformerTags)
            .Include(t => t.ImageTags)
            .Include(t => t.GalleryTags)
            .AsSplitQuery()
            .Where(t => dto.SourceIds.Contains(t.Id))
            .ToListAsync(ct);

        foreach (var source in sources)
        {
            // Move scene associations
            foreach (var st in source.SceneTags)
                if (!target.SceneTags.Any(t => t.SceneId == st.SceneId))
                    db.Set<SceneTag>().Add(new SceneTag { SceneId = st.SceneId, TagId = target.Id });
            // Move performer associations
            foreach (var pt in source.PerformerTags)
                if (!target.PerformerTags.Any(t => t.PerformerId == pt.PerformerId))
                    db.Set<PerformerTag>().Add(new PerformerTag { PerformerId = pt.PerformerId, TagId = target.Id });
            // Move image associations
            foreach (var it in source.ImageTags)
                if (!target.ImageTags.Any(t => t.ImageId == it.ImageId))
                    db.Set<ImageTag>().Add(new ImageTag { ImageId = it.ImageId, TagId = target.Id });
            // Add source name as alias
            if (!target.Aliases.Any(a => a.Alias == source.Name))
                target.Aliases.Add(new TagAlias { Alias = source.Name, TagId = target.Id });
            // Delete source
            db.Tags.Remove(source);
        }

        await db.SaveChangesAsync(ct);
        var result = await tagRepo.GetByIdWithRelationsAsync(target.Id, ct);
        return Ok(MapToDetailDto(result!));
    }

    // ===== Marker Wall =====

    [HttpGet("marker-strings")]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<List<string>>> GetMarkerStrings([FromQuery] string? q, [FromQuery] string? sort, CancellationToken ct)
    {
        var query = db.SceneMarkers.AsNoTracking().Select(m => m.Title).Distinct();
        if (!string.IsNullOrEmpty(q))
            query = query.Where(t => EF.Functions.ILike(t, $"%{q}%"));

        var result = sort == "count"
            ? await db.SceneMarkers.AsNoTracking().GroupBy(m => m.Title).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(100).ToListAsync(ct)
            : await query.OrderBy(t => t).Take(100).ToListAsync(ct);

        return Ok(result);
    }
}
