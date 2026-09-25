using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Controllers;

/// <summary>A span of a video's timeline, in seconds from its start.</summary>
public sealed record TimeRangeDto(double Start, double End);

public sealed class VideoCutPreviewRequestDto
{
    /// <summary>The parts of the timeline to remove. They may overlap or come in any order.</summary>
    public List<TimeRangeDto> Remove { get; set; } = [];

    /// <summary>
    /// False (the default) previews a lossless cut, where each kept part starts on the keyframe at or
    /// before its mark. True previews a re-encoding cut, which lands exactly on the marks.
    /// </summary>
    public bool Exact { get; set; }
}

/// <summary>What a cut does to one timed item. <c>Outcome</c> is kept, moved, joined (spans a cut and
/// now covers its kept parts back to back) or removed (lies entirely inside removed footage).</summary>
public sealed record VideoCutItemPreview(string Kind, int Id, string? Title, double Start, double? End, string Outcome, double? NewStart, double? NewEnd);

/// <summary>
/// Everything a cut would do, before anything is changed. <c>Kept</c> is where the kept parts really
/// start and end - for a lossless cut, after moving back to keyframes. <c>EstimatedBytes</c> assumes the
/// footage kept costs what it did in the original; a re-encode is measured when it runs.
/// </summary>
public sealed record VideoCutPreview(
    int FileId, double SourceDuration, bool Exact, IReadOnlyList<TimeRangeDto> Kept,
    double OutputDuration, double RemovedSeconds, long SourceBytes, long EstimatedBytes,
    IReadOnlyList<VideoCutItemPreview> Items);

public sealed class VideoCutItemDto
{
    public int VideoId { get; set; }

    /// <summary>The primary file the times were read against. A video whose primary file has since changed is not cut.</summary>
    public int FileId { get; set; }

    public List<TimeRangeDto> Remove { get; set; } = [];
}

public sealed class VideoCutRequestDto
{
    public List<VideoCutItemDto> Videos { get; set; } = [];

    /// <summary>"copy" (the default) cuts without re-encoding, which takes seconds; "h264", "hevc" or "av1" re-encode while cutting, which cuts exactly.</summary>
    public string Codec { get; set; } = "copy";

    /// <summary>"source" (the default) keeps each file's own format where it can; "mp4" or "mkv" choose one.</summary>
    public string Container { get; set; } = "source";

    /// <summary>The quality to keep when re-encoding; see the conversion endpoint.</summary>
    public string Effort { get; set; } = "highHardware";

    /// <summary>Re-encode at this lower frame rate. Needs a codec other than "copy".</summary>
    public double? OutputFrameRate { get; set; }

    /// <summary>Make the cut file the video's primary, move everything timed on the video with the cut, and delete the original.</summary>
    public bool ReplaceOriginal { get; set; }
}

/// <summary>
/// Cutting parts out of videos. Two calls cover what an extension needs to propose and apply cuts:
/// preview what a cut would do to a video - where it lands, how long and how large the result is, and
/// what happens to every marker, segment and clip - then cut one or many videos in a job.
/// </summary>
[ApiController]
[Route("api/videos")]
[RequiresPermission(Permissions.VideosRead)]
public sealed class VideoCutsController(
    CoveContext db,
    VideoConversionJobService conversion,
    VideoTimelineDependencyService timeline,
    IMediaProbeService mediaProbe,
    CoveConfiguration config,
    ICurrentPrincipalAccessor principalAccessor) : ControllerBase
{
    [HttpPost("{videoId:int}/cut/preview")]
    [RequiresPermission(Permissions.FilesRead)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosRead, RouteValueName = "videoId")]
    public async Task<ActionResult<VideoCutPreview>> Preview(int videoId, [FromBody] VideoCutPreviewRequestDto dto, CancellationToken ct)
    {
        var video = await db.Videos.AsNoTracking()
            .Include(item => item.PrimaryFile!).ThenInclude(file => file.ParentFolder)
            .SingleOrDefaultAsync(item => item.Id == videoId, ct);
        if (video is null)
            return NotFound();
        if (video.PrimaryFile is not { } file)
            return BadRequest(new { error = "The video has no primary file to cut." });

        var path = FilesystemPaths.ToNativePath(!string.IsNullOrWhiteSpace(file.Path)
            ? file.Path
            : BaseFileEntity.ComputePath(file.ParentFolder?.Path, file.Basename));
        if (!System.IO.File.Exists(path))
            return Conflict(new { error = "The video's primary file is missing on disk." });
        var probe = await mediaProbe.ProbeAsync(path, ct);
        if (probe.Status != MediaProbeStatus.Success || probe.Json is null)
            return Conflict(new { error = $"The video's primary file could not be read: {probe.Reason ?? probe.Status.ToString()}" });
        var media = ProbedMedia.Parse(probe.Json);

        var (kept, error) = VideoCut.KeepAfterRemoving(dto.Remove.Select(range => new TimeRange(range.Start, range.End)), media.Duration);
        if (kept is null)
            return BadRequest(new { error });

        if (!dto.Exact)
        {
            var ffprobe = FfprobeMediaProbeService.ResolveFfprobePath(config);
            if (ffprobe is null)
                return Conflict(new { error = "ffprobe was not found, so the file's keyframes could not be read." });
            var keyframes = new Dictionary<double, double>();
            try
            {
                foreach (var range in kept)
                    keyframes[range.Start] = await VideoKeyframes.FindAtOrBeforeAsync(ffprobe, path, media.Video!.Index, range.Start, media.StartTime, ct);
            }
            catch (VideoConversionException ex)
            {
                return Conflict(new { error = ex.Message });
            }
            kept = VideoCut.SnapStartsToKeyframes(kept, start => keyframes[start]);
        }

        var principal = principalAccessor.Current;
        var dependencies = await timeline.LoadAsync(videoId, principal?.Has(Permissions.SegmentsRead) == true, ct);
        if (!await timeline.CanReadAsync(principal, dependencies, ct))
            return Forbid();

        var items = dependencies
            .Select(dependency => Describe(dependency, kept, media.Duration))
            .OrderBy(item => item.Start)
            .ToList();
        var output = VideoCut.OutputDuration(kept);
        var sourceBytes = new FileInfo(path).Length;
        return Ok(new VideoCutPreview(
            file.Id, media.Duration, dto.Exact,
            [.. kept.Select(range => new TimeRangeDto(range.Start, range.End))],
            output, Math.Max(0, media.Duration - output), sourceBytes,
            media.Duration > 0 ? (long)(sourceBytes * (output / media.Duration)) : sourceBytes,
            items));
    }

    [HttpPost("cut")]
    [RequiresPermission(Permissions.JobsRun)]
    [RequiresPermission(Permissions.VideosWrite)]
    [RequiresPermissionWhenTrue(Permissions.VideosDeleteFile, ActionArgumentName = "dto", PropertyName = "ReplaceOriginal")]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosWrite, ActionArgumentName = "dto", PropertyName = "Videos.VideoId")]
    public async Task<ActionResult<VideoConversionJobStart>> Cut([FromBody] VideoCutRequestDto dto, CancellationToken ct)
    {
        var principal = principalAccessor.Current;
        if (dto.ReplaceOriginal && principal?.Has(Permissions.VideosDeleteFile) != true)
            return Forbid();
        if (dto.Videos.Count == 0)
            return BadRequest(new { error = "Choose at least one video to cut." });
        if (dto.Videos.GroupBy(item => item.VideoId).Any(group => group.Count() > 1))
            return BadRequest(new { error = "Each video can appear once; put all of its cuts in one entry." });

        if (!VideoConversionController.TryParse(dto.Codec, out VideoConversionCodec codec)
            || !VideoConversionController.TryParse(dto.Container, out VideoConversionContainer container)
            || !VideoConversionController.TryParse(dto.Effort, out VideoConversionEffort effort))
        {
            return BadRequest(new
            {
                error = "Unknown codec, container or effort. Codec is copy, h264, hevc or av1; container is source, mp4 or mkv; "
                    + "effort is highSoftware, highHardware, balancedSoftware or balancedHardware.",
            });
        }
        if (codec == VideoConversionCodec.Copy && dto.OutputFrameRate is > 0)
            return BadRequest(new { error = "Lowering the frame rate needs re-encoding; choose a codec other than copy." });

        var ids = dto.Videos.Select(item => item.VideoId).ToArray();
        var files = await db.Videos.AsNoTracking()
            .Where(video => ids.Contains(video.Id))
            .Select(video => new { video.Id, video.PrimaryFileId, Duration = video.PrimaryFile != null ? ((VideoFile)video.PrimaryFile).Duration : 0 })
            .ToDictionaryAsync(video => video.Id, ct);

        var cuts = new Dictionary<int, VideoCutRequest>();
        foreach (var item in dto.Videos)
        {
            if (!files.TryGetValue(item.VideoId, out var current))
                return NotFound(new { error = $"Video {item.VideoId} does not exist." });
            if (current.PrimaryFileId != item.FileId)
                return Conflict(new { error = $"Video {item.VideoId}'s primary file has changed since these cuts were chosen; preview them again." });

            var remove = item.Remove.Select(range => new TimeRange(range.Start, range.End)).ToList();
            var (kept, error) = VideoCut.KeepAfterRemoving(remove, current.Duration);
            if (kept is null)
                return BadRequest(new { error = $"Video {item.VideoId}: {error}" });

            // Checked now as well as when the job runs, so a cut the user may not make is refused while
            // they are still here rather than failing later. Lossless snapping only ever keeps more, so the
            // removals checked here cover the ones the job will make.
            if (dto.ReplaceOriginal)
            {
                var dependencies = await timeline.LoadAsync(item.VideoId, includeSegments: true, ct);
                var deletes = dependencies
                    .Where(dependency => VideoCut.MapRange(dependency.StartSec, dependency.EffectiveEnd(current.Duration), kept) is null)
                    .Select(dependency => dependency.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!await timeline.CanReadAsync(principal, dependencies, ct)
                    || !await timeline.MayChangeAsync(principal, item.VideoId, dependencies, moves: true, deletesAll: false, deletes, ct))
                {
                    return Forbid();
                }
            }

            cuts[item.VideoId] = new VideoCutRequest(item.FileId, remove);
        }

        var settings = new VideoConversionSettings(codec, container, effort, dto.ReplaceOriginal, dto.OutputFrameRate);
        return Ok(conversion.Start(principal, [], settings, cuts));
    }

    private static VideoCutItemPreview Describe(VideoTimedDependency dependency, IReadOnlyList<TimeRange> kept, double duration)
    {
        var end = dependency.EffectiveEnd(duration);
        var mapped = VideoCut.MapRange(dependency.StartSec, end, kept);
        string outcome;
        if (mapped is null)
            outcome = "removed";
        else if (kept.Zip(kept.Skip(1)).Any(pair => pair.First.End > dependency.StartSec && pair.Second.Start < end))
            outcome = "joined";
        else if (Math.Abs(mapped.Start - dependency.StartSec) < 1e-6)
            outcome = "kept";
        else
            outcome = "moved";
        return new VideoCutItemPreview(
            dependency.Kind, dependency.Id, dependency.Title, dependency.StartSec, dependency.EndSec, outcome,
            mapped?.Start, mapped is null ? null : dependency.EndSec.HasValue ? mapped.End : null);
    }
}
