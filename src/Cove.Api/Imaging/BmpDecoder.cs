using System.Buffers.Binary;
using System.Numerics;

namespace Cove.Api.Imaging;

/// <summary>
/// A decoder for Windows bitmaps, which libvips reads only through ImageMagick. It covers the
/// variants in common use: 1, 2, 4 and 8-bit palettes, 16, 24 and 32-bit colour, bit-field masks,
/// RLE4 and RLE8, and bottom-up or top-down rows, with any of the core, info, V2 to V5 and OS/2
/// headers. Embedded JPEG or PNG payloads and Huffman 1D are rejected as unsupported.
/// </summary>
internal static class BmpDecoder
{
    private const int Rgb = 0;
    private const int Rle8 = 1;
    private const int Rle4 = 2;
    private const int BitFields = 3;
    private const int AlphaBitFields = 6;

    /// <summary>The largest image decoded, in pixels: enough for 32k × 32k without exhausting memory on a forged header.</summary>
    private const long MaxPixels = 1L << 30;

    public static bool IsBmp(ReadOnlySpan<byte> data) => data.Length >= 18 && data[0] == 'B' && data[1] == 'M';

    public static (int Width, int Height) ReadSize(ReadOnlySpan<byte> data)
    {
        var header = ReadHeader(data);
        return (header.Width, header.Height);
    }

    public static PixelBuffer<Rgba32> Decode(ReadOnlySpan<byte> data)
    {
        var header = ReadHeader(data);
        var pixels = new PixelBuffer<Rgba32>(header.Width, header.Height);
        if (header.PixelOffset < 0 || header.PixelOffset > data.Length)
            throw new InvalidImageException("The bitmap's pixel data offset is outside the file.");
        var payload = data[header.PixelOffset..];

        switch (header.Compression)
        {
            case Rle8 when header.BitsPerPixel == 8:
            case Rle4 when header.BitsPerPixel == 4:
                DecodeRle(payload, header, pixels);
                break;
            case Rgb or BitFields or AlphaBitFields:
                DecodeUncompressed(payload, header, pixels);
                break;
            default:
                throw new UnsupportedImageFormatException($"Bitmap compression {header.Compression} with {header.BitsPerPixel} bits per pixel is not supported.");
        }
        return pixels;
    }

    private sealed record Header(
        int Width,
        int Height,
        bool TopDown,
        int BitsPerPixel,
        int Compression,
        int PixelOffset,
        Rgba32[] Palette,
        uint RedMask,
        uint GreenMask,
        uint BlueMask,
        uint AlphaMask);

    private static Header ReadHeader(ReadOnlySpan<byte> data)
    {
        if (!IsBmp(data))
            throw new UnsupportedImageFormatException("The data is not a bitmap.");

        var pixelOffset = BinaryPrimitives.ReadInt32LittleEndian(data[10..]);
        var infoSize = BinaryPrimitives.ReadInt32LittleEndian(data[14..]);
        if (infoSize is not (12 or 16 or 40 or 52 or 56 or 64 or 108 or 124) || data.Length < 14 + infoSize)
            throw new InvalidImageException($"Unrecognised bitmap header of {infoSize} bytes.");
        var info = data.Slice(14, infoSize);

        int width, height, bitsPerPixel, compression = Rgb;
        uint colorsUsed = 0;
        if (infoSize == 12)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(info[4..]);
            height = BinaryPrimitives.ReadInt16LittleEndian(info[6..]);
            bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(info[10..]);
        }
        else
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(info[4..]);
            height = BinaryPrimitives.ReadInt32LittleEndian(info[8..]);
            bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(info[14..]);
            if (infoSize >= 20)
                compression = BinaryPrimitives.ReadInt32LittleEndian(info[16..]);
            if (infoSize >= 36)
                colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(info[32..]);
        }

        var topDown = height < 0;
        if (topDown)
        {
            if (height == int.MinValue)
                throw new InvalidImageException("The bitmap's height is out of range.");
            height = -height;
        }
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            throw new InvalidImageException($"The bitmap's dimensions {width}×{height} are out of range.");
        if (bitsPerPixel is not (1 or 2 or 4 or 8 or 16 or 24 or 32))
            throw new UnsupportedImageFormatException($"Bitmaps with {bitsPerPixel} bits per pixel are not supported.");

        uint red, green, blue, alpha = 0;
        if (compression is BitFields or AlphaBitFields)
        {
            // An info header keeps its masks after itself; V2 and later headers hold them inside.
            var masks = infoSize >= 52 ? info[40..] : data.Length >= 14 + infoSize + 16 ? data[(14 + infoSize)..] : default;
            if (masks.Length < 12)
                throw new InvalidImageException("The bitmap's colour masks are missing.");
            red = BinaryPrimitives.ReadUInt32LittleEndian(masks);
            green = BinaryPrimitives.ReadUInt32LittleEndian(masks[4..]);
            blue = BinaryPrimitives.ReadUInt32LittleEndian(masks[8..]);
            if (masks.Length >= 16 && (infoSize >= 56 || compression == AlphaBitFields))
                alpha = BinaryPrimitives.ReadUInt32LittleEndian(masks[12..]);
        }
        else if (bitsPerPixel == 16)
        {
            (red, green, blue) = (0x7C00, 0x03E0, 0x001F);
        }
        else
        {
            (red, green, blue) = (0x00FF0000, 0x0000FF00, 0x000000FF);
            // Only V4 and V5 headers declare the fourth byte alpha; in older headers it is reserved and
            // usually zero, so it is read as alpha only when some pixel sets it (see DecodeUncompressed).
            if (bitsPerPixel == 32)
                alpha = 0xFF000000;
        }

        var palette = Array.Empty<Rgba32>();
        if (bitsPerPixel <= 8)
        {
            var entrySize = infoSize == 12 ? 3 : 4;
            var count = colorsUsed is > 0 and <= 256 ? (int)colorsUsed : 1 << bitsPerPixel;
            var tableStart = 14 + infoSize + (compression == BitFields && infoSize == 40 ? 12 : 0);
            count = Math.Min(count, Math.Max(0, (data.Length - tableStart) / entrySize));
            palette = new Rgba32[1 << bitsPerPixel];
            for (var i = 0; i < count; i++)
            {
                var entry = data.Slice(tableStart + i * entrySize, 3);
                palette[i] = new Rgba32(entry[2], entry[1], entry[0]);
            }
            for (var i = count; i < palette.Length; i++)
                palette[i] = new Rgba32(0, 0, 0);
        }

        return new Header(width, height, topDown, bitsPerPixel, compression, pixelOffset, palette, red, green, blue, alpha);
    }

    private static void DecodeUncompressed(ReadOnlySpan<byte> payload, Header header, PixelBuffer<Rgba32> pixels)
    {
        var stride = (int)(((long)header.BitsPerPixel * header.Width + 31) / 32 * 4);
        if ((long)stride * header.Height > payload.Length)
        {
            // A bitmap may omit the padding after its last row.
            var lastRow = ((long)header.BitsPerPixel * header.Width + 7) / 8;
            if ((long)stride * (header.Height - 1) + lastRow > payload.Length)
                throw new InvalidImageException("The bitmap's pixel data is truncated.");
        }

        var red = new Channel(header.RedMask);
        var green = new Channel(header.GreenMask);
        var blue = new Channel(header.BlueMask);
        var alpha = new Channel(header.AlphaMask);
        if (header.BitsPerPixel == 32 && header.Compression == Rgb && !AnyAlpha(payload, stride, header))
            alpha = new Channel(0);

        for (var row = 0; row < header.Height; row++)
        {
            var source = payload.Slice(row * stride, Math.Min(stride, payload.Length - row * stride));
            var target = pixels.Row(header.TopDown ? row : header.Height - 1 - row);
            switch (header.BitsPerPixel)
            {
                case <= 8:
                {
                    var bits = header.BitsPerPixel;
                    var mask = (1 << bits) - 1;
                    for (var x = 0; x < target.Length; x++)
                    {
                        var bit = x * bits;
                        var index = (source[bit >> 3] >> (8 - bits - (bit & 7))) & mask;
                        target[x] = header.Palette[index];
                    }
                    break;
                }
                case 24:
                    for (var x = 0; x < target.Length; x++)
                        target[x] = new Rgba32(source[x * 3 + 2], source[x * 3 + 1], source[x * 3]);
                    break;
                default:
                {
                    var bytes = header.BitsPerPixel / 8;
                    for (var x = 0; x < target.Length; x++)
                    {
                        var value = bytes == 2
                            ? BinaryPrimitives.ReadUInt16LittleEndian(source[(x * 2)..])
                            : BinaryPrimitives.ReadUInt32LittleEndian(source[(x * 4)..]);
                        target[x] = new Rgba32(red.Read(value), green.Read(value), blue.Read(value), alpha.Mask == 0 ? (byte)255 : alpha.Read(value));
                    }
                    break;
                }
            }
        }
    }

    /// <summary>Whether any pixel of a 32-bit bitmap sets its fourth byte.</summary>
    private static bool AnyAlpha(ReadOnlySpan<byte> payload, int stride, Header header)
    {
        for (var row = 0; row < header.Height; row++)
        {
            var start = row * stride;
            var end = Math.Min(start + header.Width * 4, payload.Length);
            for (var i = start + 3; i < end; i += 4)
            {
                if (payload[i] != 0)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// RLE8 and RLE4: encoded runs, absolute runs, end-of-line, delta and end-of-bitmap escapes.
    /// Pixels a delta or an early end skips are opaque black, as ImageSharp left them.
    /// </summary>
    private static void DecodeRle(ReadOnlySpan<byte> payload, Header header, PixelBuffer<Rgba32> pixels)
    {
        pixels.Pixels.AsSpan().Fill(new Rgba32(0, 0, 0));
        var four = header.BitsPerPixel == 4;
        int x = 0, y = 0, i = 0;

        void Put(int index)
        {
            if (x < header.Width && y < header.Height)
                pixels[x, header.TopDown ? y : header.Height - 1 - y] = header.Palette[index];
            x++;
        }

        while (i + 1 < payload.Length && y < header.Height)
        {
            int count = payload[i], value = payload[i + 1];
            i += 2;
            if (count > 0)
            {
                for (var n = 0; n < count; n++)
                    Put(four ? (n % 2 == 0 ? value >> 4 : value & 0x0F) : value);
                continue;
            }

            switch (value)
            {
                case 0:
                    x = 0;
                    y++;
                    break;
                case 1:
                    return;
                case 2:
                    if (i + 1 >= payload.Length)
                        return;
                    x += payload[i];
                    y += payload[i + 1];
                    i += 2;
                    break;
                default:
                {
                    var length = four ? (value + 1) / 2 : value;
                    if (i + length > payload.Length)
                        throw new InvalidImageException("The bitmap's run-length data is truncated.");
                    for (var n = 0; n < value; n++)
                        Put(four ? (n % 2 == 0 ? payload[i + n / 2] >> 4 : payload[i + n / 2] & 0x0F) : payload[i + n]);
                    // Absolute runs are padded to a 16-bit boundary.
                    i += length + (length & 1);
                    break;
                }
            }
        }
    }

    /// <summary>One colour mask, read and scaled to 8 bits the way 5-bit channels are conventionally widened.</summary>
    private readonly struct Channel
    {
        private readonly int _shift;
        private readonly int _bits;

        public Channel(uint mask)
        {
            Mask = mask;
            _shift = mask == 0 ? 0 : BitOperations.TrailingZeroCount(mask);
            _bits = BitOperations.PopCount(mask);
        }

        public uint Mask { get; }

        public byte Read(uint value)
        {
            if (Mask == 0)
                return 0;
            var sample = (value & Mask) >> _shift;
            if (_bits >= 8)
                return (byte)(sample >> (_bits - 8));
            // Replicate the high bits into the low ones, so a full-scale sample becomes 255.
            var widened = sample << (8 - _bits);
            for (var filled = _bits; filled < 8; filled += _bits)
                widened |= sample << (8 - _bits) >> filled;
            return (byte)widened;
        }
    }
}
