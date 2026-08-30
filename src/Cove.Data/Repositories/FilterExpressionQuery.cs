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

    public static async Task<IQueryable<TEntity>> ApplyAsync<TEntity, TFilter>(
        IQueryable<TEntity> input,
        FilterExpression<TFilter>? expression,
        Func<IQueryable<TEntity>, TFilter, Task<IQueryable<TEntity>>> applyLeaf)
        where TEntity : class
        where TFilter : class
    {
        if (!TryValidate(expression, out var error)) throw new ArgumentException(error, nameof(expression));
        if (expression == null || expression.Children.Count == 0) return input;

        if (expression.Operator == FilterExpressionOperator.And)
        {
            var current = input;
            foreach (var child in expression.Children)
                current = await ApplyNodeAsync(current, child, applyLeaf);
            return current;
        }

        if (expression.Operator == FilterExpressionOperator.Not)
        {
            var excluded = await ApplyNodeAsync(input, expression.Children[0], applyLeaf);
            return input.Except(excluded);
        }

        IQueryable<TEntity>? union = null;
        foreach (var child in expression.Children)
        {
            var branch = await ApplyNodeAsync(input, child, applyLeaf);
            union = union == null ? branch : union.Union(branch);
        }
        return union ?? input;
    }

    private static Task<IQueryable<TEntity>> ApplyNodeAsync<TEntity, TFilter>(
        IQueryable<TEntity> input,
        FilterExpressionNode<TFilter> node,
        Func<IQueryable<TEntity>, TFilter, Task<IQueryable<TEntity>>> applyLeaf)
        where TEntity : class
        where TFilter : class
        => node.Filter != null
            ? applyLeaf(input, node.Filter)
            : ApplyAsync(input, node.Group, applyLeaf);

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
