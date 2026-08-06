namespace Cove.Core.Auth;

/// <summary>
/// Append-only security audit log writer. Persists to the audit_events table.
/// Implementations must never throw — logging failures must not break the request.
/// </summary>
public interface IAuditService
{
    Task LogAsync(
        string action,
        string outcome,
        CovePrincipal? actor = null,
        string? targetKind = null,
        string? targetId = null,
        object? detail = null,
        CancellationToken ct = default);
}

public static class AuditOutcomes
{
    public const string Success = "success";
    public const string Fail = "fail";
    public const string Allow = "allow";
    public const string Deny = "deny";
    public const string Error = "error";
}

public static class AuditActions
{
    public const string LoginSuccess = "login.success";
    public const string LoginFail = "login.fail";
    public const string LoginLockout = "login.lockout";
    public const string Logout = "logout";
    public const string TokenRefresh = "token.refresh";
    public const string TokenRefreshConflict = "token.refresh.conflict";
    public const string TokenRefreshReuse = "token.refresh.reuse";
    public const string TokenIssue = "token.issue";
    public const string TokenRevoke = "token.revoke";
    public const string ApiTokenCreate = "api_token.create";
    public const string ApiTokenRevoke = "api_token.revoke";
    public const string ShareLinkCreate = "share_link.create";
    public const string ShareLinkAccess = "share_link.access";
    public const string ShareLinkRevoke = "share_link.revoke";
    public const string PermissionDeny = "permission.deny";
    public const string UserCreate = "user.create";
    public const string UserUpdate = "user.update";
    public const string UserDelete = "user.delete";
    public const string UserDisable = "user.disable";
    public const string UserInviteCreate = "user.invite.create";
    public const string UserInviteRedeem = "user.invite.redeem";
    public const string UserUnlock = "user.unlock";
    public const string PasswordChange = "password.change";
    public const string RoleCreate = "role.create";
    public const string RoleUpdate = "role.update";
    public const string RoleDelete = "role.delete";
    public const string RoleGrant = "role.grant";
    public const string RoleRevoke = "role.revoke";
    public const string SettingsChange = "settings.change";
    public const string AuthFailsafeEnabled = "auth.failsafe-enabled";
    public const string AuthSetupTokenCreate = "auth.setup-token.create";
    public const string SystemShutdown = "system.shutdown";
    public const string AuthSetupTokenRedeem = "auth.setup-token.redeem";
    public const string AiDataPurge = "ai_data.purge";
}
