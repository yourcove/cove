namespace Cove.ApiTests.Infrastructure;

public sealed class CoveApiTestFixture : IAsyncLifetime
{
    private CoveApiServer? _server;

    internal Uri BaseAddress
        => _server?.BaseAddress
            ?? throw new InvalidOperationException("The fluent API-test server has not been initialized.");

    public async Task InitializeAsync()
        => _server = await CoveApiServer.StartAsync();

    internal Task<ApiUser> ResetAsync(CancellationToken cancellationToken = default)
        => (_server
            ?? throw new InvalidOperationException("The fluent API-test server has not been initialized."))
            .ResetAsync(cancellationToken);

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }
    }
}
