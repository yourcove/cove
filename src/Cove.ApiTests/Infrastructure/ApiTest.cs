using Xunit.Abstractions;

namespace Cove.ApiTests.Infrastructure;

public abstract class ApiTest : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private CoveApiServer? _server;
    private ApiUser? _user;

    protected ApiTest(ITestOutputHelper output)
    {
        _output = output;
    }

    protected Uri ApiUri
        => _server?.BaseAddress
            ?? throw new InvalidOperationException("The API test server has not been initialized.");

    protected ApiUser AsUser()
        => _user
            ?? throw new InvalidOperationException("The API test user has not been initialized.");

    public async Task InitializeAsync()
    {
        try
        {
            _server = await CoveApiServer.StartAsync();
            _user = await _server.CreateOwnerAsync();
            _output.WriteLine($"Cove API listening at {ApiUri}");
            _output.WriteLine($"Pause at a breakpoint to call: curl {new Uri(ApiUri, "/health")}");
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        _user?.Dispose();
        _user = null;

        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }
    }
}
