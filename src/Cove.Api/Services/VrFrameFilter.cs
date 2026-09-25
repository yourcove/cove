using System.Globalization;
using Cove.Core.DTOs;
using Cove.Core.Enums;

namespace Cove.Api.Services;

/// <summary>
/// ffmpeg filters that turn a VR frame into what a flat screen should show: the left eye only,
/// reprojected to an ordinary 16:9 view of the scene's centre. Without this a cover is two
/// warped circles side by side.
/// </summary>
internal static class VrFrameFilter
{
    // A 100° horizontal view looks natural on a card; the vertical angle keeps it 16:9 without distortion.
    private const double HorizontalFov = 100;
    private static readonly double VerticalFov = 2 * Math.Atan(Math.Tan(HorizontalFov / 2 * Math.PI / 180) * 9 / 16) * 180 / Math.PI;

    /// <summary>
    /// A <c>v360</c> filter reading one eye of <paramref name="vr"/> and writing a flat
    /// <paramref name="width"/>-wide 16:9 frame, or null when the video is not VR.
    /// </summary>
    public static string? OneEyeFlat(VrDescriptorDto? vr, int width)
    {
        if (vr == null)
            return null;

        var input = vr.Projection switch
        {
            VrProjection.Equirectangular when vr.FieldOfView >= 360 => "input=e",
            VrProjection.Equirectangular => "input=he",
            // MKX200 is a fisheye lens with its own distortion curve; plain fisheye at its angle is
            // close enough for a still, and players that need the exact curve do not read covers.
            _ => string.Create(CultureInfo.InvariantCulture, $"input=fisheye:ih_fov={vr.FieldOfView}:iv_fov={vr.FieldOfView}"),
        };
        var stereo = vr.StereoMode switch
        {
            VrStereoMode.SideBySide => "sbs",
            VrStereoMode.TopBottom => "tb",
            _ => "2d",
        };
        var evenWidth = Math.Max(2, width / 2 * 2);
        var evenHeight = Math.Max(2, (int)Math.Round(evenWidth * 9 / 16.0 / 2) * 2);

        return string.Create(CultureInfo.InvariantCulture,
            $"v360={input}:output=flat:in_stereo={stereo}:out_stereo=2d:h_fov={HorizontalFov:0.##}:v_fov={VerticalFov:0.##}:w={evenWidth}:h={evenHeight}");
    }
}
