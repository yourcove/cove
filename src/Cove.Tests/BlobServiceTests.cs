using Cove.Api.Services;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public class BlobServiceTests
{
    [Fact]
    public async Task DeleteBlobAsync_AllowsDeletingWhileBlobIsOpenForRead()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-blob-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var config = new CoveConfiguration { GeneratedPath = tempRoot };
            var service = new BlobService(config, NullLogger<BlobService>.Instance);

            await using var upload = new MemoryStream([1, 2, 3, 4]);
            var blobId = await service.StoreBlobAsync(upload, "image/png");

            var blob = await service.GetBlobAsync(blobId);
            Assert.NotNull(blob);

            await service.DeleteBlobAsync(blobId);

            await blob!.Value.Stream.DisposeAsync();

            var deleted = await service.GetBlobAsync(blobId);
            Assert.Null(deleted);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}