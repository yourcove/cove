using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Cove.Tests.Postgres;

/// <summary>
/// A disposable Cove database on PostgreSQL with the schema production's migrations build. Share one
/// across a class with <c>IClassFixture&lt;CovePostgresDatabase&gt;</c>, or create one per test with
/// <see cref="CreateAsync"/> when tests must not see each other's rows. Mark the test class
/// <see cref="RequiresPostgresAttribute"/> so runs without a server can filter it out.
/// </summary>
public sealed class CovePostgresDatabase : IAsyncLifetime
{
    private PostgreSqlTestDatabase? _database;
    private NpgsqlDataSource? _dataSource;

    public string ConnectionString => Database.ConnectionString;

    /// <summary>The data source every context of this database shares, configured as production's.</summary>
    public NpgsqlDataSource DataSource => _dataSource ?? throw NotInitialized();

    private PostgreSqlTestDatabase Database => _database ?? throw NotInitialized();

    public static async Task<CovePostgresDatabase> CreateAsync(CancellationToken cancellationToken = default)
    {
        var database = new CovePostgresDatabase();
        try
        {
            await database.InitializeAsync(cancellationToken);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public CoveContext CreateContext(Action<DbContextOptionsBuilder<CoveContext>>? configure = null)
        => new(CreateOptions(configure));

    public DbContextOptions<CoveContext> CreateOptions(Action<DbContextOptionsBuilder<CoveContext>>? configure = null)
        => CreateOptions(DataSource, configure);

    /// <summary>
    /// Points <paramref name="builder"/> at this database with the settings <see cref="CreateOptions(Action{DbContextOptionsBuilder{CoveContext}})"/> uses, for a
    /// context that a service collection builds, such as one registered with <c>AddDbContext</c> and interceptors.
    /// </summary>
    public DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder)
        => Configure(builder, DataSource);

    /// <summary>
    /// Moves each identity sequence in the <c>public</c> schema past the largest id in its table, and never
    /// backwards. Rows seeded with explicit ids do not advance a sequence, so the next row added without an
    /// id would otherwise collide. Call it after the seed is saved: it runs on its own connection.
    /// </summary>
    public async Task SyncIdentitySequencesAsync(CancellationToken cancellationToken = default)
    {
        await using var command = DataSource.CreateCommand(SyncIdentitySequencesSql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Does what <see cref="SyncIdentitySequencesAsync(CancellationToken)"/> does on <paramref name="context"/>'s connection, so it sees
    /// rows that context's open transaction has not committed. Sequences are not transactional: the move survives a rollback.
    /// </summary>
    public static async Task SyncIdentitySequencesAsync(DbContext context, CancellationToken cancellationToken = default)
        => await context.Database.ExecuteSqlRawAsync(SyncIdentitySequencesSql, cancellationToken);

    private const string SyncIdentitySequencesSql = """
            DO $$
            DECLARE item record;
            BEGIN
                FOR item IN
                    SELECT table_schema, table_name, column_name,
                           pg_get_serial_sequence(format('%I.%I', table_schema, table_name), column_name) AS sequence_name
                    FROM information_schema.columns
                    WHERE table_schema = 'public' AND (is_identity = 'YES' OR column_default LIKE 'nextval(%')
                LOOP
                    CONTINUE WHEN item.sequence_name IS NULL;
                    EXECUTE format(
                        'SELECT setval(%L, GREATEST(COALESCE((SELECT max(%I) FROM %I.%I), 0), (SELECT CASE WHEN is_called THEN last_value ELSE last_value - 1 END FROM %s)) + 1, false)',
                        item.sequence_name, item.column_name, item.table_schema, item.table_name, item.sequence_name);
                END LOOP;
            END
            $$;
            """;

    ValueTask IAsyncLifetime.InitializeAsync() => InitializeAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_dataSource != null)
            await _dataSource.DisposeAsync();
        if (_database != null)
            await DeferredDatabaseDrops.EnqueueAsync(_database);
    }

    internal static DbContextOptions<CoveContext> CreateOptions(
        NpgsqlDataSource dataSource,
        Action<DbContextOptionsBuilder<CoveContext>>? configure = null)
    {
        var builder = new DbContextOptionsBuilder<CoveContext>();
        Configure(builder, dataSource);
        configure?.Invoke(builder);
        return builder.Options;
    }

    // Mirrors the provider settings in AddCoveData. Retry-on-failure and the DI-resolved interceptors
    // are left to the test, because they change how a test must open its own transactions.
    private static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, NpgsqlDataSource dataSource)
        => builder
            .UseNpgsql(dataSource, npgsql =>
            {
                npgsql.UseVector();
                npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            })
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));

    internal static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.EnableDynamicJson();
        builder.UseVector();
        return builder.Build();
    }

    private async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        var template = await CovePostgresTemplate.GetNameAsync(cancellationToken);
        _database = await PostgreSqlTestDatabase.CreateAsync("cove_tests", template, cancellationToken);
        _dataSource = CreateDataSource(_database.ConnectionString);
    }

    private static InvalidOperationException NotInitialized()
        => new($"{nameof(CovePostgresDatabase)} is used before it is initialized.");
}
