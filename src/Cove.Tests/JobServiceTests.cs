using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using Cove.Api.Hubs;
using Cove.Api.Services;
using Cove.Core.Events;
using Cove.Core.Interfaces;

namespace Cove.Tests;

public class JobServiceTests
{
    [Fact]
    public async Task JobWork_RunsInsideCorrelationScope()
    {
        var logger = new ScopeRecordingLogger<JobService>();
        var service = new JobService(new EventBus(), new FakeHubContext(), logger);
        await service.StartAsync(CancellationToken.None);

        try
        {
            var capturedScope = new TaskCompletionSource<IReadOnlyDictionary<string, object?>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var jobId = service.Enqueue(
                "scan",
                "Scanning library",
                (_, _) =>
                {
                    capturedScope.SetResult(logger.CurrentScope);
                    return Task.CompletedTask;
                });

            var scope = await capturedScope.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(jobId, scope["JobId"]);
            Assert.Equal("scan", scope["JobType"]);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

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

    [Fact]
    public async Task CompletedJobLog_IncludesDurationAndUnitOutcomes()
    {
        var logger = new ScopeRecordingLogger<JobService>();
        var service = new JobService(new EventBus(), new FakeHubContext(), logger);
        await service.StartAsync(CancellationToken.None);

        try
        {
            var jobId = service.Enqueue(
                "generate",
                "Generating content",
                static (progress, _) =>
                {
                    using var succeeded = progress.StartUnit("one");
                    succeeded.Complete(JobUnitOutcome.Succeeded);
                    using var skipped = progress.StartUnit("two");
                    skipped.Complete(JobUnitOutcome.Skipped);
                    return Task.CompletedTask;
                });

            var job = await WaitForTerminalStateAsync(service, jobId, TimeSpan.FromSeconds(5));
            Assert.NotNull(job);

            await WaitForLogEntryAsync(
                logger,
                entry => entry.Message.Contains("completed with status", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));

            var completion = Assert.Single(
                logger.Entries,
                entry => entry.Message.Contains("completed with status", StringComparison.Ordinal));
            Assert.Equal(JobStatus.Completed, completion.Properties["Status"]);
            Assert.IsType<long>(completion.Properties["ElapsedMs"]);
            Assert.Equal(2, completion.Properties["UnitsTotal"]);
            Assert.Equal(2, completion.Properties["UnitsCompleted"]);
            Assert.Equal(1, completion.Properties["UnitsSucceeded"]);
            Assert.Equal(0, completion.Properties["UnitsFailed"]);
            Assert.Equal(1, completion.Properties["UnitsSkipped"]);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CompletedJobLog_OmitsUnitOutcomesWhenJobDoesNotReportUnits()
    {
        var logger = new ScopeRecordingLogger<JobService>();
        var service = new JobService(new EventBus(), new FakeHubContext(), logger);
        await service.StartAsync(CancellationToken.None);

        try
        {
            var jobId = service.Enqueue(
                "refresh",
                "Refreshing",
                static (_, _) => Task.CompletedTask);

            var job = await WaitForTerminalStateAsync(service, jobId, TimeSpan.FromSeconds(5));
            Assert.NotNull(job);

            await WaitForLogEntryAsync(
                logger,
                entry => entry.Message.Contains("completed with status", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));

            var completion = Assert.Single(
                logger.Entries,
                entry => entry.Message.Contains("completed with status", StringComparison.Ordinal));
            Assert.DoesNotContain("units=", completion.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("UnitsTotal", completion.Properties);
            Assert.DoesNotContain("UnitsCompleted", completion.Properties);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RunBatchAsync_AggregatesUnitProgressAndKeepsParentCompletedWhenSomeUnitsFail()
    {
        var service = new JobService(new EventBus(), new FakeHubContext(), NullLogger<JobService>.Instance);
        await service.StartAsync(CancellationToken.None);

        try
        {
            IJobService jobs = service;
            var jobId = service.Enqueue(
                "ai-batch",
                "Analyzing videos",
                async (progress, ct) =>
                {
                    var result = await jobs.RunBatchAsync(
                        units: new[] { "video-a", "video-b", "video-c" },
                        maxInFlight: 2,
                        work: async (videoId, unit, innerCt) =>
                        {
                            unit.Report(0.5, $"Analyzing {videoId}");
                            await Task.Delay(20, innerCt);
                            if (videoId == "video-b")
                                throw new InvalidOperationException("analysis failed");

                            unit.Complete(JobUnitOutcome.Succeeded, $"Finished {videoId}");
                        },
                        progress: progress,
                        ct: ct);

                    progress.Report(1d, result.Summary);
                });

            var job = await WaitForTerminalStateAsync(service, jobId, TimeSpan.FromSeconds(5));

            Assert.NotNull(job);
            Assert.Equal(JobStatus.Completed, job.Status);
            Assert.Equal(1.0, job.Progress, 3);
            Assert.Equal(3, job.UnitsTotal);
            Assert.Equal(3, job.UnitsCompleted);
            Assert.Equal(2, job.UnitsSucceeded);
            Assert.Equal(1, job.UnitsFailed);
            Assert.Equal(0, job.UnitsSkipped);
            Assert.Equal("2 succeeded, 1 failed, 0 skipped", job.Summary);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CancellingJob_MarksCancelled_AndProcessorKeepsRunning()
    {
        var service = new JobService(new EventBus(), new FakeHubContext(), NullLogger<JobService>.Instance);
        await service.StartAsync(CancellationToken.None);

        try
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // A job that signals it has started, then blocks on its own cancellation token and throws
            // OperationCanceledException (the shape a cancelled SaveChangesAsync / Parallel.ForEachAsync
            // produces). This must be treated as a graceful cancel, not an unhandled crash.
            var jobId = service.Enqueue(
                "generate_video_phashes",
                "Generating pHashes",
                async (_, ct) =>
                {
                    started.SetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                });

            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(service.Cancel(jobId));

            var cancelled = await WaitForTerminalStateAsync(service, jobId, TimeSpan.FromSeconds(5));
            Assert.NotNull(cancelled);
            Assert.Equal(JobStatus.Cancelled, cancelled.Status);

            // The queue processor must still be alive and able to run a subsequent job — i.e. the
            // cancellation did not bubble out and tear down the processor loop / host.
            var nextId = service.Enqueue(
                "generate",
                "Follow-up",
                static (_, _) => Task.CompletedTask);

            var next = await WaitForTerminalStateAsync(service, nextId, TimeSpan.FromSeconds(5));
            Assert.NotNull(next);
            Assert.Equal(JobStatus.Completed, next.Status);
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

    private static async Task WaitForLogEntryAsync(
        ScopeRecordingLogger<JobService> logger,
        Func<ScopeRecordingLogger<JobService>.RecordedLog, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (logger.Entries.Any(predicate))
                return;

            await Task.Delay(25);
        }
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

    private sealed class ScopeRecordingLogger<T> : ILogger<T>
    {
        private readonly AsyncLocal<IReadOnlyDictionary<string, object?>?> _currentScope = new();
        private readonly ConcurrentQueue<RecordedLog> _entries = new();

        public IReadOnlyDictionary<string, object?> CurrentScope =>
            _currentScope.Value ?? new Dictionary<string, object?>();

        public IReadOnlyCollection<RecordedLog> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            var previous = _currentScope.Value;
            _currentScope.Value = state as IReadOnlyDictionary<string, object?>
                ?? (state as IEnumerable<KeyValuePair<string, object?>>)?.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value)
                ?? new Dictionary<string, object?>();
            return new ScopeLease(() => _currentScope.Value = previous);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = (state as IEnumerable<KeyValuePair<string, object?>>)?
                .Where(pair => pair.Key != "{OriginalFormat}")
                .ToDictionary(pair => pair.Key, pair => pair.Value)
                ?? new Dictionary<string, object?>();
            _entries.Enqueue(new RecordedLog(
                logLevel,
                formatter(state, exception),
                properties,
                exception));
        }

        public sealed record RecordedLog(
            LogLevel Level,
            string Message,
            IReadOnlyDictionary<string, object?> Properties,
            Exception? Exception);

        private sealed class ScopeLease(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
