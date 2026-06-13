using Cove.Core.Auth;
using Cove.Core.Entities.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Cove.Data.Auth;

public sealed class UserService : IUserService
{
    public const int MaxFailedLogins = 8;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private const string InvitePurpose = "invite";
    private const string SetupPurpose = "setup";
    private static readonly TimeSpan InviteTokenTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan SetupTokenTtl = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions UiPreferencesJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly CoveContext _db;
    private readonly IAuditService _audit;
    private readonly ILogger<UserService> _log;

    public UserService(CoveContext db, IAuditService audit, ILogger<UserService> log)
    {
        _db = db;
        _audit = audit;
        _log = log;
    }

    public Task<bool> OwnerExistsAsync(CancellationToken ct = default)
        => _db.Users.AnyAsync(user => user.IsSystem || user.Roles.Any(role => role.Role!.Name == BuiltinRoles.Owner), ct);

    public async Task<UserDto?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        var user = await _db.Users.AsNoTracking()
            .Include(u => u.Roles).ThenInclude(r => r.Role)
            .FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower(), ct);
        return user is null ? null : Map(user);
    }

    public async Task<UserDto?> GetAsync(int id, CancellationToken ct = default)
    {
        var user = await _db.Users.AsNoTracking()
            .Include(u => u.Roles).ThenInclude(r => r.Role)
            .FirstOrDefaultAsync(u => u.Id == id, ct);
        return user is null ? null : Map(user);
    }

    public async Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken ct = default)
    {
        var users = await _db.Users.AsNoTracking()
            .Include(u => u.Roles).ThenInclude(r => r.Role)
            .OrderBy(u => u.Username)
            .ToListAsync(ct);
        return users.Select(Map).ToList();
    }

    public async Task<UserDto> CreateAsync(CreateUserRequest req, CovePrincipal? actor, CancellationToken ct = default)
    {
        Validation.Username(req.Username);
        var hasPassword = !string.IsNullOrWhiteSpace(req.Password);
        if (hasPassword)
            Validation.Password(req.Password!);

        var exists = await _db.Users.AnyAsync(u => u.Username.ToLower() == req.Username.ToLower(), ct);
        if (exists) throw new InvalidOperationException("Username already in use.");

        var user = new User
        {
            Username = req.Username,
            DisplayName = req.DisplayName,
            Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email,
            PasswordHash = hasPassword ? PasswordHasher.HashPassword(req.Password!) : string.Empty,
            PasswordAlgo = PasswordHasher.Algorithm,
            IsActive = true,
            MustChangePassword = req.MustChangePassword || !hasPassword,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        if (req.Roles is { Count: > 0 })
            await SetRolesAsync(user.Id, req.Roles, actor, ct);

        await _audit.LogAsync(AuditActions.UserCreate, AuditOutcomes.Success, actor,
            "user", user.Id.ToString(), new { user.Username }, ct);

        return (await GetAsync(user.Id, ct))!;
    }

    public async Task<UserDto> BootstrapOwnerAsync(string username, string password, CovePrincipal? actor, CancellationToken ct = default)
    {
        if (await OwnerExistsAsync(ct))
            throw new InvalidOperationException("Owner account already exists.");

        Validation.Username(username);
        Validation.Password(password);
        var ownerRole = await EnsureOwnerRoleAsync(ct);
        var now = DateTime.UtcNow;
        var owner = new User
        {
            Username = username.Trim(),
            DisplayName = "Owner",
            PasswordHash = PasswordHasher.HashPassword(password),
            PasswordAlgo = PasswordHasher.Algorithm,
            IsActive = true,
            IsLocked = false,
            IsSystem = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Users.Add(owner);
        await _db.SaveChangesAsync(ct);
        _db.UserRoleAssignments.Add(new UserRoleAssignment
        {
            UserId = owner.Id,
            RoleId = ownerRole.Id,
            GrantedAt = now,
            GrantedByUserId = actor?.UserId,
        });
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActions.UserCreate, AuditOutcomes.Success, actor,
            "user", owner.Id.ToString(), new { owner.Username, role = BuiltinRoles.Owner, bootstrap = true }, ct);

        return (await GetAsync(owner.Id, ct))!;
    }

    public async Task<UserDto> UpdateAsync(int id, UpdateUserRequest req, CovePrincipal? actor, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct)
            ?? throw new KeyNotFoundException("User not found.");
        if (req.DisplayName is not null) user.DisplayName = req.DisplayName;
        if (req.Email is not null) user.Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email;
        if (req.IsActive is { } active)
        {
            if (user.IsSystem && !active) throw new InvalidOperationException("Cannot disable the Owner account.");
            user.IsActive = active;
        }
        if (req.MustChangePassword is { } mcp) user.MustChangePassword = mcp;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActions.UserUpdate, AuditOutcomes.Success, actor,
            "user", user.Id.ToString(), new { user.Username }, ct);

        return (await GetAsync(user.Id, ct))!;
    }

    public async Task DeleteAsync(int id, CovePrincipal? actor, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct)
            ?? throw new KeyNotFoundException("User not found.");
        if (user.IsSystem) throw new InvalidOperationException("Cannot delete the Owner account.");
        _db.Users.Remove(user);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditActions.UserDelete, AuditOutcomes.Success, actor,
            "user", id.ToString(), new { user.Username }, ct);
    }

    public async Task ChangePasswordAsync(int userId, string newPassword, CovePrincipal? actor, CancellationToken ct = default)
    {
        Validation.Password(newPassword);
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("User not found.");
        user.PasswordHash = PasswordHasher.HashPassword(newPassword);
        user.PasswordAlgo = PasswordHasher.Algorithm;
        user.MustChangePassword = false;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditActions.PasswordChange, AuditOutcomes.Success, actor,
            "user", user.Id.ToString(), null, ct);
    }

    public async Task<bool> VerifyPasswordAsync(int userId, string password, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return false;
        if (string.IsNullOrWhiteSpace(user.PasswordHash)) return false;

        var verified = PasswordHasher.Verify(password, user.PasswordHash, user.PasswordAlgo);
        if (!verified) return false;

        if (PasswordHasher.NeedsRehash(user.PasswordHash, user.PasswordAlgo))
        {
            user.PasswordHash = PasswordHasher.HashPassword(password);
            user.PasswordAlgo = PasswordHasher.Algorithm;
            user.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return true;
    }

    public async Task<InviteTokenDto> CreateInviteAsync(int userId, string baseUrl, CovePrincipal? actor, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("User not found.");

        var (plain, hash) = TokenService.NewOpaqueToken();
        var expires = DateTime.UtcNow.Add(InviteTokenTtl);
        _db.UserInviteTokens.Add(new UserInviteToken
        {
            UserId = user.Id,
            TokenHash = hash,
            Purpose = InvitePurpose,
            ExpiresAt = expires,
            CreatedByUserId = actor?.UserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActions.UserInviteCreate, AuditOutcomes.Success, actor,
            "user", user.Id.ToString(), new { user.Username, expiresAt = expires }, ct);

        return new InviteTokenDto(plain, BuildInviteUrl(baseUrl, plain), expires);
    }

    public async Task<InviteTokenDto> CreatePendingInviteAsync(CreateInviteRequest req, string baseUrl, CovePrincipal? actor, CancellationToken ct = default)
    {
        var username = NormalizeOptional(req.Username);
        if (username is not null)
        {
            Validation.Username(username);
            var exists = await _db.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower(), ct);
            if (exists) throw new InvalidOperationException("Username already in use.");
        }

        var roles = NormalizeRoles(req.Roles);
        if (roles.Count > 0)
            await LoadRolesAsync(roles, ct);

        var (plain, hash) = TokenService.NewOpaqueToken();
        var expires = DateTime.UtcNow.Add(InviteTokenTtl);
        _db.UserInviteTokens.Add(new UserInviteToken
        {
            TokenHash = hash,
            Purpose = InvitePurpose,
            Username = username,
            DisplayName = NormalizeOptional(req.DisplayName),
            Email = NormalizeOptional(req.Email),
            RolesJson = roles.Count > 0 ? JsonSerializer.Serialize(roles) : null,
            ExpiresAt = expires,
            CreatedByUserId = actor?.UserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActions.UserInviteCreate, AuditOutcomes.Success, actor,
            "user", username, new { username, expiresAt = expires, usernameRequired = username is null }, ct);

        return new InviteTokenDto(plain, BuildInviteUrl(baseUrl, plain), expires);
    }

    public async Task<InviteTokenInfoDto?> GetInviteInfoAsync(string token, CancellationToken ct = default)
    {
        var row = await FindLiveTokenAsync(token, InvitePurpose, ct);
        if (row is null)
            return null;

        string? username = NormalizeOptional(row.Username);
        if (row.UserId is int userId)
        {
            username = await _db.Users.AsNoTracking()
                .Where(user => user.Id == userId)
                .Select(user => user.Username)
                .FirstOrDefaultAsync(ct);
            if (username is null)
                return null;
        }

        return new InviteTokenInfoDto(true, username is null, username, row.ExpiresAt);
    }

    public async Task<UserDto> RedeemInviteAsync(string token, string password, string? username, CovePrincipal? actor, CancellationToken ct = default)
    {
        Validation.Password(password);
        var row = await FindLiveTokenAsync(token, InvitePurpose, ct);
        if (row is null)
            throw new InviteTokenException("Invite token is invalid or expired.");

        if (row.UserId is int userId)
            return await RedeemExistingUserInviteAsync(row, userId, password, username, actor, ct);

        return await RedeemPendingUserInviteAsync(row, password, username, actor, ct);
    }

    private async Task<UserDto> RedeemExistingUserInviteAsync(UserInviteToken row, int userId, string password, string? username, CovePrincipal? actor, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new InviteTokenException("Invite token is invalid or expired.");

        var requestedUsername = NormalizeOptional(username);
        if (requestedUsername is not null && !string.Equals(requestedUsername, user.Username, StringComparison.OrdinalIgnoreCase))
            throw new InviteTokenException("This invite is locked to its assigned username.");

        user.PasswordHash = PasswordHasher.HashPassword(password);
        user.PasswordAlgo = PasswordHasher.Algorithm;
        user.MustChangePassword = false;
        user.IsActive = true;
        user.IsLocked = false;
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.UpdatedAt = DateTime.UtcNow;
        row.ConsumedAt = DateTime.UtcNow;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActions.UserInviteRedeem, AuditOutcomes.Success, actor,
            "user", user.Id.ToString(), new { user.Username }, ct);

        return (await GetAsync(user.Id, ct))!;
    }

    private async Task<UserDto> RedeemPendingUserInviteAsync(UserInviteToken row, string password, string? username, CovePrincipal? actor, CancellationToken ct)
    {
        var lockedUsername = NormalizeOptional(row.Username);
        var requestedUsername = NormalizeOptional(username);
        if (lockedUsername is not null && requestedUsername is not null && !string.Equals(lockedUsername, requestedUsername, StringComparison.OrdinalIgnoreCase))
            throw new InviteTokenException("This invite is locked to its assigned username.");

        var finalUsername = lockedUsername ?? requestedUsername;
        if (finalUsername is null)
            throw new InviteTokenException("Username is required for this invite.");

        Validation.Username(finalUsername);
        var exists = await _db.Users.AnyAsync(u => u.Username.ToLower() == finalUsername.ToLower(), ct);
        if (exists) throw new InviteTokenException("Username already in use.");

        var roles = DeserializeRoles(row.RolesJson);
        var roleRows = roles.Count > 0 ? await LoadRolesAsync(roles, ct) : new List<Role>();
        var now = DateTime.UtcNow;
        var user = new User
        {
            Username = finalUsername,
            DisplayName = NormalizeOptional(row.DisplayName),
            Email = NormalizeOptional(row.Email),
            PasswordHash = PasswordHasher.HashPassword(password),
            PasswordAlgo = PasswordHasher.Algorithm,
            IsActive = true,
            IsLocked = false,
            MustChangePassword = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        foreach (var role in roleRows)
        {
            _db.UserRoleAssignments.Add(new UserRoleAssignment
            {
                UserId = user.Id,
                RoleId = role.Id,
                GrantedAt = now,
                GrantedByUserId = actor?.UserId,
            });
        }

        row.UserId = user.Id;
        row.ConsumedAt = now;
        row.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActions.UserCreate, AuditOutcomes.Success, actor,
            "user", user.Id.ToString(), new { user.Username, invite = true }, ct);
        await _audit.LogAsync(AuditActions.UserInviteRedeem, AuditOutcomes.Success, actor,
            "user", user.Id.ToString(), new { user.Username }, ct);

        return (await GetAsync(user.Id, ct))!;
    }

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static IReadOnlyList<string> NormalizeRoles(IEnumerable<string>? roles)
    {
        return roles?
            .Select(role => role.Trim())
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? [];
    }

    private static IReadOnlyList<string> DeserializeRoles(string? rolesJson)
    {
        if (string.IsNullOrWhiteSpace(rolesJson))
            return [];

        try
        {
            return NormalizeRoles(JsonSerializer.Deserialize<List<string>>(rolesJson));
        }
        catch
        {
            return [];
        }
    }

    private async Task<List<Role>> LoadRolesAsync(IReadOnlyList<string> roleNames, CancellationToken ct)
    {
        var roles = await _db.Roles.Where(role => roleNames.Contains(role.Name)).ToListAsync(ct);
        if (roles.Count != roleNames.Count)
        {
            var missing = roleNames.Except(roles.Select(role => role.Name), StringComparer.OrdinalIgnoreCase);
            throw new InvalidOperationException($"Unknown role(s): {string.Join(", ", missing)}");
        }

        return roles;
    }

    public async Task<SetupTokenDto> CreateSetupTokenAsync(CovePrincipal? actor, CancellationToken ct = default)
    {
        var (plain, hash) = TokenService.NewOpaqueToken();
        var expires = DateTime.UtcNow.Add(SetupTokenTtl);
        _db.UserInviteTokens.Add(new UserInviteToken
        {
            TokenHash = hash,
            Purpose = SetupPurpose,
            ExpiresAt = expires,
            CreatedByUserId = actor?.UserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActions.AuthSetupTokenCreate, AuditOutcomes.Success, actor,
            "auth", "setup", new { expiresAt = expires }, ct);

        return new SetupTokenDto(plain, expires);
    }

    public Task<bool> HasSetupTokenAsync(CancellationToken ct = default)
        => _db.UserInviteTokens.AnyAsync(token => token.Purpose == SetupPurpose
            && token.ConsumedAt == null
            && token.ExpiresAt > DateTime.UtcNow, ct);

    public async Task<UserDto> RedeemSetupTokenAsync(string token, string password, string? username, CovePrincipal? actor, CancellationToken ct = default)
    {
        var row = await FindLiveTokenAsync(token, SetupPurpose, ct)
            ?? throw new InviteTokenException("Setup token is invalid or expired.");

        var owner = await _db.Users
            .Include(user => user.Roles).ThenInclude(role => role.Role)
            .FirstOrDefaultAsync(user => user.IsSystem || user.Roles.Any(role => role.Role!.Name == BuiltinRoles.Owner), ct);

        UserDto dto;
        if (owner is null)
        {
            dto = await BootstrapOwnerAsync(string.IsNullOrWhiteSpace(username) ? "owner" : username.Trim(), password, actor, ct);
        }
        else
        {
            await ChangePasswordAsync(owner.Id, password, actor, ct);
            dto = (await GetAsync(owner.Id, ct))!;
        }

        row.ConsumedAt = DateTime.UtcNow;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActions.AuthSetupTokenRedeem, AuditOutcomes.Success, actor,
            "user", dto.Id.ToString(), new { dto.Username }, ct);

        return dto;
    }

    public async Task SetRolesAsync(int userId, IEnumerable<string> roleNames, CovePrincipal? actor, CancellationToken ct = default)
    {
        var nameList = roleNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var roles = await _db.Roles.Where(r => nameList.Contains(r.Name)).ToListAsync(ct);
        if (roles.Count != nameList.Count)
        {
            var missing = nameList.Except(roles.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            throw new InvalidOperationException($"Unknown role(s): {string.Join(", ", missing)}");
        }

        await _db.UserRoleAssignments.Where(r => r.UserId == userId).ExecuteDeleteAsync(ct);
        foreach (var r in roles)
            _db.UserRoleAssignments.Add(new UserRoleAssignment
            {
                UserId = userId,
                RoleId = r.Id,
                GrantedAt = DateTime.UtcNow,
                GrantedByUserId = actor?.UserId,
            });
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditActions.RoleGrant, AuditOutcomes.Success, actor,
            "user", userId.ToString(), new { roles = nameList }, ct);
    }

    public async Task<UserUiPreferencesDto?> UpdateUiPreferencesAsync(int userId, UserUiPreferencesDto preferences, CovePrincipal? actor, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("User not found.");

        var normalized = NormalizeUiPreferences(preferences);
        user.UiPreferencesJson = SerializeUiPreferences(normalized);
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActions.UserUpdate, AuditOutcomes.Success, actor,
            "user", user.Id.ToString(), new { user.Username, field = "ui_preferences" }, ct);

        return normalized;
    }

    public async Task RecordLoginSuccessAsync(int userId, string? ip, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await _db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.LastLoginAt, now)
            .SetProperty(u => u.LastLoginIp, ip is null ? null : (ip.Length > 64 ? ip[..64] : ip))
            .SetProperty(u => u.FailedLoginCount, 0)
            .SetProperty(u => u.IsLocked, false)
            .SetProperty(u => u.LockedUntil, (DateTime?)null), ct);
    }

    public async Task RecordLoginFailureAsync(int userId, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return;
        user.FailedLoginCount++;
        if (user.FailedLoginCount >= MaxFailedLogins)
        {
            user.IsLocked = true;
            user.LockedUntil = DateTime.UtcNow.Add(LockoutDuration);
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task UnlockAsync(int userId, CovePrincipal? actor, CancellationToken ct = default)
    {
        await _db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.IsLocked, false)
            .SetProperty(u => u.FailedLoginCount, 0)
            .SetProperty(u => u.LockedUntil, (DateTime?)null), ct);
        await _audit.LogAsync(AuditActions.UserUnlock, AuditOutcomes.Success, actor,
            "user", userId.ToString(), null, ct);
    }

    private UserDto Map(User u) => new(
        u.Id, u.Username, u.DisplayName, u.Email,
        u.IsActive, u.IsLocked, u.IsSystem, u.MustChangePassword,
        !string.IsNullOrWhiteSpace(u.PasswordHash),
        u.LastLoginAt, u.LastLoginIp, u.CreatedAt,
        u.Roles.Select(r => r.Role!.Name).ToList(),
        ParseUiPreferences(u.UiPreferencesJson, _log));

    private async Task<Role> EnsureOwnerRoleAsync(CancellationToken ct)
    {
        var wildcardPermission = await _db.Permissions.FirstOrDefaultAsync(p => p.Key == Permissions.All, ct);
        if (wildcardPermission is null)
        {
            _db.Permissions.Add(new Permission
            {
                Key = Permissions.All,
                Category = "System",
                Description = "All permissions.",
                Source = "core",
                Dangerous = true,
                Implies = "[]",
                IsOrphaned = false,
                RegisteredAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(ct);
        }

        var role = await _db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Name == BuiltinRoles.Owner, ct);
        if (role is not null)
        {
            if (!role.Permissions.Any(p => p.PermissionKey == Permissions.All))
            {
                _db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionKey = Permissions.All });
                await _db.SaveChangesAsync(ct);
            }
            return role;
        }

        role = new Role
        {
            Name = BuiltinRoles.Owner,
            Description = "Superuser; bypasses all checks.",
            IsBuiltin = true,
            IsSystem = true,
            Source = "core",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Roles.Add(role);
        await _db.SaveChangesAsync(ct);
        _db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionKey = Permissions.All });
        await _db.SaveChangesAsync(ct);
        return role;
    }

    private async Task<UserInviteToken?> FindLiveTokenAsync(string rawToken, string purpose, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            return null;

        var hash = TokenService.HashToken(rawToken.Trim());
        var row = await _db.UserInviteTokens.FirstOrDefaultAsync(token => token.TokenHash == hash && token.Purpose == purpose, ct);
        if (row is null || row.ConsumedAt is not null || row.ExpiresAt <= DateTime.UtcNow)
            return null;

        return row;
    }

    private static string BuildInviteUrl(string baseUrl, string token)
    {
        var normalizedBase = string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : baseUrl.TrimEnd('/');
        return $"{normalizedBase}/auth/redeem-invite?token={Uri.EscapeDataString(token)}";
    }

    public static UserUiPreferencesDto? ParseUiPreferences(string? raw, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            var parsed = NormalizeUiPreferences(JsonSerializer.Deserialize<UserUiPreferencesDto>(raw, UiPreferencesJsonOptions));
            using var document = JsonDocument.Parse(raw);
            var legacyTrackingEnabledKey = "record" + "PlaybackHistory";
            if (parsed?.Tracking is null
                && document.RootElement.TryGetProperty(legacyTrackingEnabledKey, out var legacyEnabled)
                && legacyEnabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var tracking = new UserTrackingPreferencesDto(legacyEnabled.GetBoolean(), null, null, null, null, null);
                parsed = parsed is null
                    ? new UserUiPreferencesDto(null, null, tracking, null, null)
                    : parsed with { Tracking = tracking };
            }

            return NormalizeUiPreferences(parsed);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Ignoring malformed user UI preferences JSON");
            return null;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Ignoring invalid user UI preferences");
            return null;
        }
    }

    public static string? SerializeUiPreferences(UserUiPreferencesDto? preferences)
    {
        var normalized = NormalizeUiPreferences(preferences);
        if (normalized is null)
        {
            return null;
        }

        return JsonSerializer.Serialize(normalized, UiPreferencesJsonOptions);
    }

    private static UserUiPreferencesDto? NormalizeUiPreferences(UserUiPreferencesDto? preferences)
    {
        if (preferences is null)
        {
            return null;
        }

        var theme = NormalizeThemePreferences(preferences.Theme);
        var ratingSystemOptions = NormalizeRatingSystemOptions(preferences.RatingSystemOptions);
        var tracking = NormalizeTrackingPreferences(preferences.Tracking);
        var videos = NormalizeVideosPreferences(preferences.Videos);
        var playback = NormalizePlaybackPreferences(preferences.Playback);
        var keybindingOverrides = NormalizeKeybindingOverrides(preferences.KeybindingOverrides);
        var homePageContent = string.IsNullOrWhiteSpace(preferences.HomePageContent) ? null : preferences.HomePageContent;
        var defaultFilters = NormalizeDefaultFilters(preferences.DefaultFilters);
        if (theme is null && ratingSystemOptions is null && tracking is null && videos is null && playback is null
            && keybindingOverrides is null && homePageContent is null && defaultFilters is null)
        {
            return null;
        }

        return new UserUiPreferencesDto(theme, ratingSystemOptions, tracking, videos, keybindingOverrides, playback, homePageContent, defaultFilters);
    }

    private static UserVideosPreferencesDto? NormalizeVideosPreferences(UserVideosPreferencesDto? videos)
    {
        if (videos?.IncludeCompilationGroups is not bool includeCompilationGroups)
        {
            return null;
        }

        return new UserVideosPreferencesDto(includeCompilationGroups);
    }

    private static UserPlaybackPreferencesDto? NormalizePlaybackPreferences(UserPlaybackPreferencesDto? playback)
    {
        if (playback?.SkipSeconds is not int skipSeconds)
        {
            return null;
        }

        return new UserPlaybackPreferencesDto(Math.Clamp(skipSeconds, 1, 300));
    }

    private static Dictionary<string, string>? NormalizeKeybindingOverrides(Dictionary<string, string>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return null;
        }

        var normalized = overrides
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && !string.IsNullOrWhiteSpace(entry.Value))
            .ToDictionary(entry => entry.Key.Trim(), entry => entry.Value.Trim(), StringComparer.OrdinalIgnoreCase);

        return normalized.Count > 0 ? normalized : null;
    }

    private static Dictionary<string, string>? NormalizeDefaultFilters(Dictionary<string, string>? defaultFilters)
    {
        if (defaultFilters is null || defaultFilters.Count == 0)
        {
            return null;
        }

        var normalized = defaultFilters
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && !string.IsNullOrWhiteSpace(entry.Value))
            .ToDictionary(entry => entry.Key.Trim().ToLowerInvariant(), entry => entry.Value.Trim(), StringComparer.OrdinalIgnoreCase);

        return normalized.Count > 0 ? normalized : null;
    }

    private static UserTrackingPreferencesDto? NormalizeTrackingPreferences(UserTrackingPreferencesDto? tracking)
    {
        if (tracking is null)
        {
            return null;
        }

        int? minViewSeconds = tracking.MinViewSeconds.HasValue
            ? Math.Clamp(tracking.MinViewSeconds.Value, 0, 86_400)
            : null;
        double? viewCompletionRatio = tracking.ViewCompletionRatio.HasValue
            ? Math.Clamp(tracking.ViewCompletionRatio.Value, 0.01d, 1d)
            : null;
        int? minImageDetailViewSeconds = tracking.MinImageDetailViewSeconds.HasValue
            ? Math.Clamp(tracking.MinImageDetailViewSeconds.Value, 0, 86_400)
            : null;
        int? minDerivedLikeSessionSeconds = tracking.MinDerivedLikeSessionSeconds.HasValue
            ? Math.Clamp(tracking.MinDerivedLikeSessionSeconds.Value, 0, 86_400)
            : null;
        int? sessionIdleTimeoutSec = tracking.SessionIdleTimeoutSec.HasValue
            ? Math.Clamp(tracking.SessionIdleTimeoutSec.Value, 10, 86_400)
            : null;

        if (tracking.Enabled is null
            && minViewSeconds is null
            && viewCompletionRatio is null
            && minImageDetailViewSeconds is null
            && minDerivedLikeSessionSeconds is null
            && sessionIdleTimeoutSec is null)
        {
            return null;
        }

        return new UserTrackingPreferencesDto(
            tracking.Enabled,
            minViewSeconds,
            viewCompletionRatio,
            minImageDetailViewSeconds,
            minDerivedLikeSessionSeconds,
            sessionIdleTimeoutSec);
    }

    private static UserThemePreferencesDto? NormalizeThemePreferences(UserThemePreferencesDto? theme)
    {
        if (theme is null)
        {
            return null;
        }

        var activeThemeId = string.IsNullOrWhiteSpace(theme.ActiveThemeId) ? null : theme.ActiveThemeId.Trim();
        var activeComponentStyles = theme.ActiveComponentStyles?
            .Select(style => style?.Trim())
            .Where(style => !string.IsNullOrWhiteSpace(style))
            .Select(style => style!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var activeLayoutStyle = string.IsNullOrWhiteSpace(theme.ActiveLayoutStyle) ? null : theme.ActiveLayoutStyle.Trim();

        Dictionary<string, string>? customThemeColors = null;
        if (theme.CustomThemeColors is { Count: > 0 })
        {
            customThemeColors = theme.CustomThemeColors
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && !string.IsNullOrWhiteSpace(entry.Value))
                .ToDictionary(entry => entry.Key.Trim(), entry => entry.Value.Trim(), StringComparer.OrdinalIgnoreCase);
            if (customThemeColors.Count == 0)
            {
                customThemeColors = null;
            }
        }

        Dictionary<string, Dictionary<string, string>>? styleOptions = null;
        if (theme.StyleOptions is { Count: > 0 })
        {
            styleOptions = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (styleId, options) in theme.StyleOptions)
            {
                if (string.IsNullOrWhiteSpace(styleId) || options is null || options.Count == 0)
                {
                    continue;
                }

                var normalizedOptions = options
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && !string.IsNullOrWhiteSpace(entry.Value))
                    .ToDictionary(entry => entry.Key.Trim(), entry => entry.Value.Trim(), StringComparer.OrdinalIgnoreCase);
                if (normalizedOptions.Count > 0)
                {
                    styleOptions[styleId.Trim()] = normalizedOptions;
                }
            }

            if (styleOptions.Count == 0)
            {
                styleOptions = null;
            }
        }

        if (activeThemeId is null
            && (activeComponentStyles is null || activeComponentStyles.Length == 0)
            && activeLayoutStyle is null
            && customThemeColors is null
            && styleOptions is null)
        {
            return null;
        }

        return new UserThemePreferencesDto(activeThemeId, activeComponentStyles, activeLayoutStyle, customThemeColors, styleOptions);
    }

    private static UserRatingSystemOptionsDto? NormalizeRatingSystemOptions(UserRatingSystemOptionsDto? ratingSystemOptions)
    {
        if (ratingSystemOptions is null)
        {
            return null;
        }

        var type = ratingSystemOptions.Type?.Trim().ToLowerInvariant() switch
        {
            "stars" => "stars",
            "decimal" => "decimal",
            _ => null,
        };
        if (type is null)
        {
            return null;
        }

        var starPrecision = ratingSystemOptions.StarPrecision?.Trim().ToLowerInvariant() switch
        {
            "full" => "full",
            "half" => "half",
            "quarter" => "quarter",
            "tenth" => "tenth",
            _ => "full",
        };

        return new UserRatingSystemOptionsDto(type, starPrecision);
    }

    public static class Validation
    {
        public static void Username(string username)
        {
            if (string.IsNullOrWhiteSpace(username) || username.Length < 2 || username.Length > 64)
                throw new InvalidOperationException("Username must be 2-64 characters.");
            foreach (var c in username)
                if (!(char.IsLetterOrDigit(c) || c is '_' or '-' or '.'))
                    throw new InvalidOperationException("Username may only contain letters, digits, '_', '-', '.'.");
        }
        public static void Password(string pw)
        {
            if (string.IsNullOrEmpty(pw) || pw.Length < 8 || pw.Length > 200)
                throw new InvalidOperationException("Password must be 8-200 characters.");
        }
    }
}

public sealed class RoleService : IRoleService
{
    private readonly CoveContext _db;
    private readonly IPermissionRegistry _registry;
    private readonly IAuditService _audit;

    public RoleService(CoveContext db, IPermissionRegistry registry, IAuditService audit)
    {
        _db = db;
        _registry = registry;
        _audit = audit;
    }

    public async Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken ct = default)
    {
        var roles = await _db.Roles.AsNoTracking()
            .Include(r => r.Permissions)
            .Include(r => r.Users)
            .OrderBy(r => r.Name)
            .ToListAsync(ct);
        return roles.Select(Map).ToList();
    }

    public async Task<RoleDto?> GetAsync(int id, CancellationToken ct = default)
    {
        var r = await _db.Roles.AsNoTracking()
            .Include(x => x.Permissions)
            .Include(x => x.Users)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        return r is null ? null : Map(r);
    }

    public async Task<RoleDto?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        var r = await _db.Roles.AsNoTracking()
            .Include(x => x.Permissions)
            .Include(x => x.Users)
            .FirstOrDefaultAsync(x => x.Name.ToLower() == name.ToLower(), ct);
        return r is null ? null : Map(r);
    }

    public async Task<RoleDto> CreateAsync(CreateRoleRequest req, CovePrincipal? actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new InvalidOperationException("Role name is required.");
        var exists = await _db.Roles.AnyAsync(r => r.Name.ToLower() == req.Name.ToLower(), ct);
        if (exists) throw new InvalidOperationException("Role name already in use.");
        ValidatePermissions(req.Permissions);

        var role = new Role
        {
            Name = req.Name,
            Description = req.Description,
            IsBuiltin = false,
            IsSystem = false,
            Source = "core",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Roles.Add(role);
        await _db.SaveChangesAsync(ct);

        foreach (var p in req.Permissions.Distinct())
            _db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionKey = p });
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActions.RoleCreate, AuditOutcomes.Success, actor,
            "role", role.Id.ToString(), new { role.Name }, ct);

        return (await GetAsync(role.Id, ct))!;
    }

    public async Task<RoleDto> UpdateAsync(int id, UpdateRoleRequest req, CovePrincipal? actor, CancellationToken ct = default)
    {
        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new KeyNotFoundException("Role not found.");

        if (req.Description is not null) role.Description = req.Description;
        if (req.Permissions is { } permissions)
        {
            ValidatePermissions(permissions);
            if (role.IsSystem)
            {
                // Owner role: must always include "*"
                if (!permissions.Contains("*"))
                    throw new InvalidOperationException("Cannot remove '*' from the Owner role.");
            }
            await SetPermissionsAsync(id, permissions, actor, ct);
        }
        role.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActions.RoleUpdate, AuditOutcomes.Success, actor,
            "role", role.Id.ToString(), new { role.Name }, ct);

        return (await GetAsync(role.Id, ct))!;
    }

    public async Task DeleteAsync(int id, CovePrincipal? actor, CancellationToken ct = default)
    {
        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new KeyNotFoundException("Role not found.");
        if (role.IsBuiltin) throw new InvalidOperationException("Built-in roles cannot be deleted.");
        _db.Roles.Remove(role);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditActions.RoleDelete, AuditOutcomes.Success, actor,
            "role", id.ToString(), new { role.Name }, ct);
    }

    public async Task SetPermissionsAsync(int roleId, IEnumerable<string> permissions, CovePrincipal? actor, CancellationToken ct = default)
    {
        var list = permissions.Distinct(StringComparer.Ordinal).ToList();
        ValidatePermissions(list);
        await _db.RolePermissions.Where(r => r.RoleId == roleId).ExecuteDeleteAsync(ct);
        foreach (var p in list)
            _db.RolePermissions.Add(new RolePermission { RoleId = roleId, PermissionKey = p });
        await _db.SaveChangesAsync(ct);
    }

    private void ValidatePermissions(IEnumerable<string> permissions)
    {
        foreach (var p in permissions)
        {
            if (string.IsNullOrWhiteSpace(p))
                throw new InvalidOperationException("Empty permission key.");
            if (p == "*") continue;
            if (p.EndsWith(".*", StringComparison.Ordinal))
            {
                var resource = p[..^2];
                if (string.IsNullOrEmpty(resource)) throw new InvalidOperationException($"Invalid permission '{p}'.");
                continue;
            }
            if (p.StartsWith("*.", StringComparison.Ordinal)) continue;
            if (!_registry.IsKnown(p))
                throw new InvalidOperationException($"Unknown permission '{p}'.");
        }
    }

    private static RoleDto Map(Role r) => new(
        r.Id, r.Name, r.Description, r.IsBuiltin, r.IsSystem, r.Source,
        r.Permissions.Select(p => p.PermissionKey).OrderBy(x => x).ToList(),
        r.Users.Count);
}

