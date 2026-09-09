using System.Security.Cryptography;
using System.Text;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Cove.Data.Services;

/// <summary>
/// Reconciles expression indexes for JSON custom-field paths against the committed settings rows.
/// The implementation is independent of the job surface so a future maintenance action can reuse it.
/// </summary>
public sealed class CustomFieldJsonIndexReconciler(
    CoveConfiguration configuration,
    ILogger<CustomFieldJsonIndexReconciler> logger)
{
    public const string ManagedIndexPrefix = "ix_cfv_json_v";
    private const long AdvisoryLockKey = 0x434F56454A534F4E; // COVEJSON
    private readonly CoveConfiguration _configuration = configuration;
    private readonly ILogger<CustomFieldJsonIndexReconciler> _logger = logger;

    public async Task<CustomFieldJsonIndexReconcileResult> ReconcileAsync(
        Action<double, string?>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(_configuration.DatabaseConnectionString)
        {
            Enlist = false,
            Multiplexing = false,
            Pooling = false,
            ApplicationName = "Cove JSON custom-field index reconciler",
        }.ConnectionString;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await AcquireLockAsync(connection, cancellationToken);

        var created = 0;
        var dropped = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                reportProgress?.Invoke(0.05, "Reading configured JSON paths");
                var desired = await ReadDesiredIndexesAsync(connection, cancellationToken);
                var actual = await ReadManagedIndexesAsync(connection, cancellationToken);

                var invalid = actual.Values
                    .Where(index => !index.IsValid || !index.IsReady)
                    .OrderBy(index => index.Name, StringComparer.Ordinal)
                    .ToArray();
                foreach (var index in invalid)
                {
                    reportProgress?.Invoke(0.15, $"Removing incomplete index {index.Name}");
                    await DropIndexAsync(connection, index.Name, cancellationToken);
                    dropped++;
                }
                if (invalid.Length > 0)
                    continue;

                var missing = desired.Values
                    .Where(index => !actual.ContainsKey(index.Name))
                    .OrderBy(index => index.Name, StringComparer.Ordinal)
                    .ToArray();
                for (var index = 0; index < missing.Length; index++)
                {
                    var spec = missing[index];
                    var progress = missing.Length == 0 ? 0.5 : 0.2 + (0.5 * index / missing.Length);
                    reportProgress?.Invoke(progress, $"Creating index for {spec.Path}");
                    await CreateIndexAsync(connection, spec, cancellationToken);
                    created++;

                    var createdIndex = (await ReadManagedIndexesAsync(connection, cancellationToken))
                        .GetValueOrDefault(spec.Name);
                    if (createdIndex is not { IsValid: true, IsReady: true })
                    {
                        if (createdIndex != null)
                            await DropIndexAsync(connection, spec.Name, cancellationToken);
                        throw new InvalidOperationException($"PostgreSQL did not validate managed JSON index {spec.Name}.");
                    }
                }

                // Settings may change while a concurrent build is waiting on writers. Never remove an
                // old index until every index wanted by the latest committed settings is ready.
                var latestDesired = await ReadDesiredIndexesAsync(connection, cancellationToken);
                actual = await ReadManagedIndexesAsync(connection, cancellationToken);
                if (latestDesired.Keys.Any(name => !actual.TryGetValue(name, out var index) || !index.IsValid || !index.IsReady))
                    continue;

                var obsolete = actual.Values
                    .Where(index => !latestDesired.ContainsKey(index.Name))
                    .OrderBy(index => index.Name, StringComparer.Ordinal)
                    .ToArray();
                for (var index = 0; index < obsolete.Length; index++)
                {
                    var managedIndex = obsolete[index];
                    var progress = obsolete.Length == 0 ? 0.85 : 0.75 + (0.15 * index / obsolete.Length);
                    reportProgress?.Invoke(progress, $"Removing obsolete index {managedIndex.Name}");
                    await DropIndexAsync(connection, managedIndex.Name, cancellationToken);
                    dropped++;
                }

                var finalDesired = await ReadDesiredIndexesAsync(connection, cancellationToken);
                var finalActual = await ReadManagedIndexesAsync(connection, cancellationToken);
                var finalLiveNames = finalActual.Values
                    .Where(index => index.IsValid && index.IsReady)
                    .Select(index => index.Name)
                    .ToHashSet(StringComparer.Ordinal);
                if (finalActual.Values.All(index => index.IsValid && index.IsReady)
                    && finalLiveNames.SetEquals(finalDesired.Keys))
                {
                    var result = new CustomFieldJsonIndexReconcileResult(finalDesired.Count, created, dropped);
                    reportProgress?.Invoke(1, result.Summary);
                    return result;
                }
            }
        }
        finally
        {
            await ReleaseLockAsync(connection);
        }
    }

    public static CustomFieldJsonIndexSpec BuildIndexSpec(string path, string type)
    {
        var normalizedType = CustomFieldTypes.Normalize(type);
        var typeCode = normalizedType switch
        {
            CustomFieldTypes.Text => "t",
            CustomFieldTypes.Number => "n",
            CustomFieldTypes.Boolean => "b",
            _ => throw new ArgumentException("JSON path indexes support text, number, and boolean scalar types.", nameof(type)),
        };
        var functionName = normalizedType switch
        {
            // Arbitrary JSON strings are too large for PostgreSQL B-tree tuples. The bounded UTF-8
            // prefix remains useful for exact filtering with a full-value recheck while keeping
            // inserts safe regardless of the source document's string length.
            CustomFieldTypes.Text => "cove_json_pointer_text_index_key",
            CustomFieldTypes.Number => "cove_json_pointer_number",
            CustomFieldTypes.Boolean => "cove_json_pointer_boolean",
            _ => throw new InvalidOperationException("Unsupported JSON path index type."),
        };
        var hashInput = Encoding.UTF8.GetBytes($"v5\0{normalizedType}\0{path}");
        var hash = Convert.ToHexStringLower(SHA256.HashData(hashInput))[..24];
        return new CustomFieldJsonIndexSpec(
            $"ix_cfv_json_v5_{typeCode}_{hash}",
            path,
            normalizedType,
            functionName);
    }

    private static async Task<Dictionary<string, CustomFieldJsonIndexSpec>> ReadDesiredIndexesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, CustomFieldJsonIndexSpec>(StringComparer.Ordinal);
        await using var command = CreateCommand(connection, """
            SELECT DISTINCT json_path."Path", json_path."Type"
            FROM public.custom_field_json_paths AS json_path
            JOIN public.custom_field_definitions AS definition ON definition."Id" = json_path."DefinitionId"
            WHERE definition."Type" = 'json'
              AND ((json_path."Type" = 'text' AND json_path."Filterable")
                   OR (json_path."Type" IN ('number', 'boolean') AND (json_path."Filterable" OR json_path."Sortable")))
              AND json_path."Type" IN ('text', 'number', 'boolean')
            ORDER BY json_path."Path", json_path."Type";
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var spec = BuildIndexSpec(reader.GetString(0), reader.GetString(1));
            if (result.TryGetValue(spec.Name, out var collision) && collision != spec)
                throw new InvalidOperationException($"Managed JSON index name collision for {spec.Name}.");
            result[spec.Name] = spec;
        }
        return result;
    }

    private static async Task<Dictionary<string, ManagedCustomFieldJsonIndex>> ReadManagedIndexesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ManagedCustomFieldJsonIndex>(StringComparer.Ordinal);
        await using var command = CreateCommand(connection, """
            SELECT index_class.relname,
                   index_metadata.indisvalid,
                   index_metadata.indisready
            FROM pg_class AS index_class
            JOIN pg_index AS index_metadata ON index_metadata.indexrelid = index_class.oid
            JOIN pg_class AS table_class ON table_class.oid = index_metadata.indrelid
            JOIN pg_namespace AS table_namespace ON table_namespace.oid = table_class.relnamespace
            WHERE table_namespace.nspname = 'public'
              AND table_class.relname = 'custom_field_values'
              AND left(index_class.relname, char_length('ix_cfv_json_v')) = 'ix_cfv_json_v'
            ORDER BY index_class.relname;
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var index = new ManagedCustomFieldJsonIndex(
                reader.GetString(0),
                reader.GetBoolean(1),
                reader.GetBoolean(2));
            result[index.Name] = index;
        }
        return result;
    }

    private async Task CreateIndexAsync(
        NpgsqlConnection connection,
        CustomFieldJsonIndexSpec spec,
        CancellationToken cancellationToken)
    {
        var indexedExpression = $"public.{spec.FunctionName}(\"JsonValue\", {QuoteLiteral(spec.Path)})";
        var sql = $"""
            CREATE INDEX CONCURRENTLY {QuoteIdentifier(spec.Name)}
            ON public.custom_field_values
                ("DefinitionId", "EntityType", ({indexedExpression}), "EntityId")
            WHERE "JsonValue" IS NOT NULL
              AND "Position" = 0
              AND {indexedExpression} IS NOT NULL;
            """;
        try
        {
            await using var command = CreateCommand(connection, sql);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var actual = await ReadManagedIndexesAsync(connection, cleanupTimeout.Token);
                    if (actual.TryGetValue(spec.Name, out var failed) && (!failed.IsValid || !failed.IsReady))
                        await DropIndexAsync(connection, spec.Name, cleanupTimeout.Token);
                }
                catch (Exception cleanupException)
                {
                    _logger.LogWarning(cleanupException, "Could not remove incomplete managed JSON index {IndexName}", spec.Name);
                }
            }
            throw;
        }
    }

    private static async Task DropIndexAsync(
        NpgsqlConnection connection,
        string indexName,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            $"DROP INDEX CONCURRENTLY IF EXISTS public.{QuoteIdentifier(indexName)};");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AcquireLockAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT pg_advisory_lock(@key);");
        command.Parameters.AddWithValue("key", AdvisoryLockKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ReleaseLockAsync(NpgsqlConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
            return;

        try
        {
            await using var command = CreateCommand(connection, "SELECT pg_advisory_unlock(@key);");
            command.Parameters.AddWithValue("key", AdvisoryLockKey);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not explicitly release the JSON custom-field index advisory lock");
        }
    }

    private static NpgsqlCommand CreateCommand(NpgsqlConnection connection, string sql)
        => new(sql, connection) { CommandTimeout = 0 };

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string QuoteLiteral(string value)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];
        var tag = $"cove_{hash}";
        var delimiter = $"${tag}$";
        while (value.Contains(delimiter, StringComparison.Ordinal))
        {
            tag += "x";
            delimiter = $"${tag}$";
        }
        return $"{delimiter}{value}{delimiter}";
    }

    private sealed record ManagedCustomFieldJsonIndex(string Name, bool IsValid, bool IsReady);
}

public sealed record CustomFieldJsonIndexSpec(
    string Name,
    string Path,
    string Type,
    string FunctionName);

public sealed record CustomFieldJsonIndexReconcileResult(int DesiredCount, int CreatedCount, int DroppedCount)
{
    public string Summary => $"{DesiredCount} JSON path index(es) ready; {CreatedCount} created, {DroppedCount} removed";
}
