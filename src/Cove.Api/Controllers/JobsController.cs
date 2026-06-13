using Microsoft.AspNetCore.Mvc;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Interfaces;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[RequiresPermission(Permissions.JobsRead)]
public class JobsController(IJobService jobService, IScanService scanService, IThumbnailService thumbnailService, IFingerprintService fingerprintService, IAutoTagService autoTagService, ICleanService cleanService, IBackupService backupService) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<JobInfo>> GetJobs()
        => Ok(jobService.GetAllJobs());

    [HttpGet("history")]
    public ActionResult<IReadOnlyList<JobInfo>> GetHistory()
        => Ok(jobService.GetJobHistory());

    [HttpGet("{jobId}")]
    public ActionResult<JobInfo> GetJob(string jobId)
    {
        var job = jobService.GetJob(jobId);
        return job != null ? Ok(job) : NotFound();
    }

    [HttpDelete("{jobId}")]
    [RequiresPermission(Permissions.JobsCancel)]
    public IActionResult CancelJob(string jobId)
    {
        return jobService.Cancel(jobId) ? Ok() : NotFound();
    }

    [HttpPut("{jobId}/reorder")]
    [RequiresPermission(Permissions.JobsCancel)]
    public IActionResult ReorderJob(string jobId, [FromBody] ReorderJobRequest request)
    {
        return jobService.ReorderQueued(jobId, request.BeforeJobId) ? Ok() : NotFound();
    }

    [HttpPost("scan")]
    [RequiresPermission(Permissions.LibraryScan)]
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
    public ActionResult<object> GenerateThumbnails()
    {
        var jobId = thumbnailService.StartGenerateAllThumbnails();
        return Accepted(new { jobId });
    }

    [HttpPost("generate-video-phashes")]
    [RequiresPermission(Permissions.LibraryScan)]
    public ActionResult<object> GenerateVideoPhashes()
    {
        var jobId = fingerprintService.StartGenerateVideoPhashes();
        return Accepted(new { jobId });
    }

    [HttpPost("generate-image-phashes")]
    [RequiresPermission(Permissions.LibraryScan)]
    public ActionResult<object> GenerateImagePhashes()
    {
        var jobId = fingerprintService.StartGenerateImagePhashes();
        return Accepted(new { jobId });
    }

    [HttpPost("auto-tag")]
    [RequiresPermission(Permissions.LibraryAutoTag)]
    public ActionResult<object> StartAutoTag([FromBody] AutoTagRequest? request = null)
    {
        var jobId = autoTagService.StartAutoTag(request?.PerformerIds, request?.StudioIds, request?.TagIds);
        return Accepted(new { jobId });
    }

    [HttpPost("clean")]
    [RequiresPermission(Permissions.LibraryClean)]
    public ActionResult<object> StartClean([FromQuery] bool dryRun = false)
    {
        var jobId = cleanService.StartClean(dryRun);
        return Accepted(new { jobId });
    }

    [HttpPost("backup")]
    [RequiresPermission(Permissions.SystemBackup)]
    public ActionResult<object> StartBackup()
    {
        var jobId = backupService.StartBackup();
        return Accepted(new { jobId });
    }

    [HttpGet("backup/latest")]
    [RequiresPermission(Permissions.SystemBackup)]
    public async Task<ActionResult<object>> GetLatestBackup()
    {
        var path = await backupService.GetLatestBackupPathAsync();
        return path != null ? Ok(new { path }) : NotFound();
    }
}

public class AutoTagRequest
{
    public IEnumerable<string>? PerformerIds { get; set; }
    public IEnumerable<string>? StudioIds { get; set; }
    public IEnumerable<string>? TagIds { get; set; }
}

public class ReorderJobRequest
{
    public string? BeforeJobId { get; set; }
}

