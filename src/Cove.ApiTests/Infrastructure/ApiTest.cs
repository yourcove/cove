using Cove.Core.DTOs;
using Cove.Core.Auth;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Infrastructure;

public abstract class ApiTest : IAsyncLifetime, IClassFixture<CoveApiTestFixture>
{
    private readonly ITestOutputHelper _output;
    private readonly CoveApiTestFixture _fixture;
    private IReadOnlyDictionary<string, CoveClient>? _users;
    private readonly List<CoveClient> _credentialClients = [];

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

    protected CoveClient AsUser(ApiTokenIssued token)
    {
        var client = new CoveClient($"api-token:{token.Id:N}", ApiUri, token.PlaintextToken);
        _credentialClients.Add(client);
        return client;
    }

    protected CoveClient AsShareLink(ShareLinkIssued link, string? password = null)
    {
        var client = new CoveClient(
            $"share-link:{link.Id:N}",
            ApiUri,
            headers =>
            {
                headers.Add("X-Share-Token", link.PlaintextToken);
                if (password is not null)
                    headers.Add("X-Share-Password", password);
            });
        _credentialClients.Add(client);
        return client;
    }

    protected CoveClient AsAnonymous()
    {
        var client = new CoveClient("anonymous", ApiUri, _ => { });
        _credentialClients.Add(client);
        return client;
    }

    protected DatabaseClient AsDbUser()
        => _fixture.DbUser;

    protected MetadataServiceSimulator AsMetadataService()
        => _fixture.MetadataService;

    protected DownloadSourceSimulator AsDownloadSource()
        => _fixture.DownloadSource;

    protected ExtensionRegistrySimulator AsExtensionRegistry()
        => _fixture.ExtensionRegistry;

    protected ApiTestFileManagerRecorder AsFileManagerRecorder()
        => _fixture.FileManagerRecorder;

    protected ApiTestFileSystem AsTestFileSystem()
        => _fixture.FileSystem;

    protected Task ConfigureFaceSuggestionPlanAsync(
        IReadOnlyDictionary<int, IReadOnlyList<FaceSuggestionDto>> plan,
        CancellationToken cancellationToken = default)
        => _fixture.ConfigureFaceSuggestionPlanAsync(plan, cancellationToken);

    protected void RetireApiInstanceAfterClass()
        => _fixture.RetireAfterClass();

    protected static void AssertCompletedBulkDeletion(
        JobInfo job,
        int succeeded,
        int skipped)
    {
        job.Status.Should().Be(JobStatus.Completed);
        job.UnitsTotal.Should().Be(succeeded + skipped);
        job.UnitsCompleted.Should().Be(succeeded + skipped);
        job.UnitsSucceeded.Should().Be(succeeded);
        job.UnitsFailed.Should().Be(0);
        job.UnitsSkipped.Should().Be(skipped);
    }

    public async ValueTask InitializeAsync()
    {
        try
        {
            _users = await _fixture.ResetAsync(TestContext.Current.CancellationToken);
            _output.WriteLine($"Cove API listening at {ApiUri}");
            _output.WriteLine($"Pause at a breakpoint to call: curl {new Uri(ApiUri, "/health")}");
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_users != null)
            foreach (var user in _users.Values)
                user.Dispose();
        foreach (var client in _credentialClients)
            client.Dispose();
        _credentialClients.Clear();
        _users = null;
        return ValueTask.CompletedTask;
    }
}
