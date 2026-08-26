using Cove.Core.Auth;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<IReadOnlyList<PermissionDefinition>> GetRolePermissionsAsync(CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<PermissionDefinition>>(HttpMethod.Get, WithCacheNonce("/api/roles/permissions"), payload: null, cancellationToken);

    public Task<RoleDto> GetRoleAsync(int roleId, CancellationToken cancellationToken = default)
        => SendAsync<RoleDto>(HttpMethod.Get, WithCacheNonce($"/api/roles/{roleId}"), payload: null, cancellationToken);

    public Task<RoleDto> CreateRoleAsync(CreateRoleRequest request, CancellationToken cancellationToken = default)
        => SendAsync<RoleDto>(HttpMethod.Post, "/api/roles", request, cancellationToken);

    public Task DeleteRoleAsync(int roleId, CancellationToken cancellationToken = default)
        => SendForNoContentAsync(HttpMethod.Delete, $"/api/roles/{roleId}", new { }, cancellationToken);

    public Task<IReadOnlyList<ContentRuleDto>> GetContentRulesAsync(int? roleId = null, CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<ContentRuleDto>>(HttpMethod.Get, WithCacheNonce($"/api/content-rules{(roleId.HasValue ? $"?roleId={roleId.Value}" : string.Empty)}"), payload: null, cancellationToken);

    public Task<ContentRuleDto> UpdateContentRuleAsync(int ruleId, UpdateContentRuleRequest request, CancellationToken cancellationToken = default)
        => SendAsync<ContentRuleDto>(HttpMethod.Put, $"/api/content-rules/{ruleId}", request, cancellationToken);

    public Task DeleteContentRuleAsync(int ruleId, CancellationToken cancellationToken = default)
        => SendForNoContentAsync(HttpMethod.Delete, $"/api/content-rules/{ruleId}", new { }, cancellationToken);

    public Task<IReadOnlyList<EntityOverrideDto>> GetEntityOverridesAsync(int roleId, string entityKind, CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<EntityOverrideDto>>(HttpMethod.Get, WithCacheNonce($"/api/content-rules/overrides?roleId={roleId}&entityKind={Uri.EscapeDataString(entityKind)}"), payload: null, cancellationToken);

    public Task DeleteEntityOverrideAsync(int overrideId, CancellationToken cancellationToken = default)
        => SendForNoContentAsync(HttpMethod.Delete, $"/api/content-rules/overrides/{overrideId}", new { }, cancellationToken);
}
