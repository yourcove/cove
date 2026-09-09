using Cove.Core.Interfaces;
using Cove.Data.Services;

namespace Cove.Api.Services;

/// <summary>
/// Coalesces settings changes into one exclusive JSON index reconciliation job.
/// </summary>
public sealed class CustomFieldJsonIndexJobService
{
    public const string JobType = "custom_field_json_indexes";
    private readonly IJobService _jobs;
    private readonly Func<Action<double, string?>?, CancellationToken, Task<CustomFieldJsonIndexReconcileResult>> _reconcile;
    private readonly ILogger<CustomFieldJsonIndexJobService> _logger;
    private readonly Lock _lock = new();
    private long _requestVersion;
    private long _scheduleGeneration;
    private bool _scheduled;
    private string? _jobId;

    public CustomFieldJsonIndexJobService(
        IJobService jobs,
        CustomFieldJsonIndexReconciler reconciler,
        ILogger<CustomFieldJsonIndexJobService> logger)
        : this(jobs, reconciler.ReconcileAsync, logger)
    {
    }

    internal CustomFieldJsonIndexJobService(
        IJobService jobs,
        Func<Action<double, string?>?, CancellationToken, Task<CustomFieldJsonIndexReconcileResult>> reconcile,
        ILogger<CustomFieldJsonIndexJobService> logger)
    {
        _jobs = jobs;
        _reconcile = reconcile;
        _logger = logger;
    }

    public void RequestReconcile()
    {
        long generation;
        long scheduledVersion;
        lock (_lock)
        {
            _requestVersion++;
            if (_scheduled)
            {
                // A pending exclusive job can be cancelled without its callback ever running. Do
                // not let that terminal job leave this coordinator permanently marked as scheduled.
                if (_jobId == null || _jobs.GetJob(_jobId)?.Status is JobStatus.Pending or JobStatus.Running)
                    return;

                _scheduled = false;
                _jobId = null;
            }

            _scheduled = true;
            generation = ++_scheduleGeneration;
            scheduledVersion = _requestVersion;
        }

        EnqueueMarkedJob(generation, scheduledVersion);
    }

    private void EnqueueMarkedJob(long generation, long scheduledVersion)
    {
        try
        {
            var jobId = _jobs.Enqueue(
                JobType,
                "Reconcile JSON custom-field indexes",
                (progress, cancellationToken) => ExecuteAsync(generation, scheduledVersion, progress, cancellationToken),
                exclusive: true);

            lock (_lock)
            {
                if (_scheduled && _scheduleGeneration == generation)
                    _jobId = jobId;
            }
        }
        catch (Exception exception)
        {
            lock (_lock)
            {
                if (_scheduled && _scheduleGeneration == generation)
                {
                    _scheduled = false;
                    _jobId = null;
                }
            }
            _logger.LogError(exception, "Could not enqueue JSON custom-field index reconciliation");
        }
    }

    private async Task ExecuteAsync(
        long generation,
        long scheduledVersion,
        IJobProgress progress,
        CancellationToken cancellationToken)
    {
        var attemptedVersion = scheduledVersion;
        var enqueueFollowUp = false;
        var followUpGeneration = 0L;
        var followUpVersion = 0L;
        var completedNormally = false;
        try
        {
            while (true)
            {
                long targetVersion;
                lock (_lock)
                {
                    if (!_scheduled || _scheduleGeneration != generation)
                        return;
                    targetVersion = _requestVersion;
                }
                progress.Report(0, "Reconciling configured JSON paths");
                var result = await _reconcile(progress.Report, cancellationToken);
                attemptedVersion = targetVersion;

                var rerun = false;
                lock (_lock)
                {
                    if (!_scheduled || _scheduleGeneration != generation)
                        return;

                    if (_requestVersion != targetVersion)
                        rerun = true;
                    else
                    {
                        _scheduled = false;
                        _jobId = null;
                        completedNormally = true;
                    }
                }

                if (rerun)
                    continue;

                progress.SetSummary(result.Summary);
                return;
            }
        }
        finally
        {
            if (!completedNormally)
            {
                lock (_lock)
                {
                    if (_scheduled && _scheduleGeneration == generation)
                    {
                        _scheduled = false;
                        _jobId = null;
                        if (_requestVersion > attemptedVersion)
                        {
                            _scheduled = true;
                            followUpGeneration = ++_scheduleGeneration;
                            followUpVersion = _requestVersion;
                            enqueueFollowUp = true;
                        }
                    }
                }
            }

            if (enqueueFollowUp)
                EnqueueMarkedJob(followUpGeneration, followUpVersion);
        }
    }
}
