using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.Data.Repositories;

public class SceneRepository : ISceneRepository
{
    private readonly CoveContext _db;
    public SceneRepository(CoveContext db) => _db = db;

    public async Task<Scene?> GetByIdAsync(int id, CancellationToken ct = default)
        => await _db.Scenes.FindAsync([id], ct);

    public async Task<Scene?> GetByIdWithRelationsAsync(int id, CancellationToken ct = default)
        => await _db.Scenes
            .Include(s => s.Studio)
            .Include(s => s.Urls)
            .Include(s => s.SceneTags).ThenInclude(st => st.Tag)
            .Include(s => s.ScenePerformers).ThenInclude(sp => sp.Performer)
            .Include(s => s.SceneGalleries).ThenInclude(sg => sg.Gallery)
            .Include(s => s.SceneGroups).ThenInclude(sg => sg.Group)
            .Include(s => s.Files).ThenInclude(f => f.Fingerprints)
            .Include(s => s.Files).ThenInclude(f => f.Captions)
            .Include(s => s.Files).ThenInclude(f => f.ParentFolder)
            .Include(s => s.SceneMarkers)
            .Include(s => s.RemoteIds)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<Scene>> GetAllAsync(CancellationToken ct = default)
        => await _db.Scenes.AsNoTracking().ToListAsync(ct);

    public async Task<Scene> AddAsync(Scene entity, CancellationToken ct = default)
    {
        _db.Scenes.Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task UpdateAsync(Scene entity, CancellationToken ct = default)
    {
        _db.Scenes.Update(entity);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var entity = await _db.Scenes.FindAsync([id], ct);
        if (entity != null)
        {
            _db.Scenes.Remove(entity);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
        => await _db.Scenes.CountAsync(ct);

    public async Task<(IReadOnlyList<Scene> Items, int TotalCount)> FindAsync(SceneFilter? filter, FindFilter? findFilter, CancellationToken ct = default)
    {
        ExpandedHierarchicalTagCriterion? expandedTags = null;
        if (filter?.TagsCriterion?.Depth == -1)
        {
            expandedTags = await ExpandHierarchicalTagCriterionAsync(filter.TagsCriterion, ct);
            filter.TagsCriterion = expandedTags.Criterion;
        }

        // Build a lightweight filter-only query (no Includes) for COUNT and filter predicates
        var filterQuery = _db.Scenes.AsQueryable();

        // Apply all filters to the lightweight query
        filterQuery = ApplyFilters(filterQuery, filter, expandedTags?.ValueGroups);

        // Apply text search
        if (findFilter != null && !string.IsNullOrEmpty(findFilter.Q))
        {
            var q = findFilter.Q;
            filterQuery = filterQuery.Where(s =>
                (s.Title != null && EF.Functions.ILike(s.Title, $"%{q}%")) ||
                (s.Details != null && EF.Functions.ILike(s.Details, $"%{q}%")) ||
                (s.Code != null && EF.Functions.ILike(s.Code, $"%{q}%")) ||
                s.Files.Any(f => EF.Functions.ILike(f.Basename, $"%{q}%")));
        }

        // COUNT runs on the lightweight query â€” no JOINs from Includes
        var perPage = findFilter?.PerPage ?? 25;

        // Short-circuit for count-only requests (perPage <= 0)
        if (perPage <= 0)
        {
            var count = await filterQuery.CountAsync(ct);
            return (Array.Empty<Scene>(), count);
        }

        // Run COUNT first on the lightweight query (no Includes = faster)
        var totalCount = await filterQuery.AsNoTracking().CountAsync(ct);

        // Sort and paginate on the lightweight query, then fetch only the IDs
        var sort = findFilter?.Sort ?? "updated_at";
        var desc = findFilter?.Direction == Core.Enums.SortDirection.Desc;
        filterQuery = ApplySorting(filterQuery, sort, desc, findFilter?.Seed);

        var page = findFilter?.Page ?? 1;
        var pagedIds = await filterQuery
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .Select(s => s.Id)
            .ToListAsync(ct);

        if (pagedIds.Count == 0)
            return (Array.Empty<Scene>(), totalCount);

        // Load full entities only for the paged IDs
        var items = await _db.Scenes
            .Include(s => s.Studio)
            .Include(s => s.SceneTags).ThenInclude(st => st.Tag)
            .Include(s => s.ScenePerformers).ThenInclude(sp => sp.Performer)
            .Include(s => s.SceneGalleries).ThenInclude(sg => sg.Gallery)
            .Include(s => s.Files).ThenInclude(file => file.Fingerprints)
            .Include(s => s.Files).ThenInclude(file => file.Captions)
            .Include(s => s.Files).ThenInclude(file => file.ParentFolder)
            .Include(s => s.SceneMarkers)
            .AsSplitQuery()
            .Where(s => pagedIds.Contains(s.Id))
            .AsNoTracking()
            .ToListAsync(ct);

        // Restore the sort order from the paged IDs
        var orderMap = pagedIds.Select((id, idx) => (id, idx)).ToDictionary(x => x.id, x => x.idx);
        var sorted = items.OrderBy(s => orderMap.GetValueOrDefault(s.Id, int.MaxValue)).ToList();

        return (sorted, totalCount);
    }

    private IQueryable<Scene> ApplyFilters(IQueryable<Scene> query, SceneFilter? filter, IReadOnlyList<int[]>? hierarchicalTagGroups = null)
    {
        if (filter == null) return query;
            if (!string.IsNullOrEmpty(filter.Title))
                query = query.Where(s => s.Title != null && EF.Functions.ILike(s.Title, $"%{filter.Title}%"));
            if (filter.Rating.HasValue)
                query = query.Where(s => s.Rating >= filter.Rating.Value);
            if (filter.Organized.HasValue)
                query = query.Where(s => s.Organized == filter.Organized.Value);
            if (filter.StudioId.HasValue)
                query = query.Where(s => s.StudioId == filter.StudioId.Value);
            if (filter.GroupId.HasValue)
                query = query.Where(s => s.SceneGroups.Any(sg => sg.GroupId == filter.GroupId.Value));
            if (filter.GalleryId.HasValue)
                query = query.Where(s => s.SceneGalleries.Any(sg => sg.GalleryId == filter.GalleryId.Value));
            if (filter.TagIds?.Count > 0)
                query = query.Where(s => s.SceneTags.Any(st => filter.TagIds.Contains(st.TagId)));
            if (filter.PerformerIds?.Count > 0)
                query = query.Where(s => s.ScenePerformers.Any(sp => filter.PerformerIds.Contains(sp.PerformerId)));

            // Advanced criteria
            query = ApplyIntCriterion(query, filter.RatingCriterion, s => s.Rating ?? 0);
            query = ApplyIntCriterion(query, filter.OCounterCriterion, s => s.OCounter);
            query = ApplyIntCriterion(query, filter.PlayCountCriterion, s => s.PlayCount);

            if (filter.PerformerCountCriterion != null)
                query = ApplyIntCriterion(query, filter.PerformerCountCriterion, s => s.ScenePerformers.Count);

            if (filter.DurationCriterion != null)
                query = ApplyIntCriterion(query, filter.DurationCriterion, s => (int)(s.Files.Select(f => (double?)f.Duration).Max() ?? 0));

            if (filter.ResolutionCriterion != null)
                query = FilterHelpers.ApplyResolution(query, filter.ResolutionCriterion, s => s.Files.Select(f => (int?)Math.Max(f.Width, f.Height)).Max() ?? 0);

            if (filter.FrameRateCriterion != null)
                query = ApplyIntCriterion(query, filter.FrameRateCriterion, s => (int)(s.Files.Select(f => (double?)f.FrameRate).Max() ?? 0));

            if (filter.BitrateInterval != null)
                query = ApplyIntCriterion(query, filter.BitrateInterval, s => (int)((s.Files.Select(f => (long?)f.BitRate).Max() ?? 0) / 1000));

            if (filter.FileCountCriterion != null)
                query = ApplyIntCriterion(query, filter.FileCountCriterion, s => s.Files.Count);

            query = ApplyMultiIdCriterion(query, filter.TagsCriterion, s => s.SceneTags.Select(st => st.TagId), hierarchicalTagGroups);
            query = ApplyMultiIdCriterion(query, filter.PerformersCriterion, s => s.ScenePerformers.Select(sp => sp.PerformerId));

            if (filter.StudiosCriterion != null)
            {
                var ids = filter.StudiosCriterion.Value;
                query = filter.StudiosCriterion.Modifier switch
                {
                    CriterionModifier.Includes => query.Where(s => s.StudioId.HasValue && ids.Contains(s.StudioId.Value)),
                    CriterionModifier.Excludes => query.Where(s => !s.StudioId.HasValue || !ids.Contains(s.StudioId.Value)),
                    _ => query.Where(s => s.StudioId.HasValue && ids.Contains(s.StudioId.Value)),
                };
            }

            query = ApplyMultiIdCriterion(query, filter.GroupsCriterion, s => s.SceneGroups.Select(sg => sg.GroupId));

            if (filter.OrganizedCriterion != null)
                query = query.Where(s => s.Organized == filter.OrganizedCriterion.Value);

            if (filter.HasMarkersCriterion != null)
                query = filter.HasMarkersCriterion.Value
                    ? query.Where(s => s.SceneMarkers.Count > 0)
                    : query.Where(s => s.SceneMarkers.Count == 0);

            if (filter.InteractiveCriterion != null)
                query = query.Where(s => s.Files.Any(f => f.Interactive == filter.InteractiveCriterion.Value));

            query = ApplyFingerprintCriterion(query, filter.FingerprintCriterion);
            query = ApplyFingerprintCriterion(query, filter.HashCriterion, "oshash");
            query = ApplyFingerprintCriterion(query, filter.ChecksumCriterion, "md5");

            if (filter.DuplicatedPhashCriterion != null)
                query = ApplyDuplicatedPhashCriterion(query, filter.DuplicatedPhashCriterion);

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
                    ? query.Where(s => s.ScenePerformers.Any(sp => sp.Performer!.Favorite))
                    : query.Where(s => !s.ScenePerformers.Any(sp => sp.Performer!.Favorite));

            if (filter.RemoteIdCriterion != null)
            {
                query = filter.RemoteIdCriterion.Modifier switch
                {
                    CriterionModifier.IsNull => query.Where(s => s.RemoteIds.Count == 0),
                    CriterionModifier.NotNull => query.Where(s => s.RemoteIds.Count > 0),
                    _ => query.Where(s => s.RemoteIds.Any(sid => EF.Functions.ILike(sid.Endpoint, $"%{filter.RemoteIdCriterion.Value}%"))),
                };
            }

            query = FilterHelpers.ApplyString(query, filter.TitleCriterion, s => s.Title);

            query = FilterHelpers.ApplyString(query, filter.CodeCriterion, s => s.Code);

            query = FilterHelpers.ApplyString(query, filter.DetailsCriterion, s => s.Details);

            query = FilterHelpers.ApplyString(query, filter.DirectorCriterion, s => s.Director);

            // Tag count criterion
            if (filter.TagCountCriterion != null)
                query = ApplyIntCriterion(query, filter.TagCountCriterion, s => s.SceneTags.Count);

            // Resume time criterion
            if (filter.ResumeTimeCriterion != null)
                query = ApplyIntCriterion(query, filter.ResumeTimeCriterion, s => (int)s.ResumeTime);

            // Play duration criterion
            if (filter.PlayDurationCriterion != null)
                query = ApplyIntCriterion(query, filter.PlayDurationCriterion, s => (int)s.PlayDuration);

            // Galleries criterion
            if (filter.GalleriesCriterion != null)
                query = ApplyMultiIdCriterion(query, filter.GalleriesCriterion, s => s.SceneGalleries.Select(sg => sg.GalleryId));

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
            query = FilterHelpers.ApplyNullableTimestamp(query, filter.LastPlayedAtCriterion, s => s.LastPlayedAt);

            // Performer tags criterion (filter scenes by tags of their performers)
            if (filter.PerformerTagsCriterion != null)
            {
                var ptIds = filter.PerformerTagsCriterion.Value;
                query = filter.PerformerTagsCriterion.Modifier switch
                {
                    CriterionModifier.Includes => query.Where(s => s.ScenePerformers.Any(sp => sp.Performer!.PerformerTags.Any(pt => ptIds.Contains(pt.TagId)))),
                    CriterionModifier.Excludes => query.Where(s => !s.ScenePerformers.Any(sp => sp.Performer!.PerformerTags.Any(pt => ptIds.Contains(pt.TagId)))),
                    CriterionModifier.IncludesAll => query.Where(s => ptIds.All(tid => s.ScenePerformers.Any(sp => sp.Performer!.PerformerTags.Any(pt => pt.TagId == tid)))),
                    _ => query.Where(s => s.ScenePerformers.Any(sp => sp.Performer!.PerformerTags.Any(pt => ptIds.Contains(pt.TagId)))),
                };
            }

            // Performer age criterion (age at time of scene based on scene date and performer birthdate)
            query = ApplyPerformerAgeCriterion(query, filter.PerformerAgeCriterion);

            // Captions criterion (filter by caption content)
            query = FilterHelpers.ApplyString(query, filter.CaptionsCriterion, s => s.Captions);

            // Interactive speed criterion
            if (filter.InteractiveSpeedCriterion != null)
                query = ApplyIntCriterion(query, filter.InteractiveSpeedCriterion, s => s.InteractiveSpeed ?? 0);

            // Orientation criterion: landscape, portrait, or square based on file dimensions
            if (filter.OrientationCriterion != null)
            {
                var orientation = filter.OrientationCriterion.Value.ToLower();
                query = orientation switch
                {
                    "landscape" => query.Where(s => s.Files.Any(f => f.Width > f.Height)),
                    "portrait" => query.Where(s => s.Files.Any(f => f.Height > f.Width)),
                    "square" => query.Where(s => s.Files.Any(f => f.Width == f.Height)),
                    _ => query,
                };
            }

        return query;
    }

    private static IQueryable<Scene> ApplySorting(IQueryable<Scene> query, string sort, bool desc, int? seed = null)
    {
        if (sort == "random")
            return SeededRandomOrdering.OrderBy(query, seed, scene => scene.Id);

        return ApplySortingSwitch(query, sort, desc);
    }

    private static IQueryable<Scene> ApplySortingSwitch(IQueryable<Scene> query, string sort, bool desc) => sort switch
    {
        "title" => desc ? query.OrderByDescending(s => s.Title) : query.OrderBy(s => s.Title),
        // Null dates sort to bottom: treat null as MinValue so they come last when desc
        "date" => desc ? query.OrderByDescending(s => s.Date ?? DateOnly.MinValue) : query.OrderBy(s => s.Date ?? DateOnly.MinValue),
        "rating" => desc
            ? query.OrderBy(s => s.Rating == null || s.Rating <= 0 ? 1 : 0).ThenByDescending(s => s.Rating)
            : query.OrderBy(s => s.Rating == null || s.Rating <= 0 ? 0 : 1).ThenBy(s => s.Rating),
        "play_count" => desc ? query.OrderByDescending(s => s.PlayCount) : query.OrderBy(s => s.PlayCount),
        "o_counter" => desc ? query.OrderByDescending(s => s.OCounter) : query.OrderBy(s => s.OCounter),
        "last_o_at" => ApplyLastFavoriteSort(query, desc),
        "organized" => desc ? query.OrderByDescending(s => s.Organized) : query.OrderBy(s => s.Organized),
        "last_played_at" => desc
            ? query.OrderBy(s => s.LastPlayedAt == null ? 1 : 0).ThenByDescending(s => s.LastPlayedAt)
            : query.OrderBy(s => s.LastPlayedAt == null ? 1 : 0).ThenBy(s => s.LastPlayedAt),
        "play_duration" => desc ? query.OrderByDescending(s => s.PlayDuration) : query.OrderBy(s => s.PlayDuration),
        "resume_time" => desc ? query.OrderByDescending(s => s.ResumeTime) : query.OrderBy(s => s.ResumeTime),
        "random" => query.OrderBy(s => s.Id),
        "duration" => desc
            ? query.OrderByDescending(s => s.Files.Select(file => (double?)file.Duration).Max() ?? 0)
            : query.OrderBy(s => s.Files.Select(file => (double?)file.Duration).Max() ?? 0),
        "file_size" => desc
            ? query.OrderByDescending(s => s.Files.Select(file => (long?)file.Size).Max() ?? 0)
            : query.OrderBy(s => s.Files.Select(file => (long?)file.Size).Max() ?? 0),
        "file_mod_time" => ApplyFileModTimeSort(query, desc),
        "file_count" => desc
            ? query.OrderByDescending(s => s.Files.Count)
            : query.OrderBy(s => s.Files.Count),
        "path" => ApplyPathSort(query, desc),
        "resolution" => desc
            ? query.OrderByDescending(s => s.Files.Select(file => file.Height).Max())
            : query.OrderBy(s => s.Files.Select(file => file.Height).Max()),
        "framerate" => desc
            ? query.OrderByDescending(s => s.Files.Select(file => file.FrameRate).Max())
            : query.OrderBy(s => s.Files.Select(file => file.FrameRate).Max()),
        "bitrate" => desc
            ? query.OrderByDescending(s => s.Files.Select(file => file.BitRate).Max())
            : query.OrderBy(s => s.Files.Select(file => file.BitRate).Max()),
        "phash" => ApplyPhashSort(query, desc),
        "perceptual_similarity" => ApplyPhashSort(query, desc),
        "tag_count" => desc
            ? query.OrderByDescending(s => s.SceneTags.Count)
            : query.OrderBy(s => s.SceneTags.Count),
        "performer_count" => desc
            ? query.OrderByDescending(s => s.ScenePerformers.Count)
            : query.OrderBy(s => s.ScenePerformers.Count),
        "performer_age" => ApplyPerformerAgeSort(query, desc),
        "studio" => ApplyStudioSort(query, desc),
        "code" => ApplyStudioCodeSort(query, desc),
        "studio_code" => ApplyStudioCodeSort(query, desc),
        "created_at" => desc ? query.OrderByDescending(s => s.CreatedAt) : query.OrderBy(s => s.CreatedAt),
        _ => desc ? query.OrderByDescending(s => s.UpdatedAt) : query.OrderBy(s => s.UpdatedAt),
    };

    private static IQueryable<Scene> ApplyLastFavoriteSort(IQueryable<Scene> query, bool desc)
    {
        var sortQuery = query.Select(scene => new
        {
            Scene = scene,
            LastFavoriteAt = scene.OHistory.Select(history => (DateTime?)history.OccurredAt).Max(),
        });

        return desc
            ? sortQuery.OrderBy(item => item.LastFavoriteAt == null ? 1 : 0).ThenByDescending(item => item.LastFavoriteAt).Select(item => item.Scene)
            : sortQuery.OrderBy(item => item.LastFavoriteAt == null ? 1 : 0).ThenBy(item => item.LastFavoriteAt).Select(item => item.Scene);
    }

    private static IQueryable<Scene> ApplyFileModTimeSort(IQueryable<Scene> query, bool desc)
    {
        var sortQuery = query.Select(scene => new
        {
            Scene = scene,
            FileModTime = scene.Files.Select(file => (DateTime?)file.ModTime).Max(),
        });

        return desc
            ? sortQuery.OrderBy(item => item.FileModTime == null ? 1 : 0).ThenByDescending(item => item.FileModTime).Select(item => item.Scene)
            : sortQuery.OrderBy(item => item.FileModTime == null ? 1 : 0).ThenBy(item => item.FileModTime).Select(item => item.Scene);
    }

    private static IQueryable<Scene> ApplyPathSort(IQueryable<Scene> query, bool desc)
    {
        if (desc)
        {
            var descendingQuery = query.Select(scene => new
            {
                Scene = scene,
                Path = scene.Files
                    .Select(file => file.ParentFolder != null ? file.ParentFolder.Path.Replace("\\", "/") + "/" + file.Basename : file.Basename)
                    .OrderByDescending(path => path)
                    .FirstOrDefault(),
            });

            return descendingQuery
                .OrderBy(item => item.Path == null ? 1 : 0)
                .ThenByDescending(item => item.Path)
                .Select(item => item.Scene);
        }

        var ascendingQuery = query.Select(scene => new
        {
            Scene = scene,
            Path = scene.Files
                .Select(file => file.ParentFolder != null ? file.ParentFolder.Path.Replace("\\", "/") + "/" + file.Basename : file.Basename)
                .OrderBy(path => path)
                .FirstOrDefault(),
        });

        return ascendingQuery
            .OrderBy(item => item.Path == null ? 1 : 0)
            .ThenBy(item => item.Path)
            .Select(item => item.Scene);
    }

    private static IQueryable<Scene> ApplyPhashSort(IQueryable<Scene> query, bool desc)
    {
        if (desc)
        {
            var descendingQuery = query.Select(scene => new
            {
                Scene = scene,
                Phash = scene.Files
                    .SelectMany(file => file.Fingerprints
                        .Where(fingerprint => fingerprint.Type == "phash" && fingerprint.Value != "")
                        .Select(fingerprint => fingerprint.Value))
                    .OrderByDescending(value => value)
                    .FirstOrDefault(),
            });

            return descendingQuery
                .OrderBy(item => item.Phash == null ? 1 : 0)
                .ThenByDescending(item => item.Phash)
                .Select(item => item.Scene);
        }

        var ascendingQuery = query.Select(scene => new
        {
            Scene = scene,
            Phash = scene.Files
                .SelectMany(file => file.Fingerprints
                    .Where(fingerprint => fingerprint.Type == "phash" && fingerprint.Value != "")
                    .Select(fingerprint => fingerprint.Value))
                .OrderBy(value => value)
                .FirstOrDefault(),
        });

        return ascendingQuery
            .OrderBy(item => item.Phash == null ? 1 : 0)
            .ThenBy(item => item.Phash)
            .Select(item => item.Scene);
    }

    private static IQueryable<Scene> ApplyStudioSort(IQueryable<Scene> query, bool desc)
    {
        var sortQuery = query.Select(scene => new
        {
            Scene = scene,
            StudioName = scene.Studio != null ? scene.Studio.Name : null,
        });

        return desc
            ? sortQuery.OrderBy(item => item.StudioName == null ? 1 : 0).ThenByDescending(item => item.StudioName).Select(item => item.Scene)
            : sortQuery.OrderBy(item => item.StudioName == null ? 1 : 0).ThenBy(item => item.StudioName).Select(item => item.Scene);
    }

    private static IQueryable<Scene> ApplyStudioCodeSort(IQueryable<Scene> query, bool desc)
    {
        var sortQuery = query.Select(scene => new
        {
            Scene = scene,
            Code = scene.Code,
        });

        return desc
            ? sortQuery.OrderBy(item => item.Code == null ? 1 : 0).ThenByDescending(item => item.Code).Select(item => item.Scene)
            : sortQuery.OrderBy(item => item.Code == null ? 1 : 0).ThenBy(item => item.Code).Select(item => item.Scene);
    }

    private static IQueryable<Scene> ApplyPerformerAgeSort(IQueryable<Scene> query, bool desc)
    {
        if (desc)
        {
            var descendingQuery = query.Select(scene => new
            {
                Scene = scene,
                PerformerAge = scene.ScenePerformers
                    .Where(sp => scene.Date != null && sp.Performer!.Birthdate != null)
                    .Select(sp => (int?)(
                        scene.Date!.Value.Year - sp.Performer!.Birthdate!.Value.Year
                        - ((scene.Date!.Value.Month < sp.Performer!.Birthdate!.Value.Month
                            || (scene.Date!.Value.Month == sp.Performer!.Birthdate!.Value.Month && scene.Date!.Value.Day < sp.Performer!.Birthdate!.Value.Day)) ? 1 : 0)))
                    .Max(),
            });

            return descendingQuery
                .OrderBy(item => item.PerformerAge == null ? 1 : 0)
                .ThenByDescending(item => item.PerformerAge)
                .Select(item => item.Scene);
        }

        var ascendingQuery = query.Select(scene => new
        {
            Scene = scene,
            PerformerAge = scene.ScenePerformers
                .Where(sp => scene.Date != null && sp.Performer!.Birthdate != null)
                .Select(sp => (int?)(
                    scene.Date!.Value.Year - sp.Performer!.Birthdate!.Value.Year
                    - ((scene.Date!.Value.Month < sp.Performer!.Birthdate!.Value.Month
                        || (scene.Date!.Value.Month == sp.Performer!.Birthdate!.Value.Month && scene.Date!.Value.Day < sp.Performer!.Birthdate!.Value.Day)) ? 1 : 0)))
                .Min(),
        });

        return ascendingQuery
            .OrderBy(item => item.PerformerAge == null ? 1 : 0)
            .ThenBy(item => item.PerformerAge)
            .Select(item => item.Scene);
    }

    private static IQueryable<Scene> ApplyPathCriterion(IQueryable<Scene> query, StringCriterion? criterion)
    {
        if (criterion == null) return query;

        var value = NormalizePathValue(criterion.Value);
        var pattern = $"%{value}%";

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(s => s.Files.Any(f =>
                (f.ParentFolder != null ? f.ParentFolder.Path.Replace("\\", "/") + "/" + f.Basename : f.Basename) == value)),
            CriterionModifier.NotEquals => query.Where(s => !s.Files.Any(f =>
                (f.ParentFolder != null ? f.ParentFolder.Path.Replace("\\", "/") + "/" + f.Basename : f.Basename) == value)),
            CriterionModifier.Includes => query.Where(s => s.Files.Any(f =>
                EF.Functions.ILike(
                    f.ParentFolder != null ? f.ParentFolder.Path.Replace("\\", "/") + "/" + f.Basename : f.Basename,
                    pattern))),
            CriterionModifier.Excludes => query.Where(s => !s.Files.Any(f =>
                EF.Functions.ILike(
                    f.ParentFolder != null ? f.ParentFolder.Path.Replace("\\", "/") + "/" + f.Basename : f.Basename,
                    pattern))),
            CriterionModifier.MatchesRegex => query.Where(s => s.Files.Any(f =>
                Regex.IsMatch(
                    f.ParentFolder != null ? f.ParentFolder.Path.Replace("\\", "/") + "/" + f.Basename : f.Basename,
                    value,
                    RegexOptions.IgnoreCase))),
            CriterionModifier.NotMatchesRegex => query.Where(s => !s.Files.Any(f =>
                Regex.IsMatch(
                    f.ParentFolder != null ? f.ParentFolder.Path.Replace("\\", "/") + "/" + f.Basename : f.Basename,
                    value,
                    RegexOptions.IgnoreCase))),
            CriterionModifier.IsNull => query.Where(s => !s.Files.Any(f =>
                (f.ParentFolder != null ? f.ParentFolder.Path.Replace("\\", "/") + "/" + f.Basename : f.Basename) != "")),
            CriterionModifier.NotNull => query.Where(s => s.Files.Any(f =>
                (f.ParentFolder != null ? f.ParentFolder.Path.Replace("\\", "/") + "/" + f.Basename : f.Basename) != "")),
            _ => query,
        };
    }

    private static IQueryable<Scene> ApplyFingerprintCriterion(IQueryable<Scene> query, StringCriterion? criterion, string fingerprintType)
    {
        if (criterion == null) return query;

        var value = criterion.Value;
        var pattern = $"%{value}%";

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(scene => scene.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && fingerprint.Value == value))),
            CriterionModifier.NotEquals => query.Where(scene => !scene.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && fingerprint.Value == value))),
            CriterionModifier.Includes => query.Where(scene => scene.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && EF.Functions.ILike(fingerprint.Value, pattern)))),
            CriterionModifier.Excludes => query.Where(scene => !scene.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && EF.Functions.ILike(fingerprint.Value, pattern)))),
            CriterionModifier.MatchesRegex => query.Where(scene => scene.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && Regex.IsMatch(fingerprint.Value, value, RegexOptions.IgnoreCase)))),
            CriterionModifier.NotMatchesRegex => query.Where(scene => !scene.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && Regex.IsMatch(fingerprint.Value, value, RegexOptions.IgnoreCase)))),
            CriterionModifier.IsNull => query.Where(scene => !scene.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && fingerprint.Value != ""))),
            CriterionModifier.NotNull => query.Where(scene => scene.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && fingerprint.Value != ""))),
            _ => query,
        };
    }

    private static IQueryable<Scene> ApplyFingerprintCriterion(IQueryable<Scene> query, FingerprintCriterion? criterion)
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

    private IQueryable<Scene> ApplyDuplicatedPhashCriterion(IQueryable<Scene> query, BoolCriterion criterion)
    {
        var duplicatedQuery = query.Where(scene => scene.Files
            .SelectMany(file => file.Fingerprints
                .Where(fingerprint => fingerprint.Type == "phash" && fingerprint.Value != "")
                .Select(fingerprint => fingerprint.Value))
            .Any(phash => _db.VideoFiles.Any(otherFile =>
                otherFile.SceneId.HasValue
                && otherFile.SceneId.Value != scene.Id
                && otherFile.Fingerprints.Any(otherFingerprint => otherFingerprint.Type == "phash" && otherFingerprint.Value == phash))));

        return criterion.Value ? duplicatedQuery : query.Where(scene => !duplicatedQuery.Select(item => item.Id).Contains(scene.Id));
    }

    private static IQueryable<Scene> ApplyVideoCodecCriterion(IQueryable<Scene> query, StringCriterion? criterion)
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

    private static IQueryable<Scene> ApplyAudioCodecCriterion(IQueryable<Scene> query, StringCriterion? criterion)
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

    private static IQueryable<Scene> ApplyPerformerAgeCriterion(IQueryable<Scene> query, IntCriterion? criterion)
    {
        if (criterion == null) return query;

        var value = criterion.Value;
        var value2 = criterion.Value2 ?? value;

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(s => s.Date != null && s.ScenePerformers.Any(sp =>
                sp.Performer!.Birthdate != null &&
                (s.Date.Value.Year - sp.Performer.Birthdate.Value.Year
                    - ((s.Date.Value.Month < sp.Performer.Birthdate.Value.Month
                        || (s.Date.Value.Month == sp.Performer.Birthdate.Value.Month && s.Date.Value.Day < sp.Performer.Birthdate.Value.Day)) ? 1 : 0)) == value)),
            CriterionModifier.NotEquals => query.Where(s => s.Date != null && s.ScenePerformers.Any(sp =>
                sp.Performer!.Birthdate != null &&
                (s.Date.Value.Year - sp.Performer.Birthdate.Value.Year
                    - ((s.Date.Value.Month < sp.Performer.Birthdate.Value.Month
                        || (s.Date.Value.Month == sp.Performer.Birthdate.Value.Month && s.Date.Value.Day < sp.Performer.Birthdate.Value.Day)) ? 1 : 0)) != value)),
            CriterionModifier.GreaterThan => query.Where(s => s.Date != null && s.ScenePerformers.Any(sp =>
                sp.Performer!.Birthdate != null &&
                (s.Date.Value.Year - sp.Performer.Birthdate.Value.Year
                    - ((s.Date.Value.Month < sp.Performer.Birthdate.Value.Month
                        || (s.Date.Value.Month == sp.Performer.Birthdate.Value.Month && s.Date.Value.Day < sp.Performer.Birthdate.Value.Day)) ? 1 : 0)) > value)),
            CriterionModifier.LessThan => query.Where(s => s.Date != null && s.ScenePerformers.Any(sp =>
                sp.Performer!.Birthdate != null &&
                (s.Date.Value.Year - sp.Performer.Birthdate.Value.Year
                    - ((s.Date.Value.Month < sp.Performer.Birthdate.Value.Month
                        || (s.Date.Value.Month == sp.Performer.Birthdate.Value.Month && s.Date.Value.Day < sp.Performer.Birthdate.Value.Day)) ? 1 : 0)) < value)),
            CriterionModifier.Between => query.Where(s => s.Date != null && s.ScenePerformers.Any(sp =>
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
    private static IQueryable<Scene> ApplyIntCriterion(IQueryable<Scene> query, IntCriterion? criterion, System.Linq.Expressions.Expression<Func<Scene, int>> selector)
    {
        if (criterion == null) return query;
        var val = criterion.Value;
        var val2 = criterion.Value2 ?? val;
        var param = selector.Parameters[0];
        var body = selector.Body;

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(System.Linq.Expressions.Expression.Lambda<Func<Scene, bool>>(
                System.Linq.Expressions.Expression.Equal(body, System.Linq.Expressions.Expression.Constant(val)), param)),
            CriterionModifier.NotEquals => query.Where(System.Linq.Expressions.Expression.Lambda<Func<Scene, bool>>(
                System.Linq.Expressions.Expression.NotEqual(body, System.Linq.Expressions.Expression.Constant(val)), param)),
            CriterionModifier.GreaterThan => query.Where(System.Linq.Expressions.Expression.Lambda<Func<Scene, bool>>(
                System.Linq.Expressions.Expression.GreaterThan(body, System.Linq.Expressions.Expression.Constant(val)), param)),
            CriterionModifier.LessThan => query.Where(System.Linq.Expressions.Expression.Lambda<Func<Scene, bool>>(
                System.Linq.Expressions.Expression.LessThan(body, System.Linq.Expressions.Expression.Constant(val)), param)),
            CriterionModifier.Between => query.Where(System.Linq.Expressions.Expression.Lambda<Func<Scene, bool>>(
                System.Linq.Expressions.Expression.AndAlso(
                    System.Linq.Expressions.Expression.GreaterThanOrEqual(body, System.Linq.Expressions.Expression.Constant(val)),
                    System.Linq.Expressions.Expression.LessThanOrEqual(body, System.Linq.Expressions.Expression.Constant(val2))), param)),
            CriterionModifier.NotBetween => query.Where(System.Linq.Expressions.Expression.Lambda<Func<Scene, bool>>(
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

    private static IQueryable<Scene> ApplyMultiIdCriterion(
        IQueryable<Scene> query,
        MultiIdCriterion? criterion,
        System.Linq.Expressions.Expression<Func<Scene, IEnumerable<int>>> idsSelector,
        IReadOnlyList<int[]>? valueGroups = null)
        => MultiIdCriterionQueryHelper.Apply(query, criterion, idsSelector, valueGroups);
}
