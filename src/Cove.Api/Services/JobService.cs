using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Cove.Api.Hubs;
using Cove.Core.Auth;
using Cove.Core.Events;
using Cove.Core.Interfaces;

namespace Cove.Api.Services;

public class JobService : IJobService, IHostedService
{
    private readonly List<JobEntry> _exclusiveQueue = [];
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly Dictionary<string, JobEntry> _jobs = [];
    private readonly List<JobHistoryEntry> _history = [];
    private readonly Lock _lock = new();
    private readonly IEventBus _eventBus;
    private readonly IHubContext<JobHub> _hubContext;
    private readonly ILogger<JobService> _logger;
    private readonly ICurrentPrincipalAccessor? _principalAccessor;
    private Task? _processorTask;
    private CancellationTokenSource? _cts;
    private const int MaxHistory = 50;

    public JobService(
        IEventBus eventBus,
        IHubContext<JobHub> hubContext,
        ILogger<JobService> logger,
        ICurrentPrincipalAccessor? principalAccessor = null)
    {
        _eventBus = eventBus;
        _hubContext = hubContext;
        _logger = logger;
        _principalAccessor = principalAccessor;
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
        => EnqueueCore(JobOwner.FromPrincipal(_principalAccessor?.Current), type, description, work, null, exclusive);

    public string EnqueueWithResult(
        string type,
        string description,
        Func<IJobProgress, CancellationToken, Task> work,
        string resultUrl,
        bool exclusive = true)
        => EnqueueCore(JobOwner.FromPrincipal(_principalAccessor?.Current), type, description, work, resultUrl, exclusive);

    public string EnqueueOwned(
        JobOwner owner,
        string type,
        string description,
        Func<IJobProgress, CancellationToken, Task> work,
        string? resultUrl = null,
        bool exclusive = true)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return EnqueueCore(owner, type, description, work, resultUrl, exclusive);
    }

    private string EnqueueCore(
        JobOwner? owner,
        string type,
        string description,
        Func<IJobProgress, CancellationToken, Task> work,
        string? resultUrl,
        bool exclusive)
    {
        var entry = new JobEntry
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Type = type,
            Description = description,
            Status = JobStatus.Pending,
            Progress = 0,
            StartedAt = DateTime.UtcNow,
            Work = work,
            Owner = owner,
            ResultUrl = resultUrl,
        };

        lock (_lock)
        {
            _jobs[entry.Id] = entry;
            if (exclusive)
                _exclusiveQueue.Add(entry);
        }

        if (exclusive)
            _queueSignal.Release();
        else
        {
            _ = RunConcurrentJobAsync(entry);
        }

        _logger.LogInformation("Job {JobId} enqueued ({Mode}): {Type} - {Description}", entry.Id, exclusive ? "exclusive" : "concurrent", type, description);
        NotifyClients(entry);
        return entry.Id;
    }

    public bool Cancel(string jobId)
        => CancelFor(null, jobId, includeAll: true);

    public bool CancelFor(JobOwner? owner, string jobId, bool includeAll)
    {
        JobEntry? cancelledPending = null;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(jobId, out var entry))
                return false;
            if (!CanAccess(entry.Owner, owner, includeAll))
                return false;

            if (entry.Status == JobStatus.Pending)
            {
                entry.Status = JobStatus.Cancelled;
                entry.CompletedAt = DateTime.UtcNow;
                _exclusiveQueue.Remove(entry);
                cancelledPending = entry;
            }
            else if (entry.Cts != null)
            {
                entry.Cts.Cancel();
                // Cancellation is a request, not a terminal outcome. In-flight deletion units finish
                // their already-started transaction before the callback unwinds, so keep the job active
                // until that work has actually stopped.
                entry.SubTask = "Cancellation requested";
                NotifyClients(entry);
                return true;
            }
        }

        if (cancelledPending != null)
        {
            NotifyClients(cancelledPending);
            MoveToHistory(cancelledPending);
            return true;
        }

        return false;
    }

    public bool ReorderQueued(string jobId, string? beforeJobId)
        => ReorderQueuedFor(null, jobId, beforeJobId, includeAll: true);

    public bool ReorderQueuedFor(JobOwner? owner, string jobId, string? beforeJobId, bool includeAll)
    {
        JobEntry? moved;
        lock (_lock)
        {
            var currentIndex = _exclusiveQueue.FindIndex(job => string.Equals(job.Id, jobId, StringComparison.OrdinalIgnoreCase));
            if (currentIndex < 0 || _exclusiveQueue[currentIndex].Status != JobStatus.Pending)
                return false;
            if (!CanAccess(_exclusiveQueue[currentIndex].Owner, owner, includeAll))
                return false;

            moved = _exclusiveQueue[currentIndex];
            _exclusiveQueue.RemoveAt(currentIndex);

            var targetIndex = string.IsNullOrWhiteSpace(beforeJobId)
                ? -1
                : _exclusiveQueue.FindIndex(job =>
                    string.Equals(job.Id, beforeJobId, StringComparison.OrdinalIgnoreCase)
                    && job.Status == JobStatus.Pending
                    && CanAccess(job.Owner, owner, includeAll));

            if (targetIndex < 0)
                _exclusiveQueue.Add(moved);
            else
                _exclusiveQueue.Insert(targetIndex, moved);
        }

        NotifyClients(moved);
        return true;
    }

    public JobInfo? GetJob(string jobId)
        => GetJobFor(null, jobId, includeAll: true);

    public JobInfo? GetJobFor(JobOwner? owner, string jobId, bool includeAll)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(jobId, out var entry))
                return CanAccess(entry.Owner, owner, includeAll) ? entry.ToInfo() : null;

            var historyEntry = _history.FirstOrDefault(job => string.Equals(job.Info.Id, jobId, StringComparison.OrdinalIgnoreCase));
            return historyEntry is not null && CanAccess(historyEntry.Owner, owner, includeAll)
                ? historyEntry.Info
                : null;
        }
    }

    public IReadOnlyList<JobInfo> GetAllJobs()
        => GetAllJobsFor(null, includeAll: true);

    public IReadOnlyList<JobInfo> GetAllJobsFor(JobOwner? owner, bool includeAll)
    {
        lock (_lock)
        {
            var queuedIds = _exclusiveQueue.Select(job => job.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var running = _jobs.Values
                .Where(job => job.Status == JobStatus.Running || (job.Status == JobStatus.Pending && !queuedIds.Contains(job.Id)))
                .Where(job => CanAccess(job.Owner, owner, includeAll))
                .Select(job => job.ToInfo());
            var queued = _exclusiveQueue
                .Where(job => job.Status == JobStatus.Pending)
                .Where(job => CanAccess(job.Owner, owner, includeAll))
                .Select(job => job.ToInfo());
            return running.Concat(queued).ToList();
        }
    }

    public IReadOnlyList<JobInfo> GetJobHistory()
        => GetJobHistoryFor(null, includeAll: true);

    public IReadOnlyList<JobInfo> GetJobHistoryFor(JobOwner? owner, bool includeAll)
    {
        lock (_lock)
        {
            return _history
                .Where(job => CanAccess(job.Owner, owner, includeAll))
                .Select(job => job.Info)
                .ToList();
        }
    }

    private static bool CanAccess(JobOwner? jobOwner, JobOwner? requestingOwner, bool includeAll)
        => includeAll || (jobOwner is not null && jobOwner == requestingOwner);

    public async Task CancelAllAndWaitAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            JobEntry[] active;
            lock (_lock)
                active = [.. _jobs.Values];

            if (active.Length == 0)
            {
                lock (_lock)
                    _history.Clear();
                return;
            }

            foreach (var entry in active)
                Cancel(entry.Id);

            await Task.WhenAll(active.Select(entry => entry.Completion.Task))
                .WaitAsync(cancellationToken);
        }
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _queueSignal.WaitAsync(ct);

            JobEntry? entry = null;
            lock (_lock)
            {
                while (_exclusiveQueue.Count > 0)
                {
                    var candidate = _exclusiveQueue[0];
                    _exclusiveQueue.RemoveAt(0);
                    if (candidate.Status == JobStatus.Pending)
                    {
                        entry = candidate;
                        entry.Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        entry.Status = JobStatus.Running;
                        entry.StartedAt = DateTime.UtcNow;
                        entry.Eta.Start(entry.StartedAt);
                        break;
                    }
                }
            }

            if (entry == null)
                continue;

            NotifyClients(entry);

            var progress = new JobProgress(entry, this);
            using var logScope = BeginJobLogScope(entry);

            try
            {
                _logger.LogInformation("Job {JobId} started: {Description}", entry.Id, entry.Description);

                await entry.Work(progress, entry.Cts!.Token);

                FinalizeSuccessfulWork(entry);

                LogCompleted(entry, "Exclusive");
            }
            catch (OperationCanceledException) when (entry.Cts?.IsCancellationRequested == true)
            {
                // Cancellation triggered by this job's own token (user cancel or host shutdown). This is a
                // normal, graceful stop — mark the job cancelled rather than letting the exception bubble out
                // of the queue processor (which would tear down the background processor loop / host).
                entry.Status = JobStatus.Cancelled;
                entry.CompletedAt = DateTime.UtcNow;
                LogCancelled(entry, "Exclusive");
            }
            catch (Exception ex)
            {
                entry.Status = JobStatus.Failed;
                entry.Error = ex.Message;
                entry.CompletedAt = DateTime.UtcNow;
                LogFailed(entry, "Exclusive", ex);
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
        entry.Eta.Start(entry.StartedAt);
        NotifyClients(entry);

        var progress = new JobProgress(entry, this);
        using var logScope = BeginJobLogScope(entry);

        try
        {
            _logger.LogInformation("Concurrent job {JobId} started: {Description}", entry.Id, entry.Description);
            await entry.Work(progress, entry.Cts.Token);
            FinalizeSuccessfulWork(entry);
            LogCompleted(entry, "Concurrent");
        }
        catch (OperationCanceledException) when (entry.Cts?.IsCancellationRequested == true)
        {
            // Graceful cancellation via this job's own token; mark cancelled instead of failing.
            entry.Status = JobStatus.Cancelled;
            entry.CompletedAt = DateTime.UtcNow;
            LogCancelled(entry, "Concurrent");
        }
        catch (Exception ex)
        {
            entry.Status = JobStatus.Failed;
            entry.Error = ex.Message;
            entry.CompletedAt = DateTime.UtcNow;
            LogFailed(entry, "Concurrent", ex);
        }

        NotifyClients(entry);
        MoveToHistory(entry);
    }

    private IDisposable? BeginJobLogScope(JobEntry entry) =>
        _logger.BeginScope(new Dictionary<string, object?>
        {
            ["JobId"] = entry.Id,
            ["JobType"] = entry.Type,
        });

    private void LogCompleted(JobEntry entry, string mode)
    {
        if (!HasUnitOutcome(entry))
        {
            _logger.LogInformation(
                "{Mode} job {JobId} completed with status {Status} in {ElapsedMs} ms",
                mode,
                entry.Id,
                entry.Status,
                GetElapsedMilliseconds(entry));
            return;
        }

        _logger.LogInformation(
            "{Mode} job {JobId} completed with status {Status} in {ElapsedMs} ms; units={UnitsCompleted}/{UnitsTotal}, succeeded={UnitsSucceeded}, failed={UnitsFailed}, skipped={UnitsSkipped}",
            mode,
            entry.Id,
            entry.Status,
            GetElapsedMilliseconds(entry),
            entry.UnitsCompleted.GetValueOrDefault(),
            entry.UnitsTotal.GetValueOrDefault(),
            entry.UnitsSucceeded.GetValueOrDefault(),
            entry.UnitsFailed.GetValueOrDefault(),
            entry.UnitsSkipped.GetValueOrDefault());
    }

    private void LogCancelled(JobEntry entry, string mode)
    {
        if (!HasUnitOutcome(entry))
        {
            _logger.LogInformation(
                "{Mode} job {JobId} cancelled after {ElapsedMs} ms",
                mode,
                entry.Id,
                GetElapsedMilliseconds(entry));
            return;
        }

        _logger.LogInformation(
            "{Mode} job {JobId} cancelled after {ElapsedMs} ms; units={UnitsCompleted}/{UnitsTotal}, succeeded={UnitsSucceeded}, failed={UnitsFailed}, skipped={UnitsSkipped}",
            mode,
            entry.Id,
            GetElapsedMilliseconds(entry),
            entry.UnitsCompleted.GetValueOrDefault(),
            entry.UnitsTotal.GetValueOrDefault(),
            entry.UnitsSucceeded.GetValueOrDefault(),
            entry.UnitsFailed.GetValueOrDefault(),
            entry.UnitsSkipped.GetValueOrDefault());
    }

    private void LogFailed(JobEntry entry, string mode, Exception exception)
    {
        if (!HasUnitOutcome(entry))
        {
            _logger.LogError(
                exception,
                "{Mode} job {JobId} failed after {ElapsedMs} ms",
                mode,
                entry.Id,
                GetElapsedMilliseconds(entry));
            return;
        }

        _logger.LogError(
            exception,
            "{Mode} job {JobId} failed after {ElapsedMs} ms; units={UnitsCompleted}/{UnitsTotal}, succeeded={UnitsSucceeded}, failed={UnitsFailed}, skipped={UnitsSkipped}",
            mode,
            entry.Id,
            GetElapsedMilliseconds(entry),
            entry.UnitsCompleted.GetValueOrDefault(),
            entry.UnitsTotal.GetValueOrDefault(),
            entry.UnitsSucceeded.GetValueOrDefault(),
            entry.UnitsFailed.GetValueOrDefault(),
            entry.UnitsSkipped.GetValueOrDefault());
    }

    private static bool HasUnitOutcome(JobEntry entry) =>
        entry.UnitsTotal.HasValue
        || entry.UnitsCompleted.HasValue
        || entry.UnitsSucceeded.HasValue
        || entry.UnitsFailed.HasValue
        || entry.UnitsSkipped.HasValue;

    private static long GetElapsedMilliseconds(JobEntry entry)
    {
        var completedAt = entry.CompletedAt ?? DateTime.UtcNow;
        return Math.Max(0L, (long)(completedAt - entry.StartedAt).TotalMilliseconds);
    }

    private void MoveToHistory(JobEntry entry)
    {
        lock (_lock)
        {
            _jobs.Remove(entry.Id);
            _history.Insert(0, new JobHistoryEntry(entry.Owner, entry.ToInfo()));
            if (_history.Count > MaxHistory)
                _history.RemoveRange(MaxHistory, _history.Count - MaxHistory);
        }
        entry.Completion.TrySetResult();
    }

    internal void UpdateProgress(JobEntry entry)
    {
        NotifyClients(entry);
    }

    private static void RefreshUnitProgress(JobEntry entry)
    {
        var unitsTotal = entry.UnitsTotal.GetValueOrDefault();
        if (unitsTotal == 0)
            return;

        entry.UnitsCompleted = entry.UnitsSucceeded.GetValueOrDefault() + entry.UnitsFailed.GetValueOrDefault() + entry.UnitsSkipped.GetValueOrDefault();
        entry.Progress = Math.Clamp(entry.UnitProgressTotal / unitsTotal, 0d, 1d);
        // ETA for unit-based jobs is driven by per-item completion durations (see ObserveUnitCompletion),
        // not the aggregate progress fraction, so a burst of concurrent/no-op completions can't yank it.
        entry.Summary = entry.UnitsCompleted < entry.UnitsTotal
            ? null
            : entry.UnitsFailed > 0 || entry.UnitsSkipped > 0
                ? $"{entry.UnitsSucceeded} succeeded, {entry.UnitsFailed} failed, {entry.UnitsSkipped} skipped"
                : $"{entry.UnitsSucceeded} of {entry.UnitsTotal} units succeeded";
    }

    private static void FinalizeSuccessfulWork(JobEntry entry)
    {
        entry.Status = entry.Cts?.IsCancellationRequested == true ? JobStatus.Cancelled : JobStatus.Completed;
        if (entry.Status == JobStatus.Completed)
            entry.Progress = 1.0;
        if (!string.IsNullOrWhiteSpace(entry.Summary))
            entry.SubTask = entry.Summary;
        entry.CompletedAt = DateTime.UtcNow;
    }

    private void NotifyClients(JobEntry entry)
    {
        var info = entry.ToInfo();
        var groups = entry.Owner is null
            ? new[] { JobHub.GlobalGroup }
            : new[] { JobHub.GlobalGroup, JobHub.OwnerGroup(entry.Owner) };
        _ = _hubContext.Clients.Groups(groups).SendAsync("JobUpdated", info);
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
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Dictionary<string, JobUnitState> Units { get; } = new(StringComparer.Ordinal);
        internal JobEtaEstimator Eta { get; } = new();
        internal double UnitProgressTotal { get; set; }
        public int? UnitsTotal { get; set; }
        public int? UnitsCompleted { get; set; }
        public int? UnitsSucceeded { get; set; }
        public int? UnitsFailed { get; set; }
        public int? UnitsSkipped { get; set; }
        public string? Summary { get; set; }
        public JobOwner? Owner { get; set; }
        public string? ResultUrl { get; set; }
        internal bool AggregateUnitMode { get; set; }

        public JobInfo ToInfo()
        {
            // Compute the ETA and its reference timestamp at the same instant so clients can count the
            // ETA down from UpdatedAt without drift.
            var now = DateTime.UtcNow;
            var running = Status == JobStatus.Running;
            return new(
                Id,
                Type,
                Description,
                Status,
                Progress,
                SubTask,
                StartedAt,
                CompletedAt,
                Error,
                UnitsTotal,
                UnitsCompleted,
                UnitsSucceeded,
                UnitsFailed,
                UnitsSkipped,
                Summary,
                running ? Eta.EstimateSeconds(Progress, UnitsTotal, UnitsCompleted, now) : null,
                running ? now : null,
                ResultUrl);
        }
    }

    private sealed record JobHistoryEntry(JobOwner? Owner, JobInfo Info);

    /// <summary>
    /// Estimates time-to-completion for a job.
    ///
    /// For unit-based jobs the estimate is driven by the measured wall-clock <em>duration of each
    /// completed item</em>, not by the aggregate progress fraction. This matters because items complete
    /// out of order and in bursts (concurrency), and many are effectively no-ops — e.g. entities that
    /// already had AI run on them or were already scanned, which finish near-instantly. Sampling the
    /// progress bar over a fixed time window (the old approach) made the pace estimate lurch whenever a
    /// window happened to catch a burst of slow real items right after a run of cheap no-ops, producing
    /// the wild ETA swings (5h, then 12h, on a job that was really ~2.5h out).
    ///
    /// Instead we:
    ///   * Classify each completed item as a no-op or real work by comparing its duration to a running
    ///     estimate of the typical real-item duration (a no-op is one that finishes in well under that).
    ///   * Measure real-work <em>throughput</em> (real items completed per wall-second) over a stable
    ///     window of recent real completions. Because it counts real completions over wall time, it is
    ///     inherently concurrency-correct and unaffected by no-op bursts.
    ///   * Project how many of the remaining items will be real using the no-op fraction observed so far,
    ///     and divide remaining-real-work by the throughput.
    /// This yields a steady estimate that matches the cumulative pace (the right answer for the reported
    /// case) while still adapting to genuine pace changes over the window.
    /// </summary>
    internal sealed class JobEtaEstimator
    {
        private const double WarmupSeconds = 4.0;
        private const double MaxEtaSeconds = 14d * 24 * 3600;

        // An item counts as a no-op when its duration is below this fraction of the typical real-item
        // duration. No-ops are excluded from the pace estimate but still counted for the no-op fraction.
        private const double NoOpDurationRatio = 0.25;
        // EWMA weight for the running typical-real-duration used only for classification.
        private const double RealDurationAlpha = 0.15;
        // Number of recent real completions kept to measure throughput. Large enough to be stable across
        // bursts, small enough to adapt over the life of a long job.
        private const int ThroughputWindow = 200;
        private const int MinRealSamples = 3;

        private readonly object _gate = new();
        // Timestamps of recent real-item completions, oldest-first, capped at ThroughputWindow.
        private readonly Queue<DateTime> _recentRealCompletions = new();
        private DateTime _startUtc;
        private long _completedCount;
        private long _realCount;
        private double _typicalRealDurationSeconds = -1d; // -1 = no real sample yet

        // --- Legacy fraction-based path, for jobs that report a raw progress fraction with no units. ---
        private const double TauSeconds = 30.0;
        private const double MinSampleSeconds = 1.0;
        private DateTime _lastSampleUtc;
        private double _lastProgress;
        private double _smoothedSecondsPerProgress = -1d;

        public void Start(DateTime nowUtc)
        {
            lock (_gate)
            {
                _startUtc = nowUtc;
                _completedCount = 0;
                _realCount = 0;
                _typicalRealDurationSeconds = -1d;
                _recentRealCompletions.Clear();

                _lastSampleUtc = nowUtc;
                _lastProgress = 0d;
                _smoothedSecondsPerProgress = -1d;
            }
        }

        /// <summary>Record that a single unit finished, taking <paramref name="durationSeconds"/> of wall time.</summary>
        public void ObserveUnitCompletion(double durationSeconds, DateTime nowUtc)
        {
            if (durationSeconds < 0d || double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds))
                durationSeconds = 0d;

            lock (_gate)
            {
                _completedCount++;

                // Bootstrap on the first item; thereafter an item is "real" if it took at least a small
                // fraction of the typical real-item duration. This self-corrects even if the first item
                // happens to be a no-op, because a subsequent real item is far above the tiny threshold.
                var isReal = _typicalRealDurationSeconds <= 0d
                    || durationSeconds >= NoOpDurationRatio * _typicalRealDurationSeconds;

                if (!isReal)
                    return;

                _realCount++;
                _typicalRealDurationSeconds = _typicalRealDurationSeconds <= 0d
                    ? durationSeconds
                    : _typicalRealDurationSeconds + RealDurationAlpha * (durationSeconds - _typicalRealDurationSeconds);

                _recentRealCompletions.Enqueue(nowUtc);
                while (_recentRealCompletions.Count > ThroughputWindow)
                    _recentRealCompletions.Dequeue();
            }
        }

        /// <summary>Legacy path: feed a raw aggregate progress fraction (jobs without discrete units).</summary>
        public void ObserveProgressFraction(double progress, DateTime nowUtc)
        {
            progress = Math.Clamp(progress, 0d, 1d);
            lock (_gate)
            {
                var dt = (nowUtc - _lastSampleUtc).TotalSeconds;
                if (dt < MinSampleSeconds)
                    return;

                var dp = progress - _lastProgress;
                if (dp < 0d)
                {
                    _lastSampleUtc = nowUtc;
                    _lastProgress = progress;
                    return;
                }

                if (dp <= 1e-9d)
                    return;

                var instantSecondsPerProgress = dt / dp;
                var alpha = 1d - Math.Exp(-dt / TauSeconds);
                _smoothedSecondsPerProgress = _smoothedSecondsPerProgress < 0d
                    ? instantSecondsPerProgress
                    : _smoothedSecondsPerProgress + alpha * (instantSecondsPerProgress - _smoothedSecondsPerProgress);

                _lastSampleUtc = nowUtc;
                _lastProgress = progress;
            }
        }

        public double? EstimateSeconds(double progress, int? unitsTotal, int? unitsCompleted, DateTime nowUtc)
        {
            progress = Math.Clamp(progress, 0d, 1d);
            if (progress >= 1d)
                return 0d;

            var elapsed = (nowUtc - _startUtc).TotalSeconds;
            if (elapsed < WarmupSeconds)
                return null;

            lock (_gate)
            {
                // Prefer the unit-duration model when we have discrete units and enough real samples.
                if (unitsTotal is int total && total > 0 && unitsCompleted is int completed && _realCount >= MinRealSamples)
                {
                    var remaining = total - completed;
                    if (remaining <= 0)
                        return 0d;

                    // Real-work throughput over the recent window: real completions per wall-second, up to
                    // now (a stall extends the window span and so lowers throughput, raising the ETA).
                    var windowCount = _recentRealCompletions.Count;
                    double realThroughput;
                    if (windowCount >= 2)
                    {
                        var oldest = _recentRealCompletions.Peek();
                        var span = (nowUtc - oldest).TotalSeconds;
                        realThroughput = span > 0d ? windowCount / span : 0d;
                    }
                    else
                    {
                        realThroughput = elapsed > 0d ? _realCount / elapsed : 0d;
                    }

                    if (realThroughput > 0d)
                    {
                        // Project the share of remaining items that will be real work from the share seen so far.
                        var realFraction = _completedCount > 0 ? (double)_realCount / _completedCount : 1d;
                        realFraction = Math.Clamp(realFraction, 0d, 1d);
                        var remainingReal = remaining * realFraction;
                        var eta = remainingReal / realThroughput;
                        if (!double.IsNaN(eta) && !double.IsInfinity(eta) && eta >= 0d)
                            return Math.Min(eta, MaxEtaSeconds);
                    }
                }

                // Fallback: the smoothed seconds-per-progress (legacy path), else the cumulative average.
                var overallSecondsPerProgress = progress > 0d ? elapsed / progress : -1d;
                var secondsPerProgress = _smoothedSecondsPerProgress >= 0d ? _smoothedSecondsPerProgress : overallSecondsPerProgress;
                if (secondsPerProgress < 0d)
                    return null;

                var fallbackEta = (1d - progress) * secondsPerProgress;
                if (double.IsNaN(fallbackEta) || double.IsInfinity(fallbackEta) || fallbackEta < 0d)
                    return null;

                return Math.Min(fallbackEta, MaxEtaSeconds);
            }
        }
    }

    private class JobProgress(JobEntry entry, JobService svc) : IJobProgress
    {
        private readonly object _notifyLock = new();
        private DateTime _lastReport = DateTime.MinValue;

        public void Report(double progress, string? subTask = null)
        {
            lock (svc._lock)
            {
                if (!entry.UnitsTotal.HasValue || entry.UnitsCompleted.GetValueOrDefault() >= entry.UnitsTotal.GetValueOrDefault())
                {
                    entry.Progress = Math.Clamp(progress, 0, 1);
                    entry.Eta.ObserveProgressFraction(entry.Progress, DateTime.UtcNow);
                }

                entry.SubTask = subTask;
            }

            MaybeNotify();
        }

        public void SetSummary(string summary)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(summary);
            lock (svc._lock)
            {
                entry.Summary = summary;
                entry.SubTask = summary;
                entry.Progress = 1d;
            }
            svc.UpdateProgress(entry);
        }

        public IJobUnit StartUnit(string unitId, string? label = null)
        {
            lock (svc._lock)
            {
                if (!entry.Units.TryGetValue(unitId, out var state))
                {
                    state = new JobUnitState
                    {
                        UnitId = unitId,
                        Label = label,
                        StartedAt = DateTime.UtcNow,
                    };
                    entry.Units[unitId] = state;
                    if (!entry.AggregateUnitMode)
                        entry.UnitsTotal = entry.UnitsTotal.GetValueOrDefault() + 1;
                    entry.UnitsSucceeded ??= 0;
                    entry.UnitsFailed ??= 0;
                    entry.UnitsSkipped ??= 0;
                    RefreshUnitProgress(entry);
                }

                state.StartedAt = DateTime.UtcNow;

                if (!string.IsNullOrWhiteSpace(label))
                    state.Label = label;
            }

            MaybeNotify();
            return new JobUnit(entry, unitId, svc, MaybeNotify);
        }

        public void DeclareUnits(IEnumerable<(string UnitId, string? Label)> units)
        {
            lock (svc._lock)
            {
                foreach (var (unitId, label) in units)
                {
                    if (!entry.Units.ContainsKey(unitId))
                    {
                        entry.Units[unitId] = new JobUnitState { UnitId = unitId, Label = label };
                        entry.UnitsTotal = entry.UnitsTotal.GetValueOrDefault() + 1;
                    }
                }
                entry.UnitsSucceeded ??= 0;
                entry.UnitsFailed ??= 0;
                entry.UnitsSkipped ??= 0;
                RefreshUnitProgress(entry);
            }
            svc.UpdateProgress(entry);
        }

        public void DeclareUnitCount(int totalUnits)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(totalUnits);
            lock (svc._lock)
            {
                if (entry.Units.Count > 0 || entry.UnitsCompleted.GetValueOrDefault() > 0)
                    throw new InvalidOperationException("Unit totals must be declared before units start.");

                entry.AggregateUnitMode = true;
                entry.UnitsTotal = totalUnits;
                entry.UnitsCompleted = 0;
                entry.UnitsSucceeded = 0;
                entry.UnitsFailed = 0;
                entry.UnitsSkipped = 0;
                RefreshUnitProgress(entry);
            }
            svc.UpdateProgress(entry);
        }

        private void MaybeNotify()
        {
            // Throttle SignalR updates to max 10/sec
            var shouldNotify = false;
            lock (_notifyLock)
            {
                var now = DateTime.UtcNow;
                if ((now - _lastReport).TotalMilliseconds >= 100)
                {
                    _lastReport = now;
                    shouldNotify = true;
                }
            }
            if (shouldNotify)
                svc.UpdateProgress(entry);
        }
    }

    private sealed class JobUnit(JobEntry entry, string unitId, JobService svc, Action notify) : IJobUnit
    {
        private JobUnitOutcome? _outcome;

        public JobUnitOutcome? Outcome
        {
            get
            {
                lock (svc._lock)
                {
                    return entry.Units.TryGetValue(unitId, out var state) ? state.Outcome : _outcome;
                }
            }
        }

        public void Report(double progress, string? message = null)
        {
            lock (svc._lock)
            {
                if (!entry.Units.TryGetValue(unitId, out var state) || state.IsCompleted)
                    return;

                var nextProgress = Math.Clamp(progress, 0d, 1d);
                entry.UnitProgressTotal += nextProgress - state.Progress;
                state.Progress = nextProgress;
                if (!string.IsNullOrWhiteSpace(message))
                {
                    state.Message = message;
                    entry.SubTask = message;
                }

                RefreshUnitProgress(entry);
            }

            notify();
        }

        public void Complete(JobUnitOutcome outcome, string? message = null)
        {
            lock (svc._lock)
            {
                if (!entry.Units.TryGetValue(unitId, out var state) || state.IsCompleted)
                    return;

                var completedAt = DateTime.UtcNow;
                entry.UnitProgressTotal += 1d - state.Progress;
                state.IsCompleted = true;
                state.Progress = 1d;
                state.Outcome = outcome;
                _outcome = outcome;
                state.Message = message ?? state.Message ?? state.Label;

                if (!state.DurationObserved)
                {
                    state.DurationObserved = true;
                    var durationSeconds = Math.Max(0d, (completedAt - state.StartedAt).TotalSeconds);
                    entry.Eta.ObserveUnitCompletion(durationSeconds, completedAt);
                }

                switch (outcome)
                {
                    case JobUnitOutcome.Succeeded: entry.UnitsSucceeded = entry.UnitsSucceeded.GetValueOrDefault() + 1; break;
                    case JobUnitOutcome.Failed: entry.UnitsFailed = entry.UnitsFailed.GetValueOrDefault() + 1; break;
                    case JobUnitOutcome.Skipped: entry.UnitsSkipped = entry.UnitsSkipped.GetValueOrDefault() + 1; break;
                }
                RefreshUnitProgress(entry);

                if (!string.IsNullOrWhiteSpace(message))
                    entry.SubTask = message;
                else if (!string.IsNullOrWhiteSpace(entry.Summary) && entry.UnitsCompleted.GetValueOrDefault() == entry.UnitsTotal.GetValueOrDefault())
                    entry.SubTask = entry.Summary;
            }

            notify();
        }

        public void Dispose()
        {
            lock (svc._lock)
            {
                if (entry.AggregateUnitMode
                    && entry.Units.TryGetValue(unitId, out var state)
                    && state.IsCompleted)
                {
                    entry.Units.Remove(unitId);
                }
            }
        }
    }

    internal sealed class JobUnitState
    {
        public required string UnitId { get; init; }
        public string? Label { get; set; }
        public string? Message { get; set; }
        public double Progress { get; set; }
        public bool IsCompleted { get; set; }
        public JobUnitOutcome? Outcome { get; set; }
        // When the unit began processing (set on StartUnit) so the per-item duration can be measured
        // on completion and fed to the ETA estimator. Used to distinguish near-instant no-op items
        // (e.g. already-scanned entities) from real work.
        public DateTime StartedAt { get; set; }
        public bool DurationObserved { get; set; }
    }

    internal int GetTrackedUnitCountForTests(string jobId)
    {
        lock (_lock)
            return _jobs.TryGetValue(jobId, out var entry) ? entry.Units.Count : 0;
    }
}
