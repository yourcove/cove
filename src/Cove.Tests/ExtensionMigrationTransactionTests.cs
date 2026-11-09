using System.Data.Common;
using Cove.Plugins;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Cove.Tests;

[RequiresPostgres]
public sealed class ExtensionMigrationTransactionTests
{
    // The table ExtensionManager creates before it applies extension migrations.
    private const string CreateReceiptTableSql = """
        CREATE TABLE IF NOT EXISTS extension_migrations (
            extension_id VARCHAR(256) NOT NULL,
            migration_name VARCHAR(512) NOT NULL,
            applied_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
            PRIMARY KEY (extension_id, migration_name)
        )
        """;

    [Fact]
    public async Task ApplyExtensionMigrationAsync_RollsBackSchemaWhenMigrationFailsBeforeReceipt()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext();
        await db.Database.ExecuteSqlRawAsync(CreateReceiptTableSql, cancellationToken: TestContext.Current.CancellationToken);

        var broken = new ExtensionMigration("probe", """
            CREATE TABLE migration_probe (id INTEGER PRIMARY KEY);
            INSERT INTO missing_table (id) VALUES (1);
            """);

        var failure = await Assert.ThrowsAsync<PostgresException>(() => ExtensionManager.ApplyExtensionMigrationAsync(
            db, "test-extension", broken, CancellationToken.None));
        // The script failed on its second statement, after it had created the table.
        Assert.Equal(PostgresErrorCodes.UndefinedTable, failure.SqlState);

        var tableCount = await ScalarAsync(database,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'migration_probe'");
        var receiptCount = await ScalarAsync(database,
            "SELECT count(*) FROM extension_migrations WHERE extension_id = 'test-extension' AND migration_name = 'probe'");
        Assert.Equal(0, tableCount);
        Assert.Equal(0, receiptCount);

        var retry = new ExtensionMigration("probe", "CREATE TABLE migration_probe (id INTEGER PRIMARY KEY);");
        await ExtensionManager.ApplyExtensionMigrationAsync(db, "test-extension", retry, CancellationToken.None);

        Assert.Equal(1, await ScalarAsync(database,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'migration_probe'"));
        Assert.Equal(1, await ScalarAsync(database,
            "SELECT count(*) FROM extension_migrations WHERE extension_id = 'test-extension' AND migration_name = 'probe'"));
    }

    [Fact]
    public async Task ApplyExtensionMigrationAsync_RunsManualTransactionInsideRetryingStrategy()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var transientReceiptFailure = new TransientReceiptInterceptor();
        await using var db = database.CreateContext(options => options
            .ReplaceService<IExecutionStrategyFactory, RetryingExecutionStrategyFactory>()
            .AddInterceptors(transientReceiptFailure));
        await db.Database.ExecuteSqlRawAsync(CreateReceiptTableSql, cancellationToken: TestContext.Current.CancellationToken);

        var migration = new ExtensionMigration(
            "retrying-strategy",
            "CREATE TABLE retry_strategy_probe (id INTEGER PRIMARY KEY);");

        await ExtensionManager.ApplyExtensionMigrationAsync(
            db, "test-extension", migration, CancellationToken.None);

        Assert.Equal(1, await ScalarAsync(database,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'retry_strategy_probe'"));
        Assert.Equal(1, await ScalarAsync(database,
            "SELECT count(*) FROM extension_migrations WHERE extension_id = 'test-extension' AND migration_name = 'retrying-strategy'"));
        Assert.Equal(1, transientReceiptFailure.FailureCount);
    }

    // Reads on a connection of its own, which sees only what the migration committed.
    private static async Task<long> ScalarAsync(CovePostgresDatabase database, string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class RetryingExecutionStrategyFactory(ExecutionStrategyDependencies dependencies)
        : IExecutionStrategyFactory
    {
        public IExecutionStrategy Create() => new TestRetryingExecutionStrategy(dependencies);
    }

    private sealed class TestRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 1, maxRetryDelay: TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) =>
            exception is RetryableMigrationException;
    }

    private sealed class TransientReceiptInterceptor : DbCommandInterceptor
    {
        private int _failed;

        public int FailureCount => _failed;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith(
                    "INSERT INTO extension_migrations",
                    StringComparison.Ordinal)
                && Interlocked.Exchange(ref _failed, 1) == 0)
            {
                throw new RetryableMigrationException();
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class RetryableMigrationException : Exception;
}
