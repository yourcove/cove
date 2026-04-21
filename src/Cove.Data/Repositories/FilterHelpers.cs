using System.Linq.Expressions;
using System.Globalization;
using System.Text.RegularExpressions;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Repositories;

/// <summary>
/// Generic filter helpers that work with any entity type.
/// Centralizes criterion-based filtering logic previously duplicated per-entity.
/// </summary>
internal static class FilterHelpers
{
    /// <summary>Apply an IntCriterion to a queryable using an expression selector.</summary>
    public static IQueryable<T> ApplyInt<T>(IQueryable<T> query, IntCriterion? criterion, Expression<Func<T, int>> selector)
    {
        if (criterion == null) return query;
        var val = criterion.Value;
        var val2 = criterion.Value2 ?? val;
        var param = selector.Parameters[0];
        var body = selector.Body;

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.Equal(body, Expression.Constant(val)), param)),
            CriterionModifier.NotEquals => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.NotEqual(body, Expression.Constant(val)), param)),
            CriterionModifier.GreaterThan => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.GreaterThan(body, Expression.Constant(val)), param)),
            CriterionModifier.LessThan => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.LessThan(body, Expression.Constant(val)), param)),
            CriterionModifier.Between => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.AndAlso(
                    Expression.GreaterThanOrEqual(body, Expression.Constant(val)),
                    Expression.LessThanOrEqual(body, Expression.Constant(val2))), param)),
            CriterionModifier.NotBetween => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.OrElse(
                    Expression.LessThan(body, Expression.Constant(val)),
                    Expression.GreaterThan(body, Expression.Constant(val2))), param)),
            _ => query,
        };
    }

    /// <summary>Apply a MultiIdCriterion to a queryable.</summary>
    public static IQueryable<T> ApplyMultiId<T>(
        IQueryable<T> query,
        MultiIdCriterion? criterion,
        Expression<Func<T, IEnumerable<int>>> idsSelector,
        IReadOnlyList<int[]>? valueGroups = null)
        => MultiIdCriterionQueryHelper.Apply(query, criterion, idsSelector, valueGroups);

    /// <summary>Apply a studio (single FK) MultiIdCriterion.</summary>
    public static IQueryable<T> ApplyStudioCriterion<T>(IQueryable<T> query, MultiIdCriterion? criterion, Expression<Func<T, int?>> studioIdSelector)
    {
        if (criterion == null || (criterion.Value.Count == 0 && (criterion.Excludes == null || criterion.Excludes.Count == 0)))
        {
            return query;
        }

        var param = studioIdSelector.Parameters[0];
        var body = studioIdSelector.Body;

        // StudioId.HasValue
        var hasValue = Expression.Property(body, "HasValue");
        // StudioId.Value
        var value = Expression.Property(body, "Value");
        var containsMethod = typeof(Enumerable).GetMethods()
            .First(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(int));
        Expression? predicate = null;

        if (criterion.Value.Count > 0)
        {
            var idsConst = Expression.Constant(criterion.Value.ToArray());
            var contains = Expression.Call(null, containsMethod, idsConst, value);

            predicate = criterion.Modifier switch
            {
                CriterionModifier.Includes => Expression.AndAlso(hasValue, contains),
                CriterionModifier.Excludes => Expression.OrElse(Expression.Not(hasValue), Expression.Not(contains)),
                CriterionModifier.IncludesAll => Expression.AndAlso(hasValue, contains),
                CriterionModifier.ExcludesAll => Expression.OrElse(Expression.Not(hasValue), Expression.Not(contains)),
                _ => Expression.AndAlso(hasValue, contains),
            };
        }

        if (criterion.Excludes is { Count: > 0 })
        {
            var excludedConst = Expression.Constant(criterion.Excludes.ToArray());
            var excludedContains = Expression.Call(null, containsMethod, excludedConst, value);
            var excludesPredicate = Expression.OrElse(Expression.Not(hasValue), Expression.Not(excludedContains));
            predicate = predicate == null ? excludesPredicate : Expression.AndAlso(predicate, excludesPredicate);
        }

        if (predicate == null)
        {
            return query;
        }

        return query.Where(Expression.Lambda<Func<T, bool>>(predicate, param));
    }

    /// <summary>Apply a StringCriterion to a queryable using a string property selector.</summary>
    public static IQueryable<T> ApplyString<T>(IQueryable<T> query, StringCriterion? criterion, Expression<Func<T, string?>> selector)
    {
        if (criterion == null) return query;
        var val = criterion.Value;

        // We need to compile different LINQ expressions for each modifier
        // Using the selector expression to build new lambda expressions
        var param = selector.Parameters[0];
        var body = selector.Body;

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => WhereStringEquals(query, param, body, val),
            CriterionModifier.NotEquals => WhereStringNotEquals(query, param, body, val),
            CriterionModifier.Includes => WhereStringContains(query, param, body, val),
            CriterionModifier.Excludes => WhereStringNotContains(query, param, body, val),
            CriterionModifier.MatchesRegex => WhereStringMatchesRegex(query, param, body, val),
            CriterionModifier.NotMatchesRegex => WhereStringNotMatchesRegex(query, param, body, val),
            CriterionModifier.IsNull => WhereStringIsNull(query, param, body),
            CriterionModifier.NotNull => WhereStringNotNull(query, param, body),
            _ => query,
        };
    }

    /// <summary>Apply a StringCriterion to a normalized full file path derived from a file collection.</summary>
    public static IQueryable<T> ApplyFilePath<T, TFile>(
        IQueryable<T> query,
        StringCriterion? criterion,
        Expression<Func<T, IEnumerable<TFile>>> filesSelector)
        where TFile : BaseFileEntity
    {
        if (criterion == null) return query;

        var value = NormalizePathValue(criterion.Value);
        var entityParam = filesSelector.Parameters[0];
        var fileParam = Expression.Parameter(typeof(TFile), "file");
        var fullPath = BuildNormalizedFilePathExpression(fileParam);
        var fullPathOrEmpty = Expression.Coalesce(fullPath, Expression.Constant(string.Empty));
        var equals = Expression.Equal(fullPathOrEmpty, Expression.Constant(value));
        var notEmpty = Expression.NotEqual(fullPathOrEmpty, Expression.Constant(string.Empty));

        var toLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;
        var containsMethod = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
        var contains = Expression.Call(
            Expression.Call(fullPathOrEmpty, toLower),
            containsMethod,
            Expression.Constant(value.ToLower()));

        var regexMatch = Expression.Call(
            typeof(Regex).GetMethod(nameof(Regex.IsMatch), [typeof(string), typeof(string), typeof(RegexOptions)])!,
            fullPathOrEmpty,
            Expression.Constant(value),
            Expression.Constant(RegexOptions.IgnoreCase));

        Expression? predicate = criterion.Modifier switch
        {
            CriterionModifier.Equals => Any(filesSelector.Body, fileParam, equals),
            CriterionModifier.NotEquals => Expression.Not(Any(filesSelector.Body, fileParam, equals)),
            CriterionModifier.Includes => Any(filesSelector.Body, fileParam, contains),
            CriterionModifier.Excludes => Expression.Not(Any(filesSelector.Body, fileParam, contains)),
            CriterionModifier.MatchesRegex => Any(filesSelector.Body, fileParam, regexMatch),
            CriterionModifier.NotMatchesRegex => Expression.Not(Any(filesSelector.Body, fileParam, regexMatch)),
            CriterionModifier.IsNull => Expression.Not(Any(filesSelector.Body, fileParam, notEmpty)),
            CriterionModifier.NotNull => Any(filesSelector.Body, fileParam, notEmpty),
            _ => null,
        };

        return predicate == null
            ? query
            : query.Where(Expression.Lambda<Func<T, bool>>(predicate, entityParam));
    }

    /// <summary>Apply a DateCriterion to a DateOnly? property.</summary>
    public static IQueryable<T> ApplyDate<T>(IQueryable<T> query, DateCriterion? criterion, Expression<Func<T, DateOnly?>> selector)
    {
        if (criterion == null) return query;
        if (!DateOnly.TryParse(criterion.Value, out var d1)) return query;
        DateOnly.TryParse(criterion.Value2, out var d2);

        var param = selector.Parameters[0];
        var body = selector.Body;
        // Get the .Value property of the Nullable<DateOnly>
        var value = Expression.Property(body, "Value");
        var hasValue = Expression.Property(body, "HasValue");

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.AndAlso(hasValue, Expression.Equal(value, Expression.Constant(d1))), param)),
            CriterionModifier.NotEquals => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.OrElse(Expression.Not(hasValue), Expression.NotEqual(value, Expression.Constant(d1))), param)),
            CriterionModifier.GreaterThan => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.AndAlso(hasValue, Expression.GreaterThan(value, Expression.Constant(d1))), param)),
            CriterionModifier.LessThan => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.AndAlso(hasValue, Expression.LessThan(value, Expression.Constant(d1))), param)),
            CriterionModifier.Between => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.AndAlso(hasValue,
                    Expression.AndAlso(
                        Expression.GreaterThanOrEqual(value, Expression.Constant(d1)),
                        Expression.LessThanOrEqual(value, Expression.Constant(d2)))), param)),
            CriterionModifier.NotBetween => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.AndAlso(hasValue,
                    Expression.OrElse(
                        Expression.LessThan(value, Expression.Constant(d1)),
                        Expression.GreaterThan(value, Expression.Constant(d2)))), param)),
            CriterionModifier.IsNull => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.Not(hasValue), param)),
            CriterionModifier.NotNull => query.Where(Expression.Lambda<Func<T, bool>>(
                hasValue, param)),
            _ => query,
        };
    }

    /// <summary>Apply a timestamp criterion to a DateTime property.</summary>
    public static IQueryable<T> ApplyTimestamp<T>(IQueryable<T> query, TimestampCriterion? criterion, Expression<Func<T, DateTime>> selector)
    {
        if (criterion == null) return query;
        if (criterion.Modifier == CriterionModifier.IsNull) return query.Where(_ => false);
        if (criterion.Modifier == CriterionModifier.NotNull) return query;
        if (!TryParseTimestamp(criterion.Value, out var ts1)) return query;
        var ts2 = TryParseTimestamp(criterion.Value2, out var parsedTs2) ? parsedTs2 : ts1;

        var param = selector.Parameters[0];
        var body = selector.Body;

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.Equal(body, Expression.Constant(ts1)), param)),
            CriterionModifier.NotEquals => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.NotEqual(body, Expression.Constant(ts1)), param)),
            CriterionModifier.GreaterThan => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.GreaterThan(body, Expression.Constant(ts1)), param)),
            CriterionModifier.LessThan => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.LessThan(body, Expression.Constant(ts1)), param)),
            CriterionModifier.Between => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.AndAlso(
                    Expression.GreaterThanOrEqual(body, Expression.Constant(ts1)),
                    Expression.LessThanOrEqual(body, Expression.Constant(ts2))), param)),
            CriterionModifier.NotBetween => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.OrElse(
                    Expression.LessThan(body, Expression.Constant(ts1)),
                    Expression.GreaterThan(body, Expression.Constant(ts2))), param)),
            _ => query,
        };
    }

    /// <summary>Apply a timestamp criterion to a nullable DateTime? property.</summary>
    public static IQueryable<T> ApplyNullableTimestamp<T>(IQueryable<T> query, TimestampCriterion? criterion, Expression<Func<T, DateTime?>> selector)
    {
        if (criterion == null) return query;

        var param = selector.Parameters[0];
        var body = selector.Body;
        var hasValue = Expression.Property(body, "HasValue");

        if (criterion.Modifier == CriterionModifier.IsNull)
            return query.Where(Expression.Lambda<Func<T, bool>>(Expression.Not(hasValue), param));
        if (criterion.Modifier == CriterionModifier.NotNull)
            return query.Where(Expression.Lambda<Func<T, bool>>(hasValue, param));

        if (!TryParseTimestamp(criterion.Value, out var ts1)) return query;
        var ts2 = TryParseTimestamp(criterion.Value2, out var parsedTs2) ? parsedTs2 : ts1;
        var value = Expression.Property(body, "Value");

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.AndAlso(hasValue, Expression.Equal(value, Expression.Constant(ts1))), param)),
            CriterionModifier.NotEquals => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.OrElse(Expression.Not(hasValue), Expression.NotEqual(value, Expression.Constant(ts1))), param)),
            CriterionModifier.GreaterThan => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.AndAlso(hasValue, Expression.GreaterThan(value, Expression.Constant(ts1))), param)),
            CriterionModifier.LessThan => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.AndAlso(hasValue, Expression.LessThan(value, Expression.Constant(ts1))), param)),
            CriterionModifier.Between => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.AndAlso(hasValue,
                    Expression.AndAlso(
                        Expression.GreaterThanOrEqual(value, Expression.Constant(ts1)),
                        Expression.LessThanOrEqual(value, Expression.Constant(ts2)))), param)),
            CriterionModifier.NotBetween => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.AndAlso(hasValue,
                    Expression.OrElse(
                        Expression.LessThan(value, Expression.Constant(ts1)),
                        Expression.GreaterThan(value, Expression.Constant(ts2)))), param)),
            _ => query,
        };
    }

    /// <summary>Apply a BoolCriterion to a queryable.</summary>
    public static IQueryable<T> ApplyBool<T>(IQueryable<T> query, BoolCriterion? criterion, Expression<Func<T, bool>> selector)
    {
        if (criterion == null) return query;
        var expected = criterion.Value;
        var param = selector.Parameters[0];
        var body = selector.Body;
        Expression pred = expected
            ? body
            : Expression.Not(body);
        return query.Where(Expression.Lambda<Func<T, bool>>(pred, param));
    }

    // -- Private string helpers --

    private static IQueryable<T> WhereStringEquals<T>(IQueryable<T> query, ParameterExpression param, Expression body, string val)
    {
        var pred = Expression.Equal(body, Expression.Constant(val, typeof(string)));
        return query.Where(Expression.Lambda<Func<T, bool>>(pred, param));
    }

    private static IQueryable<T> WhereStringNotEquals<T>(IQueryable<T> query, ParameterExpression param, Expression body, string val)
    {
        var pred = Expression.NotEqual(body, Expression.Constant(val, typeof(string)));
        return query.Where(Expression.Lambda<Func<T, bool>>(pred, param));
    }

    private static IQueryable<T> WhereStringContains<T>(IQueryable<T> query, ParameterExpression param, Expression body, string val)
    {
        // We can't easily use EF.Functions.ILike in expression trees, so we fall back to inline lambda
        // This is a workaround — we compile to a new expression that checks Contains
        var notNull = Expression.NotEqual(body, Expression.Constant(null, typeof(string)));
        // Use ToLower().Contains() as a portable case-insensitive search
        var toLower = Expression.Call(body, typeof(string).GetMethod("ToLower", Type.EmptyTypes)!);
        var containsMethod = typeof(string).GetMethod("Contains", new[] { typeof(string) })!;
        var contains = Expression.Call(toLower, containsMethod, Expression.Constant(val.ToLower()));
        var pred = Expression.AndAlso(notNull, contains);
        return query.Where(Expression.Lambda<Func<T, bool>>(pred, param));
    }

    private static IQueryable<T> WhereStringNotContains<T>(IQueryable<T> query, ParameterExpression param, Expression body, string val)
    {
        var isNull = Expression.Equal(body, Expression.Constant(null, typeof(string)));
        var toLower = Expression.Call(body, typeof(string).GetMethod("ToLower", Type.EmptyTypes)!);
        var containsMethod = typeof(string).GetMethod("Contains", new[] { typeof(string) })!;
        var notContains = Expression.Not(Expression.Call(toLower, containsMethod, Expression.Constant(val.ToLower())));
        var pred = Expression.OrElse(isNull, notContains);
        return query.Where(Expression.Lambda<Func<T, bool>>(pred, param));
    }

    private static IQueryable<T> WhereStringMatchesRegex<T>(IQueryable<T> query, ParameterExpression param, Expression body, string val)
    {
        var notNull = Expression.NotEqual(body, Expression.Constant(null, typeof(string)));
        var coalesced = Expression.Coalesce(body, Expression.Constant(string.Empty, typeof(string)));
        var isMatch = Expression.Call(
            typeof(Regex).GetMethod(nameof(Regex.IsMatch), new[] { typeof(string), typeof(string), typeof(RegexOptions) })!,
            coalesced,
            Expression.Constant(val),
            Expression.Constant(RegexOptions.IgnoreCase));
        var pred = Expression.AndAlso(notNull, isMatch);
        return query.Where(Expression.Lambda<Func<T, bool>>(pred, param));
    }

    private static IQueryable<T> WhereStringNotMatchesRegex<T>(IQueryable<T> query, ParameterExpression param, Expression body, string val)
    {
        var isNull = Expression.Equal(body, Expression.Constant(null, typeof(string)));
        var coalesced = Expression.Coalesce(body, Expression.Constant(string.Empty, typeof(string)));
        var isMatch = Expression.Call(
            typeof(Regex).GetMethod(nameof(Regex.IsMatch), new[] { typeof(string), typeof(string), typeof(RegexOptions) })!,
            coalesced,
            Expression.Constant(val),
            Expression.Constant(RegexOptions.IgnoreCase));
        var pred = Expression.OrElse(isNull, Expression.Not(isMatch));
        return query.Where(Expression.Lambda<Func<T, bool>>(pred, param));
    }

    private static IQueryable<T> WhereStringIsNull<T>(IQueryable<T> query, ParameterExpression param, Expression body)
    {
        var isNull = Expression.Equal(body, Expression.Constant(null, typeof(string)));
        var isEmpty = Expression.Equal(body, Expression.Constant(""));
        var pred = Expression.OrElse(isNull, isEmpty);
        return query.Where(Expression.Lambda<Func<T, bool>>(pred, param));
    }

    private static IQueryable<T> WhereStringNotNull<T>(IQueryable<T> query, ParameterExpression param, Expression body)
    {
        var notNull = Expression.NotEqual(body, Expression.Constant(null, typeof(string)));
        var notEmpty = Expression.NotEqual(body, Expression.Constant(""));
        var pred = Expression.AndAlso(notNull, notEmpty);
        return query.Where(Expression.Lambda<Func<T, bool>>(pred, param));
    }

    private static bool TryParseTimestamp(string? value, out DateTime timestamp)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            timestamp = default;
            return false;
        }

        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var parsed))
        {
            timestamp = default;
            return false;
        }

        timestamp = parsed.Kind switch
        {
            DateTimeKind.Utc => parsed,
            DateTimeKind.Local => parsed.ToUniversalTime(),
            _ => DateTime.SpecifyKind(parsed, DateTimeKind.Local).ToUniversalTime(),
        };
        return true;
    }

    private static Expression Any(Expression source, ParameterExpression itemParam, Expression predicate)
    {
        var anyMethod = typeof(Enumerable).GetMethods()
            .First(method => method.Name == nameof(Enumerable.Any) && method.GetParameters().Length == 2)
            .MakeGenericMethod(itemParam.Type);

        return Expression.Call(anyMethod, source, Expression.Lambda(predicate, itemParam));
    }

    private static Expression BuildNormalizedFilePathExpression(ParameterExpression fileParam)
    {
        var basename = Expression.Property(fileParam, nameof(BaseFileEntity.Basename));
        var parentFolder = Expression.Property(fileParam, nameof(BaseFileEntity.ParentFolder));
        var folderPath = Expression.Property(parentFolder, nameof(Folder.Path));
        var replaceMethod = typeof(string).GetMethod(nameof(string.Replace), [typeof(string), typeof(string)])!;
        var normalizedFolderPath = Expression.Call(folderPath, replaceMethod, Expression.Constant("\\"), Expression.Constant("/"));
        var combinedPath = Expression.Call(
            typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string), typeof(string)])!,
            normalizedFolderPath,
            Expression.Constant("/"),
            basename);

        return Expression.Condition(
            Expression.Equal(parentFolder, Expression.Constant(null, typeof(Folder))),
            basename,
            combinedPath);
    }

    private static string NormalizePathValue(string value) => value.Replace("\\", "/");
}
