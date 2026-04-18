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

public record JobInfo(
    string Id,
    string Type,
    string Description,
    JobStatus Status,
    double Progress,
    string? SubTask,
    DateTime StartedAt,
    DateTime? CompletedAt,
    string? Error);

public interface IJobService
{
    /// <summary>
    /// Enqueue a job. Exclusive jobs (default) run sequentially through the queue.
    /// Non-exclusive jobs run immediately as concurrent background tasks.
    /// </summary>
    string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true);
    bool Cancel(string jobId);
    JobInfo? GetJob(string jobId);
    IReadOnlyList<JobInfo> GetAllJobs();
    IReadOnlyList<JobInfo> GetJobHistory();
}

public interface IJobProgress
{
    void Report(double progress, string? subTask = null);
}

public sealed class ScanOperationOptions
{
    public List<string>? Paths { get; init; }
    public bool GenerateCovers { get; init; }
    public bool GeneratePreviews { get; init; }
    public bool GenerateSprites { get; init; }
    public bool GeneratePhashes { get; init; }
    public bool GenerateImageThumbnails { get; init; }
    public bool GenerateImagePhashes { get; init; }
    public bool Rescan { get; init; }
}

public interface IScanService
{
    string StartScan(ScanOperationOptions? options = null);
}

public interface IAutoTagService
{
    string StartAutoTag(IEnumerable<string>? performerIds = null, IEnumerable<string>? studioIds = null, IEnumerable<string>? tagIds = null);
}

public interface ICleanService
{
    string StartClean(bool dryRun = false);
}

public interface IBackupService
{
    string StartBackup();
    Task<string?> GetLatestBackupPathAsync(CancellationToken ct = default);
}

public interface IStreamService
{
    Task<(Stream stream, string contentType, long? fileSize)?> GetSceneStream(int sceneId, CancellationToken ct = default);
    Task<(Stream stream, string contentType)?> GetSceneScreenshot(int sceneId, double? seconds, CancellationToken ct = default);
}
