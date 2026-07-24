using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;

namespace Cove.Sdk;

/// <summary>Authorization conventions for endpoints registered by <see cref="IApiExtension"/>.</summary>
public static class EndpointAuthorizationExtensions
{
    /// <summary>
    /// Explicitly allow any authenticated Cove principal without requiring a permission.
    /// Prefer a concrete permission policy for endpoints that expose application data.
    /// </summary>
    /// <remarks>
    /// Escape conventions are exclusive and must not be combined with each other or with a Cove
    /// permission or entity-access policy. The host rejects conflicting metadata.
    /// </remarks>
    public static TBuilder AllowWithoutCovePermission<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(endpoint => endpoint.Metadata.Add(new CoveAllowWithoutPermissionMetadata()));
        return builder;
    }

    /// <summary>
    /// Explicitly allow anonymous Cove access. This also adds ASP.NET Core's anonymous marker so
    /// framework authorization cannot reject the request before Cove evaluates its endpoint policy.
    /// </summary>
    /// <remarks>
    /// Escape conventions are exclusive and must not be combined with each other or with a Cove
    /// permission or entity-access policy. The host rejects conflicting metadata.
    /// </remarks>
    public static TBuilder AllowCoveAnonymous<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(endpoint =>
        {
            endpoint.Metadata.Add(new CoveAllowAnonymousMetadata());
            endpoint.Metadata.Add(new AllowAnonymousAttribute());
        });
        return builder;
    }

    /// <summary>
    /// Require the caller to hold every listed permission. A scoped read grant satisfies a listed
    /// read permission only when the endpoint also declares a matching route-bound entity check.
    /// </summary>
    public static TBuilder RequireCovePermission<TBuilder>(
        this TBuilder builder,
        params string[] permissions)
        where TBuilder : IEndpointConventionBuilder
        => RequireCovePermission(builder, PermissionMode.All, permissions);

    /// <summary>
    /// Require the caller to satisfy the listed permissions using the requested mode. A scoped read
    /// grant satisfies a listed read permission only when the endpoint also declares a matching
    /// route-bound entity check; permission-only endpoints require an unrestricted grant.
    /// </summary>
    public static TBuilder RequireCovePermission<TBuilder>(
        this TBuilder builder,
        PermissionMode mode,
        params string[] permissions)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown permission mode.");
        var normalized = NormalizeRequiredValues(permissions, nameof(permissions));
        builder.Add(endpoint => endpoint.Metadata.Add(
            new CovePermissionRequirementMetadata(normalized, mode)));
        return builder;
    }

    /// <summary>
    /// Require access to the entity identified by <paramref name="routeValueName"/>. The permission
    /// is inferred only when the endpoint declares exactly one distinct Cove permission.
    /// </summary>
    public static TBuilder RequireCoveEntityAccess<TBuilder>(
        this TBuilder builder,
        string entityKind,
        string routeValueName)
        where TBuilder : IEndpointConventionBuilder
        => AddEntityAccess(builder, entityKind, routeValueName, permission: null);

    /// <summary>
    /// Require <paramref name="permission"/> for the entity identified by
    /// <paramref name="routeValueName"/>.
    /// </summary>
    /// <remarks>
    /// This convention intentionally supports route-bound IDs only. Bind and validate request bodies
    /// in the endpoint, then perform explicit authorization with Cove's authorization service when a
    /// body-bound entity ID is unavoidable; the host does not pre-read or reflect over request bodies.
    /// </remarks>
    public static TBuilder RequireCoveEntityAccess<TBuilder>(
        this TBuilder builder,
        string entityKind,
        string routeValueName,
        string permission)
        where TBuilder : IEndpointConventionBuilder
        => AddEntityAccess(builder, entityKind, routeValueName, NormalizeRequiredValue(permission, nameof(permission)));

    private static TBuilder AddEntityAccess<TBuilder>(
        TBuilder builder,
        string entityKind,
        string routeValueName,
        string? permission)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        var normalizedKind = NormalizeRequiredValue(entityKind, nameof(entityKind));
        var normalizedRouteValue = NormalizeRequiredValue(routeValueName, nameof(routeValueName));
        builder.Add(endpoint => endpoint.Metadata.Add(
            new CoveRouteEntityAccessRequirementMetadata(normalizedKind, normalizedRouteValue, permission)));
        return builder;
    }

    private static string[] NormalizeRequiredValues(string[]? values, string parameterName)
    {
        if (values is null || values.Length == 0)
            throw new ArgumentException("At least one permission is required.", parameterName);

        return values
            .Select(value => NormalizeRequiredValue(value, parameterName))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeRequiredValue(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The value cannot be empty or whitespace.", parameterName);
        return value.Trim();
    }
}
