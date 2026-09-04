using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using PermissionKeys = Cove.Core.Auth.Permissions;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Core.Common;
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
            .Include(s => s.ChildVideos)
            .Include(s => s.Files).ThenInclude(f => f.Fingerprints)
            .Include(s => s.Files).ThenInclude(f => f.Captions)
            .Include(s => s.Files).ThenInclude(f => f.ParentFolder)
            .Include(s => s.ParentVideo).ThenInclude(parent => parent!.Files).ThenInclude(f => f.Fingerprints)
            .Include(s => s.ParentVideo).ThenInclude(parent => parent!.Files).ThenInclude(f => f.Captions)
            .Include(s => s.ParentVideo).ThenInclude(parent => parent!.Files).ThenInclude(f => f.ParentFolder)
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
        if (_db.Entry(entity).State == EntityState.Detached)
        {
            throw new InvalidOperationException(
                "VideoRepository.UpdateAsync requires an entity tracked by this repository's CoveContext.");
        }

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

    internal async Task<IQueryable<Video>> BuildFilteredQueryAsync(
        VideoFilter? filter,
        FindFilter? findFilter,
        bool includeRelatedFilters = true,
        bool allowReadScopeOptimization = true,
        CancellationToken ct = default,
        FilterExpression<VideoFilter>? expression = null)
    {
        var currentPrincipal = _db.CurrentPrincipalForReadOptimization;
        var hasRelatedFilter = filter?.PerformerFilterCriterion != null
            || FilterExpressionQuery.Contains(expression, leaf => leaf.PerformerFilterCriterion != null);
        var readScopePlan = !allowReadScopeOptimization || hasRelatedFilter
            ? null
            : await ReadScopeListOptimization.TryBuildPlanAsync<Video>(
                _db,
                EntityKinds.Video,
                currentPrincipal?.Has(PermissionKeys.VideosRead) == true,
                currentPrincipal?.ReadGrantedEntityKinds.Contains(EntityKinds.Video) == true,
                ct);

        // Build a lightweight filter-only query (no Includes) for COUNT and filter predicates
        var filterQuery = (readScopePlan ?? new ReadScopeRootPlan<Video>(false, null)).Apply(_db.Videos.AsQueryable());

        async Task<IQueryable<Video>> ApplyLeafAsync(IQueryable<Video> query, VideoFilter leaf, bool applyPerformerCriterion = true)
        {
            ExpandedHierarchyCriterion? expandedTags = null;
            if (HierarchicalCriterionExpander.RequiresExpansion(leaf.TagsCriterion))
            {
                expandedTags = await HierarchicalCriterionExpander.ExpandTagsAsync(_db, leaf.TagsCriterion!, ct);
                leaf.TagsCriterion = expandedTags.Criterion;
            }
            ExpandedHierarchyCriterion? expandedStudios = null;
            if (HierarchicalCriterionExpander.RequiresExpansion(leaf.StudiosCriterion))
            {
                expandedStudios = await HierarchicalCriterionExpander.ExpandStudiosAsync(_db, leaf.StudiosCriterion!, ct);
                leaf.StudiosCriterion = expandedStudios.Criterion;
            }

            query = ApplyFilters(query, leaf, expandedTags?.ValueGroups, expandedTags?.RequiredIdGroups, expandedStudios?.ValueGroups, expandedStudios?.RequiredIdGroups);
            return includeRelatedFilters && applyPerformerCriterion
                ? await RelatedFilterQuery.ApplyToVideosAsync(_db, query, leaf.PerformerFilterCriterion, ct)
                : query;
        }

        async Task<IQueryable<Video>> ApplyExpressionNodeAsync(IQueryable<Video> input, FilterExpressionNode<VideoFilter> node)
            => node.Filter != null ? await ApplyLeafAsync(input, node.Filter) : await ApplyExpressionAsync(input, node.Group!);

        async Task<IQueryable<Video>> ApplyExpressionAsync(IQueryable<Video> input, FilterExpression<VideoFilter> group)
        {
            if (group.Operator == FilterExpressionOperator.And)
            {
                var distinctPerformerScope = group.DistinctRelatedMatches
                    || (group.RelatedScope?.MatchMode == RelatedScopeMatchMode.Distinct
                        && string.Equals(group.RelatedScope.FilterKey, nameof(VideoFilter.PerformerFilterCriterion), StringComparison.OrdinalIgnoreCase));
                var distinctCriteria = includeRelatedFilters && distinctPerformerScope
                    ? group.Children
                        .Where(child => child.Filter?.PerformerFilterCriterion is { Mode: RelatedFilterMode.AtLeastOne, Exclude: false })
                        .Select(child => child.Filter!.PerformerFilterCriterion!)
                        .ToArray()
                    : [];
                if (distinctCriteria.Length > 8)
                    throw new ArgumentException("Distinct related-performer groups may not contain more than 8 matching conditions.", nameof(expression));
                var current = distinctCriteria.Length > 1
                    ? await RelatedFilterQuery.ApplyDistinctVideoPerformersAsync(_db, input, distinctCriteria, ct)
                    : input;
                foreach (var child in group.Children)
                {
                    var handledDistinctCriterion = distinctCriteria.Length > 1
                        && child.Filter?.PerformerFilterCriterion is { Mode: RelatedFilterMode.AtLeastOne, Exclude: false };
                    current = child.Filter != null
                        ? await ApplyLeafAsync(current, child.Filter, !handledDistinctCriterion)
                        : await ApplyExpressionAsync(current, child.Group!);
                }
                return current;
            }
            if (group.Operator == FilterExpressionOperator.Not)
                return input.Except(await ApplyExpressionNodeAsync(input, group.Children[0]));
            if (group.Operator == FilterExpressionOperator.JustOne)
            {
                IQueryable<Video>? seen = null;
                IQueryable<Video>? exactlyOne = null;
                foreach (var child in group.Children)
                {
                    var branch = await ApplyExpressionNodeAsync(input, child);
                    if (seen == null) { seen = branch; exactlyOne = branch; continue; }
                    exactlyOne = exactlyOne!.Except(branch).Union(branch.Except(seen));
                    seen = seen.Union(branch);
                }
                return exactlyOne ?? input;
            }
            IQueryable<Video>? union = null;
            foreach (var child in group.Children)
            {
                var branch = await ApplyExpressionNodeAsync(input, child);
                union = union == null ? branch : union.Union(branch);
            }
            return union ?? input;
        }

        if (filter != null)
            filterQuery = await ApplyLeafAsync(filterQuery, filter);
        if (!FilterExpressionQuery.TryValidate(expression, out var expressionError))
            throw new ArgumentException(expressionError, nameof(expression));
        if (expression is { Children.Count: > 0 })
            filterQuery = await ApplyExpressionAsync(filterQuery, expression);

        filterQuery = ApplyVideoSearch(filterQuery, findFilter?.Q);

        return filterQuery;
    }

    public async Task<(IReadOnlyList<Video> Items, int TotalCount)> FindAsync(VideoFilter? filter, FindFilter? findFilter, CancellationToken ct = default, FilterExpression<VideoFilter>? expression = null)
    {
        var filterQuery = await BuildFilteredQueryAsync(filter, findFilter, ct: ct, expression: expression);

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
        var multiSortRegistry = CreateMultiSortRegistry();
        var sortClauses = multiSortRegistry.Normalize(findFilter?.Sorts);
        var hasExplicitSort = sortClauses.Count > 0 || !string.IsNullOrWhiteSpace(findFilter?.Sort);
        var primarySortClause = sortClauses.FirstOrDefault();
        var sort = primarySortClause?.Key ?? findFilter?.Sort ?? "updated_at";
        var desc = primarySortClause?.Direction == Core.Enums.SortDirection.Desc
            || (primarySortClause == null && findFilter?.Direction == Core.Enums.SortDirection.Desc);
        filterQuery = sortClauses.Count > 1
            ? ApplyMultiSorting(filterQuery, sortClauses, multiSortRegistry)
            : ApplySorting(filterQuery, sort, desc, findFilter?.Seed);
        if (!hasExplicitSort || FullTextSearchHelpers.IsRelevanceSort(sort))
            filterQuery = ApplyVideoRelevanceOrdering(filterQuery, findFilter?.Q);

        var page = findFilter?.Page ?? 1;
        var pagedIds = await filterQuery
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .Select(s => s.Id)
            .ToListAsync(ct);

        if (pagedIds.Count == 0)
            return (Array.Empty<Video>(), totalCount);

        // Load full entities only for the paged IDs.
        // Deliberately narrower than GetByIdWithRelationsAsync: the list DTO (VideosController.MapListToDto)
        // emits empty fingerprint/caption arrays and always sources tags from EffectiveTagDtoLoader, so
        // including Files.Fingerprints or the VideoTags -> Tag -> TagGroup chain here costs two extra
        // split-query round trips per page whose rows are then discarded.
        var items = await _db.Videos
            .Include(s => s.Studio)
            .Include(s => s.Urls)
            .Include(s => s.VideoPerformers).ThenInclude(sp => sp.Performer)
            .Include(s => s.VideoGalleries).ThenInclude(sg => sg.Gallery)
            .Include(s => s.GroupItems).ThenInclude(item => item.Group)
            .Include(s => s.Files)
            .Include(s => s.ParentVideo).ThenInclude(parent => parent!.Files)
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

    public async Task<VideoAggregate> AggregateAsync(VideoFilter? filter, FindFilter? findFilter, CancellationToken ct = default, FilterExpression<VideoFilter>? expression = null)
    {
        var query = await BuildFilteredQueryAsync(filter, findFilter, ct: ct, expression: expression);

        return await query.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(group => new VideoAggregate(group.Count(), group.Sum(video => video.MaxDuration), group.Sum(video => video.MaxFileSize)))
            .SingleOrDefaultAsync(ct)
            ?? new VideoAggregate(0, 0, 0);
    }

    private IQueryable<Video> ApplyFilters(IQueryable<Video> query, VideoFilter? filter, IReadOnlyList<int[]>? hierarchicalTagGroups = null, IReadOnlyList<int[]>? requiredTagGroups = null, IReadOnlyList<int[]>? hierarchicalStudioGroups = null, IReadOnlyList<int[]>? requiredStudioGroups = null)
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
            query = EngagementQueryHelpers.ApplyFavoriteCriterion(_db, query, currentUserId, AffinityHostType.Video, filter.FavoriteCriterion);
            query = EngagementQueryHelpers.ApplyAffinityIntCriterion(_db, query, currentUserId, AffinityHostType.Video, nameof(UserEntityAffinity.LikeCount), filter.LikeCounterCriterion);
            query = EngagementQueryHelpers.ApplyFavoriteCriterion(_db, query, currentUserId, AffinityHostType.Video, filter.FavoriteCriterion);
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

            query = ApplyVideoTagCriterion(query, filter.TagsCriterion, hierarchicalTagGroups, requiredTagGroups);
            query = ApplyTagDurationCriterion(query, filter.TagDurationCriterion);
            query = ApplyMultiIdCriterion(query, filter.PerformersCriterion, s => s.PerformerIds);

            query = FilterHelpers.ApplyStudioCriterion(query, filter.StudiosCriterion, s => s.StudioId, hierarchicalStudioGroups, requiredStudioGroups);

            query = ApplyMultiIdCriterion(query, filter.GroupsCriterion, s => s.GroupItems.Select(item => item.GroupId));

            if (filter.OrganizedCriterion != null)
                query = query.Where(s => s.Organized == filter.OrganizedCriterion.Value);

            if (filter.IsVrCriterion != null)
                query = query.Where(s => s.IsVr == filter.IsVrCriterion.Value);

            if (filter.HasSegmentsCriterion != null)
            {
                var hasSegments = filter.HasSegmentsCriterion.Value;
                query = hasSegments
                    ? query.Where(video => _db.Segments.Any(segment =>
                        segment.HostType == SegmentHostType.Video && segment.HostId == video.Id))
                    : query.Where(video => !_db.Segments.Any(segment =>
                        segment.HostType == SegmentHostType.Video && segment.HostId == video.Id));
            }

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
                // Null checks carry no date value, so they must be handled before parsing.
                if (crit.Modifier == CriterionModifier.IsNull)
                {
                    query = query.Where(s => s.Date == null);
                }
                else if (crit.Modifier == CriterionModifier.NotNull)
                {
                    query = query.Where(s => s.Date != null);
                }
                else if (DateOnly.TryParse(crit.Value, out var d1))
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
                        _ => query,
                    };
                }
            }

            if (filter.PerformerFavoriteCriterion != null)
                query = filter.PerformerFavoriteCriterion.Value
                    ? query.Where(s => s.VideoPerformers.Any(sp => sp.Performer!.Favorite))
                    : query.Where(s => !s.VideoPerformers.Any(sp => sp.Performer!.Favorite));

            query = FilterHelpers.ApplyRemoteId(query, filter.RemoteIdCriterion, filter.RemoteIdValueCriterion, video => video.RemoteIds, remoteId => remoteId.Endpoint, remoteId => remoteId.RemoteId);

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
            query = FilterHelpers.ApplyStringCollection(query, filter.UrlCriterion, s => s.Urls.Select(u => u.Url));

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

    internal IQueryable<Video> ApplyVideoSearch(IQueryable<Video> query, string? search)
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

        // Build relationship matches from the relationship tables toward videos. Starting from every
        // video and evaluating correlated Any expressions makes common tag searches revisit the same
        // tag and alias rows hundreds of thousands of times. Projecting only IDs also keeps UNION ALL
        // narrow and lets the final IN predicate provide set semantics without DISTINCT over video rows.
        var matchingIds = textQuery.Select(video => video.Id)
            .Concat(_db.Studios
                .Where(studio => studio.Name.ToLower().Contains(normalizedLower))
                .SelectMany(studio => studio.Videos.Select(video => video.Id)))
            .Concat(_db.Set<VideoPerformer>()
                .Where(videoPerformer => videoPerformer.Performer != null && (
                    videoPerformer.Performer.Name.ToLower().Contains(normalizedLower) ||
                    videoPerformer.Performer.Aliases.Any(alias => alias.Alias.ToLower().Contains(normalizedLower))))
                .Select(videoPerformer => videoPerformer.VideoId))
            .Concat(_db.Set<VideoTag>()
                .Where(videoTag => videoTag.Tag != null && (
                    (" " + videoTag.Tag.Name.ToLower() + " ").Contains(tagWordTerm) ||
                    videoTag.Tag.Aliases.Any(alias => (" " + alias.Alias.ToLower() + " ").Contains(tagWordTerm))))
                .Select(videoTag => videoTag.VideoId))
            .Concat(_db.Set<VideoGallery>()
                .Where(videoGallery => videoGallery.Gallery != null
                    && videoGallery.Gallery.Title != null
                    && videoGallery.Gallery.Title.ToLower().Contains(normalizedLower))
                .Select(videoGallery => videoGallery.VideoId))
            .Concat(_db.Set<GroupItem>()
                .Where(item => item.VideoId != null
                    && item.Group != null
                    && item.Group.Name.ToLower().Contains(normalizedLower))
                .Select(item => item.VideoId!.Value));

        var tokens = FullTextSearchHelpers.TokenizeSearchTerms(normalized);
        if (tokens.Count > 0)
        {
            var matchingFiles = _db.VideoFiles.Where(file => file.VideoId != null);
            foreach (var token in tokens)
                matchingFiles = matchingFiles.Where(file => file.Path.ToLower().Contains(token));
            matchingIds = matchingIds.Concat(matchingFiles.Select(file => file.VideoId!.Value));
        }

        return query.Where(video => matchingIds.Contains(video.Id));
    }

    internal IQueryable<Video> ApplyVideoRelevanceOrdering(IQueryable<Video> query, string? search)
    {
        var normalized = search?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return query;

        var lower = normalized.ToLowerInvariant();
        var exactRelationshipIds = _db.Set<VideoPerformer>()
            .Where(link => link.Performer != null && (
                link.Performer.Name.ToLower() == lower
                || link.Performer.Aliases.Any(alias => alias.Alias.ToLower() == lower)))
            .Select(link => link.VideoId)
            .Concat(_db.Set<VideoTag>()
                .Where(link => link.Tag != null && (
                    link.Tag.Name.ToLower() == lower
                    || link.Tag.Aliases.Any(alias => alias.Alias.ToLower() == lower)))
                .Select(link => link.VideoId))
            .Concat(_db.Studios
                .Where(studio => studio.Name.ToLower() == lower)
                .SelectMany(studio => studio.Videos.Select(video => video.Id)))
            .Concat(_db.Set<VideoGallery>()
                .Where(link => link.Gallery != null && link.Gallery.Title != null && link.Gallery.Title.ToLower() == lower)
                .Select(link => link.VideoId))
            .Concat(_db.Set<GroupItem>()
                .Where(item => item.VideoId != null && item.Group != null && item.Group.Name.ToLower() == lower)
                .Select(item => item.VideoId!.Value));

        var relationshipIds = _db.Set<VideoPerformer>()
            .Where(link => link.Performer != null && (
                link.Performer.Name.ToLower().Contains(lower)
                || link.Performer.Aliases.Any(alias => alias.Alias.ToLower().Contains(lower))))
            .Select(link => link.VideoId)
            .Concat(_db.Set<VideoTag>()
                .Where(link => link.Tag != null && (
                    (" " + link.Tag.Name.ToLower() + " ").Contains(" " + lower + " ")
                    || link.Tag.Aliases.Any(alias => (" " + alias.Alias.ToLower() + " ").Contains(" " + lower + " "))))
                .Select(link => link.VideoId))
            .Concat(_db.Studios
                .Where(studio => studio.Name.ToLower().Contains(lower))
                .SelectMany(studio => studio.Videos.Select(video => video.Id)))
            .Concat(_db.Set<VideoGallery>()
                .Where(link => link.Gallery != null && link.Gallery.Title != null && link.Gallery.Title.ToLower().Contains(lower))
                .Select(link => link.VideoId))
            .Concat(_db.Set<GroupItem>()
                .Where(item => item.VideoId != null && item.Group != null && item.Group.Name.ToLower().Contains(lower))
                .Select(item => item.VideoId!.Value));

        var tokens = FullTextSearchHelpers.TokenizeSearchTerms(normalized);
        var matchingFiles = tokens.Count > 0
            ? _db.VideoFiles.Where(file => file.VideoId != null)
            : _db.VideoFiles.Where(_ => false);
        foreach (var token in tokens)
            matchingFiles = matchingFiles.Where(file => file.Path.ToLower().Contains(token));

        return FullTextSearchHelpers.OrderByExactThenRelevance(
            _db,
            query,
            normalized,
            video => video.Title,
            [exactRelationshipIds, relationshipIds, matchingFiles.Select(file => file.VideoId!.Value)],
            [video => video.Title, video => video.Details, video => video.Code, video => video.Director]);
    }

    private IQueryable<Video> ApplySorting(IQueryable<Video> query, string sort, bool desc, int? seed = null)
    {
        if (sort == "random")
            return SeededRandomOrdering.OrderBy(query, seed, video => video.Id, desc);

        return ApplySortingSwitch(query, sort, desc);
    }

    private static readonly HashSet<string> AffinityMultiSortKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "play_count", "like_counter", "last_played_at", "play_duration", "resume_time",
    };

    private static CompoundSortRegistry<Video> CreateMultiSortRegistry()
        => new(new Dictionary<string, Action<CompoundSortQuery<Video>, bool>>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = (compound, desc) =>
            {
                compound.Append(video => video.Title == null ? 1 : 0, false);
                compound.Append(video => video.Title, desc);
            },
            ["rating"] = (compound, desc) => compound.AppendRating(desc),
            ["play_count"] = (compound, desc) => compound.AppendAffinityInt(nameof(UserEntityAffinity.ViewCount), desc),
            ["like_counter"] = (compound, desc) => compound.AppendAffinityInt(nameof(UserEntityAffinity.LikeCount), desc),
            ["last_like_at"] = (compound, desc) => compound.AppendInteractionTimestamp(desc),
            ["last_played_at"] = (compound, desc) => compound.AppendAffinityTimestamp(nameof(UserEntityAffinity.LastConsumedAt), desc),
            ["play_duration"] = (compound, desc) => compound.AppendAffinityDouble(nameof(UserEntityAffinity.TotalConsumedSec), desc),
            ["resume_time"] = (compound, desc) => compound.AppendAffinityDouble(nameof(UserEntityAffinity.LastPositionSec), desc),
            ["date"] = (compound, desc) =>
            {
                compound.Append(video => video.Date == null ? 1 : 0, false);
                compound.Append(video => video.Date, desc);
            },
            ["organized"] = (compound, desc) => compound.Append(video => video.Organized, desc),
            ["duration"] = (compound, desc) => compound.Append(video => video.MaxDuration, desc),
            ["file_size"] = (compound, desc) => compound.Append(video => video.MaxFileSize, desc),
            ["file_mod_time"] = (compound, desc) =>
            {
                compound.Append(video => video.MaxFileModTime == null ? 1 : 0, false);
                compound.Append(video => video.MaxFileModTime, desc);
            },
            ["file_count"] = (compound, desc) => compound.Append(video => video.FileCount, desc),
            ["path"] = (compound, desc) =>
            {
                compound.Append(video => desc ? video.MaxPath == null ? 1 : 0 : video.MinPath == null ? 1 : 0, false);
                compound.Append(video => desc ? video.MaxPath : video.MinPath, desc);
            },
            ["resolution"] = (compound, desc) => compound.Append(video => video.MaxHeight, desc),
            ["framerate"] = (compound, desc) => compound.Append(video => video.MaxFrameRate, desc),
            ["bitrate"] = (compound, desc) => compound.Append(video => video.MaxBitRate, desc),
            ["tag_count"] = (compound, desc) => compound.Append(video => video.VideoTags.Count, desc),
            ["performer_count"] = (compound, desc) => compound.Append(video => video.VideoPerformers.Count, desc),
            ["studio"] = (compound, desc) =>
            {
                compound.Append(video => video.Studio == null ? 1 : 0, false);
                compound.Append(video => video.Studio != null ? video.Studio.Name : null, desc);
            },
            ["code"] = AppendCode,
            ["studio_code"] = AppendCode,
            ["created_at"] = (compound, desc) => compound.Append(video => video.CreatedAt, desc),
            ["updated_at"] = (compound, desc) => compound.Append(video => video.UpdatedAt, desc),
        });

    private static void AppendCode(CompoundSortQuery<Video> compound, bool desc)
    {
        compound.Append(video => video.Code == null ? 1 : 0, false);
        compound.Append(video => video.Code, desc);
    }

    private IQueryable<Video> ApplyMultiSorting(
        IQueryable<Video> query,
        IReadOnlyList<SortClause> clauses,
        CompoundSortRegistry<Video> registry)
    {
        var userId = EngagementQueryHelpers.CurrentUserId(_db);
        var compound = CompoundSortQuery<Video>.Create(
            _db,
            query,
            userId,
            AffinityHostType.Video,
            RatingHostType.Video,
            includeAffinity: clauses.Any(clause => AffinityMultiSortKeys.Contains(clause.Key)),
            includeRating: clauses.Any(clause => clause.Key.Equals("rating", StringComparison.OrdinalIgnoreCase)),
            interactionHostType: InteractionHostType.Video,
            interactionKind: InteractionKind.LikeCount,
            includeInteraction: clauses.Any(clause => clause.Key.Equals("last_like_at", StringComparison.OrdinalIgnoreCase)));

        registry.Apply(compound, clauses);

        return compound.Finish(video => video.Id);
    }

    private static IOrderedQueryable<Video> AppendSort<TKey>(
        IQueryable<Video> query,
        IOrderedQueryable<Video>? ordered,
        Expression<Func<Video, TKey>> keySelector,
        bool descending)
    {
        if (ordered == null)
            return descending ? query.OrderByDescending(keySelector) : query.OrderBy(keySelector);

        return descending ? ordered.ThenByDescending(keySelector) : ordered.ThenBy(keySelector);
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
            "last_like_at" => EngagementQueryHelpers.ApplyInteractionTimestampSort(_db, query, EngagementQueryHelpers.CurrentUserId(_db), InteractionHostType.Video, InteractionKind.LikeCount, desc),
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
        var folder = NormalizeFolderPathValue(criterion.Value);
        var folderExactNeedle = $"\n{folder}\n";
        var folderDescendantNeedle = $"\n{folder}{(folder.EndsWith('/') ? "" : "/")}";

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(s => s.FileSearchText != null && EF.Functions.Like(s.FileSearchText, exactPattern)),
            CriterionModifier.NotEquals => query.Where(s => s.FileSearchText == null || !EF.Functions.Like(s.FileSearchText, exactPattern)),
            CriterionModifier.Includes => query.Where(s => s.FileSearchText != null && EF.Functions.ILike(s.FileSearchText, pattern)),
            CriterionModifier.Excludes => query.Where(s => s.FileSearchText == null || !EF.Functions.ILike(s.FileSearchText, pattern)),
            CriterionModifier.MatchesRegex => query.Where(s => s.FileSearchText != null && Regex.IsMatch(s.FileSearchText, value, RegexOptions.IgnoreCase)),
            CriterionModifier.NotMatchesRegex => query.Where(s => s.FileSearchText == null || !Regex.IsMatch(s.FileSearchText, value, RegexOptions.IgnoreCase)),
            CriterionModifier.UnderPath when FilesystemPaths.PathComparison == StringComparison.OrdinalIgnoreCase => query.Where(s => s.FileSearchText != null
                && (s.FileSearchText.ToLower().Contains(folderExactNeedle.ToLower()) || s.FileSearchText.ToLower().Contains(folderDescendantNeedle.ToLower()))),
            CriterionModifier.NotUnderPath when FilesystemPaths.PathComparison == StringComparison.OrdinalIgnoreCase => query.Where(s => s.FileSearchText == null
                || (!s.FileSearchText.ToLower().Contains(folderExactNeedle.ToLower()) && !s.FileSearchText.ToLower().Contains(folderDescendantNeedle.ToLower()))),
            CriterionModifier.UnderPath => query.Where(s => s.FileSearchText != null
                && (s.FileSearchText.Contains(folderExactNeedle) || s.FileSearchText.Contains(folderDescendantNeedle))),
            CriterionModifier.NotUnderPath => query.Where(s => s.FileSearchText == null
                || (!s.FileSearchText.Contains(folderExactNeedle) && !s.FileSearchText.Contains(folderDescendantNeedle))),
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

    private static string NormalizeFolderPathValue(string value)
    {
        var normalized = NormalizePathValue(value).Trim();
        while (normalized.Length > 1 && normalized.EndsWith('/') && !(normalized.Length == 3 && normalized[1] == ':'))
            normalized = normalized[..^1];
        return normalized;
    }

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

    private static IQueryable<Video> ApplyMultiIdCriterion(
        IQueryable<Video> query,
        MultiIdCriterion? criterion,
        System.Linq.Expressions.Expression<Func<Video, IEnumerable<int>>> idsSelector,
        IReadOnlyList<int[]>? valueGroups = null)
        => MultiIdCriterionQueryHelper.Apply(query, criterion, idsSelector, valueGroups);

    private IQueryable<Video> ApplyVideoTagCriterion(IQueryable<Video> query, MultiIdCriterion? criterion, IReadOnlyList<int[]>? valueGroups = null, IReadOnlyList<int[]>? requiredIdGroups = null)
    {
        if (criterion == null)
            return query;

        var effectiveTags = EffectiveHostTagQuery.ForHostType(_db, AffinityHostType.Video);

        if (criterion.Modifier == CriterionModifier.IsNull)
        {
            query = query.Where(video => !effectiveTags.Any(tag => tag.HostId == video.Id));
        }
        else if (criterion.Modifier == CriterionModifier.NotNull)
        {
            query = query.Where(video => effectiveTags.Any(tag => tag.HostId == video.Id));
        }
        else
        {
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
        }

        // Excluded tags arrive in a separate list (the filter UI emits `excludes` alongside an Includes
        // modifier rather than flipping the modifier), so apply them independently of the include set —
        // including the exclude-only case where there are no included tags at all. Mirrors the
        // include/exclude split used by ApplyPerformerOccurrenceTagCriterion and the shared MultiId helper.
        if (criterion.Excludes is { Count: > 0 })
            query = ApplyVideoTagNone(query, criterion.Excludes);

        if (criterion.RequiredIds is { Count: > 0 })
            query = ApplyVideoTagIncludesAll(query, criterion.RequiredIds.Select(tagId => new[] { tagId }).ToArray());

        if (requiredIdGroups is { Count: > 0 })
            query = ApplyVideoTagIncludesAll(query, requiredIdGroups);

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

        if (filter.PerformersCriterion?.RequiredIds is { Count: > 0 })
        {
            foreach (var performerId in filter.PerformersCriterion.RequiredIds.Where(id => id > 0))
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
