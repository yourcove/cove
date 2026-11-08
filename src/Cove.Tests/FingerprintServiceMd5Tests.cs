using System.Security.Cryptography;
using Cove.Api.Services;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

/// <summary>
/// ComputeMd5Async reads the file in fixed-size chunks, so the hash must match a one-shot MD5 of the
/// same bytes for every length around a chunk boundary.
/// </summary>
public sealed class FingerprintServiceMd5Tests : IDisposable
{
    private const int Chunk = FingerprintService.Md5ReadChunkSize;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cove-md5-{Guid.NewGuid():N}");

    public FingerprintServiceMd5Tests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(51_200)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData(3 * Chunk + 17)]
    public async Task ComputeMd5Async_MatchesOneShotMd5OfTheFile(int length)
    {
        var ct = TestContext.Current.CancellationToken;
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        var path = Path.Combine(_root, $"{length}.bin");
        await File.WriteAllBytesAsync(path, bytes, ct);

        var md5 = await CreateService().ComputeMd5Async(path, ct);

        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(bytes)), md5);
    }

    [Fact]
    public async Task ComputeMd5Async_Cancelled_ThrowsInsteadOfSkippingTheFile()
    {
        var path = Path.Combine(_root, "cancelled.bin");
        await File.WriteAllBytesAsync(path, new byte[Chunk + 1], TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService().ComputeMd5Async(path, cts.Token));
    }

    [Fact]
    public async Task ComputeMd5Async_MissingFile_ReturnsNull()
    {
        var md5 = await CreateService().ComputeMd5Async(Path.Combine(_root, "missing.bin"), TestContext.Current.CancellationToken);

        Assert.Null(md5);
    }

    private static FingerprintService CreateService()
    {
        var config = new CoveConfiguration();
        return new FingerprintService(null!, null!, config, new FfmpegConcurrencyLimiter(config), NullLogger<FingerprintService>.Instance);
    }
}
