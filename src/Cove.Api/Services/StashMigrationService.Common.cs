using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using Cove.Core.Helpers;

namespace Cove.Api.Services;

public partial class StashMigrationService
{
    private static string OpenReadOnly(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString();

    private static async Task<int> CountAsync(SqliteConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM \"{table}\"";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=@name";
        cmd.Parameters.AddWithValue("@name", table);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection conn, string table, string column, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM pragma_table_info('{table}') WHERE name = @column";
        cmd.Parameters.AddWithValue("@column", column);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task<Dictionary<int, List<string>>> ReadUrlsAsync(SqliteConnection conn, string table, string fkCol, CancellationToken ct)
    {
        var result = new Dictionary<int, List<string>>();
        if (!await TableExistsAsync(conn, table, ct)) return result;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT \"{fkCol}\", url FROM \"{table}\" ORDER BY \"{fkCol}\", position";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = r.GetInt32(0);
            if (!result.TryGetValue(id, out var list)) result[id] = list = [];
            list.Add(r.GetString(1));
        }
        return result;
    }

    private static async Task<Dictionary<int, List<string>>> ReadAliasesAsync(SqliteConnection conn, string table, string fkCol, CancellationToken ct)
    {
        var result = new Dictionary<int, List<string>>();
        if (!await TableExistsAsync(conn, table, ct)) return result;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT \"{fkCol}\", alias FROM \"{table}\"";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = r.GetInt32(0);
            if (!result.TryGetValue(id, out var list)) result[id] = list = [];
            list.Add(r.GetString(1));
        }
        return result;
    }

    private static async Task<Dictionary<int, List<int>>> ReadJunctionAsync(SqliteConnection conn, string table, string fkA, string fkB, CancellationToken ct)
    {
        var result = new Dictionary<int, List<int>>();
        if (!await TableExistsAsync(conn, table, ct)) return result;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT \"{fkA}\", \"{fkB}\" FROM \"{table}\"";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var a = r.GetInt32(0);
            var b = r.GetInt32(1);
            if (!result.TryGetValue(a, out var list)) result[a] = list = [];
            list.Add(b);
        }
        return result;
    }

    private static async Task<Dictionary<int, List<DateTime>>> ReadDatesAsync(SqliteConnection conn, string table, string fkCol, string dateCol, CancellationToken ct)
    {
        var result = new Dictionary<int, List<DateTime>>();
        if (!await TableExistsAsync(conn, table, ct)) return result;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT \"{fkCol}\", \"{dateCol}\" FROM \"{table}\"";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = r.GetInt32(0);
            var s = ReadStringNull(r, 1);
            if (s == null) continue;
            if (!result.TryGetValue(id, out var list)) result[id] = list = [];
            list.Add(ParseDateTime(s));
        }
        return result;
    }

    private static List<int> TopologicalSort(List<int> ids, Func<int, IEnumerable<int>> getDeps)
    {
        var result = new List<int>(ids.Count);
        var visited = new HashSet<int>(ids.Count);
        var inProgress = new HashSet<int>();
        var idSet = new HashSet<int>(ids);

        void Visit(int id)
        {
            if (visited.Contains(id) || inProgress.Contains(id)) return;
            inProgress.Add(id);
            foreach (var dep in getDeps(id))
                if (idSet.Contains(dep)) Visit(dep);
            inProgress.Remove(id);
            visited.Add(id);
            result.Add(id);
        }

        foreach (var id in ids) Visit(id);
        return result;
    }

    private static string? ReadStringNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static int? ReadIntNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    private static bool ReadBool(SqliteDataReader r, int i) => !r.IsDBNull(i) && r.GetBoolean(i);

    private static PartialDate ParsePartialDate(string? value)
        => PartialDate.TryParse(value, out var date) ? date : new PartialDate(null, DatePrecision.Day);

    private static string PartialDateSql(string dateColumn, bool hasPrecision)
        => hasPrecision
            ? $"CASE {dateColumn}_precision WHEN 2 THEN substr({dateColumn}, 1, 4) WHEN 1 THEN substr({dateColumn}, 1, 7) ELSE {dateColumn} END"
            : dateColumn;

    private static DateTime ParseDateTime(string s) =>
        DateTime.TryParse(s, null, DateTimeStyles.RoundtripKind, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : DateTime.UtcNow;

    private static DateTime? ParseDateTimeOrNull(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : ParseDateTime(s);


    private static string? ResolveImportedGalleryTitle(
        string? explicitTitle,
        int? folderId,
        int stashGalleryId,
        IReadOnlyDictionary<int, int> galleryToFile,
        IReadOnlyDictionary<int, (string Basename, int FolderId, long Size, DateTime ModTime, DateTime CreatedAt)> fileData,
        IReadOnlyDictionary<int, string> stashFolderNames)
    {
        if (!string.IsNullOrWhiteSpace(explicitTitle))
            return explicitTitle;

        if (galleryToFile.TryGetValue(stashGalleryId, out var fileId) && fileData.TryGetValue(fileId, out var file))
            return Path.GetFileNameWithoutExtension(file.Basename);

        if (folderId.HasValue && stashFolderNames.TryGetValue(folderId.Value, out var folderName) && !string.IsNullOrWhiteSpace(folderName))
            return folderName;

        return null;
    }

    private static string? GetBlobId(Dictionary<string, string> blobMap, string? checksum) =>
        checksum != null && blobMap.TryGetValue(checksum, out var id) ? id : null;

    private static string? GetFingerprintValue(List<(string Type, string Value)> fingerprints, string type) =>
        fingerprints.FirstOrDefault(fp => string.Equals(fp.Type, type, StringComparison.OrdinalIgnoreCase)).Value;

    private static string NormalizeImportedFingerprintValue(string type, object? rawValue)
    {
        var value = rawValue switch
        {
            byte[] fpBytes => Encoding.UTF8.GetString(fpBytes),
            long number when string.Equals(type, "phash", StringComparison.OrdinalIgnoreCase) => unchecked((ulong)number).ToString("x"),
            long number => number.ToString(CultureInfo.InvariantCulture),
            _ => rawValue?.ToString() ?? string.Empty,
        };

        return value.Trim();
    }

    private static string GetLastPathSegment(string path)
    {
        var normalizedPath = path.Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalizedPath))
            return path;

        var separatorIndex = normalizedPath.LastIndexOf('/');
        return separatorIndex >= 0 ? normalizedPath[(separatorIndex + 1)..] : normalizedPath;
    }

    private static string NormalizeImportedPath(string path)
        => path.Replace('\\', '/');

    private static StashImportOptions NormalizeImportOptions(StashImportOptions? options)
    {
        options ??= new StashImportOptions(null, true);
        return options with { PathMappings = NormalizeStashPathMappings(options.PathMappings) };
    }

    private static IReadOnlyList<StashPathMapping> NormalizeStashPathMappings(IReadOnlyList<StashPathMapping>? pathMappings)
    {
        if (pathMappings is null || pathMappings.Count == 0)
            return [];

        var normalized = new List<StashPathMapping>();
        foreach (var mapping in pathMappings)
        {
            var source = mapping.Source?.Trim() ?? string.Empty;
            var target = mapping.Target?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(source) && string.IsNullOrWhiteSpace(target))
                continue;
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                throw new ArgumentException("Each Stash path mapping requires both a source path and a target path.");

            source = NormalizePathMappingComponent(source);
            target = NormalizePathMappingComponent(target);
            if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                normalized.Add(new StashPathMapping(source, target));
        }

        return normalized
            .OrderByDescending(mapping => mapping.Source.Length)
            .ToArray();
    }

    private static string NormalizePathMappingComponent(string path)
    {
        var normalized = NormalizeImportedPath(path.Trim());
        return normalized == "/" ? normalized : normalized.TrimEnd('/');
    }

    private static StashConfigData ApplyStashConfigPathMappings(StashConfigData stashConfig, IReadOnlyList<StashPathMapping> pathMappings)
    {
        if (pathMappings.Count == 0)
            return stashConfig;

        return stashConfig with
        {
            Paths = stashConfig.Paths
                .Select(path => (ApplyStashPathMappings(path.Path, pathMappings) ?? path.Path, path.ExcludeImage, path.ExcludeVideo))
                .ToList(),
            GeneratedPath = ApplyStashPathMappings(stashConfig.GeneratedPath, pathMappings),
            BlobFilesPath = ApplyStashPathMappings(stashConfig.BlobFilesPath, pathMappings),
            CustomPerformerImageLocation = ApplyStashPathMappings(stashConfig.CustomPerformerImageLocation, pathMappings),
        };
    }

    private static string? ApplyStashPathMappings(string? path, IReadOnlyList<StashPathMapping> pathMappings)
    {
        if (string.IsNullOrWhiteSpace(path) || pathMappings.Count == 0)
            return path;

        var normalizedPath = NormalizePathMappingComponent(path);
        foreach (var mapping in pathMappings)
        {
            if (string.Equals(normalizedPath, mapping.Source, StringComparison.OrdinalIgnoreCase))
                return mapping.Target;

            if (mapping.Source == "/" && normalizedPath.StartsWith("/", StringComparison.Ordinal))
                return JoinMappedPath(mapping.Target, normalizedPath.TrimStart('/'));

            if (normalizedPath.StartsWith(mapping.Source + "/", StringComparison.OrdinalIgnoreCase))
                return JoinMappedPath(mapping.Target, normalizedPath[(mapping.Source.Length + 1)..]);
        }

        return NormalizeImportedPath(path);
    }

    private static string JoinMappedPath(string target, string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
            return target;

        return target == "/"
            ? $"/{suffix}"
            : $"{target.TrimEnd('/')}/{suffix}";
    }

    private static IReadOnlyList<string> GetImportedPathLookupCandidates(string path)
    {
        var normalizedPath = NormalizeImportedPath(path);
        return string.Equals(path, normalizedPath, StringComparison.OrdinalIgnoreCase)
            ? [normalizedPath]
            : [path, normalizedPath];
    }

    private static string? TrimGeneratedSuffix(string? value, string suffix)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? value[..^suffix.Length]
            : value;
    }

    private static async Task<HashSet<int>> GetLegacyAiMarkerTagIdsAsync(SqliteConnection conn, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "tags", ct))
            return [];

        var aiRootIds = new HashSet<int>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name FROM tags";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (string.Equals(r.GetString(1), "AI", StringComparison.OrdinalIgnoreCase))
                    aiRootIds.Add(r.GetInt32(0));
            }
        }

        if (aiRootIds.Count == 0 || !await TableExistsAsync(conn, "tags_relations", ct))
            return aiRootIds;

        var childMap = new Dictionary<int, List<int>>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT parent_id, child_id FROM tags_relations";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var parentId = r.GetInt32(0);
                var childId = r.GetInt32(1);
                if (!childMap.TryGetValue(parentId, out var children))
                    childMap[parentId] = children = [];
                children.Add(childId);
            }
        }

        var allAiTagIds = new HashSet<int>(aiRootIds);
        var queue = new Queue<int>(aiRootIds);
        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            foreach (var childId in childMap.GetValueOrDefault(currentId, []))
            {
                if (allAiTagIds.Add(childId))
                    queue.Enqueue(childId);
            }
        }

        return allAiTagIds;
    }

    private static void ReportPhase(IJobProgress progress, double startProgress, double endProgress, int completed, int total, string subTask)
    {
        var ratio = total <= 0 ? 1 : Math.Clamp((double)completed / total, 0, 1);
        progress.Report(startProgress + ((endProgress - startProgress) * ratio), subTask);
    }

    private static void TrimImportResultsLocked()
    {
        while (importResultOrder.Count > 20)
        {
            var oldestJobId = importResultOrder.Dequeue();
            importResults.Remove(oldestJobId);
        }
    }

    private sealed class NullJobProgress : IJobProgress
    {
        public static readonly NullJobProgress Instance = new();

        public void Report(double progress, string? subTask = null)
        {
        }
    }

    private static GenderEnum? ParseGender(string? s) => s?.ToUpperInvariant() switch
    {
        "MALE" => GenderEnum.Male,
        "FEMALE" => GenderEnum.Female,
        "TRANSGENDER_MALE" => GenderEnum.TransgenderMale,
        "TRANSGENDER_FEMALE" => GenderEnum.TransgenderFemale,
        "INTERSEX" => GenderEnum.Intersex,
        "NON_BINARY" => GenderEnum.NonBinary,
        _ => null,
    };

    private static CircumcisedEnum? ParseCircumcised(string? s) => s?.ToUpperInvariant() switch
    {
        "CUT" => CircumcisedEnum.Cut,
        "UNCUT" => CircumcisedEnum.Uncut,
        _ => null,
    };

    private static (DateOnly? start, DateOnly? end) ParseCareerLength(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return (null, null);
        var parts = s.Split('-', StringSplitOptions.TrimEntries);
        DateOnly? start = null;
        DateOnly? end = null;
        if (parts.Length >= 1 && int.TryParse(parts[0].Trim(), out var sy) && sy > 1900 && sy < 2100)
            start = new DateOnly(sy, 1, 1);
        if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[1]) && int.TryParse(parts[1].Trim(), out var ey) && ey > 1900 && ey < 2100)
            end = new DateOnly(ey, 12, 31);
        return (start, end);
    }
}
