using Cove.Core.Auth;

namespace Cove.Plugins;

/// <summary>
/// Declares a permission requirement for an extension-owned minimal API endpoint.
/// Every metadata instance attached to an endpoint must be satisfied.
/// </summary>
public sealed record CovePermissionRequirementMetadata(
    IReadOnlyList<string> Permissions,
    PermissionMode Mode);

/// <summary>
/// Declares an entity-access check whose entity ID is read from a route value.
/// When <see cref="Permission"/> is null, the endpoint must declare exactly one distinct
/// permission through <see cref="CovePermissionRequirementMetadata"/>.
/// </summary>
public sealed record CoveRouteEntityAccessRequirementMetadata(
    string EntityKind,
    string RouteValueName,
    string? Permission);

/// <summary>
/// Explicitly exempts an extension endpoint from permission requirements while still requiring
/// an authenticated Cove principal.
/// </summary>
public sealed record CoveAllowWithoutPermissionMetadata;

/// <summary>Explicitly allows an extension endpoint to run without a Cove principal.</summary>
public sealed record CoveAllowAnonymousMetadata;
