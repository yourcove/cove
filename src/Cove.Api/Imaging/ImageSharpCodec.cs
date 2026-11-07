using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Processing;
using SharpImage = SixLabors.ImageSharp.Image;

namespace Cove.Api.Imaging;

/// <summary><see cref="IImageCodec"/> backed by SixLabors ImageSharp.</summary>
public sealed class ImageSharpCodec : IImageCodec
{
    private static readonly DecoderOptions FirstFrame = new() { MaxFrames = 1 };

    public ImageHeader Identify(Stream stream) => Translate(() => ToHeader(SharpImage.Identify(stream)));

    public async Task<ImageHeader> IdentifyAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        try
        {
            return ToHeader(await SharpImage.IdentifyAsync(stream, cancellationToken));
        }
        catch (Exception ex) when (Map(ex) is { } mapped)
        {
            throw mapped;
        }
    }

    public async Task<ILoadedImage> LoadAsync(Stream stream, ImageLoadOptions options = default, CancellationToken cancellationToken = default)
    {
        var decoderOptions = options.TargetSize is { } target
            ? new DecoderOptions { MaxFrames = 1, TargetSize = new Size(target.Width, target.Height) }
            : FirstFrame;
        try
        {
            return new Loaded(await SharpImage.LoadAsync(decoderOptions, stream, cancellationToken));
        }
        catch (Exception ex) when (Map(ex) is { } mapped)
        {
            throw mapped;
        }
    }

    public PixelBuffer<TPixel> DecodePixels<TPixel>(Stream stream) where TPixel : unmanaged, IPixel<TPixel>
        => Translate(() => TPixel.Layout switch
        {
            PixelLayout.L8 => Copy<SixLabors.ImageSharp.PixelFormats.L8, TPixel>(stream),
            PixelLayout.L16 => Copy<SixLabors.ImageSharp.PixelFormats.L16, TPixel>(stream),
            PixelLayout.Rgba32 => Copy<SixLabors.ImageSharp.PixelFormats.Rgba32, TPixel>(stream),
            PixelLayout.Rgba64 => Copy<SixLabors.ImageSharp.PixelFormats.Rgba64, TPixel>(stream),
            _ => throw new ArgumentOutOfRangeException(nameof(TPixel)),
        });

    public ILoadedImage FromPixels(PixelBuffer<Rgba32> pixels)
        => new Loaded(SharpImage.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(
            MemoryMarshal.Cast<Rgba32, SixLabors.ImageSharp.PixelFormats.Rgba32>(pixels.Pixels.AsSpan()), pixels.Width, pixels.Height));

    /// <summary>ImageSharp's pixel types have the same layout as Cove's, so rows copy as bytes.</summary>
    private static PixelBuffer<TPixel> Copy<TSource, TPixel>(Stream stream)
        where TSource : unmanaged, SixLabors.ImageSharp.PixelFormats.IPixel<TSource>
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using var image = SharpImage.Load<TSource>(FirstFrame, stream);
        var buffer = new PixelBuffer<TPixel>(image.Width, image.Height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
                MemoryMarshal.Cast<TSource, TPixel>(accessor.GetRowSpan(y)).CopyTo(buffer.Row(y));
        });
        return buffer;
    }

    private static ImageHeader ToHeader(ImageInfo info)
    {
        var format = info.Metadata.DecodedImageFormat switch
        {
            JpegFormat => ImageFormat.Jpeg,
            PngFormat => ImageFormat.Png,
            GifFormat => ImageFormat.Gif,
            WebpFormat => ImageFormat.Webp,
            BmpFormat => ImageFormat.Bmp,
            TiffFormat => ImageFormat.Tiff,
            _ => ImageFormat.Unknown,
        };

        var orientation = 1;
        if (info.Metadata.ExifProfile is { } exif
            && exif.TryGetValue(ExifTag.Orientation, out var value)
            && value?.Value is >= 1 and <= 8)
            orientation = value.Value;

        // A still WebP reports no frame metadata at all, so a count of zero is one frame.
        return new ImageHeader(format, info.Width, info.Height, Math.Max(1, info.FrameMetadataCollection.Count), orientation);
    }

    private static T Translate<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (Map(ex) is { } mapped)
        {
            throw mapped;
        }
    }

    private static ImageDecodeException? Map(Exception ex) => ex switch
    {
        UnknownImageFormatException => new UnsupportedImageFormatException(ex.Message, ex),
        ImageFormatException => new InvalidImageException(ex.Message, ex),
        _ => null,
    };

    private sealed class Loaded(SharpImage image) : ILoadedImage
    {
        public int Width => image.Width;
        public int Height => image.Height;

        public void AutoOrient() => image.Mutate(static context => context.AutoOrient());

        public void Crop(PixelRect area)
            => image.Mutate(context => context.Crop(new Rectangle(area.X, area.Y, area.Width, area.Height)));

        public void Resize(PixelSize size)
            => image.Mutate(context => context.Resize(new ResizeOptions
            {
                Size = new Size(size.Width, size.Height),
                Mode = ResizeMode.Stretch,
            }));

        public Task SaveAsync(Stream destination, ImageOutput output, CancellationToken cancellationToken = default)
            => output.Format == ImageOutputFormat.Png
                ? image.SaveAsPngAsync(destination, new PngEncoder(), cancellationToken)
                : image.SaveAsJpegAsync(destination, new JpegEncoder { Quality = output.Quality }, cancellationToken);

        public void Dispose() => image.Dispose();
    }
}
