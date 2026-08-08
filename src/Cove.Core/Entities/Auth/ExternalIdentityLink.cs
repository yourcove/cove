namespace Cove.Core.Entities.Auth;

/// <summary>
/// A stable identity asserted by an authentication extension and explicitly linked to one Cove user.
/// Provider subjects are opaque, case-sensitive identifiers and are never used as Cove usernames.
/// </summary>
public sealed class ExternalIdentityLink : BaseEntity
{
    public int UserId { get; set; }
    public User? User { get; set; }
    public string ExtensionId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string ProviderLabel { get; set; } = string.Empty;
    public string? AccountLabel { get; set; }
    public DateTime? LastUsedAt { get; set; }
}
