using System.Globalization;
using System.Text;
using Cove.ApiTests.Infrastructure;
using AwesomeAssertions.Execution;

namespace Cove.ApiTests.Tests.System;

public sealed class DatabaseWipeApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/database/wipe")]
    public async Task GivenDisposableLibraryAndConfig_WhenDatabaseIsWiped_ThenBackupsAndDeletionAreExact()
    {
        var dataRoot = Path.GetDirectoryName(AsTestFileSystem().GeneratedPath)
            ?? throw new InvalidOperationException("The generated path has no data-root parent.");
        var configPath = Path.Combine(dataRoot, "cove-config.json");
        var backupDirectory = Path.Combine(dataRoot, "backups");
        var configExisted = File.Exists(configPath);
        var priorConfig = configExisted ? await File.ReadAllBytesAsync(configPath, TestContext.Current.CancellationToken) : null;
        var markerConfig = Encoding.UTF8.GetBytes($"{{\"apiTestWipeMarker\":\"{Guid.NewGuid():N}\"}}");

        try
        {
            await File.WriteAllBytesAsync(configPath, markerConfig, TestContext.Current.CancellationToken);
            var retainedUntilWipe = await AsUser().CreateVideoAsync($"Database wipe target {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
            var retainedAudioUntilWipe = await AsUser().CreateAudioAsync($"Database wipe audio target {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
            var retainedTextUntilWipe = await AsUser().CreateTextAsync($"Database wipe text target {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
            var beforeForbiddenBackups = EnumerateBackups(backupDirectory);

            var forbidden = () => AsUser(ApiTestUsers.Eva).WipeDatabaseAsync();
            await forbidden.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*returned 403 (Forbidden)*");
            EnumerateBackups(backupDirectory).Should().Equal(beforeForbiddenBackups);
            (await File.ReadAllBytesAsync(configPath, TestContext.Current.CancellationToken)).Should().Equal(markerConfig);
            (await AsUser().GetVideoByIdAsync(retainedUntilWipe.Id, TestContext.Current.CancellationToken)).Title
                .Should().Be(retainedUntilWipe.Title);
            (await AsUser().GetAudioByIdAsync(retainedAudioUntilWipe.Id, TestContext.Current.CancellationToken)).Title
                .Should().Be(retainedAudioUntilWipe.Title);
            (await AsUser().GetTextByIdAsync(retainedTextUntilWipe.Id, TestContext.Current.CancellationToken)).Title
                .Should().Be(retainedTextUntilWipe.Title);

            var wipeStartedAt = DateTime.UtcNow.AddSeconds(-1);
            RetireApiInstanceAfterClass();
            var wiped = await AsUser().WipeDatabaseAsync(TestContext.Current.CancellationToken);
            wiped.Message.Should().Be("Database and config wiped successfully");
            wiped.BackupPath.Should().NotBeNullOrWhiteSpace();
            wiped.ConfigBackupPath.Should().NotBeNullOrWhiteSpace();
            DateTime.TryParseExact(
                wiped.Timestamp,
                "yyyyMMdd_HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var backupTimestamp).Should().BeTrue();
            backupTimestamp.Should().BeOnOrAfter(wipeStartedAt)
                .And.BeOnOrBefore(DateTime.UtcNow.AddSeconds(1));
            File.Exists(wiped.BackupPath).Should().BeTrue();
            new FileInfo(wiped.BackupPath).Length.Should().BeGreaterThan(0);
            File.Exists(wiped.ConfigBackupPath).Should().BeTrue();
            (await File.ReadAllBytesAsync(wiped.ConfigBackupPath!, TestContext.Current.CancellationToken)).Should().Equal(markerConfig);
            Path.GetDirectoryName(wiped.BackupPath).Should().Be(backupDirectory);
            Path.GetDirectoryName(wiped.ConfigBackupPath).Should().Be(backupDirectory);
            Path.GetFileName(wiped.BackupPath).Should().EndWith("_pre_wipe.sql");
            Path.GetFileName(wiped.ConfigBackupPath).Should().EndWith("_pre_wipe.json");
            File.Exists(configPath).Should().BeFalse();

            var newBackups = EnumerateBackups(backupDirectory)
                .Except(beforeForbiddenBackups, StringComparer.Ordinal)
                .ToArray();
            newBackups.Should().BeEquivalentTo(wiped.BackupPath, wiped.ConfigBackupPath!);
            (await AsUser().GetVideosAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
            var removed = () => AsUser().GetVideoByIdAsync(retainedUntilWipe.Id);
            await removed.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*returned 404 (NotFound)*");
            var removedAudio = () => AsUser().GetAudioByIdAsync(retainedAudioUntilWipe.Id);
            var removedText = () => AsUser().GetTextByIdAsync(retainedTextUntilWipe.Id);
            using (new AssertionScope())
            {
                await removedAudio.Should().ThrowAsync<InvalidOperationException>()
                    .WithMessage("*returned 404 (NotFound)*");
                await removedText.Should().ThrowAsync<InvalidOperationException>()
                    .WithMessage("*returned 404 (NotFound)*");
            }
            (await AsUser().GetCurrentUserAsync(TestContext.Current.CancellationToken)).GetProperty("user")
                .GetProperty("username").GetString().Should().Be(ApiTestUsers.Owner);
        }
        finally
        {
            if (configExisted)
                await File.WriteAllBytesAsync(configPath, priorConfig!, CancellationToken.None);
            else if (File.Exists(configPath))
                File.Delete(configPath);
        }
    }

    private static string[] EnumerateBackups(string directory)
        => Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal).ToArray()
            : [];
}
