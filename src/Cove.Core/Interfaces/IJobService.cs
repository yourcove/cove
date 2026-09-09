using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Events;

namespace Cove.Core.Interfaces;

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

public enum JobUnitOutcome
{
    Succeeded,
    Failed,
    Skipped,
}

public record JobInfo(
    string Id,
    string Type,
    string Description,
    JobStatus Status,
    double Progress,
    string? SubTask,
    DateTime StartedAt,
    DateTime? CompletedAt,
    string? Error,
    int? UnitsTotal = null,
    int? UnitsCompleted = null,
    int? UnitsSucceeded = null,
    int? UnitsFailed = null,
    int? UnitsSkipped = null,
    string? Summary = null,
    // Server-computed estimate of seconds remaining (null when not yet known / stalled / not running),
    // and the UTC timestamp it was computed at so clients can count it down smoothly between updates.
    double? EtaSeconds = null,
    DateTime? UpdatedAt = null,
    string? ResultUrl = null);

public sealed record JobOwner(string Key)
{
    public static JobOwner? FromPrincipal(CovePrincipal? principal)
    {
        if (principal is null || principal.Kind is PrincipalKind.Anonymous or PrincipalKind.System)
            return null;

        if (principal.UserId is int userId)
            return new($"user:{userId.ToString(CultureInfo.InvariantCulture)}");

        return principal.TokenId is Guid tokenId
            ? new($"token:{tokenId:N}")
            : null;
    }
}

public record JobBatchResult(
    int TotalUnits,
    int SucceededUnits,
    int FailedUnits,
    int SkippedUnits,
    IReadOnlyList<string> FailedUnitIds,
    IReadOnlyList<string> SkippedUnitIds)
{
    public int CompletedUnits => SucceededUnits + FailedUnits + SkippedUnits;

    public string Summary => FailedUnits > 0 || SkippedUnits > 0
        ? $"{SucceededUnits} succeeded, {FailedUnits} failed, {SkippedUnits} skipped"
        : $"{SucceededUnits} of {TotalUnits} units succeeded";
}

public interface IJobService
{
    /// <summary>
    /// Enqueue a job. Exclusive jobs (default) run sequentially through the queue.
    /// Non-exclusive jobs run immediately as concurrent background tasks.
    /// The work callback can outlive its originating request: do not capture scoped services or
    /// HttpContext. Create a fresh scope inside the callback and honor its cancellation token.
    /// </summary>
    string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true);
    string EnqueueWithResult(
        string type,
        string description,
        Func<IJobProgress, CancellationToken, Task> work,
        string resultUrl,
        bool exclusive = true)
        => Enqueue(type, description, work, exclusive);
    string EnqueueOwned(
        JobOwner owner,
        string type,
        string description,
        Func<IJobProgress, CancellationToken, Task> work,
        string? resultUrl = null,
        bool exclusive = true)
        => Enqueue(type, description, work, exclusive);
    string EnqueueFor(
        JobOwner? owner,
        string type,
        string description,
        Func<IJobProgress, CancellationToken, Task> work,
        string? resultUrl = null,
        bool exclusive = true)
        => owner is null
            ? resultUrl is null
                ? Enqueue(type, description, work, exclusive)
                : EnqueueWithResult(type, description, work, resultUrl, exclusive)
            : EnqueueOwned(owner, type, description, work, resultUrl, exclusive);
    bool Cancel(string jobId);
    bool CancelFor(JobOwner? owner, string jobId, bool includeAll)
        => includeAll && Cancel(jobId);
    bool ReorderQueued(string jobId, string? beforeJobId);
    bool ReorderQueuedFor(JobOwner? owner, string jobId, string? beforeJobId, bool includeAll)
        => includeAll && ReorderQueued(jobId, beforeJobId);
    JobInfo? GetJob(string jobId);
    JobInfo? GetJobFor(JobOwner? owner, string jobId, bool includeAll)
        => includeAll ? GetJob(jobId) : null;
    IReadOnlyList<JobInfo> GetAllJobs();
    IReadOnlyList<JobInfo> GetAllJobsFor(JobOwner? owner, bool includeAll)
        => includeAll ? GetAllJobs() : [];
    IReadOnlyList<JobInfo> GetJobHistory();
    IReadOnlyList<JobInfo> GetJobHistoryFor(JobOwner? owner, bool includeAll)
        => includeAll ? GetJobHistory() : [];

    async Task<JobBatchResult> RunBatchAsync<T>(
        IEnumerable<T> units,
        int maxInFlight,
        Func<T, IJobUnit, CancellationToken, Task> work,
        IJobProgress progress,
        Func<T, int, string>? unitIdFactory = null,
        Func<T, string?>? labelFactory = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(progress);

        var items = units as IReadOnlyList<T> ?? units.ToList();
        if (items.Count == 0)
        {
            progress.Report(1d, "No work items.");
            return new JobBatchResult(0, 0, 0, 0, [], []);
        }

        const int maxReportedFailures = 100;
        var failedUnitIds = new ConcurrentQueue<string>();
        var skippedUnitIds = new ConcurrentQueue<string>();
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;
        var nextIndex = -1;

        var workerCount = Math.Min(items.Count, Math.Max(1, maxInFlight));
        // Schedule every fixed worker before awaiting it. Calling RunWorkerAsync directly here lets a
        // callback that completes synchronously (for example, CPU-bound pHash comparison) drain the
        // entire sequence while the task list is still being enumerated, effectively reducing the
        // configured parallelism to one.
        var tasks = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(RunWorkerAsync, ct))
            .ToArray();
        await Task.WhenAll(tasks);

        return new JobBatchResult(items.Count, succeeded, failed, skipped, failedUnitIds.ToArray(), skippedUnitIds.ToArray());

        async Task RunWorkerAsync()
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var index = Interlocked.Increment(ref nextIndex);
                if (index >= items.Count)
                    return;

                await RunUnitAsync(items[index], index);
            }
        }

        async Task RunUnitAsync(T item, int index)
        {
            var unitId = unitIdFactory?.Invoke(item, index) ?? index.ToString(CultureInfo.InvariantCulture);
            var label = labelFactory?.Invoke(item) ?? item?.ToString();
            using var unit = progress.StartUnit(unitId, label);

            try
            {
                await work(item, unit, ct);
                if (unit.Outcome is null)
                    unit.Complete(JobUnitOutcome.Succeeded);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (unit.Outcome is null)
                    unit.Complete(JobUnitOutcome.Failed, ex.Message);
            }

            switch (unit.Outcome ?? JobUnitOutcome.Succeeded)
            {
                case JobUnitOutcome.Succeeded:
                    Interlocked.Increment(ref succeeded);
                    break;
                case JobUnitOutcome.Failed:
                    var failureNumber = Interlocked.Increment(ref failed);
                    if (failureNumber <= maxReportedFailures)
                        failedUnitIds.Enqueue(unitId);
                    break;
                case JobUnitOutcome.Skipped:
                    var skipNumber = Interlocked.Increment(ref skipped);
                    if (skipNumber <= maxReportedFailures)
                        skippedUnitIds.Enqueue(unitId);
                    break;
            }
        }
    }
}

public interface IJobProgress
{
    void Report(double progress, string? subTask = null);

    void SetSummary(string summary) => Report(1d, summary);

    void DeclareUnitCount(int totalUnits) { }

    void DeclareUnits(IEnumerable<(string UnitId, string? Label)> units) { }

    IJobUnit StartUnit(string unitId, string? label = null) => new NullJobUnit(unitId, label);
}

public interface IJobUnit : IDisposable
{
    JobUnitOutcome? Outcome { get; }

    void Report(double progress, string? message = null);

    void Complete(JobUnitOutcome outcome, string? message = null);
}

public sealed class NullJobUnit(string unitId, string? label = null) : IJobUnit
{
    public string UnitId { get; } = unitId;

    public string? Label { get; } = label;

    public JobUnitOutcome? Outcome { get; private set; }

    public void Report(double progress, string? message = null)
    {
    }

    public void Complete(JobUnitOutcome outcome, string? message = null)
    {
        Outcome ??= outcome;
    }

    public void Dispose()
    {
    }
}

public sealed class ScanOperationOptions
{
    public List<string>? Paths { get; init; }
    /// <summary>
    /// Includes otherwise unchanged discovered files in the requested asset-generation pass.
    /// Intended for narrowly scoped internal workflows whose files were imported before the scan job starts.
    /// </summary>
    public bool IncludeUnchangedFilesInAssetGeneration { get; init; }
    public bool GenerateCovers { get; init; }
    public bool GeneratePreviews { get; init; }
    public bool GenerateSprites { get; init; }
    public bool GeneratePhashes { get; init; }
    public bool GenerateMd5 { get; init; }
    public bool GenerateImageThumbnails { get; init; }
    public bool GenerateImagePhashes { get; init; }
    public bool GenerateAudioPhashes { get; init; }
    public bool GenerateTextPhashes { get; init; }
    public bool Rescan { get; init; }
}

public interface IScanService
{
    string StartScan(ScanOperationOptions? options = null);
    Task<int> ImportDownloadedVideoAsync(string path, int? videoId, CancellationToken ct = default);
    Task<int> ImportDownloadedImageAsync(string path, int? imageId, CancellationToken ct = default);
    Task<int> ImportDownloadedGalleryAsync(string path, int? galleryId, CancellationToken ct = default);
    Task<int> ImportDownloadedAudioAsync(string path, int? audioId, CancellationToken ct = default);
    Task<int> ImportDownloadedTextAsync(string path, int? textDocumentId, CancellationToken ct = default);
}

public interface ICleanService
{
    string StartClean(bool dryRun = false, IReadOnlyList<string>? paths = null);

    // Binary-compatibility shim for extensions compiled against Cove 1.3 and earlier, before
    // `paths` was appended. See the note on IVideoRepository.FindAsync.
    [EditorBrowsable(EditorBrowsableState.Never)]
    string StartClean(bool dryRun) => StartClean(dryRun, null);
}

public interface IBackupService
{
    Task<BackupResultDto> CreateBackupAsync(string? reason = null, CancellationToken ct = default);
    string StartBackup();
    Task RestoreBackupAsync(string backupPath, CancellationToken ct = default);
    Task<string?> GetLatestBackupPathAsync(CancellationToken ct = default);
    Task<ConfigBackupResultDto?> CreateConfigBackupAsync(string? reason = null, CancellationToken ct = default);
    Task RestoreConfigBackupAsync(string backupPath, CancellationToken ct = default);
    Task<string?> GetLatestConfigBackupPathAsync(CancellationToken ct = default);
}

public interface IStreamService
{
    Task<(Stream stream, string contentType, long? fileSize)?> GetVideoStream(int videoId, CancellationToken ct = default);
    Task<(Stream stream, string contentType, bool useLongCache)?> GetVideoScreenshot(int videoId, double? seconds, CancellationToken ct = default);
    Task<(Stream stream, string contentType, bool useLongCache)?> GetSegmentAnimatedPreview(int videoId, double seconds, CancellationToken ct = default);
}
