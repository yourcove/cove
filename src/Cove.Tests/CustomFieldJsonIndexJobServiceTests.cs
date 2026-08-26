using Cove.Api.Services;
using Cove.Core.Interfaces;
using Cove.Data.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public sealed class CustomFieldJsonIndexJobServiceTests
{
    [Fact]
    public async Task RequestArrivingDuringCompletionSchedulesExactlyOneFollowUpJob()
    {
        var jobs = new CapturingJobService();
        CustomFieldJsonIndexJobService service = null!;
        service = new CustomFieldJsonIndexJobService(
            jobs,
            (_, _) => Task.FromResult(new CustomFieldJsonIndexReconcileResult(0, 0, 0)),
            NullLogger<CustomFieldJsonIndexJobService>.Instance);
        var requestDuringSummary = new SummaryTriggerProgress(service.RequestReconcile);

        service.RequestReconcile();
        Assert.Single(jobs.Work);

        await jobs.RunAsync(0, requestDuringSummary, TestContext.Current.CancellationToken);
        Assert.Equal(2, jobs.Work.Count);

        await jobs.RunAsync(1, new NullProgress(), TestContext.Current.CancellationToken);
        service.RequestReconcile();
        Assert.Equal(3, jobs.Work.Count);
        Assert.All(jobs.Exclusive, Assert.True);
    }

    [Fact]
    public void CancelledPendingJobDoesNotSuppressLaterRequest()
    {
        var jobs = new CapturingJobService();
        var service = CreateService(jobs);

        service.RequestReconcile();
        jobs.SetStatus(0, JobStatus.Cancelled);

        service.RequestReconcile();

        Assert.Equal(2, jobs.Work.Count);
    }

    [Fact]
    public async Task RequestArrivingDuringCancellationSchedulesFollowUpJob()
    {
        var jobs = new CapturingJobService();
        CustomFieldJsonIndexJobService service = null!;
        service = new CustomFieldJsonIndexJobService(
            jobs,
            (_, cancellationToken) =>
            {
                service.RequestReconcile();
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new CustomFieldJsonIndexReconcileResult(0, 0, 0));
            },
            NullLogger<CustomFieldJsonIndexJobService>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        service.RequestReconcile();
        await Assert.ThrowsAsync<OperationCanceledException>(() => jobs.RunAsync(0, new NullProgress(), cancellation.Token));

        Assert.Equal(2, jobs.Work.Count);
    }

    [Fact]
    public async Task RequestArrivingAfterJobStartsButBeforeCancelledCallbackSchedulesFollowUpJob()
    {
        var jobs = new CapturingJobService();
        var service = new CustomFieldJsonIndexJobService(
            jobs,
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new CustomFieldJsonIndexReconcileResult(0, 0, 0));
            },
            NullLogger<CustomFieldJsonIndexJobService>.Instance);
        using var cancellation = new CancellationTokenSource();

        service.RequestReconcile();
        jobs.SetStatus(0, JobStatus.Running);
        cancellation.Cancel();
        service.RequestReconcile();
        await Assert.ThrowsAsync<OperationCanceledException>(() => jobs.Work[0](new NullProgress(), cancellation.Token));

        Assert.Equal(2, jobs.Work.Count);
    }

    [Fact]
    public async Task CancellationWithoutNewRequestDoesNotScheduleFollowUpJob()
    {
        var jobs = new CapturingJobService();
        var service = new CustomFieldJsonIndexJobService(
            jobs,
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new CustomFieldJsonIndexReconcileResult(0, 0, 0));
            },
            NullLogger<CustomFieldJsonIndexJobService>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        service.RequestReconcile();
        await Assert.ThrowsAsync<OperationCanceledException>(() => jobs.RunAsync(0, new NullProgress(), cancellation.Token));

        Assert.Single(jobs.Work);
    }

    private static CustomFieldJsonIndexJobService CreateService(CapturingJobService jobs)
        => new(
            jobs,
            (_, _) => Task.FromResult(new CustomFieldJsonIndexReconcileResult(0, 0, 0)),
            NullLogger<CustomFieldJsonIndexJobService>.Instance);

    private sealed class CapturingJobService : IJobService
    {
        public List<Func<IJobProgress, CancellationToken, Task>> Work { get; } = [];
        public List<bool> Exclusive { get; } = [];
        private readonly List<JobInfo> _jobs = [];

        public string Enqueue(
            string type,
            string description,
            Func<IJobProgress, CancellationToken, Task> work,
            bool exclusive = true)
        {
            Assert.Equal(CustomFieldJsonIndexJobService.JobType, type);
            Work.Add(work);
            Exclusive.Add(exclusive);
            var id = $"json-index-{Work.Count}";
            _jobs.Add(CreateJobInfo(id, JobStatus.Pending));
            return id;
        }

        public async Task RunAsync(int index, IJobProgress progress, CancellationToken cancellationToken)
        {
            SetStatus(index, JobStatus.Running);
            await Work[index](progress, cancellationToken);
            SetStatus(index, JobStatus.Completed);
        }

        public void SetStatus(int index, JobStatus status)
            => _jobs[index] = CreateJobInfo(_jobs[index].Id, status);

        public bool Cancel(string jobId) => false;
        public bool ReorderQueued(string jobId, string? beforeJobId) => false;
        public JobInfo? GetJob(string jobId) => _jobs.SingleOrDefault(job => job.Id == jobId);
        public IReadOnlyList<JobInfo> GetAllJobs() => [];
        public IReadOnlyList<JobInfo> GetJobHistory() => [];

        private static JobInfo CreateJobInfo(string id, JobStatus status)
            => new(
                id,
                CustomFieldJsonIndexJobService.JobType,
                "Reconcile JSON custom-field indexes",
                status,
                0,
                null,
                DateTime.UtcNow,
                null,
                null);
    }

    private sealed class SummaryTriggerProgress(Action trigger) : IJobProgress
    {
        private Action? _trigger = trigger;

        public void Report(double progress, string? subTask = null)
        {
        }

        public void SetSummary(string summary)
            => Interlocked.Exchange(ref _trigger, null)?.Invoke();
    }

    private sealed class NullProgress : IJobProgress
    {
        public void Report(double progress, string? subTask = null)
        {
        }
    }
}
