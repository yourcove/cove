using System.Globalization;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Plugins;
using IAuthorizationService = Cove.Core.Auth.IAuthorizationService;

namespace Cove.Api.Middleware;

/// <summary>
/// Enforces Cove permission and route-bound entity metadata on extension-owned minimal API endpoints.
/// This runs after Cove resolves the current principal and before the extension request scope is active,
/// so all security decisions and denial audits use host-owned services.
/// </summary>
public sealed class ExtensionEndpointAuthorizationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        CoveConfiguration configuration,
        ICurrentPrincipalAccessor principalAccessor,
        IAuthorizationService authorization,
        IAuditService audit)
    {
        var endpoint = context.GetEndpoint();
        if (configuration.Auth?.Enabled != true
            || endpoint?.Metadata.GetMetadata<ExtensionEndpointMetadata>() is null)
        {
            await next(context);
            return;
        }

        var authorizationEvaluator = new EndpointAuthorizationEvaluator(
            context,
            endpoint,
            principalAccessor.Current,
            authorization,
            audit);
        if (!await authorizationEvaluator.AuthorizeAsync(configuration.Auth.EnforceDefaultDeny))
            return;

        await next(context);
    }

    private sealed class EndpointAuthorizationEvaluator
    {
        private readonly HttpContext _context;
        private readonly Endpoint _endpoint;
        private readonly CovePrincipal? _principal;
        private readonly IAuthorizationService _authorization;
        private readonly IAuditService _audit;
        private readonly IReadOnlyList<CovePermissionRequirementMetadata> _permissionRequirements;
        private readonly IReadOnlyList<CoveRouteEntityAccessRequirementMetadata> _entityRequirements;
        private readonly string[] _inferredPermissions;
        private readonly bool _allowsAnonymous;
        private readonly bool _allowsWithoutPermission;

        public EndpointAuthorizationEvaluator(
            HttpContext context,
            Endpoint endpoint,
            CovePrincipal? principal,
            IAuthorizationService authorization,
            IAuditService audit)
        {
            _context = context;
            _endpoint = endpoint;
            _principal = principal;
            _authorization = authorization;
            _audit = audit;
            _permissionRequirements = endpoint.Metadata
                .GetOrderedMetadata<CovePermissionRequirementMetadata>();
            _entityRequirements = endpoint.Metadata
                .GetOrderedMetadata<CoveRouteEntityAccessRequirementMetadata>();
            _inferredPermissions = _permissionRequirements
                .SelectMany(requirement => requirement.Permissions)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            _allowsAnonymous = endpoint.Metadata.GetMetadata<CoveAllowAnonymousMetadata>() is not null;
            _allowsWithoutPermission = endpoint.Metadata
                .GetMetadata<CoveAllowWithoutPermissionMetadata>() is not null;
        }

        private bool HasRequirements => _permissionRequirements.Count > 0 || _entityRequirements.Count > 0;

        private bool HasConflictingEscapePolicy => _allowsAnonymous
            ? _allowsWithoutPermission || HasRequirements
            : _allowsWithoutPermission && HasRequirements;

        public async Task<bool> AuthorizeAsync(bool enforceDefaultDeny)
        {
            if (HasConflictingEscapePolicy)
                return await DenyConflictingEscapePolicyAsync();

            if (_allowsAnonymous)
                return true;

            if (_allowsWithoutPermission)
                return await RequireAuthenticatedPrincipalAsync();

            if (!HasRequirements)
                return await AuthorizePolicylessEndpointAsync(enforceDefaultDeny);

            if (!await RequireAuthenticatedPrincipalAsync())
                return false;

            if (!await AuthorizePermissionRequirementsAsync())
                return false;

            return await AuthorizeEntityRequirementsAsync();
        }

        private async Task<bool> RequireAuthenticatedPrincipalAsync()
        {
            if (_principal is not null && _principal.Kind != PrincipalKind.Anonymous)
                return true;

            await WriteJsonAsync(StatusCodes.Status401Unauthorized, new
            {
                code = "UNAUTHORIZED",
                message = "Authentication required.",
            });
            return false;
        }

        private async Task<bool> AuthorizePolicylessEndpointAsync(bool enforceDefaultDeny)
        {
            if (!enforceDefaultDeny)
                return await RequireAuthenticatedPrincipalAsync();

            return await DenyEndpointAsync(
                new
                {
                    reason = "no_policy",
                    path = _context.Request.Path.ToString(),
                },
                new
                {
                    status = StatusCodes.Status403Forbidden,
                    title = "Forbidden",
                    detail = "This endpoint has no permission policy declared.",
                });
        }

        private async Task<bool> AuthorizePermissionRequirementsAsync()
        {
            foreach (var requirement in _permissionRequirements)
            {
                if (!IsValidPermissionRequirement(requirement))
                    return await DenyInvalidPermissionRequirementAsync();

                if (PermissionRequirementIsSatisfied(requirement))
                    continue;

                var missingPermissions = requirement.Permissions
                    .Where(permission => !PrincipalSatisfiesPermission(permission))
                    .ToArray();
                return await DenyMissingPermissionsAsync(missingPermissions);
            }

            return true;
        }

        private static bool IsValidPermissionRequirement(CovePermissionRequirementMetadata requirement)
            => requirement.Permissions.Count > 0 && Enum.IsDefined(requirement.Mode);

        private bool PermissionRequirementIsSatisfied(CovePermissionRequirementMetadata requirement)
            => requirement.Mode switch
            {
                PermissionMode.All => requirement.Permissions.All(PrincipalSatisfiesPermission),
                PermissionMode.Any => requirement.Permissions.Any(PrincipalSatisfiesPermission),
                _ => false,
            };

        private bool PrincipalSatisfiesPermission(string permission)
        {
            if (_principal!.Has(permission))
                return true;

            // A read grant is scoped, not a global permission. It is safe here only when the host
            // will also authorize this same permission against a route-bound entity below.
            return _principal.HasReadGrant(permission) && HasMatchingEntityRequirement(permission);
        }

        private bool HasMatchingEntityRequirement(string permission)
        {
            if (!CovePrincipal.TryGetReadGrantEntityKind(permission, out var grantedEntityKind))
                return false;

            return _entityRequirements.Any(requirement =>
                string.Equals(requirement.EntityKind, grantedEntityKind, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(requirement.Permission, permission, StringComparison.Ordinal)
                    || requirement.Permission is null
                    && _inferredPermissions.Length == 1
                    && string.Equals(_inferredPermissions[0], permission, StringComparison.Ordinal)));
        }

        private async Task<bool> AuthorizeEntityRequirementsAsync()
        {
            foreach (var requirement in _entityRequirements)
            {
                if (!await AuthorizeEntityRequirementAsync(requirement))
                    return false;
            }

            return true;
        }

        private async Task<bool> AuthorizeEntityRequirementAsync(
            CoveRouteEntityAccessRequirementMetadata requirement)
        {
            if (!TryGetEntityId(requirement.RouteValueName, out var entityId))
                return await DenyInvalidRouteValueAsync(requirement);

            var permission = ResolveEntityPermission(requirement);
            if (permission is null)
                return await DenyAmbiguousEntityPermissionAsync(requirement);

            if (!PermissionMatchesEntityKind(permission, requirement.EntityKind))
                return await DenyMismatchedEntityPermissionAsync(requirement.EntityKind, permission);

            var result = await _authorization.AuthorizeAsync(
                _principal!,
                permission,
                new EntityRef(requirement.EntityKind, entityId),
                _context.RequestAborted);
            if (result.Allowed)
                return true;

            return await DenyEntityAccessAsync(
                requirement.EntityKind,
                entityId,
                permission,
                result.Reason);
        }

        private bool TryGetEntityId(string routeValueName, out string entityId)
        {
            entityId = string.Empty;
            if (!_context.Request.RouteValues.TryGetValue(routeValueName, out var rawId)
                || !int.TryParse(
                    Convert.ToString(rawId, CultureInfo.InvariantCulture),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var numericId)
                || numericId <= 0)
            {
                return false;
            }

            entityId = numericId.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private string? ResolveEntityPermission(CoveRouteEntityAccessRequirementMetadata requirement)
            => requirement.Permission ?? (_inferredPermissions.Length == 1 ? _inferredPermissions[0] : null);

        private static bool PermissionMatchesEntityKind(string permission, string entityKind)
            => !CovePrincipal.TryGetReadGrantEntityKind(permission, out var permissionEntityKind)
                || string.Equals(entityKind, permissionEntityKind, StringComparison.OrdinalIgnoreCase);

        private Task<bool> DenyConflictingEscapePolicyAsync()
            => DenyEndpointAsync(
                new
                {
                    reason = "conflicting_escape_policy",
                    path = _context.Request.Path.ToString(),
                },
                new
                {
                    code = "INVALID_AUTHORIZATION_POLICY",
                    message = "Cove authorization escape metadata must be exclusive.",
                });

        private Task<bool> DenyInvalidPermissionRequirementAsync()
            => DenyEndpointAsync(
                new
                {
                    reason = "invalid_permission_requirement",
                    path = _context.Request.Path.ToString(),
                },
                new
                {
                    code = "INVALID_AUTHORIZATION_POLICY",
                    message = "The endpoint declares an invalid permission requirement.",
                });

        private Task<bool> DenyMissingPermissionsAsync(IReadOnlyList<string> missingPermissions)
            => DenyEndpointAsync(
                new
                {
                    missing = missingPermissions,
                    path = _context.Request.Path.ToString(),
                },
                new
                {
                    code = "FORBIDDEN",
                    missing = missingPermissions,
                });

        private Task<bool> DenyInvalidRouteValueAsync(
            CoveRouteEntityAccessRequirementMetadata requirement)
            => DenyEndpointAsync(
                new
                {
                    reason = "invalid_route_value",
                    entityKind = requirement.EntityKind,
                    routeValueName = requirement.RouteValueName,
                    path = _context.Request.Path.ToString(),
                },
                new
                {
                    code = "INVALID_ENTITY_REFERENCE",
                    entityKind = requirement.EntityKind,
                    routeValueName = requirement.RouteValueName,
                    message = "The required entity route value must be a positive integer.",
                });

        private Task<bool> DenyAmbiguousEntityPermissionAsync(
            CoveRouteEntityAccessRequirementMetadata requirement)
            => DenyEndpointAsync(
                new
                {
                    reason = "ambiguous_entity_permission",
                    entityKind = requirement.EntityKind,
                    routeValueName = requirement.RouteValueName,
                    path = _context.Request.Path.ToString(),
                },
                new
                {
                    code = "INVALID_AUTHORIZATION_POLICY",
                    message = "Entity access requires an explicit permission when the endpoint does not declare exactly one permission.",
                });

        private Task<bool> DenyMismatchedEntityPermissionAsync(string entityKind, string permission)
            => DenyEndpointAsync(
                new
                {
                    reason = "mismatched_entity_permission",
                    entityKind,
                    permission,
                    path = _context.Request.Path.ToString(),
                },
                new
                {
                    code = "INVALID_AUTHORIZATION_POLICY",
                    message = "The entity-access permission does not match the declared entity kind.",
                });

        private async Task<bool> DenyEntityAccessAsync(
            string entityKind,
            string entityId,
            string permission,
            string? reason)
        {
            await _audit.LogAsync(
                AuditActions.PermissionDeny,
                AuditOutcomes.Deny,
                _principal,
                entityKind,
                entityId,
                new
                {
                    permission,
                    path = _context.Request.Path.ToString(),
                    message = reason,
                    endpoint = _endpoint.DisplayName,
                },
                _context.RequestAborted);
            await WriteJsonAsync(StatusCodes.Status403Forbidden, new
            {
                code = "FORBIDDEN",
                entityKind,
                entityId,
                permission,
                message = reason ?? "Forbidden.",
            });
            return false;
        }

        private async Task<bool> DenyEndpointAsync(object auditDetail, object responseBody)
        {
            await _audit.LogAsync(
                AuditActions.PermissionDeny,
                AuditOutcomes.Deny,
                _principal,
                "endpoint",
                _endpoint.DisplayName,
                auditDetail);
            await WriteJsonAsync(StatusCodes.Status403Forbidden, responseBody);
            return false;
        }

        private Task WriteJsonAsync(int statusCode, object body)
        {
            _context.Response.StatusCode = statusCode;
            return _context.Response.WriteAsJsonAsync(
                body,
                cancellationToken: _context.RequestAborted);
        }
    }
}
