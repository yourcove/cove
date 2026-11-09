using System.Reflection;
using Npgsql;

namespace Cove.TestSupport;

// Compiled into each test project that needs a disposable PostgreSQL database, so the API tests and
// the in-process tests share one set of connection settings and one database lifecycle.
internal sealed class PostgreSqlTestDatabase : IAsyncDisposable
{
    private const string ConnectionStringEnvironmentVariable = "COVE_API_TEST_PG_ADMIN_CONNECTION_STRING";
    private static readonly string ClientName = Assembly.GetExecutingAssembly().GetName().Name!;
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

    public static Task<PostgreSqlTestDatabase> CreateAsync(
        CancellationToken cancellationToken = default)
        => CreateAsync("cove_api_test", templateDatabase: null, cancellationToken);

    /// <summary>
    /// Creates an empty database, or a copy of <paramref name="templateDatabase"/>. The name starts
    /// with <paramref name="namePrefix"/> so leftovers from an interrupted run are easy to recognize.
    /// </summary>
    public static Task<PostgreSqlTestDatabase> CreateAsync(
        string namePrefix,
        string? templateDatabase,
        CancellationToken cancellationToken = default)
        => CreateAsync(namePrefix, templateDatabase, options: null, cancellationToken);

    /// <summary>
    /// Creates an empty database with the encoding and locale managed PostgreSQL initializes its cluster with
    /// (UTF8 and the libc C locale), whatever the server's default is. upper(), lower() and text ordering follow the
    /// database's locale, so SQL that depends on them has to be tested under the one managed and all-in-one
    /// installs use. (A server the user runs themselves may have another default locale.)
    /// </summary>
    public static Task<PostgreSqlTestDatabase> CreateWithManagedLocaleAsync(
        string namePrefix,
        CancellationToken cancellationToken = default)
        => CreateAsync(namePrefix, "template0", "ENCODING 'UTF8' LOCALE_PROVIDER libc LC_COLLATE 'C' LC_CTYPE 'C'", cancellationToken);

    private static async Task<PostgreSqlTestDatabase> CreateAsync(
        string namePrefix,
        string? templateDatabase,
        string? options,
        CancellationToken cancellationToken)
    {
        var databaseName = $"{namePrefix}_{Guid.NewGuid():N}";
        var admin = LoadAdminConnectionString();

        try
        {
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = templateDatabase == null
                ? $"CREATE DATABASE {QuoteIdentifier(databaseName)}"
                : $"CREATE DATABASE {QuoteIdentifier(databaseName)} TEMPLATE {QuoteIdentifier(templateDatabase)}";
            if (options != null)
                command.CommandText += " " + options;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var source = templateDatabase == null ? string.Empty : $" from template {templateDatabase}";
            throw new InvalidOperationException(
                $"Could not create test database {databaseName}{source} ({exception.Message}). PostgreSQL-backed tests require a reachable PostgreSQL server and a test account that can create databases. Configure {ConnectionStringEnvironmentVariable} or the COVE_API_TEST_PG_* variables.",
                exception);
        }

        var databaseBuilder = new NpgsqlConnectionStringBuilder(admin)
        {
            Database = databaseName,
            Pooling = true,
            IncludeErrorDetail = true,
            ApplicationName = ClientName,
            CommandTimeout = 60,
            Timeout = 15,
        };

        return new PostgreSqlTestDatabase(databaseName, admin, databaseBuilder.ConnectionString);
    }

    /// <summary>
    /// Closes this database to new connections and ends the open ones so it can be the template of
    /// later databases: PostgreSQL refuses to copy a database while anyone is connected to it.
    /// </summary>
    public async Task SealAsTemplateAsync(CancellationToken cancellationToken = default)
    {
        await using (var poolConnection = new NpgsqlConnection(ConnectionString))
            NpgsqlConnection.ClearPool(poolConnection);

        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            ALTER DATABASE {QuoteIdentifier(DatabaseName)} WITH ALLOW_CONNECTIONS false;
            SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @databaseName AND pid <> pg_backend_pid();
            """;
        command.Parameters.AddWithValue("databaseName", DatabaseName);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
                ApplicationName = ClientName + ".Admin",
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
            ApplicationName = ClientName + ".Admin",
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
