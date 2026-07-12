using System.Net;
using System.Net.Sockets;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Cove.Tests;

[Collection("Managed Postgres integration")]
public sealed class Phase12SchemaParityTests
{
    private const string V1BaselineMigrationId = "20260516223910_V1_0";


    [Fact]
    public async Task V1BaselineMigration_CreatesFreshDatabaseSchema()
    {
        var managedRoot = ResolveManagedPostgresRoot();
        if (managedRoot == null)
            return;

        var databaseName = $"v1_baseline_{Guid.NewGuid():N}";
        await using var environment = await CreateEnvironmentAsync(managedRoot);
        await CreateDatabaseAsync(environment.AdminConnectionString, databaseName);

        try
        {
            await using var context = CreateContext(environment.Port, databaseName);
            var expectedMigrations = context.GetService<IMigrationsAssembly>().Migrations.Keys.ToArray();
            AssertNoPendingModelChanges(context);

            await context.Database.MigrateAsync();

            var applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
            var pending = (await context.Database.GetPendingMigrationsAsync()).ToArray();

            Assert.Equal(expectedMigrations, applied);
            Assert.Empty(pending);

            await AssertAuthFunctionsCreatedAsync(environment.Port, databaseName);
        }
        finally
        {
            await DropDatabaseAsync(environment.AdminConnectionString, databaseName);
        }
    }

    private static CoveContext CreateContext(int port, string databaseName)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(BuildConnectionString(port, databaseName), npgsqlOptions => npgsqlOptions.UseVector())
            .Options;

        return new CoveContext(options);
    }

    private static string BuildConnectionString(int port, string databaseName)
        => $"Host=127.0.0.1;Port={port};Database={databaseName};Username=postgres;Trust Server Certificate=true;Timeout=15;Command Timeout=30";

    private static void AssertNoPendingModelChanges(CoveContext context)
    {
        var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot;
        Assert.NotNull(snapshot);

        var differ = context.GetService<IMigrationsModelDiffer>();
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var snapshotModel = initializer.Initialize(snapshot!.Model, designTime: true);
        var designTimeModel = context.GetService<IDesignTimeModel>().Model;
        var operations = differ.GetDifferences(snapshotModel.GetRelationalModel(), designTimeModel.GetRelationalModel());
        if (operations.Count == 0)
            return;

        var details = string.Join(Environment.NewLine, operations.Select(FormatOperation));
        throw new Xunit.Sdk.XunitException($"Pending model changes detected:{Environment.NewLine}{details}");
    }

    private static string FormatOperation(MigrationOperation operation)
        => operation switch
        {
            AddColumnOperation addColumn => $"AddColumn {addColumn.Table}.{addColumn.Name} ({addColumn.ColumnType ?? addColumn.ClrType.Name})",
            AlterColumnOperation alterColumn => $"AlterColumn {alterColumn.Table}.{alterColumn.Name} ({alterColumn.ColumnType ?? alterColumn.ClrType.Name})",
            CreateTableOperation createTable => $"CreateTable {createTable.Name}",
            CreateIndexOperation createIndex => $"CreateIndex {createIndex.Table}.{createIndex.Name}",
            DropColumnOperation dropColumn => $"DropColumn {dropColumn.Table}.{dropColumn.Name}",
            DropIndexOperation dropIndex => $"DropIndex {dropIndex.Table}.{dropIndex.Name}",
            DropTableOperation dropTable => $"DropTable {dropTable.Name}",
            _ => operation.GetType().Name,
        };

    private static async Task CreateDatabaseAsync(string adminConnectionString, string databaseName)
    {
        await using var conn = new NpgsqlConnection(adminConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string adminConnectionString, string databaseName)
    {
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(adminConnectionString);
        await conn.OpenAsync();

        await using (var terminate = conn.CreateCommand())
        {
            terminate.CommandText = $"""
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = '{databaseName}' AND pid <> pg_backend_pid()
            """;
            await terminate.ExecuteNonQueryAsync();
        }

        await using var drop = conn.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\"";
        await drop.ExecuteNonQueryAsync();
    }

    private static async Task AssertAuthFunctionsCreatedAsync(int port, string databaseName)
    {
        await using var conn = new NpgsqlConnection(BuildConnectionString(port, databaseName));
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT public.cove_authz_can_read(
                true,
                false,
                false,
                ARRAY[]::text[],
                NULL::uuid,
                'video',
                1
            )
            """;
        var result = await cmd.ExecuteScalarAsync();

        Assert.True(result is bool value && value);
    }

    private static async Task<PostgresTestEnvironment> CreateEnvironmentAsync(string managedRoot)
    {
        Exception? lastError = null;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var port = ReserveLoopbackPort();
            var postgresConfig = new PostgresConfig
            {
                Managed = true,
                DataPath = managedRoot,
                Port = port,
                Database = "postgres",
            };

            var manager = new PostgresManagerService(Options.Create(postgresConfig), NullLogger<PostgresManagerService>.Instance);

            try
            {
                await manager.StartAsync(CancellationToken.None);
                return new PostgresTestEnvironment(manager, port, BuildConnectionString(port, "postgres"));
            }
            catch (Exception ex) when (attempt < 4)
            {
                lastError = ex;
                try
                {
                    await manager.StopAsync(CancellationToken.None);
                }
                catch
                {
                }
            }
        }

        throw new InvalidOperationException("Failed to start managed Postgres for V1 baseline migration tests.", lastError);
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string? ResolveManagedPostgresRoot()
    {
        var repoArtifactRoot = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "backup-verify-data");
        if (File.Exists(Path.Combine(repoArtifactRoot, "pgsql", "bin", Exe("pg_ctl"))))
            return repoArtifactRoot;

        var localAppDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cove");
        if (File.Exists(Path.Combine(localAppDataRoot, "pgsql", "bin", Exe("pg_ctl"))))
            return localAppDataRoot;

        return null;
    }

    private static string Exe(string toolName)
        => OperatingSystem.IsWindows() ? toolName + ".exe" : toolName;

    private sealed class PostgresTestEnvironment(PostgresManagerService manager, int port, string adminConnectionString) : IAsyncDisposable
    {
        public int Port { get; } = port;
        public string AdminConnectionString { get; } = adminConnectionString;

        public async ValueTask DisposeAsync()
        {
            await manager.StopAsync(CancellationToken.None);
        }
    }
}

