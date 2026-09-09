using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Cove.Api.Services;
using Cove.Api.Helpers;
using Cove.Core.Auth;
using Cove.Core.Common;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data.Repositories;
using Cove.Data.Services;
using IAuthorizationService = Cove.Core.Auth.IAuthorizationService;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[RequiresPermission(Permissions.StudiosRead)]
public class StudiosController(IStudioRepository studioRepo, MetadataServerService metadataServerService, Data.CoveContext db, IUserEngagementService engagementService, CustomFieldService? customFields = null, IFieldProvenanceService? fieldProvenanceService = null, IEventBus? eventBus = null, StudioMergeService? studioMergeService = null, ICurrentPrincipalAccessor? principalAccessor = null, BulkDeletionJobService? bulkDeletionJobService = null, BulkEntityDeletionService? bulkEntityDeletionService = null) : ControllerBase
{
    private sealed record StudioUsageCounts(int VideoCount, int ImageCount, int GalleryCount, int GroupCount, int PerformerCount, int ChildStudioCount, int AudioCount, int TextCount);
    private readonly CustomFieldService _customFields = customFields ?? new CustomFieldService(db);

    [HttpGet]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<PaginatedResponse<StudioDto>>> Find(
        [FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int perPage = 25,
        [FromQuery] string? sort = null, [FromQuery] string? direction = null,
        [FromQuery] int? seed = null,
        [FromQuery] string? sorts = null,
        [FromQuery] string? name = null, [FromQuery] bool? favorite = null,
        [FromQuery] int? parentId = null, [FromQuery] string? tagIds = null,
        CancellationToken ct = default)
    {
        var filter = new StudioFilter { Name = name, Favorite = favorite, ParentId = parentId, TagIds = QueryParsing.ParseIntList(tagIds)?.ToList() };
        var sortClauses = SortClause.Parse(sorts);
        var primarySort = sortClauses.FirstOrDefault();
        var findFilter = new FindFilter
        {
            Q = q, Page = page, PerPage = perPage, Sort = primarySort?.Key ?? sort,
            Direction = primarySort?.Direction ?? (direction == "desc" ? SortDirection.Desc : SortDirection.Asc),
            Sorts = sortClauses.Count > 0 ? sortClauses : null,
            Seed = seed,
        };

        var (items, totalCount) = await studioRepo.FindAsync(filter, findFilter, ct);
        var usageCountsByStudioId = await LoadStudioUsageCountsAsync(items.Select(item => item.Id), ct);
        var dtos = await MapListToDtos(items, usageCountsByStudioId, ct);
        return Ok(new PaginatedResponse<StudioDto>(dtos, totalCount, page, perPage));
    }

    [HttpPost("find")]
    public async Task<ActionResult<PaginatedResponse<StudioDto>>> FindPost([FromBody] FilteredQueryRequest<StudioFilter> req, CancellationToken ct)
    {
        var findFilter = req.FindFilter ?? new FindFilter();
        var filter = req.ObjectFilter ?? new StudioFilter();
        var (items, totalCount) = await studioRepo.FindAsync(filter, findFilter, ct);
        var usageCountsByStudioId = await LoadStudioUsageCountsAsync(items.Select(item => item.Id), ct);
        var dtos = await MapListToDtos(items, usageCountsByStudioId, ct);
        return Ok(new PaginatedResponse<StudioDto>(dtos, totalCount, findFilter.Page, findFilter.PerPage));
    }

    [HttpGet("{id:int}")]
    [AllowShareLinkAccess]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<StudioDto>> GetById(int id, CancellationToken ct, [FromQuery] int? depth = null)
    {
        var studio = await studioRepo.GetByIdWithRelationsAsync(id, ct);
        if (studio == null) return NotFound();
        return Ok(await MapToDetailDtoAsync(studio, ct, depth));
    }

    [HttpPost]
    [RequiresPermission(Permissions.StudiosWrite)]
    public async Task<ActionResult<StudioDto>> Create([FromBody] StudioCreateDto dto, CancellationToken ct)
    {
        var studio = new Studio
        {
            Name = dto.Name, ParentId = dto.ParentId,
            Favorite = dto.Favorite, Details = dto.Details,
            Organized = dto.Organized
        };
        if (dto.Urls?.Count > 0) studio.Urls = dto.Urls.Select(u => new StudioUrl { Url = u }).ToList();
        if (dto.Aliases?.Count > 0) studio.Aliases = dto.Aliases.Select(a => new StudioAlias { Alias = a }).ToList();
        if (dto.TagIds?.Count > 0) studio.StudioTags = dto.TagIds.Select(id => new StudioTag { TagId = id }).ToList();
        if (dto.RemoteIds?.Count > 0) studio.RemoteIds = NormalizeRemoteIds(dto.RemoteIds).Select(remoteId => new StudioRemoteId { Endpoint = remoteId.Endpoint, RemoteId = remoteId.RemoteId }).ToList();

        try
        {
            studio = await studioRepo.AddAsync(studio, ct);
        }
        catch (EntityNameConflictException exception)
        {
            return Conflict(new { code = "STUDIO_NAME_CONFLICT", message = exception.Message });
        }
        if (dto.CustomFields != null)
            await _customFields.SaveValuesAsync(CustomFieldEntityTypes.Studio, studio.Id, dto.CustomFields, ct);
        if (dto.Rating.HasValue)
            await engagementService.SetRatingAsync(AffinityHostType.Studio, studio.Id, dto.Rating, cancellationToken: ct);
        var result = await studioRepo.GetByIdWithRelationsAsync(studio.Id, ct);
        return CreatedAtAction(nameof(GetById), new { id = studio.Id }, await MapToDetailDtoAsync(result!, ct));
    }

    [HttpPut("{id:int}")]
    [RequiresPermission(Permissions.StudiosWrite)]
    [RequiresEntityAccess(EntityKinds.Studio, Permissions.StudiosWrite)]
    public async Task<ActionResult<StudioDto>> Update(int id, [FromBody] StudioUpdateDto dto, CancellationToken ct)
    {
        var studio = await studioRepo.GetByIdWithRelationsAsync(id, ct);
        if (studio == null) return NotFound();
        var clearFields = dto.ClearFields?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        if (dto.Name != null) studio.Name = dto.Name;
        if (dto.ParentId.HasValue) studio.ParentId = dto.ParentId;
        if (dto.Favorite.HasValue) studio.Favorite = dto.Favorite.Value;
        if (dto.Details != null) studio.Details = dto.Details;
        if (dto.Organized.HasValue) studio.Organized = dto.Organized.Value;
        if (clearFields.Contains("parentId")) studio.ParentId = null;
        if (clearFields.Contains("details")) studio.Details = null;

        if (dto.Urls != null)
        {
            if (MetadataCollectionUpdater.ReplaceIfChanged(studio.Urls, dto.Urls, item => item.Url, url => new StudioUrl { Url = url, StudioId = id }, StringComparer.Ordinal))
                MetadataCollectionUpdater.Touch(studio);
        }
        if (dto.Aliases != null)
        {
            if (MetadataCollectionUpdater.ReplaceIfChanged(studio.Aliases, dto.Aliases, item => item.Alias, alias => new StudioAlias { Alias = alias, StudioId = id }, StringComparer.Ordinal))
                MetadataCollectionUpdater.Touch(studio);
        }
        if (dto.TagIds != null)
        {
            if (MetadataCollectionUpdater.ReplaceIfChanged(studio.StudioTags, dto.TagIds, item => item.TagId, tagId => new StudioTag { TagId = tagId, StudioId = id }))
                MetadataCollectionUpdater.Touch(studio);
        }
        if (dto.RemoteIds != null)
        {
            var remoteIds = NormalizeRemoteIds(dto.RemoteIds).Select(item => (item.Endpoint, item.RemoteId));
            if (MetadataCollectionUpdater.ReplaceIfChanged(studio.RemoteIds, remoteIds, item => (item.Endpoint, item.RemoteId), key => new StudioRemoteId { StudioId = id, Endpoint = key.Endpoint, RemoteId = key.RemoteId }))
                MetadataCollectionUpdater.Touch(studio);
        }
        try
        {
            await studioRepo.UpdateAsync(studio, ct);
        }
        catch (EntityNameConflictException exception)
        {
            return Conflict(new { code = "STUDIO_NAME_CONFLICT", message = exception.Message });
        }
        if (dto.CustomFields != null && await _customFields.SaveValuesAsync(CustomFieldEntityTypes.Studio, id, dto.CustomFields, ct))
        {
            MetadataCollectionUpdater.Touch(studio);
            await studioRepo.UpdateAsync(studio, ct);
        }
        if (dto.Rating.HasValue)
            await engagementService.SetRatingAsync(AffinityHostType.Studio, id, dto.Rating, cancellationToken: ct);
        var updated = await studioRepo.GetByIdWithRelationsAsync(id, ct);
        return Ok(await MapToDetailDtoAsync(updated!, ct));
    }

    [HttpDelete("{id:int}")]
    [RequiresPermission(Permissions.StudiosDelete)]
    [RequiresEntityAccess(EntityKinds.Studio, Permissions.StudiosDelete)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        if (bulkEntityDeletionService is not null)
        {
            var deleted = await bulkEntityDeletionService.DeleteAsync(
                BulkDeletionEntityKind.Studio,
                id,
                new BulkDeletionExecutionContext(),
                deleteFiles: false,
                deleteGenerated: true,
                ct,
                publishEvent: false);
            return deleted ? NoContent() : NotFound();
        }

        var s = await studioRepo.GetByIdAsync(id, ct);
        if (s == null) return NotFound();
        await _customFields.DeleteValuesForEntityAsync(CustomFieldEntityTypes.Studio, id, ct);
        await studioRepo.DeleteAsync(id, ct);
        return NoContent();
    }

    // ===== Bulk Update =====

    [HttpPost("bulk")]
    [RequiresPermission(Permissions.StudiosWrite)]
    [RequiresEntityAccess(EntityKinds.Studio, Permissions.StudiosWrite, ActionArgumentName = "dto", PropertyName = "Ids")]
    public async Task<IActionResult> BulkUpdate([FromBody] BulkStudioUpdateDto dto, CancellationToken ct)
    {
        var studios = await db.Studios
            .Include(s => s.StudioTags)
            .Where(s => dto.Ids.Contains(s.Id))
            .ToListAsync(ct);
        var clearFields = dto.ClearFields?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        foreach (var s in studios)
        {
            if (clearFields.Contains("parentId")) s.ParentId = null;
            if (clearFields.Contains("details")) s.Details = null;
            if (dto.Favorite.HasValue) s.Favorite = dto.Favorite.Value;
            if (dto.Details != null) s.Details = dto.Details;
            if (dto.Organized.HasValue) s.Organized = dto.Organized.Value;

            if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Set)
            {
                s.StudioTags.Clear();
                s.StudioTags = dto.TagIds.Select(tid => new StudioTag { TagId = tid, StudioId = s.Id }).ToList();
            }
            else if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Add)
            {
                var existing = s.StudioTags.Select(st => st.TagId).ToHashSet();
                foreach (var tid in dto.TagIds.Where(t => !existing.Contains(t)))
                    s.StudioTags.Add(new StudioTag { TagId = tid, StudioId = s.Id });
            }
            else if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Remove)
            {
                s.StudioTags = s.StudioTags.Where(st => !dto.TagIds.Contains(st.TagId)).ToList();
            }
        }

        await db.SaveChangesAsync(ct);
        if (dto.Rating.HasValue)
        {
            foreach (var studio in studios)
                await engagementService.SetRatingAsync(AffinityHostType.Studio, studio.Id, dto.Rating, cancellationToken: ct);
        }
        return Ok(new BulkUpdateResult(studios.Select(studio => studio.Id).ToList()));
    }

    [HttpDelete("bulk")]
    [RequiresPermission(Permissions.StudiosDelete)]
    [RequiresEntityAccess(EntityKinds.Studio, Permissions.StudiosDelete, ActionArgumentName = "dto", PropertyName = "Ids")]
    public IActionResult BulkDelete([FromBody] BatchDeleteDto dto, CancellationToken ct)
    {
        var ids = dto.Ids.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
            return BadRequest("Select at least one studio to delete.");

        return Accepted(bulkDeletionJobService!.Start(
            principalAccessor?.Current,
            BulkDeletionEntityKind.Studio,
            ids));
    }

    // ===== Merge =====

    private async Task<StudioDto> MapToDetailDtoAsync(Studio studio, CancellationToken ct, int? depth = null)
    {
        var usageCounts = depth == -1
            ? await LoadRecursiveStudioUsageCountsAsync(studio.Id, ct)
            : (await LoadStudioUsageCountsAsync([studio.Id], ct)).GetValueOrDefault(studio.Id);
        var customFieldValues = await _customFields.GetValuesAsync(CustomFieldEntityTypes.Studio, studio.Id, ct);
        var fieldProvenance = fieldProvenanceService == null
            ? null
            : (await fieldProvenanceService.GetForHostAsync(AffinityHostType.Studio, studio.Id, ct)).ToList();
        return MapToDto(studio, usageCounts, customFieldValues, fieldProvenance);
    }

    private async Task<StudioUsageCounts> LoadRecursiveStudioUsageCountsAsync(int studioId, CancellationToken ct)
    {
        var expanded = await HierarchicalCriterionExpander.ExpandStudiosAsync(db, new MultiIdCriterion
        {
            Value = [studioId],
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

        var videoCount = await scopedVideos.CountAsync(video => video.StudioId.HasValue && ids.Contains(video.StudioId.Value), ct);
        var imageCount = await scopedImages.CountAsync(image => image.StudioId.HasValue && ids.Contains(image.StudioId.Value), ct);
        var galleryCount = await scopedGalleries.CountAsync(gallery => gallery.StudioId.HasValue && ids.Contains(gallery.StudioId.Value), ct);
        var groupCount = await scopedGroups.CountAsync(group => group.StudioId.HasValue && ids.Contains(group.StudioId.Value), ct);
        var performerCount = await scopedPerformers.CountAsync(performer => performer.VideoPerformers.Any(relation => relation.Video != null && relation.Video.StudioId.HasValue && ids.Contains(relation.Video.StudioId.Value)), ct);
        var childStudioCount = await scopedStudios.CountAsync(studio => studio.ParentId == studioId, ct);
        var audioCount = await scopedAudios.CountAsync(audio => audio.StudioId.HasValue && ids.Contains(audio.StudioId.Value), ct);
        var textCount = await scopedTexts.CountAsync(text => text.StudioId.HasValue && ids.Contains(text.StudioId.Value), ct);

        return new StudioUsageCounts(videoCount, imageCount, galleryCount, groupCount, performerCount, childStudioCount, audioCount, textCount);
    }

    private StudioDto MapToDto(Studio s, StudioUsageCounts? usageCounts = null, Dictionary<string, object>? customFieldValues = null, List<FieldProvenanceDto>? fieldProvenance = null) => new(
        s.Id, s.Name, s.ParentId, s.Parent?.Name, s.Favorite, s.Details, s.Organized,
        s.Urls.Select(u => u.Url).ToList(),
        s.Aliases.Select(a => a.Alias).ToList(),
        s.StudioTags.Where(st => st.Tag != null).Select(st => TagDtoMapping.MapTagDto(st.Tag!)).OrderForDisplay().ToList(),
        s.RemoteIds.Select(sid => new StudioRemoteIdDto(sid.Endpoint, sid.RemoteId)).ToList(),
        usageCounts?.VideoCount ?? s.VideoCount,
        usageCounts?.ImageCount ?? s.ImageCount,
        usageCounts?.GalleryCount ?? s.GalleryCount,
        usageCounts?.GroupCount ?? s.GroupCount,
        usageCounts?.PerformerCount ?? s.PerformerCount,
        usageCounts?.ChildStudioCount ?? s.ChildStudioCount,
        usageCounts?.AudioCount ?? 0,
        usageCounts?.TextCount ?? 0,
        EntityImageUrls.StudioOrNull(ControllerContext.HttpContext, s),
        customFieldValues,
        s.CreatedAt.ToString("o"), s.UpdatedAt.ToString("o"),
        fieldProvenance
    );

    private static List<StudioRemoteIdDto> NormalizeRemoteIds(IEnumerable<StudioRemoteIdDto> remoteIds)
        => remoteIds
            .Select(remoteId => new StudioRemoteIdDto(remoteId.Endpoint.Trim(), remoteId.RemoteId.Trim()))
            .Where(remoteId => !string.IsNullOrWhiteSpace(remoteId.Endpoint) && !string.IsNullOrWhiteSpace(remoteId.RemoteId))
            .GroupBy(remoteId => new { Endpoint = remoteId.Endpoint.ToUpperInvariant(), RemoteId = remoteId.RemoteId.ToUpperInvariant() })
            .Select(group => group.First())
            .ToList();

    private async Task<List<StudioDto>> MapListToDtos(IReadOnlyList<Studio> items, IReadOnlyDictionary<int, StudioUsageCounts> usageCountsByStudioId, CancellationToken ct)
    {
        if (items.Count == 0)
            return [];

        var customFieldValues = await _customFields.GetValuesAsync(CustomFieldEntityTypes.Studio, items.Select(item => item.Id), ct);
        return items.Select(studio => MapToDto(studio, usageCountsByStudioId.GetValueOrDefault(studio.Id), GetCustomFields(customFieldValues, studio.Id))).ToList();
    }

    private static Dictionary<string, object>? GetCustomFields(IReadOnlyDictionary<int, Dictionary<string, object>> lookup, int id)
        => lookup.TryGetValue(id, out var values) && values.Count > 0 ? values : null;

    private async Task<Dictionary<int, StudioUsageCounts>> LoadStudioUsageCountsAsync(IEnumerable<int> studioIds, CancellationToken ct)
    {
        var ids = studioIds
            .Where(studioId => studioId > 0)
            .Distinct()
            .ToArray();

        if (ids.Length == 0)
            return [];

        var videoCounts = await db.Videos
            .AsNoTracking()
            .Where(video => video.StudioId.HasValue && ids.Contains(video.StudioId.Value))
            .GroupBy(video => video.StudioId!.Value)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var imageCounts = await db.Images
            .AsNoTracking()
            .Where(image => image.StudioId.HasValue && ids.Contains(image.StudioId.Value))
            .GroupBy(image => image.StudioId!.Value)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var galleryCounts = await db.Galleries
            .AsNoTracking()
            .Where(gallery => gallery.StudioId.HasValue && ids.Contains(gallery.StudioId.Value))
            .GroupBy(gallery => gallery.StudioId!.Value)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var groupCounts = await db.Groups
            .AsNoTracking()
            .Where(group => group.StudioId.HasValue && ids.Contains(group.StudioId.Value))
            .GroupBy(group => group.StudioId!.Value)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var performerCounts = await db.Videos
            .AsNoTracking()
            .Where(video => video.StudioId.HasValue && ids.Contains(video.StudioId.Value))
            .Join(
                db.Set<VideoPerformer>().AsNoTracking(),
                video => video.Id,
                videoPerformer => videoPerformer.VideoId,
                (video, videoPerformer) => new { StudioId = video.StudioId!.Value, videoPerformer.PerformerId })
            .GroupBy(item => item.StudioId)
            .Select(group => new { group.Key, Count = group.Select(item => item.PerformerId).Distinct().Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var childStudioCounts = await db.Studios
            .AsNoTracking()
            .Where(studio => studio.ParentId.HasValue && ids.Contains(studio.ParentId.Value))
            .GroupBy(studio => studio.ParentId!.Value)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var audioCounts = await db.Audios
            .AsNoTracking()
            .Where(audio => audio.StudioId.HasValue && ids.Contains(audio.StudioId.Value))
            .GroupBy(audio => audio.StudioId!.Value)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var textCounts = await db.TextDocuments
            .AsNoTracking()
            .Where(text => text.StudioId.HasValue && ids.Contains(text.StudioId.Value))
            .GroupBy(text => text.StudioId!.Value)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);

        return ids.ToDictionary(
            id => id,
            id => new StudioUsageCounts(
                videoCounts.GetValueOrDefault(id),
                imageCounts.GetValueOrDefault(id),
                galleryCounts.GetValueOrDefault(id),
                groupCounts.GetValueOrDefault(id),
                performerCounts.GetValueOrDefault(id),
                childStudioCounts.GetValueOrDefault(id),
                audioCounts.GetValueOrDefault(id),
                textCounts.GetValueOrDefault(id)));
    }

    // ===== Merge =====

    [HttpPost("merge")]
    [RequiresPermission(Permissions.StudiosWrite, Permissions.StudiosDelete)]
    [RequiresEntityAccess(EntityKinds.Studio, Permissions.StudiosWrite, ActionArgumentName = "dto", PropertyName = "TargetId")]
    [RequiresEntityAccess(EntityKinds.Studio, Permissions.StudiosDelete, ActionArgumentName = "dto", PropertyName = "SourceIds")]
    public async Task<ActionResult<StudioDto>> MergeStudios([FromBody] StudioMergeDto dto, CancellationToken ct)
    {
        StudioMergeResult result;
        try
        {
            var service = studioMergeService ?? new StudioMergeService(
                db,
                eventBus,
                new PostgresEntityExternalReferenceInspector(db));
            result = await service.MergeAsync(dto.TargetId, dto.SourceIds, ct);
        }
        catch (KeyNotFoundException)
        {
            return NotFound("Target studio not found");
        }
        catch (EntityMergeBlockedException exception)
        {
            return Conflict(new
            {
                code = "STUDIO_MERGE_EXTENSION_REFERENCES",
                message = exception.Message,
                exception.ReferenceCount,
                exception.AffectedEntityCount,
                exception.HasUninspectableReferences,
            });
        }
        var merged = await studioRepo.GetByIdWithRelationsAsync(result.TargetId, ct);
        return Ok(await MapToDetailDtoAsync(merged!, ct));
    }

    // ===== Metadata Server =====

    [HttpGet("{id:int}/metadata-server/search")]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<IReadOnlyList<MetadataServerStudioMatchDto>>> SearchMetadataServer(int id, [FromQuery] string? term, [FromQuery] string? endpoint, CancellationToken ct)
    {
        var studio = await studioRepo.GetByIdWithRelationsAsync(id, ct);
        if (studio == null) return NotFound();

        if (string.IsNullOrWhiteSpace(term))
        {
            var existingRemoteId = studio.RemoteIds?.FirstOrDefault(s => string.IsNullOrWhiteSpace(endpoint) || string.Equals(s.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));
            if (existingRemoteId != null)
            {
                var existing = await metadataServerService.GetStudioMatchAsync(existingRemoteId.Endpoint, existingRemoteId.RemoteId, ct);
                if (existing != null)
                    return Ok(new[] { existing });

                term = studio.Name;
            }
            else
            {
                term = studio.Name;
            }
        }

        return Ok(await metadataServerService.SearchStudiosAsync(term, endpoint, ct));
    }

    [HttpPost("metadata-server/find-by-ids")]
    public async Task<ActionResult<IReadOnlyList<MetadataServerStudioMatchDto>>> FindMetadataServerStudiosByIds([FromBody] MetadataServerFindByIdsRequestDto dto, CancellationToken ct)
    {
        if (dto.Ids.Count == 0)
            return Ok(Array.Empty<MetadataServerStudioMatchDto>());

        return Ok(await metadataServerService.GetStudioMatchesAsync(dto.Endpoint, dto.Ids, ct));
    }

    [HttpPost("{id:int}/metadata-server/import")]
    [RequiresPermission(Permissions.StudiosWrite)]
    [RequiresEntityAccess(EntityKinds.Studio, Permissions.StudiosWrite)]
    public async Task<ActionResult<StudioDto>> ImportFromMetadataServer(int id, [FromBody] MetadataServerStudioImportRequestDto dto, CancellationToken ct)
    {
        var studio = await db.Studios
            .Include(s => s.RemoteIds)
            .Include(s => s.Aliases)
            .Include(s => s.Urls)
            .Include(s => s.StudioTags).ThenInclude(st => st.Tag).ThenInclude(tag => tag!.TagGroup)
            .Include(s => s.Parent)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
        if (studio == null) return NotFound();

        try
        {
            var imported = await metadataServerService.MergeStudioAsync(studio, dto.Endpoint, dto.StudioId, dto, ct);
            if (!imported) return NotFound();
            await db.SaveChangesAsync(ct);
        }
        catch (EntityNameConflictException exception)
        {
            return Conflict(new { code = "STUDIO_NAME_CONFLICT", message = exception.Message });
        }
        eventBus?.Publish(new EntityEvent(EventType.StudioUpdated, "Studio", studio.Id));
        var updated = await studioRepo.GetByIdWithRelationsAsync(id, ct);
        return Ok(await MapToDetailDtoAsync(updated!, ct));
    }

    [HttpPost("{id:int}/metadata-server/submit-draft")]
    [RequiresPermission(Permissions.StudiosWrite)]
    [RequiresEntityAccess(EntityKinds.Studio, Permissions.StudiosWrite)]
    public async Task<IActionResult> SubmitStudioDraft(int id, [FromBody] MetadataServerEndpointDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Endpoint))
            return BadRequest(new { message = "Endpoint is required" });

        var studio = await db.Studios
            .Include(s => s.RemoteIds)
            .Include(s => s.Aliases)
            .Include(s => s.Urls)
            .Include(s => s.Parent).ThenInclude(parent => parent!.RemoteIds)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
        if (studio == null) return NotFound();

        try
        {
            var draftId = await metadataServerService.SubmitStudioDraftAsync(studio, dto.Endpoint, ct);
            return Ok(new { draftId });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("StudioDraftInput", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("submitStudioDraft", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "This MetadataServer does not support studio draft submission. Use Studio Tagger search/import instead." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("metadata-server/batch-tag")]
    [RequiresPermission(Permissions.StudiosWrite)]
    [RequiresEntityAccess(EntityKinds.Studio, Permissions.StudiosWrite, ActionArgumentName = "dto", PropertyName = "Ids")]
    public async Task<ActionResult<object>> BatchTagFromMetadataServer([FromBody] MetadataServerStudioBatchTagRequestDto dto, [FromServices] IJobService jobService, [FromServices] IServiceScopeFactory scopeFactory, [FromServices] IAuthorizationService authorizationService, [FromServices] ICurrentPrincipalAccessor principalAccessor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Endpoint))
            return BadRequest(new { message = "Endpoint is required" });

        var ids = await ResolveSelectedStudioIdsAsync(dto, ct);
        if (ids.Count == 0)
            return BadRequest(new { message = "No studios selected for batch tagging" });

        var principal = principalAccessor.Current;
        if (principal == null)
            return Forbid();

        foreach (var id in ids)
        {
            var result = await authorizationService.AuthorizeAsync(
                principal,
                Permissions.StudiosWrite,
                new EntityRef(EntityKinds.Studio, id.ToString()),
                ct);

            if (!result.Allowed)
                return Forbid();
        }

        var jobId = jobService.EnqueueFor(
            JobOwner.FromPrincipal(principal),
            "metadata-server:studios",
            $"Tagging {ids.Count} studios from {dto.Endpoint}",
            async (progress, jobCt) =>
            {
                using var scope = scopeFactory.CreateScope();
                var metadataServerService = scope.ServiceProvider.GetRequiredService<MetadataServerService>();
                await metadataServerService.BatchTagStudiosAsync(dto.Endpoint, ids, dto.RefreshAlreadyTagged, dto.ExcludeFields, dto.CreateParentStudios, progress, jobCt);
            });

        return Ok(new { jobId, itemCount = ids.Count });
    }

    private async Task<List<int>> ResolveSelectedStudioIdsAsync(MetadataServerStudioBatchTagRequestDto dto, CancellationToken ct)
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
            var (items, totalCount) = await studioRepo.FindAsync(dto.Filter, new FindFilter
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
}
