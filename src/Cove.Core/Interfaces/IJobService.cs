using System.Collections.Concurrent;
using System.Globalization;
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
    DateTime? UpdatedAt = null);

public record JobBatchResult(
    int TotalUnits,
    int SucceededUnits,
    int FailedUnits,
    int SkippedUnits,
    IReadOnlyList<string> FailedUnitIds)
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
    bool Cancel(string jobId);
    bool ReorderQueued(string jobId, string? beforeJobId);
    JobInfo? GetJob(string jobId);
    IReadOnlyList<JobInfo> GetAllJobs();
    IReadOnlyList<JobInfo> GetJobHistory();

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
            return new JobBatchResult(0, 0, 0, 0, []);
        }

        using var gate = new SemaphoreSlim(Math.Max(1, maxInFlight));
        var failedUnitIds = new ConcurrentBag<string>();
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;

        var tasks = items.Select((item, index) => RunUnitAsync(item, index));
        await Task.WhenAll(tasks);

        return new JobBatchResult(items.Count, succeeded, failed, skipped, failedUnitIds.ToArray());

        async Task RunUnitAsync(T item, int index)
        {
            await gate.WaitAsync(ct);
            try
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
                        failedUnitIds.Add(unitId);
                        Interlocked.Increment(ref failed);
                        break;
                    case JobUnitOutcome.Skipped:
                        Interlocked.Increment(ref skipped);
                        break;
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }
}

public interface IJobProgress
{
    void Report(double progress, string? subTask = null);

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
    string StartClean(bool dryRun = false);
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
