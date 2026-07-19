using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cove.Api.Services;
using Cove.Core.Interfaces;

namespace Cove.Tests;

public class BlobServiceTests
{
    [Theory]
    [InlineData("video/webm", "video/webm")]
    [InlineData("Application/Vnd.Cove.Preview+Json; charset=utf-8", "application/vnd.cove.preview+json")]
    public async Task StoreBlobAsync_PreservesNormalizedContentType(string suppliedContentType, string expectedContentType)
    {
        await WithBlobServiceAsync(async (service, _) =>
        {
            await using var upload = new MemoryStream([1, 2, 3, 4]);
            var blobId = await service.StoreBlobAsync(upload, suppliedContentType);

            var blob = await service.GetBlobAsync(blobId);

            Assert.NotNull(blob);
            Assert.Equal(expectedContentType, blob.Value.ContentType);
            Assert.True(blob.Value.Stream.CanRead);
            Assert.True(blob.Value.Stream.CanSeek);
            await blob.Value.Stream.DisposeAsync();
        });
    }

    [Theory]
    [InlineData("application/metadata.json")]
    [InlineData("application/foo.metadata.json")]
    public async Task StoreBlobAsync_UsesCollisionProofMetadataNameForArbitraryMimeTypes(string contentType)
    {
        await WithBlobServiceAsync(async (service, tempRoot) =>
        {
            await using var upload = new MemoryStream([1, 2, 3, 4]);
            var blobId = await service.StoreBlobAsync(upload, contentType);
            var bucketDirectory = Path.Combine(tempRoot, "blobs", blobId[..2]);

            var files = Directory.EnumerateFiles(bucketDirectory).ToList();
            var blob = await service.GetBlobAsync(blobId);

            Assert.Equal(2, files.Count);
            Assert.Single(files, path => Path.GetFileName(path).StartsWith(blobId, StringComparison.Ordinal));
            Assert.Single(files, path => Path.GetFileName(path).StartsWith($".{blobId}.", StringComparison.Ordinal));
            Assert.NotNull(blob);
            Assert.Equal(contentType, blob.Value.ContentType);
            Assert.Equal([1, 2, 3, 4], await ReadAndDisposeAsync(blob.Value.Stream));
        });
    }

    [Fact]
    public async Task StoreBlobAsync_PreservesLegacyFallbackForMissingContentType()
    {
        await WithBlobServiceAsync(async (service, _) =>
        {
            await using var upload = new MemoryStream([1, 2, 3, 4]);
            var blobId = await service.StoreBlobAsync(upload, "");

            var blob = await service.GetBlobAsync(blobId);

            Assert.NotNull(blob);
            Assert.Equal("application/octet-stream", blob.Value.ContentType);
            await blob.Value.Stream.DisposeAsync();
        });
    }

    [Fact]
    public async Task GetBlobAsync_ReadsLegacyImageAndExtensionlessBlobs()
    {
        await WithBlobServiceAsync(async (service, tempRoot) =>
        {
            const string imageBlobId = "ab111111-1111-1111-1111-111111111111";
            const string extensionlessBlobId = "cd222222-2222-2222-2222-222222222222";
            var imageDirectory = Path.Combine(tempRoot, "blobs", "ab");
            var extensionlessDirectory = Path.Combine(tempRoot, "blobs", "cd");
            Directory.CreateDirectory(imageDirectory);
            Directory.CreateDirectory(extensionlessDirectory);
            await File.WriteAllBytesAsync(Path.Combine(imageDirectory, $"{imageBlobId}.png"), [1, 2]);
            await File.WriteAllBytesAsync(Path.Combine(extensionlessDirectory, extensionlessBlobId), [3, 4]);

            var image = await service.GetBlobAsync(imageBlobId);
            var extensionless = await service.GetBlobAsync(extensionlessBlobId);

            Assert.NotNull(image);
            Assert.Equal("image/png", image.Value.ContentType);
            Assert.NotNull(extensionless);
            Assert.Equal("application/octet-stream", extensionless.Value.ContentType);
            await image.Value.Stream.DisposeAsync();
            await extensionless.Value.Stream.DisposeAsync();
        });
    }

    [Fact]
    public async Task GetBlobAsync_DoesNotTreatMetadataSidecarAsPayload()
    {
        await WithBlobServiceAsync(async (service, tempRoot) =>
        {
            await using var upload = new MemoryStream([1, 2, 3, 4]);
            var blobId = await service.StoreBlobAsync(upload, "video/webm");
            var bucketDirectory = Path.Combine(tempRoot, "blobs", blobId[..2]);
            var payload = Directory.EnumerateFiles(bucketDirectory)
                .Single(path => Path.GetFileName(path).StartsWith(blobId, StringComparison.Ordinal));
            File.Delete(payload);

            var blob = await service.GetBlobAsync(blobId);

            Assert.Null(blob);
            Assert.Single(
                Directory.EnumerateFiles(bucketDirectory),
                path => Path.GetFileName(path).StartsWith($".{blobId}.", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task GetBlobAsync_FallsBackToPayloadExtensionWhenMetadataIsCorrupt()
    {
        await WithBlobServiceAsync(async (service, tempRoot) =>
        {
            await using var upload = new MemoryStream([1, 2, 3, 4]);
            var blobId = await service.StoreBlobAsync(upload, "image/png");
            var bucketDirectory = Path.Combine(tempRoot, "blobs", blobId[..2]);
            var metadata = Directory.EnumerateFiles(bucketDirectory)
                .Single(path => Path.GetFileName(path).StartsWith($".{blobId}.", StringComparison.Ordinal));
            await File.WriteAllTextAsync(metadata, "{}");

            var blob = await service.GetBlobAsync(blobId);

            Assert.NotNull(blob);
            Assert.Equal("image/png", blob.Value.ContentType);
            Assert.Equal([1, 2, 3, 4], await ReadAndDisposeAsync(blob.Value.Stream));
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("../outside")]
    [InlineData("../../outside")]
    [InlineData("01234567-89AB-CDEF-0123-456789ABCDEF")]
    public async Task GetAndDeleteBlobAsync_RejectNonCanonicalBlobIds(string blobId)
    {
        await WithBlobServiceAsync(async (service, tempRoot) =>
        {
            var sentinelPath = Path.Combine(tempRoot, "outside.png");
            await File.WriteAllBytesAsync(sentinelPath, [9, 8, 7]);

            var blob = await service.GetBlobAsync(blobId);
            await service.DeleteBlobAsync(blobId);

            Assert.Null(blob);
            Assert.Equal([9, 8, 7], await File.ReadAllBytesAsync(sentinelPath));
        });
    }

    [Fact]
    public async Task DeleteBlobAsync_AllowsDeletingWhileBlobIsOpenForRead()
    {
        await WithBlobServiceAsync(async (service, tempRoot) =>
        {
            await using var upload = new MemoryStream([1, 2, 3, 4]);
            var blobId = await service.StoreBlobAsync(upload, "video/webm");

            var blob = await service.GetBlobAsync(blobId);
            Assert.NotNull(blob);

            await service.DeleteBlobAsync(blobId);

            await blob!.Value.Stream.DisposeAsync();

            var deleted = await service.GetBlobAsync(blobId);
            Assert.Null(deleted);
            var bucketDirectory = Path.Combine(tempRoot, "blobs", blobId[..2]);
            Assert.Empty(Directory.EnumerateFiles(bucketDirectory));
        });
    }

    [Fact]
    public async Task StoreBlobAsync_CancellationRemovesPartialPayloadAndMetadata()
    {
        await WithBlobServiceAsync(async (service, tempRoot) =>
        {
            await using var upload = new MemoryStream([1, 2, 3, 4]);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.StoreBlobAsync(upload, "application/vnd.cove.preview+json", cancellation.Token));

            var blobDirectory = Path.Combine(tempRoot, "blobs");
            Assert.False(Directory.Exists(blobDirectory) && Directory.EnumerateFiles(blobDirectory, "*", SearchOption.AllDirectories).Any());
        });
    }

    [Fact]
    public async Task GetAndDeleteBlobAsync_HonorCancellation()
    {
        await WithBlobServiceAsync(async (service, _) =>
        {
            await using var upload = new MemoryStream([1, 2, 3, 4]);
            var blobId = await service.StoreBlobAsync(upload, "video/webm");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.GetBlobAsync(blobId, cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.DeleteBlobAsync(blobId, cancellation.Token));

            var blob = await service.GetBlobAsync(blobId);
            Assert.NotNull(blob);
            await blob.Value.Stream.DisposeAsync();
        });
    }

    [Fact]
    public async Task StoredBlob_CanBeServedFromMinimalApiWithRangeProcessing()
    {
        await WithBlobServiceAsync(async (service, _) =>
        {
            await using var upload = new MemoryStream([1, 2, 3, 4]);
            var blobId = await service.StoreBlobAsync(upload, "video/webm");

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IBlobService>(service);
            await using var app = builder.Build();
            app.MapGet("/blob", async (IBlobService blobs, CancellationToken ct) =>
            {
                var blob = await blobs.GetBlobAsync(blobId, ct);
                return blob == null
                    ? (IResult)Results.NotFound()
                    : Results.Stream(
                        blob.Value.Stream,
                        blob.Value.ContentType,
                        enableRangeProcessing: blob.Value.Stream.CanSeek);
            });
            await app.StartAsync();

            using var request = new HttpRequestMessage(HttpMethod.Get, "/blob");
            request.Headers.Range = new RangeHeaderValue(1, 2);
            using var response = await app.GetTestClient().SendAsync(request);

            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal("video/webm", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("bytes 1-2/4", response.Content.Headers.ContentRange?.ToString());
            Assert.Equal([2, 3], await response.Content.ReadAsByteArrayAsync());
        });
    }

    [Fact]
    public async Task ConcurrentGetAndDelete_DoNotExposeLookupOpenRace()
    {
        await WithBlobServiceAsync(async (service, _) =>
        {
            for (var iteration = 0; iteration < 25; iteration++)
            {
                await using var upload = new MemoryStream([1, 2, 3, 4]);
                var blobId = await service.StoreBlobAsync(upload, "video/webm");
                var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var readers = Enumerable.Range(0, 8).Select(async _ =>
                {
                    await start.Task;
                    var blob = await service.GetBlobAsync(blobId);
                    if (blob != null)
                    {
                        Assert.Equal("video/webm", blob.Value.ContentType);
                        Assert.Equal([1, 2, 3, 4], await ReadAndDisposeAsync(blob.Value.Stream));
                    }
                }).ToList();
                var deletion = Task.Run(async () =>
                {
                    await start.Task;
                    await service.DeleteBlobAsync(blobId);
                });

                start.SetResult(true);
                await Task.WhenAll(readers.Append(deletion));
                Assert.Null(await service.GetBlobAsync(blobId));
            }
        });
    }

    [Fact]
    public async Task ReturnedBlob_RetainsMimeAndBytesWhenCoordinatedDeleteCompletesBeforeRead()
    {
        await WithBlobServiceAsync(async (service, _) =>
        {
            await using var upload = new MemoryStream([1, 2, 3, 4]);
            var blobId = await service.StoreBlobAsync(upload, "video/webm");
            var blobReady = new TaskCompletionSource<(Stream Stream, string ContentType)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var deletionComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var reader = Task.Run(async () =>
            {
                var blob = await service.GetBlobAsync(blobId);
                Assert.NotNull(blob);
                blobReady.SetResult(blob.Value);
                await deletionComplete.Task;
                Assert.Equal("video/webm", blob.Value.ContentType);
                Assert.Equal([1, 2, 3, 4], await ReadAndDisposeAsync(blob.Value.Stream));
            });
            var deleter = Task.Run(async () =>
            {
                var blob = await blobReady.Task;
                try
                {
                    await service.DeleteBlobAsync(blobId);
                }
                finally
                {
                    deletionComplete.SetResult(true);
                }
            });

            await Task.WhenAll(reader, deleter);
        });
    }

    private static async Task<byte[]> ReadAndDisposeAsync(Stream stream)
    {
        await using (stream)
        {
            using var content = new MemoryStream();
            await stream.CopyToAsync(content);
            return content.ToArray();
        }
    }

    private static async Task WithBlobServiceAsync(Func<BlobService, string, Task> action)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-blob-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var config = new CoveConfiguration { GeneratedPath = tempRoot };
            var service = new BlobService(config, NullLogger<BlobService>.Instance);
            await action(service, tempRoot);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
