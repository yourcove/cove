using Cove.Core.DTOs;
using Xunit.Abstractions;

namespace Cove.ApiTests.Infrastructure;

public abstract class ApiTest : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly CoveApiTestFixture _fixture;
    private IReadOnlyDictionary<string, CoveClient>? _users;

    protected ApiTest(
        ITestOutputHelper output,
        CoveApiTestFixture fixture)
    {
        _output = output;
        _fixture = fixture;
    }

    protected Uri ApiUri
        => _fixture.BaseAddress;

    protected CoveClient AsUser(string username = ApiTestUsers.Owner)
    {
        if (_users == null)
            throw new InvalidOperationException("The API test users have not been initialized.");
        return _users.TryGetValue(username, out var user)
            ? user
            : throw new InvalidOperationException(
                $"API test user '{username}' is not provisioned. Available users: {string.Join(", ", _users.Keys)}.");
    }

    protected DatabaseClient AsDbUser()
        => _fixture.DbUser;

    protected MetadataServiceSimulator AsMetadataService()
        => _fixture.MetadataService;

    protected DownloadSourceSimulator AsDownloadSource()
        => _fixture.DownloadSource;

    protected ApiTestFileSystem AsTestFileSystem()
        => _fixture.FileSystem;

    protected Task ConfigureFaceSuggestionPlanAsync(
        IReadOnlyDictionary<int, IReadOnlyList<FaceSuggestionDto>> plan,
        CancellationToken cancellationToken = default)
        => _fixture.ConfigureFaceSuggestionPlanAsync(plan, cancellationToken);

    public async Task InitializeAsync()
    {
        try
        {
            _users = await _fixture.ResetAsync();
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
        if (_users != null)
            foreach (var user in _users.Values)
                user.Dispose();
        _users = null;
        return Task.CompletedTask;
    }
}
