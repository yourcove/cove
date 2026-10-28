using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Cove.Api.Hubs;
using Cove.Api.Services;
using Cove.Api.Updates;
using Cove.Core.Auth;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

/// <summary>
/// Install and automatic-update decisions of <see cref="UpdateService"/> for a native install, against a
/// fake release feed. Downloads fail (the feed serves no archives), so nothing is ever installed.
/// </summary>
public sealed class UpdateServiceTests : IAsyncLifetime
{
    private const string Offered = "999.0.0";
    private static readonly string OldSha = new('a', 64);
    private static readonly string NewSha = new('b', 64);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cove-update-service-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFeed _feed = new();
    private readonly SqliteConnection _sqlite = new("Data Source=:memory:");
    private readonly ServiceProvider _services;
    private readonly JobService _jobs = new(new EventBus(), new NullHubContext(), NullLogger<JobService>.Instance);
    private readonly CoveConfiguration _config = new();
    private readonly UpdateService _updates;

    public UpdateServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "app"));
        var options = new DbContextOptionsBuilder<CoveContext>().UseSqlite(_sqlite).Options;
        _services = new ServiceCollection().AddScoped(_ => new CoveContext(options)).BuildServiceProvider();
        _config.Updates.Channel = "stable";
        var environment = new InstallEnvironment
        {
            Kind = InstallKinds.Native,
            LaunchMode = LaunchModes.Direct,
            Rid = "linux-x64",
            AppDirectory = Path.Combine(_root, "app"),
            ExecutablePath = Path.Combine(_root, "app", "Cove"),
            AppDirectoryWritable = true,
        };
        _updates = new UpdateService(
            _config,
            new ConfigService(_config, NullLogger<ConfigService>.Instance, Path.Combine(_root, "cove-config.json")),
            _jobs,
            new NoopAudit(),
            _services.GetRequiredService<IServiceScopeFactory>(),
            new FakeLifetime(),
            new FeedHttpClientFactory(_feed),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cove:Updates:FeedBaseUrl"] = "https://feed.test/yourcove/cove",
                ["Cove:Updates:ApiBaseUrl"] = "https://api.test/repos/yourcove/cove",
            }).Build(),
            server: null,
            NullLogger<UpdateService>.Instance,
            TimeProvider.System,
            () => environment,
            _root);
    }

    public async ValueTask InitializeAsync()
    {
        await _jobs.StartAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _jobs.CancelAllAndWaitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        await _jobs.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        await _services.DisposeAsync();
        await _sqlite.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Apply_refuses_a_version_the_feed_no_longer_offers_and_refreshes_the_status()
    {
        _feed.Manifest = Manifest(Offered, OldSha);
        await _updates.CheckAsync(TestContext.Current.CancellationToken);
        _feed.Manifest = null;

        var outcome = await _updates.StartApplyAsync(null, force: true, automatic: false, Offered, TestContext.Current.CancellationToken);

        Assert.Equal(409, outcome.StatusCode);
        Assert.Contains($"Cove {Offered} is no longer offered", outcome.Message);
        Assert.Null(outcome.JobId);
        var status = await _updates.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.False(status.UpdateAvailable);
        Assert.Equal("idle", status.ApplyState);
    }

    [Fact]
    public async Task Apply_refuses_when_the_feed_now_offers_a_different_version()
    {
        _feed.Manifest = Manifest(Offered, OldSha);
        await _updates.CheckAsync(TestContext.Current.CancellationToken);
        _feed.Manifest = Manifest("999.1.0", OldSha);

        var outcome = await _updates.StartApplyAsync(null, force: true, automatic: false, Offered, TestContext.Current.CancellationToken);

        Assert.Equal(409, outcome.StatusCode);
        Assert.Contains("now offers Cove 999.1.0", outcome.Message);
        Assert.Equal("999.1.0", (await _updates.GetStatusAsync(TestContext.Current.CancellationToken)).Latest!.Version);
    }

    [Fact]
    public async Task Apply_uses_the_release_as_published_now_not_the_last_check()
    {
        _feed.Manifest = Manifest(Offered, OldSha);
        await _updates.CheckAsync(TestContext.Current.CancellationToken);
        _feed.Manifest = Manifest(Offered, NewSha);
        var manifestRequests = _feed.ManifestRequests;

        var outcome = await _updates.StartApplyAsync(null, force: true, automatic: false, Offered, TestContext.Current.CancellationToken);

        Assert.Equal(202, outcome.StatusCode);
        Assert.True(_feed.ManifestRequests > manifestRequests);
        var state = UpdateJson.Read<UpdateCheckState>(new UpdatePaths(_root).StatePath)!;
        Assert.Equal(NewSha, state.Latest!.FindAsset("linux-x64")!.Sha256);

        var job = await WaitForJobAsync(outcome.JobId!);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("HTTP 404", job.Error);
        Assert.Null(_jobs.GetRetryRequestFor(null, outcome.JobId!, includeAll: true));
        // A download failure says nothing about the release; automatic updates may try it again later.
        Assert.Empty(UpdateJson.Read<UpdateCheckState>(new UpdatePaths(_root).StatePath)!.FailedVersions);
    }

    [Fact]
    public async Task Automatic_update_never_retries_a_version_that_was_rolled_back_earlier()
    {
        _config.Updates.AutoUpdate = AutoUpdateModes.All;
        _config.Updates.AutoUpdateWindow = WindowAroundNow();
        _feed.Manifest = Manifest(Offered, OldSha);
        await _updates.CheckAsync(TestContext.Current.CancellationToken);
        var paths = new UpdatePaths(_root);
        UpdateFailureMemory.Remember(paths.StatePath, Offered, UpdateFailureOutcomes.RolledBack, "exited during startup");
        // A later update or revert replaced the journal of the rolled-back attempt.
        UpdateJson.Write(paths.JournalPath, new UpdateJournal
        {
            Kind = UpdateJournalKinds.Revert,
            State = UpdateJournalStates.Succeeded,
            FromVersion = "998.0.0",
            ToVersion = UpdateService.CurrentVersion,
            AppDirectory = Path.Combine(_root, "app"),
            CoveFileName = "Cove",
            ManagerFileName = "Cove.InstanceManager",
            LaunchMode = LaunchModes.Direct,
        });
        await _updates.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(await _updates.TryAutoUpdateAsync(TestContext.Current.CancellationToken));
        Assert.Empty(_jobs.GetAllJobs());
        var status = await _updates.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateFailureOutcomes.RolledBack, status.Latest!.PreviousOutcome);

        // Installing it by hand is still allowed.
        var manual = await _updates.StartApplyAsync(null, force: true, automatic: false, Offered, TestContext.Current.CancellationToken);
        Assert.Equal(202, manual.StatusCode);
        await WaitForJobAsync(manual.JobId!);
    }

    [Fact]
    public async Task Automatic_update_installs_a_version_that_never_failed()
    {
        _config.Updates.AutoUpdate = AutoUpdateModes.All;
        _config.Updates.AutoUpdateWindow = WindowAroundNow();
        _feed.Manifest = Manifest(Offered, OldSha);
        await _updates.CheckAsync(TestContext.Current.CancellationToken);
        UpdateFailureMemory.Remember(new UpdatePaths(_root).StatePath, "998.0.0", UpdateFailureOutcomes.RolledBack, null);

        Assert.True(await _updates.TryAutoUpdateAsync(TestContext.Current.CancellationToken));
        var jobId = (await _updates.GetStatusAsync(TestContext.Current.CancellationToken)).ApplyJobId;
        Assert.NotNull(jobId);
        await WaitForJobAsync(jobId);
    }

    private static string WindowAroundNow()
    {
        var now = TimeOnly.FromDateTime(DateTime.Now);
        return $"{now.AddHours(-1):HH\\:mm}-{now.AddHours(1):HH\\:mm}";
    }

    private async Task<JobInfo> WaitForJobAsync(string jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (_jobs.GetJob(jobId) is { Status: JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled } job)
                return job;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException($"Job {jobId} did not finish.");
    }

    private static string Manifest(string version, string sha) => $$"""
        {
          "schemaVersion": 1,
          "version": "{{version}}",
          "channel": "stable",
          "assets": [{ "rid": "linux-x64", "name": "cove-linux-x64.tar.gz", "size": 1024, "sha256": "{{sha}}" }],
          "migrations": []
        }
        """;

    private sealed class FakeFeed : HttpMessageHandler
    {
        private int _manifestRequests;

        public volatile string? Manifest;

        public int ManifestRequests => Volatile.Read(ref _manifestRequests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/releases/latest/download/cove-release.json", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _manifestRequests);
                if (Manifest is { } manifest)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest, Encoding.UTF8, "application/json") });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class FeedHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => throw new InvalidOperationException("The tests never get as far as a restart.");
    }

    private sealed class NoopAudit : IAuditService
    {
        public ConcurrentQueue<string> Actions { get; } = new();

        public Task LogAsync(
            string action,
            string outcome,
            CovePrincipal? actor = null,
            string? targetKind = null,
            string? targetId = null,
            object? detail = null,
            CancellationToken ct = default)
        {
            Actions.Enqueue($"{action}:{outcome}");
            return Task.CompletedTask;
        }
    }

    private sealed class NullHubContext : IHubContext<JobHub>
    {
        public IHubClients Clients { get; } = new NullClients();
        public IGroupManager Groups { get; } = new NullGroups();

        private sealed class NullClients : IHubClients
        {
            private static readonly IClientProxy Proxy = new NullProxy();
            public IClientProxy All => Proxy;
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
            public IClientProxy Client(string connectionId) => Proxy;
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
            public IClientProxy Group(string groupName) => Proxy;
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
            public IClientProxy User(string userId) => Proxy;
            public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
        }

        private sealed class NullProxy : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        private sealed class NullGroups : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }
}
