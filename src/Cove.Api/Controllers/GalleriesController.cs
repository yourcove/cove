using Cove.Api.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Cove.Api.Services;
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
[RequiresPermission(Permissions.GalleriesRead)]
public class GalleriesController(IGalleryRepository galleryRepo, Data.CoveContext db, IUserEngagementService engagementService, IScanService scanService, ITagProvenanceService? tagProvenanceService = null, CustomFieldService? customFields = null, IFieldProvenanceService? fieldProvenanceService = null, IEventBus? eventBus = null) : ControllerBase
{
    private readonly CustomFieldService _customFields = customFields ?? new CustomFieldService(db);
    private sealed record GalleryRelationshipCounts(IReadOnlyDictionary<int, int> ImageCounts, IReadOnlyDictionary<int, int> VideoCounts);

    [HttpGet]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<PaginatedResponse<GalleryDto>>> Find(
        [FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int perPage = 25,
        [FromQuery] string? sort = null, [FromQuery] string? direction = null,
        [FromQuery] int? seed = null,
        [FromQuery] string? sorts = null,
        [FromQuery] string? title = null, [FromQuery] int? rating = null,
        [FromQuery] bool? organized = null, [FromQuery] int? studioId = null, [FromQuery] int? imageId = null,
        [FromQuery] string? tagIds = null, [FromQuery] string? performerIds = null,
        CancellationToken ct = default)
    {
        var filter = new GalleryFilter
        {
            Title = title, Rating = rating, Organized = organized, StudioId = studioId,
            ImageId = imageId,
            TagIds = QueryParsing.ParseIntList(tagIds)?.ToList(), PerformerIds = QueryParsing.ParseIntList(performerIds)?.ToList()
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

        var (items, totalCount) = await galleryRepo.FindAsync(filter, findFilter, ct);
        var dtos = await MapListToDtos(items, ct);
        return Ok(new PaginatedResponse<GalleryDto>(dtos, totalCount, page, perPage));
    }

    [HttpPost("find")]
    public async Task<ActionResult<PaginatedResponse<GalleryDto>>> FindPost([FromBody] FilteredQueryRequest<GalleryFilter> req, CancellationToken ct)
    {
        var findFilter = req.FindFilter ?? new FindFilter();
        var filter = req.ObjectFilter ?? new GalleryFilter();
        var (items, totalCount) = await galleryRepo.FindAsync(filter, findFilter, ct);
        var dtos = await MapListToDtos(items, ct);
        return Ok(new PaginatedResponse<GalleryDto>(dtos, totalCount, findFilter.Page, findFilter.PerPage));
    }

    [HttpGet("{id:int}")]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<GalleryDto>> GetById(int id, CancellationToken ct)
    {
        var gallery = await galleryRepo.GetByIdWithRelationsAsync(id, ct);
        if (gallery == null) return NotFound();

        return Ok(await MapToDtoWithProvenanceAsync(gallery, ct));
    }

    [HttpGet("{id:int}/cover")]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<IActionResult> GetCover(int id, [FromQuery] int? max, [FromQuery] string? v, CancellationToken ct)
    {
        var gallery = await db.Galleries.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct);
        if (gallery == null) return NotFound();

        if (gallery.ImageBlobId != null)
            return Redirect(QueryCredentials.Preserve(Request, WithQuery($"/api/galleries/{id}/image", max, v)));

        if (gallery.CoverImageId.HasValue)
            return Redirect(QueryCredentials.Preserve(Request, WithQuery($"/api/stream/image/{gallery.CoverImageId.Value}/thumbnail", max, v)));

        var firstImageId = await db.Set<ImageGallery>()
            .Where(ig => ig.GalleryId == id)
            .OrderBy(ig => ig.ImageId)
            .Select(ig => (int?)ig.ImageId)
            .FirstOrDefaultAsync(ct);

        if (firstImageId.HasValue)
            return Redirect(QueryCredentials.Preserve(Request, WithQuery($"/api/stream/image/{firstImageId.Value}/thumbnail", max, v)));

        var firstVideoId = await db.Set<VideoGallery>()
            .Where(sg => sg.GalleryId == id)
            .OrderBy(sg => sg.VideoId)
            .Select(sg => (int?)sg.VideoId)
            .FirstOrDefaultAsync(ct);

        return firstVideoId.HasValue
            ? Redirect(QueryCredentials.Preserve(Request, WithQuery($"/api/stream/video/{firstVideoId.Value}/screenshot", null, v)))
            : NotFound();
    }

    [HttpPost]
    [RequiresPermission(Permissions.GalleriesWrite)]
    public async Task<ActionResult<GalleryDto>> Create([FromBody] GalleryCreateDto dto, CancellationToken ct)
    {
        var gallery = new Gallery
        {
            Title = dto.Title, Code = dto.Code, Date = ParseDate(dto.Date),
            Details = dto.Details, Photographer = dto.Photographer,
            Organized = dto.Organized, StudioId = dto.StudioId
        };
        if (dto.Urls?.Count > 0) gallery.Urls = dto.Urls.Select(u => new GalleryUrl { Url = u }).ToList();
        if (dto.TagIds?.Count > 0) gallery.GalleryTags = dto.TagIds.Select(id => new GalleryTag { TagId = id }).ToList();
        if (dto.PerformerIds?.Count > 0) gallery.GalleryPerformers = dto.PerformerIds.Select(id => new GalleryPerformer { PerformerId = id }).ToList();
        if (dto.VideoIds?.Count > 0) gallery.VideoGalleries = dto.VideoIds.Select(id => new VideoGallery { VideoId = id }).ToList();

        gallery = await galleryRepo.AddAsync(gallery, ct);
        if (dto.CustomFields != null)
            await _customFields.SaveValuesAsync(CustomFieldEntityTypes.Gallery, gallery.Id, dto.CustomFields, ct);
        if (dto.Rating.HasValue)
            await engagementService.SetRatingAsync(AffinityHostType.Gallery, gallery.Id, dto.Rating, cancellationToken: ct);
        if (dto.TagIds?.Count > 0 && tagProvenanceService != null)
        {
            await tagProvenanceService.SyncTagSetAsync(AffinityHostType.Gallery, gallery.Id, [], dto.TagIds, cancellationToken: ct);
            await db.SaveChangesAsync(ct);
        }
        var result = await galleryRepo.GetByIdWithRelationsAsync(gallery.Id, ct);
        return CreatedAtAction(nameof(GetById), new { id = gallery.Id }, await MapToDtoWithProvenanceAsync(result!, ct));
    }

    [HttpPut("{id:int}")]
    [RequiresPermission(Permissions.GalleriesWrite)]
    [RequiresEntityAccess(EntityKinds.Gallery, Permissions.GalleriesWrite)]
    public async Task<ActionResult<GalleryDto>> Update(int id, [FromBody] GalleryUpdateDto dto, CancellationToken ct)
    {
        var gallery = await galleryRepo.GetByIdWithRelationsAsync(id, ct);
        if (gallery == null) return NotFound();
        var previousTagIds = dto.TagIds != null ? gallery.GalleryTags.Select(galleryTag => galleryTag.TagId).ToArray() : [];
        var clearFields = dto.ClearFields?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        if (dto.Title != null) gallery.Title = string.IsNullOrWhiteSpace(dto.Title) ? null : dto.Title;
        if (dto.Code != null) gallery.Code = string.IsNullOrWhiteSpace(dto.Code) ? null : dto.Code;
        if (dto.Date != null) gallery.Date = ParseDate(dto.Date);
        if (dto.Details != null) gallery.Details = string.IsNullOrWhiteSpace(dto.Details) ? null : dto.Details;
        if (dto.Photographer != null) gallery.Photographer = string.IsNullOrWhiteSpace(dto.Photographer) ? null : dto.Photographer;
        if (dto.Organized.HasValue) gallery.Organized = dto.Organized.Value;
        if (dto.StudioId.HasValue) gallery.StudioId = dto.StudioId;
        if (clearFields.Contains("date")) gallery.Date = null;
        if (clearFields.Contains("studioId")) gallery.StudioId = null;

        if (dto.Urls != null)
        {
            gallery.Urls.Clear();
            gallery.Urls = dto.Urls.Select(u => new GalleryUrl { Url = u, GalleryId = id }).ToList();
        }
        if (dto.TagIds != null)
        {
            gallery.GalleryTags.Clear();
            gallery.GalleryTags = dto.TagIds.Select(tid => new GalleryTag { TagId = tid, GalleryId = id }).ToList();
        }
        if (dto.PerformerIds != null)
        {
            gallery.GalleryPerformers.Clear();
            gallery.GalleryPerformers = dto.PerformerIds.Select(pid => new GalleryPerformer { PerformerId = pid, GalleryId = id }).ToList();
        }
        if (dto.VideoIds != null)
        {
            gallery.VideoGalleries.Clear();
            gallery.VideoGalleries = dto.VideoIds.Select(sid => new VideoGallery { VideoId = sid, GalleryId = id }).ToList();
        }
        if (dto.TagIds != null && tagProvenanceService != null)
        {
            await tagProvenanceService.SyncTagSetAsync(
                AffinityHostType.Gallery,
                id,
                previousTagIds,
                gallery.GalleryTags.Select(galleryTag => galleryTag.TagId).ToArray(),
                cancellationToken: ct);
        }

        await galleryRepo.UpdateAsync(gallery, ct);
        if (dto.CustomFields != null)
            await _customFields.SaveValuesAsync(CustomFieldEntityTypes.Gallery, id, dto.CustomFields, ct);
        if (dto.Rating.HasValue)
            await engagementService.SetRatingAsync(AffinityHostType.Gallery, id, dto.Rating, cancellationToken: ct);
        var updated = await galleryRepo.GetByIdWithRelationsAsync(id, ct);
        return Ok(await MapToDtoWithProvenanceAsync(updated!, ct));
    }

    [HttpDelete("{id:int}")]
    [RequiresPermission(Permissions.GalleriesDelete)]
    [RequiresEntityAccess(EntityKinds.Gallery, Permissions.GalleriesDelete)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var g = await galleryRepo.GetByIdAsync(id, ct);
        if (g == null) return NotFound();
        if (tagProvenanceService != null)
            await tagProvenanceService.RemoveForHostAsync(AffinityHostType.Gallery, id, ct);
        await _customFields.DeleteValuesForEntityAsync(CustomFieldEntityTypes.Gallery, id, ct);
        await galleryRepo.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:int}/rescan")]
    [RequiresPermission(Permissions.LibraryScan)]
    [RequiresEntityAccess(EntityKinds.Gallery, Permissions.LibraryScan)]
    public async Task<IActionResult> Rescan(int id, CancellationToken ct)
    {
        var gallery = await db.Galleries.AsNoTracking()
            .Include(item => item.Folder)
            .Include(item => item.Files)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (gallery == null) return NotFound();

        var scanPath = gallery.Folder?.Path;
        if (string.IsNullOrWhiteSpace(scanPath))
            scanPath = gallery.Files?.Select(file => file.Path).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (string.IsNullOrWhiteSpace(scanPath)) return BadRequest("Gallery has no folder or files");

        var jobId = scanService.StartScan(new ScanOperationOptions
        {
            Paths = [scanPath],
            Rescan = true,
        });
        return Ok(new { jobId });
    }

    private async Task<GalleryDto> MapToDtoWithProvenanceAsync(Gallery gallery, CancellationToken cancellationToken = default)
    {
        var tagIds = gallery.GalleryTags
            .Where(galleryTag => galleryTag.Tag != null)
            .Select(galleryTag => galleryTag.Tag!.Id)
            .Distinct()
            .ToArray();
        var provenanceLookup = tagProvenanceService == null
            ? null
            : await tagProvenanceService.GetLookupAsync(AffinityHostType.Gallery, gallery.Id, tagIds, cancellationToken);

        var customFieldValues = await _customFields.GetValuesAsync(CustomFieldEntityTypes.Gallery, gallery.Id, cancellationToken);
        var relationshipCounts = await GetRelationshipCountsAsync([gallery.Id], cancellationToken);
        var fieldProvenance = fieldProvenanceService == null
            ? null
            : (await fieldProvenanceService.GetForHostAsync(AffinityHostType.Gallery, gallery.Id, cancellationToken)).ToList();
        return MapToDto(
            gallery,
            customFieldValues,
            GetRelationshipCount(relationshipCounts.ImageCounts, gallery.Id),
            GetRelationshipCount(relationshipCounts.VideoCounts, gallery.Id),
            provenanceLookup,
            fieldProvenance);
    }

    private GalleryDto MapToDto(Gallery g, Dictionary<string, object>? customFieldValues = null, int? imageCount = null, int? videoCount = null, IReadOnlyDictionary<int, List<TagProvenanceDto>>? provenanceLookup = null, List<FieldProvenanceDto>? fieldProvenance = null, string? displayName = null) => new(
        g.Id, g.Title, g.Code, g.Date?.ToString("yyyy-MM-dd"), g.Details, g.Photographer,
        g.Organized, g.StudioId, g.Studio?.Name,
        g.Urls.Select(u => u.Url).ToList(),
        g.GalleryTags.Where(gt => gt.Tag != null).Select(gt => TagDtoMapping.MapTagDto(gt.Tag!, GetTagProvenance(provenanceLookup, gt.Tag!.Id))).ToList(),
        g.GalleryPerformers.Where(gp => gp.Performer != null).Select(gp => gp.Performer!).OrderForDisplay().Select(performer => new PerformerSummaryDto(performer.Id, performer.Name, performer.Disambiguation, performer.Gender?.ToString(), performer.Birthdate?.ToString("yyyy-MM-dd"), performer.Favorite, EntityImageUrls.PerformerOrNull(ControllerContext.HttpContext, performer))).ToList(),
        imageCount ?? g.ImageCount,
        videoCount ?? g.VideoCount,
        g.VideoGalleries?.Select(sg => sg.VideoId).ToList() ?? [],
        g.Folder?.Path,
        g.Files?.Select(f => new GalleryFileInfoDto(f.Id, f.Path, f.Size, f.ModTime.ToString("o"),
            f.Fingerprints?.Select(fp => new FingerprintDto(fp.Type, fp.Value)).ToList() ?? [])).ToList() ?? [],
        customFieldValues,
        g.CreatedAt.ToString("o"), g.UpdatedAt.ToString("o"),
        ResolveCoverPath(g, imageCount, videoCount),
        g.CoverImageId,
        g.BackImageBlobId != null ? EntityImageUrls.GalleryBackCover(ControllerContext.HttpContext, g.Id, g.UpdatedAt) : null,
        fieldProvenance,
        displayName ?? ResolveGalleryDisplayName(g)
    );

    /// <summary>
    /// Filename/folder-name fallback used when a gallery has no Title (scan no longer stores the
    /// filename). Prefers a loaded zip-gallery file basename, else the folder name. Returns null when
    /// the gallery has neither files nor a folder loaded (e.g. list view, where the caller supplies a
    /// precomputed value instead). Only the leaf name is returned, never a full path.
    /// </summary>
    private static string? ResolveGalleryDisplayName(Gallery g)
    {
        var fileBasename = g.Files?
            .Select(f => string.IsNullOrWhiteSpace(f.Basename) ? LeafName(f.Path) : f.Basename)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        if (!string.IsNullOrWhiteSpace(fileBasename))
            return fileBasename;

        var folderPath = g.Folder?.Path;
        return string.IsNullOrWhiteSpace(folderPath) ? null : LeafName(folderPath);
    }

    private static string? LeafName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var idx = normalized.LastIndexOf('/');
        return idx >= 0 ? normalized[(idx + 1)..] : normalized;
    }

    /// <summary>Resolve cover image URL through the unified gallery cover endpoint.</summary>
    private string? ResolveCoverPath(Gallery g, int? imageCount = null, int? videoCount = null)
    {
        var resolvedImageCount = imageCount ?? g.ImageCount;
        var resolvedVideoCount = videoCount ?? g.VideoCount;
        if (g.ImageBlobId != null || g.CoverImageId != null || resolvedImageCount > 0 || resolvedVideoCount > 0) return EntityImageUrls.GalleryCover(ControllerContext.HttpContext, g.Id, g.UpdatedAt);
        return null;
    }

    private static string WithQuery(string path, int? max, string? version)
    {
        var query = new List<string>();
        if (max.HasValue && max.Value > 0) query.Add($"max={max.Value}");
        if (!string.IsNullOrWhiteSpace(version)) query.Add($"v={Uri.EscapeDataString(version)}");
        return query.Count == 0 ? path : $"{path}?{string.Join("&", query)}";
    }

    private async Task<List<GalleryDto>> MapListToDtos(IReadOnlyList<Gallery> items, CancellationToken ct)
    {
        if (items.Count == 0) return [];
        var ids = items.Select(item => item.Id).ToArray();
        var customFieldValues = await _customFields.GetValuesAsync(CustomFieldEntityTypes.Gallery, ids, ct);
        var relationshipCounts = await GetRelationshipCountsAsync(ids, ct);
        var displayNames = await GetDisplayNameFallbacksAsync(ids, ct);
        return items.Select(g => MapToDto(
            g,
            GetCustomFields(customFieldValues, g.Id),
            GetRelationshipCount(relationshipCounts.ImageCounts, g.Id),
            GetRelationshipCount(relationshipCounts.VideoCounts, g.Id),
            displayName: displayNames.GetValueOrDefault(g.Id))).ToList();
    }

    private async Task<GalleryRelationshipCounts> GetRelationshipCountsAsync(IReadOnlyCollection<int> galleryIds, CancellationToken ct)
    {
        if (galleryIds.Count == 0)
            return new GalleryRelationshipCounts(new Dictionary<int, int>(), new Dictionary<int, int>());

        var imageCounts = await db.Set<ImageGallery>()
            .AsNoTracking()
            .Where(imageGallery => galleryIds.Contains(imageGallery.GalleryId))
            .GroupBy(imageGallery => imageGallery.GalleryId)
            .Select(group => new { GalleryId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.GalleryId, item => item.Count, ct);

        var videoCounts = await db.Set<VideoGallery>()
            .AsNoTracking()
            .Where(videoGallery => galleryIds.Contains(videoGallery.GalleryId))
            .GroupBy(videoGallery => videoGallery.GalleryId)
            .Select(group => new { GalleryId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.GalleryId, item => item.Count, ct);

        return new GalleryRelationshipCounts(imageCounts, videoCounts);
    }

    private static int GetRelationshipCount(IReadOnlyDictionary<int, int> counts, int galleryId)
        => counts.TryGetValue(galleryId, out var count) ? count : 0;

    /// <summary>
    /// Lightweight per-gallery display fallback for the list view, which does not load the Files or
    /// Folder navigations. Loads only one file basename/path and the folder path per gallery (not the
    /// whole file graph), preferring a zip-gallery file basename, else the folder name.
    /// </summary>
    private async Task<Dictionary<int, string>> GetDisplayNameFallbacksAsync(IReadOnlyCollection<int> galleryIds, CancellationToken ct)
    {
        var result = new Dictionary<int, string>();
        if (galleryIds.Count == 0)
            return result;

        // First file (by id) per gallery — just basename/path, not the file graph. Fetch the minimal
        // ordered rows and pick the first per gallery client-side (EF can't translate a grouped First
        // projection here), so each gallery contributes only its earliest file.
        var fileRows = await db.Set<GalleryFile>()
            .AsNoTracking()
            .Where(f => f.GalleryId != null && galleryIds.Contains(f.GalleryId.Value))
            .OrderBy(f => f.Id)
            .Select(f => new { GalleryId = f.GalleryId!.Value, f.Basename, f.Path })
            .ToListAsync(ct);

        foreach (var row in fileRows)
        {
            if (result.ContainsKey(row.GalleryId))
                continue;
            var name = string.IsNullOrWhiteSpace(row.Basename) ? LeafName(row.Path) : row.Basename;
            if (!string.IsNullOrWhiteSpace(name))
                result[row.GalleryId] = name!;
        }

        // Folder-based galleries with no files: fall back to the folder name.
        var missingIds = galleryIds.Where(id => !result.ContainsKey(id)).ToArray();
        if (missingIds.Length > 0)
        {
            var folderRows = await db.Galleries
                .AsNoTracking()
                .Where(g => missingIds.Contains(g.Id) && g.Folder != null)
                .Select(g => new { g.Id, FolderPath = g.Folder!.Path })
                .ToListAsync(ct);

            foreach (var row in folderRows)
            {
                var name = LeafName(row.FolderPath);
                if (!string.IsNullOrWhiteSpace(name))
                    result[row.Id] = name!;
            }
        }

        return result;
    }

    private static Dictionary<string, object>? GetCustomFields(IReadOnlyDictionary<int, Dictionary<string, object>> lookup, int id)
        => lookup.TryGetValue(id, out var values) && values.Count > 0 ? values : null;

    private static List<TagProvenanceDto> GetTagProvenance(IReadOnlyDictionary<int, List<TagProvenanceDto>>? provenanceLookup, int tagId)
        => provenanceLookup != null && provenanceLookup.TryGetValue(tagId, out var provenance) ? provenance : [];

    private static DateOnly? ParseDate(string? date) => DateOnly.TryParse(date, out var d) ? d : null;

    // ===== Image Management =====

    [HttpPost("{id:int}/images")]
    [RequiresPermission(Permissions.GalleriesWrite)]
    [RequiresEntityAccess(EntityKinds.Gallery, Permissions.GalleriesWrite)]
    public async Task<IActionResult> AddImages(int id, [FromBody] GalleryAddImagesDto dto, CancellationToken ct)
    {
        var gallery = await db.Galleries.Include(g => g.ImageGalleries).FirstOrDefaultAsync(g => g.Id == id, ct);
        if (gallery == null) return NotFound();

        var existing = gallery.ImageGalleries.Select(ig => ig.ImageId).ToHashSet();
        var addedIds = dto.ImageIds.Where(existing.Add).Distinct().ToList();
        foreach (var imageId in addedIds)
            gallery.ImageGalleries.Add(new ImageGallery { ImageId = imageId, GalleryId = id });

        await db.SaveChangesAsync(ct);
        if (addedIds.Count > 0)
            PublishGalleryUpdate(id);
        return Ok(new { added = addedIds.Count });
    }

    [HttpDelete("{id:int}/images")]
    [RequiresPermission(Permissions.GalleriesWrite)]
    [RequiresEntityAccess(EntityKinds.Gallery, Permissions.GalleriesWrite)]
    public async Task<IActionResult> RemoveImages(int id, [FromBody] GalleryRemoveImagesDto dto, CancellationToken ct)
    {
        var toRemove = await db.Set<ImageGallery>()
            .Where(ig => ig.GalleryId == id && dto.ImageIds.Contains(ig.ImageId))
            .ToListAsync(ct);

        db.Set<ImageGallery>().RemoveRange(toRemove);
        await db.SaveChangesAsync(ct);
        if (toRemove.Count > 0)
            PublishGalleryUpdate(id);
        return Ok(new { removed = toRemove.Count });
    }

    // ===== Chapters =====

    [HttpGet("{id:int}/chapters")]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<List<GalleryChapterDto>>> GetChapters(int id, CancellationToken ct)
    {
        var chapters = await db.GalleryChapters
            .Where(c => c.GalleryId == id)
            .OrderBy(c => c.ImageIndex)
            .Select(c => new GalleryChapterDto(c.Id, c.Title, c.ImageIndex, c.GalleryId, c.CreatedAt.ToString("o"), c.UpdatedAt.ToString("o")))
            .ToListAsync(ct);

        return Ok(chapters);
    }

    [HttpPost("{id:int}/chapters")]
    [RequiresPermission(Permissions.GalleriesWrite)]
    [RequiresEntityAccess(EntityKinds.Gallery, Permissions.GalleriesWrite)]
    public async Task<ActionResult<GalleryChapterDto>> CreateChapter(int id, [FromBody] GalleryChapterCreateDto dto, CancellationToken ct)
    {
        var gallery = await db.Galleries.FindAsync([id], ct);
        if (gallery == null) return NotFound();

        var chapter = new GalleryChapter { Title = dto.Title, ImageIndex = dto.ImageIndex, GalleryId = id };
        db.GalleryChapters.Add(chapter);
        await db.SaveChangesAsync(ct);
        PublishGalleryUpdate(id);
        return CreatedAtAction(nameof(GetChapters), new { id }, new GalleryChapterDto(chapter.Id, chapter.Title, chapter.ImageIndex, chapter.GalleryId, chapter.CreatedAt.ToString("o"), chapter.UpdatedAt.ToString("o")));
    }

    [HttpPut("{galleryId:int}/chapters/{chapterId:int}")]
    [RequiresPermission(Permissions.GalleriesWrite)]
    [RequiresEntityAccess(EntityKinds.Gallery, Permissions.GalleriesWrite, RouteValueName = "galleryId")]
    public async Task<ActionResult<GalleryChapterDto>> UpdateChapter(int galleryId, int chapterId, [FromBody] GalleryChapterUpdateDto dto, CancellationToken ct)
    {
        var chapter = await db.GalleryChapters.FirstOrDefaultAsync(c => c.Id == chapterId && c.GalleryId == galleryId, ct);
        if (chapter == null) return NotFound();

        if (dto.Title != null) chapter.Title = dto.Title;
        if (dto.ImageIndex.HasValue) chapter.ImageIndex = dto.ImageIndex.Value;
        await db.SaveChangesAsync(ct);
        PublishGalleryUpdate(galleryId);
        return Ok(new GalleryChapterDto(chapter.Id, chapter.Title, chapter.ImageIndex, chapter.GalleryId, chapter.CreatedAt.ToString("o"), chapter.UpdatedAt.ToString("o")));
    }

    [HttpDelete("{galleryId:int}/chapters/{chapterId:int}")]
    [RequiresPermission(Permissions.GalleriesWrite)]
    [RequiresEntityAccess(EntityKinds.Gallery, Permissions.GalleriesWrite, RouteValueName = "galleryId")]
    public async Task<IActionResult> DeleteChapter(int galleryId, int chapterId, CancellationToken ct)
    {
        var chapter = await db.GalleryChapters.FirstOrDefaultAsync(c => c.Id == chapterId && c.GalleryId == galleryId, ct);
        if (chapter == null) return NotFound();
        db.GalleryChapters.Remove(chapter);
        await db.SaveChangesAsync(ct);
        PublishGalleryUpdate(galleryId);
        return NoContent();
    }

    private void PublishGalleryUpdate(int id)
        => eventBus?.Publish(new EntityEvent(EventType.GalleryUpdated, "Gallery", id));

    // ===== Bulk Operations =====

    [HttpPost("bulk")]
    [RequiresPermission(Permissions.GalleriesWrite)]
    [RequiresEntityAccess(EntityKinds.Gallery, Permissions.GalleriesWrite, ActionArgumentName = "dto", PropertyName = "Ids")]
    public async Task<IActionResult> BulkUpdate([FromBody] BulkGalleryUpdateDto dto, CancellationToken ct)
    {
        var galleries = await db.Galleries
            .Include(g => g.GalleryTags)
            .Include(g => g.GalleryPerformers)
            .AsSplitQuery()
            .Where(g => dto.Ids.Contains(g.Id))
            .ToListAsync(ct);
        var clearFields = dto.ClearFields?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        foreach (var gallery in galleries)
        {
            if (clearFields.Contains("studioId")) gallery.StudioId = null;
            if (clearFields.Contains("date")) gallery.Date = null;
            if (clearFields.Contains("code")) gallery.Code = null;
            if (clearFields.Contains("details")) gallery.Details = null;
            if (clearFields.Contains("photographer")) gallery.Photographer = null;
            if (dto.Organized.HasValue) gallery.Organized = dto.Organized.Value;
            if (dto.StudioId.HasValue) gallery.StudioId = dto.StudioId;
            if (dto.Date != null) gallery.Date = ParseDate(dto.Date);
            if (dto.Code != null) gallery.Code = string.IsNullOrWhiteSpace(dto.Code) ? null : dto.Code;
            if (dto.Details != null) gallery.Details = string.IsNullOrWhiteSpace(dto.Details) ? null : dto.Details;
            if (dto.Photographer != null) gallery.Photographer = string.IsNullOrWhiteSpace(dto.Photographer) ? null : dto.Photographer;

            if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Set)
            {
                gallery.GalleryTags.Clear();
                gallery.GalleryTags = dto.TagIds.Select(tid => new GalleryTag { TagId = tid, GalleryId = gallery.Id }).ToList();
            }
            else if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Add)
            {
                var existing = gallery.GalleryTags.Select(gt => gt.TagId).ToHashSet();
                foreach (var tid in dto.TagIds.Where(t => !existing.Contains(t)))
                    gallery.GalleryTags.Add(new GalleryTag { TagId = tid, GalleryId = gallery.Id });
            }
            else if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Remove)
            {
                gallery.GalleryTags = gallery.GalleryTags.Where(gt => !dto.TagIds.Contains(gt.TagId)).ToList();
            }

            if (dto.PerformerIds != null && dto.PerformerMode == BulkUpdateMode.Set)
            {
                gallery.GalleryPerformers.Clear();
                gallery.GalleryPerformers = dto.PerformerIds.Select(pid => new GalleryPerformer { PerformerId = pid, GalleryId = gallery.Id }).ToList();
            }
            else if (dto.PerformerIds != null && dto.PerformerMode == BulkUpdateMode.Add)
            {
                var existing = gallery.GalleryPerformers.Select(gp => gp.PerformerId).ToHashSet();
                foreach (var pid in dto.PerformerIds.Where(p => !existing.Contains(p)))
                    gallery.GalleryPerformers.Add(new GalleryPerformer { PerformerId = pid, GalleryId = gallery.Id });
            }
            else if (dto.PerformerIds != null && dto.PerformerMode == BulkUpdateMode.Remove)
            {
                gallery.GalleryPerformers = gallery.GalleryPerformers.Where(gp => !dto.PerformerIds.Contains(gp.PerformerId)).ToList();
            }
        }

        await db.SaveChangesAsync(ct);
        if (dto.Rating.HasValue)
        {
            foreach (var gallery in galleries)
                await engagementService.SetRatingAsync(AffinityHostType.Gallery, gallery.Id, dto.Rating, cancellationToken: ct);
        }
        return Ok(new BulkUpdateResult(galleries.Select(gallery => gallery.Id).ToList()));
    }

    [HttpDelete("bulk")]
    [RequiresPermission(Permissions.GalleriesDelete)]
    [RequiresEntityAccess(EntityKinds.Gallery, Permissions.GalleriesDelete, ActionArgumentName = "dto", PropertyName = "Ids")]
    public async Task<IActionResult> BulkDelete([FromBody] BatchDeleteDto dto, CancellationToken ct)
    {
        var ids = dto.Ids.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return Ok(new BulkDeleteResult([]));

        var galleries = await db.Galleries.Where(g => ids.Contains(g.Id)).ToListAsync(ct);
        foreach (var gallery in galleries)
        {
            if (tagProvenanceService != null)
                await tagProvenanceService.RemoveForHostAsync(AffinityHostType.Gallery, gallery.Id, ct);
            await _customFields.DeleteValuesForEntityAsync(CustomFieldEntityTypes.Gallery, gallery.Id, ct);
        }
        db.Galleries.RemoveRange(galleries);
        await db.SaveChangesAsync(ct);
        return Ok(new BulkDeleteResult(galleries.Select(gallery => gallery.Id).ToList()));
    }
}
