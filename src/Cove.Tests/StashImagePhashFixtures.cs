using System.Buffers.Binary;
using System.IO.Compression;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Tiff.Constants;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace Cove.Tests;

/// <summary>
/// Deterministic images in every encoding that makes Go's decoders return a different image type,
/// for <see cref="StashImagePhashTests"/>. PNG, GIF and BMP are written byte by byte so the colour
/// type, bit depth, tRNS key, palette, frame offset and BMP header are exactly what each case names;
/// interlaced PNG, lossless WebP and TIFF use ImageSharp's lossless encoders, whose decoded pixels do
/// not depend on how the encoder compresses them.
/// </summary>
internal static class StashImagePhashFixtures
{
    public static byte[] Create(string kind, int width, int height)
    {
        // Noise-free shapes put DCT coefficients level with the median, where the last bit of the
        // DCT's cosine table decides the hash bit.
        if (kind == "png_g8_diagonal")
            return Png(width, height, 0, 8, (x, y) => [(ushort)((x + y) * 255 / (width + height - 2))], null, null);
        if (kind == "png_g8_box")
            return Png(width, height, 0, 8, (x, y) => [(ushort)(Math.Abs(x - width / 2) < width / 6 && Math.Abs(y - height / 2) < height / 6 ? 255 : 0)], null, null);

        var pixels = new (int R, int G, int B, int A)[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                pixels[y * width + x] = kind.EndsWith("_solid", StringComparison.Ordinal) ? (140, 90, 200, 255) : Pixel(x, y, width, height);
        (int R, int G, int B, int A) At(int x, int y) => pixels[y * width + x];

        var palette = new List<(byte R, byte G, byte B)>();
        for (var r = 0; r < 6; r++)
            for (var g = 0; g < 6; g++)
                for (var b = 0; b < 6; b++)
                    palette.Add(((byte)(r * 51), (byte)(g * 51), (byte)(b * 51)));
        byte Index(int x, int y)
        {
            var p = At(x, y);
            return (byte)(p.R * 6 / 256 * 36 + p.G * 6 / 256 * 6 + p.B * 6 / 256);
        }

        // Transparency keys cover a whole region, so they change the hash rather than a pixel or two.
        bool Keyed(int x, int y) => x >= width / 4 && x < width / 2 && y >= height / 4 && y < height / 2;
        var grayKey = Gray(At(width / 2, height / 2), Depth(kind));
        var rgbKey = Rgb(At(width / 3, height / 3), Depth(kind), width / 3, height / 3, false);
        return kind switch
        {
            "png_g1" or "png_g2" or "png_g4" or "png_g8" or "png_g16" => Png(width, height, 0, Depth(kind), (x, y) => Gray(At(x, y), Depth(kind)), null, null),
            "png_g4_trns" or "png_g8_trns" or "png_g16_trns"
                => Png(width, height, 0, Depth(kind), (x, y) => Keyed(x, y) ? grayKey : Gray(At(x, y), Depth(kind)), null, Be16(grayKey[0])),
            "png_ga8" => Png(width, height, 4, 8, (x, y) => [(ushort)Gray8(At(x, y)), (ushort)At(x, y).A], null, null),
            "png_ga16" => Png(width, height, 4, 16, (x, y) => [Wide(Gray8(At(x, y)), x + y), Wide(At(x, y).A, x * 3 + y)], null, null),
            "png_rgb8" or "png_rgb8_solid" => Png(width, height, 2, 8, (x, y) => Rgb(At(x, y), 8, x, y, false), null, null),
            "png_rgb16" => Png(width, height, 2, 16, (x, y) => Rgb(At(x, y), 16, x, y, false), null, null),
            "png_rgba8" => Png(width, height, 6, 8, (x, y) => Rgb(At(x, y), 8, x, y, true), null, null),
            "png_rgba16" => Png(width, height, 6, 16, (x, y) => Rgb(At(x, y), 16, x, y, true), null, null),
            "png_rgb8_trns" or "png_rgb16_trns" => Png(width, height, 2, Depth(kind),
                (x, y) => Keyed(x, y) ? rgbKey : Rgb(At(x, y), Depth(kind), x, y, false), null, [.. rgbKey.SelectMany(Be16)]),
            "png_p8" => Png(width, height, 3, 8, (x, y) => [Index(x, y)], palette, null),
            "png_p8_trns" => Png(width, height, 3, 8, (x, y) => [Index(x, y)], palette,
                [.. Enumerable.Range(0, palette.Count).Select(i => (byte)(i % 7 == 0 ? 0 : 255 - i))]),
            "png_p4" => Png(width, height, 3, 4, (x, y) => [(ushort)(Gray8(At(x, y)) >> 4)],
                [.. Enumerable.Range(0, 16).Select(i => ((byte)(i * 17), (byte)(255 - i * 13), (byte)(i * 29 % 256)))], null),
            "png_rgb8_adam7" => Encode<Rgb24>(pixels, width, height, p => new Rgb24((byte)p.R, (byte)p.G, (byte)p.B),
                new PngEncoder { InterlaceMethod = PngInterlaceMode.Adam7, ColorType = PngColorType.Rgb, BitDepth = PngBitDepth.Bit8 }),
            "gif_full" => Gif(width, height, new Rectangle(0, 0, width, height), Index, palette, null),
            "gif_transparent" => Gif(width, height, new Rectangle(0, 0, width, height), (x, y) => Keyed(x, y) ? (byte)7 : Index(x, y), palette, 7),
            "gif_offset" => Gif(width, height, new Rectangle(width / 5, height / 6, width - width / 5 - width / 7, height - height / 6 - height / 9),
                Index, palette, null),
            // An already-64×64 first frame away from the origin: Go hashes screen coordinates 0–63, and
            // reports palette entry 0 (here a colour no other entry uses, or transparent) outside the frame.
            "gif_frame64" => Gif(width, height, new Rectangle(width / 5, height / 6, 64, 64), Index, [(200, 60, 120), .. palette.Skip(1)], null),
            "gif_frame64_t0" => Gif(width, height, new Rectangle(width / 5, height / 6, 64, 64), Index, [(200, 60, 120), .. palette.Skip(1)], 0),
            "bmp_24" => Bmp(width, height, 24, At, v5: false),
            "bmp_32_v3" => Bmp(width, height, 32, At, v5: false),
            "bmp_32_v5" => Bmp(width, height, 32, At, v5: true),
            "bmp_8" => Bmp8(width, height, Index, palette),
            "webp_lossless" => Encode<Rgb24>(pixels, width, height, p => new Rgb24((byte)p.R, (byte)p.G, (byte)p.B),
                new WebpEncoder { FileFormat = WebpFileFormatType.Lossless, Quality = 100 }),
            "webp_lossless_alpha" => Encode<Rgba32>(pixels, width, height, p => new Rgba32((byte)p.R, (byte)p.G, (byte)p.B, (byte)p.A),
                new WebpEncoder { FileFormat = WebpFileFormatType.Lossless, Quality = 100 }),
            "webp_lossy" => Encode<Rgb24>(pixels, width, height, p => new Rgb24((byte)p.R, (byte)p.G, (byte)p.B),
                new WebpEncoder { FileFormat = WebpFileFormatType.Lossy, Quality = 80 }),
            "webp_lossy_alpha" => Encode<Rgba32>(pixels, width, height, p => new Rgba32((byte)p.R, (byte)p.G, (byte)p.B, (byte)p.A),
                new WebpEncoder { FileFormat = WebpFileFormatType.Lossy, Quality = 80 }),
            "jpg_420" or "jpg_444" or "jpg_gray" => Encode<Rgb24>(pixels, width, height, p => new Rgb24((byte)p.R, (byte)p.G, (byte)p.B),
                new JpegEncoder
                {
                    Quality = 85,
                    ColorType = kind switch
                    {
                        "jpg_420" => JpegEncodingColor.YCbCrRatio420,
                        "jpg_444" => JpegEncodingColor.YCbCrRatio444,
                        _ => JpegEncodingColor.Luminance,
                    },
                }),
            // A colour map of v << 8 rather than v * 257, so 16-bit precision changes the result.
            "tiff_palette16" => Tiff(width, height, Index, [.. palette.Select(p => ((ushort)(p.R << 8), (ushort)(p.G << 8), (ushort)(p.B << 8)))]),
            "tiff_rgb" or "tiff_gray" or "tiff_palette" => Encode<Rgb24>(pixels, width, height, p => new Rgb24((byte)p.R, (byte)p.G, (byte)p.B),
                new TiffEncoder
                {
                    BitsPerPixel = kind == "tiff_rgb" ? TiffBitsPerPixel.Bit24 : TiffBitsPerPixel.Bit8,
                    PhotometricInterpretation = kind switch
                    {
                        "tiff_rgb" => TiffPhotometricInterpretation.Rgb,
                        "tiff_gray" => TiffPhotometricInterpretation.BlackIsZero,
                        _ => TiffPhotometricInterpretation.PaletteColor,
                    },
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown fixture kind."),
        };
    }

    private static int Depth(string kind)
        => kind.Split('_') is [_, var format, ..] && int.TryParse(format.TrimStart('g', 'r', 'b', 'a'), out var depth) ? depth : 8;

    // Smooth structure for the hash to see, plus per-pixel noise so rounding differences show up.
    private static (int R, int G, int B, int A) Pixel(int x, int y, int width, int height)
    {
        double u = (x + 0.5) / width, v = (y + 0.5) / height;
        var n = Noise(x, y);
        static int Clamp(double value) => Math.Clamp((int)Math.Round(value), 0, 255);
        var r = 128 + 100 * Math.Sin(6.1 * u + 2.3 * v) + 20 * Math.Cos(17 * u * v) + n % 9 - 4;
        var g = 120 + 90 * Math.Cos(4.7 * v - 3.1 * u) + (u > 0.3 && u < 0.55 && v > 0.2 && v < 0.7 ? 60 : 0) + (n >> 4) % 7 - 3;
        var b = 110 + 110 * Math.Sin(9.3 * u * u + 5.5 * v) + (n >> 8) % 11 - 5;
        var a = 255 * (0.35 + 0.65 * u) - (n >> 12) % 23;
        return (Clamp(r), Clamp(g), Clamp(b), Clamp(a));
    }

    private static int Noise(int x, int y)
    {
        unchecked
        {
            var s = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ 0x9E3779B9u;
            s ^= s << 13;
            s ^= s >> 17;
            s ^= s << 5;
            return (int)(s & 0x7fffffff);
        }
    }

    private static int Gray8((int R, int G, int B, int A) p) => (p.R * 299 + p.G * 587 + p.B * 114) / 1000;

    // A 16-bit sample near v8 * 257 whose low byte varies, so 16-bit precision matters.
    private static ushort Wide(int v8, int salt) => (ushort)Math.Clamp(v8 * 257 + Noise(salt, v8) % 251 - 125, 0, 65535);

    private static ushort[] Gray((int R, int G, int B, int A) p, int depth) => depth switch
    {
        16 => [Wide(Gray8(p), p.R + p.B)],
        8 => [(ushort)Gray8(p)],
        _ => [(ushort)(Gray8(p) >> (8 - depth))],
    };

    private static ushort[] Rgb((int R, int G, int B, int A) p, int depth, int x, int y, bool alpha)
    {
        if (depth == 8)
            return alpha ? [(ushort)p.R, (ushort)p.G, (ushort)p.B, (ushort)p.A] : [(ushort)p.R, (ushort)p.G, (ushort)p.B];
        ushort[] rgb = [Wide(p.R, x), Wide(p.G, y), Wide(p.B, x + y)];
        return alpha ? [.. rgb, Wide(p.A, x * y)] : rgb;
    }

    private static byte[] Be16(ushort value) => [(byte)(value >> 8), (byte)value];

    private static byte[] Encode<TPixel>((int R, int G, int B, int A)[] pixels, int width, int height,
        Func<(int R, int G, int B, int A), TPixel> convert, SixLabors.ImageSharp.Formats.IImageEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using var image = new Image<TPixel>(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                image[x, y] = convert(pixels[y * width + x]);
        using var stream = new MemoryStream();
        image.Save(stream, encoder);
        return stream.ToArray();
    }

    private static byte[] Png(int width, int height, byte colorType, int depth, Func<int, int, ushort[]> sample,
        IReadOnlyList<(byte R, byte G, byte B)>? palette, byte[]? transparency)
    {
        var channels = colorType switch { 0 or 3 => 1, 2 => 3, 4 => 2, _ => 4 };
        var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0); // filter: none
            if (depth < 8)
            {
                int pending = 0, bits = 0;
                for (var x = 0; x < width; x++)
                {
                    pending = (pending << depth) | (sample(x, y)[0] & ((1 << depth) - 1));
                    bits += depth;
                    if (bits == 8)
                    {
                        raw.WriteByte((byte)pending);
                        pending = bits = 0;
                    }
                }
                if (bits > 0)
                    raw.WriteByte((byte)(pending << (8 - bits)));
                continue;
            }
            for (var x = 0; x < width; x++)
            {
                foreach (var value in sample(x, y).Take(channels))
                {
                    if (depth == 16)
                        raw.WriteByte((byte)(value >> 8));
                    raw.WriteByte((byte)value);
                }
            }
        }

        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            raw.WriteTo(zlib);

        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = (byte)depth;
        header[9] = colorType;
        Chunk(png, "IHDR", header);
        if (palette != null)
            Chunk(png, "PLTE", [.. palette.SelectMany(p => new[] { p.R, p.G, p.B })]);
        if (transparency != null)
            Chunk(png, "tRNS", transparency);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        byte[] typed = [.. System.Text.Encoding.ASCII.GetBytes(type), .. data];
        stream.Write(typed);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        stream.Write(crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    // LZW with 9-bit codes and a clear code every 254 symbols, so the code table never grows.
    private static byte[] Gif(int width, int height, Rectangle frame, Func<int, int, byte> index,
        IReadOnlyList<(byte R, byte G, byte B)> palette, int? transparent)
    {
        var gif = new MemoryStream();
        gif.Write("GIF89a"u8);
        var screen = new byte[7];
        BinaryPrimitives.WriteUInt16LittleEndian(screen, (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(screen.AsSpan(2), (ushort)height);
        screen[4] = 0xF7; // global colour table of 256 entries
        gif.Write(screen);
        for (var i = 0; i < 256; i++)
        {
            var p = i < palette.Count ? palette[i] : default;
            gif.Write([p.R, p.G, p.B]);
        }
        if (transparent is { } t)
            gif.Write([0x21, 0xF9, 4, 1, 0, 0, (byte)t, 0]);

        gif.WriteByte(0x2C);
        var descriptor = new byte[9];
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor, (ushort)frame.X);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(2), (ushort)frame.Y);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(4), (ushort)frame.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(6), (ushort)frame.Height);
        gif.Write(descriptor);
        gif.WriteByte(8);

        var bits = new List<bool>();
        void Code(int code)
        {
            for (var b = 0; b < 9; b++)
                bits.Add(((code >> b) & 1) != 0);
        }
        Code(256);
        var sinceClear = 0;
        for (var y = 0; y < frame.Height; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                if (sinceClear == 254)
                {
                    Code(256);
                    sinceClear = 0;
                }
                Code(index(frame.X + x, frame.Y + y));
                sinceClear++;
            }
        }
        Code(257);
        var packed = new byte[(bits.Count + 7) / 8];
        for (var i = 0; i < bits.Count; i++)
            if (bits[i])
                packed[i / 8] |= (byte)(1 << (i % 8));
        for (var i = 0; i < packed.Length; i += 255)
        {
            var length = Math.Min(255, packed.Length - i);
            gif.WriteByte((byte)length);
            gif.Write(packed, i, length);
        }
        gif.WriteByte(0);
        gif.WriteByte(0x3B);
        return gif.ToArray();
    }

    // Baseline little-endian TIFF: one strip of 8-bit palette indexes and a 16-bit colour map.
    private static byte[] Tiff(int width, int height, Func<int, int, byte> index, IReadOnlyList<(ushort R, ushort G, ushort B)> palette)
    {
        const int entryCount = 10;
        const int ifdOffset = 8;
        const int colorMapOffset = ifdOffset + 2 + entryCount * 12 + 4;
        const int pixelOffset = colorMapOffset + 3 * 256 * 2;

        var tiff = new byte[pixelOffset + width * height];
        "II*\0"u8.CopyTo(tiff);
        BinaryPrimitives.WriteInt32LittleEndian(tiff.AsSpan(4), ifdOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(ifdOffset), entryCount);
        var entry = ifdOffset + 2;
        void Entry(ushort tag, ushort type, int count, int value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(entry), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(entry + 2), type);
            BinaryPrimitives.WriteInt32LittleEndian(tiff.AsSpan(entry + 4), count);
            if (type == 3 && count == 1)
                BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(entry + 8), (ushort)value);
            else
                BinaryPrimitives.WriteInt32LittleEndian(tiff.AsSpan(entry + 8), value);
            entry += 12;
        }
        const ushort Short = 3, Long = 4;
        Entry(256, Long, 1, width);
        Entry(257, Long, 1, height);
        Entry(258, Short, 1, 8);              // BitsPerSample
        Entry(259, Short, 1, 1);              // Compression: none
        Entry(262, Short, 1, 3);              // PhotometricInterpretation: palette
        Entry(273, Long, 1, pixelOffset);     // StripOffsets
        Entry(277, Short, 1, 1);              // SamplesPerPixel
        Entry(278, Long, 1, height);          // RowsPerStrip
        Entry(279, Long, 1, width * height);  // StripByteCounts
        Entry(320, Short, 3 * 256, colorMapOffset);

        for (var i = 0; i < 256; i++)
        {
            var p = i < palette.Count ? palette[i] : default;
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(colorMapOffset + i * 2), p.R);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(colorMapOffset + (256 + i) * 2), p.G);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(colorMapOffset + (512 + i) * 2), p.B);
        }
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                tiff[pixelOffset + y * width + x] = index(x, y);
        return tiff;
    }

    private static byte[] Bmp(int width, int height, int bitsPerPixel, Func<int, int, (int R, int G, int B, int A)> at, bool v5)
    {
        var rowSize = (width * bitsPerPixel / 8 + 3) & ~3;
        var infoLength = v5 ? 124 : 40;
        var header = BmpHeader(width, height, bitsPerPixel, infoLength, 14 + infoLength, rowSize);
        if (v5)
        {
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(30), 3); // BI_BITFIELDS
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(54), 0x00FF0000);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(58), 0x0000FF00);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(62), 0x000000FF);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(66), 0xFF000000);
            "BGRs"u8.CopyTo(header.AsSpan(70));
        }

        var bmp = new MemoryStream();
        bmp.Write(header);
        var row = new byte[rowSize];
        for (var y = height - 1; y >= 0; y--)
        {
            Array.Clear(row);
            for (var x = 0; x < width; x++)
            {
                var p = at(x, y);
                var o = x * bitsPerPixel / 8;
                row[o] = (byte)p.B;
                row[o + 1] = (byte)p.G;
                row[o + 2] = (byte)p.R;
                if (bitsPerPixel == 32)
                    row[o + 3] = (byte)p.A;
            }
            bmp.Write(row);
        }
        return bmp.ToArray();
    }

    private static byte[] Bmp8(int width, int height, Func<int, int, byte> index, IReadOnlyList<(byte R, byte G, byte B)> palette)
    {
        var rowSize = (width + 3) & ~3;
        var header = BmpHeader(width, height, 8, 40, 14 + 40 + 256 * 4, rowSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(46), 256);

        var bmp = new MemoryStream();
        bmp.Write(header);
        for (var i = 0; i < 256; i++)
        {
            var p = i < palette.Count ? palette[i] : default;
            bmp.Write([p.B, p.G, p.R, 0]);
        }
        var row = new byte[rowSize];
        for (var y = height - 1; y >= 0; y--)
        {
            Array.Clear(row);
            for (var x = 0; x < width; x++)
                row[x] = index(x, y);
            bmp.Write(row);
        }
        return bmp.ToArray();
    }

    private static byte[] BmpHeader(int width, int height, int bitsPerPixel, int infoLength, int pixelOffset, int rowSize)
    {
        var header = new byte[14 + infoLength];
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(2), pixelOffset + rowSize * height);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(10), pixelOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(14), infoLength);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(28), (short)bitsPerPixel);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(34), rowSize * height);
        return header;
    }
}
