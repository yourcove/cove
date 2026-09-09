using System.Data;
using System.Reflection;
using Cove.Api.Services;
using Cove.ApiTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cove.ApiTests.Tests.Metadata;

public sealed class ScanPostgresStorageTests(MetadataImportPostgresFixture fixture)
    : IClassFixture<MetadataImportPostgresFixture>
{
    [Fact]
    public async Task RecordsUseTemporaryTablesAndPreserveManagedKeysAndOrdering()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = fixture.CreateContext();
        NpgsqlConnection connection;
        int stageProcessId;
        using (var records = new ScanDiskCollection<Record>(
            db,
            item => item.Key,
            new CollidingComparer(StringComparer.OrdinalIgnoreCase),
            ScanSortKey.OrdinalIgnoreCase,
            StringComparer.OrdinalIgnoreCase))
        {
            connection = GetConnection(records);
            stageProcessId = connection.ProcessID;
            using var timeoutCommand = new NpgsqlCommand { Connection = connection };
            Assert.Equal(300, timeoutCommand.CommandTimeout);
            for (var i = 600; i >= 0; i--) records.Add(new Record($"file-{i:D4}", i));
            records.Add(new Record("FILE-0000", -1));
            records.Add(new Record("s", 601));
            records.Add(new Record("ſ", 602));
            records.Add(new Record("t", 603));
            records.Add(new Record(CreateIncompressibleKey(), 604));

            Assert.Equal(605, records.Count);
            Assert.Equal(Enumerable.Range(0, 601), records.Take(601).Select(item => item.Value));
            Assert.True(records.TryGet(new Record("FILE-0000", 100), out var found));
            Assert.Equal(0, found!.Value);
            Assert.True(records.TryGet(new Record("s", -1), out var latinS));
            Assert.Equal(601, latinS!.Value);
            Assert.True(records.TryGet(new Record("ſ", -1), out var longS));
            Assert.Equal(602, longS!.Value);
            Assert.Equal(new[] { "s", "t", "ſ" },
                records.Where(item => item.Value is >= 601 and <= 603).Select(item => item.Key));

            using var crossThreadEnumerator = records.GetEnumerator();
            Assert.True(crossThreadEnumerator.MoveNext());
            await Task.Run(() => { while (crossThreadEnumerator.MoveNext()) { } }, ct);

            await using var command = new NpgsqlCommand(
                "SELECT relpersistence::text FROM pg_class WHERE oid = 'pg_temp.cove_scan_items'::regclass", connection);
            Assert.Equal("t", await command.ExecuteScalarAsync(ct));
        }

        Assert.Equal(ConnectionState.Closed, connection.State);
        await db.Database.OpenConnectionAsync(ct);
        await using var check = new NpgsqlCommand(
            "SELECT NOT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid=$1)",
            (NpgsqlConnection)db.Database.GetDbConnection());
        check.Parameters.AddWithValue(stageProcessId);
        Assert.True((bool)(await check.ExecuteScalarAsync(ct))!);
    }

    [Fact]
    public async Task RecordsHonorCancellationBeforeServerSort()
    {
        await using var db = fixture.CreateContext();
        using var canceled = new CancellationTokenSource();
        using var records = new ScanDiskCollection<Record>(db, item => item.Key, ct: canceled.Token);
        records.Add(new Record("value", 1));
        canceled.Cancel();

        Assert.Throws<OperationCanceledException>(() => records.ToList());
    }

    [Fact]
    public async Task CaptionIndexUsesTemporaryTablesAndPreservesPrefixRules()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = fixture.CreateContext();
        var root = Path.Combine(Path.GetTempPath(), $"scan-postgres-caption-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            foreach (var name in new[] { "Café.en.vtt", "CAFÉ.es.srt", "Café-extra.vtt", "Cafeteria.vtt", "Café.txt", "s.vtt", "ſ.vtt" })
                await File.WriteAllTextAsync(Path.Combine(root, name), "caption", ct);

            NpgsqlConnection connection;
            using (var index = new ScanCaptionIndex(db, ct))
            {
                connection = GetConnection(index);
                var expected = new[] { "Café.en.vtt", "CAFÉ.es.srt", "Café-extra.vtt" }.Order(StringComparer.OrdinalIgnoreCase);
                Assert.Equal(expected, index.Find(Path.Combine(root, "café.mp4")).Select(Path.GetFileName));
                Assert.Equal(6, index.Find(Path.Combine(root, ".mp4")).Count);
                await File.WriteAllTextAsync(Path.Combine(root, "Café.fr.vtt"), "late", ct);
                Assert.Equal(3, index.Find(Path.Combine(root, "CAFÉ.mp4")).Count);
                Assert.Equal(new[] { "s.vtt" }, index.Find(Path.Combine(root, "s.mp4")).Select(Path.GetFileName));

                await using var command = new NpgsqlCommand("""
                    SELECT count(*) FROM pg_class
                    WHERE relname IN ('cove_scan_caption_directories','cove_scan_captions') AND relpersistence = 't'
                    """, connection);
                Assert.Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync(ct)));
            }
            Assert.Equal(ConnectionState.Closed, connection.State);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static NpgsqlConnection GetConnection(object stage)
        => (NpgsqlConnection)stage.GetType().GetField("connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(stage)!;

    private static string CreateIncompressibleKey()
        => string.Concat(Enumerable.Range(0, 2_048).Select(index => (char)(0x400 + (index * 7919 % 0x3bff))));

    private sealed record Record(string Key, int Value);

    private sealed class CollidingComparer(StringComparer inner) : StringComparer
    {
        public override int Compare(string? x, string? y) => inner.Compare(x, y);
        public override bool Equals(string? x, string? y) => inner.Equals(x, y);
        public override int GetHashCode(string obj) => 1;
    }
}
