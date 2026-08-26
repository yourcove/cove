using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;

namespace Cove.Tests.Integration;

public sealed class CompoundSortValidationSmokeTests
{
    [Fact]
    public async Task UnsupportedVideoCompoundSortReturnsBadRequest()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/videos?sorts=unsupported%3Adesc%2Cdate%3Adesc", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Unsupported compound sort.", problem?.Title);
        Assert.Contains("unsupported", problem?.Detail);
    }
}
