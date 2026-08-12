using System.Net;

namespace Cove.Tests.Integration;

public sealed class StartupHealthSmokeTests
{
    [Fact]
    public async Task DirectClient_UsesDynamicallySelectedKestrelPort()
    {
        using var factory = new CoveWebApplicationFactory("IntegrationStartup");
        var configuredBaseAddress = factory.ClientOptions.BaseAddress;
        using var client = factory.CreateClient();

        await WaitForStartupAsync(client);

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

        await WaitForStartupAsync(client);

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
