using System.Collections.Concurrent;
using System.Diagnostics;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cove.Tests.Postgres;

[RequiresPostgres]
public sealed class CoveNpgsqlTests
{
    [Fact]
    public async Task DedicatedConnection_TracesOnlyUnderAParentAndNeverNamesTheConnectionString()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        using var traced = new TracedCommands();
        var connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false }.ConnectionString;

        await RunAsync(CoveNpgsql.CreateDedicatedConnection(connectionString), traced.Marker + "background", ct);
        using (traced.StartParent())
            await RunAsync(CoveNpgsql.CreateDedicatedConnection(connectionString), traced.Marker + "request", ct);

        var activity = Assert.Single(traced.Activities);
        Assert.Contains("request", (string)activity.GetTagItem("db.query.text")!);
        Assert.NotNull(activity.ParentId);
        Assert.Equal(CoveNpgsql.DedicatedDataSourceName, activity.GetTagItem("db.npgsql.data_source"));
    }

    [Fact]
    public async Task DedicatedConnection_ForAHostContext_UsesTheConfiguredConnectionString()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        var services = new ServiceCollection();
        services.AddSingleton(new CoveConfiguration { DatabaseConnectionString = database.ConnectionString });
        services.AddDbContext<CoveContext>(options => database.Configure(options));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        using var traced = new TracedCommands();

        using (traced.StartParent())
            await RunAsync(CoveNpgsql.CreateDedicatedConnection(db, settings => settings.Pooling = false), traced.Marker, ct);

        var activity = Assert.Single(traced.Activities);
        Assert.Equal(CoveNpgsql.DedicatedDataSourceName, activity.GetTagItem("db.npgsql.data_source"));
    }

    [Fact]
    public async Task DedicatedConnection_ForAContextBuiltOutsideTheHost_StillOpens()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        await using var db = database.CreateContext();

        await RunAsync(CoveNpgsql.CreateDedicatedConnection(db, settings => settings.Pooling = false), "outside-host", ct);
    }

    private static async Task RunAsync(NpgsqlConnection connection, string marker, CancellationToken ct)
    {
        await using (connection)
        {
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand($"SELECT 1 /* {marker} */", connection);
            await command.ExecuteScalarAsync(ct);
        }
    }

    // The listener is process-wide and other tests run queries concurrently, so keep only this test's commands.
    private sealed class TracedCommands : IDisposable
    {
        private readonly ActivitySource _parents = new("Cove.Tests.CoveNpgsqlTests");
        private readonly ActivityListener _listener;

        public TracedCommands()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Npgsql" || source == _parents,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (activity.Source.Name == "Npgsql" && activity.GetTagItem("db.query.text") is string text && text.Contains(Marker))
                        Activities.Add(activity);
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public string Marker { get; } = Guid.NewGuid().ToString("N");

        public ConcurrentBag<Activity> Activities { get; } = [];

        public Activity? StartParent() => _parents.StartActivity("request");

        public void Dispose()
        {
            _listener.Dispose();
            _parents.Dispose();
        }
    }
}
