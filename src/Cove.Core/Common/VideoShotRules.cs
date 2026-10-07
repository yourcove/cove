namespace Cove.Core.Common;

/// <summary>
/// The limits <c>IVideoShotService</c> enforces on a set of shot boundaries. They are read-only fields
/// rather than constants, so an extension compiled against one release sees the values of the release
/// it runs on.
/// </summary>
public static class VideoShotRules
{
    /// <summary>How far a boundary may be from where it belongs and still be snapped into place.</summary>
    public static readonly double BoundaryToleranceSec = 0.001;

    /// <summary>The shortest shot a set may contain.</summary>
    public static readonly double MinShotDurationSec = 0.001;

    public static readonly int MaxShotsPerSet = 50_000;

    /// <summary>
    /// An analysed duration may exceed the file's probed duration by this much, or by
    /// <see cref="DurationSanityFraction"/> of it when that is larger; beyond that the write is
    /// taken to be for the wrong file.
    /// </summary>
    public static readonly double DurationSanityMinSec = 2.0;

    public static readonly double DurationSanityFraction = 0.01;

    /// <summary>
    /// An analysed duration may fall short of the file's probed duration down to this fraction of it,
    /// because the probed duration is the container's and the video stream can end before the audio
    /// does; anything shorter is taken to be for the wrong file.
    /// </summary>
    public static readonly double DurationSanityShortestFraction = 0.5;

    public static readonly int MaxLabelLength = 100;
    public static readonly int MaxKeyLength = 200;
    public static readonly int MaxPayloadBytes = 65_536;

    /// <summary>The source key of a set drawn by hand.</summary>
    public static readonly string ManualSourceKey = "user";
}
