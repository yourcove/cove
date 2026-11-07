namespace Cove.Api.Imaging;

/// <summary>
/// Decoding, editing and encoding of still images, independent of the library that does it.
/// Every operation works on the first frame of an animation. Failures to decode surface as
/// <see cref="UnsupportedImageFormatException"/> or <see cref="InvalidImageException"/>.
/// </summary>
public interface IImageCodec
{
    /// <summary>Reads the header. The stream must be positioned at the start of the image.</summary>
    ImageHeader Identify(Stream stream);

    Task<ImageHeader> IdentifyAsync(Stream stream, CancellationToken cancellationToken = default);

    /// <summary>Decodes the first frame for editing. The stream is not used after this returns.</summary>
    Task<ILoadedImage> LoadAsync(Stream stream, ImageLoadOptions options = default, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decodes the first frame to pixels of the requested layout without applying EXIF orientation.
    /// Narrower samples widen exactly (an 8-bit value v becomes v × 257); wider ones are not narrowed
    /// to fit, so asking for 8 bits from a 16-bit image rounds as ImageSharp did.
    /// </summary>
    PixelBuffer<TPixel> DecodePixels<TPixel>(Stream stream) where TPixel : unmanaged, IPixel<TPixel>;

    /// <summary>Wraps pixels for editing and encoding.</summary>
    ILoadedImage FromPixels(PixelBuffer<Rgba32> pixels);
}

/// <summary>A decoded image being edited. Operations apply in the order they are called.</summary>
public interface ILoadedImage : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>Applies the EXIF orientation so the pixels are upright, and resets it to 1.</summary>
    void AutoOrient();

    /// <summary>Keeps <paramref name="area"/>, which must lie inside the image.</summary>
    void Crop(PixelRect area);

    /// <summary>Resamples to exactly <paramref name="size"/>.</summary>
    void Resize(PixelSize size);

    /// <summary>
    /// Encodes the image. JPEG output drops any alpha channel without compositing, keeping the
    /// colour stored under transparent pixels, as ImageSharp did.
    /// </summary>
    Task SaveAsync(Stream destination, ImageOutput output, CancellationToken cancellationToken = default);
}

public static class ImageCodec
{
    /// <summary>The codec the application uses.</summary>
    public static IImageCodec Default { get; } = new ImageSharpCodec();
}
