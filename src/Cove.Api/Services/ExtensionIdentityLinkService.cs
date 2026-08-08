using System.Security.Cryptography;
using System.Text;
using Cove.Core.Auth;
using Cove.Data.Auth;
using Cove.Plugins;

namespace Cove.Api.Services;

public sealed class ExtensionIdentityLinkService(
    ICurrentPrincipalAccessor principals,
    IExternalIdentityService identities,
    IExtensionLoginSessionService loginSessions,
    ExtensionIdentityLinkTicketStore tickets) : IExtensionIdentityLinkService
{
    public ExtensionIdentityLinkIntent? BeginLink(
        HttpContext context,
        string extensionId,
        string providerId)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (principals.Current?.UserId is not int userId)
            return null;

        string normalizedExtensionId;
        string normalizedProviderId;
        try
        {
            normalizedExtensionId = NormalizeRequired(extensionId, 256);
            normalizedProviderId = NormalizeRequired(providerId, 512);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var browserBinding = loginSessions.BeginBrowserSession(context);
        var token = tickets.CreateIntent(
            userId,
            normalizedExtensionId,
            normalizedProviderId,
            browserBinding);
        return new ExtensionIdentityLinkIntent(token, browserBinding);
    }

    public async Task<ExtensionIdentityLinkPreparation> PrepareLinkAsync(
        HttpContext context,
        string intentToken,
        string browserBinding,
        ExtensionIdentityAssertion assertion,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ExtensionIdentityAssertion identity;
        try
        {
            identity = ExternalIdentityService.Normalize(assertion);
        }
        catch (ArgumentException)
        {
            return new(null, ExtensionIdentityLinkPreparationFailure.InvalidRequest);
        }

        if (!loginSessions.IsBrowserSession(context, browserBinding))
            return new(null, ExtensionIdentityLinkPreparationFailure.BrowserMismatch);

        var intent = tickets.TryTakeIntent(
            intentToken,
            browserBinding,
            identity.ExtensionId,
            identity.ProviderId);
        if (intent is null)
            return new(null, ExtensionIdentityLinkPreparationFailure.InvalidRequest);

        var existingUserId = await identities.ResolveUserIdAsync(identity, ct);
        if (existingUserId is int linkedUserId && linkedUserId != intent.UserId)
            return new(null, ExtensionIdentityLinkPreparationFailure.IdentityConflict);

        return new(
            tickets.CreatePending(intent.UserId, browserBinding, identity),
            ExtensionIdentityLinkPreparationFailure.None);
    }

    public async Task<ExtensionIdentityLinkPreparation> PrepareDirectLinkAsync(
        HttpContext context,
        ExtensionIdentityAssertion assertion,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (principals.Current?.UserId is not int userId)
            return new(null, ExtensionIdentityLinkPreparationFailure.InvalidRequest);

        ExtensionIdentityAssertion identity;
        try
        {
            identity = ExternalIdentityService.Normalize(assertion);
        }
        catch (ArgumentException)
        {
            return new(null, ExtensionIdentityLinkPreparationFailure.InvalidRequest);
        }

        var existingUserId = await identities.ResolveUserIdAsync(identity, ct);
        if (existingUserId is int linkedUserId && linkedUserId != userId)
            return new(null, ExtensionIdentityLinkPreparationFailure.IdentityConflict);

        var browserBinding = loginSessions.BeginBrowserSession(context);
        return new(
            tickets.CreatePending(userId, browserBinding, identity),
            ExtensionIdentityLinkPreparationFailure.None);
    }

    public Task<PendingExternalIdentityLinkDto?> PreviewAsync(
        HttpContext context,
        string code,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (principals.Current?.UserId is not int userId
            || !TryGetBrowserBinding(context, out var browserBinding))
        {
            return Task.FromResult<PendingExternalIdentityLinkDto?>(null);
        }

        var pending = tickets.PeekPending(code, userId, browserBinding);
        return Task.FromResult(pending is null
            ? null
            : new PendingExternalIdentityLinkDto(
                pending.Identity.ProviderLabel,
                pending.Identity.AccountLabel));
    }

    public async Task<ExternalIdentityLinkDto?> ConfirmAsync(
        HttpContext context,
        string code,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var actor = principals.Current;
        if (actor?.UserId is not int userId
            || !TryGetBrowserBinding(context, out var browserBinding))
        {
            return null;
        }

        var pending = tickets.TryTakePending(code, userId, browserBinding);
        return pending is null
            ? null
            : await identities.CreateLinkAsync(userId, pending.Identity, actor, ct);
    }

    public bool Cancel(HttpContext context, string code)
    {
        ArgumentNullException.ThrowIfNull(context);
        return principals.Current?.UserId is int userId
            && TryGetBrowserBinding(context, out var browserBinding)
            && tickets.TryTakePending(code, userId, browserBinding) is not null;
    }

    private static bool TryGetBrowserBinding(HttpContext context, out string browserBinding)
    {
        if (context.Request.Cookies.TryGetValue(
                ExtensionLoginSessionService.BrowserBindingCookieName,
                out var value)
            && !string.IsNullOrWhiteSpace(value)
            && value.Length <= 256
            && !value.Any(char.IsControl))
        {
            browserBinding = value;
            return true;
        }

        browserBinding = string.Empty;
        return false;
    }

    private static string NormalizeRequired(string? value, int maximumLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Length > maximumLength
            || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("The external identity identifier is invalid.");
        }
        return normalized;
    }
}

/// <summary>Bounded process-local link intents and pending confirmations.</summary>
public sealed class ExtensionIdentityLinkTicketStore(TimeProvider timeProvider)
{
    private const int MaximumEntries = 4096;
    private static readonly TimeSpan IntentTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly Dictionary<string, Intent> _intents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    private sealed record Intent(
        int UserId,
        string ExtensionId,
        string ProviderId,
        byte[] BrowserBindingHash,
        DateTimeOffset CreatedAt);

    internal sealed record Pending(
        int UserId,
        ExtensionIdentityAssertion Identity,
        byte[] BrowserBindingHash,
        DateTimeOffset CreatedAt);

    public string CreateIntent(
        int userId,
        string extensionId,
        string providerId,
        string browserBinding)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            Sweep(now);
            TrimOldestIntent();
            var token = ExtensionLoginTicketStore.RandomToken();
            _intents[token] = new Intent(
                userId,
                extensionId,
                providerId,
                Hash(browserBinding),
                now);
            return token;
        }
    }

    internal ExternalIdentityLinkIntentTicket? TryTakeIntent(
        string token,
        string browserBinding,
        string extensionId,
        string providerId)
    {
        if (!IsToken(token))
            return null;

        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            Sweep(now);
            if (!_intents.TryGetValue(token, out var intent)
                || !FixedTimeEquals(intent.BrowserBindingHash, Hash(browserBinding))
                || !string.Equals(intent.ExtensionId, extensionId, StringComparison.Ordinal)
                || !string.Equals(intent.ProviderId, providerId, StringComparison.Ordinal))
            {
                return null;
            }

            _intents.Remove(token);
            return new ExternalIdentityLinkIntentTicket(intent.UserId);
        }
    }

    public string CreatePending(
        int userId,
        string browserBinding,
        ExtensionIdentityAssertion identity)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            Sweep(now);
            TrimOldestPending();
            var code = ExtensionLoginTicketStore.RandomToken();
            _pending[code] = new Pending(userId, identity, Hash(browserBinding), now);
            return code;
        }
    }

    internal Pending? PeekPending(string code, int userId, string browserBinding)
    {
        if (!IsToken(code))
            return null;
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            Sweep(now);
            return Matches(_pending.GetValueOrDefault(code), userId, browserBinding)
                ? _pending[code]
                : null;
        }
    }

    internal Pending? TryTakePending(string code, int userId, string browserBinding)
    {
        if (!IsToken(code))
            return null;
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            Sweep(now);
            var pending = _pending.GetValueOrDefault(code);
            if (!Matches(pending, userId, browserBinding))
                return null;
            _pending.Remove(code);
            return pending;
        }
    }

    private static bool Matches(Pending? pending, int userId, string browserBinding) =>
        pending is not null
        && pending.UserId == userId
        && FixedTimeEquals(pending.BrowserBindingHash, Hash(browserBinding));

    private void Sweep(DateTimeOffset now)
    {
        foreach (var key in _intents
                     .Where(entry => now - entry.Value.CreatedAt >= IntentTtl)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            _intents.Remove(key);
        }
        foreach (var key in _pending
                     .Where(entry => now - entry.Value.CreatedAt >= PendingTtl)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            _pending.Remove(key);
        }
    }

    private void TrimOldestIntent()
    {
        if (_intents.Count < MaximumEntries)
            return;
        var oldest = _intents.MinBy(entry => entry.Value.CreatedAt);
        _intents.Remove(oldest.Key);
    }

    private void TrimOldestPending()
    {
        if (_pending.Count < MaximumEntries)
            return;
        var oldest = _pending.MinBy(entry => entry.Value.CreatedAt);
        _pending.Remove(oldest.Key);
    }

    private static bool IsToken(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256
        && !value.Any(char.IsControl);

    private static byte[] Hash(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static bool FixedTimeEquals(byte[] left, byte[] right) =>
        CryptographicOperations.FixedTimeEquals(left, right);
}

internal sealed record ExternalIdentityLinkIntentTicket(int UserId);
