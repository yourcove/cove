using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Repositories;

internal sealed record ReadScopeRootPlan<TEntity>(bool UseIgnoreQueryFilters, Expression<Func<TEntity, bool>>? Predicate)
    where TEntity : BaseEntity
{
    public IQueryable<TEntity> Apply(IQueryable<TEntity> query)
    {
        if (UseIgnoreQueryFilters)
        {
            query = query.IgnoreQueryFilters();
        }

        return Predicate is null ? query : query.Where(Predicate);
    }
}

internal static class ReadScopeListOptimization
{
    private static readonly MethodInfo EnumerableContainsMethod = typeof(Enumerable)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == nameof(Enumerable.Contains) && method.GetParameters().Length == 2);

    private static readonly MethodInfo StringContainsMethod = typeof(string)
        .GetMethod(nameof(string.Contains), [typeof(string)])!;

    private static readonly MethodInfo StringStartsWithMethod = typeof(string)
        .GetMethod(nameof(string.StartsWith), [typeof(string)])!;

    private static readonly MethodInfo StringEndsWithMethod = typeof(string)
        .GetMethod(nameof(string.EndsWith), [typeof(string)])!;

    private static readonly MethodInfo StringToLowerMethod = typeof(string)
        .GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;

    private static readonly MethodInfo RegexIsMatchMethod = typeof(Regex)
        .GetMethod(nameof(Regex.IsMatch), [typeof(string), typeof(string), typeof(RegexOptions)])!;

    public static async Task<ReadScopeRootPlan<TEntity>?> TryBuildPlanAsync<TEntity>(
        CoveContext db,
        string entityKind,
        bool hasDirectReadPermission,
        bool hasReadGrant,
        CancellationToken ct)
        where TEntity : BaseEntity
    {
        var principal = db.CurrentPrincipalForReadOptimization;
        if (principal is null || db.AuthorizationBypassedForReadOptimization || db.CurrentShareLinkIdForReadOptimization is not null)
        {
            return null;
        }

        if (!principal.ReadRestrictedEntityKinds.Contains(entityKind) || (!hasDirectReadPermission && !hasReadGrant))
        {
            return null;
        }

        var roleNames = principal.Roles.ToArray();
        if (roleNames.Length == 0)
        {
            return hasDirectReadPermission
                ? new ReadScopeRootPlan<TEntity>(true, null)
                : new ReadScopeRootPlan<TEntity>(true, _ => false);
        }

        var entityKindLower = entityKind.ToLowerInvariant();
        var roleIds = await db.Roles
            .AsNoTracking()
            .Where(role => roleNames.Contains(role.Name))
            .Select(role => role.Id)
            .ToArrayAsync(ct);

        if (roleIds.Length == 0)
        {
            return hasDirectReadPermission
                ? new ReadScopeRootPlan<TEntity>(true, null)
                : new ReadScopeRootPlan<TEntity>(true, _ => false);
        }

        var rules = await db.RoleContentRules
            .AsNoTracking()
            .Where(rule => roleIds.Contains(rule.RoleId)
                && rule.EntityKind.ToLower() == entityKindLower
                && (rule.AppliesTo.ToLower() == "read" || rule.AppliesTo.ToLower() == "all"))
            .Select(rule => new RuleRow(rule.Effect, rule.ScopeKind, rule.ScopeValue))
            .ToListAsync(ct);

        var overrides = await db.RoleEntityOverrides
            .AsNoTracking()
            .Where(overrideItem => roleIds.Contains(overrideItem.RoleId)
                && overrideItem.EntityKind.ToLower() == entityKindLower
                && (overrideItem.AppliesTo.ToLower() == "read" || overrideItem.AppliesTo.ToLower() == "all"))
            .Select(overrideItem => new OverrideRow(overrideItem.Effect, overrideItem.EntityId))
            .ToListAsync(ct);

        var allowOverrideIds = new HashSet<int>();
        var denyOverrideIds = new HashSet<int>();

        foreach (var overrideRow in overrides)
        {
            if (!int.TryParse(overrideRow.EntityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var entityId))
            {
                continue;
            }

            if (string.Equals(overrideRow.Effect, "deny", StringComparison.OrdinalIgnoreCase))
            {
                denyOverrideIds.Add(entityId);
                continue;
            }

            if (string.Equals(overrideRow.Effect, "allow", StringComparison.OrdinalIgnoreCase))
            {
                allowOverrideIds.Add(entityId);
            }
        }

        Expression<Func<TEntity, bool>>? allowRulePredicate = null;
        Expression<Func<TEntity, bool>>? denyRulePredicate = null;
        var hasUnsupportedAllowRule = false;
        var hasUnsupportedDenyRule = false;
        var hasAnyDenyRule = false;

        foreach (var rule in rules)
        {
            var isAllowRule = string.Equals(rule.Effect, "allow", StringComparison.OrdinalIgnoreCase);
            var compiledRule = TryCompileRule<TEntity>(db, entityKindLower, rule.ScopeKind, rule.ScopeValue);

            if (compiledRule is null)
            {
                if (isAllowRule)
                {
                    hasUnsupportedAllowRule = true;
                }
                else
                {
                    hasUnsupportedDenyRule = true;
                    hasAnyDenyRule = true;
                }

                continue;
            }

            if (isAllowRule)
            {
                allowRulePredicate = OrElse(allowRulePredicate, compiledRule);
                continue;
            }

            hasAnyDenyRule = true;
            denyRulePredicate = OrElse(denyRulePredicate, compiledRule);
        }

        if (hasDirectReadPermission)
        {
            if (hasUnsupportedDenyRule || (hasAnyDenyRule && hasUnsupportedAllowRule))
            {
                return null;
            }
        }
        else if (hasUnsupportedAllowRule)
        {
            return null;
        }

        var finalPredicate = BuildFinalPredicate<TEntity>(
            allowOverrideIds,
            denyOverrideIds,
            allowRulePredicate,
            denyRulePredicate,
            hasDirectReadPermission);

        return new ReadScopeRootPlan<TEntity>(true, finalPredicate);
    }

    private static Expression<Func<TEntity, bool>>? BuildFinalPredicate<TEntity>(
        IReadOnlyCollection<int> allowOverrideIds,
        IReadOnlyCollection<int> denyOverrideIds,
        Expression<Func<TEntity, bool>>? allowRulePredicate,
        Expression<Func<TEntity, bool>>? denyRulePredicate,
        bool hasDirectReadPermission)
        where TEntity : BaseEntity
    {
        var allowOverridePredicate = BuildIdSetPredicate<TEntity>(allowOverrideIds);
        var denyOverridePredicate = BuildIdSetPredicate<TEntity>(denyOverrideIds);

        if (hasDirectReadPermission && denyOverridePredicate is null && denyRulePredicate is null)
        {
            return null;
        }

        var parameter = Expression.Parameter(typeof(TEntity), "entity");
        Expression body;

        if (hasDirectReadPermission)
        {
            if (denyRulePredicate is null)
            {
                body = Expression.Constant(true);
            }
            else
            {
                var denyBody = ReplaceParameter(denyRulePredicate, parameter);
                body = allowRulePredicate is null
                    ? Expression.Not(denyBody)
                    : Expression.OrElse(Expression.Not(denyBody), ReplaceParameter(allowRulePredicate, parameter));
            }
        }
        else
        {
            body = allowRulePredicate is null
                ? Expression.Constant(false)
                : ReplaceParameter(allowRulePredicate, parameter);
        }

        if (allowOverridePredicate is not null)
        {
            body = Expression.OrElse(ReplaceParameter(allowOverridePredicate, parameter), body);
        }

        if (denyOverridePredicate is not null)
        {
            body = Expression.AndAlso(Expression.Not(ReplaceParameter(denyOverridePredicate, parameter)), body);
        }

        if (body is ConstantExpression constant && constant.Value is bool boolValue)
        {
            return boolValue ? null : _ => false;
        }

        return Expression.Lambda<Func<TEntity, bool>>(body, parameter);
    }

    private static Expression<Func<TEntity, bool>>? BuildIdSetPredicate<TEntity>(IReadOnlyCollection<int> entityIds)
        where TEntity : BaseEntity
    {
        if (entityIds.Count == 0)
        {
            return null;
        }

        var idSet = entityIds.ToArray();
        return entity => idSet.Contains(entity.Id);
    }

    private static Expression<Func<TEntity, bool>>? TryCompileRule<TEntity>(
        CoveContext db,
        string entityKind,
        string scopeKind,
        string scopeValue)
        where TEntity : BaseEntity
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(scopeValue) ? "{}" : scopeValue);
            return TryCompileRule<TEntity>(db, entityKind, scopeKind, document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Expression<Func<TEntity, bool>>? TryCompileRule<TEntity>(
        CoveContext db,
        string entityKind,
        string scopeKind,
        JsonElement scopeValue)
        where TEntity : BaseEntity
    {
        switch ((scopeKind ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "all":
                return _ => true;
            case "tag":
                return TryGetIntProperty(scopeValue, "tagId", out var tagId)
                    ? BuildCollectionContainsPredicate<TEntity>("TagIds", tagId)
                    : null;
            case "studio":
                return TryGetIntProperty(scopeValue, "studioId", out var studioId)
                    ? BuildScalarEqualsPredicate<TEntity>("StudioId", studioId)
                    : null;
            case "attribute":
                return TryCompileAttributeRule<TEntity>(scopeValue);
            case "expression":
                return TryCompileExpressionRule<TEntity>(db, entityKind, scopeValue);
            default:
                return null;
        }
    }

    private static Expression<Func<TEntity, bool>>? TryCompileExpressionRule<TEntity>(
        CoveContext db,
        string entityKind,
        JsonElement scopeValue)
        where TEntity : BaseEntity
    {
        var expressionOperator = TryGetStringProperty(scopeValue, "op")?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(expressionOperator))
        {
            return null;
        }

        if (expressionOperator == "not")
        {
            if (!TryGetPropertyIgnoreCase(scopeValue, "rule", out var childRule))
            {
                return null;
            }

            var compiledChildRule = TryCompileEmbeddedRule<TEntity>(db, entityKind, childRule);
            return compiledChildRule is null ? null : Not(compiledChildRule);
        }

        if (!TryGetPropertyIgnoreCase(scopeValue, "rules", out var rulesElement) || rulesElement.ValueKind != JsonValueKind.Array)
        {
            return expressionOperator == "and" ? _ => true : _ => false;
        }

        Expression<Func<TEntity, bool>>? combinedRule = null;
        foreach (var childRule in rulesElement.EnumerateArray())
        {
            var compiledChildRule = TryCompileEmbeddedRule<TEntity>(db, entityKind, childRule);
            if (compiledChildRule is null)
            {
                return null;
            }

            combinedRule = expressionOperator == "and"
                ? AndAlso(combinedRule, compiledChildRule)
                : OrElse(combinedRule, compiledChildRule);
        }

        return combinedRule ?? (expressionOperator == "and" ? _ => true : _ => false);
    }

    private static Expression<Func<TEntity, bool>>? TryCompileEmbeddedRule<TEntity>(
        CoveContext db,
        string entityKind,
        JsonElement rule)
        where TEntity : BaseEntity
    {
        var scopeKind = TryGetStringProperty(rule, "scopeKind") ?? TryGetStringProperty(rule, "scope_kind");
        if (string.IsNullOrWhiteSpace(scopeKind))
        {
            return null;
        }

        if (!TryGetPropertyIgnoreCase(rule, "scopeValue", out var nestedScopeValue)
            && !TryGetPropertyIgnoreCase(rule, "scope_value", out nestedScopeValue))
        {
            nestedScopeValue = default;
        }

        return TryCompileRule<TEntity>(db, entityKind, scopeKind, nestedScopeValue.ValueKind == JsonValueKind.Undefined ? default : nestedScopeValue);
    }

    private static Expression<Func<TEntity, bool>>? TryCompileAttributeRule<TEntity>(JsonElement scopeValue)
        where TEntity : BaseEntity
    {
        var path = (TryGetStringProperty(scopeValue, "path") ?? TryGetStringProperty(scopeValue, "field"))?.Trim();
        if (string.IsNullOrWhiteSpace(path) || path.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        if (!TryResolveProperty(typeof(TEntity), path, out var property))
        {
            return null;
        }

        var parameter = Expression.Parameter(typeof(TEntity), "entity");
        var propertyAccess = Expression.Property(parameter, property);

        if (TryGetPropertyIgnoreCase(scopeValue, "exists", out var existsElement) && TryConvertJsonValue(existsElement, typeof(bool), out var existsValue))
        {
            var existenceCheck = BuildExistenceCheck(propertyAccess);
            var expectedExists = (bool)existsValue!;
            var body = expectedExists
                ? existenceCheck ?? Expression.Constant(true)
                : existenceCheck is null ? Expression.Constant(false) : Expression.Not(existenceCheck);
            return Expression.Lambda<Func<TEntity, bool>>(body, parameter);
        }

        if (TryGetPropertyIgnoreCase(scopeValue, "equals", out var equalsElement))
        {
            return BuildAttributePredicate<TEntity>(parameter, propertyAccess, property.PropertyType, equalsElement, AttributeOperator.Equals);
        }

        if (TryGetPropertyIgnoreCase(scopeValue, "notEquals", out var notEqualsElement))
        {
            return BuildAttributePredicate<TEntity>(parameter, propertyAccess, property.PropertyType, notEqualsElement, AttributeOperator.NotEquals);
        }

        if (TryGetPropertyIgnoreCase(scopeValue, "contains", out var containsElement))
        {
            return BuildAttributePredicate<TEntity>(parameter, propertyAccess, property.PropertyType, containsElement, AttributeOperator.Contains);
        }

        if (TryGetPropertyIgnoreCase(scopeValue, "startsWith", out var startsWithElement))
        {
            return BuildAttributePredicate<TEntity>(parameter, propertyAccess, property.PropertyType, startsWithElement, AttributeOperator.StartsWith);
        }

        if (TryGetPropertyIgnoreCase(scopeValue, "endsWith", out var endsWithElement))
        {
            return BuildAttributePredicate<TEntity>(parameter, propertyAccess, property.PropertyType, endsWithElement, AttributeOperator.EndsWith);
        }

        if (TryGetPropertyIgnoreCase(scopeValue, "regex", out var regexElement))
        {
            return BuildAttributePredicate<TEntity>(parameter, propertyAccess, property.PropertyType, regexElement, AttributeOperator.Regex);
        }

        if (TryGetPropertyIgnoreCase(scopeValue, "in", out var inElement))
        {
            return BuildAttributePredicate<TEntity>(parameter, propertyAccess, property.PropertyType, inElement, AttributeOperator.In);
        }

        if (TryGetPropertyIgnoreCase(scopeValue, "gt", out var greaterThanElement))
        {
            return BuildAttributePredicate<TEntity>(parameter, propertyAccess, property.PropertyType, greaterThanElement, AttributeOperator.GreaterThan);
        }

        if (TryGetPropertyIgnoreCase(scopeValue, "gte", out var greaterThanOrEqualElement))
        {
            return BuildAttributePredicate<TEntity>(parameter, propertyAccess, property.PropertyType, greaterThanOrEqualElement, AttributeOperator.GreaterThanOrEqual);
        }

        if (TryGetPropertyIgnoreCase(scopeValue, "lt", out var lessThanElement))
        {
            return BuildAttributePredicate<TEntity>(parameter, propertyAccess, property.PropertyType, lessThanElement, AttributeOperator.LessThan);
        }

        if (TryGetPropertyIgnoreCase(scopeValue, "lte", out var lessThanOrEqualElement))
        {
            return BuildAttributePredicate<TEntity>(parameter, propertyAccess, property.PropertyType, lessThanOrEqualElement, AttributeOperator.LessThanOrEqual);
        }

        return null;
    }

    private static Expression<Func<TEntity, bool>>? BuildAttributePredicate<TEntity>(
        ParameterExpression parameter,
        Expression propertyAccess,
        Type propertyType,
        JsonElement valueElement,
        AttributeOperator attributeOperator)
        where TEntity : BaseEntity
    {
        var nonNullableType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        var nullGuard = BuildExistenceCheck(propertyAccess);
        var unwrappedPropertyAccess = Nullable.GetUnderlyingType(propertyType) is null
            ? propertyAccess
            : Expression.Property(propertyAccess, nameof(Nullable<int>.Value));

        if (propertyType == typeof(string))
        {
            var textValue = valueElement.ToString().ToLowerInvariant();
            var loweredProperty = Expression.Call(propertyAccess, StringToLowerMethod);
            Expression stringBody = attributeOperator switch
            {
                AttributeOperator.Equals => Expression.Equal(loweredProperty, Expression.Constant(textValue)),
                AttributeOperator.NotEquals => Expression.NotEqual(loweredProperty, Expression.Constant(textValue)),
                AttributeOperator.Contains => Expression.Call(loweredProperty, StringContainsMethod, Expression.Constant(textValue)),
                AttributeOperator.StartsWith => Expression.Call(loweredProperty, StringStartsWithMethod, Expression.Constant(textValue)),
                AttributeOperator.EndsWith => Expression.Call(loweredProperty, StringEndsWithMethod, Expression.Constant(textValue)),
                AttributeOperator.Regex => Expression.Call(
                    RegexIsMatchMethod,
                    propertyAccess,
                    Expression.Constant(valueElement.ToString()),
                    Expression.Constant(RegexOptions.IgnoreCase)),
                AttributeOperator.In => BuildStringInBody(loweredProperty, valueElement),
                _ => Expression.Empty(),
            };

            if (stringBody.NodeType == ExpressionType.Default)
            {
                return null;
            }

            if (nullGuard is not null)
            {
                stringBody = Expression.AndAlso(nullGuard, stringBody);
            }

            return Expression.Lambda<Func<TEntity, bool>>(stringBody, parameter);
        }

        var collectionElementType = GetEnumerableElementType(propertyType);
        if (collectionElementType is not null)
        {
            if (attributeOperator != AttributeOperator.Contains || !TryConvertJsonValue(valueElement, collectionElementType, out var collectionValue))
            {
                return null;
            }

            var containsMethod = GetEnumerableContainsMethod(collectionElementType);
            Expression containsBody = Expression.Call(containsMethod, propertyAccess, Expression.Constant(collectionValue, collectionElementType));
            if (nullGuard is not null)
            {
                containsBody = Expression.AndAlso(nullGuard, containsBody);
            }

            return Expression.Lambda<Func<TEntity, bool>>(containsBody, parameter);
        }

        if (attributeOperator == AttributeOperator.In)
        {
            if (valueElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var parsedValues = new List<object>();
            foreach (var arrayItem in valueElement.EnumerateArray())
            {
                if (TryConvertJsonValue(arrayItem, nonNullableType, out var parsedValue))
                {
                    parsedValues.Add(parsedValue!);
                }
            }

            if (parsedValues.Count == 0)
            {
                return null;
            }

            var typedValues = Array.CreateInstance(nonNullableType, parsedValues.Count);
            for (var index = 0; index < parsedValues.Count; index++)
            {
                typedValues.SetValue(parsedValues[index], index);
            }

            Expression inBody = Expression.Call(
                GetEnumerableContainsMethod(nonNullableType),
                Expression.Constant(typedValues, typedValues.GetType()),
                unwrappedPropertyAccess);

            if (nullGuard is not null)
            {
                inBody = Expression.AndAlso(nullGuard, inBody);
            }

            return Expression.Lambda<Func<TEntity, bool>>(inBody, parameter);
        }

        if (attributeOperator is AttributeOperator.GreaterThan or AttributeOperator.GreaterThanOrEqual or AttributeOperator.LessThan or AttributeOperator.LessThanOrEqual)
        {
            if (!SupportsOrderedComparison(nonNullableType) || !TryConvertJsonValue(valueElement, nonNullableType, out var comparisonValue))
            {
                return null;
            }

            var comparisonConstant = Expression.Constant(comparisonValue, nonNullableType);
            Expression comparisonBody = attributeOperator switch
            {
                AttributeOperator.GreaterThan => Expression.GreaterThan(unwrappedPropertyAccess, comparisonConstant),
                AttributeOperator.GreaterThanOrEqual => Expression.GreaterThanOrEqual(unwrappedPropertyAccess, comparisonConstant),
                AttributeOperator.LessThan => Expression.LessThan(unwrappedPropertyAccess, comparisonConstant),
                AttributeOperator.LessThanOrEqual => Expression.LessThanOrEqual(unwrappedPropertyAccess, comparisonConstant),
                _ => Expression.Empty(),
            };

            if (comparisonBody.NodeType == ExpressionType.Default)
            {
                return null;
            }

            if (nullGuard is not null)
            {
                comparisonBody = Expression.AndAlso(nullGuard, comparisonBody);
            }

            return Expression.Lambda<Func<TEntity, bool>>(comparisonBody, parameter);
        }

        if (!TryConvertJsonValue(valueElement, nonNullableType, out var scalarValue))
        {
            return null;
        }

        var scalarConstant = Expression.Constant(scalarValue, nonNullableType);
        Expression scalarBody = attributeOperator switch
        {
            AttributeOperator.Equals => Expression.Equal(unwrappedPropertyAccess, scalarConstant),
            AttributeOperator.NotEquals => Expression.NotEqual(unwrappedPropertyAccess, scalarConstant),
            _ => Expression.Empty(),
        };

        if (scalarBody.NodeType == ExpressionType.Default)
        {
            return null;
        }

        if (nullGuard is not null)
        {
            scalarBody = Expression.AndAlso(nullGuard, scalarBody);
        }

        return Expression.Lambda<Func<TEntity, bool>>(scalarBody, parameter);
    }

    private static Expression BuildStringInBody(Expression loweredProperty, JsonElement valueElement)
    {
        if (valueElement.ValueKind != JsonValueKind.Array)
        {
            return Expression.Empty();
        }

        var values = valueElement
            .EnumerateArray()
            .Select(item => item.ToString().ToLowerInvariant())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (values.Length == 0)
        {
            return Expression.Empty();
        }

        return Expression.Call(
            GetEnumerableContainsMethod(typeof(string)),
            Expression.Constant(values, typeof(string[])),
            loweredProperty);
    }

    private static Expression<Func<TEntity, bool>>? BuildCollectionContainsPredicate<TEntity>(string propertyName, int expectedValue)
        where TEntity : BaseEntity
    {
        if (!TryResolveProperty(typeof(TEntity), propertyName, out var property))
        {
            return null;
        }

        var collectionElementType = GetEnumerableElementType(property.PropertyType);
        if (collectionElementType != typeof(int))
        {
            return null;
        }

        var parameter = Expression.Parameter(typeof(TEntity), "entity");
        var propertyAccess = Expression.Property(parameter, property);
        Expression containsBody = Expression.Call(
            GetEnumerableContainsMethod(typeof(int)),
            propertyAccess,
            Expression.Constant(expectedValue));

        var nullGuard = BuildExistenceCheck(propertyAccess);
        if (nullGuard is not null)
        {
            containsBody = Expression.AndAlso(nullGuard, containsBody);
        }

        return Expression.Lambda<Func<TEntity, bool>>(containsBody, parameter);
    }

    private static Expression<Func<TEntity, bool>>? BuildScalarEqualsPredicate<TEntity>(string propertyName, int expectedValue)
        where TEntity : BaseEntity
    {
        if (!TryResolveProperty(typeof(TEntity), propertyName, out var property))
        {
            return null;
        }

        var parameter = Expression.Parameter(typeof(TEntity), "entity");
        var propertyAccess = Expression.Property(parameter, property);
        var nullGuard = BuildExistenceCheck(propertyAccess);
        var unwrappedPropertyAccess = Nullable.GetUnderlyingType(property.PropertyType) is null
            ? propertyAccess
            : Expression.Property(propertyAccess, nameof(Nullable<int>.Value));
        Expression equalsBody = Expression.Equal(unwrappedPropertyAccess, Expression.Constant(expectedValue));

        if (nullGuard is not null)
        {
            equalsBody = Expression.AndAlso(nullGuard, equalsBody);
        }

        return Expression.Lambda<Func<TEntity, bool>>(equalsBody, parameter);
    }

    private static Expression? BuildExistenceCheck(Expression propertyAccess)
    {
        if (!propertyAccess.Type.IsValueType)
        {
            return Expression.NotEqual(propertyAccess, Expression.Constant(null, propertyAccess.Type));
        }

        return Nullable.GetUnderlyingType(propertyAccess.Type) is null
            ? null
            : Expression.Property(propertyAccess, nameof(Nullable<int>.HasValue));
    }

    private static Type? GetEnumerableElementType(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        var enumerableInterface = type
            .GetInterfaces()
            .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        return enumerableInterface?.GetGenericArguments()[0];
    }

    private static MethodInfo GetEnumerableContainsMethod(Type elementType) => EnumerableContainsMethod.MakeGenericMethod(elementType);

    private static bool SupportsOrderedComparison(Type type)
    {
        var nonNullableType = Nullable.GetUnderlyingType(type) ?? type;
        return nonNullableType == typeof(int)
            || nonNullableType == typeof(long)
            || nonNullableType == typeof(float)
            || nonNullableType == typeof(double)
            || nonNullableType == typeof(decimal)
            || nonNullableType == typeof(DateOnly)
            || nonNullableType == typeof(DateTime);
    }

    private static bool TryConvertJsonValue(JsonElement element, Type targetType, out object? value)
    {
        var nonNullableType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        value = null;

        if (nonNullableType == typeof(string))
        {
            value = element.ToString();
            return true;
        }

        if (nonNullableType == typeof(bool))
        {
            if (element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False)
            {
                value = element.GetBoolean();
                return true;
            }

            return bool.TryParse(element.ToString(), out var boolValue) && AssignValue(boolValue, out value);
        }

        if (nonNullableType == typeof(int))
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var intValue))
            {
                value = intValue;
                return true;
            }

            return TryParseLooseNumber<int>(element.ToString(), NumberParser<int>.TryParse, out value);
        }

        if (nonNullableType == typeof(long))
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var longValue))
            {
                value = longValue;
                return true;
            }

            return TryParseLooseNumber<long>(element.ToString(), NumberParser<long>.TryParse, out value);
        }

        if (nonNullableType == typeof(float))
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out var floatValue))
            {
                value = floatValue;
                return true;
            }

            return TryParseLooseNumber<float>(element.ToString(), NumberParser<float>.TryParse, out value);
        }

        if (nonNullableType == typeof(double))
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var doubleValue))
            {
                value = doubleValue;
                return true;
            }

            return TryParseLooseNumber<double>(element.ToString(), NumberParser<double>.TryParse, out value);
        }

        if (nonNullableType == typeof(decimal))
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var decimalValue))
            {
                value = decimalValue;
                return true;
            }

            return TryParseLooseNumber<decimal>(element.ToString(), NumberParser<decimal>.TryParse, out value);
        }

        if (nonNullableType == typeof(DateOnly))
        {
            if (DateOnly.TryParse(element.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnlyValue))
            {
                value = dateOnlyValue;
                return true;
            }

            return false;
        }

        if (nonNullableType == typeof(DateTime))
        {
            if (DateTime.TryParse(element.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTimeValue))
            {
                value = dateTimeValue;
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool TryParseLooseNumber<T>(string rawValue, TryParseNumber<T> tryParse, out object? parsedValue)
    {
        parsedValue = null;
        var normalizedValue = NormalizeLooseNumber(rawValue);
        if (!tryParse(normalizedValue, out var numericValue))
        {
            return false;
        }

        parsedValue = numericValue;
        return true;
    }

    private static string NormalizeLooseNumber(string value) => Regex.Replace(value ?? string.Empty, "[^0-9.\\-]", string.Empty);

    private static bool AssignValue<T>(T value, out object? boxedValue)
    {
        boxedValue = value;
        return true;
    }

    private static bool TryResolveProperty(Type entityType, string propertyPath, out PropertyInfo property)
    {
        property = entityType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, propertyPath, StringComparison.OrdinalIgnoreCase))!;

        return property is not null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement propertyValue)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                propertyValue = property.Value;
                return true;
            }
        }

        propertyValue = default;
        return false;
    }

    private static bool TryGetIntProperty(JsonElement element, string propertyName, out int value)
    {
        value = default;
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var propertyValue)
            || !TryConvertJsonValue(propertyValue, typeof(int), out var parsedValue)
            || parsedValue is not int intValue)
        {
            return false;
        }

        value = intValue;
        return true;
    }

    private static string? TryGetStringProperty(JsonElement element, string propertyName)
        => TryGetPropertyIgnoreCase(element, propertyName, out var propertyValue) ? propertyValue.ToString() : null;

    private static Expression<Func<TEntity, bool>>? OrElse<TEntity>(Expression<Func<TEntity, bool>>? left, Expression<Func<TEntity, bool>> right)
        where TEntity : BaseEntity
    {
        if (left is null)
        {
            return right;
        }

        var parameter = Expression.Parameter(typeof(TEntity), "entity");
        return Expression.Lambda<Func<TEntity, bool>>(
            Expression.OrElse(ReplaceParameter(left, parameter), ReplaceParameter(right, parameter)),
            parameter);
    }

    private static Expression<Func<TEntity, bool>>? AndAlso<TEntity>(Expression<Func<TEntity, bool>>? left, Expression<Func<TEntity, bool>> right)
        where TEntity : BaseEntity
    {
        if (left is null)
        {
            return right;
        }

        var parameter = Expression.Parameter(typeof(TEntity), "entity");
        return Expression.Lambda<Func<TEntity, bool>>(
            Expression.AndAlso(ReplaceParameter(left, parameter), ReplaceParameter(right, parameter)),
            parameter);
    }

    private static Expression<Func<TEntity, bool>> Not<TEntity>(Expression<Func<TEntity, bool>> predicate)
        where TEntity : BaseEntity
    {
        var parameter = Expression.Parameter(typeof(TEntity), "entity");
        return Expression.Lambda<Func<TEntity, bool>>(Expression.Not(ReplaceParameter(predicate, parameter)), parameter);
    }

    private static Expression ReplaceParameter<TEntity>(Expression<Func<TEntity, bool>> expression, ParameterExpression parameter)
        where TEntity : BaseEntity
        => new ReplaceParameterVisitor(expression.Parameters[0], parameter).Visit(expression.Body)!;

    private sealed class ReplaceParameterVisitor(ParameterExpression source, ParameterExpression target) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            => node == source ? target : base.VisitParameter(node);
    }

    private sealed record RuleRow(string Effect, string ScopeKind, string ScopeValue);

    private sealed record OverrideRow(string Effect, string EntityId);

    private enum AttributeOperator
    {
        Equals,
        NotEquals,
        Contains,
        StartsWith,
        EndsWith,
        Regex,
        In,
        GreaterThan,
        GreaterThanOrEqual,
        LessThan,
        LessThanOrEqual,
    }

    private delegate bool TryParseNumber<T>(string rawValue, out T value);

    private static class NumberParser<T>
    {
        public static bool TryParse(string rawValue, out T value)
        {
            object? parsedValue = null;
            var parsed = typeof(T) == typeof(int)
                ? int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue) && AssignValue(intValue, out parsedValue)
                : typeof(T) == typeof(long)
                    ? long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue) && AssignValue(longValue, out parsedValue)
                    : typeof(T) == typeof(float)
                        ? float.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var floatValue) && AssignValue(floatValue, out parsedValue)
                        : typeof(T) == typeof(double)
                            ? double.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var doubleValue) && AssignValue(doubleValue, out parsedValue)
                            : typeof(T) == typeof(decimal)
                                ? decimal.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var decimalValue) && AssignValue(decimalValue, out parsedValue)
                                : false;

            if (!parsed || parsedValue is not T typedValue)
            {
                value = default!;
                return false;
            }

            value = typedValue;
            return true;
        }
    }
}