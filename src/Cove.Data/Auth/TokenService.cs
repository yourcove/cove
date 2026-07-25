using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Cove.Data.Auth;

public sealed class TokenService : ITokenService
{
    public const int DefaultRefreshDays = 30;
    public const string JwtIssuer = "Cove";
    public const string JwtAudience = "Cove";
    private const string SessionIdClaim = "cove_session_id";

    private readonly CoveContext _db;
    private readonly CoveConfiguration _config;
    private readonly IPermissionRegistry _registry;
    private readonly ILogger<TokenService> _log;

    public TokenService(CoveContext db, CoveConfiguration config, IPermissionRegistry registry, ILogger<TokenService> log)
    {
        _db = db;
        _config = config;
        _registry = registry;
        _log = log;
    }

    public async Task<TokenPair> IssueForUserAsync(int userId, string? ip, string? userAgent, CancellationToken ct = default)
    {
        var user = await _db.Users.AsNoTracking()
            .Include(u => u.Roles).ThenInclude(r => r.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnauthorizedException("User not found.");
        if (!user.IsActive || user.IsLocked)
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
        var hash = HashToken(refreshToken);
        var existing = await _db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
            ?? throw new UnauthorizedException("Invalid refresh token.");

        if (existing.RevokedAt is not null)
        {
            // Reuse-detection: already-rotated token presented again. Burn the chain.
            await RevokeChainInternalAsync(existing, ct);
            throw new UnauthorizedException("Refresh token reuse detected; chain revoked.");
        }
        if (existing.ExpiresAt < DateTime.UtcNow)
            throw new UnauthorizedException("Refresh token expired.");
        if (existing.User is null || !existing.User.IsActive || existing.User.IsLocked)
            throw new UnauthorizedException("Account is not active.");

        // Mark current as revoked + rotate to a new child.
        existing.RevokedAt = DateTime.UtcNow;
        existing.LastUsedAt = DateTime.UtcNow;

        var (newPlain, newHash) = NewOpaqueToken();
        var refreshExpires = DateTime.UtcNow.AddDays(GetRefreshTokenDays());
        var refreshId = Guid.NewGuid();
        var rotated = new RefreshToken
        {
            Id = refreshId,
            UserId = existing.UserId,
            ParentId = existing.Id,
            TokenHash = newHash,
            UserAgent = Trunc(userAgent, 500),
            Ip = Trunc(ip, 64),
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
            ExpiresAt = refreshExpires,
        };
        _db.RefreshTokens.Add(rotated);

        var roleNames = await _db.UserRoleAssignments
            .Where(r => r.UserId == existing.UserId)
            .Select(r => r.Role!.Name)
            .ToListAsync(ct);
        var (jwt, jwtExpires) = IssueJwt(existing.UserId, existing.User.Username, roleNames, refreshId);

        await _db.SaveChangesAsync(ct);

        var dto = await BuildUserDto(existing.User, ct);
        return new TokenPair(jwt, newPlain, jwtExpires, refreshExpires, dto);
    }

    public async Task RevokeChainAsync(string refreshToken, CancellationToken ct = default)
    {
        var hash = HashToken(refreshToken);
        var token = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null) return;
        await RevokeChainInternalAsync(token, ct);
    }

    private async Task RevokeChainInternalAsync(RefreshToken anyTokenInChain, CancellationToken ct)
    {
        // Walk to the chain root by repeatedly following ParentId, then revoke every descendant.
        var rootId = anyTokenInChain.Id;
        var current = anyTokenInChain;
        while (current.ParentId is { } pid)
        {
            var parent = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.Id == pid, ct);
            if (parent is null) break;
            rootId = parent.Id;
            current = parent;
        }
        // BFS down the chain
        var ids = new HashSet<Guid> { rootId };
        var frontier = new List<Guid> { rootId };
        while (frontier.Count > 0)
        {
            var children = await _db.RefreshTokens
                .Where(t => t.ParentId != null && frontier.Contains(t.ParentId.Value))
                .Select(t => t.Id)
                .ToListAsync(ct);
            frontier = [];
            foreach (var c in children)
                if (ids.Add(c)) frontier.Add(c);
        }
        var now = DateTime.UtcNow;
        await _db.RefreshTokens
            .Where(t => ids.Contains(t.Id) && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
    }

    public Task RevokeAllForUserAsync(int userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
    }

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
            if (user is null || !user.IsActive || user.IsLocked) return null;

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
        if (record.User is null || !record.User.IsActive || record.User.IsLocked) return null;

        var roleIds = record.User.Roles.Select(r => r.RoleId).Distinct().ToArray();
        var roleNames = record.User.Roles.Select(r => r.Role!.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var basePermissions = record.User.Roles
            .SelectMany(r => r.Role!.Permissions.Select(p => p.PermissionKey))
            .ToList();
        var expanded = _registry.Expand(basePermissions);
        var (readRestrictedEntityKinds, readGrantedEntityKinds) = await GetReadAccessProfileAsync(roleIds, ct);

        // Token scope is intersected with user permissions — never expansive.
        if (!string.IsNullOrEmpty(record.ScopePermissions))
        {
            try
            {
                var scope = JsonSerializer.Deserialize<List<string>>(record.ScopePermissions);
                if (scope is { Count: > 0 })
                {
                    var scopeSet = _registry.Expand(scope);
                    expanded.IntersectWith(scopeSet);
                }
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "Ignoring malformed scope permissions for API token {TokenId}", record.Id);
            }
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
        await _db.ApiTokens
            .Where(t => t.Id == id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTime.UtcNow), ct);
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
