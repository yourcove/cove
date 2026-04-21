using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Cove.Core.Interfaces;
using Cove.Core.DTOs;
using Npgsql;

namespace Cove.Api.Services;

public class BackupService(
    IJobService jobService,
    CoveConfiguration config,
    ILogger<BackupService> logger) : IBackupService
{
    private enum BackupFormat
    {
        PlainSql,
        CustomDump,
    }

    private static string BackupDir => CoveDefaultPaths.GetDataSubdirectory("backups");

    public async Task<BackupResultDto> CreateBackupAsync(string? reason = null, CancellationToken ct = default)
    {
        var backupDir = BackupDir;
        Directory.CreateDirectory(backupDir);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var suffix = ToFileSafeToken(reason);
        var fileName = string.IsNullOrEmpty(suffix)
            ? $"cove_backup_{timestamp}.sql"
            : $"cove_backup_{timestamp}_{suffix}.sql";
        var backupPath = Path.Combine(backupDir, fileName);

        try
        {
            await RunPgDumpAsync(backupPath, ct);

            var fileInfo = new FileInfo(backupPath);
            if (!fileInfo.Exists || fileInfo.Length == 0)
                throw new InvalidOperationException($"Backup completed but {backupPath} was empty.");

            logger.LogInformation("Database backup created at {Path}", backupPath);
            return new BackupResultDto(backupPath, fileInfo.Length, timestamp);
        }
        catch
        {
            try
            {
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
            }
            catch (Exception cleanupEx)
            {
                logger.LogWarning(cleanupEx, "Failed to clean up incomplete backup at {Path}", backupPath);
            }

            throw;
        }
    }

    public string StartBackup()
    {
        return jobService.Enqueue("backup", "Backing up database", async (progress, ct) =>
        {
            progress.Report(0.1, "Starting backup...");

            try
            {
                progress.Report(0.3, "Running pg_dump...");
                var backup = await CreateBackupAsync("manual", ct);

                progress.Report(1.0, "Backup complete");
                logger.LogInformation("Database backed up to {Path}", backup.BackupPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Backup failed");
                throw;
            }
        }, exclusive: true);
    }

    public async Task RestoreBackupAsync(string backupPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
            throw new ArgumentException("Backup path is required.", nameof(backupPath));

        if (!File.Exists(backupPath))
            throw new FileNotFoundException($"Backup file not found: {backupPath}", backupPath);

        var format = await DetectBackupFormatAsync(backupPath, ct);
        var builder = new NpgsqlConnectionStringBuilder(GetConfiguredConnectionString());

        NpgsqlConnection.ClearAllPools();
        await TerminateDatabaseConnectionsAsync(builder, ct);

        try
        {
            switch (format)
            {
                case BackupFormat.CustomDump:
                    await RunPgRestoreAsync(builder, backupPath, ct);
                    break;
                case BackupFormat.PlainSql:
                    await RunPsqlRestoreAsync(builder, backupPath, ct);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported backup format for {backupPath}.");
            }

            logger.LogInformation("Database restored from {Path}", backupPath);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
        }
    }

    public Task<string?> GetLatestBackupPathAsync(CancellationToken ct = default)
    {
        var backupDir = BackupDir;
        if (!Directory.Exists(backupDir))
            return Task.FromResult<string?>(null);

        var latest = Directory.EnumerateFiles(backupDir, "cove_backup_*")
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .FirstOrDefault();

        return Task.FromResult(latest);
    }

    private async Task RunPgDumpAsync(string backupPath, CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(GetConfiguredConnectionString());
        var startInfo = CreateToolStartInfo("pg_dump", builder.Password);

        startInfo.ArgumentList.Add("--format=plain");
        startInfo.ArgumentList.Add("--encoding=UTF8");
        startInfo.ArgumentList.Add("--clean");
        startInfo.ArgumentList.Add("--if-exists");
        startInfo.ArgumentList.Add("--no-owner");
        startInfo.ArgumentList.Add("--no-privileges");
        AddConnectionArguments(startInfo, builder);
        startInfo.ArgumentList.Add("--file");
        startInfo.ArgumentList.Add(backupPath);

        await RunToolAsync(startInfo, ct);
    }

    private async Task RunPgRestoreAsync(NpgsqlConnectionStringBuilder builder, string backupPath, CancellationToken ct)
    {
        var startInfo = CreateToolStartInfo("pg_restore", builder.Password);

        startInfo.ArgumentList.Add("--clean");
        startInfo.ArgumentList.Add("--if-exists");
        startInfo.ArgumentList.Add("--no-owner");
        startInfo.ArgumentList.Add("--no-privileges");
        AddConnectionArguments(startInfo, builder);
        startInfo.ArgumentList.Add("--dbname");
        startInfo.ArgumentList.Add(GetRequiredDatabaseName(builder));
        startInfo.ArgumentList.Add(backupPath);

        await RunToolAsync(startInfo, ct);
    }

    private async Task RunPsqlRestoreAsync(NpgsqlConnectionStringBuilder builder, string backupPath, CancellationToken ct)
    {
        var startInfo = CreateToolStartInfo("psql", builder.Password);

        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("ON_ERROR_STOP=1");
        AddConnectionArguments(startInfo, builder);
        startInfo.ArgumentList.Add("--file");
        startInfo.ArgumentList.Add(backupPath);

        await RunToolAsync(startInfo, ct);
    }

    private async Task RunToolAsync(ProcessStartInfo startInfo, CancellationToken ct)
    {
        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException($"Failed to start {startInfo.FileName}.");

            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);

            var stderr = await stderrTask;
            var stdout = await stdoutTask;
            if (process.ExitCode != 0)
            {
                var errorOutput = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                throw new InvalidOperationException($"{Path.GetFileName(startInfo.FileName)} failed: {errorOutput.Trim()}".Trim());
            }
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"Unable to start {Path.GetFileName(startInfo.FileName)}. Ensure PostgreSQL client tools are installed and available on PATH.",
                ex);
        }
    }

    private ProcessStartInfo CreateToolStartInfo(string toolName, string? password)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveToolPath(toolName),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrEmpty(password))
            startInfo.Environment["PGPASSWORD"] = password;

        return startInfo;
    }

    private static void AddConnectionArguments(ProcessStartInfo startInfo, NpgsqlConnectionStringBuilder builder)
    {
        var host = string.IsNullOrWhiteSpace(builder.Host) ? "localhost" : builder.Host;
        var username = GetRequiredUsername(builder);
        var database = GetRequiredDatabaseName(builder);

        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add(host);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(builder.Port.ToString());
        startInfo.ArgumentList.Add("--username");
        startInfo.ArgumentList.Add(username);
        startInfo.ArgumentList.Add("--dbname");
        startInfo.ArgumentList.Add(database);
    }

    private string ResolveToolPath(string toolName)
    {
        var executableName = OperatingSystem.IsWindows() && !toolName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? toolName + ".exe"
            : toolName;

        if (config.Postgres.Managed)
        {
            var managedDataPath = string.IsNullOrWhiteSpace(config.Postgres.DataPath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cove")
                : config.Postgres.DataPath;
            var managedCandidate = Path.Combine(managedDataPath, "pgsql", "bin", executableName);
            if (File.Exists(managedCandidate))
                return managedCandidate;
        }

        var pgBin = Environment.GetEnvironmentVariable("PG_BIN");
        if (!string.IsNullOrWhiteSpace(pgBin))
        {
            var directCandidate = Path.Combine(pgBin, executableName);
            if (File.Exists(directCandidate))
                return directCandidate;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(dir, executableName);
            if (File.Exists(candidate))
                return candidate;
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     }.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                var postgresRoot = Path.Combine(root, "PostgreSQL");
                if (!Directory.Exists(postgresRoot))
                    continue;

                foreach (var versionDir in Directory.EnumerateDirectories(postgresRoot))
                {
                    var candidate = Path.Combine(versionDir, "bin", executableName);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }

        return executableName;
    }

    private async Task<BackupFormat> DetectBackupFormatAsync(string backupPath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(backupPath);
        var headerBuffer = new byte[5];
        var read = await stream.ReadAsync(headerBuffer.AsMemory(0, headerBuffer.Length), ct);
        if (read == headerBuffer.Length && Encoding.ASCII.GetString(headerBuffer) == "PGDMP")
            return BackupFormat.CustomDump;

        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var sawSqlContent = false;
        for (var linesChecked = 0; linesChecked < 200; linesChecked++)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null)
                break;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!line.TrimStart().StartsWith("--", StringComparison.Ordinal))
            {
                sawSqlContent = true;
                break;
            }
        }

        if (!sawSqlContent)
            throw new InvalidOperationException($"{backupPath} is not a restorable backup. It only contains metadata comments.");

        return BackupFormat.PlainSql;
    }

    private async Task TerminateDatabaseConnectionsAsync(NpgsqlConnectionStringBuilder builder, CancellationToken ct)
    {
        var databaseName = GetRequiredDatabaseName(builder);
        var maintenanceDatabase = string.Equals(databaseName, "postgres", StringComparison.OrdinalIgnoreCase)
            ? "template1"
            : "postgres";

        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(builder.ConnectionString)
        {
            Database = maintenanceDatabase,
            Pooling = false,
        };

        await using var conn = new NpgsqlConnection(maintenanceBuilder.ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT pg_terminate_backend(pid)
            FROM pg_stat_activity
            WHERE datname = @database AND pid <> pg_backend_pid();";
        cmd.Parameters.AddWithValue("database", databaseName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string GetRequiredDatabaseName(NpgsqlConnectionStringBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Database))
            throw new InvalidOperationException("Database name is missing from the PostgreSQL connection string.");

        return builder.Database;
    }

    private static string GetRequiredUsername(NpgsqlConnectionStringBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Username))
            throw new InvalidOperationException("Username is missing from the PostgreSQL connection string.");

        return builder.Username;
    }

    private string GetConfiguredConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(config.DatabaseConnectionString))
            return config.DatabaseConnectionString;

        if (!string.IsNullOrWhiteSpace(config.Postgres.ConnectionString))
            return config.Postgres.ConnectionString;

        if (config.Postgres.Managed)
        {
            return $"Host=127.0.0.1;Port={config.Postgres.Port};Database={config.Postgres.Database};Username=postgres;Trust Server Certificate=true;Timeout=15;Command Timeout=30";
        }

        throw new InvalidOperationException("PostgreSQL connection string is not configured.");
    }

    private static string? ToFileSafeToken(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return null;

        var builder = new StringBuilder(reason.Length);
        foreach (var character in reason)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length == 0 || builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        var sanitized = builder.ToString().Trim('_');
        return string.IsNullOrEmpty(sanitized) ? null : sanitized;
    }
}
