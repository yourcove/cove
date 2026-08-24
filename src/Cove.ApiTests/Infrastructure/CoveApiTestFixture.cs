using Cove.Core.DTOs;

namespace Cove.ApiTests.Infrastructure;

public sealed class CoveApiTestFixture : IAsyncLifetime
{
    private readonly CoveApiTestPool _pool;
    private CoveApiServer? _server;
    private bool _retireAfterClass;

    public CoveApiTestFixture(CoveApiTestPool pool)
        => _pool = pool;

    internal bool IsInitialized
        => _server is not null;

    internal Uri BaseAddress
        => _server?.BaseAddress
            ?? throw new InvalidOperationException("The fluent API-test server has not been initialized.");

    internal string DatabaseName
        => (_server
            ?? throw new InvalidOperationException("The fluent API-test server has not been initialized."))
            .DatabaseName;

    internal string DataRoot
        => (_server
            ?? throw new InvalidOperationException("The fluent API-test server has not been initialized."))
            .DataRoot;

    internal long ProcessStartedTimestamp
        => (_server
            ?? throw new InvalidOperationException("The fluent API-test server has not been initialized."))
            .ProcessStartedTimestamp;

    internal long ReadyTimestamp
        => (_server
            ?? throw new InvalidOperationException("The fluent API-test server has not been initialized."))
            .ReadyTimestamp;

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

    internal ApiTestFileManagerRecorder FileManagerRecorder
        => (_server
            ?? throw new InvalidOperationException("The fluent API-test server has not been initialized."))
            .FileManagerRecorder;

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

    public async ValueTask InitializeAsync()
        => _server = await _pool.RentAsync(TestContext.Current.CancellationToken);

    internal Task<IReadOnlyDictionary<string, CoveClient>> ResetAsync(
        CancellationToken cancellationToken = default)
        => (_server
            ?? throw new InvalidOperationException("The fluent API-test server has not been initialized."))
            .ResetAsync(cancellationToken);

    internal void RetireAfterClass()
    {
        if (_server is null)
            throw new InvalidOperationException("The fluent API-test server has not been initialized.");
        _retireAfterClass = true;
    }

    public async ValueTask DisposeAsync()
    {
        var server = _server;
        _server = null;
        if (server is not null)
        {
            if (_retireAfterClass)
                await _pool.RetireAsync(server);
            else
                _pool.Return(server);
        }
        _retireAfterClass = false;
    }
}
