using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using Cove.Api.Hubs;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Events;
using Cove.Core.Interfaces;

namespace Cove.Tests;

public class JobServiceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SimultaneousLimit_BoundsBothLegacyJobModesAndStartsNextAfterCompletion(int limit)
    {
        var config = new CoveConfiguration { MaxParallelTasks = 12, MaxSimultaneousJobs = limit };
        var service = new JobService(new EventBus(), new FakeHubContext(), NullLogger<JobService>.Instance, config: config);
        var started = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var releases = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var ids = Enumerable.Range(0, 4).Select(i => service.Enqueue(
            i == 0 ? "tagging" : "delete", $"Job {i}",
            async (_, ct) => { started[i].SetResult(); await releases[i].Task.WaitAsync(ct); },
            exclusive: i % 2 == 0)).ToArray();
        await service.StartAsync(CancellationToken.None);
        try
        {
            await Task.WhenAll(started.Take(limit).Select(gate => gate.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(ids.Take(limit), id => Assert.Equal(JobStatus.Running, service.GetJob(id)!.Status));
            Assert.All(ids.Skip(limit), id => Assert.Equal(JobStatus.Pending, service.GetJob(id)!.Status));
            Assert.False(started[limit].Task.IsCompleted);

            releases[0].SetResult();
            await started[limit].Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(JobStatus.Pending, service.GetJob(ids[limit + 1])!.Status);
        }
        finally
        {
            foreach (var release in releases) release.TrySetResult();
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopAsync_WaitsForRunningJobsAndDoesNotStartPendingWork()
    {
        var service = new JobService(new EventBus(), new FakeHubContext(), NullLogger<JobService>.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unwinding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Enqueue("tagging", "Tagging", async (_, ct) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { unwinding.SetResult(); await release.Task; }
        });
        var pending = service.Enqueue("delete", "Delete", (_, _) => throw new InvalidOperationException("Must not start"));
        await service.StartAsync(CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stop = service.StopAsync(CancellationToken.None);
            await unwinding.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stop.IsCompleted);
            Assert.Equal(JobStatus.Pending, service.GetJob(pending)!.Status);
            release.SetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(JobStatus.Pending, service.GetJob(pending)!.Status);
        }
        finally
        {
            release.TrySetResult();
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SimultaneousLimit_RuntimeIncreaseAndDecreasePreserveRunningJobs()
    {
        var config = new CoveConfiguration { MaxSimultaneousJobs = 1 };
        var service = new JobService(new EventBus(), new FakeHubContext(), NullLogger<JobService>.Instance, config: config);
        var started = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var releases = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var ids = Enumerable.Range(0, 4).Select(i => service.Enqueue("work", $"Job {i}",
            async (_, ct) => { started[i].SetResult(); await releases[i].Task.WaitAsync(ct); })).ToArray();
        await service.StartAsync(CancellationToken.None);
        try
        {
            await started[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
            config.MaxSimultaneousJobs = 2;
            await started[1].Task.WaitAsync(TimeSpan.FromSeconds(5));
            config.MaxSimultaneousJobs = 1;
            releases[0].SetResult();
            await WaitForTerminalStateAsync(service, ids[0], TimeSpan.FromSeconds(5));
            // Give the dispatcher a settings-recheck interval after the first slot is released.
            await Task.Delay(350);
            Assert.Equal(JobStatus.Running, service.GetJob(ids[1])!.Status);
            Assert.Equal(JobStatus.Pending, service.GetJob(ids[2])!.Status);
            releases[1].SetResult();
            await started[2].Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(JobStatus.Pending, service.GetJob(ids[3])!.Status);
        }
        finally
        {
            foreach (var release in releases) release.TrySetResult();
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SimultaneousLimit_FailureCancellationAndReorderingDoNotStrandSlots()
    {
        var service = new JobService(new EventBus(), new FakeHubContext(), NullLogger<JobService>.Instance);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unwinding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new ConcurrentQueue<string>();
        var first = service.Enqueue("tagging", "Tagging", async (_, ct) =>
        {
            firstStarted.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { unwinding.SetResult(); await release.Task; }
        });
        var cancelled = service.Enqueue("delete", "Cancelled pending job", (_, _) => throw new InvalidOperationException("Must not run"));
        var failed = service.Enqueue("delete", "Failing job", (_, _) => { order.Enqueue("failure"); throw new InvalidOperationException("Expected"); });
        var next = service.Enqueue("delete", "Following job", (_, _) => { order.Enqueue("next"); return Task.CompletedTask; });
        await service.StartAsync(CancellationToken.None);
        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(service.Cancel(cancelled));
            Assert.True(service.ReorderQueued(next, failed));
            Assert.True(service.Cancel(first));
            await unwinding.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(JobStatus.Running, service.GetJob(first)!.Status);
            Assert.Equal(JobStatus.Pending, service.GetJob(next)!.Status);
            release.SetResult();
            await WaitForTerminalStateAsync(service, failed, TimeSpan.FromSeconds(5));
            Assert.Equal(JobStatus.Cancelled, service.GetJob(first)!.Status);
            Assert.Equal(JobStatus.Cancelled, service.GetJob(cancelled)!.Status);
            Assert.Equal(JobStatus.Failed, service.GetJob(failed)!.Status);
            Assert.Equal(JobStatus.Completed, service.GetJob(next)!.Status);
            Assert.Equal(new[] { "next", "failure" }, order.ToArray());
        }
        finally
        {
            release.TrySetResult();
            await service.StopAsync(CancellationToken.None);
        }
    }

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

            var scope = await capturedScope.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

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
    public async Task RunBatchAsync_SchedulesConfiguredWorkersBeforeSynchronousWorkRuns()
    {
        IJobService jobs = new JobService(new EventBus(), new FakeHubContext(), NullLogger<JobService>.Instance);
        using var bothWorkersEntered = new ManualResetEventSlim();
        var activeWorkers = 0;
        var maximumActiveWorkers = 0;

        var result = await jobs.RunBatchAsync(
            units: new[] { 1, 2 },
            maxInFlight: 2,
            work: (_, unit, _) =>
            {
                var active = Interlocked.Increment(ref activeWorkers);
                UpdateMaximum(ref maximumActiveWorkers, active);
                if (active == 2)
                    bothWorkersEntered.Set();

                try
                {
                    if (!bothWorkersEntered.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("The configured workers did not overlap.");
                    unit.Complete(JobUnitOutcome.Succeeded);
                    return Task.CompletedTask;
                }
                finally
                {
                    Interlocked.Decrement(ref activeWorkers);
                }
            },
            progress: new NoOpJobProgress());

        Assert.Equal(2, maximumActiveWorkers);
        Assert.Equal(2, result.SucceededUnits);
        Assert.Equal(0, result.FailedUnits);
    }

    [Fact]
    public async Task DeclaredUnitsExposeStableTotalsBeforeTheFirstUnitStarts()
    {
        var hub = new FakeHubContext();
        var service = new JobService(new EventBus(), hub, NullLogger<JobService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var declared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var jobId = service.Enqueue("delete", "Deleting images", async (progress, ct) =>
            {
                progress.DeclareUnits(Enumerable.Range(1, 5_000).Select(id => (id.ToString(), (string?)"Deleting image")));
                declared.SetResult();
                await release.Task.WaitAsync(ct);
                foreach (var id in Enumerable.Range(1, 5_000))
                {
                    using var unit = progress.StartUnit(id.ToString(), "Deleting image");
                    unit.Complete(JobUnitOutcome.Succeeded);
                }
            });

            await declared.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var running = service.GetJob(jobId);
            Assert.NotNull(running);
            Assert.Equal(5_000, running.UnitsTotal);
            Assert.Equal(0, running.UnitsCompleted);
            Assert.Equal(0d, running.Progress);

            release.SetResult();
            var completed = await WaitForTerminalStateAsync(service, jobId, TimeSpan.FromSeconds(5));
            Assert.Equal(5_000, completed?.UnitsCompleted);
            Assert.InRange(hub.SendCount, 1, 20);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DeclaredUnitCountExposesStableTotalsWithoutPreallocatingUnitRecords()
    {
        var service = new JobService(new EventBus(), new FakeHubContext(), NullLogger<JobService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var declared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var jobId = service.Enqueue("delete", "Deleting library entities", async (progress, ct) =>
            {
                progress.DeclareUnitCount(250_000);
                declared.SetResult();
                await release.Task.WaitAsync(ct);
            });

            await declared.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var running = service.GetJob(jobId);
            Assert.NotNull(running);
            Assert.Equal(250_000, running.UnitsTotal);
            Assert.Equal(0, running.UnitsCompleted);
            Assert.Equal(0, service.GetTrackedUnitCountForTests(jobId));

            release.SetResult();
            await WaitForTerminalStateAsync(service, jobId, TimeSpan.FromSeconds(5));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task OwnedJobsAreVisibleOnlyToTheirOwnerUnlessGlobalAccessIsGranted()
    {
        var service = new JobService(new EventBus(), new FakeHubContext(), NullLogger<JobService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var owner = new JobOwner("user:41");
            var otherOwner = new JobOwner("user:73");
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var jobId = service.EnqueueOwned(
                owner,
                "duplicate-search",
                "Finding duplicate videos",
                async (_, ct) => await release.Task.WaitAsync(ct),
                resultUrl: "/duplicates?search=test-search");

            Assert.NotNull(service.GetJobFor(owner, jobId, includeAll: false));
            Assert.Null(service.GetJobFor(otherOwner, jobId, includeAll: false));
            Assert.Equal(jobId, Assert.Single(service.GetAllJobsFor(owner, includeAll: false)).Id);
            Assert.Empty(service.GetAllJobsFor(otherOwner, includeAll: false));

            var globallyVisible = service.GetJobFor(otherOwner, jobId, includeAll: true);
            Assert.NotNull(globallyVisible);
            Assert.Equal("/duplicates?search=test-search", globallyVisible.ResultUrl);

            release.SetResult();
            await WaitForTerminalStateAsync(service, jobId, TimeSpan.FromSeconds(5));
            Assert.Equal(jobId, Assert.Single(service.GetJobHistoryFor(owner, includeAll: false)).Id);
            Assert.Empty(service.GetJobHistoryFor(otherOwner, includeAll: false));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task LegacyEnqueueOwnsARequestOriginatedJobFromTheAmbientPrincipal()
    {
        var principals = new CurrentPrincipalAccessor();
        principals.Set(new CovePrincipal
        {
            UserId = 41,
            Username = "request-user",
            Kind = PrincipalKind.User,
            Roles = new HashSet<string>(),
            Permissions = new HashSet<string>(),
        });
        var service = new JobService(
            new EventBus(),
            new FakeHubContext(),
            NullLogger<JobService>.Instance,
            principals);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await service.StartAsync(CancellationToken.None);

        try
        {
            var jobId = service.Enqueue("request-job", "Request job", (_, _) => release.Task);
            var owner = new JobOwner("user:41");

            Assert.NotNull(service.GetJobFor(owner, jobId, includeAll: false));
            Assert.Null(service.GetJobFor(new JobOwner("user:42"), jobId, includeAll: false));
        }
        finally
        {
            release.TrySetResult();
            await service.StopAsync(CancellationToken.None);
            principals.Set(null);
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

            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
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

    [Fact]
    public async Task RunningCancellationRemainsActiveUntilInFlightWorkUnwinds()
    {
        var service = new JobService(new EventBus(), new FakeHubContext(), NullLogger<JobService>.Instance);
        await service.StartAsync(CancellationToken.None);

        try
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var jobId = service.Enqueue(
                "image-bulk-delete",
                "Deleting images",
                async (_, ct) =>
                {
                    started.SetResult();
                    try
                    {
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        cancellationObserved.SetResult();
                        await release.Task;
                        throw;
                    }
                });

            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(service.Cancel(jobId));
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var stillRunning = Assert.Single(service.GetAllJobs());
            Assert.Equal(jobId, stillRunning.Id);
            Assert.Equal(JobStatus.Running, stillRunning.Status);
            Assert.Equal("Cancellation requested", stillRunning.SubTask);
            Assert.Null(stillRunning.CompletedAt);
            Assert.Empty(service.GetJobHistory());

            release.SetResult();
            var cancelled = await WaitForTerminalStateAsync(service, jobId, TimeSpan.FromSeconds(5));
            Assert.Equal(JobStatus.Cancelled, cancelled?.Status);
            Assert.NotNull(cancelled?.CompletedAt);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CancelAllAndWaitAsync_WaitsForRunningWorkToUnwindAndClearsHistory()
    {
        var service = new JobService(new EventBus(), new FakeHubContext(), NullLogger<JobService>.Instance);
        await service.StartAsync(CancellationToken.None);

        try
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var unwinding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            service.Enqueue(
                "scan",
                "Long-running scan",
                async (_, ct) =>
                {
                    started.SetResult();
                    try
                    {
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        unwinding.SetResult();
                        await release.Task;
                        throw;
                    }
                });

            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var drain = service.CancelAllAndWaitAsync(TestContext.Current.CancellationToken);
            await unwinding.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.False(drain.IsCompleted);

            release.SetResult();
            await drain.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Empty(service.GetAllJobs());
            Assert.Empty(service.GetJobHistory());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task JobStartedByApiRequest_CanBeRetriedWithThatRequest_ButJobsItStartsCannot()
    {
        const string body = "{\"items\":[{\"url\":\"https://example.com/watch?v=1\",\"entity\":\"Video\"}]}";
        var accessor = new HttpContextAccessor { HttpContext = await CaptureRequestAsync("POST", "/api/system/downloaders/download-batch", body) };
        var service = new JobService(new EventBus(), new FakeHubContext(), NullLogger<JobService>.Instance, requestCapture: new JobRequestCapture(accessor));
        await service.StartAsync(CancellationToken.None);

        try
        {
            string? followUpId = null;
            var jobId = service.Enqueue(
                "download-batch",
                "Downloading 1 item",
                (_, _) =>
                {
                    followUpId = service.Enqueue("scan", "Scanning library", static (_, _) => Task.CompletedTask);
                    return Task.CompletedTask;
                },
                exclusive: false);

            await WaitForTerminalStateAsync(service, jobId, TimeSpan.FromSeconds(5));
            Assert.NotNull(followUpId);
            await WaitForTerminalStateAsync(service, followUpId!, TimeSpan.FromSeconds(5));

            Assert.Equal(
                new JobReplayRequest("POST", "/api/system/downloaders/download-batch", body),
                service.GetRetryRequestFor(null, jobId, includeAll: true));
            Assert.Null(service.GetRetryRequestFor(null, followUpId!, includeAll: true));
            Assert.Contains(service.GetStoredJobHistoryFor(null, includeAll: true), job => job.Info.Id == jobId && job.Request is not null);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("{\"site\":\"example.com\",\"password\":\"hunter2\"}")]
    [InlineData("{\"servers\":[{\"apiKey\":\"abc\"}]}")]
    [InlineData("not json")]
    public async Task JobStartedByRequestCarryingASecret_CannotBeRetried(string body)
    {
        var accessor = new HttpContextAccessor { HttpContext = await CaptureRequestAsync("POST", "/api/jobs/scan", body) };
        var service = new JobService(new EventBus(), new FakeHubContext(), NullLogger<JobService>.Instance, requestCapture: new JobRequestCapture(accessor));
        await service.StartAsync(CancellationToken.None);

        try
        {
            var jobId = service.Enqueue("scan", "Scanning library", static (_, _) => Task.CompletedTask);
            await WaitForTerminalStateAsync(service, jobId, TimeSpan.FromSeconds(5));

            Assert.Null(service.GetRetryRequestFor(null, jobId, includeAll: true));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task JobHistory_IsRestoredFromTheStoreAndEveryFinishedJobIsSaved()
    {
        var saved = new StoredJob(
            new JobOwner("user:1"),
            new JobInfo("old-job", "scan", "Scanning library", JobStatus.Completed, 1, null, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(-1), null),
            new JobReplayRequest("POST", "/api/jobs/scan", null));
        var store = new FakeJobHistoryStore([saved]);
        var service = new JobService(new EventBus(), new FakeHubContext(), NullLogger<JobService>.Instance, historyStore: store);
        await service.StartAsync(CancellationToken.None);

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (service.GetJobHistory().Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(25, TestContext.Current.CancellationToken);

            Assert.Equal(saved.Request, service.GetRetryRequestFor(new JobOwner("user:1"), "old-job", includeAll: false));
            Assert.Null(service.GetRetryRequestFor(new JobOwner("user:2"), "old-job", includeAll: false));

            var jobId = service.Enqueue("clean", "Cleaning library", static (_, _) => Task.CompletedTask);
            await WaitForTerminalStateAsync(service, jobId, TimeSpan.FromSeconds(5));

            // Terminal status can be observed just before history persistence finishes.
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (!store.Saved.Any(job => job.Info.Id == jobId) && DateTime.UtcNow < deadline)
                await Task.Delay(25, TestContext.Current.CancellationToken);

            Assert.Equal(["old-job", jobId], service.GetJobHistory().Select(job => job.Id).Reverse());
            Assert.Contains(store.Saved, job => job.Info.Id == jobId && job.Info.Status == JobStatus.Completed);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task JobEnqueuedWithoutRetry_IsNotTiedToTheRequestThatStartedIt()
    {
        var accessor = new HttpContextAccessor { HttpContext = await CaptureRequestAsync("POST", "/api/updates/apply", "{\"force\":false}") };
        var service = new JobService(new EventBus(), new FakeHubContext(), NullLogger<JobService>.Instance, requestCapture: new JobRequestCapture(accessor));
        await service.StartAsync(CancellationToken.None);

        try
        {
            var jobId = service.EnqueueWithoutRetry("system-update", "Update Cove to 2.0.0", static (_, _) => Task.CompletedTask, exclusive: false);
            await WaitForTerminalStateAsync(service, jobId, TimeSpan.FromSeconds(5));

            Assert.Null(service.GetRetryRequestFor(null, jobId, includeAll: true));
            Assert.Contains(service.GetStoredJobHistoryFor(null, includeAll: true), job => job.Info.Id == jobId && job.Request is null);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task LateFailure_MarksAFinishedJobFailed_AndSavesTheCorrection()
    {
        var store = new FakeJobHistoryStore([]);
        var service = new JobService(new EventBus(), new FakeHubContext(), NullLogger<JobService>.Instance, historyStore: store);
        await service.StartAsync(CancellationToken.None);

        try
        {
            var jobId = service.Enqueue("system-update", "Update Cove to 2.0.0", static (_, _) => Task.CompletedTask, exclusive: false);
            Assert.Equal(JobStatus.Completed, (await WaitForTerminalStateAsync(service, jobId, TimeSpan.FromSeconds(5)))!.Status);

            service.RecordLateFailure(jobId, "Cove 2.0.0 exited during startup.");

            var job = Assert.Single(service.GetJobHistory(), job => job.Id == jobId);
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Equal("Cove 2.0.0 exited during startup.", job.Error);
            Assert.Equal(JobStatus.Failed, store.Saved.Last(saved => saved.Info.Id == jobId).Info.Status);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task LateFailure_RecordedBeforeTheSavedHistoryLoads_IsAppliedWhenItDoes()
    {
        var saved = new StoredJob(
            null,
            new JobInfo("update-job", "system-update", "Update Cove to 2.0.0", JobStatus.Completed, 1, "Restarting into Cove 2.0.0", DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddMinutes(-2), null),
            null);
        var load = new TaskCompletionSource<IReadOnlyList<StoredJob>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeJobHistoryStore([saved], load.Task);
        var service = new JobService(new EventBus(), new FakeHubContext(), NullLogger<JobService>.Instance, historyStore: store);
        await service.StartAsync(CancellationToken.None);

        try
        {
            service.RecordLateFailure("update-job", "Rolled back.");
            load.SetResult([saved]);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (service.GetJobHistory().Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(25, TestContext.Current.CancellationToken);

            var job = Assert.Single(service.GetJobHistory());
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Equal("Rolled back.", job.Error);
            Assert.Contains(store.Saved, correction => correction.Info.Id == "update-job" && correction.Info.Status == JobStatus.Failed);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<HttpContext> CaptureRequestAsync(string method, string path, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);
        await JobRequestCapture.CaptureAsync(context);
        return context;
    }

    private sealed class FakeJobHistoryStore(IReadOnlyList<StoredJob> initial, Task<IReadOnlyList<StoredJob>>? load = null) : IJobHistoryStore
    {
        public ConcurrentQueue<StoredJob> Saved { get; } = new();

        public Task<IReadOnlyList<StoredJob>> LoadRecentAsync(CancellationToken ct) => load ?? Task.FromResult(initial);

        public void Save(StoredJob job) => Saved.Enqueue(job);
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
        private readonly FakeClientProxy _proxy = new();
        public FakeHubContext() => Clients = new FakeHubClients(_proxy);
        public int SendCount => _proxy.SendCount;
        public IHubClients Clients { get; }

        public IGroupManager Groups { get; } = new FakeGroupManager();
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        var current = Volatile.Read(ref target);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private sealed class NoOpJobProgress : IJobProgress
    {
        public void Report(double progress, string? subTask = null)
        {
        }
    }

    private sealed class FakeHubClients : IHubClients
    {
        private readonly IClientProxy _proxy;
        public FakeHubClients(IClientProxy proxy) => _proxy = proxy;

        public IClientProxy All => _proxy;

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _proxy;

        public IClientProxy Client(string connectionId) => _proxy;

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _proxy;

        public IClientProxy Group(string groupName) => _proxy;

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _proxy;

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => _proxy;

        public IClientProxy User(string userId) => _proxy;

        public IClientProxy Users(IReadOnlyList<string> userIds) => _proxy;
    }

    private sealed class FakeClientProxy : IClientProxy
    {
        private int _sendCount;
        public int SendCount => Volatile.Read(ref _sendCount);
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _sendCount);
            return Task.CompletedTask;
        }
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
