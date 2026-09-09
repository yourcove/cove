using System.Reflection;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cove.Api.Middleware;

/// <summary>
/// Enforces model-bound conditional permissions before the entity-access filter performs any
/// per-entity authorization work.
/// </summary>
public sealed class ConditionalPermissionActionFilter(
    ICurrentPrincipalAccessor principalAccessor,
    IAuditService audit) : IAsyncActionFilter, IOrderedFilter
{
    public int Order => -10_000;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor action)
        {
            await next();
            return;
        }

        var requirements = action.MethodInfo
            .GetCustomAttributes<RequiresPermissionWhenTrueAttribute>(inherit: true)
            .Where(requirement => IsEnabled(requirement, context.ActionArguments))
            .ToArray();
        if (requirements.Length == 0)
        {
            await next();
            return;
        }

        var principal = principalAccessor.Current;
        if (principal is null || principal.Kind == PrincipalKind.Anonymous)
        {
            context.Result = new ObjectResult(new { code = "UNAUTHORIZED", message = "Authentication required." })
            {
                StatusCode = StatusCodes.Status401Unauthorized,
            };
            return;
        }

        foreach (var requirement in requirements)
        {
            if (principal.Has(requirement.Permission))
                continue;

            context.Result = new ObjectResult(new
            {
                code = "FORBIDDEN",
                permission = requirement.Permission,
                message = $"Missing permission: {requirement.Permission}",
            })
            { StatusCode = StatusCodes.Status403Forbidden };
            await audit.LogAsync(
                AuditActions.PermissionDeny,
                AuditOutcomes.Deny,
                principal,
                "endpoint",
                action.DisplayName,
                new
                {
                    permission = requirement.Permission,
                    reason = "conditional_permission",
                    path = context.HttpContext.Request.Path.ToString(),
                },
                context.HttpContext.RequestAborted);
            return;
        }

        await next();
    }

    private static bool IsEnabled(
        RequiresPermissionWhenTrueAttribute requirement,
        IDictionary<string, object?> arguments)
    {
        if (string.IsNullOrWhiteSpace(requirement.ActionArgumentName)
            || !arguments.TryGetValue(requirement.ActionArgumentName, out var value)
            || value is null)
            return false;

        if (!string.IsNullOrWhiteSpace(requirement.PropertyName))
        {
            var property = value.GetType().GetProperty(
                requirement.PropertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            value = property?.GetValue(value);
        }

        return value is true || bool.TryParse(value?.ToString(), out var parsed) && parsed;
    }
}
