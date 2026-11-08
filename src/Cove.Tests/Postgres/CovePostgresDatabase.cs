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

    ValueTask IAsyncLifetime.InitializeAsync() => InitializeAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_dataSource != null)
            await _dataSource.DisposeAsync();
        if (_database != null)
            await _database.DisposeAsync();
    }

    // Mirrors the provider settings in AddCoveData. Retry-on-failure and the DI-resolved interceptors
    // are left to the test, because they change how a test must open its own transactions.
    internal static DbContextOptions<CoveContext> CreateOptions(
        NpgsqlDataSource dataSource,
        Action<DbContextOptionsBuilder<CoveContext>>? configure = null)
    {
        var builder = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(dataSource, npgsql =>
            {
                npgsql.UseVector();
                npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            })
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        configure?.Invoke(builder);
        return builder.Options;
    }

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
