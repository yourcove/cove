using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using Regex = System.Text.RegularExpressions.Regex;
using RegexOptions = System.Text.RegularExpressions.RegexOptions;
using PermissionKeys = Cove.Core.Auth.Permissions;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.Data.Repositories;

public class PerformerRepository : IPerformerRepository
{
    private readonly CoveContext _db;
    public PerformerRepository(CoveContext db) => _db = db;

    private IQueryable<Performer> ApplyPerformerSearch(IQueryable<Performer> query, string? search)
    {
        var textQuery = FullTextSearchHelpers.Apply(_db, query, search,
            p => p.Name,
            p => p.Disambiguation,
            p => p.Details,
            p => p.SearchText);

        var normalized = search?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return textQuery;
        var normalizedLower = normalized.ToLowerInvariant();

        var withAliases = textQuery
            .Concat(query.Where(p => p.Aliases.Any(alias => alias.Alias.ToLower().Contains(normalizedLower))));

        return FullTextSearchHelpers.ApplyRelationalMatches(withAliases, query, search,
            tagSelectors: [p => p.PerformerTags.Where(pt => pt.Tag != null).Select(pt => pt.Tag!)]);
    }

    private static IQueryable<Performer> ApplyCareerLengthCriterion(IQueryable<Performer> query, IntCriterion? criterion)
    {
        if (criterion == null)
            return query;

        var upperBound = criterion.Value2 ?? criterion.Value;
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var lengthQuery = query.Select(performer => new
        {
            Performer = performer,
            HasCareer = performer.CareerStart != null,
            CareerLength = performer.CareerStart == null
                ? 0
                : ((performer.CareerEnd ?? today).Year - performer.CareerStart.Value.Year)
                    - (((performer.CareerEnd ?? today).Month < performer.CareerStart.Value.Month)
                        || (((performer.CareerEnd ?? today).Month == performer.CareerStart.Value.Month)
                            && ((performer.CareerEnd ?? today).Day < performer.CareerStart.Value.Day))
                        ? 1
                        : 0),
        });

        var filtered = criterion.Modifier switch
        {
            CriterionModifier.Equals => lengthQuery.Where(item => item.HasCareer && item.CareerLength == criterion.Value),
            CriterionModifier.NotEquals => lengthQuery.Where(item => !item.HasCareer || item.CareerLength != criterion.Value),
            CriterionModifier.GreaterThan => lengthQuery.Where(item => item.HasCareer && item.CareerLength > criterion.Value),
            CriterionModifier.LessThan => lengthQuery.Where(item => item.HasCareer && item.CareerLength < criterion.Value),
            CriterionModifier.Between => lengthQuery.Where(item => item.HasCareer && item.CareerLength >= criterion.Value && item.CareerLength <= upperBound),
            CriterionModifier.NotBetween => lengthQuery.Where(item => !item.HasCareer || item.CareerLength < criterion.Value || item.CareerLength > upperBound),
            CriterionModifier.IsNull => lengthQuery.Where(item => !item.HasCareer),
            CriterionModifier.NotNull => lengthQuery.Where(item => item.HasCareer),
            _ => lengthQuery,
        };

        return filtered.Select(item => item.Performer);
    }

    private static IQueryable<Performer> ApplyCareerLengthSort(IQueryable<Performer> query, bool desc)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var sortQuery = query.Select(performer => new
        {
            Performer = performer,
            HasCareer = performer.CareerStart != null,
            CareerLength = performer.CareerStart == null
                ? 0
                : ((performer.CareerEnd ?? today).Year - performer.CareerStart.Value.Year)
                    - (((performer.CareerEnd ?? today).Month < performer.CareerStart.Value.Month)
                        || (((performer.CareerEnd ?? today).Month == performer.CareerStart.Value.Month)
                            && ((performer.CareerEnd ?? today).Day < performer.CareerStart.Value.Day))
                        ? 1
                        : 0),
        });

        return desc
            ? sortQuery.OrderBy(item => item.HasCareer ? 0 : 1).ThenByDescending(item => item.CareerLength).ThenByDescending(item => item.Performer.Id).Select(item => item.Performer)
            : sortQuery.OrderBy(item => item.HasCareer ? 0 : 1).ThenBy(item => item.CareerLength).ThenBy(item => item.Performer.Id).Select(item => item.Performer);
    }

    private static IQueryable<Performer> ApplyHeightSort(IQueryable<Performer> query, bool desc)
    {
        var sortQuery = query.Select(performer => new
        {
            Performer = performer,
            HasHeight = performer.HeightCm != null && performer.HeightCm > 0,
            Height = performer.HeightCm ?? 0,
        });

        return desc
            ? sortQuery.OrderBy(item => item.HasHeight ? 0 : 1).ThenByDescending(item => item.Height).ThenByDescending(item => item.Performer.Id).Select(item => item.Performer)
            : sortQuery.OrderBy(item => item.HasHeight ? 0 : 1).ThenBy(item => item.Height).ThenBy(item => item.Performer.Id).Select(item => item.Performer);
    }

    private IQueryable<Performer> ApplyLastPlayedAtSort(IQueryable<Performer> query, bool desc)
    {
        var userId = EngagementQueryHelpers.CurrentUserId(_db);
        if (userId is not int selectedUserId)
            return desc ? query.OrderByDescending(performer => performer.Id) : query.OrderBy(performer => performer.Id);

        var ordered = CompoundSortOrdering.Append(
            query,
            ordered: null,
            performer => _db.UserEntityAffinities
                .Where(affinity => affinity.UserId == selectedUserId
                    && affinity.HostType == AffinityHostType.Video
                    && performer.VideoPerformers.Any(videoPerformer => videoPerformer.VideoId == affinity.HostId))
                .Max(affinity => affinity.LastConsumedAt) ?? (desc ? DateTime.MinValue : DateTime.MaxValue),
            desc);

        return desc ? ordered.ThenByDescending(performer => performer.Id) : ordered.ThenBy(performer => performer.Id);
    }

    private IQueryable<Performer> ApplyPlayCountSort(IQueryable<Performer> query, bool desc)
        => ApplyVideoAffinityIntSumSort(query, nameof(UserEntityAffinity.ViewCount), desc);

    private IQueryable<Performer> ApplyVideoAffinityIntSumCriterion(IQueryable<Performer> query, IntCriterion? criterion, string propertyName)
    {
        if (criterion == null)
            return query;

        var userId = EngagementQueryHelpers.CurrentUserId(_db);
        if (userId is not int selectedUserId)
            return FilterHelpers.ApplyInt(query, criterion, _ => 0);

        return FilterHelpers.ApplyInt(query, criterion, performer => _db.UserEntityAffinities
            .Where(affinity => affinity.UserId == selectedUserId
                && affinity.HostType == AffinityHostType.Video
                && performer.VideoPerformers.Any(videoPerformer => videoPerformer.VideoId == affinity.HostId))
            .Sum(affinity => EF.Property<int>(affinity, propertyName)));
    }

    private IQueryable<Performer> ApplyVideoAffinityIntSumSort(IQueryable<Performer> query, string propertyName, bool desc)
    {
        var userId = EngagementQueryHelpers.CurrentUserId(_db);
        if (userId is not int selectedUserId)
            return desc ? query.OrderByDescending(performer => performer.Id) : query.OrderBy(performer => performer.Id);

        var ordered = CompoundSortOrdering.Append(
            query,
            ordered: null,
            performer => _db.UserEntityAffinities
                .Where(affinity => affinity.UserId == selectedUserId
                    && affinity.HostType == AffinityHostType.Video
                    && performer.VideoPerformers.Any(videoPerformer => videoPerformer.VideoId == affinity.HostId))
                .Sum(affinity => EF.Property<int>(affinity, propertyName)),
            desc);

        return desc ? ordered.ThenByDescending(performer => performer.Id) : ordered.ThenBy(performer => performer.Id);
    }

    private static IQueryable<Performer> ApplyMeasurementsSort(IQueryable<Performer> query, bool desc)
    {
        var measuredQuery = query.Select(performer => new
        {
            Performer = performer,
            HasMeasurements = performer.Measurements != null && performer.Measurements != string.Empty,
            NormalizedMeasurements = performer.Measurements == null ? string.Empty : performer.Measurements.Trim().ToUpper(),
            FirstHyphen = performer.Measurements == null ? -1 : performer.Measurements.IndexOf("-"),
        });

        var segmentedQuery = measuredQuery.Select(item => new
        {
            item.Performer,
            item.HasMeasurements,
            BustSegment = item.FirstHyphen > 0
                ? item.NormalizedMeasurements.Substring(0, item.FirstHyphen)
                : item.NormalizedMeasurements,
            RemainingSegments = item.FirstHyphen >= 0
                ? item.NormalizedMeasurements.Substring(item.FirstHyphen + 1)
                : string.Empty,
        });

        var sortQuery = segmentedQuery.Select(item => new
        {
            item.Performer,
            item.HasMeasurements,
            item.BustSegment,
            RemainingHyphen = item.RemainingSegments.IndexOf("-"),
            item.RemainingSegments,
        });

        var normalizedQuery = sortQuery.Select(item => new
        {
            item.Performer,
            item.HasMeasurements,
            item.BustSegment,
            WaistSegment = item.RemainingHyphen > 0
                ? item.RemainingSegments.Substring(0, item.RemainingHyphen)
                : item.RemainingSegments,
            HipsSegment = item.RemainingHyphen >= 0
                ? item.RemainingSegments.Substring(item.RemainingHyphen + 1)
                : string.Empty,
        });

        return desc
            ? normalizedQuery
                .OrderBy(item => item.HasMeasurements ? 0 : 1)
                .ThenByDescending(item => item.BustSegment.Length)
                .ThenByDescending(item => item.BustSegment)
                .ThenByDescending(item => item.WaistSegment.Length)
                .ThenByDescending(item => item.WaistSegment)
                .ThenByDescending(item => item.HipsSegment.Length)
                .ThenByDescending(item => item.HipsSegment)
                .ThenByDescending(item => item.Performer.Id)
                .Select(item => item.Performer)
            : normalizedQuery
                .OrderBy(item => item.HasMeasurements ? 0 : 1)
                .ThenBy(item => item.BustSegment.Length)
                .ThenBy(item => item.BustSegment)
                .ThenBy(item => item.WaistSegment.Length)
                .ThenBy(item => item.WaistSegment)
                .ThenBy(item => item.HipsSegment.Length)
                .ThenBy(item => item.HipsSegment)
                .ThenBy(item => item.Performer.Id)
                .Select(item => item.Performer);
    }

    public async Task<Performer?> GetByIdAsync(int id, CancellationToken ct = default)
        => await _db.Performers.FindAsync([id], ct);

    public async Task<Performer?> GetByIdWithRelationsAsync(int id, CancellationToken ct = default)
        => await _db.Performers
            .Include(p => p.Urls)
            .Include(p => p.Aliases)
            .Include(p => p.PerformerTags).ThenInclude(pt => pt.Tag).ThenInclude(tag => tag!.TagGroup)
            .Include(p => p.RemoteIds)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<Performer>> FindByNamesOrRemoteIdsAsync(
        IReadOnlyList<string> names,
        string? remoteEndpoint,
        IReadOnlyList<string> remoteIds,
        CancellationToken ct = default)
    {
        var query = _db.Performers
            .AsNoTracking()
            .Include(p => p.Aliases)
            .Include(p => p.RemoteIds);

        if (!string.IsNullOrWhiteSpace(remoteEndpoint) && remoteIds.Count > 0)
        {
            return await query
                .Where(p =>
                    (p.RemoteIds.Any(r => r.Endpoint == remoteEndpoint && remoteIds.Contains(r.RemoteId)))
                    || names.Contains(p.Name)
                    || p.Aliases.Any(a => names.Contains(a.Alias)))
                .ToListAsync(ct);
        }

        return await query
            .Where(p => names.Contains(p.Name) || p.Aliases.Any(a => names.Contains(a.Alias)))
            .ToListAsync(ct);
    }

    public async Task<Performer?> FindByRemoteIdAsync(string remoteEndpoint, string remoteId, CancellationToken ct = default)
        => await _db.Performers
            .Include(p => p.Aliases)
            .Include(p => p.RemoteIds)
            .FirstOrDefaultAsync(
                p => p.RemoteIds.Any(r => r.Endpoint == remoteEndpoint && r.RemoteId == remoteId),
                ct);

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
        ExpandedHierarchyCriterion? expandedTags = null;
        if (HierarchicalCriterionExpander.RequiresExpansion(filter?.TagsCriterion))
        {
            expandedTags = await HierarchicalCriterionExpander.ExpandTagsAsync(_db, filter!.TagsCriterion!, ct);
            filter.TagsCriterion = expandedTags.Criterion;
        }

        ExpandedHierarchyCriterion? expandedStudios = null;
        if (HierarchicalCriterionExpander.RequiresExpansion(filter?.StudiosCriterion))
        {
            expandedStudios = await HierarchicalCriterionExpander.ExpandStudiosAsync(_db, filter!.StudiosCriterion!, ct);
            filter.StudiosCriterion = expandedStudios.Criterion;
        }

        var currentPrincipal = _db.CurrentPrincipalForReadOptimization;
        var readScopePlan = await ReadScopeListOptimization.TryBuildPlanAsync<Performer>(
            _db,
            EntityKinds.Performer,
            currentPrincipal?.Has(PermissionKeys.PerformersRead) == true,
            currentPrincipal?.ReadGrantedEntityKinds.Contains(EntityKinds.Performer) == true,
            ct);

        var query = (readScopePlan ?? new ReadScopeRootPlan<Performer>(false, null)).Apply(_db.Performers.AsQueryable());
        var currentUserId = EngagementQueryHelpers.CurrentUserId(_db);

        if (filter != null)
        {
            if (!string.IsNullOrEmpty(filter.Name))
                query = query.Where(p => EF.Functions.ILike(p.Name, $"%{filter.Name}%"));
            if (filter.Favorite.HasValue)
                query = query.Where(p => p.Favorite == filter.Favorite.Value);
            if (filter.Rating.HasValue)
                query = EngagementQueryHelpers.ApplyRatingMinimum(_db, query, currentUserId, RatingHostType.Performer, filter.Rating.Value);
            if (filter.TagIds?.Count > 0)
                query = query.Where(p => p.PerformerTags.Any(pt => filter.TagIds.Contains(pt.TagId)));
            if (filter.StudioId.HasValue)
                query = query.Where(p => p.VideoPerformers.Any(sp => sp.Video!.StudioId == filter.StudioId.Value));

            // Advanced criteria
            query = FilterHelpers.ApplyString(query, filter.NameCriterion, p => p.Name);
            query = EngagementQueryHelpers.ApplyRatingCriterion(_db, query, currentUserId, RatingHostType.Performer, filter.RatingCriterion);
            query = FilterHelpers.ApplyInt(query, filter.HeightCriterion, p => p.HeightCm ?? 0);
            query = FilterHelpers.ApplyInt(query, filter.WeightCriterion, p => p.Weight ?? 0);

            if (filter.VideoCountCriterion != null)
            {
                query = filter.VideoCountCriterion.Modifier switch
                {
                    CriterionModifier.IsNull => query.Where(p => !p.VideoPerformers.Any()),
                    CriterionModifier.NotNull => query.Where(p => p.VideoPerformers.Any()),
                    _ => FilterHelpers.ApplyInt(query, filter.VideoCountCriterion, p => p.VideoPerformers.Count),
                };
            }

            if (filter.StudioCountCriterion != null)
            {
                query = filter.StudioCountCriterion.Modifier switch
                {
                    CriterionModifier.IsNull => query.Where(p => !p.VideoPerformers.Any(sp => sp.Video != null && sp.Video.StudioId.HasValue)),
                    CriterionModifier.NotNull => query.Where(p => p.VideoPerformers.Any(sp => sp.Video != null && sp.Video.StudioId.HasValue)),
                    _ => FilterHelpers.ApplyInt(query, filter.StudioCountCriterion, p => p.VideoPerformers
                        .Where(sp => sp.Video != null && sp.Video.StudioId.HasValue)
                        .Select(sp => sp.Video!.StudioId!.Value)
                        .Distinct()
                        .Count()),
                };
            }

            query = FilterHelpers.ApplyInt(query, filter.ImageCountCriterion, p => p.ImagePerformers.Count);
            query = FilterHelpers.ApplyInt(query, filter.GalleryCountCriterion, p => p.GalleryPerformers.Count);
            query = FilterHelpers.ApplyInt(query, filter.RemoteIdCountCriterion, p => p.RemoteIds.Count);

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
            query = FilterHelpers.ApplyMultiId(query, filter.TagsCriterion, p => p.PerformerTags.Select(pt => pt.TagId), expandedTags?.ValueGroups, expandedTags?.RequiredIdGroups);
            if (filter.StudiosCriterion is { Modifier: CriterionModifier.IncludesAll, Value.Count: > 0 } studiosCriterion)
            {
                if (expandedStudios?.ValueGroups is { Count: > 0 } studioGroups)
                {
                    foreach (var studioGroup in studioGroups.Where(group => group.Length > 0))
                    {
                        var requiredGroup = studioGroup;
                        query = query.Where(p => p.VideoPerformers.Any(sp => sp.Video != null && sp.Video.StudioId.HasValue && requiredGroup.Contains(sp.Video.StudioId.Value)));
                    }
                }
                else
                {
                    foreach (var studioId in studiosCriterion.Value.Distinct())
                    {
                        var requiredStudioId = studioId;
                        query = query.Where(p => p.VideoPerformers.Any(sp => sp.Video != null && sp.Video.StudioId == requiredStudioId));
                    }
                }

                if (studiosCriterion.Excludes?.Count > 0)
                {
                    var excludedStudioIds = studiosCriterion.Excludes.Distinct().ToArray();
                    query = query.Where(p => !p.VideoPerformers.Any(sp => sp.Video != null && sp.Video.StudioId.HasValue && excludedStudioIds.Contains(sp.Video.StudioId.Value)));
                }
            }
            else
            {
                query = FilterHelpers.ApplyMultiId(
                    query,
                    filter.StudiosCriterion,
                    p => p.VideoPerformers
                        .Where(sp => sp.Video != null && sp.Video.StudioId.HasValue)
                        .Select(sp => sp.Video!.StudioId!.Value),
                    expandedStudios?.ValueGroups,
                    expandedStudios?.RequiredIdGroups);
            }

            if (filter.StudiosCriterion is { Modifier: CriterionModifier.IncludesAll, Value.Count: > 0 }
                && (expandedStudios?.RequiredIdGroups is { Count: > 0 }
                    || filter.StudiosCriterion.RequiredIds is { Count: > 0 }))
            {
                foreach (var studioGroup in expandedStudios?.RequiredIdGroups
                    ?? (filter.StudiosCriterion.RequiredIds ?? []).Where(id => id > 0).Distinct().Select(id => new[] { id }).ToArray())
                {
                    var requiredStudioIds = studioGroup;
                    query = query.Where(p => p.VideoPerformers.Any(sp => sp.Video != null && sp.Video.StudioId.HasValue && requiredStudioIds.Contains(sp.Video.StudioId.Value)));
                }
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
            query = ApplyCareerLengthCriterion(query, filter.CareerLengthCriterion);

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

            // PenisLength as int (rounded)
            query = FilterHelpers.ApplyInt(query, filter.PenisLengthCriterion, p => (int)(p.PenisLength ?? 0));

            // Count criteria
            query = FilterHelpers.ApplyInt(query, filter.TagCountCriterion, p => p.TagCount);
            query = ApplyVideoAffinityIntSumCriterion(query, filter.PlayCountCriterion, nameof(UserEntityAffinity.ViewCount));
            query = ApplyVideoAffinityIntSumCriterion(query, filter.LikeCounterCriterion, nameof(UserEntityAffinity.LikeCount));

            // Groups criterion
            if (filter.GroupsCriterion != null)
            {
                var gIds = filter.GroupsCriterion.Value;
                query = filter.GroupsCriterion.Modifier switch
                {
                    CriterionModifier.IsNull => query.Where(p => !p.VideoPerformers.Any(sp => sp.Video!.GroupItems.Any())),
                    CriterionModifier.NotNull => query.Where(p => p.VideoPerformers.Any(sp => sp.Video!.GroupItems.Any())),
                    CriterionModifier.Includes => query.Where(p => p.VideoPerformers.Any(sp => sp.Video!.GroupItems.Any(item => gIds.Contains(item.GroupId)))),
                    CriterionModifier.Excludes => query.Where(p => !p.VideoPerformers.Any(sp => sp.Video!.GroupItems.Any(item => gIds.Contains(item.GroupId)))),
                    _ when gIds.Count == 0 => query,
                    _ => query.Where(p => p.VideoPerformers.Any(sp => sp.Video!.GroupItems.Any(item => gIds.Contains(item.GroupId)))),
                };
            }

            query = FilterHelpers.ApplyRemoteId(query, filter.RemoteIdCriterion, filter.RemoteIdValueCriterion, performer => performer.RemoteIds, remoteId => remoteId.Endpoint, remoteId => remoteId.RemoteId);

            query = query.ApplyCustomFieldCriteria(_db, CustomFieldEntityTypes.Performer, filter.CustomFieldCriterion, filter.CustomFieldCriteria);
        }

        query = ApplyPerformerSearch(query, findFilter?.Q);

        var totalCount = await query.AsNoTracking().CountAsync(ct);

        var multiSortRegistry = CreatePerformerMultiSortRegistry(EngagementQueryHelpers.CurrentUserId(_db));
        var sortClauses = multiSortRegistry.Normalize(findFilter?.Sorts);
        var primarySort = sortClauses.FirstOrDefault();
        var hasExplicitSort = sortClauses.Count > 0 || !string.IsNullOrWhiteSpace(findFilter?.Sort);
        var sort = primarySort?.Key ?? findFilter?.Sort ?? "name";
        var desc = primarySort?.Direction == Core.Enums.SortDirection.Desc
            || (primarySort is null && findFilter?.Direction == Core.Enums.SortDirection.Desc);
        query = sortClauses.Count > 1
            ? ApplyPerformerMultiSort(query, sortClauses, multiSortRegistry)
            : FilterHelpers.TryParseCustomFieldSort(sort, out _, out _)
            ? query.ApplyCustomFieldSort(_db, CustomFieldEntityTypes.Performer, sort, desc)
            : sort switch
            {
            "name" => desc ? query.OrderByDescending(p => p.Name) : query.OrderBy(p => p.Name),
            "rating" => EngagementQueryHelpers.ApplyRatingSort(_db, query, EngagementQueryHelpers.CurrentUserId(_db), RatingHostType.Performer, desc),
            "created_at" => desc ? query.OrderByDescending(p => p.CreatedAt) : query.OrderBy(p => p.CreatedAt),
            "birthdate" => desc ? query.OrderByDescending(p => p.Birthdate) : query.OrderBy(p => p.Birthdate),
            "video_count" => desc ? query.OrderByDescending(p => p.VideoPerformers.Count) : query.OrderBy(p => p.VideoPerformers.Count),
            "image_count" => desc ? query.OrderByDescending(p => p.ImagePerformers.Count) : query.OrderBy(p => p.ImagePerformers.Count),
            "gallery_count" => desc ? query.OrderByDescending(p => p.GalleryPerformers.Count) : query.OrderBy(p => p.GalleryPerformers.Count),
            "latest_video_date" => desc ? query.OrderByDescending(p => p.VideoPerformers.Max(sp => sp.Video!.Date)) : query.OrderBy(p => p.VideoPerformers.Max(sp => sp.Video!.Date)),
            "total_file_size" => desc ? query.OrderByDescending(p => p.VideoPerformers.Sum(sp => (long?)sp.Video!.MaxFileSize) ?? 0L) : query.OrderBy(p => p.VideoPerformers.Sum(sp => (long?)sp.Video!.MaxFileSize) ?? 0L),
            "career_length" => ApplyCareerLengthSort(query, desc),
            "height" => ApplyHeightSort(query, desc),
            "weight" => desc ? query.OrderByDescending(p => p.Weight) : query.OrderBy(p => p.Weight),
            "measurements" => ApplyMeasurementsSort(query, desc),
            "tag_count" => desc ? query.OrderByDescending(p => p.TagCount) : query.OrderBy(p => p.TagCount),
            "like_counter" => ApplyVideoAffinityIntSumSort(query, nameof(UserEntityAffinity.LikeCount), desc),
            "play_count" => ApplyPlayCountSort(query, desc),
            "last_like_at" => EngagementQueryHelpers.ApplyAffinityTimestampSort(_db, query, EngagementQueryHelpers.CurrentUserId(_db), AffinityHostType.Performer, nameof(UserEntityAffinity.FavoritedAt), desc),
            "last_played_at" => ApplyLastPlayedAtSort(query, desc),
            "random" => SeededRandomOrdering.OrderBy(query, findFilter?.Seed, p => p.Id, desc),
            _ => desc ? query.OrderByDescending(p => p.UpdatedAt) : query.OrderBy(p => p.UpdatedAt),
            };
        if (!hasExplicitSort)
            query = FullTextSearchHelpers.OrderByRelevance(_db, query, findFilter?.Q);

        var page = findFilter?.Page ?? 1;
        var perPage = findFilter?.PerPage ?? 25;

        if (perPage <= 0)
        {
            return (Array.Empty<Performer>(), totalCount);
        }

        var pagedIds = await query
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .Select(p => p.Id)
            .ToListAsync(ct);

        if (pagedIds.Count == 0)
        {
            return (Array.Empty<Performer>(), totalCount);
        }

        var items = await _db.Performers
            .Include(p => p.Urls)
            .Include(p => p.Aliases)
            .Include(p => p.PerformerTags).ThenInclude(pt => pt.Tag).ThenInclude(tag => tag!.TagGroup)
            .Include(p => p.RemoteIds)
            .AsSplitQuery()
            .Where(p => pagedIds.Contains(p.Id))
            .AsNoTracking()
            .ToListAsync(ct);

        var orderMap = pagedIds.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        var sortedItems = items.OrderBy(p => orderMap.GetValueOrDefault(p.Id, int.MaxValue)).ToList();

        return (sortedItems, totalCount);
    }

    private CompoundSortRegistry<Performer> CreatePerformerMultiSortRegistry(int? userId)
        => new(new Dictionary<string, Action<CompoundSortQuery<Performer>, bool>>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = (compound, desc) => compound.Append(performer => performer.Name, desc),
            ["rating"] = (compound, desc) => compound.AppendRating(desc),
            ["created_at"] = (compound, desc) => compound.Append(performer => performer.CreatedAt, desc),
            ["updated_at"] = (compound, desc) => compound.Append(performer => performer.UpdatedAt, desc),
            ["birthdate"] = (compound, desc) => compound.Append(performer => performer.Birthdate, desc),
            ["video_count"] = (compound, desc) => compound.Append(performer => performer.VideoPerformers.Count, desc),
            ["image_count"] = (compound, desc) => compound.Append(performer => performer.ImagePerformers.Count, desc),
            ["gallery_count"] = (compound, desc) => compound.Append(performer => performer.GalleryPerformers.Count, desc),
            ["latest_video_date"] = (compound, desc) => compound.Append(performer => performer.VideoPerformers.Max(link => link.Video!.Date), desc),
            ["total_file_size"] = (compound, desc) => compound.Append(performer => performer.VideoPerformers.Sum(link => (long?)link.Video!.MaxFileSize) ?? 0L, desc),
            ["height"] = (compound, desc) => compound.Append(performer => performer.HeightCm, desc),
            ["weight"] = (compound, desc) => compound.Append(performer => performer.Weight, desc),
            ["tag_count"] = (compound, desc) => compound.Append(performer => performer.TagCount, desc),
            ["like_counter"] = (compound, desc) =>
            {
                if (userId is int selectedUserId)
                    compound.Append(performer => _db.UserEntityAffinities
                        .Where(affinity => affinity.UserId == selectedUserId && affinity.HostType == AffinityHostType.Video
                            && performer.VideoPerformers.Any(link => link.VideoId == affinity.HostId))
                        .Sum(affinity => affinity.LikeCount), desc);
            },
            ["play_count"] = (compound, desc) =>
            {
                if (userId is int selectedUserId)
                    compound.Append(performer => _db.UserEntityAffinities
                        .Where(affinity => affinity.UserId == selectedUserId && affinity.HostType == AffinityHostType.Video
                            && performer.VideoPerformers.Any(link => link.VideoId == affinity.HostId))
                        .Sum(affinity => affinity.ViewCount), desc);
            },
            ["last_like_at"] = (compound, desc) => compound.AppendAffinityTimestamp(nameof(UserEntityAffinity.FavoritedAt), desc),
            ["last_played_at"] = (compound, desc) =>
            {
                if (userId is int selectedUserId)
                    compound.Append(performer => _db.UserEntityAffinities
                        .Where(affinity => affinity.UserId == selectedUserId && affinity.HostType == AffinityHostType.Video
                            && performer.VideoPerformers.Any(link => link.VideoId == affinity.HostId))
                        .Max(affinity => affinity.LastConsumedAt) ?? (desc ? DateTime.MinValue : DateTime.MaxValue), desc);
            },
        });

    private IQueryable<Performer> ApplyPerformerMultiSort(
        IQueryable<Performer> query,
        IReadOnlyList<SortClause> clauses,
        CompoundSortRegistry<Performer> registry)
    {
        var userId = EngagementQueryHelpers.CurrentUserId(_db);
        var compound = CompoundSortQuery<Performer>.Create(
            _db, query, userId, AffinityHostType.Performer, RatingHostType.Performer,
            includeAffinity: clauses.Any(clause => clause.Key.Equals("last_like_at", StringComparison.OrdinalIgnoreCase)),
            includeRating: clauses.Any(clause => clause.Key.Equals("rating", StringComparison.OrdinalIgnoreCase)));
        registry.Apply(compound, clauses);

        return compound.Finish(performer => performer.Id);
    }

}

public class TagRepository : ITagRepository
{
    private readonly record struct TagEntityPair(int TagId, int EntityId);

    private readonly CoveContext _db;
    public TagRepository(CoveContext db) => _db = db;

    private IQueryable<Tag> ApplyTagSearch(IQueryable<Tag> query, string? search)
    {
        var textQuery = FullTextSearchHelpers.Apply(_db, query, search,
            t => t.Name,
            t => t.SortName,
            t => t.Description,
            t => t.SearchText);

        var normalized = search?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return textQuery;
        var normalizedLower = normalized.ToLowerInvariant();

        return textQuery
            .Concat(query.Where(t => t.Aliases.Any(alias => alias.Alias.ToLower().Contains(normalizedLower))))
            .Distinct();
    }

    public async Task<Tag?> GetByIdAsync(int id, CancellationToken ct = default)
        => await _db.Tags.FindAsync([id], ct);

    public async Task<Tag?> GetByIdWithRelationsAsync(int id, CancellationToken ct = default)
        => await _db.Tags
            .Include(t => t.Aliases)
            .Include(t => t.TagGroup)
            .Include(t => t.RemoteIds)
            .Include(t => t.ParentRelations).ThenInclude(tp => tp.Parent).ThenInclude(parent => parent!.TagGroup)
            .Include(t => t.ChildRelations).ThenInclude(tp => tp.Child).ThenInclude(child => child!.TagGroup)
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
        var query = _db.Tags.AsQueryable();

        if (filter != null)
        {
            if (!string.IsNullOrEmpty(filter.Name))
                query = query.Where(t => EF.Functions.ILike(t.Name, $"%{filter.Name}%"));
            if (filter.Favorite.HasValue)
                query = query.Where(t => t.Favorite == filter.Favorite.Value);

            // Rating (overall) — minimum + advanced criterion, joined from the per-user Rating table.
            var currentUserId = EngagementQueryHelpers.CurrentUserId(_db);
            if (filter.Rating.HasValue)
                query = EngagementQueryHelpers.ApplyRatingMinimum(_db, query, currentUserId, RatingHostType.Tag, filter.Rating.Value);
            query = EngagementQueryHelpers.ApplyRatingCriterion(_db, query, currentUserId, RatingHostType.Tag, filter.RatingCriterion);

            // Advanced criteria
            if (filter.FavoriteCriterion != null)
                query = query.Where(t => t.Favorite == filter.FavoriteCriterion.Value);

            // Multi-ID criteria
            query = FilterHelpers.ApplyMultiId(query, filter.ParentsCriterion, t => t.ParentRelations.Select(tp => tp.ParentId));
            query = FilterHelpers.ApplyMultiId(query, filter.ChildrenCriterion, t => t.ChildRelations.Select(tp => tp.ChildId));
            query = FilterHelpers.ApplyStudioCriterion(query, filter.TagGroupsCriterion, t => t.TagGroupId);

            // Timestamp criteria
            query = FilterHelpers.ApplyTimestamp(query, filter.CreatedAtCriterion, t => t.CreatedAt);
            query = FilterHelpers.ApplyTimestamp(query, filter.UpdatedAtCriterion, t => t.UpdatedAt);

            // String criteria
            query = FilterHelpers.ApplyString(query, filter.NameCriterion, t => t.Name);
            query = FilterHelpers.ApplyString(query, filter.SortNameCriterion, t => t.SortName);
            query = FilterHelpers.ApplyString(query, filter.DescriptionCriterion, t => t.Description);

            query = FilterHelpers.ApplyRemoteId(query, filter.RemoteIdCriterion, filter.RemoteIdValueCriterion, tag => tag.RemoteIds, remoteId => remoteId.Endpoint, remoteId => remoteId.RemoteId);

            query = FilterHelpers.ApplyInt(query, filter.RemoteIdCountCriterion, t => t.RemoteIds.Count);

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

            query = query.ApplyCustomFieldCriteria(_db, CustomFieldEntityTypes.Tag, filter.CustomFieldCriterion, filter.CustomFieldCriteria);
        }

        query = ApplyTagSearch(query, findFilter?.Q);

        if (filter != null)
        {
            query = await ApplyTagCountCriteriaAsync(query, filter, ct);
        }

        var perPage = findFilter?.PerPage ?? 25;
        if (perPage <= 0)
        {
            var count = await query.CountAsync(ct);
            return (Array.Empty<Tag>(), count);
        }

        var totalCount = await query.AsNoTracking().CountAsync(ct);

        var multiSortRegistry = CreateTagMultiSortRegistry();
        var sortClauses = multiSortRegistry.Normalize(findFilter?.Sorts);
        var primarySort = sortClauses.FirstOrDefault();
        var hasExplicitSort = sortClauses.Count > 0 || !string.IsNullOrWhiteSpace(findFilter?.Sort);
        var sort = primarySort?.Key ?? findFilter?.Sort ?? "name";
        var desc = primarySort?.Direction == Core.Enums.SortDirection.Desc
            || (primarySort is null && findFilter?.Direction == Core.Enums.SortDirection.Desc);
        query = sortClauses.Count > 1
            ? ApplyTagMultiSort(query, sortClauses, multiSortRegistry)
            : FilterHelpers.TryParseCustomFieldSort(sort, out _, out _)
            ? query.ApplyCustomFieldSort(_db, CustomFieldEntityTypes.Tag, sort, desc)
            : sort switch
            {
            "name" => ApplyStableTagSort(query, t => t.Name, desc),
            "rating" => EngagementQueryHelpers.ApplyRatingSort(_db, query, EngagementQueryHelpers.CurrentUserId(_db), RatingHostType.Tag, desc),
            "tag_group" => ApplyTagGroupSort(query, desc),
            "video_count" => ApplyStableTagSort(query, t => t.VideoCount, desc),
            "gallery_count" => ApplyStableTagSort(query, t => t.GalleryCount, desc),
            "group_count" => ApplyStableTagSort(query, t => t.GroupCount, desc),
            "image_count" => ApplyStableTagSort(query, t => t.ImageCount, desc),
            "performer_count" => ApplyStableTagSort(query, t => t.PerformerCount, desc),
            "studio_count" => ApplyStableTagSort(query, t => t.StudioCount, desc),
            "latest_video_date" => ApplyStableTagSort(query, t => t.VideoTags.Max(st => st.Video!.Date), desc),
            "total_file_size" => ApplyStableTagSort(query, t => t.VideoTags.Sum(st => (long?)st.Video!.MaxFileSize) ?? 0L, desc),
            "created_at" => ApplyStableTagSort(query, t => t.CreatedAt, desc),
            "updated_at" => ApplyStableTagSort(query, t => t.UpdatedAt, desc),
            "random" => SeededRandomOrdering.OrderBy(query, findFilter?.Seed, t => t.Id, desc),
            _ => ApplyStableTagSort(query, t => t.UpdatedAt, desc),
            };
        if (!hasExplicitSort)
            query = FullTextSearchHelpers.OrderByRelevance(_db, query, findFilter?.Q);

        var page = findFilter?.Page ?? 1;
        var pagedIds = await query
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .Select(t => t.Id)
            .ToListAsync(ct);

        if (pagedIds.Count == 0)
        {
            return (Array.Empty<Tag>(), totalCount);
        }

        var items = await _db.Tags
            .Include(t => t.Aliases)
            .Include(t => t.TagGroup)
            .AsSplitQuery()
            .Where(t => pagedIds.Contains(t.Id))
            .AsNoTracking()
            .ToListAsync(ct);

        var orderMap = pagedIds.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        var sortedItems = items.OrderBy(t => orderMap.GetValueOrDefault(t.Id, int.MaxValue)).ToList();

        return (sortedItems, totalCount);
    }

    private static IQueryable<Tag> ApplyTagGroupSort(IQueryable<Tag> query, bool desc)
    {
        var sortQuery = query.Select(tag => new
        {
            Tag = tag,
            HasGroup = tag.TagGroupId.HasValue,
            GroupSortOrder = tag.TagGroup != null ? tag.TagGroup.SortOrder : int.MaxValue,
            GroupName = tag.TagGroup != null ? tag.TagGroup.Name : null,
            tag.Name,
        });

        return desc
            ? sortQuery.OrderBy(item => item.HasGroup ? 0 : 1).ThenByDescending(item => item.GroupSortOrder).ThenByDescending(item => item.GroupName).ThenByDescending(item => item.Name).ThenBy(item => item.Tag.Id).Select(item => item.Tag)
            : sortQuery.OrderBy(item => item.HasGroup ? 0 : 1).ThenBy(item => item.GroupSortOrder).ThenBy(item => item.GroupName).ThenBy(item => item.Name).ThenBy(item => item.Tag.Id).Select(item => item.Tag);
    }

    private static IOrderedQueryable<Tag> ApplyStableTagSort<TKey>(
        IQueryable<Tag> query,
        Expression<Func<Tag, TKey>> keySelector,
        bool desc)
        => desc
            ? query.OrderByDescending(keySelector).ThenBy(tag => tag.Id)
            : query.OrderBy(keySelector).ThenBy(tag => tag.Id);

    private static CompoundSortRegistry<Tag> CreateTagMultiSortRegistry()
        => new(new Dictionary<string, Action<CompoundSortQuery<Tag>, bool>>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = (compound, desc) => compound.Append(tag => tag.Name, desc),
            ["rating"] = (compound, desc) => compound.AppendRating(desc),
            ["video_count"] = (compound, desc) => compound.Append(tag => tag.VideoCount, desc),
            ["gallery_count"] = (compound, desc) => compound.Append(tag => tag.GalleryCount, desc),
            ["group_count"] = (compound, desc) => compound.Append(tag => tag.GroupCount, desc),
            ["image_count"] = (compound, desc) => compound.Append(tag => tag.ImageCount, desc),
            ["performer_count"] = (compound, desc) => compound.Append(tag => tag.PerformerCount, desc),
            ["studio_count"] = (compound, desc) => compound.Append(tag => tag.StudioCount, desc),
            ["latest_video_date"] = (compound, desc) => compound.Append(tag => tag.VideoTags.Max(link => link.Video!.Date), desc),
            ["total_file_size"] = (compound, desc) => compound.Append(tag => tag.VideoTags.Sum(link => (long?)link.Video!.MaxFileSize) ?? 0L, desc),
            ["created_at"] = (compound, desc) => compound.Append(tag => tag.CreatedAt, desc),
            ["updated_at"] = (compound, desc) => compound.Append(tag => tag.UpdatedAt, desc),
        });

    private IQueryable<Tag> ApplyTagMultiSort(IQueryable<Tag> query, IReadOnlyList<SortClause> clauses, CompoundSortRegistry<Tag> registry)
    {
        var userId = EngagementQueryHelpers.CurrentUserId(_db);
        var compound = CompoundSortQuery<Tag>.Create(
            _db, query, userId, null, RatingHostType.Tag,
            includeAffinity: false,
            includeRating: clauses.Any(clause => clause.Key.Equals("rating", StringComparison.OrdinalIgnoreCase)));
        registry.Apply(compound, clauses);

        return compound.Finish(tag => tag.Id);
    }

    private async Task<IQueryable<Tag>> ApplyTagCountCriteriaAsync(IQueryable<Tag> query, TagFilter filter, CancellationToken ct)
    {
        query = filter.VideoCountIncludesChildren
            ? query
            : FilterHelpers.ApplyInt(query, filter.VideoCountCriterion, t => t.VideoCount);
        query = filter.PerformerCountIncludesChildren
            ? query
            : FilterHelpers.ApplyInt(query, filter.PerformerCountCriterion, t => t.PerformerCount);
        query = filter.ImageCountIncludesChildren
            ? query
            : FilterHelpers.ApplyInt(query, filter.ImageCountCriterion, t => t.ImageCount);
        query = filter.GalleryCountIncludesChildren
            ? query
            : FilterHelpers.ApplyInt(query, filter.GalleryCountCriterion, t => t.GalleryCount);
        query = filter.StudioCountIncludesChildren
            ? query
            : FilterHelpers.ApplyInt(query, filter.StudioCountCriterion, t => t.StudioCount);
        query = filter.GroupCountIncludesChildren
            ? query
            : FilterHelpers.ApplyInt(query, filter.GroupCountCriterion, t => t.GroupCount);
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

        Dictionary<int, int>? videoCountsByTagId = null;
        Dictionary<int, int>? performerCountsByTagId = null;
        Dictionary<int, int>? imageCountsByTagId = null;
        Dictionary<int, int>? galleryCountsByTagId = null;
        Dictionary<int, int>? studioCountsByTagId = null;
        Dictionary<int, int>? groupCountsByTagId = null;

        if (filter.VideoCountIncludesChildren && filter.VideoCountCriterion != null)
        {
            videoCountsByTagId = CountDistinctEntitiesByRootTagId(
                await _db.Set<VideoTag>()
                    .AsNoTracking()
                    .Where(videoTag => relevantTagIds.Contains(videoTag.TagId))
                    .Select(videoTag => new TagEntityPair(videoTag.TagId, videoTag.VideoId))
                    .ToListAsync(ct),
                rootTagIdsByDescendantTagId);
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
            return MatchesTagCountCriterion(filter.VideoCountCriterion, filter.VideoCountIncludesChildren, tagId, videoCountsByTagId)
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
        => (filter.VideoCountIncludesChildren && filter.VideoCountCriterion != null)
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

    public async Task<IReadOnlyList<Tag>> FindByNamesAsync(IReadOnlyList<string> names, CancellationToken ct = default)
    {
        var loweredNames = names.Select(n => n.ToLowerInvariant()).ToList();
        return await _db.Tags
            .Where(t => loweredNames.Contains(t.Name.ToLower()))
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<Dictionary<string, Tag>> FindOrCreateByNamesAsync(IReadOnlyList<string> names, CancellationToken ct = default)
    {
        const string tagNameUniqueConstraint = "IX_tags_Name";
        const int maxAttempts = 3;

        var normalizedNames = names
            .Where(static n => !string.IsNullOrWhiteSpace(n))
            .Select(static n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedNames.Length == 0)
            return new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase);

        var lowered = normalizedNames.Select(static n => n.ToLowerInvariant()).ToArray();

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var existing = await _db.Tags
                .Where(t => lowered.Contains(t.Name.ToLower()))
                .ToListAsync(ct);

            var byName = existing.ToDictionary(static t => t.Name, StringComparer.OrdinalIgnoreCase);
            var created = new List<Tag>();
            foreach (var name in normalizedNames)
            {
                if (byName.ContainsKey(name)) continue;
                var tag = new Tag { Name = name, SortName = name };
                _db.Tags.Add(tag);
                byName[name] = tag;
                created.Add(tag);
            }

            if (created.Count == 0) return byName;

            try
            {
                await _db.SaveChangesAsync(ct);
                return byName;
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
                when (attempt < maxAttempts - 1)
            {
                var inner = ex.InnerException;
                var sqlState = inner?.GetType().GetProperty("SqlState")?.GetValue(inner) as string;
                var constraint = inner?.GetType().GetProperty("ConstraintName")?.GetValue(inner) as string;
                if (sqlState != "23505" || constraint != tagNameUniqueConstraint) throw;
                foreach (var tag in created)
                    _db.Entry(tag).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            }
        }

        throw new InvalidOperationException("Could not resolve tags after duplicate-name retry.");
    }
}

public class StudioRepository : IStudioRepository
{
    private readonly CoveContext _db;
    public StudioRepository(CoveContext db) => _db = db;

    private IQueryable<Studio> ApplyStudioSearch(IQueryable<Studio> query, string? search)
    {
        var textQuery = FullTextSearchHelpers.Apply(_db, query, search,
            s => s.Name,
            s => s.Details,
            s => s.SearchText);

        var normalized = search?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return textQuery;
        var normalizedLower = normalized.ToLowerInvariant();

        var withAliases = textQuery
            .Concat(query.Where(s => s.Aliases.Any(alias => alias.Alias.ToLower().Contains(normalizedLower))));

        return FullTextSearchHelpers.ApplyRelationalMatches(withAliases, query, search,
            tagSelectors: [s => s.StudioTags.Where(st => st.Tag != null).Select(st => st.Tag!)]);
    }

    private IQueryable<Studio> ApplyStudioRatingSort(IQueryable<Studio> query, bool desc)
        => EngagementQueryHelpers.ApplyRatingSort(_db, query, EngagementQueryHelpers.CurrentUserId(_db), RatingHostType.Studio, desc);

    public async Task<Studio?> GetByIdAsync(int id, CancellationToken ct = default) => await _db.Studios.FindAsync([id], ct);

    public async Task<Studio?> GetByIdWithRelationsAsync(int id, CancellationToken ct = default)
        => await _db.Studios
            .Include(s => s.Parent)
            .Include(s => s.Urls).Include(s => s.Aliases)
            .Include(s => s.StudioTags).ThenInclude(st => st.Tag).ThenInclude(tag => tag!.TagGroup)
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
        ExpandedHierarchyCriterion? expandedTags = null;
        if (HierarchicalCriterionExpander.RequiresExpansion(filter?.TagsCriterion))
        {
            expandedTags = await HierarchicalCriterionExpander.ExpandTagsAsync(_db, filter!.TagsCriterion!, ct);
            filter.TagsCriterion = expandedTags.Criterion;
        }

        var query = _db.Studios.AsQueryable();
        if (filter != null)
        {
            if (!string.IsNullOrEmpty(filter.Name)) query = query.Where(s => EF.Functions.ILike(s.Name, $"%{filter.Name}%"));
            if (filter.Favorite.HasValue) query = query.Where(s => s.Favorite == filter.Favorite.Value);
            if (filter.ParentId.HasValue) query = query.Where(s => s.ParentId == filter.ParentId.Value);
            if (filter.TagIds?.Count > 0) query = query.Where(s => s.StudioTags.Any(st => filter.TagIds.Contains(st.TagId)));

            // Advanced criteria
            query = EngagementQueryHelpers.ApplyRatingCriterion(_db, query, EngagementQueryHelpers.CurrentUserId(_db), RatingHostType.Studio, filter.RatingCriterion);
            query = FilterHelpers.ApplyInt(query, filter.VideoCountCriterion, s => s.VideoCount);
            query = FilterHelpers.ApplyInt(query, filter.GalleryCountCriterion, s => s.GalleryCount);
            query = FilterHelpers.ApplyInt(query, filter.ImageCountCriterion, s => s.ImageCount);

            if (filter.FavoriteCriterion != null)
                query = query.Where(s => s.Favorite == filter.FavoriteCriterion.Value);

            // Multi-ID criteria
            query = FilterHelpers.ApplyMultiId(query, filter.TagsCriterion, s => s.StudioTags.Select(st => st.TagId), expandedTags?.ValueGroups, expandedTags?.RequiredIdGroups);

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

            query = FilterHelpers.ApplyRemoteId(query, filter.RemoteIdCriterion, filter.RemoteIdValueCriterion, studio => studio.RemoteIds, remoteId => remoteId.Endpoint, remoteId => remoteId.RemoteId);

            query = FilterHelpers.ApplyInt(query, filter.RemoteIdCountCriterion, s => s.RemoteIds.Count);

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

            // Parents (multi-ID on the single parent studio FK)
            query = FilterHelpers.ApplyStudioCriterion(query, filter.ParentsCriterion, s => s.ParentId);

            // Count criteria
            query = FilterHelpers.ApplyInt(query, filter.ParentCountCriterion, s => s.ParentId.HasValue ? 1 : 0);
            query = FilterHelpers.ApplyInt(query, filter.ChildCountCriterion, s => s.ChildStudioCount);
            query = FilterHelpers.ApplyInt(query, filter.TagCountCriterion, s => s.TagCount);
            query = FilterHelpers.ApplyInt(query, filter.GroupCountCriterion, s => s.GroupCount);

            // Bool criteria
            if (filter.OrganizedCriterion != null)
                query = query.Where(s => s.Organized == filter.OrganizedCriterion.Value);

            query = query.ApplyCustomFieldCriteria(_db, CustomFieldEntityTypes.Studio, filter.CustomFieldCriterion, filter.CustomFieldCriteria);
        }
        query = ApplyStudioSearch(query, findFilter?.Q);

        var perPage = findFilter?.PerPage ?? 25;
        if (perPage <= 0)
        {
            var count = await query.CountAsync(ct);
            return (Array.Empty<Studio>(), count);
        }

        var totalCount = await query.AsNoTracking().CountAsync(ct);
        var multiSortRegistry = CreateStudioMultiSortRegistry();
        var sortClauses = multiSortRegistry.Normalize(findFilter?.Sorts);
        var primarySort = sortClauses.FirstOrDefault();
        var hasExplicitSort = sortClauses.Count > 0 || !string.IsNullOrWhiteSpace(findFilter?.Sort);
        var sort = primarySort?.Key ?? findFilter?.Sort ?? "name";
        var desc = primarySort?.Direction == Core.Enums.SortDirection.Desc
            || (primarySort is null && findFilter?.Direction == Core.Enums.SortDirection.Desc);
        query = sortClauses.Count > 1
            ? ApplyStudioMultiSort(query, sortClauses, multiSortRegistry)
            : FilterHelpers.TryParseCustomFieldSort(sort, out _, out _)
            ? query.ApplyCustomFieldSort(_db, CustomFieldEntityTypes.Studio, sort, desc)
            : sort switch
            {
            "name" => desc ? query.OrderByDescending(s => s.Name) : query.OrderBy(s => s.Name),
            "video_count" => desc ? query.OrderByDescending(s => s.VideoCount) : query.OrderBy(s => s.VideoCount),
            "gallery_count" => desc ? query.OrderByDescending(s => s.GalleryCount) : query.OrderBy(s => s.GalleryCount),
            "image_count" => desc ? query.OrderByDescending(s => s.ImageCount) : query.OrderBy(s => s.ImageCount),
            "latest_video_date" => desc ? query.OrderByDescending(s => s.Videos.Max(video => video.Date)) : query.OrderBy(s => s.Videos.Max(video => video.Date)),
            "total_file_size" => desc ? query.OrderByDescending(s => s.Videos.Sum(video => (long?)video.MaxFileSize) ?? 0L) : query.OrderBy(s => s.Videos.Sum(video => (long?)video.MaxFileSize) ?? 0L),
            "rating" => ApplyStudioRatingSort(query, desc),
            "parent_count" => desc ? query.OrderByDescending(s => s.ParentId.HasValue ? 1 : 0).ThenByDescending(s => s.Id) : query.OrderBy(s => s.ParentId.HasValue ? 1 : 0).ThenBy(s => s.Id),
            "child_count" => desc ? query.OrderByDescending(s => s.ChildStudioCount) : query.OrderBy(s => s.ChildStudioCount),
            "tag_count" => desc ? query.OrderByDescending(s => s.TagCount) : query.OrderBy(s => s.TagCount),
            "created_at" => desc ? query.OrderByDescending(s => s.CreatedAt) : query.OrderBy(s => s.CreatedAt),
            "updated_at" => desc ? query.OrderByDescending(s => s.UpdatedAt) : query.OrderBy(s => s.UpdatedAt),
            "random" => SeededRandomOrdering.OrderBy(query, findFilter?.Seed, s => s.Id, desc),
            _ => desc ? query.OrderByDescending(s => s.UpdatedAt) : query.OrderBy(s => s.UpdatedAt),
            };
        if (!hasExplicitSort)
            query = FullTextSearchHelpers.OrderByRelevance(_db, query, findFilter?.Q);
        var page = findFilter?.Page ?? 1;
        var pagedIds = await query
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .Select(s => s.Id)
            .ToListAsync(ct);

        if (pagedIds.Count == 0)
        {
            return (Array.Empty<Studio>(), totalCount);
        }

        var items = await _db.Studios
            .Include(s => s.Parent)
            .Include(s => s.Urls)
            .Include(s => s.Aliases)
            .Include(s => s.StudioTags).ThenInclude(st => st.Tag).ThenInclude(tag => tag!.TagGroup)
            .Include(s => s.RemoteIds)
            .AsSplitQuery()
            .Where(s => pagedIds.Contains(s.Id))
            .AsNoTracking()
            .ToListAsync(ct);

        var orderMap = pagedIds.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        var sortedItems = items.OrderBy(s => orderMap.GetValueOrDefault(s.Id, int.MaxValue)).ToList();
        return (sortedItems, totalCount);
    }

    private static CompoundSortRegistry<Studio> CreateStudioMultiSortRegistry()
        => new(new Dictionary<string, Action<CompoundSortQuery<Studio>, bool>>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = (compound, desc) => compound.Append(studio => studio.Name, desc),
            ["rating"] = (compound, desc) => compound.AppendRating(desc),
            ["video_count"] = (compound, desc) => compound.Append(studio => studio.VideoCount, desc),
            ["gallery_count"] = (compound, desc) => compound.Append(studio => studio.GalleryCount, desc),
            ["image_count"] = (compound, desc) => compound.Append(studio => studio.ImageCount, desc),
            ["latest_video_date"] = (compound, desc) => compound.Append(studio => studio.Videos.Max(video => video.Date), desc),
            ["total_file_size"] = (compound, desc) => compound.Append(studio => studio.Videos.Sum(video => (long?)video.MaxFileSize) ?? 0L, desc),
            ["parent_count"] = (compound, desc) => compound.Append(studio => studio.ParentId.HasValue ? 1 : 0, desc),
            ["child_count"] = (compound, desc) => compound.Append(studio => studio.ChildStudioCount, desc),
            ["tag_count"] = (compound, desc) => compound.Append(studio => studio.TagCount, desc),
            ["created_at"] = (compound, desc) => compound.Append(studio => studio.CreatedAt, desc),
            ["updated_at"] = (compound, desc) => compound.Append(studio => studio.UpdatedAt, desc),
        });

    private IQueryable<Studio> ApplyStudioMultiSort(IQueryable<Studio> query, IReadOnlyList<SortClause> clauses, CompoundSortRegistry<Studio> registry)
    {
        var userId = EngagementQueryHelpers.CurrentUserId(_db);
        var compound = CompoundSortQuery<Studio>.Create(
            _db, query, userId, null, RatingHostType.Studio,
            includeAffinity: false,
            includeRating: clauses.Any(clause => clause.Key.Equals("rating", StringComparison.OrdinalIgnoreCase)));
        registry.Apply(compound, clauses);

        return compound.Finish(studio => studio.Id);
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
            .Include(g => g.GalleryTags).ThenInclude(gt => gt.Tag).ThenInclude(tag => tag!.TagGroup)
            .Include(g => g.GalleryPerformers).ThenInclude(gp => gp.Performer)
            .Include(g => g.Chapters)
            .Include(g => g.Files).ThenInclude(f => f.ParentFolder)
            .Include(g => g.Files).ThenInclude(f => f.Fingerprints)
            .Include(g => g.Folder)
            .Include(g => g.VideoGalleries)
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
        ExpandedHierarchyCriterion? expandedTags = null;
        if (HierarchicalCriterionExpander.RequiresExpansion(filter?.TagsCriterion))
        {
            expandedTags = await HierarchicalCriterionExpander.ExpandTagsAsync(_db, filter!.TagsCriterion!, ct);
            filter.TagsCriterion = expandedTags.Criterion;
        }
        ExpandedHierarchyCriterion? expandedStudios = null;
        if (HierarchicalCriterionExpander.RequiresExpansion(filter?.StudiosCriterion))
        {
            expandedStudios = await HierarchicalCriterionExpander.ExpandStudiosAsync(_db, filter!.StudiosCriterion!, ct);
            filter.StudiosCriterion = expandedStudios.Criterion;
        }

        var query = _db.Galleries.AsQueryable();
        var currentUserId = EngagementQueryHelpers.CurrentUserId(_db) ?? -1;
        if (filter != null)
        {
            if (!string.IsNullOrEmpty(filter.Title)) query = query.Where(g => g.Title != null && EF.Functions.ILike(g.Title, $"%{filter.Title}%"));
            if (filter.Organized.HasValue) query = query.Where(g => g.Organized == filter.Organized.Value);
            if (filter.StudioId.HasValue) query = query.Where(g => g.StudioId == filter.StudioId.Value);
            if (filter.ImageId.HasValue) query = query.Where(g => g.ImageGalleries.Any(ig => ig.ImageId == filter.ImageId.Value));
            if (filter.TagIds?.Count > 0) query = query.Where(g => g.TagIds.Any(id => filter.TagIds.Contains(id)));
            if (filter.PerformerIds?.Count > 0) query = query.Where(g => g.PerformerIds.Any(id => filter.PerformerIds.Contains(id)));

            // Advanced criteria
            query = EngagementQueryHelpers.ApplyRatingCriterion(_db, query, EngagementQueryHelpers.CurrentUserId(_db), RatingHostType.Gallery, filter.RatingCriterion);
            query = FilterHelpers.ApplyInt(query, filter.ImageCountCriterion, g => g.ImageCount);
            query = FilterHelpers.ApplyInt(query, filter.LikeCounterCriterion, gallery =>
                (gallery.ImageGalleries.Select(link => _db.UserEntityAffinities
                    .Where(affinity => affinity.UserId == currentUserId
                        && affinity.HostType == AffinityHostType.Image
                        && affinity.HostId == link.ImageId)
                    .Sum(affinity => (int?)affinity.LikeCount) ?? 0).Sum())
                + (gallery.VideoGalleries.Select(link => _db.UserEntityAffinities
                    .Where(affinity => affinity.UserId == currentUserId
                        && affinity.HostType == AffinityHostType.Video
                        && affinity.HostId == link.VideoId)
                    .Sum(affinity => (int?)affinity.LikeCount) ?? 0).Sum()));
            query = FilterHelpers.ApplyNullableTimestamp(query, filter.LastLikedAtCriterion, gallery =>
                gallery.ImageGalleries.Select(link => _db.Interactions
                    .Where(interaction => interaction.UserId == currentUserId
                        && interaction.HostType == InteractionHostType.Image
                        && interaction.HostId == link.ImageId
                        && interaction.Kind == InteractionKind.LikeCount)
                    .Max(interaction => (DateTime?)interaction.At))
                    .Concat(gallery.VideoGalleries.Select(link => _db.Interactions
                        .Where(interaction => interaction.UserId == currentUserId
                            && interaction.HostType == InteractionHostType.Video
                            && interaction.HostId == link.VideoId
                            && interaction.Kind == InteractionKind.LikeCount)
                        .Max(interaction => (DateTime?)interaction.At)))
                    .Max());

            if (filter.OrganizedCriterion != null)
                query = query.Where(g => g.Organized == filter.OrganizedCriterion.Value);

            if (filter.PerformerFavoriteCriterion != null)
                query = filter.PerformerFavoriteCriterion.Value
                    ? query.Where(g => g.GalleryPerformers.Any(gp => gp.Performer!.Favorite))
                    : query.Where(g => !g.GalleryPerformers.Any(gp => gp.Performer!.Favorite));

            // Multi-ID criteria
            query = FilterHelpers.ApplyMultiId(query, filter.TagsCriterion, g => g.TagIds, expandedTags?.ValueGroups, expandedTags?.RequiredIdGroups);
            query = FilterHelpers.ApplyMultiId(query, filter.PerformersCriterion, g => g.PerformerIds);

            query = FilterHelpers.ApplyStudioCriterion(query, filter.StudiosCriterion, g => g.StudioId, expandedStudios?.ValueGroups, expandedStudios?.RequiredIdGroups);

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
            query = FilterHelpers.ApplyInt(query, filter.TagCountCriterion, g => g.TagCount);
            query = FilterHelpers.ApplyInt(query, filter.PerformerCountCriterion, g => g.PerformerCount);

            // Videos criterion
            query = FilterHelpers.ApplyMultiId(query, filter.VideosCriterion, g => g.VideoGalleries.Select(sg => sg.VideoId));

            // Performer tags criterion
            if (filter.PerformerTagsCriterion != null)
            {
                var ptCriterion = filter.PerformerTagsCriterion;
                var ptIds = ptCriterion.Value.Where(id => id > 0).Distinct().ToArray();
                var ptExcludes = ptCriterion.Excludes?.Where(id => id > 0).Distinct().ToArray() ?? [];

                if (ptCriterion.Modifier == CriterionModifier.IsNull)
                    query = query.Where(g => !g.GalleryPerformers.Any(gp => gp.Performer!.PerformerTags.Any()));
                else if (ptCriterion.Modifier == CriterionModifier.NotNull)
                    query = query.Where(g => g.GalleryPerformers.Any(gp => gp.Performer!.PerformerTags.Any()));
                else
                {
                    if (ptIds.Length > 0)
                    {
                        query = ptCriterion.Modifier switch
                        {
                            CriterionModifier.Excludes => query.Where(g => !g.GalleryPerformers.Any(gp => gp.Performer!.PerformerTags.Any(pt => ptIds.Contains(pt.TagId)))),
                            CriterionModifier.IncludesAll => query.Where(g => ptIds.All(tid => g.GalleryPerformers.Any(gp => gp.Performer!.PerformerTags.Any(pt => pt.TagId == tid)))),
                            _ => query.Where(g => g.GalleryPerformers.Any(gp => gp.Performer!.PerformerTags.Any(pt => ptIds.Contains(pt.TagId)))),
                        };
                    }

                    // Excludes arrive as a separate list alongside an Includes modifier (see the filter UI's
                    // MultiIdEditor); apply them independently so an exclude-only filter still works.
                    if (ptExcludes.Length > 0)
                        query = query.Where(g => !g.GalleryPerformers.Any(gp => gp.Performer!.PerformerTags.Any(pt => ptExcludes.Contains(pt.TagId))));
                }
            }

            query = query.ApplyCustomFieldCriteria(_db, CustomFieldEntityTypes.Gallery, filter.CustomFieldCriterion, filter.CustomFieldCriteria);
        }
        var galleryBase = query;
        var galleryText = FullTextSearchHelpers.Apply(_db, galleryBase, findFilter?.Q,
            g => g.Title,
            g => g.Code,
            g => g.Details,
            g => g.Photographer,
            g => g.SearchText);
        query = FullTextSearchHelpers.ApplyRelationalMatches(galleryText, galleryBase, findFilter?.Q,
            tagSelectors: [g => g.GalleryTags.Where(gt => gt.Tag != null).Select(gt => gt.Tag!)],
            performerSelectors: [g => g.GalleryPerformers.Where(gp => gp.Performer != null).Select(gp => gp.Performer!)]);
        // Path search: match the gallery's own folder path as well as its (zip) file paths, since
        // folder-based galleries have no GalleryFile rows. Paths are stored forward-slash normalized.
        var galleryPathTerm = findFilter?.Q?.Trim();
        if (!string.IsNullOrWhiteSpace(galleryPathTerm))
        {
            var pathTerm = galleryPathTerm.ToLowerInvariant().Replace('\\', '/');
            query = query.Concat(galleryBase.Where(g =>
                g.Files.Any(f => f.Path.ToLower().Contains(pathTerm)) ||
                (g.Folder != null && g.Folder.Path.ToLower().Contains(pathTerm)))).Distinct();
        }

        var totalCount = await query.CountAsync(ct);
        var multiSortRegistry = CreateGalleryMultiSortRegistry(currentUserId);
        var sortClauses = multiSortRegistry.Normalize(findFilter?.Sorts);
        var primarySort = sortClauses.FirstOrDefault();
        var hasExplicitSort = sortClauses.Count > 0 || !string.IsNullOrWhiteSpace(findFilter?.Sort);
        var sort = primarySort?.Key ?? findFilter?.Sort ?? "updated_at";
        var desc = primarySort?.Direction == Core.Enums.SortDirection.Desc
            || (primarySort is null && findFilter?.Direction == Core.Enums.SortDirection.Desc);
        query = sortClauses.Count > 1
            ? ApplyGalleryMultiSort(query, sortClauses, multiSortRegistry)
            : FilterHelpers.TryParseCustomFieldSort(sort, out _, out _)
            ? query.ApplyCustomFieldSort(_db, CustomFieldEntityTypes.Gallery, sort, desc)
            : sort switch
            {
            "updated_at" => desc ? query.OrderByDescending(g => g.UpdatedAt) : query.OrderBy(g => g.UpdatedAt),
            "date" => desc ? query.OrderByDescending(g => g.Date ?? DateOnly.MinValue) : query.OrderBy(g => g.Date ?? DateOnly.MinValue),
            "studio" => ApplyGalleryStudioSort(query, desc),
            "file_mod_time" => ApplyGalleryFileModTimeSort(query, desc),
            "file_count" => desc ? query.OrderByDescending(g => g.Files.Count) : query.OrderBy(g => g.Files.Count),
            "path" => ApplyGalleryPathSort(query, desc),
            "title" => desc ? query.OrderByDescending(g => g.Title) : query.OrderBy(g => g.Title),
            "code" => desc ? query.OrderByDescending(g => g.Code) : query.OrderBy(g => g.Code),
            "photographer" => desc ? query.OrderByDescending(g => g.Photographer) : query.OrderBy(g => g.Photographer),
            "organized" => desc ? query.OrderByDescending(g => g.Organized).ThenByDescending(g => g.Id) : query.OrderBy(g => g.Organized).ThenBy(g => g.Id),
            "image_count" => desc ? query.OrderByDescending(g => g.ImageCount) : query.OrderBy(g => g.ImageCount),
            "video_count" => desc ? query.OrderByDescending(g => g.VideoCount) : query.OrderBy(g => g.VideoCount),
            "rating" => ApplyGalleryRatingSort(query, desc),
            "like_counter" => desc
                ? query.OrderByDescending(gallery =>
                    gallery.ImageGalleries.Select(link => _db.UserEntityAffinities.Where(affinity => affinity.UserId == currentUserId && affinity.HostType == AffinityHostType.Image && affinity.HostId == link.ImageId).Sum(affinity => (int?)affinity.LikeCount) ?? 0).Sum()
                    + gallery.VideoGalleries.Select(link => _db.UserEntityAffinities.Where(affinity => affinity.UserId == currentUserId && affinity.HostType == AffinityHostType.Video && affinity.HostId == link.VideoId).Sum(affinity => (int?)affinity.LikeCount) ?? 0).Sum()).ThenByDescending(gallery => gallery.Id)
                : query.OrderBy(gallery =>
                    gallery.ImageGalleries.Select(link => _db.UserEntityAffinities.Where(affinity => affinity.UserId == currentUserId && affinity.HostType == AffinityHostType.Image && affinity.HostId == link.ImageId).Sum(affinity => (int?)affinity.LikeCount) ?? 0).Sum()
                    + gallery.VideoGalleries.Select(link => _db.UserEntityAffinities.Where(affinity => affinity.UserId == currentUserId && affinity.HostType == AffinityHostType.Video && affinity.HostId == link.VideoId).Sum(affinity => (int?)affinity.LikeCount) ?? 0).Sum()).ThenBy(gallery => gallery.Id),
            "last_like_at" => desc
                ? query.OrderByDescending(gallery => gallery.ImageGalleries.Select(link => _db.Interactions.Where(interaction => interaction.UserId == currentUserId && interaction.HostType == InteractionHostType.Image && interaction.HostId == link.ImageId && interaction.Kind == InteractionKind.LikeCount).Max(interaction => (DateTime?)interaction.At)).Concat(gallery.VideoGalleries.Select(link => _db.Interactions.Where(interaction => interaction.UserId == currentUserId && interaction.HostType == InteractionHostType.Video && interaction.HostId == link.VideoId && interaction.Kind == InteractionKind.LikeCount).Max(interaction => (DateTime?)interaction.At))).Max()).ThenByDescending(gallery => gallery.Id)
                : query.OrderBy(gallery => gallery.ImageGalleries.Select(link => _db.Interactions.Where(interaction => interaction.UserId == currentUserId && interaction.HostType == InteractionHostType.Image && interaction.HostId == link.ImageId && interaction.Kind == InteractionKind.LikeCount).Max(interaction => (DateTime?)interaction.At)).Concat(gallery.VideoGalleries.Select(link => _db.Interactions.Where(interaction => interaction.UserId == currentUserId && interaction.HostType == InteractionHostType.Video && interaction.HostId == link.VideoId && interaction.Kind == InteractionKind.LikeCount).Max(interaction => (DateTime?)interaction.At))).Max()).ThenBy(gallery => gallery.Id),
            "performer_count" => desc ? query.OrderByDescending(g => g.PerformerCount) : query.OrderBy(g => g.PerformerCount),
            "tag_count" => desc ? query.OrderByDescending(g => g.TagCount) : query.OrderBy(g => g.TagCount),
            "typical_resolution" => ApplyGalleryTypicalResolutionSort(query, desc),
            "zip_file_count" => desc
                ? query.OrderByDescending(g => g.Files.Count(file => file.Basename.EndsWith(".zip")))
                : query.OrderBy(g => g.Files.Count(file => file.Basename.EndsWith(".zip"))),
            "created_at" => desc ? query.OrderByDescending(g => g.CreatedAt) : query.OrderBy(g => g.CreatedAt),
            "random" => SeededRandomOrdering.OrderBy(query, findFilter?.Seed, g => g.Id, desc),
            _ => desc ? query.OrderByDescending(g => g.UpdatedAt) : query.OrderBy(g => g.UpdatedAt),
            };
        if (!hasExplicitSort)
            query = FullTextSearchHelpers.OrderByRelevance(_db, query, findFilter?.Q);
        var page = findFilter?.Page ?? 1;
        var perPage = findFilter?.PerPage ?? 25;

        if (perPage <= 0)
        {
            return (Array.Empty<Gallery>(), totalCount);
        }

        var pagedIds = await query
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .Select(g => g.Id)
            .ToListAsync(ct);

        if (pagedIds.Count == 0)
        {
            return (Array.Empty<Gallery>(), totalCount);
        }

        var items = await _db.Galleries
            .Include(g => g.Studio)
            .Include(g => g.GalleryTags).ThenInclude(gt => gt.Tag).ThenInclude(tag => tag!.TagGroup)
            .Include(g => g.GalleryPerformers).ThenInclude(gp => gp.Performer)
            .AsSplitQuery()
            .Where(g => pagedIds.Contains(g.Id))
            .AsNoTracking()
            .ToListAsync(ct);

        var orderMap = pagedIds.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        var sortedItems = items.OrderBy(g => orderMap.GetValueOrDefault(g.Id, int.MaxValue)).ToList();
        return (sortedItems, totalCount);
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

    internal CompoundSortRegistry<Gallery> CreateGalleryMultiSortRegistry(int currentUserId)
        => new(new Dictionary<string, Action<CompoundSortQuery<Gallery>, bool>>(StringComparer.OrdinalIgnoreCase)
        {
            ["updated_at"] = (compound, desc) => compound.Append(gallery => gallery.UpdatedAt, desc),
            ["rating"] = (compound, desc) => compound.AppendRating(desc),
            ["created_at"] = (compound, desc) => compound.Append(gallery => gallery.CreatedAt, desc),
            ["date"] = (compound, desc) => compound.Append(gallery => gallery.Date, desc),
            ["studio"] = (compound, desc) =>
            {
                compound.Append(gallery => gallery.Studio == null ? 1 : 0, false);
                compound.Append(gallery => gallery.Studio != null ? gallery.Studio.Name : null, desc);
            },
            ["file_mod_time"] = (compound, desc) => compound.Append(gallery => gallery.Files.Select(file => (DateTime?)file.ModTime).Max() ?? (gallery.Folder != null ? (DateTime?)gallery.Folder.ModTime : null), desc),
            ["file_count"] = (compound, desc) => compound.Append(gallery => gallery.Files.Count, desc),
            ["path"] = (compound, desc) => compound.Append(gallery => gallery.Folder != null ? gallery.Folder.Path : gallery.Files.Select(file => file.Path).OrderBy(path => path).FirstOrDefault(), desc),
            ["title"] = (compound, desc) => compound.Append(gallery => gallery.Title, desc),
            ["code"] = (compound, desc) => compound.Append(gallery => gallery.Code, desc),
            ["photographer"] = (compound, desc) => compound.Append(gallery => gallery.Photographer, desc),
            ["organized"] = (compound, desc) => compound.Append(gallery => gallery.Organized, desc),
            ["image_count"] = (compound, desc) => compound.Append(gallery => gallery.ImageCount, desc),
            ["video_count"] = (compound, desc) => compound.Append(gallery => gallery.VideoCount, desc),
            ["performer_count"] = (compound, desc) => compound.Append(gallery => gallery.PerformerCount, desc),
            ["tag_count"] = (compound, desc) => compound.Append(gallery => gallery.TagCount, desc),
            ["like_counter"] = (compound, desc) => compound.Append(gallery =>
                gallery.ImageGalleries.Select(link => _db.UserEntityAffinities
                    .Where(affinity => affinity.UserId == currentUserId && affinity.HostType == AffinityHostType.Image && affinity.HostId == link.ImageId)
                    .Sum(affinity => (int?)affinity.LikeCount) ?? 0).Sum()
                + gallery.VideoGalleries.Select(link => _db.UserEntityAffinities
                    .Where(affinity => affinity.UserId == currentUserId && affinity.HostType == AffinityHostType.Video && affinity.HostId == link.VideoId)
                    .Sum(affinity => (int?)affinity.LikeCount) ?? 0).Sum(), desc),
            ["last_like_at"] = (compound, desc) => compound.Append(gallery =>
                gallery.ImageGalleries.Select(link => _db.Interactions
                    .Where(interaction => interaction.UserId == currentUserId
                        && interaction.HostType == InteractionHostType.Image
                        && interaction.HostId == link.ImageId
                        && interaction.Kind == InteractionKind.LikeCount)
                    .Max(interaction => (DateTime?)interaction.At))
                    .Concat(gallery.VideoGalleries.Select(link => _db.Interactions
                        .Where(interaction => interaction.UserId == currentUserId
                            && interaction.HostType == InteractionHostType.Video
                            && interaction.HostId == link.VideoId
                            && interaction.Kind == InteractionKind.LikeCount)
                        .Max(interaction => (DateTime?)interaction.At)))
                    .Max(), desc),
        });

    private IQueryable<Gallery> ApplyGalleryMultiSort(
        IQueryable<Gallery> query,
        IReadOnlyList<SortClause> clauses,
        CompoundSortRegistry<Gallery> registry)
    {
        var userId = EngagementQueryHelpers.CurrentUserId(_db);
        var compound = CompoundSortQuery<Gallery>.Create(
            _db, query, userId, null, RatingHostType.Gallery,
            includeAffinity: false,
            includeRating: clauses.Any(clause => clause.Key.Equals("rating", StringComparison.OrdinalIgnoreCase)));
        registry.Apply(compound, clauses);

        return compound.Finish(gallery => gallery.Id);
    }

    private static IQueryable<Gallery> ApplyGalleryStudioSort(IQueryable<Gallery> query, bool desc)
    {
        var sortQuery = query.Select(gallery => new
        {
            Gallery = gallery,
            StudioName = gallery.Studio != null ? gallery.Studio.Name : null,
        });

        return desc
            ? sortQuery.OrderBy(item => item.StudioName == null ? 1 : 0).ThenByDescending(item => item.StudioName).Select(item => item.Gallery)
            : sortQuery.OrderBy(item => item.StudioName == null ? 1 : 0).ThenBy(item => item.StudioName).Select(item => item.Gallery);
    }

    private static IQueryable<Gallery> ApplyGalleryPathSort(IQueryable<Gallery> query, bool desc)
    {
        // Both folders.Path and files.Path are stored in forward-slash form (normalized
        // by CoveContext.SaveChanges). files.Path is btree-indexed.
        if (desc)
        {
            var descendingQuery = query.Select(gallery => new
            {
                Gallery = gallery,
                Path = gallery.Folder != null
                    ? gallery.Folder.Path
                    : gallery.Files.Select(file => file.Path).OrderByDescending(path => path).FirstOrDefault(),
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
                ? gallery.Folder.Path
                : gallery.Files.Select(file => file.Path).OrderBy(path => path).FirstOrDefault(),
        });

        return ascendingQuery
            .OrderBy(item => item.Path == null ? 1 : 0)
            .ThenBy(item => item.Path)
            .Select(item => item.Gallery);
    }

    private IQueryable<Gallery> ApplyGalleryRatingSort(IQueryable<Gallery> query, bool desc)
        => EngagementQueryHelpers.ApplyRatingSort(_db, query, EngagementQueryHelpers.CurrentUserId(_db), RatingHostType.Gallery, desc);

    private static IQueryable<Gallery> ApplyGalleryTypicalResolutionSort(IQueryable<Gallery> query, bool desc)
    {
        var sortQuery = query.Select(gallery => new
        {
            Gallery = gallery,
            TypicalResolution = gallery.ImageGalleries
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
                .Select(group => (int?)group.Key)
                .FirstOrDefault(),
        });

        return desc
            ? sortQuery.OrderBy(item => item.TypicalResolution == null ? 1 : 0).ThenByDescending(item => item.TypicalResolution).Select(item => item.Gallery)
            : sortQuery.OrderBy(item => item.TypicalResolution == null ? 1 : 0).ThenBy(item => item.TypicalResolution).Select(item => item.Gallery);
    }

    private static IQueryable<Gallery> ApplyGalleryPathCriterion(IQueryable<Gallery> query, StringCriterion? criterion)
    {
        if (criterion == null) return query;

        var value = criterion.Value.Replace("\\", "/");
        var normalizedValue = value.ToLowerInvariant();

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(gallery =>
                (gallery.Folder != null && gallery.Folder.Path == value)
                || gallery.Files.Any(file => file.Path == value)),
            CriterionModifier.NotEquals => query.Where(gallery =>
                (gallery.Folder == null || gallery.Folder.Path != value)
                && !gallery.Files.Any(file => file.Path == value)),
            CriterionModifier.Includes => query.Where(gallery =>
                (gallery.Folder != null && gallery.Folder.Path.ToLower().Contains(normalizedValue))
                || gallery.Files.Any(file => file.Path.ToLower().Contains(normalizedValue))),
            CriterionModifier.Excludes => query.Where(gallery =>
                (gallery.Folder == null || !gallery.Folder.Path.ToLower().Contains(normalizedValue))
                && !gallery.Files.Any(file => file.Path.ToLower().Contains(normalizedValue))),
            CriterionModifier.MatchesRegex => query.Where(gallery =>
                (gallery.Folder != null && Regex.IsMatch(gallery.Folder.Path, value, RegexOptions.IgnoreCase))
                || gallery.Files.Any(file => Regex.IsMatch(file.Path, value, RegexOptions.IgnoreCase))),
            CriterionModifier.NotMatchesRegex => query.Where(gallery =>
                (gallery.Folder == null || !Regex.IsMatch(gallery.Folder.Path, value, RegexOptions.IgnoreCase))
                && !gallery.Files.Any(file => Regex.IsMatch(file.Path, value, RegexOptions.IgnoreCase))),
            CriterionModifier.IsNull => query.Where(gallery =>
                (gallery.Folder == null || gallery.Folder.Path == "")
                && !gallery.Files.Any(file => file.Path != "")),
            CriterionModifier.NotNull => query.Where(gallery =>
                (gallery.Folder != null && gallery.Folder.Path != "")
                || gallery.Files.Any(file => file.Path != "")),
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
            .Include(i => i.ImageTags).ThenInclude(it => it.Tag).ThenInclude(tag => tag!.TagGroup)
            .Include(i => i.ImagePerformers).ThenInclude(ip => ip.Performer)
            .Include(i => i.ImageGalleries).ThenInclude(ig => ig.Gallery)
            .Include(i => i.Files).ThenInclude(f => f.ParentFolder)
            .AsSplitQuery()
            .FirstOrDefaultAsync(i => i.Id == id, ct);

    public async Task<IReadOnlyList<ImagePerformer>> GetImagePerformersAsync(IReadOnlyList<int> imageIds, CancellationToken ct = default)
        => await _db.Set<ImagePerformer>()
            .AsNoTracking()
            .Include(static ip => ip.Performer)
                .ThenInclude(static p => p!.RemoteIds)
            .Where(ip => imageIds.Contains(ip.ImageId) && ip.Performer != null)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<int>> GetTagIdsAsync(int imageId, CancellationToken ct = default)
        => await _db.Set<ImageTag>()
            .AsNoTracking()
            .Where(it => it.ImageId == imageId)
            .Select(it => it.TagId)
            .ToListAsync(ct);

    public void AddTagLink(int imageId, int tagId)
        => _db.Set<ImageTag>().Add(new ImageTag { ImageId = imageId, TagId = tagId });

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
        ExpandedHierarchyCriterion? expandedTags = null;
        if (HierarchicalCriterionExpander.RequiresExpansion(filter?.TagsCriterion))
        {
            expandedTags = await HierarchicalCriterionExpander.ExpandTagsAsync(_db, filter!.TagsCriterion!, ct);
            filter.TagsCriterion = expandedTags.Criterion;
        }
        ExpandedHierarchyCriterion? expandedStudios = null;
        if (HierarchicalCriterionExpander.RequiresExpansion(filter?.StudiosCriterion))
        {
            expandedStudios = await HierarchicalCriterionExpander.ExpandStudiosAsync(_db, filter!.StudiosCriterion!, ct);
            filter.StudiosCriterion = expandedStudios.Criterion;
        }

        var currentPrincipal = _db.CurrentPrincipalForReadOptimization;
        var readScopePlan = await ReadScopeListOptimization.TryBuildPlanAsync<Image>(
            _db,
            EntityKinds.Image,
            currentPrincipal?.Has(PermissionKeys.ImagesRead) == true,
            currentPrincipal?.ReadGrantedEntityKinds.Contains(EntityKinds.Image) == true,
            ct);

        // Build filter query once (lightweight, no includes)
        var filterQuery = (readScopePlan ?? new ReadScopeRootPlan<Image>(false, null)).Apply(_db.Images.AsQueryable());
        filterQuery = ApplyImageFilters(filterQuery, filter, expandedTags?.ValueGroups, expandedTags?.RequiredIdGroups, expandedStudios?.ValueGroups, expandedStudios?.RequiredIdGroups);
        var imageBase = filterQuery;
        var imageText = FullTextSearchHelpers.Apply(_db, imageBase, findFilter?.Q,
            i => i.Title,
            i => i.Details,
            i => i.Code,
            i => i.Photographer,
            i => i.FileSearchText,
            i => i.SearchText);
        filterQuery = FullTextSearchHelpers.ApplyRelationalMatches(imageText, imageBase, findFilter?.Q,
            tagSelectors: [i => i.ImageTags.Where(it => it.Tag != null).Select(it => it.Tag!)],
            performerSelectors: [i => i.ImagePerformers.Where(ip => ip.Performer != null).Select(ip => ip.Performer!)]);
        filterQuery = FullTextSearchHelpers.ApplyFilePathMatch(filterQuery, imageBase, findFilter?.Q, i => i.Files);

        var perPage = findFilter?.PerPage ?? 25;

        if (perPage <= 0)
        {
            var count = await filterQuery.CountAsync(ct);
            return (Array.Empty<Image>(), count);
        }

        var totalCount = await filterQuery.AsNoTracking().CountAsync(ct);

        // Sort and paginate on the lightweight query, then fetch only the IDs
        var multiSortRegistry = CreateImageMultiSortRegistry();
        var sortClauses = multiSortRegistry.Normalize(findFilter?.Sorts);
        var primarySort = sortClauses.FirstOrDefault();
        var hasExplicitSort = sortClauses.Count > 0 || !string.IsNullOrWhiteSpace(findFilter?.Sort);
        var sort = primarySort?.Key ?? findFilter?.Sort ?? "updated_at";
        var desc = primarySort?.Direction == Core.Enums.SortDirection.Desc
            || (primarySort is null && findFilter?.Direction == Core.Enums.SortDirection.Desc);
        filterQuery = sortClauses.Count > 1
            ? ApplyImageMultiSort(filterQuery, sortClauses, multiSortRegistry)
            : ApplySorting(filterQuery, sort, desc, findFilter?.Seed);
        if (!hasExplicitSort)
            filterQuery = FullTextSearchHelpers.OrderByRelevance(_db, filterQuery, findFilter?.Q);

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
            .Include(i => i.Urls)
            .Include(i => i.ImageTags).ThenInclude(it => it.Tag).ThenInclude(tag => tag!.TagGroup)
            .Include(i => i.ImagePerformers).ThenInclude(ip => ip.Performer)
            .Include(i => i.ImageGalleries).ThenInclude(ig => ig.Gallery)
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

    public async Task<ImageAggregate> AggregateAsync(ImageFilter? filter, FindFilter? findFilter, CancellationToken ct = default)
    {
        ExpandedHierarchyCriterion? expandedTags = null;
        if (HierarchicalCriterionExpander.RequiresExpansion(filter?.TagsCriterion))
        {
            expandedTags = await HierarchicalCriterionExpander.ExpandTagsAsync(_db, filter!.TagsCriterion!, ct);
            filter.TagsCriterion = expandedTags.Criterion;
        }
        ExpandedHierarchyCriterion? expandedStudios = null;
        if (HierarchicalCriterionExpander.RequiresExpansion(filter?.StudiosCriterion))
        {
            expandedStudios = await HierarchicalCriterionExpander.ExpandStudiosAsync(_db, filter!.StudiosCriterion!, ct);
            filter.StudiosCriterion = expandedStudios.Criterion;
        }

        var currentPrincipal = _db.CurrentPrincipalForReadOptimization;
        var readScopePlan = await ReadScopeListOptimization.TryBuildPlanAsync<Image>(
            _db, EntityKinds.Image,
            currentPrincipal?.Has(PermissionKeys.ImagesRead) == true,
            currentPrincipal?.ReadGrantedEntityKinds.Contains(EntityKinds.Image) == true, ct);
        var query = (readScopePlan ?? new ReadScopeRootPlan<Image>(false, null)).Apply(_db.Images.AsQueryable());
        query = ApplyImageFilters(query, filter, expandedTags?.ValueGroups, expandedTags?.RequiredIdGroups, expandedStudios?.ValueGroups, expandedStudios?.RequiredIdGroups);
        var imageBase = query;
        var imageText = FullTextSearchHelpers.Apply(_db, imageBase, findFilter?.Q,
            image => image.Title, image => image.Details, image => image.Code,
            image => image.Photographer, image => image.FileSearchText, image => image.SearchText);
        query = FullTextSearchHelpers.ApplyRelationalMatches(imageText, imageBase, findFilter?.Q,
            tagSelectors: [image => image.ImageTags.Where(link => link.Tag != null).Select(link => link.Tag!)],
            performerSelectors: [image => image.ImagePerformers.Where(link => link.Performer != null).Select(link => link.Performer!)]);
        query = FullTextSearchHelpers.ApplyFilePathMatch(query, imageBase, findFilter?.Q, image => image.Files);

        return await query.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(group => new ImageAggregate(
                group.Count(),
                group.Sum(image => image.MaxFileSize)))
            .SingleOrDefaultAsync(ct)
            ?? new ImageAggregate(0, 0);
    }

    private IQueryable<Image> ApplyImageFilters(IQueryable<Image> query, ImageFilter? filter, IReadOnlyList<int[]>? hierarchicalTagGroups = null, IReadOnlyList<int[]>? requiredTagGroups = null, IReadOnlyList<int[]>? hierarchicalStudioGroups = null, IReadOnlyList<int[]>? requiredStudioGroups = null)
    {
        if (filter == null) return query;

        // Simple filters
        if (filter.Ids?.Count > 0)
            query = query.Where(i => filter.Ids.Contains(i.Id));
        if (!string.IsNullOrEmpty(filter.Title))
            query = FilterHelpers.ApplyString(query, new StringCriterion { Value = filter.Title, Modifier = CriterionModifier.Includes }, DisplayTitleSelector);
        if (filter.Organized.HasValue) query = query.Where(i => i.Organized == filter.Organized.Value);
        if (filter.StudioId.HasValue) query = query.Where(i => i.StudioId == filter.StudioId.Value);
        if (filter.GalleryId.HasValue) query = query.Where(i => i.ImageGalleries.Any(ig => ig.GalleryId == filter.GalleryId.Value));
        if (filter.TagIds?.Count > 0) query = query.Where(i => i.TagIds.Any(id => filter.TagIds.Contains(id)));
        if (filter.PerformerIds?.Count > 0) query = query.Where(i => i.PerformerIds.Any(id => filter.PerformerIds.Contains(id)));

        // Advanced criteria
        if (filter.RatingCriterion != null)
            query = EngagementQueryHelpers.ApplyRatingCriterion(_db, query, EngagementQueryHelpers.CurrentUserId(_db), RatingHostType.Image, filter.RatingCriterion);
        if (filter.LikeCounterCriterion != null)
            query = EngagementQueryHelpers.ApplyAffinityIntCriterion(_db, query, EngagementQueryHelpers.CurrentUserId(_db), AffinityHostType.Image, nameof(UserEntityAffinity.LikeCount), filter.LikeCounterCriterion);
        if (filter.OrganizedCriterion != null)
            query = query.Where(i => i.Organized == filter.OrganizedCriterion.Value);
        if (filter.ResolutionCriterion != null)
            query = FilterHelpers.ApplyResolution(query, filter.ResolutionCriterion, i => i.MaxResolution);

        // Multi-ID criteria
        if (filter.TagsCriterion != null)
            query = FilterHelpers.ApplyMultiId(query, filter.TagsCriterion, i => i.TagIds, hierarchicalTagGroups, requiredTagGroups);
        if (filter.PerformersCriterion != null)
            query = FilterHelpers.ApplyMultiId(query, filter.PerformersCriterion, i => i.PerformerIds);

        query = FilterHelpers.ApplyStudioCriterion(query, filter.StudiosCriterion, i => i.StudioId, hierarchicalStudioGroups, requiredStudioGroups);

        if (filter.GalleriesCriterion != null)
            query = FilterHelpers.ApplyMultiId(query, filter.GalleriesCriterion, i => i.ImageGalleries.Select(ig => ig.GalleryId));

        query = ApplyPathCriterion(query, filter.PathCriterion);

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
        query = FilterHelpers.ApplyInt(query, filter.FileCountCriterion, i => i.FileCount);
        query = FilterHelpers.ApplyInt(query, filter.TagCountCriterion, i => i.TagCount);
        query = FilterHelpers.ApplyInt(query, filter.PerformerCountCriterion, i => i.ImagePerformers.Count);

        query = ApplyPerformerOccurrenceTagCriterion(query, filter.PerformerTagsCriterion, GetIncludedPerformerIds(filter));

        query = ApplyPerformerAgeCriterion(query, filter.PerformerAgeCriterion);

        if (filter.OrientationCriterion != null)
            query = ApplyOrientationCriterion(query, filter.OrientationCriterion);

        query = query.ApplyCustomFieldCriteria(_db, CustomFieldEntityTypes.Image, filter.CustomFieldCriterion, filter.CustomFieldCriteria);

        return query;
    }

    private static int[] GetIncludedPerformerIds(ImageFilter filter)
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

    private IQueryable<Image> ApplyPerformerOccurrenceTagCriterion(IQueryable<Image> query, MultiIdCriterion? criterion, IReadOnlyCollection<int> performerIds)
    {
        if (criterion == null)
            return query;

        var tagIds = criterion.Value.Where(tagId => tagId > 0).Distinct().ToArray();
        var excludedTagIds = criterion.Excludes?.Where(tagId => tagId > 0).Distinct().ToArray() ?? [];
        if (tagIds.Length == 0 && excludedTagIds.Length == 0)
            return query;

        var scopedApplications = _db.TagApplications.AsNoTracking()
            .Where(application => application.HostType == AffinityHostType.Image
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
                CriterionModifier.Excludes => query.Where(image => !scopedApplications.Any(application => application.HostId == image.Id && tagIds.Contains(application.TagId))),
                CriterionModifier.ExcludesAll => ApplyPerformerOccurrenceTagExcludesAll(query, scopedApplications, tagIds),
                CriterionModifier.IncludesAll => ApplyPerformerOccurrenceTagIncludesAll(query, scopedApplications, tagIds),
                _ => query.Where(image => scopedApplications.Any(application => application.HostId == image.Id && tagIds.Contains(application.TagId))),
            };
        }

        if (excludedTagIds.Length > 0)
        {
            query = query.Where(image => !scopedApplications.Any(application => application.HostId == image.Id && excludedTagIds.Contains(application.TagId)));
        }

        return query;
    }

    private static IQueryable<Image> ApplyPerformerOccurrenceTagIncludesAll(IQueryable<Image> query, IQueryable<TagApplication> applications, IReadOnlyCollection<int> tagIds)
    {
        foreach (var tagId in tagIds)
        {
            query = query.Where(image => applications.Any(application => application.HostId == image.Id && application.TagId == tagId));
        }

        return query;
    }

    private static IQueryable<Image> ApplyPerformerOccurrenceTagExcludesAll(IQueryable<Image> query, IQueryable<TagApplication> applications, IReadOnlyCollection<int> tagIds)
    {
        var matchingAll = query;
        foreach (var tagId in tagIds)
        {
            matchingAll = matchingAll.Where(image => applications.Any(application => application.HostId == image.Id && application.TagId == tagId));
        }

        return query.Where(image => !matchingAll.Select(match => match.Id).Contains(image.Id));
    }

    private IQueryable<Image> ApplySorting(IQueryable<Image> query, string sort, bool desc, int? seed = null)
    {
        if (sort == "random")
            return SeededRandomOrdering.OrderBy(query, seed, image => image.Id, desc);

        return ApplySortingSwitch(query, sort, desc);
    }

    private static CompoundSortRegistry<Image> CreateImageMultiSortRegistry()
        => new(new Dictionary<string, Action<CompoundSortQuery<Image>, bool>>(StringComparer.OrdinalIgnoreCase)
        {
            ["updated_at"] = (compound, desc) => compound.Append(image => image.UpdatedAt, desc),
            ["rating"] = (compound, desc) => compound.AppendRating(desc),
            ["like_counter"] = (compound, desc) => compound.AppendAffinityInt(nameof(UserEntityAffinity.LikeCount), desc),
            ["created_at"] = (compound, desc) => compound.Append(image => image.CreatedAt, desc),
            ["date"] = (compound, desc) => compound.Append(image => image.Date, desc),
            ["file_mod_time"] = (compound, desc) => compound.Append(image => image.MaxFileModTime, desc),
            ["file_size"] = (compound, desc) => compound.Append(image => image.MaxFileSize, desc),
            ["resolution"] = (compound, desc) => compound.Append(image => image.MaxResolution, desc),
            ["path"] = (compound, desc) => compound.Append(image => desc ? image.MaxPath : image.MinPath, desc),
            ["title"] = (compound, desc) => compound.Append(DisplayTitleSelector, desc),
            ["performer_count"] = (compound, desc) => compound.Append(image => image.ImagePerformers.Count, desc),
            ["tag_count"] = (compound, desc) => compound.Append(image => image.TagCount, desc),
        });

    private IQueryable<Image> ApplyImageMultiSort(IQueryable<Image> query, IReadOnlyList<SortClause> clauses, CompoundSortRegistry<Image> registry)
    {
        var userId = EngagementQueryHelpers.CurrentUserId(_db);
        var compound = CompoundSortQuery<Image>.Create(
            _db, query, userId, AffinityHostType.Image, RatingHostType.Image,
            includeAffinity: clauses.Any(clause => clause.Key.Equals("like_counter", StringComparison.OrdinalIgnoreCase)),
            includeRating: clauses.Any(clause => clause.Key.Equals("rating", StringComparison.OrdinalIgnoreCase)));
        registry.Apply(compound, clauses);

        return compound.Finish(image => image.Id);
    }

    private IQueryable<Image> ApplySortingSwitch(IQueryable<Image> query, string sort, bool desc)
    {
        if (FilterHelpers.TryParseCustomFieldSort(sort, out _, out _))
            return query.ApplyCustomFieldSort(_db, CustomFieldEntityTypes.Image, sort, desc);

        return sort switch
        {
            "title" => ApplyDisplayTitleSort(query, desc),
            "date" => desc ? query.OrderByDescending(i => i.Date ?? DateOnly.MinValue) : query.OrderBy(i => i.Date ?? DateOnly.MinValue),
            "rating" => EngagementQueryHelpers.ApplyRatingSort(_db, query, EngagementQueryHelpers.CurrentUserId(_db), RatingHostType.Image, desc),
            "like_counter" => EngagementQueryHelpers.ApplyAffinityIntSort(_db, query, EngagementQueryHelpers.CurrentUserId(_db), AffinityHostType.Image, nameof(UserEntityAffinity.LikeCount), desc),
            "random" => query.OrderBy(i => i.Id),
            "file_mod_time" => ApplyFileModTimeSort(query, desc),
            "file_size" => desc ? query.OrderByDescending(i => i.MaxFileSize) : query.OrderBy(i => i.MaxFileSize),
            "resolution" => desc ? query.OrderByDescending(i => i.MaxResolution) : query.OrderBy(i => i.MaxResolution),
            "path" => ApplyPathSort(query, desc),
            "tag_count" => desc ? query.OrderByDescending(i => i.TagCount) : query.OrderBy(i => i.TagCount),
            "performer_count" => desc ? query.OrderByDescending(i => i.ImagePerformers.Count) : query.OrderBy(i => i.ImagePerformers.Count),
            "created_at" => desc ? query.OrderByDescending(i => i.CreatedAt) : query.OrderBy(i => i.CreatedAt),
            _ => desc ? query.OrderByDescending(i => i.UpdatedAt) : query.OrderBy(i => i.UpdatedAt),
        };
    }

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
        return desc
            ? query.OrderBy(image => image.MaxFileModTime == null ? 1 : 0).ThenByDescending(image => image.MaxFileModTime)
            : query.OrderBy(image => image.MaxFileModTime == null ? 1 : 0).ThenBy(image => image.MaxFileModTime);
    }

    private static IQueryable<Image> ApplyPathSort(IQueryable<Image> query, bool desc)
    {
        return desc
            ? query.OrderBy(image => image.MaxPath == null ? 1 : 0).ThenByDescending(image => image.MaxPath).ThenByDescending(image => image.Id)
            : query.OrderBy(image => image.MinPath).ThenBy(image => image.Id);
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
            "landscape" => query.Where(i => i.HasLandscapeFiles || i.Files.Any(file => file.Width > file.Height)),
            "portrait" => query.Where(i => i.HasPortraitFiles || i.Files.Any(file => file.Height > file.Width)),
            "square" => query.Where(i => i.HasSquareFiles || i.Files.Any(file => file.Width > 0 && file.Width == file.Height)),
            _ => query,
        };

        if (ReferenceEquals(matchingQuery, query))
            return query;

        return criterion.Modifier switch
        {
            CriterionModifier.NotEquals => query.Where(i => !matchingQuery.Select(item => item.Id).Contains(i.Id)),
            CriterionModifier.IsNull => query.Where(i => !i.HasDimensionData && !i.Files.Any(file => file.Width > 0 && file.Height > 0)),
            CriterionModifier.NotNull => query.Where(i => i.HasDimensionData || i.Files.Any(file => file.Width > 0 && file.Height > 0)),
            _ => matchingQuery,
        };
    }

    private static IQueryable<Image> ApplyPathCriterion(IQueryable<Image> query, StringCriterion? criterion)
    {
        if (criterion == null) return query;

        var value = criterion.Value.Replace("\\", "/");
        var pattern = $"%{value}%";
        var exactPattern = $"%\n{value}\n%";

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(i => i.FileSearchText != null && EF.Functions.Like(i.FileSearchText, exactPattern)),
            CriterionModifier.NotEquals => query.Where(i => i.FileSearchText == null || !EF.Functions.Like(i.FileSearchText, exactPattern)),
            CriterionModifier.Includes => query.Where(i => i.FileSearchText != null && EF.Functions.ILike(i.FileSearchText, pattern)),
            CriterionModifier.Excludes => query.Where(i => i.FileSearchText == null || !EF.Functions.ILike(i.FileSearchText, pattern)),
            CriterionModifier.MatchesRegex => query.Where(i => i.FileSearchText != null && Regex.IsMatch(i.FileSearchText, value, RegexOptions.IgnoreCase)),
            CriterionModifier.NotMatchesRegex => query.Where(i => i.FileSearchText == null || !Regex.IsMatch(i.FileSearchText, value, RegexOptions.IgnoreCase)),
            CriterionModifier.IsNull => query.Where(i => i.FileCount == 0 || i.FileSearchText == null || i.FileSearchText == ""),
            CriterionModifier.NotNull => query.Where(i => i.FileCount > 0 && i.FileSearchText != null && i.FileSearchText != ""),
            _ => query,
        };
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
            .Include(g => g.GroupTags).ThenInclude(gt => gt.Tag).ThenInclude(tag => tag!.TagGroup)
            .Include(g => g.GroupItems)
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
        ExpandedHierarchyCriterion? expandedTags = null;
        if (HierarchicalCriterionExpander.RequiresExpansion(filter?.TagsCriterion))
        {
            expandedTags = await HierarchicalCriterionExpander.ExpandTagsAsync(_db, filter!.TagsCriterion!, ct);
            filter.TagsCriterion = expandedTags.Criterion;
        }
        ExpandedHierarchyCriterion? expandedStudios = null;
        if (HierarchicalCriterionExpander.RequiresExpansion(filter?.StudiosCriterion))
        {
            expandedStudios = await HierarchicalCriterionExpander.ExpandStudiosAsync(_db, filter!.StudiosCriterion!, ct);
            filter.StudiosCriterion = expandedStudios.Criterion;
        }

        var query = _db.Groups.AsQueryable();
        if (filter != null)
        {
            if (!string.IsNullOrEmpty(filter.Name)) query = query.Where(g => EF.Functions.ILike(g.Name, $"%{filter.Name}%"));
            if (filter.StudioId.HasValue) query = query.Where(g => g.StudioId == filter.StudioId.Value);
            if (filter.TagIds?.Count > 0)
                query = query.Where(g => g.GroupTags.Any(gt => filter.TagIds.Contains(gt.TagId)));

            // Advanced criteria
            query = EngagementQueryHelpers.ApplyRatingCriterion(_db, query, EngagementQueryHelpers.CurrentUserId(_db), RatingHostType.Group, filter.RatingCriterion);
            query = FilterHelpers.ApplyInt(query, filter.DurationCriterion, g => g.Duration ?? 0);

            if (filter.KindCriterion != null)
            {
                var kind = ParseGroupKind(filter.KindCriterion.Value);
                if (kind.HasValue)
                {
                    query = filter.KindCriterion.Modifier switch
                    {
                        CriterionModifier.NotEquals or CriterionModifier.Excludes => query.Where(g => g.Kind != kind.Value),
                        _ => query.Where(g => g.Kind == kind.Value),
                    };
                }
            }

            // Multi-ID criteria
            query = FilterHelpers.ApplyMultiId(query, filter.TagsCriterion, g => g.GroupTags.Select(gt => gt.TagId), expandedTags?.ValueGroups, expandedTags?.RequiredIdGroups);

            query = FilterHelpers.ApplyStudioCriterion(query, filter.StudiosCriterion, g => g.StudioId, expandedStudios?.ValueGroups, expandedStudios?.RequiredIdGroups);

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
            query = FilterHelpers.ApplyString(query, filter.AliasesCriterion, g => g.Aliases);
            query = FilterHelpers.ApplyString(query, filter.DirectorCriterion, g => g.Director);
            query = FilterHelpers.ApplyString(query, filter.SynopsisCriterion, g => g.Synopsis);
            query = FilterHelpers.ApplyString(query, filter.QuerySourceKeyCriterion, g => g.QuerySourceKey);
            query = ApplyAllowedHostTypesCriterion(query, filter.AllowedHostTypesCriterion);
            query = FilterHelpers.ApplyBool(query, filter.HasQueryCriterion, g => g.QueryJson != null && g.QueryJson != string.Empty);
            if (filter.IsBuiltInCriterion != null)
            {
                string[] builtInKeys = ["save-for-later", "watch-history", "continue-watching"];
                query = filter.IsBuiltInCriterion.Value
                    ? query.Where(g => builtInKeys.Contains(g.QuerySourceKey))
                    : query.Where(g => !builtInKeys.Contains(g.QuerySourceKey));
            }
            query = FilterHelpers.ApplyBool(query, filter.ShowInVideoListsCriterion, g => g.ShowInVideoLists);
            query = FilterHelpers.ApplyNullableTimestamp(query, filter.LastResolvedAtCriterion, g => g.LastResolvedAt);
            query = FilterHelpers.ApplyInt(query, filter.SortOrderCriterion, g => g.SortOrder);
            query = FilterHelpers.ApplyInt(query, filter.CachedItemCountCriterion, g => g.CachedItemCount ?? 0);

            // Count criteria
            query = FilterHelpers.ApplyInt(query, filter.ItemCountCriterion, g => g.GroupItems.Count);
            query = FilterHelpers.ApplyInt(query, filter.VideoCountCriterion, g => g.GroupItems.Where(item => item.VideoId != null).Select(item => item.VideoId).Distinct().Count());
            query = FilterHelpers.ApplyInt(query, filter.ImageCountCriterion, g => g.GroupItems.Count(item => item.Kind == GroupItemKind.Image));
            query = FilterHelpers.ApplyInt(query, filter.AudioCountCriterion, g => g.GroupItems.Count(item => item.Kind == GroupItemKind.Audio));
            query = FilterHelpers.ApplyInt(query, filter.TextCountCriterion, g => g.GroupItems.Count(item => item.Kind == GroupItemKind.Text));
            query = FilterHelpers.ApplyInt(query, filter.GalleryCountCriterion, g => g.GroupItems.Count(item => item.Kind == GroupItemKind.Gallery));
            query = FilterHelpers.ApplyInt(query, filter.PerformerItemCountCriterion, g => g.GroupItems.Count(item => item.Kind == GroupItemKind.Performer));
            query = FilterHelpers.ApplyInt(query, filter.StudioItemCountCriterion, g => g.GroupItems.Count(item => item.Kind == GroupItemKind.Studio));
            query = FilterHelpers.ApplyInt(query, filter.TagItemCountCriterion, g => g.GroupItems.Count(item => item.Kind == GroupItemKind.Tag));
            query = FilterHelpers.ApplyInt(query, filter.FaceCountCriterion, g => g.GroupItems.Count(item => item.Kind == GroupItemKind.Face));
            query = FilterHelpers.ApplyInt(query, filter.SegmentCountCriterion, g => g.GroupItems.Count(item => item.Kind == GroupItemKind.Segment));
            query = FilterHelpers.ApplyInt(query, filter.SubGroupCountCriterion, g => g.SubGroupRelations.Count);
            query = FilterHelpers.ApplyInt(query, filter.ContainingGroupCountCriterion, g => g.ContainingGroupRelations.Count);
            query = FilterHelpers.ApplyInt(query, filter.TagCountCriterion, g => g.GroupTags.Count);

            // Performers criterion (direct performer items or performers in videos belonging to this group)
            query = FilterHelpers.ApplyMultiId(
                query,
                filter.PerformersCriterion,
                g => g.GroupItems
                    .Where(item => item.Video != null)
                    .SelectMany(item => item.Video!.VideoPerformers.Select(videoPerformer => videoPerformer.PerformerId))
                    .Concat(g.GroupItems
                        .Where(item => item.HostType == "performer" || item.Kind == GroupItemKind.Performer)
                        .Select(item => item.HostId)));

            query = query.ApplyCustomFieldCriteria(_db, CustomFieldEntityTypes.Group, filter.CustomFieldCriterion, filter.CustomFieldCriteria);
        }
        var groupBase = query;
        var groupText = FullTextSearchHelpers.Apply(_db, groupBase, findFilter?.Q,
            g => g.Name,
            g => g.Aliases,
            g => g.Director,
            g => g.Synopsis,
            g => g.SearchText);
        query = FullTextSearchHelpers.ApplyRelationalMatches(groupText, groupBase, findFilter?.Q,
            tagSelectors: [g => g.GroupTags.Where(gt => gt.Tag != null).Select(gt => gt.Tag!)]);

        var totalCount = await query.CountAsync(ct);
        var hasExplicitSort = !string.IsNullOrWhiteSpace(findFilter?.Sort);
        var sort = findFilter?.Sort ?? "name";
        var desc = findFilter?.Direction == Core.Enums.SortDirection.Desc;
        query = FilterHelpers.TryParseCustomFieldSort(sort, out _, out _)
            ? query.ApplyCustomFieldSort(_db, CustomFieldEntityTypes.Group, sort, desc)
            : sort switch
            {
            "name" => desc ? query.OrderByDescending(g => g.Name) : query.OrderBy(g => g.Name),
            "sort_order" or "sortOrder" => desc
                ? query.OrderByDescending(g => g.SortOrder).ThenByDescending(g => g.Name).ThenByDescending(g => g.Id)
                : query.OrderBy(g => g.SortOrder).ThenBy(g => g.Name).ThenBy(g => g.Id),
            "date" => desc ? query.OrderByDescending(g => g.Date ?? DateOnly.MinValue) : query.OrderBy(g => g.Date ?? DateOnly.MinValue),
            "rating" => EngagementQueryHelpers.ApplyRatingSort(_db, query, EngagementQueryHelpers.CurrentUserId(_db), RatingHostType.Group, desc),
            "created_at" => desc ? query.OrderByDescending(g => g.CreatedAt) : query.OrderBy(g => g.CreatedAt),
            "updated_at" or "updatedAt" => desc ? query.OrderByDescending(g => g.UpdatedAt).ThenByDescending(g => g.Id) : query.OrderBy(g => g.UpdatedAt).ThenBy(g => g.Id),
            "item_count" => ApplyGroupIntSort(query, g => g.GroupItems.Count, desc),
            "video_count" => ApplyGroupIntSort(query, g => g.GroupItems.Where(item => item.VideoId != null).Select(item => item.VideoId).Distinct().Count(), desc),
            "image_count" => ApplyGroupIntSort(query, g => g.GroupItems.Count(item => item.Kind == GroupItemKind.Image), desc),
            "audio_count" => ApplyGroupIntSort(query, g => g.GroupItems.Count(item => item.Kind == GroupItemKind.Audio), desc),
            "text_count" => ApplyGroupIntSort(query, g => g.GroupItems.Count(item => item.Kind == GroupItemKind.Text), desc),
            "gallery_count" => ApplyGroupIntSort(query, g => g.GroupItems.Count(item => item.Kind == GroupItemKind.Gallery), desc),
            "performer_count" or "performer_item_count" => ApplyGroupIntSort(query, g => g.GroupItems.Count(item => item.Kind == GroupItemKind.Performer), desc),
            "studio_count" or "studio_item_count" => ApplyGroupIntSort(query, g => g.GroupItems.Count(item => item.Kind == GroupItemKind.Studio), desc),
            "tag_item_count" => ApplyGroupIntSort(query, g => g.GroupItems.Count(item => item.Kind == GroupItemKind.Tag), desc),
            "tag_count" => ApplyGroupIntSort(query, g => g.GroupTags.Count, desc),
            "face_count" => ApplyGroupIntSort(query, g => g.GroupItems.Count(item => item.Kind == GroupItemKind.Face), desc),
            "segment_count" => ApplyGroupIntSort(query, g => g.GroupItems.Count(item => item.Kind == GroupItemKind.Segment), desc),
            "subgroup_count" => ApplyGroupIntSort(query, g => g.SubGroupRelations.Count, desc),
            "containing_group_count" => ApplyGroupIntSort(query, g => g.ContainingGroupRelations.Count, desc),
            "cached_item_count" => ApplyGroupIntSort(query, g => g.CachedItemCount ?? 0, desc),
            "last_resolved_at" => desc ? query.OrderByDescending(g => g.LastResolvedAt).ThenByDescending(g => g.Id) : query.OrderBy(g => g.LastResolvedAt).ThenBy(g => g.Id),
            "query_source_key" => desc ? query.OrderByDescending(g => g.QuerySourceKey).ThenByDescending(g => g.Id) : query.OrderBy(g => g.QuerySourceKey).ThenBy(g => g.Id),
            "show_in_video_lists" => desc ? query.OrderByDescending(g => g.ShowInVideoLists).ThenByDescending(g => g.Id) : query.OrderBy(g => g.ShowInVideoLists).ThenBy(g => g.Id),
            "aliases" => desc ? query.OrderByDescending(g => g.Aliases ?? g.Name).ThenByDescending(g => g.Id) : query.OrderBy(g => g.Aliases ?? g.Name).ThenBy(g => g.Id),
            "random" => SeededRandomOrdering.OrderBy(query, findFilter?.Seed, g => g.Id, desc),
            _ => desc ? query.OrderByDescending(g => g.UpdatedAt) : query.OrderBy(g => g.UpdatedAt),
            };
        if (!hasExplicitSort)
            query = FullTextSearchHelpers.OrderByRelevance(_db, query, findFilter?.Q);
        var page = findFilter?.Page ?? 1;
        var perPage = findFilter?.PerPage ?? 25;
        if (perPage <= 0)
        {
            return (Array.Empty<Group>(), totalCount);
        }

        var pagedIds = await query
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .Select(g => g.Id)
            .ToListAsync(ct);

        if (pagedIds.Count == 0)
        {
            return (Array.Empty<Group>(), totalCount);
        }

        var items = await _db.Groups
            .Include(g => g.Studio)
            .Include(g => g.Urls)
            .Include(g => g.GroupTags).ThenInclude(gt => gt.Tag).ThenInclude(tag => tag!.TagGroup)
            .Include(g => g.GroupItems)
            .Include(g => g.SubGroupRelations)
            .Include(g => g.ContainingGroupRelations)
            .AsSplitQuery()
            .Where(g => pagedIds.Contains(g.Id))
            .AsNoTracking()
            .ToListAsync(ct);

        var orderMap = pagedIds.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index);
        items = items.OrderBy(group => orderMap.GetValueOrDefault(group.Id, int.MaxValue)).ToList();
        return (items, totalCount);
    }

    private static GroupKind? ParseGroupKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().ToLowerInvariant() switch
        {
            "static" => GroupKind.Static,
            "dynamic" => GroupKind.Dynamic,
            _ => Enum.TryParse<GroupKind>(value, ignoreCase: true, out var parsed) ? parsed : null,
        };
    }

    private static IQueryable<Group> ApplyGroupIntSort(IQueryable<Group> query, System.Linq.Expressions.Expression<Func<Group, int>> selector, bool desc)
        => desc
            ? query.OrderByDescending(selector).ThenByDescending(group => group.Id)
            : query.OrderBy(selector).ThenBy(group => group.Id);

    private static IQueryable<Group> ApplyAllowedHostTypesCriterion(IQueryable<Group> query, StringCriterion? criterion)
    {
        if (criterion == null)
            return query;

        var value = criterion.Value.Trim().ToLowerInvariant();
        return criterion.Modifier switch
        {
            CriterionModifier.Equals or CriterionModifier.Includes => query.Where(group => group.AllowedHostTypes.Any(hostType => hostType.ToLower() == value)),
            CriterionModifier.NotEquals or CriterionModifier.Excludes => query.Where(group => !group.AllowedHostTypes.Any(hostType => hostType.ToLower() == value)),
            CriterionModifier.IsNull => query.Where(group => group.AllowedHostTypes.Count == 0),
            CriterionModifier.NotNull => query.Where(group => group.AllowedHostTypes.Count > 0),
            _ => query,
        };
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

    public async Task<IReadOnlyList<SavedFilter>> GetAllForUserAsync(int? userId, CancellationToken ct = default)
        => await _db.SavedFilters.Where(f => f.UserId == userId).AsNoTracking().ToListAsync(ct);
    public async Task<IReadOnlyList<SavedFilter>> GetByModeForUserAsync(Core.Enums.FilterMode mode, int? userId, CancellationToken ct = default)
        => await _db.SavedFilters.Where(f => f.Mode == mode && f.UserId == userId).AsNoTracking().ToListAsync(ct);

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
