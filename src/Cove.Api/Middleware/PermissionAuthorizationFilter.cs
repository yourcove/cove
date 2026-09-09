using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using IAuthorizationService = Cove.Core.Auth.IAuthorizationService;

namespace Cove.Api.Middleware;

/// <summary>
/// Global default-deny authorization filter.
/// Rejects any controller action that does not declare a <see cref="RequiresPermissionAttribute"/>,
/// <see cref="AllowAnonymousAttribute"/>, or <see cref="AllowWithoutPermissionAttribute"/>
/// — but ONLY when <see cref="AuthConfig.EnforceDefaultDeny"/> is enabled.
/// </summary>
public sealed class PermissionAuthorizationFilter : IAsyncAuthorizationFilter
{
    private readonly ICurrentPrincipalAccessor _principalAccessor;
    private readonly IAuthorizationService _authz;
    private readonly IAuditService _audit;
    private readonly CoveConfiguration _config;

    public PermissionAuthorizationFilter(
        ICurrentPrincipalAccessor principalAccessor,
        IAuthorizationService authz,
        IAuditService audit,
        CoveConfiguration config)
    {
        _principalAccessor = principalAccessor;
        _authz = authz;
        _audit = audit;
        _config = config;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor cad) return;

        var authEnabled = _config.Auth?.Enabled ?? false;

        // [AllowAnonymous] short-circuit (works in any mode)
        var hasAllowAnonymous = cad.MethodInfo.GetCustomAttributes(true).OfType<AllowAnonymousAttribute>().Any()
            || cad.ControllerTypeInfo.GetCustomAttributes(true).OfType<AllowAnonymousAttribute>().Any();
        if (hasAllowAnonymous) return;

        // When the deployment hasn't enabled auth, skip enforcement entirely.
        if (!authEnabled) return;

        var hasAllowNoPerm = cad.MethodInfo.GetCustomAttributes(true).OfType<AllowWithoutPermissionAttribute>().Any()
            || cad.ControllerTypeInfo.GetCustomAttributes(true).OfType<AllowWithoutPermissionAttribute>().Any();

        var requires = cad.MethodInfo.GetCustomAttributes(true).OfType<RequiresPermissionAttribute>().ToList();
        if (requires.Count == 0)
            requires = cad.ControllerTypeInfo.GetCustomAttributes(true).OfType<RequiresPermissionAttribute>().ToList();

        var principal = _principalAccessor.Current;

        if (principal?.Kind == PrincipalKind.ShareLink)
        {
            var allowsShareLink = cad.MethodInfo.GetCustomAttributes(true).OfType<AllowShareLinkAccessAttribute>().Any()
                || cad.ControllerTypeInfo.GetCustomAttributes(true).OfType<AllowShareLinkAccessAttribute>().Any();
            if (!allowsShareLink)
            {
                context.Result = new ObjectResult(new
                {
                    code = "FORBIDDEN",
                    message = "This endpoint is outside the share link viewing bundle.",
                })
                { StatusCode = StatusCodes.Status403Forbidden };
                await _audit.LogAsync(AuditActions.PermissionDeny, AuditOutcomes.Deny, principal,
                    "endpoint", cad.DisplayName, new { reason = "share_link_route", path = context.HttpContext.Request.Path.ToString() });
                return;
            }
        }

        if (hasAllowNoPerm)
        {
            if (principal is null || principal.Kind == PrincipalKind.Anonymous)
            {
                context.Result = Unauthorized();
                return;
            }
            return;
        }

        if (requires.Count == 0)
        {
            // No attributes declared. Behavior depends on EnforceDefaultDeny:
            //   true  -> 403 (strict)
            //   false -> require an authenticated user but allow the call (legacy migration mode)
            if (_config.Auth?.EnforceDefaultDeny == true)
            {
                context.Result = new ObjectResult(new ProblemDetails
                {
                    Status = StatusCodes.Status403Forbidden,
                    Title = "Forbidden",
                    Detail = "This endpoint has no permission policy declared.",
                })
                { StatusCode = StatusCodes.Status403Forbidden };
                await _audit.LogAsync(AuditActions.PermissionDeny, AuditOutcomes.Deny, principal,
                    "endpoint", cad.DisplayName, new { reason = "no_policy", path = context.HttpContext.Request.Path.ToString() });
                return;
            }
            if (principal is null || principal.Kind == PrincipalKind.Anonymous)
            {
                context.Result = Unauthorized();
                return;
            }
            return;
        }

        if (principal is null || principal.Kind == PrincipalKind.Anonymous)
        {
            context.Result = Unauthorized();
            return;
        }

        foreach (var attr in requires)
        {
            bool Satisfies(string permission) => principal.Has(permission) || principal.HasReadGrant(permission);

            var passed = attr.Mode == PermissionMode.All
                ? attr.Permissions.All(Satisfies)
                : attr.Permissions.Any(Satisfies);
            if (!passed)
            {
                context.Result = new ObjectResult(new
                {
                    code = "FORBIDDEN",
                    missing = attr.Permissions.Where(p => !Satisfies(p)).ToArray(),
                })
                { StatusCode = StatusCodes.Status403Forbidden };
                await _audit.LogAsync(AuditActions.PermissionDeny, AuditOutcomes.Deny, principal,
                    "endpoint", cad.DisplayName,
                    new { missing = attr.Permissions.Where(p => !Satisfies(p)).ToArray(),
                          path = context.HttpContext.Request.Path.ToString() });
                return;
            }
        }
    }

    private static IActionResult Unauthorized() => new ObjectResult(new
    {
        code = "UNAUTHORIZED",
        message = "Authentication required.",
    })
    { StatusCode = StatusCodes.Status401Unauthorized };
}
