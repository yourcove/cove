using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using PermissionKeys = Cove.Core.Auth.Permissions;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data.Services;

namespace Cove.Data.Repositories;

public class VideoRepository : IVideoRepository
{
    private readonly CoveContext _db;
    public VideoRepository(CoveContext db) => _db = db;

    public async Task<Video?> GetByIdAsync(int id, CancellationToken ct = default)
        => await _db.Videos.FindAsync([id], ct);

    public async Task<Video?> GetByIdWithRelationsAsync(int id, CancellationToken ct = default)
        => await _db.Videos
            .Include(s => s.Studio)
            .Include(s => s.Urls)
            .Include(s => s.VideoTags).ThenInclude(st => st.Tag).ThenInclude(tag => tag!.TagGroup)
            .Include(s => s.VideoPerformers).ThenInclude(sp => sp.Performer)
            .Include(s => s.VideoGalleries).ThenInclude(sg => sg.Gallery)
            .Include(s => s.GroupItems).ThenInclude(item => item.Group)
            .Include(s => s.Files).ThenInclude(f => f.Fingerprints)
            .Include(s => s.Files).ThenInclude(f => f.Captions)
            .Include(s => s.Files).ThenInclude(f => f.ParentFolder)
            .Include(s => s.RemoteIds)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<Video>> GetAllAsync(CancellationToken ct = default)
        => await _db.Videos.AsNoTracking().ToListAsync(ct);

    public async Task<Video> AddAsync(Video entity, CancellationToken ct = default)
    {
        _db.Videos.Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task UpdateAsync(Video entity, CancellationToken ct = default)
    {
        _db.Videos.Update(entity);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var entity = await _db.Videos.FindAsync([id], ct);
        if (entity != null)
        {
            _db.Videos.Remove(entity);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
        => await _db.Videos.CountAsync(ct);

    public async Task<IReadOnlyList<VideoPerformer>> GetVideoPerformersAsync(IReadOnlyList<int> videoIds, CancellationToken ct = default)
        => await _db.Set<VideoPerformer>()
            .AsNoTracking()
            .Include(static vp => vp.Performer)
                .ThenInclude(static p => p!.RemoteIds)
            .Where(vp => videoIds.Contains(vp.VideoId) && vp.Performer != null)
            .ToListAsync(ct);

    public async Task<(IReadOnlyList<Video> Items, int TotalCount)> FindAsync(VideoFilter? filter, FindFilter? findFilter, CancellationToken ct = default)
    {
        ExpandedHierarchicalTagCriterion? expandedTags = null;
        if (filter?.TagsCriterion?.Depth == -1)
        {
            expandedTags = await ExpandHierarchicalTagCriterionAsync(filter.TagsCriterion, ct);
            filter.TagsCriterion = expandedTags.Criterion;
        }

        var currentPrincipal = _db.CurrentPrincipalForReadOptimization;
        var readScopePlan = await ReadScopeListOptimization.TryBuildPlanAsync<Video>(
            _db,
            EntityKinds.Video,
            currentPrincipal?.Has(PermissionKeys.VideosRead) == true,
            currentPrincipal?.ReadGrantedEntityKinds.Contains(EntityKinds.Video) == true,
            ct);

        // Build a lightweight filter-only query (no Includes) for COUNT and filter predicates
        var filterQuery = (readScopePlan ?? new ReadScopeRootPlan<Video>(false, null)).Apply(_db.Videos.AsQueryable());

        // Apply all filters to the lightweight query
        filterQuery = ApplyFilters(filterQuery, filter, expandedTags?.ValueGroups);

        filterQuery = ApplyVideoSearch(filterQuery, findFilter?.Q);

        // COUNT runs on the lightweight query â€” no JOINs from Includes
        var perPage = findFilter?.PerPage ?? 25;

        // Short-circuit for count-only requests (perPage <= 0)
        if (perPage <= 0)
        {
            var count = await filterQuery.CountAsync(ct);
            return (Array.Empty<Video>(), count);
        }

        // Run COUNT first on the lightweight query (no Includes = faster)
        var totalCount = await filterQuery.AsNoTracking().CountAsync(ct);

        // Sort and paginate on the lightweight query, then fetch only the IDs
        var hasExplicitSort = !string.IsNullOrWhiteSpace(findFilter?.Sort);
        var sort = findFilter?.Sort ?? "updated_at";
        var desc = findFilter?.Direction == Core.Enums.SortDirection.Desc;
        filterQuery = ApplySorting(filterQuery, sort, desc, findFilter?.Seed);
        if (!hasExplicitSort)
            filterQuery = FullTextSearchHelpers.OrderByRelevance(_db, filterQuery, findFilter?.Q);

        var page = findFilter?.Page ?? 1;
        var pagedIds = await filterQuery
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .Select(s => s.Id)
            .ToListAsync(ct);

        if (pagedIds.Count == 0)
            return (Array.Empty<Video>(), totalCount);

        // Load full entities only for the paged IDs
        var items = await _db.Videos
            .Include(s => s.Studio)
            .Include(s => s.Urls)
            .Include(s => s.VideoTags).ThenInclude(st => st.Tag).ThenInclude(tag => tag!.TagGroup)
            .Include(s => s.VideoPerformers).ThenInclude(sp => sp.Performer)
            .Include(s => s.VideoGalleries).ThenInclude(sg => sg.Gallery)
            .Include(s => s.GroupItems).ThenInclude(item => item.Group)
            .Include(s => s.Files).ThenInclude(f => f.Fingerprints)
            .Include(s => s.RemoteIds)
            .AsSplitQuery()
            .Where(s => pagedIds.Contains(s.Id))
            .AsNoTracking()
            .ToListAsync(ct);

        // Restore the sort order from the paged IDs
        var orderMap = pagedIds.Select((id, idx) => (id, idx)).ToDictionary(x => x.id, x => x.idx);
        var sorted = items.OrderBy(s => orderMap.GetValueOrDefault(s.Id, int.MaxValue)).ToList();

        return (sorted, totalCount);
    }

    private IQueryable<Video> ApplyFilters(IQueryable<Video> query, VideoFilter? filter, IReadOnlyList<int[]>? hierarchicalTagGroups = null)
    {
        if (filter == null) return query;
        var currentUserId = EngagementQueryHelpers.CurrentUserId(_db);
            if (filter.Ids?.Count > 0)
                query = query.Where(s => filter.Ids.Contains(s.Id));
            if (!string.IsNullOrEmpty(filter.Title))
                query = query.Where(s => s.Title != null && EF.Functions.ILike(s.Title, $"%{filter.Title}%"));
            if (filter.Rating.HasValue)
                query = EngagementQueryHelpers.ApplyRatingMinimum(_db, query, currentUserId, RatingHostType.Video, filter.Rating.Value);
            if (filter.Organized.HasValue)
                query = query.Where(s => s.Organized == filter.Organized.Value);
            if (filter.IsVr.HasValue)
                query = query.Where(s => s.IsVr == filter.IsVr.Value);
            if (filter.StudioId.HasValue)
                query = query.Where(s => s.StudioId == filter.StudioId.Value);
            if (filter.GroupId.HasValue)
                query = query.Where(s => s.GroupItems.Any(item => item.GroupId == filter.GroupId.Value));
            if (filter.GalleryId.HasValue)
                query = query.Where(s => s.VideoGalleries.Any(sg => sg.GalleryId == filter.GalleryId.Value));
            if (filter.TagIds?.Count > 0)
                query = ApplyVideoTagAny(query, filter.TagIds);
            if (filter.PerformerIds?.Count > 0)
                query = query.Where(s => s.PerformerIds.Any(id => filter.PerformerIds.Contains(id)));

            // Advanced criteria
            query = EngagementQueryHelpers.ApplyRatingCriterion(_db, query, currentUserId, RatingHostType.Video, filter.RatingCriterion);
            query = EngagementQueryHelpers.ApplyAffinityIntCriterion(_db, query, currentUserId, AffinityHostType.Video, nameof(UserEntityAffinity.LikeCount), filter.LikeCounterCriterion);
            query = EngagementQueryHelpers.ApplyAffinityIntCriterion(_db, query, currentUserId, AffinityHostType.Video, nameof(UserEntityAffinity.ViewCount), filter.PlayCountCriterion);

            if (filter.PerformerCountCriterion != null)
                query = ApplyIntCriterion(query, filter.PerformerCountCriterion, s => s.VideoPerformers.Count);

            if (filter.DurationCriterion != null)
                query = ApplyIntCriterion(query, filter.DurationCriterion, s => (int)s.MaxDuration);

            if (filter.ResolutionCriterion != null)
                query = FilterHelpers.ApplyResolution(query, filter.ResolutionCriterion, s => s.MaxResolution);

            if (filter.FrameRateCriterion != null)
                query = ApplyIntCriterion(query, filter.FrameRateCriterion, s => (int)s.MaxFrameRate);

            if (filter.BitrateInterval != null)
                query = ApplyBitrateCriterion(query, filter.BitrateInterval);

            if (filter.FileCountCriterion != null)
                query = ApplyIntCriterion(query, filter.FileCountCriterion, s => s.FileCount);

            query = ApplyVideoTagCriterion(query, filter.TagsCriterion, hierarchicalTagGroups);
            query = ApplyTagDurationCriterion(query, filter.TagDurationCriterion);
            query = ApplyMultiIdCriterion(query, filter.PerformersCriterion, s => s.PerformerIds);

            if (filter.StudiosCriterion != null)
            {
                var ids = filter.StudiosCriterion.Value;
                query = filter.StudiosCriterion.Modifier switch
                {
                    CriterionModifier.IsNull => query.Where(s => !s.StudioId.HasValue),
                    CriterionModifier.NotNull => query.Where(s => s.StudioId.HasValue),
                    CriterionModifier.Includes => query.Where(s => s.StudioId.HasValue && ids.Contains(s.StudioId.Value)),
                    CriterionModifier.Excludes => query.Where(s => !s.StudioId.HasValue || !ids.Contains(s.StudioId.Value)),
                    _ when ids.Count == 0 => query,
                    _ => query.Where(s => s.StudioId.HasValue && ids.Contains(s.StudioId.Value)),
                };
            }

            query = ApplyMultiIdCriterion(query, filter.GroupsCriterion, s => s.GroupItems.Select(item => item.GroupId));

            if (filter.OrganizedCriterion != null)
                query = query.Where(s => s.Organized == filter.OrganizedCriterion.Value);

            if (filter.IsVrCriterion != null)
                query = query.Where(s => s.IsVr == filter.IsVrCriterion.Value);

            query = ApplyFingerprintCriterion(query, filter.FingerprintCriterion);
            query = ApplyFingerprintCriterion(query, filter.HashCriterion, "oshash");
            query = ApplyFingerprintCriterion(query, filter.ChecksumCriterion, "md5");

            if (filter.DuplicatedPhashCriterion != null)
                query = ApplyDuplicatedPhashCriterion(query, filter.DuplicatedPhashCriterion);

            if (filter.DuplicatedTitleCriterion != null)
                query = ApplyDuplicatedTitleCriterion(query, filter.DuplicatedTitleCriterion);

            if (filter.DuplicatedRemoteIdCriterion != null)
                query = ApplyDuplicatedRemoteIdCriterion(query, filter.DuplicatedRemoteIdCriterion);

            query = ApplyPathCriterion(query, filter.PathCriterion);

            query = ApplyVideoCodecCriterion(query, filter.VideoCodecCriterion);

            query = ApplyAudioCodecCriterion(query, filter.AudioCodecCriterion);

            if (filter.DateCriterion != null)
            {
                var crit = filter.DateCriterion;
                if (DateOnly.TryParse(crit.Value, out var d1))
                {
                    DateOnly.TryParse(crit.Value2, out var d2);
                    query = crit.Modifier switch
                    {
                        CriterionModifier.Equals => query.Where(s => s.Date == d1),
                        CriterionModifier.NotEquals => query.Where(s => s.Date != d1),
                        CriterionModifier.GreaterThan => query.Where(s => s.Date > d1),
                        CriterionModifier.LessThan => query.Where(s => s.Date < d1),
                        CriterionModifier.Between => query.Where(s => s.Date >= d1 && s.Date <= d2),
                        CriterionModifier.NotBetween => query.Where(s => s.Date < d1 || s.Date > d2),
                        CriterionModifier.IsNull => query.Where(s => s.Date == null),
                        CriterionModifier.NotNull => query.Where(s => s.Date != null),
                        _ => query,
                    };
                }
            }

            if (filter.PerformerFavoriteCriterion != null)
                query = filter.PerformerFavoriteCriterion.Value
                    ? query.Where(s => s.VideoPerformers.Any(sp => sp.Performer!.Favorite))
                    : query.Where(s => !s.VideoPerformers.Any(sp => sp.Performer!.Favorite));

            if (filter.RemoteIdCriterion != null)
            {
                query = filter.RemoteIdCriterion.Modifier switch
                {
                    CriterionModifier.IsNull => query.Where(s => s.RemoteIds.Count == 0),
                    CriterionModifier.NotNull => query.Where(s => s.RemoteIds.Count > 0),
                    _ => query.Where(s => s.RemoteIds.Any(sid => EF.Functions.ILike(sid.Endpoint, $"%{filter.RemoteIdCriterion.Value}%"))),
                };
            }

            query = ApplyIntCriterion(query, filter.RemoteIdCountCriterion, s => s.RemoteIds.Count);

            query = FilterHelpers.ApplyString(query, filter.TitleCriterion, s => s.Title);

            query = FilterHelpers.ApplyString(query, filter.CodeCriterion, s => s.Code);

            query = FilterHelpers.ApplyString(query, filter.DetailsCriterion, s => s.Details);

            query = FilterHelpers.ApplyString(query, filter.DirectorCriterion, s => s.Director);

            // Tag count criterion
            if (filter.TagCountCriterion != null)
                query = ApplyEffectiveTagCountCriterion(query, filter.TagCountCriterion);

            // Resume time criterion
            if (filter.ResumeTimeCriterion != null)
                query = EngagementQueryHelpers.ApplyAffinityDoubleAsIntCriterion(_db, query, currentUserId, AffinityHostType.Video, nameof(UserEntityAffinity.LastPositionSec), filter.ResumeTimeCriterion);

            // Play duration criterion
            if (filter.PlayDurationCriterion != null)
                query = EngagementQueryHelpers.ApplyAffinityDoubleAsIntCriterion(_db, query, currentUserId, AffinityHostType.Video, nameof(UserEntityAffinity.TotalConsumedSec), filter.PlayDurationCriterion);

            // Galleries criterion
            if (filter.GalleriesCriterion != null)
                query = ApplyMultiIdCriterion(query, filter.GalleriesCriterion, s => s.VideoGalleries.Select(sg => sg.GalleryId));

            // URL criterion
            if (filter.UrlCriterion != null)
            {
                var val = filter.UrlCriterion.Value;
                query = filter.UrlCriterion.Modifier switch
                {
                    CriterionModifier.Includes => query.Where(s => s.Urls.Any(u => EF.Functions.ILike(u.Url, $"%{val}%"))),
                    CriterionModifier.Excludes => query.Where(s => !s.Urls.Any(u => EF.Functions.ILike(u.Url, $"%{val}%"))),
                    CriterionModifier.IsNull => query.Where(s => s.Urls.Count == 0),
                    CriterionModifier.NotNull => query.Where(s => s.Urls.Count > 0),
                    _ => query.Where(s => s.Urls.Any(u => EF.Functions.ILike(u.Url, $"%{val}%"))),
                };
            }

            // Timestamp criteria
            query = FilterHelpers.ApplyTimestamp(query, filter.CreatedAtCriterion, s => s.CreatedAt);
            query = FilterHelpers.ApplyTimestamp(query, filter.UpdatedAtCriterion, s => s.UpdatedAt);
            query = EngagementQueryHelpers.ApplyAffinityTimestampCriterion(_db, query, currentUserId, AffinityHostType.Video, nameof(UserEntityAffinity.LastConsumedAt), filter.LastPlayedAtCriterion);

            query = ApplyPerformerOccurrenceTagCriterion(query, filter.PerformerTagsCriterion, GetIncludedPerformerIds(filter));

            // Performer age criterion (age at time of video based on video date and performer birthdate)
            query = ApplyPerformerAgeCriterion(query, filter.PerformerAgeCriterion);

            // Captions criterion (filter by caption content)
            query = FilterHelpers.ApplyString(query, filter.CaptionsCriterion, s => s.Captions);

            query = query.ApplyCustomFieldCriteria(_db, CustomFieldEntityTypes.Video, filter.CustomFieldCriterion, filter.CustomFieldCriteria);

            // Orientation criterion: landscape, portrait, or square based on file dimensions
            if (filter.OrientationCriterion != null)
            {
                var orientation = filter.OrientationCriterion.Value.ToLower();
                query = orientation switch
                {
                    "landscape" => query.Where(s => s.HasLandscapeFiles || s.Files.Any(file => file.Width > file.Height)),
                    "portrait" => query.Where(s => s.HasPortraitFiles || s.Files.Any(file => file.Height > file.Width)),
                    "square" => query.Where(s => s.HasSquareFiles || s.Files.Any(file => file.Width > 0 && file.Width == file.Height)),
                    _ => query,
                };
            }

        return query;
    }

    private IQueryable<Video> ApplyVideoSearch(IQueryable<Video> query, string? search)
    {
        var textQuery = FullTextSearchHelpers.Apply(_db, query, search,
            s => s.Title,
            s => s.Details,
            s => s.Code,
            s => s.FileSearchText,
            s => s.SearchText);

        var normalized = search?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return textQuery;
        var normalizedLower = normalized.ToLowerInvariant();
        // Tags (and tag aliases) match on whole words rather than substrings so a search like
        // "1F" does not also pull in videos tagged "1F1M". Space-padding both sides makes the
        // term match only when it appears as a complete space-delimited word, and works on both
        // PostgreSQL and the SQLite test provider.
        var tagWordTerm = $" {normalizedLower} ";

        var relationalQuery = query.Where(s =>
            (s.Studio != null && s.Studio.Name.ToLower().Contains(normalizedLower)) ||
            s.VideoPerformers.Any(sp => sp.Performer != null && (
                sp.Performer.Name.ToLower().Contains(normalizedLower) ||
                sp.Performer.Aliases.Any(alias => alias.Alias.ToLower().Contains(normalizedLower)))) ||
            s.VideoTags.Any(st => st.Tag != null && (
                (" " + st.Tag.Name.ToLower() + " ").Contains(tagWordTerm) ||
                st.Tag.Aliases.Any(alias => (" " + alias.Alias.ToLower() + " ").Contains(tagWordTerm)))) ||
            s.VideoGalleries.Any(sg => sg.Gallery != null && sg.Gallery.Title != null && sg.Gallery.Title.ToLower().Contains(normalizedLower)) ||
            s.GroupItems.Any(item => item.Group != null && item.Group.Name.ToLower().Contains(normalizedLower)));

        var combined = textQuery.Concat(relationalQuery).Distinct();
        return FullTextSearchHelpers.ApplyFilePathMatch(combined, query, search, s => s.Files);
    }

    private IQueryable<Video> ApplySorting(IQueryable<Video> query, string sort, bool desc, int? seed = null)
    {
        if (sort == "random")
            return SeededRandomOrdering.OrderBy(query, seed, video => video.Id, desc);

        return ApplySortingSwitch(query, sort, desc);
    }

    private IQueryable<Video> ApplySortingSwitch(IQueryable<Video> query, string sort, bool desc)
    {
        if (FilterHelpers.TryParseCustomFieldSort(sort, out _, out _))
            return query.ApplyCustomFieldSort(_db, CustomFieldEntityTypes.Video, sort, desc);

        return sort switch
        {
            "title" => desc ? query.OrderByDescending(s => s.Title) : query.OrderBy(s => s.Title),
            // Null dates sort to bottom: treat null as MinValue so they come last when desc
            "date" => desc ? query.OrderByDescending(s => s.Date ?? DateOnly.MinValue) : query.OrderBy(s => s.Date ?? DateOnly.MinValue),
            "rating" => EngagementQueryHelpers.ApplyRatingSort(_db, query, EngagementQueryHelpers.CurrentUserId(_db), RatingHostType.Video, desc),
            "play_count" => EngagementQueryHelpers.ApplyAffinityIntSort(_db, query, EngagementQueryHelpers.CurrentUserId(_db), AffinityHostType.Video, nameof(UserEntityAffinity.ViewCount), desc),
            "like_counter" => EngagementQueryHelpers.ApplyAffinityIntSort(_db, query, EngagementQueryHelpers.CurrentUserId(_db), AffinityHostType.Video, nameof(UserEntityAffinity.LikeCount), desc),
            "last_like_at" => EngagementQueryHelpers.ApplyAffinityTimestampSort(_db, query, EngagementQueryHelpers.CurrentUserId(_db), AffinityHostType.Video, nameof(UserEntityAffinity.FavoritedAt), desc),
            "organized" => desc ? query.OrderByDescending(s => s.Organized) : query.OrderBy(s => s.Organized),
            "last_played_at" => EngagementQueryHelpers.ApplyAffinityTimestampSort(_db, query, EngagementQueryHelpers.CurrentUserId(_db), AffinityHostType.Video, nameof(UserEntityAffinity.LastConsumedAt), desc),
            "play_duration" => EngagementQueryHelpers.ApplyAffinityDoubleSort(_db, query, EngagementQueryHelpers.CurrentUserId(_db), AffinityHostType.Video, nameof(UserEntityAffinity.TotalConsumedSec), desc),
            "resume_time" => EngagementQueryHelpers.ApplyAffinityDoubleSort(_db, query, EngagementQueryHelpers.CurrentUserId(_db), AffinityHostType.Video, nameof(UserEntityAffinity.LastPositionSec), desc),
            "random" => query.OrderBy(s => s.Id),
            "duration" => desc ? query.OrderByDescending(s => s.MaxDuration) : query.OrderBy(s => s.MaxDuration),
            "file_size" => desc ? query.OrderByDescending(s => s.MaxFileSize) : query.OrderBy(s => s.MaxFileSize),
            "file_mod_time" => ApplyFileModTimeSort(query, desc),
            "file_count" => desc ? query.OrderByDescending(s => s.FileCount) : query.OrderBy(s => s.FileCount),
            "path" => ApplyPathSort(query, desc),
            "resolution" => desc ? query.OrderByDescending(s => s.MaxHeight) : query.OrderBy(s => s.MaxHeight),
            "framerate" => desc ? query.OrderByDescending(s => s.MaxFrameRate) : query.OrderBy(s => s.MaxFrameRate),
            "bitrate" => ApplyBitrateSort(query, desc),
            "phash" => ApplyPhashSort(query, desc),
            "perceptual_similarity" => ApplyPhashSort(query, desc),
            "tag_count" => desc
                ? query.OrderByDescending(s => s.VideoTags.Count)
                : query.OrderBy(s => s.VideoTags.Count),
            "performer_count" => desc
                ? query.OrderByDescending(s => s.VideoPerformers.Count)
                : query.OrderBy(s => s.VideoPerformers.Count),
            "performer_age" => ApplyPerformerAgeSort(query, desc),
            "studio" => ApplyStudioSort(query, desc),
            "code" => ApplyStudioCodeSort(query, desc),
            "studio_code" => ApplyStudioCodeSort(query, desc),
            "created_at" => desc ? query.OrderByDescending(s => s.CreatedAt) : query.OrderBy(s => s.CreatedAt),
            _ => desc ? query.OrderByDescending(s => s.UpdatedAt) : query.OrderBy(s => s.UpdatedAt),
        };
    }

    private static IQueryable<Video> ApplyBitrateCriterion(IQueryable<Video> query, IntCriterion criterion)
    {
        var valStart = (long)criterion.Value * 1000L;
        var valEndExclusive = ((long)criterion.Value + 1L) * 1000L;
        var val2EndExclusive = ((long)(criterion.Value2 ?? criterion.Value) + 1L) * 1000L;

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(video => video.MaxBitRate >= valStart && video.MaxBitRate < valEndExclusive),
            CriterionModifier.NotEquals => query.Where(video => video.MaxBitRate < valStart || video.MaxBitRate >= valEndExclusive),
            CriterionModifier.GreaterThan => query.Where(video => video.MaxBitRate >= valEndExclusive),
            CriterionModifier.LessThan => query.Where(video => video.MaxBitRate < valStart),
            CriterionModifier.Between => query.Where(video => video.MaxBitRate >= valStart && video.MaxBitRate < val2EndExclusive),
            CriterionModifier.NotBetween => query.Where(video => video.MaxBitRate < valStart || video.MaxBitRate >= val2EndExclusive),
            _ => query,
        };
    }

    private static IQueryable<Video> ApplyBitrateSort(IQueryable<Video> query, bool desc)
        => desc
            ? query.OrderByDescending(video => video.MaxBitRate).ThenByDescending(video => video.Id)
            : query.OrderBy(video => video.MaxBitRate).ThenBy(video => video.Id);

    private static IQueryable<Video> ApplyFileModTimeSort(IQueryable<Video> query, bool desc)
    {
        return desc
            ? query.OrderBy(video => video.MaxFileModTime == null ? 1 : 0).ThenByDescending(video => video.MaxFileModTime)
            : query.OrderBy(video => video.MaxFileModTime == null ? 1 : 0).ThenBy(video => video.MaxFileModTime);
    }

    private static IQueryable<Video> ApplyPathSort(IQueryable<Video> query, bool desc)
    {
        return desc
            ? query.OrderBy(video => video.MaxPath == null ? 1 : 0).ThenByDescending(video => video.MaxPath).ThenByDescending(video => video.Id)
            : query.OrderBy(video => video.MinPath).ThenBy(video => video.Id);
    }

    private static IQueryable<Video> ApplyPhashSort(IQueryable<Video> query, bool desc)
    {
        if (desc)
        {
            var descendingQuery = query.Select(video => new
            {
                Video = video,
                Phash = video.Files
                    .SelectMany(file => file.Fingerprints
                        .Where(fingerprint => fingerprint.Type == "phash" && fingerprint.Value != "")
                        .Select(fingerprint => fingerprint.Value))
                    .OrderByDescending(value => value)
                    .FirstOrDefault(),
            });

            return descendingQuery
                .OrderBy(item => item.Phash == null ? 1 : 0)
                .ThenByDescending(item => item.Phash)
                .Select(item => item.Video);
        }

        var ascendingQuery = query.Select(video => new
        {
            Video = video,
            Phash = video.Files
                .SelectMany(file => file.Fingerprints
                    .Where(fingerprint => fingerprint.Type == "phash" && fingerprint.Value != "")
                    .Select(fingerprint => fingerprint.Value))
                .OrderBy(value => value)
                .FirstOrDefault(),
        });

        return ascendingQuery
            .OrderBy(item => item.Phash == null ? 1 : 0)
            .ThenBy(item => item.Phash)
            .Select(item => item.Video);
    }

    private static IQueryable<Video> ApplyStudioSort(IQueryable<Video> query, bool desc)
    {
        var sortQuery = query.Select(video => new
        {
            Video = video,
            StudioName = video.Studio != null ? video.Studio.Name : null,
        });

        return desc
            ? sortQuery.OrderBy(item => item.StudioName == null ? 1 : 0).ThenByDescending(item => item.StudioName).Select(item => item.Video)
            : sortQuery.OrderBy(item => item.StudioName == null ? 1 : 0).ThenBy(item => item.StudioName).Select(item => item.Video);
    }

    private static IQueryable<Video> ApplyStudioCodeSort(IQueryable<Video> query, bool desc)
    {
        var sortQuery = query.Select(video => new
        {
            Video = video,
            Code = video.Code,
        });

        return desc
            ? sortQuery.OrderBy(item => item.Code == null ? 1 : 0).ThenByDescending(item => item.Code).Select(item => item.Video)
            : sortQuery.OrderBy(item => item.Code == null ? 1 : 0).ThenBy(item => item.Code).Select(item => item.Video);
    }

    private static IQueryable<Video> ApplyPerformerAgeSort(IQueryable<Video> query, bool desc)
    {
        if (desc)
        {
            var descendingQuery = query.Select(video => new
            {
                Video = video,
                PerformerAge = video.VideoPerformers
                    .Where(sp => video.Date != null && sp.Performer!.Birthdate != null)
                    .Select(sp => (int?)(
                        video.Date!.Value.Year - sp.Performer!.Birthdate!.Value.Year
                        - ((video.Date!.Value.Month < sp.Performer!.Birthdate!.Value.Month
                            || (video.Date!.Value.Month == sp.Performer!.Birthdate!.Value.Month && video.Date!.Value.Day < sp.Performer!.Birthdate!.Value.Day)) ? 1 : 0)))
                    .Max(),
            });

            return descendingQuery
                .OrderBy(item => item.PerformerAge == null ? 1 : 0)
                .ThenByDescending(item => item.PerformerAge)
                .Select(item => item.Video);
        }

        var ascendingQuery = query.Select(video => new
        {
            Video = video,
            PerformerAge = video.VideoPerformers
                .Where(sp => video.Date != null && sp.Performer!.Birthdate != null)
                .Select(sp => (int?)(
                    video.Date!.Value.Year - sp.Performer!.Birthdate!.Value.Year
                    - ((video.Date!.Value.Month < sp.Performer!.Birthdate!.Value.Month
                        || (video.Date!.Value.Month == sp.Performer!.Birthdate!.Value.Month && video.Date!.Value.Day < sp.Performer!.Birthdate!.Value.Day)) ? 1 : 0)))
                .Min(),
        });

        return ascendingQuery
            .OrderBy(item => item.PerformerAge == null ? 1 : 0)
            .ThenBy(item => item.PerformerAge)
            .Select(item => item.Video);
    }

    private static IQueryable<Video> ApplyPathCriterion(IQueryable<Video> query, StringCriterion? criterion)
    {
        if (criterion == null) return query;

        var value = NormalizePathValue(criterion.Value);
        var pattern = $"%{value}%";
        var exactPattern = $"%\n{value}\n%";

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(s => s.FileSearchText != null && EF.Functions.Like(s.FileSearchText, exactPattern)),
            CriterionModifier.NotEquals => query.Where(s => s.FileSearchText == null || !EF.Functions.Like(s.FileSearchText, exactPattern)),
            CriterionModifier.Includes => query.Where(s => s.FileSearchText != null && EF.Functions.ILike(s.FileSearchText, pattern)),
            CriterionModifier.Excludes => query.Where(s => s.FileSearchText == null || !EF.Functions.ILike(s.FileSearchText, pattern)),
            CriterionModifier.MatchesRegex => query.Where(s => s.FileSearchText != null && Regex.IsMatch(s.FileSearchText, value, RegexOptions.IgnoreCase)),
            CriterionModifier.NotMatchesRegex => query.Where(s => s.FileSearchText == null || !Regex.IsMatch(s.FileSearchText, value, RegexOptions.IgnoreCase)),
            CriterionModifier.IsNull => query.Where(s => s.FileCount == 0 || s.FileSearchText == null || s.FileSearchText == ""),
            CriterionModifier.NotNull => query.Where(s => s.FileCount > 0 && s.FileSearchText != null && s.FileSearchText != ""),
            _ => query,
        };
    }

    private static IQueryable<Video> ApplyFingerprintCriterion(IQueryable<Video> query, StringCriterion? criterion, string fingerprintType)
    {
        if (criterion == null) return query;

        var value = criterion.Value;
        var pattern = $"%{value}%";

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(video => video.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && fingerprint.Value == value))),
            CriterionModifier.NotEquals => query.Where(video => !video.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && fingerprint.Value == value))),
            CriterionModifier.Includes => query.Where(video => video.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && EF.Functions.ILike(fingerprint.Value, pattern)))),
            CriterionModifier.Excludes => query.Where(video => !video.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && EF.Functions.ILike(fingerprint.Value, pattern)))),
            CriterionModifier.MatchesRegex => query.Where(video => video.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && Regex.IsMatch(fingerprint.Value, value, RegexOptions.IgnoreCase)))),
            CriterionModifier.NotMatchesRegex => query.Where(video => !video.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && Regex.IsMatch(fingerprint.Value, value, RegexOptions.IgnoreCase)))),
            CriterionModifier.IsNull => query.Where(video => !video.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && fingerprint.Value != ""))),
            CriterionModifier.NotNull => query.Where(video => video.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && fingerprint.Value != ""))),
            _ => query,
        };
    }

    private static IQueryable<Video> ApplyFingerprintCriterion(IQueryable<Video> query, FingerprintCriterion? criterion)
    {
        if (criterion == null || string.IsNullOrWhiteSpace(criterion.Type)) return query;

        return ApplyFingerprintCriterion(
            query,
            new StringCriterion
            {
                Value = criterion.Value,
                Modifier = criterion.Modifier,
            },
            criterion.Type);
    }

    private IQueryable<Video> ApplyDuplicatedPhashCriterion(IQueryable<Video> query, BoolCriterion criterion)
    {
        var duplicatedQuery = query.Where(video => video.Files
            .SelectMany(file => file.Fingerprints
                .Where(fingerprint => fingerprint.Type == "phash" && fingerprint.Value != "")
                .Select(fingerprint => fingerprint.Value))
            .Any(phash => _db.VideoFiles.Any(otherFile =>
                otherFile.VideoId.HasValue
                && otherFile.VideoId.Value != video.Id
                && otherFile.Fingerprints.Any(otherFingerprint => otherFingerprint.Type == "phash" && otherFingerprint.Value == phash))));

        return criterion.Value ? duplicatedQuery : query.Where(video => !duplicatedQuery.Select(item => item.Id).Contains(video.Id));
    }

    private IQueryable<Video> ApplyDuplicatedTitleCriterion(IQueryable<Video> query, BoolCriterion criterion)
    {
        var duplicatedQuery = query.Where(video => video.Title != null && video.Title != ""
            && _db.Videos.Any(other => other.Id != video.Id
                && other.Title != null
                && other.Title.ToLower() == video.Title.ToLower()));

        return criterion.Value ? duplicatedQuery : query.Where(video => !duplicatedQuery.Select(item => item.Id).Contains(video.Id));
    }

    private IQueryable<Video> ApplyDuplicatedRemoteIdCriterion(IQueryable<Video> query, BoolCriterion criterion)
    {
        var duplicatedQuery = query.Where(video => video.RemoteIds.Any(remoteId =>
            remoteId.RemoteId != ""
            && _db.Set<VideoRemoteId>().Any(other => other.Id != remoteId.Id
                && other.Endpoint == remoteId.Endpoint
                && other.RemoteId == remoteId.RemoteId)));

        return criterion.Value ? duplicatedQuery : query.Where(video => !duplicatedQuery.Select(item => item.Id).Contains(video.Id));
    }

    private static IQueryable<Video> ApplyVideoCodecCriterion(IQueryable<Video> query, StringCriterion? criterion)
    {
        if (criterion == null) return query;

        var value = criterion.Value;
        var pattern = $"%{value}%";

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(s => s.Files.Any(f => f.VideoCodec == value)),
            CriterionModifier.NotEquals => query.Where(s => !s.Files.Any(f => f.VideoCodec == value)),
            CriterionModifier.Includes => query.Where(s => s.Files.Any(f => EF.Functions.ILike(f.VideoCodec, pattern))),
            CriterionModifier.Excludes => query.Where(s => !s.Files.Any(f => EF.Functions.ILike(f.VideoCodec, pattern))),
            CriterionModifier.MatchesRegex => query.Where(s => s.Files.Any(f => Regex.IsMatch(f.VideoCodec ?? string.Empty, value, RegexOptions.IgnoreCase))),
            CriterionModifier.NotMatchesRegex => query.Where(s => !s.Files.Any(f => Regex.IsMatch(f.VideoCodec ?? string.Empty, value, RegexOptions.IgnoreCase))),
            CriterionModifier.IsNull => query.Where(s => !s.Files.Any(f => f.VideoCodec != null && f.VideoCodec != "")),
            CriterionModifier.NotNull => query.Where(s => s.Files.Any(f => f.VideoCodec != null && f.VideoCodec != "")),
            _ => query,
        };
    }

    private static IQueryable<Video> ApplyAudioCodecCriterion(IQueryable<Video> query, StringCriterion? criterion)
    {
        if (criterion == null) return query;

        var value = criterion.Value;
        var pattern = $"%{value}%";

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(s => s.Files.Any(f => f.AudioCodec == value)),
            CriterionModifier.NotEquals => query.Where(s => !s.Files.Any(f => f.AudioCodec == value)),
            CriterionModifier.Includes => query.Where(s => s.Files.Any(f => EF.Functions.ILike(f.AudioCodec, pattern))),
            CriterionModifier.Excludes => query.Where(s => !s.Files.Any(f => EF.Functions.ILike(f.AudioCodec, pattern))),
            CriterionModifier.MatchesRegex => query.Where(s => s.Files.Any(f => Regex.IsMatch(f.AudioCodec ?? string.Empty, value, RegexOptions.IgnoreCase))),
            CriterionModifier.NotMatchesRegex => query.Where(s => !s.Files.Any(f => Regex.IsMatch(f.AudioCodec ?? string.Empty, value, RegexOptions.IgnoreCase))),
            CriterionModifier.IsNull => query.Where(s => !s.Files.Any(f => f.AudioCodec != null && f.AudioCodec != "")),
            CriterionModifier.NotNull => query.Where(s => s.Files.Any(f => f.AudioCodec != null && f.AudioCodec != "")),
            _ => query,
        };
    }

    private static IQueryable<Video> ApplyPerformerAgeCriterion(IQueryable<Video> query, IntCriterion? criterion)
    {
        if (criterion == null) return query;

        var value = criterion.Value;
        var value2 = criterion.Value2 ?? value;

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(s => s.Date != null && s.VideoPerformers.Any(sp =>
                sp.Performer!.Birthdate != null &&
                (s.Date.Value.Year - sp.Performer.Birthdate.Value.Year
                    - ((s.Date.Value.Month < sp.Performer.Birthdate.Value.Month
                        || (s.Date.Value.Month == sp.Performer.Birthdate.Value.Month && s.Date.Value.Day < sp.Performer.Birthdate.Value.Day)) ? 1 : 0)) == value)),
            CriterionModifier.NotEquals => query.Where(s => s.Date != null && s.VideoPerformers.Any(sp =>
                sp.Performer!.Birthdate != null &&
                (s.Date.Value.Year - sp.Performer.Birthdate.Value.Year
                    - ((s.Date.Value.Month < sp.Performer.Birthdate.Value.Month
                        || (s.Date.Value.Month == sp.Performer.Birthdate.Value.Month && s.Date.Value.Day < sp.Performer.Birthdate.Value.Day)) ? 1 : 0)) != value)),
            CriterionModifier.GreaterThan => query.Where(s => s.Date != null && s.VideoPerformers.Any(sp =>
                sp.Performer!.Birthdate != null &&
                (s.Date.Value.Year - sp.Performer.Birthdate.Value.Year
                    - ((s.Date.Value.Month < sp.Performer.Birthdate.Value.Month
                        || (s.Date.Value.Month == sp.Performer.Birthdate.Value.Month && s.Date.Value.Day < sp.Performer.Birthdate.Value.Day)) ? 1 : 0)) > value)),
            CriterionModifier.LessThan => query.Where(s => s.Date != null && s.VideoPerformers.Any(sp =>
                sp.Performer!.Birthdate != null &&
                (s.Date.Value.Year - sp.Performer.Birthdate.Value.Year
                    - ((s.Date.Value.Month < sp.Performer.Birthdate.Value.Month
                        || (s.Date.Value.Month == sp.Performer.Birthdate.Value.Month && s.Date.Value.Day < sp.Performer.Birthdate.Value.Day)) ? 1 : 0)) < value)),
            CriterionModifier.Between => query.Where(s => s.Date != null && s.VideoPerformers.Any(sp =>
                sp.Performer!.Birthdate != null &&
                (s.Date.Value.Year - sp.Performer.Birthdate.Value.Year
                    - ((s.Date.Value.Month < sp.Performer.Birthdate.Value.Month
                        || (s.Date.Value.Month == sp.Performer.Birthdate.Value.Month && s.Date.Value.Day < sp.Performer.Birthdate.Value.Day)) ? 1 : 0)) >= value &&
                (s.Date.Value.Year - sp.Performer.Birthdate.Value.Year
                    - ((s.Date.Value.Month < sp.Performer.Birthdate.Value.Month
                        || (s.Date.Value.Month == sp.Performer.Birthdate.Value.Month && s.Date.Value.Day < sp.Performer.Birthdate.Value.Day)) ? 1 : 0)) <= value2)),
            _ => query,
        };
    }

    private static string NormalizePathValue(string value) => value.Replace("\\", "/");

    // Helper methods for criterion-based filtering
    private static IQueryable<Video> ApplyIntCriterion(IQueryable<Video> query, IntCriterion? criterion, System.Linq.Expressions.Expression<Func<Video, int>> selector)
    {
        if (criterion == null) return query;
        var val = criterion.Value;
        var val2 = criterion.Value2 ?? val;
        var param = selector.Parameters[0];
        var body = selector.Body;

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(System.Linq.Expressions.Expression.Lambda<Func<Video, bool>>(
                System.Linq.Expressions.Expression.Equal(body, System.Linq.Expressions.Expression.Constant(val)), param)),
            CriterionModifier.NotEquals => query.Where(System.Linq.Expressions.Expression.Lambda<Func<Video, bool>>(
                System.Linq.Expressions.Expression.NotEqual(body, System.Linq.Expressions.Expression.Constant(val)), param)),
            CriterionModifier.GreaterThan => query.Where(System.Linq.Expressions.Expression.Lambda<Func<Video, bool>>(
                System.Linq.Expressions.Expression.GreaterThan(body, System.Linq.Expressions.Expression.Constant(val)), param)),
            CriterionModifier.LessThan => query.Where(System.Linq.Expressions.Expression.Lambda<Func<Video, bool>>(
                System.Linq.Expressions.Expression.LessThan(body, System.Linq.Expressions.Expression.Constant(val)), param)),
            CriterionModifier.Between => query.Where(System.Linq.Expressions.Expression.Lambda<Func<Video, bool>>(
                System.Linq.Expressions.Expression.AndAlso(
                    System.Linq.Expressions.Expression.GreaterThanOrEqual(body, System.Linq.Expressions.Expression.Constant(val)),
                    System.Linq.Expressions.Expression.LessThanOrEqual(body, System.Linq.Expressions.Expression.Constant(val2))), param)),
            CriterionModifier.NotBetween => query.Where(System.Linq.Expressions.Expression.Lambda<Func<Video, bool>>(
                System.Linq.Expressions.Expression.OrElse(
                    System.Linq.Expressions.Expression.LessThan(body, System.Linq.Expressions.Expression.Constant(val)),
                    System.Linq.Expressions.Expression.GreaterThan(body, System.Linq.Expressions.Expression.Constant(val2))), param)),
            _ => query,
        };
    }

    private sealed record ExpandedHierarchicalTagCriterion(MultiIdCriterion Criterion, IReadOnlyList<int[]> ValueGroups);

    private async Task<ExpandedHierarchicalTagCriterion> ExpandHierarchicalTagCriterionAsync(MultiIdCriterion criterion, CancellationToken ct)
    {
        var relationships = await _db.Set<TagParent>()
            .AsNoTracking()
            .Select(tp => new { tp.ParentId, tp.ChildId })
            .ToListAsync(ct);

        var childrenByParent = relationships
            .GroupBy(tp => tp.ParentId)
            .ToDictionary(group => group.Key, group => group.Select(tp => tp.ChildId).ToArray());

        var valueGroups = criterion.Value
            .Distinct()
            .Select(tagId => ExpandTagGroup(tagId, childrenByParent))
            .ToList();

        var flatValue = valueGroups.SelectMany(group => group).Distinct().ToList();
        var flatExcludes = criterion.Excludes?
            .Distinct()
            .SelectMany(tagId => ExpandTagGroup(tagId, childrenByParent))
            .Distinct()
            .ToList();

        return new ExpandedHierarchicalTagCriterion(
            new MultiIdCriterion
            {
                Value = flatValue,
                Modifier = criterion.Modifier,
                Excludes = flatExcludes is { Count: > 0 } ? flatExcludes : null,
                Depth = criterion.Depth,
            },
            valueGroups);
    }

    private static int[] ExpandTagGroup(int rootTagId, IReadOnlyDictionary<int, int[]> childrenByParent)
    {
        var expanded = new HashSet<int> { rootTagId };
        var queue = new Queue<int>();
        queue.Enqueue(rootTagId);

        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            if (!childrenByParent.TryGetValue(parentId, out var childIds))
            {
                continue;
            }

            foreach (var childId in childIds)
            {
                if (expanded.Add(childId))
                    queue.Enqueue(childId);
            }
        }

        return expanded.ToArray();
    }

    private static IQueryable<Video> ApplyMultiIdCriterion(
        IQueryable<Video> query,
        MultiIdCriterion? criterion,
        System.Linq.Expressions.Expression<Func<Video, IEnumerable<int>>> idsSelector,
        IReadOnlyList<int[]>? valueGroups = null)
        => MultiIdCriterionQueryHelper.Apply(query, criterion, idsSelector, valueGroups);

    private IQueryable<Video> ApplyVideoTagCriterion(IQueryable<Video> query, MultiIdCriterion? criterion, IReadOnlyList<int[]>? valueGroups = null)
    {
        if (criterion == null)
            return query;

        var effectiveTags = EffectiveHostTagQuery.ForHostType(_db, AffinityHostType.Video);

        if (criterion.Modifier == CriterionModifier.IsNull)
            return query.Where(video => !effectiveTags.Any(tag => tag.HostId == video.Id));

        if (criterion.Modifier == CriterionModifier.NotNull)
            return query.Where(video => effectiveTags.Any(tag => tag.HostId == video.Id));

        var groups = valueGroups?.Where(group => group.Length > 0).ToArray()
            ?? criterion.Value.Where(tagId => tagId > 0).Select(tagId => new[] { tagId }).ToArray();
        if (groups.Length > 0)
        {
            var ids = groups.SelectMany(group => group).Distinct().ToArray();
            query = criterion.Modifier switch
            {
                CriterionModifier.Excludes => ApplyVideoTagNone(query, ids),
                CriterionModifier.ExcludesAll => ApplyVideoTagExcludesAll(query, groups),
                CriterionModifier.IncludesAll => ApplyVideoTagIncludesAll(query, groups),
                _ => ApplyVideoTagAny(query, ids),
            };
        }

        // Excluded tags arrive in a separate list (the filter UI emits `excludes` alongside an Includes
        // modifier rather than flipping the modifier), so apply them independently of the include set —
        // including the exclude-only case where there are no included tags at all. Mirrors the
        // include/exclude split used by ApplyPerformerOccurrenceTagCriterion and the shared MultiId helper.
        if (criterion.Excludes is { Count: > 0 })
            query = ApplyVideoTagNone(query, criterion.Excludes);

        return query;
    }

    private IQueryable<Video> ApplyVideoTagIncludesAll(IQueryable<Video> query, IReadOnlyList<int[]> groups)
    {
        foreach (var group in groups)
        {
            query = ApplyVideoTagAny(query, group);
        }

        return query;
    }

    private IQueryable<Video> ApplyVideoTagExcludesAll(IQueryable<Video> query, IReadOnlyList<int[]> groups)
    {
        var matchingAll = query;
        foreach (var group in groups)
        {
            matchingAll = ApplyVideoTagAny(matchingAll, group);
        }

        return query.Where(video => !matchingAll.Select(match => match.Id).Contains(video.Id));
    }

    private IQueryable<Video> ApplyVideoTagAny(IQueryable<Video> query, IReadOnlyCollection<int> tagIds)
    {
        var ids = tagIds.Where(tagId => tagId > 0).Distinct().ToArray();
        if (ids.Length == 0)
            return query;

        var effectiveTags = EffectiveHostTagQuery.ForHostType(_db, AffinityHostType.Video);
        return query.Where(video => effectiveTags.Any(tag => tag.HostId == video.Id && ids.Contains(tag.TagId)));
    }

    private IQueryable<Video> ApplyVideoTagNone(IQueryable<Video> query, IReadOnlyCollection<int> tagIds)
    {
        var ids = tagIds.Where(tagId => tagId > 0).Distinct().ToArray();
        if (ids.Length == 0)
            return query;

        var effectiveTags = EffectiveHostTagQuery.ForHostType(_db, AffinityHostType.Video);
        return query.Where(video => !effectiveTags.Any(tag => tag.HostId == video.Id && ids.Contains(tag.TagId)));
    }

    private IQueryable<Video> ApplyEffectiveTagCountCriterion(IQueryable<Video> query, IntCriterion criterion)
    {
        var effectiveTags = EffectiveHostTagQuery.ForHostType(_db, AffinityHostType.Video);
        return ApplyIntCriterion(query, criterion, video => effectiveTags
            .Where(tag => tag.HostId == video.Id)
            .Select(tag => tag.TagId)
            .Distinct()
            .Count());
    }

    private static int[] GetIncludedPerformerIds(VideoFilter filter)
    {
        var ids = new HashSet<int>();
        if (filter.PerformerIds is { Count: > 0 })
        {
            foreach (var performerId in filter.PerformerIds.Where(id => id > 0))
                ids.Add(performerId);
        }

        if (filter.PerformersCriterion?.Value is { Count: > 0 }
            && filter.PerformersCriterion.Modifier is CriterionModifier.Includes or CriterionModifier.IncludesAll)
        {
            foreach (var performerId in filter.PerformersCriterion.Value.Where(id => id > 0))
                ids.Add(performerId);
        }

        return ids.ToArray();
    }

    private IQueryable<Video> ApplyPerformerOccurrenceTagCriterion(IQueryable<Video> query, MultiIdCriterion? criterion, IReadOnlyCollection<int> performerIds)
    {
        if (criterion == null)
            return query;

        var tagIds = criterion.Value.Where(tagId => tagId > 0).Distinct().ToArray();
        var excludedTagIds = criterion.Excludes?.Where(tagId => tagId > 0).Distinct().ToArray() ?? [];
        if (tagIds.Length == 0 && excludedTagIds.Length == 0)
            return query;

        var scopedApplications = _db.TagApplications.AsNoTracking()
            .Where(application => application.HostType == AffinityHostType.Video
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
                CriterionModifier.Excludes => query.Where(video => !scopedApplications.Any(application => application.HostId == video.Id && tagIds.Contains(application.TagId))),
                CriterionModifier.ExcludesAll => ApplyPerformerOccurrenceTagExcludesAll(query, scopedApplications, tagIds),
                CriterionModifier.IncludesAll => ApplyPerformerOccurrenceTagIncludesAll(query, scopedApplications, tagIds),
                _ => query.Where(video => scopedApplications.Any(application => application.HostId == video.Id && tagIds.Contains(application.TagId))),
            };
        }

        if (excludedTagIds.Length > 0)
        {
            query = query.Where(video => !scopedApplications.Any(application => application.HostId == video.Id && excludedTagIds.Contains(application.TagId)));
        }

        return query;
    }

    private static IQueryable<Video> ApplyPerformerOccurrenceTagIncludesAll(IQueryable<Video> query, IQueryable<TagApplication> applications, IReadOnlyCollection<int> tagIds)
    {
        foreach (var tagId in tagIds)
        {
            query = query.Where(video => applications.Any(application => application.HostId == video.Id && application.TagId == tagId));
        }

        return query;
    }

    private static IQueryable<Video> ApplyPerformerOccurrenceTagExcludesAll(IQueryable<Video> query, IQueryable<TagApplication> applications, IReadOnlyCollection<int> tagIds)
    {
        var matchingAll = query;
        foreach (var tagId in tagIds)
        {
            matchingAll = matchingAll.Where(video => applications.Any(application => application.HostId == video.Id && application.TagId == tagId));
        }

        return query.Where(video => !matchingAll.Select(match => match.Id).Contains(video.Id));
    }

    private IQueryable<Video> ApplyTagDurationCriterion(IQueryable<Video> query, TagDurationCriterion? criterion)
    {
        foreach (var clause in GetTagDurationClauses(criterion))
        {
            query = ApplyTagDurationClause(query, clause);
        }

        return query;
    }

    private static IReadOnlyList<TagDurationClause> GetTagDurationClauses(TagDurationCriterion? criterion)
    {
        if (criterion == null)
            return [];

        var clauses = criterion.Clauses.Where(IsTagDurationClauseValid).ToArray();
        if (clauses.Length > 0)
            return clauses;

        return IsTagDurationClauseValid(criterion) ? [criterion] : [];
    }

    private static bool IsTagDurationClauseValid(TagDurationClause clause)
        => clause.TagId > 0 && clause.Value.HasValue;

    private IQueryable<Video> ApplyTagDurationClause(IQueryable<Video> query, TagDurationClause criterion)
    {
        if (!IsTagDurationClauseValid(criterion))
            return query;

        var value = criterion.Value.GetValueOrDefault();
        var value2 = criterion.Value2 ?? value;

        var applications = _db.TagApplications.AsNoTracking()
            .Where(application => application.HostType == AffinityHostType.Video && application.TagId == criterion.TagId);

        var contextMode = criterion.ContextMode?.Trim().ToLowerInvariant();
        if (contextMode == "host")
            applications = applications.Where(application => application.ContextType == null && application.ContextId == null);
        else if (contextMode == "context")
            applications = applications.Where(application => application.ContextType != null && application.ContextId != null);

        var contextType = criterion.ContextType?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(contextType))
            applications = applications.Where(application => application.ContextType == contextType);

        var usePercent = string.Equals(criterion.Unit, "percent", StringComparison.OrdinalIgnoreCase);
        var durationQuery = usePercent
            ? applications
                .GroupBy(application => application.HostId)
                .Select(group => new
                {
                    HostId = group.Key,
                    Value = group.Max(application => application.TotalDurationSec != null && application.HostDurationSec != null && application.HostDurationSec > 0
                        ? application.TotalDurationSec * 100d / application.HostDurationSec
                        : null),
                })
            : applications
                .GroupBy(application => application.HostId)
                .Select(group => new
                {
                    HostId = group.Key,
                    Value = group.Max(application => application.TotalDurationSec),
                });

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(video => durationQuery.Any(row => row.HostId == video.Id && row.Value != null && row.Value == value)),
            CriterionModifier.NotEquals => query.Where(video => durationQuery.Any(row => row.HostId == video.Id && row.Value != null && row.Value != value)),
            CriterionModifier.GreaterThan => query.Where(video => durationQuery.Any(row => row.HostId == video.Id && row.Value != null && row.Value > value)),
            CriterionModifier.LessThan => query.Where(video => durationQuery.Any(row => row.HostId == video.Id && row.Value != null && row.Value < value)),
            CriterionModifier.Between => query.Where(video => durationQuery.Any(row => row.HostId == video.Id && row.Value != null && row.Value >= value && row.Value <= value2)),
            CriterionModifier.NotBetween => query.Where(video => durationQuery.Any(row => row.HostId == video.Id && row.Value != null && (row.Value < value || row.Value > value2))),
            _ => query,
        };
    }
}
