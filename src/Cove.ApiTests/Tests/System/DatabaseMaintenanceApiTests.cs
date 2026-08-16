using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Interfaces;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.System;

[Collection(ApiTestLane1Collection.Name)]
public sealed class DatabaseMaintenanceApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/database/migrate")]
    [CoversEndpoint("POST", "/api/database/optimize")]
    [CoversEndpoint("POST", "/api/database/backup")]
    [CoversEndpoint("POST", "/api/jobs/backup")]
    [CoversEndpoint("GET", "/api/jobs/backup/latest")]
    public async Task GivenDisposableDatabase_WhenMaintenanceAndBackupsRun_ThenPermissionsFilesJobsAndResultsAreExact()
    {
        var beforeLatest = await ReadLatestBackupAsync(AsUser());

        var forbiddenBackup = () => AsUser(ApiTestUsers.Eva).BackupDatabaseAsync();
        var forbiddenMigrate = () => AsUser(ApiTestUsers.Eva).MigrateDatabaseAsync();
        var forbiddenOptimize = () => AsUser(ApiTestUsers.Eva).OptimizeDatabaseAsync();
        var forbiddenBackupJob = () => AsUser(ApiTestUsers.Eva).StartDatabaseBackupJobAsync();
        var forbiddenLatest = () => AsUser(ApiTestUsers.Eva).GetLatestDatabaseBackupAsync();
        await forbiddenBackup.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        await forbiddenMigrate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        await forbiddenOptimize.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        await forbiddenBackupJob.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        await forbiddenLatest.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await ReadLatestBackupAsync(AsUser())).Should().Be(beforeLatest);
        (await AsUser().GetJobHistoryAsync()).Should().BeEmpty();
        (await AsUser().ReadEndpointAsync(ReadEndpoint.Jobs)).EnumerateArray().Should().BeEmpty();

        var migration = await AsUser().MigrateDatabaseAsync();
        migration.Message.Should().Be("Database is already up to date");
        migration.AppliedMigrations.Should().BeEmpty();
        migration.PendingMigrations.Should().BeEmpty();
        migration.PreMigrationBackupPath.Should().BeNull();
        migration.MigrationRequired.Should().BeFalse();

        (await AsUser().OptimizeDatabaseAsync()).Message.Should().Be("Database optimized");

        var startedAt = DateTime.UtcNow.AddSeconds(-1);
        var backup = await AsUser().BackupDatabaseAsync();
        AssertBackupFile(backup.BackupPath, backup.SizeBytes, backup.Timestamp, startedAt);

        var latestAfterDirect = await AsUser().GetLatestDatabaseBackupAsync();
        latestAfterDirect.Path.Should().Be(backup.BackupPath);

        await WaitForNextBackupTimestampAsync(backup.Timestamp);
        var jobStartedAt = DateTime.UtcNow.AddSeconds(-1);
        var backupJobId = await AsUser().StartDatabaseBackupJobAsync();
        var completed = await AsUser().WaitForTerminalJobAsync(backupJobId);
        completed.Id.Should().Be(backupJobId);
        completed.Type.Should().Be("backup");
        completed.Description.Should().Be("Backing up database");
        completed.Status.Should().Be(JobStatus.Completed);
        completed.Progress.Should().Be(1);
        completed.Error.Should().BeNull();
        completed.CompletedAt.Should().NotBeNull().And.BeOnOrAfter(completed.StartedAt);

        var latestAfterJob = await AsUser().GetLatestDatabaseBackupAsync();
        latestAfterJob.Path.Should().NotBe(backup.BackupPath);
        File.Exists(latestAfterJob.Path).Should().BeTrue();
        var latestFile = new FileInfo(latestAfterJob.Path);
        latestFile.Length.Should().BeGreaterThan(0);
        latestFile.LastWriteTimeUtc.Should().BeOnOrAfter(jobStartedAt).And.BeOnOrBefore(DateTime.UtcNow.AddSeconds(1));
        Path.GetDirectoryName(latestAfterJob.Path).Should().Be(Path.GetDirectoryName(backup.BackupPath));
        Path.GetFileName(latestAfterJob.Path).Should().StartWith("cove_backup_").And.EndWith("_manual.sql");
        var historyJob = (await AsUser().GetJobHistoryAsync()).Should().ContainSingle().Which;
        historyJob.Id.Should().Be(backupJobId);
        historyJob.Type.Should().Be("backup");
        historyJob.Description.Should().Be("Backing up database");
        historyJob.Status.Should().Be(JobStatus.Completed);
        historyJob.Progress.Should().Be(1);
        historyJob.Error.Should().BeNull();
        (await AsUser().ReadEndpointAsync(ReadEndpoint.Jobs)).EnumerateArray().Should().BeEmpty();
    }

    private static async Task<(HttpStatusCode Status, string? Path)> ReadLatestBackupAsync(CoveClient client)
    {
        using var httpClient = client.CreateHttpClient();
        using var response = await httpClient.GetAsync($"/api/jobs/backup/latest?nonce={Guid.NewGuid():N}");
        var payload = response.StatusCode == HttpStatusCode.OK
            ? await response.Content.ReadFromJsonAsync<LatestBackupResponse>(ApiJson.Options)
            : null;
        return (response.StatusCode, payload?.Path);
    }

    private static async Task WaitForNextBackupTimestampAsync(string previousTimestamp)
    {
        while (DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) == previousTimestamp)
            await Task.Delay(25);
    }

    private static void AssertBackupFile(
        string path,
        long size,
        string timestamp,
        DateTime startedAt)
    {
        path.Should().NotBeNullOrWhiteSpace();
        File.Exists(path).Should().BeTrue();
        var file = new FileInfo(path);
        file.Length.Should().Be(size).And.BeGreaterThan(0);
        file.Extension.Should().Be(".sql");
        file.Name.Should().StartWith("cove_backup_").And.EndWith("_manual.sql");
        DateTime.TryParseExact(
            timestamp,
            "yyyyMMdd_HHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed).Should().BeTrue();
        parsed.Should().BeOnOrAfter(startedAt).And.BeOnOrBefore(DateTime.UtcNow.AddSeconds(1));
    }
}
