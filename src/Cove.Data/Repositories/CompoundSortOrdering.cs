using System.Linq.Expressions;
using Cove.Core.Interfaces;

namespace Cove.Data.Repositories;

public static class CompoundSortOrdering
{
    public static List<SortClause> Normalize(IEnumerable<SortClause>? clauses, IReadOnlySet<string> supportedKeys)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return (clauses ?? [])
            .Where(clause => clause is not null
                && !string.IsNullOrWhiteSpace(clause.Key)
                && supportedKeys.Contains(clause.Key)
                && seen.Add(clause.Key))
            .Take(SortClause.MaxClauses)
            .ToList();
    }

    public static IOrderedQueryable<TEntity> Append<TEntity, TKey>(
        IQueryable<TEntity> query,
        IOrderedQueryable<TEntity>? ordered,
        Expression<Func<TEntity, TKey>> keySelector,
        bool descending)
        => ordered is null
            ? descending ? query.OrderByDescending(keySelector) : query.OrderBy(keySelector)
            : descending ? ordered.ThenByDescending(keySelector) : ordered.ThenBy(keySelector);

    public static IQueryable<TEntity> Finish<TEntity>(
        IQueryable<TEntity> query,
        IOrderedQueryable<TEntity>? ordered,
        Expression<Func<TEntity, int>> idSelector)
        => ordered is null ? query.OrderBy(idSelector) : ordered.ThenBy(idSelector);
}
