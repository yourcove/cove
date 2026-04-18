using Microsoft.EntityFrameworkCore;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.Data.Repositories;

public class PerformerRepository : IPerformerRepository
{
    private readonly CoveContext _db;
    public PerformerRepository(CoveContext db) => _db = db;

    public async Task<Performer?> GetByIdAsync(int id, CancellationToken ct = default)
        => await _db.Performers.FindAsync([id], ct);

    public async Task<Performer?> GetByIdWithRelationsAsync(int id, CancellationToken ct = default)
        => await _db.Performers
            .Include(p => p.Urls)
            .Include(p => p.Aliases)
            .Include(p => p.PerformerTags).ThenInclude(pt => pt.Tag)
            .Include(p => p.RemoteIds)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<Performer>> GetAllAsync(CancellationToken ct = default)
        => await _db.Performers.AsNoTracking().ToListAsync(ct);

    public async Task<Performer> AddAsync(Performer entity, CancellationToken ct = default)
    {
        _db.Performers.Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task UpdateAsync(Performer entity, CancellationToken ct = default)
    {
        _db.Performers.Update(entity);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var entity = await _db.Performers.FindAsync([id], ct);
        if (entity != null)
        {
            _db.Performers.Remove(entity);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
        => await _db.Performers.CountAsync(ct);

    public async Task<(IReadOnlyList<Performer> Items, int TotalCount)> FindAsync(PerformerFilter? filter, FindFilter? findFilter, CancellationToken ct = default)
    {
        var query = _db.Performers
            .Include(p => p.PerformerTags).ThenInclude(pt => pt.Tag)
            .AsSplitQuery()
            .AsQueryable();

        if (filter != null)
        {
            if (!string.IsNullOrEmpty(filter.Name))
                query = query.Where(p => EF.Functions.ILike(p.Name, $"%{filter.Name}%"));
            if (filter.Favorite.HasValue)
                query = query.Where(p => p.Favorite == filter.Favorite.Value);
            if (filter.Rating.HasValue)
                query = query.Where(p => p.Rating >= filter.Rating.Value);
            if (filter.TagIds?.Count > 0)
                query = query.Where(p => p.PerformerTags.Any(pt => filter.TagIds.Contains(pt.TagId)));
            if (filter.StudioId.HasValue)
                query = query.Where(p => p.ScenePerformers.Any(sp => sp.Scene!.StudioId == filter.StudioId.Value));

            // Advanced criteria
            query = FilterHelpers.ApplyInt(query, filter.RatingCriterion, p => p.Rating ?? 0);
            query = FilterHelpers.ApplyInt(query, filter.HeightCriterion, p => p.HeightCm ?? 0);
            query = FilterHelpers.ApplyInt(query, filter.WeightCriterion, p => p.Weight ?? 0);
            query = FilterHelpers.ApplyInt(query, filter.SceneCountCriterion, p => p.ScenePerformers.Count);
            query = FilterHelpers.ApplyInt(query, filter.ImageCountCriterion, p => p.ImagePerformers.Count);
            query = FilterHelpers.ApplyInt(query, filter.GalleryCountCriterion, p => p.GalleryPerformers.Count);

            // Age criterion â€” computed from Birthdate
            if (filter.AgeCriterion != null && filter.AgeCriterion.Value > 0)
            {
                var now = DateOnly.FromDateTime(DateTime.Today);
                // Convert age to birth date range
                var val = filter.AgeCriterion.Value;
                var val2 = filter.AgeCriterion.Value2 ?? val;
                var oldestBirth = now.AddYears(-val2 - 1).AddDays(1);
                var youngestBirth = now.AddYears(-val);
                query = filter.AgeCriterion.Modifier switch
                {
                    CriterionModifier.Equals => query.Where(p => p.Birthdate.HasValue && p.Birthdate.Value >= now.AddYears(-val - 1).AddDays(1) && p.Birthdate.Value <= now.AddYears(-val)),
                    CriterionModifier.NotEquals => query.Where(p => !p.Birthdate.HasValue || p.Birthdate.Value < now.AddYears(-val - 1).AddDays(1) || p.Birthdate.Value > now.AddYears(-val)),
                    CriterionModifier.GreaterThan => query.Where(p => p.Birthdate.HasValue && p.Birthdate.Value < youngestBirth),
                    CriterionModifier.LessThan => query.Where(p => p.Birthdate.HasValue && p.Birthdate.Value > youngestBirth),
                    CriterionModifier.Between => query.Where(p => p.Birthdate.HasValue && p.Birthdate.Value >= oldestBirth && p.Birthdate.Value <= youngestBirth),
                    CriterionModifier.NotBetween => query.Where(p => p.Birthdate.HasValue && (p.Birthdate.Value < oldestBirth || p.Birthdate.Value > youngestBirth)),
                    _ => query,
                };
            }

            // String criteria
            query = FilterHelpers.ApplyString(query, filter.GenderCriterion, p => p.Gender != null ? p.Gender.ToString() : null);
            query = FilterHelpers.ApplyString(query, filter.EthnicityCriterion, p => p.Ethnicity);
            query = FilterHelpers.ApplyString(query, filter.CountryCriterion, p => p.Country);
            query = FilterHelpers.ApplyString(query, filter.UrlCriterion, p => p.Urls.Select(u => u.Url).FirstOrDefault());

            if (filter.FavoriteCriterion != null)
                query = query.Where(p => p.Favorite == filter.FavoriteCriterion.Value);

            // Multi-ID criteria
            query = FilterHelpers.ApplyMultiId(query, filter.TagsCriterion, p => p.PerformerTags.Select(pt => pt.TagId));

            if (filter.StudiosCriterion != null)
            {
                var ids = filter.StudiosCriterion.Value;
                query = filter.StudiosCriterion.Modifier switch
                {
                    CriterionModifier.Includes => query.Where(p => p.ScenePerformers.Any(sp => sp.Scene!.StudioId.HasValue && ids.Contains(sp.Scene.StudioId.Value))),
                    CriterionModifier.Excludes => query.Where(p => !p.ScenePerformers.Any(sp => sp.Scene!.StudioId.HasValue && ids.Contains(sp.Scene.StudioId.Value))),
                    _ => query.Where(p => p.ScenePerformers.Any(sp => sp.Scene!.StudioId.HasValue && ids.Contains(sp.Scene.StudioId.Value))),
                };
            }

            // Date criteria
            query = FilterHelpers.ApplyDate(query, filter.BirthdateCriterion, p => p.Birthdate);

            // Timestamp criteria
            query = FilterHelpers.ApplyTimestamp(query, filter.CreatedAtCriterion, p => p.CreatedAt);
            query = FilterHelpers.ApplyTimestamp(query, filter.UpdatedAtCriterion, p => p.UpdatedAt);

            // RemoteId criterion
            if (filter.RemoteIdCriterion != null)
            {
                query = filter.RemoteIdCriterion.Modifier switch
                {
                    CriterionModifier.IsNull => query.Where(p => p.RemoteIds.Count == 0),
                    CriterionModifier.NotNull => query.Where(p => p.RemoteIds.Count > 0),
                    _ => query.Where(p => p.RemoteIds.Any(sid => EF.Functions.ILike(sid.Endpoint, $"%{filter.RemoteIdCriterion.Value}%"))),
                };
            }
        }

        if (findFilter != null && !string.IsNullOrEmpty(findFilter.Q))
        {
            var q = findFilter.Q;
            query = query.Where(p =>
                EF.Functions.ILike(p.Name, $"%{q}%") ||
                (p.Disambiguation != null && EF.Functions.ILike(p.Disambiguation, $"%{q}%")) ||
                p.Aliases.Any(a => EF.Functions.ILike(a.Alias, $"%{q}%")));
        }

        var totalCount = await query.CountAsync(ct);

        var sort = findFilter?.Sort ?? "name";
        var desc = findFilter?.Direction == Core.Enums.SortDirection.Desc;
        query = sort switch
        {
            "name" => desc ? query.OrderByDescending(p => p.Name) : query.OrderBy(p => p.Name),
            "rating" => desc ? query.OrderByDescending(p => p.Rating) : query.OrderBy(p => p.Rating),
            "created_at" => desc ? query.OrderByDescending(p => p.CreatedAt) : query.OrderBy(p => p.CreatedAt),
            "birthdate" => desc ? query.OrderByDescending(p => p.Birthdate) : query.OrderBy(p => p.Birthdate),
            "scene_count" => desc
                ? query.OrderByDescending(p => p.ScenePerformers.Count)
                : query.OrderBy(p => p.ScenePerformers.Count),
            "image_count" => desc
                ? query.OrderByDescending(p => p.ImagePerformers.Count)
                : query.OrderBy(p => p.ImagePerformers.Count),
            "gallery_count" => desc
                ? query.OrderByDescending(p => p.GalleryPerformers.Count)
                : query.OrderBy(p => p.GalleryPerformers.Count),
            "height" => desc ? query.OrderByDescending(p => p.HeightCm) : query.OrderBy(p => p.HeightCm),
            "weight" => desc ? query.OrderByDescending(p => p.Weight) : query.OrderBy(p => p.Weight),
            "tag_count" => desc
                ? query.OrderByDescending(p => p.PerformerTags.Count)
                : query.OrderBy(p => p.PerformerTags.Count),
            "random" => query.OrderBy(_ => EF.Functions.Random()),
            _ => desc ? query.OrderByDescending(p => p.UpdatedAt) : query.OrderBy(p => p.UpdatedAt),
        };

        var page = findFilter?.Page ?? 1;
        var perPage = findFilter?.PerPage ?? 25;
        var items = await query.Skip((page - 1) * perPage).Take(perPage).AsNoTracking().ToListAsync(ct);

        return (items, totalCount);
    }
}

public class TagRepository : ITagRepository
{
    private readonly CoveContext _db;
    public TagRepository(CoveContext db) => _db = db;

    public async Task<Tag?> GetByIdAsync(int id, CancellationToken ct = default)
        => await _db.Tags.FindAsync([id], ct);

    public async Task<Tag?> GetByIdWithRelationsAsync(int id, CancellationToken ct = default)
        => await _db.Tags
            .Include(t => t.Aliases)
            .Include(t => t.ParentRelations).ThenInclude(tp => tp.Parent)
            .Include(t => t.ChildRelations).ThenInclude(tp => tp.Child)
            .Include(t => t.RemoteIds)
            .AsSplitQuery()
            .FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<Tag?> GetByNameAsync(string name, CancellationToken ct = default)
        => await _db.Tags.FirstOrDefaultAsync(t => t.Name == name, ct);

    public async Task<IReadOnlyList<Tag>> GetAllAsync(CancellationToken ct = default)
        => await _db.Tags.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);

    public async Task<Tag> AddAsync(Tag entity, CancellationToken ct = default)
    {
        _db.Tags.Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task UpdateAsync(Tag entity, CancellationToken ct = default)
    {
        _db.Tags.Update(entity);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var entity = await _db.Tags.FindAsync([id], ct);
        if (entity != null)
        {
            _db.Tags.Remove(entity);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
        => await _db.Tags.CountAsync(ct);

    public async Task<(IReadOnlyList<Tag> Items, int TotalCount)> FindAsync(TagFilter? filter, FindFilter? findFilter, CancellationToken ct = default)
    {
        var query = _db.Tags
            .Include(t => t.Aliases)
            .AsQueryable();

        if (filter != null)
        {
            if (!string.IsNullOrEmpty(filter.Name))
                query = query.Where(t => EF.Functions.ILike(t.Name, $"%{filter.Name}%"));
            if (filter.Favorite.HasValue)
                query = query.Where(t => t.Favorite == filter.Favorite.Value);

            // Advanced criteria
            if (filter.FavoriteCriterion != null)
                query = query.Where(t => t.Favorite == filter.FavoriteCriterion.Value);

            query = FilterHelpers.ApplyInt(query, filter.SceneCountCriterion, t => t.SceneTags.Count);
            query = FilterHelpers.ApplyInt(query, filter.PerformerCountCriterion, t => t.PerformerTags.Count);

            // Marker count â€” count scene markers where this tag is primary or associated
            if (filter.MarkerCountCriterion != null)
            {
                var markerCountVal = filter.MarkerCountCriterion.Value;
                var markerCountVal2 = filter.MarkerCountCriterion.Value2 ?? markerCountVal;
                query = filter.MarkerCountCriterion.Modifier switch
                {
                    CriterionModifier.Equals => query.Where(t =>
                        _db.SceneMarkers.Count(m => m.PrimaryTagId == t.Id || m.SceneMarkerTags.Any(mt => mt.TagId == t.Id)) == markerCountVal),
                    CriterionModifier.NotEquals => query.Where(t =>
                        _db.SceneMarkers.Count(m => m.PrimaryTagId == t.Id || m.SceneMarkerTags.Any(mt => mt.TagId == t.Id)) != markerCountVal),
                    CriterionModifier.GreaterThan => query.Where(t =>
                        _db.SceneMarkers.Count(m => m.PrimaryTagId == t.Id || m.SceneMarkerTags.Any(mt => mt.TagId == t.Id)) > markerCountVal),
                    CriterionModifier.LessThan => query.Where(t =>
                        _db.SceneMarkers.Count(m => m.PrimaryTagId == t.Id || m.SceneMarkerTags.Any(mt => mt.TagId == t.Id)) < markerCountVal),
                    CriterionModifier.Between => query.Where(t =>
                        _db.SceneMarkers.Count(m => m.PrimaryTagId == t.Id || m.SceneMarkerTags.Any(mt => mt.TagId == t.Id)) >= markerCountVal &&
                        _db.SceneMarkers.Count(m => m.PrimaryTagId == t.Id || m.SceneMarkerTags.Any(mt => mt.TagId == t.Id)) <= markerCountVal2),
                    CriterionModifier.NotBetween => query.Where(t =>
                        _db.SceneMarkers.Count(m => m.PrimaryTagId == t.Id || m.SceneMarkerTags.Any(mt => mt.TagId == t.Id)) < markerCountVal ||
                        _db.SceneMarkers.Count(m => m.PrimaryTagId == t.Id || m.SceneMarkerTags.Any(mt => mt.TagId == t.Id)) > markerCountVal2),
                    _ => query,
                };
            }

            // Multi-ID criteria
            query = FilterHelpers.ApplyMultiId(query, filter.ParentsCriterion, t => t.ParentRelations.Select(tp => tp.ParentId));
            query = FilterHelpers.ApplyMultiId(query, filter.ChildrenCriterion, t => t.ChildRelations.Select(tp => tp.ChildId));

            // Timestamp criteria
            query = FilterHelpers.ApplyTimestamp(query, filter.CreatedAtCriterion, t => t.CreatedAt);
            query = FilterHelpers.ApplyTimestamp(query, filter.UpdatedAtCriterion, t => t.UpdatedAt);
        }

        if (findFilter != null && !string.IsNullOrEmpty(findFilter.Q))
        {
            var q = findFilter.Q;
            query = query.Where(t =>
                EF.Functions.ILike(t.Name, $"%{q}%") ||
                (t.Description != null && EF.Functions.ILike(t.Description, $"%{q}%")) ||
                t.Aliases.Any(a => EF.Functions.ILike(a.Alias, $"%{q}%")));
        }

        var totalCount = await query.CountAsync(ct);

        var sort = findFilter?.Sort ?? "name";
        var desc = findFilter?.Direction == Core.Enums.SortDirection.Desc;
        query = sort switch
        {
            "name" => desc ? query.OrderByDescending(t => t.Name) : query.OrderBy(t => t.Name),
            _ => desc ? query.OrderByDescending(t => t.UpdatedAt) : query.OrderBy(t => t.UpdatedAt),
        };

        var page = findFilter?.Page ?? 1;
        var perPage = findFilter?.PerPage ?? 25;
        var items = await query.Skip((page - 1) * perPage).Take(perPage).AsNoTracking().ToListAsync(ct);

        return (items, totalCount);
    }
}

public class StudioRepository : IStudioRepository
{
    private readonly CoveContext _db;
    public StudioRepository(CoveContext db) => _db = db;

    public async Task<Studio?> GetByIdAsync(int id, CancellationToken ct = default) => await _db.Studios.FindAsync([id], ct);

    public async Task<Studio?> GetByIdWithRelationsAsync(int id, CancellationToken ct = default)
        => await _db.Studios
            .Include(s => s.Parent).Include(s => s.Children)
            .Include(s => s.Urls).Include(s => s.Aliases)
            .Include(s => s.StudioTags).ThenInclude(st => st.Tag)
            .Include(s => s.RemoteIds)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<Studio>> GetAllAsync(CancellationToken ct = default)
        => await _db.Studios.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);

    public async Task<Studio> AddAsync(Studio entity, CancellationToken ct = default)
    {
        _db.Studios.Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task UpdateAsync(Studio entity, CancellationToken ct = default)
    {
        _db.Studios.Update(entity);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var entity = await _db.Studios.FindAsync([id], ct);
        if (entity != null) { _db.Studios.Remove(entity); await _db.SaveChangesAsync(ct); }
    }

    public async Task<int> CountAsync(CancellationToken ct = default) => await _db.Studios.CountAsync(ct);

    public async Task<(IReadOnlyList<Studio> Items, int TotalCount)> FindAsync(StudioFilter? filter, FindFilter? findFilter, CancellationToken ct = default)
    {
        var query = _db.Studios.Include(s => s.StudioTags).ThenInclude(st => st.Tag).Include(s => s.RemoteIds).AsSplitQuery().AsQueryable();
        if (filter != null)
        {
            if (!string.IsNullOrEmpty(filter.Name)) query = query.Where(s => EF.Functions.ILike(s.Name, $"%{filter.Name}%"));
            if (filter.Favorite.HasValue) query = query.Where(s => s.Favorite == filter.Favorite.Value);
            if (filter.ParentId.HasValue) query = query.Where(s => s.ParentId == filter.ParentId.Value);
            if (filter.TagIds?.Count > 0) query = query.Where(s => s.StudioTags.Any(st => filter.TagIds.Contains(st.TagId)));

            // Advanced criteria
            query = FilterHelpers.ApplyInt(query, filter.RatingCriterion, s => s.Rating ?? 0);
            query = FilterHelpers.ApplyInt(query, filter.SceneCountCriterion, s => s.Scenes.Count);
            query = FilterHelpers.ApplyInt(query, filter.GalleryCountCriterion, s => s.Galleries.Count);
            query = FilterHelpers.ApplyInt(query, filter.ImageCountCriterion, s => s.Images.Count);

            if (filter.FavoriteCriterion != null)
                query = query.Where(s => s.Favorite == filter.FavoriteCriterion.Value);

            // Multi-ID criteria
            query = FilterHelpers.ApplyMultiId(query, filter.TagsCriterion, s => s.StudioTags.Select(st => st.TagId));

            // String criteria
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

            if (filter.RemoteIdCriterion != null)
            {
                query = filter.RemoteIdCriterion.Modifier switch
                {
                    CriterionModifier.IsNull => query.Where(s => s.RemoteIds.Count == 0),
                    CriterionModifier.NotNull => query.Where(s => s.RemoteIds.Count > 0),
                    _ => query.Where(s => s.RemoteIds.Any(sid => EF.Functions.ILike(sid.Endpoint, $"%{filter.RemoteIdCriterion.Value}%"))),
                };
            }

            // Timestamp criteria
            query = FilterHelpers.ApplyTimestamp(query, filter.CreatedAtCriterion, s => s.CreatedAt);
            query = FilterHelpers.ApplyTimestamp(query, filter.UpdatedAtCriterion, s => s.UpdatedAt);
        }
        if (findFilter != null && !string.IsNullOrEmpty(findFilter.Q))
            query = query.Where(s => EF.Functions.ILike(s.Name, $"%{findFilter.Q}%"));

        var totalCount = await query.CountAsync(ct);
        var sort = findFilter?.Sort ?? "name";
        var desc = findFilter?.Direction == Core.Enums.SortDirection.Desc;
        query = sort switch
        {
            "name" => desc ? query.OrderByDescending(s => s.Name) : query.OrderBy(s => s.Name),
            _ => desc ? query.OrderByDescending(s => s.UpdatedAt) : query.OrderBy(s => s.UpdatedAt),
        };
        var page = findFilter?.Page ?? 1;
        var perPage = findFilter?.PerPage ?? 25;
        var items = await query.Skip((page - 1) * perPage).Take(perPage).AsNoTracking().ToListAsync(ct);
        return (items, totalCount);
    }
}

public class GalleryRepository : IGalleryRepository
{
    private readonly CoveContext _db;
    public GalleryRepository(CoveContext db) => _db = db;

    public async Task<Gallery?> GetByIdAsync(int id, CancellationToken ct = default) => await _db.Galleries.FindAsync([id], ct);

    public async Task<Gallery?> GetByIdWithRelationsAsync(int id, CancellationToken ct = default)
        => await _db.Galleries
            .Include(g => g.Studio).Include(g => g.Urls)
            .Include(g => g.GalleryTags).ThenInclude(gt => gt.Tag)
            .Include(g => g.GalleryPerformers).ThenInclude(gp => gp.Performer)
            .Include(g => g.Chapters)
            .Include(g => g.Files).ThenInclude(f => f.ParentFolder)
            .Include(g => g.Files).ThenInclude(f => f.Fingerprints)
            .Include(g => g.Folder)
            .Include(g => g.SceneGalleries)
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == id, ct);

    public async Task<IReadOnlyList<Gallery>> GetAllAsync(CancellationToken ct = default)
        => await _db.Galleries.AsNoTracking().ToListAsync(ct);

    public async Task<Gallery> AddAsync(Gallery entity, CancellationToken ct = default)
    {
        _db.Galleries.Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task UpdateAsync(Gallery entity, CancellationToken ct = default)
    {
        _db.Galleries.Update(entity);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var entity = await _db.Galleries.FindAsync([id], ct);
        if (entity != null) { _db.Galleries.Remove(entity); await _db.SaveChangesAsync(ct); }
    }

    public async Task<int> CountAsync(CancellationToken ct = default) => await _db.Galleries.CountAsync(ct);

    public async Task<(IReadOnlyList<Gallery> Items, int TotalCount)> FindAsync(GalleryFilter? filter, FindFilter? findFilter, CancellationToken ct = default)
    {
        var query = _db.Galleries.Include(g => g.GalleryTags).ThenInclude(gt => gt.Tag).AsSplitQuery().AsQueryable();
        if (filter != null)
        {
            if (!string.IsNullOrEmpty(filter.Title)) query = query.Where(g => g.Title != null && EF.Functions.ILike(g.Title, $"%{filter.Title}%"));
            if (filter.Organized.HasValue) query = query.Where(g => g.Organized == filter.Organized.Value);
            if (filter.StudioId.HasValue) query = query.Where(g => g.StudioId == filter.StudioId.Value);
            if (filter.TagIds?.Count > 0) query = query.Where(g => g.GalleryTags.Any(gt => filter.TagIds.Contains(gt.TagId)));
            if (filter.PerformerIds?.Count > 0) query = query.Where(g => g.GalleryPerformers.Any(gp => filter.PerformerIds.Contains(gp.PerformerId)));

            // Advanced criteria
            query = FilterHelpers.ApplyInt(query, filter.RatingCriterion, g => g.Rating ?? 0);
            query = FilterHelpers.ApplyInt(query, filter.ImageCountCriterion, g => g.ImageGalleries.Count);

            if (filter.OrganizedCriterion != null)
                query = query.Where(g => g.Organized == filter.OrganizedCriterion.Value);

            if (filter.PerformerFavoriteCriterion != null)
                query = filter.PerformerFavoriteCriterion.Value
                    ? query.Where(g => g.GalleryPerformers.Any(gp => gp.Performer!.Favorite))
                    : query.Where(g => !g.GalleryPerformers.Any(gp => gp.Performer!.Favorite));

            // Multi-ID criteria
            query = FilterHelpers.ApplyMultiId(query, filter.TagsCriterion, g => g.GalleryTags.Select(gt => gt.TagId));
            query = FilterHelpers.ApplyMultiId(query, filter.PerformersCriterion, g => g.GalleryPerformers.Select(gp => gp.PerformerId));

            if (filter.StudiosCriterion != null)
            {
                var ids = filter.StudiosCriterion.Value;
                query = filter.StudiosCriterion.Modifier switch
                {
                    CriterionModifier.Includes => query.Where(g => g.StudioId.HasValue && ids.Contains(g.StudioId.Value)),
                    CriterionModifier.Excludes => query.Where(g => !g.StudioId.HasValue || !ids.Contains(g.StudioId.Value)),
                    _ => query.Where(g => g.StudioId.HasValue && ids.Contains(g.StudioId.Value)),
                };
            }

            // Path criterion
            if (filter.PathCriterion != null)
            {
                var val = filter.PathCriterion.Value;
                query = filter.PathCriterion.Modifier switch
                {
                    CriterionModifier.Equals => query.Where(g => g.Files.Any(f => f.Basename == val)),
                    CriterionModifier.NotEquals => query.Where(g => !g.Files.Any(f => f.Basename == val)),
                    CriterionModifier.Includes => query.Where(g => g.Files.Any(f => EF.Functions.ILike(f.Basename, $"%{val}%"))),
                    CriterionModifier.Excludes => query.Where(g => !g.Files.Any(f => EF.Functions.ILike(f.Basename, $"%{val}%"))),
                    _ => query,
                };
            }

            // URL criterion
            if (filter.UrlCriterion != null)
            {
                var val = filter.UrlCriterion.Value;
                query = filter.UrlCriterion.Modifier switch
                {
                    CriterionModifier.Includes => query.Where(g => g.Urls.Any(u => EF.Functions.ILike(u.Url, $"%{val}%"))),
                    CriterionModifier.Excludes => query.Where(g => !g.Urls.Any(u => EF.Functions.ILike(u.Url, $"%{val}%"))),
                    CriterionModifier.IsNull => query.Where(g => g.Urls.Count == 0),
                    CriterionModifier.NotNull => query.Where(g => g.Urls.Count > 0),
                    _ => query.Where(g => g.Urls.Any(u => EF.Functions.ILike(u.Url, $"%{val}%"))),
                };
            }

            // Date criterion
            query = FilterHelpers.ApplyDate(query, filter.DateCriterion, g => g.Date);

            // Timestamp criteria
            query = FilterHelpers.ApplyTimestamp(query, filter.CreatedAtCriterion, g => g.CreatedAt);
            query = FilterHelpers.ApplyTimestamp(query, filter.UpdatedAtCriterion, g => g.UpdatedAt);
        }
        if (findFilter != null && !string.IsNullOrEmpty(findFilter.Q))
            query = query.Where(g => (g.Title != null && EF.Functions.ILike(g.Title, $"%{findFilter.Q}%")));

        var totalCount = await query.CountAsync(ct);
        var sort = findFilter?.Sort ?? "updated_at";
        var desc = findFilter?.Direction == Core.Enums.SortDirection.Desc;
        query = sort switch
        {
            "title" => desc ? query.OrderByDescending(g => g.Title) : query.OrderBy(g => g.Title),
            "image_count" => desc ? query.OrderByDescending(g => g.ImageGalleries.Count) : query.OrderBy(g => g.ImageGalleries.Count),
            "rating" => desc ? query.OrderByDescending(g => g.Rating) : query.OrderBy(g => g.Rating),
            "created_at" => desc ? query.OrderByDescending(g => g.CreatedAt) : query.OrderBy(g => g.CreatedAt),
            "random" => query.OrderBy(g => EF.Functions.Random()),
            _ => desc ? query.OrderByDescending(g => g.UpdatedAt) : query.OrderBy(g => g.UpdatedAt),
        };
        var page = findFilter?.Page ?? 1;
        var perPage = findFilter?.PerPage ?? 25;
        var items = await query.Skip((page - 1) * perPage).Take(perPage).AsNoTracking().ToListAsync(ct);
        return (items, totalCount);
    }
}

public class ImageRepository : IImageRepository
{
    private readonly CoveContext _db;
    public ImageRepository(CoveContext db) => _db = db;

    public async Task<Image?> GetByIdAsync(int id, CancellationToken ct = default) => await _db.Images.FindAsync([id], ct);

    public async Task<Image?> GetByIdWithRelationsAsync(int id, CancellationToken ct = default)
        => await _db.Images
            .Include(i => i.Studio).Include(i => i.Urls)
            .Include(i => i.ImageTags).ThenInclude(it => it.Tag)
            .Include(i => i.ImagePerformers).ThenInclude(ip => ip.Performer)
            .Include(i => i.ImageGalleries)
            .Include(i => i.Files).ThenInclude(f => f.ParentFolder)
            .AsSplitQuery()
            .FirstOrDefaultAsync(i => i.Id == id, ct);

    public async Task<IReadOnlyList<Image>> GetAllAsync(CancellationToken ct = default)
        => await _db.Images.AsNoTracking().ToListAsync(ct);

    public async Task<Image> AddAsync(Image entity, CancellationToken ct = default)
    {
        _db.Images.Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task UpdateAsync(Image entity, CancellationToken ct = default)
    {
        _db.Images.Update(entity);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var entity = await _db.Images.FindAsync([id], ct);
        if (entity != null) { _db.Images.Remove(entity); await _db.SaveChangesAsync(ct); }
    }

    public async Task<int> CountAsync(CancellationToken ct = default) => await _db.Images.CountAsync(ct);

    public async Task<(IReadOnlyList<Image> Items, int TotalCount)> FindAsync(ImageFilter? filter, FindFilter? findFilter, CancellationToken ct = default)
    {
        ExpandedHierarchicalTagCriterion? expandedTags = null;
        if (filter?.TagsCriterion?.Depth == -1)
        {
            expandedTags = await ExpandHierarchicalTagCriterionAsync(filter.TagsCriterion, ct);
            filter.TagsCriterion = expandedTags.Criterion;
        }

        // Build filter query once (lightweight, no includes)
        var filterQuery = _db.Images.AsQueryable();
        filterQuery = ApplyImageFilters(filterQuery, filter, expandedTags?.ValueGroups);
        if (findFilter != null && !string.IsNullOrEmpty(findFilter.Q))
            filterQuery = filterQuery.Where(i => (i.Title != null && EF.Functions.ILike(i.Title, $"%{findFilter.Q}%")));

        var perPage = findFilter?.PerPage ?? 25;
        var totalCount = await filterQuery.AsNoTracking().CountAsync(ct);

        // Sort and paginate on the lightweight query, then fetch only the IDs
        var sort = findFilter?.Sort ?? "updated_at";
        var desc = findFilter?.Direction == Core.Enums.SortDirection.Desc;
        filterQuery = sort switch
        {
            "title" => desc ? filterQuery.OrderByDescending(i => i.Title) : filterQuery.OrderBy(i => i.Title),
            "rating" => desc ? filterQuery.OrderByDescending(i => i.Rating) : filterQuery.OrderBy(i => i.Rating),
            "o_counter" => desc ? filterQuery.OrderByDescending(i => i.OCounter) : filterQuery.OrderBy(i => i.OCounter),
            "random" => filterQuery.OrderBy(_ => EF.Functions.Random()),
            "created_at" => desc ? filterQuery.OrderByDescending(i => i.CreatedAt) : filterQuery.OrderBy(i => i.CreatedAt),
            _ => desc ? filterQuery.OrderByDescending(i => i.UpdatedAt) : filterQuery.OrderBy(i => i.UpdatedAt),
        };

        var page = findFilter?.Page ?? 1;
        var pagedIds = await filterQuery
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .Select(i => i.Id)
            .ToListAsync(ct);

        if (pagedIds.Count == 0)
            return (Array.Empty<Image>(), totalCount);

        // Load full entities only for the paged IDs
        var items = await _db.Images
            .Include(i => i.ImageTags).ThenInclude(it => it.Tag)
            .Include(i => i.ImagePerformers).ThenInclude(ip => ip.Performer)
            .Include(i => i.ImageGalleries)
            .Include(i => i.Files)
            .AsSplitQuery()
            .Where(i => pagedIds.Contains(i.Id))
            .AsNoTracking()
            .ToListAsync(ct);

        // Restore the sort order from the paged IDs
        var orderMap = pagedIds.Select((id, idx) => (id, idx)).ToDictionary(x => x.id, x => x.idx);
        var sorted = items.OrderBy(i => orderMap.GetValueOrDefault(i.Id, int.MaxValue)).ToList();

        return (sorted, totalCount);
    }

    private static IQueryable<Image> ApplyImageFilters(IQueryable<Image> query, ImageFilter? filter, IReadOnlyList<int[]>? hierarchicalTagGroups = null)
    {
        if (filter == null) return query;

        // Simple filters
        if (!string.IsNullOrEmpty(filter.Title)) query = query.Where(i => i.Title != null && EF.Functions.ILike(i.Title, $"%{filter.Title}%"));
        if (filter.Organized.HasValue) query = query.Where(i => i.Organized == filter.Organized.Value);
        if (filter.StudioId.HasValue) query = query.Where(i => i.StudioId == filter.StudioId.Value);
        if (filter.GalleryId.HasValue) query = query.Where(i => i.ImageGalleries.Any(ig => ig.GalleryId == filter.GalleryId.Value));
        if (filter.TagIds?.Count > 0) query = query.Where(i => i.ImageTags.Any(it => filter.TagIds.Contains(it.TagId)));
        if (filter.PerformerIds?.Count > 0) query = query.Where(i => i.ImagePerformers.Any(ip => filter.PerformerIds.Contains(ip.PerformerId)));

        // Advanced criteria
        if (filter.RatingCriterion != null)
            query = FilterHelpers.ApplyInt(query, filter.RatingCriterion, i => i.Rating ?? 0);
        if (filter.OCounterCriterion != null)
            query = FilterHelpers.ApplyInt(query, filter.OCounterCriterion, i => i.OCounter);
        if (filter.OrganizedCriterion != null)
            query = query.Where(i => i.Organized == filter.OrganizedCriterion.Value);
        if (filter.ResolutionCriterion != null)
            query = FilterHelpers.ApplyInt(query, filter.ResolutionCriterion, i => i.Files.Select(f => f.Height).Max());

        // Multi-ID criteria
        if (filter.TagsCriterion != null)
            query = FilterHelpers.ApplyMultiId(query, filter.TagsCriterion, i => i.ImageTags.Select(it => it.TagId), hierarchicalTagGroups);
        if (filter.PerformersCriterion != null)
            query = FilterHelpers.ApplyMultiId(query, filter.PerformersCriterion, i => i.ImagePerformers.Select(ip => ip.PerformerId));

        if (filter.StudiosCriterion != null)
        {
            var ids = filter.StudiosCriterion.Value;
            query = filter.StudiosCriterion.Modifier switch
            {
                CriterionModifier.Includes => query.Where(i => i.StudioId.HasValue && ids.Contains(i.StudioId.Value)),
                CriterionModifier.Excludes => query.Where(i => !i.StudioId.HasValue || !ids.Contains(i.StudioId.Value)),
                _ => query.Where(i => i.StudioId.HasValue && ids.Contains(i.StudioId.Value)),
            };
        }

        if (filter.GalleriesCriterion != null)
            query = FilterHelpers.ApplyMultiId(query, filter.GalleriesCriterion, i => i.ImageGalleries.Select(ig => ig.GalleryId));

        if (filter.PathCriterion != null)
        {
            var val = filter.PathCriterion.Value;
            query = filter.PathCriterion.Modifier switch
            {
                CriterionModifier.Equals => query.Where(i => i.Files.Any(f => f.Basename == val)),
                CriterionModifier.NotEquals => query.Where(i => !i.Files.Any(f => f.Basename == val)),
                CriterionModifier.Includes => query.Where(i => i.Files.Any(f => EF.Functions.ILike(f.Basename, $"%{val}%"))),
                CriterionModifier.Excludes => query.Where(i => !i.Files.Any(f => EF.Functions.ILike(f.Basename, $"%{val}%"))),
                _ => query,
            };
        }

        if (filter.PerformerFavoriteCriterion != null)
            query = filter.PerformerFavoriteCriterion.Value
                ? query.Where(i => i.ImagePerformers.Any(ip => ip.Performer!.Favorite))
                : query.Where(i => !i.ImagePerformers.Any(ip => ip.Performer!.Favorite));

        query = FilterHelpers.ApplyTimestamp(query, filter.CreatedAtCriterion, i => i.CreatedAt);
        query = FilterHelpers.ApplyTimestamp(query, filter.UpdatedAtCriterion, i => i.UpdatedAt);

        return query;
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
}

public class GroupRepository : IGroupRepository
{
    private readonly CoveContext _db;
    public GroupRepository(CoveContext db) => _db = db;

    public async Task<Group?> GetByIdAsync(int id, CancellationToken ct = default) => await _db.Groups.FindAsync([id], ct);

    public async Task<Group?> GetByIdWithRelationsAsync(int id, CancellationToken ct = default)
        => await _db.Groups
            .Include(g => g.Studio).Include(g => g.Urls)
            .Include(g => g.GroupTags).ThenInclude(gt => gt.Tag)
            .Include(g => g.SceneGroups).ThenInclude(sg => sg.Scene)
            .Include(g => g.SubGroupRelations)
            .Include(g => g.ContainingGroupRelations)
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == id, ct);

    public async Task<IReadOnlyList<Group>> GetAllAsync(CancellationToken ct = default)
        => await _db.Groups.AsNoTracking().OrderBy(g => g.Name).ToListAsync(ct);

    public async Task<Group> AddAsync(Group entity, CancellationToken ct = default)
    {
        _db.Groups.Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task UpdateAsync(Group entity, CancellationToken ct = default)
    {
        _db.Groups.Update(entity);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var entity = await _db.Groups.FindAsync([id], ct);
        if (entity != null) { _db.Groups.Remove(entity); await _db.SaveChangesAsync(ct); }
    }

    public async Task<int> CountAsync(CancellationToken ct = default) => await _db.Groups.CountAsync(ct);

    public async Task<(IReadOnlyList<Group> Items, int TotalCount)> FindAsync(GroupFilter? filter, FindFilter? findFilter, CancellationToken ct = default)
    {
        var query = _db.Groups.Include(g => g.GroupTags).ThenInclude(gt => gt.Tag).AsSplitQuery().AsQueryable();
        if (filter != null)
        {
            if (!string.IsNullOrEmpty(filter.Name)) query = query.Where(g => EF.Functions.ILike(g.Name, $"%{filter.Name}%"));
            if (filter.StudioId.HasValue) query = query.Where(g => g.StudioId == filter.StudioId.Value);
            if (filter.TagIds?.Count > 0)
                query = query.Where(g => g.GroupTags.Any(gt => filter.TagIds.Contains(gt.TagId)));

            // Advanced criteria
            query = FilterHelpers.ApplyInt(query, filter.RatingCriterion, g => g.Rating ?? 0);
            query = FilterHelpers.ApplyInt(query, filter.DurationCriterion, g => g.Duration ?? 0);

            // Multi-ID criteria
            query = FilterHelpers.ApplyMultiId(query, filter.TagsCriterion, g => g.GroupTags.Select(gt => gt.TagId));

            if (filter.StudiosCriterion != null)
            {
                var ids = filter.StudiosCriterion.Value;
                query = filter.StudiosCriterion.Modifier switch
                {
                    CriterionModifier.Includes => query.Where(g => g.StudioId.HasValue && ids.Contains(g.StudioId.Value)),
                    CriterionModifier.Excludes => query.Where(g => !g.StudioId.HasValue || !ids.Contains(g.StudioId.Value)),
                    _ => query.Where(g => g.StudioId.HasValue && ids.Contains(g.StudioId.Value)),
                };
            }

            // URL criterion
            if (filter.UrlCriterion != null)
            {
                var val = filter.UrlCriterion.Value;
                query = filter.UrlCriterion.Modifier switch
                {
                    CriterionModifier.Includes => query.Where(g => g.Urls.Any(u => EF.Functions.ILike(u.Url, $"%{val}%"))),
                    CriterionModifier.Excludes => query.Where(g => !g.Urls.Any(u => EF.Functions.ILike(u.Url, $"%{val}%"))),
                    CriterionModifier.IsNull => query.Where(g => g.Urls.Count == 0),
                    CriterionModifier.NotNull => query.Where(g => g.Urls.Count > 0),
                    _ => query.Where(g => g.Urls.Any(u => EF.Functions.ILike(u.Url, $"%{val}%"))),
                };
            }

            // Date criterion
            query = FilterHelpers.ApplyDate(query, filter.DateCriterion, g => g.Date);

            // Timestamp criteria
            query = FilterHelpers.ApplyTimestamp(query, filter.CreatedAtCriterion, g => g.CreatedAt);
            query = FilterHelpers.ApplyTimestamp(query, filter.UpdatedAtCriterion, g => g.UpdatedAt);
        }
        if (findFilter != null && !string.IsNullOrEmpty(findFilter.Q))
            query = query.Where(g => EF.Functions.ILike(g.Name, $"%{findFilter.Q}%"));

        var totalCount = await query.CountAsync(ct);
        var sort = findFilter?.Sort ?? "name";
        var desc = findFilter?.Direction == Core.Enums.SortDirection.Desc;
        query = sort switch
        {
            "name" => desc ? query.OrderByDescending(g => g.Name) : query.OrderBy(g => g.Name),
            _ => desc ? query.OrderByDescending(g => g.UpdatedAt) : query.OrderBy(g => g.UpdatedAt),
        };
        var page = findFilter?.Page ?? 1;
        var perPage = findFilter?.PerPage ?? 25;
        var items = await query.Skip((page - 1) * perPage).Take(perPage).AsNoTracking().ToListAsync(ct);
        return (items, totalCount);
    }
}

public class SavedFilterRepository : ISavedFilterRepository
{
    private readonly CoveContext _db;
    public SavedFilterRepository(CoveContext db) => _db = db;

    public async Task<SavedFilter?> GetByIdAsync(int id, CancellationToken ct = default) => await _db.SavedFilters.FindAsync([id], ct);
    public async Task<IReadOnlyList<SavedFilter>> GetAllAsync(CancellationToken ct = default) => await _db.SavedFilters.AsNoTracking().ToListAsync(ct);
    public async Task<IReadOnlyList<SavedFilter>> GetByModeAsync(Core.Enums.FilterMode mode, CancellationToken ct = default)
        => await _db.SavedFilters.Where(f => f.Mode == mode).AsNoTracking().ToListAsync(ct);

    public async Task<SavedFilter> AddAsync(SavedFilter entity, CancellationToken ct = default)
    {
        _db.SavedFilters.Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task UpdateAsync(SavedFilter entity, CancellationToken ct = default) { _db.SavedFilters.Update(entity); await _db.SaveChangesAsync(ct); }
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var entity = await _db.SavedFilters.FindAsync([id], ct);
        if (entity != null) { _db.SavedFilters.Remove(entity); await _db.SaveChangesAsync(ct); }
    }
    public async Task<int> CountAsync(CancellationToken ct = default) => await _db.SavedFilters.CountAsync(ct);
}

public class SceneMarkerRepository : ISceneMarkerRepository
{
    private readonly CoveContext _db;
    public SceneMarkerRepository(CoveContext db) => _db = db;

    public async Task<SceneMarker?> GetByIdAsync(int id, CancellationToken ct = default) => await _db.SceneMarkers.FindAsync([id], ct);
    public async Task<IReadOnlyList<SceneMarker>> GetAllAsync(CancellationToken ct = default) => await _db.SceneMarkers.AsNoTracking().ToListAsync(ct);
    public async Task<IReadOnlyList<SceneMarker>> GetBySceneIdAsync(int sceneId, CancellationToken ct = default)
        => await _db.SceneMarkers.Include(m => m.PrimaryTag).Where(m => m.SceneId == sceneId).AsNoTracking().ToListAsync(ct);

    public async Task<SceneMarker> AddAsync(SceneMarker entity, CancellationToken ct = default)
    {
        _db.SceneMarkers.Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task UpdateAsync(SceneMarker entity, CancellationToken ct = default) { _db.SceneMarkers.Update(entity); await _db.SaveChangesAsync(ct); }
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var entity = await _db.SceneMarkers.FindAsync([id], ct);
        if (entity != null) { _db.SceneMarkers.Remove(entity); await _db.SaveChangesAsync(ct); }
    }
    public async Task<int> CountAsync(CancellationToken ct = default) => await _db.SceneMarkers.CountAsync(ct);
}
