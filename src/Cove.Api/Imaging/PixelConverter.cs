using System.Runtime.InteropServices;
using NetVips;
using VipsImage = NetVips.Image;

namespace Cove.Api.Imaging;

/// <summary>
/// Converts decoded samples to Cove's pixel layouts. Widening is exact (v × 257); narrowing rounds,
/// and colour becomes luminance with BT.709 weights, both as ImageSharp did. CMYK becomes RGB
/// with Go's <c>color.CMYK</c> conversion, without colour management.
/// </summary>
internal static class PixelConverter
{
    private enum Model
    {
        Gray,
        Rgb,
        Cmyk,
    }

    public static PixelBuffer<TPixel> FromRgba32<TPixel>(PixelBuffer<Rgba32> source) where TPixel : unmanaged, IPixel<TPixel>
    {
        if (TPixel.Layout == PixelLayout.Rgba32)
            return (PixelBuffer<TPixel>)(object)source;
        var samples = new Bytes(MemoryMarshal.AsBytes(source.Pixels.AsSpan()).ToArray());
        return Convert<TPixel, Bytes>(samples, source.Width, source.Height, Model.Rgb, bands: 4, alpha: true);
    }

    public static PixelBuffer<TPixel> FromRgba64<TPixel>(PixelBuffer<Rgba64> source) where TPixel : unmanaged, IPixel<TPixel>
    {
        if (TPixel.Layout == PixelLayout.Rgba64)
            return (PixelBuffer<TPixel>)(object)source;
        var samples = new Shorts(MemoryMarshal.Cast<Rgba64, ushort>(source.Pixels.AsSpan()).ToArray());
        return Convert<TPixel, Shorts>(samples, source.Width, source.Height, Model.Rgb, bands: 4, alpha: true);
    }

    public static PixelBuffer<TPixel> FromVips<TPixel>(VipsImage image) where TPixel : unmanaged, IPixel<TPixel>
    {
        var owned = new List<VipsImage>();
        try
        {
            var model = image.Interpretation switch
            {
                Enums.Interpretation.Cmyk when image.Bands >= 4 => Model.Cmyk,
                Enums.Interpretation.Bw or Enums.Interpretation.Grey16 when image.Bands <= 2 => Model.Gray,
                Enums.Interpretation.Srgb or Enums.Interpretation.Rgb or Enums.Interpretation.Rgb16 when image.Bands >= 3 => Model.Rgb,
                Enums.Interpretation.Multiband when image.Bands <= 2 => Model.Gray,
                Enums.Interpretation.Multiband when image.Bands <= 4 => Model.Rgb,
                _ => (Model?)null,
            };
            if (model is null)
            {
                var wide = image.Format != Enums.BandFormat.Uchar;
                image = Own(image.Colourspace(wide ? Enums.Interpretation.Rgb16 : Enums.Interpretation.Srgb));
                model = Model.Rgb;
            }

            var colourBands = model switch { Model.Gray => 1, Model.Rgb => 3, _ => 4 };
            var alpha = image.Bands > colourBands;
            var bands = Math.Min(image.Bands, colourBands + 1);
            if (image.Bands > bands)
                image = Own(image.ExtractBand(0, n: bands));

            switch (image.Format)
            {
                case Enums.BandFormat.Uchar:
                    return Convert<TPixel, Bytes>(new Bytes(image.WriteToMemory<byte>()), image.Width, image.Height, model.Value, bands, alpha);
                case Enums.BandFormat.Ushort:
                    return Convert<TPixel, Shorts>(new Shorts(image.WriteToMemory<ushort>()), image.Width, image.Height, model.Value, bands, alpha);
                default:
                    throw new InvalidImageException($"Images with {image.Format} samples are not supported.");
            }
        }
        finally
        {
            foreach (var item in owned)
                item.Dispose();
        }

        VipsImage Own(VipsImage next)
        {
            owned.Add(next);
            return next;
        }
    }

    private interface ISamples
    {
        int Length { get; }
        ushort Wide(int index);
        byte Narrow(int index);
    }

    private readonly struct Bytes(byte[] data) : ISamples
    {
        public int Length => data.Length;
        public ushort Wide(int index) => (ushort)(data[index] * 0x101);
        public byte Narrow(int index) => data[index];
    }

    private readonly struct Shorts(ushort[] data) : ISamples
    {
        public int Length => data.Length;
        public ushort Wide(int index) => data[index];
        public byte Narrow(int index) => To8(data[index]);
    }

    private static byte To8(uint value) => (byte)((value * 255 + 32895) >> 16);

    private static PixelBuffer<TPixel> Convert<TPixel, TSamples>(TSamples samples, int width, int height, Model model, int bands, bool alpha)
        where TPixel : unmanaged, IPixel<TPixel>
        where TSamples : struct, ISamples
    {
        if (samples.Length != width * height * bands)
            throw new InvalidImageException("The decoder returned an unexpected number of samples.");

        var buffer = new PixelBuffer<TPixel>(width, height);
        var count = width * height;
        switch (TPixel.Layout)
        {
            case PixelLayout.Rgba64:
            {
                var target = MemoryMarshal.Cast<TPixel, Rgba64>(buffer.Pixels.AsSpan());
                for (var i = 0; i < count; i++)
                {
                    var (r, g, b) = Wide(samples, i * bands, model);
                    target[i] = new Rgba64(r, g, b, alpha ? samples.Wide(i * bands + bands - 1) : ushort.MaxValue);
                }
                break;
            }
            case PixelLayout.Rgba32:
            {
                var target = MemoryMarshal.Cast<TPixel, Rgba32>(buffer.Pixels.AsSpan());
                for (var i = 0; i < count; i++)
                {
                    var (r, g, b) = Narrow(samples, i * bands, model);
                    target[i] = new Rgba32(r, g, b, alpha ? samples.Narrow(i * bands + bands - 1) : byte.MaxValue);
                }
                break;
            }
            case PixelLayout.L8:
            {
                var target = MemoryMarshal.Cast<TPixel, L8>(buffer.Pixels.AsSpan());
                for (var i = 0; i < count; i++)
                {
                    if (model == Model.Gray)
                    {
                        target[i] = new L8(samples.Narrow(i * bands));
                        continue;
                    }
                    var (r, g, b) = Narrow(samples, i * bands, model);
                    target[i] = new L8((byte)((r * .2126F) + (g * .7152F) + (b * .0722F) + 0.5F));
                }
                break;
            }
            case PixelLayout.L16:
            {
                var target = MemoryMarshal.Cast<TPixel, L16>(buffer.Pixels.AsSpan());
                for (var i = 0; i < count; i++)
                {
                    if (model == Model.Gray)
                    {
                        target[i] = new L16(samples.Wide(i * bands));
                        continue;
                    }
                    var (r, g, b) = Wide(samples, i * bands, model);
                    target[i] = new L16((ushort)((r * .2126F) + (g * .7152F) + (b * .0722F) + 0.5F));
                }
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(TPixel));
        }
        return buffer;
    }

    private static (ushort R, ushort G, ushort B) Wide<TSamples>(TSamples samples, int offset, Model model) where TSamples : struct, ISamples
    {
        switch (model)
        {
            case Model.Gray:
                var v = samples.Wide(offset);
                return (v, v, v);
            case Model.Rgb:
                return (samples.Wide(offset), samples.Wide(offset + 1), samples.Wide(offset + 2));
            default:
                // color.CMYK.RGBA(): each channel's ink is removed from white, scaled by the black.
                var w = 0xffffu - samples.Wide(offset + 3);
                return ((ushort)((0xffffu - samples.Wide(offset)) * w / 0xffff),
                    (ushort)((0xffffu - samples.Wide(offset + 1)) * w / 0xffff),
                    (ushort)((0xffffu - samples.Wide(offset + 2)) * w / 0xffff));
        }
    }

    private static (byte R, byte G, byte B) Narrow<TSamples>(TSamples samples, int offset, Model model) where TSamples : struct, ISamples
    {
        switch (model)
        {
            case Model.Gray:
                var v = samples.Narrow(offset);
                return (v, v, v);
            case Model.Rgb:
                return (samples.Narrow(offset), samples.Narrow(offset + 1), samples.Narrow(offset + 2));
            default:
                var (r, g, b) = Wide(samples, offset, model);
                return ((byte)(r >> 8), (byte)(g >> 8), (byte)(b >> 8));
        }
    }
}
