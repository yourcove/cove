using Cove.Core.DTOs;
using Cove.Core.Enums;

namespace Cove.Core.Helpers;

/// <summary>
/// Infers a VR layout from the file-name conventions VR studios and players share
/// (<c>_180_LR</c>, <c>_360_TB</c>, <c>_3dh</c>, <c>_MKX200</c>, <c>_FISHEYE190</c>, …) and from frame dimensions.
/// </summary>
public static class VrDescriptorDetector
{
    private static readonly char[] Separators = ['_', '-', '.', ' ', '[', ']', '(', ')', '+', ','];

    // Tokens that mean VR on their own.
    private static readonly HashSet<string> StrongTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "VR", "VR180", "VR360", "3DH", "3DV", "180X180", "360X180", "MKX200", "MKX220", "VRCA220",
        "FISHEYE", "FISHEYE180", "FISHEYE190", "FISHEYE200", "RF52", "OCULUS", "GEARVR", "PSVR",
    };

    private static readonly HashSet<string> SideBySideTokens = new(StringComparer.OrdinalIgnoreCase) { "LR", "SBS", "3DH" };
    private static readonly HashSet<string> TopBottomTokens = new(StringComparer.OrdinalIgnoreCase) { "TB", "OU", "3DV" };

    // Stereoscopic flat video (a 3D film): half or full side by side, half or full over-under.
    private static readonly HashSet<string> FlatSideBySideTokens = new(StringComparer.OrdinalIgnoreCase) { "HSBS", "FSBS", "LRF" };
    private static readonly HashSet<string> FlatTopBottomTokens = new(StringComparer.OrdinalIgnoreCase) { "HOU", "FOU", "HTB", "FTB", "HTAB", "FTAB", "TBF" };

    /// <summary>
    /// Returns a layout when the file name marks the file as VR, otherwise null. A bare <c>180</c> or
    /// <c>SBS</c> is not enough on its own (a flat 3D film or a runtime could match), but the two together are.
    /// Dimensions only break ties the name leaves open, such as the stereo packing of a 360 video.
    /// </summary>
    public static VrDescriptorDto? DetectFromFileName(string? path, int width = 0, int height = 0)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var name = Path.GetFileNameWithoutExtension(path);
        var tokens = name.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return null;

        var strong = tokens.Any(StrongTokens.Contains);
        var has180 = tokens.Any(t => t is "180" || t.Equals("VR180", StringComparison.OrdinalIgnoreCase) || t.Equals("180X180", StringComparison.OrdinalIgnoreCase));
        var has360 = tokens.Any(t => t is "360" || t.Equals("VR360", StringComparison.OrdinalIgnoreCase) || t.Equals("360X180", StringComparison.OrdinalIgnoreCase));
        VrStereoMode? stereo = tokens.Any(SideBySideTokens.Contains) ? VrStereoMode.SideBySide
            : tokens.Any(TopBottomTokens.Contains) ? VrStereoMode.TopBottom
            : tokens.Any(t => t.Equals("MONO", StringComparison.OrdinalIgnoreCase)) ? VrStereoMode.Mono
            : null;

        if (!strong && !((has180 || has360) && stereo.HasValue))
            return null;

        var projection = VrProjection.Equirectangular;
        var fieldOfView = has360 ? 360 : 180;
        foreach (var token in tokens.Select(t => t.ToUpperInvariant()))
        {
            switch (token)
            {
                case "MKX200": projection = VrProjection.Mkx200; fieldOfView = 200; break;
                case "MKX220" or "VRCA220": projection = VrProjection.Mkx200; fieldOfView = 220; break;
                case "FISHEYE" or "FISHEYE180": projection = VrProjection.Fisheye; fieldOfView = 180; break;
                case "FISHEYE190" or "RF52": projection = VrProjection.Fisheye; fieldOfView = 190; break;
                case "FISHEYE200": projection = VrProjection.Fisheye; fieldOfView = 200; break;
            }
        }

        return new VrDescriptorDto(projection, fieldOfView, stereo ?? GuessStereo(fieldOfView, width, height), Inferred: true);
    }

    /// <summary>
    /// A stereoscopic flat layout when the file name marks a 3D film (<c>HSBS</c>, <c>FSBS</c>,
    /// <c>HOU</c>, …, or <c>3D</c> next to <c>SBS</c> or <c>OU</c>), otherwise null.
    /// Unlike <see cref="DetectFromFileName"/> this never marks a file as VR by itself: it only decides
    /// how a video someone already flagged VR is laid out.
    /// </summary>
    public static VrDescriptorDto? DetectFlatStereoFromFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var tokens = Path.GetFileNameWithoutExtension(path).Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var has3D = tokens.Any(t => t.Equals("3D", StringComparison.OrdinalIgnoreCase));
        VrStereoMode? stereo =
            tokens.Any(FlatSideBySideTokens.Contains) || (has3D && tokens.Any(t => t.Equals("SBS", StringComparison.OrdinalIgnoreCase)))
                ? VrStereoMode.SideBySide
            : tokens.Any(FlatTopBottomTokens.Contains) || (has3D && tokens.Any(t => t.Equals("OU", StringComparison.OrdinalIgnoreCase)))
                ? VrStereoMode.TopBottom
            : null;

        return stereo.HasValue ? new VrDescriptorDto(VrProjection.Flat, 0, stereo.Value, Inferred: true) : null;
    }

    /// <summary>
    /// The layout to play a video flagged VR with: explicit values, then file-name detection (VR,
    /// then 3D film), then a guess from dimensions. Returns null for non-VR videos.
    /// </summary>
    public static VrDescriptorDto? Resolve(bool isVr, VrProjection? projection, int? fieldOfView, VrStereoMode? stereoMode, string? path, int width, int height)
    {
        if (!isVr)
            return null;

        var detected = DetectFromFileName(path, width, height)
            ?? DetectFlatStereoFromFileName(path)
            ?? GuessFromDimensions(width, height);
        if (projection is null && fieldOfView is null && stereoMode is null)
            return detected;

        return new VrDescriptorDto(
            projection ?? detected.Projection,
            // A 3D film has no field of view to lend a sphere someone picked for it.
            fieldOfView ?? DefaultFieldOfView(projection) ?? (projection is not null && detected.Projection == VrProjection.Flat ? 180 : detected.FieldOfView),
            stereoMode ?? detected.StereoMode);
    }

    /// <summary>
    /// A frame roughly twice as wide as tall is the common side-by-side 180 layout. A square frame
    /// is usually top-bottom 360. Anything else falls back to side-by-side 180, the most common VR format.
    /// </summary>
    public static VrDescriptorDto GuessFromDimensions(int width, int height)
    {
        if (width > 0 && height > 0 && Math.Abs((double)width / height - 1) < 0.05)
            return new VrDescriptorDto(VrProjection.Equirectangular, 360, VrStereoMode.TopBottom, Inferred: true);
        return new VrDescriptorDto(VrProjection.Equirectangular, 180, VrStereoMode.SideBySide, Inferred: true);
    }

    private static int? DefaultFieldOfView(VrProjection? projection) => projection switch
    {
        VrProjection.Mkx200 => 200,
        VrProjection.Fisheye => 190,
        VrProjection.Flat => 0,
        _ => null,
    };

    private static VrStereoMode GuessStereo(int fieldOfView, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return fieldOfView == 360 ? VrStereoMode.TopBottom : VrStereoMode.SideBySide;
        var aspect = (double)width / height;
        if (fieldOfView == 360)
            // Mono 360 is 2:1; stereo 360 stacks two of those into 1:1.
            return aspect > 1.5 ? VrStereoMode.Mono : VrStereoMode.TopBottom;
        // Stereo 180 puts two square eyes side by side (2:1); a square frame is mono 180.
        return aspect > 1.5 ? VrStereoMode.SideBySide : VrStereoMode.Mono;
    }
}
