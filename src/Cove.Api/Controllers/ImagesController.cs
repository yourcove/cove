using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Cove.Api.Http;
using Cove.Api.Services;
using Cove.Api.Helpers;
using Cove.Core.Auth;
using Cove.Core.Common;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Helpers;
using Cove.Core.Enums;
using Cove.Core.Events;
using Cove.Core.Interfaces;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[RequiresPermission(Permissions.ImagesRead)]
public class ImagesController(IImageRepository imageRepo, Data.CoveContext db, IUserEngagementService engagementService, CustomFieldService customFields, IScanService scanService, ImageDeletionService imageDeletionService, ITagProvenanceService? tagProvenanceService = null, ICurrentPrincipalAccessor? principalAccessor = null, IFieldProvenanceService? fieldProvenanceService = null, BulkDeletionJobService? bulkDeletionJobService = null) : ControllerBase
{
    private bool CanReadFiles => principalAccessor?.Current?.Has(Permissions.FilesRead) == true;
    private static string GetVisibleBasename(string path, string basename) => string.IsNullOrWhiteSpace(basename) ? System.IO.Path.GetFileName(path) : basename;

    [HttpGet]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<PaginatedResponse<ImageDto>>> Find(
        [FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int perPage = 25,
        [FromQuery] string? sort = null, [FromQuery] string? direction = null,
        [FromQuery] int? seed = null,
        [FromQuery] string? sorts = null,
        [FromQuery] string? title = null, [FromQuery] int? rating = null,
        [FromQuery] bool? organized = null, [FromQuery] int? studioId = null,
        [FromQuery] string? tagIds = null, [FromQuery] string? performerIds = null,
        [FromQuery] int? galleryId = null, [FromQuery] string? ids = null,
        CancellationToken ct = default)
    {
        var filter = new ImageFilter
        {
            Ids = QueryParsing.ParseIntList(ids)?.ToList(),
            Title = title, Rating = rating, Organized = organized, StudioId = studioId,
            TagIds = QueryParsing.ParseIntList(tagIds)?.ToList(), PerformerIds = QueryParsing.ParseIntList(performerIds)?.ToList(),
            GalleryId = galleryId
        };
        var sortClauses = SortClause.Parse(sorts);
        var primarySort = sortClauses.FirstOrDefault();
        var findFilter = new FindFilter
        {
            Q = q, Page = page, PerPage = perPage, Sort = primarySort?.Key ?? sort,
            Direction = primarySort?.Direction ?? (direction == "desc" ? SortDirection.Desc : SortDirection.Asc),
            Sorts = sortClauses.Count > 0 ? sortClauses : null,
            Seed = seed,
        };

        var (items, totalCount) = await imageRepo.FindAsync(filter, findFilter, ct);
        var dtos = await MapListToDtos(items, ct);
        return Ok(new PaginatedResponse<ImageDto>(dtos, totalCount, page, perPage));
    }

    [HttpPost("find")]
    public async Task<ActionResult<PaginatedResponse<ImageDto>>> FindPost([FromBody] FilteredQueryRequest<ImageFilter> req, CancellationToken ct)
    {
        var findFilter = req.FindFilter ?? new FindFilter();
        var filter = req.ObjectFilter ?? new ImageFilter();
        var (items, totalCount) = await imageRepo.FindAsync(filter, findFilter, ct);
        var dtos = await MapListToDtos(items, ct);
        return Ok(new PaginatedResponse<ImageDto>(dtos, totalCount, findFilter.Page, findFilter.PerPage));
    }

    [HttpPost("aggregate")]
    public async Task<ActionResult<ImageAggregate>> Aggregate([FromBody] FilteredQueryRequest<ImageFilter> req, CancellationToken ct)
    {
        var filter = req.ObjectFilter ?? new ImageFilter();
        if (req.Ids is { Count: > 0 }) filter.Ids = req.Ids;
        return Ok(await imageRepo.AggregateAsync(filter, req.FindFilter, ct));
    }

    [HttpGet("{id:int}")]
    [AllowShareLinkAccess]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<ImageDto>> GetById(int id, CancellationToken ct)
    {
        var image = await imageRepo.GetByIdWithRelationsAsync(id, ct);
        if (image == null) return NotFound();
        return Ok(await MapToDtoWithProvenanceAsync(image, ct));
    }

    [HttpPost]
    [RequiresPermission(Permissions.ImagesWrite)]
    [RequiresEntityAccess(EntityKinds.Gallery, Permissions.GalleriesRead, RouteValueName = null, ActionArgumentName = "dto", PropertyName = "GalleryIds", DeniedBehavior = EntityAccessDeniedBehavior.Forbidden)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsRead, RouteValueName = null, ActionArgumentName = "dto", PropertyName = "GroupIds.GroupId", DeniedBehavior = EntityAccessDeniedBehavior.Forbidden)]
    public async Task<ActionResult<ImageDto>> Create([FromBody] ImageCreateDto dto, CancellationToken ct)
    {
        var date = PartialDate.Parse(dto.Date);
        var image = new Image
        {
            Title = dto.Title,
            Code = dto.Code,
            Details = dto.Details,
            Photographer = dto.Photographer,
            Organized = dto.Organized,
            StudioId = dto.StudioId,
            Date = date.Value,
            DatePrecision = date.Precision
        };

        if (dto.Urls?.Count > 0)
            image.Urls = dto.Urls.Select(u => new ImageUrl { Url = u }).ToList();
        if (dto.TagIds?.Count > 0)
            image.ImageTags = dto.TagIds.Select(tagId => new ImageTag { TagId = tagId }).ToList();
        if (dto.PerformerIds?.Count > 0)
            image.ImagePerformers = dto.PerformerIds.Select(performerId => new ImagePerformer { PerformerId = performerId }).ToList();
        if (dto.GalleryIds?.Count > 0)
            image.ImageGalleries = dto.GalleryIds.Select(gid => new ImageGallery { GalleryId = gid }).ToList();

        image = await imageRepo.AddAsync(image, ct);
        if (dto.GroupIds != null)
        {
            await ReplaceWholeImageGroupItemsAsync(image.Id, dto.GroupIds, image.Title, ct);
            await db.SaveChangesAsync(ct);
        }
        if (dto.CustomFields != null)
            await customFields.SaveValuesAsync(CustomFieldEntityTypes.Image, image.Id, dto.CustomFields, ct);
        if (dto.Rating.HasValue)
            await engagementService.SetRatingAsync(AffinityHostType.Image, image.Id, dto.Rating, cancellationToken: ct);
        if (dto.TagIds?.Count > 0 && tagProvenanceService != null)
        {
            await tagProvenanceService.SyncTagSetAsync(AffinityHostType.Image, image.Id, [], dto.TagIds, cancellationToken: ct);
            await db.SaveChangesAsync(ct);
        }
        var result = await imageRepo.GetByIdWithRelationsAsync(image.Id, ct);
        return CreatedAtAction(nameof(GetById), new { id = image.Id }, await MapToDtoWithProvenanceAsync(result!, ct));
    }

    [HttpPut("{id:int}")]
    [RequiresPermission(Permissions.ImagesWrite)]
    [RequiresEntityAccess(EntityKinds.Image, Permissions.ImagesWrite)]
    [RequiresEntityAccess(EntityKinds.Gallery, Permissions.GalleriesRead, RouteValueName = null, ActionArgumentName = "dto", PropertyName = "GalleryIds", DeniedBehavior = EntityAccessDeniedBehavior.Forbidden)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsRead, RouteValueName = null, ActionArgumentName = "dto", PropertyName = "GroupIds.GroupId", DeniedBehavior = EntityAccessDeniedBehavior.Forbidden)]
    public async Task<ActionResult<ImageDto>> Update(int id, [FromBody] ImageUpdateDto dto, CancellationToken ct)
    {
        var image = await imageRepo.GetByIdWithRelationsAsync(id, ct);
        if (image == null) return NotFound();
        var previousTagIds = dto.TagIds != null ? image.ImageTags.Select(imageTag => imageTag.TagId).ToArray() : [];
        var clearFields = dto.ClearFields?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        if (dto.Title != null) image.Title = string.IsNullOrWhiteSpace(dto.Title) ? null : dto.Title;
        if (dto.Code != null) image.Code = string.IsNullOrWhiteSpace(dto.Code) ? null : dto.Code;
        if (dto.Details != null) image.Details = string.IsNullOrWhiteSpace(dto.Details) ? null : dto.Details;
        if (dto.Photographer != null) image.Photographer = string.IsNullOrWhiteSpace(dto.Photographer) ? null : dto.Photographer;
        if (dto.Organized.HasValue) image.Organized = dto.Organized.Value;
        if (dto.StudioId.HasValue) image.StudioId = dto.StudioId;
        if (dto.Date != null) { var date = PartialDate.Parse(dto.Date); image.Date = date.Value; image.DatePrecision = date.Precision; }
        if (clearFields.Contains("date")) image.Date = null;
        if (clearFields.Contains("studioId")) image.StudioId = null;

        if (dto.Urls != null)
        {
            if (MetadataCollectionUpdater.ReplaceIfChanged(image.Urls, dto.Urls, item => item.Url, url => new ImageUrl { Url = url, ImageId = id }, StringComparer.Ordinal))
                MetadataCollectionUpdater.Touch(image);
        }
        if (dto.TagIds != null)
        {
            if (MetadataCollectionUpdater.ReplaceIfChanged(image.ImageTags, dto.TagIds, item => item.TagId, tagId => new ImageTag { TagId = tagId, ImageId = id }))
                MetadataCollectionUpdater.Touch(image);
        }
        if (dto.PerformerIds != null)
        {
            if (MetadataCollectionUpdater.ReplaceIfChanged(image.ImagePerformers, dto.PerformerIds, item => item.PerformerId, performerId => new ImagePerformer { PerformerId = performerId, ImageId = id }))
                MetadataCollectionUpdater.Touch(image);
        }
        if (dto.GalleryIds != null)
        {
            if (MetadataCollectionUpdater.ReplaceIfChanged(image.ImageGalleries, dto.GalleryIds, item => item.GalleryId, galleryId => new ImageGallery { GalleryId = galleryId, ImageId = id }))
                MetadataCollectionUpdater.Touch(image);
        }
        if (dto.GroupIds != null)
        {
            if (await ReplaceWholeImageGroupItemsAsync(id, dto.GroupIds, image.Title, ct))
                MetadataCollectionUpdater.Touch(image);
        }
        if (dto.TagIds != null && tagProvenanceService != null)
        {
            await tagProvenanceService.SyncTagSetAsync(
                AffinityHostType.Image,
                id,
                previousTagIds,
                image.ImageTags.Select(imageTag => imageTag.TagId).ToArray(),
                cancellationToken: ct);
        }

        await imageRepo.UpdateAsync(image, ct);
        if (dto.CustomFields != null && await customFields.SaveValuesAsync(CustomFieldEntityTypes.Image, id, dto.CustomFields, ct))
        {
            MetadataCollectionUpdater.Touch(image);
            await imageRepo.UpdateAsync(image, ct);
        }
        if (dto.Rating.HasValue)
            await engagementService.SetRatingAsync(AffinityHostType.Image, id, dto.Rating, cancellationToken: ct);
        var updated = await imageRepo.GetByIdWithRelationsAsync(id, ct);
        return Ok(await MapToDtoWithProvenanceAsync(updated!, ct));
    }

    [HttpDelete("{id:int}")]
    [RequiresPermission(Permissions.ImagesDelete)]
    [RequiresPermissionWhenTrue(Permissions.ImagesDeleteFile, ActionArgumentName = "deleteFile")]
    [RequiresEntityAccess(EntityKinds.Image, Permissions.ImagesDelete)]
    public async Task<IActionResult> Delete(int id, [FromQuery] bool deleteFile = false, [FromQuery] bool deleteGenerated = false, CancellationToken ct = default)
    {
        if (deleteFile && principalAccessor?.Current?.Has(Permissions.ImagesDeleteFile) != true)
            return Forbid();
        return await imageDeletionService.DeleteAsync(id, deleteFile, deleteGenerated, ct) ? NoContent() : NotFound();
    }

    [HttpPost("{id:int}/rescan")]
    [RequiresPermission(Permissions.LibraryScan)]
    [RequiresEntityAccess(EntityKinds.Image, Permissions.LibraryScan)]
    public async Task<IActionResult> Rescan(int id, CancellationToken ct)
    {
        var image = await db.Images.AsNoTracking()
            .Include(item => item.Files)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (image == null) return NotFound();

        var filePath = image.Files
            .Select(file => file.Path)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (string.IsNullOrWhiteSpace(filePath)) return BadRequest("Image has no files");

        var jobId = scanService.StartScan(new ScanOperationOptions
        {
            Paths = [filePath],
            Rescan = true,
        });
        return Ok(new { jobId });
    }

    private async Task<ImageDto> MapToDtoWithProvenanceAsync(Image image, CancellationToken cancellationToken = default)
    {
        var tagIds = image.ImageTags
            .Where(imageTag => imageTag.Tag != null)
            .Select(imageTag => imageTag.Tag!.Id)
            .Distinct()
            .ToArray();
        var provenanceLookup = tagProvenanceService == null
            ? null
            : await tagProvenanceService.GetLookupAsync(AffinityHostType.Image, image.Id, tagIds, cancellationToken);

        var snapshot = (await engagementService.GetSnapshotsAsync(AffinityHostType.Image, [image.Id], cancellationToken)).GetValueOrDefault(image.Id);
        var customFieldValues = await customFields.GetValuesAsync(CustomFieldEntityTypes.Image, image.Id, cancellationToken);
        var groups = await GetGroupsAsync(image.Id, cancellationToken);
        var contextTagApplications = await GetContextTagApplicationsAsync(image.Id, cancellationToken);
        var performerCounts = await PerformerSummaryCountsLoader.LoadAsync(db, image.ImagePerformers.Select(link => link.PerformerId), cancellationToken, principalAccessor);
        var fieldProvenance = fieldProvenanceService == null
            ? null
            : (await fieldProvenanceService.GetForHostAsync(AffinityHostType.Image, image.Id, cancellationToken)).ToList();
        return MapToDto(image, customFieldValues, null, groups, provenanceLookup, snapshot, principalAccessor?.Current?.UserId != null, contextTagApplications, fieldProvenance, performerCounts);
    }

    private ImageDto MapToDto(Image i, Dictionary<string, object>? customFieldValues = null, int? galleryCount = null, List<GroupSummaryDto>? groups = null, IReadOnlyDictionary<int, List<TagProvenanceDto>>? provenanceLookup = null, UserEngagementSnapshot? engagement = null, bool preferUserSnapshot = false, List<TagApplicationDto>? contextTagApplications = null, List<FieldProvenanceDto>? fieldProvenance = null, IReadOnlyDictionary<int, PerformerSummaryCounts>? performerCounts = null) => new(
        i.Id, i.Title, i.Code, i.Details, i.Photographer,
        i.Organized,
        i.StudioId, i.Studio?.Name,
        PartialDate.Format(i.Date, i.DatePrecision),
        i.Urls.Select(u => u.Url).ToList(),
        i.ImageTags.Where(it => it.Tag != null).Select(it => TagDtoMapping.MapTagDto(it.Tag!, GetTagProvenance(provenanceLookup, it.Tag!.Id))).OrderForDisplay().ToList(),
        i.ImagePerformers.Where(ip => ip.Performer != null).Select(ip => ip.Performer!).OrderForDisplay().Select(performer => new PerformerSummaryDto(performer.Id, performer.Name, performer.Disambiguation, performer.Gender?.ToString(), PartialDate.Format(performer.Birthdate, performer.BirthdatePrecision), performer.Favorite, EntityImageUrls.PerformerOrNull(ControllerContext.HttpContext, performer), performerCounts?.GetValueOrDefault(performer.Id)?.VideoCount ?? 0, performerCounts?.GetValueOrDefault(performer.Id)?.ImageCount ?? 0, performerCounts?.GetValueOrDefault(performer.Id)?.GalleryCount ?? 0, performerCounts?.GetValueOrDefault(performer.Id)?.AudioCount ?? 0, performerCounts?.GetValueOrDefault(performer.Id)?.TextCount ?? 0, performer.Country, PartialDate.Format(performer.DeathDate, performer.DeathDatePrecision))).ToList(),
        galleryCount ?? i.GalleryCount,
        i.ImageGalleries?.Select(ig => ig.GalleryId).ToList() ?? [],
        i.ImageGalleries?.Where(ig => ig.Gallery != null).Select(ig => new GallerySummaryDto(ig.GalleryId, ig.Gallery!.Title, PartialDate.Format(ig.Gallery.Date, ig.Gallery.DatePrecision))).ToList() ?? [],
        groups ?? [],
        i.Files?.Select(f => new ImageFileDto(
            f.Id,
            CanReadFiles ? f.Path : string.Empty,
            GetVisibleBasename(f.Path, f.Basename),
            f.Format ?? "",
            f.Width,
            f.Height,
            f.Size)).ToList() ?? [],
        customFieldValues,
        i.CreatedAt.ToString("o"), i.UpdatedAt.ToString("o"),
        contextTagApplications,
        fieldProvenance
    );

    private async Task<List<ImageDto>> MapListToDtos(IReadOnlyList<Image> items, CancellationToken ct)
    {
        if (items.Count == 0)
            return [];

        var preferUserSnapshot = principalAccessor?.Current?.UserId != null;
        var snapshots = await engagementService.GetSnapshotsAsync(AffinityHostType.Image, items.Select(item => item.Id), ct);
        var customFieldValues = await customFields.GetValuesAsync(CustomFieldEntityTypes.Image, items.Select(item => item.Id), ct);
        var groupLookup = await GetGroupsLookupAsync(items.Select(item => item.Id), ct);
        return items.Select(i => MapListToDto(i, i.GalleryCount, GetCustomFields(customFieldValues, i.Id), GetGroups(groupLookup, i.Id), snapshots.GetValueOrDefault(i.Id), preferUserSnapshot)).ToList();
    }

    private ImageDto MapListToDto(Image i, int galleryCount, Dictionary<string, object>? customFieldValues = null, List<GroupSummaryDto>? groups = null, UserEngagementSnapshot? engagement = null, bool preferUserSnapshot = false) => new(
        i.Id, i.Title, i.Code, i.Details, i.Photographer,
        i.Organized,
        i.StudioId, i.Studio?.Name,
        PartialDate.Format(i.Date, i.DatePrecision),
        i.Urls.Select(u => u.Url).ToList(),
        i.ImageTags.Where(it => it.Tag != null).Select(it => TagDtoMapping.MapTagDto(it.Tag!)).OrderForDisplay().ToList(),
        i.ImagePerformers.Where(ip => ip.Performer != null).Select(ip => ip.Performer!).OrderForDisplay().Select(performer => new PerformerSummaryDto(performer.Id, performer.Name, performer.Disambiguation, performer.Gender?.ToString(), PartialDate.Format(performer.Birthdate, performer.BirthdatePrecision), performer.Favorite, EntityImageUrls.PerformerOrNull(ControllerContext.HttpContext, performer), Country: performer.Country, DeathDate: PartialDate.Format(performer.DeathDate, performer.DeathDatePrecision))).ToList(),
        galleryCount,
        i.ImageGalleries?.Select(ig => ig.GalleryId).ToList() ?? [],
        i.ImageGalleries?.Where(ig => ig.Gallery != null).Select(ig => new GallerySummaryDto(ig.GalleryId, ig.Gallery!.Title, PartialDate.Format(ig.Gallery.Date, ig.Gallery.DatePrecision))).ToList() ?? [],
        groups ?? [],
        i.Files?.Select(f => new ImageFileDto(
            f.Id,
            CanReadFiles ? f.Path : string.Empty,
            GetVisibleBasename(f.Path, f.Basename),
            f.Format ?? "",
            f.Width,
            f.Height,
            f.Size)).ToList() ?? [],
        customFieldValues,
        i.CreatedAt.ToString("o"), i.UpdatedAt.ToString("o")
    );

    private static Dictionary<string, object>? GetCustomFields(IReadOnlyDictionary<int, Dictionary<string, object>> lookup, int id)
        => lookup.TryGetValue(id, out var values) && values.Count > 0 ? values : null;

    private async Task<List<TagApplicationDto>?> GetContextTagApplicationsAsync(int imageId, CancellationToken ct)
    {
        var applications = await db.TagApplications.AsNoTracking()
            .Where(item => item.HostType == AffinityHostType.Image && item.HostId == imageId)
            .Include(item => item.Tag).ThenInclude(tag => tag!.Aliases)
            .Include(item => item.Tag).ThenInclude(tag => tag!.TagGroup)
            .AsSplitQuery()
            .OrderBy(item => item.ContextType)
            .ThenBy(item => item.ContextId)
            .ThenBy(item => item.Tag!.TagGroupId.HasValue ? 0 : 1)
            .ThenBy(item => item.Tag!.TagGroup != null ? item.Tag.TagGroup.SortOrder : int.MaxValue)
            .ThenBy(item => item.Tag!.TagGroup != null ? item.Tag.TagGroup.Name : null)
            .ThenBy(item => item.Tag!.SortName ?? item.Tag.Name)
            .ThenBy(item => item.TagId)
            .ToListAsync(ct);

        return applications.Count == 0 ? null : applications.Select(TagApplicationsController.Map).ToList();
    }

    // ===== Activity Tracking =====

    [HttpPost("{id:int}/like")]
    [RequiresPermission(Permissions.ImagesWrite)]
    [RequiresEntityAccess(EntityKinds.Image, Permissions.ImagesWrite)]
    public async Task<ActionResult<int>> IncrementLike(int id, CancellationToken ct)
    {
        var snapshot = await engagementService.IncrementImageLikeAsync(id, ct);
        if (snapshot == null) return NotFound();
        return Ok(snapshot.LikeCount);
    }

    [HttpPost("{id:int}/like/historical")]
    [RequiresPermission(Permissions.ImagesWrite)]
    [RequiresEntityAccess(EntityKinds.Image, Permissions.ImagesWrite)]
    public async Task<ActionResult<int>> AddHistoricalLike(int id, HistoricalLikeDto request, CancellationToken ct)
    {
        var at = NormalizeHistoricalLikeAt(request.At);
        if (at > DateTime.UtcNow) return BadRequest("Historical likes must be dated in the past.");
        var snapshot = await engagementService.AddHistoricalLikeAsync(AffinityHostType.Image, id, at, ct);
        return snapshot == null ? NotFound() : Ok(snapshot.LikeCount);
    }

    [HttpGet("{id:int}/history")]
    [RequiresEntityAccess(EntityKinds.Image, Permissions.ImagesRead)]
    public async Task<ActionResult<VideoHistoryDto>> GetHistory(int id, CancellationToken ct)
    {
        var history = await engagementService.GetHistoryAsync(AffinityHostType.Image, id, ct);
        return history is null ? NotFound() : Ok(history);
    }

    [HttpDelete("{id:int}/like/history")]
    [RequiresPermission(Permissions.ImagesWrite)]
    [RequiresEntityAccess(EntityKinds.Image, Permissions.ImagesWrite)]
    public async Task<IActionResult> DeleteLikeFromHistory(int id, [FromQuery] DateTime at, CancellationToken ct)
    {
        var snapshot = await engagementService.DeleteLikeAtAsync(AffinityHostType.Image, id, at, ct);
        if (snapshot == null) return NotFound();
        return NoContent();
    }

    [HttpDelete("{id:int}/like")]
    [RequiresPermission(Permissions.ImagesWrite)]
    [RequiresEntityAccess(EntityKinds.Image, Permissions.ImagesWrite)]
    public async Task<ActionResult<int>> DecrementLike(int id, CancellationToken ct)
    {
        var snapshot = await engagementService.DecrementImageLikeAsync(id, ct);
        if (snapshot == null) return NotFound();
        return Ok(snapshot.LikeCount);
    }

    [HttpPost("{id:int}/like/reset")]
    [RequiresPermission(Permissions.ImagesWrite)]
    [RequiresEntityAccess(EntityKinds.Image, Permissions.ImagesWrite)]
    public async Task<ActionResult<int>> ResetLike(int id, CancellationToken ct)
    {
        var snapshot = await engagementService.ResetImageLikeAsync(id, ct);
        if (snapshot == null) return NotFound();
        return Ok(snapshot.LikeCount);
    }

    private static DateTime NormalizeHistoricalLikeAt(DateTime at)
        => at.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : at.ToUniversalTime();

    // ===== Bulk Operations =====

    [HttpDelete("bulk")]
    [RequiresPermission(Permissions.ImagesDelete)]
    [RequiresPermissionWhenTrue(Permissions.ImagesDeleteFile, ActionArgumentName = "dto", PropertyName = "DeleteFiles")]
    [RequiresEntityAccess(EntityKinds.Image, Permissions.ImagesDelete, ActionArgumentName = "dto", PropertyName = "Ids")]
    public IActionResult QueueBulkDelete([FromBody] BatchDeleteDto dto, CancellationToken ct)
    {
        if (dto.DeleteFiles && principalAccessor?.Current?.Has(Permissions.ImagesDeleteFile) != true)
            return Forbid();

        var ids = dto.Ids.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return BadRequest("Select at least one image to delete.");

        var queued = bulkDeletionJobService!.Start(
            principalAccessor?.Current,
            BulkDeletionEntityKind.Image,
            ids,
            dto.DeleteFiles,
            dto.DeleteGenerated);
        return Accepted(queued);
    }

    [HttpPost("bulk")]
    [RequiresPermission(Permissions.ImagesWrite)]
    [RequiresEntityAccess(EntityKinds.Image, Permissions.ImagesWrite, ActionArgumentName = "dto", PropertyName = "Ids")]
    [RequiresEntityAccess(EntityKinds.Gallery, Permissions.GalleriesRead, RouteValueName = null, ActionArgumentName = "dto", PropertyName = "GalleryIds", DeniedBehavior = EntityAccessDeniedBehavior.Forbidden)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsRead, RouteValueName = null, ActionArgumentName = "dto", PropertyName = "GroupIds.GroupId", DeniedBehavior = EntityAccessDeniedBehavior.Forbidden)]
    public async Task<IActionResult> BulkUpdate([FromBody] BulkImageUpdateDto dto, CancellationToken ct)
    {
        var images = await db.Images
            .Include(i => i.ImageTags)
            .Include(i => i.ImagePerformers)
            .Include(i => i.ImageGalleries)
            .AsSplitQuery()
            .Where(i => dto.Ids.Contains(i.Id))
            .ToListAsync(ct);
        var clearFields = dto.ClearFields?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        foreach (var image in images)
        {
            var previousTagIds = dto.TagIds != null ? image.ImageTags.Select(imageTag => imageTag.TagId).ToArray() : [];

            if (clearFields.Contains("studioId")) image.StudioId = null;
            if (clearFields.Contains("date")) image.Date = null;
            if (clearFields.Contains("code")) image.Code = null;
            if (clearFields.Contains("details")) image.Details = null;
            if (clearFields.Contains("photographer")) image.Photographer = null;
            if (dto.Organized.HasValue) image.Organized = dto.Organized.Value;
            if (dto.StudioId.HasValue) image.StudioId = dto.StudioId;
            if (dto.Date != null) { var date = PartialDate.Parse(dto.Date); image.Date = date.Value; image.DatePrecision = date.Precision; }
            if (dto.Code != null) image.Code = dto.Code;
            if (dto.Details != null) image.Details = dto.Details;
            if (dto.Photographer != null) image.Photographer = dto.Photographer;

            if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Set)
            {
                image.ImageTags.Clear();
                image.ImageTags = dto.TagIds.Select(tid => new ImageTag { TagId = tid, ImageId = image.Id }).ToList();
            }
            else if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Add)
            {
                var existing = image.ImageTags.Select(it => it.TagId).ToHashSet();
                foreach (var tid in dto.TagIds.Where(t => !existing.Contains(t)))
                    image.ImageTags.Add(new ImageTag { TagId = tid, ImageId = image.Id });
            }
            else if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Remove)
            {
                image.ImageTags = image.ImageTags.Where(it => !dto.TagIds.Contains(it.TagId)).ToList();
            }

            if (dto.TagIds != null && tagProvenanceService != null)
            {
                await tagProvenanceService.SyncTagSetAsync(
                    AffinityHostType.Image,
                    image.Id,
                    previousTagIds,
                    image.ImageTags.Select(imageTag => imageTag.TagId).ToArray(),
                    cancellationToken: ct);
            }

            if (dto.PerformerIds != null && dto.PerformerMode == BulkUpdateMode.Set)
            {
                image.ImagePerformers.Clear();
                image.ImagePerformers = dto.PerformerIds.Select(pid => new ImagePerformer { PerformerId = pid, ImageId = image.Id }).ToList();
            }
            else if (dto.PerformerIds != null && dto.PerformerMode == BulkUpdateMode.Add)
            {
                var existing = image.ImagePerformers.Select(ip => ip.PerformerId).ToHashSet();
                foreach (var pid in dto.PerformerIds.Where(p => !existing.Contains(p)))
                    image.ImagePerformers.Add(new ImagePerformer { PerformerId = pid, ImageId = image.Id });
            }
            else if (dto.PerformerIds != null && dto.PerformerMode == BulkUpdateMode.Remove)
            {
                image.ImagePerformers = image.ImagePerformers.Where(ip => !dto.PerformerIds.Contains(ip.PerformerId)).ToList();
            }

            if (dto.GalleryIds != null && dto.GalleryMode == BulkUpdateMode.Set)
            {
                image.ImageGalleries.Clear();
                image.ImageGalleries = dto.GalleryIds.Select(gid => new ImageGallery { GalleryId = gid, ImageId = image.Id }).ToList();
            }
            else if (dto.GalleryIds != null && dto.GalleryMode == BulkUpdateMode.Add)
            {
                var existing = image.ImageGalleries.Select(ig => ig.GalleryId).ToHashSet();
                foreach (var gid in dto.GalleryIds.Where(g => !existing.Contains(g)))
                    image.ImageGalleries.Add(new ImageGallery { GalleryId = gid, ImageId = image.Id });
            }
            else if (dto.GalleryIds != null && dto.GalleryMode == BulkUpdateMode.Remove)
            {
                image.ImageGalleries = image.ImageGalleries.Where(ig => !dto.GalleryIds.Contains(ig.GalleryId)).ToList();
            }
        }

        await db.SaveChangesAsync(ct);
        if (dto.Rating.HasValue)
        {
            foreach (var image in images)
                await engagementService.SetRatingAsync(AffinityHostType.Image, image.Id, dto.Rating, cancellationToken: ct);
        }
        return Ok(new BulkUpdateResult(images.Select(image => image.Id).ToList()));
    }


    private async Task<List<GroupSummaryDto>> GetGroupsAsync(int imageId, CancellationToken ct)
        => await db.GroupItems.AsNoTracking()
            .Where(item => item.HostType == "image" && item.HostId == imageId && item.Kind == GroupItemKind.Image)
            .OrderBy(item => item.OrderIndex)
            .ThenBy(item => item.Id)
            .Select(item => new GroupSummaryDto(item.GroupId, item.Group!.Name, item.OrderIndex))
            .ToListAsync(ct);

    private async Task<Dictionary<int, List<GroupSummaryDto>>> GetGroupsLookupAsync(IEnumerable<int> imageIds, CancellationToken ct)
    {
        var ids = imageIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var rows = await db.GroupItems.AsNoTracking()
            .Where(item => item.HostType == "image" && ids.Contains(item.HostId) && item.Kind == GroupItemKind.Image)
            .OrderBy(item => item.OrderIndex)
            .ThenBy(item => item.Id)
            .Select(item => new { item.HostId, Group = new GroupSummaryDto(item.GroupId, item.Group!.Name, item.OrderIndex) })
            .ToListAsync(ct);

        return rows
            .GroupBy(row => row.HostId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.Group).ToList());
    }

    private static List<GroupSummaryDto> GetGroups(IReadOnlyDictionary<int, List<GroupSummaryDto>> lookup, int imageId)
        => lookup.TryGetValue(imageId, out var groups) ? groups : [];

    private async Task<bool> ReplaceWholeImageGroupItemsAsync(int imageId, IReadOnlyCollection<VideoGroupInputDto> groups, string? imageTitle, CancellationToken ct)
    {
        var existing = await db.GroupItems
            .Where(item => item.HostType == "image" && item.HostId == imageId && item.Kind == GroupItemKind.Image)
            .ToListAsync(ct);

        var normalizedGroups = groups
            .Where(group => group is { GroupId: > 0 })
            .GroupBy(group => group.GroupId)
            .Select((group, index) => new { GroupId = group.Key, OrderIndex = index })
            .ToList();

        if (existing.OrderBy(item => item.OrderIndex).Select(item => (item.GroupId, item.OrderIndex))
            .SequenceEqual(normalizedGroups.Select(item => (item.GroupId, item.OrderIndex))))
            return false;

        if (existing.Count > 0)
            db.GroupItems.RemoveRange(existing);

        if (normalizedGroups.Count == 0)
        {
            return true;
        }

        db.GroupItems.AddRange(normalizedGroups.Select(group => new GroupItem
        {
            GroupId = group.GroupId,
            OrderIndex = group.OrderIndex,
            Kind = GroupItemKind.Image,
            HostType = "image",
            HostId = imageId,
            ImageId = imageId,
            Title = string.IsNullOrWhiteSpace(imageTitle) ? null : imageTitle.Trim(),
        }));
        return true;
    }

    private static List<TagProvenanceDto> GetTagProvenance(IReadOnlyDictionary<int, List<TagProvenanceDto>>? provenanceLookup, int tagId)
        => provenanceLookup != null && provenanceLookup.TryGetValue(tagId, out var provenance) ? provenance : [];
}
