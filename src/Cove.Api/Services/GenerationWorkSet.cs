using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Cove.Api.Services;

/// <summary>Disk-backed eligible video IDs keep job totals stable without retaining every graph.</summary>
internal sealed class GenerationWorkSet : IAsyncDisposable
{
    private readonly string path;
    private GenerationWorkSet(string directory) => path = Path.Combine(directory, $"cove-generate-{Guid.NewGuid():N}.db");
    private SqliteConnection connection = null!;
    internal int Count { get; private set; }

    internal static async Task<GenerationWorkSet> CreateAsync(CancellationToken ct, string? directory = null)
    {
        var result = new GenerationWorkSet(directory ?? Path.GetTempPath());
        try
        {
            result.connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = result.path, Pooling = false }.ToString());
            await result.connection.OpenAsync(ct);
            using var command = result.connection.CreateCommand();
            command.CommandText = "PRAGMA cache_size=-2048; CREATE TABLE videos(id INTEGER PRIMARY KEY)";
            await command.ExecuteNonQueryAsync(ct);
            return result;
        }
        catch
        {
            await result.DisposeAsync();
            throw;
        }
    }

    internal async Task AddAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO videos(id) VALUES($id)";
        var parameter = command.Parameters.Add("$id", SqliteType.Integer);
        var added = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            parameter.Value = id;
            added = checked(added + await command.ExecuteNonQueryAsync(ct));
        }
        transaction.Commit();
        Count = checked(Count + added);
    }

    internal async IAsyncEnumerable<int[]> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM videos ORDER BY id";
        using var reader = await command.ExecuteReaderAsync(ct);
        var batch = new List<int>(GenerationSelection.BatchSize);
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            batch.Add(reader.GetInt32(0));
            if (batch.Count == GenerationSelection.BatchSize)
            {
                yield return batch.ToArray();
                batch.Clear();
            }
        }
        if (batch.Count > 0)
            yield return batch.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (connection != null)
            await connection.DisposeAsync();
        File.Delete(path);
    }
}
