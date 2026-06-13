using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Data.Services;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[RequiresPermission(Permissions.AudiosRead)]
public class AudiosController(CoveContext db, CustomFieldService customFields, IScanService scanService, IThumbnailService thumbnailService, IBlobService blobService, ICurrentPrincipalAccessor? principalAccessor = null, IFieldProvenanceService? fieldProvenanceService = null, IUserEngagementService? engagementService = null) : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

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
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        perPage = Math.Clamp(perPage, 1, 250);
        var descending = string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase);

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

        query = ApplySort(query, sort, descending, seed);
        if (FullTextSearchHelpers.ShouldOrderByRelevance(db, q, sort))
            query = FullTextSearchHelpers.OrderByRelevance(db, query, q);

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

        var query = db.Audios.AsNoTracking().AsQueryable();

        var audioBase = query;
        var audioText = FullTextSearchHelpers.Apply(db, audioBase, findFilter.Q,
            audio => audio.Title,
            audio => audio.Code,
            audio => audio.Details,
            audio => audio.FileSearchText,
            audio => audio.SearchText);
        query = FullTextSearchHelpers.ApplyRelationalMatches(audioText, audioBase, findFilter.Q,
            tagSelectors: [audio => audio.AudioTags.Where(at => at.Tag != null).Select(at => at.Tag!)],
            performerSelectors: [audio => audio.AudioPerformers.Where(ap => ap.Performer != null).Select(ap => ap.Performer!)]);

        query = ApplyFilter(query, req.ObjectFilter);
        query = ApplySort(query, findFilter.Sort, descending, findFilter.Seed);
        if (FullTextSearchHelpers.ShouldOrderByRelevance(db, findFilter.Q, findFilter.Sort))
            query = FullTextSearchHelpers.OrderByRelevance(db, query, findFilter.Q);

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

    [HttpGet("{id:int}")]
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

        Response.Headers["Accept-Ranges"] = "bytes";
        var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
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
    public async Task<ActionResult<AudioDto>> Create([FromBody] AudioCreateDto dto, CancellationToken ct)
    {
        var tagIds = dto.TagIds?.Where(tagId => tagId > 0).Distinct().ToArray() ?? [];
        var performerIds = dto.PerformerIds?.Where(performerId => performerId > 0).Distinct().ToArray() ?? [];
        var audio = new Audio
        {
            Title = NormalizeOptionalText(dto.Title),
            Code = NormalizeOptionalText(dto.Code),
            Details = NormalizeOptionalText(dto.Details),
            Organized = dto.Organized,
            StudioId = dto.StudioId,
            Date = ParseDate(dto.Date),
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

        if (dto.Title != null) audio.Title = NormalizeOptionalText(dto.Title);
        if (dto.Code != null) audio.Code = NormalizeOptionalText(dto.Code);
        if (dto.Details != null) audio.Details = NormalizeOptionalText(dto.Details);
        if (dto.Organized.HasValue) audio.Organized = dto.Organized.Value;
        if (dto.StudioId.HasValue) audio.StudioId = dto.StudioId;
        if (dto.Date != null) audio.Date = ParseDate(dto.Date);

        if (dto.Urls != null)
        {
            audio.Urls.Clear();
            audio.Urls = dto.Urls
                .Select(url => NormalizeOptionalText(url))
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(url => new AudioUrl { AudioId = id, Url = url! })
                .ToList();
        }

        if (dto.TagIds != null)
        {
            var tagIds = dto.TagIds.Where(tagId => tagId > 0).Distinct().ToArray();
            audio.AudioTags.Clear();
            audio.AudioTags = tagIds.Select(tagId => new AudioTag { AudioId = id, TagId = tagId }).ToList();
            audio.TagIds = tagIds;
        }

        if (dto.PerformerIds != null)
        {
            var performerIds = dto.PerformerIds.Where(performerId => performerId > 0).Distinct().ToArray();
            audio.AudioPerformers.Clear();
            audio.AudioPerformers = performerIds.Select(performerId => new AudioPerformer { AudioId = id, PerformerId = performerId }).ToList();
            audio.PerformerIds = performerIds;
        }

        if (dto.GroupIds != null)
        {
            await ReplaceWholeAudioGroupItemsAsync(id, dto.GroupIds, audio.Title, ct);
        }

        await db.SaveChangesAsync(ct);

        if (dto.CustomFields != null)
        {
            await customFields.SaveValuesAsync(CustomFieldEntityTypes.Audio, id, dto.CustomFields, ct);
        }

        var updated = await db.Audios.AsNoTracking()
            .Include(item => item.Studio)
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
            if (dto.Date != null) audio.Date = ParseDate(dto.Date);
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
        return Ok(new { updated = items.Count });
    }

    [HttpDelete("bulk")]
    [RequiresPermission(Permissions.AudiosDelete)]
    [RequiresEntityAccess(EntityKinds.Audio, Permissions.AudiosDelete, ActionArgumentName = "dto", PropertyName = "Ids")]
    public async Task<IActionResult> BulkDelete([FromBody] BatchDeleteDto dto, CancellationToken ct)
    {
        var ids = dto.Ids.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return NoContent();

        var idsToDelete = ids.ToHashSet();
        var deletedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = await db.Audios.Include(item => item.Files).Where(item => ids.Contains(item.Id)).ToListAsync(ct);
        var groupItems = await db.GroupItems.Where(item => item.HostType == "audio" && ids.Contains(item.HostId)).ToListAsync(ct);
        db.GroupItems.RemoveRange(groupItems);
        try
        {
            foreach (var item in items)
                await DeleteAudioArtifactsAsync(item, idsToDelete, deletedPaths, dto.DeleteFiles, dto.DeleteGenerated, ct);
        }
        catch (AudioFileDeleteException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        foreach (var id in ids)
        {
            await customFields.DeleteValuesForEntityAsync(CustomFieldEntityTypes.Audio, id, ct);
        }
        db.Audios.RemoveRange(items);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    [RequiresPermission(Permissions.AudiosDelete)]
    [RequiresEntityAccess(EntityKinds.Audio, Permissions.AudiosDelete)]
    public async Task<IActionResult> Delete(int id, [FromQuery] bool deleteFile = false, [FromQuery] bool deleteGenerated = false, CancellationToken ct = default)
    {
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

    private IQueryable<Audio> ApplySort(IQueryable<Audio> query, string? sort, bool descending, int? seed = null)
    {
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

    private IQueryable<Audio> ApplyFilter(IQueryable<Audio> query, AudioFilter? filter)
    {
        if (filter == null)
            return query;

        query = EngagementQueryHelpers.ApplyRatingCriterion(db, query, EngagementQueryHelpers.CurrentUserId(db), RatingHostType.Audio, filter.RatingCriterion);
        query = EngagementQueryHelpers.ApplyAffinityIntCriterion(db, query, EngagementQueryHelpers.CurrentUserId(db), AffinityHostType.Audio, nameof(UserEntityAffinity.ViewCount), filter.PlayCountCriterion);
        query = EngagementQueryHelpers.ApplyAffinityIntCriterion(db, query, EngagementQueryHelpers.CurrentUserId(db), AffinityHostType.Audio, nameof(UserEntityAffinity.LikeCount), filter.LikeCounterCriterion);
        query = EngagementQueryHelpers.ApplyAffinityDoubleAsIntCriterion(db, query, EngagementQueryHelpers.CurrentUserId(db), AffinityHostType.Audio, nameof(UserEntityAffinity.TotalConsumedSec), filter.PlayDurationCriterion);
        query = EngagementQueryHelpers.ApplyAffinityTimestampCriterion(db, query, EngagementQueryHelpers.CurrentUserId(db), AffinityHostType.Audio, nameof(UserEntityAffinity.LastConsumedAt), filter.LastPlayedAtCriterion);
        query = FilterHelpers.ApplyString(query, filter.TitleCriterion, audio => audio.Title);
        query = FilterHelpers.ApplyString(query, filter.CodeCriterion, audio => audio.Code);
        query = FilterHelpers.ApplyString(query, filter.DetailsCriterion, audio => audio.Details);
        query = FilterHelpers.ApplyFilePath(query, filter.PathCriterion, audio => audio.Files);
        query = ApplyAudioFileStringCriterion(query, filter.FormatCriterion, "format");
        query = ApplyAudioFileStringCriterion(query, filter.AudioCodecCriterion, "audioCodec");
        query = FilterHelpers.ApplyString(query, filter.UrlCriterion, audio => audio.Urls.Select(url => url.Url).FirstOrDefault());
        query = FilterHelpers.ApplyBool(query, filter.OrganizedCriterion, audio => audio.Organized);
        query = FilterHelpers.ApplyBool(query, filter.HasVideoFilesCriterion, audio => audio.HasVideoFiles);
        query = FilterHelpers.ApplyBool(query, filter.HasCoverCriterion, audio => audio.ImageBlobId != null && audio.ImageBlobId != string.Empty);
        query = FilterHelpers.ApplyDate(query, filter.DateCriterion, audio => audio.Date);
        query = FilterHelpers.ApplyInt(query, filter.DurationCriterion, audio => (int)audio.MaxDuration);
        query = FilterHelpers.ApplyLong(query, filter.BitRateCriterion, audio => audio.MaxBitRate);
        query = FilterHelpers.ApplyLong(query, filter.FileSizeCriterion, audio => audio.MaxFileSize);
        query = FilterHelpers.ApplyNullableTimestamp(query, filter.FileModTimeCriterion, audio => audio.MaxFileModTime);
        query = FilterHelpers.ApplyInt(query, filter.FileCountCriterion, audio => audio.FileCount);
        query = FilterHelpers.ApplyInt(query, filter.TrackCountCriterion, audio => audio.Tracks.Count);
        query = FilterHelpers.ApplyString(query, filter.TrackTitleCriterion, audio => audio.Tracks.Select(track => track.Title).FirstOrDefault());
        query = FilterHelpers.ApplyInt(query, filter.SampleRateCriterion, audio => audio.Files.Max(file => file.SampleRate) ?? 0);
        query = FilterHelpers.ApplyInt(query, filter.ChannelsCriterion, audio => audio.Files.Max(file => file.Channels) ?? 0);
        query = ApplyEffectiveTagCountCriterion(query, filter.TagCountCriterion);
        query = FilterHelpers.ApplyInt(query, filter.PerformerCountCriterion, audio => audio.AudioPerformers.Count);
        query = ApplyAudioTagCriterion(query, filter.TagsCriterion);
        query = FilterHelpers.ApplyMultiId(query, filter.PerformersCriterion, audio => audio.AudioPerformers.Select(link => link.PerformerId));
        query = ApplyPerformerOccurrenceTagCriterion(query, filter.PerformerTagsCriterion, GetIncludedPerformerIds(filter));
        query = FilterHelpers.ApplyStudioCriterion(query, filter.StudiosCriterion, audio => audio.StudioId);
        query = FilterHelpers.ApplyMultiId(query, filter.GroupsCriterion, audio => db.GroupItems
            .Where(item => item.HostType == "audio" && item.HostId == audio.Id && item.Kind == GroupItemKind.Audio)
            .Select(item => item.GroupId));
        query = FilterHelpers.ApplyTimestamp(query, filter.CreatedAtCriterion, audio => audio.CreatedAt);
        query = FilterHelpers.ApplyTimestamp(query, filter.UpdatedAtCriterion, audio => audio.UpdatedAt);
        query = query.ApplyCustomFieldCriteria(db, CustomFieldEntityTypes.Audio, filter.CustomFieldCriterion, filter.CustomFieldCriteria);

        return query;
    }

    private static IQueryable<Audio> ApplyAudioFileStringCriterion(IQueryable<Audio> query, StringCriterion? criterion, string field)
    {
        if (criterion == null)
            return query;

        var value = criterion.Value.Trim();
        var lowered = value.ToLowerInvariant();
        return field switch
        {
            "format" => criterion.Modifier switch
            {
                CriterionModifier.Equals => query.Where(audio => audio.Files.Any(file => file.Format == value)),
                CriterionModifier.NotEquals => query.Where(audio => !audio.Files.Any(file => file.Format == value)),
                CriterionModifier.Includes => query.Where(audio => audio.Files.Any(file => file.Format != null && file.Format.ToLower().Contains(lowered))),
                CriterionModifier.Excludes => query.Where(audio => !audio.Files.Any(file => file.Format != null && file.Format.ToLower().Contains(lowered))),
                CriterionModifier.IsNull => query.Where(audio => !audio.Files.Any(file => file.Format != string.Empty)),
                CriterionModifier.NotNull => query.Where(audio => audio.Files.Any(file => file.Format != string.Empty)),
                _ => query,
            },
            "audioCodec" => criterion.Modifier switch
            {
                CriterionModifier.Equals => query.Where(audio => audio.Files.Any(file => file.AudioCodec == value)),
                CriterionModifier.NotEquals => query.Where(audio => !audio.Files.Any(file => file.AudioCodec == value)),
                CriterionModifier.Includes => query.Where(audio => audio.Files.Any(file => file.AudioCodec != null && file.AudioCodec.ToLower().Contains(lowered))),
                CriterionModifier.Excludes => query.Where(audio => !audio.Files.Any(file => file.AudioCodec != null && file.AudioCodec.ToLower().Contains(lowered))),
                CriterionModifier.IsNull => query.Where(audio => !audio.Files.Any(file => file.AudioCodec != string.Empty)),
                CriterionModifier.NotNull => query.Where(audio => audio.Files.Any(file => file.AudioCodec != string.Empty)),
                _ => query,
            },
            _ => query,
        };
    }

    private static int[] GetIncludedPerformerIds(AudioFilter filter)
    {
        if (filter.PerformersCriterion?.Value is not { Count: > 0 }
            || filter.PerformersCriterion.Modifier is not (CriterionModifier.Includes or CriterionModifier.IncludesAll))
        {
            return [];
        }

        return filter.PerformersCriterion.Value.Where(id => id > 0).Distinct().ToArray();
    }

    private IQueryable<Audio> ApplyPerformerOccurrenceTagCriterion(IQueryable<Audio> query, MultiIdCriterion? criterion, IReadOnlyCollection<int> performerIds)
    {
        if (criterion == null)
            return query;

        var tagIds = criterion.Value.Where(tagId => tagId > 0).Distinct().ToArray();
        var excludedTagIds = criterion.Excludes?.Where(tagId => tagId > 0).Distinct().ToArray() ?? [];
        if (tagIds.Length == 0 && excludedTagIds.Length == 0)
            return query;

        var scopedApplications = db.TagApplications.AsNoTracking()
            .Where(application => application.HostType == AffinityHostType.Audio
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
                CriterionModifier.Excludes => query.Where(audio => !scopedApplications.Any(application => application.HostId == audio.Id && tagIds.Contains(application.TagId))),
                CriterionModifier.ExcludesAll => ApplyPerformerOccurrenceTagExcludesAll(query, scopedApplications, tagIds),
                CriterionModifier.IncludesAll => ApplyPerformerOccurrenceTagIncludesAll(query, scopedApplications, tagIds),
                _ => query.Where(audio => scopedApplications.Any(application => application.HostId == audio.Id && tagIds.Contains(application.TagId))),
            };
        }

        if (excludedTagIds.Length > 0)
        {
            query = query.Where(audio => !scopedApplications.Any(application => application.HostId == audio.Id && excludedTagIds.Contains(application.TagId)));
        }

        return query;
    }

    private static IQueryable<Audio> ApplyPerformerOccurrenceTagIncludesAll(IQueryable<Audio> query, IQueryable<TagApplication> applications, IReadOnlyCollection<int> tagIds)
    {
        foreach (var tagId in tagIds)
        {
            query = query.Where(audio => applications.Any(application => application.HostId == audio.Id && application.TagId == tagId));
        }

        return query;
    }

    private static IQueryable<Audio> ApplyPerformerOccurrenceTagExcludesAll(IQueryable<Audio> query, IQueryable<TagApplication> applications, IReadOnlyCollection<int> tagIds)
    {
        var matchingAll = query;
        foreach (var tagId in tagIds)
        {
            matchingAll = matchingAll.Where(audio => applications.Any(application => application.HostId == audio.Id && application.TagId == tagId));
        }

        return query.Where(audio => !matchingAll.Select(match => match.Id).Contains(audio.Id));
    }

    private async Task<AudioDto> MapToDetailDtoAsync(Audio audio, CancellationToken ct)
    {
        var groups = await GetGroupsAsync(audio.Id, ct);
        var customFieldValues = await customFields.GetValuesAsync(CustomFieldEntityTypes.Audio, audio.Id, ct);
        var effectiveTags = await EffectiveTagDtoLoader.LoadAsync(db, AffinityHostType.Audio, [audio.Id], ct);
        var contextTagApplications = await GetContextTagApplicationsAsync(audio.Id, ct);
        var fieldProvenance = fieldProvenanceService == null
            ? null
            : (await fieldProvenanceService.GetForHostAsync(AffinityHostType.Audio, audio.Id, ct)).ToList();
        return MapToDto(audio, groups, customFieldValues, effectiveTags, contextTagApplications, fieldProvenance);
    }

    private AudioDto MapToDto(Audio audio, List<GroupSummaryDto>? groups, Dictionary<string, object>? customFieldValues, IReadOnlyDictionary<int, List<TagDto>>? effectiveTagsByAudioId = null, List<TagApplicationDto>? contextTagApplications = null, List<FieldProvenanceDto>? fieldProvenance = null) => new(
        audio.Id,
        audio.Title,
        audio.Code,
        audio.Details,
        audio.Organized,
        audio.StudioId,
        audio.Studio?.Name,
        audio.Date?.ToString("yyyy-MM-dd"),
        audio.Urls.Select(url => url.Url).ToList(),
        GetEffectiveTags(audio, effectiveTagsByAudioId),
        audio.AudioPerformers.Where(link => link.Performer != null).Select(link => new PerformerSummaryDto(
            link.Performer!.Id,
            link.Performer.Name,
            link.Performer.Disambiguation,
            link.Performer.Gender?.ToString(),
            link.Performer.Birthdate?.ToString("yyyy-MM-dd"),
            link.Performer.Favorite,
            EntityImageUrls.PerformerOrNull(ControllerContext.HttpContext, link.Performer!))).ToList(),
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
            .ThenBy(item => item.Tag!.Name)
            .ToListAsync(ct);

        return applications.Count == 0 ? null : applications.Select(TagApplicationsController.Map).ToList();
    }

    private IQueryable<Audio> ApplyEffectiveTagCountCriterion(IQueryable<Audio> query, IntCriterion? criterion)
    {
        if (criterion == null)
            return query;

        var effectiveTags = EffectiveHostTagQuery.ForHostType(db, AffinityHostType.Audio);
        return FilterHelpers.ApplyInt(query, criterion, audio => effectiveTags
            .Where(tag => tag.HostId == audio.Id)
            .Select(tag => tag.TagId)
            .Distinct()
            .Count());
    }

    private IQueryable<Audio> ApplyAudioTagCriterion(IQueryable<Audio> query, MultiIdCriterion? criterion)
    {
        if (criterion == null)
            return query;

        var effectiveTags = EffectiveHostTagQuery.ForHostType(db, AffinityHostType.Audio);
        if (criterion.Modifier == CriterionModifier.IsNull)
            return query.Where(audio => !effectiveTags.Any(tag => tag.HostId == audio.Id));
        if (criterion.Modifier == CriterionModifier.NotNull)
            return query.Where(audio => effectiveTags.Any(tag => tag.HostId == audio.Id));

        var ids = criterion.Value.Where(tagId => tagId > 0).Distinct().ToArray();
        if (ids.Length > 0)
        {
            query = criterion.Modifier switch
            {
                CriterionModifier.Excludes => query.Where(audio => !effectiveTags.Any(tag => tag.HostId == audio.Id && ids.Contains(tag.TagId))),
                CriterionModifier.ExcludesAll => ApplyAudioTagExcludesAll(query, effectiveTags, ids),
                CriterionModifier.IncludesAll => ApplyAudioTagIncludesAll(query, effectiveTags, ids),
                _ => query.Where(audio => effectiveTags.Any(tag => tag.HostId == audio.Id && ids.Contains(tag.TagId))),
            };
        }

        var excludedIds = criterion.Excludes?.Where(tagId => tagId > 0).Distinct().ToArray() ?? [];
        if (excludedIds.Length > 0)
            query = query.Where(audio => !effectiveTags.Any(tag => tag.HostId == audio.Id && excludedIds.Contains(tag.TagId)));

        return query;
    }

    private static IQueryable<Audio> ApplyAudioTagIncludesAll(IQueryable<Audio> query, IQueryable<EffectiveHostTagRow> effectiveTags, IReadOnlyCollection<int> tagIds)
    {
        foreach (var tagId in tagIds)
        {
            query = query.Where(audio => effectiveTags.Any(tag => tag.HostId == audio.Id && tag.TagId == tagId));
        }

        return query;
    }

    private static IQueryable<Audio> ApplyAudioTagExcludesAll(IQueryable<Audio> query, IQueryable<EffectiveHostTagRow> effectiveTags, IReadOnlyCollection<int> tagIds)
    {
        var matchingAll = query;
        foreach (var tagId in tagIds)
        {
            matchingAll = matchingAll.Where(audio => effectiveTags.Any(tag => tag.HostId == audio.Id && tag.TagId == tagId));
        }

        return query.Where(audio => !matchingAll.Select(match => match.Id).Contains(audio.Id));
    }

    private static List<TagDto> GetEffectiveTags(Audio audio, IReadOnlyDictionary<int, List<TagDto>>? effectiveTagsByAudioId)
        => effectiveTagsByAudioId != null && effectiveTagsByAudioId.TryGetValue(audio.Id, out var tags)
            ? tags
            : audio.AudioTags.Where(link => link.Tag != null).Select(link => TagDtoMapping.MapTagDto(link.Tag!)).ToList();

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

    private async Task ReplaceWholeAudioGroupItemsAsync(int audioId, IReadOnlyCollection<VideoGroupInputDto> groups, string? audioTitle, CancellationToken ct)
    {
        var existing = await db.GroupItems
            .Where(item => item.HostType == "audio" && item.HostId == audioId && item.Kind == GroupItemKind.Audio)
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
            Kind = GroupItemKind.Audio,
            HostType = "audio",
            HostId = audioId,
            Title = NormalizeOptionalText(audioTitle),
        }));
    }

    private static DateOnly? ParseDate(string? value)
        => DateOnly.TryParse(value, out var parsed) ? parsed : null;

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GetVisibleBasename(string path, string basename)
        => string.IsNullOrWhiteSpace(basename) ? Path.GetFileName(path) : basename;
}
