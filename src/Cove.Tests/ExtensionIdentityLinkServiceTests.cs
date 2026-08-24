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

public sealed class ExtensionIdentityLinkServiceTests
{
    [Fact]
    public async Task Link_requires_same_browser_and_explicit_same_user_confirmation()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.SetPrincipal(1, "alice");
        var start = NewContext();
        var intent = fixture.Links.BeginLink(start, "com.example.auth", "provider-a");
        Assert.NotNull(intent);
        SetBindingCookie(start, intent.BrowserBinding);

        var preparation = await fixture.Links.PrepareLinkAsync(start, intent.Token, intent.BrowserBinding, Identity(), TestContext.Current.CancellationToken);

        Assert.Equal(ExtensionIdentityLinkPreparationFailure.None, preparation.Failure);
        Assert.NotNull(preparation.Code);
        Assert.Empty(await fixture.Identities.ListForUserAsync(1, TestContext.Current.CancellationToken));

        fixture.SetPrincipal(2, "bob");
        Assert.Null(await fixture.Links.PreviewAsync(start, preparation.Code!, TestContext.Current.CancellationToken));
        Assert.Null(await fixture.Links.ConfirmAsync(start, preparation.Code!, TestContext.Current.CancellationToken));

        fixture.SetPrincipal(1, "alice");
        var preview = await fixture.Links.PreviewAsync(start, preparation.Code!, TestContext.Current.CancellationToken);
        Assert.Equal("Example provider", preview?.ProviderLabel);
        Assert.Equal("alice@example.test", preview?.AccountLabel);

        var link = await fixture.Links.ConfirmAsync(start, preparation.Code!, TestContext.Current.CancellationToken);
        Assert.NotNull(link);
        Assert.Equal(1, link.UserId);
        Assert.Null(await fixture.Links.ConfirmAsync(start, preparation.Code!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Callback_from_another_browser_does_not_consume_link_intent()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.SetPrincipal(1, "alice");
        var browser = NewContext();
        var intent = fixture.Links.BeginLink(browser, "com.example.auth", "provider-a")!;
        var other = NewContext();
        var otherBinding = fixture.LoginSessions.BeginBrowserSession(other);
        SetBindingCookie(other, otherBinding);

        var rejected = await fixture.Links.PrepareLinkAsync(other, intent.Token, intent.BrowserBinding, Identity(), TestContext.Current.CancellationToken);

        Assert.Equal(ExtensionIdentityLinkPreparationFailure.BrowserMismatch, rejected.Failure);

        SetBindingCookie(browser, intent.BrowserBinding);
        var accepted = await fixture.Links.PrepareLinkAsync(browser, intent.Token, intent.BrowserBinding, Identity(), TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionIdentityLinkPreparationFailure.None, accepted.Failure);
    }

    [Fact]
    public async Task Identity_linked_to_another_user_is_rejected_before_confirmation()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Identities.CreateLinkAsync(2, Identity(), Principal(2, "bob"), TestContext.Current.CancellationToken);
        fixture.SetPrincipal(1, "alice");
        var browser = NewContext();
        var intent = fixture.Links.BeginLink(browser, "com.example.auth", "provider-a")!;
        SetBindingCookie(browser, intent.BrowserBinding);

        var result = await fixture.Links.PrepareLinkAsync(browser, intent.Token, intent.BrowserBinding, Identity(), TestContext.Current.CancellationToken);

        Assert.Equal(ExtensionIdentityLinkPreparationFailure.IdentityConflict, result.Failure);
        Assert.Null(result.Code);
    }

    [Fact]
    public async Task Directly_validated_identity_still_requires_same_browser_confirmation()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.SetPrincipal(1, "alice");
        var context = NewContext();

        var preparation = await fixture.Links.PrepareDirectLinkAsync(context, Identity(), TestContext.Current.CancellationToken);

        Assert.Equal(ExtensionIdentityLinkPreparationFailure.None, preparation.Failure);
        Assert.NotNull(preparation.Code);
        Assert.Empty(await fixture.Identities.ListForUserAsync(1, TestContext.Current.CancellationToken));

        var setCookie = context.Response.Headers.SetCookie.ToString();
        var cookiePair = setCookie.Split(';', 2)[0];
        context.Request.Headers.Cookie = cookiePair;
        Assert.NotNull(await fixture.Links.PreviewAsync(context, preparation.Code!, TestContext.Current.CancellationToken));
        Assert.NotNull(await fixture.Links.ConfirmAsync(context, preparation.Code!, TestContext.Current.CancellationToken));
    }

    private static ExtensionIdentityAssertion Identity() => new(
        "com.example.auth",
        "provider-a",
        "subject-a",
        "oidc",
        "Example provider",
        "alice@example.test");

    private static CovePrincipal Principal(int userId, string username) => new()
    {
        UserId = userId,
        Username = username,
        Kind = PrincipalKind.User,
        Roles = new HashSet<string>(),
        Permissions = new HashSet<string>(),
    };

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        return context;
    }

    private static void SetBindingCookie(DefaultHttpContext context, string binding) =>
        context.Request.Headers.Cookie = $"{ExtensionLoginSessionService.BrowserBindingCookieName}={binding}";

    private sealed class Fixture(
        SqliteConnection connection,
        CoveContext db,
        CurrentPrincipalAccessor principals,
        ExternalIdentityService identities,
        ExtensionLoginSessionService loginSessions,
        ExtensionIdentityLinkService links) : IAsyncDisposable
    {
        public ExternalIdentityService Identities { get; } = identities;
        public ExtensionLoginSessionService LoginSessions { get; } = loginSessions;
        public ExtensionIdentityLinkService Links { get; } = links;

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new CoveContext(new DbContextOptionsBuilder<CoveContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();
            db.Users.AddRange(User(1, "alice"), User(2, "bob"));
            await db.SaveChangesAsync();

            var audit = new NoopAudit();
            var time = TimeProvider.System;
            var identities = new ExternalIdentityService(db, audit, time);
            var config = new CoveConfiguration
            {
                Auth =
                {
                    JwtSecret = "test-secret-that-is-long-enough-for-hmac",
                    AccessTokenMinutes = 15,
                    RefreshTokenDays = 30,
                },
            };
            var sessions = new ExtensionLoginSessionService(
                new UserService(db, audit, NullLogger<UserService>.Instance),
                new TokenService(db, config, new PermissionRegistry(), NullLogger<TokenService>.Instance),
                identities,
                audit,
                config,
                new ExtensionLoginTicketStore(time),
                NullLogger<ExtensionLoginSessionService>.Instance);
            var principals = new CurrentPrincipalAccessor();
            var links = new ExtensionIdentityLinkService(
                principals,
                identities,
                sessions,
                new ExtensionIdentityLinkTicketStore(time));
            return new Fixture(connection, db, principals, identities, sessions, links);
        }

        public void SetPrincipal(int userId, string username) =>
            principals.Set(Principal(userId, username));

        public async ValueTask DisposeAsync()
        {
            principals.Set(null);
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }

        private static User User(int id, string username) => new()
        {
            Id = id,
            Username = username,
            PasswordHash = "hash",
            PasswordAlgo = "test",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
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
}
