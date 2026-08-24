using Npgsql;

namespace Cove.ApiTests.Infrastructure;

internal sealed class PostgreSqlTestDatabase : IAsyncDisposable
{
    private const string ConnectionStringEnvironmentVariable = "COVE_API_TEST_PG_ADMIN_CONNECTION_STRING";
    private readonly string _adminConnectionString;
    private bool _disposed;

    private PostgreSqlTestDatabase(
        string databaseName,
        string adminConnectionString,
        string connectionString)
    {
        DatabaseName = databaseName;
        _adminConnectionString = adminConnectionString;
        ConnectionString = connectionString;
    }

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    public static async Task<PostgreSqlTestDatabase> CreateAsync(
        CancellationToken cancellationToken = default)
    {
        var databaseName = $"cove_api_test_{Guid.NewGuid():N}";
        var admin = LoadAdminConnectionString();

        try
        {
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE {QuoteIdentifier(databaseName)}";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Fluent API tests require a reachable PostgreSQL server and a test account that can create databases. Configure {ConnectionStringEnvironmentVariable} or the COVE_API_TEST_PG_* variables.",
                exception);
        }

        var databaseBuilder = new NpgsqlConnectionStringBuilder(admin)
        {
            Database = databaseName,
            Pooling = true,
            IncludeErrorDetail = true,
            ApplicationName = "Cove.ApiTests",
            CommandTimeout = 60,
            Timeout = 15,
        };

        return new PostgreSqlTestDatabase(databaseName, admin, databaseBuilder.ConnectionString);
    }

    internal static async Task<bool> ExistsAsync(
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        await using var connection = new NpgsqlConnection(LoadAdminConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @databaseName)";
        command.Parameters.AddWithValue("databaseName", databaseName);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await using (var poolConnection = new NpgsqlConnection(ConnectionString))
            NpgsqlConnection.ClearPool(poolConnection);

        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS {QuoteIdentifier(DatabaseName)} WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    private static string LoadAdminConnectionString()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var configuredBuilder = new NpgsqlConnectionStringBuilder(configured)
            {
                Pooling = false,
                IncludeErrorDetail = true,
                ApplicationName = "Cove.ApiTests.Admin",
                CommandTimeout = 60,
                Timeout = 15,
            };
            return configuredBuilder.ConnectionString;
        }

        var host = FirstValue("COVE_API_TEST_PG_HOST", "PGHOST") ?? "127.0.0.1";
        var port = ParsePort(FirstValue("COVE_API_TEST_PG_PORT", "PGPORT"));
        var username = Environment.GetEnvironmentVariable("COVE_API_TEST_PG_USER") ?? "postgres";
        var password = Environment.GetEnvironmentVariable("COVE_API_TEST_PG_PASSWORD");
        password ??= Environment.GetEnvironmentVariable("PGPASSWORD");

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Username = username,
            Password = password ?? string.Empty,
            Database = Environment.GetEnvironmentVariable("COVE_API_TEST_PG_ADMIN_DB") ?? "postgres",
            Pooling = false,
            IncludeErrorDetail = true,
            ApplicationName = "Cove.ApiTests.Admin",
            CommandTimeout = 60,
            Timeout = 15,
        };

        return builder.ConnectionString;
    }

    private static string? FirstValue(string preferredName, string fallbackName)
    {
        var preferred = Environment.GetEnvironmentVariable(preferredName);
        return string.IsNullOrWhiteSpace(preferred)
            ? Environment.GetEnvironmentVariable(fallbackName)
            : preferred;
    }

    private static int ParsePort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 5432;
        if (int.TryParse(value, out var port) && port is > 0 and <= 65_535)
            return port;
        throw new InvalidOperationException($"Invalid PostgreSQL port '{value}'.");
    }

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
