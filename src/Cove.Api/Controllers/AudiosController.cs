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
using Cove.Data.Services;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[RequiresPermission(Permissions.AudiosRead)]
public class AudiosController(CoveContext db, CustomFieldService customFields, IScanService scanService, IThumbnailService thumbnailService, IBlobService blobService, ICurrentPrincipalAccessor? principalAccessor = null, IFieldProvenanceService? fieldProvenanceService = null, IUserEngagementService? engagementService = null, BulkDeletionJobService? bulkDeletionJobService = null, BulkEntityDeletionService? bulkEntityDeletionService = null, PhysicalFileDeletionRecoverySignal? physicalFileDeletionRecoverySignal = null) : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private static readonly HashSet<string> AffinityMultiSortKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "play_count", "like_counter", "play_duration", "last_played_at",
    };

    private bool CanReadFiles => principalAccessor?.Current?.Has(Permissions.FilesRead) == true;
    private IUserEngagementService EngagementService => engagementService ?? new UserEngagementService(db, principalAccessor ?? new CurrentPrincipalAccessor());

    [HttpGet]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<PaginatedResponse<AudioDto>>> Find(
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

        var query = db.Audios.AsNoTracking().AsQueryable();

        var audioBase = query;
        var audioText = FullTextSearchHelpers.Apply(db, audioBase, q,
            audio => audio.Title,
            audio => audio.Code,
            audio => audio.Details,
            audio => audio.FileSearchText,
            audio => audio.SearchText);
        query = FullTextSearchHelpers.ApplyRelationalMatches(audioText, audioBase, q,
            tagSelectors: [audio => audio.AudioTags.Where(at => at.Tag != null).Select(at => at.Tag!)],
            performerSelectors: [audio => audio.AudioPerformers.Where(ap => ap.Performer != null).Select(ap => ap.Performer!)]);
        query = FullTextSearchHelpers.ApplyFilePathMatch(query, audioBase, q, audio => audio.Files);

        query = ApplySort(query, sort, descending, seed, sortClauses);
        if (FullTextSearchHelpers.ShouldOrderByRelevance(db, q, sort))
            query = FullTextSearchHelpers.OrderByExactThenRelevance(db, query, q, audio => audio.Title);

        var totalCount = await query.CountAsync(ct);
        var pagedIds = await query
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .Select(audio => audio.Id)
            .ToListAsync(ct);

        var items = await LoadListItemsAsync(pagedIds, ct);

        var effectiveTags = await EffectiveTagDtoLoader.LoadAsync(db, AffinityHostType.Audio, items.Select(audio => audio.Id), ct);
        var dtos = items.Select(audio => MapToDto(audio, null, null, effectiveTags)).ToList();
        return Ok(new PaginatedResponse<AudioDto>(dtos, totalCount, page, perPage));
    }

    [HttpPost("find")]
    public async Task<ActionResult<PaginatedResponse<AudioDto>>> FindPost([FromBody] FilteredQueryRequest<AudioFilter> req, CancellationToken ct)
    {
        var findFilter = req.FindFilter ?? new FindFilter();
        var page = Math.Max(1, findFilter.Page);
        var perPage = Math.Clamp(findFilter.PerPage, 1, 250);
        var descending = findFilter.Direction == Cove.Core.Enums.SortDirection.Desc;
        var query = await AudioFilterQuery.BuildAsync(db, req.ObjectFilter, findFilter, ct: ct);
        query = ApplySort(query, findFilter.Sort, descending, findFilter.Seed, findFilter.Sorts);
        if (FullTextSearchHelpers.ShouldOrderByRelevance(db, findFilter.Q, findFilter.Sort))
            query = FullTextSearchHelpers.OrderByExactThenRelevance(db, query, findFilter.Q, audio => audio.Title);

        var totalCount = await query.CountAsync(ct);
        var pagedIds = await query
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .Select(audio => audio.Id)
            .ToListAsync(ct);

        var items = await LoadListItemsAsync(pagedIds, ct);

        var effectiveTags = await EffectiveTagDtoLoader.LoadAsync(db, AffinityHostType.Audio, items.Select(audio => audio.Id), ct);
        var dtos = items.Select(audio => MapToDto(audio, null, null, effectiveTags)).ToList();
        return Ok(new PaginatedResponse<AudioDto>(dtos, totalCount, page, perPage));
    }

    [HttpPost("aggregate")]
    public async Task<ActionResult<AudioAggregate>> Aggregate([FromBody] FilteredQueryRequest<AudioFilter> req, CancellationToken ct)
    {
        var findFilter = req.FindFilter ?? new FindFilter();
        var query = await AudioFilterQuery.BuildAsync(db, req.ObjectFilter, findFilter, ct: ct);
        if (req.Ids is { Count: > 0 }) query = query.Where(audio => req.Ids.Contains(audio.Id));

        return Ok(await query.GroupBy(_ => 1)
            .Select(group => new AudioAggregate(group.Count(), group.Sum(audio => audio.MaxDuration), group.Sum(audio => audio.MaxFileSize)))
            .SingleOrDefaultAsync(ct)
            ?? new AudioAggregate(0, 0, 0));
    }

    [HttpGet("{id:int}")]
    [AllowShareLinkAccess]
    public async Task<ActionResult<AudioDto>> GetById(int id, CancellationToken ct)
    {
        var audio = await db.Audios.AsNoTracking()
            .Include(item => item.Studio)
            .Include(item => item.Urls)
            .Include(item => item.Files)
            .Include(item => item.AudioTags).ThenInclude(link => link.Tag)
            .Include(item => item.AudioPerformers).ThenInclude(link => link.Performer)
            .Include(item => item.Tracks)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (audio == null)
        {
            return NotFound();
        }

        return Ok(await MapToDetailDtoAsync(audio, ct));
    }

    [HttpGet("{id:int}/stream")]
    [AllowShareLinkAccess]
    [RequiresPermission(Permissions.StreamRead)]
    public async Task<IActionResult> Stream(int id, CancellationToken ct)
    {
        var audio = await db.Audios.AsNoTracking()
            .Include(item => item.Files)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        var file = audio?.Files
            .OrderByDescending(item => item.Duration)
            .ThenBy(item => item.Id)
            .FirstOrDefault();
        if (file == null || string.IsNullOrWhiteSpace(file.Path) || !System.IO.File.Exists(file.Path))
        {
            return NotFound();
        }

        if (!ContentTypes.TryGetContentType(file.Path, out var contentType))
        {
            contentType = file.HasVideoTrack ? "video/mp4" : "audio/mpeg";
        }

        var stream = FileReadRace.TryOpenRead(
            file.Path,
            FileShare.ReadWrite | FileShare.Delete,
            pathWasObserved: true);
        if (stream == null) return NotFound();

        Response.Headers["Accept-Ranges"] = "bytes";
        return File(stream, contentType, enableRangeProcessing: true);
    }

    [HttpGet("{id:int}/history")]
    [RequiresPermission(Permissions.AudiosRead)]
    [RequiresEntityAccess(EntityKinds.Audio, Permissions.AudiosRead)]
    public async Task<ActionResult<VideoHistoryDto>> GetHistory(int id, CancellationToken ct)
    {
        var history = await EngagementService.GetHistoryAsync(AffinityHostType.Audio, id, ct);
        return history is null ? NotFound() : Ok(history);
    }

    [HttpPost("{id:int}/like")]
    [RequiresPermission(Permissions.AudiosWrite)]
    [RequiresEntityAccess(EntityKinds.Audio, Permissions.AudiosWrite)]
    public async Task<ActionResult<int>> IncrementLike(int id, CancellationToken ct)
    {
        var snapshot = await EngagementService.IncrementLikeAsync(AffinityHostType.Audio, id, ct);
        return snapshot == null ? NotFound() : Ok(snapshot.LikeCount);
    }

    [HttpPost("{id:int}/like/historical")]
    [RequiresPermission(Permissions.AudiosWrite)]
    [RequiresEntityAccess(EntityKinds.Audio, Permissions.AudiosWrite)]
    public async Task<ActionResult<int>> AddHistoricalLike(int id, HistoricalLikeDto request, CancellationToken ct)
    {
        var at = request.At.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(request.At, DateTimeKind.Utc) : request.At.ToUniversalTime();
        if (at > DateTime.UtcNow) return BadRequest("Historical likes must be dated in the past.");
        var snapshot = await EngagementService.AddHistoricalLikeAsync(AffinityHostType.Audio, id, at, ct);
        return snapshot == null ? NotFound() : Ok(snapshot.LikeCount);
    }

    [HttpDelete("{id:int}/like/history")]
    [RequiresPermission(Permissions.AudiosWrite)]
    [RequiresEntityAccess(EntityKinds.Audio, Permissions.AudiosWrite)]
    public async Task<IActionResult> DeleteLikeFromHistory(int id, [FromQuery] DateTime at, CancellationToken ct)
    {
        var snapshot = await EngagementService.DeleteLikeAtAsync(AffinityHostType.Audio, id, at, ct);
        if (snapshot == null) return NotFound();
        return NoContent();
    }

    [HttpDelete("{id:int}/like")]
    [RequiresPermission(Permissions.AudiosWrite)]
    [RequiresEntityAccess(EntityKinds.Audio, Permissions.AudiosWrite)]
    public async Task<ActionResult<int>> DecrementLike(int id, CancellationToken ct)
    {
        var snapshot = await EngagementService.DecrementLikeAsync(AffinityHostType.Audio, id, ct);
        return snapshot == null ? NotFound() : Ok(snapshot.LikeCount);
    }

    [HttpPost("{id:int}/like/reset")]
    [RequiresPermission(Permissions.AudiosWrite)]
    [RequiresEntityAccess(EntityKinds.Audio, Permissions.AudiosWrite)]
    public async Task<ActionResult<int>> ResetLike(int id, CancellationToken ct)
    {
        var snapshot = await EngagementService.ResetLikeAsync(AffinityHostType.Audio, id, ct);
        return snapshot == null ? NotFound() : Ok(snapshot.LikeCount);
    }

    [HttpPost("{id:int}/activity/reset")]
    [RequiresPermission(Permissions.AudiosWrite)]
    [RequiresEntityAccess(EntityKinds.Audio, Permissions.AudiosWrite)]
    public async Task<IActionResult> ResetActivity(int id, CancellationToken ct)
    {
        var snapshot = await EngagementService.ResetActivityAsync(AffinityHostType.Audio, id, ct);
        if (snapshot == null) return NotFound();
        return NoContent();
    }

    [HttpPost("{id:int}/rescan")]
    [RequiresPermission(Permissions.LibraryScan)]
    [RequiresEntityAccess(EntityKinds.Audio, Permissions.LibraryScan)]
    public async Task<IActionResult> Rescan(int id, CancellationToken ct)
    {
        var audio = await db.Audios.AsNoTracking()
            .Include(item => item.Files)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (audio == null) return NotFound();

        var filePath = audio.Files
            .Select(file => file.Path)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (string.IsNullOrWhiteSpace(filePath)) return BadRequest("Audio has no files");

        var jobId = scanService.StartScan(new ScanOperationOptions
        {
            Paths = [filePath],
            Rescan = true,
        });
        return Ok(new { jobId });
    }

    [HttpPost]
    [RequiresPermission(Permissions.AudiosWrite)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsRead, RouteValueName = null, ActionArgumentName = "dto",
        PropertyName = "GroupIds.GroupId", DeniedBehavior = EntityAccessDeniedBehavior.Forbidden)]
    public async Task<ActionResult<AudioDto>> Create([FromBody] AudioCreateDto dto, CancellationToken ct)
    {
        var tagIds = dto.TagIds?.Where(tagId => tagId > 0).Distinct().ToArray() ?? [];
        var performerIds = dto.PerformerIds?.Where(performerId => performerId > 0).Distinct().ToArray() ?? [];
        var date = PartialDate.Parse(dto.Date);
        var audio = new Audio
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
            Urls = dto.Urls?.Select(NormalizeOptionalText).Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => new AudioUrl { Url = url! }).ToList() ?? [],
            AudioTags = tagIds.Select(tagId => new AudioTag { TagId = tagId }).ToList(),
            AudioPerformers = performerIds.Select(performerId => new AudioPerformer { PerformerId = performerId }).ToList(),
        };

        db.Audios.Add(audio);
        await db.SaveChangesAsync(ct);

        if (dto.GroupIds != null)
        {
            await ReplaceWholeAudioGroupItemsAsync(audio.Id, dto.GroupIds, audio.Title, ct);
            await db.SaveChangesAsync(ct);
        }

        if (dto.CustomFields != null)
        {
            await customFields.SaveValuesAsync(CustomFieldEntityTypes.Audio, audio.Id, dto.CustomFields, ct);
        }

        var created = await GetAudioForDtoAsync(audio.Id, ct);
        if (created == null) return NotFound();
        return CreatedAtAction(nameof(GetById), new { id = audio.Id }, await MapToDetailDtoAsync(created, ct));
    }

    [HttpPost("from-file")]
    [RequiresPermission(Permissions.AudiosWrite)]
    public async Task<ActionResult<AudioDto>> CreateFromFile([FromBody] FileBackedCreateDto? dto, CancellationToken ct)
    {
        var filePath = dto?.FilePath?.Trim();
        if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
            return BadRequest(new { error = "A valid file path is required." });

        var audioId = await scanService.ImportDownloadedAudioAsync(filePath, audioId: null, ct);
        var audio = await db.Audios.AsNoTracking()
            .Include(item => item.Studio)
            .Include(item => item.Urls)
            .Include(item => item.Files)
            .Include(item => item.AudioTags).ThenInclude(link => link.Tag)
            .Include(item => item.AudioPerformers).ThenInclude(link => link.Performer)
            .Include(item => item.Tracks)
            .FirstOrDefaultAsync(item => item.Id == audioId, ct);
        if (audio == null) return NotFound();

        return CreatedAtAction(nameof(GetById), new { id = audioId }, await MapToDetailDtoAsync(audio, ct));
    }

    [HttpPut("{id:int}")]
    [RequiresPermission(Permissions.AudiosWrite)]
    [RequiresEntityAccess(EntityKinds.Audio, Permissions.AudiosWrite)]
    [RequiresEntityAccess(EntityKinds.Group, Permissions.GroupsRead, RouteValueName = null, ActionArgumentName = "dto",
        PropertyName = "GroupIds.GroupId", DeniedBehavior = EntityAccessDeniedBehavior.Forbidden)]
    public async Task<ActionResult<AudioDto>> Update(int id, [FromBody] AudioUpdateDto dto, CancellationToken ct)
    {
        var audio = await db.Audios
            .Include(item => item.Urls)
            .Include(item => item.AudioTags)
            .Include(item => item.AudioPerformers)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (audio == null)
        {
            return NotFound();
        }
        var clearFields = dto.ClearFields?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        if (dto.Title != null) audio.Title = NormalizeOptionalText(dto.Title);
        if (dto.Code != null) audio.Code = NormalizeOptionalText(dto.Code);
        if (dto.Details != null) audio.Details = NormalizeOptionalText(dto.Details);
        if (dto.Organized.HasValue) audio.Organized = dto.Organized.Value;
        if (dto.StudioId.HasValue) audio.StudioId = dto.StudioId;
        if (dto.Date != null) { var date = PartialDate.Parse(dto.Date); audio.Date = date.Value; audio.DatePrecision = date.Precision; }
        if (clearFields.Contains("studioId")) audio.StudioId = null;

        if (dto.Urls != null)
        {
            var urls = dto.Urls
                .Select(url => NormalizeOptionalText(url))
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(url => url!)
                .ToList();
            if (MetadataCollectionUpdater.ReplaceIfChanged(audio.Urls, urls, item => item.Url, url => new AudioUrl { AudioId = id, Url = url }, StringComparer.Ordinal))
                MetadataCollectionUpdater.Touch(audio);
        }

        if (dto.TagIds != null)
        {
            var tagIds = dto.TagIds.Where(tagId => tagId > 0).Distinct().ToArray();
            if (MetadataCollectionUpdater.ReplaceIfChanged(audio.AudioTags, tagIds, item => item.TagId, tagId => new AudioTag { AudioId = id, TagId = tagId }))
                MetadataCollectionUpdater.Touch(audio);
            audio.TagIds = tagIds;
        }

        if (dto.PerformerIds != null)
        {
            var performerIds = dto.PerformerIds.Where(performerId => performerId > 0).Distinct().ToArray();
            if (MetadataCollectionUpdater.ReplaceIfChanged(audio.AudioPerformers, performerIds, item => item.PerformerId, performerId => new AudioPerformer { AudioId = id, PerformerId = performerId }))
                MetadataCollectionUpdater.Touch(audio);
            audio.PerformerIds = performerIds;
        }

        if (dto.GroupIds != null)
        {
            if (await ReplaceWholeAudioGroupItemsAsync(id, dto.GroupIds, audio.Title, ct))
                MetadataCollectionUpdater.Touch(audio);
        }

        await db.SaveChangesAsync(ct);

        if (dto.CustomFields != null && await customFields.SaveValuesAsync(CustomFieldEntityTypes.Audio, id, dto.CustomFields, ct))
        {
            MetadataCollectionUpdater.Touch(audio);
            await db.SaveChangesAsync(ct);
        }

        var updated = await db.Audios.AsNoTracking()
            .Include(item => item.Studio)
            .Include(item => item.Urls)
            .Include(item => item.Files)
            .Include(item => item.AudioTags).ThenInclude(link => link.Tag)
            .Include(item => item.AudioPerformers).ThenInclude(link => link.Performer)
            .Include(item => item.Tracks)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (updated == null)
        {
            return NotFound();
        }

        return Ok(await MapToDetailDtoAsync(updated, ct));
    }

    [HttpPost("bulk")]
    [RequiresPermission(Permissions.AudiosWrite)]
    [RequiresEntityAccess(EntityKinds.Audio, Permissions.AudiosWrite, ActionArgumentName = "dto", PropertyName = "Ids")]
    public async Task<IActionResult> BulkUpdate([FromBody] BulkAudioUpdateDto dto, CancellationToken ct)
    {
        var items = await db.Audios
            .Include(item => item.AudioTags)
            .Include(item => item.AudioPerformers)
            .Where(item => dto.Ids.Contains(item.Id))
            .ToListAsync(ct);
        var clearFields = dto.ClearFields?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        foreach (var audio in items)
        {
            if (clearFields.Contains("studioId")) audio.StudioId = null;
            if (clearFields.Contains("date")) audio.Date = null;
            if (clearFields.Contains("code")) audio.Code = null;
            if (clearFields.Contains("details")) audio.Details = null;
            if (dto.Organized.HasValue) audio.Organized = dto.Organized.Value;
            if (dto.StudioId.HasValue) audio.StudioId = dto.StudioId;
            if (dto.Date != null) { var date = PartialDate.Parse(dto.Date); audio.Date = date.Value; audio.DatePrecision = date.Precision; }
            if (dto.Code != null) audio.Code = NormalizeOptionalText(dto.Code);
            if (dto.Details != null) audio.Details = NormalizeOptionalText(dto.Details);

            if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Set)
            {
                audio.AudioTags.Clear();
                audio.AudioTags = dto.TagIds.Where(tagId => tagId > 0).Distinct().Select(tagId => new AudioTag { AudioId = audio.Id, TagId = tagId }).ToList();
            }
            else if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Add)
            {
                var existing = audio.AudioTags.Select(link => link.TagId).ToHashSet();
                foreach (var tagId in dto.TagIds.Where(tagId => tagId > 0).Distinct().Where(tagId => !existing.Contains(tagId)))
                    audio.AudioTags.Add(new AudioTag { AudioId = audio.Id, TagId = tagId });
            }
            else if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Remove)
            {
                audio.AudioTags = audio.AudioTags.Where(link => !dto.TagIds.Contains(link.TagId)).ToList();
            }

            if (dto.PerformerIds != null && dto.PerformerMode == BulkUpdateMode.Set)
            {
                audio.AudioPerformers.Clear();
                audio.AudioPerformers = dto.PerformerIds.Where(performerId => performerId > 0).Distinct().Select(performerId => new AudioPerformer { AudioId = audio.Id, PerformerId = performerId }).ToList();
            }
            else if (dto.PerformerIds != null && dto.PerformerMode == BulkUpdateMode.Add)
            {
                var existing = audio.AudioPerformers.Select(link => link.PerformerId).ToHashSet();
                foreach (var performerId in dto.PerformerIds.Where(performerId => performerId > 0).Distinct().Where(performerId => !existing.Contains(performerId)))
                    audio.AudioPerformers.Add(new AudioPerformer { AudioId = audio.Id, PerformerId = performerId });
            }
            else if (dto.PerformerIds != null && dto.PerformerMode == BulkUpdateMode.Remove)
            {
                audio.AudioPerformers = audio.AudioPerformers.Where(link => !dto.PerformerIds.Contains(link.PerformerId)).ToList();
            }

            if (dto.TagIds != null) audio.TagIds = audio.AudioTags.Select(link => link.TagId).Distinct().ToArray();
            if (dto.PerformerIds != null) audio.PerformerIds = audio.AudioPerformers.Select(link => link.PerformerId).Distinct().ToArray();
        }

        await db.SaveChangesAsync(ct);
        return Ok(new BulkUpdateResult(items.Select(audio => audio.Id).ToList()));
    }

    [HttpDelete("bulk")]
    [RequiresPermission(Permissions.AudiosDelete)]
    [RequiresPermissionWhenTrue(Permissions.FilesDelete, ActionArgumentName = "dto", PropertyName = "DeleteFiles")]
    [RequiresEntityAccess(EntityKinds.Audio, Permissions.AudiosDelete, ActionArgumentName = "dto", PropertyName = "Ids")]
    public IActionResult BulkDelete([FromBody] BatchDeleteDto dto, CancellationToken ct)
    {
        if (dto.DeleteFiles && principalAccessor?.Current?.Has(Permissions.FilesDelete) != true)
            return Forbid();

        var ids = dto.Ids.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
            return BadRequest("Select at least one audio item to delete.");

        var queued = bulkDeletionJobService!.Start(
            principalAccessor?.Current,
            BulkDeletionEntityKind.Audio,
            ids,
            dto.DeleteFiles,
            dto.DeleteGenerated);
        return Accepted(queued);
    }

    [HttpDelete("{id:int}")]
    [RequiresPermission(Permissions.AudiosDelete)]
    [RequiresPermissionWhenTrue(Permissions.FilesDelete, ActionArgumentName = "deleteFile")]
    [RequiresEntityAccess(EntityKinds.Audio, Permissions.AudiosDelete)]
    public async Task<IActionResult> Delete(int id, [FromQuery] bool deleteFile = false, [FromQuery] bool deleteGenerated = false, CancellationToken ct = default)
    {
        if (deleteFile && principalAccessor?.Current?.Has(Permissions.FilesDelete) != true)
            return Forbid();

        if (bulkEntityDeletionService is not null)
        {
            var executionContext = new BulkDeletionExecutionContext();
            if (!await bulkEntityDeletionService.DeleteAsync(
                    BulkDeletionEntityKind.Audio,
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

        var audio = await db.Audios.Include(item => item.Files).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (audio == null) return NotFound();

        var groupItems = await db.GroupItems.Where(item => item.HostType == "audio" && item.HostId == id).ToListAsync(ct);
        db.GroupItems.RemoveRange(groupItems);
        try
        {
            await DeleteAudioArtifactsAsync(audio, new HashSet<int> { id }, new HashSet<string>(StringComparer.OrdinalIgnoreCase), deleteFile, deleteGenerated, ct);
        }
        catch (AudioFileDeleteException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        await customFields.DeleteValuesForEntityAsync(CustomFieldEntityTypes.Audio, id, ct);
        db.Audios.Remove(audio);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task DeleteAudioArtifactsAsync(Audio audio, IReadOnlySet<int> idsToDelete, HashSet<string> deletedPaths, bool deleteFiles, bool deleteGenerated, CancellationToken ct)
    {
        if (deleteFiles)
        {
            foreach (var file in audio.Files)
            {
                var path = file.Path;
                if (string.IsNullOrWhiteSpace(path) || !deletedPaths.Add(path))
                    continue;

                var referencedByKeptAudio = await db.Set<AudioFile>()
                    .AnyAsync(audioFile => audioFile.Path == path && audioFile.AudioId.HasValue && !idsToDelete.Contains(audioFile.AudioId.Value), ct);
                if (!referencedByKeptAudio && System.IO.File.Exists(path))
                    DeleteAudioFile(path);
            }
        }

        if (audio.Files.Count > 0)
            db.AudioFiles.RemoveRange(audio.Files);

        if (!string.IsNullOrWhiteSpace(audio.ImageBlobId))
        {
            if (deleteGenerated)
                await thumbnailService.DeleteBlobGeneratedFilesAsync(audio.ImageBlobId, ct);
            await blobService.DeleteBlobAsync(audio.ImageBlobId, ct);
        }
    }

    private static void DeleteAudioFile(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AudioFileDeleteException($"Could not delete audio file from disk because it is currently locked or unavailable: {path}", ex);
        }
    }

    private sealed class AudioFileDeleteException(string message, Exception innerException) : Exception(message, innerException);

    private async Task<List<Audio>> LoadListItemsAsync(IReadOnlyList<int> pagedIds, CancellationToken ct)
    {
        if (pagedIds.Count == 0)
            return [];

        var items = await db.Audios.AsNoTracking()
            .Include(audio => audio.Studio)
            .Include(audio => audio.Urls)
            .Include(audio => audio.Files)
            .Include(audio => audio.AudioTags).ThenInclude(link => link.Tag)
            .Include(audio => audio.AudioPerformers).ThenInclude(link => link.Performer)
            .Include(audio => audio.Tracks)
            .Where(audio => pagedIds.Contains(audio.Id))
            .AsSplitQuery()
            .ToListAsync(ct);

        var orderMap = pagedIds.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index);
        return items.OrderBy(audio => orderMap.GetValueOrDefault(audio.Id, int.MaxValue)).ToList();
    }

    private IQueryable<Audio> ApplySort(
        IQueryable<Audio> query,
        string? sort,
        bool descending,
        int? seed = null,
        IEnumerable<SortClause>? sorts = null)
    {
        var multiSortRegistry = CreateMultiSortRegistry();
        var clauses = multiSortRegistry.Normalize(sorts);
        if (clauses.Count > 1)
            return ApplyMultiSort(query, clauses, multiSortRegistry);

        if (FilterHelpers.TryParseCustomFieldSort(sort, out _, out _))
            return query.ApplyCustomFieldSort(db, CustomFieldEntityTypes.Audio, sort, descending);

        return (sort ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "random" => Cove.Data.Repositories.SeededRandomOrdering.OrderBy(query, seed, audio => audio.Id, descending),
            "title" => descending ? query.OrderByDescending(audio => audio.Title).ThenByDescending(audio => audio.Id) : query.OrderBy(audio => audio.Title).ThenBy(audio => audio.Id),
            "date" => descending ? query.OrderByDescending(audio => audio.Date).ThenByDescending(audio => audio.Id) : query.OrderBy(audio => audio.Date).ThenBy(audio => audio.Id),
            "duration" => descending ? query.OrderByDescending(audio => audio.MaxDuration).ThenByDescending(audio => audio.Id) : query.OrderBy(audio => audio.MaxDuration).ThenBy(audio => audio.Id),
            "rating" => EngagementQueryHelpers.ApplyRatingSort(db, query, EngagementQueryHelpers.CurrentUserId(db), RatingHostType.Audio, descending),
            "play_count" => EngagementQueryHelpers.ApplyAffinityIntSort(db, query, EngagementQueryHelpers.CurrentUserId(db), AffinityHostType.Audio, nameof(UserEntityAffinity.ViewCount), descending),
            "like_counter" => EngagementQueryHelpers.ApplyAffinityIntSort(db, query, EngagementQueryHelpers.CurrentUserId(db), AffinityHostType.Audio, nameof(UserEntityAffinity.LikeCount), descending),
            "play_duration" => EngagementQueryHelpers.ApplyAffinityDoubleSort(db, query, EngagementQueryHelpers.CurrentUserId(db), AffinityHostType.Audio, nameof(UserEntityAffinity.TotalConsumedSec), descending),
            "last_played_at" => EngagementQueryHelpers.ApplyAffinityTimestampSort(db, query, EngagementQueryHelpers.CurrentUserId(db), AffinityHostType.Audio, nameof(UserEntityAffinity.LastConsumedAt), descending),
            "file_size" => descending ? query.OrderByDescending(audio => audio.MaxFileSize).ThenByDescending(audio => audio.Id) : query.OrderBy(audio => audio.MaxFileSize).ThenBy(audio => audio.Id),
            "file_mod_time" => descending ? query.OrderByDescending(audio => audio.MaxFileModTime).ThenByDescending(audio => audio.Id) : query.OrderBy(audio => audio.MaxFileModTime).ThenBy(audio => audio.Id),
            "file_count" => descending ? query.OrderByDescending(audio => audio.FileCount).ThenByDescending(audio => audio.Id) : query.OrderBy(audio => audio.FileCount).ThenBy(audio => audio.Id),
            "path" => descending ? query.OrderByDescending(audio => audio.MaxPath).ThenByDescending(audio => audio.Id) : query.OrderBy(audio => audio.MinPath).ThenBy(audio => audio.Id),
            "bitrate" or "bit_rate" => descending ? query.OrderByDescending(audio => audio.MaxBitRate).ThenByDescending(audio => audio.Id) : query.OrderBy(audio => audio.MaxBitRate).ThenBy(audio => audio.Id),
            "has_video" or "has_video_files" => descending ? query.OrderByDescending(audio => audio.HasVideoFiles).ThenByDescending(audio => audio.Id) : query.OrderBy(audio => audio.HasVideoFiles).ThenBy(audio => audio.Id),
            "track_count" => descending ? query.OrderByDescending(audio => audio.Tracks.Count).ThenByDescending(audio => audio.Id) : query.OrderBy(audio => audio.Tracks.Count).ThenBy(audio => audio.Id),
            "tag_count" => descending ? query.OrderByDescending(audio => audio.AudioTags.Count).ThenByDescending(audio => audio.Id) : query.OrderBy(audio => audio.AudioTags.Count).ThenBy(audio => audio.Id),
            "performer_count" => descending ? query.OrderByDescending(audio => audio.AudioPerformers.Count).ThenByDescending(audio => audio.Id) : query.OrderBy(audio => audio.AudioPerformers.Count).ThenBy(audio => audio.Id),
            "updatedat" or "updated_at" => descending ? query.OrderByDescending(audio => audio.UpdatedAt).ThenByDescending(audio => audio.Id) : query.OrderBy(audio => audio.UpdatedAt).ThenBy(audio => audio.Id),
            "createdat" => descending ? query.OrderByDescending(audio => audio.CreatedAt).ThenByDescending(audio => audio.Id) : query.OrderBy(audio => audio.CreatedAt).ThenBy(audio => audio.Id),
            "created_at" => descending ? query.OrderByDescending(audio => audio.CreatedAt).ThenByDescending(audio => audio.Id) : query.OrderBy(audio => audio.CreatedAt).ThenBy(audio => audio.Id),
            _ => descending ? query.OrderByDescending(audio => audio.UpdatedAt).ThenByDescending(audio => audio.Id) : query.OrderBy(audio => audio.UpdatedAt).ThenBy(audio => audio.Id),
        };
    }

    private static CompoundSortRegistry<Audio> CreateMultiSortRegistry()
        => new(new Dictionary<string, Action<CompoundSortQuery<Audio>, bool>>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = (compound, desc) =>
            {
                compound.Append(audio => audio.Title == null ? 1 : 0, false);
                compound.Append(audio => audio.Title, desc);
            },
            ["rating"] = (compound, desc) => compound.AppendRating(desc),
            ["play_count"] = (compound, desc) => compound.AppendAffinityInt(nameof(UserEntityAffinity.ViewCount), desc),
            ["like_counter"] = (compound, desc) => compound.AppendAffinityInt(nameof(UserEntityAffinity.LikeCount), desc),
            ["play_duration"] = (compound, desc) => compound.AppendAffinityDouble(nameof(UserEntityAffinity.TotalConsumedSec), desc),
            ["last_played_at"] = (compound, desc) => compound.AppendAffinityTimestamp(nameof(UserEntityAffinity.LastConsumedAt), desc),
            ["date"] = (compound, desc) =>
            {
                compound.Append(audio => audio.Date == null ? 1 : 0, false);
                compound.Append(audio => audio.Date, desc);
            },
            ["duration"] = (compound, desc) => compound.Append(audio => audio.MaxDuration, desc),
            ["file_size"] = (compound, desc) => compound.Append(audio => audio.MaxFileSize, desc),
            ["file_mod_time"] = (compound, desc) =>
            {
                compound.Append(audio => audio.MaxFileModTime == null ? 1 : 0, false);
                compound.Append(audio => audio.MaxFileModTime, desc);
            },
            ["file_count"] = (compound, desc) => compound.Append(audio => audio.FileCount, desc),
            ["path"] = (compound, desc) => compound.Append(audio => desc ? audio.MaxPath : audio.MinPath, desc),
            ["bitrate"] = (compound, desc) => compound.Append(audio => audio.MaxBitRate, desc),
            ["has_video_files"] = (compound, desc) => compound.Append(audio => audio.HasVideoFiles, desc),
            ["track_count"] = (compound, desc) => compound.Append(audio => audio.Tracks.Count, desc),
            ["tag_count"] = (compound, desc) => compound.Append(audio => audio.AudioTags.Count, desc),
            ["performer_count"] = (compound, desc) => compound.Append(audio => audio.AudioPerformers.Count, desc),
            ["createdAt"] = (compound, desc) => compound.Append(audio => audio.CreatedAt, desc),
            ["created_at"] = (compound, desc) => compound.Append(audio => audio.CreatedAt, desc),
            ["updatedAt"] = (compound, desc) => compound.Append(audio => audio.UpdatedAt, desc),
            ["updated_at"] = (compound, desc) => compound.Append(audio => audio.UpdatedAt, desc),
        });

    private IQueryable<Audio> ApplyMultiSort(IQueryable<Audio> query, IReadOnlyList<SortClause> clauses, CompoundSortRegistry<Audio> registry)
    {
        var userId = EngagementQueryHelpers.CurrentUserId(db);
        var compound = CompoundSortQuery<Audio>.Create(
            db, query, userId, AffinityHostType.Audio, RatingHostType.Audio,
            includeAffinity: clauses.Any(clause => AffinityMultiSortKeys.Contains(clause.Key)),
            includeRating: clauses.Any(clause => clause.Key.Equals("rating", StringComparison.OrdinalIgnoreCase)));
        registry.Apply(compound, clauses);

        return compound.Finish(audio => audio.Id);
    }

    private async Task<AudioDto> MapToDetailDtoAsync(Audio audio, CancellationToken ct)
    {
        var groups = await GetGroupsAsync(audio.Id, ct);
        var customFieldValues = await customFields.GetValuesAsync(CustomFieldEntityTypes.Audio, audio.Id, ct);
        var effectiveTags = await EffectiveTagDtoLoader.LoadAsync(db, AffinityHostType.Audio, [audio.Id], ct);
        var contextTagApplications = await GetContextTagApplicationsAsync(audio.Id, ct);
        var performerCounts = await PerformerSummaryCountsLoader.LoadAsync(db, audio.AudioPerformers.Select(link => link.PerformerId), ct, principalAccessor);
        var fieldProvenance = fieldProvenanceService == null
            ? null
            : (await fieldProvenanceService.GetForHostAsync(AffinityHostType.Audio, audio.Id, ct)).ToList();
        return MapToDto(audio, groups, customFieldValues, effectiveTags, contextTagApplications, fieldProvenance, performerCounts);
    }

    private AudioDto MapToDto(Audio audio, List<GroupSummaryDto>? groups, Dictionary<string, object>? customFieldValues, IReadOnlyDictionary<int, List<TagDto>>? effectiveTagsByAudioId = null, List<TagApplicationDto>? contextTagApplications = null, List<FieldProvenanceDto>? fieldProvenance = null, IReadOnlyDictionary<int, PerformerSummaryCounts>? performerCounts = null) => new(
        audio.Id,
        audio.Title,
        audio.Code,
        audio.Details,
        audio.Organized,
        audio.StudioId,
        audio.Studio?.Name,
        PartialDate.Format(audio.Date, audio.DatePrecision),
        audio.Urls.Select(url => url.Url).ToList(),
        GetEffectiveTags(audio, effectiveTagsByAudioId),
        audio.AudioPerformers.Where(link => link.Performer != null).Select(link => link.Performer!).OrderForDisplay().Select(performer => new PerformerSummaryDto(
            performer.Id,
            performer.Name,
            performer.Disambiguation,
            performer.Gender?.ToString(),
            PartialDate.Format(performer.Birthdate, performer.BirthdatePrecision),
            performer.Favorite,
            EntityImageUrls.PerformerOrNull(ControllerContext.HttpContext, performer),
            performerCounts?.GetValueOrDefault(performer.Id)?.VideoCount ?? 0,
            performerCounts?.GetValueOrDefault(performer.Id)?.ImageCount ?? 0,
            performerCounts?.GetValueOrDefault(performer.Id)?.GalleryCount ?? 0,
            performerCounts?.GetValueOrDefault(performer.Id)?.AudioCount ?? 0,
            performerCounts?.GetValueOrDefault(performer.Id)?.TextCount ?? 0,
            performer.Country,
            PartialDate.Format(performer.DeathDate, performer.DeathDatePrecision))).ToList(),
        audio.Tracks.OrderBy(track => track.OrderIndex).ThenBy(track => track.Id).Select(track => new AudioTrackDto(track.Id, track.OrderIndex, track.Title, track.StartSec, track.EndSec)).ToList(),
        audio.Files.OrderBy(file => file.Id).Select(file => new AudioFileDto(
            file.Id,
            CanReadFiles ? file.Path : string.Empty,
            GetVisibleBasename(file.Path, file.Basename),
            file.Format,
            file.Duration,
            file.AudioCodec,
            file.BitRate,
            file.SampleRate,
            file.Channels,
            file.Size,
            file.HasVideoTrack)).ToList(),
        groups ?? [],
        customFieldValues,
        audio.CreatedAt.ToString("o"),
        audio.UpdatedAt.ToString("o"),
        audio.FileCount,
        audio.MaxDuration,
        audio.HasVideoFiles,
        audio.ImageBlobId != null ? EntityImageUrls.Audio(ControllerContext.HttpContext, audio.Id, audio.UpdatedAt) : null,
        contextTagApplications,
        fieldProvenance);

    private async Task<List<TagApplicationDto>?> GetContextTagApplicationsAsync(int audioId, CancellationToken ct)
    {
        var applications = await db.TagApplications.AsNoTracking()
            .Where(item => item.HostType == AffinityHostType.Audio && item.HostId == audioId)
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

    private static List<TagDto> GetEffectiveTags(Audio audio, IReadOnlyDictionary<int, List<TagDto>>? effectiveTagsByAudioId)
        => effectiveTagsByAudioId != null && effectiveTagsByAudioId.TryGetValue(audio.Id, out var tags)
            ? tags
            : audio.AudioTags.Where(link => link.Tag != null).Select(link => TagDtoMapping.MapTagDto(link.Tag!)).OrderForDisplay().ToList();

    private async Task<Audio?> GetAudioForDtoAsync(int id, CancellationToken ct)
        => await db.Audios.AsNoTracking()
            .Include(item => item.Studio)
            .Include(item => item.Urls)
            .Include(item => item.Files)
            .Include(item => item.AudioTags).ThenInclude(link => link.Tag)
            .Include(item => item.AudioPerformers).ThenInclude(link => link.Performer)
            .Include(item => item.Tracks)
            .FirstOrDefaultAsync(item => item.Id == id, ct);

    private async Task<List<GroupSummaryDto>> GetGroupsAsync(int audioId, CancellationToken ct)
        => await db.GroupItems.AsNoTracking()
            .Where(item => item.HostType == "audio" && item.HostId == audioId && item.Kind == GroupItemKind.Audio)
            .OrderBy(item => item.OrderIndex)
            .ThenBy(item => item.Id)
            .Select(item => new GroupSummaryDto(item.GroupId, item.Group!.Name, 0))
            .ToListAsync(ct);

    private async Task<bool> ReplaceWholeAudioGroupItemsAsync(int audioId, IReadOnlyCollection<VideoGroupInputDto> groups, string? audioTitle, CancellationToken ct)
    {
        var existing = await db.GroupItems
            .Where(item => item.HostType == "audio" && item.HostId == audioId && item.Kind == GroupItemKind.Audio)
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
            Kind = GroupItemKind.Audio,
            HostType = "audio",
            HostId = audioId,
            Title = NormalizeOptionalText(audioTitle),
        }));
        return true;
    }

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GetVisibleBasename(string path, string basename)
        => string.IsNullOrWhiteSpace(basename) ? Path.GetFileName(path) : basename;
}
