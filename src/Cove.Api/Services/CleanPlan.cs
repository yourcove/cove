using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Cove.Api.Services;

/// <summary>Retains the inspect-before-delete boundary without keeping every candidate ID in RAM.</summary>
internal sealed class CleanPlan : IAsyncDisposable
{
    internal const int BatchSize = 256;
    private readonly string path;
    private SqliteConnection connection = null!;
    private CleanPlan(string directory) => path = Path.Combine(directory, $"cove-clean-{Guid.NewGuid():N}.db");

    internal static async Task<CleanPlan> CreateAsync(CancellationToken ct, string? directory = null)
    {
        var plan = new CleanPlan(directory ?? Path.GetTempPath());
        try
        {
            plan.connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = plan.path, Pooling = false }.ToString());
            await plan.connection.OpenAsync(ct);
            using var command = plan.connection.CreateCommand();
            command.CommandText = "PRAGMA cache_size=-2048; CREATE TABLE targets(kind TEXT NOT NULL,id INTEGER NOT NULL,PRIMARY KEY(kind,id))";
            await command.ExecuteNonQueryAsync(ct);
            return plan;
        }
        catch
        {
            await plan.DisposeAsync();
            throw;
        }
    }

    internal async Task AddAsync(string kind, IEnumerable<int> ids, CancellationToken ct)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO targets(kind,id) VALUES($kind,$id)";
        command.Parameters.AddWithValue("$kind", kind);
        var parameter = command.Parameters.Add("$id", SqliteType.Integer);
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            parameter.Value = id;
            await command.ExecuteNonQueryAsync(ct);
        }
        transaction.Commit();
    }

    internal async Task<int> CountAsync(string kind, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM targets WHERE kind=$kind";
        command.Parameters.AddWithValue("$kind", kind);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    internal async IAsyncEnumerable<int[]> ReadAsync(string kind, [EnumeratorCancellation] CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM targets WHERE kind=$kind ORDER BY id";
        command.Parameters.AddWithValue("$kind", kind);
        using var reader = await command.ExecuteReaderAsync(ct);
        var batch = new List<int>(BatchSize);
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            batch.Add(reader.GetInt32(0));
            if (batch.Count == BatchSize)
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
