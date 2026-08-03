using System.Text.Json;
using System.Numerics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Common;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Helpers;
using Cove.Core.Interfaces;
using Cove.Data.Repositories;
using Cove.Data.Services;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[RequiresPermission(Permissions.VideosRead)]
public class VideosController(IVideoRepository videoRepo, Data.CoveContext db, MetadataServerService metadataServerService, IThumbnailService thumbnailService, IScanService scanService, IMemoryCache memoryCache, IBlobService blobService, IStreamService streamService, IUserEngagementService engagementService, CustomFieldService customFields, IEventBus eventBus, ITagProvenanceService? tagProvenanceService = null, ICurrentPrincipalAccessor? principalAccessor = null, IFieldProvenanceService? fieldProvenanceService = null) : ControllerBase
{
    private bool CanReadFiles => principalAccessor?.Current?.Has(Permissions.FilesRead) == true;
    private bool HasUserScopedEngagement => principalAccessor?.Current?.UserId != null;
    private static string GetVisibleBasename(string path, string basename) => string.IsNullOrWhiteSpace(basename) ? System.IO.Path.GetFileName(path) : basename;
    private sealed record PerformerSupplementalCounts(int AudioCount, int TextCount);

    [HttpGet]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<PaginatedResponse<VideoDto>>> Find(
        [FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int perPage = 25,
        [FromQuery] string? sort = null, [FromQuery] string? direction = null,
        [FromQuery] int? seed = null,
        [FromQuery] string? title = null, [FromQuery] int? rating = null,
        [FromQuery] bool? organized = null, [FromQuery] int? studioId = null,
        [FromQuery] int? groupId = null, [FromQuery] int? galleryId = null, [FromQuery] string? tagIds = null, [FromQuery] string? performerIds = null,
        [FromQuery] string? ids = null,
        CancellationToken ct = default)
    {
        var filter = new VideoFilter
        {
            Ids = QueryParsing.ParseIntList(ids)?.ToList(),
            Title = title, Rating = rating, Organized = organized, StudioId = studioId, GroupId = groupId, GalleryId = galleryId,
            TagIds = QueryParsing.ParseIntList(tagIds)?.ToList(), PerformerIds = QueryParsing.ParseIntList(performerIds)?.ToList()
        };
        var findFilter = new FindFilter
        {
            Q = q, Page = page, PerPage = perPage, Sort = sort,
            Direction = direction == "desc" ? Core.Enums.SortDirection.Desc : Core.Enums.SortDirection.Asc,
            Seed = seed,
        };

        var (items, totalCount) = await videoRepo.FindAsync(filter, findFilter, ct);
        var effectiveTags = await EffectiveTagDtoLoader.LoadAsync(db, AffinityHostType.Video, items.Select(video => video.Id), ct);
        var engagement = await engagementService.GetVideoSnapshotsAsync(items.Select(video => video.Id), ct);
        var customFieldValues = await customFields.GetValuesAsync(CustomFieldEntityTypes.Video, items.Select(video => video.Id), ct);
        var dtos = items.Select(video => MapListToDto(video, GetCustomFields(customFieldValues, video.Id), engagement.GetValueOrDefault(video.Id), HasUserScopedEngagement, effectiveTags)).ToList();
        return Ok(new PaginatedResponse<VideoDto>(dtos, totalCount, page, perPage));
    }

    [HttpGet("with-compilations")]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<PaginatedResponse<VideoListEntryDto>>> FindWithCompilations(
        [FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int perPage = 25,
        [FromQuery] string? sort = null, [FromQuery] string? direction = null,
        [FromQuery] int? seed = null,
        [FromQuery] string? title = null, [FromQuery] int? rating = null,
        [FromQuery] bool? organized = null, [FromQuery] bool? isVr = null, [FromQuery] int? studioId = null,
        [FromQuery] int? groupId = null, [FromQuery] int? galleryId = null, [FromQuery] string? tagIds = null, [FromQuery] string? performerIds = null,
        CancellationToken ct = default)
    {
        var safePage = Math.Max(1, page);
        var safePerPage = Math.Clamp(perPage, 1, 250);
        var tagIdList = QueryParsing.ParseIntList(tagIds)?.ToList() ?? [];
        var performerIdList = QueryParsing.ParseIntList(performerIds)?.ToList() ?? [];
        var userId = principalAccessor?.Current?.UserId;

        var videoQuery = db.Videos.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(title))
            videoQuery = videoQuery.Where(video => video.Title != null && EF.Functions.ILike(video.Title, $"%{title.Trim()}%"));
        if (organized.HasValue)
            videoQuery = videoQuery.Where(video => video.Organized == organized.Value);
        if (isVr.HasValue)
            videoQuery = videoQuery.Where(video => video.IsVr == isVr.Value);
        if (studioId.HasValue)
            videoQuery = videoQuery.Where(video => video.StudioId == studioId.Value);
        if (groupId.HasValue)
            videoQuery = videoQuery.Where(video => video.GroupItems.Any(item => item.GroupId == groupId.Value));
        if (galleryId.HasValue)
            videoQuery = videoQuery.Where(video => video.VideoGalleries.Any(videoGallery => videoGallery.GalleryId == galleryId.Value));
        if (tagIdList.Count > 0)
        {
            var effectiveVideoTags = EffectiveHostTagQuery.ForHostType(db, AffinityHostType.Video);
            videoQuery = videoQuery.Where(video => effectiveVideoTags.Any(tag => tag.HostId == video.Id && tagIdList.Contains(tag.TagId)));
        }
        if (performerIdList.Count > 0)
            videoQuery = videoQuery.Where(video => video.VideoPerformers.Any(videoPerformer => performerIdList.Contains(videoPerformer.PerformerId)));
        if (rating.HasValue)
        {
            videoQuery = userId.HasValue
                ? videoQuery.Where(video => db.Ratings.Any(item =>
                    item.UserId == userId.Value && item.HostType == RatingHostType.Video && item.HostId == video.Id && item.Aspect == "overall" && item.Value >= rating.Value))
                : videoQuery.Where(_ => false);
        }

        var compilationQuery = db.Groups.AsNoTracking()
            .Where(group => group.ShowInVideoLists && (group.Kind == GroupKind.Dynamic || group.GroupItems.Any(item => item.Kind == GroupItemKind.VideoRange || item.Kind == GroupItemKind.Video || item.Kind == GroupItemKind.Image || item.Kind == GroupItemKind.Audio || item.Kind == GroupItemKind.Text || item.Kind == GroupItemKind.Segment)));
        if (!string.IsNullOrWhiteSpace(title))
            compilationQuery = compilationQuery.Where(group => EF.Functions.ILike(group.Name, $"%{title.Trim()}%"));
        if (studioId.HasValue)
            compilationQuery = compilationQuery.Where(group => group.StudioId == studioId.Value);
        if (tagIdList.Count > 0)
            compilationQuery = compilationQuery.Where(group => group.GroupTags.Any(groupTag => tagIdList.Contains(groupTag.TagId)));
        if (performerIdList.Count > 0)
            compilationQuery = compilationQuery.Where(group => group.GroupItems.Any(item => item.Video != null && item.Video.VideoPerformers.Any(videoPerformer => performerIdList.Contains(videoPerformer.PerformerId))));
        if (rating.HasValue)
        {
            compilationQuery = userId.HasValue
                ? compilationQuery.Where(group => db.Ratings.Any(item =>
                    item.UserId == userId.Value && item.HostType == RatingHostType.Group && item.HostId == group.Id && item.Aspect == "overall" && item.Value >= rating.Value))
                : compilationQuery.Where(_ => false);
        }
        if (organized.HasValue || groupId.HasValue || galleryId.HasValue)
            compilationQuery = compilationQuery.Where(_ => false);

        videoQuery = FullTextSearchHelpers.Apply(db, videoQuery, q,
            video => video.Title,
            video => video.Details,
            video => video.Code,
            video => video.FileSearchText,
            video => video.SearchText);
        compilationQuery = FullTextSearchHelpers.Apply(db, compilationQuery, q,
            group => group.Name,
            group => group.Aliases,
            group => group.Synopsis,
            group => group.Director,
            group => group.SearchText);

        var videoRows = videoQuery.Select(video => new VideoListEntryKey
        {
            Kind = "video",
            Id = video.Id,
            Title = video.Title ?? video.FileSearchText ?? string.Empty,
            Date = video.Date,
            CreatedAt = video.CreatedAt,
            UpdatedAt = video.UpdatedAt,
            Duration = video.MaxDuration,
            BitRate = db.VideoFiles
                .Where(file => file.VideoId == (video.ParentVideoId ?? video.Id))
                .Max(file => (long?)file.BitRate) ?? 0L,
            Rating = userId.HasValue
                ? db.Ratings.Where(item => item.UserId == userId.Value && item.HostType == RatingHostType.Video && item.HostId == video.Id && item.Aspect == "overall").Select(item => item.Value).FirstOrDefault()
                : 0,
        });
        var compilationRows = compilationQuery.Select(group => new VideoListEntryKey
        {
            Kind = "compilation",
            Id = group.Id,
            Title = group.Name,
            Date = group.Date,
            CreatedAt = group.CreatedAt,
            UpdatedAt = group.UpdatedAt,
            Duration = group.Duration ?? 0,
            BitRate = 0,
            Rating = userId.HasValue
                ? db.Ratings.Where(item => item.UserId == userId.Value && item.HostType == RatingHostType.Group && item.HostId == group.Id && item.Aspect == "overall").Select(item => item.Value).FirstOrDefault()
                : 0,
        });

        var combinedQuery = videoRows.Concat(compilationRows);
        var totalCount = await combinedQuery.CountAsync(ct);
        var orderedQuery = ApplyVideoListEntrySorting(combinedQuery, sort, string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase), seed);
        var rows = await orderedQuery.Skip((safePage - 1) * safePerPage).Take(safePerPage).ToListAsync(ct);

        var videoIds = rows.Where(row => row.Kind == "video").Select(row => row.Id).ToArray();
        var groupIds = rows.Where(row => row.Kind == "compilation").Select(row => row.Id).ToArray();

        var videoLookup = videoIds.Length == 0
            ? new Dictionary<int, Video>()
            : await db.Videos
                .Include(video => video.Studio)
                .Include(video => video.ParentVideo).ThenInclude(parent => parent!.Files)
                .Include(video => video.ChildVideos)
                .Include(video => video.Urls)
                .Include(video => video.VideoTags).ThenInclude(videoTag => videoTag.Tag).ThenInclude(tag => tag!.TagGroup)
                .Include(video => video.VideoPerformers).ThenInclude(videoPerformer => videoPerformer.Performer)
                .Include(video => video.VideoGalleries).ThenInclude(videoGallery => videoGallery.Gallery)
                .Include(video => video.Files)
                .Include(video => video.GroupItems).ThenInclude(item => item.Group)
                .AsSplitQuery()
                .AsNoTracking()
                .Where(video => videoIds.Contains(video.Id))
                .ToDictionaryAsync(video => video.Id, ct);

        var groupLookup = groupIds.Length == 0
            ? new Dictionary<int, Group>()
            : await db.Groups
                .Include(group => group.Studio)
                .Include(group => group.Urls)
                .Include(group => group.GroupTags).ThenInclude(groupTag => groupTag.Tag).ThenInclude(tag => tag!.TagGroup)
                .Include(group => group.GroupItems)
                .Include(group => group.SubGroupRelations)
                .Include(group => group.ContainingGroupRelations)
                .AsSplitQuery()
                .AsNoTracking()
                .Where(group => groupIds.Contains(group.Id))
                .ToDictionaryAsync(group => group.Id, ct);

        var engagement = await engagementService.GetVideoSnapshotsAsync(videoIds, ct);
        var customFieldValues = await customFields.GetValuesAsync(CustomFieldEntityTypes.Video, videoIds, ct);
        var effectiveTags = await EffectiveTagDtoLoader.LoadAsync(db, AffinityHostType.Video, videoIds, ct);
        var entries = rows.Select(row =>
        {
            if (row.Kind == "video" && videoLookup.TryGetValue(row.Id, out var video))
            return new VideoListEntryDto("video", video.Id, MapListToDto(video, GetCustomFields(customFieldValues, video.Id), engagement.GetValueOrDefault(video.Id), HasUserScopedEngagement, effectiveTags));
            if (row.Kind == "compilation" && groupLookup.TryGetValue(row.Id, out var group))
                return new VideoListEntryDto("compilation", group.Id, Group: MapCompilationGroupToDto(group));
            return null;
        }).Where(entry => entry != null).Cast<VideoListEntryDto>().ToList();

        return Ok(new PaginatedResponse<VideoListEntryDto>(entries, totalCount, safePage, safePerPage));
    }

    /// <summary>POST-based filtered query supporting advanced criteria (JSON body).</summary>
    [HttpPost("find")]
    public async Task<IActionResult> FindPost([FromBody] FilteredQueryRequest<VideoFilter> req, CancellationToken ct)
    {
        var cacheKey = $"videos_find_{JsonSerializer.Serialize(req)}";
        if (memoryCache.TryGetValue(cacheKey, out PaginatedResponse<VideoDto>? cachedResult) && cachedResult != null)
        {
            return Ok(cachedResult);
        }

        var findFilter = req.FindFilter ?? new FindFilter();
        var filter = req.ObjectFilter ?? new VideoFilter();
        var (items, totalCount) = await videoRepo.FindAsync(filter, findFilter, ct);
        var effectiveTags = await EffectiveTagDtoLoader.LoadAsync(db, AffinityHostType.Video, items.Select(video => video.Id), ct);
        var engagement = await engagementService.GetVideoSnapshotsAsync(items.Select(video => video.Id), ct);
        var customFieldValues = await customFields.GetValuesAsync(CustomFieldEntityTypes.Video, items.Select(video => video.Id), ct);
        var dtos = items.Select(video => MapListToDto(video, GetCustomFields(customFieldValues, video.Id), engagement.GetValueOrDefault(video.Id), HasUserScopedEngagement, effectiveTags)).ToList();
        var result = new PaginatedResponse<VideoDto>(dtos, totalCount, findFilter.Page, findFilter.PerPage);

        memoryCache.Set(cacheKey, result, TimeSpan.FromSeconds(1));
        return Ok(result);
    }

    [HttpGet("{id:int}")]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<VideoDto>> GetById(int id, CancellationToken ct)
    {
        var video = await videoRepo.GetByIdWithRelationsAsync(id, ct);
        if (video == null) return NotFound();
        var engagement = (await engagementService.GetVideoSnapshotsAsync([id], ct)).GetValueOrDefault(id);
        return Ok(await MapToDtoWithProvenanceAsync(video, engagement, HasUserScopedEngagement, ct));
    }

    [HttpPost]
    [RequiresPermission(Permissions.VideosWrite)]
    public async Task<ActionResult<VideoDto>> Create([FromBody] VideoCreateDto dto, CancellationToken ct)
    {
        Video? parentVideo = null;
        if (dto.ParentVideoId.HasValue)
        {
            var parentResolution = await ResolveSubVideoParentAsync(dto.ParentVideoId.Value, dto.ClipStartSec, dto.ClipEndSec, ct);
            if (parentResolution.Error is not null)
                return BadRequest(parentResolution.Error);

            parentVideo = parentResolution.ParentVideo;
            dto = dto with { ClipStartSec = parentResolution.ClipStartSec, ClipEndSec = parentResolution.ClipEndSec };
        }

        var parsedDate = ParseDate(dto.Date);
        var video = new Video
        {
            Title = dto.Title, Code = dto.Code, Details = dto.Details, Director = dto.Director,
            Date = parsedDate ?? parentVideo?.Date, Organized = dto.Organized, IsVr = dto.IsVr, StudioId = dto.StudioId ?? parentVideo?.StudioId,
            Captions = dto.Captions,
            ParentVideoId = parentVideo?.Id, ClipStartSec = dto.ClipStartSec, ClipEndSec = dto.ClipEndSec,
        };
        if (parentVideo is not null)
        {
            video.Captions ??= parentVideo.Captions;
            ApplySubVideoFileMetrics(video, parentVideo);
        }
        if (dto.Urls?.Count > 0)
            video.Urls = dto.Urls.Select(u => new VideoUrl { Url = u }).ToList();
        if (dto.TagIds?.Count > 0)
            video.VideoTags = dto.TagIds.Select(id => new VideoTag { TagId = id }).ToList();
        if (dto.PerformerIds?.Count > 0)
            video.VideoPerformers = dto.PerformerIds.Select(id => new VideoPerformer { PerformerId = id }).ToList();
        if (dto.GalleryIds?.Count > 0)
            video.VideoGalleries = dto.GalleryIds.Select(id => new VideoGallery { GalleryId = id }).ToList();
        if (dto.Groups?.Count > 0)
            video.GroupItems = dto.Groups.Select(group => new GroupItem
            {
                GroupId = group.GroupId,
                OrderIndex = group.VideoIndex,
                Kind = GroupItemKind.Video,
            }).ToList();
        if (dto.RemoteIds?.Count > 0)
            video.RemoteIds = NormalizeRemoteIds(dto.RemoteIds).Select(remoteId => new VideoRemoteId { Endpoint = remoteId.Endpoint, RemoteId = remoteId.RemoteId }).ToList();

        video = await videoRepo.AddAsync(video, ct);
        if (dto.CustomFields != null)
            await customFields.SaveValuesAsync(CustomFieldEntityTypes.Video, video.Id, dto.CustomFields, ct);
        if (dto.TagIds?.Count > 0 && tagProvenanceService != null)
        {
            await tagProvenanceService.SyncTagSetAsync(AffinityHostType.Video, video.Id, [], dto.TagIds, cancellationToken: ct);
            await db.SaveChangesAsync(ct);
        }
        if (dto.Rating.HasValue)
            await engagementService.SetVideoRatingAsync(video.Id, dto.Rating, cancellationToken: ct);

        var result = await videoRepo.GetByIdWithRelationsAsync(video.Id, ct);
        var engagement = (await engagementService.GetVideoSnapshotsAsync([video.Id], ct)).GetValueOrDefault(video.Id);
        return CreatedAtAction(nameof(GetById), new { id = video.Id }, await MapToDtoWithProvenanceAsync(result!, engagement, HasUserScopedEngagement, ct));
    }

    [HttpPost("from-file")]
    [RequiresPermission(Permissions.VideosWrite)]
    public async Task<ActionResult<VideoDto>> CreateFromFile([FromBody] FileBackedCreateDto? dto, CancellationToken ct)
    {
        var filePath = dto?.FilePath?.Trim();
        if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
            return BadRequest(new { error = "A valid file path is required." });

        var videoId = await scanService.ImportDownloadedVideoAsync(filePath, videoId: null, ct);
        var video = await videoRepo.GetByIdWithRelationsAsync(videoId, ct);
        if (video == null) return NotFound();

        var engagement = (await engagementService.GetVideoSnapshotsAsync([videoId], ct)).GetValueOrDefault(videoId);
        return CreatedAtAction(nameof(GetById), new { id = videoId }, await MapToDtoWithProvenanceAsync(video, engagement, HasUserScopedEngagement, ct));
    }

    [HttpPut("{id:int}")]
    [RequiresPermission(Permissions.VideosWrite)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosWrite)]
    public async Task<ActionResult<VideoDto>> Update(int id, [FromBody] VideoUpdateDto dto, CancellationToken ct)
    {
        var video = await videoRepo.GetByIdWithRelationsAsync(id, ct);
        if (video == null) return NotFound();
        var previousTagIds = dto.TagIds != null ? video.VideoTags.Select(videoTag => videoTag.TagId).ToArray() : [];
        var clearFields = dto.ClearFields?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        if (dto.Title != null) video.Title = string.IsNullOrWhiteSpace(dto.Title) ? null : dto.Title;
        if (dto.Code != null) video.Code = string.IsNullOrWhiteSpace(dto.Code) ? null : dto.Code;
        if (dto.Details != null) video.Details = string.IsNullOrWhiteSpace(dto.Details) ? null : dto.Details;
        if (dto.Director != null) video.Director = string.IsNullOrWhiteSpace(dto.Director) ? null : dto.Director;
        if (dto.Date != null) video.Date = ParseDate(dto.Date);
        if (dto.Organized.HasValue) video.Organized = dto.Organized.Value;
        if (dto.IsVr.HasValue) video.IsVr = dto.IsVr.Value;
        if (dto.StudioId.HasValue) video.StudioId = dto.StudioId;
        if (dto.Captions != null) video.Captions = string.IsNullOrWhiteSpace(dto.Captions) ? null : dto.Captions;
        if (clearFields.Contains("date")) video.Date = null;
        if (clearFields.Contains("studioId")) video.StudioId = null;
        if (video.ParentVideoId.HasValue && (dto.ClipStartSec.HasValue || dto.ClipEndSec.HasValue))
        {
            var parentResolution = await ResolveSubVideoParentAsync(video.ParentVideoId.Value, dto.ClipStartSec ?? video.ClipStartSec, dto.ClipEndSec ?? video.ClipEndSec, ct);
            if (parentResolution.Error is not null)
                return BadRequest(parentResolution.Error);

            video.ClipStartSec = parentResolution.ClipStartSec;
            video.ClipEndSec = parentResolution.ClipEndSec;
            ApplySubVideoFileMetrics(video, parentResolution.ParentVideo!);
        }
        else if (!video.ParentVideoId.HasValue && (dto.ClipStartSec.HasValue || dto.ClipEndSec.HasValue))
        {
            return BadRequest("Clip timing can only be changed on sub-videos.");
        }

        if (dto.Urls != null)
        {
            video.Urls.Clear();
            video.Urls = dto.Urls.Select(u => new VideoUrl { Url = u, VideoId = id }).ToList();
        }
        if (dto.TagIds != null)
        {
            video.VideoTags.Clear();
            video.VideoTags = dto.TagIds.Select(tid => new VideoTag { TagId = tid, VideoId = id }).ToList();
        }
        if (dto.PerformerIds != null)
        {
            video.VideoPerformers.Clear();
            video.VideoPerformers = dto.PerformerIds.Select(pid => new VideoPerformer { PerformerId = pid, VideoId = id }).ToList();
        }
        if (dto.GalleryIds != null)
        {
            video.VideoGalleries.Clear();
            video.VideoGalleries = dto.GalleryIds.Select(gid => new VideoGallery { GalleryId = gid, VideoId = id }).ToList();
        }
        if (dto.Groups != null)
        {
            ReplaceWholeVideoGroupItems(video, dto.Groups);
        }
        if (dto.RemoteIds != null)
        {
            video.RemoteIds.Clear();
            video.RemoteIds = NormalizeRemoteIds(dto.RemoteIds).Select(remoteId => new VideoRemoteId { VideoId = id, Endpoint = remoteId.Endpoint, RemoteId = remoteId.RemoteId }).ToList();
        }
        if (dto.TagIds != null && tagProvenanceService != null)
        {
            await tagProvenanceService.SyncTagSetAsync(
                AffinityHostType.Video,
                id,
                previousTagIds,
                video.VideoTags.Select(videoTag => videoTag.TagId).ToArray(),
                cancellationToken: ct);
        }

        await videoRepo.UpdateAsync(video, ct);
        if (dto.CustomFields != null)
            await customFields.SaveValuesAsync(CustomFieldEntityTypes.Video, id, dto.CustomFields, ct);
        if (dto.Rating.HasValue)
            await engagementService.SetVideoRatingAsync(id, dto.Rating, cancellationToken: ct);
        var updated = await videoRepo.GetByIdWithRelationsAsync(id, ct);
        var engagement = (await engagementService.GetVideoSnapshotsAsync([id], ct)).GetValueOrDefault(id);
        return Ok(await MapToDtoWithProvenanceAsync(updated!, engagement, HasUserScopedEngagement, ct));
    }

    [HttpDelete("{id:int}")]
    [RequiresPermission(Permissions.VideosDelete)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosDelete)]
    public async Task<IActionResult> Delete(int id, [FromQuery] bool deleteFile = false, [FromQuery] bool deleteGenerated = false, CancellationToken ct = default)
    {
        var video = await videoRepo.GetByIdWithRelationsAsync(id, ct);
        if (video == null) return NotFound();
        await DeleteVideoArtifactsAsync(video, new HashSet<int> { id }, new HashSet<string>(StringComparer.OrdinalIgnoreCase), deleteFile, deleteGenerated, ct);
        if (tagProvenanceService != null)
            await tagProvenanceService.RemoveForHostAsync(AffinityHostType.Video, id, ct);
        await customFields.DeleteValuesForEntityAsync(CustomFieldEntityTypes.Video, id, ct);
        await videoRepo.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("destroy")]
    [RequiresPermission(Permissions.VideosDelete)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosDelete, ActionArgumentName = "dto", PropertyName = "Ids")]
    public async Task<IActionResult> DestroyBatch([FromBody] BatchDeleteDto dto, CancellationToken ct)
    {
        var deletedIds = new List<int>();
        var idsToDelete = dto.Ids.ToHashSet();
        var deletedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in dto.Ids)
        {
            var video = await videoRepo.GetByIdWithRelationsAsync(id, ct);
            if (video != null)
            {
                await DeleteVideoArtifactsAsync(video, idsToDelete, deletedPaths, dto.DeleteFiles, dto.DeleteGenerated, ct);

                if (tagProvenanceService != null)
                    await tagProvenanceService.RemoveForHostAsync(AffinityHostType.Video, id, ct);
                await customFields.DeleteValuesForEntityAsync(CustomFieldEntityTypes.Video, id, ct);
                await videoRepo.DeleteAsync(id, ct);
                deletedIds.Add(id);
            }
        }
        return Ok(new BulkDeleteResult(deletedIds));
    }

    private async Task DeleteVideoArtifactsAsync(Video video, IReadOnlySet<int> idsToDelete, HashSet<string> deletedPaths, bool deleteFiles, bool deleteGenerated, CancellationToken ct)
    {
        if (deleteFiles)
        {
            foreach (var file in video.Files)
            {
                var path = file.Path;
                if (string.IsNullOrWhiteSpace(path) || !deletedPaths.Add(path))
                    continue;

                var referencedByKeptVideo = await db.Set<VideoFile>()
                    .AnyAsync(videoFile => videoFile.Path == path && videoFile.VideoId.HasValue && !idsToDelete.Contains(videoFile.VideoId.Value), ct);
                if (!referencedByKeptVideo && System.IO.File.Exists(path))
                    System.IO.File.Delete(path);
            }
        }

        if (video.Files.Count > 0)
            db.VideoFiles.RemoveRange(video.Files);

        if (deleteGenerated)
            await thumbnailService.DeleteVideoGeneratedFilesAsync(video.Id, ct);

        if (!string.IsNullOrWhiteSpace(video.ImageBlobId))
        {
            if (deleteGenerated)
                await thumbnailService.DeleteBlobGeneratedFilesAsync(video.ImageBlobId, ct);
            await blobService.DeleteBlobAsync(video.ImageBlobId, ct);
        }
    }

    [HttpGet("{id:int}/metadata-server/search")]
    public async Task<ActionResult<IReadOnlyList<MetadataServerVideoMatchDto>>> SearchMetadataServer(int id, [FromQuery] string? term, [FromQuery] string? endpoint, CancellationToken ct)
    {
        var video = await videoRepo.GetByIdWithRelationsAsync(id, ct);
        if (video == null) return NotFound();

        return Ok(await metadataServerService.SearchVideosAsync(video, term, endpoint, ct));
    }

    // Fetch matches directly by this server's ids (e.g. a video's existing remote ids), so the tagger can
    // refresh/rescrape from a known remote entry without a name search.
    [HttpPost("metadata-server/find-by-ids")]
    public async Task<ActionResult<IReadOnlyList<MetadataServerVideoMatchDto>>> FindMetadataServerVideosByIds([FromBody] MetadataServerFindByIdsRequestDto dto, CancellationToken ct)
    {
        if (dto.Ids.Count == 0)
            return Ok(Array.Empty<MetadataServerVideoMatchDto>());

        var results = new List<MetadataServerVideoMatchDto>();
        foreach (var videoId in dto.Ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var match = await metadataServerService.GetVideoMatchAsync(dto.Endpoint, videoId, ct);
            if (match != null)
                results.Add(match);
        }

        return Ok(results);
    }

    [HttpPost("{id:int}/metadata-server/import")]
    [RequiresPermission(Permissions.VideosWrite)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosWrite)]
    public async Task<ActionResult<VideoDto>> ImportFromMetadataServer(int id, [FromBody] MetadataServerVideoImportRequestDto dto, CancellationToken ct)
    {
        var video = await videoRepo.GetByIdWithRelationsAsync(id, ct);
        if (video == null) return NotFound();

        var imported = await metadataServerService.MergeVideoAsync(video, dto.Endpoint, dto.VideoId, dto, ct);
        if (!imported) return NotFound();

        await db.SaveChangesAsync(ct);
        PublishVideoEvent(EventType.VideoUpdated, id);
        var updated = await videoRepo.GetByIdWithRelationsAsync(id, ct);
        return Ok(await MapToDtoWithProvenanceAsync(updated!, cancellationToken: ct));
    }

    [HttpPost("{id:int}/metadata-server/submit-fingerprints")]
    [RequiresPermission(Permissions.VideosWrite)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosWrite)]
    public async Task<IActionResult> SubmitFingerprints(int id, [FromBody] MetadataServerEndpointDto dto, CancellationToken ct)
    {
        var video = await videoRepo.GetByIdWithRelationsAsync(id, ct);
        if (video == null) return NotFound();

        await metadataServerService.SubmitFingerprintsAsync(video, dto.Endpoint, ct);
        return Ok();
    }

    [HttpPost("{id:int}/metadata-server/submit-draft")]
    [RequiresPermission(Permissions.VideosWrite)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosWrite)]
    public async Task<IActionResult> SubmitVideoDraft(int id, [FromBody] MetadataServerEndpointDto dto, CancellationToken ct)
    {
        var video = await videoRepo.GetByIdWithRelationsAsync(id, ct);
        if (video == null) return NotFound();

        var draftId = await metadataServerService.SubmitVideoDraftAsync(video, dto.Endpoint, ct);
        return Ok(new { draftId });
    }

    [HttpPost("{id:int}/cover/from-frame")]
    [RequiresPermission(Permissions.VideosWrite)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosWrite)]
    public async Task<IActionResult> SetCoverFromFrame(int id, [FromBody] GenerateScreenshotDto? dto, CancellationToken ct)
    {
        var existingVideo = await db.Videos
            .AsNoTracking()
            .Where(video => video.Id == id)
            .Select(video => new { video.ImageBlobId })
            .SingleOrDefaultAsync(ct);
        if (existingVideo == null) return NotFound();

        await thumbnailService.GenerateVideoThumbnailAsync(id, dto?.AtSeconds, ct);
        var screenshot = await streamService.GetVideoScreenshot(id, dto?.AtSeconds, ct);
        if (screenshot == null) return NotFound();

        await using var screenshotStream = screenshot.Value.stream;
        var imageBlobId = await blobService.StoreBlobAsync(screenshotStream, screenshot.Value.contentType, ct);
        int updatedRows;
        try
        {
            updatedRows = await db.Videos
                .Where(video => video.Id == id && video.ImageBlobId == existingVideo.ImageBlobId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(video => video.ImageBlobId, imageBlobId)
                        .SetProperty(video => video.UpdatedAt, DateTime.UtcNow),
                    ct);
        }
        catch (Exception updateException)
        {
            try
            {
                await blobService.DeleteBlobAsync(imageBlobId, CancellationToken.None);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "The cover update failed and the generated cover could not be cleaned up.",
                    updateException,
                    cleanupException);
            }

            throw;
        }

        if (updatedRows != 1)
        {
            await blobService.DeleteBlobAsync(imageBlobId, CancellationToken.None);
            return Conflict(new
            {
                message = "The video cover changed while this cover was being generated. Please try again.",
            });
        }

        if (!string.IsNullOrWhiteSpace(existingVideo.ImageBlobId))
            await blobService.DeleteBlobAsync(existingVideo.ImageBlobId, CancellationToken.None);

        PublishVideoEvent(EventType.VideoUpdated, id);
        return Ok(new { success = true });
    }

    private async Task<VideoDto> MapToDtoWithProvenanceAsync(Video video, UserEngagementSnapshot? engagement = null, bool preferUserSnapshot = false, CancellationToken cancellationToken = default)
    {
        var effectiveTags = await EffectiveTagDtoLoader.LoadAsync(db, AffinityHostType.Video, [video.Id], cancellationToken);
        var contextTagApplications = await LoadContextTagApplicationsAsync(video.Id, cancellationToken);
        var fieldProvenance = fieldProvenanceService == null
            ? null
            : (await fieldProvenanceService.GetForHostAsync(AffinityHostType.Video, video.Id, cancellationToken)).ToList();
        var performerCounts = await LoadPerformerSupplementalCountsAsync(
            video.VideoPerformers
                .Where(videoPerformer => videoPerformer.Performer != null)
                .Select(videoPerformer => videoPerformer.Performer!.Id)
                .Distinct()
                .ToArray(),
            cancellationToken);

        var customFieldValues = await customFields.GetValuesAsync(CustomFieldEntityTypes.Video, video.Id, cancellationToken);
        return MapToDto(video, customFieldValues, engagement, preferUserSnapshot, effectiveTags, contextTagApplications, fieldProvenance, performerCounts);
    }

    private async Task<IReadOnlyDictionary<int, PerformerSupplementalCounts>> LoadPerformerSupplementalCountsAsync(IReadOnlyCollection<int> performerIds, CancellationToken cancellationToken)
    {
        if (performerIds.Count == 0) return new Dictionary<int, PerformerSupplementalCounts>();

        var audioCounts = await db.Set<AudioPerformer>()
            .Where(audioPerformer => performerIds.Contains(audioPerformer.PerformerId))
            .GroupBy(audioPerformer => audioPerformer.PerformerId)
            .Select(group => new { PerformerId = group.Key, Count = group.Select(audioPerformer => audioPerformer.AudioId).Distinct().Count() })
            .ToDictionaryAsync(item => item.PerformerId, item => item.Count, cancellationToken);

        var textCounts = await db.Set<TextPerformer>()
            .Where(textPerformer => performerIds.Contains(textPerformer.PerformerId))
            .GroupBy(textPerformer => textPerformer.PerformerId)
            .Select(group => new { PerformerId = group.Key, Count = group.Select(textPerformer => textPerformer.TextDocumentId).Distinct().Count() })
            .ToDictionaryAsync(item => item.PerformerId, item => item.Count, cancellationToken);

        return performerIds.ToDictionary(
            id => id,
            id => new PerformerSupplementalCounts(audioCounts.GetValueOrDefault(id), textCounts.GetValueOrDefault(id)));
    }

    private sealed class VideoListEntryKey
    {
        public string Kind { get; set; } = string.Empty;
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public DateOnly? Date { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public double Duration { get; set; }
        public long BitRate { get; set; }
        public int Rating { get; set; }
    }

    private static IOrderedQueryable<VideoListEntryKey> ApplyVideoListEntrySorting(IQueryable<VideoListEntryKey> query, string? sort, bool desc, int? seed)
    {
        var randomSeed = seed ?? 0;
        return sort switch
        {
            "title" or "name" => desc
                ? query.OrderByDescending(item => item.Title).ThenByDescending(item => item.Kind).ThenByDescending(item => item.Id)
                : query.OrderBy(item => item.Title).ThenBy(item => item.Kind).ThenBy(item => item.Id),
            "date" => desc
                ? query.OrderByDescending(item => item.Date ?? DateOnly.MinValue).ThenByDescending(item => item.Id)
                : query.OrderBy(item => item.Date ?? DateOnly.MinValue).ThenBy(item => item.Id),
            "rating" => desc
                ? query.OrderBy(item => item.Rating <= 0 ? 1 : 0).ThenByDescending(item => item.Rating).ThenByDescending(item => item.Id)
                : query.OrderBy(item => item.Rating <= 0 ? 0 : 1).ThenBy(item => item.Rating).ThenBy(item => item.Id),
            "created_at" => desc
                ? query.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
                : query.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id),
            "duration" => desc
                ? query.OrderByDescending(item => item.Duration).ThenByDescending(item => item.Id)
                : query.OrderBy(item => item.Duration).ThenBy(item => item.Id),
            "bitrate" => desc
                ? query.OrderByDescending(item => item.BitRate).ThenByDescending(item => item.Id)
                : query.OrderBy(item => item.BitRate).ThenBy(item => item.Id),
            "random" => desc
                ? query.OrderByDescending(item => ((long)item.Id * 1103515245L + randomSeed + (item.Kind == "compilation" ? 7919 : 0)) % 2147483647L).ThenByDescending(item => item.Id)
                : query.OrderBy(item => ((long)item.Id * 1103515245L + randomSeed + (item.Kind == "compilation" ? 7919 : 0)) % 2147483647L).ThenBy(item => item.Id),
            _ => desc
                ? query.OrderByDescending(item => item.UpdatedAt).ThenByDescending(item => item.Id)
                : query.OrderBy(item => item.UpdatedAt).ThenBy(item => item.Id),
        };
    }

    private GroupDto MapCompilationGroupToDto(Group group) => new(
        group.Id, group.Name, group.Aliases, group.Date?.ToString("yyyy-MM-dd"),
        group.StudioId, group.Studio?.Name, group.Director, group.Synopsis,
        group.Urls.Select(url => url.Url).ToList(),
        group.GroupTags.Where(groupTag => groupTag.Tag != null).Select(groupTag => TagDtoMapping.MapTagDto(groupTag.Tag!)).ToList(),
        group.GroupItems.Where(item => item.VideoId.HasValue).Select(item => item.VideoId!.Value).Distinct().Count(),
        group.GroupItems.Count,
        true,
        group.SubGroupRelations?.Count ?? 0,
        group.ContainingGroupRelations?.Count ?? 0,
        null,
        group.CreatedAt.ToString("o"), group.UpdatedAt.ToString("o"),
        group.FrontImageBlobId != null ? EntityImageUrls.GroupFront(ControllerContext.HttpContext, group.Id, group.UpdatedAt) : GetCompilationPosterPath(group),
        group.BackImageBlobId != null ? EntityImageUrls.GroupBack(ControllerContext.HttpContext, group.Id, group.UpdatedAt) : null,
        group.Kind,
        group.QuerySourceKey,
        group.QueryJson,
        group.LastResolvedAt?.ToString("o"),
        group.CachedItemCount,
        group.ShowInVideoLists,
        group.AllowedHostTypes,
        group.SortOrder
    );

    private string? GetCompilationPosterPath(Group group)
    {
        var firstRange = group.GroupItems
            .Where(item => item.VideoId.HasValue)
            .OrderBy(item => item.OrderIndex)
            .ThenBy(item => item.Id)
            .FirstOrDefault();

        return firstRange?.VideoId is int videoId
            ? EntityImageUrls.VideoScreenshot(ControllerContext.HttpContext, videoId, group.UpdatedAt, firstRange.StartSec)
            : null;
    }

    private VideoDto MapToDto(Video s, Dictionary<string, object>? customFieldValues = null, UserEngagementSnapshot? engagement = null, bool preferUserSnapshot = false, IReadOnlyDictionary<int, List<TagDto>>? effectiveTagsByVideoId = null, List<TagApplicationDto>? contextTagApplications = null, List<FieldProvenanceDto>? fieldProvenance = null, IReadOnlyDictionary<int, PerformerSupplementalCounts>? performerCounts = null) => new(
        s.Id, s.Title, s.Code, s.Details, s.Director,
        s.Date?.ToString("yyyy-MM-dd"),
        s.Organized, s.IsVr, s.StudioId, s.Studio?.Name,
        s.Captions,
        s.Urls.Select(u => u.Url).ToList(),
        GetEffectiveTags(s, effectiveTagsByVideoId),
        s.VideoPerformers.Where(sp => sp.Performer != null).Select(sp => sp.Performer!).OrderForDisplay().Select(performer => MapPerformerSummary(performer, performerCounts)).ToList(),
        EffectiveFiles(s).Select(f => new VideoFileDto(
            f.Id,
            CanReadFiles ? f.Path : string.Empty,
            GetVisibleBasename(f.Path, f.Basename),
            f.Format,
            f.Width,
            f.Height,
            f.Duration,
            f.VideoCodec,
            f.AudioCodec,
            f.FrameRate,
            f.BitRate,
            f.Size,
            f.Fingerprints.Select(fp => new FingerprintDto(fp.Type, fp.Value)).ToList(),
            f.Captions.Select(c => new CaptionDto(c.Id, c.LanguageCode, c.CaptionType, c.Filename)).ToList())).ToList(),
        MapWholeVideoGroups(s),
        s.VideoGalleries.Where(sg => sg.Gallery != null).Select(sg => new GallerySummaryDto(sg.Gallery!.Id, sg.Gallery.Title, sg.Gallery.Date?.ToString("yyyy-MM-dd"))).ToList(),
        s.RemoteIds.Select(remoteId => new VideoRemoteIdDto(remoteId.Endpoint, remoteId.RemoteId)).ToList(),
        customFieldValues,
        s.CreatedAt.ToString("o"), s.UpdatedAt.ToString("o"),
        contextTagApplications,
        fieldProvenance,
        s.ParentVideoId,
        GetVideoDisplayTitle(s.ParentVideo),
        s.ClipStartSec,
        s.ClipEndSec,
        s.ChildVideos.Count,
        ImagePath: s.ImageBlobId != null ? EntityImageUrls.Video(ControllerContext.HttpContext, s.Id, s.UpdatedAt, 1280) : null
    );

    private VideoDto MapListToDto(Video s, Dictionary<string, object>? customFieldValues = null, UserEngagementSnapshot? engagement = null, bool preferUserSnapshot = false, IReadOnlyDictionary<int, List<TagDto>>? effectiveTagsByVideoId = null) => new(
        s.Id, s.Title, s.Code, s.Details, s.Director,
        s.Date?.ToString("yyyy-MM-dd"),
        s.Organized, s.IsVr, s.StudioId, s.Studio?.Name,
        s.Captions,
        s.Urls.Select(u => u.Url).ToList(),
        GetEffectiveTags(s, effectiveTagsByVideoId),
        s.VideoPerformers.Where(sp => sp.Performer != null).Select(sp => sp.Performer!).OrderForDisplay().Select(performer => MapPerformerSummary(performer, null)).ToList(),
        EffectiveFiles(s).Select(f => new VideoFileDto(
            f.Id,
            CanReadFiles ? f.Path : string.Empty,
            GetVisibleBasename(f.Path, f.Basename),
            f.Format,
            f.Width,
            f.Height,
            f.Duration,
            f.VideoCodec,
            f.AudioCodec,
            f.FrameRate,
            f.BitRate,
            f.Size,
            [],
            [])).ToList(),
        MapWholeVideoGroups(s),
        s.VideoGalleries.Where(sg => sg.Gallery != null).Select(sg => new GallerySummaryDto(sg.Gallery!.Id, sg.Gallery.Title, sg.Gallery.Date?.ToString("yyyy-MM-dd"))).ToList(),
        s.RemoteIds.Select(remoteId => new VideoRemoteIdDto(remoteId.Endpoint, remoteId.RemoteId)).ToList(),
        customFieldValues,
        s.CreatedAt.ToString("o"), s.UpdatedAt.ToString("o"),
        ParentVideoId: s.ParentVideoId,
        ParentVideoTitle: GetVideoDisplayTitle(s.ParentVideo),
        ClipStartSec: s.ClipStartSec,
        ClipEndSec: s.ClipEndSec,
        ChildVideoCount: s.ChildVideos.Count,
        ImagePath: s.ImageBlobId != null ? EntityImageUrls.Video(ControllerContext.HttpContext, s.Id, s.UpdatedAt, 1280) : null
    );

    private static List<TagDto> GetEffectiveTags(Video video, IReadOnlyDictionary<int, List<TagDto>>? effectiveTagsByVideoId)
        => effectiveTagsByVideoId != null && effectiveTagsByVideoId.TryGetValue(video.Id, out var tags)
            ? tags
            : video.VideoTags.Where(videoTag => videoTag.Tag != null).Select(videoTag => MapTagDto(videoTag.Tag!)).ToList();

    private PerformerSummaryDto MapPerformerSummary(Performer performer, IReadOnlyDictionary<int, PerformerSupplementalCounts>? supplementalCounts)
    {
        var supplemental = supplementalCounts != null && supplementalCounts.TryGetValue(performer.Id, out var counts)
            ? counts
            : null;
        return new PerformerSummaryDto(
            performer.Id,
            performer.Name,
            performer.Disambiguation,
            performer.Gender?.ToString(),
            performer.Birthdate?.ToString("yyyy-MM-dd"),
            performer.Favorite,
            EntityImageUrls.PerformerOrNull(ControllerContext.HttpContext, performer),
            performer.VideoCount,
            performer.ImageCount,
            performer.GalleryCount,
            supplemental?.AudioCount ?? 0,
            supplemental?.TextCount ?? 0);
    }

    private static IEnumerable<VideoFile> EffectiveFiles(Video video)
        => video.Files.Count > 0 ? video.Files : video.ParentVideo?.Files ?? Enumerable.Empty<VideoFile>();

    private static List<VideoRemoteIdDto> NormalizeRemoteIds(IEnumerable<VideoRemoteIdDto> remoteIds)
        => remoteIds
            .Select(remoteId => new VideoRemoteIdDto(remoteId.Endpoint.Trim(), remoteId.RemoteId.Trim()))
            .Where(remoteId => !string.IsNullOrWhiteSpace(remoteId.Endpoint) && !string.IsNullOrWhiteSpace(remoteId.RemoteId))
            .GroupBy(remoteId => new { Endpoint = remoteId.Endpoint.ToUpperInvariant(), RemoteId = remoteId.RemoteId.ToUpperInvariant() })
            .Select(group => group.First())
            .ToList();

    private static string? GetVideoDisplayTitle(Video? video)
        => !string.IsNullOrWhiteSpace(video?.Title)
            ? video.Title
            : video?.Files.OrderBy(file => file.Id).FirstOrDefault()?.Basename;

    private async Task<SubVideoParentResolution> ResolveSubVideoParentAsync(int parentVideoId, double? requestedStartSec, double? requestedEndSec, CancellationToken ct)
    {
        var requestedVideo = await db.Videos.AsNoTracking()
            .Include(video => video.Files)
            .FirstOrDefaultAsync(video => video.Id == parentVideoId, ct);
        if (requestedVideo is null)
            return SubVideoParentResolution.Fail("Parent video was not found.");

        var parentVideo = requestedVideo;
        while (parentVideo.ParentVideoId.HasValue)
        {
            parentVideo = await db.Videos.AsNoTracking()
                .Include(video => video.Files)
                .FirstOrDefaultAsync(video => video.Id == parentVideo.ParentVideoId.Value, ct);

            if (parentVideo is null)
                return SubVideoParentResolution.Fail("Parent video was not found.");
        }

        var sourceDuration = parentVideo.Files.Count > 0
            ? parentVideo.Files.Max(file => file.Duration)
            : parentVideo.MaxDuration;
        if (sourceDuration <= 0)
            return SubVideoParentResolution.Fail("Parent video has no playable duration.");

        var requestedRange = GetVideoEffectiveClipRange(requestedVideo, sourceDuration);
        var startSec = NormalizeRequestedClipBoundary(requestedStartSec, requestedRange.startSec, requestedRange.endSec, isEndBoundary: false);
        var endSec = NormalizeRequestedClipBoundary(requestedEndSec, requestedRange.startSec, requestedRange.endSec, isEndBoundary: true);

        if (startSec < requestedRange.startSec || startSec >= requestedRange.endSec)
            return SubVideoParentResolution.Fail("Clip start must be within the selected parent video range.");
        if (startSec >= sourceDuration)
            return SubVideoParentResolution.Fail("Clip start must be before the end of the parent video.");
        if (endSec > requestedRange.endSec)
            return SubVideoParentResolution.Fail("Clip end must be within the selected parent video range.");
        if (endSec <= startSec)
            return SubVideoParentResolution.Fail("Clip end must be greater than clip start.");

        return new SubVideoParentResolution(parentVideo, startSec, endSec, null);
    }

    private static (double startSec, double endSec) GetVideoEffectiveClipRange(Video video, double sourceDuration)
    {
        var startSec = Math.Max(0, video.ClipStartSec ?? 0);
        var endSec = Math.Min(sourceDuration, video.ClipEndSec ?? sourceDuration);
        return endSec > startSec ? (startSec, endSec) : (startSec, sourceDuration);
    }

    private static double NormalizeRequestedClipBoundary(double? requestedSec, double rangeStartSec, double rangeEndSec, bool isEndBoundary)
    {
        var defaultValue = isEndBoundary ? rangeEndSec : rangeStartSec;
        if (!requestedSec.HasValue)
            return defaultValue;

        var value = requestedSec.Value;
        if (value >= rangeStartSec && value <= rangeEndSec)
            return value;

        var relativeRange = Math.Max(0, rangeEndSec - rangeStartSec);
        if (value >= 0 && value <= relativeRange)
            return rangeStartSec + value;

        return value;
    }

    private static void ApplySubVideoFileMetrics(Video video, Video parentVideo)
    {
        var clipStart = video.ClipStartSec ?? 0;
        var clipEnd = video.ClipEndSec ?? parentVideo.MaxDuration;
        video.FileCount = parentVideo.FileCount > 0 ? parentVideo.FileCount : parentVideo.Files.Count;
        video.MaxDuration = Math.Max(0, clipEnd - clipStart);
        video.MaxResolution = parentVideo.MaxResolution;
        video.MaxHeight = parentVideo.MaxHeight;
        video.MaxFrameRate = parentVideo.MaxFrameRate;
        video.MaxBitRate = parentVideo.MaxBitRate;
        video.MaxFileSize = parentVideo.MaxFileSize;
        video.MaxFileModTime = parentVideo.MaxFileModTime;
        video.MinPath = parentVideo.MinPath;
        video.MaxPath = parentVideo.MaxPath;
        video.FileSearchText = parentVideo.FileSearchText;
        video.HasDimensionData = parentVideo.HasDimensionData;
        video.HasLandscapeFiles = parentVideo.HasLandscapeFiles;
        video.HasPortraitFiles = parentVideo.HasPortraitFiles;
        video.HasSquareFiles = parentVideo.HasSquareFiles;
    }

    private sealed record SubVideoParentResolution(Video? ParentVideo, double ClipStartSec, double ClipEndSec, string? Error)
    {
        public static SubVideoParentResolution Fail(string error) => new(null, 0, 0, error);
    }

    private static Dictionary<string, object>? GetCustomFields(IReadOnlyDictionary<int, Dictionary<string, object>> lookup, int id)
        => lookup.TryGetValue(id, out var values) && values.Count > 0 ? values : null;

    private async Task<List<TagApplicationDto>> LoadContextTagApplicationsAsync(int videoId, CancellationToken ct)
    {
        var applications = await db.TagApplications
            .AsNoTracking()
            .Include(application => application.Tag).ThenInclude(tag => tag!.Aliases)
            .Include(application => application.Tag).ThenInclude(tag => tag!.TagGroup)
            .AsSplitQuery()
            .Where(application => application.HostType == AffinityHostType.Video
                && application.HostId == videoId
                && application.ContextType != null
                && application.ContextId != null)
            .OrderBy(application => application.ContextType)
            .ThenBy(application => application.ContextId)
            .ThenBy(application => application.Tag!.Name)
            .ToListAsync(ct);

        return applications.Select(TagApplicationsController.Map).ToList();
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
            HasImage: tag.ImageOverrideBlobId != null || tag.ImageBlobId != null);

    // ===== Activity Tracking =====

    [HttpPost("{id:int}/play")]
    [RequiresPermission(Permissions.VideosRead)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosRead)]
    public async Task<IActionResult> RecordPlay(int id, CancellationToken ct)
    {
        var snapshot = await engagementService.RecordVideoPlayAsync(id, ct);
        if (snapshot == null) return NotFound();
        return NoContent();
    }

    [HttpDelete("{id:int}/play")]
    [RequiresPermission(Permissions.VideosWrite)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosWrite)]
    public async Task<IActionResult> DeletePlay(int id, CancellationToken ct)
    {
        var snapshot = await engagementService.DeleteVideoPlayAsync(id, ct);
        if (snapshot == null) return NotFound();
        return NoContent();
    }

    [HttpPost("{id:int}/play/reset")]
    [RequiresPermission(Permissions.VideosWrite)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosWrite)]
    public async Task<IActionResult> ResetPlayCount(int id, CancellationToken ct)
    {
        var snapshot = await engagementService.ResetVideoPlayAsync(id, ct);
        if (snapshot == null) return NotFound();
        return NoContent();
    }

    [HttpPost("{id:int}/like")]
    [RequiresPermission(Permissions.VideosRead)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosRead)]
    public async Task<ActionResult<int>> IncrementLike(int id, CancellationToken ct)
    {
        var snapshot = await engagementService.IncrementVideoLikeAsync(id, ct);
        if (snapshot == null) return NotFound();
        return Ok(snapshot.LikeCount);
    }

    [HttpPost("{id:int}/like/historical")]
    [RequiresPermission(Permissions.VideosWrite)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosWrite)]
    public async Task<ActionResult<int>> AddHistoricalLike(int id, HistoricalLikeDto request, CancellationToken ct)
    {
        var at = request.At.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(request.At, DateTimeKind.Utc)
            : request.At.ToUniversalTime();
        if (at > DateTime.UtcNow)
            return BadRequest("Historical likes must be dated in the past.");

        var snapshot = await engagementService.AddHistoricalVideoLikeAsync(id, at, ct);
        if (snapshot == null) return NotFound();
        return Ok(snapshot.LikeCount);
    }

    [HttpDelete("{id:int}/like")]
    [RequiresPermission(Permissions.VideosRead)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosRead)]
    public async Task<IActionResult> DecrementLike(int id, CancellationToken ct)
    {
        var snapshot = await engagementService.DecrementVideoLikeAsync(id, ct);
        if (snapshot == null) return NotFound();
        return NoContent();
    }

    [HttpPost("{id:int}/like/reset")]
    [RequiresPermission(Permissions.VideosWrite)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosWrite)]
    public async Task<IActionResult> ResetLike(int id, CancellationToken ct)
    {
        var snapshot = await engagementService.ResetVideoLikeAsync(id, ct);
        if (snapshot == null) return NotFound();
        return NoContent();
    }

    [HttpGet("{id:int}/history")]
    [RequiresPermission(Permissions.VideosRead)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosRead)]
    public async Task<ActionResult<VideoHistoryDto>> GetHistory(int id, CancellationToken ct)
    {
        var history = await engagementService.GetVideoHistoryAsync(id, ct);
        return history is null ? NotFound() : Ok(history);
    }

    [HttpPost("{id:int}/activity/reset")]
    [RequiresPermission(Permissions.VideosWrite)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosWrite)]
    public async Task<IActionResult> ResetActivity(int id, CancellationToken ct)
    {
        var snapshot = await engagementService.ResetVideoActivityAsync(id, ct);
        if (snapshot == null) return NotFound();
        return NoContent();
    }

    [HttpPost("{id:int}/rating")]
    [RequiresPermission(Permissions.VideosRead)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosRead)]
    public async Task<ActionResult<int?>> SetRating(int id, [FromBody] VideoRatingDto dto, CancellationToken ct)
    {
        var snapshot = await engagementService.SetVideoRatingAsync(id, dto.Value, dto.Aspect, ct);
        return snapshot is null ? NotFound() : Ok(snapshot.Rating);
    }

    [HttpGet("{id:int}/ratings")]
    [RequiresPermission(Permissions.VideosRead)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosRead)]
    public async Task<ActionResult<EntityRatingsDto>> GetRatings(int id, CancellationToken ct)
    {
        var ratings = await engagementService.GetRatingsByAspectAsync(AffinityHostType.Video, id, ct);
        return ratings is null ? NotFound() : Ok(new EntityRatingsDto(id, ratings));
    }

    [HttpDelete("{id:int}/rating")]
    [RequiresPermission(Permissions.VideosRead)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosRead)]
    public async Task<IActionResult> ClearRating(int id, [FromQuery] string aspect = "overall", CancellationToken ct = default)
    {
        var snapshot = await engagementService.SetVideoRatingAsync(id, null, aspect, ct);
        return snapshot is null ? NotFound() : NoContent();
    }

    // ===== Video Wall/Discovery =====

    [HttpGet("wall")]
    public async Task<ActionResult<List<VideoDto>>> VideoWall([FromQuery] string? q, [FromQuery] int count = 24, CancellationToken ct = default)
    {
        var query = db.Videos
            .Include(s => s.Files).ThenInclude(f => f.Fingerprints)
            .Include(s => s.VideoTags).ThenInclude(st => st.Tag)
            .Include(s => s.VideoPerformers).ThenInclude(sp => sp.Performer)
            .Include(s => s.Studio)
            .AsNoTracking();

        query = FullTextSearchHelpers.Apply(db, query, q,
            video => video.Title,
            video => video.Details,
            video => video.Code,
            video => video.FileSearchText,
            video => video.SearchText);

        var videos = await query.OrderBy(_ => EF.Functions.Random()).Take(count).ToListAsync(ct);
        var engagement = await engagementService.GetVideoSnapshotsAsync(videos.Select(video => video.Id), ct);
        var customFieldValues = await customFields.GetValuesAsync(CustomFieldEntityTypes.Video, videos.Select(video => video.Id), ct);
        return Ok(videos.Select(video => MapToDto(video, GetCustomFields(customFieldValues, video.Id), engagement.GetValueOrDefault(video.Id), HasUserScopedEngagement)).ToList());
    }

    [HttpGet("duplicates")]
    public async Task<ActionResult<List<List<VideoDto>>>> FindDuplicates(
        [FromQuery] string? matchType = "fingerprint",
        [FromQuery] int distance = 0,
        [FromQuery] double? durationDiff = null,
        CancellationToken ct = default)
    {
        var groups = (matchType ?? "fingerprint").Trim().ToLowerInvariant() switch
        {
            "phash" or "visual" => await FindPhashDuplicateVideoIdsAsync(Math.Max(0, distance), durationDiff, ct),
            "title" => await FindTitleDuplicateVideoIdsAsync(ct),
            "remoteid" or "remote-id" or "remote_id" => await FindRemoteIdDuplicateVideoIdsAsync(ct),
            _ => await FindExactFingerprintDuplicateVideoIdsAsync(ct),
        };

        var result = new List<List<VideoDto>>();
        foreach (var videoIds in groups)
        {
            var videos = await db.Videos
                .Include(s => s.Files).ThenInclude(f => f.Fingerprints)
                .Include(s => s.VideoTags).ThenInclude(st => st.Tag)
                .Include(s => s.VideoPerformers).ThenInclude(sp => sp.Performer)
                .Include(s => s.Studio)
                .Include(s => s.RemoteIds)
                .Where(s => videoIds.Contains(s.Id))
                .AsNoTracking()
                .ToListAsync(ct);

            if (videos.Count > 1)
            {
                var orderedVideos = videos.OrderBy(video => video.Title ?? video.Files.Select(file => file.Basename).FirstOrDefault() ?? string.Empty).ThenBy(video => video.Id).ToList();
                var customFieldValues = await customFields.GetValuesAsync(CustomFieldEntityTypes.Video, orderedVideos.Select(video => video.Id), ct);
                result.Add(orderedVideos.Select(video => MapToDto(video, GetCustomFields(customFieldValues, video.Id))).ToList());
            }
        }

        return Ok(result);
    }

    private async Task<List<List<int>>> FindExactFingerprintDuplicateVideoIdsAsync(CancellationToken ct)
    {
        var fingerprintRows = await db.Set<FileFingerprint>()
            .Where(fingerprint => (fingerprint.Type == "oshash" || fingerprint.Type == "md5") && fingerprint.Value != "")
            .Select(fingerprint => new { fingerprint.Type, fingerprint.Value, fingerprint.FileId })
            .AsNoTracking()
            .ToListAsync(ct);

        var keys = fingerprintRows
            .GroupBy(fingerprint => new { fingerprint.Type, fingerprint.Value })
            .Where(group => group.Select(fingerprint => fingerprint.FileId).Distinct().Count() > 1)
            .Select(group => new { group.Key.Type, group.Key.Value })
            .ToList();

        var result = new List<List<int>>();
        var seenGroups = new HashSet<string>();
        foreach (var key in keys)
        {
            var videoIds = await db.VideoFiles
                .Where(file => file.VideoId.HasValue && file.Fingerprints.Any(fingerprint => fingerprint.Type == key.Type && fingerprint.Value == key.Value))
                .Select(file => file.VideoId!.Value)
                .Distinct()
                .OrderBy(id => id)
                .ToListAsync(ct);

            AddVideoGroup(result, seenGroups, videoIds);
        }

        return result;
    }

    private async Task<List<List<int>>> FindPhashDuplicateVideoIdsAsync(int maxDistance, double? durationDiff, CancellationToken ct)
    {
        var files = await db.VideoFiles
            .Include(file => file.Fingerprints)
            .Where(file => file.VideoId.HasValue)
            .AsNoTracking()
            .ToListAsync(ct);

        var candidates = files
            .SelectMany(file => file.Fingerprints
                .Where(fingerprint => fingerprint.Type == "phash" && fingerprint.Value != "")
                .Select(fingerprint => TryParsePHash(fingerprint.Value, out var parsedHash)
                    ? new DuplicatePHashCandidate(file.VideoId!.Value, file.Duration, parsedHash)
                    : (DuplicatePHashCandidate?)null))
            .Where(candidate => candidate.HasValue)
            .Select(candidate => candidate!.Value)
            .ToList();

        var videoIds = candidates.Select(candidate => candidate.VideoId).Distinct().ToArray();
        var parent = videoIds.ToDictionary(id => id, id => id);

        for (var leftIndex = 0; leftIndex < candidates.Count; leftIndex++)
        {
            var left = candidates[leftIndex];
            for (var rightIndex = leftIndex + 1; rightIndex < candidates.Count; rightIndex++)
            {
                var right = candidates[rightIndex];
                if (left.VideoId == right.VideoId) continue;
                if (durationDiff.HasValue && Math.Abs(left.Duration - right.Duration) > durationDiff.Value) continue;
                if (BitOperations.PopCount(left.Hash ^ right.Hash) <= maxDistance)
                    Union(parent, left.VideoId, right.VideoId);
            }
        }

        return parent.Keys
            .GroupBy(id => Find(parent, id))
            .Select(group => group.OrderBy(id => id).ToList())
            .Where(group => group.Count > 1)
            .OrderBy(group => group[0])
            .ToList();
    }

    private async Task<List<List<int>>> FindTitleDuplicateVideoIdsAsync(CancellationToken ct)
    {
        var rows = await db.Videos
            .Where(video => video.Title != null && video.Title != "")
            .Select(video => new { video.Id, video.Title })
            .AsNoTracking()
            .ToListAsync(ct);

        return rows
            .GroupBy(row => row.Title!.Trim().ToLowerInvariant())
            .Select(group => group.Select(row => row.Id).OrderBy(id => id).ToList())
            .Where(group => group.Count > 1)
            .OrderBy(group => group[0])
            .ToList();
    }

    private async Task<List<List<int>>> FindRemoteIdDuplicateVideoIdsAsync(CancellationToken ct)
    {
        var rows = await db.Set<VideoRemoteId>()
            .Where(remoteId => remoteId.RemoteId != "")
            .Select(remoteId => new { remoteId.VideoId, remoteId.Endpoint, remoteId.RemoteId })
            .AsNoTracking()
            .ToListAsync(ct);

        return rows
            .GroupBy(row => $"{row.Endpoint.Trim().ToLowerInvariant()}\n{row.RemoteId.Trim().ToLowerInvariant()}")
            .Select(group => group.Select(row => row.VideoId).Distinct().OrderBy(id => id).ToList())
            .Where(group => group.Count > 1)
            .OrderBy(group => group[0])
            .ToList();
    }

    private static void AddVideoGroup(List<List<int>> result, HashSet<string> seenGroups, List<int> videoIds)
    {
        if (videoIds.Count <= 1) return;
        var key = string.Join(',', videoIds);
        if (seenGroups.Add(key)) result.Add(videoIds);
    }

    private static bool TryParsePHash(string value, out ulong hash)
    {
        hash = 0;
        var normalized = value.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];
        if (normalized.Length is 0 or > 16) return false;
        if (normalized.Any(character => !Uri.IsHexDigit(character))) return false;
        return ulong.TryParse(normalized, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out hash);
    }

    private static int Find(Dictionary<int, int> parent, int id)
    {
        if (parent[id] == id) return id;
        parent[id] = Find(parent, parent[id]);
        return parent[id];
    }

    private static void Union(Dictionary<int, int> parent, int left, int right)
    {
        var leftRoot = Find(parent, left);
        var rightRoot = Find(parent, right);
        if (leftRoot != rightRoot) parent[rightRoot] = leftRoot;
    }

    private readonly record struct DuplicatePHashCandidate(int VideoId, double Duration, ulong Hash);

    // ===== Bulk Operations =====

    [HttpPost("bulk")]
    [RequiresPermission(Permissions.VideosWrite)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosWrite, ActionArgumentName = "dto", PropertyName = "Ids")]
    public async Task<IActionResult> BulkUpdate([FromBody] BulkVideoUpdateDto dto, CancellationToken ct)
    {
        var videos = await db.Videos
            .Include(s => s.VideoTags)
            .Include(s => s.VideoPerformers)
            .Include(s => s.VideoGalleries)
            .Include(s => s.GroupItems)
            .Where(s => dto.Ids.Contains(s.Id))
            .ToListAsync(ct);
        var clearFields = dto.ClearFields?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        foreach (var video in videos)
        {
            var previousTagIds = dto.TagIds != null ? video.VideoTags.Select(videoTag => videoTag.TagId).ToArray() : [];

            if (clearFields.Contains("studioId")) video.StudioId = null;
            if (clearFields.Contains("date")) video.Date = null;
            if (clearFields.Contains("code")) video.Code = null;
            if (clearFields.Contains("director")) video.Director = null;
            if (dto.Organized.HasValue) video.Organized = dto.Organized.Value;
            if (dto.IsVr.HasValue) video.IsVr = dto.IsVr.Value;
            if (dto.StudioId.HasValue) video.StudioId = dto.StudioId;
            if (dto.Date != null) video.Date = ParseDate(dto.Date);
            if (dto.Code != null) video.Code = dto.Code;
            if (dto.Director != null) video.Director = dto.Director;

            if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Set)
            {
                video.VideoTags.Clear();
                video.VideoTags = dto.TagIds.Select(tid => new VideoTag { TagId = tid, VideoId = video.Id }).ToList();
            }
            else if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Add)
            {
                var existing = video.VideoTags.Select(st => st.TagId).ToHashSet();
                foreach (var tid in dto.TagIds.Where(t => !existing.Contains(t)))
                    video.VideoTags.Add(new VideoTag { TagId = tid, VideoId = video.Id });
            }
            else if (dto.TagIds != null && dto.TagMode == BulkUpdateMode.Remove)
            {
                video.VideoTags = video.VideoTags.Where(st => !dto.TagIds.Contains(st.TagId)).ToList();
            }

            if (dto.TagIds != null && tagProvenanceService != null)
            {
                await tagProvenanceService.SyncTagSetAsync(
                    AffinityHostType.Video,
                    video.Id,
                    previousTagIds,
                    video.VideoTags.Select(videoTag => videoTag.TagId).ToArray(),
                    cancellationToken: ct);
            }

            if (dto.PerformerIds != null && dto.PerformerMode == BulkUpdateMode.Set)
            {
                video.VideoPerformers.Clear();
                video.VideoPerformers = dto.PerformerIds.Select(pid => new VideoPerformer { PerformerId = pid, VideoId = video.Id }).ToList();
            }
            else if (dto.PerformerIds != null && dto.PerformerMode == BulkUpdateMode.Add)
            {
                var existing = video.VideoPerformers.Select(sp => sp.PerformerId).ToHashSet();
                foreach (var pid in dto.PerformerIds.Where(p => !existing.Contains(p)))
                    video.VideoPerformers.Add(new VideoPerformer { PerformerId = pid, VideoId = video.Id });
            }
            else if (dto.PerformerIds != null && dto.PerformerMode == BulkUpdateMode.Remove)
            {
                video.VideoPerformers = video.VideoPerformers.Where(sp => !dto.PerformerIds.Contains(sp.PerformerId)).ToList();
            }

            if (dto.GalleryIds != null && dto.GalleryMode == BulkUpdateMode.Set)
            {
                video.VideoGalleries.Clear();
                video.VideoGalleries = dto.GalleryIds.Select(gid => new VideoGallery { GalleryId = gid, VideoId = video.Id }).ToList();
            }
            else if (dto.GalleryIds != null && dto.GalleryMode == BulkUpdateMode.Add)
            {
                var existing = video.VideoGalleries.Select(sg => sg.GalleryId).ToHashSet();
                foreach (var gid in dto.GalleryIds.Where(g => !existing.Contains(g)))
                    video.VideoGalleries.Add(new VideoGallery { GalleryId = gid, VideoId = video.Id });
            }
            else if (dto.GalleryIds != null && dto.GalleryMode == BulkUpdateMode.Remove)
            {
                video.VideoGalleries = video.VideoGalleries.Where(sg => !dto.GalleryIds.Contains(sg.GalleryId)).ToList();
            }

            if (dto.GroupIds != null && dto.GroupMode == BulkUpdateMode.Set)
            {
                ReplaceWholeVideoGroupItems(video, dto.GroupIds);
            }
            else if (dto.GroupIds != null && dto.GroupMode == BulkUpdateMode.Add)
            {
                var existing = video.GroupItems
                    .Where(item => item.Kind == GroupItemKind.Video)
                    .Select(item => item.GroupId)
                    .ToHashSet();
                foreach (var g in dto.GroupIds.Where(g => !existing.Contains(g.GroupId)))
                    video.GroupItems.Add(new GroupItem
                    {
                        GroupId = g.GroupId,
                        OrderIndex = g.VideoIndex,
                        Kind = GroupItemKind.Video,
                        VideoId = video.Id,
                    });
            }
            else if (dto.GroupIds != null && dto.GroupMode == BulkUpdateMode.Remove)
            {
                var removeIds = dto.GroupIds.Select(g => g.GroupId).ToHashSet();
                RemoveWholeVideoGroupItems(video, video.GroupItems.Where(item => item.Kind == GroupItemKind.Video && removeIds.Contains(item.GroupId)).ToList());
            }
        }

        await db.SaveChangesAsync(ct);
        if (dto.Rating.HasValue)
        {
            foreach (var video in videos)
                await engagementService.SetVideoRatingAsync(video.Id, dto.Rating, cancellationToken: ct);
        }
        return Ok(new BulkUpdateResult(videos.Select(video => video.Id).ToList()));
    }

    private static List<GroupSummaryDto> MapWholeVideoGroups(Video video)
        => video.GroupItems
            .Where(item => item.Kind == GroupItemKind.Video && item.Group != null)
            .OrderBy(item => item.OrderIndex)
            .Select(item => new GroupSummaryDto(item.Group!.Id, item.Group.Name, item.OrderIndex))
            .ToList();

    private void ReplaceWholeVideoGroupItems(Video video, IEnumerable<VideoGroupInputDto> groups)
    {
        RemoveWholeVideoGroupItems(video, video.GroupItems.Where(item => item.Kind == GroupItemKind.Video).ToList());
        foreach (var group in groups.Where(group => group is { GroupId: > 0 }))
        {
            video.GroupItems.Add(new GroupItem
            {
                GroupId = group.GroupId,
                OrderIndex = group.VideoIndex,
                Kind = GroupItemKind.Video,
                VideoId = video.Id,
            });
        }
    }

    private void RemoveWholeVideoGroupItems(Video video, IReadOnlyCollection<GroupItem> items)
    {
        foreach (var item in items)
        {
            video.GroupItems.Remove(item);
        }

        if (items.Count > 0)
        {
            db.GroupItems.RemoveRange(items);
        }
    }

    // ===== Merge =====

    [HttpPost("merge")]
    [RequiresPermission(Permissions.VideosWrite)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosWrite, ActionArgumentName = "dto", PropertyName = "TargetId")]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosWrite, ActionArgumentName = "dto", PropertyName = "SourceIds")]
    public async Task<ActionResult<VideoDto>> MergeVideos([FromBody] VideoMergeDto dto, CancellationToken ct)
    {
        var target = await videoRepo.GetByIdWithRelationsAsync(dto.TargetId, ct);
        if (target == null) return NotFound("Target video not found");

        var sources = await db.Videos
            .Include(s => s.Files)
            .Include(s => s.VideoTags)
            .Include(s => s.VideoPerformers)
            .Include(s => s.VideoGalleries)
            .Include(s => s.Urls)
            .Where(s => dto.SourceIds.Contains(s.Id) && s.Id != target.Id)
            .OrderBy(s => s.Id)
            .ToListAsync(ct);

        var existingTagIds = target.VideoTags.Select(st => st.TagId).ToHashSet();
        var existingPerfIds = target.VideoPerformers.Select(sp => sp.PerformerId).ToHashSet();

        foreach (var source in sources)
        {
            // Move files to target
            foreach (var f in source.Files) f.VideoId = target.Id;
            // Merge tags
            foreach (var st in source.VideoTags.Where(st => !existingTagIds.Contains(st.TagId)))
                target.VideoTags.Add(new VideoTag { TagId = st.TagId, VideoId = target.Id });
            // Merge performers
            foreach (var sp in source.VideoPerformers.Where(sp => !existingPerfIds.Contains(sp.PerformerId)))
                target.VideoPerformers.Add(new VideoPerformer { PerformerId = sp.PerformerId, VideoId = target.Id });
            // Delete source
            if (tagProvenanceService != null)
                await tagProvenanceService.RemoveForHostAsync(AffinityHostType.Video, source.Id, ct);
            db.Videos.Remove(source);
        }

        await db.SaveChangesAsync(ct);
        if (sources.Count > 0)
        {
            PublishVideoEvent(EventType.VideoUpdated, target.Id);
            foreach (var source in sources)
                PublishVideoEvent(EventType.VideoDeleted, source.Id);
        }

        var result = await videoRepo.GetByIdWithRelationsAsync(target.Id, ct);
        var engagement = (await engagementService.GetVideoSnapshotsAsync([target.Id], ct)).GetValueOrDefault(target.Id);
        return Ok(await MapToDtoWithProvenanceAsync(result!, engagement, HasUserScopedEngagement, ct));
    }

    // ===== Generate Screenshot =====

    [HttpPost("{id:int}/generate-screenshot")]
    [RequiresPermission(Permissions.VideosWrite)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosWrite)]
    public async Task<IActionResult> GenerateScreenshot(int id, [FromBody] GenerateScreenshotDto? dto, CancellationToken ct)
    {
        if (!await db.Videos.AsNoTracking().AnyAsync(video => video.Id == id, ct))
            return NotFound();

        await thumbnailService.GenerateVideoThumbnailAsync(id, dto?.AtSeconds, ct);
        await db.Videos
            .Where(video => video.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(video => video.UpdatedAt, DateTime.UtcNow),
                ct);
        PublishVideoEvent(EventType.VideoUpdated, id);
        return Ok(new { success = true });
    }

    // ===== Rescan =====

    [HttpPost("{id:int}/rescan")]
    [RequiresPermission(Permissions.LibraryScan)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.LibraryScan)]
    public async Task<IActionResult> Rescan(int id, CancellationToken ct)
    {
        var video = await db.Videos.Include(s => s.Files).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (video == null) return NotFound();

        var filePath = video.Files.FirstOrDefault()?.ParentFolder != null 
            ? Path.Combine(video.Files.First().ParentFolder!.Path, video.Files.First().Basename)
            : video.Files.FirstOrDefault()?.Basename;
        
        if (string.IsNullOrEmpty(filePath)) return BadRequest("Video has no files");

        var jobId = scanService.StartScan(new ScanOperationOptions
        {
            Paths = [filePath],
            Rescan = true,
        });
        return Ok(new { jobId });
    }

    // ===== Assign File =====

    [HttpPost("{id:int}/assign-file")]
    [RequiresPermission(Permissions.VideosWrite)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosWrite)]
    public async Task<IActionResult> AssignFile(int id, [FromBody] VideoAssignFileDto dto, CancellationToken ct)
    {
        var video = await db.Videos.FindAsync([id], ct);
        if (video == null) return NotFound("Video not found");

        var file = await db.Set<VideoFile>().FirstOrDefaultAsync(f => f.Id == dto.FileId, ct);
        if (file == null) return NotFound("File not found");
        if (file.VideoId == id) return Ok();

        var previousOwnerId = file.VideoId;
        file.VideoId = id;
        await db.SaveChangesAsync(ct);
        if (previousOwnerId is int previousId && previousId != id)
            PublishVideoEvent(EventType.VideoUpdated, previousId);
        PublishVideoEvent(EventType.VideoUpdated, id);
        return Ok();
    }

    private void PublishVideoEvent(EventType type, int id)
        => eventBus.Publish(new EntityEvent(type, "Video", id));

    private static DateOnly? ParseDate(string? date) => DateOnly.TryParse(date, out var d) ? d : null;
}

public record GenerateScreenshotDto(double? AtSeconds = null);
