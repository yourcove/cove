namespace Cove.Core.Entities.Auth;

/// <summary>
/// Long-lived refresh token, rotated on every use. Near-simultaneous reuse is treated
/// as a recoverable client race; older reuse triggers chain revocation.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int UserId { get; set; }

    /// <summary>SHA-256 of the opaque token. Plaintext is never stored.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Previous token in the rotation chain. Null for the chain root.</summary>
    public Guid? ParentId { get; set; }

    public string? UserAgent { get; set; }
    public string? Ip { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public User? User { get; set; }
    public RefreshToken? Parent { get; set; }
}
