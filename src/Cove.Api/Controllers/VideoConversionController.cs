using Microsoft.AspNetCore.Mvc;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Entities;

namespace Cove.Api.Controllers;

public sealed class VideoConversionRequestDto
{
    public List<int> VideoIds { get; set; } = [];
    /// <summary>"h264", "hevc", "av1", or "copy" to keep the video stream and change only the container.</summary>
    public string Codec { get; set; } = "hevc";
    /// <summary>"mp4" or "mkv".</summary>
    public string Container { get; set; } = "mp4";
    /// <summary>
    /// One rung of the conversion ladder: "highSoftware", "highHardware",
    /// "balancedSoftware" or "balancedHardware".
    /// </summary>
    public string Effort { get; set; } = "highHardware";

    /// <summary>Re-encode at this frame rate instead of the source's. Null keeps the source's.</summary>
    public double? OutputFrameRate { get; set; }

    /// <summary>Convert even when the predicted saving is below the worthwhile threshold.</summary>
    public bool ConvertMarginalSavings { get; set; }
    /// <summary>Verify each converted file, make it the video's primary file and delete the original from disk.</summary>
    public bool ReplaceOriginal { get; set; }
    /// <summary>Convert even when the projected result is larger than the original.</summary>
    public bool ConvertEvenIfLarger { get; set; }
}

[ApiController]
[Route("api/video-conversion")]
[RequiresPermission(Permissions.VideosRead)]
public sealed class VideoConversionController(
    VideoConversionJobService conversionService,
    ICurrentPrincipalAccessor principalAccessor) : ControllerBase
{
    /// <summary>The encoder each codec would use on this machine. The first call runs short test encodes.</summary>
    [HttpGet("encoders")]
    [RequiresPermission(Permissions.JobsRun)]
    public ActionResult<IReadOnlyList<VideoConversionEncoderInfo>> Encoders()
        => Ok(conversionService.DescribeEncoders());

    [HttpPost]
    [RequiresPermission(Permissions.JobsRun)]
    [RequiresPermission(Permissions.VideosWrite)]
    [RequiresPermissionWhenTrue(Permissions.VideosDeleteFile, ActionArgumentName = "dto", PropertyName = "ReplaceOriginal")]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosWrite, ActionArgumentName = "dto", PropertyName = "VideoIds")]
    public ActionResult<VideoConversionJobStart> Start([FromBody] VideoConversionRequestDto dto)
    {
        if (dto.ReplaceOriginal && principalAccessor.Current?.Has(Permissions.VideosDeleteFile) != true)
            return Forbid();

        if (dto.VideoIds.All(id => id <= 0))
            return BadRequest(new { error = "Select at least one video to convert." });

        if (!TryParse(dto.Codec, out VideoConversionCodec codec)
            || !TryParse(dto.Container, out VideoConversionContainer container)
            || !TryParse(dto.Effort, out VideoConversionEffort effort))
        {
            return BadRequest(new
            {
                error = "Unknown conversion option. Codec is h264, hevc, av1 or copy; container is mp4 or mkv; "
                    + "effort is highSoftware, highHardware, balancedSoftware or balancedHardware.",
            });
        }

        // A requested rate only ever lowers a video's own rate; the job clamps per video, because a
        // selection can mix source rates.
        if (dto.OutputFrameRate is { } fps && (fps < 1 || fps > 240))
            return BadRequest(new { error = "Output frame rate must be between 1 and 240." });

        var settings = new VideoConversionSettings(
            codec, container, effort, dto.ReplaceOriginal,
            dto.OutputFrameRate, dto.ConvertMarginalSavings, dto.ConvertEvenIfLarger);
        return Accepted(conversionService.Start(principalAccessor.Current, dto.VideoIds, settings));
    }

    internal static bool TryParse<TEnum>(string? value, out TEnum result) where TEnum : struct, Enum
        => Enum.TryParse(value?.Trim(), ignoreCase: true, out result)
            && Enum.IsDefined(result)
            // Enum.TryParse also accepts numbers; only the documented names are part of the API.
            && !int.TryParse(value, out _);
}
