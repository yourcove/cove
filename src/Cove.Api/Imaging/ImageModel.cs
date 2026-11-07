namespace Cove.Api.Imaging;

/// <summary>The container formats Cove decodes itself. Anything else is <see cref="Unknown"/>.</summary>
public enum ImageFormat
{
    Unknown = 0,
    Jpeg,
    Png,
    Gif,
    Webp,
    Bmp,
    Tiff,
}

public readonly record struct PixelSize(int Width, int Height);

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public static PixelRect Intersect(PixelRect a, PixelRect b)
    {
        var x = Math.Max(a.X, b.X);
        var y = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        return right <= x || bottom <= y ? default : new PixelRect(x, y, right - x, bottom - y);
    }
}

/// <summary>
/// What an image's header says, without decoding its pixels. <see cref="Width"/> and
/// <see cref="Height"/> are the stored dimensions; <see cref="DisplaySize"/> applies the EXIF
/// orientation the way <see cref="ILoadedImage.AutoOrient"/> does.
/// </summary>
/// <param name="FrameCount">1 for a still image; the number of frames for an animation.</param>
/// <param name="Orientation">The EXIF orientation, 1 to 8; 1 when the image has none.</param>
public sealed record ImageHeader(ImageFormat Format, int Width, int Height, int FrameCount, int Orientation)
{
    /// <summary>
    /// Orientations 5 to 8 carry a quarter turn (alone or with a mirror) and so transpose the
    /// axes; 1 to 4 are upright, mirrored or a half turn, which all preserve them.
    /// </summary>
    public bool SwapsAxes => Orientation is 5 or 6 or 7 or 8;

    public PixelSize DisplaySize => SwapsAxes ? new PixelSize(Height, Width) : new PixelSize(Width, Height);
}

public enum ImageOutputFormat
{
    Jpeg,
    Png,
}

public readonly record struct ImageOutput(ImageOutputFormat Format, int Quality)
{
    public static ImageOutput Jpeg(int quality) => new(ImageOutputFormat.Jpeg, quality);
    public static ImageOutput Png { get; } = new(ImageOutputFormat.Png, 0);

    public string ContentType => Format == ImageOutputFormat.Png ? "image/png" : "image/jpeg";
}

/// <param name="TargetSize">
/// The size the caller is about to resize the image to. A decoder that can decode at a reduced
/// resolution (JPEG's scaled IDCT) may use it to skip work; the loaded image is never smaller
/// than this in either dimension unless the source is.
/// </param>
public readonly record struct ImageLoadOptions(PixelSize? TargetSize = null);

/// <summary>The base of every decode failure, so callers can catch one type.</summary>
public class ImageDecodeException : Exception
{
    public ImageDecodeException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>The data is not in a format the codec decodes. A caller may try another decoder, such as ffmpeg.</summary>
public sealed class UnsupportedImageFormatException : ImageDecodeException
{
    public UnsupportedImageFormatException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>The data is in a supported format but is damaged or uses a variant the codec cannot read.</summary>
public sealed class InvalidImageException : ImageDecodeException
{
    public InvalidImageException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public static class ImageGeometry
{
    /// <summary>
    /// The largest size with the source's aspect ratio that fits in <paramref name="box"/>. This is
    /// the size ImageSharp's <c>ResizeMode.Max</c> produced, single-precision rounding included, so
    /// thumbnails keep their dimensions across decoders. It scales up as well as down; callers that
    /// only shrink check first.
    /// </summary>
    public static PixelSize FitWithin(PixelSize source, PixelSize box)
    {
        var targetWidth = box.Width;
        var targetHeight = box.Height;
        var percentHeight = MathF.Abs(box.Height / (float)source.Height);
        var percentWidth = MathF.Abs(box.Width / (float)source.Width);
        var ratio = box.Height / (float)box.Width;
        var sourceRatio = source.Height / (float)source.Width;
        if (sourceRatio < ratio)
            targetHeight = (int)MathF.Round(source.Height * percentWidth);
        else
            targetWidth = (int)MathF.Round(source.Width * percentHeight);
        return new PixelSize(Math.Max(1, targetWidth), Math.Max(1, targetHeight));
    }
}
