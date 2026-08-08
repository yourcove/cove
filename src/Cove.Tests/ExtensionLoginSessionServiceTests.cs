using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public sealed class ExtensionLoginSessionServiceTests
{
    [Fact]
    public async Task Completed_external_login_is_browser_bound_and_one_time()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = NewDb(connection);
        await db.Database.EnsureCreatedAsync();
        await SeedUserAsync(db);
        var service = CreateService(db);
        var browser = NewContext();
        var binding = service.BeginBrowserSession(browser);
        var setCookie = browser.Response.Headers.SetCookie.ToString();
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        SetBindingCookie(browser, binding);

        var completion = await service.CompleteAsync(
            browser,
            binding,
            "com.example.oidc",
            " alice ");

        Assert.Equal(ExtensionLoginCompletionFailure.None, completion.Failure);
        Assert.False(string.IsNullOrWhiteSpace(completion.Code));
        Assert.Empty(await db.RefreshTokens.AsNoTracking().ToListAsync());
        Assert.Null((await db.Users.AsNoTracking().SingleAsync()).LastLoginAt);

        var otherBrowser = NewContext();
        var otherBinding = service.BeginBrowserSession(otherBrowser);
        SetBindingCookie(otherBrowser, otherBinding);
        Assert.Null(await service.RedeemAsync(otherBrowser, completion.Code!));

        var redeemed = await service.RedeemAsync(browser, completion.Code!);
        Assert.NotNull(redeemed);
        Assert.Equal("com.example.oidc", redeemed.ExtensionId);
        Assert.Equal("alice", redeemed.TokenPair.User.Username);
        Assert.Single(await db.RefreshTokens.AsNoTracking().ToListAsync());
        Assert.NotNull((await db.Users.AsNoTracking().SingleAsync()).LastLoginAt);
        Assert.Null(await service.RedeemAsync(browser, completion.Code!));
    }

    [Fact]
    public async Task Completion_rejects_missing_browser_binding_and_unusable_users()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = NewDb(connection);
        await db.Database.EnsureCreatedAsync();
        await SeedUserAsync(db);
        db.Users.AddRange(
            User(2, "inactive", isActive: false),
            User(3, "locked", isLocked: true));
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var browser = NewContext();
        var binding = service.BeginBrowserSession(browser);

        var wrongBrowser = await service.CompleteAsync(
            browser,
            binding,
            "com.example.oidc",
            "alice");
        Assert.Equal(ExtensionLoginCompletionFailure.BrowserMismatch, wrongBrowser.Failure);

        SetBindingCookie(browser, binding);
        var missingUser = await service.CompleteAsync(
            browser,
            binding,
            "com.example.oidc",
            "missing");
        Assert.Equal(ExtensionLoginCompletionFailure.UserRejected, missingUser.Failure);
        Assert.Null(missingUser.Code);

        var inactive = await service.CompleteAsync(
            browser,
            binding,
            "com.example.oidc",
            "inactive");
        var locked = await service.CompleteAsync(
            browser,
            binding,
            "com.example.oidc",
            "locked");
        Assert.Equal(ExtensionLoginCompletionFailure.UserRejected, inactive.Failure);
        Assert.Equal(ExtensionLoginCompletionFailure.UserRejected, locked.Failure);
    }

    [Fact]
    public async Task Completed_external_login_expires_before_redemption()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = NewDb(connection);
        await db.Database.EnsureCreatedAsync();
        await SeedUserAsync(db);
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var service = CreateService(db, time);
        var browser = NewContext();
        var binding = service.BeginBrowserSession(browser);
        SetBindingCookie(browser, binding);
        var completion = await service.CompleteAsync(
            browser,
            binding,
            "com.example.oidc",
            "alice");

        time.Advance(TimeSpan.FromSeconds(61));

        Assert.Null(await service.RedeemAsync(browser, completion.Code!));
        Assert.Empty(await db.RefreshTokens.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Redemption_rechecks_account_state_before_issuing_tokens()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = NewDb(connection);
        await db.Database.EnsureCreatedAsync();
        await SeedUserAsync(db);
        var service = CreateService(db);
        var browser = NewContext();
        var binding = service.BeginBrowserSession(browser);
        SetBindingCookie(browser, binding);
        var completion = await service.CompleteAsync(
            browser,
            binding,
            "com.example.oidc",
            "alice");
        var user = await db.Users.SingleAsync();
        user.IsActive = false;
        await db.SaveChangesAsync();

        Assert.Null(await service.RedeemAsync(browser, completion.Code!));
        Assert.Empty(await db.RefreshTokens.AsNoTracking().ToListAsync());
    }

    private static ExtensionLoginSessionService CreateService(
        CoveContext db,
        TimeProvider? timeProvider = null)
    {
        var config = new CoveConfiguration
        {
            Auth =
            {
                JwtSecret = "test-secret-that-is-long-enough-for-hmac",
                AccessTokenMinutes = 15,
                RefreshTokenDays = 30,
            },
        };
        var audit = new NoopAudit();
        return new ExtensionLoginSessionService(
            new UserService(db, audit, NullLogger<UserService>.Instance),
            new TokenService(db, config, new PermissionRegistry(), NullLogger<TokenService>.Instance),
            audit,
            config,
            new ExtensionLoginTicketStore(timeProvider ?? TimeProvider.System),
            NullLogger<ExtensionLoginSessionService>.Instance);
    }

    private static CoveContext NewDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;
        return new TestCoveContext(options);
    }

    private static async Task SeedUserAsync(CoveContext db)
    {
        db.Users.Add(User(1, "alice"));
        await db.SaveChangesAsync();
    }

    private static User User(
        int id,
        string username,
        bool isActive = true,
        bool isLocked = false) => new()
        {
            Id = id,
            Username = username,
            PasswordHash = "hash",
            PasswordAlgo = "test",
            IsActive = isActive,
            IsLocked = isLocked,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        context.Request.Headers.UserAgent = "extension-login-test";
        return context;
    }

    private static void SetBindingCookie(DefaultHttpContext context, string binding) =>
        context.Request.Headers.Cookie = $"{ExtensionLoginSessionService.BrowserBindingCookieName}={binding}";

    private sealed class TestCoveContext(DbContextOptions<CoveContext> options) : CoveContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => base.OnModelCreating(modelBuilder);
    }

    private sealed class NoopAudit : IAuditService
    {
        public Task LogAsync(
            string action,
            string outcome,
            CovePrincipal? actor = null,
            string? targetKind = null,
            string? targetId = null,
            object? detail = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
}
