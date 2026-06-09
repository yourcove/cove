using Cove.Core.Auth;
using Cove.Core.Entities.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cove.Data.Auth;

/// <summary>
/// Bootstraps the auth system on startup:
///   1. Upserts the permission catalog from <see cref="IPermissionRegistry"/>.
///   2. Seeds the built-in roles (Owner / Admin / Member / Viewer / Guest).
///   3. Backfills the Owner role for existing system users.
///
/// Runs after the DB has been migrated by Program.cs (we register it as a hosted
/// service that executes once at startup).
/// </summary>
public sealed class BootstrapAuthService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IPermissionRegistry _registry;
    private readonly ILogger<BootstrapAuthService> _log;
    private readonly SemaphoreSlim _bootstrapLock = new(1, 1);
    private CancellationTokenSource? _bootstrapCts;
    private Task? _bootstrapTask;

    public BootstrapAuthService(
        IServiceProvider services,
        IPermissionRegistry registry,
        ILogger<BootstrapAuthService> log)
    {
        _services = services;
        _registry = registry;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _bootstrapCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _bootstrapTask = Task.Run(() => BootstrapWhenReadyAsync(_bootstrapCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_bootstrapCts != null)
            await _bootstrapCts.CancelAsync();

        if (_bootstrapTask == null)
            return;

        try
        {
            await _bootstrapTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task BootstrapWhenReadyAsync(CancellationToken ct)
    {
        await WaitForAuthSchemaAsync(ct);

        try
        {
            await EnsureAuthStateAsync(includeOwnerUser: true, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Auth bootstrap failed");
        }
    }

    public Task RefreshPermissionCatalogAsync(CancellationToken ct)
        => EnsureAuthStateAsync(includeOwnerUser: false, ct);

    private async Task EnsureAuthStateAsync(bool includeOwnerUser, CancellationToken ct)
    {
        await _bootstrapLock.WaitAsync(ct);
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

            var livePermissions = await UpsertPermissionsAsync(db, ct);
            await SeedBuiltinRolesAsync(db, livePermissions, ct);
            if (includeOwnerUser)
                await EnsureOwnerUserAsync(db, ct);

            _log.LogInformation("Auth bootstrap complete (permissions={PermCount}, roles={RoleCount}, users={UserCount})",
                await db.Permissions.CountAsync(ct),
                await db.Roles.CountAsync(ct),
                await db.Users.CountAsync(ct));
        }
        finally
        {
            _bootstrapLock.Release();
        }
    }

    private async Task WaitForAuthSchemaAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                if (await db.Database.CanConnectAsync(ct))
                {
                    var connection = db.Database.GetDbConnection();
                    var shouldClose = connection.State != System.Data.ConnectionState.Open;
                    if (shouldClose)
                        await connection.OpenAsync(ct);

                    try
                    {
                        await using var cmd = connection.CreateCommand();
                        cmd.CommandText = "SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name='roles'";
                        if (await cmd.ExecuteScalarAsync(ct) != null)
                            return;
                    }
                    finally
                    {
                        if (shouldClose)
                            await connection.CloseAsync();
                    }
                }
            }
            catch
            {
                // Database and schema may still be starting up.
            }

            await Task.Delay(1000, ct);
        }
    }

    private async Task<IReadOnlyList<PermissionDefinition>> UpsertPermissionsAsync(CoveContext db, CancellationToken ct)
    {
        var existing = await db.Permissions.ToDictionaryAsync(p => p.Key, ct);
        var livePermissions = _registry.All;
        var live = livePermissions.ToDictionary(p => p.Key);

        foreach (var def in live.Values)
        {
            if (existing.TryGetValue(def.Key, out var row))
            {
                row.Category = def.Category;
                row.Description = def.Description;
                row.Source = def.Source;
                row.Dangerous = def.Dangerous;
                row.Implies = System.Text.Json.JsonSerializer.Serialize(def.Implies ?? []);
                row.IsOrphaned = false;
            }
            else
            {
                db.Permissions.Add(new Permission
                {
                    Key = def.Key,
                    Category = def.Category,
                    Description = def.Description,
                    Source = def.Source,
                    Dangerous = def.Dangerous,
                    Implies = System.Text.Json.JsonSerializer.Serialize(def.Implies ?? []),
                    IsOrphaned = false,
                    RegisteredAt = DateTime.UtcNow,
                });
            }
        }

        // Mark orphans (keys present in DB but not declared in code anymore).
        foreach (var (key, row) in existing)
        {
            if (!live.ContainsKey(key))
                row.IsOrphaned = true;
        }

        await db.SaveChangesAsync(ct);
        return livePermissions;
    }

    private async Task SeedBuiltinRolesAsync(CoveContext db, IReadOnlyList<PermissionDefinition> livePermissions, CancellationToken ct)
    {
        var byName = await db.Roles.Include(r => r.Permissions).ToDictionaryAsync(r => r.Name, ct);

        Role EnsureRole(string name, string desc, bool isSystem)
        {
            if (byName.TryGetValue(name, out var existing))
            {
                existing.IsBuiltin = true;
                existing.IsSystem = isSystem;
                existing.Description ??= desc;
                return existing;
            }
            var role = new Role
            {
                Name = name,
                Description = desc,
                IsBuiltin = true,
                IsSystem = isSystem,
                Source = "core",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.Roles.Add(role);
            byName[name] = role;
            return role;
        }

        var owner = EnsureRole(BuiltinRoles.Owner, "Superuser; bypasses all checks.", isSystem: true);
        var admin = EnsureRole(BuiltinRoles.Admin, "Full operational control.", isSystem: false);
        var member = EnsureRole(BuiltinRoles.Member, "Day-to-day editor.", isSystem: false);
        var viewer = EnsureRole(BuiltinRoles.Viewer, "Read-only access.", isSystem: false);
        var guest = EnsureRole(BuiltinRoles.Guest, "Public/share-link target.", isSystem: false);

        await db.SaveChangesAsync(ct);

        // Owner: always has *, even after upgrades.
        EnsurePermissions(db, owner, ["*"]);
        EnsurePermissions(db, admin, [Permissions.ApiTokensWrite, Permissions.ShareLinksWrite, Permissions.AiDataRead, Permissions.AiDataClear]);
        EnsurePermissions(db, admin, livePermissions
            .Where(definition => definition.GrantToAdminsByDefault)
            .Select(permission => permission.Key)
            .ToArray());

        // For Admin/Member/Viewer/Guest, only seed if currently empty (we don't want to
        // stomp admin customizations on every boot).
        if (admin.Permissions.Count == 0)
            EnsurePermissions(db, admin, Permissions.AdminDefaults().ToArray());
        if (member.Permissions.Count == 0)
            EnsurePermissions(db, member, Permissions.MemberDefaults);
        if (viewer.Permissions.Count == 0)
            EnsurePermissions(db, viewer, Permissions.ViewerDefaults);
        if (guest.Permissions.Count == 0)
            EnsurePermissions(db, guest, Permissions.GuestDefaults);

        await db.SaveChangesAsync(ct);
    }

    private static void EnsurePermissions(CoveContext db, Role role, string[] permissions)
    {
        var have = role.Permissions.Select(p => p.PermissionKey).ToHashSet();
        foreach (var key in permissions.Distinct())
        {
            if (have.Contains(key)) continue;
            db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionKey = key });
            have.Add(key);
        }
    }
    private async Task EnsureOwnerUserAsync(CoveContext db, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(ct))
        {
            // Backfill: if an "owner" user exists but has no Owner role, assign it.
            var ownerRole = await db.Roles.FirstAsync(r => r.Name == BuiltinRoles.Owner, ct);
            var systemUsers = await db.Users.Include(u => u.Roles).Where(u => u.IsSystem).ToListAsync(ct);
            foreach (var su in systemUsers)
            {
                if (!su.Roles.Any(r => r.RoleId == ownerRole.Id))
                {
                    db.UserRoleAssignments.Add(new UserRoleAssignment
                    {
                        UserId = su.Id, RoleId = ownerRole.Id, GrantedAt = DateTime.UtcNow,
                    });
                }
            }
            await db.SaveChangesAsync(ct);
        }
    }
}
