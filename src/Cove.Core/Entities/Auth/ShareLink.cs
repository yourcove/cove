namespace Cove.Core.Entities.Auth;

/// <summary>
/// Anonymous, time-limited, optionally password-gated, read-only share link.
/// Materialized as a synthetic Guest principal with explicit RoleEntityOverride
/// rows for the listed entities at request time.
/// </summary>
public class ShareLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int? CreatedByUserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;

    public string EntityKind { get; set; } = string.Empty;

    /// <summary>JSON array of entity ids visible through this link.</summary>
    public string EntityIds { get; set; } = "[]";

    /// <summary>Snapshot of readable child entities included by container shares.</summary>
    public string ContainedEntityIds { get; set; } = "[]";

    public DateTime? ExpiresAt { get; set; }

    /// <summary>Optional BCrypt hash gating access.</summary>
    public string? PasswordHash { get; set; }

    public int ViewCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }

    public User? CreatedBy { get; set; }
}
