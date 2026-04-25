using Microsoft.EntityFrameworkCore;
using Regex = System.Text.RegularExpressions.Regex;
using RegexOptions = System.Text.RegularExpressions.RegexOptions;
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
        ExpandedHierarchicalStudioCriterion? expandedStudios = null;
        if (filter?.StudiosCriterion?.Depth == -1)
        {
            expandedStudios = await ExpandHierarchicalStudioCriterionAsync(filter.StudiosCriterion, ct);
            filter.StudiosCriterion = expandedStudios.Criterion;
        }

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
            query = FilterHelpers.ApplyString(query, filter.NameCriterion, p => p.Name);
            query = FilterHelpers.ApplyInt(query, filter.RatingCriterion, p => p.Rating ?? 0);
            query = FilterHelpers.ApplyInt(query, filter.HeightCriterion, p => p.HeightCm ?? 0);
            query = FilterHelpers.ApplyInt(query, filter.WeightCriterion, p => p.Weight ?? 0);

            if (filter.SceneCountCriterion != null)
            {
                query = filter.SceneCountCriterion.Modifier switch
                {
                    CriterionModifier.IsNull => query.Where(p => !p.ScenePerformers.Any()),
                    CriterionModifier.NotNull => query.Where(p => p.ScenePerformers.Any()),
                    _ => FilterHelpers.ApplyInt(query, filter.SceneCountCriterion, p => p.ScenePerformers.Count),
                };
            }

            if (filter.StudioCountCriterion != null)
            {
                query = filter.StudioCountCriterion.Modifier switch
                {
                    CriterionModifier.IsNull => query.Where(p => !p.ScenePerformers.Any(sp => sp.Scene != null && sp.Scene.StudioId.HasValue)),
                    CriterionModifier.NotNull => query.Where(p => p.ScenePerformers.Any(sp => sp.Scene != null && sp.Scene.StudioId.HasValue)),
                    _ => FilterHelpers.ApplyInt(query, filter.StudioCountCriterion, p => p.ScenePerformers
                        .Where(sp => sp.Scene != null && sp.Scene.StudioId.HasValue)
                        .Select(sp => sp.Scene!.StudioId!.Value)
                        .Distinct()
                        .Count()),
                };
            }

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
            if (filter.StudiosCriterion is { Modifier: CriterionModifier.IncludesAll, Value.Count: > 0 } studiosCriterion)
            {
                if (expandedStudios?.ValueGroups is { Count: > 0 } studioGroups)
                {
                    foreach (var studioGroup in studioGroups.Where(group => group.Length > 0))
                    {
                        var requiredGroup = studioGroup;
                        query = query.Where(p => p.ScenePerformers.Any(sp => sp.Scene != null && sp.Scene.StudioId.HasValue && requiredGroup.Contains(sp.Scene.StudioId.Value)));
                    }
                }
                else
                {
                    foreach (var studioId in studiosCriterion.Value.Distinct())
                    {
                        var requiredStudioId = studioId;
                        query = query.Where(p => p.ScenePerformers.Any(sp => sp.Scene != null && sp.Scene.StudioId == requiredStudioId));
                    }
                }

                if (studiosCriterion.Excludes?.Count > 0)
                {
                    var excludedStudioIds = studiosCriterion.Excludes.Distinct().ToArray();
                    query = query.Where(p => !p.ScenePerformers.Any(sp => sp.Scene != null && sp.Scene.StudioId.HasValue && excludedStudioIds.Contains(sp.Scene.StudioId.Value)));
                }
            }
            else
            {
                query = FilterHelpers.ApplyMultiId(
                    query,
                    filter.StudiosCriterion,
                    p => p.ScenePerformers
                        .Where(sp => sp.Scene != null && sp.Scene.StudioId.HasValue)
                        .Select(sp => sp.Scene!.StudioId!.Value),
                    expandedStudios?.ValueGroups);
            }

            // Date criteria
            query = FilterHelpers.ApplyDate(query, filter.BirthdateCriterion, p => p.Birthdate);
            query = FilterHelpers.ApplyDate(query, filter.DeathDateCriterion, p => p.DeathDate);
            query = FilterHelpers.ApplyDate(query, filter.CareerStartCriterion, p => p.CareerStart);
            query = FilterHelpers.ApplyDate(query, filter.CareerEndCriterion, p => p.CareerEnd);

            // Timestamp criteria
            query = FilterHelpers.ApplyTimestamp(query, filter.CreatedAtCriterion, p => p.CreatedAt);
            query = FilterHelpers.ApplyTimestamp(query, filter.UpdatedAtCriterion, p => p.UpdatedAt);

            // String criteria for new fields
            query = FilterHelpers.ApplyString(query, filter.DisambiguationCriterion, p => p.Disambiguation);
            query = FilterHelpers.ApplyString(query, filter.DetailsCriterion, p => p.Details);
            query = FilterHelpers.ApplyString(query, filter.EyeColorCriterion, p => p.EyeColor);
            query = FilterHelpers.ApplyString(query, filter.HairColorCriterion, p => p.HairColor);
            query = FilterHelpers.ApplyString(query, filter.MeasurementsCriterion, p => p.Measurements);
            query = FilterHelpers.ApplyString(query, filter.FakeTitsCriterion, p => p.FakeTits);
            if (filter.CircumcisedCriterion != null)
            {
                var val = filter.CircumcisedCriterion.Value;
                if (Enum.TryParse<Core.Enums.CircumcisedEnum>(val, true, out var circumVal))
                {
                    query = filter.CircumcisedCriterion.Modifier switch
                    {
                        CriterionModifier.Equals => query.Where(p => p.Circumcised == circumVal),
                        CriterionModifier.NotEquals => query.Where(p => p.Circumcised != circumVal),
                        CriterionModifier.IsNull => query.Where(p => p.Circumcised == null),
                        CriterionModifier.NotNull => query.Where(p => p.Circumcised != null),
                        _ => query.Where(p => p.Circumcised == circumVal),
                    };
                }
                else
                {
                    query = filter.CircumcisedCriterion.Modifier switch
                    {
                        CriterionModifier.IsNull => query.Where(p => p.Circumcised == null),
                        CriterionModifier.NotNull => query.Where(p => p.Circumcised != null),
                        _ => query,
                    };
                }
            }
            query = FilterHelpers.ApplyString(query, filter.TattooCriterion, p => p.Tattoos);
            query = FilterHelpers.ApplyString(query, filter.PiercingsCriterion, p => p.Piercings);

            // Aliases criterion
            if (filter.AliasesCriterion != null)
            {
                var aliasVal = filter.AliasesCriterion.Value;
                query = filter.AliasesCriterion.Modifier switch
                {
                    CriterionModifier.Includes => query.Where(p => p.Aliases.Any(a => EF.Functions.ILike(a.Alias, $"%{aliasVal}%"))),
                    CriterionModifier.Excludes => query.Where(p => !p.Aliases.Any(a => EF.Functions.ILike(a.Alias, $"%{aliasVal}%"))),
                    CriterionModifier.IsNull => query.Where(p => p.Aliases.Count == 0),
                    CriterionModifier.NotNull => query.Where(p => p.Aliases.Count > 0),
                    _ => query.Where(p => p.Aliases.Any(a => EF.Functions.ILike(a.Alias, $"%{aliasVal}%"))),
                };
            }

            if (filter.RemoteIdValueCriterion != null)
            {
                var remoteIdValue = filter.RemoteIdValueCriterion.Value?.Trim() ?? string.Empty;

                query = filter.RemoteIdValueCriterion.Modifier switch
                {
                    CriterionModifier.Equals when remoteIdValue.Length > 0 => query.Where(p => p.RemoteIds.Any(sid => sid.RemoteId == remoteIdValue)),
                    CriterionModifier.NotEquals when remoteIdValue.Length > 0 => query.Where(p => !p.RemoteIds.Any(sid => sid.RemoteId == remoteIdValue)),
                    CriterionModifier.Includes when remoteIdValue.Length > 0 => query.Where(p => p.RemoteIds.Any(sid => EF.Functions.ILike(sid.RemoteId, $"%{remoteIdValue}%"))),
                    CriterionModifier.Excludes when remoteIdValue.Length > 0 => query.Where(p => !p.RemoteIds.Any(sid => EF.Functions.ILike(sid.RemoteId, $"%{remoteIdValue}%"))),
                    CriterionModifier.IsNull when remoteIdValue.Length > 0 => query.Where(p => !p.RemoteIds.Any(sid => EF.Functions.ILike(sid.RemoteId, $"%{remoteIdValue}%"))),
                    CriterionModifier.NotNull when remoteIdValue.Length > 0 => query.Where(p => p.RemoteIds.Any(sid => EF.Functions.ILike(sid.RemoteId, $"%{remoteIdValue}%"))),
                    CriterionModifier.IsNull => query.Where(p => p.RemoteIds.Count == 0),
                    CriterionModifier.NotNull => query.Where(p => p.RemoteIds.Count > 0),
                    _ => query,
                };
            }

            // PenisLength as int (rounded)
            query = FilterHelpers.ApplyInt(query, filter.PenisLengthCriterion, p => (int)(p.PenisLength ?? 0));

            // Count criteria
            query = FilterHelpers.ApplyInt(query, filter.TagCountCriterion, p => p.PerformerTags.Count);
            query = FilterHelpers.ApplyInt(query, filter.PlayCountCriterion, p => p.ScenePerformers.Sum(sp => sp.Scene!.PlayCount));
            query = FilterHelpers.ApplyInt(query, filter.OCounterCriterion, p => p.ScenePerformers.Sum(sp => sp.Scene!.OCounter));

            // Groups criterion
            if (filter.GroupsCriterion != null)
            {
                var gIds = filter.GroupsCriterion.Value;
                query = filter.GroupsCriterion.Modifier switch
                {
                    CriterionModifier.Includes => query.Where(p => p.ScenePerformers.Any(sp => sp.Scene!.SceneGroups.Any(sg => gIds.Contains(sg.GroupId)))),
                    CriterionModifier.Excludes => query.Where(p => !p.ScenePerformers.Any(sp => sp.Scene!.SceneGroups.Any(sg => gIds.Contains(sg.GroupId)))),
                    _ => query.Where(p => p.ScenePerformers.Any(sp => sp.Scene!.SceneGroups.Any(sg => gIds.Contains(sg.GroupId)))),
                };
            }

            // IgnoreAutoTag criterion
            if (filter.IgnoreAutoTagCriterion != null)
                query = query.Where(p => p.IgnoreAutoTag == filter.IgnoreAutoTagCriterion.Value);

            // Marker count criterion
            if (filter.MarkerCountCriterion != null)
            {
                var mcVal = filter.MarkerCountCriterion.Value;
                var mcVal2 = filter.MarkerCountCriterion.Value2 ?? mcVal;
                query = filter.MarkerCountCriterion.Modifier switch
                {
                    CriterionModifier.Equals => query.Where(p => p.ScenePerformers.SelectMany(sp => sp.Scene!.SceneMarkers).Count() == mcVal),
                    CriterionModifier.NotEquals => query.Where(p => p.ScenePerformers.SelectMany(sp => sp.Scene!.SceneMarkers).Count() != mcVal),
                    CriterionModifier.GreaterThan => query.Where(p => p.ScenePerformers.SelectMany(sp => sp.Scene!.SceneMarkers).Count() > mcVal),
                    CriterionModifier.LessThan => query.Where(p => p.ScenePerformers.SelectMany(sp => sp.Scene!.SceneMarkers).Count() < mcVal),
                    CriterionModifier.Between => query.Where(p => p.ScenePerformers.SelectMany(sp => sp.Scene!.SceneMarkers).Count() >= mcVal &&
                        p.ScenePerformers.SelectMany(sp => sp.Scene!.SceneMarkers).Count() <= mcVal2),
                    _ => query,
                };
            }

            // RemoteId criterion
            if (filter.RemoteIdCriterion != null)
            {
                var providerValue = filter.RemoteIdCriterion.Value?.Trim() ?? string.Empty;
                var normalizedProvider = providerValue.ToLower();
                var hasProviderFilter = !string.IsNullOrWhiteSpace(providerValue);

                query = filter.RemoteIdCriterion.Modifier switch
                {
                    CriterionModifier.Equals when hasProviderFilter => query.Where(p => p.RemoteIds.Any(sid => sid.Endpoint.ToLower() == normalizedProvider)),
                    CriterionModifier.NotEquals when hasProviderFilter => query.Where(p => !p.RemoteIds.Any(sid => sid.Endpoint.ToLower() == normalizedProvider)),
                    CriterionModifier.Includes when hasProviderFilter => query.Where(p => p.RemoteIds.Any(sid => sid.Endpoint.ToLower().Contains(normalizedProvider))),
                    CriterionModifier.Excludes when hasProviderFilter => query.Where(p => !p.RemoteIds.Any(sid => sid.Endpoint.ToLower().Contains(normalizedProvider))),
                    CriterionModifier.IsNull when hasProviderFilter => query.Where(p => !p.RemoteIds.Any(sid => sid.Endpoint.ToLower().Contains(normalizedProvider))),
                    CriterionModifier.NotNull when hasProviderFilter => query.Where(p => p.RemoteIds.Any(sid => sid.Endpoint.ToLower().Contains(normalizedProvider))),
                    CriterionModifier.IsNull => query.Where(p => p.RemoteIds.Count == 0),
                    CriterionModifier.NotNull => query.Where(p => p.RemoteIds.Count > 0),
                    _ => query,
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
            "rating" => desc
                ? query.OrderBy(p => p.Rating == null || p.Rating <= 0 ? 1 : 0).ThenByDescending(p => p.Rating)
                : query.OrderBy(p => p.Rating == null || p.Rating <= 0 ? 0 : 1).ThenBy(p => p.Rating),
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
            "random" => SeededRandomOrdering.OrderBy(query, findFilter?.Seed, p => p.Id),
            _ => desc ? query.OrderByDescending(p => p.UpdatedAt) : query.OrderBy(p => p.UpdatedAt),
        };

        var page = findFilter?.Page ?? 1;
        var perPage = findFilter?.PerPage ?? 25;
        var items = await query.Skip((page - 1) * perPage).Take(perPage).AsNoTracking().ToListAsync(ct);

        return (items, totalCount);
    }

    private sealed record ExpandedHierarchicalStudioCriterion(MultiIdCriterion Criterion, IReadOnlyList<int[]> ValueGroups);

    private async Task<ExpandedHierarchicalStudioCriterion> ExpandHierarchicalStudioCriterionAsync(MultiIdCriterion criterion, CancellationToken ct)
    {
        var studios = await _db.Studios
            .AsNoTracking()
            .Select(studio => new { studio.Id, studio.ParentId })
            .ToListAsync(ct);

        var childrenByParent = studios
            .Where(studio => studio.ParentId.HasValue)
            .GroupBy(studio => studio.ParentId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(studio => studio.Id).ToArray());

        var valueGroups = criterion.Value
            .Distinct()
            .Select(studioId => ExpandStudioGroup(studioId, childrenByParent))
            .ToList();

        var flatValue = valueGroups.SelectMany(group => group).Distinct().ToList();
        var flatExcludes = criterion.Excludes?
            .Distinct()
            .SelectMany(studioId => ExpandStudioGroup(studioId, childrenByParent))
            .Distinct()
            .ToList();

        return new ExpandedHierarchicalStudioCriterion(
            new MultiIdCriterion
            {
                Value = flatValue,
                Modifier = criterion.Modifier,
                Excludes = flatExcludes is { Count: > 0 } ? flatExcludes : null,
                Depth = criterion.Depth,
            },
            valueGroups);
    }

    private static int[] ExpandStudioGroup(int rootStudioId, IReadOnlyDictionary<int, int[]> childrenByParent)
    {
        var expanded = new HashSet<int> { rootStudioId };
        var queue = new Queue<int>();
        queue.Enqueue(rootStudioId);

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

public class TagRepository : ITagRepository
{
    private readonly record struct TagEntityPair(int TagId, int EntityId);

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
            .Include(t => t.RemoteIds)
            .AsSplitQuery()
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

            // Multi-ID criteria
            query = FilterHelpers.ApplyMultiId(query, filter.ParentsCriterion, t => t.ParentRelations.Select(tp => tp.ParentId));
            query = FilterHelpers.ApplyMultiId(query, filter.ChildrenCriterion, t => t.ChildRelations.Select(tp => tp.ChildId));

            // Timestamp criteria
            query = FilterHelpers.ApplyTimestamp(query, filter.CreatedAtCriterion, t => t.CreatedAt);
            query = FilterHelpers.ApplyTimestamp(query, filter.UpdatedAtCriterion, t => t.UpdatedAt);

            // String criteria
            query = FilterHelpers.ApplyString(query, filter.NameCriterion, t => t.Name);
            query = FilterHelpers.ApplyString(query, filter.SortNameCriterion, t => t.SortName);
            query = FilterHelpers.ApplyString(query, filter.DescriptionCriterion, t => t.Description);

            if (filter.RemoteIdValueCriterion != null)
            {
                var remoteIdValue = filter.RemoteIdValueCriterion.Value?.Trim() ?? string.Empty;

                query = filter.RemoteIdValueCriterion.Modifier switch
                {
                    CriterionModifier.Equals when remoteIdValue.Length > 0 => query.Where(t => t.RemoteIds.Any(remoteId => remoteId.RemoteId == remoteIdValue)),
                    CriterionModifier.NotEquals when remoteIdValue.Length > 0 => query.Where(t => !t.RemoteIds.Any(remoteId => remoteId.RemoteId == remoteIdValue)),
                    CriterionModifier.Includes when remoteIdValue.Length > 0 => query.Where(t => t.RemoteIds.Any(remoteId => EF.Functions.ILike(remoteId.RemoteId, $"%{remoteIdValue}%"))),
                    CriterionModifier.Excludes when remoteIdValue.Length > 0 => query.Where(t => !t.RemoteIds.Any(remoteId => EF.Functions.ILike(remoteId.RemoteId, $"%{remoteIdValue}%"))),
                    CriterionModifier.IsNull when remoteIdValue.Length > 0 => query.Where(t => !t.RemoteIds.Any(remoteId => EF.Functions.ILike(remoteId.RemoteId, $"%{remoteIdValue}%"))),
                    CriterionModifier.NotNull when remoteIdValue.Length > 0 => query.Where(t => t.RemoteIds.Any(remoteId => EF.Functions.ILike(remoteId.RemoteId, $"%{remoteIdValue}%"))),
                    CriterionModifier.IsNull => query.Where(t => t.RemoteIds.Count == 0),
                    CriterionModifier.NotNull => query.Where(t => t.RemoteIds.Count > 0),
                    _ => query,
                };
            }

            if (filter.RemoteIdCriterion != null)
            {
                var providerValue = filter.RemoteIdCriterion.Value?.Trim() ?? string.Empty;
                var normalizedProvider = providerValue.ToLower();
                var hasProviderFilter = !string.IsNullOrWhiteSpace(providerValue);

                query = filter.RemoteIdCriterion.Modifier switch
                {
                    CriterionModifier.Equals when hasProviderFilter => query.Where(t => t.RemoteIds.Any(remoteId => remoteId.Endpoint.ToLower() == normalizedProvider)),
                    CriterionModifier.NotEquals when hasProviderFilter => query.Where(t => !t.RemoteIds.Any(remoteId => remoteId.Endpoint.ToLower() == normalizedProvider)),
                    CriterionModifier.Includes when hasProviderFilter => query.Where(t => t.RemoteIds.Any(remoteId => remoteId.Endpoint.ToLower().Contains(normalizedProvider))),
                    CriterionModifier.Excludes when hasProviderFilter => query.Where(t => !t.RemoteIds.Any(remoteId => remoteId.Endpoint.ToLower().Contains(normalizedProvider))),
                    CriterionModifier.IsNull when hasProviderFilter => query.Where(t => !t.RemoteIds.Any(remoteId => remoteId.Endpoint.ToLower().Contains(normalizedProvider))),
                    CriterionModifier.NotNull when hasProviderFilter => query.Where(t => t.RemoteIds.Any(remoteId => remoteId.Endpoint.ToLower().Contains(normalizedProvider))),
                    CriterionModifier.IsNull => query.Where(t => t.RemoteIds.Count == 0),
                    CriterionModifier.NotNull => query.Where(t => t.RemoteIds.Count > 0),
                    _ => query,
                };
            }

            // Aliases criterion
            if (filter.AliasesCriterion != null)
            {
                var aliasVal = filter.AliasesCriterion.Value;
                query = filter.AliasesCriterion.Modifier switch
                {
                    CriterionModifier.Includes => query.Where(t => t.Aliases.Any(a => EF.Functions.ILike(a.Alias, $"%{aliasVal}%"))),
                    CriterionModifier.Excludes => query.Where(t => !t.Aliases.Any(a => EF.Functions.ILike(a.Alias, $"%{aliasVal}%"))),
                    CriterionModifier.IsNull => query.Where(t => t.Aliases.Count == 0),
                    CriterionModifier.NotNull => query.Where(t => t.Aliases.Count > 0),
                    _ => query.Where(t => t.Aliases.Any(a => EF.Functions.ILike(a.Alias, $"%{aliasVal}%"))),
                };
            }

            // IgnoreAutoTag criterion
            if (filter.IgnoreAutoTagCriterion != null)
                query = query.Where(t => t.IgnoreAutoTag == filter.IgnoreAutoTagCriterion.Value);
        }

        if (findFilter != null && !string.IsNullOrEmpty(findFilter.Q))
        {
            var q = findFilter.Q;
            query = query.Where(t =>
                EF.Functions.ILike(t.Name, $"%{q}%") ||
                (t.Description != null && EF.Functions.ILike(t.Description, $"%{q}%")) ||
                t.Aliases.Any(a => EF.Functions.ILike(a.Alias, $"%{q}%")));
        }

        if (filter != null)
        {
            query = await ApplyTagCountCriteriaAsync(query, filter, ct);
        }

        var totalCount = await query.CountAsync(ct);

        var sort = findFilter?.Sort ?? "name";
        var desc = findFilter?.Direction == Core.Enums.SortDirection.Desc;
        query = sort switch
        {
            "name" => desc ? query.OrderByDescending(t => t.Name) : query.OrderBy(t => t.Name),
            "scene_count" => desc ? query.OrderByDescending(t => t.SceneTags.Count) : query.OrderBy(t => t.SceneTags.Count),
            "gallery_count" => desc ? query.OrderByDescending(t => t.GalleryTags.Count) : query.OrderBy(t => t.GalleryTags.Count),
            "group_count" => desc ? query.OrderByDescending(t => t.GroupTags.Count) : query.OrderBy(t => t.GroupTags.Count),
            "image_count" => desc ? query.OrderByDescending(t => t.ImageTags.Count) : query.OrderBy(t => t.ImageTags.Count),
            "performer_count" => desc ? query.OrderByDescending(t => t.PerformerTags.Count) : query.OrderBy(t => t.PerformerTags.Count),
            "studio_count" => desc ? query.OrderByDescending(t => t.StudioTags.Count) : query.OrderBy(t => t.StudioTags.Count),
            "created_at" => desc ? query.OrderByDescending(t => t.CreatedAt) : query.OrderBy(t => t.CreatedAt),
            "updated_at" => desc ? query.OrderByDescending(t => t.UpdatedAt) : query.OrderBy(t => t.UpdatedAt),
            "random" => SeededRandomOrdering.OrderBy(query, findFilter?.Seed, t => t.Id),
            _ => desc ? query.OrderByDescending(t => t.UpdatedAt) : query.OrderBy(t => t.UpdatedAt),
        };

        var page = findFilter?.Page ?? 1;
        var perPage = findFilter?.PerPage ?? 25;
        var items = await query.Skip((page - 1) * perPage).Take(perPage).AsNoTracking().ToListAsync(ct);

        return (items, totalCount);
    }

    private async Task<IQueryable<Tag>> ApplyTagCountCriteriaAsync(IQueryable<Tag> query, TagFilter filter, CancellationToken ct)
    {
        query = filter.SceneCountIncludesChildren
            ? query
            : FilterHelpers.ApplyInt(query, filter.SceneCountCriterion, t => t.SceneTags.Count);
        query = filter.MarkerCountIncludesChildren
            ? query
            : ApplyDirectMarkerCountCriterion(query, filter.MarkerCountCriterion);
        query = filter.PerformerCountIncludesChildren
            ? query
            : FilterHelpers.ApplyInt(query, filter.PerformerCountCriterion, t => t.PerformerTags.Count);
        query = filter.ImageCountIncludesChildren
            ? query
            : FilterHelpers.ApplyInt(query, filter.ImageCountCriterion, t => t.ImageTags.Count);
        query = filter.GalleryCountIncludesChildren
            ? query
            : FilterHelpers.ApplyInt(query, filter.GalleryCountCriterion, t => t.GalleryTags.Count);
        query = filter.StudioCountIncludesChildren
            ? query
            : FilterHelpers.ApplyInt(query, filter.StudioCountCriterion, t => t.StudioTags.Count);
        query = filter.GroupCountIncludesChildren
            ? query
            : FilterHelpers.ApplyInt(query, filter.GroupCountCriterion, t => t.GroupTags.Count);
        query = FilterHelpers.ApplyInt(query, filter.ParentCountCriterion, t => t.ParentRelations.Count);
        query = FilterHelpers.ApplyInt(query, filter.ChildCountCriterion, t => t.ChildRelations.Count);

        if (!NeedsChildTagCountAggregation(filter))
        {
            return query;
        }

        var candidateTagIds = await query.Select(t => t.Id).ToListAsync(ct);
        if (candidateTagIds.Count == 0)
        {
            return query.Where(_ => false);
        }

        var tagAndDescendantIdsByTagId = await GetTagAndDescendantIdsByTagIdAsync(candidateTagIds, ct);
        var relevantTagIds = tagAndDescendantIdsByTagId.Values.SelectMany(tagIds => tagIds).Distinct().ToArray();
        var rootTagIdsByDescendantTagId = BuildRootTagIdsByDescendantTagId(tagAndDescendantIdsByTagId);

        Dictionary<int, int>? sceneCountsByTagId = null;
        Dictionary<int, int>? markerCountsByTagId = null;
        Dictionary<int, int>? performerCountsByTagId = null;
        Dictionary<int, int>? imageCountsByTagId = null;
        Dictionary<int, int>? galleryCountsByTagId = null;
        Dictionary<int, int>? studioCountsByTagId = null;
        Dictionary<int, int>? groupCountsByTagId = null;

        if (filter.SceneCountIncludesChildren && filter.SceneCountCriterion != null)
        {
            sceneCountsByTagId = CountDistinctEntitiesByRootTagId(
                await _db.Set<SceneTag>()
                    .AsNoTracking()
                    .Where(sceneTag => relevantTagIds.Contains(sceneTag.TagId))
                    .Select(sceneTag => new TagEntityPair(sceneTag.TagId, sceneTag.SceneId))
                    .ToListAsync(ct),
                rootTagIdsByDescendantTagId);
        }

        if (filter.MarkerCountIncludesChildren && filter.MarkerCountCriterion != null)
        {
            var primaryMarkerPairs = await _db.SceneMarkers
                .AsNoTracking()
                .Where(marker => relevantTagIds.Contains(marker.PrimaryTagId))
                .Select(marker => new TagEntityPair(marker.PrimaryTagId, marker.Id))
                .ToListAsync(ct);
            var secondaryMarkerPairs = await _db.Set<SceneMarkerTag>()
                .AsNoTracking()
                .Where(sceneMarkerTag => relevantTagIds.Contains(sceneMarkerTag.TagId))
                .Select(sceneMarkerTag => new TagEntityPair(sceneMarkerTag.TagId, sceneMarkerTag.SceneMarkerId))
                .ToListAsync(ct);

            markerCountsByTagId = CountDistinctEntitiesByRootTagId(primaryMarkerPairs.Concat(secondaryMarkerPairs), rootTagIdsByDescendantTagId);
        }

        if (filter.PerformerCountIncludesChildren && filter.PerformerCountCriterion != null)
        {
            performerCountsByTagId = CountDistinctEntitiesByRootTagId(
                await _db.Set<PerformerTag>()
                    .AsNoTracking()
                    .Where(performerTag => relevantTagIds.Contains(performerTag.TagId))
                    .Select(performerTag => new TagEntityPair(performerTag.TagId, performerTag.PerformerId))
                    .ToListAsync(ct),
                rootTagIdsByDescendantTagId);
        }

        if (filter.ImageCountIncludesChildren && filter.ImageCountCriterion != null)
        {
            imageCountsByTagId = CountDistinctEntitiesByRootTagId(
                await _db.Set<ImageTag>()
                    .AsNoTracking()
                    .Where(imageTag => relevantTagIds.Contains(imageTag.TagId))
                    .Select(imageTag => new TagEntityPair(imageTag.TagId, imageTag.ImageId))
                    .ToListAsync(ct),
                rootTagIdsByDescendantTagId);
        }

        if (filter.GalleryCountIncludesChildren && filter.GalleryCountCriterion != null)
        {
            galleryCountsByTagId = CountDistinctEntitiesByRootTagId(
                await _db.Set<GalleryTag>()
                    .AsNoTracking()
                    .Where(galleryTag => relevantTagIds.Contains(galleryTag.TagId))
                    .Select(galleryTag => new TagEntityPair(galleryTag.TagId, galleryTag.GalleryId))
                    .ToListAsync(ct),
                rootTagIdsByDescendantTagId);
        }

        if (filter.StudioCountIncludesChildren && filter.StudioCountCriterion != null)
        {
            studioCountsByTagId = CountDistinctEntitiesByRootTagId(
                await _db.Set<StudioTag>()
                    .AsNoTracking()
                    .Where(studioTag => relevantTagIds.Contains(studioTag.TagId))
                    .Select(studioTag => new TagEntityPair(studioTag.TagId, studioTag.StudioId))
                    .ToListAsync(ct),
                rootTagIdsByDescendantTagId);
        }

        if (filter.GroupCountIncludesChildren && filter.GroupCountCriterion != null)
        {
            groupCountsByTagId = CountDistinctEntitiesByRootTagId(
                await _db.Set<GroupTag>()
                    .AsNoTracking()
                    .Where(groupTag => relevantTagIds.Contains(groupTag.TagId))
                    .Select(groupTag => new TagEntityPair(groupTag.TagId, groupTag.GroupId))
                    .ToListAsync(ct),
                rootTagIdsByDescendantTagId);
        }

        var matchingTagIds = candidateTagIds.Where(tagId =>
        {
            return MatchesTagCountCriterion(filter.SceneCountCriterion, filter.SceneCountIncludesChildren, tagId, sceneCountsByTagId)
                && MatchesTagCountCriterion(filter.MarkerCountCriterion, filter.MarkerCountIncludesChildren, tagId, markerCountsByTagId)
                && MatchesTagCountCriterion(filter.PerformerCountCriterion, filter.PerformerCountIncludesChildren, tagId, performerCountsByTagId)
                && MatchesTagCountCriterion(filter.ImageCountCriterion, filter.ImageCountIncludesChildren, tagId, imageCountsByTagId)
                && MatchesTagCountCriterion(filter.GalleryCountCriterion, filter.GalleryCountIncludesChildren, tagId, galleryCountsByTagId)
                && MatchesTagCountCriterion(filter.StudioCountCriterion, filter.StudioCountIncludesChildren, tagId, studioCountsByTagId)
                && MatchesTagCountCriterion(filter.GroupCountCriterion, filter.GroupCountIncludesChildren, tagId, groupCountsByTagId);
        }).ToArray();

        if (matchingTagIds.Length == 0)
        {
            return query.Where(_ => false);
        }

        return matchingTagIds.Length == candidateTagIds.Count ? query : query.Where(tag => matchingTagIds.Contains(tag.Id));
    }

    private async Task<Dictionary<int, int[]>> GetTagAndDescendantIdsByTagIdAsync(IReadOnlyCollection<int> rootTagIds, CancellationToken ct)
    {
        var relations = await _db.Set<TagParent>()
            .AsNoTracking()
            .Select(relation => new { relation.ParentId, relation.ChildId })
            .ToListAsync(ct);

        var childIdsByParentId = relations
            .GroupBy(relation => relation.ParentId)
            .ToDictionary(group => group.Key, group => group.Select(relation => relation.ChildId).ToArray());

        var tagAndDescendantIdsByTagId = new Dictionary<int, int[]>(rootTagIds.Count);
        foreach (var rootTagId in rootTagIds)
        {
            var visited = new HashSet<int> { rootTagId };
            var queue = new Queue<int>();
            queue.Enqueue(rootTagId);

            while (queue.Count > 0)
            {
                var currentTagId = queue.Dequeue();
                if (!childIdsByParentId.TryGetValue(currentTagId, out var childIds))
                {
                    continue;
                }

                foreach (var childId in childIds)
                {
                    if (visited.Add(childId))
                    {
                        queue.Enqueue(childId);
                    }
                }
            }

            tagAndDescendantIdsByTagId[rootTagId] = [.. visited];
        }

        return tagAndDescendantIdsByTagId;
    }

    private static bool NeedsChildTagCountAggregation(TagFilter filter)
        => (filter.SceneCountIncludesChildren && filter.SceneCountCriterion != null)
        || (filter.MarkerCountIncludesChildren && filter.MarkerCountCriterion != null)
        || (filter.PerformerCountIncludesChildren && filter.PerformerCountCriterion != null)
        || (filter.ImageCountIncludesChildren && filter.ImageCountCriterion != null)
        || (filter.GalleryCountIncludesChildren && filter.GalleryCountCriterion != null)
        || (filter.StudioCountIncludesChildren && filter.StudioCountCriterion != null)
        || (filter.GroupCountIncludesChildren && filter.GroupCountCriterion != null);

    private static Dictionary<int, int[]> BuildRootTagIdsByDescendantTagId(IReadOnlyDictionary<int, int[]> tagAndDescendantIdsByTagId)
    {
        var rootTagIdsByDescendantTagId = new Dictionary<int, List<int>>();
        foreach (var (rootTagId, descendantTagIds) in tagAndDescendantIdsByTagId)
        {
            foreach (var descendantTagId in descendantTagIds)
            {
                if (!rootTagIdsByDescendantTagId.TryGetValue(descendantTagId, out var rootTagIds))
                {
                    rootTagIds = [];
                    rootTagIdsByDescendantTagId[descendantTagId] = rootTagIds;
                }

                rootTagIds.Add(rootTagId);
            }
        }

        return rootTagIdsByDescendantTagId.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray());
    }

    private static Dictionary<int, int> CountDistinctEntitiesByRootTagId(IEnumerable<TagEntityPair> pairs, IReadOnlyDictionary<int, int[]> rootTagIdsByDescendantTagId)
    {
        var entityIdsByRootTagId = new Dictionary<int, HashSet<int>>();
        foreach (var pair in pairs)
        {
            if (!rootTagIdsByDescendantTagId.TryGetValue(pair.TagId, out var rootTagIds))
            {
                continue;
            }

            foreach (var rootTagId in rootTagIds)
            {
                if (!entityIdsByRootTagId.TryGetValue(rootTagId, out var entityIds))
                {
                    entityIds = [];
                    entityIdsByRootTagId[rootTagId] = entityIds;
                }

                entityIds.Add(pair.EntityId);
            }
        }

        return entityIdsByRootTagId.ToDictionary(entry => entry.Key, entry => entry.Value.Count);
    }

    private static bool MatchesTagCountCriterion(IntCriterion? criterion, bool includeChildren, int rootTagId, Dictionary<int, int>? countsByTagId)
    {
        if (criterion == null || !includeChildren)
        {
            return true;
        }

        var value = 0;
        if (countsByTagId != null && countsByTagId.TryGetValue(rootTagId, out var count))
        {
            value = count;
        }

        return MatchesIntCriterion(criterion, value);
    }

    private IQueryable<Tag> ApplyDirectMarkerCountCriterion(IQueryable<Tag> query, IntCriterion? criterion)
    {
        if (criterion == null)
        {
            return query;
        }

        var markerCountVal = criterion.Value;
        var markerCountVal2 = criterion.Value2 ?? markerCountVal;
        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(tag =>
                _db.SceneMarkers.Count(marker => marker.PrimaryTagId == tag.Id || marker.SceneMarkerTags.Any(sceneMarkerTag => sceneMarkerTag.TagId == tag.Id)) == markerCountVal),
            CriterionModifier.NotEquals => query.Where(tag =>
                _db.SceneMarkers.Count(marker => marker.PrimaryTagId == tag.Id || marker.SceneMarkerTags.Any(sceneMarkerTag => sceneMarkerTag.TagId == tag.Id)) != markerCountVal),
            CriterionModifier.GreaterThan => query.Where(tag =>
                _db.SceneMarkers.Count(marker => marker.PrimaryTagId == tag.Id || marker.SceneMarkerTags.Any(sceneMarkerTag => sceneMarkerTag.TagId == tag.Id)) > markerCountVal),
            CriterionModifier.LessThan => query.Where(tag =>
                _db.SceneMarkers.Count(marker => marker.PrimaryTagId == tag.Id || marker.SceneMarkerTags.Any(sceneMarkerTag => sceneMarkerTag.TagId == tag.Id)) < markerCountVal),
            CriterionModifier.Between => query.Where(tag =>
                _db.SceneMarkers.Count(marker => marker.PrimaryTagId == tag.Id || marker.SceneMarkerTags.Any(sceneMarkerTag => sceneMarkerTag.TagId == tag.Id)) >= markerCountVal &&
                _db.SceneMarkers.Count(marker => marker.PrimaryTagId == tag.Id || marker.SceneMarkerTags.Any(sceneMarkerTag => sceneMarkerTag.TagId == tag.Id)) <= markerCountVal2),
            CriterionModifier.NotBetween => query.Where(tag =>
                _db.SceneMarkers.Count(marker => marker.PrimaryTagId == tag.Id || marker.SceneMarkerTags.Any(sceneMarkerTag => sceneMarkerTag.TagId == tag.Id)) < markerCountVal ||
                _db.SceneMarkers.Count(marker => marker.PrimaryTagId == tag.Id || marker.SceneMarkerTags.Any(sceneMarkerTag => sceneMarkerTag.TagId == tag.Id)) > markerCountVal2),
            CriterionModifier.IsNull => query.Where(tag =>
                _db.SceneMarkers.Count(marker => marker.PrimaryTagId == tag.Id || marker.SceneMarkerTags.Any(sceneMarkerTag => sceneMarkerTag.TagId == tag.Id)) == 0),
            CriterionModifier.NotNull => query.Where(tag =>
                _db.SceneMarkers.Count(marker => marker.PrimaryTagId == tag.Id || marker.SceneMarkerTags.Any(sceneMarkerTag => sceneMarkerTag.TagId == tag.Id)) > 0),
            _ => query,
        };
    }

    private static bool MatchesIntCriterion(IntCriterion criterion, int value)
    {
        var upperBound = criterion.Value2 ?? criterion.Value;
        return criterion.Modifier switch
        {
            CriterionModifier.Equals => value == criterion.Value,
            CriterionModifier.NotEquals => value != criterion.Value,
            CriterionModifier.GreaterThan => value > criterion.Value,
            CriterionModifier.LessThan => value < criterion.Value,
            CriterionModifier.Between => value >= criterion.Value && value <= upperBound,
            CriterionModifier.NotBetween => value < criterion.Value || value > upperBound,
            CriterionModifier.IsNull => value == 0,
            CriterionModifier.NotNull => value > 0,
            _ => true,
        };
    }
}

public class StudioRepository : IStudioRepository
{
    private readonly CoveContext _db;
    public StudioRepository(CoveContext db) => _db = db;

    private static IQueryable<Studio> ApplyStudioRatingSort(IQueryable<Studio> query, bool desc)
    {
        var sortQuery = query.Select(studio => new
        {
            Studio = studio,
            studio.Rating,
        });

        return desc
            ? sortQuery.OrderBy(item => item.Rating == null || item.Rating <= 0 ? 1 : 0).ThenByDescending(item => item.Rating).Select(item => item.Studio)
            : sortQuery.OrderBy(item => item.Rating == null || item.Rating <= 0 ? 0 : 1).ThenBy(item => item.Rating).Select(item => item.Studio);
    }

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

            // String criteria
            query = FilterHelpers.ApplyString(query, filter.NameCriterion, s => s.Name);
            query = FilterHelpers.ApplyString(query, filter.DetailsCriterion, s => s.Details);

            // Aliases criterion
            if (filter.AliasesCriterion != null)
            {
                var aliasVal = filter.AliasesCriterion.Value;
                query = filter.AliasesCriterion.Modifier switch
                {
                    CriterionModifier.Includes => query.Where(s => s.Aliases.Any(a => EF.Functions.ILike(a.Alias, $"%{aliasVal}%"))),
                    CriterionModifier.Excludes => query.Where(s => !s.Aliases.Any(a => EF.Functions.ILike(a.Alias, $"%{aliasVal}%"))),
                    CriterionModifier.IsNull => query.Where(s => s.Aliases.Count == 0),
                    CriterionModifier.NotNull => query.Where(s => s.Aliases.Count > 0),
                    _ => query.Where(s => s.Aliases.Any(a => EF.Functions.ILike(a.Alias, $"%{aliasVal}%"))),
                };
            }

            // Parents (multi-ID on parent studios)
            if (filter.ParentsCriterion != null)
            {
                var pIds = filter.ParentsCriterion.Value;
                query = filter.ParentsCriterion.Modifier switch
                {
                    CriterionModifier.Includes => query.Where(s => s.ParentId.HasValue && pIds.Contains(s.ParentId.Value)),
                    CriterionModifier.Excludes => query.Where(s => !s.ParentId.HasValue || !pIds.Contains(s.ParentId.Value)),
                    _ => query.Where(s => s.ParentId.HasValue && pIds.Contains(s.ParentId.Value)),
                };
            }

            // Count criteria
            query = FilterHelpers.ApplyInt(query, filter.ChildCountCriterion, s => s.Children.Count);
            query = FilterHelpers.ApplyInt(query, filter.TagCountCriterion, s => s.StudioTags.Count);
            query = FilterHelpers.ApplyInt(query, filter.GroupCountCriterion, s => s.Groups.Count);

            // Bool criteria
            if (filter.IgnoreAutoTagCriterion != null)
                query = query.Where(s => s.IgnoreAutoTag == filter.IgnoreAutoTagCriterion.Value);
            if (filter.OrganizedCriterion != null)
                query = query.Where(s => s.Organized == filter.OrganizedCriterion.Value);
        }
        if (findFilter != null && !string.IsNullOrEmpty(findFilter.Q))
            query = query.Where(s => EF.Functions.ILike(s.Name, $"%{findFilter.Q}%"));

        if (filter == null || !filter.ParentId.HasValue)
            query = query.Where(s => s.ParentId == null);

        var totalCount = await query.CountAsync(ct);
        var sort = findFilter?.Sort ?? "name";
        var desc = findFilter?.Direction == Core.Enums.SortDirection.Desc;
        query = sort switch
        {
            "name" => desc ? query.OrderByDescending(s => s.Name) : query.OrderBy(s => s.Name),
            "scene_count" => desc ? query.OrderByDescending(s => s.Scenes.Count) : query.OrderBy(s => s.Scenes.Count),
            "gallery_count" => desc ? query.OrderByDescending(s => s.Galleries.Count) : query.OrderBy(s => s.Galleries.Count),
            "image_count" => desc ? query.OrderByDescending(s => s.Images.Count) : query.OrderBy(s => s.Images.Count),
            "rating" => ApplyStudioRatingSort(query, desc),
            "child_count" => desc ? query.OrderByDescending(s => s.Children.Count) : query.OrderBy(s => s.Children.Count),
            "tag_count" => desc ? query.OrderByDescending(s => s.StudioTags.Count) : query.OrderBy(s => s.StudioTags.Count),
            "created_at" => desc ? query.OrderByDescending(s => s.CreatedAt) : query.OrderBy(s => s.CreatedAt),
            "updated_at" => desc ? query.OrderByDescending(s => s.UpdatedAt) : query.OrderBy(s => s.UpdatedAt),
            "random" => SeededRandomOrdering.OrderBy(query, findFilter?.Seed, s => s.Id),
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
            .Include(g => g.ImageGalleries)
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
            if (filter.ImageId.HasValue) query = query.Where(g => g.ImageGalleries.Any(ig => ig.ImageId == filter.ImageId.Value));
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

            query = FilterHelpers.ApplyStudioCriterion(query, filter.StudiosCriterion, g => g.StudioId);

            query = ApplyGalleryPathCriterion(query, filter.PathCriterion);
            query = ApplyGalleryFingerprintCriterion(query, filter.FingerprintCriterion);
            query = ApplyGalleryFingerprintCriterion(query, filter.ChecksumCriterion, "md5");
            query = ApplyGalleryPerformerAgeCriterion(query, filter.PerformerAgeCriterion);
            query = ApplyTypicalResolutionCriterion(query, filter.TypicalResolutionCriterion);

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

            // String criteria
            query = FilterHelpers.ApplyString(query, filter.TitleCriterion, g => g.Title);
            query = FilterHelpers.ApplyString(query, filter.CodeCriterion, g => g.Code);
            query = FilterHelpers.ApplyString(query, filter.DetailsCriterion, g => g.Details);
            query = FilterHelpers.ApplyString(query, filter.PhotographerCriterion, g => g.Photographer);

            // Count criteria
            query = FilterHelpers.ApplyInt(query, filter.FileCountCriterion, g => g.Files.Count);
            query = FilterHelpers.ApplyInt(query, filter.TagCountCriterion, g => g.GalleryTags.Count);
            query = FilterHelpers.ApplyInt(query, filter.PerformerCountCriterion, g => g.GalleryPerformers.Count);

            // Scenes criterion
            query = FilterHelpers.ApplyMultiId(query, filter.ScenesCriterion, g => g.SceneGalleries.Select(sg => sg.SceneId));

            // Performer tags criterion
            if (filter.PerformerTagsCriterion != null)
            {
                var ptIds = filter.PerformerTagsCriterion.Value;
                query = filter.PerformerTagsCriterion.Modifier switch
                {
                    CriterionModifier.Includes => query.Where(g => g.GalleryPerformers.Any(gp => gp.Performer!.PerformerTags.Any(pt => ptIds.Contains(pt.TagId)))),
                    CriterionModifier.Excludes => query.Where(g => !g.GalleryPerformers.Any(gp => gp.Performer!.PerformerTags.Any(pt => ptIds.Contains(pt.TagId)))),
                    CriterionModifier.IncludesAll => query.Where(g => ptIds.All(tid => g.GalleryPerformers.Any(gp => gp.Performer!.PerformerTags.Any(pt => pt.TagId == tid)))),
                    _ => query.Where(g => g.GalleryPerformers.Any(gp => gp.Performer!.PerformerTags.Any(pt => ptIds.Contains(pt.TagId)))),
                };
            }
        }
        if (findFilter != null && !string.IsNullOrEmpty(findFilter.Q))
            query = query.Where(g => (g.Title != null && EF.Functions.ILike(g.Title, $"%{findFilter.Q}%")));

        var totalCount = await query.CountAsync(ct);
        var sort = findFilter?.Sort ?? "updated_at";
        var desc = findFilter?.Direction == Core.Enums.SortDirection.Desc;
        query = sort switch
        {
            "updated_at" => desc ? query.OrderByDescending(g => g.UpdatedAt) : query.OrderBy(g => g.UpdatedAt),
            "date" => desc ? query.OrderByDescending(g => g.Date ?? DateOnly.MinValue) : query.OrderBy(g => g.Date ?? DateOnly.MinValue),
            "file_mod_time" => ApplyGalleryFileModTimeSort(query, desc),
            "path" => ApplyGalleryPathSort(query, desc),
            "title" => desc ? query.OrderByDescending(g => g.Title) : query.OrderBy(g => g.Title),
            "image_count" => desc ? query.OrderByDescending(g => g.ImageGalleries.Count) : query.OrderBy(g => g.ImageGalleries.Count),
            "rating" => ApplyGalleryRatingSort(query, desc),
            "performer_count" => desc ? query.OrderByDescending(g => g.GalleryPerformers.Count) : query.OrderBy(g => g.GalleryPerformers.Count),
            "tag_count" => desc ? query.OrderByDescending(g => g.GalleryTags.Count) : query.OrderBy(g => g.GalleryTags.Count),
            "zip_file_count" => desc
                ? query.OrderByDescending(g => g.Files.Count(file => file.Basename.EndsWith(".zip")))
                : query.OrderBy(g => g.Files.Count(file => file.Basename.EndsWith(".zip"))),
            "created_at" => desc ? query.OrderByDescending(g => g.CreatedAt) : query.OrderBy(g => g.CreatedAt),
            "random" => SeededRandomOrdering.OrderBy(query, findFilter?.Seed, g => g.Id),
            _ => desc ? query.OrderByDescending(g => g.UpdatedAt) : query.OrderBy(g => g.UpdatedAt),
        };
        var page = findFilter?.Page ?? 1;
        var perPage = findFilter?.PerPage ?? 25;
        var items = await query.Skip((page - 1) * perPage).Take(perPage).AsNoTracking().ToListAsync(ct);
        return (items, totalCount);
    }

    private static IQueryable<Gallery> ApplyGalleryFileModTimeSort(IQueryable<Gallery> query, bool desc)
    {
        var sortQuery = query.Select(gallery => new
        {
            Gallery = gallery,
            FileModTime = gallery.Files.Select(file => (DateTime?)file.ModTime).Max()
                ?? (gallery.Folder != null ? (DateTime?)gallery.Folder.ModTime : null),
        });

        return desc
            ? sortQuery.OrderBy(item => item.FileModTime == null ? 1 : 0).ThenByDescending(item => item.FileModTime).Select(item => item.Gallery)
            : sortQuery.OrderBy(item => item.FileModTime == null ? 1 : 0).ThenBy(item => item.FileModTime).Select(item => item.Gallery);
    }

    private static IQueryable<Gallery> ApplyGalleryPathSort(IQueryable<Gallery> query, bool desc)
    {
        if (desc)
        {
            var descendingQuery = query.Select(gallery => new
            {
                Gallery = gallery,
                Path = gallery.Folder != null
                    ? gallery.Folder.Path.Replace("\\", "/")
                    : gallery.Files
                        .Select(file => file.ParentFolder != null ? file.ParentFolder.Path.Replace("\\", "/") + "/" + file.Basename : file.Basename)
                        .OrderByDescending(path => path)
                        .FirstOrDefault(),
            });

            return descendingQuery
                .OrderBy(item => item.Path == null ? 1 : 0)
                .ThenByDescending(item => item.Path)
                .Select(item => item.Gallery);
        }

        var ascendingQuery = query.Select(gallery => new
        {
            Gallery = gallery,
            Path = gallery.Folder != null
                ? gallery.Folder.Path.Replace("\\", "/")
                : gallery.Files
                    .Select(file => file.ParentFolder != null ? file.ParentFolder.Path.Replace("\\", "/") + "/" + file.Basename : file.Basename)
                    .OrderBy(path => path)
                    .FirstOrDefault(),
        });

        return ascendingQuery
            .OrderBy(item => item.Path == null ? 1 : 0)
            .ThenBy(item => item.Path)
            .Select(item => item.Gallery);
    }

    private static IQueryable<Gallery> ApplyGalleryRatingSort(IQueryable<Gallery> query, bool desc)
    {
        var sortQuery = query.Select(gallery => new
        {
            Gallery = gallery,
            gallery.Rating,
        });

        return desc
            ? sortQuery.OrderBy(item => item.Rating == null || item.Rating <= 0 ? 1 : 0).ThenByDescending(item => item.Rating).Select(item => item.Gallery)
            : sortQuery.OrderBy(item => item.Rating == null || item.Rating <= 0 ? 0 : 1).ThenBy(item => item.Rating).Select(item => item.Gallery);
    }

    private static IQueryable<Gallery> ApplyGalleryPathCriterion(IQueryable<Gallery> query, StringCriterion? criterion)
    {
        if (criterion == null) return query;

        var value = criterion.Value.Replace("\\", "/");
        var normalizedValue = value.ToLowerInvariant();

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(gallery =>
                (gallery.Folder != null && gallery.Folder.Path.Replace("\\", "/") == value)
                || gallery.Files.Any(file => (file.ParentFolder != null ? file.ParentFolder.Path.Replace("\\", "/") + "/" + file.Basename : file.Basename) == value)),
            CriterionModifier.NotEquals => query.Where(gallery =>
                (gallery.Folder == null || gallery.Folder.Path.Replace("\\", "/") != value)
                && !gallery.Files.Any(file => (file.ParentFolder != null ? file.ParentFolder.Path.Replace("\\", "/") + "/" + file.Basename : file.Basename) == value)),
            CriterionModifier.Includes => query.Where(gallery =>
                (gallery.Folder != null && gallery.Folder.Path.Replace("\\", "/").ToLower().Contains(normalizedValue))
                || gallery.Files.Any(file => (file.ParentFolder != null ? file.ParentFolder.Path.Replace("\\", "/") + "/" + file.Basename : file.Basename).ToLower().Contains(normalizedValue))),
            CriterionModifier.Excludes => query.Where(gallery =>
                (gallery.Folder == null || !gallery.Folder.Path.Replace("\\", "/").ToLower().Contains(normalizedValue))
                && !gallery.Files.Any(file => (file.ParentFolder != null ? file.ParentFolder.Path.Replace("\\", "/") + "/" + file.Basename : file.Basename).ToLower().Contains(normalizedValue))),
            CriterionModifier.MatchesRegex => query.Where(gallery =>
                (gallery.Folder != null && Regex.IsMatch(gallery.Folder.Path.Replace("\\", "/"), value, RegexOptions.IgnoreCase))
                || gallery.Files.Any(file => Regex.IsMatch(file.ParentFolder != null ? file.ParentFolder.Path.Replace("\\", "/") + "/" + file.Basename : file.Basename, value, RegexOptions.IgnoreCase))),
            CriterionModifier.NotMatchesRegex => query.Where(gallery =>
                (gallery.Folder == null || !Regex.IsMatch(gallery.Folder.Path.Replace("\\", "/"), value, RegexOptions.IgnoreCase))
                && !gallery.Files.Any(file => Regex.IsMatch(file.ParentFolder != null ? file.ParentFolder.Path.Replace("\\", "/") + "/" + file.Basename : file.Basename, value, RegexOptions.IgnoreCase))),
            CriterionModifier.IsNull => query.Where(gallery =>
                (gallery.Folder == null || gallery.Folder.Path == "")
                && !gallery.Files.Any(file => (file.ParentFolder != null ? file.ParentFolder.Path.Replace("\\", "/") + "/" + file.Basename : file.Basename) != "")),
            CriterionModifier.NotNull => query.Where(gallery =>
                (gallery.Folder != null && gallery.Folder.Path != "")
                || gallery.Files.Any(file => (file.ParentFolder != null ? file.ParentFolder.Path.Replace("\\", "/") + "/" + file.Basename : file.Basename) != "")),
            _ => query,
        };
    }

    private static IQueryable<Gallery> ApplyGalleryFingerprintCriterion(IQueryable<Gallery> query, StringCriterion? criterion, string fingerprintType)
    {
        if (criterion == null) return query;

        var value = criterion.Value;
        var pattern = $"%{value}%";

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(gallery => gallery.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && fingerprint.Value == value))),
            CriterionModifier.NotEquals => query.Where(gallery => !gallery.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && fingerprint.Value == value))),
            CriterionModifier.Includes => query.Where(gallery => gallery.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && EF.Functions.ILike(fingerprint.Value, pattern)))),
            CriterionModifier.Excludes => query.Where(gallery => !gallery.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && EF.Functions.ILike(fingerprint.Value, pattern)))),
            CriterionModifier.MatchesRegex => query.Where(gallery => gallery.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && Regex.IsMatch(fingerprint.Value, value, RegexOptions.IgnoreCase)))),
            CriterionModifier.NotMatchesRegex => query.Where(gallery => !gallery.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && Regex.IsMatch(fingerprint.Value, value, RegexOptions.IgnoreCase)))),
            CriterionModifier.IsNull => query.Where(gallery => !gallery.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && fingerprint.Value != ""))),
            CriterionModifier.NotNull => query.Where(gallery => gallery.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && fingerprint.Value != ""))),
            _ => query,
        };
    }

    private static IQueryable<Gallery> ApplyGalleryFingerprintCriterion(IQueryable<Gallery> query, FingerprintCriterion? criterion)
    {
        if (criterion == null || string.IsNullOrWhiteSpace(criterion.Type)) return query;

        return ApplyGalleryFingerprintCriterion(
            query,
            new StringCriterion
            {
                Value = criterion.Value,
                Modifier = criterion.Modifier,
            },
            criterion.Type);
    }

    private static IQueryable<Gallery> ApplyGalleryPerformerAgeCriterion(IQueryable<Gallery> query, IntCriterion? criterion)
    {
        if (criterion == null) return query;

        var value = criterion.Value;
        var value2 = criterion.Value2 ?? value;

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(g => g.Date != null && g.GalleryPerformers.Any(gp =>
                gp.Performer!.Birthdate != null &&
                (g.Date.Value.Year - gp.Performer.Birthdate.Value.Year
                    - ((g.Date.Value.Month < gp.Performer.Birthdate.Value.Month
                        || (g.Date.Value.Month == gp.Performer.Birthdate.Value.Month && g.Date.Value.Day < gp.Performer.Birthdate.Value.Day)) ? 1 : 0)) == value)),
            CriterionModifier.NotEquals => query.Where(g => g.Date != null && g.GalleryPerformers.Any(gp =>
                gp.Performer!.Birthdate != null &&
                (g.Date.Value.Year - gp.Performer.Birthdate.Value.Year
                    - ((g.Date.Value.Month < gp.Performer.Birthdate.Value.Month
                        || (g.Date.Value.Month == gp.Performer.Birthdate.Value.Month && g.Date.Value.Day < gp.Performer.Birthdate.Value.Day)) ? 1 : 0)) != value)),
            CriterionModifier.GreaterThan => query.Where(g => g.Date != null && g.GalleryPerformers.Any(gp =>
                gp.Performer!.Birthdate != null &&
                (g.Date.Value.Year - gp.Performer.Birthdate.Value.Year
                    - ((g.Date.Value.Month < gp.Performer.Birthdate.Value.Month
                        || (g.Date.Value.Month == gp.Performer.Birthdate.Value.Month && g.Date.Value.Day < gp.Performer.Birthdate.Value.Day)) ? 1 : 0)) > value)),
            CriterionModifier.LessThan => query.Where(g => g.Date != null && g.GalleryPerformers.Any(gp =>
                gp.Performer!.Birthdate != null &&
                (g.Date.Value.Year - gp.Performer.Birthdate.Value.Year
                    - ((g.Date.Value.Month < gp.Performer.Birthdate.Value.Month
                        || (g.Date.Value.Month == gp.Performer.Birthdate.Value.Month && g.Date.Value.Day < gp.Performer.Birthdate.Value.Day)) ? 1 : 0)) < value)),
            CriterionModifier.Between => query.Where(g => g.Date != null && g.GalleryPerformers.Any(gp =>
                gp.Performer!.Birthdate != null &&
                (g.Date.Value.Year - gp.Performer.Birthdate.Value.Year
                    - ((g.Date.Value.Month < gp.Performer.Birthdate.Value.Month
                        || (g.Date.Value.Month == gp.Performer.Birthdate.Value.Month && g.Date.Value.Day < gp.Performer.Birthdate.Value.Day)) ? 1 : 0)) >= value &&
                (g.Date.Value.Year - gp.Performer.Birthdate.Value.Year
                    - ((g.Date.Value.Month < gp.Performer.Birthdate.Value.Month
                        || (g.Date.Value.Month == gp.Performer.Birthdate.Value.Month && g.Date.Value.Day < gp.Performer.Birthdate.Value.Day)) ? 1 : 0)) <= value2)),
            CriterionModifier.NotBetween => query.Where(g => g.Date != null && g.GalleryPerformers.Any(gp =>
                gp.Performer!.Birthdate != null &&
                ((g.Date.Value.Year - gp.Performer.Birthdate.Value.Year
                    - ((g.Date.Value.Month < gp.Performer.Birthdate.Value.Month
                        || (g.Date.Value.Month == gp.Performer.Birthdate.Value.Month && g.Date.Value.Day < gp.Performer.Birthdate.Value.Day)) ? 1 : 0)) < value ||
                 (g.Date.Value.Year - gp.Performer.Birthdate.Value.Year
                    - ((g.Date.Value.Month < gp.Performer.Birthdate.Value.Month
                        || (g.Date.Value.Month == gp.Performer.Birthdate.Value.Month && g.Date.Value.Day < gp.Performer.Birthdate.Value.Day)) ? 1 : 0)) > value2))),
            _ => query,
        };
    }

    private static IQueryable<Gallery> ApplyTypicalResolutionCriterion(IQueryable<Gallery> query, IntCriterion? criterion)
    {
        if (criterion == null) return query;

        return FilterHelpers.ApplyResolution(query, criterion, gallery => gallery.ImageGalleries
            .SelectMany(imageGallery => imageGallery.Image!.Files.Select(file =>
                Math.Max(file.Width, file.Height) >= 9840 ? 9999 :
                Math.Max(file.Width, file.Height) >= 7424 ? 4320 :
                Math.Max(file.Width, file.Height) >= 6656 ? 4032 :
                Math.Max(file.Width, file.Height) >= 5632 ? 3384 :
                Math.Max(file.Width, file.Height) >= 4480 ? 2880 :
                Math.Max(file.Width, file.Height) >= 3200 ? 2160 :
                Math.Max(file.Width, file.Height) >= 2240 ? 1440 :
                Math.Max(file.Width, file.Height) >= 1600 ? 1080 :
                Math.Max(file.Width, file.Height) >= 1120 ? 720 :
                Math.Max(file.Width, file.Height) >= 907 ? 540 :
                Math.Max(file.Width, file.Height) >= 747 ? 480 :
                Math.Max(file.Width, file.Height) >= 533 ? 360 :
                Math.Max(file.Width, file.Height) >= 341 ? 240 :
                Math.Max(file.Width, file.Height) >= 144 ? 144 : 0))
            .Where(bucket => bucket > 0)
            .GroupBy(bucket => bucket)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Key)
            .Select(group => group.Key)
            .FirstOrDefault());
    }
}

public class ImageRepository : IImageRepository
{
    private readonly CoveContext _db;
    public ImageRepository(CoveContext db) => _db = db;

    private static readonly System.Linq.Expressions.Expression<Func<Image, string?>> DisplayTitleSelector = image =>
        image.Title != null && image.Title != ""
            ? image.Title
            : image.Files
                .OrderBy(file => file.Basename)
                .Select(file => file.Basename)
                .FirstOrDefault();

    public async Task<Image?> GetByIdAsync(int id, CancellationToken ct = default) => await _db.Images.FindAsync([id], ct);

    public async Task<Image?> GetByIdWithRelationsAsync(int id, CancellationToken ct = default)
        => await _db.Images
            .Include(i => i.Studio).Include(i => i.Urls)
            .Include(i => i.ImageTags).ThenInclude(it => it.Tag)
            .Include(i => i.ImagePerformers).ThenInclude(ip => ip.Performer)
            .Include(i => i.ImageGalleries).ThenInclude(ig => ig.Gallery)
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
        {
            var searchTerm = findFilter.Q.ToLowerInvariant();
            filterQuery = filterQuery.Where(i =>
                (i.Title != null && i.Title.ToLower().Contains(searchTerm)) ||
                (i.Details != null && i.Details.ToLower().Contains(searchTerm)) ||
                (i.Code != null && i.Code.ToLower().Contains(searchTerm)) ||
                (i.Photographer != null && i.Photographer.ToLower().Contains(searchTerm)) ||
                i.Files.Any(f =>
                    f.Basename.ToLower().Contains(searchTerm) ||
                    (f.ParentFolder != null ? f.ParentFolder.Path.Replace("\\", "/") + "/" + f.Basename : f.Basename).ToLower().Contains(searchTerm)));
        }

        var perPage = findFilter?.PerPage ?? 25;

        if (perPage <= 0)
        {
            var count = await filterQuery.CountAsync(ct);
            return (Array.Empty<Image>(), count);
        }

        var totalCount = await filterQuery.AsNoTracking().CountAsync(ct);

        // Sort and paginate on the lightweight query, then fetch only the IDs
        var sort = findFilter?.Sort ?? "updated_at";
        var desc = findFilter?.Direction == Core.Enums.SortDirection.Desc;
        filterQuery = ApplySorting(filterQuery, sort, desc, findFilter?.Seed);

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
            .Include(i => i.Studio)
            .Include(i => i.ImageTags).ThenInclude(it => it.Tag)
            .Include(i => i.ImagePerformers).ThenInclude(ip => ip.Performer)
            .Include(i => i.ImageGalleries).ThenInclude(ig => ig.Gallery)
            .Include(i => i.Files).ThenInclude(f => f.ParentFolder)
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
        if (!string.IsNullOrEmpty(filter.Title))
            query = FilterHelpers.ApplyString(query, new StringCriterion { Value = filter.Title, Modifier = CriterionModifier.Includes }, DisplayTitleSelector);
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
            query = FilterHelpers.ApplyResolution(query, filter.ResolutionCriterion, i => i.Files.Select(f => Math.Max(f.Width, f.Height)).Max());

        // Multi-ID criteria
        if (filter.TagsCriterion != null)
            query = FilterHelpers.ApplyMultiId(query, filter.TagsCriterion, i => i.ImageTags.Select(it => it.TagId), hierarchicalTagGroups);
        if (filter.PerformersCriterion != null)
            query = FilterHelpers.ApplyMultiId(query, filter.PerformersCriterion, i => i.ImagePerformers.Select(ip => ip.PerformerId));

        query = FilterHelpers.ApplyStudioCriterion(query, filter.StudiosCriterion, i => i.StudioId);

        if (filter.GalleriesCriterion != null)
            query = FilterHelpers.ApplyMultiId(query, filter.GalleriesCriterion, i => i.ImageGalleries.Select(ig => ig.GalleryId));

        query = FilterHelpers.ApplyFilePath(query, filter.PathCriterion, i => i.Files);

        query = ApplyFingerprintCriterion(query, filter.FingerprintCriterion);
        query = ApplyFingerprintCriterion(query, filter.ChecksumCriterion, "md5");

        if (filter.PerformerFavoriteCriterion != null)
            query = filter.PerformerFavoriteCriterion.Value
                ? query.Where(i => i.ImagePerformers.Any(ip => ip.Performer!.Favorite))
                : query.Where(i => !i.ImagePerformers.Any(ip => ip.Performer!.Favorite));

        query = FilterHelpers.ApplyTimestamp(query, filter.CreatedAtCriterion, i => i.CreatedAt);
        query = FilterHelpers.ApplyTimestamp(query, filter.UpdatedAtCriterion, i => i.UpdatedAt);

        // String criteria
        query = FilterHelpers.ApplyString(query, filter.TitleCriterion, DisplayTitleSelector);
        query = FilterHelpers.ApplyString(query, filter.CodeCriterion, i => i.Code);
        query = FilterHelpers.ApplyString(query, filter.DetailsCriterion, i => i.Details);
        query = FilterHelpers.ApplyString(query, filter.PhotographerCriterion, i => i.Photographer);

        // URL criterion
        if (filter.UrlCriterion != null)
        {
            var urlVal = filter.UrlCriterion.Value;
            query = filter.UrlCriterion.Modifier switch
            {
                CriterionModifier.Includes => query.Where(i => i.Urls.Any(u => EF.Functions.ILike(u.Url, $"%{urlVal}%"))),
                CriterionModifier.Excludes => query.Where(i => !i.Urls.Any(u => EF.Functions.ILike(u.Url, $"%{urlVal}%"))),
                CriterionModifier.IsNull => query.Where(i => i.Urls.Count == 0),
                CriterionModifier.NotNull => query.Where(i => i.Urls.Count > 0),
                _ => query.Where(i => i.Urls.Any(u => EF.Functions.ILike(u.Url, $"%{urlVal}%"))),
            };
        }

        // Date criterion
        query = FilterHelpers.ApplyDate(query, filter.DateCriterion, i => i.Date);

        // Count criteria
        query = FilterHelpers.ApplyInt(query, filter.FileCountCriterion, i => i.Files.Count);
        query = FilterHelpers.ApplyInt(query, filter.TagCountCriterion, i => i.ImageTags.Count);
        query = FilterHelpers.ApplyInt(query, filter.PerformerCountCriterion, i => i.ImagePerformers.Count);

        // Performer tags criterion
        if (filter.PerformerTagsCriterion != null)
        {
            var ptIds = filter.PerformerTagsCriterion.Value;
            query = filter.PerformerTagsCriterion.Modifier switch
            {
                CriterionModifier.Includes => query.Where(i => i.ImagePerformers.Any(ip => ip.Performer!.PerformerTags.Any(pt => ptIds.Contains(pt.TagId)))),
                CriterionModifier.Excludes => query.Where(i => !i.ImagePerformers.Any(ip => ip.Performer!.PerformerTags.Any(pt => ptIds.Contains(pt.TagId)))),
                CriterionModifier.IncludesAll => query.Where(i => ptIds.All(tid => i.ImagePerformers.Any(ip => ip.Performer!.PerformerTags.Any(pt => pt.TagId == tid)))),
                _ => query.Where(i => i.ImagePerformers.Any(ip => ip.Performer!.PerformerTags.Any(pt => ptIds.Contains(pt.TagId)))),
            };
        }

        query = ApplyPerformerAgeCriterion(query, filter.PerformerAgeCriterion);

        if (filter.OrientationCriterion != null)
            query = ApplyOrientationCriterion(query, filter.OrientationCriterion);

        return query;
    }

    private static IQueryable<Image> ApplySorting(IQueryable<Image> query, string sort, bool desc, int? seed = null)
    {
        if (sort == "random")
            return SeededRandomOrdering.OrderBy(query, seed, image => image.Id);

        return ApplySortingSwitch(query, sort, desc);
    }

    private static IQueryable<Image> ApplySortingSwitch(IQueryable<Image> query, string sort, bool desc) => sort switch
    {
        "title" => ApplyDisplayTitleSort(query, desc),
        "date" => desc ? query.OrderByDescending(i => i.Date ?? DateOnly.MinValue) : query.OrderBy(i => i.Date ?? DateOnly.MinValue),
        "rating" => desc
            ? query.OrderBy(i => i.Rating == null || i.Rating <= 0 ? 1 : 0).ThenByDescending(i => i.Rating)
            : query.OrderBy(i => i.Rating == null || i.Rating <= 0 ? 0 : 1).ThenBy(i => i.Rating),
        "o_counter" => desc ? query.OrderByDescending(i => i.OCounter) : query.OrderBy(i => i.OCounter),
        "random" => query.OrderBy(i => i.Id),
        "file_mod_time" => ApplyFileModTimeSort(query, desc),
        "file_size" => desc
            ? query.OrderByDescending(i => i.Files.Select(file => (long?)file.Size).Max() ?? 0)
            : query.OrderBy(i => i.Files.Select(file => (long?)file.Size).Max() ?? 0),
        "path" => ApplyPathSort(query, desc),
        "tag_count" => desc
            ? query.OrderByDescending(i => i.ImageTags.Count)
            : query.OrderBy(i => i.ImageTags.Count),
        "performer_count" => desc
            ? query.OrderByDescending(i => i.ImagePerformers.Count)
            : query.OrderBy(i => i.ImagePerformers.Count),
        "created_at" => desc ? query.OrderByDescending(i => i.CreatedAt) : query.OrderBy(i => i.CreatedAt),
        _ => desc ? query.OrderByDescending(i => i.UpdatedAt) : query.OrderBy(i => i.UpdatedAt),
    };

    private static IQueryable<Image> ApplyDisplayTitleSort(IQueryable<Image> query, bool desc)
    {
        if (desc)
        {
            var descendingQuery = query.Select(image => new
            {
                Image = image,
                DisplayTitle = image.Title != null && image.Title != ""
                    ? image.Title
                    : image.Files
                        .OrderByDescending(file => file.Basename)
                        .Select(file => file.Basename)
                        .FirstOrDefault(),
            });

            return descendingQuery
                .OrderBy(item => item.DisplayTitle == null ? 1 : 0)
                .ThenByDescending(item => item.DisplayTitle)
                .Select(item => item.Image);
        }

        var ascendingQuery = query.Select(image => new
        {
            Image = image,
            DisplayTitle = image.Title != null && image.Title != ""
                ? image.Title
                : image.Files
                    .OrderBy(file => file.Basename)
                    .Select(file => file.Basename)
                    .FirstOrDefault(),
        });

        return ascendingQuery
            .OrderBy(item => item.DisplayTitle == null ? 1 : 0)
            .ThenBy(item => item.DisplayTitle)
            .Select(item => item.Image);
    }

    private static IQueryable<Image> ApplyFileModTimeSort(IQueryable<Image> query, bool desc)
    {
        var sortQuery = query.Select(image => new
        {
            Image = image,
            FileModTime = image.Files.Select(file => (DateTime?)file.ModTime).Max(),
        });

        return desc
            ? sortQuery.OrderBy(item => item.FileModTime == null ? 1 : 0).ThenByDescending(item => item.FileModTime).Select(item => item.Image)
            : sortQuery.OrderBy(item => item.FileModTime == null ? 1 : 0).ThenBy(item => item.FileModTime).Select(item => item.Image);
    }

    private static IQueryable<Image> ApplyPathSort(IQueryable<Image> query, bool desc)
    {
        if (desc)
        {
            var descendingQuery = query.Select(image => new
            {
                Image = image,
                Path = image.Files
                    .Select(file => file.ParentFolder != null ? file.ParentFolder.Path.Replace("\\", "/") + "/" + file.Basename : file.Basename)
                    .OrderByDescending(path => path)
                    .FirstOrDefault(),
            });

            return descendingQuery
                .OrderBy(item => item.Path == null ? 1 : 0)
                .ThenByDescending(item => item.Path)
                .Select(item => item.Image);
        }

        var ascendingQuery = query.Select(image => new
        {
            Image = image,
            Path = image.Files
                .Select(file => file.ParentFolder != null ? file.ParentFolder.Path.Replace("\\", "/") + "/" + file.Basename : file.Basename)
                .OrderBy(path => path)
                .FirstOrDefault(),
        });

        return ascendingQuery
            .OrderBy(item => item.Path == null ? 1 : 0)
            .ThenBy(item => item.Path)
            .Select(item => item.Image);
    }

    private static IQueryable<Image> ApplyFingerprintCriterion(IQueryable<Image> query, StringCriterion? criterion, string fingerprintType)
    {
        if (criterion == null) return query;

        var value = criterion.Value;
        var pattern = $"%{value}%";

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(image => image.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && fingerprint.Value == value))),
            CriterionModifier.NotEquals => query.Where(image => !image.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && fingerprint.Value == value))),
            CriterionModifier.Includes => query.Where(image => image.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && EF.Functions.ILike(fingerprint.Value, pattern)))),
            CriterionModifier.Excludes => query.Where(image => !image.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && EF.Functions.ILike(fingerprint.Value, pattern)))),
            CriterionModifier.MatchesRegex => query.Where(image => image.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && Regex.IsMatch(fingerprint.Value, value, RegexOptions.IgnoreCase)))),
            CriterionModifier.NotMatchesRegex => query.Where(image => !image.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && Regex.IsMatch(fingerprint.Value, value, RegexOptions.IgnoreCase)))),
            CriterionModifier.IsNull => query.Where(image => !image.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && fingerprint.Value != ""))),
            CriterionModifier.NotNull => query.Where(image => image.Files.Any(file =>
                file.Fingerprints.Any(fingerprint => fingerprint.Type == fingerprintType && fingerprint.Value != ""))),
            _ => query,
        };
    }

    private static IQueryable<Image> ApplyFingerprintCriterion(IQueryable<Image> query, FingerprintCriterion? criterion)
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

    private static IQueryable<Image> ApplyPerformerAgeCriterion(IQueryable<Image> query, IntCriterion? criterion)
    {
        if (criterion == null) return query;

        var value = criterion.Value;
        var value2 = criterion.Value2 ?? value;

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(i => i.Date != null && i.ImagePerformers.Any(ip =>
                ip.Performer!.Birthdate != null &&
                (i.Date.Value.Year - ip.Performer.Birthdate.Value.Year
                    - ((i.Date.Value.Month < ip.Performer.Birthdate.Value.Month
                        || (i.Date.Value.Month == ip.Performer.Birthdate.Value.Month && i.Date.Value.Day < ip.Performer.Birthdate.Value.Day)) ? 1 : 0)) == value)),
            CriterionModifier.NotEquals => query.Where(i => i.Date != null && i.ImagePerformers.Any(ip =>
                ip.Performer!.Birthdate != null &&
                (i.Date.Value.Year - ip.Performer.Birthdate.Value.Year
                    - ((i.Date.Value.Month < ip.Performer.Birthdate.Value.Month
                        || (i.Date.Value.Month == ip.Performer.Birthdate.Value.Month && i.Date.Value.Day < ip.Performer.Birthdate.Value.Day)) ? 1 : 0)) != value)),
            CriterionModifier.GreaterThan => query.Where(i => i.Date != null && i.ImagePerformers.Any(ip =>
                ip.Performer!.Birthdate != null &&
                (i.Date.Value.Year - ip.Performer.Birthdate.Value.Year
                    - ((i.Date.Value.Month < ip.Performer.Birthdate.Value.Month
                        || (i.Date.Value.Month == ip.Performer.Birthdate.Value.Month && i.Date.Value.Day < ip.Performer.Birthdate.Value.Day)) ? 1 : 0)) > value)),
            CriterionModifier.LessThan => query.Where(i => i.Date != null && i.ImagePerformers.Any(ip =>
                ip.Performer!.Birthdate != null &&
                (i.Date.Value.Year - ip.Performer.Birthdate.Value.Year
                    - ((i.Date.Value.Month < ip.Performer.Birthdate.Value.Month
                        || (i.Date.Value.Month == ip.Performer.Birthdate.Value.Month && i.Date.Value.Day < ip.Performer.Birthdate.Value.Day)) ? 1 : 0)) < value)),
            CriterionModifier.Between => query.Where(i => i.Date != null && i.ImagePerformers.Any(ip =>
                ip.Performer!.Birthdate != null &&
                (i.Date.Value.Year - ip.Performer.Birthdate.Value.Year
                    - ((i.Date.Value.Month < ip.Performer.Birthdate.Value.Month
                        || (i.Date.Value.Month == ip.Performer.Birthdate.Value.Month && i.Date.Value.Day < ip.Performer.Birthdate.Value.Day)) ? 1 : 0)) >= value &&
                (i.Date.Value.Year - ip.Performer.Birthdate.Value.Year
                    - ((i.Date.Value.Month < ip.Performer.Birthdate.Value.Month
                        || (i.Date.Value.Month == ip.Performer.Birthdate.Value.Month && i.Date.Value.Day < ip.Performer.Birthdate.Value.Day)) ? 1 : 0)) <= value2)),
            _ => query,
        };
    }

    private static IQueryable<Image> ApplyOrientationCriterion(IQueryable<Image> query, StringCriterion criterion)
    {
        var orientation = criterion.Value.ToLowerInvariant();

        IQueryable<Image> matchingQuery = orientation switch
        {
            "landscape" => query.Where(i => i.Files.Any(f => f.Width > f.Height)),
            "portrait" => query.Where(i => i.Files.Any(f => f.Height > f.Width)),
            "square" => query.Where(i => i.Files.Any(f => f.Width == f.Height && f.Width > 0)),
            _ => query,
        };

        if (ReferenceEquals(matchingQuery, query))
            return query;

        return criterion.Modifier switch
        {
            CriterionModifier.NotEquals => query.Where(i => !matchingQuery.Select(item => item.Id).Contains(i.Id)),
            CriterionModifier.IsNull => query.Where(i => !i.Files.Any(f => f.Width > 0 && f.Height > 0)),
            CriterionModifier.NotNull => query.Where(i => i.Files.Any(f => f.Width > 0 && f.Height > 0)),
            _ => matchingQuery,
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

            query = FilterHelpers.ApplyStudioCriterion(query, filter.StudiosCriterion, g => g.StudioId);

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

            // String criteria
            query = FilterHelpers.ApplyString(query, filter.NameCriterion, g => g.Name);
            query = FilterHelpers.ApplyString(query, filter.DirectorCriterion, g => g.Director);
            query = FilterHelpers.ApplyString(query, filter.SynopsisCriterion, g => g.Synopsis);

            // Count criteria
            query = FilterHelpers.ApplyInt(query, filter.SceneCountCriterion, g => g.SceneGroups.Count);
            query = FilterHelpers.ApplyInt(query, filter.TagCountCriterion, g => g.GroupTags.Count);

            // Performers criterion (performers in scenes belonging to this group)
            if (filter.PerformersCriterion != null)
            {
                var pIds = filter.PerformersCriterion.Value;
                query = filter.PerformersCriterion.Modifier switch
                {
                    CriterionModifier.Includes => query.Where(g => g.SceneGroups.Any(sg => sg.Scene!.ScenePerformers.Any(sp => pIds.Contains(sp.PerformerId)))),
                    CriterionModifier.Excludes => query.Where(g => !g.SceneGroups.Any(sg => sg.Scene!.ScenePerformers.Any(sp => pIds.Contains(sp.PerformerId)))),
                    CriterionModifier.IncludesAll => query.Where(g => pIds.All(pid => g.SceneGroups.Any(sg => sg.Scene!.ScenePerformers.Any(sp => sp.PerformerId == pid)))),
                    _ => query.Where(g => g.SceneGroups.Any(sg => sg.Scene!.ScenePerformers.Any(sp => pIds.Contains(sp.PerformerId)))),
                };
            }
        }
        if (findFilter != null && !string.IsNullOrEmpty(findFilter.Q))
            query = query.Where(g => EF.Functions.ILike(g.Name, $"%{findFilter.Q}%"));

        var totalCount = await query.CountAsync(ct);
        var sort = findFilter?.Sort ?? "name";
        var desc = findFilter?.Direction == Core.Enums.SortDirection.Desc;
        query = sort switch
        {
            "name" => desc ? query.OrderByDescending(g => g.Name) : query.OrderBy(g => g.Name),
            "date" => desc ? query.OrderByDescending(g => g.Date ?? DateOnly.MinValue) : query.OrderBy(g => g.Date ?? DateOnly.MinValue),
            "rating" => desc
                ? query.OrderBy(item => item.Rating == null || item.Rating <= 0 ? 1 : 0).ThenByDescending(item => item.Rating)
                : query.OrderBy(item => item.Rating == null || item.Rating <= 0 ? 0 : 1).ThenBy(item => item.Rating),
            "created_at" => desc ? query.OrderByDescending(g => g.CreatedAt) : query.OrderBy(g => g.CreatedAt),
            "random" => SeededRandomOrdering.OrderBy(query, findFilter?.Seed, g => g.Id),
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
