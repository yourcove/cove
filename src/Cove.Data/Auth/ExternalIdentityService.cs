using Cove.Core.Auth;
using Cove.Core.Entities.Auth;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Auth;

public sealed class ExternalIdentityService(
    CoveContext db,
    IAuditService audit,
    TimeProvider timeProvider) : IExternalIdentityService
{
    private static readonly TimeSpan LastUsedWriteInterval = TimeSpan.FromMinutes(15);

    public async Task<int?> ResolveUserIdAsync(
        ExtensionIdentityAssertion assertion,
        CancellationToken ct = default)
    {
        var identity = Normalize(assertion);
        return await db.ExternalIdentityLinks
            .AsNoTracking()
            .Where(link => link.ExtensionId == identity.ExtensionId
                && link.ProviderId == identity.ProviderId
                && link.Subject == identity.Subject)
            .Select(link => (int?)link.UserId)
            .SingleOrDefaultAsync(ct);
    }

    public async Task MarkUsedAsync(
        ExtensionIdentityAssertion assertion,
        CancellationToken ct = default)
    {
        var identity = Normalize(assertion);
        var link = await FindAsync(identity, ct);
        if (link is null)
            return;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (link.LastUsedAt is DateTime lastUsed
            && now - lastUsed < LastUsedWriteInterval)
        {
            return;
        }

        link.LastUsedAt = now;
        link.UpdatedAt = link.LastUsedAt.Value;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ExternalIdentityLinkDto>> ListForUserAsync(
        int userId,
        CancellationToken ct = default) => await db.ExternalIdentityLinks
        .AsNoTracking()
        .Where(link => link.UserId == userId)
        .OrderBy(link => link.ProviderLabel)
        .ThenBy(link => link.AccountLabel)
        .ThenBy(link => link.Id)
        .Select(link => Map(link))
        .ToListAsync(ct);

    public async Task<ExternalIdentityLinkDto> CreateLinkAsync(
        int userId,
        ExtensionIdentityAssertion assertion,
        CovePrincipal? actor,
        CancellationToken ct = default)
    {
        var identity = Normalize(assertion);
        var userExists = await db.Users.AnyAsync(user => user.Id == userId, ct);
        if (!userExists)
            throw new KeyNotFoundException("User not found.");

        var existing = await FindAsync(identity, ct);
        if (existing is not null)
        {
            if (existing.UserId != userId)
                throw new ExternalIdentityConflictException();

            existing.ProviderLabel = identity.ProviderLabel;
            existing.AccountLabel = identity.AccountLabel;
            existing.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            return Map(existing);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var link = new ExternalIdentityLink
        {
            UserId = userId,
            ExtensionId = identity.ExtensionId,
            ProviderId = identity.ProviderId,
            Subject = identity.Subject,
            ProviderLabel = identity.ProviderLabel,
            AccountLabel = identity.AccountLabel,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ExternalIdentityLinks.Add(link);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(link).State = EntityState.Detached;
            existing = await FindAsync(identity, ct);
            if (existing is null)
                throw;
            if (existing.UserId != userId)
                throw new ExternalIdentityConflictException();
            return Map(existing);
        }

        await audit.LogAsync(
            AuditActions.ExternalIdentityLink,
            AuditOutcomes.Success,
            actor,
            "external_identity",
            link.Id.ToString(),
            new { link.ExtensionId, link.ProviderId, userId },
            ct);
        return Map(link);
    }

    public async Task RemoveLinkAsync(
        int userId,
        int linkId,
        CovePrincipal? actor,
        CancellationToken ct = default)
    {
        var removedLink = await db.ExternalIdentityLinks
            .FirstOrDefaultAsync(candidate => candidate.Id == linkId && candidate.UserId == userId, ct)
            ?? throw new KeyNotFoundException("External identity link not found.");
        db.ExternalIdentityLinks.Remove(removedLink);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(
            AuditActions.ExternalIdentityUnlink,
            AuditOutcomes.Success,
            actor,
            "external_identity",
            removedLink.Id.ToString(),
            new { removedLink.ExtensionId, removedLink.ProviderId, userId },
            ct);
    }

    public async Task<int> CountProviderLinksAsync(
        string extensionId,
        string providerId,
        CancellationToken ct = default)
    {
        var normalizedExtensionId = NormalizeRequired(extensionId, 256, "extension ID");
        var normalizedProviderId = NormalizeRequired(providerId, 512, "provider ID");
        return await db.ExternalIdentityLinks.CountAsync(
            link => link.ExtensionId == normalizedExtensionId
                && link.ProviderId == normalizedProviderId,
            ct);
    }

    private Task<ExternalIdentityLink?> FindAsync(
        ExtensionIdentityAssertion identity,
        CancellationToken ct) => db.ExternalIdentityLinks.SingleOrDefaultAsync(
        link => link.ExtensionId == identity.ExtensionId
            && link.ProviderId == identity.ProviderId
            && link.Subject == identity.Subject,
        ct);

    public static ExtensionIdentityAssertion Normalize(ExtensionIdentityAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        return new ExtensionIdentityAssertion(
            NormalizeRequired(assertion.ExtensionId, 256, "extension ID"),
            NormalizeRequired(assertion.ProviderId, 512, "provider ID"),
            ValidateExactRequired(assertion.Subject, 512, "subject"),
            NormalizeRequired(assertion.Method, 128, "method"),
            NormalizeRequired(assertion.ProviderLabel, 128, "provider label"),
            NormalizeOptional(assertion.AccountLabel, 256, "account label"))
        {
            IsAuthoritative = assertion.IsAuthoritative,
        };
    }

    private static string NormalizeRequired(string? value, int maximumLength, string name)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Length > maximumLength
            || normalized.Any(char.IsControl))
        {
            throw new ArgumentException($"The external identity {name} is invalid.");
        }
        return normalized;
    }

    private static string? NormalizeOptional(string? value, int maximumLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return NormalizeRequired(value, maximumLength, name);
    }

    private static string ValidateExactRequired(string? value, int maximumLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new ArgumentException($"The external identity {name} is invalid.");
        }

        // A protocol subject is an opaque, case-sensitive identifier. Trimming or otherwise
        // normalizing it can collapse two identities that the authority considers distinct.
        return value;
    }

    private static ExternalIdentityLinkDto Map(ExternalIdentityLink link) => new(
        link.Id,
        link.UserId,
        link.ExtensionId,
        link.ProviderId,
        link.ProviderLabel,
        link.AccountLabel,
        link.CreatedAt,
        link.LastUsedAt);
}
