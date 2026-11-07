using System.Runtime.InteropServices;

namespace Cove.Api.Imaging;

/// <summary>How a decoded pixel is laid out in memory: channel count and bits per channel.</summary>
public enum PixelLayout
{
    /// <summary>One 8-bit luminance channel.</summary>
    L8,
    /// <summary>One 16-bit luminance channel.</summary>
    L16,
    /// <summary>8-bit red, green, blue and straight (not premultiplied) alpha.</summary>
    Rgba32,
    /// <summary>16-bit red, green, blue and straight alpha.</summary>
    Rgba64,
}

public interface IPixel<TSelf> where TSelf : unmanaged, IPixel<TSelf>
{
    static abstract PixelLayout Layout { get; }
}

[StructLayout(LayoutKind.Sequential)]
public record struct L8(byte Value) : IPixel<L8>
{
    public static PixelLayout Layout => PixelLayout.L8;
}

[StructLayout(LayoutKind.Sequential)]
public record struct L16(ushort Value) : IPixel<L16>
{
    public static PixelLayout Layout => PixelLayout.L16;
}

[StructLayout(LayoutKind.Sequential)]
public record struct Rgba32(byte R, byte G, byte B, byte A = 255) : IPixel<Rgba32>
{
    public static PixelLayout Layout => PixelLayout.Rgba32;
}

[StructLayout(LayoutKind.Sequential)]
public record struct Rgba64(ushort R, ushort G, ushort B, ushort A = ushort.MaxValue) : IPixel<Rgba64>
{
    public static PixelLayout Layout => PixelLayout.Rgba64;
}

/// <summary>A decoded image held as rows of pixels, top to bottom.</summary>
public sealed class PixelBuffer<TPixel> where TPixel : unmanaged, IPixel<TPixel>
{
    public PixelBuffer(int width, int height)
        : this(width, height, new TPixel[checked(width * height)])
    {
    }

    public PixelBuffer(int width, int height, TPixel fill)
        : this(width, height)
    {
        Pixels.AsSpan().Fill(fill);
    }

    public PixelBuffer(int width, int height, TPixel[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (pixels.Length != checked(width * height))
            throw new ArgumentException($"Expected {width * height} pixels, got {pixels.Length}.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public TPixel[] Pixels { get; }
    public PixelRect Bounds => new(0, 0, Width, Height);

    public Span<TPixel> Row(int y) => Pixels.AsSpan(y * Width, Width);

    public ref TPixel this[int x, int y] => ref Pixels[y * Width + x];

    public PixelBuffer<TPixel> Clone() => new(Width, Height, (TPixel[])Pixels.Clone());
}
