using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Common;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using IAuthorizationService = Cove.Core.Auth.IAuthorizationService;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[RequiresPermission(Permissions.PerformersRead)]
public class PerformersController(IPerformerRepository performerRepo, MetadataServerService metadataServerService, PerformerScrapeService performerScrapeService, Data.CoveContext db, IUserEngagementService engagementService, IPerformerMergeService performerMergeService, CustomFieldService? customFields = null, IFieldProvenanceService? fieldProvenanceService = null) : ControllerBase
{
    private sealed record PerformerUsageCounts(int VideoCount, int ImageCount, int GalleryCount, int GroupCount, int AudioCount, int TextCount, int LikeCount);
    private readonly CustomFieldService _customFields = customFields ?? new CustomFieldService(db);

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
        var filter = new PerformerFilter { Name = name, Favorite = favorite, Rating = rating, TagIds = QueryParsing.ParseIntList(tagIds)?.ToList(), StudioId = studioId };
        var findFilter = new FindFilter
        {
            Q = q, Page = page, PerPage = perPage, Sort = sort,
            Direction = direction == "desc" ? SortDirection.Desc : SortDirection.Asc,
            Seed = seed,
        };

        var (items, totalCount) = await performerRepo.FindAsync(filter, findFilter, ct);
        var usageCountsByPerformerId = await LoadPerformerUsageCountsAsync(items.Select(item => item.Id), ct);
        var customFieldValues = await _customFields.GetValuesAsync(CustomFieldEntityTypes.Performer, items.Select(item => item.Id), ct);
        var dtos = items.Select(p => MapToDto(p, usageCountsByPerformerId.GetValueOrDefault(p.Id), GetCustomFields(customFieldValues, p.Id))).ToList();
        return Ok(new PaginatedResponse<PerformerDto>(dtos, totalCount, page, perPage));
    }

    [HttpPost("find")]
    public async Task<ActionResult<PaginatedResponse<PerformerDto>>> FindPost([FromBody] FilteredQueryRequest<PerformerFilter> req, CancellationToken ct)
    {
        var findFilter = req.FindFilter ?? new FindFilter();
        var filter = req.ObjectFilter ?? new PerformerFilter();
        var (items, totalCount) = await performerRepo.FindAsync(filter, findFilter, ct);
        var usageCountsByPerformerId = await LoadPerformerUsageCountsAsync(items.Select(item => item.Id), ct);
        var customFieldValues = await _customFields.GetValuesAsync(CustomFieldEntityTypes.Performer, items.Select(item => item.Id), ct);
        var dtos = items.Select(p => MapToDto(p, usageCountsByPerformerId.GetValueOrDefault(p.Id), GetCustomFields(customFieldValues, p.Id))).ToList();
        return Ok(new PaginatedResponse<PerformerDto>(dtos, totalCount, findFilter.Page, findFilter.PerPage));
    }

    [HttpGet("{id:int}")]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<PerformerDto>> GetById(int id, CancellationToken ct)
    {
        var performer = await performerRepo.GetByIdWithRelationsAsync(id, ct);
        if (performer == null) return NotFound();
        return Ok(await MapToDetailDtoAsync(performer, ct));
    }

    [HttpGet("{id:int}/groups")]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<PaginatedResponse<GroupDto>>> GetGroups(
        int id,
        [FromQuery] string? q,
        [FromQuery] int page = 1,
        [FromQuery] int perPage = 18,
        [FromQuery] string? sort = null,
        [FromQuery] string? direction = null,
        CancellationToken ct = default)
    {
        var performerExists = await db.Performers.AsNoTracking().AnyAsync(performer => performer.Id == id, ct);
        if (!performerExists)
            return NotFound();

        page = Math.Max(1, page);
        perPage = Math.Clamp(perPage, 1, 250);

        var directGroupIds = await db.GroupItems
            .AsNoTracking()
            .Where(item => (item.HostType == "performer" || item.Kind == GroupItemKind.Performer) && item.HostId == id)
            .Select(item => item.GroupId)
            .ToListAsync(ct);
        var videoGroupIds = await (
            from videoPerformer in db.Set<VideoPerformer>().AsNoTracking()
            join groupItem in db.GroupItems.AsNoTracking().Where(item => item.VideoId.HasValue)
                on videoPerformer.VideoId equals groupItem.VideoId!.Value
            where videoPerformer.PerformerId == id
            select groupItem.GroupId
        ).ToListAsync(ct);
        var groupIds = directGroupIds.Concat(videoGroupIds).Distinct().ToArray();
        if (groupIds.Length == 0)
            return Ok(new PaginatedResponse<GroupDto>([], 0, page, perPage));

        var query = db.Groups
            .AsNoTracking()
            .Include(group => group.Studio)
            .Include(group => group.Urls)
            .Include(group => group.GroupTags).ThenInclude(groupTag => groupTag.Tag).ThenInclude(tag => tag!.TagGroup)
            .Include(group => group.GroupItems)
            .Include(group => group.SubGroupRelations)
            .Include(group => group.ContainingGroupRelations)
            .AsSplitQuery()
            .Where(group => groupIds.Contains(group.Id));

        if (!string.IsNullOrWhiteSpace(q))
        {
            var normalizedQuery = q.Trim().ToLowerInvariant();
            query = query.Where(group => group.Name.ToLower().Contains(normalizedQuery) || (group.Aliases != null && group.Aliases.ToLower().Contains(normalizedQuery)));
        }

        query = ApplyGroupSort(query, sort, direction == "desc");
        var totalCount = await query.CountAsync(ct);
        var groups = await query.Skip((page - 1) * perPage).Take(perPage).ToListAsync(ct);
        var customFieldValues = await _customFields.GetValuesAsync(CustomFieldEntityTypes.Group, groups.Select(group => group.Id), ct);
        return Ok(new PaginatedResponse<GroupDto>(groups.Select(group => MapGroupToDto(group, GetCustomFields(customFieldValues, group.Id))).ToList(), totalCount, page, perPage));
    }

    [HttpGet("{id:int}/appears-with")]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<PaginatedResponse<PerformerDto>>> GetAppearsWith(
        int id,
        [FromQuery] string? q,
        [FromQuery] int page = 1,
        [FromQuery] int perPage = 18,
        [FromQuery] string? sort = null,
        [FromQuery] string? direction = null,
        CancellationToken ct = default)
    {
        var performerExists = await db.Performers.AsNoTracking().AnyAsync(performer => performer.Id == id, ct);
        if (!performerExists)
            return NotFound();

        page = Math.Max(1, page);
        perPage = Math.Clamp(perPage, 1, 250);

        var coPerformerCounts =
            from videoPerformer in db.Set<VideoPerformer>().AsNoTracking()
            join coPerformer in db.Set<VideoPerformer>().AsNoTracking()
                on videoPerformer.VideoId equals coPerformer.VideoId
            where videoPerformer.PerformerId == id && coPerformer.PerformerId != id
            group coPerformer by coPerformer.PerformerId into grouped
            select new
            {
                PerformerId = grouped.Key,
                VideoCount = grouped.Select(item => item.VideoId).Distinct().Count(),
            };

        var query =
            from relation in coPerformerCounts
            join performer in db.Performers.AsNoTracking() on relation.PerformerId equals performer.Id
            select new { relation.PerformerId, relation.VideoCount, performer.Name };

        if (!string.IsNullOrWhiteSpace(q))
        {
            var normalizedQuery = q.Trim().ToLowerInvariant();
            query = query.Where(item => item.Name.ToLower().Contains(normalizedQuery));
        }

        var desc = direction == "desc";
        query = sort switch
        {
            "name" => desc ? query.OrderByDescending(item => item.Name) : query.OrderBy(item => item.Name),
            _ => query.OrderByDescending(item => item.VideoCount).ThenBy(item => item.Name),
        };

        var totalCount = await query.CountAsync(ct);
        var pageRows = await query.Skip((page - 1) * perPage).Take(perPage).ToListAsync(ct);
        var pageIds = pageRows.Select(item => item.PerformerId).ToArray();
        var performers = await db.Performers
            .AsNoTracking()
            .Include(performer => performer.Urls)
            .Include(performer => performer.Aliases)
            .Include(performer => performer.PerformerTags).ThenInclude(performerTag => performerTag.Tag).ThenInclude(tag => tag!.TagGroup)
            .Include(performer => performer.RemoteIds)
            .AsSplitQuery()
            .Where(performer => pageIds.Contains(performer.Id))
            .ToListAsync(ct);
        var performersById = performers.ToDictionary(performer => performer.Id);
        var usageCountsByPerformerId = await LoadPerformerUsageCountsAsync(pageIds, ct);
        var customFieldValues = await _customFields.GetValuesAsync(CustomFieldEntityTypes.Performer, pageIds, ct);
        var dtos = pageRows
            .Where(row => performersById.ContainsKey(row.PerformerId))
            .Select(row => MapToDto(performersById[row.PerformerId], usageCountsByPerformerId.GetValueOrDefault(row.PerformerId), GetCustomFields(customFieldValues, row.PerformerId)))
            .ToList();

        return Ok(new PaginatedResponse<PerformerDto>(dtos, totalCount, page, perPage));
    }

    [HttpPost]
    [RequiresPermission(Permissions.PerformersWrite)]
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
            Favorite = dto.Favorite, Details = dto.Details
        };
        if (dto.Urls?.Count > 0) performer.Urls = dto.Urls.Select(u => new PerformerUrl { Url = u }).ToList();
        if (dto.Aliases?.Count > 0) performer.Aliases = dto.Aliases.Select(a => new PerformerAlias { Alias = a }).ToList();
        if (dto.TagIds?.Count > 0) performer.PerformerTags = dto.TagIds.Select(id => new PerformerTag { TagId = id }).ToList();
        if (dto.RemoteIds?.Count > 0) performer.RemoteIds = NormalizeRemoteIds(dto.RemoteIds).Select(remoteId => new PerformerRemoteId { Endpoint = remoteId.Endpoint, RemoteId = remoteId.RemoteId }).ToList();

        performer = await performerRepo.AddAsync(performer, ct);
        if (dto.CustomFields != null)
            await _customFields.SaveValuesAsync(CustomFieldEntityTypes.Performer, performer.Id, dto.CustomFields, ct);
        if (dto.Rating.HasValue)
            await engagementService.SetRatingAsync(AffinityHostType.Performer, performer.Id, dto.Rating, cancellationToken: ct);
        var result = await performerRepo.GetByIdWithRelationsAsync(performer.Id, ct);
        return CreatedAtAction(nameof(GetById), new { id = performer.Id }, await MapToDetailDtoAsync(result!, ct));
    }

    [HttpPut("{id:int}")]
    [RequiresPermission(Permissions.PerformersWrite)]
    [RequiresEntityAccess(EntityKinds.Performer, Permissions.PerformersWrite)]
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
        if (dto.Details != null) p.Details = dto.Details;

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
        if (dto.RemoteIds != null)
        {
            p.RemoteIds.Clear();
            p.RemoteIds = NormalizeRemoteIds(dto.RemoteIds).Select(remoteId => new PerformerRemoteId { PerformerId = id, Endpoint = remoteId.Endpoint, RemoteId = remoteId.RemoteId }).ToList();
        }
        foreach (var field in dto.ClearFields?.Distinct(StringComparer.OrdinalIgnoreCase) ?? [])
        {
            switch (field.ToLowerInvariant())
            {
                case "disambiguation": p.Disambiguation = null; break;
                case "gender": p.Gender = null; break;
                case "birthdate": p.Birthdate = null; break;
                case "deathdate": p.DeathDate = null; break;
                case "ethnicity": p.Ethnicity = null; break;
                case "country": p.Country = null; break;
                case "eyecolor": p.EyeColor = null; break;
                case "haircolor": p.HairColor = null; break;
                case "heightcm": p.HeightCm = null; break;
                case "weight": p.Weight = null; break;
                case "measurements": p.Measurements = null; break;
                case "faketits": p.FakeTits = null; break;
                case "penislength": p.PenisLength = null; break;
                case "circumcised": p.Circumcised = null; break;
                case "careerstart": p.CareerStart = null; break;
                case "careerend": p.CareerEnd = null; break;
                case "tattoos": p.Tattoos = null; break;
                case "piercings": p.Piercings = null; break;
                case "details": p.Details = null; break;
            }
        }
        await performerRepo.UpdateAsync(p, ct);
        if (dto.CustomFields != null)
            await _customFields.SaveValuesAsync(CustomFieldEntityTypes.Performer, id, dto.CustomFields, ct);
        if (dto.Rating.HasValue)
            await engagementService.SetRatingAsync(AffinityHostType.Performer, id, dto.Rating, cancellationToken: ct);
        var updated = await performerRepo.GetByIdWithRelationsAsync(id, ct);
        return Ok(await MapToDetailDtoAsync(updated!, ct));
    }

    [HttpPost("{id:int}/scrape-url")]
    [RequiresPermission(Permissions.PerformersScrape, Permissions.PerformersWrite)]
    [RequiresEntityAccess(EntityKinds.Performer, Permissions.PerformersWrite)]
    public async Task<ActionResult<PerformerDto>> ScrapeUrl(int id, [FromBody] PerformerScrapeUrlRequestDto dto, CancellationToken ct)
    {
        return await Scrape(id, new PerformerScrapeRequestDto("url", null, dto.Url, null, dto.CreateMissingTags), ct);
    }

    [HttpPost("{id:int}/scrape")]
    [RequiresPermission(Permissions.PerformersScrape, Permissions.PerformersWrite)]
    [RequiresEntityAccess(EntityKinds.Performer, Permissions.PerformersWrite)]
    public async Task<ActionResult<PerformerDto>> Scrape(int id, [FromBody] PerformerScrapeRequestDto dto, CancellationToken ct)
    {
        var performer = await performerRepo.GetByIdWithRelationsAsync(id, ct);
        if (performer == null)
            return NotFound();

        var resolvedScrape = await ResolveScrapeAsync(performer, dto, ct);
        if (resolvedScrape.ErrorResult != null)
            return resolvedScrape.ErrorResult;

        await performerScrapeService.ApplyAsync(performer, resolvedScrape.Scraped!, dto.CreateMissingTags, ct: ct);
        await performerRepo.UpdateAsync(performer, ct);

        var updated = await performerRepo.GetByIdWithRelationsAsync(id, ct);
        return Ok(await MapToDetailDtoAsync(updated!, ct));
    }

    [HttpPost("{id:int}/scrape-preview")]
    [RequiresPermission(Permissions.PerformersScrape)]
    public async Task<ActionResult<PerformerScrapePreviewDto>> PreviewScrape(int id, [FromBody] PerformerScrapeRequestDto dto, CancellationToken ct)
    {
        var performer = await performerRepo.GetByIdWithRelationsAsync(id, ct);
        if (performer == null)
            return NotFound();

        var resolvedScrape = await ResolveScrapeAsync(performer, dto, ct);
        if (resolvedScrape.ErrorResult != null)
            return resolvedScrape.ErrorResult;

        return Ok(new PerformerScrapePreviewDto(resolvedScrape.Scraped!, resolvedScrape.InputKind!, resolvedScrape.SourceValue));
    }

    [HttpPost("{id:int}/apply-scraped")]
    [RequiresPermission(Permissions.PerformersWrite)]
    [RequiresEntityAccess(EntityKinds.Performer, Permissions.PerformersWrite)]
    public async Task<ActionResult<PerformerDto>> ApplyScraped(int id, [FromBody] PerformerApplyScrapedRequestDto dto, CancellationToken ct)
    {
        var performer = await performerRepo.GetByIdWithRelationsAsync(id, ct);
        if (performer == null)
            return NotFound();

        await performerScrapeService.ApplyAsync(performer, dto.Scraped, dto.CreateMissingTags, dto.ReplaceFields, dto.CollectionModes, ct);
        await performerRepo.UpdateAsync(performer, ct);

        var updated = await performerRepo.GetByIdWithRelationsAsync(id, ct);
        return Ok(await MapToDetailDtoAsync(updated!, ct));
    }

    private async Task<ResolvedPerformerScrape> ResolveScrapeAsync(Performer performer, PerformerScrapeRequestDto dto, CancellationToken ct)
    {

        var inputKind = dto.InputKind?.Trim().ToLowerInvariant();
        if (inputKind is not ("url" or "name"))
            inputKind = !string.IsNullOrWhiteSpace(dto.Name) ? "name" : "url";

        ScrapedPerformerDto? scraped;
        string? sourceValue;
        if (inputKind == "name")
        {
            var name = string.IsNullOrWhiteSpace(dto.Name) ? performer.Name : dto.Name.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return new ResolvedPerformerScrape(BadRequest(new { error = "A performer name is required before scraping." }), null, null, null);

            scraped = await performerScrapeService.ScrapeByNameAsync(name, dto.ScraperId, ct);
            sourceValue = name;
        }
        else
        {
            var url = string.IsNullOrWhiteSpace(dto.Url)
                ? performer.Urls.Select(item => item.Url).FirstOrDefault()
                : dto.Url.Trim();

            if (string.IsNullOrWhiteSpace(url))
                return new ResolvedPerformerScrape(BadRequest(new { error = "A performer URL is required before scraping." }), null, null, null);

            scraped = await performerScrapeService.ScrapeByUrlAsync(url, dto.ScraperId, ct);
            sourceValue = url;
        }

        if (scraped == null)
            return new ResolvedPerformerScrape(NotFound(new { error = "Scrape returned no performer metadata." }), null, null, null);

        return new ResolvedPerformerScrape(null, scraped, inputKind, sourceValue);
    }

    private sealed record ResolvedPerformerScrape(ActionResult? ErrorResult, ScrapedPerformerDto? Scraped, string? InputKind, string? SourceValue);

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

    [HttpPost("metadata-server/find-by-ids")]
    public async Task<ActionResult<IReadOnlyList<MetadataServerPerformerMatchDto>>> FindMetadataServerPerformersByIds([FromBody] MetadataServerFindByIdsRequestDto dto, CancellationToken ct)
    {
        if (dto.Ids.Count == 0)
            return Ok(Array.Empty<MetadataServerPerformerMatchDto>());

        return Ok(await metadataServerService.GetPerformerMatchesAsync(dto.Endpoint, dto.Ids, ct));
    }

    [HttpPost("{id:int}/metadata-server/import")]
    [RequiresPermission(Permissions.PerformersWrite)]
    [RequiresEntityAccess(EntityKinds.Performer, Permissions.PerformersWrite)]
    public async Task<ActionResult<PerformerDto>> ImportFromMetadataServer(int id, [FromBody] MetadataServerPerformerImportRequestDto dto, CancellationToken ct)
    {
        var performer = await performerRepo.GetByIdWithRelationsAsync(id, ct);
        if (performer == null)
            return NotFound();

        var imported = await metadataServerService.MergePerformerAsync(performer, dto.Endpoint, dto.PerformerId, dto, ct);
        if (!imported)
            return NotFound();

        await performerRepo.UpdateAsync(performer, ct);
        var updated = await performerRepo.GetByIdWithRelationsAsync(id, ct);
        return Ok(await MapToDetailDtoAsync(updated!, ct));
    }

    [HttpPost("{id:int}/metadata-server/submit-draft")]
    [RequiresPermission(Permissions.PerformersWrite)]
    [RequiresEntityAccess(EntityKinds.Performer, Permissions.PerformersWrite)]
    public async Task<IActionResult> SubmitPerformerDraft(int id, [FromBody] MetadataServerEndpointDto dto, CancellationToken ct)
    {
        var performer = await performerRepo.GetByIdWithRelationsAsync(id, ct);
        if (performer == null) return NotFound();

        var draftId = await metadataServerService.SubmitPerformerDraftAsync(performer, dto.Endpoint, ct);
        return Ok(new { draftId });
    }

    [HttpPost("metadata-server/batch-tag")]
    [RequiresPermission(Permissions.PerformersWrite)]
    [RequiresEntityAccess(EntityKinds.Performer, Permissions.PerformersWrite, ActionArgumentName = "dto", PropertyName = "Ids")]
    public async Task<ActionResult<object>> BatchTagFromMetadataServer([FromBody] MetadataServerPerformerBatchTagRequestDto dto, [FromServices] IJobService jobService, [FromServices] IServiceScopeFactory scopeFactory, [FromServices] IAuthorizationService authorizationService, [FromServices] ICurrentPrincipalAccessor principalAccessor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Endpoint))
            return BadRequest(new { message = "Endpoint is required" });

        var ids = await ResolveSelectedPerformerIdsAsync(dto, ct);
        if (ids.Count == 0)
            return BadRequest(new { message = "No performers selected for batch tagging" });

        var principal = principalAccessor.Current;
        if (principal == null)
            return Forbid();

        foreach (var id in ids)
        {
            var result = await authorizationService.AuthorizeAsync(
                principal,
                Permissions.PerformersWrite,
                new EntityRef(EntityKinds.Performer, id.ToString()),
                ct);

            if (!result.Allowed)
                return Forbid();
        }

        var jobId = jobService.Enqueue(
            "metadata-server:performers",
            $"Tagging {ids.Count} performers from {dto.Endpoint}",
            async (progress, jobCt) =>
            {
                using var scope = scopeFactory.CreateScope();
                var metadataServerService = scope.ServiceProvider.GetRequiredService<MetadataServerService>();
                await metadataServerService.BatchTagPerformersAsync(dto.Endpoint, ids, dto.RefreshAlreadyTagged, dto.ExcludeFields, progress, jobCt);
            });

        return Ok(new { jobId, itemCount = ids.Count });
    }

    [HttpDelete("{id:int}")]
    [RequiresPermission(Permissions.PerformersDelete)]
    [RequiresEntityAccess(EntityKinds.Performer, Permissions.PerformersDelete)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var p = await performerRepo.GetByIdAsync(id, ct);
        if (p == null) return NotFound();
        await _customFields.DeleteValuesForEntityAsync(CustomFieldEntityTypes.Performer, id, ct);
        await performerRepo.DeleteAsync(id, ct);
        return NoContent();
    }

    private async Task<PerformerDto> MapToDetailDtoAsync(Performer performer, CancellationToken ct)
    {
        var usageCounts = (await LoadPerformerUsageCountsAsync([performer.Id], ct)).GetValueOrDefault(performer.Id);
        var customFieldValues = await _customFields.GetValuesAsync(CustomFieldEntityTypes.Performer, performer.Id, ct);
        var fieldProvenance = fieldProvenanceService == null
            ? null
            : (await fieldProvenanceService.GetForHostAsync(AffinityHostType.Performer, performer.Id, ct)).ToList();
        var faceCount = await db.Faces
            .AsNoTracking()
            .CountAsync(face => face.PerformerId == performer.Id && face.MergedIntoFaceId == null, ct);
        return MapToDto(performer, usageCounts, customFieldValues, fieldProvenance, faceCount);
    }

    private static IQueryable<Group> ApplyGroupSort(IQueryable<Group> query, string? sort, bool desc) => sort switch
    {
        "date" => desc ? query.OrderByDescending(group => group.Date) : query.OrderBy(group => group.Date),
        "created_at" => desc ? query.OrderByDescending(group => group.CreatedAt) : query.OrderBy(group => group.CreatedAt),
        "updated_at" => desc ? query.OrderByDescending(group => group.UpdatedAt) : query.OrderBy(group => group.UpdatedAt),
        "item_count" => desc ? query.OrderByDescending(group => group.GroupItems.Count) : query.OrderBy(group => group.GroupItems.Count),
        _ => desc ? query.OrderByDescending(group => group.Name) : query.OrderBy(group => group.Name),
    };

    private GroupDto MapGroupToDto(Group group, Dictionary<string, object>? customFieldValues = null) => new(
        group.Id,
        group.Name,
        group.Aliases,
        group.Date?.ToString("yyyy-MM-dd"),
        group.StudioId,
        group.Studio?.Name,
        group.Director,
        group.Synopsis,
        group.Urls.Select(url => url.Url).ToList(),
        group.GroupTags.Where(groupTag => groupTag.Tag != null).Select(groupTag => TagDtoMapping.MapTagDto(groupTag.Tag!)).ToList(),
        group.GroupItems.Select(item => item.VideoId).Where(videoId => videoId.HasValue).Distinct().Count(),
        group.GroupItems.Count,
        group.GroupItems.Any(item => item.Kind == GroupItemKind.VideoRange),
        group.SubGroupRelations?.Count ?? 0,
        group.ContainingGroupRelations?.Count ?? 0,
        customFieldValues,
        group.CreatedAt.ToString("o"),
        group.UpdatedAt.ToString("o"),
        ResolveGroupFrontImagePath(group),
        group.BackImageBlobId != null ? EntityImageUrls.GroupBack(ControllerContext.HttpContext, group.Id, group.UpdatedAt) : null,
        group.Kind,
        group.QuerySourceKey,
        group.QueryJson,
        group.LastResolvedAt?.ToString("o"),
        group.CachedItemCount,
        group.ShowInVideoLists,
        group.AllowedHostTypes);

    private string? ResolveGroupFrontImagePath(Group group)
        => group.FrontImageBlobId != null || group.GroupItems.Any(item => item.ImageId.HasValue || item.VideoId.HasValue)
            ? EntityImageUrls.GroupFront(ControllerContext.HttpContext, group.Id, group.UpdatedAt)
            : null;

    private PerformerDto MapToDto(Performer p, PerformerUsageCounts? usageCounts = null, Dictionary<string, object>? customFieldValues = null, List<FieldProvenanceDto>? fieldProvenance = null, int faceCount = 0) => new(
        p.Id, p.Name, p.Disambiguation, p.Gender?.ToString(),
        p.Birthdate?.ToString("yyyy-MM-dd"), p.DeathDate?.ToString("yyyy-MM-dd"),
        p.Ethnicity, p.Country, p.EyeColor, p.HairColor, p.HeightCm, p.Weight,
        p.Measurements, p.FakeTits, p.PenisLength, p.Circumcised?.ToString(),
        p.CareerStart?.ToString("yyyy-MM-dd"), p.CareerEnd?.ToString("yyyy-MM-dd"),
        p.Tattoos, p.Piercings, p.Favorite, p.Details,
        p.Urls.Select(u => u.Url).ToList(),
        p.Aliases.Select(a => a.Alias).ToList(),
        p.PerformerTags
            .Where(pt => pt.Tag != null)
            .OrderBy(pt => TagDtoMapping.EffectiveSortName(pt.Tag!))
            .Select(pt => TagDtoMapping.MapTagDto(pt.Tag!))
            .ToList(),
        p.RemoteIds.Select(remoteId => new PerformerRemoteIdDto(remoteId.Endpoint, remoteId.RemoteId)).ToList(),
        usageCounts?.VideoCount ?? p.VideoCount,
        usageCounts?.ImageCount ?? p.ImageCount,
        usageCounts?.GalleryCount ?? p.GalleryCount,
        usageCounts?.GroupCount ?? 0,
        usageCounts?.AudioCount ?? 0,
        usageCounts?.TextCount ?? 0,
        EntityImageUrls.PerformerOrNull(ControllerContext.HttpContext, p),
        customFieldValues,
        p.CreatedAt.ToString("o"), p.UpdatedAt.ToString("o"),
        fieldProvenance,
        faceCount,
        usageCounts?.LikeCount ?? 0
    );

    private static Dictionary<string, object>? GetCustomFields(IReadOnlyDictionary<int, Dictionary<string, object>> lookup, int id)
        => lookup.TryGetValue(id, out var values) && values.Count > 0 ? values : null;

    private static List<PerformerRemoteIdDto> NormalizeRemoteIds(IEnumerable<PerformerRemoteIdDto> remoteIds)
        => remoteIds
            .Select(remoteId => new PerformerRemoteIdDto(remoteId.Endpoint.Trim(), remoteId.RemoteId.Trim()))
            .Where(remoteId => !string.IsNullOrWhiteSpace(remoteId.Endpoint) && !string.IsNullOrWhiteSpace(remoteId.RemoteId))
            .GroupBy(remoteId => new { Endpoint = remoteId.Endpoint.ToUpperInvariant(), RemoteId = remoteId.RemoteId.ToUpperInvariant() })
            .Select(group => group.First())
            .ToList();

    private async Task<Dictionary<int, PerformerUsageCounts>> LoadPerformerUsageCountsAsync(IEnumerable<int> performerIds, CancellationToken ct)
    {
        var ids = performerIds
            .Where(performerId => performerId > 0)
            .Distinct()
            .ToArray();

        if (ids.Length == 0)
            return [];

        var videoCounts = await db.Set<VideoPerformer>()
            .AsNoTracking()
            .Where(videoPerformer => ids.Contains(videoPerformer.PerformerId))
            .GroupBy(videoPerformer => videoPerformer.PerformerId)
            .Select(group => new { group.Key, Count = group.Select(videoPerformer => videoPerformer.VideoId).Distinct().Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var imageCounts = await db.Set<ImagePerformer>()
            .AsNoTracking()
            .Where(imagePerformer => ids.Contains(imagePerformer.PerformerId))
            .GroupBy(imagePerformer => imagePerformer.PerformerId)
            .Select(group => new { group.Key, Count = group.Select(imagePerformer => imagePerformer.ImageId).Distinct().Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var galleryCounts = await db.Set<GalleryPerformer>()
            .AsNoTracking()
            .Where(galleryPerformer => ids.Contains(galleryPerformer.PerformerId))
            .GroupBy(galleryPerformer => galleryPerformer.PerformerId)
            .Select(group => new { group.Key, Count = group.Select(galleryPerformer => galleryPerformer.GalleryId).Distinct().Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var audioCounts = await db.Set<AudioPerformer>()
            .AsNoTracking()
            .Where(audioPerformer => ids.Contains(audioPerformer.PerformerId))
            .GroupBy(audioPerformer => audioPerformer.PerformerId)
            .Select(group => new { group.Key, Count = group.Select(audioPerformer => audioPerformer.AudioId).Distinct().Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var textCounts = await db.Set<TextPerformer>()
            .AsNoTracking()
            .Where(textPerformer => ids.Contains(textPerformer.PerformerId))
            .GroupBy(textPerformer => textPerformer.PerformerId)
            .Select(group => new { group.Key, Count = group.Select(textPerformer => textPerformer.TextDocumentId).Distinct().Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, ct);
        var currentUserId = Data.Repositories.EngagementQueryHelpers.CurrentUserId(db);
        var likeCounts = currentUserId is int selectedUserId
            ? await (
                from videoPerformer in db.Set<VideoPerformer>().AsNoTracking()
                join affinity in db.UserEntityAffinities.AsNoTracking()
                    on videoPerformer.VideoId equals affinity.HostId
                where ids.Contains(videoPerformer.PerformerId)
                    && affinity.UserId == selectedUserId
                    && affinity.HostType == AffinityHostType.Video
                group affinity by videoPerformer.PerformerId into performerLikes
                select new { PerformerId = performerLikes.Key, Count = performerLikes.Sum(affinity => affinity.LikeCount) }
            ).ToDictionaryAsync(item => item.PerformerId, item => item.Count, ct)
            : [];

        var directGroupRows = await db.GroupItems
            .AsNoTracking()
            .Where(groupItem => groupItem.HostType == "performer" && ids.Contains(groupItem.HostId))
            .Select(groupItem => new { PerformerId = groupItem.HostId, groupItem.GroupId })
            .ToListAsync(ct);
        var videoGroupRows = await (
            from videoPerformer in db.Set<VideoPerformer>().AsNoTracking()
            join groupItem in db.GroupItems.AsNoTracking().Where(item => item.VideoId.HasValue)
                on videoPerformer.VideoId equals groupItem.VideoId!.Value
            where ids.Contains(videoPerformer.PerformerId)
            select new { videoPerformer.PerformerId, groupItem.GroupId }
        ).ToListAsync(ct);
        var groupCounts = directGroupRows
            .Concat(videoGroupRows)
            .GroupBy(item => item.PerformerId)
            .ToDictionary(group => group.Key, group => group.Select(item => item.GroupId).Distinct().Count());

        return ids.ToDictionary(
            id => id,
            id => new PerformerUsageCounts(
                videoCounts.GetValueOrDefault(id),
                imageCounts.GetValueOrDefault(id),
                galleryCounts.GetValueOrDefault(id),
                groupCounts.GetValueOrDefault(id),
                audioCounts.GetValueOrDefault(id),
                textCounts.GetValueOrDefault(id),
                likeCounts.GetValueOrDefault(id)));
    }

    private static DateOnly? ParseDate(string? date) => DateOnly.TryParse(date, out var d) ? d : null;
    private static T? ParseEnum<T>(string? value) where T : struct, Enum => Enum.TryParse<T>(value, true, out var e) ? e : null;

    private async Task<List<int>> ResolveSelectedPerformerIdsAsync(MetadataServerPerformerBatchTagRequestDto dto, CancellationToken ct)
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
            var (items, totalCount) = await performerRepo.FindAsync(dto.Filter, new FindFilter
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

    // ===== Bulk Operations =====

    [HttpPost("bulk")]
    [RequiresPermission(Permissions.PerformersWrite)]
    public async Task<IActionResult> BulkUpdate([FromBody] BulkPerformerUpdateDto dto, CancellationToken ct)
    {
        var performers = await db.Performers
            .Include(p => p.PerformerTags)
            .Where(p => dto.Ids.Contains(p.Id))
            .ToListAsync(ct);

        foreach (var p in performers)
        {
            if (dto.Favorite.HasValue) p.Favorite = dto.Favorite.Value;
            if (dto.Gender != null) p.Gender = ParseEnum<GenderEnum>(dto.Gender);
            if (dto.Details != null) p.Details = dto.Details;

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
        if (dto.Rating.HasValue)
        {
            foreach (var performer in performers)
                await engagementService.SetRatingAsync(AffinityHostType.Performer, performer.Id, dto.Rating, cancellationToken: ct);
        }
        return Ok(new { updated = performers.Count });
    }

    [HttpDelete("bulk")]
    [RequiresPermission(Permissions.PerformersDelete)]
    [RequiresEntityAccess(EntityKinds.Performer, Permissions.PerformersDelete, ActionArgumentName = "dto", PropertyName = "Ids")]
    public async Task<IActionResult> BulkDelete([FromBody] BatchDeleteDto dto, CancellationToken ct)
    {
        var ids = dto.Ids.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return Ok(new { deleted = 0 });

        var performers = await db.Performers.Where(performer => ids.Contains(performer.Id)).ToListAsync(ct);
        foreach (var performer in performers)
            await _customFields.DeleteValuesForEntityAsync(CustomFieldEntityTypes.Performer, performer.Id, ct);
        db.Performers.RemoveRange(performers);
        await db.SaveChangesAsync(ct);
        return Ok(new { deleted = performers.Count });
    }

    // ===== Merge =====

    [HttpPost("merge")]
    [RequiresPermission(Permissions.PerformersWrite)]
    public async Task<ActionResult<PerformerDto>> MergePerformers([FromBody] PerformerMergeDto dto, CancellationToken ct)
    {
        var merged = await performerMergeService.MergeAsync(dto.TargetId, dto.SourceIds, ct);
        if (merged == null) return NotFound("Target performer not found");

        var result = await performerRepo.GetByIdWithRelationsAsync(merged.Id, ct);
        return Ok(await MapToDetailDtoAsync(result!, ct));
    }
}
