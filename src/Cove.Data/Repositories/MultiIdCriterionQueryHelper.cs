using System.Linq.Expressions;
using Cove.Core.Interfaces;

namespace Cove.Data.Repositories;

internal static class MultiIdCriterionQueryHelper
{
    public static IQueryable<TEntity> Apply<TEntity>(
        IQueryable<TEntity> query,
        MultiIdCriterion? criterion,
        Expression<Func<TEntity, IEnumerable<int>>> idsSelector,
        IReadOnlyList<int[]>? valueGroups = null,
        IReadOnlyList<int[]>? requiredIdGroups = null)
    {
        if (criterion?.Modifier == CriterionModifier.IsNull || criterion?.Modifier == CriterionModifier.NotNull)
        {
            query = ApplyNullPresence(query, criterion.Modifier, idsSelector);
        }
        else if (criterion == null || (criterion.Value.Count == 0 && (criterion.Excludes == null || criterion.Excludes.Count == 0) && (criterion.RequiredIds == null || criterion.RequiredIds.Count == 0) && requiredIdGroups is not { Count: > 0 }))
        {
            return query;
        }
        else
        {
            query = valueGroups is { Count: > 0 } && criterion.Value.Count > 0 && criterion.Modifier is CriterionModifier.IncludesAll or CriterionModifier.ExcludesAll
                ? ApplyGroupedValues(query, criterion, valueGroups, idsSelector)
                : ApplyFlatValues(query, criterion, idsSelector);
        }

        if (criterion.Excludes?.Count > 0)
        {
            query = ApplyExcludedIds(query, criterion.Excludes, idsSelector);
        }

        if (criterion.RequiredIds?.Count > 0)
        {
            query = ApplyRequiredIds(query, criterion.RequiredIds, idsSelector);
        }

        if (requiredIdGroups is { Count: > 0 })
        {
            query = ApplyRequiredIdGroups(query, requiredIdGroups, idsSelector);
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
        var selectedIds = criterion.Value.Where(id => id > 0).Distinct().ToArray();
        if (selectedIds.Length == 0)
        {
            return query;
        }

        var anySelectedInEntity = BuildAnyEntityIdEquals(entityIds, selectedIds);
        var allSelectedInEntity = BuildAllEntityIdsPresent(entityIds, selectedIds);

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

    private static IQueryable<TEntity> ApplyNullPresence<TEntity>(
        IQueryable<TEntity> query,
        CriterionModifier modifier,
        Expression<Func<TEntity, IEnumerable<int>>> idsSelector)
    {
        var entityParam = idsSelector.Parameters[0];
        var hasAny = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Any),
            [typeof(int)],
            idsSelector.Body);
        Expression body = modifier == CriterionModifier.IsNull ? Expression.Not(hasAny) : hasAny;
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

            Expression? anyGroupInEntity = null;
            foreach (var groupId in group.Distinct())
            {
                var entityIdParam = Expression.Parameter(typeof(int), "entityId");
                var entityHasGroupId = Expression.Call(
                    typeof(Enumerable),
                    nameof(Enumerable.Any),
                    [typeof(int)],
                    entityIds,
                    Expression.Lambda<Func<int, bool>>(
                        Expression.Equal(entityIdParam, Expression.Constant(groupId)),
                        entityIdParam));

                anyGroupInEntity = anyGroupInEntity == null
                    ? entityHasGroupId
                    : Expression.OrElse(anyGroupInEntity, entityHasGroupId);
            }

            if (anyGroupInEntity == null)
            {
                continue;
            }

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
        var anyExcludedInEntity = BuildAnyEntityIdEquals(entityIds, excludedIds.Where(id => id > 0).Distinct().ToArray());

        var body = Expression.Not(anyExcludedInEntity);
        return query.Where(Expression.Lambda<Func<TEntity, bool>>(body, entityParam));
    }

    private static IQueryable<TEntity> ApplyRequiredIds<TEntity>(
        IQueryable<TEntity> query,
        IReadOnlyCollection<int> requiredIds,
        Expression<Func<TEntity, IEnumerable<int>>> idsSelector)
    {
        var selectedIds = requiredIds.Where(id => id > 0).Distinct().ToArray();
        if (selectedIds.Length == 0)
        {
            return query;
        }

        var body = BuildAllEntityIdsPresent(idsSelector.Body, selectedIds);
        return query.Where(Expression.Lambda<Func<TEntity, bool>>(body, idsSelector.Parameters[0]));
    }

    private static IQueryable<TEntity> ApplyRequiredIdGroups<TEntity>(
        IQueryable<TEntity> query,
        IReadOnlyList<int[]> requiredIdGroups,
        Expression<Func<TEntity, IEnumerable<int>>> idsSelector)
    {
        var criterion = new MultiIdCriterion { Modifier = CriterionModifier.IncludesAll };
        return ApplyGroupedValues(query, criterion, requiredIdGroups, idsSelector);
    }

    private static Expression BuildAnyEntityIdEquals(Expression entityIds, IReadOnlyCollection<int> selectedIds)
    {
        Expression? anySelectedInEntity = null;
        foreach (var selectedId in selectedIds)
        {
            var entityIdParam = Expression.Parameter(typeof(int), "entityId");
            var entityHasId = Expression.Call(
                typeof(Enumerable),
                nameof(Enumerable.Any),
                [typeof(int)],
                entityIds,
                Expression.Lambda<Func<int, bool>>(
                    Expression.Equal(entityIdParam, Expression.Constant(selectedId)),
                    entityIdParam));

            anySelectedInEntity = anySelectedInEntity == null
                ? entityHasId
                : Expression.OrElse(anySelectedInEntity, entityHasId);
        }

        return anySelectedInEntity ?? Expression.Constant(false);
    }

    private static Expression BuildAllEntityIdsPresent(Expression entityIds, IReadOnlyCollection<int> selectedIds)
    {
        Expression? allSelectedInEntity = null;
        foreach (var selectedId in selectedIds)
        {
            var entityIdParam = Expression.Parameter(typeof(int), "entityId");
            var entityHasId = Expression.Call(
                typeof(Enumerable),
                nameof(Enumerable.Any),
                [typeof(int)],
                entityIds,
                Expression.Lambda<Func<int, bool>>(
                    Expression.Equal(entityIdParam, Expression.Constant(selectedId)),
                    entityIdParam));

            allSelectedInEntity = allSelectedInEntity == null
                ? entityHasId
                : Expression.AndAlso(allSelectedInEntity, entityHasId);
        }

        return allSelectedInEntity ?? Expression.Constant(true);
    }
}
