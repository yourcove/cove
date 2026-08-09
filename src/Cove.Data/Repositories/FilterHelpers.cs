using System.Linq.Expressions;
using System.Globalization;
using System.Text.RegularExpressions;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Core.Common;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Repositories;

/// <summary>
/// Generic filter helpers that work with any entity type.
/// Centralizes criterion-based filtering logic previously duplicated per-entity.
/// </summary>
public static class FilterHelpers
{
    public static IQueryable<T> ApplyBooleanKeywordSearch<T>(IQueryable<T> query, string? search, params Expression<Func<T, string?>>[] selectors)
    {
        var groups = ParseKeywordSearch(search);
        if (groups.Count == 0 || selectors.Length == 0) return query;

        var parameter = Expression.Parameter(typeof(T), "entity");
        Expression? anyGroup = null;

        foreach (var group in groups)
        {
            Expression? allTerms = null;
            foreach (var term in group)
            {
                var termExpression = BuildTermExpression(parameter, term.Value, selectors);
                if (term.Negated) termExpression = Expression.Not(termExpression);
                allTerms = allTerms == null ? termExpression : Expression.AndAlso(allTerms, termExpression);
            }

            if (allTerms != null)
                anyGroup = anyGroup == null ? allTerms : Expression.OrElse(anyGroup, allTerms);
        }

        return anyGroup == null ? query : query.Where(Expression.Lambda<Func<T, bool>>(anyGroup, parameter));
    }

    public static IQueryable<T> ApplyCustomFieldCriterion<T>(this IQueryable<T> query, CoveContext db, string entityType, CustomFieldCriterion? criterion)
        where T : BaseEntity
    {
        if (criterion is null || string.IsNullOrWhiteSpace(criterion.Key)) return query;

        var key = criterion.Key.Trim();
        var normalizedEntityType = entityType.Trim().ToLowerInvariant();
        var values = db.CustomFieldValues.Where(value => value.EntityType == normalizedEntityType && value.Definition!.Key == key);

        if (criterion.Modifier == CriterionModifier.IsNull)
        {
            return query.Where(entity => !values.Any(value => value.EntityId == entity.Id));
        }

        if (criterion.Modifier == CriterionModifier.NotNull)
        {
            return query.Where(entity => values.Any(value => value.EntityId == entity.Id));
        }

        if (string.IsNullOrWhiteSpace(criterion.Value)) return query;

        var type = CustomFieldTypes.Normalize(criterion.Type);
        if (CustomFieldTypes.IsNumberLike(type)) return ApplyNumberCustomField(query, values, criterion);
        if (CustomFieldTypes.IsBoolean(type)) return ApplyBooleanCustomField(query, values, criterion);
        if (CustomFieldTypes.IsDateLike(type)) return ApplyDateCustomField(query, values, criterion);
        if (CustomFieldTypes.IsTimestampLike(type)) return ApplyTimestampCustomField(query, values, criterion);
        if (CustomFieldTypes.IsReference(type)) return ApplyIntegerCustomField(query, values, criterion);

        var text = criterion.Value.Trim();
        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(entity => values.Any(value => value.EntityId == entity.Id && value.TextValue == text)),
            CriterionModifier.NotEquals => query.Where(entity => !values.Any(value => value.EntityId == entity.Id && value.TextValue == text)),
            CriterionModifier.Includes => query.Where(entity => values.Any(value => value.EntityId == entity.Id && value.TextValue != null && EF.Functions.ILike(value.TextValue, $"%{text}%"))),
            CriterionModifier.Excludes => query.Where(entity => !values.Any(value => value.EntityId == entity.Id && value.TextValue != null && EF.Functions.ILike(value.TextValue, $"%{text}%"))),
            _ => query,
        };
    }

    public static IQueryable<T> ApplyCustomFieldCriteria<T>(this IQueryable<T> query, CoveContext db, string entityType, CustomFieldCriterion? criterion, IEnumerable<CustomFieldCriterion>? criteria)
        where T : BaseEntity
    {
        query = query.ApplyCustomFieldCriterion(db, entityType, criterion);
        if (criteria is null) return query;

        foreach (var clause in criteria)
            query = query.ApplyCustomFieldCriterion(db, entityType, clause);

        return query;
    }

    public static IQueryable<T> ApplyCustomFieldSort<T>(this IQueryable<T> query, CoveContext db, string entityType, string? sort, bool desc)
        where T : BaseEntity
    {
        if (!TryParseCustomFieldSort(sort, out var key, out var type))
            return query;

        var normalizedEntityType = entityType.Trim().ToLowerInvariant();
        var values = db.CustomFieldValues.Where(value => value.EntityType == normalizedEntityType && value.Definition!.Key == key);
        if (CustomFieldTypes.IsNumberLike(type)) return SortByCustomField(query, values, value => value.NumberValue, desc);
        if (CustomFieldTypes.IsBoolean(type)) return SortByCustomField(query, values, value => value.BoolValue, desc);
        if (CustomFieldTypes.IsDateLike(type)) return SortByCustomField(query, values, value => value.DateValue, desc);
        if (CustomFieldTypes.IsTimestampLike(type)) return SortByCustomField(query, values, value => value.TimestampValue, desc);
        if (CustomFieldTypes.IsReference(type)) return SortByCustomField(query, values, value => value.IntegerValue, desc);
        return SortByCustomField(query, values, value => value.TextValue, desc);
    }

    public static bool TryParseCustomFieldSort(string? sort, out string key, out string type)
    {
        key = string.Empty;
        type = CustomFieldTypes.Text;
        if (string.IsNullOrWhiteSpace(sort)) return false;

        var parts = sort.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !string.Equals(parts[0], "custom", StringComparison.OrdinalIgnoreCase)) return false;

        if (parts.Length >= 3)
        {
            type = CustomFieldTypes.Normalize(parts[1]);
            key = parts[2];
        }
        else
        {
            key = parts[1];
        }

        return !string.IsNullOrWhiteSpace(key);
    }

    private static IQueryable<T> SortByCustomField<T, TValue>(IQueryable<T> query, IQueryable<CustomFieldValue> values, Expression<Func<CustomFieldValue, TValue?>> valueSelector, bool desc)
        where T : BaseEntity
    {
        var sorted = query.Select(entity => new
        {
            Entity = entity,
            Value = values.Where(value => value.EntityId == entity.Id).OrderBy(value => value.Position).Select(valueSelector).FirstOrDefault(),
        });

        return desc
            ? sorted.OrderBy(item => item.Value == null).ThenByDescending(item => item.Value).ThenByDescending(item => item.Entity.Id).Select(item => item.Entity)
            : sorted.OrderBy(item => item.Value == null).ThenBy(item => item.Value).ThenBy(item => item.Entity.Id).Select(item => item.Entity);
    }

    private static IQueryable<T> ApplyNumberCustomField<T>(IQueryable<T> query, IQueryable<CustomFieldValue> values, CustomFieldCriterion criterion)
        where T : BaseEntity
    {
        if (!decimal.TryParse(criterion.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)) return query;
        var number2 = decimal.TryParse(criterion.Value2, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedNumber2) ? parsedNumber2 : number;
        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(entity => values.Any(value => value.EntityId == entity.Id && value.NumberValue == number)),
            CriterionModifier.NotEquals => query.Where(entity => !values.Any(value => value.EntityId == entity.Id && value.NumberValue == number)),
            CriterionModifier.GreaterThan => query.Where(entity => values.Any(value => value.EntityId == entity.Id && value.NumberValue > number)),
            CriterionModifier.LessThan => query.Where(entity => values.Any(value => value.EntityId == entity.Id && value.NumberValue < number)),
            CriterionModifier.Between => query.Where(entity => values.Any(value => value.EntityId == entity.Id && value.NumberValue >= number && value.NumberValue <= number2)),
            CriterionModifier.NotBetween => query.Where(entity => !values.Any(value => value.EntityId == entity.Id && value.NumberValue >= number && value.NumberValue <= number2)),
            _ => query,
        };
    }

    private static IQueryable<T> ApplyIntegerCustomField<T>(IQueryable<T> query, IQueryable<CustomFieldValue> values, CustomFieldCriterion criterion)
        where T : BaseEntity
    {
        if (!int.TryParse(criterion.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) return query;
        return criterion.Modifier switch
        {
            CriterionModifier.Equals or CriterionModifier.Includes => query.Where(entity => values.Any(value => value.EntityId == entity.Id && value.IntegerValue == number)),
            CriterionModifier.NotEquals or CriterionModifier.Excludes => query.Where(entity => !values.Any(value => value.EntityId == entity.Id && value.IntegerValue == number)),
            _ => query,
        };
    }

    private static IQueryable<T> ApplyBooleanCustomField<T>(IQueryable<T> query, IQueryable<CustomFieldValue> values, CustomFieldCriterion criterion)
        where T : BaseEntity
    {
        if (!bool.TryParse(criterion.Value, out var boolValue)) return query;
        return criterion.Modifier switch
        {
            CriterionModifier.NotEquals or CriterionModifier.Excludes => query.Where(entity => !values.Any(value => value.EntityId == entity.Id && value.BoolValue == boolValue)),
            _ => query.Where(entity => values.Any(value => value.EntityId == entity.Id && value.BoolValue == boolValue)),
        };
    }

    private static IQueryable<T> ApplyDateCustomField<T>(IQueryable<T> query, IQueryable<CustomFieldValue> values, CustomFieldCriterion criterion)
        where T : BaseEntity
    {
        if (!DateOnly.TryParse(criterion.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return query;
        var date2 = DateOnly.TryParse(criterion.Value2, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate2) ? parsedDate2 : date;
        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(entity => values.Any(value => value.EntityId == entity.Id && value.DateValue == date)),
            CriterionModifier.NotEquals => query.Where(entity => !values.Any(value => value.EntityId == entity.Id && value.DateValue == date)),
            CriterionModifier.GreaterThan => query.Where(entity => values.Any(value => value.EntityId == entity.Id && value.DateValue > date)),
            CriterionModifier.LessThan => query.Where(entity => values.Any(value => value.EntityId == entity.Id && value.DateValue < date)),
            CriterionModifier.Between => query.Where(entity => values.Any(value => value.EntityId == entity.Id && value.DateValue >= date && value.DateValue <= date2)),
            CriterionModifier.NotBetween => query.Where(entity => !values.Any(value => value.EntityId == entity.Id && value.DateValue >= date && value.DateValue <= date2)),
            _ => query,
        };
    }

    private static IQueryable<T> ApplyTimestampCustomField<T>(IQueryable<T> query, IQueryable<CustomFieldValue> values, CustomFieldCriterion criterion)
        where T : BaseEntity
    {
        if (!DateTime.TryParse(criterion.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)) return query;
        var timestamp2 = DateTime.TryParse(criterion.Value2, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedTimestamp2) ? parsedTimestamp2 : timestamp;
        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(entity => values.Any(value => value.EntityId == entity.Id && value.TimestampValue == timestamp)),
            CriterionModifier.NotEquals => query.Where(entity => !values.Any(value => value.EntityId == entity.Id && value.TimestampValue == timestamp)),
            CriterionModifier.GreaterThan => query.Where(entity => values.Any(value => value.EntityId == entity.Id && value.TimestampValue > timestamp)),
            CriterionModifier.LessThan => query.Where(entity => values.Any(value => value.EntityId == entity.Id && value.TimestampValue < timestamp)),
            CriterionModifier.Between => query.Where(entity => values.Any(value => value.EntityId == entity.Id && value.TimestampValue >= timestamp && value.TimestampValue <= timestamp2)),
            CriterionModifier.NotBetween => query.Where(entity => !values.Any(value => value.EntityId == entity.Id && value.TimestampValue >= timestamp && value.TimestampValue <= timestamp2)),
            _ => query,
        };
    }


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

    public static IQueryable<T> ApplyLong<T>(IQueryable<T> query, IntCriterion? criterion, Expression<Func<T, long>> selector)
    {
        if (criterion == null) return query;
        var val = (long)criterion.Value;
        var val2 = (long)(criterion.Value2 ?? criterion.Value);
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

    /// <summary>Apply a resolution bucket criterion using a max-dimension selector.</summary>
    public static IQueryable<T> ApplyResolution<T>(IQueryable<T> query, IntCriterion? criterion, Expression<Func<T, int>> selector)
    {
        if (criterion == null) return query;
        if (!ResolutionBuckets.TryGetBounds(criterion.Value, out var minInclusive, out var maxInclusive)) return query;

        var param = selector.Parameters[0];
        var body = selector.Body;

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.AndAlso(
                    Expression.GreaterThanOrEqual(body, Expression.Constant(minInclusive)),
                    Expression.LessThanOrEqual(body, Expression.Constant(maxInclusive))), param)),
            CriterionModifier.NotEquals => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.OrElse(
                    Expression.LessThan(body, Expression.Constant(minInclusive)),
                    Expression.GreaterThan(body, Expression.Constant(maxInclusive))), param)),
            CriterionModifier.GreaterThan => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.GreaterThan(body, Expression.Constant(maxInclusive)), param)),
            CriterionModifier.LessThan => query.Where(Expression.Lambda<Func<T, bool>>(
                Expression.LessThan(body, Expression.Constant(minInclusive)), param)),
            _ => query,
        };
    }

    /// <summary>Apply a MultiIdCriterion to a queryable.</summary>
    public static IQueryable<T> ApplyMultiId<T>(
        IQueryable<T> query,
        MultiIdCriterion? criterion,
        Expression<Func<T, IEnumerable<int>>> idsSelector,
        IReadOnlyList<int[]>? valueGroups = null,
        IReadOnlyList<int[]>? requiredIdGroups = null)
        => MultiIdCriterionQueryHelper.Apply(query, criterion, idsSelector, valueGroups, requiredIdGroups);

    /// <summary>Apply a studio (single FK) MultiIdCriterion.</summary>
    public static IQueryable<T> ApplyStudioCriterion<T>(IQueryable<T> query, MultiIdCriterion? criterion, Expression<Func<T, int?>> studioIdSelector, IReadOnlyList<int[]>? valueGroups = null, IReadOnlyList<int[]>? requiredIdGroups = null)
    {
        if (criterion?.Modifier == CriterionModifier.IsNull || criterion?.Modifier == CriterionModifier.NotNull)
        {
            var nullParam = studioIdSelector.Parameters[0];
            var nullBody = studioIdSelector.Body;
            var hasStudioValue = Expression.Property(nullBody, "HasValue");
            Expression nullPredicate = criterion.Modifier == CriterionModifier.IsNull ? Expression.Not(hasStudioValue) : hasStudioValue;
            query = query.Where(Expression.Lambda<Func<T, bool>>(nullPredicate, nullParam));
        }
        else if (criterion == null || (criterion.Value.Count == 0 && (criterion.Excludes == null || criterion.Excludes.Count == 0) && (criterion.RequiredIds == null || criterion.RequiredIds.Count == 0) && requiredIdGroups is not { Count: > 0 }))
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

        var criterionIds = criterion.Value.Where(id => id > 0).Distinct().ToArray();
        if (valueGroups is { Count: > 0 }
            && criterion.Modifier is CriterionModifier.IncludesAll or CriterionModifier.ExcludesAll)
        {
            Expression? allGroupsMatched = null;
            foreach (var group in valueGroups)
            {
                var groupIds = group.Where(id => id > 0).Distinct().ToArray();
                if (groupIds.Length == 0) continue;
                var groupContains = Expression.Call(null, containsMethod, Expression.Constant(groupIds), value);
                var groupMatched = Expression.AndAlso(hasValue, groupContains);
                allGroupsMatched = allGroupsMatched == null
                    ? groupMatched
                    : Expression.AndAlso(allGroupsMatched, groupMatched);
            }

            if (allGroupsMatched != null)
            {
                predicate = criterion.Modifier == CriterionModifier.IncludesAll
                    ? allGroupsMatched
                    : Expression.Not(allGroupsMatched);
            }
        }
        else if (criterionIds.Length > 0)
        {
            var idsConst = Expression.Constant(criterionIds);
            var contains = Expression.Call(null, containsMethod, idsConst, value);

            predicate = criterion.Modifier switch
            {
                CriterionModifier.Includes => Expression.AndAlso(hasValue, contains),
                CriterionModifier.Excludes => Expression.OrElse(Expression.Not(hasValue), Expression.Not(contains)),
                CriterionModifier.IncludesAll when criterionIds.Length == 1 => Expression.AndAlso(hasValue, contains),
                CriterionModifier.IncludesAll => Expression.Constant(false),
                CriterionModifier.ExcludesAll when criterionIds.Length == 1 => Expression.OrElse(Expression.Not(hasValue), Expression.Not(contains)),
                CriterionModifier.ExcludesAll => null,
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

        if (criterion.RequiredIds is { Count: > 0 })
        {
            var requiredIds = criterion.RequiredIds.Where(id => id > 0).Distinct().ToArray();
            Expression requiredPredicate = requiredIds.Length switch
            {
                0 => Expression.Constant(true),
                1 => Expression.AndAlso(
                    hasValue,
                    Expression.Equal(value, Expression.Constant(requiredIds[0]))),
                _ => Expression.Constant(false),
            };
            predicate = predicate == null ? requiredPredicate : Expression.AndAlso(predicate, requiredPredicate);
        }

        if (requiredIdGroups is { Count: > 0 })
        {
            foreach (var group in requiredIdGroups)
            {
                var groupIds = group.Where(id => id > 0).Distinct().ToArray();
                if (groupIds.Length == 0) continue;
                var groupContains = Expression.Call(null, containsMethod, Expression.Constant(groupIds), value);
                var groupPredicate = Expression.AndAlso(hasValue, groupContains);
                predicate = predicate == null ? groupPredicate : Expression.AndAlso(predicate, groupPredicate);
            }
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
        var folder = NormalizeFolderPathValue(criterion.Value);
        var pathComparisonIgnoresCase = FilesystemPaths.PathComparison == StringComparison.OrdinalIgnoreCase;
        Expression comparablePath = pathComparisonIgnoresCase
            ? Expression.Call(fullPathOrEmpty, toLower)
            : fullPathOrEmpty;
        var comparableFolder = pathComparisonIgnoresCase ? folder.ToLowerInvariant() : folder;
        var folderEquals = Expression.Equal(comparablePath, Expression.Constant(comparableFolder));
        var startsWithMethod = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
        var folderPrefix = folder.EndsWith('/') ? folder : folder + "/";
        var folderDescendant = Expression.Call(
            comparablePath,
            startsWithMethod,
            Expression.Constant(pathComparisonIgnoresCase ? folderPrefix.ToLowerInvariant() : folderPrefix));
        var underPath = Expression.OrElse(folderEquals, folderDescendant);

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
            CriterionModifier.UnderPath => Any(filesSelector.Body, fileParam, underPath),
            CriterionModifier.NotUnderPath => Expression.Not(Any(filesSelector.Body, fileParam, underPath)),
            CriterionModifier.IsNull => Expression.Not(Any(filesSelector.Body, fileParam, notEmpty)),
            CriterionModifier.NotNull => Any(filesSelector.Body, fileParam, notEmpty),
            _ => null,
        };

        return predicate == null
            ? query
            : query.Where(Expression.Lambda<Func<T, bool>>(predicate, entityParam));
    }

    /// <summary>Apply a provider-endpoint criterion to an entity's remote ID endpoints.</summary>
    public static IQueryable<T> ApplyRemoteIdEndpoint<T>(
        IQueryable<T> query,
        StringCriterion? criterion,
        Expression<Func<T, IEnumerable<string>>> endpointsSelector)
    {
        var value = criterion?.Value?.Trim();
        if (criterion == null) return query;

        var entityParam = endpointsSelector.Parameters[0];
        var endpointParam = Expression.Parameter(typeof(string), "endpoint");
        var endpointOrEmpty = Expression.Coalesce(endpointParam, Expression.Constant(string.Empty));
        var hasAnyEndpoint = Any(
            endpointsSelector.Body,
            endpointParam,
            Expression.NotEqual(endpointOrEmpty, Expression.Constant(string.Empty)));
        if (string.IsNullOrWhiteSpace(value))
        {
            var globalNullPredicate = criterion.Modifier switch
            {
                CriterionModifier.IsNull => Expression.Not(hasAnyEndpoint),
                CriterionModifier.NotNull => hasAnyEndpoint,
                _ => null,
            };
            return globalNullPredicate == null
                ? query
                : query.Where(Expression.Lambda<Func<T, bool>>(globalNullPredicate, entityParam));
        }

        var toLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;
        var normalizedEndpoint = Expression.Call(endpointOrEmpty, toLower);
        var normalizedValue = value.ToLowerInvariant();
        var equals = Expression.Equal(normalizedEndpoint, Expression.Constant(normalizedValue));
        var contains = Expression.Call(
            normalizedEndpoint,
            typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!,
            Expression.Constant(normalizedValue));
        var regexMatch = Expression.Call(
            typeof(Regex).GetMethod(nameof(Regex.IsMatch), [typeof(string), typeof(string)])!,
            endpointOrEmpty,
            Expression.Constant($"(?i){value}"));

        var anyEquals = Any(endpointsSelector.Body, endpointParam, equals);
        Expression? predicate = criterion.Modifier switch
        {
            CriterionModifier.Equals => anyEquals,
            CriterionModifier.NotEquals => Expression.Not(anyEquals),
            CriterionModifier.Includes => Any(endpointsSelector.Body, endpointParam, contains),
            CriterionModifier.Excludes => Expression.Not(Any(endpointsSelector.Body, endpointParam, contains)),
            CriterionModifier.MatchesRegex => Any(endpointsSelector.Body, endpointParam, regexMatch),
            CriterionModifier.NotMatchesRegex => Expression.Not(Any(endpointsSelector.Body, endpointParam, regexMatch)),
            CriterionModifier.IsNull => Expression.Not(anyEquals),
            CriterionModifier.NotNull => anyEquals,
            _ => null,
        };

        return predicate == null
            ? query
            : query.Where(Expression.Lambda<Func<T, bool>>(predicate, entityParam));
    }

    public static IQueryable<T> ApplyRemoteId<T, TRemoteId>(
        IQueryable<T> query,
        StringCriterion? endpointCriterion,
        StringCriterion? valueCriterion,
        Expression<Func<T, IEnumerable<TRemoteId>>> remoteIdsSelector,
        Expression<Func<TRemoteId, string>> endpointSelector,
        Expression<Func<TRemoteId, string>> valueSelector)
    {
        if (valueCriterion == null)
            return ApplyRemoteIdEndpoint(query, endpointCriterion, Project(remoteIdsSelector, endpointSelector));

        var endpoint = endpointCriterion?.Value?.Trim();
        var value = valueCriterion.Value?.Trim() ?? string.Empty;
        var entityParam = remoteIdsSelector.Parameters[0];
        var remoteIdParam = Expression.Parameter(typeof(TRemoteId), "remoteId");
        var endpointBody = new ParameterReplaceVisitor(endpointSelector.Parameters[0], remoteIdParam).Visit(endpointSelector.Body)!;
        var valueBody = new ParameterReplaceVisitor(valueSelector.Parameters[0], remoteIdParam).Visit(valueSelector.Body)!;
        var endpointMatches = string.IsNullOrWhiteSpace(endpoint)
            ? Expression.Constant(true)
            : CaseInsensitiveEquals(endpointBody, endpoint);
        var hasScopedRemoteId = Any(remoteIdsSelector.Body, remoteIdParam, endpointMatches);

        if (valueCriterion.Modifier is CriterionModifier.IsNull or CriterionModifier.NotNull)
        {
            var nullPredicate = valueCriterion.Modifier == CriterionModifier.IsNull
                ? Expression.Not(hasScopedRemoteId)
                : hasScopedRemoteId;
            return query.Where(Expression.Lambda<Func<T, bool>>(nullPredicate, entityParam));
        }

        if (string.IsNullOrWhiteSpace(value)) return query;

        var valueMatches = BuildCaseInsensitiveStringMatch(valueBody, value, valueCriterion.Modifier);
        var pairMatches = Expression.AndAlso(endpointMatches, valueMatches);
        var anyPairMatches = Any(remoteIdsSelector.Body, remoteIdParam, pairMatches);
        var predicate = valueCriterion.Modifier is CriterionModifier.NotEquals
            or CriterionModifier.Excludes
            or CriterionModifier.NotMatchesRegex
            ? Expression.Not(anyPairMatches)
            : anyPairMatches;
        return query.Where(Expression.Lambda<Func<T, bool>>(predicate, entityParam));
    }

    private static Expression<Func<T, IEnumerable<string>>> Project<T, TItem>(
        Expression<Func<T, IEnumerable<TItem>>> source,
        Expression<Func<TItem, string>> selector)
    {
        var select = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Select),
            [typeof(TItem), typeof(string)],
            source.Body,
            selector);
        return Expression.Lambda<Func<T, IEnumerable<string>>>(select, source.Parameters);
    }

    private static Expression CaseInsensitiveEquals(Expression body, string value)
    {
        var coalesced = Expression.Coalesce(body, Expression.Constant(string.Empty));
        return Expression.Equal(
            Expression.Call(coalesced, typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!),
            Expression.Constant(value.ToLowerInvariant()));
    }

    private static Expression BuildCaseInsensitiveStringMatch(Expression body, string value, CriterionModifier modifier)
    {
        var coalesced = Expression.Coalesce(body, Expression.Constant(string.Empty));
        var lowered = Expression.Call(coalesced, typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!);
        return modifier switch
        {
            CriterionModifier.Equals or CriterionModifier.NotEquals => Expression.Equal(lowered, Expression.Constant(value.ToLowerInvariant())),
            CriterionModifier.Includes or CriterionModifier.Excludes => Expression.Call(
                lowered,
                typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!,
                Expression.Constant(value.ToLowerInvariant())),
            CriterionModifier.MatchesRegex or CriterionModifier.NotMatchesRegex => Expression.Call(
                typeof(Regex).GetMethod(nameof(Regex.IsMatch), [typeof(string), typeof(string)])!,
                coalesced,
                Expression.Constant($"(?i){value}")),
            _ => Expression.Constant(false),
        };
    }

    /// <summary>Apply a DateCriterion to a DateOnly? property.</summary>
    public static IQueryable<T> ApplyDate<T>(IQueryable<T> query, DateCriterion? criterion, Expression<Func<T, DateOnly?>> selector)
    {
        if (criterion == null) return query;

        var param = selector.Parameters[0];
        var body = selector.Body;
        // Get the .Value property of the Nullable<DateOnly>
        var value = Expression.Property(body, "Value");
        var hasValue = Expression.Property(body, "HasValue");

        // Null checks carry no date value, so they must be handled before parsing.
        if (criterion.Modifier == CriterionModifier.IsNull)
            return query.Where(Expression.Lambda<Func<T, bool>>(Expression.Not(hasValue), param));
        if (criterion.Modifier == CriterionModifier.NotNull)
            return query.Where(Expression.Lambda<Func<T, bool>>(hasValue, param));

        if (!DateOnly.TryParse(criterion.Value, out var d1)) return query;
        DateOnly.TryParse(criterion.Value2, out var d2);

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

    private static Expression BuildTermExpression<T>(ParameterExpression parameter, string value, Expression<Func<T, string?>>[] selectors)
    {
        var normalizedValue = Expression.Constant(value.ToLower());
        var stringContains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
        var stringToLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;
        Expression? anyField = null;

        foreach (var selector in selectors)
        {
            var body = new ParameterReplaceVisitor(selector.Parameters[0], parameter).Visit(selector.Body)!;
            var coalesced = Expression.Coalesce(body, Expression.Constant(string.Empty));
            var lowered = Expression.Call(coalesced, stringToLower);
            var contains = Expression.Call(lowered, stringContains, normalizedValue);
            anyField = anyField == null ? contains : Expression.OrElse(anyField, contains);
        }

        return anyField ?? Expression.Constant(false);
    }

    private static List<List<SearchTerm>> ParseKeywordSearch(string? search)
    {
        var tokens = TokenizeSearch(search);
        var groups = new List<List<SearchTerm>>();
        var current = new List<SearchTerm>();
        var negateNext = false;

        foreach (var token in tokens)
        {
            if (token.Equals("OR", StringComparison.OrdinalIgnoreCase))
            {
                if (current.Count > 0)
                {
                    groups.Add(current);
                    current = [];
                }
                negateNext = false;
                continue;
            }

            if (token.Equals("AND", StringComparison.OrdinalIgnoreCase))
                continue;

            if (token.Equals("NOT", StringComparison.OrdinalIgnoreCase))
            {
                negateNext = true;
                continue;
            }

            var value = token;
            var negated = negateNext;
            negateNext = false;
            if (value.StartsWith("-", StringComparison.Ordinal) && value.Length > 1)
            {
                negated = true;
                value = value[1..];
            }

            if (!string.IsNullOrWhiteSpace(value))
                current.Add(new SearchTerm(value, negated));
        }

        if (current.Count > 0) groups.Add(current);
        return groups;
    }

    private static List<string> TokenizeSearch(string? search)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(search)) return tokens;

        var current = new List<char>();
        var inQuote = false;
        for (var i = 0; i < search.Length; i++)
        {
            var ch = search[i];
            if (ch == '"')
            {
                if (inQuote)
                {
                    if (current.Count > 0)
                    {
                        tokens.Add(new string(current.ToArray()));
                        current.Clear();
                    }
                    inQuote = false;
                }
                else
                {
                    if (current.Count > 0)
                    {
                        tokens.Add(new string(current.ToArray()));
                        current.Clear();
                    }
                    inQuote = true;
                }
                continue;
            }

            if (!inQuote && char.IsWhiteSpace(ch))
            {
                if (current.Count > 0)
                {
                    tokens.Add(new string(current.ToArray()));
                    current.Clear();
                }
                continue;
            }

            current.Add(ch);
        }

        if (current.Count > 0)
            tokens.Add(new string(current.ToArray()));

        return tokens;
    }

    private sealed record SearchTerm(string Value, bool Negated);

    private sealed class ParameterReplaceVisitor(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }

    private static string NormalizePathValue(string value) => value.Replace("\\", "/");

    private static string NormalizeFolderPathValue(string value)
    {
        var normalized = NormalizePathValue(value).Trim();
        while (normalized.Length > 1 && normalized.EndsWith('/') && !(normalized.Length == 3 && normalized[1] == ':'))
            normalized = normalized[..^1];
        return normalized;
    }
}
