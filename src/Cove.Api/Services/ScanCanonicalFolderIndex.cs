using System.Diagnostics;
using Cove.Core.Common;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cove.Api.Services;

/// <summary>
/// Finds stored folders whose path canonicalizes to a discovered directory even though the stored string
/// differs (e.g. Stash-migrated paths, or a case-only difference on Windows), so a scan reuses them instead
/// of creating duplicates. Only a canonical-path hash and id are held per folder, so the index costs about
/// eight bytes per folder and one streamed read. It is built on first use: most scans resolve every
/// directory by exact stored path and never need it.
/// </summary>
internal sealed class ScanCanonicalFolderIndex(ILogger logger)
{
    // (hash << 32) | id, sorted: equal hashes are contiguous and ordered by id.
    private long[]? entries;

    /// <summary>
    /// Returns the lowest-id stored folder whose canonical path equals each requested path. Paths are
    /// expected in stored (canonical) form; paths with no such folder are absent from the result.
    /// </summary>
    internal async Task<Dictionary<string, int>> FindAsync(
        CoveContext db,
        IReadOnlyCollection<string> canonicalPaths,
        CancellationToken ct)
    {
        var found = new Dictionary<string, int>(FilesystemPaths.PathComparer);
        if (canonicalPaths.Count == 0)
            return found;

        var index = entries ?? await BuildAsync(db, ct);
        var candidateIds = new HashSet<int>();
        foreach (var path in canonicalPaths)
            AddCandidates(index, FilesystemPaths.PathComparer.GetHashCode(path), candidateIds);
        if (candidateIds.Count == 0)
            return found;

        // Hashes only narrow the search; confirm each candidate against its current stored path.
        var wanted = new HashSet<string>(canonicalPaths, FilesystemPaths.PathComparer);
        foreach (var chunk in candidateIds.Order().Chunk(1000))
        {
            var rows = await db.Folders
                .AsNoTracking()
                .Where(folder => chunk.Contains(folder.Id))
                .Select(folder => new { folder.Id, folder.Path })
                .ToListAsync(ct);
            foreach (var row in rows)
            {
                var canonical = ScanPath.TryCanonicalizeStoredFolderPath(row.Path);
                if (canonical == null || !wanted.Contains(canonical))
                    continue;
                if (!found.TryGetValue(canonical, out var existingId) || row.Id < existingId)
                    found[canonical] = row.Id;
            }
        }

        return found;
    }

    private async Task<long[]> BuildAsync(CoveContext db, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var built = new List<long>();
        await foreach (var folder in db.Folders
            .AsNoTracking()
            .Select(folder => new { folder.Id, folder.Path })
            .AsAsyncEnumerable()
            .WithCancellation(ct))
        {
            var canonical = ScanPath.TryCanonicalizeStoredFolderPath(folder.Path);
            if (canonical != null)
                built.Add(Pack(FilesystemPaths.PathComparer.GetHashCode(canonical), folder.Id));
        }

        var index = built.ToArray();
        Array.Sort(index);
        entries = index;
        logger.LogInformation(
            "Indexed {FolderCount} stored folder(s) by canonical path in {ElapsedMs} ms to match differently-normalized folder paths.",
            index.Length,
            stopwatch.ElapsedMilliseconds);
        return index;
    }

    private static void AddCandidates(long[] index, int hash, HashSet<int> candidateIds)
    {
        // Ids are positive, so the packed value with id 0 sorts before every entry for this hash.
        var position = Array.BinarySearch(index, Pack(hash, 0));
        if (position < 0)
            position = ~position;
        for (; position < index.Length && (int)(index[position] >> 32) == hash; position++)
            candidateIds.Add((int)(uint)index[position]);
    }

    private static long Pack(int hash, int id) => ((long)hash << 32) | (uint)id;
}
