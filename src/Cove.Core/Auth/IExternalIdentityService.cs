namespace Cove.Core.Auth;

/// <summary>
/// A stable external identity after an authentication extension has validated its protocol. The
/// extension and provider identifiers scope the exact, case-sensitive subject. Labels are display
/// metadata only and never participate in account resolution.
/// </summary>
public sealed record ExtensionIdentityAssertion(
    string ExtensionId,
    string ProviderId,
    string Subject,
    string Method,
    string ProviderLabel,
    string? AccountLabel = null);

public sealed record ExternalIdentityLinkDto(
    int Id,
    int UserId,
    string ExtensionId,
    string ProviderId,
    string ProviderLabel,
    string? AccountLabel,
    DateTime CreatedAt,
    DateTime? LastUsedAt);

public sealed record PendingExternalIdentityLinkDto(
    string ProviderLabel,
    string? AccountLabel);

public sealed class ExternalIdentityConflictException : InvalidOperationException
{
    public ExternalIdentityConflictException() : base("This external identity is already linked to another Cove user.")
    {
    }
}

/// <summary>Cove-owned persistence and lifecycle rules for external identity links.</summary>
public interface IExternalIdentityService
{
    Task<int?> ResolveUserIdAsync(
        ExtensionIdentityAssertion assertion,
        CancellationToken ct = default);

    Task MarkUsedAsync(
        ExtensionIdentityAssertion assertion,
        CancellationToken ct = default);

    Task<IReadOnlyList<ExternalIdentityLinkDto>> ListForUserAsync(
        int userId,
        CancellationToken ct = default);

    Task<ExternalIdentityLinkDto> CreateLinkAsync(
        int userId,
        ExtensionIdentityAssertion assertion,
        CovePrincipal? actor,
        CancellationToken ct = default);

    Task RemoveLinkAsync(
        int userId,
        int linkId,
        CovePrincipal? actor,
        CancellationToken ct = default);

    Task<int> CountProviderLinksAsync(
        string extensionId,
        string providerId,
        CancellationToken ct = default);
}
