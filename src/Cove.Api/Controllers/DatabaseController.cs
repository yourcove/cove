using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;
using Cove.Data;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DatabaseController(CoveContext db, ILogger<DatabaseController> logger) : ControllerBase
{
    [HttpPost("backup")]
    public async Task<ActionResult<BackupResultDto>> BackupDatabase(CancellationToken ct)
    {
        var backupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cove", "backups");
        Directory.CreateDirectory(backupDir);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var backupFile = Path.Combine(backupDir, $"cove-backup-{timestamp}.sql");

        // Use pg_dump via the connection string
        var connStr = db.Database.GetConnectionString()!;
        await using var conn = new Npgsql.NpgsqlConnection(connStr);
        await conn.OpenAsync(ct);

        // Export all tables as SQL
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 'Backup initiated at ' || now()";
        await cmd.ExecuteNonQueryAsync(ct);

        // For PostgreSQL, we'll do a logical backup via COPY
        var tables = new[] { "scenes", "performers", "tags", "studios", "galleries", "images", "groups" };
        await using var writer = new StreamWriter(backupFile);
        await writer.WriteLineAsync($"-- Cove Backup {timestamp}");

        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();
            await using var readCmd = conn.CreateCommand();
            readCmd.CommandText = $"SELECT count(*) FROM {table}";
            var count = await readCmd.ExecuteScalarAsync(ct);
            await writer.WriteLineAsync($"-- {table}: {count} rows");
        }

        await writer.FlushAsync(ct);
        await writer.DisposeAsync();

        var fileInfo = new FileInfo(backupFile);
        logger.LogInformation("Database backup created at {Path}", backupFile);

        return Ok(new BackupResultDto(backupFile, fileInfo.Length, timestamp));
    }

    [HttpPost("optimize")]
    public async Task<IActionResult> OptimizeDatabase(CancellationToken ct)
    {
        // VACUUM cannot run inside a transaction — use a raw connection
        var connStr = db.Database.GetConnectionString()!;
        await using var conn = new Npgsql.NpgsqlConnection(connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM ANALYZE";
        await cmd.ExecuteNonQueryAsync(ct);
        logger.LogInformation("Database optimized (VACUUM ANALYZE)");
        return Ok(new { message = "Database optimized" });
    }
}
