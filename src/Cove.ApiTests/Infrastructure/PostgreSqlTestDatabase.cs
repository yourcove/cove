using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cove.ApiTests.Infrastructure;

internal sealed class PostgreSqlTestDatabase : IAsyncDisposable
{
    private const string ConnectionStringEnvironmentVariable = "COVE_API_TEST_PG_ADMIN_CONNECTION_STRING";

    /// <summary>
    /// Name of the database holding the migrated schema that every test database is cloned from.
    /// </summary>
    /// <remarks>
    /// Fixed rather than unique per run so that at most one of these ever exists on the server: a
    /// per-run name would leave one behind for every run that did not shut down cleanly.
    /// </remarks>
    private const string TemplateDatabaseName = "cove_api_test_template";

    /// <summary>
    /// Serializes template setup so the schema is built exactly once, since lanes start concurrently.
    /// </summary>
    private static readonly SemaphoreSlim TemplateGate = new(1, 1);

    private static bool _templateReady;

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

        // Clone the migrated template instead of handing the host an empty database to migrate. The
        // schema is built once per run; every database after that is a copy.
        await TemplateGate.WaitAsync(cancellationToken);
        try
        {
            if (!_templateReady)
            {
                try
                {
                    await BuildTemplateAsync(admin, cancellationToken);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        $"Could not build the API-test schema template. Fluent API tests require a reachable PostgreSQL server and a test account that can create databases (configure {ConnectionStringEnvironmentVariable} or the COVE_API_TEST_PG_* variables), and the migrations must apply cleanly to an empty database.",
                        exception);
                }

                _templateReady = true;
            }

            try
            {
                await ExecuteAdminAsync(
                    admin,
                    $"CREATE DATABASE {QuoteIdentifier(databaseName)} TEMPLATE {QuoteIdentifier(TemplateDatabaseName)}",
                    cancellationToken);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Could not clone the API-test schema template {TemplateDatabaseName}. PostgreSQL refuses to copy a database while another session is connected to it, so this points at a session that outlived its test rather than at the server itself.",
                    exception);
            }
        }
        finally
        {
            TemplateGate.Release();
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

    /// <summary>
    /// Applies the migrations to a fresh template database, then leaves no session connected to it.
    /// </summary>
    /// <remarks>
    /// Migrating here rather than letting the host do it on startup is the whole point: the baseline
    /// migration issues a great many statements, and it used to be paid once per test database.
    /// The host still runs its own startup checks against each clone, finds the schema current, and
    /// continues.
    /// </remarks>
    private static async Task BuildTemplateAsync(string admin, CancellationToken cancellationToken)
    {
        // Rebuilt every run, so a schema change can never be served from a stale template.
        await ExecuteAdminAsync(
            admin,
            $"DROP DATABASE IF EXISTS {QuoteIdentifier(TemplateDatabaseName)} WITH (FORCE)",
            cancellationToken);
        await ExecuteAdminAsync(
            admin,
            $"CREATE DATABASE {QuoteIdentifier(TemplateDatabaseName)}",
            cancellationToken);

        // Pooling off so this connection cannot outlive the migration: a pooled one would still be
        // attached to the template and CREATE DATABASE ... TEMPLATE would refuse to read it.
        var templateConnectionString = new NpgsqlConnectionStringBuilder(admin)
        {
            Database = TemplateDatabaseName,
            Pooling = false,
            IncludeErrorDetail = true,
            ApplicationName = "Cove.ApiTests.Template",
            CommandTimeout = 600,
            Timeout = 15,
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(templateConnectionString, npgsqlOptions => npgsqlOptions.UseVector())
            .Options;

        await using (var context = new CoveContext(options))
            await context.Database.MigrateAsync(cancellationToken);

        await using (var templateConnection = new NpgsqlConnection(templateConnectionString))
            NpgsqlConnection.ClearPool(templateConnection);

        // Refuse further connections, the way PostgreSQL keeps template0 safe to copy.
        // CREATE DATABASE fails if any session is attached to the source, and this makes it
        // impossible for one to attach once the schema is built. It does not evict a session that is
        // already attached, which is why the migration connection is closed above rather than after.
        await ExecuteAdminAsync(
            admin,
            $"ALTER DATABASE {QuoteIdentifier(TemplateDatabaseName)} WITH ALLOW_CONNECTIONS false",
            cancellationToken);
    }

    private static async Task ExecuteAdminAsync(
        string admin,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
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
