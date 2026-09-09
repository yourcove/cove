using Cove.Api.Services;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public class ScanStorageTests
{
    [Fact]
    public void RecordsPreserveFirstValueAndManagedOrdering()
    {
        using var db = CreateContext();
        using var records = new ScanDiskCollection<Record>(
            db,
            item => item.Key,
            StringComparer.OrdinalIgnoreCase,
            ScanSortKey.OrdinalIgnoreCase,
            StringComparer.OrdinalIgnoreCase);
        for (var i = 600; i >= 0; i--) records.Add(new Record($"file-{i:D4}", i));
        records.Add(new Record("FILE-0000", -1));
        records.Add(new Record("s", 601));
        records.Add(new Record("ſ", 602));
        records.Add(new Record("t", 603));
        Assert.Equal(604, records.Count);
        Assert.Equal(Enumerable.Range(0, 601), records.Take(601).Select(item => item.Value));
        Assert.True(records.TryGet(new Record("FILE-0000", 100), out var found));
        Assert.Equal(0, found!.Value);
        Assert.Equal(new[] { "s", "t", "ſ" },
            records.Where(item => item.Value is >= 601 and <= 603).Select(item => item.Key));
        Assert.All(records.Chunk(256), batch => Assert.InRange(batch.Length, 1, 256));
    }

    [Fact]
    public void DirectorySortKeysPreserveDepthThenManagedPathOrder()
    {
        var paths = new[] { "/root/z", "/root", "/root/a/nested", "/root/A" };
        var expected = paths.OrderBy(path => path.Count(character => character == '/'))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expected, paths.OrderBy(path => ScanSortKey.DirectoryDepth(path), ByteArrayComparer.Instance));
    }

    [Fact]
    public void CaptionIndexPreservesPrefixCaseRulesAndCachesEachDirectoryUntilDisposed()
    {
        using var db = CreateContext();
        var directory = Path.Combine(Path.GetTempPath(), $"scan-caption-{Guid.NewGuid():N}");
        var media = Path.Combine(directory, "media");
        Directory.CreateDirectory(media);
        try
        {
            foreach (var name in new[] { "Café.en.vtt", "CAFÉ.es.srt", "Café-extra.vtt", "Cafeteria.vtt", "Café.txt" })
                File.WriteAllText(Path.Combine(media, name), "caption");
            using (var index = new ScanCaptionIndex(db))
            {
                var expected = new[] { "Café.en.vtt", "CAFÉ.es.srt", "Café-extra.vtt" }.Order(StringComparer.OrdinalIgnoreCase);
                Assert.Equal(expected, index.Find(Path.Combine(media, "café.mp4")).Select(Path.GetFileName));
                Assert.Equal(4, index.Find(Path.Combine(media, ".mp4")).Count);
                File.WriteAllText(Path.Combine(media, "Café.fr.vtt"), "late");
                Assert.Equal(3, index.Find(Path.Combine(media, "CAFÉ.mp4")).Count);
                Assert.Empty(index.Find(Path.Combine(media, "unrelated.mp4")));
            }
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            using (var index = new ScanCaptionIndex(db, canceled.Token))
                Assert.Throws<OperationCanceledException>(() => index.Find(Path.Combine(media, "café.mp4")));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"scan-storage-{Guid.NewGuid():N}")
            .Options;
        return new CoveContext(options);
    }

    private sealed record Record(string Key, int Value);

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        internal static readonly ByteArrayComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
