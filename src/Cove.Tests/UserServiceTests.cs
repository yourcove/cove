using Cove.Core.Auth;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Auth;
using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

/// <summary>
/// Integration tests for UserService against an in-memory CoveContext.
/// Focused on lockout, password verification, and audit emission behavior.
/// </summary>
public class UserServiceTests
{
    private static CoveContext NewDb(string name = "users")
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"{name}-{Guid.NewGuid():N}")
            .Options;
        return new TestCoveContext(options);
    }

    [Fact]
    public async Task RecordLoginFailure_locks_account_after_threshold()
    {
        await using var db = NewDb("lockout");
        db.Users.Add(new User
        {
            Username = "bob",
            DisplayName = "Bob",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("correct-horse-battery-staple", workFactor: 4),
            PasswordAlgo = "bcrypt",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var userId = (await db.Users.AsNoTracking().FirstAsync(u => u.Username == "bob", cancellationToken: TestContext.Current.CancellationToken)).Id;

        var svc = new UserService(db, new NoopAudit(), NullLogger<UserService>.Instance);

        for (var i = 0; i < UserService.MaxFailedLogins - 1; i++)
            await svc.RecordLoginFailureAsync(userId, TestContext.Current.CancellationToken);

        var midway = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(midway.IsLocked);
        Assert.Equal(UserService.MaxFailedLogins - 1, midway.FailedLoginCount);

        await svc.RecordLoginFailureAsync(userId, TestContext.Current.CancellationToken);

        var locked = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(locked.IsLocked);
        Assert.Equal(UserService.MaxFailedLogins, locked.FailedLoginCount);
        Assert.NotNull(locked.LockedUntil);
    }

    [Fact]
    public async Task VerifyPassword_returns_false_for_wrong_password()
    {
        await using var db = NewDb("verify");
        db.Users.Add(new User
        {
            Username = "alice",
            DisplayName = "Alice",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("hunter2", workFactor: 4),
            PasswordAlgo = "bcrypt",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var userId = (await db.Users.AsNoTracking().FirstAsync(cancellationToken: TestContext.Current.CancellationToken)).Id;

        var svc = new UserService(db, new NoopAudit(), NullLogger<UserService>.Instance);
        Assert.True(await svc.VerifyPasswordAsync(userId, "hunter2", TestContext.Current.CancellationToken));
        Assert.False(await svc.VerifyPasswordAsync(userId, "wrong", TestContext.Current.CancellationToken));
        Assert.False(await svc.VerifyPasswordAsync(99999, "anything", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Username_validation_rejects_empty_and_too_long()
    {
        Assert.Throws<InvalidOperationException>(() => UserService.Validation.Username(""));
        Assert.Throws<InvalidOperationException>(() => UserService.Validation.Username(new string('a', 200)));
        UserService.Validation.Username("good_name");
    }

    [Fact]
    public void Password_validation_rejects_short()
    {
        Assert.Throws<InvalidOperationException>(() => UserService.Validation.Password("short"));
        UserService.Validation.Password("longenough123");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Direct_user_creation_requires_a_password(string? password)
    {
        await using var db = NewDb($"required-password-{Guid.NewGuid():N}");
        var svc = new UserService(db, new NoopAudit(), NullLogger<UserService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(new CreateUserRequest("password-required", password!), null, TestContext.Current.CancellationToken));
        Assert.Empty(db.Users);
    }

    [Fact]
    public async Task BootstrapOwner_creates_single_owner_account()
    {
        await using var db = NewDb("bootstrap-owner");
        var svc = new UserService(db, new NoopAudit(), NullLogger<UserService>.Instance);

        Assert.False(await svc.OwnerExistsAsync(TestContext.Current.CancellationToken));

        var owner = await svc.BootstrapOwnerAsync("owner", "longenough123", null, TestContext.Current.CancellationToken);

        Assert.True(owner.IsSystem);
        Assert.True(owner.HasPassword);
        Assert.Contains(BuiltinRoles.Owner, owner.Roles);
        Assert.True(await svc.OwnerExistsAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.BootstrapOwnerAsync("other", "longenough123", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Existing_user_invite_can_reset_password_once()
    {
        await using var db = NewDb("invite-redeem");
        var svc = new UserService(db, new NoopAudit(), NullLogger<UserService>.Instance);
        var user = await svc.CreateAsync(new CreateUserRequest("invitee", "oldpassword123", DisplayName: "Invitee"), null, TestContext.Current.CancellationToken);

        Assert.True(user.HasPassword);

        var invite = await svc.CreateInviteAsync(user.Id, "http://cove.local", null, TestContext.Current.CancellationToken);
        Assert.Contains("/auth/redeem-invite?token=", invite.Url, StringComparison.Ordinal);

        var redeemed = await svc.RedeemInviteAsync(invite.Token, "newpassword123", null, null, TestContext.Current.CancellationToken);

        Assert.True(redeemed.HasPassword);
        Assert.False(redeemed.MustChangePassword);
        Assert.False(await svc.VerifyPasswordAsync(user.Id, "oldpassword123", TestContext.Current.CancellationToken));
        Assert.True(await svc.VerifyPasswordAsync(user.Id, "newpassword123", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InviteTokenException>(() => svc.RedeemInviteAsync(invite.Token, "anotherpass123", null, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Pending_invite_can_create_user_with_recipient_username()
    {
        await using var db = NewDb("pending-invite-redeem");
        var svc = new UserService(db, new NoopAudit(), NullLogger<UserService>.Instance);

        var invite = await svc.CreatePendingInviteAsync(new CreateInviteRequest(DisplayName: "Invited User", Email: "invitee@example.test"), "http://cove.local", null, TestContext.Current.CancellationToken);
        var info = await svc.GetInviteInfoAsync(invite.Token, TestContext.Current.CancellationToken);

        Assert.NotNull(info);
        Assert.True(info.UsernameRequired);
        Assert.Null(info.Username);

        await Assert.ThrowsAsync<InviteTokenException>(() => svc.RedeemInviteAsync(invite.Token, "newpassword123", null, null, TestContext.Current.CancellationToken));

        var redeemed = await svc.RedeemInviteAsync(invite.Token, "newpassword123", "chosen-name", null, TestContext.Current.CancellationToken);

        Assert.Equal("chosen-name", redeemed.Username);
        Assert.Equal("Invited User", redeemed.DisplayName);
        Assert.Equal("invitee@example.test", redeemed.Email);
        Assert.True(redeemed.HasPassword);
        Assert.True(await svc.VerifyPasswordAsync(redeemed.Id, "newpassword123", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Setup_token_bootstraps_owner_once()
    {
        await using var db = NewDb("setup-token");
        var svc = new UserService(db, new NoopAudit(), NullLogger<UserService>.Instance);

        var setup = await svc.CreateSetupTokenAsync(null, TestContext.Current.CancellationToken);
        Assert.True(await svc.HasSetupTokenAsync(TestContext.Current.CancellationToken));

        var owner = await svc.RedeemSetupTokenAsync(setup.Token, "ownerpass123", "owner", null, TestContext.Current.CancellationToken);

        Assert.Equal("owner", owner.Username);
        Assert.Contains(BuiltinRoles.Owner, owner.Roles);
        Assert.False(await svc.HasSetupTokenAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InviteTokenException>(() => svc.RedeemSetupTokenAsync(setup.Token, "ownerpass123", "owner", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Issued_jwt_has_expiry_live_session_and_refresh_uses_configured_ttl()
    {
        await using var db = NewDb("token-age");
        await SeedOwnerAsync(db);

        var config = new CoveConfiguration { Auth = { JwtSecret = "test-secret-that-is-long-enough-for-hmac", AccessTokenMinutes = 15, RefreshTokenDays = 30 } };
        var tokens = new TokenService(db, config, new PermissionRegistry(), NullLogger<TokenService>.Instance);

        var pair = await tokens.IssueForUserAsync(1, "127.0.0.1", "test", TestContext.Current.CancellationToken);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(pair.AccessToken);
        var principal = await tokens.ResolveAsync($"Bearer {pair.AccessToken}", "127.0.0.1", "test", TestContext.Current.CancellationToken);

        Assert.Contains(jwt.Claims, claim => string.Equals(claim.Type, JwtRegisteredClaimNames.Exp, StringComparison.Ordinal));
        Assert.NotNull(principal);
        Assert.InRange(pair.AccessExpires - DateTime.UtcNow, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(16));
        Assert.InRange(pair.RefreshExpires - DateTime.UtcNow, TimeSpan.FromDays(29), TimeSpan.FromDays(31));
    }

    [Fact]
    public async Task ResolveExistingUserAsync_builds_host_owned_principal_and_rejects_unusable_accounts()
    {
        await using var db = NewDb("external-user-assertion");
        await SeedOwnerAsync(db);
        db.Users.AddRange(
            new User
            {
                Id = 2,
                Username = "inactive",
                PasswordHash = "hash",
                PasswordAlgo = "test",
                IsActive = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            new User
            {
                Id = 3,
                Username = "locked",
                PasswordHash = "hash",
                PasswordAlgo = "test",
                IsActive = true,
                IsLocked = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var config = new CoveConfiguration { Auth = { JwtSecret = "test-secret-that-is-long-enough-for-hmac" } };
        var tokens = new TokenService(db, config, new PermissionRegistry(), NullLogger<TokenService>.Instance);

        var principal = await tokens.ResolveExistingUserAsync(1, "127.0.0.1", "test-agent", TestContext.Current.CancellationToken);

        Assert.NotNull(principal);
        Assert.Equal(1, principal.UserId);
        Assert.Equal("owner", principal.Username);
        Assert.Equal(PrincipalKind.User, principal.Kind);
        Assert.Contains(BuiltinRoles.Owner, principal.Roles);
        Assert.Contains(Permissions.All, principal.Permissions);
        Assert.Equal("127.0.0.1", principal.Ip);
        Assert.Equal("test-agent", principal.UserAgent);
        Assert.Null(await tokens.ResolveExistingUserAsync(999, null, null, TestContext.Current.CancellationToken));
        Assert.Null(await tokens.ResolveExistingUserAsync(2, null, null, TestContext.Current.CancellationToken));
        Assert.Null(await tokens.ResolveExistingUserAsync(3, null, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsync_rejects_jwt_after_session_is_revoked()
    {
        await using var db = NewDb("token-revoked-session");
        await SeedOwnerAsync(db);

        var config = new CoveConfiguration { Auth = { JwtSecret = "test-secret-that-is-long-enough-for-hmac", AccessTokenMinutes = 15, RefreshTokenDays = 30 } };
        var tokens = new TokenService(db, config, new PermissionRegistry(), NullLogger<TokenService>.Instance);

        var pair = await tokens.IssueForUserAsync(1, "127.0.0.1", "test", TestContext.Current.CancellationToken);

        Assert.NotNull(await tokens.ResolveAsync($"Bearer {pair.AccessToken}", "127.0.0.1", "test", TestContext.Current.CancellationToken));

        var session = await db.RefreshTokens.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        session.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Null(await tokens.ResolveAsync($"Bearer {pair.AccessToken}", "127.0.0.1", "test", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Existing_sessions_and_api_tokens_reject_a_user_who_loses_their_password()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new TestCoveContext(options);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await SeedOwnerAsync(db);

        var config = new CoveConfiguration { Auth = { JwtSecret = "test-secret-that-is-long-enough-for-hmac", AccessTokenMinutes = 15, RefreshTokenDays = 30 } };
        var tokens = new TokenService(db, config, new PermissionRegistry(), NullLogger<TokenService>.Instance);
        var pair = await tokens.IssueForUserAsync(1, "127.0.0.1", "test", TestContext.Current.CancellationToken);
        var apiToken = await tokens.CreateApiTokenAsync(1, "test token", null, null, null, TestContext.Current.CancellationToken);

        var user = await db.Users.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        user.PasswordHash = string.Empty;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<UnauthorizedException>(
            () => tokens.RefreshAsync(pair.RefreshToken, "127.0.0.1", "test", TestContext.Current.CancellationToken));
        Assert.Null(await tokens.ResolveAsync($"Bearer {pair.AccessToken}", "127.0.0.1", "test", TestContext.Current.CancellationToken));
        Assert.Null(await tokens.ResolveAsync($"Bearer {apiToken.PlaintextToken}", "127.0.0.1", "test", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Recent_refresh_token_reuse_does_not_revoke_the_rotated_session()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new TestCoveContext(options);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await SeedOwnerAsync(db);

        var config = new CoveConfiguration { Auth = { JwtSecret = "test-secret-that-is-long-enough-for-hmac" } };
        var tokens = new TokenService(db, config, new PermissionRegistry(), NullLogger<TokenService>.Instance);
        var original = await tokens.IssueForUserAsync(1, "127.0.0.1", "test", TestContext.Current.CancellationToken);
        var rotated = await tokens.RefreshAsync(original.RefreshToken, "127.0.0.1", "test", TestContext.Current.CancellationToken);

        var exception = await Record.ExceptionAsync(
            () => tokens.RefreshAsync(original.RefreshToken, "127.0.0.1", "test", TestContext.Current.CancellationToken));

        Assert.IsType<RefreshTokenConflictException>(exception);
        Assert.NotNull(await tokens.ResolveAsync($"Bearer {rotated.AccessToken}", "127.0.0.1", "test", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Recent_refresh_token_reuse_preserves_an_active_later_descendant()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new TestCoveContext(options);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await SeedOwnerAsync(db);

        var config = new CoveConfiguration { Auth = { JwtSecret = "test-secret-that-is-long-enough-for-hmac" } };
        var tokens = new TokenService(db, config, new PermissionRegistry(), NullLogger<TokenService>.Instance);
        var original = await tokens.IssueForUserAsync(1, "127.0.0.1", "test", TestContext.Current.CancellationToken);
        var firstRotation = await tokens.RefreshAsync(original.RefreshToken, "127.0.0.1", "test", TestContext.Current.CancellationToken);
        var secondRotation = await tokens.RefreshAsync(firstRotation.RefreshToken, "127.0.0.1", "test", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<RefreshTokenConflictException>(
            () => tokens.RefreshAsync(original.RefreshToken, "127.0.0.1", "test", TestContext.Current.CancellationToken));

        Assert.NotNull(await tokens.ResolveAsync($"Bearer {secondRotation.AccessToken}", "127.0.0.1", "test", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Older_refresh_token_reuse_still_revokes_the_rotated_session()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new TestCoveContext(options);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await SeedOwnerAsync(db);

        var config = new CoveConfiguration { Auth = { JwtSecret = "test-secret-that-is-long-enough-for-hmac" } };
        var tokens = new TokenService(db, config, new PermissionRegistry(), NullLogger<TokenService>.Instance);
        var original = await tokens.IssueForUserAsync(1, "127.0.0.1", "test", TestContext.Current.CancellationToken);
        var rotated = await tokens.RefreshAsync(original.RefreshToken, "127.0.0.1", "test", TestContext.Current.CancellationToken);
        var originalEntity = await db.RefreshTokens.SingleAsync(token => token.ParentId == null, cancellationToken: TestContext.Current.CancellationToken);
        originalEntity.RevokedAt = DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(1));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<UnauthorizedException>(
            () => tokens.RefreshAsync(original.RefreshToken, "127.0.0.1", "test", TestContext.Current.CancellationToken));

        Assert.Null(await tokens.ResolveAsync($"Bearer {rotated.AccessToken}", "127.0.0.1", "test", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsync_rejects_jwt_after_auth_database_is_recreated()
    {
        await using var db = NewDb("token-db-recreated");
        await SeedOwnerAsync(db);

        var config = new CoveConfiguration { Auth = { JwtSecret = "test-secret-that-is-long-enough-for-hmac", AccessTokenMinutes = 15, RefreshTokenDays = 30 } };
        var tokens = new TokenService(db, config, new PermissionRegistry(), NullLogger<TokenService>.Instance);

        var pair = await tokens.IssueForUserAsync(1, "127.0.0.1", "test", TestContext.Current.CancellationToken);

        db.RefreshTokens.RemoveRange(db.RefreshTokens);
        db.UserRoleAssignments.RemoveRange(db.UserRoleAssignments);
        db.Users.RemoveRange(db.Users);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await SeedOwnerAsync(db);

        Assert.Null(await tokens.ResolveAsync($"Bearer {pair.AccessToken}", "127.0.0.1", "test", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsync_returns_null_for_malformed_bearer_token()
    {
        await using var db = NewDb("token-malformed");
        var config = new CoveConfiguration { Auth = { JwtSecret = "test-secret-that-is-long-enough-for-hmac", RefreshTokenDays = 30 } };
        var tokens = new TokenService(db, config, new PermissionRegistry(), NullLogger<TokenService>.Instance);

        var principal = await tokens.ResolveAsync("Bearer not-a-jwt", "127.0.0.1", "test", TestContext.Current.CancellationToken);

        Assert.Null(principal);
    }

    [Fact]
    public async Task ResolveAsync_waits_for_api_token_last_used_update()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var interceptor = new BlockingApiTokenUpdateInterceptor();
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new TestCoveContext(options);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await SeedOwnerAsync(db);

        var config = new CoveConfiguration { Auth = { JwtSecret = "test-secret-that-is-long-enough-for-hmac" } };
        var tokens = new TokenService(db, config, new PermissionRegistry(), NullLogger<TokenService>.Instance);
        var issued = await tokens.CreateApiTokenAsync(1, "test token", null, null, null, TestContext.Current.CancellationToken);
        interceptor.BlockUpdates = true;

        var resolveTask = tokens.ResolveAsync($"Bearer {issued.PlaintextToken}", "127.0.0.1", "test", TestContext.Current.CancellationToken);
        await interceptor.UpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        bool resolvedBeforeUpdateCompleted;
        try
        {
            resolvedBeforeUpdateCompleted = await Task.WhenAny(
                resolveTask,
                Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken)) == resolveTask;
        }
        finally
        {
            interceptor.AllowUpdate.TrySetResult();
        }

        var principal = await resolveTask;
        var lastUsedAt = await db.ApiTokens.AsNoTracking()
            .Where(token => token.Id == issued.Id)
            .Select(token => token.LastUsedAt)
            .SingleAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(resolvedBeforeUpdateCompleted);
        Assert.NotNull(principal);
        Assert.NotNull(lastUsedAt);
    }

    private sealed class TestCoveContext(DbContextOptions<CoveContext> options) : CoveContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
        }
    }

    private sealed class BlockingApiTokenUpdateInterceptor : DbCommandInterceptor
    {
        public bool BlockUpdates { get; set; }
        public TaskCompletionSource UpdateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowUpdate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (BlockUpdates
                && command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("LastUsedAt", StringComparison.OrdinalIgnoreCase))
            {
                UpdateStarted.TrySetResult();
                await AllowUpdate.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private static async Task SeedOwnerAsync(CoveContext db)
    {
        var now = DateTime.UtcNow;
        if (!await db.Roles.AnyAsync(r => r.Id == 1))
        {
            db.Permissions.Add(new Permission
            {
                Key = Permissions.All,
                Category = "test",
                Description = "Test permission",
            });
            db.Roles.Add(new Role
            {
                Id = 1,
                Name = BuiltinRoles.Owner,
                Description = "Owner",
                IsBuiltin = true,
                IsSystem = true,
                Source = "core",
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.RolePermissions.Add(new RolePermission { RoleId = 1, PermissionKey = Permissions.All });
        }

        db.Users.Add(new User
        {
            Id = 1,
            Username = "owner",
            PasswordHash = "hash",
            PasswordAlgo = "test",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.UserRoleAssignments.Add(new UserRoleAssignment { UserId = 1, RoleId = 1, GrantedAt = now });
        await db.SaveChangesAsync();
    }

    private sealed class NoopAudit : IAuditService
    {
        public Task LogAsync(string action, string outcome, CovePrincipal? actor = null,
            string? targetKind = null, string? targetId = null, object? detail = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
