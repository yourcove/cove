using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.Data.Repositories;

public static class FilterExpressionQuery
{
    public const int MaxDepth = 8;
    public const int MaxLeaves = 100;

    public static bool TryValidate<TFilter>(FilterExpression<TFilter>? expression, out string? error) where TFilter : class
    {
        error = null;
        if (expression == null) return true;
        var leaves = 0;
        return ValidateGroup(expression, 1, ref leaves, ref error);
    }

    public static bool Contains<TFilter>(FilterExpression<TFilter>? expression, Func<TFilter, bool> predicate) where TFilter : class
        => expression?.Children?.Any(node => node != null && (node.Filter != null ? predicate(node.Filter) : Contains(node.Group, predicate))) == true;

    /// <summary>Whether the expression holds an OR, NOT or just-one group anywhere, rather than only AND groups.</summary>
    internal static bool HasSetOperation<TFilter>(FilterExpression<TFilter>? expression) where TFilter : class
        => expression != null
            && (expression.Operator != FilterExpressionOperator.And
                || expression.Children.Any(node => node?.Group != null && HasSetOperation(node.Group)));

    public static Task<IQueryable<TEntity>> ApplyAsync<TEntity, TFilter>(
        IQueryable<TEntity> input,
        FilterExpression<TFilter>? expression,
        Func<IQueryable<TEntity>, TFilter, Task<IQueryable<TEntity>>> applyLeaf,
        Func<IQueryable<TEntity>, RelatedFilterScope, IReadOnlyList<TFilter>, Task<IQueryable<TEntity>>>? applyDistinctScope = null)
        where TEntity : BaseEntity
        where TFilter : class
        => ApplyAsync(input, input, expression, applyLeaf, applyDistinctScope);

    /// <summary>
    /// Applies <paramref name="expression"/> to <paramref name="input"/>, starting OR, NOT and just-one branches
    /// from <paramref name="root"/>. Pass the unfiltered, read-scoped query as the root when the leaves build their
    /// own queries, so each branch does not repeat the conditions already applied to the input.
    /// <paramref name="input"/> must be narrowed from <paramref name="root"/>, and the leaf and distinct-scope
    /// callbacks may only narrow their source by conditions that do not depend on it; otherwise a NOT could keep
    /// rows that its branch, built from the root, failed to match.
    /// </summary>
    internal static async Task<IQueryable<TEntity>> ApplyAsync<TEntity, TFilter>(
        IQueryable<TEntity> root,
        IQueryable<TEntity> input,
        FilterExpression<TFilter>? expression,
        Func<IQueryable<TEntity>, TFilter, Task<IQueryable<TEntity>>> applyLeaf,
        Func<IQueryable<TEntity>, RelatedFilterScope, IReadOnlyList<TFilter>, Task<IQueryable<TEntity>>>? applyDistinctScope = null)
        where TEntity : BaseEntity
        where TFilter : class
    {
        if (!TryValidate(expression, out var error)) throw new ArgumentException(error, nameof(expression));
        if (expression == null || expression.Children.Count == 0) return input;
        return await ApplyGroupAsync(root, input, expression, applyLeaf, applyDistinctScope);
    }

    /// <summary>
    /// Rows of <paramref name="input"/> that match at least one branch. Branches are compared by id, so the
    /// database unions narrow id lists instead of whole rows; build each branch from the expression's root
    /// rather than from <paramref name="input"/>, so it does not repeat every condition already applied.
    /// </summary>
    internal static IQueryable<TEntity> MatchingAny<TEntity>(IQueryable<TEntity> input, IEnumerable<IQueryable<TEntity>> branches)
        where TEntity : BaseEntity
    {
        IQueryable<int>? ids = null;
        foreach (var branch in branches)
        {
            var branchIds = branch.Select(entity => entity.Id);
            ids = ids == null ? branchIds : ids.Union(branchIds);
        }
        return ids == null ? input : input.Where(entity => ids.Any(id => id == entity.Id));
    }

    /// <summary>Rows of <paramref name="input"/> that are not in <paramref name="excluded"/>, compared by id.</summary>
    internal static IQueryable<TEntity> MatchingNone<TEntity>(IQueryable<TEntity> input, IQueryable<TEntity> excluded)
        where TEntity : BaseEntity
        => input.Where(entity => !excluded.Any(match => match.Id == entity.Id));

    /// <summary>Rows of <paramref name="input"/> that match exactly one branch, compared by id.</summary>
    internal static IQueryable<TEntity> MatchingExactlyOne<TEntity>(IQueryable<TEntity> input, IEnumerable<IQueryable<TEntity>> branches)
        where TEntity : BaseEntity
    {
        IQueryable<int>? ids = null;
        foreach (var branch in branches)
        {
            var branchIds = branch.Select(entity => entity.Id).Distinct();
            ids = ids == null ? branchIds : ids.Concat(branchIds);
        }
        if (ids == null) return input;
        var once = ids.GroupBy(id => id).Where(group => group.Count() == 1).Select(group => group.Key);
        return input.Where(entity => once.Any(id => id == entity.Id));
    }

    private static async Task<IQueryable<TEntity>> ApplyGroupAsync<TEntity, TFilter>(
        IQueryable<TEntity> root,
        IQueryable<TEntity> input,
        FilterExpression<TFilter> expression,
        Func<IQueryable<TEntity>, TFilter, Task<IQueryable<TEntity>>> applyLeaf,
        Func<IQueryable<TEntity>, RelatedFilterScope, IReadOnlyList<TFilter>, Task<IQueryable<TEntity>>>? applyDistinctScope)
        where TEntity : BaseEntity
        where TFilter : class
    {
        if (expression.Operator == FilterExpressionOperator.And)
        {
            if (expression.RelatedScope?.MatchMode == RelatedScopeMatchMode.Distinct)
            {
                if (applyDistinctScope == null)
                    throw new NotSupportedException($"Distinct assignment is not supported for related scope '{expression.RelatedScope.FilterKey}' by this repository.");
                var scoped = await applyDistinctScope(input, expression.RelatedScope, expression.Children.Select(child => child.Filter!).ToArray());
                foreach (var child in expression.Children)
                    scoped = await applyLeaf(scoped, child.Filter!);
                return scoped;
            }
            var current = input;
            foreach (var child in expression.Children)
                current = await ApplyNodeAsync(root, current, child, applyLeaf, applyDistinctScope);
            return current;
        }

        if (expression.Operator == FilterExpressionOperator.Not)
            return MatchingNone(input, await ApplyNodeAsync(root, root, expression.Children[0], applyLeaf, applyDistinctScope));

        var branches = new List<IQueryable<TEntity>>(expression.Children.Count);
        foreach (var child in expression.Children)
            branches.Add(await ApplyNodeAsync(root, root, child, applyLeaf, applyDistinctScope));
        return expression.Operator == FilterExpressionOperator.JustOne
            ? MatchingExactlyOne(input, branches)
            : MatchingAny(input, branches);
    }

    private static Task<IQueryable<TEntity>> ApplyNodeAsync<TEntity, TFilter>(
        IQueryable<TEntity> root,
        IQueryable<TEntity> input,
        FilterExpressionNode<TFilter> node,
        Func<IQueryable<TEntity>, TFilter, Task<IQueryable<TEntity>>> applyLeaf,
        Func<IQueryable<TEntity>, RelatedFilterScope, IReadOnlyList<TFilter>, Task<IQueryable<TEntity>>>? applyDistinctScope)
        where TEntity : BaseEntity
        where TFilter : class
        => node.Filter != null
            ? applyLeaf(input, node.Filter)
            : ApplyGroupAsync(root, input, node.Group!, applyLeaf, applyDistinctScope);

    private static bool ValidateGroup<TFilter>(FilterExpression<TFilter> group, int depth, ref int leaves, ref string? error) where TFilter : class
    {
        if (depth > MaxDepth)
        {
            error = $"Filter expressions may not exceed {MaxDepth} group levels.";
            return false;
        }
        if (group.Children == null)
        {
            error = "Filter-expression children may not be null.";
            return false;
        }
        if (!Enum.IsDefined(group.Operator))
        {
            error = $"Unsupported filter-expression operator '{group.Operator}'.";
            return false;
        }
        if ((group.DistinctRelatedMatches || group.RelatedScope?.MatchMode == RelatedScopeMatchMode.Distinct)
            && group.Operator != FilterExpressionOperator.And)
        {
            error = "Distinct related matches are supported only by AND filter-expression groups.";
            return false;
        }
        if (group.RelatedScope is { FilterKey.Length: 0 })
        {
            error = "Related filter-expression scopes must identify a filter key.";
            return false;
        }
        if (group.RelatedScope != null)
        {
            if (group.DistinctRelatedMatches)
            {
                error = "Filter-expression groups may not combine legacy distinct matching with a related scope.";
                return false;
            }
            if (!Enum.IsDefined(group.RelatedScope.MatchMode))
            {
                error = $"Unsupported related-scope match mode '{group.RelatedScope.MatchMode}'.";
                return false;
            }
            if (group.Operator != FilterExpressionOperator.And)
            {
                error = "Related filter-expression scopes are supported only by AND groups.";
                return false;
            }
            if (group.Children.Count < 2 || group.Children.Any(child => child == null || child.Filter == null || child.Group != null))
            {
                error = "Related filter-expression scopes must contain at least two direct filter conditions.";
                return false;
            }
            var relatedProperty = typeof(TFilter).GetProperties().FirstOrDefault(property =>
                string.Equals(property.Name, group.RelatedScope.FilterKey, StringComparison.OrdinalIgnoreCase));
            if (relatedProperty == null
                || !relatedProperty.PropertyType.IsGenericType
                || relatedProperty.PropertyType.GetGenericTypeDefinition() != typeof(RelatedFilterCriterion<>)
                || group.Children.Any(child => relatedProperty.GetValue(child.Filter) == null))
            {
                error = $"Every condition in a related filter-expression scope must use '{group.RelatedScope.FilterKey}'.";
                return false;
            }
            if (group.Children.Any(child =>
                {
                    var criterion = relatedProperty.GetValue(child.Filter)!;
                    return (RelatedFilterMode)criterion.GetType().GetProperty(nameof(RelatedFilterCriterion<object>.Mode))!.GetValue(criterion)! != RelatedFilterMode.AtLeastOne
                        || (bool)criterion.GetType().GetProperty(nameof(RelatedFilterCriterion<object>.Exclude))!.GetValue(criterion)!;
                }))
            {
                error = "Related filter-expression scopes require positive at-least-one conditions.";
                return false;
            }
            var supportsDistinctAssignment = typeof(TFilter) == typeof(VideoFilter)
                    && relatedProperty.Name == nameof(VideoFilter.PerformerFilterCriterion)
                || typeof(TFilter) == typeof(AudioFilter)
                    && relatedProperty.Name == nameof(AudioFilter.PerformerFilterCriterion)
                || typeof(TFilter) == typeof(PerformerFilter)
                    && relatedProperty.Name == nameof(PerformerFilter.AudioFilterCriterion);
            if (group.RelatedScope.MatchMode == RelatedScopeMatchMode.Distinct
                && !supportsDistinctAssignment)
            {
                error = $"Distinct assignment is not supported for related scope '{group.RelatedScope.FilterKey}'.";
                return false;
            }
            if (group.RelatedScope.MatchMode == RelatedScopeMatchMode.Distinct && group.Children.Count > 8)
            {
                error = "Distinct related filter-expression scopes may not contain more than 8 conditions.";
                return false;
            }
        }
        if (group.Operator == FilterExpressionOperator.Not && group.Children.Count != 1)
        {
            error = "NOT filter-expression groups must contain exactly one child.";
            return false;
        }
        if (depth > 1 && group.Children.Count == 0)
        {
            error = "Nested filter groups may not be empty.";
            return false;
        }
        foreach (var child in group.Children)
        {
            if (child == null)
            {
                error = "Filter-expression children may not contain null nodes.";
                return false;
            }
            if ((child.Filter == null) == (child.Group == null))
            {
                error = "Each filter-expression child must contain exactly one filter or group.";
                return false;
            }
            if (child.Filter != null && ++leaves > MaxLeaves)
            {
                error = $"Filter expressions may not contain more than {MaxLeaves} filters.";
                return false;
            }
            if (child.Group != null && !ValidateGroup(child.Group, depth + 1, ref leaves, ref error)) return false;
        }
        return true;
    }
}
