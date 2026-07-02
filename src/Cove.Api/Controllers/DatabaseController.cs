using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;
using Cove.Data;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[RequiresPermission(Permissions.SystemRead)]
public class DatabaseController(CoveContext db, IBackupService backupService, CoveConfiguration config, ILogger<DatabaseController> logger) : ControllerBase
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

        var backup = await backupService.CreateBackupAsync("pre_migration", ct);
        logger.LogInformation("Pre-migration database backup created at {Path}", backup.BackupPath);

        // Data-backfill migrations (e.g. BackfillDenormalizedIdArrays) rewrite whole tables and can
        // easily exceed the default 30s command timeout on a large library — the command then times
        // out and EF's retry strategy re-runs it, looping. Lift the timeout for the gated migration
        // run so big datasets can finish. The context is request-scoped, so this only affects this call.
        db.Database.SetCommandTimeout(TimeSpan.FromHours(2));
        await db.Database.MigrateAsync(ct);

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
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM ANALYZE";
        await cmd.ExecuteNonQueryAsync(ct);
        logger.LogInformation("Database optimized (VACUUM ANALYZE)");
        return Ok(new { message = "Database optimized" });
    }

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

