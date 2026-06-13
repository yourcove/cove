using Cove.Core.Auth;
using Cove.Core.Entities.Auth;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

public sealed class AuthBypassPrincipalProvider
{
    // When no eligible user exists yet (owner setup not completed), throttle the DB lookup so we don't
    // re-query on every request, and only emit the warning once instead of spamming the log.
    private static readonly TimeSpan LookupRetryInterval = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuthBypassPrincipalProvider> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int? _cachedUserId;
    private string _cachedUsername = "owner";
    private DateTime _lastLookupUtc = DateTime.MinValue;
    private bool _loggedMissingWarning;

    public AuthBypassPrincipalProvider(IServiceScopeFactory scopeFactory, ILogger<AuthBypassPrincipalProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async ValueTask<CovePrincipal> GetAsync(string? ip, string? userAgent, CancellationToken ct)
    {
        if (_cachedUserId is int cachedUserId)
            return CreatePrincipal(cachedUserId, _cachedUsername, ip, userAgent);

        await _lock.WaitAsync(ct);
        try
        {
            if (_cachedUserId is null && DateTime.UtcNow - _lastLookupUtc >= LookupRetryInterval)
            {
                _lastLookupUtc = DateTime.UtcNow;

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                // Prefer the bootstrap-created system owner, but also accept any active user that holds
                // the Owner role (e.g. one created from Security settings when owner setup was skipped),
                // so auth-disabled mode runs as a real owner instead of the permission-less fallback.
                var owner = await db.Users.AsNoTracking()
                    .Where(user => user.IsActive && !user.IsLocked
                        && (user.IsSystem || user.Roles.Any(assignment => assignment.Role!.Name == BuiltinRoles.Owner)))
                    .OrderByDescending(user => user.IsSystem)
                    .ThenBy(user => user.Id)
                    .Select(user => new { user.Id, user.Username })
                    .FirstOrDefaultAsync(ct);

                if (owner is not null)
                {
                    _cachedUserId = owner.Id;
                    _cachedUsername = owner.Username;
                }
                else if (!_loggedMissingWarning)
                {
                    _loggedMissingWarning = true;
                    _logger.LogWarning("Authentication bypass requested with auth disabled, but no active owner/system user was found. Complete owner setup (or create an active user with the Owner role) so requests run as the owner.");
                }
            }
        }
        finally
        {
            _lock.Release();
        }

        return _cachedUserId is int resolvedUserId
            ? CreatePrincipal(resolvedUserId, _cachedUsername, ip, userAgent)
            : CreateFallbackPrincipal(ip, userAgent);
    }

    private static CovePrincipal CreatePrincipal(int userId, string username, string? ip, string? userAgent) => new()
    {
        UserId = userId,
        Username = username,
        Kind = PrincipalKind.System,
        Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Owner" },
        Permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "*" },
        Ip = ip,
        UserAgent = userAgent,
    };

    private static CovePrincipal CreateFallbackPrincipal(string? ip, string? userAgent) => new()
    {
        UserId = null,
        Username = "system",
        Kind = PrincipalKind.System,
        Roles = new HashSet<string>(),
        Permissions = new HashSet<string> { "*" },
        Ip = ip,
        UserAgent = userAgent,
    };
}