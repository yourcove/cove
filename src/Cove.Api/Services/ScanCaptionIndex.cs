using Cove.Core.Common;
using Cove.Data;
using Npgsql;
using NpgsqlTypes;

namespace Cove.Api.Services;

/// <summary>Enumerates each caption directory once and seeks matching filename prefixes in PostgreSQL.</summary>
internal sealed class ScanCaptionIndex : IDisposable
{
    private readonly NpgsqlConnection? connection;
    private readonly NpgsqlTransaction? transaction;
    private readonly Dictionary<string, List<string>>? memoryDirectories;
    private readonly object gate = new();
    private readonly CancellationToken ct;

    internal ScanCaptionIndex(CoveContext db, CancellationToken ct = default)
    {
        this.ct = ct;
        connection = ScanPostgresStage.TryOpen(db);
        if (connection == null)
        {
            memoryDirectories = new Dictionary<string, List<string>>(FilesystemPaths.PathComparer);
            return;
        }

        try
        {
            transaction = connection.BeginTransaction();
            using var command = new NpgsqlCommand("""
                CREATE TEMP TABLE cove_scan_caption_directories(hash integer NOT NULL,path text NOT NULL) ON COMMIT DROP;
                CREATE INDEX ON cove_scan_caption_directories(hash);
                CREATE TEMP TABLE cove_scan_captions(
                    directory_hash integer NOT NULL,
                    directory text NOT NULL,
                    name_key bytea NOT NULL,
                    path text NOT NULL
                ) ON COMMIT DROP;
                CREATE INDEX ON cove_scan_captions(directory_hash,name_key);
                """, connection, transaction);
            command.ExecuteNonQuery();
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal IReadOnlyList<string> Find(string videoPath)
    {
        ct.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(videoPath);
        if (directory == null || !Directory.Exists(directory)) return [];
        var prefix = Path.Combine(directory, Path.GetFileNameWithoutExtension(videoPath));
        lock (gate)
        {
            EnsureDirectoryLoaded(directory);
            if (memoryDirectories != null)
                return memoryDirectories[directory]
                    .Where(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.OrdinalIgnoreCase).ToList();

            var matches = new List<string>();
            var prefixKey = ScanSortKey.OrdinalIgnoreCase(Path.GetFileNameWithoutExtension(videoPath));
            using var command = new NpgsqlCommand(prefixKey.Length == 0
                ? "SELECT directory,path FROM pg_temp.cove_scan_captions WHERE directory_hash=$1"
                : "SELECT directory,path FROM pg_temp.cove_scan_captions WHERE directory_hash=$1 AND name_key >= $2 AND name_key < $3",
                connection, transaction);
            command.Parameters.AddWithValue(FilesystemPaths.PathComparer.GetHashCode(directory));
            if (prefixKey.Length > 0)
            {
                command.Parameters.AddWithValue(NpgsqlDbType.Bytea, prefixKey);
                command.Parameters.AddWithValue(NpgsqlDbType.Bytea, ScanSortKey.PrefixUpperBound(prefixKey));
            }
            using var registration = ct.Register(command.Cancel);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (!FilesystemPaths.PathComparer.Equals(reader.GetString(0), directory)) continue;
                var file = reader.GetString(1);
                if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                matches.Add(file);
            }
            return matches.Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private void EnsureDirectoryLoaded(string directory)
    {
        if (memoryDirectories != null)
        {
            if (memoryDirectories.ContainsKey(directory)) return;
            memoryDirectories[directory] = EnumerateCaptions(directory);
            return;
        }

        var directoryHash = FilesystemPaths.PathComparer.GetHashCode(directory);
        using (var lookup = new NpgsqlCommand(
            "SELECT path FROM pg_temp.cove_scan_caption_directories WHERE hash=$1", connection, transaction))
        {
            lookup.Parameters.AddWithValue(directoryHash);
            using var reader = lookup.ExecuteReader();
            while (reader.Read())
                if (FilesystemPaths.PathComparer.Equals(reader.GetString(0), directory)) return;
        }

        transaction!.Save("directory_load");
        try
        {
            using var insert = new NpgsqlCommand("""
                INSERT INTO pg_temp.cove_scan_captions(directory_hash,directory,name_key,path)
                VALUES($1,$2,$3,$4)
                """, connection, transaction);
            insert.Parameters.AddWithValue(directoryHash);
            insert.Parameters.AddWithValue(directory);
            var nameParameter = insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea });
            var pathParameter = insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text });
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (!IsCaption(file)) continue;
                nameParameter.Value = ScanSortKey.OrdinalIgnoreCase(Path.GetFileName(file));
                pathParameter.Value = file;
                insert.ExecuteNonQuery();
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            transaction.Rollback("directory_load");
        }
        transaction.Release("directory_load");
        using var loaded = new NpgsqlCommand(
            "INSERT INTO pg_temp.cove_scan_caption_directories(hash,path) VALUES($1,$2)", connection, transaction);
        loaded.Parameters.AddWithValue(directoryHash);
        loaded.Parameters.AddWithValue(directory);
        loaded.ExecuteNonQuery();
    }

    private List<string> EnumerateCaptions(string directory)
    {
        try
        {
            var captions = new List<string>();
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (IsCaption(file)) captions.Add(file);
            }
            return captions;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static bool IsCaption(string file)
        => file.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".srt", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        lock (gate)
        {
            try { transaction?.Dispose(); }
            finally { connection?.Dispose(); }
        }
    }
}
