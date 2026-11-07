using Cove.Api.Imaging;
using NetVips;
using VipsImage = NetVips.Image;

namespace Cove.Tests;

/// <summary>Encodes and inspects images for tests, through libvips and Cove's own codec.</summary>
internal static class TestImages
{
    /// <summary>A transparent black image, the colour a fresh bitmap starts as.</summary>
    public static PixelBuffer<Rgba32> Blank(int width, int height) => new(width, height, new Rgba32(0, 0, 0, 0));

    public static PixelBuffer<Rgba32> Solid(int width, int height, Rgba32 color) => new(width, height, color);

    /// <summary>
    /// Encodes <paramref name="pixels"/> as <paramref name="format"/> (jpeg, png, gif, webp or tiff).
    /// JPEG drops alpha; WebP is lossless unless a quality is given.
    /// </summary>
    public static byte[] Encode(PixelBuffer<Rgba32> pixels, string format, int? quality = null, int? orientation = null)
    {
        using var image = ToVips(pixels, orientation);
        switch (format)
        {
            case "jpeg" or "jpg":
                using (var rgb = image.ExtractBand(0, n: 3))
                    return rgb.JpegsaveBuffer(q: quality ?? 75);
            case "png":
                return image.PngsaveBuffer();
            case "gif":
                return image.GifsaveBuffer();
            case "webp" when quality is { } q:
                // An opaque image is written without an alpha channel, as encoders do.
                if (pixels.Pixels.All(p => p.A == 255))
                {
                    using var rgb = image.ExtractBand(0, n: 3);
                    return rgb.WebpsaveBuffer(q: q, keep: Enums.ForeignKeep.None);
                }
                return image.WebpsaveBuffer(q: q);
            case "webp":
                return image.WebpsaveBuffer(lossless: true);
            case "tiff":
                return image.TiffsaveBuffer();
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, null);
        }
    }

    public static byte[] Jpeg(int width, int height, Rgba32? color = null, int? quality = null, int? orientation = null)
        => Encode(color is { } c ? Solid(width, height, c) : Blank(width, height), "jpeg", quality, orientation);

    public static byte[] Png(int width, int height, Rgba32? color = null)
        => Encode(color is { } c ? Solid(width, height, c) : Blank(width, height), "png");

    /// <summary>A GIF or lossless WebP animation of the given frames, which must share one size.</summary>
    public static byte[] Animated(string format, params PixelBuffer<Rgba32>[] frames)
    {
        var pages = frames.Select(frame => ToVips(frame, orientation: null)).ToArray();
        try
        {
            using var strip = VipsImage.Arrayjoin(pages, across: 1);
            using var animation = strip.Mutate(image =>
            {
                image.Set(GValue.GIntType, "page-height", frames[0].Height);
                image.Set(GValue.ArrayIntType, "delay", Enumerable.Repeat(100, frames.Length).ToArray());
            });
            return format switch
            {
                "gif" => animation.GifsaveBuffer(),
                "webp" => animation.WebpsaveBuffer(lossless: true),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
            };
        }
        finally
        {
            foreach (var page in pages)
                page.Dispose();
        }
    }

    /// <summary>
    /// Rewraps a still lossy WebP as an animation of that one frame (VP8X with the animation flag, ANIM and a
    /// single ANMF), which decoders that cannot read animation reject although it shows one picture.
    /// </summary>
    public static byte[] SingleFrameAnimatedWebp(byte[] stillWebp, int width, int height)
    {
        var vp8 = stillWebp.AsSpan(12);
        if (!vp8[..4].SequenceEqual("VP8 "u8))
            throw new ArgumentException("Expected a simple lossy WebP.", nameof(stillWebp));
        var frame = vp8.ToArray();

        var body = new List<byte>();
        void Chunk(string type, byte[] data)
        {
            body.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
            body.AddRange(BitConverter.GetBytes((uint)data.Length));
            body.AddRange(data);
            if (data.Length % 2 == 1)
                body.Add(0);
        }
        static byte[] U24(int value) => [(byte)value, (byte)(value >> 8), (byte)(value >> 16)];

        Chunk("VP8X", [0x02, 0, 0, 0, .. U24(width - 1), .. U24(height - 1)]);
        Chunk("ANIM", [0, 0, 0, 0, 0, 0]);
        Chunk("ANMF", [.. U24(0), .. U24(0), .. U24(width - 1), .. U24(height - 1), .. U24(100), 0, .. frame]);

        var file = new List<byte>();
        file.AddRange("RIFF"u8.ToArray());
        file.AddRange(BitConverter.GetBytes((uint)(4 + body.Count)));
        file.AddRange("WEBP"u8.ToArray());
        file.AddRange(body);
        return [.. file];
    }

    public static async Task WriteAsync(string path, byte[] data)
        => await File.WriteAllBytesAsync(path, data, TestContext.Current.CancellationToken);

    public static PixelBuffer<Rgba32> Decode(byte[] data) => Decode(new MemoryStream(data, writable: false));

    public static PixelBuffer<Rgba32> Decode(Stream stream) => ImageCodec.Default.DecodePixels<Rgba32>(stream);

    public static ImageHeader Identify(byte[] data) => ImageCodec.Default.Identify(new MemoryStream(data, writable: false));

    public static ImageHeader Identify(Stream stream) => ImageCodec.Default.Identify(stream);

    private static VipsImage ToVips(PixelBuffer<Rgba32> pixels, int? orientation)
    {
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.Pixels.AsSpan());
        using var raw = VipsImage.NewFromMemoryCopy(bytes, pixels.Width, pixels.Height, 4, Enums.BandFormat.Uchar);
        using var srgb = raw.Copy(interpretation: Enums.Interpretation.Srgb);
        return orientation is { } value ? srgb.Mutate(image => image.Set(GValue.GIntType, "orientation", value)) : srgb.Copy();
    }
}
