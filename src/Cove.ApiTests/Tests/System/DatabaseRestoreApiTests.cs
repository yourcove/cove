using Cove.ApiTests.Infrastructure;

namespace Cove.ApiTests.Tests.System;

public sealed class DatabaseRestoreApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/database/restore")]
    public async Task GivenDatabaseBackup_WhenOwnerRestoresIt_ThenOnlyTheBackupPointRemains()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var retained = await AsUser().CreateVideoAsync($"Database restore retained {suffix}", TestContext.Current.CancellationToken);
        var backup = await AsUser().BackupDatabaseAsync(TestContext.Current.CancellationToken);
        var addedAfterBackup = await AsUser().CreateVideoAsync($"Database restore removed {suffix}", TestContext.Current.CancellationToken);
        (await AsUser().GetVideoByIdAsync(retained.Id, TestContext.Current.CancellationToken)).Title.Should().Be(retained.Title);
        (await AsUser().GetVideoByIdAsync(addedAfterBackup.Id, TestContext.Current.CancellationToken)).Title.Should().Be(addedAfterBackup.Title);
        (await AsUser().GetLatestDatabaseBackupAsync(TestContext.Current.CancellationToken)).Path.Should().Be(backup.BackupPath);

        var forbidden = () => AsUser(ApiTestUsers.Eva).RestoreDatabaseAsync(backup.BackupPath);
        await forbidden.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await AsUser().GetLatestDatabaseBackupAsync(TestContext.Current.CancellationToken)).Path.Should().Be(backup.BackupPath);
        (await AsUser().GetVideoByIdAsync(retained.Id, TestContext.Current.CancellationToken)).Title.Should().Be(retained.Title);
        (await AsUser().GetVideoByIdAsync(addedAfterBackup.Id, TestContext.Current.CancellationToken)).Title.Should().Be(addedAfterBackup.Title);

        RetireApiInstanceAfterClass();
        var restored = await AsUser().RestoreDatabaseAsync(backup.BackupPath, TestContext.Current.CancellationToken);
        restored.Message.Should().Be("Database restored successfully");
        restored.BackupPath.Should().Be(backup.BackupPath);
        restored.PreRestoreBackupPath.Should().NotBeNullOrWhiteSpace().And.NotBe(backup.BackupPath);
        File.Exists(restored.PreRestoreBackupPath).Should().BeTrue();
        new FileInfo(restored.PreRestoreBackupPath!).Length.Should().BeGreaterThan(0);

        var retainedAfterRestore = await AsUser().GetVideoByIdAsync(retained.Id, TestContext.Current.CancellationToken);
        retainedAfterRestore.Id.Should().Be(retained.Id);
        retainedAfterRestore.Title.Should().Be(retained.Title);
        var removed = () => AsUser().GetVideoByIdAsync(addedAfterBackup.Id);
        await removed.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
    }
}
