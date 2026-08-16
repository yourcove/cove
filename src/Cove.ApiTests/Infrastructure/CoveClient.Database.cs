using System.Net;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<BackupResultDto> BackupDatabaseAsync(
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<BackupResultDto>(
            HttpMethod.Post,
            "/api/database/backup",
            payload: null,
            HttpStatusCode.OK,
            cancellationToken);

    public Task<DatabaseMigrationResultDto> MigrateDatabaseAsync(
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<DatabaseMigrationResultDto>(
            HttpMethod.Post,
            "/api/database/migrate",
            payload: null,
            HttpStatusCode.OK,
            cancellationToken);

    public Task<RestoreBackupResultDto> RestoreDatabaseAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<RestoreBackupResultDto>(
            HttpMethod.Post,
            "/api/database/restore",
            new RestoreBackupRequestDto(backupPath),
            HttpStatusCode.OK,
            cancellationToken);

    public Task<DatabaseOperationMessage> OptimizeDatabaseAsync(
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<DatabaseOperationMessage>(
            HttpMethod.Post,
            "/api/database/optimize",
            payload: null,
            HttpStatusCode.OK,
            cancellationToken);

    public Task<string> StartDatabaseBackupJobAsync(
        CancellationToken cancellationToken = default)
        => StartJobAsync("/api/jobs/backup", cancellationToken);

    public Task<LatestBackupResponse> GetLatestDatabaseBackupAsync(
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<LatestBackupResponse>(
            HttpMethod.Get,
            WithCacheNonce("/api/jobs/backup/latest"),
            payload: null,
            HttpStatusCode.OK,
            cancellationToken);
}

public sealed record DatabaseOperationMessage(string Message);

public sealed record LatestBackupResponse(string Path);
