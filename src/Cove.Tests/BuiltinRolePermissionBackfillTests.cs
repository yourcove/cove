using Cove.Core.Auth;
using Cove.Core.Entities.Auth;
using Cove.Data;
using Cove.Data.Auth;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

/// <summary>
/// Built-in role defaults seed only an empty role, so a default added after release would never reach
/// an install whose roles already exist. The auth bootstrap adds those keys to the existing roles on
/// every start, and leaves the rest of an administrator's customization alone.
/// </summary>
[RequiresPostgres]
public class BuiltinRolePermissionBackfillTests
{
    [Fact]
    public async Task Fresh_install_seeds_every_default_of_each_builtin_role()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var bootstrap = CreateBootstrap(database);

        await bootstrap.RefreshPermissionCatalogAsync(TestContext.Current.CancellationToken);

        Assert.Superset(Permissions.ViewerDefaults.ToHashSet(), await RolePermissionsAsync(database, BuiltinRoles.Viewer));
        Assert.Superset(Permissions.MemberDefaults.ToHashSet(), await RolePermissionsAsync(database, BuiltinRoles.Member));
        Assert.Superset(Permissions.GuestDefaults.ToHashSet(), await RolePermissionsAsync(database, BuiltinRoles.Guest));
    }

    [Fact]
    public async Task Existing_viewer_and_member_roles_gain_the_backfilled_keys_and_keep_their_customization()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await SeedCustomizedRolesAsync(database);
        var bootstrap = CreateBootstrap(database);

        await bootstrap.RefreshPermissionCatalogAsync(TestContext.Current.CancellationToken);
        // A second start must not try to insert the same keys again.
        await bootstrap.RefreshPermissionCatalogAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            new HashSet<string> { Permissions.VideosRead, Permissions.SavedFiltersWrite, Permissions.SavedFiltersDelete },
            await RolePermissionsAsync(database, BuiltinRoles.Viewer));
        Assert.Equal(
            new HashSet<string> { Permissions.VideosWrite, Permissions.EmbeddingsRead, Permissions.AiRunsRead },
            await RolePermissionsAsync(database, BuiltinRoles.Member));
    }

    private static BootstrapAuthService CreateBootstrap(CovePostgresDatabase database)
    {
        var services = new ServiceCollection();
        services.AddDbContext<CoveContext>(builder => database.Configure(builder));
        return new BootstrapAuthService(
            services.BuildServiceProvider(),
            new PermissionRegistry(),
            NullLogger<BootstrapAuthService>.Instance);
    }

    // An install from before the backfill: the permission catalog and both roles exist, and an
    // administrator has cut each role down to a single permission.
    private static async Task SeedCustomizedRolesAsync(CovePostgresDatabase database)
    {
        await using var db = database.CreateContext();
        foreach (var definition in Permissions.CorePermissions)
        {
            db.Permissions.Add(new Permission
            {
                Key = definition.Key,
                Category = definition.Category,
                Description = definition.Description,
                Source = definition.Source,
                Dangerous = definition.Dangerous,
                RegisteredAt = DateTime.UtcNow,
            });
        }

        db.Roles.Add(CustomizedRole(BuiltinRoles.Viewer, Permissions.VideosRead));
        db.Roles.Add(CustomizedRole(BuiltinRoles.Member, Permissions.VideosWrite));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static Role CustomizedRole(string name, string permission) => new()
    {
        Name = name,
        IsBuiltin = true,
        Source = "core",
        Permissions = [new RolePermission { PermissionKey = permission }],
    };

    private static async Task<HashSet<string>> RolePermissionsAsync(CovePostgresDatabase database, string roleName)
    {
        await using var db = database.CreateContext();
        var keys = await db.RolePermissions.AsNoTracking()
            .Where(permission => permission.Role!.Name == roleName)
            .Select(permission => permission.PermissionKey)
            .ToListAsync(TestContext.Current.CancellationToken);
        return keys.ToHashSet();
    }
}
