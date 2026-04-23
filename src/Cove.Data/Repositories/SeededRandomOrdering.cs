using System.Linq.Expressions;

namespace Cove.Data.Repositories;

internal static class SeededRandomOrdering
{
    private const long Modulus = 2147483647L;

    public static IQueryable<TEntity> OrderBy<TEntity>(IQueryable<TEntity> query, int? seed, Expression<Func<TEntity, int>> idSelector)
    {
        var normalizedSeed = Math.Abs((long)(seed ?? 1));
        if (normalizedSeed == 0)
            normalizedSeed = 1;

        var parameter = idSelector.Parameters[0];
        var idAsLong = Expression.Convert(idSelector.Body, typeof(long));
        var multiplied = Expression.Multiply(idAsLong, Expression.Constant(normalizedSeed, typeof(long)));
        var body = Expression.Modulo(multiplied, Expression.Constant(Modulus, typeof(long)));
        var selector = Expression.Lambda<Func<TEntity, long>>(body, parameter);

        return query.OrderBy(selector);
    }
}