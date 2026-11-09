using System.Net;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Cove.Tests.Integration;

[RequiresPostgres]
public sealed class StartupHealthSmokeTests
{
    [Fact]
    public async Task DirectClient_UsesDynamicallySelectedKestrelPort()
    {
        using var factory = new CoveWebApplicationFactory("IntegrationStartup");
        var configuredBaseAddress = factory.ClientOptions.BaseAddress;
        using var client = factory.CreateClient();

        await WaitForStartupAsync(client);

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(configuredBaseAddress, client.BaseAddress);
        Assert.NotEqual(80, client.BaseAddress?.Port);
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsOk_AfterStartup()
    {
        using var factory = new CoveWebApplicationFactory("IntegrationStartup");
        using var client = factory.CreateAuthenticatedClient();

        await WaitForStartupAsync(client);

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ServicesThatReadTheConnectionStringAtStartup_TargetTheTestDatabase()
    {
        // Program reads Cove:Postgres into CoveConfiguration before the host is built. Services such as
        // the custom-field index reconciler, backups and the database controller open their own
        // connections from it, so it must name this test's database rather than one from the environment.
        using var factory = new CoveWebApplicationFactory();
        var expected = new NpgsqlConnectionStringBuilder(await factory.GetDatabaseConnectionStringAsync());

        var configuration = factory.Services.GetRequiredService<CoveConfiguration>();
        var actual = new NpgsqlConnectionStringBuilder(configuration.DatabaseConnectionString);

        Assert.Equal(expected.Host, actual.Host);
        Assert.Equal(expected.Port, actual.Port);
        Assert.Equal(expected.Database, actual.Database);
        Assert.False(factory.Services.GetRequiredService<IOptions<PostgresConfig>>().Value.Managed);
        Assert.DoesNotContain(
            factory.Services.GetServices<IHostedService>(),
            service => service is PostgresManagerService);
    }

    [Fact]
    public async Task TheStartupMigrationCheck_SeesProductionsMigrations()
    {
        // EF looks migrations up by the exact context type, so the pending-migration check the
        // IntegrationStartup boot runs proves something only on production's CoveContext.
        using var factory = new CoveWebApplicationFactory();

        var (contextType, migrations, pending) = await factory.WithDbContextAsync(async db => (
            db.GetType(),
            db.Database.GetMigrations().ToArray(),
            (await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken)).ToArray()));

        Assert.Equal(typeof(CoveContext), contextType);
        Assert.NotEmpty(migrations);
        Assert.Empty(pending);
    }

    private static async Task WaitForStartupAsync(HttpClient client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync("/health/startup");
                if (response.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch (Exception exception)
            {
                lastError = exception;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The API did not report startup readiness in time.", lastError);
    }
}
