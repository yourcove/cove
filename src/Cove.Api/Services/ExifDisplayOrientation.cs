using Cove.Api.Imaging;

namespace Cove.Api.Services;

/// <summary>
/// Converts an image's stored pixel dimensions into the <em>display</em> dimensions a viewer sees.
/// A camera that records in one orientation and tags the file with another — how phones store
/// portrait photos — leaves the stored grid transposed relative to the picture. This is the still
/// image counterpart of <see cref="FfprobeDisplayOrientation"/>, and exists for the same reason:
/// Cove's thumbnails are auto-oriented (ThumbnailService applies AutoOrient), so storing the raw
/// grid would leave the recorded size contradicting the thumbnail Cove itself renders.
/// </summary>
internal static class ExifDisplayOrientation
{
    /// <summary>
    /// Returns <paramref name="storedWidth"/>/<paramref name="storedHeight"/> transposed when the
    /// image's EXIF orientation turns the picture onto its side.
    /// </summary>
    internal static (int Width, int Height) Apply(ImageHeader? image, int storedWidth, int storedHeight)
        => SwapsAxes(image) ? (storedHeight, storedWidth) : (storedWidth, storedHeight);

    /// <summary>
    /// True when the image's EXIF orientation rotates the picture a quarter turn either way
    /// (orientations 5 to 8, see <see cref="ImageHeader.SwapsAxes"/>). A mirror never changes the
    /// dimensions, only which way round the picture reads.
    /// </summary>
    internal static bool SwapsAxes(ImageHeader? image) => image?.SwapsAxes == true;
}
