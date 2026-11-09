using Cove.Api.Middleware;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Auth;
using Cove.Tests.Postgres;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

/// <summary>
/// API token scopes are intersected with the owner's permissions and can never be expansive.
/// A rejected scope names the permissions that caused the rejection, so a caller does not have
/// to bisect its request to learn which key was at fault.
/// </summary>
public class ApiTokenScopeTests
{
    [Fact]
    [RequiresPostgres]
    public async Task Rejected_scope_names_the_permissions_the_user_does_not_hold()
    {
        await using var fixture = await TokenServiceFixture.CreateAsync();
        var actor = PrincipalWith([Permissions.ApiTokensWrite, Permissions.VideosRead]);

        var error = await Assert.ThrowsAsync<ForbiddenException>(() => fixture.Tokens.CreateApiTokenAsync(
            1, "escalating token", [Permissions.VideosRead, Permissions.UsersRead, Permissions.SystemWipe],
            null, actor, TestContext.Current.CancellationToken));

        Assert.Equal([Permissions.UsersRead, Permissions.SystemWipe], error.MissingPermissions);
        Assert.Equal(Permissions.UsersRead, error.MissingPermission);
        Assert.Contains("not held by the requesting identity: users.read, system.wipe", error.Message);
        Assert.DoesNotContain(Permissions.VideosRead, error.MissingPermissions);
        Assert.Empty(await fixture.Db.ApiTokens.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [RequiresPostgres]
    public async Task Rejected_scope_separates_unknown_permissions_from_unheld_permissions()
    {
        await using var fixture = await TokenServiceFixture.CreateAsync();
        var actor = PrincipalWith([Permissions.ApiTokensWrite, Permissions.VideosRead]);

        var error = await Assert.ThrowsAsync<ForbiddenException>(() => fixture.Tokens.CreateApiTokenAsync(
            1, "typo token", [Permissions.VideosRead, "videos.raed", "videos.*", Permissions.UsersRead],
            null, actor, TestContext.Current.CancellationToken));

        Assert.Equal(["videos.raed", "videos.*", Permissions.UsersRead], error.MissingPermissions);
        Assert.Contains("unknown: videos.raed, videos.*", error.Message);
        Assert.Contains("not held by the requesting identity: users.read", error.Message);
    }

    [Fact]
    [RequiresPostgres]
    public async Task A_scope_the_user_holds_through_a_wildcard_or_a_read_grant_is_accepted()
    {
        await using var fixture = await TokenServiceFixture.CreateAsync();
        var actor = new CovePrincipal
        {
            UserId = 1,
            Username = "owner",
            Kind = PrincipalKind.User,
            Roles = new HashSet<string>(),
            Permissions = new HashSet<string> { "videos.*", Permissions.SystemRead },
            ReadGrantedEntityKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { EntityKinds.Image },
        };

        var issued = await fixture.Tokens.CreateApiTokenAsync(
            1, "scoped token", [Permissions.VideosDelete, Permissions.SystemRead, Permissions.ImagesRead],
            null, actor, TestContext.Current.CancellationToken);

        Assert.Equal([Permissions.VideosDelete, Permissions.SystemRead, Permissions.ImagesRead], issued.Scope);
    }

    [Fact]
    [RequiresPostgres]
    public async Task A_scope_without_an_actor_is_refused_and_names_every_requested_permission()
    {
        await using var fixture = await TokenServiceFixture.CreateAsync();

        var error = await Assert.ThrowsAsync<ForbiddenException>(() => fixture.Tokens.CreateApiTokenAsync(
            1, "actorless token", [Permissions.VideosRead, "videos.raed"],
            null, null, TestContext.Current.CancellationToken));

        Assert.Equal(["videos.raed", Permissions.VideosRead], error.MissingPermissions);
        Assert.Empty(await fixture.Db.ApiTokens.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_forbidden_response_carries_every_missing_permission()
    {
        var context = ExceptionContextFor(ForbiddenException.ForMissingPermissions(
            "API token scopes must be known permissions already held by the requesting identity.",
            ["videos.raed", Permissions.UsersRead]));

        new AuthExceptionFilter().OnException(context);

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.True(context.ExceptionHandled);
        Assert.Equal("FORBIDDEN", ReadMember(result.Value, "code"));
        Assert.Equal(new[] { "videos.raed", Permissions.UsersRead }, ReadMember(result.Value, "missing"));
    }

    [Fact]
    public void The_forbidden_response_omits_the_missing_array_when_no_permission_was_recorded()
    {
        var context = ExceptionContextFor(new ForbiddenException("Not your entity."));

        new AuthExceptionFilter().OnException(context);

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Null(ReadMember(result.Value, "missing"));
    }

    private static ExceptionContext ExceptionContextFor(Exception exception) => new(
        new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
        [])
    {
        Exception = exception,
    };

    private static object? ReadMember(object? value, string name) =>
        value?.GetType().GetProperty(name)?.GetValue(value);

    [Fact]
    [RequiresPostgres]
    public async Task An_unscoped_token_is_issued_without_a_permission_check()
    {
        await using var fixture = await TokenServiceFixture.CreateAsync();

        var issued = await fixture.Tokens.CreateApiTokenAsync(
            1, "full token", null, null, null, TestContext.Current.CancellationToken);

        Assert.Null(issued.Scope);
    }

    private static CovePrincipal PrincipalWith(string[] permissions) => new()
    {
        UserId = 1,
        Username = "owner",
        Kind = PrincipalKind.User,
        Roles = new HashSet<string>(),
        Permissions = new HashSet<string>(permissions, StringComparer.Ordinal),
    };

    private sealed class TokenServiceFixture : IAsyncDisposable
    {
        private readonly CovePostgresDatabase _database;

        private TokenServiceFixture(CovePostgresDatabase database, CoveContext db, ITokenService tokens)
        {
            _database = database;
            Db = db;
            Tokens = tokens;
        }

        public CoveContext Db { get; }
        public ITokenService Tokens { get; }

        public static async Task<TokenServiceFixture> CreateAsync()
        {
            var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
            var db = database.CreateContext();
            try
            {
                await SeedOwnerAsync(db);
                await database.SyncIdentitySequencesAsync(TestContext.Current.CancellationToken);
            }
            catch
            {
                await db.DisposeAsync();
                await database.DisposeAsync();
                throw;
            }

            var config = new CoveConfiguration
            {
                Auth = { JwtSecret = "test-secret-that-is-long-enough-for-hmac", AccessTokenMinutes = 15, RefreshTokenDays = 30 },
            };
            return new TokenServiceFixture(
                database,
                db,
                new TokenService(db, config, new PermissionRegistry(), NullLogger<TokenService>.Instance));
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _database.DisposeAsync();
        }

        private static async Task SeedOwnerAsync(CoveContext db)
        {
            var now = DateTime.UtcNow;
            db.Permissions.Add(new Permission { Key = Permissions.All, Category = "test", Description = "Test permission" });
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
    }
}
