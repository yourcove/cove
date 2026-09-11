using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Cove.Api.Services;

internal interface IStashFileClaims : IAsyncDisposable
{
    Task<bool> AddAsync(string value, CancellationToken ct);
    Task SeedAsync(IEnumerable<string> values, CancellationToken ct);
}

// Managed equality preserves each importer's case rules even when hashes collide.
internal sealed class StashFileClaims : IStashFileClaims
{
    private readonly StringComparer comparer;
    private readonly NpgsqlConnection connection;
    private NpgsqlTransaction? transaction;

    private StashFileClaims(NpgsqlConnection connection, StringComparer comparer)
        => (this.connection, this.comparer) = (connection, comparer);

    internal static async Task<IStashFileClaims> CreateAsync(CoveContext db, StringComparer comparer, CancellationToken ct)
    {
        var source = (NpgsqlConnection)db.Database.GetDbConnection();
        // Claims must survive destination SaveChanges rollback/retry. A dedicated, unpooled
        // session also guarantees cleanup if creation, cancellation or disposal fails.
        var settings = new NpgsqlConnectionStringBuilder(source.ConnectionString) { Pooling = false, Enlist = false, Multiplexing = false };
        var claims = new StashFileClaims(source.CloneWith(settings.ConnectionString), comparer);
        try
        {
            await claims.connection.OpenAsync(ct);
            claims.transaction = await claims.connection.BeginTransactionAsync(ct);
            await using var command = new NpgsqlCommand("""
                CREATE TEMP TABLE cove_stash_file_claims(hash integer NOT NULL, value text NOT NULL) ON COMMIT DROP;
                CREATE INDEX ON cove_stash_file_claims(hash);
                """, claims.connection, claims.transaction);
            await command.ExecuteNonQueryAsync(ct);
            return claims;
        }
        catch
        {
            await claims.DisposeAsync();
            throw;
        }
    }

    public async Task SeedAsync(IEnumerable<string> values, CancellationToken ct)
    {
        await using var writer = await connection.BeginBinaryImportAsync(
            "COPY cove_stash_file_claims(hash,value) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var value in values)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(comparer.GetHashCode(value), NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(value, NpgsqlDbType.Text, ct);
        }
        await writer.CompleteAsync(ct);
    }

    public async Task<bool> AddAsync(string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var command = new NpgsqlCommand(
            "SELECT value FROM cove_stash_file_claims WHERE hash=$1", connection, transaction);
        command.Parameters.AddWithValue(comparer.GetHashCode(value));
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                if (comparer.Equals(value, reader.GetString(0))) return false;
        command.CommandText = "INSERT INTO cove_stash_file_claims(hash,value) VALUES($1,$2)";
        command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync(ct);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (transaction != null) await transaction.DisposeAsync();
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }
}
