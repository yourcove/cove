using Cove.Api.Imaging;

namespace Cove.Api.Services;

/// <summary>
/// The pHash of an image file, computed the way Stash's <c>imagephash</c> package does:
/// Go decodes the file, <c>nfnt/resize</c> reduces it to 64×64 with its Bilinear filter, and
/// goimagehash hashes the result (<see cref="GoImageHash"/>).
///
/// nfnt/resize has a separate fixed-point path for each Go image type, and each rounds
/// differently, so the hash depends on which type Go's decoder returns. This class works out that
/// type from the file's headers (<see cref="EncodedImageHeaders"/>), decodes the pixels at a
/// matching depth through <see cref="ImageCodec"/>, and runs a port of that path
/// (<see cref="NfntBilinear"/>).
///
/// PNG, GIF, BMP, lossless WebP and TIFF decode to the same pixels in every correct decoder, so
/// their hashes match Stash's exactly. The exceptions are hashes that would need pixels the codec
/// does not expose: lossy JPEG and WebP (Go's decoders round differently), CMYK JPEG, and TIFF with
/// premultiplied alpha (decoders un-premultiply it). Those hashes come out close to Stash's.
/// </summary>
internal static class StashImagePhash
{
    public readonly record struct Result(ulong Hash, int Width, int Height);

    /// <summary>How Go represents the decoded image, which selects the nfnt/resize path.</summary>
    internal enum GoImageKind
    {
        /// <summary><c>*image.Gray</c>: 8-bit single channel.</summary>
        Gray,
        /// <summary><c>*image.Gray16</c>: 16-bit single channel.</summary>
        Gray16,
        /// <summary><c>*image.RGBA</c> or <c>*image.NRGBA</c>: 8-bit, alpha-premultiplied before filtering.</summary>
        Rgba,
        /// <summary><c>*image.RGBA64</c> or <c>*image.NRGBA64</c>: 16-bit, premultiplied before filtering.</summary>
        Rgba64,
        /// <summary>
        /// <c>*image.YCbCr</c>, from lossy JPEG and WebP: the Y, Cb and Cr planes are filtered at 8 bits
        /// and converted to RGB afterwards. The codec does not expose the decoded planes, so they are
        /// recovered from its RGB output with Go's own conversion.
        /// </summary>
        YCbCr,
        /// <summary>
        /// <c>*image.NYCbCrA</c>, from lossy WebP with an alpha channel: filtered at 16 bits from each
        /// pixel's <c>RGBA()</c>, the YCbCr colour premultiplied by alpha.
        /// </summary>
        NYCbCrA,
        /// <summary>
        /// Every other type (<c>*image.Paletted</c>, <c>*image.CMYK</c>): filtered at 16 bits from each
        /// pixel's <c>color.Color.RGBA()</c>.
        /// </summary>
        Generic,
    }

    /// <param name="Kind">The Go image type.</param>
    /// <param name="Crop">A GIF's first frame, which Go decodes on its own rather than onto the logical screen.</param>
    /// <param name="ForceOpaque">
    /// x/image/bmp ignores the alpha byte of a 32-bit BMP with a BITMAPINFOHEADER. Only the 8-bit
    /// <see cref="GoImageKind.Rgba"/> path reads it, the only one a BMP of that kind takes.
    /// </param>
    /// <param name="ColorKey16">
    /// A 16-bit PNG's tRNS colour key (one gray sample, or red, green and blue). Go makes every exact
    /// match transparent and every other pixel opaque. Decoders differ in whether they apply the key,
    /// so the alpha is derived from it here and the decoded alpha ignored.
    /// </param>
    /// <param name="Background">
    /// A GIF frame's palette entry 0, which Go's <c>Paletted.At</c> returns outside the frame.
    /// </param>
    internal readonly record struct Plan(
        GoImageKind Kind,
        PixelRect? Crop = null,
        bool ForceOpaque = false,
        ushort[]? ColorKey16 = null,
        Rgba64 Background = default);

    /// <summary>Hashes an encoded image. The stream must be seekable.</summary>
    public static Result Compute(Stream stream) => Compute(stream, ImageCodec.Default);

    internal static Result Compute(Stream stream, IImageCodec codec)
    {
        var start = stream.Position;
        var info = codec.Identify(stream);
        stream.Position = start;
        var plan = PlanFor(info, stream);
        stream.Position = start;

        var gray = plan.Kind switch
        {
            GoImageKind.Gray => Reduce(codec.DecodePixels<L8>(stream), plan, Gray8Pixels.Instance),
            GoImageKind.Gray16 => Reduce(codec.DecodePixels<L16>(stream), plan, Gray16Pixels.Instance),
            GoImageKind.Rgba => Reduce(codec.DecodePixels<Rgba32>(stream), plan, RgbaPixels.Instance),
            GoImageKind.Rgba64 => Reduce(codec.DecodePixels<Rgba64>(stream), plan, Rgba64Pixels.Instance),
            GoImageKind.YCbCr => Reduce(codec.DecodePixels<Rgba32>(stream), plan, YCbCrPixels.Instance),
            GoImageKind.NYCbCrA => Reduce(codec.DecodePixels<Rgba32>(stream), plan, NYCbCrAPixels.Instance),
            _ => Reduce(codec.DecodePixels<Rgba64>(stream), plan, GenericPixels.Instance),
        };
        return new Result(GoImageHash.Hash(gray), info.Width, info.Height);
    }

    /// <summary>
    /// The Go image type for a file, following the decoders Stash registers: image/jpeg,
    /// image/png, image/gif, golang.org/x/image/webp, and (through disintegration/imaging)
    /// golang.org/x/image/bmp and golang.org/x/image/tiff. Stash converts any other format to BMP
    /// with ffmpeg first; that BMP is usually 24-bit, which the final case follows.
    /// </summary>
    internal static Plan PlanFor(ImageHeader info, Stream stream)
    {
        var start = stream.Position;
        try
        {
            return PlanFromHeaders(info, stream, start);
        }
        finally
        {
            stream.Position = start;
        }
    }

    private static Plan PlanFromHeaders(ImageHeader info, Stream stream, long start)
    {
        switch (info.Format)
        {
            case ImageFormat.Png when EncodedImageHeaders.ReadPng(stream) is { } png:
            {
                var sixteenBit = png.BitDepth == 16;
                if (png.ColorType == EncodedImageHeaders.PngHeader.Palette)
                    return new Plan(GoImageKind.Generic);
                // A tRNS colour key turns grayscale into NRGBA so the keyed pixels can be transparent.
                if (png.ColorType == EncodedImageHeaders.PngHeader.Grayscale && !png.HasColorKey)
                    return new Plan(sixteenBit ? GoImageKind.Gray16 : GoImageKind.Gray);
                if (sixteenBit && png.HasColorKey)
                {
                    stream.Position = start;
                    return new Plan(GoImageKind.Rgba64, ColorKey16: EncodedImageHeaders.ReadPngColorKey16(stream));
                }
                return new Plan(sixteenBit ? GoImageKind.Rgba64 : GoImageKind.Rgba);
            }

            case ImageFormat.Jpeg:
                return EncodedImageHeaders.ReadJpeg(stream) switch
                {
                    EncodedImageHeaders.JpegColor.Gray => new Plan(GoImageKind.Gray),
                    EncodedImageHeaders.JpegColor.Cmyk => new Plan(GoImageKind.Generic),
                    EncodedImageHeaders.JpegColor.Rgb => new Plan(GoImageKind.Rgba),
                    _ => new Plan(GoImageKind.YCbCr),
                };

            case ImageFormat.Gif:
            {
                var gif = EncodedImageHeaders.ReadGif(stream);
                var crop = gif?.FirstFrame is { } bounds && bounds != new PixelRect(0, 0, info.Width, info.Height) ? bounds : (PixelRect?)null;
                return new Plan(GoImageKind.Generic, crop, Background: gif?.PaletteEntryZero ?? default);
            }

            case ImageFormat.Webp:
            {
                var webp = EncodedImageHeaders.ReadWebp(stream);
                if (webp is null or { Lossless: true })
                    return new Plan(GoImageKind.Rgba);
                // Lossy WebP is *image.YCbCr, or *image.NYCbCrA when an ALPH chunk gives it alpha.
                return new Plan(webp.HasAlpha ? GoImageKind.NYCbCrA : GoImageKind.YCbCr);
            }

            case ImageFormat.Bmp when EncodedImageHeaders.ReadBmp(stream) is { } bmp:
            {
                if (bmp.BitsPerPixel <= 8)
                    return new Plan(GoImageKind.Generic);
                var opaque = bmp.BitsPerPixel != 32 || bmp.InfoHeaderSize == 40;
                return new Plan(GoImageKind.Rgba, ForceOpaque: opaque);
            }

            case ImageFormat.Tiff:
            {
                var tiff = EncodedImageHeaders.ReadTiff(stream);
                var sixteenBit = tiff?.BitsPerSample == 16;
                return tiff?.Photometric switch
                {
                    EncodedImageHeaders.TiffHeader.BlackIsZero or EncodedImageHeaders.TiffHeader.WhiteIsZero
                        => new Plan(sixteenBit ? GoImageKind.Gray16 : GoImageKind.Gray),
                    EncodedImageHeaders.TiffHeader.PaletteColor => new Plan(GoImageKind.Generic),
                    _ => new Plan(sixteenBit ? GoImageKind.Rgba64 : GoImageKind.Rgba),
                };
            }
        }

        return new Plan(GoImageKind.Rgba, ForceOpaque: true);
    }

    /// <summary>
    /// The bounds of a GIF's first image descriptor, or null when the stream does not reach one.
    /// </summary>
    internal static PixelRect? ReadGifFirstFrameBounds(Stream stream) => EncodedImageHeaders.ReadGif(stream)?.FirstFrame;

    /// <summary>
    /// The samples of a 16-bit grayscale or truecolour PNG's tRNS chunk, or null when it has none.
    /// </summary>
    internal static ushort[]? ReadPngColorKey16(Stream stream) => EncodedImageHeaders.ReadPngColorKey16(stream);

    // ---- Pixel sources: the values each nfnt path filters, and Go's RGBA() of a decoded pixel ----

    /// <summary>
    /// A decoded pixel format and the nfnt path it feeds. <see cref="GoRgba"/> is the 16-bit
    /// premultiplied colour Go's <c>RGBA()</c> reports for a pixel, which goimagehash reads directly
    /// when the image is already 64×64, because nfnt/resize then returns it unchanged.
    /// </summary>
    private interface IPixels<TPixel> where TPixel : unmanaged, IPixel<TPixel>
    {
        int Channels { get; }
        TPixel FromRgba64(Rgba64 pixel);
        (uint R, uint G, uint B) GoRgba(TPixel pixel, in Plan plan);
    }

    /// <summary>A source filtered by nfnt's 8-bit paths.</summary>
    private interface IPixels8<TPixel> : IPixels<TPixel> where TPixel : unmanaged, IPixel<TPixel>
    {
        void Fill(ReadOnlySpan<TPixel> row, Span<byte> destination, in Plan plan);

        /// <summary>Go's RGBA() of one filtered output pixel.</summary>
        (uint R, uint G, uint B) Output(ReadOnlySpan<byte> pixel);
    }

    /// <summary>A source filtered by nfnt's 16-bit paths, whose output pixels are the 16-bit RGBA() values.</summary>
    private interface IPixels16<TPixel> : IPixels<TPixel> where TPixel : unmanaged, IPixel<TPixel>
    {
        void Fill(ReadOnlySpan<TPixel> row, Span<ushort> destination, in Plan plan);
    }

    private sealed class Gray8Pixels : IPixels8<L8>
    {
        public static readonly Gray8Pixels Instance = new();
        public int Channels => 1;
        public L8 FromRgba64(Rgba64 pixel) => new((byte)(pixel.R >> 8));

        public void Fill(ReadOnlySpan<L8> row, Span<byte> destination, in Plan plan)
        {
            for (var i = 0; i < row.Length; i++)
                destination[i] = row[i].Value;
        }

        public (uint R, uint G, uint B) Output(ReadOnlySpan<byte> pixel) => Widen(pixel[0], pixel[0], pixel[0]);

        public (uint R, uint G, uint B) GoRgba(L8 pixel, in Plan plan) => Widen(pixel.Value, pixel.Value, pixel.Value);
    }

    private sealed class Gray16Pixels : IPixels16<L16>
    {
        public static readonly Gray16Pixels Instance = new();
        public int Channels => 1;
        public L16 FromRgba64(Rgba64 pixel) => new(pixel.R);

        public void Fill(ReadOnlySpan<L16> row, Span<ushort> destination, in Plan plan)
        {
            for (var i = 0; i < row.Length; i++)
                destination[i] = row[i].Value;
        }

        public (uint R, uint G, uint B) GoRgba(L16 pixel, in Plan plan) => (pixel.Value, pixel.Value, pixel.Value);
    }

    private sealed class RgbaPixels : IPixels8<Rgba32>
    {
        public static readonly RgbaPixels Instance = new();
        public int Channels => 4;
        public Rgba32 FromRgba64(Rgba64 pixel) => Narrow(pixel);

        // resizeNRGBA premultiplies each 8-bit sample by alpha with integer division before
        // filtering. For opaque images that is the identity, which makes it resizeRGBA too.
        public void Fill(ReadOnlySpan<Rgba32> row, Span<byte> destination, in Plan plan)
        {
            for (var i = 0; i < row.Length; i++)
            {
                var p = row[i];
                int a = plan.ForceOpaque ? 0xff : p.A;
                destination[i * 4] = (byte)(p.R * a / 0xff);
                destination[i * 4 + 1] = (byte)(p.G * a / 0xff);
                destination[i * 4 + 2] = (byte)(p.B * a / 0xff);
                destination[i * 4 + 3] = (byte)a;
            }
        }

        public (uint R, uint G, uint B) Output(ReadOnlySpan<byte> pixel) => Widen(pixel[0], pixel[1], pixel[2]);

        // color.NRGBA's RGBA(): widen to 16 bits, then premultiply by the widened alpha.
        public (uint R, uint G, uint B) GoRgba(Rgba32 pixel, in Plan plan)
        {
            uint a = plan.ForceOpaque ? 0xffffu : pixel.A * 0x101u;
            return (pixel.R * 0x101u * a / 0xffff, pixel.G * 0x101u * a / 0xffff, pixel.B * 0x101u * a / 0xffff);
        }
    }

    private sealed class Rgba64Pixels : IPixels16<Rgba64>
    {
        public static readonly Rgba64Pixels Instance = new();
        public int Channels => 4;
        public Rgba64 FromRgba64(Rgba64 pixel) => pixel;

        // resizeNRGBA64's premultiplication, which is also color.NRGBA64's RGBA().
        public void Fill(ReadOnlySpan<Rgba64> row, Span<ushort> destination, in Plan plan)
        {
            for (var i = 0; i < row.Length; i++)
                Premultiplied(row[i], Alpha(row[i], plan), destination.Slice(i * 4, 4));
        }

        public (uint R, uint G, uint B) GoRgba(Rgba64 pixel, in Plan plan)
        {
            uint a = Alpha(pixel, plan);
            return (pixel.R * a / 0xffff, pixel.G * a / 0xffff, pixel.B * a / 0xffff);
        }

        private static ushort Alpha(Rgba64 pixel, in Plan plan)
        {
            if (plan.ColorKey16 is not { } key)
                return pixel.A;
            var keyed = key.Length == 1
                ? pixel.R == key[0]
                : pixel.R == key[0] && pixel.G == key[1] && pixel.B == key[2];
            return keyed ? (ushort)0 : (ushort)0xffff;
        }
    }

    private sealed class GenericPixels : IPixels16<Rgba64>
    {
        public static readonly GenericPixels Instance = new();
        public int Channels => 4;
        public Rgba64 FromRgba64(Rgba64 pixel) => pixel;

        // resizeGeneric filters the 16-bit premultiplied values each pixel's RGBA() returns. Palette
        // entries are color.RGBA (opaque), color.NRGBA (from a PNG tRNS chunk) or color.RGBA64 (TIFF's
        // 16-bit colour map), and GIF's transparent entry is color.RGBA{}. Loaded as Rgba64, an 8-bit
        // entry widens by 257, so color.NRGBA64's conversion gives the right result for all of them.
        public void Fill(ReadOnlySpan<Rgba64> row, Span<ushort> destination, in Plan plan)
        {
            for (var i = 0; i < row.Length; i++)
                Premultiplied(row[i], row[i].A, destination.Slice(i * 4, 4));
        }

        public (uint R, uint G, uint B) GoRgba(Rgba64 pixel, in Plan plan)
            => ((uint)pixel.R * pixel.A / 0xffff, (uint)pixel.G * pixel.A / 0xffff, (uint)pixel.B * pixel.A / 0xffff);
    }

    private sealed class YCbCrPixels : IPixels8<Rgba32>
    {
        public static readonly YCbCrPixels Instance = new();
        public int Channels => 3;
        public Rgba32 FromRgba64(Rgba64 pixel) => Narrow(pixel);

        public void Fill(ReadOnlySpan<Rgba32> row, Span<byte> destination, in Plan plan)
        {
            for (var i = 0; i < row.Length; i++)
            {
                var (y, cb, cr) = RgbToYCbCr(row[i]);
                destination[i * 3] = y;
                destination[i * 3 + 1] = cb;
                destination[i * 3 + 2] = cr;
            }
        }

        // nfnt returns a 4:4:4 *image.YCbCr; goimagehash reads it through YCbCr.RGBA().
        public (uint R, uint G, uint B) Output(ReadOnlySpan<byte> pixel) => YCbCrToRgba64(pixel[0], pixel[1], pixel[2]);

        public (uint R, uint G, uint B) GoRgba(Rgba32 pixel, in Plan plan)
        {
            var (y, cb, cr) = RgbToYCbCr(pixel);
            return YCbCrToRgba64(y, cb, cr);
        }
    }

    private sealed class NYCbCrAPixels : IPixels16<Rgba32>
    {
        public static readonly NYCbCrAPixels Instance = new();
        public int Channels => 4;
        public Rgba32 FromRgba64(Rgba64 pixel) => Narrow(pixel);

        public void Fill(ReadOnlySpan<Rgba32> row, Span<ushort> destination, in Plan plan)
        {
            for (var i = 0; i < row.Length; i++)
            {
                var (r, g, b) = GoRgba(row[i], plan);
                destination[i * 4] = (ushort)r;
                destination[i * 4 + 1] = (ushort)g;
                destination[i * 4 + 2] = (ushort)b;
                destination[i * 4 + 3] = (ushort)(row[i].A * 0x101);
            }
        }

        // color.NYCbCrA's RGBA(): the opaque YCbCr colour, premultiplied by the widened alpha.
        public (uint R, uint G, uint B) GoRgba(Rgba32 pixel, in Plan plan)
        {
            var (y, cb, cr) = RgbToYCbCr(pixel);
            var (r, g, b) = YCbCrToRgba64(y, cb, cr);
            var a = pixel.A * 0x101u;
            return (r * a / 0xffff, g * a / 0xffff, b * a / 0xffff);
        }
    }

    private static Rgba32 Narrow(Rgba64 pixel) => new((byte)(pixel.R >> 8), (byte)(pixel.G >> 8), (byte)(pixel.B >> 8), (byte)(pixel.A >> 8));

    private static (uint R, uint G, uint B) Widen(byte r, byte g, byte b) => (r * 0x101u, g * 0x101u, b * 0x101u);

    private static void Premultiplied(Rgba64 pixel, long alpha, Span<ushort> destination)
    {
        destination[0] = (ushort)(pixel.R * alpha / 0xffff);
        destination[1] = (ushort)(pixel.G * alpha / 0xffff);
        destination[2] = (ushort)(pixel.B * alpha / 0xffff);
        destination[3] = (ushort)alpha;
    }

    /// <summary>Go's color.RGBToYCbCr.</summary>
    private static (byte Y, byte Cb, byte Cr) RgbToYCbCr(Rgba32 pixel)
    {
        int r = pixel.R, g = pixel.G, b = pixel.B;
        var y = (19595 * r + 38470 * g + 7471 * b + (1 << 15)) >> 16;
        var cb = -11056 * r - 21712 * g + 32768 * b + (257 << 15);
        cb = ((uint)cb & 0xff000000) == 0 ? cb >> 16 : ~(cb >> 31);
        var cr = 32768 * r - 27440 * g - 5328 * b + (257 << 15);
        cr = ((uint)cr & 0xff000000) == 0 ? cr >> 16 : ~(cr >> 31);
        return ((byte)y, (byte)cb, (byte)cr);
    }

    /// <summary>Go's color.YCbCr.RGBA().</summary>
    private static (uint R, uint G, uint B) YCbCrToRgba64(byte y, byte cb, byte cr)
    {
        var yy = y * 0x10101;
        int cb1 = cb - 128, cr1 = cr - 128;
        return (Channel(yy + 91881 * cr1), Channel(yy - 22554 * cb1 - 46802 * cr1), Channel(yy + 116130 * cb1));

        static uint Channel(int value) => ((uint)value & 0xff000000) == 0 ? (uint)(value >> 8) : (uint)(~(value >> 31) & 0xffff);
    }

    // ---- Resize and grayscale ----

    /// <summary>
    /// Hashes an image already in memory, such as a video sprite, the way goimagehash hashes the
    /// <c>*image.NRGBA</c> montage Stash pastes its BMP frames into.
    /// </summary>
    public static ulong ComputeNrgba(PixelBuffer<Rgba32> image)
        => GoImageHash.Hash(Reduce(image, new Plan(GoImageKind.Rgba), RgbaPixels.Instance));

    private static double[] Reduce<TPixel>(PixelBuffer<TPixel> frame, Plan plan, IPixels<TPixel> source)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var bounds = plan.Crop is { } crop ? PixelRect.Intersect(crop, frame.Bounds) : frame.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidImageException("The image has no pixels to hash.");

        ReadOnlySpan<TPixel> Row(int y) => frame.Row(bounds.Y + y).Slice(bounds.X, bounds.Width);

        const int size = GoImageHash.Size;
        var gray = new double[size * size];

        if (bounds.Width == size && bounds.Height == size)
        {
            // nfnt/resize returns an image that is already the target size unchanged, bounds and all,
            // and goimagehash reads its pixels at (0,0)–(63,63). For a GIF frame placed elsewhere on the
            // screen those coordinates are partly outside the frame, where Go reports palette entry 0.
            var background = source.FromRgba64(plan.Background);
            for (var y = 0; y < size; y++)
            {
                var screenRow = frame.Row(y);
                for (var x = 0; x < size; x++)
                {
                    var pixel = bounds.Contains(x, y) || plan.Crop is null ? screenRow[x] : background;
                    var (r, g, b) = source.GoRgba(pixel, plan);
                    gray[y * size + x] = GoImageHash.PixelToGray(r, g, b);
                }
            }
            return gray;
        }

        var channels = source.Channels;
        if (source is IPixels16<TPixel> wide)
        {
            var result = NfntBilinear.Resize16(bounds.Width, bounds.Height, channels,
                (y, destination) => wide.Fill(Row(y), destination, plan));
            for (var i = 0; i < size * size; i++)
            {
                var p = result.AsSpan(i * channels, channels);
                gray[i] = channels == 1 ? GoImageHash.PixelToGray(p[0], p[0], p[0]) : GoImageHash.PixelToGray(p[0], p[1], p[2]);
            }
        }
        else
        {
            var narrow = (IPixels8<TPixel>)source;
            var result = NfntBilinear.Resize8(bounds.Width, bounds.Height, channels,
                (y, destination) => narrow.Fill(Row(y), destination, plan));
            for (var i = 0; i < size * size; i++)
            {
                var (r, g, b) = narrow.Output(result.AsSpan(i * channels, channels));
                gray[i] = GoImageHash.PixelToGray(r, g, b);
            }
        }
        return gray;
    }
}
