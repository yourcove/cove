using System.Buffers.Binary;
using Cove.Api.Imaging;
using static Cove.Api.Imaging.EncodedImageHeaders;

namespace Cove.Tests.Imaging;

/// <summary>
/// The header readers parse untrusted files, so beyond reading the fields Go's decoders act on they
/// must return null, never throw or recurse without bound, for any damaged input.
/// </summary>
public sealed class EncodedImageHeadersTests
{
    // ---- JPEG: Go's image/jpeg colour rules ----

    [Theory]
    [InlineData(1, false, null, "ABC", "Gray")]
    [InlineData(3, false, null, "ABC", "YCbCr")]
    [InlineData(3, false, 1, "ABC", "YCbCr")]
    [InlineData(3, false, 0, "ABC", "Rgb")]   // Adobe transform 0: RGB
    [InlineData(3, false, null, "RGB", "Rgb")] // components named R, G, B
    [InlineData(3, true, 0, "ABC", "YCbCr")]  // a JFIF marker wins over the Adobe transform
    [InlineData(3, true, null, "RGB", "YCbCr")]
    [InlineData(4, false, 2, "ABC", "Cmyk")]
    public void ReadJpeg_FollowsGosColourModelRules(int components, bool jfif, int? adobeTransform, string ids, string expected)
    {
        Assert.Equal(Enum.Parse<JpegColor>(expected), ReadJpeg(new MemoryStream(Jpeg(components, jfif, adobeTransform, ids))));
    }

    [Fact]
    public void ReadJpeg_ReadsAnEncodedJpeg()
    {
        Assert.Equal(JpegColor.YCbCr, ReadJpeg(new MemoryStream(TestImages.Jpeg(8, 8, new Rgba32(10, 200, 30)))));
    }

    // ---- WebP ----

    [Fact]
    public void ReadWebp_ReportsASingleFrameAnimation()
    {
        var still = TestImages.Encode(TestImages.Solid(6, 4, new Rgba32(255, 0, 0)), "webp", quality: 80);
        Assert.Equal(new WebpHeader(false, false, false), ReadWebp(new MemoryStream(still)));
        Assert.Equal(new WebpHeader(false, false, true), ReadWebp(new MemoryStream(TestImages.SingleFrameAnimatedWebp(still, 6, 4))));
    }

    /// <summary>A frame inside a frame is invalid; following each one would overflow the stack.</summary>
    [Fact]
    public void ReadWebp_DoesNotFollowFramesNestedInFrames()
    {
        var still = TestImages.Encode(TestImages.Solid(6, 4, new Rgba32(255, 0, 0)), "webp", quality: 80);
        var nested = NestFrames(Chunk(still, "VP8 "), depth: 200_000);
        var file = Riff([.. Vp8x(animated: true), .. nested]);

        Assert.Null(ReadWebp(new MemoryStream(file)));
        var plan = Record.Exception(() => Cove.Api.Services.StashImagePhash.Compute(new MemoryStream(file)));
        Assert.True(plan is null or ImageDecodeException, plan?.ToString());
    }

    // ---- Every reader, every truncation ----

    public static TheoryData<string> Fixtures() => new(
        "png_rgb16_trns", "png_p8_trns", "jpg_420", "gif_transparent", "gif_offset",
        "webp_lossy_alpha", "webp_lossless", "bmp_32_v5", "bmp_8", "tiff_palette16", "tiff_rgb");

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Readers_ReturnNullOrAValueForEveryTruncation(string kind)
    {
        var (width, height) = kind is "jpg_420" or "webp_lossy_alpha" or "webp_lossless" or "tiff_rgb" ? (64, 64) : (130, 41);
        var data = StashImagePhashFixtures.Create(kind, width, height);
        for (var length = 0; length <= data.Length; length += Math.Max(1, data.Length / 400))
        {
            foreach (var read in new Func<Stream, object?>[] { s => ReadPng(s), s => ReadJpeg(s), s => ReadGif(s), s => ReadWebp(s), s => ReadBmp(s), s => ReadTiff(s), s => ReadPngColorKey16(s) })
            {
                var exception = Record.Exception(() => read(new MemoryStream(data, 0, length, writable: false)));
                Assert.True(exception is null, $"{kind} cut to {length} bytes: {exception}");
            }
        }
    }

    // ---- Builders ----

    private static byte[] Jpeg(int components, bool jfif, int? adobeTransform, string ids)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        if (jfif)
            bytes.AddRange([0xFF, 0xE0, 0, 16, .. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        if (adobeTransform is { } transform)
            bytes.AddRange([0xFF, 0xEE, 0, 14, .. "Adobe"u8, 0, 100, 0, 0, 0, 0, (byte)transform]);
        bytes.AddRange([0xFF, 0xC0, 0, (byte)(8 + 3 * components), 8, 0, 8, 0, 8, (byte)components]);
        for (var i = 0; i < components; i++)
            bytes.AddRange([(byte)(i < ids.Length ? ids[i] : 'K'), 0x11, 0]);
        bytes.AddRange([0xFF, 0xDA, 0, 2, 0xFF, 0xD9]);
        return [.. bytes];
    }

    private static byte[] Chunk(byte[] webp, string type)
    {
        for (var offset = 12; offset + 8 <= webp.Length;)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(offset + 4));
            if (webp.AsSpan(offset, 4).SequenceEqual(System.Text.Encoding.ASCII.GetBytes(type)))
                return webp[offset..(offset + 8 + size + (size & 1))];
            offset += 8 + size + (size & 1);
        }
        throw new InvalidOperationException($"No {type} chunk.");
    }

    private static byte[] NestFrames(byte[] inner, int depth)
    {
        // Built from the inside out: each frame is its 16-byte header and everything inside it.
        var total = inner.Length + depth * 24L;
        var file = new byte[total];
        var position = 0;
        for (var level = 0; level < depth; level++)
        {
            var size = (uint)(total - position - 8);
            "ANMF"u8.CopyTo(file.AsSpan(position));
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(position + 4), size);
            position += 24;
        }
        inner.CopyTo(file.AsSpan(position));
        return file;
    }

    private static byte[] Vp8x(bool animated)
    {
        var chunk = new byte[18];
        "VP8X"u8.CopyTo(chunk);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), 10);
        chunk[8] = (byte)(animated ? 0x02 : 0);
        chunk[12] = 5; // canvas 6×4, stored minus one
        chunk[15] = 3;
        return chunk;
    }

    private static byte[] Riff(byte[] body)
    {
        var file = new byte[12 + body.Length];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(4 + body.Length));
        "WEBP"u8.CopyTo(file.AsSpan(8));
        body.CopyTo(file.AsSpan(12));
        return file;
    }
}
