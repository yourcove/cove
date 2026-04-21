using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;
using Cove.Data;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DatabaseController(CoveContext db, IBackupService backupService, ILogger<DatabaseController> logger) : ControllerBase
{
    [HttpPost("backup")]
    public async Task<ActionResult<BackupResultDto>> BackupDatabase(CancellationToken ct)
    {
        var backup = await backupService.CreateBackupAsync("manual", ct);
        return Ok(backup);
    }

    [HttpPost("restore")]
    public async Task<IActionResult> RestoreDatabase([FromBody] RestoreBackupRequestDto request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.BackupPath))
            return BadRequest(new { message = "Backup path is required." });

        logger.LogWarning("Database restore initiated from {Path}", request.BackupPath);
        await backupService.RestoreBackupAsync(request.BackupPath, ct);
        return Ok(new { message = "Database restored successfully", backupPath = request.BackupPath });
    }

    [HttpPost("optimize")]
    public async Task<IActionResult> OptimizeDatabase(CancellationToken ct)
    {
        // VACUUM cannot run inside a transaction — use a raw connection
        var connStr = db.Database.GetConnectionString()!;
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM ANALYZE";
        await cmd.ExecuteNonQueryAsync(ct);
        logger.LogInformation("Database optimized (VACUUM ANALYZE)");
        return Ok(new { message = "Database optimized" });
    }

    [HttpPost("wipe")]
    public async Task<IActionResult> WipeDatabase(CancellationToken ct)
    {
        logger.LogWarning("Database wipe initiated");
        var backup = await backupService.CreateBackupAsync("pre_wipe", ct);

        var connStr = db.Database.GetConnectionString()!;
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        // TRUNCATE root tables with CASCADE clears all dependent junction tables
        cmd.CommandText = @"
            TRUNCATE TABLE scenes, performers, tags, studios, galleries, images, groups,
                           folders, files, saved_filters
            RESTART IDENTITY CASCADE;";
        await cmd.ExecuteNonQueryAsync(ct);

        logger.LogInformation("Database wiped successfully after backup {Path}", backup.BackupPath);
        return Ok(new { message = "Database wiped successfully", backupPath = backup.BackupPath, backupTimestamp = backup.Timestamp });
    }
}
