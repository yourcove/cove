using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using Cove.Data.Repositories;
using Cove.Data.Services;
using IAuthorizationService = Cove.Core.Auth.IAuthorizationService;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[RequiresPermission(Permissions.TagsRead)]
public class TagsController(
    ITagRepository tagRepo,
    Data.CoveContext db,
    CustomFieldService customFields,
    IUserEngagementService engagementService,
    SegmentSpanResolver? spanResolver = null,
    IFieldProvenanceService? fieldProvenanceService = null,
    ExtensionEntityFilterService? extensionFilters = null,
    ICurrentPrincipalAccessor? principalAccessor = null) : ControllerBase
{
    private const int ExtensionFilterCandidateLimit = 5_000;

    private sealed record TagUsageCounts(
        int VideoCount,
        int SegmentCount,
        int ImageCount,
        int GalleryCount,
        int GroupCount,
        int PerformerCount,
        int StudioCount,
        int AudioCount,
        int TextCount)
    {
        public int TotalUsageCount => VideoCount + SegmentCount + ImageCount + GalleryCount + GroupCount + PerformerCount + StudioCount + AudioCount + TextCount;
    }

    private sealed record GraphRelation(int ParentId, int ChildId);

    private async Task EvictSegmentSpanCachesForTagsAsync(IEnumerable<int> tagIds, CancellationToken ct)
    {
        if (spanResolver is null)
            return;

        var ids = tagIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
            return;

        var videoIds = await db.Segments.AsNoTracking()
            .Where(segment => segment.HostType == SegmentHostType.Video && segment.TagId.HasValue && ids.Contains(segment.TagId.Value))
            .Select(segment => segment.HostId)
            .Distinct()
            .ToListAsync(ct);

        foreach (var videoId in videoIds)
            spanResolver.EvictVideo(videoId);
    }

    [HttpGet]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<PaginatedResponse<TagListDto>>> Find(
        [FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int perPage = 25,
        [FromQuery] string? sort = null, [FromQuery] string? direction = null,
        [FromQuery] int? seed = null,
        [FromQuery] string? name = null, [FromQuery] bool? favorite = null,
        [FromQuery] int? rating = null,
        [FromQuery] bool includeCounts = true,
        CancellationToken ct = default)
    {
        var filter = new TagFilter { Name = name, Favorite = favorite, Rating = rating };
        var findFilter = new FindFilter
        {
            Q = q, Page = page, PerPage = perPage, Sort = sort,
            Direction = direction == "desc" ? SortDirection.Desc : SortDirection.Asc,
            Seed = seed,
        };

        var (items, totalCount) = await tagRepo.FindAsync(filter, findFilter, ct);
        // Usage counts aggregate over tag_applications/segments (millions of rows) and dominate this
        // endpoint's latency; callers that only need id/name (autocomplete) opt out via includeCounts.
        var usageCountsByTagId = includeCounts
            ? await LoadTagUsageCountsAsync(items.Select(tag => tag.Id), ct)
            : new Dictionary<int, TagUsageCounts>();
        var dtos = MapTagListDtos(items, usageCountsByTagId);
        return Ok(new PaginatedResponse<TagListDto>(dtos, totalCount, page, perPage));
    }

    [HttpPost("find")]
    public async Task<ActionResult<PaginatedResponse<TagListDto>>> FindPost([FromBody] FilteredQueryRequest<TagFilter> req, CancellationToken ct)
    {
        var findFilter = req.FindFilter ?? new FindFilter();
        var filter = req.ObjectFilter ?? new TagFilter();
        var extensionCriteria = filter.ExtensionCriteria ?? [];
        IReadOnlyList<Tag> items;
        int totalCount;
        if (extensionCriteria.Count == 0)
        {
            (items, totalCount) = await tagRepo.FindAsync(filter, findFilter, ct);
        }
        else
        {
            if (extensionFilters is null || principalAccessor?.Current is null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails { Title = "Extension filtering is unavailable." });

            try
            {
                var candidateFindFilter = CopyFindFilter(findFilter, page: 1, perPage: ExtensionFilterCandidateLimit + 1);
                var (candidateItems, coreCount) = await tagRepo.FindAsync(filter, candidateFindFilter, ct);
                if (coreCount > ExtensionFilterCandidateLimit)
                    throw new ExtensionEntityFilterLimitException($"Extension filtering supports at most {ExtensionFilterCandidateLimit} core candidates per query.");

                var candidateIds = candidateItems.Select(tag => tag.Id).ToArray();
                var authorizedQuery = await ReadScopeListOptimization.ApplyAsync<Tag>(db, EntityKinds.Tag, Permissions.TagsRead, ct);
                var authorizedIds = await authorizedQuery
                    .AsNoTracking()
                    .Where(tag => candidateIds.Contains(tag.Id))
                    .Select(tag => tag.Id)
                    .ToHashSetAsync(ct);
                candidateItems = candidateItems.Where(tag => authorizedIds.Contains(tag.Id)).ToArray();

                var matchingIds = await extensionFilters.ApplyAsync(
                    "tags",
                    extensionCriteria,
                    candidateItems.Select(tag => tag.Id).ToArray(),
                    principalAccessor.Current,
                    ct);
                totalCount = matchingIds.Count;
                var page = Math.Max(1, findFilter.Page);
                var perPage = Math.Max(0, findFilter.PerPage);
                var pagedIds = perPage == 0
                    ? []
                    : matchingIds.Skip((page - 1) * perPage).Take(perPage).ToArray();
                var itemById = candidateItems.ToDictionary(tag => tag.Id);
                items = pagedIds.Select(id => itemById[id]).ToArray();
            }
            catch (ExtensionEntityFilterValidationException ex)
            {
                return UnprocessableEntity(new ProblemDetails { Title = "Invalid extension filter.", Detail = ex.Message });
            }
            catch (ExtensionEntityFilterLimitException ex)
            {
                return UnprocessableEntity(new ProblemDetails { Title = "Extension filter limit exceeded.", Detail = ex.Message });
            }
            catch (ExtensionEntityFilterProviderException ex)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails { Title = "Extension filter provider unavailable.", Detail = ex.Message });
            }
        }
        var usageCountsByTagId = await LoadTagUsageCountsAsync(items.Select(tag => tag.Id), ct);
        var dtos = MapTagListDtos(items, usageCountsByTagId);
        return Ok(new PaginatedResponse<TagListDto>(dtos, totalCount, findFilter.Page, findFilter.PerPage));
    }

    private static FindFilter CopyFindFilter(FindFilter source, int page, int perPage) => new()
    {
        Q = source.Q,
        Page = page,
        PerPage = perPage,
        Sort = source.Sort,
        Direction = source.Direction,
        Seed = source.Seed,
    };

    [HttpPost("graph")]
    public async Task<ActionResult<TagGraphResponseDto>> Graph([FromBody] FilteredQueryRequest<TagFilter> req, CancellationToken ct)
    {
        const int graphNodeLimit = 5000;

        var requestFindFilter = req.FindFilter ?? new FindFilter();
        var graphResultLimit = Math.Clamp(requestFindFilter.PerPage > 0 ? requestFindFilter.PerPage : graphNodeLimit, 1, graphNodeLimit);
        var graphFindFilter = new FindFilter
        {
            Q = requestFindFilter.Q,
            Sort = requestFindFilter.Sort,
            Direction = requestFindFilter.Direction,
            Seed = requestFindFilter.Seed,
            Page = 1,
            PerPage = graphResultLimit,
        };

        var filter = req.ObjectFilter ?? new TagFilter();
        var extensionCriteria = filter.ExtensionCriteria ?? [];
        IReadOnlyList<Tag> items;
        int totalCount;
        if (extensionCriteria.Count == 0)
        {
            (items, totalCount) = await tagRepo.FindAsync(filter, graphFindFilter, ct);
        }
        else
        {
            var candidateFindFilter = CopyFindFilter(
                graphFindFilter,
                page: 1,
                perPage: ExtensionFilterCandidateLimit + 1);
            (items, totalCount) = await tagRepo.FindAsync(filter, candidateFindFilter, ct);
        }

        if (extensionCriteria.Count > 0)
        {
            if (extensionFilters is null || principalAccessor?.Current is null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails { Title = "Extension filtering is unavailable." });
            try
            {
                if (totalCount > ExtensionFilterCandidateLimit)
                    throw new ExtensionEntityFilterLimitException($"Extension filtering supports at most {ExtensionFilterCandidateLimit} core candidates per query.");
                var candidateIds = items.Select(tag => tag.Id).ToArray();
                var authorizedQuery = await ReadScopeListOptimization.ApplyAsync<Tag>(db, EntityKinds.Tag, Permissions.TagsRead, ct);
                var authorizedIds = await authorizedQuery
                    .AsNoTracking()
                    .Where(tag => candidateIds.Contains(tag.Id))
                    .Select(tag => tag.Id)
                    .ToHashSetAsync(ct);
                items = items.Where(tag => authorizedIds.Contains(tag.Id)).ToArray();
                var matchingIds = (await extensionFilters.ApplyAsync(
                    "tags", extensionCriteria, items.Select(tag => tag.Id).ToArray(), principalAccessor.Current, ct)).ToHashSet();
                items = items.Where(tag => matchingIds.Contains(tag.Id)).ToArray();
                totalCount = items.Count;
                items = items.Take(graphResultLimit).ToArray();
            }
            catch (ExtensionEntityFilterValidationException ex)
            {
                return UnprocessableEntity(new ProblemDetails { Title = "Invalid extension filter.", Detail = ex.Message });
            }
            catch (ExtensionEntityFilterLimitException ex)
            {
                return UnprocessableEntity(new ProblemDetails { Title = "Extension filter limit exceeded.", Detail = ex.Message });
            }
            catch (ExtensionEntityFilterProviderException ex)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails { Title = "Extension filter provider unavailable.", Detail = ex.Message });
            }
        }
        if (items.Count == 0)
            return Ok(new TagGraphResponseDto([], [], totalCount));

        var ids = items.Select(tag => tag.Id).ToList();
        var parentIdsByTagId = ids.ToDictionary(id => id, _ => new List<int>());
        var childIdsByTagId = ids.ToDictionary(id => id, _ => new List<int>());
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

        var usageCountsByTagId = await LoadTagUsageCountsAsync(ids, ct);
        var graphItems = items
            .Select(tag =>
            {
                var usageCounts = usageCountsByTagId.GetValueOrDefault(tag.Id) ?? new TagUsageCounts(0, 0, 0, 0, 0, 0, 0, 0, 0);

                return new TagGraphNodeDto(
                    tag.Id,
                    tag.Name,
                    tag.Favorite,
                    tag.Description,
                    EntityImageUrls.TagOrNull(ControllerContext.HttpContext, tag),
                    tag.TagGroupId,
                    tag.TagGroup?.Name,
                    tag.TagGroup?.Color,
                    parentIdsByTagId[tag.Id],
                    childIdsByTagId[tag.Id],
                    usageCounts.TotalUsageCount,
                    usageCounts.VideoCount,
                    usageCounts.SegmentCount,
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
    public async Task<ActionResult<TagDetailDto>> GetById(int id, CancellationToken ct, [FromQuery] int? depth = null)
    {
        var tag = await db.Tags
            .AsNoTracking()
            .Include(t => t.Aliases)
            .Include(t => t.TagGroup)
            .Include(t => t.RemoteIds)
            .Include(t => t.ParentRelations).ThenInclude(tp => tp.Parent).ThenInclude(parent => parent!.TagGroup)
            .Include(t => t.ChildRelations).ThenInclude(tp => tp.Child).ThenInclude(child => child!.TagGroup)
            .AsSplitQuery()
            .FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tag == null) return NotFound();

        return Ok(await MapToDetailDtoAsync(tag, ct, depth));
    }

    [HttpGet("{id:int}/segments")]
    public async Task<ActionResult<IReadOnlyList<TagSegmentWallDto>>> GetSegments(int id, [FromQuery] int count = 100, CancellationToken ct = default)
    {
        var exists = await db.Tags.AsNoTracking().AnyAsync(tag => tag.Id == id, ct);
        if (!exists)
            return NotFound();

        count = Math.Clamp(count, 1, 250);

        var segments = await (
            from segment in db.VisibleSegments().AsNoTracking()
            join video in db.Videos.AsNoTracking() on segment.HostId equals video.Id
            where segment.HostType == SegmentHostType.Video && segment.TagId == id
            orderby segment.UpdatedAt descending, segment.Id descending
            select new TagSegmentWallDto(
                segment.Id,
                segment.Title,
                segment.StartSec,
                segment.EndSec,
                segment.Kind ?? "segment",
                segment.SourceKey,
                segment.Confidence,
                video.Id,
                video.Title ?? $"Video #{video.Id}")
        )
            .Take(count)
            .ToListAsync(ct);

        return Ok(segments);
    }

    [HttpPost]
    [RequiresPermission(Permissions.TagsWrite)]
    public async Task<ActionResult<TagDetailDto>> Create([FromBody] TagCreateDto dto, CancellationToken ct)
    {
        var existing = await tagRepo.GetByNameAsync(dto.Name, ct);
        if (existing != null) return Conflict(new { message = $"Tag '{dto.Name}' already exists" });

        var validation = await ValidateTagMetadataAsync(dto.Color, dto.TagGroupId, ct);
        if (validation != null) return validation;

        var tag = new Tag
        {
            Name = dto.Name, SortName = dto.SortName, Description = dto.Description,
            Color = NormalizeOptionalText(dto.Color),
            TagGroupId = NormalizeOptionalId(dto.TagGroupId),
            Favorite = dto.Favorite,
            Organized = dto.Organized,
            MinOccurrenceSec = NormalizeOptionalPositive(dto.MinOccurrenceSec),
            MinOccurrencePercent = NormalizeOptionalPercent(dto.MinOccurrencePercent),
            ShowAsSegment = dto.ShowAsSegment,
            SegmentColorOverride = NormalizeOptionalText(dto.SegmentColorOverride),
            SegmentLaneOverride = dto.SegmentLaneOverride,
        };
        if (dto.Aliases?.Count > 0) tag.Aliases = dto.Aliases.Select(a => new TagAlias { Alias = a }).ToList();
        if (dto.ParentIds?.Count > 0) tag.ParentRelations = dto.ParentIds.Select(pid => new TagParent { ParentId = pid }).ToList();
        if (dto.ChildIds?.Count > 0) tag.ChildRelations = dto.ChildIds.Select(cid => new TagParent { ChildId = cid }).ToList();
        if (dto.RemoteIds?.Count > 0) tag.RemoteIds = NormalizeRemoteIds(dto.RemoteIds).Select(remoteId => new TagRemoteId { Endpoint = remoteId.Endpoint, RemoteId = remoteId.RemoteId }).ToList();

        tag = await tagRepo.AddAsync(tag, ct);
        if (dto.CustomFields != null)
            await customFields.SaveValuesAsync(CustomFieldEntityTypes.Tag, tag.Id, dto.CustomFields, ct);
        var result = await tagRepo.GetByIdWithRelationsAsync(tag.Id, ct);
        return CreatedAtAction(nameof(GetById), new { id = tag.Id }, await MapToDetailDtoAsync(result!, ct));
    }

    [HttpPut("{id:int}")]
    [RequiresPermission(Permissions.TagsWrite)]
    [RequiresEntityAccess(EntityKinds.Tag, Permissions.TagsWrite)]
    public async Task<ActionResult<TagDetailDto>> Update(int id, [FromBody] TagUpdateDto dto, CancellationToken ct)
    {
        var tag = tagRepo != null
            ? await tagRepo.GetByIdWithRelationsAsync(id, ct)
            : await db.Tags
                .Include(t => t.Aliases)
                .Include(t => t.TagGroup)
                .Include(t => t.RemoteIds)
                .Include(t => t.ParentRelations)
                .Include(t => t.ChildRelations)
                .FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tag == null) return NotFound();

            var validation = await ValidateTagMetadataAsync(dto.Color, dto.TagGroupId, ct);
            if (validation != null) return validation;

        if (dto.Name != null) tag.Name = dto.Name;
        if (dto.SortName != null) tag.SortName = dto.SortName;
        if (dto.Description != null) tag.Description = dto.Description;
        tag.Color = NormalizeOptionalText(dto.Color);
        tag.TagGroupId = NormalizeOptionalId(dto.TagGroupId);
        if (dto.Favorite.HasValue) tag.Favorite = dto.Favorite.Value;
        if (dto.Organized.HasValue) tag.Organized = dto.Organized.Value;
        tag.MinOccurrenceSec = NormalizeOptionalPositive(dto.MinOccurrenceSec);
        tag.MinOccurrencePercent = NormalizeOptionalPercent(dto.MinOccurrencePercent);
        tag.ShowAsSegment = dto.ShowAsSegment;
        tag.SegmentColorOverride = NormalizeOptionalText(dto.SegmentColorOverride);
        tag.SegmentLaneOverride = dto.SegmentLaneOverride;

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
        if (dto.RemoteIds != null)
        {
            tag.RemoteIds.Clear();
            tag.RemoteIds = NormalizeRemoteIds(dto.RemoteIds).Select(remoteId => new TagRemoteId { TagId = id, Endpoint = remoteId.Endpoint, RemoteId = remoteId.RemoteId }).ToList();
        }
        if (tagRepo != null)
        {
            await tagRepo.UpdateAsync(tag, ct);
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }
        if (dto.CustomFields != null)
            await customFields.SaveValuesAsync(CustomFieldEntityTypes.Tag, id, dto.CustomFields, ct);
        await EvictSegmentSpanCachesForTagsAsync([id], ct);
        var updated = tagRepo != null
            ? await tagRepo.GetByIdWithRelationsAsync(id, ct)
            : await db.Tags
                .AsNoTracking()
                .Include(t => t.Aliases)
                .Include(t => t.TagGroup)
                .Include(t => t.RemoteIds)
                .Include(t => t.ParentRelations).ThenInclude(tp => tp.Parent).ThenInclude(parent => parent!.TagGroup)
                .Include(t => t.ChildRelations).ThenInclude(tp => tp.Child).ThenInclude(child => child!.TagGroup)
                .FirstOrDefaultAsync(t => t.Id == id, ct);
        return Ok(await MapToDetailDtoAsync(updated!, ct));
    }

    [HttpGet("{id:int}/metadata-server/search")]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<IReadOnlyList<MetadataServerTagMatchDto>>> SearchMetadataServer(int id, [FromServices] MetadataServerService metadataServerService, [FromQuery] string? term, [FromQuery] string? endpoint, CancellationToken ct)
    {
        var tag = await tagRepo.GetByIdWithRelationsAsync(id, ct);
        if (tag == null)
            return NotFound();

        await db.Entry(tag).Collection(t => t.RemoteIds).LoadAsync(ct);

        if (string.IsNullOrWhiteSpace(term))
        {
            var existingRemoteId = tag.RemoteIds.FirstOrDefault(remoteId => string.IsNullOrWhiteSpace(endpoint) || string.Equals(remoteId.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));
            if (existingRemoteId != null)
            {
                var existing = await metadataServerService.GetTagMatchAsync(existingRemoteId.Endpoint, existingRemoteId.RemoteId, ct);
                if (existing != null)
                    return Ok(new[] { existing });
            }

            term = tag.Name;
        }

        return Ok(await metadataServerService.SearchTagsAsync(term, endpoint, ct));
    }

    [HttpPost("metadata-server/find-by-ids")]
    public async Task<ActionResult<IReadOnlyList<MetadataServerTagMatchDto>>> FindMetadataServerTagsByIds([FromServices] MetadataServerService metadataServerService, [FromBody] MetadataServerFindByIdsRequestDto dto, CancellationToken ct)
    {
        if (dto.Ids.Count == 0)
            return Ok(Array.Empty<MetadataServerTagMatchDto>());

        var results = new List<MetadataServerTagMatchDto>();
        foreach (var tagId in dto.Ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var match = await metadataServerService.GetTagMatchAsync(dto.Endpoint, tagId, ct);
            if (match != null)
                results.Add(match);
        }

        return Ok(results);
    }

    [HttpPost("{id:int}/metadata-server/import")]
    [RequiresPermission(Permissions.TagsWrite)]
    [RequiresEntityAccess(EntityKinds.Tag, Permissions.TagsWrite)]
    public async Task<ActionResult<TagDetailDto>> ImportFromMetadataServer(int id, [FromServices] MetadataServerService metadataServerService, [FromBody] MetadataServerTagImportRequestDto dto, CancellationToken ct)
    {
        var tag = await tagRepo.GetByIdWithRelationsAsync(id, ct);
        if (tag == null)
            return NotFound();

        await db.Entry(tag).Collection(t => t.RemoteIds).LoadAsync(ct);

        var imported = await metadataServerService.MergeTagAsync(tag, dto.Endpoint, dto.TagId, ct);
        if (!imported)
            return NotFound();

        await tagRepo.UpdateAsync(tag, ct);
        var updated = await tagRepo.GetByIdWithRelationsAsync(id, ct);
        return Ok(await MapToDetailDtoAsync(updated!, ct));
    }

    [HttpPost("{id:int}/metadata-server/submit-draft")]
    [RequiresPermission(Permissions.TagsWrite)]
    [RequiresEntityAccess(EntityKinds.Tag, Permissions.TagsWrite)]
    public async Task<IActionResult> SubmitTagDraft(int id, [FromServices] MetadataServerService metadataServerService, [FromBody] MetadataServerEndpointDto dto, CancellationToken ct)
    {
        var tag = await tagRepo.GetByIdWithRelationsAsync(id, ct);
        if (tag == null)
            return NotFound();

        var draftId = await metadataServerService.SubmitTagDraftAsync(tag, dto.Endpoint, ct);
        return Ok(new { draftId });
    }

    [HttpPost("metadata-server/batch-tag")]
    [RequiresPermission(Permissions.TagsWrite)]
    [RequiresEntityAccess(EntityKinds.Tag, Permissions.TagsWrite, ActionArgumentName = "dto", PropertyName = "Ids")]
    public async Task<ActionResult<object>> BatchTagFromMetadataServer([FromBody] MetadataServerTagBatchTagRequestDto dto, [FromServices] IJobService jobService, [FromServices] IServiceScopeFactory scopeFactory, [FromServices] IAuthorizationService authorizationService, [FromServices] ICurrentPrincipalAccessor principalAccessor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Endpoint))
            return BadRequest(new { message = "Endpoint is required" });

        var ids = await ResolveSelectedTagIdsAsync(dto, ct);
        if (ids.Count == 0)
            return BadRequest(new { message = "No tags selected for batch tagging" });

        var principal = principalAccessor.Current;
        if (principal == null)
            return Forbid();

        foreach (var id in ids)
        {
            var result = await authorizationService.AuthorizeAsync(
                principal,
                Permissions.TagsWrite,
                new EntityRef(EntityKinds.Tag, id.ToString()),
                ct);

            if (!result.Allowed)
                return Forbid();
        }

        var jobId = jobService.Enqueue(
            "metadata-server:tags",
            $"Tagging {ids.Count} tags from {dto.Endpoint}",
            async (progress, jobCt) =>
            {
                using var scope = scopeFactory.CreateScope();
                var metadataServerService = scope.ServiceProvider.GetRequiredService<MetadataServerService>();
                await metadataServerService.BatchTagTagsAsync(dto.Endpoint, ids, dto.RefreshAlreadyTagged, dto.ExcludeFields, progress, jobCt);
            });

        return Ok(new { jobId, itemCount = ids.Count });
    }

    [HttpDelete("{id:int}")]
    [RequiresPermission(Permissions.TagsDelete)]
    [RequiresEntityAccess(EntityKinds.Tag, Permissions.TagsDelete)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var tag = await tagRepo.GetByIdAsync(id, ct);
        if (tag == null) return NotFound();
        await customFields.DeleteValuesForEntityAsync(CustomFieldEntityTypes.Tag, id, ct);
        await tagRepo.DeleteAsync(id, ct);
        return NoContent();
    }

    private async Task<List<int>> ResolveSelectedTagIdsAsync(MetadataServerTagBatchTagRequestDto dto, CancellationToken ct)
    {
        if (dto.Ids?.Count > 0)
            return dto.Ids.Distinct().ToList();

        if (!dto.SelectAll && dto.Filter == null)
            return [];

        const int pageSize = 500;
        var ids = new List<int>();
        var page = 1;

        while (true)
        {
            var (items, totalCount) = await tagRepo.FindAsync(dto.Filter, new FindFilter
            {
                Page = page,
                PerPage = pageSize,
                Sort = "id",
                Direction = SortDirection.Asc,
            }, ct);

            if (items.Count == 0)
                break;

            ids.AddRange(items.Select(item => item.Id));
            if (ids.Count >= totalCount)
                break;

            page++;
        }

        return ids.Distinct().ToList();
    }

    private async Task<TagDetailDto> MapToDetailDtoAsync(Tag t, CancellationToken ct, int? depth = null)
    {
        var usageCounts = depth == -1
            ? await LoadRecursiveTagUsageCountsAsync(t.Id, ct)
            : (await LoadTagUsageCountsAsync([t.Id], ct)).GetValueOrDefault(t.Id)
                ?? new TagUsageCounts(0, 0, 0, 0, 0, 0, 0, 0, 0);
        var fieldProvenance = fieldProvenanceService == null
            ? null
            : (await fieldProvenanceService.GetForHostAsync(AffinityHostType.Tag, t.Id, ct)).ToList();

        return new TagDetailDto(
            t.Id,
            t.Name,
            t.SortName,
            t.Description,
            t.Favorite,
            t.Aliases.Select(a => a.Alias).ToList(),
            t.ParentRelations
                .Where(pr => pr.Parent != null)
                .Select(pr => pr.Parent!)
                .OrderBy(TagDtoMapping.EffectiveSortName)
                .Select(parent => MapTagDto(parent))
                .ToList(),
            t.ChildRelations
                .Where(cr => cr.Child != null)
                .Select(cr => cr.Child!)
                .OrderBy(TagDtoMapping.EffectiveSortName)
                .Select(child => MapTagDto(child))
                .ToList(),
            usageCounts.VideoCount,
            usageCounts.PerformerCount,
            usageCounts.ImageCount,
            usageCounts.GalleryCount,
            usageCounts.StudioCount,
            usageCounts.GroupCount,
            usageCounts.AudioCount,
            usageCounts.TextCount,
            usageCounts.SegmentCount,
            await customFields.GetValuesAsync(CustomFieldEntityTypes.Tag, t.Id, ct),
            t.CreatedAt.ToString("o"),
            t.UpdatedAt.ToString("o"),
            t.ShowAsSegment,
            t.SegmentColorOverride,
            t.SegmentLaneOverride,
            t.Color,
            t.TagGroupId,
            t.TagGroup?.Name,
            t.TagGroup?.Color,
            t.MinOccurrenceSec,
            t.MinOccurrencePercent,
            t.RemoteIds.Select(remoteId => new TagRemoteIdDto(remoteId.Endpoint, remoteId.RemoteId)).ToList(),
            t.Organized,
            fieldProvenance);
    }

    private async Task<TagUsageCounts> LoadRecursiveTagUsageCountsAsync(int tagId, CancellationToken ct)
    {
        var expanded = await HierarchicalCriterionExpander.ExpandTagsAsync(db, new MultiIdCriterion
        {
            Value = [tagId],
            Modifier = CriterionModifier.Includes,
            Depth = -1,
        }, ct);
        var ids = expanded.Criterion.Value;

        var scopedVideos = await ReadScopeListOptimization.ApplyAsync<Video>(db, EntityKinds.Video, Permissions.VideosRead, ct);
        var scopedImages = await ReadScopeListOptimization.ApplyAsync<Image>(db, EntityKinds.Image, Permissions.ImagesRead, ct);
        var scopedGalleries = await ReadScopeListOptimization.ApplyAsync<Gallery>(db, EntityKinds.Gallery, Permissions.GalleriesRead, ct);
        var scopedGroups = await ReadScopeListOptimization.ApplyAsync<Group>(db, EntityKinds.Group, Permissions.GroupsRead, ct);
        var scopedPerformers = await ReadScopeListOptimization.ApplyAsync<Performer>(db, EntityKinds.Performer, Permissions.PerformersRead, ct);
        var scopedStudios = await ReadScopeListOptimization.ApplyAsync<Studio>(db, EntityKinds.Studio, Permissions.StudiosRead, ct);
        var scopedAudios = await ReadScopeListOptimization.ApplyAsync<Audio>(db, EntityKinds.Audio, Permissions.AudiosRead, ct);
        var scopedTexts = await ReadScopeListOptimization.ApplyAsync<TextDocument>(db, EntityKinds.Text, Permissions.TextsRead, ct);

        var videoCount = await scopedVideos
            .Join(EffectiveHostTagQuery.ForHostType(db, AffinityHostType.Video).Where(tag => ids.Contains(tag.TagId)), video => video.Id, tag => tag.HostId, (video, _) => video.Id)
            .Distinct().CountAsync(ct);
        var segmentCount = (await LoadVideoSegmentCountsAsync(ids, ct)).Values.Sum();
        var imageCount = await scopedImages.CountAsync(image => image.ImageTags.Any(tag => ids.Contains(tag.TagId)), ct);
        var galleryCount = await scopedGalleries.CountAsync(gallery => gallery.GalleryTags.Any(tag => ids.Contains(tag.TagId)), ct);
        var groupCount = await scopedGroups.CountAsync(group => group.GroupTags.Any(tag => ids.Contains(tag.TagId)), ct);
        var performerCount = await scopedPerformers.CountAsync(performer => performer.PerformerTags.Any(tag => ids.Contains(tag.TagId)), ct);
        var studioCount = await scopedStudios.CountAsync(studio => studio.StudioTags.Any(tag => ids.Contains(tag.TagId)), ct);
        var audioCount = await scopedAudios
            .Join(EffectiveHostTagQuery.ForHostType(db, AffinityHostType.Audio).Where(tag => ids.Contains(tag.TagId)), audio => audio.Id, tag => tag.HostId, (audio, _) => audio.Id)
            .Distinct().CountAsync(ct);
        var textCount = await scopedTexts.CountAsync(text => text.TextTags.Any(tag => ids.Contains(tag.TagId)), ct);

        return new TagUsageCounts(videoCount, segmentCount, imageCount, galleryCount, groupCount, performerCount, studioCount, audioCount, textCount);
    }

    private List<TagListDto> MapTagListDtos(IReadOnlyList<Tag> items, IReadOnlyDictionary<int, TagUsageCounts> usageCountsByTagId)
    {
        if (items.Count == 0) return [];

        return items.Select(t =>
        {
            var usageCounts = usageCountsByTagId.GetValueOrDefault(t.Id) ?? new TagUsageCounts(0, 0, 0, 0, 0, 0, 0, 0, 0);

            return new TagListDto(
                t.Id,
                t.Name,
                t.Description,
                t.Favorite,
                t.Aliases.Select(a => a.Alias).ToList(),
                usageCounts.VideoCount,
                usageCounts.SegmentCount,
                usageCounts.ImageCount,
                usageCounts.GalleryCount,
                usageCounts.GroupCount,
                usageCounts.PerformerCount,
                usageCounts.StudioCount,
                EntityImageUrls.TagOrNull(ControllerContext.HttpContext, t),
                t.ShowAsSegment,
                t.SegmentColorOverride,
                t.SegmentLaneOverride,
                t.Color,
                t.TagGroupId,
                t.TagGroup?.Name,
                t.TagGroup?.Color,
                t.MinOccurrenceSec,
                t.MinOccurrencePercent,
                t.Organized);
        }).ToList();
    }

    private async Task<Dictionary<int, TagUsageCounts>> LoadTagUsageCountsAsync(IEnumerable<int> tagIds, CancellationToken ct)
    {
        var ids = tagIds
            .Where(tagId => tagId > 0)
            .Distinct()
            .ToArray();

        if (ids.Length == 0)
            return [];

        var videoCounts = await EffectiveHostTagQuery.ForHostType(db, AffinityHostType.Video)
            .AsNoTracking()
            .Where(tag => ids.Contains(tag.TagId))
            .Select(tag => new { tag.TagId, tag.HostId })
            .Distinct()
            .GroupBy(tag => tag.TagId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var segmentCounts = await LoadVideoSegmentCountsAsync(ids, ct);
        var imageCounts = await db.Set<ImageTag>()
            .AsNoTracking()
            .Where(imageTag => ids.Contains(imageTag.TagId))
            .GroupBy(imageTag => imageTag.TagId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var galleryCounts = await db.Set<GalleryTag>()
            .AsNoTracking()
            .Where(galleryTag => ids.Contains(galleryTag.TagId))
            .GroupBy(galleryTag => galleryTag.TagId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var groupCounts = await db.Set<GroupTag>()
            .AsNoTracking()
            .Where(groupTag => ids.Contains(groupTag.TagId))
            .GroupBy(groupTag => groupTag.TagId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var performerCounts = await db.Set<PerformerTag>()
            .AsNoTracking()
            .Where(performerTag => ids.Contains(performerTag.TagId))
            .GroupBy(performerTag => performerTag.TagId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var studioCounts = await db.Set<StudioTag>()
            .AsNoTracking()
            .Where(studioTag => ids.Contains(studioTag.TagId))
            .GroupBy(studioTag => studioTag.TagId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var audioCounts = await EffectiveHostTagQuery.ForHostType(db, AffinityHostType.Audio)
            .AsNoTracking()
            .Where(tag => ids.Contains(tag.TagId))
            .Select(tag => new { tag.TagId, tag.HostId })
            .Distinct()
            .GroupBy(tag => tag.TagId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var textCounts = await db.Set<TextTag>()
            .AsNoTracking()
            .Where(textTag => ids.Contains(textTag.TagId))
            .GroupBy(textTag => textTag.TagId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);

        return ids.ToDictionary(
            id => id,
            id => new TagUsageCounts(
                videoCounts.GetValueOrDefault(id),
                segmentCounts.GetValueOrDefault(id),
                imageCounts.GetValueOrDefault(id),
                galleryCounts.GetValueOrDefault(id),
                groupCounts.GetValueOrDefault(id),
                performerCounts.GetValueOrDefault(id),
                studioCounts.GetValueOrDefault(id),
                audioCounts.GetValueOrDefault(id),
                textCounts.GetValueOrDefault(id)));
    }

    private static TagDto MapTagDto(Tag tag, List<TagProvenanceDto>? provenance = null)
        => new(
            tag.Id,
            tag.Name,
            tag.Description,
            tag.Favorite,
            tag.Aliases.Select(alias => alias.Alias).ToList(),
            tag.ShowAsSegment,
            tag.SegmentColorOverride,
            tag.SegmentLaneOverride,
            provenance,
            tag.Color,
            tag.TagGroupId,
            tag.TagGroup?.Name,
            tag.TagGroup?.Color,
            tag.MinOccurrenceSec,
            tag.MinOccurrencePercent,
            Organized: tag.Organized,
            HasImage: tag.ImageOverrideBlobId != null || tag.ImageBlobId != null);

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? NormalizeOptionalId(int? value)
        => value is > 0 ? value : null;

    private static double? NormalizeOptionalPositive(double? value)
        => value is > 0 ? value : null;

    private static double? NormalizeOptionalPercent(double? value)
        => value is > 0 ? Math.Min(value.Value, 100d) : null;

    private static List<TagRemoteIdDto> NormalizeRemoteIds(IEnumerable<TagRemoteIdDto> remoteIds)
        => remoteIds
            .Select(remoteId => new TagRemoteIdDto(remoteId.Endpoint.Trim(), remoteId.RemoteId.Trim()))
            .Where(remoteId => remoteId.Endpoint.Length > 0 && remoteId.RemoteId.Length > 0)
            .DistinctBy(remoteId => (remoteId.Endpoint.ToUpperInvariant(), remoteId.RemoteId.ToUpperInvariant()))
            .ToList();

    private async Task<ActionResult<TagDetailDto>?> ValidateTagMetadataAsync(string? color, int? tagGroupId, CancellationToken ct)
    {
        var normalizedColor = NormalizeOptionalText(color);
        if (normalizedColor != null && !IsHexColor(normalizedColor))
            return BadRequest(new { message = "Color must be #RRGGBB or #RRGGBBAA." });

        var normalizedGroupId = NormalizeOptionalId(tagGroupId);
        if (normalizedGroupId.HasValue && !await db.TagGroups.AsNoTracking().AnyAsync(group => group.Id == normalizedGroupId.Value, ct))
            return BadRequest(new { message = "Tag group does not exist." });

        return null;
    }

    private static bool IsHexColor(string value)
    {
        if (value.Length is not (7 or 9) || value[0] != '#')
            return false;

        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }

        return true;
    }

    private async Task<Dictionary<int, int>> LoadVideoSegmentCountsAsync(IEnumerable<int> tagIds, CancellationToken ct)
    {
        var ids = tagIds.Distinct().ToList();
        if (ids.Count == 0)
            return [];

        return await db.VisibleSegments(SegmentHostType.Video)
            .AsNoTracking()
            .Where(segment => segment.TagId.HasValue && ids.Contains(segment.TagId.Value))
            .GroupBy(segment => segment.TagId!.Value)
            .Select(group => new { TagId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.TagId, item => item.Count, ct);
    }

    // ===== Bulk Operations =====

    [HttpPost("bulk")]
    [RequiresPermission(Permissions.TagsWrite)]
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
            if (dto.Color != null) tag.Color = string.IsNullOrWhiteSpace(dto.Color) ? null : dto.Color.Trim();
            if (dto.ClearFields?.Contains("tagGroupId", StringComparer.OrdinalIgnoreCase) == true) tag.TagGroupId = null;
            else if (dto.TagGroupId.HasValue) tag.TagGroupId = NormalizeOptionalId(dto.TagGroupId);
            if (dto.MinOccurrenceSec.HasValue) tag.MinOccurrenceSec = dto.MinOccurrenceSec;
            if (dto.MinOccurrencePercent.HasValue) tag.MinOccurrencePercent = dto.MinOccurrencePercent;
            if (dto.Organized.HasValue) tag.Organized = dto.Organized.Value;
            if (dto.Favorite.HasValue) tag.Favorite = dto.Favorite.Value;

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

        // Rating is per-user (Rating table), not a tag entity field — set it through the engagement service.
        if (dto.Rating.HasValue)
            foreach (var tag in tags)
                await engagementService.SetRatingAsync(AffinityHostType.Tag, tag.Id, dto.Rating, cancellationToken: ct);

        await EvictSegmentSpanCachesForTagsAsync(tags.Select(tag => tag.Id), ct);
        return Ok(new { updated = tags.Count });
    }

    [HttpDelete("bulk")]
    [RequiresPermission(Permissions.TagsDelete)]
    public async Task<IActionResult> BulkDelete([FromBody] BatchDeleteDto dto, CancellationToken ct)
    {
        var tags = await db.Tags.Where(t => dto.Ids.Contains(t.Id)).ToListAsync(ct);
        if (tags.Count == 0)
            return Ok(new { deleted = 0 });

        db.Tags.RemoveRange(tags);
        foreach (var tag in tags)
            await customFields.DeleteValuesForEntityAsync(CustomFieldEntityTypes.Tag, tag.Id, ct);
        await db.SaveChangesAsync(ct);
        return Ok(new { deleted = tags.Count });
    }

    // ===== Merge =====

    [HttpPost("merge")]
    [RequiresPermission(Permissions.TagsWrite)]
    public async Task<ActionResult<TagDetailDto>> MergeTags([FromBody] TagMergeDto dto, CancellationToken ct)
    {
        var target = await tagRepo.GetByIdWithRelationsAsync(dto.TargetId, ct);
        if (target == null) return NotFound("Target tag not found");

        var sources = await db.Tags
            .Include(t => t.Aliases)
            .Include(t => t.VideoTags)
            .Include(t => t.PerformerTags)
            .Include(t => t.ImageTags)
            .Include(t => t.GalleryTags)
            .AsSplitQuery()
            .Where(t => dto.SourceIds.Contains(t.Id) && t.Id != target.Id)
            .ToListAsync(ct);

        // Seed dedup sets from the target's *actual* associations in the database. target was loaded
        // without its join-table collections, and entries we add during the loop never land in its
        // navigation collections — so checking target.VideoTags/ImageTags/etc. would miss both
        // pre-existing duplicates and duplicates contributed by another source in the same merge,
        // violating the join-table primary keys. Adding to the HashSet as we go dedups across sources.
        var targetVideoIds = (await db.Set<VideoTag>().Where(t => t.TagId == target.Id).Select(t => t.VideoId).ToListAsync(ct)).ToHashSet();
        var targetPerformerIds = (await db.Set<PerformerTag>().Where(t => t.TagId == target.Id).Select(t => t.PerformerId).ToListAsync(ct)).ToHashSet();
        var targetImageIds = (await db.Set<ImageTag>().Where(t => t.TagId == target.Id).Select(t => t.ImageId).ToListAsync(ct)).ToHashSet();
        var targetGalleryIds = (await db.Set<GalleryTag>().Where(t => t.TagId == target.Id).Select(t => t.GalleryId).ToListAsync(ct)).ToHashSet();
        var targetAliases = target.Aliases.Select(alias => alias.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            // Move video associations
            foreach (var st in source.VideoTags)
                if (targetVideoIds.Add(st.VideoId))
                    db.Set<VideoTag>().Add(new VideoTag { VideoId = st.VideoId, TagId = target.Id });
            // Move performer associations
            foreach (var pt in source.PerformerTags)
                if (targetPerformerIds.Add(pt.PerformerId))
                    db.Set<PerformerTag>().Add(new PerformerTag { PerformerId = pt.PerformerId, TagId = target.Id });
            // Move image associations
            foreach (var it in source.ImageTags)
                if (targetImageIds.Add(it.ImageId))
                    db.Set<ImageTag>().Add(new ImageTag { ImageId = it.ImageId, TagId = target.Id });
            // Move gallery associations
            foreach (var gt in source.GalleryTags)
                if (targetGalleryIds.Add(gt.GalleryId))
                    db.Set<GalleryTag>().Add(new GalleryTag { GalleryId = gt.GalleryId, TagId = target.Id });
            // Add source name as alias
            if (!string.IsNullOrWhiteSpace(source.Name)
                && !string.Equals(source.Name, target.Name, StringComparison.OrdinalIgnoreCase)
                && targetAliases.Add(source.Name))
                target.Aliases.Add(new TagAlias { Alias = source.Name, TagId = target.Id });
            // Delete source
            await customFields.DeleteValuesForEntityAsync(CustomFieldEntityTypes.Tag, source.Id, ct);
            db.Tags.Remove(source);
        }

        // Re-point tag-keyed timeline data from the source tags to the target so a merge moves the
        // segments instead of orphaning them. Without this, deleting the source tag would trigger the
        // segments' ON DELETE SET NULL and leave kind=tag rows with no tag.
        var mergedSourceIds = sources.Select(source => source.Id).ToArray();
        if (mergedSourceIds.Length > 0)
        {
            await db.Segments
                .Where(segment => segment.TagId != null && mergedSourceIds.Contains(segment.TagId.Value))
                .ExecuteUpdateAsync(setters => setters.SetProperty(segment => segment.TagId, target.Id), ct);
            await db.Set<SegmentDisplayRule>()
                .Where(rule => rule.TagId != null && mergedSourceIds.Contains(rule.TagId.Value))
                .ExecuteUpdateAsync(setters => setters.SetProperty(rule => rule.TagId, target.Id), ct);
        }

        await db.SaveChangesAsync(ct);
        var result = await tagRepo.GetByIdWithRelationsAsync(target.Id, ct);
        return Ok(await MapToDetailDtoAsync(result!, ct));
    }

    // ===== Segment Wall =====

    [HttpGet("segment-titles")]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<List<string>>> GetSegmentTitles([FromQuery] string? q, [FromQuery] string? sort, CancellationToken ct)
    {
        var query = db.Segments
            .AsNoTracking()
            .Where(segment => segment.HostType == SegmentHostType.Video && segment.Title != null && segment.Title != string.Empty)
            .Select(segment => segment.Title!)
            .Distinct();
        if (!string.IsNullOrEmpty(q))
        {
            if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                query = query.Where(title => EF.Functions.ILike(title, $"%{q}%"));
            }
            else
            {
                var normalizedQuery = q.ToUpperInvariant();
                query = query.Where(title => title.ToUpper().Contains(normalizedQuery));
            }
        }

        var result = sort == "count"
            ? await db.Segments
                .AsNoTracking()
                .Where(segment => segment.HostType == SegmentHostType.Video && segment.Title != null && segment.Title != string.Empty)
                .GroupBy(segment => segment.Title!)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .Take(100)
                .ToListAsync(ct)
            : await query.OrderBy(t => t).Take(100).ToListAsync(ct);

        return Ok(result);
    }
}
