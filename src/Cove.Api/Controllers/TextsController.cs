using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Cove.Api.Http;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Helpers;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[RequiresPermission(Permissions.TextsRead)]
public class TextsController(CoveContext db, CustomFieldService customFields, TextExtractionService textExtractionService, IScanService scanService, IThumbnailService thumbnailService, IBlobService blobService, ICurrentPrincipalAccessor? principalAccessor = null, IFieldProvenanceService? fieldProvenanceService = null) : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private bool CanReadFiles => principalAccessor?.Current?.Has(Permissions.FilesRead) == true;

    [HttpGet]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<PaginatedResponse<TextDocumentDto>>> Find(
        [FromQuery] string? q,
        [FromQuery] int page = 1,
        [FromQuery] int perPage = 25,
        [FromQuery] string? sort = null,
        [FromQuery] string? direction = null,
        [FromQuery] int? seed = null,
        [FromQuery] string? sorts = null,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        perPage = Math.Clamp(perPage, 1, 250);
        var sortClauses = SortClause.Parse(sorts);
        var primarySort = sortClauses.FirstOrDefault();
        sort = primarySort?.Key ?? sort;
        var descending = primarySort?.Direction == Cove.Core.Enums.SortDirection.Desc
            || (primarySort is null && string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase));

        var query = db.TextDocuments.AsNoTracking().AsQueryable();

        var textBase = query;
        var textQuery = FullTextSearchHelpers.Apply(db, textBase, q,
            text => text.Title,
            text => text.Code,
            text => text.Details,
            text => text.FileSearchText,
            text => text.SearchText);
        query = FullTextSearchHelpers.ApplyRelationalMatches(textQuery, textBase, q,
            tagSelectors: [text => text.TextTags.Where(tt => tt.Tag != null).Select(tt => tt.Tag!)],
            performerSelectors: [text => text.TextPerformers.Where(tp => tp.Performer != null).Select(tp => tp.Performer!)]);
        query = FullTextSearchHelpers.ApplyFilePathMatch(query, textBase, q, text => text.Files);

        query = ApplySort(query, sort, descending, seed, sortClauses);
        if (FullTextSearchHelpers.ShouldOrderByRelevance(db, q, sort))
            query = FullTextSearchHelpers.OrderByRelevance(db, query, q);

        var totalCount = await query.CountAsync(ct);
        var pagedIds = await query
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .Select(text => text.Id)
            .ToListAsync(ct);

        var items = await LoadListItemsAsync(pagedIds, ct);

        var dtos = items.Select(text => MapToDto(text, null, null)).ToList();
        return Ok(new PaginatedResponse<TextDocumentDto>(dtos, totalCount, page, perPage));
    }

    [HttpPost("find")]
    public async Task<ActionResult<PaginatedResponse<TextDocumentDto>>> FindPost([FromBody] FilteredQueryRequest<TextDocumentFilter> req, CancellationToken ct)
    {
        var findFilter = req.FindFilter ?? new FindFilter();
        var page = Math.Max(1, findFilter.Page);
        var perPage = Math.Clamp(findFilter.PerPage, 1, 250);
        var descending = findFilter.Direction == Cove.Core.Enums.SortDirection.Desc;
        ExpandedHierarchyCriterion? expandedTags = null;
        if (HierarchicalCriterionExpander.RequiresExpansion(req.ObjectFilter?.TagsCriterion))
        {
            expandedTags = await HierarchicalCriterionExpander.ExpandTagsAsync(db, req.ObjectFilter!.TagsCriterion!, ct);
            req.ObjectFilter.TagsCriterion = expandedTags.Criterion;
        }
        ExpandedHierarchyCriterion? expandedStudios = null;
        if (HierarchicalCriterionExpander.RequiresExpansion(req.ObjectFilter?.StudiosCriterion))
        {
            expandedStudios = await HierarchicalCriterionExpander.ExpandStudiosAsync(db, req.ObjectFilter!.StudiosCriterion!, ct);
            req.ObjectFilter.StudiosCriterion = expandedStudios.Criterion;
        }

        var query = db.TextDocuments.AsNoTracking().AsQueryable();

        var textBase = query;
        var textQuery = FullTextSearchHelpers.Apply(db, textBase, findFilter.Q,
            text => text.Title,
            text => text.Code,
            text => text.Details,
            text => text.FileSearchText,
            text => text.SearchText);
        query = FullTextSearchHelpers.ApplyRelationalMatches(textQuery, textBase, findFilter.Q,
            tagSelectors: [text => text.TextTags.Where(tt => tt.Tag != null).Select(tt => tt.Tag!)],
            performerSelectors: [text => text.TextPerformers.Where(tp => tp.Performer != null).Select(tp => tp.Performer!)]);
        query = FullTextSearchHelpers.ApplyFilePathMatch(query, textBase, findFilter.Q, text => text.Files);

        query = ApplyFilter(query, req.ObjectFilter, expandedTags?.ValueGroups, expandedTags?.RequiredIdGroups, expandedStudios?.ValueGroups, expandedStudios?.RequiredIdGroups);
        query = ApplySort(query, findFilter.Sort, descending, findFilter.Seed, findFilter.Sorts);
        if (FullTextSearchHelpers.ShouldOrderByRelevance(db, findFilter.Q, findFilter.Sort))
            query = FullTextSearchHelpers.OrderByRelevance(db, query, findFilter.Q);

        var totalCount = await query.CountAsync(ct);
        var pagedIds = await query
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .Select(text => text.Id)
            .ToListAsync(ct);

        var items = await LoadListItemsAsync(pagedIds, ct);

        var dtos = items.Select(text => MapToDto(text, null, null)).ToList();
        return Ok(new PaginatedResponse<TextDocumentDto>(dtos, totalCount, page, perPage));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<TextDocumentDto>> GetById(int id, CancellationToken ct)
    {
        var text = await db.TextDocuments.AsNoTracking()
            .Include(item => item.Studio)
            .Include(item => item.Urls)
            .Include(item => item.Files)
            .Include(item => item.TextTags).ThenInclude(link => link.Tag)
            .Include(item => item.TextPerformers).ThenInclude(link => link.Performer)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (text == null)
        {
            return NotFound();
        }

        return Ok(await MapToDetailDtoAsync(text, ct));
    }

    [HttpGet("{id:int}/content")]
    public async Task<ActionResult<TextContentDto>> GetContent(int id, CancellationToken ct)
    {
        var text = await db.TextDocuments.AsNoTracking()
            .Include(item => item.Files)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        var file = text?.Files
            .OrderByDescending(item => item.WordCount)
            .ThenBy(item => item.Id)
            .FirstOrDefault();
        if (file == null || string.IsNullOrWhiteSpace(file.Path) || !System.IO.File.Exists(file.Path))
        {
            return NotFound();
        }

        var extracted = await textExtractionService.ExtractContentAsync(file.Path, ct);
        return Ok(new TextContentDto(extracted.Format, extracted.RenderMode, extracted.Content));
    }

    [HttpGet("{id:int}/file")]
    [RequiresPermission(Permissions.StreamRead)]
    public async Task<IActionResult> GetFile(int id, CancellationToken ct)
    {
        var text = await db.TextDocuments.AsNoTracking()
            .Include(item => item.Files)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        var file = text?.Files
            .OrderByDescending(item => item.WordCount)
            .ThenBy(item => item.Id)
            .FirstOrDefault();
        if (file == null || string.IsNullOrWhiteSpace(file.Path) || !System.IO.File.Exists(file.Path))
        {
            return NotFound();
        }

        if (!ContentTypes.TryGetContentType(file.Path, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return File(stream, contentType, enableRangeProcessing: true);
    }

    [HttpPost]
    [RequiresPermission(Permissions.TextsWrite)]
    public async Task<ActionResult<TextDocumentDto>> Create([FromBody] TextDocumentCreateDto dto, CancellationToken ct)
    {
        var tagIds = dto.TagIds?.Where(tagId => tagId > 0).Distinct().ToArray() ?? [];
        var performerIds = dto.PerformerIds?.Where(performerId => performerId > 0).Distinct().ToArray() ?? [];
        var text = new TextDocument
        {
            Title = NormalizeOptionalText(dto.Title),
            Code = NormalizeOptionalText(dto.Code),
            Details = NormalizeOptionalText(dto.Details),
            Organized = dto.Organized,
            StudioId = dto.StudioId,
            Date = ParseDate(dto.Date),
            TagIds = tagIds,
            PerformerIds = performerIds,
            Urls = dto.Urls?.Select(NormalizeOptionalText).Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => new TextUrl { Url = url! }).ToList() ?? [],
            TextTags = tagIds.Select(tagId => new TextTag { TagId = tagId }).ToList(),
            TextPerformers = performerIds.Select(performerId => new TextPerformer { PerformerId = performerId }).ToList(),
        };

        db.TextDocuments.Add(text);
        await db.SaveChangesAsync(ct);

        if (dto.GroupIds != null)
        {
            await ReplaceWholeTextGroupItemsAsync(text.Id, dto.GroupIds, text.Title, ct);
            await db.SaveChangesAsync(ct);
        }

        if (dto.CustomFields != null)
        {
            await customFields.SaveValuesAsync(CustomFieldEntityTypes.Text, text.Id, dto.CustomFields, ct);
        }

        var created = await GetTextForDtoAsync(text.Id, ct);
        if (created == null) return NotFound();
        return CreatedAtAction(nameof(GetById), new { id = text.Id }, await MapToDetailDtoAsync(created, ct));
    }

    [HttpPost("from-file")]
    [RequiresPermission(Permissions.TextsWrite)]
    public async Task<ActionResult<TextDocumentDto>> CreateFromFile([FromBody] FileBackedCreateDto? dto, CancellationToken ct)
    {
        var filePath = dto?.FilePath?.Trim();
        if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
            return BadRequest(new { error = "A valid file path is required." });

        var textDocumentId = await scanService.ImportDownloadedTextAsync(filePath, textDocumentId: null, ct);
        var text = await db.TextDocuments.AsNoTracking()
            .Include(item => item.Studio)
            .Include(item => item.Urls)
            .Include(item => item.Files)
            .Include(item => item.TextTags).ThenInclude(link => link.Tag)
            .Include(item => item.TextPerformers).ThenInclude(link => link.Performer)
            .FirstOrDefaultAsync(item => item.Id == textDocumentId, ct);
        if (text == null) return NotFound();

        return CreatedAtAction(nameof(GetById), new { id = textDocumentId }, await MapToDetailDtoAsync(text, ct));
    }

    [HttpPost("{id:int}/rescan")]
    [RequiresPermission(Permissions.LibraryScan)]
    [RequiresEntityAccess(EntityKinds.Text, Permissions.LibraryScan)]
    public async Task<IActionResult> Rescan(int id, CancellationToken ct)
    {
        var text = await db.TextDocuments.AsNoTracking()
            .Include(item => item.Files)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (text == null) return NotFound();

        var filePath = text.Files
            .Select(file => file.Path)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (string.IsNullOrWhiteSpace(filePath)) return BadRequest("Text has no files");

        var jobId = scanService.StartScan(new ScanOperationOptions
        {
            Paths = [filePath],
            Rescan = true,
        });
        return Ok(new { jobId });
    }

    [HttpPut("{id:int}")]
    [RequiresPermission(Permissions.TextsWrite)]
    [RequiresEntityAccess(EntityKinds.Text, Permissions.TextsWrite)]
    public async Task<ActionResult<TextDocumentDto>> Update(int id, [FromBody] TextDocumentUpdateDto dto, CancellationToken ct)
    {
        var text = await db.TextDocuments
            .Include(item => item.Urls)
            .Include(item => item.TextTags)
            .Include(item => item.TextPerformers)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (text == null)
        {
            return NotFound();
        }
        var clearFields = dto.ClearFields?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        if (dto.Title != null) text.Title = NormalizeOptionalText(dto.Title);
        if (dto.Code != null) text.Code = NormalizeOptionalText(dto.Code);
        if (dto.Details != null) text.Details = NormalizeOptionalText(dto.Details);
        if (dto.Organized.HasValue) text.Organized = dto.Organized.Value;
        if (dto.StudioId.HasValue) text.StudioId = dto.StudioId;
        if (dto.Date != null) text.Date = ParseDate(dto.Date);
        if (clearFields.Contains("studioId")) text.StudioId = null;

        if (dto.Urls != null)
        {
            text.Urls.Clear();
            text.Urls = dto.Urls
                .Select(url => NormalizeOptionalText(url))
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(url => new TextUrl { TextDocumentId = id, Url = url! })
                .ToList();
        }

        if (dto.TagIds != null)
        {
            var tagIds = dto.TagIds.Where(tagId => tagId > 0).Distinct().ToArray();
            text.TextTags.Clear();
            text.TextTags = tagIds.Select(tagId => new TextTag { TextDocumentId = id, TagId = tagId }).ToList();
            text.TagIds = tagIds;
        }

        if (dto.PerformerIds != null)
        {
            var performerIds = dto.PerformerIds.Where(performerId => performerId > 0).Distinct().ToArray();
            text.TextPerformers.Clear();
            text.TextPerformers = performerIds.Select(performerId => new TextPerformer { TextDocumentId = id, PerformerId = performerId }).ToList();
            text.PerformerIds = performerIds;
        }

        if (dto.GroupIds != null)
        {
            await ReplaceWholeTextGroupItemsAsync(id, dto.GroupIds, text.Title, ct);
        }

        await db.SaveChangesAsync(ct);

        if (dto.CustomFields != null)
        {
            await customFields.SaveValuesAsync(CustomFieldEntityTypes.Text, id, dto.CustomFields, ct);
        }

        var updated = await db.TextDocuments.AsNoTracking()
            .Include(item => item.Studio)
            .Include(item => item.Files)
            .Include(item => item.TextTags).ThenInclude(link => link.Tag)
            .Include(item => item.TextPerformers).ThenInclude(link => link.Performer)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (updated == null)
        {
            return NotFound();
        }

        return Ok(await MapToDetailDtoAsync(updated, ct));
    }

    [HttpPost("bulk")]
    [RequiresPermission(Permissions.TextsWrite)]
    [RequiresEntityAccess(EntityKinds.Text, Permissions.TextsWrite, ActionArgumentName = "dto", PropertyName = "Ids")]
    public async Task<IActionResult> BulkUpdate([FromBody] BulkTextDocumentUpdateDto dto, CancellationToken ct)
    {
        var items = await db.TextDocuments
            .Include(item => item.TextTags)
            .Include(item => item.TextPerformers)
            .Where(item => dto.Ids.Contains(item.Id))
            .ToListAsync(ct);
        var clearFields = dto.ClearFields?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        foreach (var text in items)
        {
            if (clearFields.Contains("studioId")) text.StudioId = null;
            if (clearFields.Contains("date")) text.Date = null;
            if (clearFields.Contains("code")) text.Code = null;
            if (clearFields.Contains("details")) text.Details = null;
            if (dto.Organized.HasValue) text.Organized = dto.Organized.Value;
            if (dto.StudioId.HasValue) text.StudioId = dto.StudioId;
            if (dto.Date != null) text.Date = ParseDate(dto.Date);
            if (dto.Code != null) text.Code = NormalizeOptionalText(dto.Code);
            if (dto.Details != null) text.Details = NormalizeOptionalText(dto.Details);

            if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Set)
            {
                text.TextTags.Clear();
                text.TextTags = dto.TagIds.Where(tagId => tagId > 0).Distinct().Select(tagId => new TextTag { TextDocumentId = text.Id, TagId = tagId }).ToList();
            }
            else if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Add)
            {
                var existing = text.TextTags.Select(link => link.TagId).ToHashSet();
                foreach (var tagId in dto.TagIds.Where(tagId => tagId > 0).Distinct().Where(tagId => !existing.Contains(tagId)))
                    text.TextTags.Add(new TextTag { TextDocumentId = text.Id, TagId = tagId });
            }
            else if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Remove)
            {
                text.TextTags = text.TextTags.Where(link => !dto.TagIds.Contains(link.TagId)).ToList();
            }

            if (dto.PerformerIds != null && dto.PerformerMode == BulkUpdateMode.Set)
            {
                text.TextPerformers.Clear();
                text.TextPerformers = dto.PerformerIds.Where(performerId => performerId > 0).Distinct().Select(performerId => new TextPerformer { TextDocumentId = text.Id, PerformerId = performerId }).ToList();
            }
            else if (dto.PerformerIds != null && dto.PerformerMode == BulkUpdateMode.Add)
            {
                var existing = text.TextPerformers.Select(link => link.PerformerId).ToHashSet();
                foreach (var performerId in dto.PerformerIds.Where(performerId => performerId > 0).Distinct().Where(performerId => !existing.Contains(performerId)))
                    text.TextPerformers.Add(new TextPerformer { TextDocumentId = text.Id, PerformerId = performerId });
            }
            else if (dto.PerformerIds != null && dto.PerformerMode == BulkUpdateMode.Remove)
            {
                text.TextPerformers = text.TextPerformers.Where(link => !dto.PerformerIds.Contains(link.PerformerId)).ToList();
            }

            if (dto.TagIds != null) text.TagIds = text.TextTags.Select(link => link.TagId).Distinct().ToArray();
            if (dto.PerformerIds != null) text.PerformerIds = text.TextPerformers.Select(link => link.PerformerId).Distinct().ToArray();
        }

        await db.SaveChangesAsync(ct);
        return Ok(new BulkUpdateResult(items.Select(text => text.Id).ToList()));
    }

    [HttpDelete("bulk")]
    [RequiresPermission(Permissions.TextsDelete)]
    [RequiresEntityAccess(EntityKinds.Text, Permissions.TextsDelete, ActionArgumentName = "dto", PropertyName = "Ids")]
    public async Task<IActionResult> BulkDelete([FromBody] BatchDeleteDto dto, CancellationToken ct)
    {
        var ids = dto.Ids.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return new EntityMutationNoContentResult([]);

        var idsToDelete = ids.ToHashSet();
        var deletedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = await db.TextDocuments.Include(item => item.Files).Where(item => ids.Contains(item.Id)).ToListAsync(ct);
        var groupItems = await db.GroupItems.Where(item => item.HostType == "text" && ids.Contains(item.HostId)).ToListAsync(ct);
        db.GroupItems.RemoveRange(groupItems);
        foreach (var item in items)
            await DeleteTextArtifactsAsync(item, idsToDelete, deletedPaths, dto.DeleteFiles, dto.DeleteGenerated, ct);
        foreach (var id in ids)
        {
            await customFields.DeleteValuesForEntityAsync(CustomFieldEntityTypes.Text, id, ct);
        }
        db.TextDocuments.RemoveRange(items);
        await db.SaveChangesAsync(ct);
        return new EntityMutationNoContentResult(items.Select(item => item.Id).ToList());
    }

    [HttpDelete("{id:int}")]
    [RequiresPermission(Permissions.TextsDelete)]
    [RequiresEntityAccess(EntityKinds.Text, Permissions.TextsDelete)]
    public async Task<IActionResult> Delete(int id, [FromQuery] bool deleteFile = false, [FromQuery] bool deleteGenerated = false, CancellationToken ct = default)
    {
        var text = await db.TextDocuments.Include(item => item.Files).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (text == null) return NotFound();

        var groupItems = await db.GroupItems.Where(item => item.HostType == "text" && item.HostId == id).ToListAsync(ct);
        db.GroupItems.RemoveRange(groupItems);
        await DeleteTextArtifactsAsync(text, new HashSet<int> { id }, new HashSet<string>(StringComparer.OrdinalIgnoreCase), deleteFile, deleteGenerated, ct);
        await customFields.DeleteValuesForEntityAsync(CustomFieldEntityTypes.Text, id, ct);
        db.TextDocuments.Remove(text);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task DeleteTextArtifactsAsync(TextDocument text, IReadOnlySet<int> idsToDelete, HashSet<string> deletedPaths, bool deleteFiles, bool deleteGenerated, CancellationToken ct)
    {
        if (deleteFiles)
        {
            foreach (var file in text.Files)
            {
                var path = file.Path;
                if (string.IsNullOrWhiteSpace(path) || !deletedPaths.Add(path))
                    continue;

                var referencedByKeptText = await db.Set<TextFile>()
                    .AnyAsync(textFile => textFile.Path == path && textFile.TextDocumentId.HasValue && !idsToDelete.Contains(textFile.TextDocumentId.Value), ct);
                if (!referencedByKeptText && System.IO.File.Exists(path))
                    System.IO.File.Delete(path);
            }
        }

        if (text.Files.Count > 0)
            db.TextFiles.RemoveRange(text.Files);

        if (!string.IsNullOrWhiteSpace(text.ImageBlobId))
        {
            if (deleteGenerated)
                await thumbnailService.DeleteBlobGeneratedFilesAsync(text.ImageBlobId, ct);
            await blobService.DeleteBlobAsync(text.ImageBlobId, ct);
        }
    }

    private async Task<List<TextDocument>> LoadListItemsAsync(IReadOnlyList<int> pagedIds, CancellationToken ct)
    {
        if (pagedIds.Count == 0)
            return [];

        var items = await db.TextDocuments.AsNoTracking()
            .Include(text => text.Studio)
            .Include(text => text.Urls)
            .Include(text => text.Files)
            .Include(text => text.TextTags).ThenInclude(link => link.Tag)
            .Include(text => text.TextPerformers).ThenInclude(link => link.Performer)
            .Where(text => pagedIds.Contains(text.Id))
            .AsSplitQuery()
            .ToListAsync(ct);

        var orderMap = pagedIds.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index);
        return items.OrderBy(text => orderMap.GetValueOrDefault(text.Id, int.MaxValue)).ToList();
    }

    private static readonly HashSet<string> MultiSortKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "updatedAt", "updated_at", "createdAt", "created_at", "date", "words", "pages",
        "file_size", "file_mod_time", "file_count", "path", "tag_count", "performer_count", "title", "rating",
        "read_count", "like_counter", "read_duration", "last_read_at",
    };

    private IQueryable<TextDocument> ApplySort(
        IQueryable<TextDocument> query,
        string? sort,
        bool descending,
        int? seed = null,
        IEnumerable<SortClause>? sorts = null)
    {
        var clauses = CompoundSortOrdering.Normalize(sorts, MultiSortKeys);
        if (clauses.Count > 1)
            return ApplyMultiSort(query, clauses);

        if (FilterHelpers.TryParseCustomFieldSort(sort, out _, out _))
            return query.ApplyCustomFieldSort(db, CustomFieldEntityTypes.Text, sort, descending);

        return (sort ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "random" => Cove.Data.Repositories.SeededRandomOrdering.OrderBy(query, seed, text => text.Id, descending),
            "title" => descending ? query.OrderByDescending(text => text.Title).ThenByDescending(text => text.Id) : query.OrderBy(text => text.Title).ThenBy(text => text.Id),
            "date" => descending ? query.OrderByDescending(text => text.Date).ThenByDescending(text => text.Id) : query.OrderBy(text => text.Date).ThenBy(text => text.Id),
            "words" => descending ? query.OrderByDescending(text => text.MaxWordCount).ThenByDescending(text => text.Id) : query.OrderBy(text => text.MaxWordCount).ThenBy(text => text.Id),
            "pages" => descending ? query.OrderByDescending(text => text.MaxPageCount).ThenByDescending(text => text.Id) : query.OrderBy(text => text.MaxPageCount).ThenBy(text => text.Id),
            "rating" => EngagementQueryHelpers.ApplyRatingSort(db, query, EngagementQueryHelpers.CurrentUserId(db), RatingHostType.Text, descending),
            "play_count" or "read_count" => EngagementQueryHelpers.ApplyAffinityIntSort(db, query, EngagementQueryHelpers.CurrentUserId(db), AffinityHostType.Text, nameof(UserEntityAffinity.ViewCount), descending),
            "like_counter" => EngagementQueryHelpers.ApplyAffinityIntSort(db, query, EngagementQueryHelpers.CurrentUserId(db), AffinityHostType.Text, nameof(UserEntityAffinity.LikeCount), descending),
            "play_duration" or "read_duration" => EngagementQueryHelpers.ApplyAffinityDoubleSort(db, query, EngagementQueryHelpers.CurrentUserId(db), AffinityHostType.Text, nameof(UserEntityAffinity.TotalConsumedSec), descending),
            "last_read_at" or "last_played_at" => EngagementQueryHelpers.ApplyAffinityTimestampSort(db, query, EngagementQueryHelpers.CurrentUserId(db), AffinityHostType.Text, nameof(UserEntityAffinity.LastConsumedAt), descending),
            "file_size" => descending ? query.OrderByDescending(text => text.MaxFileSize).ThenByDescending(text => text.Id) : query.OrderBy(text => text.MaxFileSize).ThenBy(text => text.Id),
            "file_mod_time" => descending ? query.OrderByDescending(text => text.MaxFileModTime).ThenByDescending(text => text.Id) : query.OrderBy(text => text.MaxFileModTime).ThenBy(text => text.Id),
            "file_count" => descending ? query.OrderByDescending(text => text.FileCount).ThenByDescending(text => text.Id) : query.OrderBy(text => text.FileCount).ThenBy(text => text.Id),
            "path" => descending ? query.OrderByDescending(text => text.MaxPath).ThenByDescending(text => text.Id) : query.OrderBy(text => text.MinPath).ThenBy(text => text.Id),
            "tag_count" => descending ? query.OrderByDescending(text => text.TextTags.Count).ThenByDescending(text => text.Id) : query.OrderBy(text => text.TextTags.Count).ThenBy(text => text.Id),
            "performer_count" => descending ? query.OrderByDescending(text => text.TextPerformers.Count).ThenByDescending(text => text.Id) : query.OrderBy(text => text.TextPerformers.Count).ThenBy(text => text.Id),
            "updatedat" or "updated_at" => descending ? query.OrderByDescending(text => text.UpdatedAt).ThenByDescending(text => text.Id) : query.OrderBy(text => text.UpdatedAt).ThenBy(text => text.Id),
            "createdat" => descending ? query.OrderByDescending(text => text.CreatedAt).ThenByDescending(text => text.Id) : query.OrderBy(text => text.CreatedAt).ThenBy(text => text.Id),
            "created_at" => descending ? query.OrderByDescending(text => text.CreatedAt).ThenByDescending(text => text.Id) : query.OrderBy(text => text.CreatedAt).ThenBy(text => text.Id),
            _ => descending ? query.OrderByDescending(text => text.UpdatedAt).ThenByDescending(text => text.Id) : query.OrderBy(text => text.UpdatedAt).ThenBy(text => text.Id),
        };
    }

    private IQueryable<TextDocument> ApplyMultiSort(IQueryable<TextDocument> query, IReadOnlyList<SortClause> clauses)
    {
        IOrderedQueryable<TextDocument>? ordered = null;
        var userId = EngagementQueryHelpers.CurrentUserId(db);
        foreach (var clause in clauses)
        {
            var desc = clause.Direction == Cove.Core.Enums.SortDirection.Desc;
            switch (clause.Key.ToLowerInvariant())
            {
                case "title":
                    ordered = CompoundSortOrdering.Append(query, ordered, text => text.Title == null ? 1 : 0, false);
                    ordered = CompoundSortOrdering.Append(query, ordered, text => text.Title, desc);
                    break;
                case "rating":
                    ordered = EngagementQueryHelpers.AppendRatingSort(db, query, ordered, userId, RatingHostType.Text, desc);
                    break;
                case "read_count":
                    ordered = EngagementQueryHelpers.AppendAffinityIntSort(db, query, ordered, userId, AffinityHostType.Text, nameof(UserEntityAffinity.ViewCount), desc);
                    break;
                case "like_counter":
                    ordered = EngagementQueryHelpers.AppendAffinityIntSort(db, query, ordered, userId, AffinityHostType.Text, nameof(UserEntityAffinity.LikeCount), desc);
                    break;
                case "read_duration":
                    ordered = EngagementQueryHelpers.AppendAffinityDoubleSort(db, query, ordered, userId, AffinityHostType.Text, nameof(UserEntityAffinity.TotalConsumedSec), desc);
                    break;
                case "last_read_at":
                    ordered = EngagementQueryHelpers.AppendAffinityTimestampSort(db, query, ordered, userId, AffinityHostType.Text, nameof(UserEntityAffinity.LastConsumedAt), desc);
                    break;
                case "date":
                    ordered = CompoundSortOrdering.Append(query, ordered, text => text.Date == null ? 1 : 0, false);
                    ordered = CompoundSortOrdering.Append(query, ordered, text => text.Date, desc);
                    break;
                case "words": ordered = CompoundSortOrdering.Append(query, ordered, text => text.MaxWordCount, desc); break;
                case "pages": ordered = CompoundSortOrdering.Append(query, ordered, text => text.MaxPageCount, desc); break;
                case "file_size": ordered = CompoundSortOrdering.Append(query, ordered, text => text.MaxFileSize, desc); break;
                case "file_mod_time":
                    ordered = CompoundSortOrdering.Append(query, ordered, text => text.MaxFileModTime == null ? 1 : 0, false);
                    ordered = CompoundSortOrdering.Append(query, ordered, text => text.MaxFileModTime, desc);
                    break;
                case "file_count": ordered = CompoundSortOrdering.Append(query, ordered, text => text.FileCount, desc); break;
                case "path": ordered = CompoundSortOrdering.Append(query, ordered, text => desc ? text.MaxPath : text.MinPath, desc); break;
                case "tag_count": ordered = CompoundSortOrdering.Append(query, ordered, text => text.TextTags.Count, desc); break;
                case "performer_count": ordered = CompoundSortOrdering.Append(query, ordered, text => text.TextPerformers.Count, desc); break;
                case "createdat":
                case "created_at": ordered = CompoundSortOrdering.Append(query, ordered, text => text.CreatedAt, desc); break;
                case "updatedat":
                case "updated_at": ordered = CompoundSortOrdering.Append(query, ordered, text => text.UpdatedAt, desc); break;
            }
        }

        return CompoundSortOrdering.Finish(query, ordered, text => text.Id);
    }

    private IQueryable<TextDocument> ApplyFilter(IQueryable<TextDocument> query, TextDocumentFilter? filter, IReadOnlyList<int[]>? hierarchicalTagGroups = null, IReadOnlyList<int[]>? requiredTagGroups = null, IReadOnlyList<int[]>? hierarchicalStudioGroups = null, IReadOnlyList<int[]>? requiredStudioGroups = null)
    {
        if (filter == null)
            return query;

        query = EngagementQueryHelpers.ApplyRatingCriterion(db, query, EngagementQueryHelpers.CurrentUserId(db), RatingHostType.Text, filter.RatingCriterion);
        query = EngagementQueryHelpers.ApplyAffinityIntCriterion(db, query, EngagementQueryHelpers.CurrentUserId(db), AffinityHostType.Text, nameof(UserEntityAffinity.ViewCount), filter.PlayCountCriterion);
        query = EngagementQueryHelpers.ApplyAffinityIntCriterion(db, query, EngagementQueryHelpers.CurrentUserId(db), AffinityHostType.Text, nameof(UserEntityAffinity.LikeCount), filter.LikeCounterCriterion);
        query = EngagementQueryHelpers.ApplyAffinityDoubleAsIntCriterion(db, query, EngagementQueryHelpers.CurrentUserId(db), AffinityHostType.Text, nameof(UserEntityAffinity.TotalConsumedSec), filter.PlayDurationCriterion);
        query = EngagementQueryHelpers.ApplyAffinityTimestampCriterion(db, query, EngagementQueryHelpers.CurrentUserId(db), AffinityHostType.Text, nameof(UserEntityAffinity.LastConsumedAt), filter.LastReadAtCriterion);
        query = FilterHelpers.ApplyString(query, filter.TitleCriterion, text => text.Title);
        query = FilterHelpers.ApplyString(query, filter.CodeCriterion, text => text.Code);
        query = FilterHelpers.ApplyString(query, filter.DetailsCriterion, text => text.Details);
        query = FilterHelpers.ApplyString(query, filter.ContentCriterion, text => text.SearchText);
        query = FilterHelpers.ApplyFilePath(query, filter.PathCriterion, text => text.Files);
        query = ApplyTextFileStringCriterion(query, filter.FormatCriterion);
        query = FilterHelpers.ApplyString(query, filter.UrlCriterion, text => text.Urls.Select(url => url.Url).FirstOrDefault());
        query = FilterHelpers.ApplyBool(query, filter.OrganizedCriterion, text => text.Organized);
        query = FilterHelpers.ApplyBool(query, filter.HasCoverCriterion, text => text.ImageBlobId != null && text.ImageBlobId != string.Empty);
        query = FilterHelpers.ApplyDate(query, filter.DateCriterion, text => text.Date);
        query = FilterHelpers.ApplyInt(query, filter.WordCountCriterion, text => text.MaxWordCount ?? 0);
        query = FilterHelpers.ApplyInt(query, filter.PageCountCriterion, text => text.MaxPageCount ?? 0);
        query = FilterHelpers.ApplyLong(query, filter.FileSizeCriterion, text => text.MaxFileSize);
        query = FilterHelpers.ApplyNullableTimestamp(query, filter.FileModTimeCriterion, text => text.MaxFileModTime);
        query = FilterHelpers.ApplyInt(query, filter.FileCountCriterion, text => text.FileCount);
        query = FilterHelpers.ApplyInt(query, filter.TagCountCriterion, text => text.TextTags.Count);
        query = FilterHelpers.ApplyInt(query, filter.PerformerCountCriterion, text => text.TextPerformers.Count);
        query = FilterHelpers.ApplyMultiId(query, filter.TagsCriterion, text => text.TextTags.Select(link => link.TagId), hierarchicalTagGroups, requiredTagGroups);
        query = FilterHelpers.ApplyMultiId(query, filter.PerformersCriterion, text => text.TextPerformers.Select(link => link.PerformerId));
        query = ApplyPerformerOccurrenceTagCriterion(query, filter.PerformerTagsCriterion, GetIncludedPerformerIds(filter));
        query = FilterHelpers.ApplyStudioCriterion(query, filter.StudiosCriterion, text => text.StudioId, hierarchicalStudioGroups, requiredStudioGroups);
        query = FilterHelpers.ApplyMultiId(query, filter.GroupsCriterion, text => db.GroupItems
            .Where(item => item.HostType == "text" && item.HostId == text.Id && item.Kind == GroupItemKind.Text)
            .Select(item => item.GroupId));
        query = FilterHelpers.ApplyTimestamp(query, filter.CreatedAtCriterion, text => text.CreatedAt);
        query = FilterHelpers.ApplyTimestamp(query, filter.UpdatedAtCriterion, text => text.UpdatedAt);
        query = query.ApplyCustomFieldCriteria(db, CustomFieldEntityTypes.Text, filter.CustomFieldCriterion, filter.CustomFieldCriteria);

        return query;
    }

    private static IQueryable<TextDocument> ApplyTextFileStringCriterion(IQueryable<TextDocument> query, StringCriterion? criterion)
    {
        if (criterion == null)
            return query;

        var value = criterion.Value.Trim();
        var lowered = value.ToLowerInvariant();
        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(text => text.Files.Any(file => file.Format == value)),
            CriterionModifier.NotEquals => query.Where(text => !text.Files.Any(file => file.Format == value)),
            CriterionModifier.Includes => query.Where(text => text.Files.Any(file => file.Format != null && file.Format.ToLower().Contains(lowered))),
            CriterionModifier.Excludes => query.Where(text => !text.Files.Any(file => file.Format != null && file.Format.ToLower().Contains(lowered))),
            CriterionModifier.IsNull => query.Where(text => !text.Files.Any(file => file.Format != string.Empty)),
            CriterionModifier.NotNull => query.Where(text => text.Files.Any(file => file.Format != string.Empty)),
            _ => query,
        };
    }

    private static int[] GetIncludedPerformerIds(TextDocumentFilter filter)
    {
        var ids = new HashSet<int>();
        if (filter.PerformersCriterion?.Value is { Count: > 0 }
            && filter.PerformersCriterion.Modifier is CriterionModifier.Includes or CriterionModifier.IncludesAll)
            ids.UnionWith(filter.PerformersCriterion.Value.Where(id => id > 0));

        if (filter.PerformersCriterion?.RequiredIds is { Count: > 0 })
            ids.UnionWith(filter.PerformersCriterion.RequiredIds.Where(id => id > 0));

        return ids.ToArray();
    }

    private IQueryable<TextDocument> ApplyPerformerOccurrenceTagCriterion(IQueryable<TextDocument> query, MultiIdCriterion? criterion, IReadOnlyCollection<int> performerIds)
    {
        if (criterion == null)
            return query;

        var tagIds = criterion.Value.Where(tagId => tagId > 0).Distinct().ToArray();
        var excludedTagIds = criterion.Excludes?.Where(tagId => tagId > 0).Distinct().ToArray() ?? [];
        if (tagIds.Length == 0 && excludedTagIds.Length == 0)
            return query;

        var scopedApplications = db.TagApplications.AsNoTracking()
            .Where(application => application.HostType == AffinityHostType.Text
                && application.ContextType == "performer"
                && application.ContextId != null);

        if (performerIds.Count > 0)
        {
            var performerIdArray = performerIds.ToArray();
            scopedApplications = scopedApplications.Where(application => application.ContextId != null && performerIdArray.Contains(application.ContextId.Value));
        }

        if (tagIds.Length > 0)
        {
            query = criterion.Modifier switch
            {
                CriterionModifier.Excludes => query.Where(text => !scopedApplications.Any(application => application.HostId == text.Id && tagIds.Contains(application.TagId))),
                CriterionModifier.ExcludesAll => ApplyPerformerOccurrenceTagExcludesAll(query, scopedApplications, tagIds),
                CriterionModifier.IncludesAll => ApplyPerformerOccurrenceTagIncludesAll(query, scopedApplications, tagIds),
                _ => query.Where(text => scopedApplications.Any(application => application.HostId == text.Id && tagIds.Contains(application.TagId))),
            };
        }

        if (excludedTagIds.Length > 0)
        {
            query = query.Where(text => !scopedApplications.Any(application => application.HostId == text.Id && excludedTagIds.Contains(application.TagId)));
        }

        return query;
    }

    private static IQueryable<TextDocument> ApplyPerformerOccurrenceTagIncludesAll(IQueryable<TextDocument> query, IQueryable<TagApplication> applications, IReadOnlyCollection<int> tagIds)
    {
        foreach (var tagId in tagIds)
        {
            query = query.Where(text => applications.Any(application => application.HostId == text.Id && application.TagId == tagId));
        }

        return query;
    }

    private static IQueryable<TextDocument> ApplyPerformerOccurrenceTagExcludesAll(IQueryable<TextDocument> query, IQueryable<TagApplication> applications, IReadOnlyCollection<int> tagIds)
    {
        var matchingAll = query;
        foreach (var tagId in tagIds)
        {
            matchingAll = matchingAll.Where(text => applications.Any(application => application.HostId == text.Id && application.TagId == tagId));
        }

        return query.Where(text => !matchingAll.Select(match => match.Id).Contains(text.Id));
    }

    private async Task<TextDocumentDto> MapToDetailDtoAsync(TextDocument text, CancellationToken ct)
    {
        var groups = await GetGroupsAsync(text.Id, ct);
        var customFieldValues = await customFields.GetValuesAsync(CustomFieldEntityTypes.Text, text.Id, ct);
        var contextTagApplications = await GetContextTagApplicationsAsync(text.Id, ct);
        var fieldProvenance = fieldProvenanceService == null
            ? null
            : (await fieldProvenanceService.GetForHostAsync(AffinityHostType.Text, text.Id, ct)).ToList();
        return MapToDto(text, groups, customFieldValues, contextTagApplications, fieldProvenance);
    }

    private TextDocumentDto MapToDto(TextDocument text, List<GroupSummaryDto>? groups, Dictionary<string, object>? customFieldValues, List<TagApplicationDto>? contextTagApplications = null, List<FieldProvenanceDto>? fieldProvenance = null) => new(
        text.Id,
        text.Title,
        text.Code,
        text.Details,
        text.Organized,
        text.StudioId,
        text.Studio?.Name,
        text.Date?.ToString("yyyy-MM-dd"),
        text.Urls.Select(url => url.Url).ToList(),
        text.TextTags.Where(link => link.Tag != null).Select(link => TagDtoMapping.MapTagDto(link.Tag!)).ToList(),
        text.TextPerformers.Where(link => link.Performer != null).Select(link => link.Performer!).OrderForDisplay().Select(performer => new PerformerSummaryDto(
            performer.Id,
            performer.Name,
            performer.Disambiguation,
            performer.Gender?.ToString(),
            performer.Birthdate?.ToString("yyyy-MM-dd"),
            performer.Favorite,
            EntityImageUrls.PerformerOrNull(ControllerContext.HttpContext, performer))).ToList(),
        text.Files.OrderBy(file => file.Id).Select(file => new TextFileDto(
            file.Id,
            CanReadFiles ? file.Path : string.Empty,
            GetVisibleBasename(file.Path, file.Basename),
            file.Format,
            file.PageCount,
            file.WordCount,
            file.ExcerptText,
            file.Size)).ToList(),
        groups ?? [],
        customFieldValues,
        text.CreatedAt.ToString("o"),
        text.UpdatedAt.ToString("o"),
        text.FileCount,
        text.MaxWordCount,
        text.MaxPageCount,
        text.ImageBlobId != null ? EntityImageUrls.Text(ControllerContext.HttpContext, text.Id, text.UpdatedAt) : null,
        contextTagApplications,
        fieldProvenance);

    private async Task<List<TagApplicationDto>?> GetContextTagApplicationsAsync(int textId, CancellationToken ct)
    {
        var applications = await db.TagApplications.AsNoTracking()
            .Where(item => item.HostType == AffinityHostType.Text && item.HostId == textId)
            .Include(item => item.Tag).ThenInclude(tag => tag!.Aliases)
            .Include(item => item.Tag).ThenInclude(tag => tag!.TagGroup)
            .AsSplitQuery()
            .OrderBy(item => item.ContextType)
            .ThenBy(item => item.ContextId)
            .ThenBy(item => item.Tag!.Name)
            .ToListAsync(ct);

        return applications.Count == 0 ? null : applications.Select(TagApplicationsController.Map).ToList();
    }

    private async Task<TextDocument?> GetTextForDtoAsync(int id, CancellationToken ct)
        => await db.TextDocuments.AsNoTracking()
            .Include(item => item.Studio)
            .Include(item => item.Urls)
            .Include(item => item.Files)
            .Include(item => item.TextTags).ThenInclude(link => link.Tag)
            .Include(item => item.TextPerformers).ThenInclude(link => link.Performer)
            .FirstOrDefaultAsync(item => item.Id == id, ct);

    private async Task<List<GroupSummaryDto>> GetGroupsAsync(int textDocumentId, CancellationToken ct)
        => await db.GroupItems.AsNoTracking()
            .Where(item => item.HostType == "text" && item.HostId == textDocumentId && item.Kind == GroupItemKind.Text)
            .OrderBy(item => item.OrderIndex)
            .ThenBy(item => item.Id)
            .Select(item => new GroupSummaryDto(item.GroupId, item.Group!.Name, 0))
            .ToListAsync(ct);

    private async Task ReplaceWholeTextGroupItemsAsync(int textDocumentId, IReadOnlyCollection<VideoGroupInputDto> groups, string? textTitle, CancellationToken ct)
    {
        var existing = await db.GroupItems
            .Where(item => item.HostType == "text" && item.HostId == textDocumentId && item.Kind == GroupItemKind.Text)
            .ToListAsync(ct);

        if (existing.Count > 0)
        {
            db.GroupItems.RemoveRange(existing);
        }

        var normalizedGroups = groups
            .Where(group => group is { GroupId: > 0 })
            .GroupBy(group => group.GroupId)
            .Select((group, index) => new { GroupId = group.Key, OrderIndex = index })
            .ToList();

        if (normalizedGroups.Count == 0)
        {
            return;
        }

        db.GroupItems.AddRange(normalizedGroups.Select(group => new GroupItem
        {
            GroupId = group.GroupId,
            OrderIndex = group.OrderIndex,
            Kind = GroupItemKind.Text,
            HostType = "text",
            HostId = textDocumentId,
            Title = NormalizeOptionalText(textTitle),
        }));
    }

    private static DateOnly? ParseDate(string? value)
        => DateOnly.TryParse(value, out var parsed) ? parsed : null;

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GetVisibleBasename(string path, string basename)
        => string.IsNullOrWhiteSpace(basename) ? Path.GetFileName(path) : basename;
}
