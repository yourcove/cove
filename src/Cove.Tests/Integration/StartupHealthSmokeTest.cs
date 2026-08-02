using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests.Integration;

public sealed class StartupHealthSmokeTests
{
    [Fact]
    public async Task DirectClient_UsesDynamicallySelectedKestrelPort()
    {
        using var factory = new CoveWebApplicationFactory("IntegrationStartup");
        var configuredBaseAddress = factory.ClientOptions.BaseAddress;
        using var client = factory.CreateClient();

        await WaitForStartupDatabaseAsync(factory);

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(configuredBaseAddress, client.BaseAddress);
        Assert.NotEqual(80, client.BaseAddress?.Port);
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsOk_AfterStartup()
    {
        using var factory = new CoveWebApplicationFactory("IntegrationStartup");
        using var client = factory.CreateAuthenticatedClient();

        await WaitForStartupDatabaseAsync(factory);

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task WaitForStartupDatabaseAsync(CoveWebApplicationFactory factory)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var isReady = await factory.WithDbContextAsync(async db =>
                {
                    try
                    {
                        await db.Users.AsNoTracking().AnyAsync();
                        return true;
                    }
                    catch (SqliteException exception) when (exception.SqliteErrorCode == 1 && exception.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                });

                if (isReady)
                    return;
            }
            catch (Exception exception)
            {
                lastError = exception;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Startup database schema was not ready in time.", lastError);
    }
}
