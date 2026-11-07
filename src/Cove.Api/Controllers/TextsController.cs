using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Cove.Api.Http;
using Cove.Api.Helpers;
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
public class TextsController(CoveContext db, CustomFieldService customFields, TextExtractionService textExtractionService, IScanService scanService, IThumbnailService thumbnailService, IBlobService blobService, ICurrentPrincipalAccessor? principalAccessor = null, IFieldProvenanceService? fieldProvenanceService = null, IUserEngagementService? engagementService = null, BulkDeletionJobService? bulkDeletionJobService = null, BulkEntityDeletionService? bulkEntityDeletionService = null, PhysicalFileDeletionRecoverySignal? physicalFileDeletionRecoverySignal = null, ITagProvenanceService? tagProvenanceService = null, ITextDocumentRepository? textRepository = null) : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private bool CanReadFiles => principalAccessor?.Current?.Has(Permissions.FilesRead) == true;
    private IUserEngagementService EngagementService => engagementService ?? new Cove.Data.Services.UserEngagementService(db, principalAccessor ?? new CurrentPrincipalAccessor());
    private ITextDocumentRepository Texts => textRepository ?? new TextDocumentRepository(db);

    [HttpGet("{id:int}/history")]
    [RequiresEntityAccess(EntityKinds.Text, Permissions.TextsRead)]
    public async Task<ActionResult<VideoHistoryDto>> GetHistory(int id, CancellationToken ct)
    {
        var history = await EngagementService.GetHistoryAsync(AffinityHostType.Text, id, ct);
        return history is null ? NotFound() : Ok(history);
    }

    [HttpPost("{id:int}/like")]
    [RequiresPermission(Permissions.TextsRead)]
    [RequiresEntityAccess(EntityKinds.Text, Permissions.TextsRead)]
    public async Task<ActionResult<int>> IncrementLike(int id, CancellationToken ct)
    {
        var snapshot = await EngagementService.IncrementLikeAsync(AffinityHostType.Text, id, ct);
        return snapshot == null ? NotFound() : Ok(snapshot.LikeCount);
    }

    [HttpPost("{id:int}/like/historical")]
    [RequiresPermission(Permissions.TextsRead)]
    [RequiresEntityAccess(EntityKinds.Text, Permissions.TextsRead)]
    public async Task<ActionResult<int>> AddHistoricalLike(int id, HistoricalLikeDto request, CancellationToken ct)
    {
        var at = request.At.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(request.At, DateTimeKind.Utc) : request.At.ToUniversalTime();
        if (at > DateTime.UtcNow) return BadRequest("Historical likes must be dated in the past.");
        var snapshot = await EngagementService.AddHistoricalLikeAsync(AffinityHostType.Text, id, at, ct);
        return snapshot == null ? NotFound() : Ok(snapshot.LikeCount);
    }

    [HttpDelete("{id:int}/like/history")]
    [RequiresPermission(Permissions.TextsRead)]
    [RequiresEntityAccess(EntityKinds.Text, Permissions.TextsRead)]
    public async Task<IActionResult> DeleteLikeFromHistory(int id, [FromQuery] DateTime at, CancellationToken ct)
    {
        var snapshot = await EngagementService.DeleteLikeAtAsync(AffinityHostType.Text, id, at, ct);
        if (snapshot == null) return NotFound();
        return NoContent();
    }

    [HttpDelete("{id:int}/like")]
    [RequiresPermission(Permissions.TextsRead)]
    [RequiresEntityAccess(EntityKinds.Text, Permissions.TextsRead)]
    public async Task<ActionResult<int>> DecrementLike(int id, CancellationToken ct)
    {
        var snapshot = await EngagementService.DecrementLikeAsync(AffinityHostType.Text, id, ct);
        return snapshot == null ? NotFound() : Ok(snapshot.LikeCount);
    }

    [HttpPost("{id:int}/like/reset")]
    [RequiresPermission(Permissions.TextsRead)]
    [RequiresEntityAccess(EntityKinds.Text, Permissions.TextsRead)]
    public async Task<ActionResult<int>> ResetLike(int id, CancellationToken ct)
    {
        var snapshot = await EngagementService.ResetLikeAsync(AffinityHostType.Text, id, ct);
        return snapshot == null ? NotFound() : Ok(snapshot.LikeCount);
    }

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
        perPage = Math.Clamp(perPage, 1, ListPaging.MaxPerPage);
        var findFilter = new FindFilter
        {
            Q = q,
            Page = page,
            PerPage = perPage,
            Sort = sort,
            Direction = string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase) ? Cove.Core.Enums.SortDirection.Desc : Cove.Core.Enums.SortDirection.Asc,
            Sorts = SortClause.Parse(sorts),
            Seed = seed,
        };
        var (items, totalCount) = await Texts.FindAsync(null, findFilter, ct);

        var tagIds = await EffectiveTagDtoLoader.LoadIdsAsync(db, AffinityHostType.Text, items.Select(text => text.Id), ct);
        var favorites = await EngagementQueryHelpers.GetCatalogFavoritesAsync(db, ct);
        var dtos = items.Select(text => MapToDto(text, favorites, null, null, tagIdsByTextId: tagIds)).ToList();
        return Ok(new PaginatedResponse<TextDocumentDto>(dtos, totalCount, page, perPage));
    }

    [HttpPost("find")]
    public async Task<ActionResult<PaginatedResponse<TextDocumentDto>>> FindPost([FromBody] FilteredQueryRequest<TextDocumentFilter> req, CancellationToken ct)
    {
        if (!FilterExpressionQuery.TryValidate(req.FilterExpression, out var expressionError))
            return BadRequest(new { message = expressionError });
        var findFilter = req.FindFilter ?? new FindFilter();
        findFilter.Page = Math.Max(1, findFilter.Page);
        findFilter.PerPage = Math.Clamp(findFilter.PerPage, 1, ListPaging.MaxPerPage);
        var (items, totalCount) = await Texts.FindAsync(req.ObjectFilter, findFilter, ct, req.FilterExpression);

        var tagIds = await EffectiveTagDtoLoader.LoadIdsAsync(db, AffinityHostType.Text, items.Select(text => text.Id), ct);
        var favorites = await EngagementQueryHelpers.GetCatalogFavoritesAsync(db, ct);
        var dtos = items.Select(text => MapToDto(text, favorites, null, null, tagIdsByTextId: tagIds)).ToList();
        return Ok(new PaginatedResponse<TextDocumentDto>(dtos, totalCount, findFilter.Page, findFilter.PerPage));
    }

    [HttpPost("aggregate")]
    public async Task<ActionResult<TextAggregate>> Aggregate([FromBody] FilteredQueryRequest<TextDocumentFilter> req, CancellationToken ct)
    {
        if (!FilterExpressionQuery.TryValidate(req.FilterExpression, out var expressionError))
            return BadRequest(new { message = expressionError });
        return Ok(await Texts.AggregateAsync(req.ObjectFilter, req.FindFilter, req.Ids, ct, req.FilterExpression));
    }

    [HttpGet("{id:int}")]
    [AllowShareLinkAccess]
    public async Task<ActionResult<TextDocumentDto>> GetById(int id, CancellationToken ct)
    {
        var text = await Texts.GetByIdWithRelationsAsync(id, ct);
        if (text == null)
        {
            return NotFound();
        }

        return Ok(await MapToDetailDtoAsync(text, ct));
    }

    [HttpGet("{id:int}/content")]
    [AllowShareLinkAccess]
    public async Task<ActionResult<TextContentDto>> GetContent(int id, CancellationToken ct)
    {
        var text = await db.TextDocuments.AsNoTracking()
            .Include(item => item.Files)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        var file = text?.Files
            .OrderByDescending(item => item.WordCount)
            .ThenBy(item => item.Id)
            .FirstOrDefault();
        if (file == null || string.IsNullOrWhiteSpace(file.Path))
        {
            return NotFound();
        }

        if (!System.IO.File.Exists(file.Path)) return NotFound();

        try
        {
            var extracted = await textExtractionService.ExtractContentAsync(file.Path, ct);
            return Ok(new TextContentDto(extracted.Format, extracted.RenderMode, extracted.Content));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException ex) when (FileReadRace.IsWindowsDeletionRace(ex, file.Path))
        {
            return NotFound();
        }
    }

    [HttpGet("{id:int}/file")]
    [AllowShareLinkAccess]
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

        var stream = FileReadRace.TryOpenRead(file.Path, pathWasObserved: true);
        if (stream == null) return NotFound();

        return File(stream, contentType, enableRangeProcessing: true);
    }

    [HttpPost]
    [RequiresPermission(Permissions.TextsWrite)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsRead, RouteValueName = null, ActionArgumentName = "dto",
        PropertyName = "GroupIds.GroupId", DeniedBehavior = EntityAccessDeniedBehavior.Forbidden)]
    public async Task<ActionResult<TextDocumentDto>> Create([FromBody] TextDocumentCreateDto dto, CancellationToken ct)
    {
        var tagIds = dto.TagIds?.Where(tagId => tagId > 0).Distinct().ToArray() ?? [];
        var performerIds = dto.PerformerIds?.Where(performerId => performerId > 0).Distinct().ToArray() ?? [];
        var date = PartialDate.Parse(dto.Date);
        var text = new TextDocument
        {
            Title = NormalizeOptionalText(dto.Title),
            Code = NormalizeOptionalText(dto.Code),
            Details = NormalizeOptionalText(dto.Details),
            Organized = dto.Organized,
            StudioId = dto.StudioId,
            Date = date.Value,
            DatePrecision = date.Precision,
            TagIds = tagIds,
            PerformerIds = performerIds,
            Urls = dto.Urls?.Select(NormalizeOptionalText).Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => new TextUrl { Url = url! }).ToList() ?? [],
            TextTags = tagIds.Select(tagId => new TextTag { TagId = tagId }).ToList(),
            TextPerformers = performerIds.Select(performerId => new TextPerformer { PerformerId = performerId }).ToList(),
        };

        db.TextDocuments.Add(text);
        await db.SaveChangesAsync(ct);
        if (tagIds.Length > 0 && tagProvenanceService != null)
        {
            await tagProvenanceService.SyncTagSetAsync(AffinityHostType.Text, text.Id, [], tagIds, cancellationToken: ct);
            await db.SaveChangesAsync(ct);
        }

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
        var text = await Texts.GetByIdWithRelationsAsync(textDocumentId, ct);
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
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsRead, RouteValueName = null, ActionArgumentName = "dto",
        PropertyName = "GroupIds.GroupId", DeniedBehavior = EntityAccessDeniedBehavior.Forbidden)]
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
        if (dto.Date != null) { var date = PartialDate.Parse(dto.Date); text.Date = date.Value; text.DatePrecision = date.Precision; }
        if (clearFields.Contains("studioId")) text.StudioId = null;

        if (dto.Urls != null)
        {
            var urls = dto.Urls
                .Select(url => NormalizeOptionalText(url))
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(url => url!)
                .ToList();
            if (MetadataCollectionUpdater.ReplaceIfChanged(text.Urls, urls, item => item.Url, url => new TextUrl { TextDocumentId = id, Url = url }, StringComparer.Ordinal))
                MetadataCollectionUpdater.Touch(text);
        }

        if (dto.TagIds != null)
        {
            var previousTagIds = text.TextTags.Select(link => link.TagId).ToArray();
            var tagIds = dto.TagIds.Where(tagId => tagId > 0).Distinct().ToArray();
            if (MetadataCollectionUpdater.ReplaceIfChanged(text.TextTags, tagIds, item => item.TagId, tagId => new TextTag { TextDocumentId = id, TagId = tagId }))
                MetadataCollectionUpdater.Touch(text);
            text.TagIds = tagIds;
            // The text shows its links plus every tag a source recorded on it, so a tag taken off here drops
            // its records too, as the video edit form does; otherwise a scraped tag would stay on as a locked one.
            if (tagProvenanceService != null)
                await tagProvenanceService.SyncTagSetAsync(AffinityHostType.Text, id, previousTagIds, tagIds, cancellationToken: ct);
        }

        if (dto.PerformerIds != null)
        {
            var performerIds = dto.PerformerIds.Where(performerId => performerId > 0).Distinct().ToArray();
            if (MetadataCollectionUpdater.ReplaceIfChanged(text.TextPerformers, performerIds, item => item.PerformerId, performerId => new TextPerformer { TextDocumentId = id, PerformerId = performerId }))
                MetadataCollectionUpdater.Touch(text);
            text.PerformerIds = performerIds;
        }

        if (dto.GroupIds != null)
        {
            if (await ReplaceWholeTextGroupItemsAsync(id, dto.GroupIds, text.Title, ct))
                MetadataCollectionUpdater.Touch(text);
        }

        await db.SaveChangesAsync(ct);

        if (dto.CustomFields != null && await customFields.SaveValuesAsync(CustomFieldEntityTypes.Text, id, dto.CustomFields, ct))
        {
            MetadataCollectionUpdater.Touch(text);
            await db.SaveChangesAsync(ct);
        }

        var updated = await Texts.GetByIdWithRelationsAsync(id, ct);
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
            if (dto.Date != null) { var date = PartialDate.Parse(dto.Date); text.Date = date.Value; text.DatePrecision = date.Precision; }
            if (dto.Code != null) text.Code = NormalizeOptionalText(dto.Code);
            if (dto.Details != null) text.Details = NormalizeOptionalText(dto.Details);

            var previousTagIds = dto.TagIds != null ? text.TextTags.Select(link => link.TagId).ToArray() : [];
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
            if (dto.TagIds != null && tagProvenanceService != null)
                await tagProvenanceService.SyncTagSetAsync(AffinityHostType.Text, text.Id, previousTagIds, text.TagIds, cancellationToken: ct);
            if (dto.PerformerIds != null) text.PerformerIds = text.TextPerformers.Select(link => link.PerformerId).Distinct().ToArray();
        }

        await db.SaveChangesAsync(ct);
        return Ok(new BulkUpdateResult(items.Select(text => text.Id).ToList()));
    }

    [HttpDelete("bulk")]
    [RequiresPermission(Permissions.TextsDelete)]
    [RequiresPermissionWhenTrue(Permissions.FilesDelete, ActionArgumentName = "dto", PropertyName = "DeleteFiles")]
    [RequiresEntityAccess(EntityKinds.Text, Permissions.TextsDelete, ActionArgumentName = "dto", PropertyName = "Ids")]
    public IActionResult BulkDelete([FromBody] BatchDeleteDto dto, CancellationToken ct)
    {
        if (dto.DeleteFiles && principalAccessor?.Current?.Has(Permissions.FilesDelete) != true)
            return Forbid();

        var ids = dto.Ids.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
            return BadRequest("Select at least one text document to delete.");

        var queued = bulkDeletionJobService!.Start(
            principalAccessor?.Current,
            BulkDeletionEntityKind.Text,
            ids,
            dto.DeleteFiles,
            dto.DeleteGenerated);
        return Accepted(queued);
    }

    [HttpDelete("{id:int}")]
    [RequiresPermission(Permissions.TextsDelete)]
    [RequiresPermissionWhenTrue(Permissions.FilesDelete, ActionArgumentName = "deleteFile")]
    [RequiresEntityAccess(EntityKinds.Text, Permissions.TextsDelete)]
    public async Task<IActionResult> Delete(int id, [FromQuery] bool deleteFile = false, [FromQuery] bool deleteGenerated = false, CancellationToken ct = default)
    {
        if (deleteFile && principalAccessor?.Current?.Has(Permissions.FilesDelete) != true)
            return Forbid();

        if (bulkEntityDeletionService is not null)
        {
            var executionContext = new BulkDeletionExecutionContext();
            if (!await bulkEntityDeletionService.DeleteAsync(
                    BulkDeletionEntityKind.Text,
                    id,
                    executionContext,
                    deleteFile,
                    deleteGenerated,
                    ct,
                    publishEvent: false))
                return NotFound();
            if (deleteFile)
                physicalFileDeletionRecoverySignal?.Notify();
            return NoContent();
        }

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

    private async Task<TextDocumentDto> MapToDetailDtoAsync(TextDocument text, CancellationToken ct)
    {
        var groups = await GetGroupsAsync(text.Id, ct);
        var customFieldValues = await customFields.GetValuesAsync(CustomFieldEntityTypes.Text, text.Id, ct);
        var effectiveTags = await EffectiveTagDtoLoader.LoadAsync(db, AffinityHostType.Text, [text.Id], ct);
        var favorites = await EngagementQueryHelpers.GetCatalogFavoritesAsync(db, ct);
        var contextTagApplications = await GetContextTagApplicationsAsync(text.Id, ct);
        var performerCounts = await PerformerSummaryCountsLoader.LoadAsync(db, text.TextPerformers.Select(link => link.PerformerId), ct, principalAccessor);
        var fieldProvenance = fieldProvenanceService == null
            ? null
            : (await fieldProvenanceService.GetForHostAsync(AffinityHostType.Text, text.Id, ct)).ToList();
        return MapToDto(text, favorites, groups, customFieldValues, effectiveTags, contextTagApplications, fieldProvenance, performerCounts);
    }

    private TextDocumentDto MapToDto(TextDocument text, CatalogFavorites favorites, List<GroupSummaryDto>? groups, Dictionary<string, object>? customFieldValues, IReadOnlyDictionary<int, List<TagDto>>? effectiveTagsByTextId = null, List<TagApplicationDto>? contextTagApplications = null, List<FieldProvenanceDto>? fieldProvenance = null, IReadOnlyDictionary<int, PerformerSummaryCounts>? performerCounts = null, IReadOnlyDictionary<int, List<int>>? tagIdsByTextId = null) => new(
        text.Id,
        text.Title,
        text.Code,
        text.Details,
        text.Organized,
        text.StudioId,
        text.Studio?.Name,
        PartialDate.Format(text.Date, text.DatePrecision),
        text.Urls.Select(url => url.Url).ToList(),
        // A list entry carries tag ids instead; see TagIds.
        tagIdsByTextId != null ? [] : GetEffectiveTags(text, favorites, effectiveTagsByTextId),
        text.TextPerformers.Where(link => link.Performer != null).Select(link => link.Performer!).OrderForDisplay().Select(performer => new PerformerSummaryDto(
            performer.Id,
            performer.Name,
            performer.Disambiguation,
            performer.Gender?.ToString(),
            PartialDate.Format(performer.Birthdate, performer.BirthdatePrecision),
            favorites.IsPerformer(performer.Id),
            EntityImageUrls.PerformerOrNull(ControllerContext.HttpContext, performer),
            performerCounts?.GetValueOrDefault(performer.Id)?.VideoCount ?? 0,
            performerCounts?.GetValueOrDefault(performer.Id)?.ImageCount ?? 0,
            performerCounts?.GetValueOrDefault(performer.Id)?.GalleryCount ?? 0,
            performerCounts?.GetValueOrDefault(performer.Id)?.AudioCount ?? 0,
            performerCounts?.GetValueOrDefault(performer.Id)?.TextCount ?? 0,
            performer.Country,
            PartialDate.Format(performer.DeathDate, performer.DeathDatePrecision))).ToList(),
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
        fieldProvenance)
    {
        TagIds = tagIdsByTextId?.GetValueOrDefault(text.Id),
    };

    private static List<TagDto> GetEffectiveTags(TextDocument text, CatalogFavorites favorites, IReadOnlyDictionary<int, List<TagDto>>? effectiveTagsByTextId)
        => effectiveTagsByTextId != null && effectiveTagsByTextId.TryGetValue(text.Id, out var tags)
            ? tags
            : text.TextTags.Where(link => link.Tag != null).Select(link => TagDtoMapping.MapTagDto(link.Tag!, favorites)).OrderForDisplay().ToList();

    private async Task<List<TagApplicationDto>?> GetContextTagApplicationsAsync(int textId, CancellationToken ct)
    {
        var applications = await db.TagApplications.AsNoTracking()
            .Where(item => item.HostType == AffinityHostType.Text && item.HostId == textId)
            .Include(item => item.Tag).ThenInclude(tag => tag!.Aliases)
            .Include(item => item.Tag).ThenInclude(tag => tag!.TagGroup)
            .AsSplitQuery()
            .OrderBy(item => item.ContextType)
            .ThenBy(item => item.ContextId)
            .ThenBy(item => item.Tag!.TagGroupId.HasValue ? 0 : 1)
            .ThenBy(item => item.Tag!.TagGroup != null ? item.Tag.TagGroup.SortOrder : int.MaxValue)
            .ThenBy(item => NaturalSort.Key(item.Tag!.TagGroup != null ? item.Tag.TagGroup.Name : null))
            .ThenBy(item => NaturalSort.Key(item.Tag!.SortName ?? item.Tag.Name))
            .ThenBy(item => item.TagId)
            .ToListAsync(ct);

        var favorites = await EngagementQueryHelpers.GetCatalogFavoritesAsync(db, ct);
        return applications.Count == 0 ? null : applications.Select(application => TagApplicationsController.Map(application, favorites)).ToList();
    }

    private async Task<TextDocument?> GetTextForDtoAsync(int id, CancellationToken ct)
        => await Texts.GetByIdWithRelationsAsync(id, ct);

    private async Task<List<GroupSummaryDto>> GetGroupsAsync(int textDocumentId, CancellationToken ct)
        => await db.GroupItems.AsNoTracking()
            .Where(item => item.HostType == "text" && item.HostId == textDocumentId && item.Kind == GroupItemKind.Text)
            .OrderBy(item => item.OrderIndex)
            .ThenBy(item => item.Id)
            .Select(item => new GroupSummaryDto(item.GroupId, item.Group!.Name, 0))
            .ToListAsync(ct);

    private async Task<bool> ReplaceWholeTextGroupItemsAsync(int textDocumentId, IReadOnlyCollection<VideoGroupInputDto> groups, string? textTitle, CancellationToken ct)
    {
        var existing = await db.GroupItems
            .Where(item => item.HostType == "text" && item.HostId == textDocumentId && item.Kind == GroupItemKind.Text)
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
            Kind = GroupItemKind.Text,
            HostType = "text",
            HostId = textDocumentId,
            Title = NormalizeOptionalText(textTitle),
        }));
        return true;
    }

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GetVisibleBasename(string path, string basename)
        => string.IsNullOrWhiteSpace(basename) ? Path.GetFileName(path) : basename;
}
