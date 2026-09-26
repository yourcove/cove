using System.Globalization;
using Cove.Core.DTOs;
using Cove.Core.Enums;

namespace Cove.Api.Services;

/// <summary>
/// ffmpeg filters that turn a VR frame into what a flat screen should show: the left eye only,
/// reprojected to an ordinary 16:9 view of the scene's centre. Without this a cover is two
/// warped circles side by side. A 3D film's eyes are only separated, not reprojected.
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
        => Flat(vr, width, stereoOutput: false);

    /// <summary>
    /// Like <see cref="OneEyeFlat"/> but keeps both eyes, side by side, each <paramref name="eyeWidth"/>
    /// wide: the texture a stereoscopic card in a headset wants. A mono source yields a single view.
    /// </summary>
    public static string? StereoFlat(VrDescriptorDto? vr, int eyeWidth)
        => Flat(vr, eyeWidth, stereoOutput: true);

    private static string? Flat(VrDescriptorDto? vr, int width, bool stereoOutput)
    {
        if (vr == null)
            return null;
        if (vr.Projection == VrProjection.Flat)
            return FlatStereo(vr.StereoMode, width, stereoOutput);

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
        // v360's w/h are per eye; a side-by-side output is twice as wide.
        var outStereo = stereoOutput && stereo != "2d" ? "sbs" : "2d";

        return string.Create(CultureInfo.InvariantCulture,
            $"v360={input}:output=flat:in_stereo={stereo}:out_stereo={outStereo}:h_fov={HorizontalFov:0.##}:v_fov={VerticalFov:0.##}:w={evenWidth}:h={evenHeight}");
    }

    /// <summary>
    /// A 3D film needs no reprojection, only its eyes pulled apart. Each eye is stretched to 16:9, which
    /// also undoes the squeeze of half-width or half-height packing. A mono flat video is an ordinary one.
    /// </summary>
    private static string? FlatStereo(VrStereoMode stereoMode, int width, bool stereoOutput)
    {
        if (stereoMode == VrStereoMode.Mono)
            return null;

        var evenWidth = Math.Max(2, width / 2 * 2);
        var evenHeight = Math.Max(2, (int)Math.Round(evenWidth * 9 / 16.0 / 2) * 2);
        var sideBySide = stereoMode == VrStereoMode.SideBySide;
        if (!stereoOutput)
        {
            var leftEye = sideBySide ? "crop=iw/2:ih:0:0" : "crop=iw:ih/2:0:0";
            return string.Create(CultureInfo.InvariantCulture, $"{leftEye},scale={evenWidth}:{evenHeight},setsar=1");
        }
        // Both eyes side by side, each evenWidth wide, like the reprojected cards.
        var packing = sideBySide ? "" : "stereo3d=abl:sbsl,";
        return string.Create(CultureInfo.InvariantCulture, $"{packing}scale={evenWidth * 2}:{evenHeight},setsar=1");
    }
}
