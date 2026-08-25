using Microsoft.AspNetCore.Mvc;
using Cove.Api.Services;
using Cove.Api.Hubs;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class JobsController(
    IJobService jobService,
    IScanService scanService,
    IThumbnailService thumbnailService,
    IFingerprintService fingerprintService,
    ICleanService cleanService,
    IBackupService backupService,
    ICurrentPrincipalAccessor principalAccessor,
    CoveContext db,
    DuplicateSearchJobService duplicateSearchJobService,
    ILogger<JobsController>? logger = null) : ControllerBase
{
    [HttpGet]
    [AllowWithoutPermission]
    public async Task<ActionResult<IReadOnlyList<JobInfo>>> GetJobs(CancellationToken ct)
    {
        var (owner, includeAll) = await GetVisibilityAsync(ct);
        return Ok(jobService.GetAllJobsFor(owner, includeAll));
    }

    [HttpGet("history")]
    [AllowWithoutPermission]
    public async Task<ActionResult<IReadOnlyList<JobInfo>>> GetHistory(CancellationToken ct)
    {
        var (owner, includeAll) = await GetVisibilityAsync(ct);
        return Ok(jobService.GetJobHistoryFor(owner, includeAll));
    }

    [HttpGet("{jobId}")]
    [AllowWithoutPermission]
    public async Task<ActionResult<JobInfo>> GetJob(string jobId, CancellationToken ct)
    {
        var (owner, includeAll) = await GetVisibilityAsync(ct);
        var job = jobService.GetJobFor(owner, jobId, includeAll);
        return job != null ? Ok(job) : NotFound();
    }

    [HttpDelete("{jobId}")]
    [RequiresPermission(Permissions.JobsCancel)]
    public async Task<IActionResult> CancelJob(string jobId, CancellationToken ct)
    {
        var (owner, includeAll) = await GetVisibilityAsync(ct);
        if (!jobService.CancelFor(owner, jobId, includeAll))
            return NotFound();
        var jobAfterCancellation = jobService.GetJobFor(owner, jobId, includeAll);

        try
        {
            var appliedMigrations = await db.Database.GetAppliedMigrationsAsync(CancellationToken.None);
            if (jobAfterCancellation is { Status: JobStatus.Cancelled, CompletedAt: not null }
                && appliedMigrations.Contains(DuplicateSearchDeletionClaim.MigrationId, StringComparer.Ordinal))
            {
                var now = DateTime.UtcNow;
                await db.DuplicateSearches
                    .Where(search => search.JobId == jobId
                        && (search.Status == Cove.Core.Entities.DuplicateSearchStatus.Pending
                            || search.Status == Cove.Core.Entities.DuplicateSearchStatus.Running))
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(search => search.Status, Cove.Core.Entities.DuplicateSearchStatus.Cancelled)
                        .SetProperty(search => search.CompletedAt, now)
                        .SetProperty(search => search.ExpiresAt, now.AddDays(7)), CancellationToken.None);
                await duplicateSearchJobService.ReleaseCancelledPendingDeletionAsync(jobId, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            // The in-memory cancellation already succeeded. Recovery will reconcile a durable search
            // after database availability returns, so do not turn a successful cancel into a 500.
            logger?.LogWarning(ex, "Job {JobId} was cancelled, but its durable duplicate-search status could not be updated.", jobId);
        }
        return Ok();
    }

    [HttpPut("{jobId}/reorder")]
    [RequiresPermission(Permissions.JobsCancel)]
    public async Task<IActionResult> ReorderJob(string jobId, [FromBody] ReorderJobRequest request, CancellationToken ct)
    {
        var (owner, includeAll) = await GetVisibilityAsync(ct);
        return jobService.ReorderQueuedFor(owner, jobId, request.BeforeJobId, includeAll) ? Ok() : NotFound();
    }

    [HttpPost("scan")]
    [RequiresPermission(Permissions.LibraryScan)]
    [RequiresUnscopedEntityAccess("write")]
    public ActionResult<object> StartScan([FromQuery] bool generatePreviews = false)
    {
        var jobId = scanService.StartScan(new ScanOperationOptions
        {
            GeneratePreviews = generatePreviews,
        });
        return Accepted(new { jobId });
    }

    [HttpPost("generate-thumbnails")]
    [RequiresPermission(Permissions.LibraryScan)]
    [RequiresUnscopedEntityAccess("write")]
    public ActionResult<object> GenerateThumbnails()
    {
        var jobId = thumbnailService.StartGenerateAllThumbnails();
        return Accepted(new { jobId });
    }

    [HttpPost("generate-video-phashes")]
    [RequiresPermission(Permissions.LibraryScan)]
    [RequiresUnscopedEntityAccess("write")]
    public ActionResult<object> GenerateVideoPhashes()
    {
        var jobId = fingerprintService.StartGenerateVideoPhashes();
        return Accepted(new { jobId });
    }

    [HttpPost("generate-image-phashes")]
    [RequiresPermission(Permissions.LibraryScan)]
    [RequiresUnscopedEntityAccess("write")]
    public ActionResult<object> GenerateImagePhashes()
    {
        var jobId = fingerprintService.StartGenerateImagePhashes();
        return Accepted(new { jobId });
    }

    [HttpPost("clean")]
    [RequiresPermission(Permissions.LibraryClean)]
    [RequiresUnscopedEntityAccess("delete")]
    public ActionResult<object> StartClean([FromQuery] bool dryRun = false)
    {
        var jobId = cleanService.StartClean(dryRun);
        return Accepted(new { jobId });
    }

    [HttpPost("backup")]
    [RequiresPermission(Permissions.SystemBackup)]
    [RequiresUnscopedEntityAccess("read")]
    public ActionResult<object> StartBackup()
    {
        var jobId = backupService.StartBackup();
        return Accepted(new { jobId });
    }

    [HttpGet("backup/latest")]
    [RequiresPermission(Permissions.SystemBackup)]
    [RequiresUnscopedEntityAccess("read")]
    public async Task<ActionResult<object>> GetLatestBackup()
    {
        var path = await backupService.GetLatestBackupPathAsync();
        return path != null ? Ok(new { path }) : NotFound();
    }

    private async Task<(JobOwner? Owner, bool IncludeAll)> GetVisibilityAsync(CancellationToken ct)
    {
        var principal = principalAccessor.Current;
        var includeAll = await JobHub.CanReadGlobalStreamAsync(principal, Permissions.JobsRead, db, ct);
        return (JobOwner.FromPrincipal(principal), includeAll);
    }
}

public class ReorderJobRequest
{
    public string? BeforeJobId { get; set; }
}
