namespace Cove.Core.Auth;

/// <summary>
/// Decision returned by <see cref="IAuthorizationService"/>. Carries the missing permission
/// for a clean 403 response body.
/// </summary>
public readonly struct AuthorizationResult
{
    public bool Allowed { get; init; }
    public string? Reason { get; init; }
    public string? MissingPermission { get; init; }

    public static AuthorizationResult Allow() => new() { Allowed = true };
    public static AuthorizationResult Deny(string reason, string? missing = null) => new()
    {
        Allowed = false, Reason = reason, MissingPermission = missing
    };
}

/// <summary>
/// Central authorization service. Encodes the deny-overrides-allow algorithm from
/// docs/AUTH_AND_PERMISSIONS_DESIGN.md §3.3.
/// </summary>
public interface IAuthorizationService
{
    AuthorizationResult Authorize(CovePrincipal? principal, string permission, EntityRef? entity = null);
    Task<AuthorizationResult> AuthorizeAsync(CovePrincipal? principal, string permission, EntityRef? entity, CancellationToken ct);
    void Require(CovePrincipal? principal, string permission, EntityRef? entity = null);
    bool Has(CovePrincipal? principal, string permission);

    async Task<IReadOnlyList<AuthorizationResult>> AuthorizeManyAsync(CovePrincipal? principal, string permission, IReadOnlyList<EntityRef> entities, CancellationToken ct)
    {
        var results = new List<AuthorizationResult>(entities.Count);
        foreach (var entity in entities)
            results.Add(await AuthorizeAsync(principal, permission, entity, ct));
        return results;
    }
}
