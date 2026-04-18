using System.Linq.Expressions;
using Cove.Core.Interfaces;

namespace Cove.Data.Repositories;

internal static class MultiIdCriterionQueryHelper
{
    public static IQueryable<TEntity> Apply<TEntity>(
        IQueryable<TEntity> query,
        MultiIdCriterion? criterion,
        Expression<Func<TEntity, IEnumerable<int>>> idsSelector,
        IReadOnlyList<int[]>? valueGroups = null)
    {
        if (criterion == null || (criterion.Value.Count == 0 && (criterion.Excludes == null || criterion.Excludes.Count == 0)))
        {
            return query;
        }

        query = valueGroups is { Count: > 0 } && criterion.Value.Count > 0 && criterion.Modifier is CriterionModifier.IncludesAll or CriterionModifier.ExcludesAll
            ? ApplyGroupedValues(query, criterion, valueGroups, idsSelector)
            : ApplyFlatValues(query, criterion, idsSelector);

        if (criterion.Excludes?.Count > 0)
        {
            query = ApplyExcludedIds(query, criterion.Excludes, idsSelector);
        }

        return query;
    }

    private static IQueryable<TEntity> ApplyFlatValues<TEntity>(
        IQueryable<TEntity> query,
        MultiIdCriterion criterion,
        Expression<Func<TEntity, IEnumerable<int>>> idsSelector)
    {
        if (criterion.Value.Count == 0)
        {
            return query;
        }

        var entityParam = idsSelector.Parameters[0];
        var entityIds = idsSelector.Body;
        var selectedIds = Expression.Constant(criterion.Value.ToArray());

        var entityIdParam = Expression.Parameter(typeof(int), "entityId");
        var anySelectedInEntity = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Any),
            [typeof(int)],
            entityIds,
            Expression.Lambda<Func<int, bool>>(
                Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(int)], selectedIds, entityIdParam),
                entityIdParam));

        var selectedIdParam = Expression.Parameter(typeof(int), "selectedId");
        var allSelectedInEntity = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.All),
            [typeof(int)],
            selectedIds,
            Expression.Lambda<Func<int, bool>>(
                Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(int)], entityIds, selectedIdParam),
                selectedIdParam));

        Expression body = criterion.Modifier switch
        {
            CriterionModifier.Includes => anySelectedInEntity,
            CriterionModifier.Excludes => Expression.Not(anySelectedInEntity),
            CriterionModifier.IncludesAll => allSelectedInEntity,
            CriterionModifier.ExcludesAll => Expression.Not(allSelectedInEntity),
            _ => anySelectedInEntity,
        };

        return query.Where(Expression.Lambda<Func<TEntity, bool>>(body, entityParam));
    }

    private static IQueryable<TEntity> ApplyGroupedValues<TEntity>(
        IQueryable<TEntity> query,
        MultiIdCriterion criterion,
        IReadOnlyList<int[]> valueGroups,
        Expression<Func<TEntity, IEnumerable<int>>> idsSelector)
    {
        var entityParam = idsSelector.Parameters[0];
        var entityIds = idsSelector.Body;

        Expression? allGroupsMatched = null;
        foreach (var group in valueGroups)
        {
            if (group.Length == 0)
            {
                continue;
            }

            var groupIds = Expression.Constant(group);
            var entityIdParam = Expression.Parameter(typeof(int), "entityId");
            var anyGroupInEntity = Expression.Call(
                typeof(Enumerable),
                nameof(Enumerable.Any),
                [typeof(int)],
                entityIds,
                Expression.Lambda<Func<int, bool>>(
                    Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(int)], groupIds, entityIdParam),
                    entityIdParam));

            allGroupsMatched = allGroupsMatched == null
                ? anyGroupInEntity
                : Expression.AndAlso(allGroupsMatched, anyGroupInEntity);
        }

        if (allGroupsMatched == null)
        {
            return query;
        }

        var body = criterion.Modifier == CriterionModifier.IncludesAll
            ? allGroupsMatched
            : Expression.Not(allGroupsMatched);

        return query.Where(Expression.Lambda<Func<TEntity, bool>>(body, entityParam));
    }

    private static IQueryable<TEntity> ApplyExcludedIds<TEntity>(
        IQueryable<TEntity> query,
        IReadOnlyCollection<int> excludedIds,
        Expression<Func<TEntity, IEnumerable<int>>> idsSelector)
    {
        var entityParam = idsSelector.Parameters[0];
        var entityIds = idsSelector.Body;
        var excludedConst = Expression.Constant(excludedIds.ToArray());
        var excludedIdParam = Expression.Parameter(typeof(int), "excludedId");

        var anyExcludedInEntity = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Any),
            [typeof(int)],
            entityIds,
            Expression.Lambda<Func<int, bool>>(
                Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(int)], excludedConst, excludedIdParam),
                excludedIdParam));

        var body = Expression.Not(anyExcludedInEntity);
        return query.Where(Expression.Lambda<Func<TEntity, bool>>(body, entityParam));
    }
}