using System.Buffers.Binary;

namespace Cove.Api.Imaging;

/// <summary>
/// Reads the header fields that decide how a decoder represents an image's pixels: a PNG's
/// colour type, a JPEG's components, a GIF's first frame. They are read from the encoded bytes
/// rather than from a decoding library, so the answers do not depend on which library decodes.
/// Each reader expects the stream at the start of the file, returns null for data it cannot
/// follow, and leaves the stream position unspecified.
/// </summary>
internal static class EncodedImageHeaders
{
    internal sealed record PngHeader(int BitDepth, int ColorType, bool HasColorKey)
    {
        public const int Grayscale = 0;
        public const int Palette = 3;
    }

    internal enum JpegColor
    {
        Gray,
        YCbCr,
        Rgb,
        Cmyk,
    }

    internal sealed record GifHeader(int ScreenWidth, int ScreenHeight, PixelRect? FirstFrame, Rgba64 PaletteEntryZero);

    internal sealed record WebpHeader(bool Lossless, bool HasAlpha, bool Animated = false);

    internal sealed record BmpHeader(int InfoHeaderSize, int BitsPerPixel);

    internal sealed record TiffHeader(int Photometric, int BitsPerSample, ushort[]? ColorMap = null)
    {
        public const int WhiteIsZero = 0;
        public const int BlackIsZero = 1;
        public const int PaletteColor = 3;
    }

    /// <summary>IHDR and whether a tRNS chunk gives a grayscale or truecolour image a colour key.</summary>
    internal static PngHeader? ReadPng(Stream stream)
    {
        Span<byte> signature = stackalloc byte[8];
        Span<byte> chunk = stackalloc byte[8];
        Span<byte> ihdr = stackalloc byte[13];
        if (!ReadExactly(stream, signature) || !ReadExactly(stream, chunk) || !chunk[4..].SequenceEqual("IHDR"u8)
            || !ReadExactly(stream, ihdr))
            return null;

        int bitDepth = ihdr[8], colorType = ihdr[9];
        if (!Skip(stream, BinaryPrimitives.ReadUInt32BigEndian(chunk) - 13 + 4L))
            return null;

        var hasColorKey = false;
        while (ReadExactly(stream, chunk))
        {
            var type = chunk[4..];
            if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8))
                break;
            if (type.SequenceEqual("tRNS"u8) && colorType is 0 or 2)
            {
                hasColorKey = true;
                break;
            }
            if (!Skip(stream, BinaryPrimitives.ReadUInt32BigEndian(chunk) + 4L))
                break;
        }
        return new PngHeader(bitDepth, colorType, hasColorKey);
    }

    /// <summary>
    /// The colour model Go's image/jpeg decodes to: one component is gray, four are CMYK, and three
    /// are RGB only without a JFIF marker and with either an Adobe transform of 0 or components
    /// named R, G and B; otherwise YCbCr.
    /// </summary>
    internal static JpegColor? ReadJpeg(Stream stream)
    {
        if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8)
            return null;

        var jfif = false;
        int? adobeTransform = null;
        Span<byte> segment = stackalloc byte[16];
        int components = 0;
        Span<byte> ids = stackalloc byte[3];
        Span<byte> lengthBytes = stackalloc byte[2];
        Span<byte> component = stackalloc byte[3];
        while (true)
        {
            int marker;
            do
            {
                if ((marker = stream.ReadByte()) < 0)
                    return null;
            }
            while (marker != 0xFF);
            while ((marker = stream.ReadByte()) == 0xFF)
            {
            }
            if (marker < 0)
                return null;
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
                continue;
            if (marker is 0xD9 or 0xDA)
                break;

            if (!ReadExactly(stream, lengthBytes))
                return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes) - 2;
            if (length < 0)
                return null;
            var start = stream.Position;

            if (marker == 0xE0 && length >= 5)
            {
                var head = segment[..5];
                if (!ReadExactly(stream, head))
                    return null;
                jfif |= head.SequenceEqual("JFIF\0"u8);
            }
            else if (marker == 0xEE && length >= 12)
            {
                var head = segment[..12];
                if (!ReadExactly(stream, head))
                    return null;
                if (head[..5].SequenceEqual("Adobe"u8))
                    adobeTransform = head[11];
            }
            else if (marker is (>= 0xC0 and <= 0xC3) or (>= 0xC5 and <= 0xC7) or (>= 0xC9 and <= 0xCB) or (>= 0xCD and <= 0xCF)
                && length >= 6)
            {
                var head = segment[..6];
                if (!ReadExactly(stream, head))
                    return null;
                components = head[5];
                for (var i = 0; i < Math.Min(components, 3); i++)
                {
                    if (!ReadExactly(stream, component))
                        return null;
                    ids[i] = component[0];
                }
            }

            stream.Position = start;
            if (!Skip(stream, length))
                return null;
        }

        return components switch
        {
            1 => JpegColor.Gray,
            3 when !jfif && (adobeTransform == 0 || (ids[0] == 'R' && ids[1] == 'G' && ids[2] == 'B')) => JpegColor.Rgb,
            3 => JpegColor.YCbCr,
            4 => JpegColor.Cmyk,
            _ => null,
        };
    }

    /// <summary>
    /// The logical screen, the bounds of the first image descriptor, and palette entry 0 of the
    /// first frame's colour table (local, else global): transparent when it is the frame's
    /// transparent index or there is no table.
    /// </summary>
    internal static GifHeader? ReadGif(Stream stream)
    {
        Span<byte> header = stackalloc byte[13];
        if (!ReadExactly(stream, header) || !header[..3].SequenceEqual("GIF"u8))
            return null;

        int screenWidth = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        int screenHeight = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        var packed = header[10];
        Rgba64? globalEntryZero = null;
        if ((packed & 0x80) != 0)
        {
            globalEntryZero = ReadEntryZero(stream, 3 << ((packed & 0x07) + 1));
            if (globalEntryZero is null)
                return new GifHeader(screenWidth, screenHeight, null, default);
        }

        var transparentIndex = -1;
        Span<byte> descriptor = stackalloc byte[9];
        Span<byte> control = stackalloc byte[4];
        while (true)
        {
            switch (stream.ReadByte())
            {
                case 0x2C:
                    if (!ReadExactly(stream, descriptor))
                        return new GifHeader(screenWidth, screenHeight, null, default);
                    var frame = new PixelRect(
                        BinaryPrimitives.ReadUInt16LittleEndian(descriptor),
                        BinaryPrimitives.ReadUInt16LittleEndian(descriptor[2..]),
                        BinaryPrimitives.ReadUInt16LittleEndian(descriptor[4..]),
                        BinaryPrimitives.ReadUInt16LittleEndian(descriptor[6..]));
                    var local = descriptor[8];
                    var entryZero = (local & 0x80) != 0 ? ReadEntryZero(stream, 3 << ((local & 0x07) + 1)) : globalEntryZero;
                    return new GifHeader(screenWidth, screenHeight, frame, transparentIndex == 0 ? default : entryZero ?? default);
                case 0x21:
                    var label = stream.ReadByte();
                    if (label < 0)
                        return new GifHeader(screenWidth, screenHeight, null, default);
                    // Extension data is a chain of length-prefixed sub-blocks ending at a zero length.
                    int size;
                    var first = true;
                    while ((size = stream.ReadByte()) > 0)
                    {
                        if (label == 0xF9 && first && size >= 4)
                        {
                            if (!ReadExactly(stream, control))
                                return new GifHeader(screenWidth, screenHeight, null, default);
                            transparentIndex = (control[0] & 0x01) != 0 ? control[3] : -1;
                            if (!Skip(stream, size - 4))
                                return new GifHeader(screenWidth, screenHeight, null, default);
                        }
                        else if (!Skip(stream, size))
                        {
                            return new GifHeader(screenWidth, screenHeight, null, default);
                        }
                        first = false;
                    }
                    if (size < 0)
                        return new GifHeader(screenWidth, screenHeight, null, default);
                    break;
                default:
                    return new GifHeader(screenWidth, screenHeight, null, default);
            }
        }

        static Rgba64? ReadEntryZero(Stream stream, int tableLength)
        {
            Span<byte> entry = stackalloc byte[3];
            if (!ReadExactly(stream, entry) || !Skip(stream, tableLength - 3))
                return null;
            return new Rgba64((ushort)(entry[0] * 0x101), (ushort)(entry[1] * 0x101), (ushort)(entry[2] * 0x101));
        }
    }

    /// <summary>
    /// Whether the first image is lossless (VP8L) and, if lossy, whether an ALPH chunk gives it
    /// alpha. In an animation the first frame's sub-chunks decide.
    /// </summary>
    internal static WebpHeader? ReadWebp(Stream stream)
    {
        Span<byte> riff = stackalloc byte[12];
        if (!ReadExactly(stream, riff) || !riff[..4].SequenceEqual("RIFF"u8) || !riff[8..].SequenceEqual("WEBP"u8))
            return null;
        // The top-level chunks, then (once) the sub-chunks of the first ANMF frame. A frame nested in a
        // frame is not valid WebP and ends the search rather than being followed.
        Span<byte> chunk = stackalloc byte[8];
        var end = stream.Length;
        var hasAlpha = false;
        var animated = false;
        var inFrame = false;
        while (stream.Position + 8 <= end && ReadExactly(stream, chunk))
        {
            var type = chunk[..4];
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            var padded = size + (size & 1);
            if (type.SequenceEqual("VP8L"u8))
                return new WebpHeader(Lossless: true, HasAlpha: true, animated);
            if (type.SequenceEqual("VP8 "u8))
                return new WebpHeader(Lossless: false, hasAlpha, animated);
            if (type.SequenceEqual("ALPH"u8))
                hasAlpha = true;
            if (type.SequenceEqual("VP8X"u8) && size >= 1 && !inFrame)
            {
                var flags = stream.ReadByte();
                if (flags < 0)
                    return null;
                animated = (flags & 0x02) != 0;
                stream.Position -= 1;
            }
            if (type.SequenceEqual("ANMF"u8))
            {
                if (inFrame || size < 16)
                    return null;
                animated = true;
                inFrame = true;
                end = Math.Min(stream.Position + size, stream.Length);
                if (!Skip(stream, 16))
                    return null;
                continue;
            }
            if (!Skip(stream, padded))
                return null;
        }
        return null;
    }

    internal static BmpHeader? ReadBmp(Stream stream)
    {
        Span<byte> header = stackalloc byte[30];
        if (!ReadExactly(stream, header) || header[0] != 'B' || header[1] != 'M')
            return null;
        var infoSize = BinaryPrimitives.ReadInt32LittleEndian(header[14..]);
        int bitsPerPixel = infoSize == 12
            ? BinaryPrimitives.ReadUInt16LittleEndian(header[24..])
            : BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
        return new BmpHeader(infoSize, bitsPerPixel);
    }

    /// <summary>
    /// PhotometricInterpretation, the first BitsPerSample value and, for a palette image, the ColorMap
    /// (all red entries, then green, then blue) of the first IFD.
    /// </summary>
    internal static TiffHeader? ReadTiff(Stream stream)
    {
        Span<byte> header = stackalloc byte[8];
        if (!ReadExactly(stream, header))
            return null;
        bool little;
        if (header[0] == 'I' && header[1] == 'I')
            little = true;
        else if (header[0] == 'M' && header[1] == 'M')
            little = false;
        else
            return null;

        if (U16(header[2..]) != 42)
            return null;
        stream.Position = U32(header[4..]);

        Span<byte> count = stackalloc byte[2];
        if (!ReadExactly(stream, count))
            return null;
        int? photometric = null;
        var bitsPerSample = 1;
        uint colorMapOffset = 0, colorMapCount = 0;
        Span<byte> entry = stackalloc byte[12];
        Span<byte> first = stackalloc byte[2];
        for (var i = U16(count); i > 0; i--)
        {
            if (!ReadExactly(stream, entry))
                return null;
            var tag = U16(entry);
            var type = U16(entry[2..]);
            var values = U32(entry[4..]);
            if (tag == 320 && type == 3)
            {
                colorMapCount = values;
                colorMapOffset = U32(entry[8..]);
            }
            else             if (tag == 262)
            {
                photometric = Short(entry[8..], type);
            }
            else if (tag == 258 && values > 0)
            {
                // Up to two SHORTs fit in the entry itself; more are stored at the offset it holds.
                if (type == 3 && values > 2)
                {
                    var resume = stream.Position;
                    stream.Position = U32(entry[8..]);
                    if (!ReadExactly(stream, first))
                        return null;
                    bitsPerSample = U16(first);
                    stream.Position = resume;
                }
                else
                {
                    bitsPerSample = Short(entry[8..], type);
                }
            }
        }
        if (photometric is not { } value)
            return null;

        ushort[]? colorMap = null;
        if (value == TiffHeader.PaletteColor && colorMapCount is > 0 and <= 3 * 65536 && colorMapCount % 3 == 0
            && colorMapOffset + colorMapCount * 2L <= stream.Length)
        {
            stream.Position = colorMapOffset;
            var bytes = new byte[colorMapCount * 2];
            if (ReadExactly(stream, bytes))
            {
                colorMap = new ushort[colorMapCount];
                for (var i = 0; i < colorMap.Length; i++)
                    colorMap[i] = (ushort)U16(bytes.AsSpan(i * 2));
            }
        }
        return new TiffHeader(value, bitsPerSample, colorMap);

        int U16(ReadOnlySpan<byte> data) => little ? BinaryPrimitives.ReadUInt16LittleEndian(data) : BinaryPrimitives.ReadUInt16BigEndian(data);
        uint U32(ReadOnlySpan<byte> data) => little ? BinaryPrimitives.ReadUInt32LittleEndian(data) : BinaryPrimitives.ReadUInt32BigEndian(data);
        int Short(ReadOnlySpan<byte> data, int type) => type == 4 ? (int)U32(data) : U16(data);
    }

    /// <summary>
    /// The samples of a 16-bit grayscale or truecolour PNG's tRNS chunk, or null when it has none.
    /// </summary>
    internal static ushort[]? ReadPngColorKey16(Stream stream)
    {
        Span<byte> header = stackalloc byte[8];
        Span<byte> data = stackalloc byte[6];
        if (!ReadExactly(stream, header))
            return null;

        while (ReadExactly(stream, header))
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = header[4..];
            if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8))
                return null;

            if (type.SequenceEqual("tRNS"u8) && length is 2 or 6)
            {
                var samples = data[..(int)length];
                if (!ReadExactly(stream, samples))
                    return null;
                var key = new ushort[samples.Length / 2];
                for (var i = 0; i < key.Length; i++)
                    key[i] = BinaryPrimitives.ReadUInt16BigEndian(samples[(i * 2)..]);
                return key;
            }

            // Chunk data and its CRC.
            if (!Skip(stream, length + 4L))
                return null;
        }
        return null;
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
        => stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length;

    private static bool Skip(Stream stream, long count)
    {
        if (count < 0 || stream.Position + count > stream.Length)
            return false;
        stream.Position += count;
        return true;
    }
}
