using System.Data.Common;
using Cove.Api.Services;
using Cove.Core.Entities;
using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cove.Tests;

public sealed class ScanFileIdentityServiceTests
{
    [Fact]
    public async Task ComputeOshashAsync_ReusesScanCacheWhenMoveDetectionIsDisabled()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cove-scan-hash-cache-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(path, new byte[70_000], TestContext.Current.CancellationToken);

        try
        {
            var moveIndex = new MoveDetectionIndex { Enabled = false };
            var first = await ScanFileIdentityService.ComputeOshashAsync(
                path,
                moveIndex,
                TestContext.Current.CancellationToken);
            Assert.NotNull(first);

            File.Delete(path);

            var cached = await ScanFileIdentityService.ComputeOshashAsync(
                path,
                moveIndex,
                TestContext.Current.CancellationToken);
            Assert.Equal(first, cached);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MatchExistingAsync_DoesNotQueryDatabaseWhenHashIsNotKnown()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cove-move-index-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(path, new byte[70_000], TestContext.Current.CancellationToken);

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var commands = new CommandRecorderInterceptor();
            var options = new DbContextOptionsBuilder<CoveContext>()
                .UseSqlite(connection)
                .AddInterceptors(commands)
                .Options;
            await using var db = new CoveContext(options);
            await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var info = new FileInfo(path);
            db.Videos.Add(new Video
            {
                Files =
                [
                    new VideoFile
                    {
                        Basename = "stored.mp4",
                        ParentFolder = new Folder { Path = Path.GetTempPath() },
                        Size = info.Length,
                        Fingerprints =
                        [
                            new FileFingerprint { Type = "oshash", Value = "not-a-valid-oshash" },
                        ],
                    },
                ],
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            commands.Clear();
            var moveIndex = await MoveDetectionIndex.LoadAsync(
                db,
                enabled: true,
                TestContext.Current.CancellationToken);
            Assert.True(moveIndex.Enabled);
            Assert.Equal(1, moveIndex.KnownFingerprintCount);
            Assert.Single(commands.ReaderCommands);
            commands.Clear();

            var service = new ScanFileIdentityService(null!);
            var (match, isMove) = await service.MatchExistingAsync(
                db.VideoFiles,
                path,
                folderId: 1,
                basename: info.Name,
                new FileStat(info.Length, info.LastWriteTimeUtc, info.LastWriteTimeUtc),
                moveIndex,
                TestContext.Current.CancellationToken);

            Assert.Null(match);
            Assert.False(isMove);
            Assert.Empty(commands.ReaderCommands);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class CommandRecorderInterceptor : DbCommandInterceptor
    {
        public List<string> ReaderCommands { get; } = [];

        public void Clear() => ReaderCommands.Clear();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReaderCommands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
