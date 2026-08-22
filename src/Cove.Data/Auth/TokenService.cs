using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Cove.Data.Auth;

public sealed class TokenService : ITokenService, IExistingUserPrincipalResolver
{
    public const int DefaultRefreshDays = 30;
    public const string JwtIssuer = "Cove";
    public const string JwtAudience = "Cove";
    public static readonly TimeSpan RefreshReuseGracePeriod = TimeSpan.FromSeconds(10);
    private const string SessionIdClaim = "cove_session_id";

    private readonly CoveContext _db;
    private readonly CoveConfiguration _config;
    private readonly IPermissionRegistry _registry;
    private readonly ILogger<TokenService> _log;
    private readonly IAuditService? _audit;

    public TokenService(CoveContext db, CoveConfiguration config, IPermissionRegistry registry, ILogger<TokenService> log, IAuditService? audit = null)
    {
        _db = db;
        _config = config;
        _registry = registry;
        _log = log;
        _audit = audit;
    }

    public async Task<TokenPair> IssueForUserAsync(int userId, string? ip, string? userAgent, CancellationToken ct = default)
    {
        var user = await _db.Users.AsNoTracking()
            .Include(u => u.Roles).ThenInclude(r => r.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnauthorizedException("User not found.");
        if (!user.IsActive || user.IsLocked || string.IsNullOrWhiteSpace(user.PasswordHash))
            throw new UnauthorizedException("Account is not active.");

        var roleNames = user.Roles.Select(r => r.Role!.Name).ToList();
        var dto = await BuildUserDto(user, ct);

        var (refreshPlain, refreshHash) = NewOpaqueToken();
        var refreshId = Guid.NewGuid();
        var refreshExpires = DateTime.UtcNow.AddDays(GetRefreshTokenDays());
        var (jwt, jwtExpires) = IssueJwt(user.Id, user.Username, roleNames, refreshId);

        var entity = new RefreshToken
        {
            Id = refreshId,
            UserId = user.Id,
            TokenHash = refreshHash,
            UserAgent = Trunc(userAgent, 500),
            Ip = Trunc(ip, 64),
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
            ExpiresAt = refreshExpires,
        };
        _db.RefreshTokens.Add(entity);
        await _db.SaveChangesAsync(ct);

        return new TokenPair(jwt, refreshPlain, jwtExpires, refreshExpires, dto);
    }

    public async Task<TokenPair> RefreshAsync(string refreshToken, string? ip, string? userAgent, CancellationToken ct = default)
    {
        var (newPlain, newHash) = NewOpaqueToken();
        var refreshId = Guid.NewGuid();
        var rotatedAt = DateTime.UtcNow;
        var refreshExpires = rotatedAt.AddDays(GetRefreshTokenDays());
        var strategy = _db.Database.CreateExecutionStrategy();
        var attempt = await strategy.ExecuteInTransactionAsync(
            operation: operationCt => RefreshOnceAsync(
                refreshToken, ip, userAgent, refreshId, newPlain, newHash,
                rotatedAt, refreshExpires, operationCt),
            verifySucceeded: verifyCt => _db.RefreshTokens.AsNoTracking()
                .AnyAsync(t => t.Id == refreshId && t.TokenHash == newHash, verifyCt),
            cancellationToken: ct);

        if (attempt.Error is not null)
            throw attempt.Error;
        return attempt.Pair!;
    }

    private async Task<RefreshAttempt> RefreshOnceAsync(
        string refreshToken,
        string? ip,
        string? userAgent,
        Guid refreshId,
        string newPlain,
        string newHash,
        DateTime rotatedAt,
        DateTime refreshExpires,
        CancellationToken ct)
    {
        var priorCandidate = _db.ChangeTracker.Entries<RefreshToken>()
            .FirstOrDefault(entry => entry.Entity.Id == refreshId);
        if (priorCandidate is not null)
            priorCandidate.State = EntityState.Detached;

        var hash = HashToken(refreshToken);
        var existing = await _db.RefreshTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
            ?? throw new UnauthorizedException("Invalid refresh token.");
        var rootId = await FindChainRootIdAsync(existing, ct);
        if (await LockRefreshTokenRootsAsync([rootId], ct) == 0)
            throw new UnauthorizedException("Invalid refresh token.");

        // Reload after taking the family-root lock so revocation and rotation
        // decisions cannot race another operation in this token family.
        existing = await _db.RefreshTokens.AsNoTracking()
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.Id == existing.Id, ct)
            ?? throw new UnauthorizedException("Invalid refresh token.");

        if (existing.RevokedAt is not null)
            return await HandleRevokedRefreshTokenAsync(existing, rootId, ct);
        if (existing.ExpiresAt < DateTime.UtcNow)
            throw new UnauthorizedException("Refresh token expired.");
        if (existing.User is null
            || !existing.User.IsActive
            || existing.User.IsLocked
            || string.IsNullOrWhiteSpace(existing.User.PasswordHash))
            throw new UnauthorizedException("Account is not active.");

        var dto = await BuildUserDto(existing.User, ct);

        // Atomically claim this token so concurrent requests cannot both create a
        // child. The execution strategy keeps this claim and child insert in one
        // retriable transaction and verifies ambiguous commits by the fixed child ID.
        var claimed = await _db.RefreshTokens
            .Where(t => t.Id == existing.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(update => update
                .SetProperty(t => t.RevokedAt, rotatedAt)
                .SetProperty(t => t.LastUsedAt, rotatedAt), ct);
        if (claimed == 0)
        {
            var current = await _db.RefreshTokens.AsNoTracking()
                .FirstAsync(t => t.Id == existing.Id, ct);
            return await HandleRevokedRefreshTokenAsync(current, rootId, ct);
        }

        var rotated = new RefreshToken
        {
            Id = refreshId,
            UserId = existing.UserId,
            ParentId = existing.Id,
            TokenHash = newHash,
            UserAgent = Trunc(userAgent, 500),
            Ip = Trunc(ip, 64),
            CreatedAt = rotatedAt,
            LastUsedAt = rotatedAt,
            ExpiresAt = refreshExpires,
        };
        _db.RefreshTokens.Add(rotated);

        var (jwt, jwtExpires) = IssueJwt(existing.UserId, existing.User.Username, dto.Roles, refreshId);
        await _db.SaveChangesAsync(ct);

        return new RefreshAttempt(
            new TokenPair(jwt, newPlain, jwtExpires, refreshExpires, dto),
            null);
    }

    private async Task<RefreshAttempt> HandleRevokedRefreshTokenAsync(
        RefreshToken existing,
        Guid rootId,
        CancellationToken ct)
    {
        var recentlyRotated = existing.RevokedAt >= DateTime.UtcNow.Subtract(RefreshReuseGracePeriod);
        if (recentlyRotated)
        {
            var descendantIds = await GetChainIdsAsync(existing.Id, ct);
            var hasActiveDescendant = await _db.RefreshTokens.AsNoTracking()
                .AnyAsync(t => t.Id != existing.Id && descendantIds.Contains(t.Id) && t.RevokedAt == null, ct);
            if (hasActiveDescendant)
                return new RefreshAttempt(null, new RefreshTokenConflictException());
        }

        // Reuse outside the narrow race window remains compromise detection.
        await RevokeChainRowsAsync(rootId, ct);
        return new RefreshAttempt(
            null,
            new UnauthorizedException("Refresh token reuse detected; chain revoked."));
    }

    public async Task RevokeChainAsync(string refreshToken, CancellationToken ct = default)
    {
        var hash = HashToken(refreshToken);
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteInTransactionAsync(
            operation: async operationCt =>
            {
                var token = await _db.RefreshTokens.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.TokenHash == hash, operationCt);
                if (token is null) return;
                var rootId = await FindChainRootIdAsync(token, operationCt);
                await LockRefreshTokenRootsAsync([rootId], operationCt);
                await RevokeChainRowsAsync(rootId, operationCt);
            },
            verifySucceeded: _ => Task.FromResult(false),
            cancellationToken: ct);
    }

    private async Task<Guid> FindChainRootIdAsync(RefreshToken token, CancellationToken ct)
    {
        var rootId = token.Id;
        var parentId = token.ParentId;
        while (parentId is { } pid)
        {
            var parent = await _db.RefreshTokens.AsNoTracking()
                .Where(t => t.Id == pid)
                .Select(t => new { t.Id, t.ParentId })
                .FirstOrDefaultAsync(ct);
            if (parent is null) break;
            rootId = parent.Id;
            parentId = parent.ParentId;
        }
        return rootId;
    }

    private async Task<int> LockRefreshTokenRootsAsync(IReadOnlyCollection<Guid> rootIds, CancellationToken ct)
    {
        var orderedRootIds = rootIds.Distinct().Order().ToArray();
        if (orderedRootIds.Length == 0) return 0;

        // PostgreSQL row locks serialize operations in one token family without
        // creating a new MVCC row version solely for coordination. SQLite is used
        // only by tests and does not support FOR UPDATE.
        if (!string.Equals(_db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
            return await _db.RefreshTokens.CountAsync(t => orderedRootIds.Contains(t.Id), ct);

        var transaction = _db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Refresh-token family locks require an active transaction.");
        var connection = _db.Database.GetDbConnection();
        var locked = 0;
        foreach (var rootId in orderedRootIds)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = """
                SELECT "Id"
                FROM refresh_tokens
                WHERE "Id" = @id
                FOR UPDATE
                """;
            var idParameter = command.CreateParameter();
            idParameter.ParameterName = "id";
            idParameter.Value = rootId;
            command.Parameters.Add(idParameter);
            if (await command.ExecuteScalarAsync(ct) is not null)
                locked++;
        }
        return locked;
    }

    private async Task<HashSet<Guid>> GetChainIdsAsync(Guid rootId, CancellationToken ct)
    {
        var ids = new HashSet<Guid> { rootId };
        var frontier = new List<Guid> { rootId };
        while (frontier.Count > 0)
        {
            var children = await _db.RefreshTokens.AsNoTracking()
                .Where(t => t.ParentId != null && frontier.Contains(t.ParentId.Value))
                .Select(t => t.Id)
                .ToListAsync(ct);
            frontier = [];
            foreach (var c in children)
                if (ids.Add(c)) frontier.Add(c);
        }
        return ids;
    }

    private async Task RevokeChainRowsAsync(Guid rootId, CancellationToken ct)
    {
        var ids = await GetChainIdsAsync(rootId, ct);
        var now = DateTime.UtcNow;
        await _db.RefreshTokens
            .Where(t => ids.Contains(t.Id) && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
    }

    public async Task RevokeAllForUserAsync(int userId, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteInTransactionAsync(
            operation: async operationCt =>
            {
                var rootIds = await _db.RefreshTokens.AsNoTracking()
                    .Where(t => t.UserId == userId && t.ParentId == null)
                    .Select(t => t.Id)
                    .ToListAsync(operationCt);
                await LockRefreshTokenRootsAsync(rootIds, operationCt);
                var now = DateTime.UtcNow;
                await _db.RefreshTokens
                    .Where(t => t.UserId == userId && t.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), operationCt);
            },
            verifySucceeded: _ => Task.FromResult(false),
            cancellationToken: ct);
    }

    private sealed record RefreshAttempt(TokenPair? Pair, Exception? Error);

    public async Task<CovePrincipal?> ResolveAsync(string? authorizationHeader, string? ip, string? userAgent, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader)) return null;
        if (!authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = authorizationHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(token)) return null;

        // Cove API tokens are prefixed "cove_pat_<id>_<secret>".
        if (token.StartsWith("cove_pat_", StringComparison.Ordinal))
            return await ResolveApiTokenAsync(token, ip, userAgent, ct);

        return await ResolveJwtAsync(token, ip, userAgent, ct);
    }

    private async Task<CovePrincipal?> ResolveJwtAsync(string token, string? ip, string? userAgent, CancellationToken ct)
    {
        var handler = new JwtSecurityTokenHandler();
        if (!handler.CanReadToken(token))
        {
            _log.LogDebug("Bearer token rejected because it is not a readable JWT");
            return null;
        }

        var keyBytes = Encoding.UTF8.GetBytes(_config.Auth.JwtSecret);
        try
        {
            var p = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = JwtIssuer,
                ValidateAudience = true, ValidAudience = JwtAudience,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
                ClockSkew = TimeSpan.FromSeconds(30),
            }, out _);

            var sub = p.FindFirst(ClaimTypes.NameIdentifier)?.Value
                   ?? p.FindFirst("sub")?.Value;
            if (!int.TryParse(sub, out var userId)) return null;

            var sessionClaim = p.FindFirst(SessionIdClaim)?.Value;
            if (!Guid.TryParse(sessionClaim, out var sessionId)) return null;

            var session = await _db.RefreshTokens.AsNoTracking()
                .Where(t => t.Id == sessionId && t.UserId == userId)
                .Select(t => new { t.RevokedAt, t.ExpiresAt })
                .FirstOrDefaultAsync(ct);
            if (session is null || session.RevokedAt is not null || session.ExpiresAt < DateTime.UtcNow)
                return null;

            var user = await _db.Users.AsNoTracking()
                .Include(u => u.Roles).ThenInclude(r => r.Role).ThenInclude(r => r!.Permissions)
                .FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null
                || !user.IsActive
                || user.IsLocked
                || string.IsNullOrWhiteSpace(user.PasswordHash)) return null;

            var roleIds = user.Roles.Select(r => r.RoleId).Distinct().ToArray();
            var roleNames = user.Roles.Select(r => r.Role!.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var permissionKeys = user.Roles
                .SelectMany(r => r.Role!.Permissions.Select(p => p.PermissionKey))
                .ToList();
            var perms = _registry.Expand(permissionKeys);
            var (readRestrictedEntityKinds, readGrantedEntityKinds) = await GetReadAccessProfileAsync(roleIds, ct);

            return new CovePrincipal
            {
                UserId = user.Id,
                Username = user.Username,
                Kind = PrincipalKind.User,
                Roles = roleNames,
                Permissions = perms,
                ReadRestrictedEntityKinds = readRestrictedEntityKinds,
                ReadGrantedEntityKinds = readGrantedEntityKinds,
                ClaimsPrincipal = p,
                Ip = ip,
                UserAgent = userAgent,
            };
        }
        catch (SecurityTokenExpiredException)
        {
            _log.LogTrace("Bearer token rejected because it is expired");
            return null;
        }
        catch (SecurityTokenMalformedException ex)
        {
            _log.LogDebug(ex, "Bearer token rejected because it is malformed");
            return null;
        }
        catch (SecurityTokenException ex)
        {
            _log.LogDebug(ex, "Bearer token rejected");
            return null;
        }
    }

    private async Task<CovePrincipal?> ResolveApiTokenAsync(string raw, string? ip, string? userAgent, CancellationToken ct)
    {
        // Format: cove_pat_<guid-no-dashes>_<secret>
        var rest = raw["cove_pat_".Length..];
        var split = rest.IndexOf('_');
        if (split < 0) return null;
        var idPart = rest[..split];
        var secret = rest[(split + 1)..];
        if (!Guid.TryParseExact(idPart, "N", out var tokenId)) return null;

        var record = await _db.ApiTokens.AsNoTracking()
            .Include(t => t.User).ThenInclude(u => u!.Roles).ThenInclude(r => r.Role).ThenInclude(r => r!.Permissions)
            .FirstOrDefaultAsync(t => t.Id == tokenId, ct);
        if (record is null || record.RevokedAt is not null) return null;
        if (record.ExpiresAt is { } exp && exp < DateTime.UtcNow) return null;
        if (!BCrypt.Net.BCrypt.Verify(secret, record.TokenHash)) return null;
        if (record.User is null
            || !record.User.IsActive
            || record.User.IsLocked
            || string.IsNullOrWhiteSpace(record.User.PasswordHash)) return null;

        var roleIds = record.User.Roles.Select(r => r.RoleId).Distinct().ToArray();
        var roleNames = record.User.Roles.Select(r => r.Role!.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var basePermissions = record.User.Roles
            .SelectMany(r => r.Role!.Permissions.Select(p => p.PermissionKey))
            .ToList();
        var expanded = _registry.Expand(basePermissions);
        var (readRestrictedEntityKinds, readGrantedEntityKinds) = await GetReadAccessProfileAsync(roleIds, ct);
        HashSet<string>? scopeSet = null;

        // Token scope is intersected with user permissions — never expansive.
        if (!string.IsNullOrEmpty(record.ScopePermissions))
        {
            try
            {
                var scope = JsonSerializer.Deserialize<List<string>>(record.ScopePermissions);
                if (scope is { Count: > 0 })
                {
                    scopeSet = _registry.Expand(scope);
                    expanded = PermissionSet.Intersect(expanded, scopeSet);
                }
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "Ignoring malformed scope permissions for API token {TokenId}", record.Id);
            }
        }

        if (scopeSet is not null)
        {
            readGrantedEntityKinds = readGrantedEntityKinds
                .Where(entityKind =>
                    CovePrincipal.TryGetReadGrantPermission(entityKind, out var readPermission)
                    && PermissionSet.Grants(scopeSet, readPermission))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        // Best-effort last-used update. This must complete before the request pipeline
        // can use the same scoped CoveContext for endpoint work.
        try
        {
            await _db.ApiTokens
                .Where(t => t.Id == tokenId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastUsedAt, DateTime.UtcNow), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to update LastUsedAt for API token {TokenId}", tokenId);
        }

        return new CovePrincipal
        {
            UserId = record.UserId,
            Username = record.User.Username,
            Kind = PrincipalKind.ApiToken,
            Roles = roleNames,
            Permissions = expanded,
            ReadRestrictedEntityKinds = readRestrictedEntityKinds,
            ReadGrantedEntityKinds = readGrantedEntityKinds,
            TokenId = record.Id,
            Ip = ip,
            UserAgent = userAgent,
        };
    }

    public async Task<CovePrincipal?> ResolveExistingUserAsync(
        int userId,
        string? ip,
        string? userAgent,
        CancellationToken ct = default)
    {
        if (userId <= 0)
            return null;

        var user = await _db.Users.AsNoTracking()
            .Include(candidate => candidate.Roles)
                .ThenInclude(assignment => assignment.Role)
                .ThenInclude(role => role!.Permissions)
            .FirstOrDefaultAsync(
                candidate => candidate.Id == userId,
                ct);
        if (user is null
            || !user.IsActive
            || user.IsLocked
            || string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            _log.LogDebug(
                "Externally authenticated Cove user {UserId} was rejected because the account is missing, inactive, locked, or lacks a password",
                userId);
            return null;
        }

        var roleIds = user.Roles.Select(assignment => assignment.RoleId).Distinct().ToArray();
        var roleNames = user.Roles
            .Select(assignment => assignment.Role!.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var permissionKeys = user.Roles
            .SelectMany(assignment => assignment.Role!.Permissions.Select(permission => permission.PermissionKey))
            .ToList();
        var permissions = _registry.Expand(permissionKeys);
        var (readRestrictedEntityKinds, readGrantedEntityKinds) = await GetReadAccessProfileAsync(roleIds, ct);

        return new CovePrincipal
        {
            UserId = user.Id,
            Username = user.Username,
            Kind = PrincipalKind.User,
            Roles = roleNames,
            Permissions = permissions,
            ReadRestrictedEntityKinds = readRestrictedEntityKinds,
            ReadGrantedEntityKinds = readGrantedEntityKinds,
            Ip = ip,
            UserAgent = userAgent,
        };
    }

    [Obsolete("Username-only external authentication is no longer accepted. Resolve a linked Cove user ID.")]
    public Task<CovePrincipal?> ResolveExistingUserAsync(
        string username,
        string? ip,
        string? userAgent,
        CancellationToken ct = default) => Task.FromResult<CovePrincipal?>(null);

    private async Task<(HashSet<string> RestrictedKinds, HashSet<string> GrantedKinds)> GetReadAccessProfileAsync(IEnumerable<int> roleIds, CancellationToken ct)
    {
        var ids = roleIds.Distinct().ToArray();
        if (ids.Length == 0)
            return (
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var contentRules = await _db.RoleContentRules.AsNoTracking()
            .Where(rule => ids.Contains(rule.RoleId) && (rule.AppliesTo == "read" || rule.AppliesTo == "all"))
            .Select(rule => new { rule.EntityKind, rule.Effect })
            .ToListAsync(ct);

        var entityOverrides = await _db.RoleEntityOverrides.AsNoTracking()
            .Where(overrideItem => ids.Contains(overrideItem.RoleId) && (overrideItem.AppliesTo == "read" || overrideItem.AppliesTo == "all"))
            .Select(overrideItem => new { overrideItem.EntityKind, overrideItem.Effect })
            .ToListAsync(ct);

        var restrictedKinds = contentRules
            .Select(rule => rule.EntityKind)
            .Concat(entityOverrides.Select(overrideItem => overrideItem.EntityKind))
            .Where(entityKind => !string.IsNullOrWhiteSpace(entityKind))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var grantedKinds = contentRules
            .Where(rule => string.Equals(rule.Effect, "allow", StringComparison.OrdinalIgnoreCase))
            .Select(rule => rule.EntityKind)
            .Concat(entityOverrides
                .Where(overrideItem => string.Equals(overrideItem.Effect, "allow", StringComparison.OrdinalIgnoreCase))
                .Select(overrideItem => overrideItem.EntityKind))
            .Where(entityKind => !string.IsNullOrWhiteSpace(entityKind))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (restrictedKinds, grantedKinds);
    }

    public async Task<ApiTokenIssued> CreateApiTokenAsync(int userId, string name, IEnumerable<string>? scope, DateTime? expiresAt, CovePrincipal? actor, CancellationToken ct = default)
    {
        var (plain, hash) = NewBCryptToken();
        var id = Guid.NewGuid();
        var prefix = plain[..4];
        var raw = $"cove_pat_{id:N}_{plain}";
        var scopeList = scope?.ToList();
        var entity = new ApiToken
        {
            Id = id,
            UserId = userId,
            Name = name,
            TokenHash = hash,
            Prefix = prefix,
            ScopePermissions = scopeList is { Count: > 0 } ? JsonSerializer.Serialize(scopeList) : null,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
        };
        _db.ApiTokens.Add(entity);
        await _db.SaveChangesAsync(ct);
        return new ApiTokenIssued(id, name, raw, prefix, scopeList, entity.CreatedAt, expiresAt);
    }

    public async Task RevokeApiTokenAsync(Guid id, CovePrincipal? actor, CancellationToken ct = default)
    {
        if (actor?.UserId is not int userId)
            return;

        var affectedRows = await _db.ApiTokens
            .Where(t => t.Id == id && t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTime.UtcNow), ct);
        if (affectedRows > 0 && _audit is not null)
        {
            await _audit.LogAsync(AuditActions.ApiTokenRevoke, AuditOutcomes.Success, actor,
                "api_token", id.ToString(), null, ct);
        }
    }

    public async Task<IReadOnlyList<ApiTokenDto>> ListApiTokensAsync(int userId, CancellationToken ct = default)
    {
        var rows = await _db.ApiTokens.AsNoTracking()
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(t =>
        {
            List<string>? scope = null;
            if (!string.IsNullOrEmpty(t.ScopePermissions))
            {
                try { scope = JsonSerializer.Deserialize<List<string>>(t.ScopePermissions); }
                catch (JsonException ex)
                {
                    _log.LogWarning(ex, "Ignoring malformed scope permissions while listing API token {TokenId}", t.Id);
                }
            }
            return new ApiTokenDto(t.Id, t.Name, t.Prefix, scope, t.CreatedAt, t.LastUsedAt, t.ExpiresAt);
        }).ToList();
    }

    private (string jwt, DateTime expires) IssueJwt(int userId, string username, IEnumerable<string> roleNames, Guid sessionId)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config.Auth.JwtSecret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var issuedAt = DateTime.UtcNow;
        var expires = issuedAt.AddMinutes(GetAccessTokenMinutes());
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, username),
            new(SessionIdClaim, sessionId.ToString("N")),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat, EpochTime.GetIntDate(issuedAt).ToString(), ClaimValueTypes.Integer64),
        };
        foreach (var r in roleNames)
            claims.Add(new Claim(ClaimTypes.Role, r));
        var token = new JwtSecurityToken(JwtIssuer, JwtAudience, claims, notBefore: issuedAt, expires: expires, signingCredentials: creds);
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    private int GetAccessTokenMinutes() => Math.Clamp(_config.Auth.AccessTokenMinutes, 1, 1440);

    private int GetRefreshTokenDays() => Math.Clamp(_config.Auth.RefreshTokenDays, 1, 3650);

    private async Task<UserDto> BuildUserDto(User user, CancellationToken ct)
    {
        var roleNames = await _db.UserRoleAssignments
            .Where(a => a.UserId == user.Id)
            .Select(a => a.Role!.Name)
            .ToListAsync(ct);
        return new UserDto(user.Id, user.Username, user.DisplayName, user.Email,
            user.IsActive, user.IsLocked, user.IsSystem, user.MustChangePassword,
            !string.IsNullOrWhiteSpace(user.PasswordHash),
            user.LastLoginAt, user.LastLoginIp, user.CreatedAt, roleNames,
            UserService.ParseUiPreferences(user.UiPreferencesJson, _log));
    }

    public static string HashToken(string raw)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw)));
    }

    public static (string plain, string sha256Hash) NewOpaqueToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var plain = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return (plain, HashToken(plain));
    }

    public static (string plain, string bcryptHash) NewBCryptToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var plain = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return (plain, BCrypt.Net.BCrypt.HashPassword(plain, workFactor: 12));
    }

    private static string? Trunc(string? s, int max) =>
        s is null ? null : (s.Length <= max ? s : s[..max]);
}
