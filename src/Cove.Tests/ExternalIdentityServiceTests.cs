using Cove.Core.Auth;
using Cove.Core.Entities.Auth;
using Cove.Data;
using Cove.Data.Auth;
using Cove.Plugins;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class ExternalIdentityServiceTests
{
    [Fact]
    public async Task One_user_can_link_multiple_exact_external_subjects()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync(1, "alice", hasPassword: true);
        var service = fixture.Service();

        var authentik = await service.CreateLinkAsync(alice.Id, Identity("authentik", "subject-a", "Authentik"), Principal(alice), TestContext.Current.CancellationToken);
        var replacement = await service.CreateLinkAsync(alice.Id, Identity("replacement", "subject-b", "Replacement"), Principal(alice), TestContext.Current.CancellationToken);

        Assert.NotEqual(authentik.Id, replacement.Id);
        Assert.Equal(alice.Id, await service.ResolveUserIdAsync(Identity("authentik", "subject-a", "Authentik"), TestContext.Current.CancellationToken));
        Assert.Equal(alice.Id, await service.ResolveUserIdAsync(Identity("replacement", "subject-b", "Replacement"), TestContext.Current.CancellationToken));
        Assert.Null(await service.ResolveUserIdAsync(Identity("authentik", "SUBJECT-A", "Authentik"), TestContext.Current.CancellationToken));
        Assert.Equal(2, (await service.ListForUserAsync(alice.Id, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task External_identity_cannot_be_rebound_to_another_user()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync(1, "alice", hasPassword: true);
        var bob = await fixture.AddUserAsync(2, "bob", hasPassword: true);
        var service = fixture.Service();
        var identity = Identity("authentik", "shared-subject", "Authentik");

        await service.CreateLinkAsync(alice.Id, identity, Principal(alice), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ExternalIdentityConflictException>(
            () => service.CreateLinkAsync(bob.Id, identity, Principal(bob), TestContext.Current.CancellationToken));
        Assert.Equal(alice.Id, await service.ResolveUserIdAsync(identity, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Opaque_subject_whitespace_is_not_normalized_into_another_identity()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alice = await fixture.AddUserAsync(1, "alice", hasPassword: true);
        var service = fixture.Service();

        await service.CreateLinkAsync(alice.Id, Identity("authentik", "subject-a", "Authentik"), Principal(alice), TestContext.Current.CancellationToken);

        Assert.Null(await service.ResolveUserIdAsync(Identity("authentik", " subject-a ", "Authentik"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Removing_last_identity_does_not_depend_on_password_state()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync(1, "external-only", hasPassword: false);
        var service = fixture.Service();
        var link = await service.CreateLinkAsync(user.Id, Identity("authentik", "only-subject", "Authentik"), Principal(user), TestContext.Current.CancellationToken);

        await service.RemoveLinkAsync(user.Id, link.Id, Principal(user), TestContext.Current.CancellationToken);

        Assert.Empty(await service.ListForUserAsync(user.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Link_usage_counts_and_last_used_are_provider_scoped()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync(1, "alice", hasPassword: true);
        var service = fixture.Service();
        var identity = Identity("authentik", "subject-a", "Authentik");
        await service.CreateLinkAsync(user.Id, identity, Principal(user), TestContext.Current.CancellationToken);

        Assert.Equal(1, await service.CountProviderLinksAsync(identity.ExtensionId, identity.ProviderId, TestContext.Current.CancellationToken));
        Assert.Equal(0, await service.CountProviderLinksAsync(identity.ExtensionId, "other", TestContext.Current.CancellationToken));

        await service.MarkUsedAsync(identity, TestContext.Current.CancellationToken);

        Assert.NotNull(Assert.Single(await service.ListForUserAsync(user.Id, TestContext.Current.CancellationToken)).LastUsedAt);
    }

    private static ExtensionIdentityAssertion Identity(
        string providerId,
        string subject,
        string providerLabel,
        string extensionId = "com.example.auth") => new(
            extensionId,
            providerId,
            subject,
            "oidc",
            providerLabel,
            "Alice at provider");

    private static CovePrincipal Principal(User user) => new()
    {
        UserId = user.Id,
        Username = user.Username,
        Kind = PrincipalKind.User,
        Roles = new HashSet<string>(),
        Permissions = new HashSet<string>(),
    };

    private sealed class Fixture(SqliteConnection connection, CoveContext db) : IAsyncDisposable
    {
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<CoveContext>()
                .UseSqlite(connection)
                .Options;
            var db = new CoveContext(options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async Task<User> AddUserAsync(int id, string username, bool hasPassword)
        {
            var now = DateTime.UtcNow;
            var user = new User
            {
                Id = id,
                Username = username,
                PasswordHash = hasPassword ? "hash" : string.Empty,
                PasswordAlgo = "test",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return user;
        }

        public ExternalIdentityService Service() => new(db, new NoopAudit(), TimeProvider.System);

        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }
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
