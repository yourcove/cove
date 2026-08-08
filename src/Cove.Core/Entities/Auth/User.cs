namespace Cove.Core.Entities.Auth;

/// <summary>
/// Cove user account. Authentication subject for the API.
/// </summary>
public class User : BaseEntity
{
    public string Username { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Email { get; set; }

    /// <summary>BCrypt hash of the user password. Verified with constant-time compare.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Algorithm tag, e.g. "bcrypt" or "argon2id". Forward-compatibility seam.</summary>
    public string PasswordAlgo { get; set; } = "bcrypt";

    /// <summary>If false, user cannot log in (admin-disabled). Distinct from IsLocked.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>If true, user is temporarily locked out (e.g. by failed-login policy).</summary>
    public bool IsLocked { get; set; }

    public int FailedLoginCount { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public string? LastLoginIp { get; set; }
    public DateTime? LockedUntil { get; set; }
    public bool MustChangePassword { get; set; }

    /// <summary>TOTP secret for second-factor (designed-for; not enabled in v1).</summary>
    public string? TotpSecret { get; set; }

    /// <summary>Per-user UI preferences persisted across browsers.</summary>
    public string? UiPreferencesJson { get; set; }

    /// <summary>True for the bootstrap "owner" account that cannot be deleted.</summary>
    public bool IsSystem { get; set; }

    public ICollection<UserRoleAssignment> Roles { get; set; } = [];
    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
    public ICollection<ApiToken> ApiTokens { get; set; } = [];
    public ICollection<ExternalIdentityLink> ExternalIdentities { get; set; } = [];
}
