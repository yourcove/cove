using Xunit.Abstractions;

namespace Cove.ApiTests.Infrastructure;

public abstract class ApiTest : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly CoveApiTestFixture _fixture;
    private ApiUser? _user;

    protected ApiTest(
        ITestOutputHelper output,
        CoveApiTestFixture fixture)
    {
        _output = output;
        _fixture = fixture;
    }

    protected Uri ApiUri
        => _fixture.BaseAddress;

    protected ApiUser AsUser()
        => _user
            ?? throw new InvalidOperationException("The API test user has not been initialized.");

    public async Task InitializeAsync()
    {
        try
        {
            _user = await _fixture.ResetAsync();
            _output.WriteLine($"Cove API listening at {ApiUri}");
            _output.WriteLine($"Pause at a breakpoint to call: curl {new Uri(ApiUri, "/health")}");
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public Task DisposeAsync()
    {
        _user?.Dispose();
        _user = null;
        return Task.CompletedTask;
    }
}
