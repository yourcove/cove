using System.Collections.Concurrent;
using System.Diagnostics;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cove.Data;

/// <summary>
/// Every Npgsql connection Cove opens should come from a data source built here. A connection made
/// with <c>new NpgsqlConnection(connectionString)</c> or <c>CloneWith</c> belongs to an implicit data
/// source that Npgsql names after the connection string (minus only the password), and that name is
/// attached to every database span and metric when telemetry is on.
/// </summary>
public static class CoveNpgsql
{
    public const string DataSourceName = "cove";
    public const string DedicatedDataSourceName = "cove-dedicated";

    // Unpooled data sources hold no connections, so keeping one per distinct connection string for
    // the life of the process costs nothing; Npgsql does the same for its implicit data sources.
    private static readonly ConcurrentDictionary<string, NpgsqlDataSource> DedicatedDataSources = new(StringComparer.Ordinal);

    public static NpgsqlDataSourceBuilder CreateDataSourceBuilder(string connectionString, string name = DataSourceName)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString) { Name = name };
        // Background services poll the database constantly; outside a traced operation each query
        // would become its own root trace and bury request traces. Only trace commands that have a
        // parent. (Npgsql calls these filters only while a tracing listener is attached.) Physical
        // connection opens have no filter, so they are not traced at all.
        builder.ConfigureTracing(tracing => tracing
            .ConfigureCommandFilter(_ => Activity.Current is not null)
            .ConfigureBatchFilter(_ => Activity.Current is not null)
            .ConfigureCopyOperationFilter(_ => Activity.Current is not null)
            .EnablePhysicalOpenTracing(false));
        return builder;
    }

    /// <summary>
    /// A closed connection outside the application pool, for work that needs its own session:
    /// maintenance statements, advisory locks, temporary tables. Pass a connection string that still
    /// has its password, such as <see cref="CoveConfiguration.DatabaseConnectionString"/>.
    /// </summary>
    public static NpgsqlConnection CreateDedicatedConnection(string connectionString) =>
        DedicatedDataSources
            .GetOrAdd(connectionString, static value => CreateDataSourceBuilder(value, DedicatedDataSourceName).Build())
            .CreateConnection();

    /// <summary>
    /// A closed, dedicated connection to the database <paramref name="db"/> uses, with
    /// <paramref name="configure"/> applied to its settings.
    /// </summary>
    public static NpgsqlConnection CreateDedicatedConnection(CoveContext db, Action<NpgsqlConnectionStringBuilder> configure)
    {
        var source = (NpgsqlConnection)db.Database.GetDbConnection();
        // The context's own connection string has had its password removed, so start from the
        // configured one when it points at the same database.
        var current = new NpgsqlConnectionStringBuilder(source.ConnectionString);
        var configured = db.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()
            ?.ApplicationServiceProvider?.GetService<CoveConfiguration>()?.DatabaseConnectionString;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var settings = new NpgsqlConnectionStringBuilder(configured);
            if (settings.Host == current.Host && settings.Port == current.Port
                && settings.Database == current.Database && settings.Username == current.Username)
            {
                configure(settings);
                return CreateDedicatedConnection(settings.ConnectionString);
            }
        }

        // No usable configured string (a context built outside the host): CloneWith carries the
        // password over. The clone falls back to an implicit, connection-string-named data source.
        configure(current);
        return source.CloneWith(current.ConnectionString);
    }
}
