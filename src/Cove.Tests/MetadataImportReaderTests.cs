using System.Text;
using System.Text.Json;
using Cove.Api.Services;

namespace Cove.Tests;

public sealed class MetadataImportReaderTests
{
    [Theory]
    [InlineData(false, false, 1024)]
    [InlineData(false, true, 1024)]
    [InlineData(true, false, 1024)]
    [InlineData(false, false, 16384)]
    [InlineData(false, true, 16384)]
    [InlineData(true, false, 16384)]
    public async Task LargeValuesRequireLinearParsingWork(bool unused, bool nested, int readSize)
    {
        var value = nested
            ? "[" + string.Join(',', Enumerable.Repeat("{\"x\":1}", 400_000)) + "]"
            : JsonSerializer.Serialize(new string('x', 3_200_000) + " café 😀");
        var entity = "{\"name\":\"large\",\"payload\":" + value + "}";
        var json = unused
            ? "{\"unused\":" + value + ",\"tags\":[{\"name\":\"after\"}]}"
            : "{\"tags\":[" + entity + ",{\"name\":\"after\"}]}";
        var bytes = Encoding.UTF8.GetBytes(json);
        using var stream = new LimitedReadStream(bytes, readSize);
        var reader = new MetadataImportReader();
        var names = new List<string>();
        await reader.ReadAsync(stream, (_, _) => Task.CompletedTask,
            (_, item, _) =>
            {
                names.Add(item.GetProperty("name").GetString()!);
                if (names[^1] == "large")
                    Assert.Equal(entity, item.GetRawText());
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        Assert.Equal(unused ? ["after"] : new[] { "large", "after" }, names);
        Assert.InRange(reader.ParseInputBytes, bytes.Length, 8L * bytes.Length);
    }

    [Fact]
    public async Task LargeEntityFollowedBySmallEntitiesRequiresLinearParsingAndCopyingWork()
    {
        const int smallCount = 200_000;
        var bytes = Encoding.UTF8.GetBytes("{\"tags\":[{\"name\":\"" + new string('x', 2_200_000)
            + "\"}," + string.Join(',', Enumerable.Repeat("{\"name\":\"small\"}", smallCount)) + "]}");
        using var stream = new LimitedReadStream(bytes, 1024);
        var reader = new MetadataImportReader();
        var count = 0;
        await reader.ReadAsync(stream, (_, _) => Task.CompletedTask,
            (_, item, _) =>
            {
                var name = item.GetProperty("name").GetString();
                if (count++ == 0)
                    Assert.Equal(2_200_000, name!.Length);
                else
                    Assert.Equal("small", name);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        Assert.Equal(smallCount + 1, count);
        Assert.InRange(reader.ParseInputBytes, bytes.Length, 8L * bytes.Length);
        Assert.InRange(reader.BufferCopyBytes, 0, 4L * bytes.Length);
    }

    [Fact]
    public async Task RejectsTruncatedLargeEntity()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"tags\":[{\"name\":\"" + new string('x', 200_000)));
        await Assert.ThrowsAnyAsync<JsonException>(() => new MetadataImportReader().ReadAsync(
            stream, (_, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HonorsCancellationWhileRefillingLargeEntity()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new LimitedReadStream(Encoding.UTF8.GetBytes(
            "{\"tags\":[{\"name\":\"" + new string('x', 200_000) + "\"}]}"), 1024,
            () => cancellation.Cancel());
        var callbacks = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MetadataImportReader().ReadAsync(
            stream, (_, _) => Task.CompletedTask,
            (_, _, _) => { callbacks++; return Task.CompletedTask; }, cancellation.Token));
        Assert.Equal(0, callbacks);
    }

    private sealed class LimitedReadStream(byte[] bytes, int readSize, Action? cancel = null) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= 40_000)
                cancel?.Invoke();
            return base.ReadAsync(buffer[..Math.Min(readSize, buffer.Length)], cancellationToken);
        }
    }

    [Fact]
    public async Task ReadsEntitiesAcrossBuffersAndSkipsUnusedSections()
    {
        var name = new string('x', 150_000);
        var json = "{\"videos\":[" + string.Join(',', Enumerable.Repeat("{\"unused\":true}", 100_000))
            + "],\"tags\":[{\"name\":\"" + name + "\"}],\"studios\":null,\"groups\":[{\"name\":\"group\"}]}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var sections = new List<string>();
        var entities = new List<(string Section, string Name)>();
        await new MetadataImportReader().ReadAsync(stream,
            (section, _) => { sections.Add(section); return Task.CompletedTask; },
            (section, entity, _) =>
            {
                entities.Add((section, entity.GetProperty("name").GetString()!));
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        Assert.Equal(["tags", "studios", "groups"], sections);
        Assert.Equal(new[] { ("tags", name), ("groups", "group") }, entities);
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    [InlineData("utf-32")]
    [InlineData("utf-32BE")]
    public async Task AcceptsBomEncodedFilesWithShortReads(string encodingName)
    {
        var encoding = Encoding.GetEncoding(encodingName);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("{\"tags\":[{\"name\":\"café 😀\"}]}" )).ToArray();
        using var stream = new ShortReadStream(bytes);
        string? name = null;
        await new MetadataImportReader().ReadAsync(stream, (_, _) => Task.CompletedTask,
            (_, entity, _) => { name = entity.GetProperty("name").GetString(); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);
        Assert.Equal("café 😀", name);
    }

    private sealed class ShortReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    [Theory]
    [InlineData("{\"tags\":[{}]} trailing")]
    [InlineData("{\"tags\":[{}],\"unused\":[")]
    [InlineData("{\"tags\":{}}")]
    [InlineData("[]")]
    [InlineData("")]
    public async Task RejectsMalformedOrInvalidDocuments(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await Assert.ThrowsAnyAsync<JsonException>(() => new MetadataImportReader().ReadAsync(
            stream, (_, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HonorsCancellationBetweenEntities()
    {
        using var stream = new MemoryStream("{\"tags\":[{},{}]}"u8.ToArray());
        using var cancellation = new CancellationTokenSource();
        var count = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MetadataImportReader().ReadAsync(
            stream, (_, _) => Task.CompletedTask,
            (_, _, _) => { count++; cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token));
        Assert.Equal(1, count);
    }
}
