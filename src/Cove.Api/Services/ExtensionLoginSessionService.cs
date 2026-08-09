using System.Security.Cryptography;
using System.Text;
using Cove.Api.Middleware;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Data.Auth;
using Cove.Plugins;

namespace Cove.Api.Services;

/// <summary>
/// Scoped host implementation used by interactive authentication extensions and AuthController.
/// </summary>
public sealed class ExtensionLoginSessionService(
    IUserService users,
    ITokenService tokens,
    IExternalIdentityService identities,
    IAuditService audit,
    CoveConfiguration configuration,
    ExtensionLoginTicketStore tickets,
    ILogger<ExtensionLoginSessionService> logger) : IExtensionLoginSessionService
{
    public const string BrowserBindingCookieName = "cove_external_login_binding";
    private static readonly TimeSpan BrowserBindingTtl = TimeSpan.FromMinutes(10);

    public string BeginBrowserSession(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var binding = ExtensionLoginTicketStore.RandomToken();
        context.Response.Cookies.Append(
            BrowserBindingCookieName,
            binding,
            BrowserCookieOptions(context, BrowserBindingTtl));
        return binding;
    }

    public bool IsBrowserSession(HttpContext context, string browserBinding)
    {
        ArgumentNullException.ThrowIfNull(context);

        return IsBoundValue(browserBinding)
            && context.Request.Cookies.TryGetValue(BrowserBindingCookieName, out var cookie)
            && IsBoundValue(cookie)
            && FixedTimeEquals(cookie, browserBinding);
    }

    public async Task<ExtensionLoginCompletion> CompleteAsync(
        HttpContext context,
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
            return new(null, ExtensionLoginCompletionFailure.InvalidRequest);
        }

        if (!IsBrowserSession(context, browserBinding))
            return new(null, ExtensionLoginCompletionFailure.BrowserMismatch);

        var ip = AuthDisabledRequestGuard
            .GetEffectiveRemoteAddress(context, configuration.Auth)
            ?.ToString();
        var userAgent = context.Request.Headers.UserAgent.ToString();
        var anonymous = CovePrincipal.Anonymous(ip, userAgent);
        var userId = await identities.ResolveUserIdAsync(identity, ct);
        if (userId is null)
        {
            await audit.LogAsync(
                AuditActions.LoginFail,
                AuditOutcomes.Fail,
                anonymous,
                "external_identity",
                null,
                new
                {
                    reason = "external_identity_unlinked",
                    identity.ExtensionId,
                    identity.ProviderId,
                },
                ct);
            return new(null, ExtensionLoginCompletionFailure.IdentityUnlinked);
        }

        var user = await users.GetAsync(userId.Value, ct);
        if (user is null || !user.IsActive || user.IsLocked || !user.HasPassword)
        {
            await audit.LogAsync(
                AuditActions.LoginFail,
                AuditOutcomes.Fail,
                anonymous,
                "user",
                userId.Value.ToString(),
                new
                {
                    reason = user is null
                        ? "external_missing_user"
                        : user.IsLocked
                            ? "external_locked_user"
                            : !user.IsActive
                                ? "external_inactive_user"
                                : "external_missing_password",
                    identity.ExtensionId,
                    identity.ProviderId,
                },
                ct);
            return new(null, ExtensionLoginCompletionFailure.UserRejected);
        }

        return new(
            tickets.Create(identity, browserBinding, user.Id),
            ExtensionLoginCompletionFailure.None);
    }

    [Obsolete("Username-only external authentication is no longer accepted. Use the identity assertion overload.")]
    public Task<ExtensionLoginCompletion> CompleteAsync(
        HttpContext context,
        string browserBinding,
        string extensionId,
        string username,
        CancellationToken ct = default) => Task.FromResult(new ExtensionLoginCompletion(
            null,
            ExtensionLoginCompletionFailure.InvalidRequest));

    public async Task<ExtensionLoginRedemption?> RedeemAsync(
        HttpContext context,
        string code,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(code)
            || code.Length > 256
            || !context.Request.Cookies.TryGetValue(BrowserBindingCookieName, out var browserBinding)
            || !IsBoundValue(browserBinding))
        {
            return null;
        }

        var ticket = tickets.TryRedeem(code.Trim(), browserBinding);
        if (ticket is null)
            return null;

        context.Response.Cookies.Delete(
            BrowserBindingCookieName,
            BrowserCookieOptions(context, TimeSpan.Zero));

        var ip = AuthDisabledRequestGuard
            .GetEffectiveRemoteAddress(context, configuration.Auth)
            ?.ToString();
        var userAgent = context.Request.Headers.UserAgent.ToString();
        var anonymous = CovePrincipal.Anonymous(ip, userAgent);
        var linkedUserId = await identities.ResolveUserIdAsync(ticket.Identity, ct);
        if (linkedUserId != ticket.UserId)
        {
            await audit.LogAsync(
                AuditActions.LoginFail,
                AuditOutcomes.Fail,
                anonymous,
                "external_identity",
                null,
                new
                {
                    reason = "external_identity_changed",
                    ticket.Identity.ExtensionId,
                    ticket.Identity.ProviderId,
                },
                ct);
            return null;
        }

        var user = await users.GetAsync(ticket.UserId, ct);
        if (user is null || !user.IsActive || user.IsLocked || !user.HasPassword)
        {
            await audit.LogAsync(
                AuditActions.LoginFail,
                AuditOutcomes.Fail,
                anonymous,
                "user",
                ticket.UserId.ToString(),
                new
                {
                    reason = "external_account_changed",
                    ticket.Identity.ExtensionId,
                    ticket.Identity.ProviderId,
                },
                ct);
            return null;
        }

        try
        {
            await users.RecordLoginSuccessAsync(user.Id, ip, ct);
            var pair = await tokens.IssueForUserAsync(user.Id, ip, userAgent, ct);
            await identities.MarkUsedAsync(ticket.Identity, ct);
            await audit.LogAsync(
                AuditActions.LoginSuccess,
                AuditOutcomes.Success,
                anonymous,
                "user",
                user.Id.ToString(),
                new
                {
                    method = ticket.Identity.Method,
                    ticket.Identity.ExtensionId,
                    ticket.Identity.ProviderId,
                },
                ct);
            return new ExtensionLoginRedemption(ticket.Identity.ExtensionId, pair);
        }
        catch (UnauthorizedException ex)
        {
            logger.LogDebug(
                ex,
                "External login from extension {ExtensionId} lost its usable Cove account during redemption",
                ticket.Identity.ExtensionId);
            return null;
        }
    }

    private static CookieOptions BrowserCookieOptions(HttpContext context, TimeSpan maxAge) => new()
    {
        HttpOnly = true,
        IsEssential = true,
        SameSite = SameSiteMode.Lax,
        Secure = context.Request.IsHttps,
        Path = "/",
        MaxAge = maxAge,
    };

    private static bool IsValidText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Any(char.IsControl);

    private static bool IsBoundValue(string? value) => IsValidText(value, 256);

    internal static bool FixedTimeEquals(string left, string right)
    {
        var leftHash = SHA256.HashData(Encoding.UTF8.GetBytes(left));
        var rightHash = SHA256.HashData(Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }
}

/// <summary>Process-local, bounded store for 60-second external login tickets.</summary>
public sealed class ExtensionLoginTicketStore(TimeProvider timeProvider)
{
    private const int MaximumTickets = 4096;
    private static readonly TimeSpan TicketTtl = TimeSpan.FromSeconds(60);
    private readonly object _gate = new();
    private readonly Dictionary<string, Ticket> _tickets = new(StringComparer.Ordinal);

    private sealed record Ticket(
        ExtensionIdentityAssertion Identity,
        byte[] BrowserBindingHash,
        int UserId,
        DateTimeOffset CreatedAt);

    public string Create(ExtensionIdentityAssertion identity, string browserBinding, int userId)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            SweepExpired(now);
            if (_tickets.Count >= MaximumTickets)
            {
                var oldest = _tickets.MinBy(entry => entry.Value.CreatedAt);
                _tickets.Remove(oldest.Key);
            }

            var code = RandomToken();
            _tickets[code] = new Ticket(
                identity,
                Hash(browserBinding),
                userId,
                now);
            return code;
        }
    }

    internal ExtensionLoginTicket? TryRedeem(string code, string browserBinding)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            SweepExpired(now);
            if (!_tickets.TryGetValue(code, out var ticket)
                || !CryptographicOperations.FixedTimeEquals(
                    ticket.BrowserBindingHash,
                    Hash(browserBinding)))
            {
                return null;
            }

            _tickets.Remove(code);
            return new ExtensionLoginTicket(ticket.Identity, ticket.UserId);
        }
    }

    private void SweepExpired(DateTimeOffset now)
    {
        foreach (var code in _tickets
                     .Where(entry => now - entry.Value.CreatedAt >= TicketTtl)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            _tickets.Remove(code);
        }
    }

    internal static string RandomToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] Hash(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value));
}

internal sealed record ExtensionLoginTicket(ExtensionIdentityAssertion Identity, int UserId);
