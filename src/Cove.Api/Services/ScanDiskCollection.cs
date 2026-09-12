using System.Collections;
using System.Text.Json;
using Cove.Data;
using Npgsql;
using NpgsqlTypes;

namespace Cove.Api.Services;

/// <summary>Temporary, first-wins keyed scan records with PostgreSQL ordering and bounded reads.</summary>
internal sealed class ScanDiskCollection<T> : ICollection<T>, IReadOnlyCollection<T>, IDisposable
{
    private readonly NpgsqlConnection? connection;
    private readonly NpgsqlTransaction? transaction;
    private readonly List<MemoryItem>? memoryItems;
    private readonly Func<T, string> key;
    private readonly StringComparer comparer;
    private readonly StringComparer orderComparer;
    private readonly Func<string, byte[]> orderKey;
    private readonly CancellationToken ct;
    private readonly object gate = new();
    private long nextSequence;
    private int count;

    public int Count { get { lock (gate) return count; } }
    public bool IsReadOnly => false;

    internal ScanDiskCollection(
        CoveContext db,
        Func<T, string> key,
        StringComparer? comparer = null,
        Func<string, byte[]>? orderKey = null,
        StringComparer? orderComparer = null,
        CancellationToken ct = default)
    {
        this.key = key;
        this.comparer = comparer ?? StringComparer.Ordinal;
        this.orderKey = orderKey ?? ScanSortKey.Ordinal;
        this.orderComparer = orderComparer ?? StringComparer.Ordinal;
        this.ct = ct;
        connection = ScanPostgresStage.TryOpen(db);
        if (connection == null)
        {
            // Cove runs on PostgreSQL. This serialized fallback keeps provider-independent unit tests
            // from retaining the candidate objects whose lifetime those tests verify.
            memoryItems = [];
            return;
        }

        try
        {
            transaction = connection.BeginTransaction();
            using var command = new NpgsqlCommand("""
                CREATE TEMP TABLE cove_scan_items (
                    sequence bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    hash integer NOT NULL,
                    key text NOT NULL,
                    sort_key bytea NOT NULL,
                    value text NOT NULL
                ) ON COMMIT DROP;
                CREATE INDEX ON cove_scan_items(hash);
                """, connection, transaction);
            command.ExecuteNonQuery();
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Add(T item) => TryAdd(item);

    internal bool TryAdd(T item)
    {
        var itemKey = key(item);
        lock (gate)
        {
            if (memoryItems != null)
            {
                if (memoryItems.Any(existing => comparer.Equals(existing.Key, itemKey))) return false;
                memoryItems.Add(new MemoryItem(++nextSequence, itemKey, JsonSerializer.Serialize(item)));
                count++;
                return true;
            }

            using (var lookup = new NpgsqlCommand(
                "SELECT key FROM pg_temp.cove_scan_items WHERE hash=$1", connection, transaction))
            {
                lookup.Parameters.AddWithValue(comparer.GetHashCode(itemKey));
                using var reader = lookup.ExecuteReader();
                while (reader.Read())
                    if (comparer.Equals(reader.GetString(0), itemKey)) return false;
            }
            using var insert = new NpgsqlCommand(
                "INSERT INTO pg_temp.cove_scan_items(hash,key,sort_key,value) VALUES($1,$2,$3,$4)", connection, transaction);
            insert.Parameters.AddWithValue(comparer.GetHashCode(itemKey));
            insert.Parameters.AddWithValue(itemKey);
            insert.Parameters.AddWithValue(NpgsqlDbType.Bytea, orderKey(itemKey));
            insert.Parameters.AddWithValue(JsonSerializer.Serialize(item));
            insert.ExecuteNonQuery();
            count++;
            return true;
        }
    }

    internal bool TryGet(T lookup, out T? value)
    {
        var lookupKey = key(lookup);
        lock (gate)
        {
            if (memoryItems != null)
            {
                var item = memoryItems.FirstOrDefault(existing => comparer.Equals(existing.Key, lookupKey));
                value = item == null ? default : JsonSerializer.Deserialize<T>(item.Value);
                return item != null;
            }

            using var command = new NpgsqlCommand(
                "SELECT key,value FROM pg_temp.cove_scan_items WHERE hash=$1", connection, transaction);
            command.Parameters.AddWithValue(comparer.GetHashCode(lookupKey));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!comparer.Equals(reader.GetString(0), lookupKey)) continue;
                value = JsonSerializer.Deserialize<T>(reader.GetString(1));
                return true;
            }
            value = default;
            return false;
        }
    }

    public bool Contains(T item)
    {
        var lookupKey = key(item);
        lock (gate)
        {
            if (memoryItems != null)
                return memoryItems.Any(existing => comparer.Equals(existing.Key, lookupKey));
            using var command = new NpgsqlCommand(
                "SELECT key FROM pg_temp.cove_scan_items WHERE hash=$1", connection, transaction);
            command.Parameters.AddWithValue(comparer.GetHashCode(lookupKey));
            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (comparer.Equals(reader.GetString(0), lookupKey)) return true;
            return false;
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        if (memoryItems != null)
        {
            List<string> values;
            lock (gate)
                values = memoryItems.OrderBy(item => item.Key, Comparer<string>.Create(orderComparer.Compare))
                    .ThenBy(item => item.Sequence).Select(item => item.Value).ToList();
            foreach (var value in values) yield return JsonSerializer.Deserialize<T>(value)!;
            yield break;
        }

        var cursorDeclared = false;
        try
        {
            lock (gate)
            {
                using var declare = new NpgsqlCommand(
                    "DECLARE cove_scan_items_cursor NO SCROLL CURSOR FOR SELECT value FROM pg_temp.cove_scan_items ORDER BY sort_key,sequence",
                    connection, transaction);
                declare.ExecuteNonQueryAsync(ct).GetAwaiter().GetResult();
                cursorDeclared = true;
            }
            while (true)
            {
                List<string> values;
                lock (gate)
                {
                    using var fetch = new NpgsqlCommand("FETCH FORWARD 256 FROM cove_scan_items_cursor", connection, transaction);
                    using var reader = fetch.ExecuteReaderAsync(ct).GetAwaiter().GetResult();
                    values = [];
                    while (reader.Read()) values.Add(reader.GetString(0));
                }
                if (values.Count == 0) yield break;
                foreach (var value in values)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return JsonSerializer.Deserialize<T>(value)!;
                }
            }
        }
        finally
        {
            if (cursorDeclared)
            {
                lock (gate)
                {
                    try
                    {
                        using var close = new NpgsqlCommand("CLOSE cove_scan_items_cursor", connection, transaction);
                        close.ExecuteNonQuery();
                    }
                    catch (PostgresException) { }
                }
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void CopyTo(T[] array, int arrayIndex) { foreach (var item in this) array[arrayIndex++] = item; }
    public bool Remove(T item) => throw new NotSupportedException();

    public void Clear()
    {
        lock (gate)
        {
            if (memoryItems != null) memoryItems.Clear();
            else
            {
                using var command = new NpgsqlCommand("TRUNCATE pg_temp.cove_scan_items", connection, transaction);
                command.ExecuteNonQuery();
            }
            count = 0;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            try { transaction?.Dispose(); }
            finally { connection?.Dispose(); }
        }
    }

    private sealed record MemoryItem(long Sequence, string Key, string Value);
}
