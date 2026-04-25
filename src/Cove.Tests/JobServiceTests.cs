using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Cove.Api.Hubs;
using Cove.Api.Services;
using Cove.Core.Events;
using Cove.Core.Interfaces;

namespace Cove.Tests;

public class JobServiceTests
{
    [Fact]
    public async Task ExclusiveJob_CompletesWithCompletedStatusAndTimestamp()
    {
        var service = new JobService(new EventBus(), new FakeHubContext(), NullLogger<JobService>.Instance);
        await service.StartAsync(CancellationToken.None);

        try
        {
            var jobId = service.Enqueue(
                "generate",
                "Generating content",
                static (progress, _) =>
                {
                    progress.Report(0.5, "Halfway");
                    return Task.CompletedTask;
                });

            var job = await WaitForTerminalStateAsync(service, jobId, TimeSpan.FromSeconds(5));

            Assert.NotNull(job);
            Assert.Equal(JobStatus.Completed, job.Status);
            Assert.Equal(1.0, job.Progress, 3);
            Assert.NotNull(job.CompletedAt);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<JobInfo?> WaitForTerminalStateAsync(IJobService service, string jobId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var job = service.GetJob(jobId);
            if (job is { Status: JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled })
                return job;

            await Task.Delay(25);
        }

        return service.GetJob(jobId);
    }

    private sealed class FakeHubContext : IHubContext<JobHub>
    {
        public IHubClients Clients { get; } = new FakeHubClients();

        public IGroupManager Groups { get; } = new FakeGroupManager();
    }

    private sealed class FakeHubClients : IHubClients
    {
        private static readonly IClientProxy Proxy = new FakeClientProxy();

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

    private sealed class FakeClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}