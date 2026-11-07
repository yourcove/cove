using Cove.Api.Imaging;
using NetVips;
using VipsImage = NetVips.Image;

namespace Cove.ApiTests.Infrastructure;

public static class ApiTestImages
{
    private const string OnePixelPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGD4DwABBAEAX+XDSwAAAABJRU5ErkJggg==";
    private const string RedPixelPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==";
    private const string BluePixelPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYPj/HwADAgH/5ncLrgAAAABJRU5ErkJggg==";

    public static byte[] OnePixelPng()
        => Convert.FromBase64String(OnePixelPngBase64);

    public static byte[] RedPixelPng()
        => Convert.FromBase64String(RedPixelPngBase64);

    public static byte[] BluePixelPng()
        => Convert.FromBase64String(BluePixelPngBase64);

    /// <summary>
    /// A JPEG whose stored grid is <paramref name="width"/>x<paramref name="height"/> and which
    /// carries the given EXIF orientation, the way a phone records a photo it wants turned before
    /// display. Orientations 5 to 8 carry a quarter turn, so a viewer sees the transposed size.
    /// </summary>
    public static byte[] OrientedJpeg(int width, int height, ushort orientation)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (orientation is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(orientation), "EXIF orientation is 1 through 8.");

        using var black = VipsImage.Black(width, height, bands: 3);
        using var rgb = black.Cast(Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb);
        using var oriented = rgb.Mutate(image => image.Set(GValue.GIntType, "orientation", (int)orientation));
        return oriented.JpegsaveBuffer(q: 75);
    }

    /// <summary>A solid <paramref name="width"/>x<paramref name="height"/> image as jpeg, png or (lossy) webp.</summary>
    public static byte[] Solid(string format, int width, int height, Rgba32 color)
    {
        var pixels = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new PixelBuffer<Rgba32>(width, height, color).Pixels.AsSpan());
        using var raw = VipsImage.NewFromMemoryCopy(pixels, width, height, 4, Enums.BandFormat.Uchar);
        using var image = raw.Copy(interpretation: Enums.Interpretation.Srgb);
        switch (format)
        {
            case "jpeg":
                using (var opaque = image.ExtractBand(0, n: 3))
                    return opaque.JpegsaveBuffer(q: 75);
            case "png":
                return image.PngsaveBuffer();
            case "webp":
                return image.WebpsaveBuffer(q: 75);
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, null);
        }
    }

    /// <summary>The stored size of an encoded image.</summary>
    public static (int Width, int Height) Size(byte[] data)
    {
        var header = ImageCodec.Default.Identify(new MemoryStream(data, writable: false));
        return (header.Width, header.Height);
    }
}
