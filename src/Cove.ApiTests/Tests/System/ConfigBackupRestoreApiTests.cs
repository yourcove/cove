using System.Globalization;
using System.Text;
using Cove.ApiTests.Infrastructure;

namespace Cove.ApiTests.Tests.System;

[Collection(ApiTestLane1Collection.Name)]
public sealed class ConfigBackupRestoreApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/database/config/backup")]
    [CoversEndpoint("POST", "/api/database/config/restore")]
    public async Task GivenSavedTestConfig_WhenOwnerBacksUpAndRestoresIt_ThenFilesPermissionsAndSnapshotsAreExact()
    {
        var dataRoot = Path.GetDirectoryName(AsTestFileSystem().GeneratedPath)
            ?? throw new InvalidOperationException("The generated path has no data-root parent.");
        var configPath = Path.Combine(dataRoot, "cove-config.json");
        var configExisted = File.Exists(configPath);
        var priorConfig = configExisted ? await File.ReadAllBytesAsync(configPath, TestContext.Current.CancellationToken) : null;
        var originalConfig = Encoding.UTF8.GetBytes($"{{\"apiTestMarker\":\"{Guid.NewGuid():N}\"}}");
        var changedConfig = Encoding.UTF8.GetBytes($"{{\"apiTestMarker\":\"changed-{Guid.NewGuid():N}\"}}");
        var backupDirectory = Path.Combine(dataRoot, "backups");

        try
        {
            await File.WriteAllBytesAsync(configPath, originalConfig, TestContext.Current.CancellationToken);
            var beforeForbiddenBackup = EnumerateConfigBackups(backupDirectory);
            var forbiddenBackup = () => AsUser(ApiTestUsers.Eva).BackupConfigAsync();
            await forbiddenBackup.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
            EnumerateConfigBackups(backupDirectory).Should().Equal(beforeForbiddenBackup);
            (await File.ReadAllBytesAsync(configPath, TestContext.Current.CancellationToken)).Should().Equal(originalConfig);

            var backup = await AsUser().BackupConfigAsync(TestContext.Current.CancellationToken);
            backup.BackupPath.Should().NotBeNullOrWhiteSpace();
            File.Exists(backup.BackupPath).Should().BeTrue();
            new FileInfo(backup.BackupPath).Length.Should().Be(backup.SizeBytes).And.Be(originalConfig.Length);
            Path.GetFileName(backup.BackupPath).Should().StartWith("cove_config_").And.EndWith("_manual.json");
            (await File.ReadAllBytesAsync(backup.BackupPath, TestContext.Current.CancellationToken)).Should().Equal(originalConfig);

            var beforeForbiddenRestore = EnumerateConfigBackups(backupDirectory);
            var forbiddenRestore = () => AsUser(ApiTestUsers.Eva).RestoreConfigAsync(backup.BackupPath);
            await forbiddenRestore.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
            EnumerateConfigBackups(backupDirectory).Should().Equal(beforeForbiddenRestore);
            (await File.ReadAllBytesAsync(configPath, TestContext.Current.CancellationToken)).Should().Equal(originalConfig);

            await File.WriteAllBytesAsync(configPath, changedConfig, TestContext.Current.CancellationToken);
            await WaitForNextBackupTimestampAsync(backup.Timestamp);
            var beforeRestore = EnumerateConfigBackups(backupDirectory);
            var restored = await AsUser().RestoreConfigAsync(backup.BackupPath, TestContext.Current.CancellationToken);
            restored.Message.Should().Be("Config restored successfully. Restart Cove for changes to take effect.");
            restored.BackupPath.Should().Be(backup.BackupPath);
            (await File.ReadAllBytesAsync(configPath, TestContext.Current.CancellationToken)).Should().Equal(originalConfig);

            var afterRestore = EnumerateConfigBackups(backupDirectory);
            var preRestorePath = afterRestore.Except(beforeRestore, StringComparer.Ordinal).Should().ContainSingle().Which;
            Path.GetFileName(preRestorePath).Should().StartWith("cove_config_").And.EndWith("_pre_restore.json");
            (await File.ReadAllBytesAsync(preRestorePath, TestContext.Current.CancellationToken)).Should().Equal(changedConfig);
            var latest = await AsUser().ReadEndpointAsync(ReadEndpoint.LatestConfigBackup, TestContext.Current.CancellationToken);
            latest.GetProperty("path").GetString().Should().Be(preRestorePath);
        }
        finally
        {
            if (configExisted)
                await File.WriteAllBytesAsync(configPath, priorConfig!, CancellationToken.None);
            else if (File.Exists(configPath))
                File.Delete(configPath);
        }
    }

    private static string[] EnumerateConfigBackups(string directory)
        => Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "cove_config_*.json").Order(StringComparer.Ordinal).ToArray()
            : [];

    private static async Task WaitForNextBackupTimestampAsync(string previousTimestamp)
    {
        while (DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) == previousTimestamp)
            await Task.Delay(25);
    }
}
