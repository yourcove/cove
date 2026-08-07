using System.Collections.Concurrent;
using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cove.Api.Services;

/// <summary>
/// Resolves and creates persisted folder rows for discovered filesystem directories.
/// </summary>
internal sealed class ScanFolderResolver(ILogger logger)
{
    // Striped locks serialize creation of the same folder without retaining one lock per scanned path.
    private static readonly SemaphoreSlim[] FolderCreationLocks =
        Enumerable.Range(0, 256).Select(static _ => new SemaphoreSlim(1, 1)).ToArray();

    private static SemaphoreSlim GetFolderCreationLock(string dirPath)
        => FolderCreationLocks[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(dirPath) % (uint)FolderCreationLocks.Length];

    public async Task<ConcurrentDictionary<string, int>> ResolveAsync(
        CoveContext db,
        IReadOnlyCollection<DiscoveredFile> files,
        CancellationToken ct)
    {
        // Use the host filesystem's case sensitivity so two folders differing only by case (distinct on
        // Linux, e.g. .../Weibtm and .../weibtm) get separate folder ids instead of being collapsed —
        // which would make their identically-named files collide on the unique (ParentFolderId, Basename) index.
        var folderIdsByPath = new ConcurrentDictionary<string, int>(FilesystemPaths.PathComparer);

        var directories = files
            .Select(file => ScanPath.NormalizeStoredFolderPath(Path.GetDirectoryName(file.Path) ?? file.Path))
            .Distinct(FilesystemPaths.PathComparer)
            .ToList();

        if (directories.Count == 0)
            return folderIdsByPath;

        // Load all already-known folders in bulk.
        foreach (var chunk in directories.Chunk(1000))
        {
            var rows = await db.Folders
                .AsNoTracking()
                .Where(folder => chunk.Contains(folder.Path))
                .Select(folder => new { folder.Path, folder.Id })
                .ToListAsync(ct);

            foreach (var row in rows)
                folderIdsByPath[row.Path] = row.Id;
        }

        // Create any folders that don't exist yet. Shallowest paths first so a child can pick up its
        // parent's id from the map without an extra query.
        var missing = directories
            .Where(dir => !folderIdsByPath.ContainsKey(dir))
            .OrderBy(dir => dir.Count(c => c == '/'))
            .ThenBy(dir => dir, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A discovered directory that didn't match an existing folder by exact stored path may still be
        // the SAME physical directory as a folder already in the DB that was stored under a differently
        // normalized path — most commonly a Stash-migrated folder, whose path was stored with only
        // backslash->slash conversion rather than the scanner's full canonicalization (Path.GetFullPath +
        // trailing-slash trim). Reuse it by matching on canonicalized path; otherwise the scan creates a
        // duplicate folder and therefore a duplicate entry for every file under it. Canonical full-path
        // equality means the same directory, so this can never merge genuinely distinct folders.
        if (missing.Count > 0)
        {
            var canonicalFolderIds = new Dictionary<string, int>(FilesystemPaths.PathComparer);
            var candidateFolders = await db.Folders
                .AsNoTracking()
                .Select(folder => new { folder.Id, folder.Path })
                .ToListAsync(ct);
            foreach (var candidate in candidateFolders)
            {
                var canonical = ScanPath.TryCanonicalizeStoredFolderPath(candidate.Path);
                if (canonical != null)
                    canonicalFolderIds.TryAdd(canonical, candidate.Id);
            }

            var reusedByCanonicalPath = 0;
            foreach (var dir in missing)
            {
                if (!folderIdsByPath.ContainsKey(dir) && canonicalFolderIds.TryGetValue(dir, out var existingId))
                {
                    folderIdsByPath[dir] = existingId;
                    reusedByCanonicalPath++;
                }
            }

            if (reusedByCanonicalPath > 0)
                logger.LogInformation(
                    "Scan reused {Count} existing folder(s) matched by canonicalized path (differently-normalized stored paths, e.g. Stash-migrated) to avoid duplicate folders.",
                    reusedByCanonicalPath);

            missing = missing.Where(dir => !folderIdsByPath.ContainsKey(dir)).ToList();
        }

        foreach (var dir in missing)
        {
            if (folderIdsByPath.ContainsKey(dir))
                continue;

            var existing = await db.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Path == dir, ct);
            if (existing != null)
            {
                folderIdsByPath[dir] = existing.Id;
                continue;
            }

            var folder = new Folder
            {
                Path = dir,
                ModTime = TryGetDirectoryModTime(dir),
            };

            var parentDir = ScanPath.GetParentStoredFolderPath(dir);
            if (!string.IsNullOrEmpty(parentDir) && parentDir != dir)
            {
                if (folderIdsByPath.TryGetValue(parentDir, out var parentId))
                    folder.ParentFolderId = parentId;
                else
                {
                    var parent = await db.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Path == parentDir, ct);
                    if (parent != null)
                        folder.ParentFolderId = parent.Id;
                }
            }

            db.Folders.Add(folder);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Lost a race (or unique-constraint hit): fall back to the persisted row.
                db.Entry(folder).State = EntityState.Detached;
                var raced = await db.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Path == dir, ct);
                if (raced == null)
                    throw;
                folderIdsByPath[dir] = raced.Id;
                continue;
            }

            folderIdsByPath[dir] = folder.Id;
            db.Entry(folder).State = EntityState.Detached;
        }

        return folderIdsByPath;
    }

    private static DateTime TryGetDirectoryModTime(string dirPath)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(dirPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            return DateTime.UtcNow;
        }
    }

    public async Task<Folder> EnsureAsync(CoveContext db, string dirPath, CancellationToken ct, Dictionary<string, Folder>? folderCache = null)
    {
        dirPath = ScanPath.NormalizeStoredFolderPath(dirPath);
        if (folderCache != null && folderCache.TryGetValue(dirPath, out var cachedFolder))
            return cachedFolder;

        var folder = await db.Folders.FirstOrDefaultAsync(f => f.Path == dirPath, ct);
        if (folder != null)
        {
            folderCache?.TryAdd(dirPath, folder);
            return folder;
        }

        var folderLock = GetFolderCreationLock(dirPath);
        await folderLock.WaitAsync(ct);
        try
        {
            folder = await db.Folders.FirstOrDefaultAsync(f => f.Path == dirPath, ct);
            if (folder != null)
            {
                folderCache?.TryAdd(dirPath, folder);
                return folder;
            }

            folder = new Folder
            {
                Path = dirPath,
                ModTime = Directory.GetLastWriteTimeUtc(dirPath)
            };

            // Link parent folder if path has a parent
            var parentDir = ScanPath.GetParentStoredFolderPath(dirPath);
            if (!string.IsNullOrEmpty(parentDir) && parentDir != dirPath)
            {
                var parentFolder = await db.Folders.FirstOrDefaultAsync(f => f.Path == parentDir, ct);
                if (parentFolder != null)
                    folder.ParentFolderId = parentFolder.Id;
            }

            db.Folders.Add(folder);
            try
            {
                await db.SaveChangesAsync(ct);
                folderCache?.TryAdd(dirPath, folder);
                return folder;
            }
            catch (DbUpdateException)
            {
                db.Entry(folder).State = EntityState.Detached;
                var existing = await db.Folders.FirstOrDefaultAsync(f => f.Path == dirPath, ct);
                if (existing != null)
                {
                    folderCache?.TryAdd(dirPath, existing);
                    return existing;
                }

                throw;
            }
        }
        finally
        {
            folderLock.Release();
        }
    }
}
