using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Interfaces;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PerformersController(IPerformerRepository performerRepo, MetadataServerService metadataServerService, Data.CoveContext db) : ControllerBase
{
    [HttpGet]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<PaginatedResponse<PerformerDto>>> Find(
        [FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int perPage = 25,
        [FromQuery] string? sort = null, [FromQuery] string? direction = null,
        [FromQuery] int? seed = null,
        [FromQuery] string? name = null, [FromQuery] bool? favorite = null,
        [FromQuery] int? rating = null, [FromQuery] string? tagIds = null,
        [FromQuery] int? studioId = null,
        CancellationToken ct = default)
    {
        var filter = new PerformerFilter { Name = name, Favorite = favorite, Rating = rating, TagIds = ParseIntList(tagIds), StudioId = studioId };
        var findFilter = new FindFilter
        {
            Q = q, Page = page, PerPage = perPage, Sort = sort,
            Direction = direction == "desc" ? SortDirection.Desc : SortDirection.Asc,
            Seed = seed,
        };

        var (items, totalCount) = await performerRepo.FindAsync(filter, findFilter, ct);
        var ids = items.Select(p => p.Id).ToList();
        var sceneCounts = await db.Set<ScenePerformer>().Where(sp => ids.Contains(sp.PerformerId)).GroupBy(sp => sp.PerformerId).ToDictionaryAsync(g => g.Key, g => g.Count(), ct);
        var imageCounts = await db.Set<ImagePerformer>().Where(ip => ids.Contains(ip.PerformerId)).GroupBy(ip => ip.PerformerId).ToDictionaryAsync(g => g.Key, g => g.Count(), ct);
        var galleryCounts = await db.Set<GalleryPerformer>().Where(gp => ids.Contains(gp.PerformerId)).GroupBy(gp => gp.PerformerId).ToDictionaryAsync(g => g.Key, g => g.Count(), ct);
        var dtos = items.Select(p => MapToDto(p,
            sceneCounts.GetValueOrDefault(p.Id),
            imageCounts.GetValueOrDefault(p.Id),
            galleryCounts.GetValueOrDefault(p.Id)
        )).ToList();
        return Ok(new PaginatedResponse<PerformerDto>(dtos, totalCount, page, perPage));
    }

    [HttpPost("find")]
    public async Task<ActionResult<PaginatedResponse<PerformerDto>>> FindPost([FromBody] FilteredQueryRequest<PerformerFilter> req, CancellationToken ct)
    {
        var findFilter = req.FindFilter ?? new FindFilter();
        var filter = req.ObjectFilter ?? new PerformerFilter();
        var (items, totalCount) = await performerRepo.FindAsync(filter, findFilter, ct);
        var ids = items.Select(p => p.Id).ToList();
        var sceneCounts = await db.Set<ScenePerformer>().Where(sp => ids.Contains(sp.PerformerId)).GroupBy(sp => sp.PerformerId).ToDictionaryAsync(g => g.Key, g => g.Count(), ct);
        var imageCounts = await db.Set<ImagePerformer>().Where(ip => ids.Contains(ip.PerformerId)).GroupBy(ip => ip.PerformerId).ToDictionaryAsync(g => g.Key, g => g.Count(), ct);
        var galleryCounts = await db.Set<GalleryPerformer>().Where(gp => ids.Contains(gp.PerformerId)).GroupBy(gp => gp.PerformerId).ToDictionaryAsync(g => g.Key, g => g.Count(), ct);
        var dtos = items.Select(p => MapToDto(p,
            sceneCounts.GetValueOrDefault(p.Id),
            imageCounts.GetValueOrDefault(p.Id),
            galleryCounts.GetValueOrDefault(p.Id)
        )).ToList();
        return Ok(new PaginatedResponse<PerformerDto>(dtos, totalCount, findFilter.Page, findFilter.PerPage));
    }

    [HttpGet("{id:int}")]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<PerformerDto>> GetById(int id, CancellationToken ct)
    {
        var performer = await performerRepo.GetByIdWithRelationsAsync(id, ct);
        if (performer == null) return NotFound();
        // Use explicit COUNT queries â€” navigation properties for join tables aren't loaded
        var sceneCount = await db.Set<ScenePerformer>().CountAsync(sp => sp.PerformerId == id, ct);
        var imageCount = await db.Set<ImagePerformer>().CountAsync(ip => ip.PerformerId == id, ct);
        var galleryCount = await db.Set<GalleryPerformer>().CountAsync(gp => gp.PerformerId == id, ct);
        return Ok(MapToDto(performer, sceneCount, imageCount, galleryCount));
    }

    [HttpPost]
    public async Task<ActionResult<PerformerDto>> Create([FromBody] PerformerCreateDto dto, CancellationToken ct)
    {
        var performer = new Performer
        {
            Name = dto.Name, Disambiguation = dto.Disambiguation,
            Gender = ParseEnum<GenderEnum>(dto.Gender), Birthdate = ParseDate(dto.Birthdate),
            DeathDate = ParseDate(dto.DeathDate), Ethnicity = dto.Ethnicity, Country = dto.Country,
            EyeColor = dto.EyeColor, HairColor = dto.HairColor, HeightCm = dto.HeightCm,
            Weight = dto.Weight, Measurements = dto.Measurements, FakeTits = dto.FakeTits,
            PenisLength = dto.PenisLength, Circumcised = ParseEnum<CircumcisedEnum>(dto.Circumcised),
            CareerStart = ParseDate(dto.CareerStart), CareerEnd = ParseDate(dto.CareerEnd),
            Tattoos = dto.Tattoos, Piercings = dto.Piercings,
            Favorite = dto.Favorite, Rating = dto.Rating, Details = dto.Details,
            IgnoreAutoTag = dto.IgnoreAutoTag
        };
        if (dto.Urls?.Count > 0) performer.Urls = dto.Urls.Select(u => new PerformerUrl { Url = u }).ToList();
        if (dto.Aliases?.Count > 0) performer.Aliases = dto.Aliases.Select(a => new PerformerAlias { Alias = a }).ToList();
        if (dto.TagIds?.Count > 0) performer.PerformerTags = dto.TagIds.Select(id => new PerformerTag { TagId = id }).ToList();

        performer = await performerRepo.AddAsync(performer, ct);
        var result = await performerRepo.GetByIdWithRelationsAsync(performer.Id, ct);
        return CreatedAtAction(nameof(GetById), new { id = performer.Id }, MapToDto(result!));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<PerformerDto>> Update(int id, [FromBody] PerformerUpdateDto dto, CancellationToken ct)
    {
        var p = await performerRepo.GetByIdWithRelationsAsync(id, ct);
        if (p == null) return NotFound();

        if (dto.Name != null) p.Name = dto.Name;
        if (dto.Disambiguation != null) p.Disambiguation = dto.Disambiguation;
        if (dto.Gender != null) p.Gender = ParseEnum<GenderEnum>(dto.Gender);
        if (dto.Birthdate != null) p.Birthdate = ParseDate(dto.Birthdate);
        if (dto.DeathDate != null) p.DeathDate = ParseDate(dto.DeathDate);
        if (dto.Ethnicity != null) p.Ethnicity = dto.Ethnicity;
        if (dto.Country != null) p.Country = dto.Country;
        if (dto.EyeColor != null) p.EyeColor = dto.EyeColor;
        if (dto.HairColor != null) p.HairColor = dto.HairColor;
        if (dto.HeightCm.HasValue) p.HeightCm = dto.HeightCm;
        if (dto.Weight.HasValue) p.Weight = dto.Weight;
        if (dto.Measurements != null) p.Measurements = dto.Measurements;
        if (dto.FakeTits != null) p.FakeTits = dto.FakeTits;
        if (dto.PenisLength.HasValue) p.PenisLength = dto.PenisLength;
        if (dto.Circumcised != null) p.Circumcised = ParseEnum<CircumcisedEnum>(dto.Circumcised);
        if (dto.CareerStart != null) p.CareerStart = ParseDate(dto.CareerStart);
        if (dto.CareerEnd != null) p.CareerEnd = ParseDate(dto.CareerEnd);
        if (dto.Tattoos != null) p.Tattoos = dto.Tattoos;
        if (dto.Piercings != null) p.Piercings = dto.Piercings;
        if (dto.Favorite.HasValue) p.Favorite = dto.Favorite.Value;
        if (dto.Rating.HasValue) p.Rating = dto.Rating;
        if (dto.Details != null) p.Details = dto.Details;
        if (dto.IgnoreAutoTag.HasValue) p.IgnoreAutoTag = dto.IgnoreAutoTag.Value;

        if (dto.Urls != null)
        {
            p.Urls.Clear();
            p.Urls = dto.Urls.Select(u => new PerformerUrl { Url = u, PerformerId = id }).ToList();
        }
        if (dto.Aliases != null)
        {
            p.Aliases.Clear();
            p.Aliases = dto.Aliases.Select(a => new PerformerAlias { Alias = a, PerformerId = id }).ToList();
        }
        if (dto.TagIds != null)
        {
            p.PerformerTags.Clear();
            p.PerformerTags = dto.TagIds.Select(tid => new PerformerTag { TagId = tid, PerformerId = id }).ToList();
        }
        if (dto.CustomFields != null) p.CustomFields = dto.CustomFields;

        await performerRepo.UpdateAsync(p, ct);
        var updated = await performerRepo.GetByIdWithRelationsAsync(id, ct);
        return Ok(MapToDto(updated!));
    }

    [HttpGet("{id:int}/metadata-server/search")]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<IReadOnlyList<MetadataServerPerformerMatchDto>>> SearchMetadataServer(int id, [FromQuery] string? term, [FromQuery] string? endpoint, CancellationToken ct)
    {
        var performer = await performerRepo.GetByIdWithRelationsAsync(id, ct);
        if (performer == null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(term))
        {
            var existingRemoteId = performer.RemoteIds.FirstOrDefault(remoteId => string.IsNullOrWhiteSpace(endpoint) || string.Equals(remoteId.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));
            if (existingRemoteId != null)
            {
                var existing = await metadataServerService.GetPerformerMatchAsync(existingRemoteId.Endpoint, existingRemoteId.RemoteId, ct);
                if (existing != null)
                    return Ok(new[] { existing });
            }

            term = performer.Name;
        }

        return Ok(await metadataServerService.SearchPerformersAsync(term, endpoint, ct));
    }

    [HttpPost("{id:int}/metadata-server/import")]
    public async Task<ActionResult<PerformerDto>> ImportFromMetadataServer(int id, [FromBody] MetadataServerPerformerImportRequestDto dto, CancellationToken ct)
    {
        var performer = await performerRepo.GetByIdWithRelationsAsync(id, ct);
        if (performer == null)
            return NotFound();

        var imported = await metadataServerService.MergePerformerAsync(performer, dto.Endpoint, dto.PerformerId, ct);
        if (!imported)
            return NotFound();

        await performerRepo.UpdateAsync(performer, ct);
        var updated = await performerRepo.GetByIdWithRelationsAsync(id, ct);
        return Ok(MapToDto(updated!));
    }

    [HttpPost("{id:int}/metadata-server/submit-draft")]
    public async Task<IActionResult> SubmitPerformerDraft(int id, [FromBody] MetadataServerEndpointDto dto, CancellationToken ct)
    {
        var performer = await performerRepo.GetByIdWithRelationsAsync(id, ct);
        if (performer == null) return NotFound();

        var draftId = await metadataServerService.SubmitPerformerDraftAsync(performer, dto.Endpoint, ct);
        return Ok(new { draftId });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var p = await performerRepo.GetByIdAsync(id, ct);
        if (p == null) return NotFound();
        await performerRepo.DeleteAsync(id, ct);
        return NoContent();
    }

    private static PerformerDto MapToDto(Performer p, int? sceneCount = null, int? imageCount = null, int? galleryCount = null, int? groupCount = null) => new(
        p.Id, p.Name, p.Disambiguation, p.Gender?.ToString(),
        p.Birthdate?.ToString("yyyy-MM-dd"), p.DeathDate?.ToString("yyyy-MM-dd"),
        p.Ethnicity, p.Country, p.EyeColor, p.HairColor, p.HeightCm, p.Weight,
        p.Measurements, p.FakeTits, p.PenisLength, p.Circumcised?.ToString(),
        p.CareerStart?.ToString("yyyy-MM-dd"), p.CareerEnd?.ToString("yyyy-MM-dd"),
        p.Tattoos, p.Piercings, p.Favorite, p.Rating, p.Details, p.IgnoreAutoTag,
        p.Urls.Select(u => u.Url).ToList(),
        p.Aliases.Select(a => a.Alias).ToList(),
        p.PerformerTags.Where(pt => pt.Tag != null).Select(pt => new TagDto(pt.Tag!.Id, pt.Tag.Name, pt.Tag.Description, pt.Tag.Favorite, pt.Tag.IgnoreAutoTag, [])).ToList(),
        p.RemoteIds.Select(remoteId => new PerformerRemoteIdDto(remoteId.Endpoint, remoteId.RemoteId)).ToList(),
        sceneCount ?? p.ScenePerformers?.Count ?? 0, imageCount ?? p.ImagePerformers?.Count ?? 0, galleryCount ?? p.GalleryPerformers?.Count ?? 0, groupCount ?? 0,
        p.ImageBlobId != null ? EntityImageUrls.Performer(p.Id, p.UpdatedAt) : null,
        p.CustomFields,
        p.CreatedAt.ToString("o"), p.UpdatedAt.ToString("o")
    );

    private static DateOnly? ParseDate(string? date) => DateOnly.TryParse(date, out var d) ? d : null;
    private static T? ParseEnum<T>(string? value) where T : struct, Enum => Enum.TryParse<T>(value, true, out var e) ? e : null;
    private static List<int>? ParseIntList(string? csv) => string.IsNullOrEmpty(csv) ? null : csv.Split(',').Select(int.Parse).ToList();

    // ===== Bulk Operations =====

    [HttpPost("bulk")]
    public async Task<IActionResult> BulkUpdate([FromBody] BulkPerformerUpdateDto dto, CancellationToken ct)
    {
        var performers = await db.Performers
            .Include(p => p.PerformerTags)
            .Where(p => dto.Ids.Contains(p.Id))
            .ToListAsync(ct);

        foreach (var p in performers)
        {
            if (dto.Rating.HasValue) p.Rating = dto.Rating;
            if (dto.Favorite.HasValue) p.Favorite = dto.Favorite.Value;

            if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Set)
            {
                p.PerformerTags.Clear();
                p.PerformerTags = dto.TagIds.Select(tid => new PerformerTag { TagId = tid, PerformerId = p.Id }).ToList();
            }
            else if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Add)
            {
                var existing = p.PerformerTags.Select(pt => pt.TagId).ToHashSet();
                foreach (var tid in dto.TagIds.Where(t => !existing.Contains(t)))
                    p.PerformerTags.Add(new PerformerTag { TagId = tid, PerformerId = p.Id });
            }
            else if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Remove)
            {
                p.PerformerTags = p.PerformerTags.Where(pt => !dto.TagIds.Contains(pt.TagId)).ToList();
            }
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { updated = performers.Count });
    }

    // ===== Merge =====

    [HttpPost("merge")]
    public async Task<ActionResult<PerformerDto>> MergePerformers([FromBody] PerformerMergeDto dto, CancellationToken ct)
    {
        var target = await performerRepo.GetByIdWithRelationsAsync(dto.TargetId, ct);
        if (target == null) return NotFound("Target performer not found");

        var sources = await db.Performers
            .Include(p => p.Aliases)
            .Include(p => p.Urls)
            .Include(p => p.ScenePerformers)
            .Include(p => p.ImagePerformers)
            .Include(p => p.GalleryPerformers)
            .Where(p => dto.SourceIds.Contains(p.Id))
            .ToListAsync(ct);

        foreach (var source in sources)
        {
            // Move scene associations
            foreach (var sp in source.ScenePerformers)
                if (!target.ScenePerformers.Any(t => t.SceneId == sp.SceneId))
                    target.ScenePerformers.Add(new ScenePerformer { SceneId = sp.SceneId, PerformerId = target.Id });
            // Move image associations
            foreach (var ip in source.ImagePerformers)
                if (!target.ImagePerformers.Any(t => t.ImageId == ip.ImageId))
                    target.ImagePerformers.Add(new ImagePerformer { ImageId = ip.ImageId, PerformerId = target.Id });
            // Add source name as alias
            if (!target.Aliases.Any(a => a.Alias == source.Name))
                target.Aliases.Add(new PerformerAlias { Alias = source.Name, PerformerId = target.Id });
            // Delete source
            db.Performers.Remove(source);
        }

        await db.SaveChangesAsync(ct);
        var result = await performerRepo.GetByIdWithRelationsAsync(target.Id, ct);
        return Ok(MapToDto(result!));
    }
}
