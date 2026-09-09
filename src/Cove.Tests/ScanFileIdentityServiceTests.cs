using System.Data.Common;
using Cove.Api.Services;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public sealed class ScanFileIdentityServiceTests
{
    public static TheoryData<BaseFileEntity> FileTypes => new()
    {
        NewFile<VideoFile>(),
        NewFile<ImageFile>(),
        NewFile<AudioFile>(),
        NewFile<TextFile>(),
    };

    [Fact]
    public async Task ComputeOshashAsync_ReusesScanCacheWhenMoveDetectionIsDisabled()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cove-scan-hash-cache-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(path, new byte[70_000], TestContext.Current.CancellationToken);

        try
        {
            var moveIndex = new MoveDetectionIndex { Enabled = false };
            var first = await ScanFileIdentityService.ComputeOshashAsync(path, moveIndex, TestContext.Current.CancellationToken);
            Assert.NotNull(first);

            File.Delete(path);

            var cached = await ScanFileIdentityService.ComputeOshashAsync(path, moveIndex, TestContext.Current.CancellationToken);
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
            var moveIndex = await MoveDetectionIndex.LoadAsync(db, enabled: true, TestContext.Current.CancellationToken);
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

    [Theory]
    [MemberData(nameof(FileTypes))]
    public async Task RefreshChangedFingerprintsAsync_InvalidatesDisabledFingerprintsForEveryFileType(BaseFileEntity file)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, new byte[131_072], TestContext.Current.CancellationToken);
            var service = new ScanFileIdentityService(new StubFingerprintService("replacement-md5"));
            var expectedOshash = await ScanFileIdentityService.ComputeOshashAsync(path, TestContext.Current.CancellationToken);
            Assert.False(string.IsNullOrWhiteSpace(expectedOshash));

            await service.RefreshChangedFingerprintsAsync(
                file,
                path,
                md5Enabled: false,
                moveIndex: null,
                TestContext.Current.CancellationToken);

            Assert.Equal(string.Empty, Fingerprint(file, "md5").Value);
            Assert.Equal(string.Empty, Fingerprint(file, "phash").Value);
            Assert.Equal(expectedOshash, Fingerprint(file, "oshash").Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [MemberData(nameof(FileTypes))]
    public async Task RefreshChangedFingerprintsAsync_ReplacesEnabledMd5AndInvalidatesPhashForEveryFileType(BaseFileEntity file)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, new byte[131_072], TestContext.Current.CancellationToken);
            var service = new ScanFileIdentityService(new StubFingerprintService("replacement-md5"));

            await service.RefreshChangedFingerprintsAsync(
                file,
                path,
                md5Enabled: true,
                moveIndex: null,
                TestContext.Current.CancellationToken);

            Assert.Equal("replacement-md5", Fingerprint(file, "md5").Value);
            Assert.Equal(string.Empty, Fingerprint(file, "phash").Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RefreshChangedFingerprintsAsync_ClearsHashesWhenRecomputationFails()
    {
        var file = NewFile<VideoFile>();
        var service = new ScanFileIdentityService(new StubFingerprintService(null));

        await service.RefreshChangedFingerprintsAsync(
            file,
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}"),
            md5Enabled: true,
            moveIndex: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, Fingerprint(file, "oshash").Value);
        Assert.Equal(string.Empty, Fingerprint(file, "md5").Value);
        Assert.Equal(string.Empty, Fingerprint(file, "phash").Value);
    }

    [Fact]
    public async Task AudioProcessor_ContentChangeUsesRequestedMd5AndInvalidatesPhashOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), $"changed-{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(path, new byte[131_072], TestContext.Current.CancellationToken);

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var db = await CreateContextAsync(connection);
            var folder = new Folder { Path = Path.GetDirectoryName(path)! };
            var file = NewFile<AudioFile>();
            file.Basename = Path.GetFileName(path);
            file.ParentFolder = folder;
            var audio = new Audio { Files = [file] };
            db.Audios.Add(audio);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var fingerprints = new StubFingerprintService("replacement-md5");
            var identity = new ScanFileIdentityService(fingerprints);
            var processor = new ScanAudioProcessor(
                new CoveConfiguration(),
                fingerprints,
                new StubMediaProbeService(),
                new ScanFolderResolver(NullLogger.Instance),
                identity,
                NullLogger.Instance);

            await processor.ProcessAsync(
                db,
                path,
                audioId: null,
                TestContext.Current.CancellationToken,
                parentFolderId: folder.Id,
                contentChanged: true,
                scanOptions: new ScanOperationOptions { GenerateMd5 = true });

            Assert.Equal("replacement-md5", Fingerprint(file, "md5").Value);
            Assert.Equal(string.Empty, Fingerprint(file, "phash").Value);
            Assert.Equal(1, fingerprints.Md5CallCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TextProcessor_ContentChangeInvalidatesDisabledFingerprints()
    {
        var path = Path.Combine(Path.GetTempPath(), $"changed-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, new string('x', 131_072), TestContext.Current.CancellationToken);

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var db = await CreateContextAsync(connection);
            var folder = new Folder { Path = Path.GetDirectoryName(path)! };
            var file = NewFile<TextFile>();
            file.Basename = Path.GetFileName(path);
            file.ParentFolder = folder;
            var document = new TextDocument { Files = [file] };
            db.TextDocuments.Add(document);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var fingerprints = new StubFingerprintService("replacement-md5");
            var identity = new ScanFileIdentityService(fingerprints);
            var processor = new ScanTextProcessor(
                new CoveConfiguration(),
                fingerprints,
                new TextExtractionService(),
                new ScanFolderResolver(NullLogger.Instance),
                identity,
                NullLogger.Instance);

            await processor.ProcessAsync(
                db,
                path,
                textDocumentId: null,
                TestContext.Current.CancellationToken,
                parentFolderId: folder.Id,
                contentChanged: true,
                scanOptions: new ScanOperationOptions());

            Assert.Equal(string.Empty, Fingerprint(file, "md5").Value);
            Assert.Equal(string.Empty, Fingerprint(file, "phash").Value);
            Assert.Equal(0, fingerprints.Md5CallCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<CoveContext> CreateContextAsync(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<CoveContext>().UseSqlite(connection).Options;
        var db = new CoveContext(options);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return db;
    }

    private static TFile NewFile<TFile>() where TFile : BaseFileEntity, new()
    {
        var file = new TFile();
        file.Fingerprints.Add(new FileFingerprint { Type = "oshash", Value = "stale-oshash" });
        file.Fingerprints.Add(new FileFingerprint { Type = "md5", Value = "stale-md5" });
        file.Fingerprints.Add(new FileFingerprint { Type = "phash", Value = "stale-phash" });
        return file;
    }

    private static FileFingerprint Fingerprint(BaseFileEntity file, string type) =>
        Assert.Single(file.Fingerprints, item => item.Type == type);

    private sealed class StubFingerprintService(string? md5) : IFingerprintService
    {
        public int Md5CallCount { get; private set; }

        public Task<string?> ComputeMd5Async(string path, CancellationToken ct = default)
        {
            Md5CallCount++;
            return Task.FromResult(md5);
        }
        public Task<string?> ComputeImagePhashAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> ComputeVideoPhashAsync(string path, double duration, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> ComputeAudioPhashAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> ComputeTextPhashAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public string StartGenerateVideoPhashes() => "noop";
        public string StartGenerateImagePhashes() => "noop";
    }

    private sealed class StubMediaProbeService : IMediaProbeService
    {
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(MediaProbeResult.Succeeded("""
                {
                  "format": { "duration": "1", "bit_rate": "128000" },
                  "streams": [{ "codec_type": "audio", "codec_name": "mp3", "sample_rate": "44100", "channels": 2 }]
                }
                """));
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
