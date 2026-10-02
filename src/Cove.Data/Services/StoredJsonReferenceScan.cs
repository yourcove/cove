using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Services;

/// <summary>
/// Prefilters the JSON reference scans that entity merges run over Cove-owned JSON columns. The JSON
/// rewriters (<see cref="EntityReferenceJsonRewriter"/> and <see cref="TagReferenceJsonRewriter"/>)
/// are a no-op for rows whose stored JSON never mentions a merged source id, so loading whole tables
/// for the rewrite is O(table) work for nothing. This helper restricts the load to rows whose JSON
/// text contains at least one source id: the leading-wildcard LIKE still scans the column on the
/// server (an id may appear anywhere in arbitrary JSON, so no index helps), but only matching rows
/// are transferred, materialized, and change-tracked. CAST (rather than the Postgres :: operator)
/// keeps the predicate valid on both the Npgsql and SQLite providers.
/// </summary>
internal static class StoredJsonReferenceScan
{
    public static IQueryable<TEntity> PrefilterBySourceIds<TEntity>(
        DbSet<TEntity> source,
        string table,
        string jsonColumn,
        IReadOnlyCollection<int> sourceIds)
        where TEntity : class
    {
        if (sourceIds.Count == 0)
            return source.Where(entity => false);

        var condition = string.Join(
            " OR ",
            sourceIds.Select(id => $"CAST(\"{jsonColumn}\" AS TEXT) LIKE '%{id}%'"));
        var sql = $"SELECT * FROM {table} WHERE {condition}";
        return source.FromSqlRaw(sql);
    }
}
