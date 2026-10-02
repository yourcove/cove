using Cove.Api.Services;
using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public sealed class ScanCanonicalFolderIndexTests
{
    [Fact]
    public async Task FindAsync_MatchesDifferentlyNormalizedStoredPathsAndPrefersLowestId()
    {
        var root = ScanPath.NormalizeStoredFolderPath(Path.Combine(Path.GetTempPath(), $"canonical-index-{Guid.NewGuid():N}"));
        var target = root + "/target";
        await using var db = CreateContext();
        var dotted = new Folder { Id = 5, Path = target + "/." };
        var trailing = new Folder { Id = 9, Path = target + "/" };
        var unrelated = new Folder { Id = 3, Path = root + "/other" };
        db.Folders.AddRange(dotted, trailing, unrelated);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var index = new ScanCanonicalFolderIndex(NullLogger.Instance);
        var found = await index.FindAsync(db, [target, root + "/missing"], TestContext.Current.CancellationToken);

        Assert.Equal(5, Assert.Single(found).Value);
        Assert.Equal(5, found[target]);
    }

    [Fact]
    public async Task FindAsync_ConfirmsCandidatesAgainstCurrentStoredPath()
    {
        var root = ScanPath.NormalizeStoredFolderPath(Path.Combine(Path.GetTempPath(), $"canonical-index-{Guid.NewGuid():N}"));
        await using var db = CreateContext();
        var folder = new Folder { Id = 1, Path = root + "/before/." };
        db.Folders.Add(folder);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var index = new ScanCanonicalFolderIndex(NullLogger.Instance);
        Assert.Single(await index.FindAsync(db, [root + "/before"], TestContext.Current.CancellationToken));

        // The index is a snapshot of hashes; a row renamed after it was built must not match its old path.
        folder.Path = root + "/after";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Empty(await index.FindAsync(db, [root + "/before"], TestContext.Current.CancellationToken));
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"scan-canonical-folder-index-{Guid.NewGuid():N}")
            .Options;
        return new CoveContext(options);
    }
}
