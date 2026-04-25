using System.Linq.Expressions;

namespace Cove.Data.Repositories;

internal static class SeededRandomOrdering
{
    private const long Modulus = 2147483647L;
    private const long PrimaryModulus = 13L;
    private const long SecondaryModulus = 97L;

    public static IQueryable<TEntity> OrderBy<TEntity>(IQueryable<TEntity> query, int? seed, Expression<Func<TEntity, int>> idSelector, bool desc = false)
    {
        var normalizedSeed = Math.Abs((long)(seed ?? 1));
        if (normalizedSeed == 0)
            normalizedSeed = 1;

        var parameter = idSelector.Parameters[0];
        var idAsLong = Expression.Convert(idSelector.Body, typeof(long));
        var seedConstant = Expression.Constant(normalizedSeed, typeof(long));

        // Use multiple mixed modulo keys so small contiguous ids do not stay monotonic.
        var primaryBody = Expression.Modulo(
            Expression.Add(
                Expression.Multiply(idAsLong, Expression.Constant(17L, typeof(long))),
                Expression.Multiply(seedConstant, Expression.Constant(31L, typeof(long)))),
            Expression.Constant(PrimaryModulus, typeof(long)));
        var secondaryBody = Expression.Modulo(
            Expression.Add(
                Expression.Multiply(idAsLong, Expression.Constant(101L, typeof(long))),
                Expression.Multiply(seedConstant, Expression.Constant(131L, typeof(long)))),
            Expression.Constant(SecondaryModulus, typeof(long)));
        var tertiaryBody = Expression.Modulo(
            Expression.Add(
                Expression.Multiply(idAsLong, Expression.Constant(1103515245L, typeof(long))),
                Expression.Multiply(seedConstant, Expression.Constant(12345L, typeof(long)))),
            Expression.Constant(Modulus, typeof(long)));

        var primarySelector = Expression.Lambda<Func<TEntity, long>>(primaryBody, parameter);
        var secondarySelector = Expression.Lambda<Func<TEntity, long>>(secondaryBody, parameter);
        var tertiarySelector = Expression.Lambda<Func<TEntity, long>>(tertiaryBody, parameter);

        return desc
            ? query.OrderByDescending(primarySelector)
                .ThenByDescending(secondarySelector)
                .ThenByDescending(tertiarySelector)
                .ThenByDescending(idSelector)
            : query.OrderBy(primarySelector)
                .ThenBy(secondarySelector)
                .ThenBy(tertiarySelector)
                .ThenBy(idSelector);
    }
}