using Cove.Core.Auth;
using Cove.Core.Entities.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Data;
using System.Globalization;

namespace Cove.Data.Auth;

public sealed class AuthorizationService : IAuthorizationService
{
    private readonly CoveContext _db;
    private readonly ILogger<AuthorizationService> _log;

    public AuthorizationService(CoveContext db, ILogger<AuthorizationService> log)
    {
        _db = db;
        _log = log;
    }

    public bool Has(CovePrincipal? principal, string permission)
    {
        if (principal is null) return false;
        return principal.Has(permission);
    }

    public AuthorizationResult Authorize(CovePrincipal? principal, string permission, EntityRef? entity = null)
    {
        if (principal is null)
            return AuthorizationResult.Deny("Not authenticated.", permission);
        if (principal.Kind == PrincipalKind.Anonymous)
            return AuthorizationResult.Deny("Anonymous principal.", permission);
        var hasDirectPermission = principal.Has(permission);
        var hasGrantedRead = !hasDirectPermission && principal.HasReadGrant(permission);
        if (!hasDirectPermission && !hasGrantedRead)
            return AuthorizationResult.Deny($"Missing permission '{permission}'.", permission);

        if (entity is null)
            return AuthorizationResult.Allow();

        var allowed = CanAccessEntityAsync(principal, permission, entity.Value, hasDirectPermission, hasGrantedRead, CancellationToken.None)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

        return allowed
            ? AuthorizationResult.Allow()
            : AuthorizationResult.Deny($"Access denied for {entity.Value.Kind}:{entity.Value.Id} ({VerbFor(permission)}).", permission);
    }

    public async Task<AuthorizationResult> AuthorizeAsync(CovePrincipal? principal, string permission, EntityRef? entity, CancellationToken ct)
    {
        if (principal is null)
            return AuthorizationResult.Deny("Not authenticated.", permission);
        if (principal.Kind == PrincipalKind.Anonymous)
            return AuthorizationResult.Deny("Anonymous principal.", permission);
        var hasDirectPermission = principal.Has(permission);
        var hasGrantedRead = !hasDirectPermission && principal.HasReadGrant(permission);
        if (!hasDirectPermission && !hasGrantedRead)
            return AuthorizationResult.Deny($"Missing permission '{permission}'.", permission);

        if (entity is null)
            return AuthorizationResult.Allow();

        var allowed = await CanAccessEntityAsync(principal, permission, entity.Value, hasDirectPermission, hasGrantedRead, ct);
        return allowed
            ? AuthorizationResult.Allow()
            : AuthorizationResult.Deny($"Access denied for {entity.Value.Kind}:{entity.Value.Id} ({VerbFor(permission)}).", permission);
    }

    private static string VerbFor(string permission)
    {
        if (string.Equals(permission, Permissions.LibraryScan, StringComparison.OrdinalIgnoreCase))
            return "write";

        var dot = permission.IndexOf('.');
        if (dot < 0) return "all";

        var rest = permission[(dot + 1)..];
        if (rest.StartsWith("read", StringComparison.OrdinalIgnoreCase)) return "read";
        if (rest.StartsWith("write", StringComparison.OrdinalIgnoreCase)) return "write";
        if (rest.StartsWith("delete", StringComparison.OrdinalIgnoreCase)) return "delete";
        return "all";
    }

    public void Require(CovePrincipal? principal, string permission, EntityRef? entity = null)
    {
        var result = Authorize(principal, permission, entity);
        if (!result.Allowed)
            throw new ForbiddenException(result.Reason ?? "Forbidden.", permission, entity);
    }

    private async Task<bool> CanAccessEntityAsync(CovePrincipal principal, string permission, EntityRef entity, bool hasDirectPermission, bool hasGrantedRead, CancellationToken ct)
    {
        if (principal.Has(Permissions.All))
            return true;

        if (!int.TryParse(entity.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var entityId))
        {
            _log.LogWarning("Entity access check skipped for non-integer id {EntityKind}:{EntityId}", entity.Kind, entity.Id);
            return true;
        }

        var appliesTo = VerbFor(permission);
        var connection = _db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();

            if (string.Equals(appliesTo, "read", StringComparison.OrdinalIgnoreCase))
            {
                command.CommandText = "SELECT public.cove_authz_can_read(@p_bypass, @p_has_read_permission, @p_has_read_grant, @p_role_names, @p_share_link_id, @p_kind, @p_entity_id)";
                command.Parameters.Add(new NpgsqlParameter<bool>("p_bypass", false));
                command.Parameters.Add(new NpgsqlParameter<bool>("p_has_read_permission", hasDirectPermission));
                command.Parameters.Add(new NpgsqlParameter<bool>("p_has_read_grant", hasGrantedRead));
                command.Parameters.Add(new NpgsqlParameter<string[]>("p_role_names", principal.Roles.ToArray()));
                var shareLinkParam = new NpgsqlParameter("p_share_link_id", DbType.Guid)
                {
                    Value = principal.Kind == PrincipalKind.ShareLink && principal.TokenId is Guid tokenId
                        ? tokenId
                        : DBNull.Value,
                };
                command.Parameters.Add(shareLinkParam);
                command.Parameters.Add(new NpgsqlParameter<string>("p_kind", entity.Kind.ToLowerInvariant()));
                command.Parameters.Add(new NpgsqlParameter<int>("p_entity_id", entityId));
            }
            else
            {
                command.CommandText = "SELECT public.cove_authz_can_access(@p_bypass, @p_has_permission, @p_role_names, @p_kind, @p_entity_id, @p_applies_to)";
                command.Parameters.Add(new NpgsqlParameter<bool>("p_bypass", false));
                command.Parameters.Add(new NpgsqlParameter<bool>("p_has_permission", hasDirectPermission));
                command.Parameters.Add(new NpgsqlParameter<string[]>("p_role_names", principal.Roles.ToArray()));
                command.Parameters.Add(new NpgsqlParameter<string>("p_kind", entity.Kind.ToLowerInvariant()));
                command.Parameters.Add(new NpgsqlParameter<int>("p_entity_id", entityId));
                command.Parameters.Add(new NpgsqlParameter<string>("p_applies_to", appliesTo));
            }

            var result = await command.ExecuteScalarAsync(ct);
            return result is true || result is bool boolean && boolean;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }
}
