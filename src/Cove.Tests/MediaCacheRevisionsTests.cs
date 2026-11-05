using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.Api.Controllers;
using Cove.Api.Http;

namespace Cove.Tests;

public class MediaCacheRevisionsTests
{
    [Fact]
    public async Task ServerRevisions_MatchTheUisContract()
    {
        // The UI builds most media URLs and the server builds the rest; a family bumped on only one side
        // leaves half of its URLs under the old cache key.
        var contractPath = FindRepositoryFile("ui", "src", "api", "mediaCacheRevisions.json");
        var contract = JsonSerializer.Deserialize<Dictionary<string, int>>(
            await File.ReadAllTextAsync(contractPath, TestContext.Current.CancellationToken))
            ?? throw new InvalidOperationException("The media cache revision contract is empty.");

        Assert.Equal(contract.OrderBy(entry => entry.Key), MediaCacheRevisions.All.OrderBy(entry => entry.Key));
    }

    [Fact]
    public void ServerBuiltMediaUrls_CarryTheirFamilysRevision()
    {
        var updatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        Assert.Equal(
            $"/api/tags/7/image?max=640&v=2026-01-02T03%3A04%3A05.0000000Z&r={MediaCacheRevisions.EntityImages}",
            EntityImageUrls.Tag(7, updatedAt));
        Assert.Equal(
            $"/api/stream/video/7/screenshot?v=2026-01-02T03%3A04%3A05.0000000Z&seconds=1.5&r={MediaCacheRevisions.VideoScreenshots}",
            EntityImageUrls.VideoScreenshot(null, 7, updatedAt, 1.5));
        Assert.Equal(
            $"/api/stream/detection/7/crop?max=640&r={MediaCacheRevisions.DetectionCrops}",
            EntityImageUrls.DetectionCrop(null, 7));

        // Unversioned URLs in DTOs must match what the UI builds, or the same file is fetched twice.
        Assert.Equal($"/api/stream/image/7?r={MediaCacheRevisions.LibraryImages}", EntityImageUrls.LibraryImage(7));
        Assert.Equal(
            $"/api/stream/image/7/thumbnail?max=320&r={MediaCacheRevisions.LibraryImages}",
            EntityImageUrls.LibraryThumbnail(7, 320));
        Assert.Equal(
            $"/api/stream/video/7/screenshot?r={MediaCacheRevisions.VideoScreenshots}",
            EntityImageUrls.UnversionedVideoScreenshot(7));
        Assert.Equal($"/api/texts/7/image?r={MediaCacheRevisions.EntityImages}", EntityImageUrls.UnversionedText(7));
    }

    [Fact]
    public void ZeroRevision_AddsNoParameter()
    {
        var query = new List<KeyValuePair<string, string?>>();

        MediaCacheRevisions.Append(query, 0);

        Assert.Empty(query);
    }

    [Fact]
    public void MediaControllers_SetCacheLifetimesOnlyThroughMediaCache()
    {
        // MediaCache is the one place media lifetimes can be changed; a hand-written header would
        // silently keep its old lifetime. Extension bundles and assets are code, not media, and are
        // deliberately never cached, so ExtensionsController keeps its own headers.
        var controllers = Path.GetDirectoryName(FindRepositoryFile("src", "Cove.Api", "Controllers", "StreamController.cs"))!;
        // [ResponseCache(NoStore = true)] is allowed: it never stores anything, so it cannot leave a
        // stale copy behind (the auth endpoints use it).
        var direct = new Regex(
            @"""cache-control""|HeaderNames\.CacheControl|\.CacheControl\s*=(?!=)|\bResponseCache(Attribute)?\s*\((?![^)]*NoStore\s*=\s*true)",
            RegexOptions.IgnoreCase);

        var offenders = Directory.EnumerateFiles(controllers, "*.cs", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "ExtensionsController.cs")
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (path, line, number: index + 1))
                .Where(entry => !entry.line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                    && !entry.line.Contains("Request.Headers", StringComparison.Ordinal)
                    && direct.IsMatch(entry.line)))
            .Select(entry => $"{Path.GetFileName(entry.path)}:{entry.number}: {entry.line.Trim()}")
            .ToList();

        Assert.Empty(offenders);
    }

    private static string FindRepositoryFile(params string[] relativeSegments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. relativeSegments]);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"Could not locate repository file '{Path.Combine(relativeSegments)}'.");
    }
}
