using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[RequiresPermission(Permissions.SystemRead)]
public class DatabaseController(
    CoveContext db,
    IBackupService backupService,
    CoveConfiguration config,
    ILogger<DatabaseController> logger,
    NameRuleEnforcementService? nameRuleEnforcement = null) : ControllerBase
{
    [HttpPost("backup")]
    [RequiresPermission(Permissions.SystemBackup)]
    public async Task<ActionResult<BackupResultDto>> BackupDatabase(CancellationToken ct)
    {
        var backup = await backupService.CreateBackupAsync("manual", ct);
        return Ok(backup);
    }

    [HttpPost("restore")]
    [RequiresPermission(Permissions.SystemRestore)]
    public async Task<ActionResult<RestoreBackupResultDto>> RestoreDatabase([FromBody] RestoreBackupRequestDto request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.BackupPath))
            return BadRequest(new { message = "Backup path is required." });

        logger.LogWarning("Database restore initiated from {Path}", request.BackupPath);
        var preRestoreBackup = await backupService.CreateBackupAsync("pre_restore", ct);
        logger.LogInformation("Pre-restore database backup created at {Path}", preRestoreBackup.BackupPath);
        await backupService.RestoreBackupAsync(request.BackupPath, ct);
        return Ok(new RestoreBackupResultDto("Database restored successfully", request.BackupPath, preRestoreBackup.BackupPath));
    }

    [HttpPost("migrate")]
    [RequiresPermission(Permissions.SystemSettingsWrite)]
    public async Task<ActionResult<DatabaseMigrationResultDto>> MigrateDatabase(CancellationToken ct)
    {
        var pendingMigrations = (await db.Database.GetPendingMigrationsAsync(ct)).ToArray();
        if (pendingMigrations.Length == 0)
        {
            return Ok(new DatabaseMigrationResultDto(
                "Database is already up to date",
                [],
                [],
                null,
                MigrationRequired: false));
        }

        logger.LogWarning(
            "Manual database migration initiated for {Count} pending migration(s): {Migrations}",
            pendingMigrations.Length,
            string.Join(", ", pendingMigrations));

        NameRuleUpgradePreparation? nameRulePreparation = null;
        if (pendingMigrations.Contains(NameRuleEnforcementService.MigrationId, StringComparer.Ordinal))
        {
            if (nameRuleEnforcement == null)
                throw new InvalidOperationException("The name-rule enforcement service is unavailable.");

            try
            {
                nameRulePreparation = await nameRuleEnforcement.PreflightAsync(ct);
            }
            catch (NameRuleUpgradeBlockedException exception)
            {
                logger.LogWarning(
                    "Name-rule upgrade preflight blocked migration with {GroupCount} unresolved groups and {ClaimCount} claims",
                    exception.UnresolvedGroupCount,
                    exception.UnresolvedClaimCount);
                return Conflict(new
                {
                    code = "NAME_RULE_CONFLICTS",
                    message = exception.Message,
                    unresolvedGroupCount = exception.UnresolvedGroupCount,
                    unresolvedClaimCount = exception.UnresolvedClaimCount,
                    tagUnresolvedGroupCount = exception.TagGroupCount,
                    performerUnresolvedGroupCount = exception.PerformerGroupCount,
                    studioUnresolvedGroupCount = exception.StudioGroupCount,
                });
            }
        }

        var backup = await backupService.CreateBackupAsync("pre_migration", ct);
        logger.LogInformation("Pre-migration database backup created at {Path}", backup.BackupPath);

        // Data-backfill migrations (e.g. BackfillDenormalizedIdArrays) rewrite whole tables and can
        // easily exceed the default 30s command timeout on a large library — the command then times
        // out and EF's retry strategy re-runs it, looping. Lift the timeout for the gated migration
        // run so big datasets can finish. The context is request-scoped, so this only affects this call.
        db.Database.SetCommandTimeout(TimeSpan.FromHours(2));
        await using var nameRuleStaging = nameRulePreparation != null
            ? await nameRuleEnforcement!.StageAsync(nameRulePreparation, ct)
            : null;
        try
        {
            await db.Database.MigrateAsync(ct);
        }
        catch (PostgresException exception) when (NameRuleEnforcementService.IsGuardFailure(exception))
        {
            logger.LogWarning("Name-rule upgrade guard rejected a concurrently changed or unstaged database");
            return Conflict(new
            {
                code = "NAME_RULE_PREFLIGHT_CHANGED",
                message = NameRuleEnforcementService.GuardFailureMessage,
                preMigrationBackupPath = backup.BackupPath,
            });
        }

        var remainingMigrations = (await db.Database.GetPendingMigrationsAsync(ct)).ToArray();
        logger.LogInformation(
            "Manual database migration completed. Applied {Count} migration(s); {RemainingCount} remain pending",
            pendingMigrations.Length,
            remainingMigrations.Length);

        return Ok(new DatabaseMigrationResultDto(
            "Database migrations applied successfully",
            pendingMigrations,
            remainingMigrations,
            backup.BackupPath,
            MigrationRequired: remainingMigrations.Length > 0));
    }

    // Build a fresh, non-pooled connection string for maintenance statements (VACUUM/TRUNCATE)
    // that must run outside a transaction and outside the EF pool. Use the configured connection
    // string (password intact) rather than db.Database.GetConnectionString(): EF is backed by an
    // NpgsqlDataSource, so GetConnectionString() returns a password-REDACTED string, which throws
    // "No password has been provided (SASL/SCRAM-SHA-256)" against a password-protected server.
    private string BuildMaintenanceConnectionString()
        => new NpgsqlConnectionStringBuilder(config.DatabaseConnectionString) { Pooling = false }.ConnectionString;

    [HttpPost("optimize")]
    [RequiresPermission(Permissions.SystemSettingsWrite)]
    public async Task<IActionResult> OptimizeDatabase(CancellationToken ct)
    {
        // VACUUM cannot run inside a transaction — use a raw connection
        var connStr = BuildMaintenanceConnectionString();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(ct);

        var relationCount = await OptimizeRelationsAsync(conn, ct);

        logger.LogInformation("Database optimized (VACUUM ANALYZE) for {RelationCount} relation(s)", relationCount);
        return Ok(new { message = "Database optimized" });
    }

    internal static async Task<int> OptimizeRelationsAsync(NpgsqlConnection conn, CancellationToken ct = default)
    {
        var relations = new List<DatabaseRelation>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH database_owner AS (
                SELECT database.datdba
                FROM pg_catalog.pg_database database
                WHERE database.datname = current_database()
            )
            SELECT namespace.nspname, relation.relname
            FROM pg_catalog.pg_class relation
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            CROSS JOIN database_owner
            WHERE relation.relkind IN ('r', 'p', 'm')
              AND namespace.nspname NOT LIKE 'pg\_%' ESCAPE '\'
              AND namespace.nspname <> 'information_schema'
              AND (
                  pg_catalog.pg_has_role(current_user, relation.relowner, 'USAGE')
                  OR pg_catalog.pg_has_role(current_user, database_owner.datdba, 'USAGE')
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_catalog.pg_inherits inheritance
                  JOIN pg_catalog.pg_class parent ON parent.oid = inheritance.inhparent
                  WHERE inheritance.inhrelid = relation.oid
                    AND parent.relkind IN ('r', 'p')
                    AND (
                        pg_catalog.pg_has_role(current_user, parent.relowner, 'USAGE')
                        OR pg_catalog.pg_has_role(current_user, database_owner.datdba, 'USAGE')
                    )
              )
            ORDER BY namespace.nspname, relation.relname
            """;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                relations.Add(new DatabaseRelation(reader.GetString(0), reader.GetString(1)));
        }

        if (relations.Count > 0)
        {
            cmd.CommandText = BuildVacuumAnalyzeCommand(relations);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return relations.Count;
    }

    internal static string BuildVacuumAnalyzeCommand(IReadOnlyList<DatabaseRelation> relations)
        => $"VACUUM ANALYZE {string.Join(", ", relations.Select(relation => $"{QuoteIdentifier(relation.Schema)}.{QuoteIdentifier(relation.Name)}"))}";

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    internal readonly record struct DatabaseRelation(string Schema, string Name);

    [HttpPost("wipe")]
    [RequiresPermission(Permissions.SystemWipe)]
    public async Task<ActionResult<WipeResultDto>> WipeDatabase(CancellationToken ct)
    {
        logger.LogWarning("Database + config wipe initiated");
        var backup = await backupService.CreateBackupAsync("pre_wipe", ct);
        var configBackup = await backupService.CreateConfigBackupAsync("pre_wipe", ct);

        var connStr = BuildMaintenanceConnectionString();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        // TRUNCATE root tables with CASCADE clears all dependent junction tables
        cmd.CommandText = @"
            TRUNCATE TABLE videos, performers, tags, studios, galleries, images, groups,
                           folders, files, saved_filters,
                           ai_runs, embeddings, detections, segments, segment_display_profiles,
                           faces, tag_applications, extension_data
            RESTART IDENTITY CASCADE;";
        await cmd.ExecuteNonQueryAsync(ct);

        // Remove the on-disk config so the setup wizard reappears on next launch.
        try
        {
            var configPath = Path.Combine(CoveDefaultPaths.GetDataRoot(), "cove-config.json");
            if (System.IO.File.Exists(configPath))
                System.IO.File.Delete(configPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete user config file during wipe");
        }

        logger.LogInformation("Database wiped successfully after backups DB={DbPath} Config={ConfigPath}", backup.BackupPath, configBackup?.BackupPath);
        return Ok(new WipeResultDto(
            "Database and config wiped successfully",
            backup.BackupPath,
            backup.Timestamp,
            configBackup?.BackupPath));
    }

    [HttpPost("config/backup")]
    [RequiresPermission(Permissions.SystemBackup)]
    public async Task<ActionResult<ConfigBackupResultDto>> BackupConfig(CancellationToken ct)
    {
        var result = await backupService.CreateConfigBackupAsync("manual", ct);
        if (result == null)
            return NotFound(new { message = "No saved config to back up." });
        return Ok(result);
    }

    [HttpPost("config/restore")]
    [RequiresPermission(Permissions.SystemRestore)]
    public async Task<IActionResult> RestoreConfig([FromBody] RestoreBackupRequestDto request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.BackupPath))
            return BadRequest(new { message = "Config backup path is required." });

        logger.LogWarning("Config restore initiated from {Path}", request.BackupPath);
        await backupService.RestoreConfigBackupAsync(request.BackupPath, ct);
        return Ok(new { message = "Config restored successfully. Restart Cove for changes to take effect.", backupPath = request.BackupPath });
    }

    [HttpGet("config/latest-backup")]
    [RequiresPermission(Permissions.SystemRead)]
    public async Task<ActionResult<object>> GetLatestConfigBackup(CancellationToken ct)
    {
        var path = await backupService.GetLatestConfigBackupPathAsync(ct);
        return Ok(new { path });
    }
}
