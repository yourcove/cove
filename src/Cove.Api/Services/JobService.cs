using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Cove.Api.Hubs;
using Cove.Core.Events;
using Cove.Core.Interfaces;

namespace Cove.Api.Services;

public class JobService : IJobService, IHostedService
{
    private readonly Channel<JobEntry> _queue = Channel.CreateUnbounded<JobEntry>();
    private readonly Dictionary<string, JobEntry> _jobs = [];
    private readonly List<JobInfo> _history = [];
    private readonly Lock _lock = new();
    private readonly IEventBus _eventBus;
    private readonly IHubContext<JobHub> _hubContext;
    private readonly ILogger<JobService> _logger;
    private Task? _processorTask;
    private CancellationTokenSource? _cts;
    private const int MaxHistory = 50;

    public JobService(IEventBus eventBus, IHubContext<JobHub> hubContext, ILogger<JobService> logger)
    {
        _eventBus = eventBus;
        _hubContext = hubContext;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processorTask = Task.Run(() => ProcessQueueAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_processorTask != null)
        {
            try { await _processorTask; } catch (OperationCanceledException) { }
        }
    }

    public string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
    {
        var entry = new JobEntry
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Type = type,
            Description = description,
            Status = JobStatus.Pending,
            Progress = 0,
            StartedAt = DateTime.UtcNow,
            Work = work
        };

        lock (_lock) { _jobs[entry.Id] = entry; }

        if (exclusive)
        {
            _queue.Writer.TryWrite(entry);
        }
        else
        {
            _ = RunConcurrentJobAsync(entry);
        }

        _logger.LogInformation("Job {JobId} enqueued ({Mode}): {Type} - {Description}", entry.Id, exclusive ? "exclusive" : "concurrent", type, description);
        NotifyClients(entry);
        return entry.Id;
    }

    public bool Cancel(string jobId)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(jobId, out var entry) && entry.Cts != null)
            {
                entry.Cts.Cancel();
                entry.Status = JobStatus.Cancelled;
                NotifyClients(entry);
                return true;
            }
        }
        return false;
    }

    public JobInfo? GetJob(string jobId)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(jobId, out var entry))
                return entry.ToInfo();

            return _history.FirstOrDefault(job => string.Equals(job.Id, jobId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public IReadOnlyList<JobInfo> GetAllJobs()
    {
        lock (_lock) { return _jobs.Values.Where(j => j.Status is JobStatus.Pending or JobStatus.Running).Select(j => j.ToInfo()).ToList(); }
    }

    public IReadOnlyList<JobInfo> GetJobHistory()
    {
        lock (_lock) { return [.. _history]; }
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        await foreach (var entry in _queue.Reader.ReadAllAsync(ct))
        {
            entry.Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            entry.Status = JobStatus.Running;
            entry.StartedAt = DateTime.UtcNow;
            NotifyClients(entry);

            var progress = new JobProgress(entry, this);

            try
            {
                var msg = $"Job {entry.Id} started: {entry.Description}";
                _logger.LogInformation("{Message}", msg);
                Console.WriteLine($"[JobService] {msg}");

                await entry.Work(progress, entry.Cts.Token);

                FinalizeSuccessfulWork(entry);

                var statusMsg = $"Job {entry.Id} completed with status {entry.Status}";
                _logger.LogInformation("{Message}", statusMsg);
                Console.WriteLine($"[JobService] {statusMsg}");
            }
            catch (OperationCanceledException)
            {
                entry.Status = JobStatus.Cancelled;
                entry.CompletedAt = DateTime.UtcNow;
                var msg = $"Job {entry.Id} cancelled";
                _logger.LogInformation("{Message}", msg);
                Console.WriteLine($"[JobService] {msg}");
            }
            catch (Exception ex)
            {
                entry.Status = JobStatus.Failed;
                entry.Error = ex.Message;
                entry.CompletedAt = DateTime.UtcNow;
                _logger.LogError(ex, "Job {JobId} failed", entry.Id);
                Console.WriteLine($"[JobService] Job {entry.Id} failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }

            NotifyClients(entry);
            MoveToHistory(entry);
        }
    }

    private async Task RunConcurrentJobAsync(JobEntry entry)
    {
        entry.Cts = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? CancellationToken.None);
        entry.Status = JobStatus.Running;
        entry.StartedAt = DateTime.UtcNow;
        NotifyClients(entry);

        var progress = new JobProgress(entry, this);

        try
        {
            _logger.LogInformation("Concurrent job {JobId} started: {Description}", entry.Id, entry.Description);
            await entry.Work(progress, entry.Cts.Token);
            FinalizeSuccessfulWork(entry);
            _logger.LogInformation("Concurrent job {JobId} completed", entry.Id);
        }
        catch (OperationCanceledException)
        {
            entry.Status = JobStatus.Cancelled;
            entry.CompletedAt = DateTime.UtcNow;
            _logger.LogInformation("Concurrent job {JobId} cancelled", entry.Id);
        }
        catch (Exception ex)
        {
            entry.Status = JobStatus.Failed;
            entry.Error = ex.Message;
            entry.CompletedAt = DateTime.UtcNow;
            _logger.LogError(ex, "Concurrent job {JobId} failed", entry.Id);
        }

        NotifyClients(entry);
        MoveToHistory(entry);
    }

    private void MoveToHistory(JobEntry entry)
    {
        lock (_lock)
        {
            _jobs.Remove(entry.Id);
            _history.Insert(0, entry.ToInfo());
            if (_history.Count > MaxHistory)
                _history.RemoveRange(MaxHistory, _history.Count - MaxHistory);
        }
    }

    internal void UpdateProgress(JobEntry entry)
    {
        NotifyClients(entry);
    }

    private static void FinalizeSuccessfulWork(JobEntry entry)
    {
        entry.Status = entry.Cts?.IsCancellationRequested == true ? JobStatus.Cancelled : JobStatus.Completed;
        if (entry.Status == JobStatus.Completed)
            entry.Progress = 1.0;
        entry.CompletedAt = DateTime.UtcNow;
    }

    private void NotifyClients(JobEntry entry)
    {
        var info = entry.ToInfo();
        _ = _hubContext.Clients.All.SendAsync("JobUpdated", info);
        _eventBus.Publish(new JobEvent(
            entry.Status switch
            {
                JobStatus.Running => EventType.ScanStarted,
                JobStatus.Completed => EventType.ScanCompleted,
                _ => EventType.ScanProgress
            },
            info.Id, info.Description, info.Progress, info.SubTask));
    }

    internal class JobEntry
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public string Description { get; set; } = "";
        public JobStatus Status { get; set; }
        public double Progress { get; set; }
        public string? SubTask { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? Error { get; set; }
        public Func<IJobProgress, CancellationToken, Task> Work { get; set; } = null!;
        public CancellationTokenSource? Cts { get; set; }

        public JobInfo ToInfo() => new(Id, Type, Description, Status, Progress, SubTask, StartedAt, CompletedAt, Error);
    }

    private class JobProgress(JobEntry entry, JobService svc) : IJobProgress
    {
        private DateTime _lastReport = DateTime.MinValue;

        public void Report(double progress, string? subTask = null)
        {
            entry.Progress = Math.Clamp(progress, 0, 1);
            entry.SubTask = subTask;

            // Throttle SignalR updates to max 10/sec
            var now = DateTime.UtcNow;
            if ((now - _lastReport).TotalMilliseconds >= 100)
            {
                _lastReport = now;
                svc.UpdateProgress(entry);
            }
        }
    }
}
