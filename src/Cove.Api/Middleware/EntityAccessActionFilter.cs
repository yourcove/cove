using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using IAuthorizationService = Cove.Core.Auth.IAuthorizationService;

namespace Cove.Api.Middleware;

public sealed class EntityAccessActionFilter : IAsyncActionFilter
{
    private readonly ICurrentPrincipalAccessor _principalAccessor;
    private readonly IAuthorizationService _authz;
    private readonly IAuditService _audit;
    private readonly CoveContext _db;

    public EntityAccessActionFilter(
        ICurrentPrincipalAccessor principalAccessor,
        IAuthorizationService authz,
        IAuditService audit,
        CoveContext db)
    {
        _principalAccessor = principalAccessor;
        _authz = authz;
        _audit = audit;
        _db = db;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor cad)
        {
            await next();
            return;
        }

        var requirements = cad.MethodInfo.GetCustomAttributes(true).OfType<RequiresEntityAccessAttribute>().ToList();
        if (requirements.Count == 0)
            requirements = cad.ControllerTypeInfo.GetCustomAttributes(true).OfType<RequiresEntityAccessAttribute>().ToList();

        var unscopedRequirements = cad.MethodInfo.GetCustomAttributes(true).OfType<RequiresUnscopedEntityAccessAttribute>().ToList();
        if (unscopedRequirements.Count == 0)
            unscopedRequirements = cad.ControllerTypeInfo.GetCustomAttributes(true).OfType<RequiresUnscopedEntityAccessAttribute>().ToList();

        if (requirements.Count == 0 && unscopedRequirements.Count == 0)
        {
            await next();
            return;
        }

        var principal = _principalAccessor.Current;
        if (principal is null || principal.Kind == PrincipalKind.Anonymous)
        {
            context.Result = new ObjectResult(new
            {
                code = "UNAUTHORIZED",
                message = "Authentication required.",
            })
            { StatusCode = StatusCodes.Status401Unauthorized };
            return;
        }

        if (!principal.Has(Permissions.All))
        {
            foreach (var requirement in unscopedRequirements)
            {
                var selection = !string.IsNullOrWhiteSpace(requirement.ActionArgumentName)
                    && context.ActionArguments.TryGetValue(requirement.ActionArgumentName, out var argument)
                        ? ExtractValues(argument, requirement.PropertyName).ToList()
                        : [];
                if (!string.IsNullOrWhiteSpace(requirement.ActionArgumentName) && selection.Count > 0)
                    continue;

                var roleNames = principal.Roles.ToArray();
                var hasDenies = string.Equals(requirement.AppliesTo, "read", StringComparison.OrdinalIgnoreCase)
                    && principal.ReadRestrictedEntityKinds.Count > 0
                    || await _db.RoleContentRules.AsNoTracking().AnyAsync(rule =>
                        rule.Role != null && roleNames.Contains(rule.Role.Name)
                        && rule.Effect == "deny"
                        && (rule.AppliesTo == requirement.AppliesTo || rule.AppliesTo == "all"),
                        context.HttpContext.RequestAborted)
                    || await _db.RoleEntityOverrides.AsNoTracking().AnyAsync(overrideItem =>
                        overrideItem.Role != null && roleNames.Contains(overrideItem.Role.Name)
                        && overrideItem.Effect == "deny"
                        && (overrideItem.AppliesTo == requirement.AppliesTo || overrideItem.AppliesTo == "all"),
                        context.HttpContext.RequestAborted);
                if (!hasDenies)
                    continue;

                context.Result = new ObjectResult(new
                {
                    code = "FORBIDDEN",
                    message = $"This operation requires unrestricted {requirement.AppliesTo} access.",
                })
                { StatusCode = StatusCodes.Status403Forbidden };
                await _audit.LogAsync(AuditActions.PermissionDeny, AuditOutcomes.Deny, principal,
                    "endpoint", cad.DisplayName,
                    new { reason = "scoped_global_library_mutation", appliesTo = requirement.AppliesTo,
                        path = context.HttpContext.Request.Path.ToString() });
                return;
            }
        }

        foreach (var requirement in requirements)
        {
            var ids = ResolveIds(requirement, context);
            foreach (var id in ids)
            {
                var result = await _authz.AuthorizeAsync(
                    principal,
                    requirement.Permission,
                    new EntityRef(requirement.EntityKind, id),
                    context.HttpContext.RequestAborted);

                if (result.Allowed)
                    continue;

                var concealDenied = requirement.DeniedBehavior == EntityAccessDeniedBehavior.NotFound
                    || (requirement.DeniedBehavior == EntityAccessDeniedBehavior.Default
                        && requirement.Permission.EndsWith(".read", StringComparison.OrdinalIgnoreCase));
                context.Result = concealDenied
                    ? new NotFoundResult()
                    : new ObjectResult(new
                    {
                        code = "FORBIDDEN",
                        entityKind = requirement.EntityKind,
                        entityId = id,
                        permission = requirement.Permission,
                        message = result.Reason ?? "Forbidden.",
                    })
                    { StatusCode = StatusCodes.Status403Forbidden };

                await _audit.LogAsync(
                    AuditActions.PermissionDeny,
                    AuditOutcomes.Deny,
                    principal,
                    requirement.EntityKind,
                    id,
                    new
                    {
                        permission = requirement.Permission,
                        path = context.HttpContext.Request.Path.ToString(),
                        message = result.Reason,
                    },
                    context.HttpContext.RequestAborted);

                return;
            }
        }

        await next();
    }

    private static IReadOnlyList<string> ResolveIds(RequiresEntityAccessAttribute requirement, ActionExecutingContext context)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(requirement.RouteValueName)
            && context.RouteData.Values.TryGetValue(requirement.RouteValueName, out var routeValue))
        {
            foreach (var id in NormalizeIds(routeValue))
                ids.Add(id);
        }

        if (!string.IsNullOrWhiteSpace(requirement.ActionArgumentName)
            && context.ActionArguments.TryGetValue(requirement.ActionArgumentName, out var actionArgument))
        {
            foreach (var value in ExtractValues(actionArgument, requirement.PropertyName))
            {
                foreach (var id in NormalizeIds(value))
                    ids.Add(id);
            }
        }

        return ids.ToList();
    }

    private static IEnumerable<object?> ExtractValues(object? source, string? propertyPath)
    {
        if (source is null)
            return [];

        if (string.IsNullOrWhiteSpace(propertyPath))
            return FlattenTerminal(source);

        var segments = propertyPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            return FlattenTerminal(source);

        return ExtractValues(source, segments, 0);
    }

    private static IEnumerable<object?> ExtractValues(object? source, string[] segments, int index)
    {
        if (source is null)
            return [];

        if (source is IEnumerable enumerable && source is not string)
        {
            var flattened = new List<object?>();
            foreach (var item in enumerable)
                flattened.AddRange(ExtractValues(item, segments, index));
            return flattened;
        }

        var property = source.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, segments[index], StringComparison.OrdinalIgnoreCase));
        if (property is null)
            return [];

        var value = property.GetValue(source);
        if (index == segments.Length - 1)
            return FlattenTerminal(value);

        return ExtractValues(value, segments, index + 1);
    }

    private static IEnumerable<object?> FlattenTerminal(object? value)
    {
        if (value is null)
            return [];

        if (value is IEnumerable enumerable && value is not string)
        {
            var items = new List<object?>();
            foreach (var item in enumerable)
                items.Add(item);
            return items;
        }

        return [value];
    }

    private static IEnumerable<string> NormalizeIds(object? value)
    {
        if (value is null)
            yield break;

        switch (value)
        {
            case int intValue:
                yield return intValue.ToString(CultureInfo.InvariantCulture);
                yield break;
            case long longValue:
                yield return longValue.ToString(CultureInfo.InvariantCulture);
                yield break;
            case short shortValue:
                yield return shortValue.ToString(CultureInfo.InvariantCulture);
                yield break;
            case string stringValue when !string.IsNullOrWhiteSpace(stringValue):
                yield return stringValue.Trim();
                yield break;
            case JsonElement jsonElement:
                if (jsonElement.ValueKind == JsonValueKind.Number && jsonElement.TryGetInt64(out var numericId))
                {
                    yield return numericId.ToString(CultureInfo.InvariantCulture);
                    yield break;
                }

                if (jsonElement.ValueKind == JsonValueKind.String)
                {
                    var text = jsonElement.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        yield return text.Trim();
                }

                yield break;
        }

        var invariantText = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(invariantText))
            yield return invariantText.Trim();
    }
}
