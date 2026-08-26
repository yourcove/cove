using System.Data.Common;
using Cove.Plugins;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cove.Tests;

public sealed class ExtensionMigrationTransactionTests
{
    [Fact]
    public async Task ApplyExtensionMigrationAsync_RollsBackSchemaWhenMigrationFailsBeforeReceipt()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var db = new DbContext(new DbContextOptionsBuilder<DbContext>()
            .UseSqlite(connection)
            .Options);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE extension_migrations (
                extension_id TEXT NOT NULL,
                migration_name TEXT NOT NULL,
                PRIMARY KEY (extension_id, migration_name)
            );
            """, cancellationToken: TestContext.Current.CancellationToken);

        var broken = new ExtensionMigration("probe", """
            CREATE TABLE migration_probe (id INTEGER PRIMARY KEY);
            INSERT INTO missing_table (id) VALUES (1);
            """);

        await Assert.ThrowsAnyAsync<Exception>(() => ExtensionManager.ApplyExtensionMigrationAsync(
            db, "test-extension", broken, CancellationToken.None));

        var tableCount = await ScalarAsync(connection,
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'migration_probe'");
        var receiptCount = await ScalarAsync(connection,
            "SELECT count(*) FROM extension_migrations WHERE extension_id = 'test-extension' AND migration_name = 'probe'");
        Assert.Equal(0, tableCount);
        Assert.Equal(0, receiptCount);

        var retry = new ExtensionMigration("probe", "CREATE TABLE migration_probe (id INTEGER PRIMARY KEY);");
        await ExtensionManager.ApplyExtensionMigrationAsync(db, "test-extension", retry, CancellationToken.None);

        Assert.Equal(1, await ScalarAsync(connection,
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'migration_probe'"));
        Assert.Equal(1, await ScalarAsync(connection,
            "SELECT count(*) FROM extension_migrations WHERE extension_id = 'test-extension' AND migration_name = 'probe'"));
    }

    [Fact]
    public async Task ApplyExtensionMigrationAsync_RunsManualTransactionInsideRetryingStrategy()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var transientReceiptFailure = new TransientReceiptInterceptor();
        await using var db = new DbContext(new DbContextOptionsBuilder<DbContext>()
            .UseSqlite(connection)
            .ReplaceService<IExecutionStrategyFactory, RetryingExecutionStrategyFactory>()
            .AddInterceptors(transientReceiptFailure)
            .Options);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE extension_migrations (
                extension_id TEXT NOT NULL,
                migration_name TEXT NOT NULL,
                PRIMARY KEY (extension_id, migration_name)
            );
            """, cancellationToken: TestContext.Current.CancellationToken);

        var migration = new ExtensionMigration(
            "retrying-strategy",
            "CREATE TABLE retry_strategy_probe (id INTEGER PRIMARY KEY);");

        await ExtensionManager.ApplyExtensionMigrationAsync(
            db, "test-extension", migration, CancellationToken.None);

        Assert.Equal(1, await ScalarAsync(connection,
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'retry_strategy_probe'"));
        Assert.Equal(1, await ScalarAsync(connection,
            "SELECT count(*) FROM extension_migrations WHERE extension_id = 'test-extension' AND migration_name = 'retrying-strategy'"));
        Assert.Equal(1, transientReceiptFailure.FailureCount);
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
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
