using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Cove.Api.Services;
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
    ConfigService configService,
    NameRuleEnforcementService? nameRuleEnforcement = null,
    CustomFieldJsonIndexJobService? jsonIndexJobs = null,
    DatabaseMigrationCoordinator? migrations = null) : ControllerBase
{
    [HttpPost("backup")]
    [RequiresPermission(Permissions.SystemBackup)]
    [RequiresUnscopedEntityAccess("read")]
    public async Task<ActionResult<BackupResultDto>> BackupDatabase(CancellationToken ct)
    {
        var backup = await backupService.CreateBackupAsync("manual", ct);
        return Ok(backup);
    }

    [HttpPost("restore")]
    [RequiresPermission(Permissions.SystemRestore)]
    [RequiresUnscopedEntityAccess("read")]
    [RequiresUnscopedEntityAccess("write")]
    [RequiresUnscopedEntityAccess("delete")]
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
    [RequiresUnscopedEntityAccess("read")]
    [RequiresUnscopedEntityAccess("write")]
    [RequiresUnscopedEntityAccess("delete")]
    public async Task<ActionResult<DatabaseMigrationResultDto>> MigrateDatabase(CancellationToken ct)
    {
        if (migrations == null)
            throw new InvalidOperationException("The database migration coordinator is unavailable.");

        // The request only waits: closing or reloading the page leaves the migration running, and a
        // request made while one runs joins it rather than starting another.
        return await migrations.RunOrJoin(RunMigrationAsync).WaitAsync(ct);
    }

    private static async Task<ActionResult> RunMigrationAsync(IServiceProvider services, Action<string> setPhase, CancellationToken ct)
    {
        var db = services.GetRequiredService<CoveContext>();
        var backupService = services.GetRequiredService<IBackupService>();
        var logger = services.GetRequiredService<ILogger<DatabaseController>>();
        var nameRuleEnforcement = services.GetService<NameRuleEnforcementService>();
        var jsonIndexJobs = services.GetService<CustomFieldJsonIndexJobService>();

        var pendingMigrations = (await db.Database.GetPendingMigrationsAsync(ct)).ToArray();
        if (pendingMigrations.Length == 0)
        {
            // A retry after a committed migration may arrive after the original request was
            // cancelled, before it could enqueue post-migration index reconciliation.
            jsonIndexJobs?.RequestReconcile();
            return new OkObjectResult(new DatabaseMigrationResultDto(
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
                return new ConflictObjectResult(new
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

        setPhase(DatabaseMigrationCoordinator.BackupPhase);
        var backup = await backupService.CreateBackupAsync("pre_migration", ct);
        logger.LogInformation("Pre-migration database backup created at {Path}", backup.BackupPath);

        // Data-backfill migrations (e.g. BackfillDenormalizedIdArrays) rewrite whole tables and can
        // easily exceed the default 30s command timeout on a large library — the command then times
        // out and EF's retry strategy re-runs it, looping. Lift the timeout for the gated migration
        // run so big datasets can finish. The context belongs to this run's scope, so this affects nothing else.
        db.Database.SetCommandTimeout(TimeSpan.FromHours(2));
        setPhase(DatabaseMigrationCoordinator.MigratePhase);
        await using var nameRuleStaging = nameRulePreparation != null
            ? await nameRuleEnforcement!.StageAsync(nameRulePreparation, ct)
            : null;
        try
        {
            await db.Database.MigrateAsync(ct);
            // MigrateAsync returns after its migration transactions commit. Schedule immediately so
            // later request cancellation cannot strand the newly available JSON index functions.
            jsonIndexJobs?.RequestReconcile();
        }
        catch (PostgresException exception) when (NameRuleEnforcementService.IsGuardFailure(exception))
        {
            logger.LogWarning("Name-rule upgrade guard rejected a concurrently changed or unstaged database");
            return new ConflictObjectResult(new
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
        return new OkObjectResult(new DatabaseMigrationResultDto(
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
    // TCP keepalive lets a long statement without a command timeout notice a connection that was
    // dropped silently, instead of waiting on it forever.
    private string BuildMaintenanceConnectionString()
        => new NpgsqlConnectionStringBuilder(config.DatabaseConnectionString) { Pooling = false, TcpKeepAlive = true }.ConnectionString;

    internal const string OptimizeJobType = "optimize";

    // VACUUM ANALYZE over a large library takes far longer than an HTTP request should stay open, and
    // a reverse proxy's idle timeout or a closed tab would abort the request and cancel it. Run it as a
    // job instead: it continues server-side, reports per-table progress, and stops only when the job is
    // cancelled or the server shuts down. Plain VACUUM does not block reads or writes, so the job is not
    // exclusive and does not hold up scans or other jobs; a request while one is pending or running
    // returns that job instead of queueing another full pass.
    [HttpPost("optimize")]
    [RequiresPermission(Permissions.SystemSettingsWrite)]
    public ActionResult<object> OptimizeDatabase([FromServices] IJobService jobService)
    {
        var active = jobService.GetAllJobs().FirstOrDefault(job =>
            job.Type == OptimizeJobType && job.Status is JobStatus.Pending or JobStatus.Running);
        if (active is not null)
            return Accepted(new { jobId = active.Id });

        var connStr = BuildMaintenanceConnectionString();
        var log = logger;
        var jobId = jobService.Enqueue(OptimizeJobType, "Optimizing database", async (progress, ct) =>
        {
            // VACUUM cannot run inside a transaction — use a raw connection
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

            var result = await OptimizeRelationsAsync(conn, ct, progress);

            log.LogInformation(
                "Database optimized (VACUUM ANALYZE) for {RelationCount} relation(s); {SkippedCount} no longer existed",
                result.Vacuumed, result.Skipped.Count);
            progress.SetSummary(result.Skipped.Count == 0
                ? $"Vacuumed and analyzed {result.Vacuumed} table(s)"
                : $"Vacuumed and analyzed {result.Vacuumed} table(s); skipped {result.Skipped.Count} that no longer existed");
        }, exclusive: false);

        return Accepted(new { jobId });
    }

    internal sealed record OptimizeResult(int Vacuumed, IReadOnlyList<DatabaseRelation> Skipped);

    internal static async Task<OptimizeResult> OptimizeRelationsAsync(NpgsqlConnection conn, CancellationToken ct = default, IJobProgress? progress = null)
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

        // One table per statement so the job reports which table it is on. A single table can still
        // outlast the connection's command timeout, or wait behind a conflicting lock, so the statement
        // has no timeout of its own; the cancellation token cancels it on the server.
        cmd.CommandTimeout = 0;
        var skipped = new List<DatabaseRelation>();
        for (var i = 0; i < relations.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var relation = relations[i];
            progress?.Report((double)i / relations.Count, $"Vacuuming {relation.Schema}.{relation.Name}");
            cmd.CommandText = BuildVacuumAnalyzeCommand([relation]);
            try
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName)
            {
                // Dropped or renamed since it was listed, for example by an extension or a migration
                // that ran during the pass. There is nothing left to vacuum.
                skipped.Add(relation);
            }
        }

        return new OptimizeResult(relations.Count - skipped.Count, skipped);
    }

    internal static string BuildVacuumAnalyzeCommand(IReadOnlyList<DatabaseRelation> relations)
        => $"VACUUM ANALYZE {string.Join(", ", relations.Select(relation => $"{QuoteIdentifier(relation.Schema)}.{QuoteIdentifier(relation.Name)}"))}";

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    internal readonly record struct DatabaseRelation(string Schema, string Name);

    [HttpPost("wipe")]
    [RequiresPermission(Permissions.SystemWipe)]
    [RequiresUnscopedEntityAccess("read")]
    [RequiresUnscopedEntityAccess("delete")]
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

        // Parse before copying so a file that is not a Cove config never replaces cove-config.json.
        CoveConfigDto restored;
        try
        {
            restored = await configService.ReadConfigFileAsync(request.BackupPath, ct);
        }
        catch (FileNotFoundException)
        {
            return NotFound(new { message = $"Config backup not found: {request.BackupPath}" });
        }
        catch (InvalidDataException ex)
        {
            return BadRequest(new { message = ex.Message });
        }

        await backupService.RestoreConfigBackupAsync(request.BackupPath, ct);
        // Apply to the running process the same way startup applies cove-config.json, so the restore
        // takes effect now and later saves do not write the old in-memory config back over it.
        await configService.ApplyWithoutSavingAsync(restored);
        return Ok(new RestoreConfigResultDto("Config restored and applied.", request.BackupPath));
    }

    [HttpGet("config/latest-backup")]
    [RequiresPermission(Permissions.SystemBackup)]
    public async Task<ActionResult<object>> GetLatestConfigBackup(CancellationToken ct)
    {
        var path = await backupService.GetLatestConfigBackupPathAsync(ct);
        return Ok(new { path });
    }
}

public sealed record RestoreConfigResultDto(string Message, string BackupPath);
