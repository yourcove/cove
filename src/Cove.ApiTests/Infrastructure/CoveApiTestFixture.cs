using Cove.Core.DTOs;

namespace Cove.ApiTests.Infrastructure;

public sealed class CoveApiTestFixture : IAsyncLifetime
{
    private CoveApiServer? _server;

    internal Uri BaseAddress
        => _server?.BaseAddress
            ?? throw new InvalidOperationException("The fluent API-test server has not been initialized.");

    internal MetadataServiceSimulator MetadataService
        => (_server
            ?? throw new InvalidOperationException("The fluent API-test server has not been initialized."))
            .MetadataService;

    internal DownloadSourceSimulator DownloadSource
        => (_server
            ?? throw new InvalidOperationException("The fluent API-test server has not been initialized."))
            .DownloadSource;

    internal ExtensionRegistrySimulator ExtensionRegistry
        => (_server
            ?? throw new InvalidOperationException("The fluent API-test server has not been initialized."))
            .ExtensionRegistry;

    internal ApiTestFileSystem FileSystem
        => (_server
            ?? throw new InvalidOperationException("The fluent API-test server has not been initialized."))
            .FileSystem;

    internal DatabaseClient DbUser
        => (_server
            ?? throw new InvalidOperationException("The fluent API-test server has not been initialized."))
            .DbUser;

    internal Task ConfigureFaceSuggestionPlanAsync(
        IReadOnlyDictionary<int, IReadOnlyList<FaceSuggestionDto>> plan,
        CancellationToken cancellationToken = default)
        => (_server
            ?? throw new InvalidOperationException("The fluent API-test server has not been initialized."))
            .ConfigureFaceSuggestionPlanAsync(plan, cancellationToken);

    public async Task InitializeAsync()
        => _server = await CoveApiServer.StartAsync();

    internal Task<IReadOnlyDictionary<string, CoveClient>> ResetAsync(
        CancellationToken cancellationToken = default)
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
