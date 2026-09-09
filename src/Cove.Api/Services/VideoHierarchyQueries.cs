using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cove.Api.Services;

internal static class VideoHierarchyQueries
{
    // Keep each IN predicate comfortably below provider parameter limits while still avoiding one
    // query per parent for unusually broad clip hierarchies.
    private const int ParentBatchSize = 500;

    public static async Task<int[]> ExpandDeletionScopeAsync(
        CoveContext db,
        IEnumerable<int> rootVideoIds,
        CancellationToken ct)
    {
        var roots = rootVideoIds.Where(id => id > 0).Distinct().ToArray();
        if (roots.Length == 0)
            return [];

        var result = new List<int>(roots);
        var visited = roots.ToHashSet();
        var frontier = roots;
        while (frontier.Length > 0)
        {
            var next = new List<int>();
            foreach (var parentIds in frontier.Chunk(ParentBatchSize))
            {
                var childIds = await db.Videos
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(video => video.ParentVideoId.HasValue && parentIds.Contains(video.ParentVideoId.Value))
                    .Select(video => video.Id)
                    .ToArrayAsync(ct);
                foreach (var childId in childIds)
                {
                    if (!visited.Add(childId))
                        continue;
                    result.Add(childId);
                    next.Add(childId);
                }
            }

            frontier = [.. next];
        }

        return [.. result];
    }

    /// <summary>
    /// Expands a deletion scope while locking each discovered PostgreSQL video row before its
    /// children are queried. The parent-row locks prevent a new child from being attached after
    /// the caller authorizes the resulting scope but before the parent is deleted.
    /// </summary>
    public static async Task<int[]> ExpandAndLockDeletionScopeAsync(
        CoveContext db,
        IEnumerable<int> rootVideoIds,
        CancellationToken ct)
    {
        var roots = rootVideoIds.Where(id => id > 0).Distinct().Order().ToArray();
        if (roots.Length == 0)
            return [];

        if (!string.Equals(
                db.Database.ProviderName,
                "Npgsql.EntityFrameworkCore.PostgreSQL",
                StringComparison.Ordinal))
            return await ExpandDeletionScopeAsync(db, roots, ct);

        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Locking a video deletion scope requires an active transaction.");

        var result = new List<int>();
        var visited = new HashSet<int>();
        var frontier = roots;
        while (frontier.Length > 0)
        {
            var locked = new List<int>();
            foreach (var batch in frontier.Order().Chunk(ParentBatchSize))
                locked.AddRange(await LockVideoRowsAsync(db, batch, ct));

            var next = new List<int>();
            foreach (var videoId in locked)
            {
                if (visited.Add(videoId))
                    result.Add(videoId);
            }

            foreach (var parentIds in locked.Chunk(ParentBatchSize))
            {
                var childIds = await db.Videos
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(video => video.ParentVideoId.HasValue && parentIds.Contains(video.ParentVideoId.Value))
                    .Select(video => video.Id)
                    .ToArrayAsync(ct);
                foreach (var childId in childIds)
                {
                    if (!visited.Contains(childId))
                        next.Add(childId);
                }
            }

            frontier = next.Distinct().Order().ToArray();
        }

        return [.. result];
    }

    private static async Task<int[]> LockVideoRowsAsync(
        CoveContext db,
        IReadOnlyCollection<int> videoIds,
        CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Locking video rows requires an active transaction.");
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();

        var placeholders = new List<string>(videoIds.Count);
        var index = 0;
        foreach (var videoId in videoIds.Order())
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"video_id_{index++}";
            parameter.Value = videoId;
            command.Parameters.Add(parameter);
            placeholders.Add($"@{parameter.ParameterName}");
        }

        command.CommandText = $"SELECT \"Id\" FROM videos WHERE \"Id\" IN ({string.Join(", ", placeholders)}) ORDER BY \"Id\" FOR UPDATE";
        var locked = new List<int>(videoIds.Count);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            locked.Add(reader.GetInt32(0));
        return [.. locked];
    }
}
