using Cove.ApiTests.Infrastructure;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.System;

[Collection(ApiTestLane1Collection.Name)]
public sealed class DatabaseRestoreApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/database/restore")]
    public async Task GivenDatabaseBackup_WhenOwnerRestoresIt_ThenOnlyTheBackupPointRemains()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var retained = await AsUser().CreateVideoAsync($"Database restore retained {suffix}");
        var backup = await AsUser().BackupDatabaseAsync();
        var addedAfterBackup = await AsUser().CreateVideoAsync($"Database restore removed {suffix}");
        (await AsUser().GetVideoByIdAsync(retained.Id)).Title.Should().Be(retained.Title);
        (await AsUser().GetVideoByIdAsync(addedAfterBackup.Id)).Title.Should().Be(addedAfterBackup.Title);
        (await AsUser().GetLatestDatabaseBackupAsync()).Path.Should().Be(backup.BackupPath);

        var forbidden = () => AsUser(ApiTestUsers.Eva).RestoreDatabaseAsync(backup.BackupPath);
        await forbidden.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await AsUser().GetLatestDatabaseBackupAsync()).Path.Should().Be(backup.BackupPath);
        (await AsUser().GetVideoByIdAsync(retained.Id)).Title.Should().Be(retained.Title);
        (await AsUser().GetVideoByIdAsync(addedAfterBackup.Id)).Title.Should().Be(addedAfterBackup.Title);

        var restored = await AsUser().RestoreDatabaseAsync(backup.BackupPath);
        restored.Message.Should().Be("Database restored successfully");
        restored.BackupPath.Should().Be(backup.BackupPath);
        restored.PreRestoreBackupPath.Should().NotBeNullOrWhiteSpace().And.NotBe(backup.BackupPath);
        File.Exists(restored.PreRestoreBackupPath).Should().BeTrue();
        new FileInfo(restored.PreRestoreBackupPath!).Length.Should().BeGreaterThan(0);

        var retainedAfterRestore = await AsUser().GetVideoByIdAsync(retained.Id);
        retainedAfterRestore.Id.Should().Be(retained.Id);
        retainedAfterRestore.Title.Should().Be(retained.Title);
        var removed = () => AsUser().GetVideoByIdAsync(addedAfterBackup.Id);
        await removed.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
    }
}
