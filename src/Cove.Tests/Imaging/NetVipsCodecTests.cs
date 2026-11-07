using System.Buffers.Binary;
using Cove.Api.Imaging;
using Cove.Api.Services;
using NetVips;
using VipsImage = NetVips.Image;

namespace Cove.Tests.Imaging;

/// <summary>
/// What <see cref="NetVipsCodec"/> must do, including the places it deliberately follows ImageSharp,
/// which Cove used before: the fit-within rounding, bitmap decoding, exact 16-bit palette TIFF
/// colours, chroma subsampling on re-encode, and how bad input is refused.
/// </summary>
public sealed class NetVipsCodecTests
{
    private static readonly IImageCodec Codec = new NetVipsCodec();

    // ---- Geometry ----

    /// <summary>Sizes ImageSharp's ResizeMode.Max produced, recorded before it was removed.</summary>
    [Theory]
    [InlineData(2200, 1400, 640, 640, 640, 407)]
    [InlineData(1400, 2200, 640, 640, 407, 640)]
    [InlineData(1920, 1080, 160, 160, 160, 90)]
    [InlineData(333, 211, 77, 77, 77, 49)]
    [InlineData(1001, 999, 500, 500, 500, 499)]
    [InlineData(999, 1001, 500, 500, 499, 500)]
    [InlineData(3, 1000, 64, 64, 1, 64)]
    [InlineData(1000, 3, 64, 64, 64, 1)]
    [InlineData(4032, 3024, 1280, 1280, 1280, 960)]
    [InlineData(641, 640, 640, 640, 640, 639)]
    [InlineData(160, 90, 64, 64, 64, 36)]
    [InlineData(7, 5, 3, 3, 3, 2)]
    [InlineData(1500, 1000, 300, 200, 300, 200)]
    [InlineData(1000, 1500, 300, 200, 133, 200)]
    [InlineData(1366, 768, 320, 180, 320, 180)]
    [InlineData(800, 600, 123, 77, 103, 77)]
    public void FitWithin_MatchesImageSharpRounding(int width, int height, int boxWidth, int boxHeight, int expectedWidth, int expectedHeight)
    {
        Assert.Equal(new PixelSize(expectedWidth, expectedHeight), ImageGeometry.FitWithin(new PixelSize(width, height), new PixelSize(boxWidth, boxHeight)));
    }

    [Theory]
    [InlineData(333, 211, 77, 49)]
    [InlineData(1001, 999, 500, 499)]
    [InlineData(7, 5, 3, 2)]
    [InlineData(64, 64, 200, 200)]
    public async Task Resize_ProducesExactlyTheRequestedSize(int width, int height, int targetWidth, int targetHeight)
    {
        var data = TestImages.Encode(Pattern(width, height), "png");
        using var image = await Codec.LoadAsync(new MemoryStream(data), cancellationToken: TestContext.Current.CancellationToken);
        image.Resize(new PixelSize(targetWidth, targetHeight));
        var output = await SaveAsync(image, ImageOutput.Png);
        Assert.Equal((targetWidth, targetHeight), (Codec.Identify(new MemoryStream(output)).Width, Codec.Identify(new MemoryStream(output)).Height));
    }

    [Fact]
    public async Task Resize_DoesNotBleedTheColourOfTransparentPixels()
    {
        // Opaque blue on the left; on the right, red that is fully transparent and so must not show.
        var pixels = new PixelBuffer<Rgba32>(64, 16);
        for (var y = 0; y < 16; y++)
            for (var x = 0; x < 64; x++)
                pixels[x, y] = x < 32 ? new Rgba32(0, 0, 255) : new Rgba32(255, 0, 0, 0);
        using var image = Codec.FromPixels(pixels);
        image.Resize(new PixelSize(16, 4));
        var resized = Codec.DecodePixels<Rgba32>(new MemoryStream(await SaveAsync(image, ImageOutput.Png)));

        Assert.All(resized.Pixels.Where(p => p.A > 0), p => Assert.True(p.R < 8, $"red leaked into {p}"));
    }

    [Fact]
    public async Task Crop_KeepsExactlyThePixelsInside()
    {
        var source = Pattern(120, 80);
        var area = new PixelRect(17, 9, 61, 40);
        using var image = await Codec.LoadAsync(new MemoryStream(TestImages.Encode(source, "png")), cancellationToken: TestContext.Current.CancellationToken);
        image.Crop(area);
        var cropped = Codec.DecodePixels<Rgba32>(new MemoryStream(await SaveAsync(image, ImageOutput.Png)));

        Assert.Equal((61, 40), (cropped.Width, cropped.Height));
        for (var y = 0; y < area.Height; y++)
            Assert.True(cropped.Row(y).SequenceEqual(source.Row(area.Y + y).Slice(area.X, area.Width)));
    }

    [Fact]
    public async Task Crop_OutsideTheImage_Throws()
    {
        using var image = await Codec.LoadAsync(new MemoryStream(TestImages.Png(10, 10)), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Crop(new PixelRect(5, 5, 6, 2)));
    }

    [Fact]
    public async Task TargetSize_ShrinksAJpegOnLoadButNeverBelowTheTarget()
    {
        var data = TestImages.Encode(Pattern(1600, 900), "jpeg", quality: 85);
        using var image = await Codec.LoadAsync(new MemoryStream(data), new ImageLoadOptions(new PixelSize(160, 90)), TestContext.Current.CancellationToken);
        Assert.Equal((200, 112), (image.Width, image.Height));

        using var tighter = await Codec.LoadAsync(new MemoryStream(data), new ImageLoadOptions(new PixelSize(201, 90)), TestContext.Current.CancellationToken);
        Assert.Equal((400, 225), (tighter.Width, tighter.Height));
    }

    // ---- Orientation ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task AutoOrient_TurnsThePixelsTheWayTheTagSays(int orientation)
    {
        var source = Pattern(48, 32);
        var data = EncodeJpeg444(source, orientation);
        var header = Codec.Identify(new MemoryStream(data));
        Assert.Equal(orientation, header.Orientation);

        using var image = await Codec.LoadAsync(new MemoryStream(data), cancellationToken: TestContext.Current.CancellationToken);
        image.AutoOrient();
        var upright = Codec.DecodePixels<Rgba32>(new MemoryStream(await SaveAsync(image, ImageOutput.Png)));

        Assert.Equal((header.DisplaySize.Width, header.DisplaySize.Height), (upright.Width, upright.Height));
        var expected = Orient(source, orientation);
        var mean = 0.0;
        for (var i = 0; i < expected.Pixels.Length; i++)
            mean += Math.Abs(expected.Pixels[i].R - upright.Pixels[i].R) + Math.Abs(expected.Pixels[i].G - upright.Pixels[i].G) + Math.Abs(expected.Pixels[i].B - upright.Pixels[i].B);
        Assert.True(mean / (expected.Pixels.Length * 3) < 3, $"mean difference {mean / (expected.Pixels.Length * 3):F2}");
    }

    // ---- Decoding ----

    [Theory]
    [InlineData("png")]
    [InlineData("tiff")]
    [InlineData("webp")]
    public void LosslessFormats_DecodeToTheEncodedPixels(string format)
    {
        var source = Pattern(97, 61, alpha: true);
        Assert.Equal(source.Pixels, Codec.DecodePixels<Rgba32>(new MemoryStream(TestImages.Encode(source, format))).Pixels);
    }

    [Fact]
    public void SixteenBitPng_DecodesAtFullPrecision()
    {
        var samples = new ushort[13 * 7 * 3];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (ushort)(i * 7919 % 65536);
        using var raw = VipsImage.NewFromMemoryCopy(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()), 13, 7, 3, Enums.BandFormat.Ushort);
        using var rgb16 = raw.Copy(interpretation: Enums.Interpretation.Rgb16);
        var decoded = Codec.DecodePixels<Rgba64>(new MemoryStream(rgb16.PngsaveBuffer()));

        for (var i = 0; i < decoded.Pixels.Length; i++)
            Assert.Equal(new Rgba64(samples[i * 3], samples[i * 3 + 1], samples[i * 3 + 2]), decoded.Pixels[i]);
    }

    [Fact]
    public void EightBitSamples_WidenExactlyAndSixteenBitNarrowWithRounding()
    {
        var source = Pattern(16, 16);
        var data = TestImages.Encode(source, "png");
        var wide = Codec.DecodePixels<Rgba64>(new MemoryStream(data));
        for (var i = 0; i < source.Pixels.Length; i++)
            Assert.Equal(source.Pixels[i].R * 257, wide.Pixels[i].R);

        // 0xFF00 is 254.0 in 8 bits, so it rounds to 254 where taking the high byte would give 255.
        using var raw = VipsImage.NewFromMemoryCopy(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ushort[] { 0xFF00 }.AsSpan()), 1, 1, 1, Enums.BandFormat.Ushort);
        using var grey16 = raw.Copy(interpretation: Enums.Interpretation.Grey16);
        Assert.Equal(new L8(254), Codec.DecodePixels<L8>(new MemoryStream(grey16.PngsaveBuffer())).Pixels[0]);
    }

    [Fact]
    public void SixteenBitPngColourKey_MakesTheKeyedPixelsTransparent()
    {
        var data = StashImagePhashFixtures.Create("png_rgb16_trns", 64, 64);
        var decoded = Codec.DecodePixels<Rgba64>(new MemoryStream(data));
        Assert.Contains(decoded.Pixels, p => p.A == 0);
        Assert.Contains(decoded.Pixels, p => p.A == ushort.MaxValue);
    }

    /// <summary>libvips keeps 8 bits of a TIFF colour map; the codec restores the 16-bit entries.</summary>
    [Fact]
    public void PaletteTiff_DecodesTheFullSixteenBitColourMap()
    {
        // The fixture's colour map is v << 8, which ×257 widening of the high byte would not give.
        var decoded = Codec.DecodePixels<Rgba64>(new MemoryStream(StashImagePhashFixtures.Create("tiff_palette16", 64, 64)));
        Assert.All(decoded.Pixels, p => Assert.True((p.R & 0xFF) == 0 && (p.G & 0xFF) == 0 && (p.B & 0xFF) == 0, $"{p} is not a colour-map entry"));
        Assert.Contains(decoded.Pixels, p => p.R != 0);
    }

    [Fact]
    public void Gif_DecodesTheFirstFrameOnItsLogicalScreen()
    {
        var data = TestImages.Animated("gif", TestImages.Solid(6, 4, new Rgba32(255, 0, 0)), TestImages.Solid(6, 4, new Rgba32(0, 0, 255)));
        Assert.Equal(new ImageHeader(ImageFormat.Gif, 6, 4, 2, 1), Codec.Identify(new MemoryStream(data)));
        Assert.All(Codec.DecodePixels<Rgba32>(new MemoryStream(data)).Pixels, p => Assert.Equal(new Rgba32(255, 0, 0), p));
    }

    [Fact]
    public void AnimatedWebp_ReportsItsFrames()
    {
        var data = TestImages.Animated("webp", TestImages.Solid(6, 4, new Rgba32(255, 0, 0)), TestImages.Solid(6, 4, new Rgba32(0, 0, 255)));
        Assert.Equal(new ImageHeader(ImageFormat.Webp, 6, 4, 2, 1), Codec.Identify(new MemoryStream(data)));
        Assert.Equal(1, Codec.Identify(new MemoryStream(TestImages.Encode(TestImages.Solid(6, 4, new Rgba32(1, 2, 3)), "webp"))).FrameCount);
    }

    [Fact]
    public void CmykJpeg_DecodesWithGosConversion()
    {
        using var rgb = VipsImage.Black(8, 8, bands: 3).Linear([1, 1, 1], [200, 100, 50]).Cast(Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb);
        using var cmyk = rgb.Colourspace(Enums.Interpretation.Cmyk);
        var data = cmyk.JpegsaveBuffer(q: 100);
        using var stored = VipsImage.NewFromBuffer(data);
        var ink = stored.Getpoint(0, 0);

        var decoded = Codec.DecodePixels<Rgba64>(new MemoryStream(data)).Pixels[0];
        var white = 0xffffu - (uint)Math.Round(ink[3]) * 0x101;
        Assert.Equal((ushort)((0xffffu - (uint)Math.Round(ink[0]) * 0x101) * white / 0xffff), decoded.R);
    }

    // ---- Bitmaps ----

    [Fact]
    public void Bitmap24_DecodesBottomUpRows()
    {
        var source = Pattern(5, 3);
        Assert.Equal(source.Pixels, Codec.DecodePixels<Rgba32>(new MemoryStream(Bitmap(source, 24, infoSize: 40))).Pixels);
        Assert.Equal(new ImageHeader(ImageFormat.Bmp, 5, 3, 1, 1), Codec.Identify(new MemoryStream(Bitmap(source, 24, infoSize: 40))));
    }

    [Fact]
    public void Bitmap24_DecodesTopDownRows()
    {
        var source = Pattern(5, 3);
        Assert.Equal(source.Pixels, Codec.DecodePixels<Rgba32>(new MemoryStream(Bitmap(source, 24, infoSize: 40, topDown: true))).Pixels);
    }

    [Fact]
    public void Bitmap32WithAnInfoHeader_UsesTheFourthByteOnlyWhenSomePixelSetsIt()
    {
        var translucent = Pattern(4, 4, alpha: true);
        Assert.Equal(translucent.Pixels, Codec.DecodePixels<Rgba32>(new MemoryStream(Bitmap(translucent, 32, infoSize: 40))).Pixels);

        // A reserved byte left at zero everywhere means an opaque image, not an invisible one.
        var zeroed = new PixelBuffer<Rgba32>(4, 4, translucent.Pixels.Select(p => p with { A = 0 }).ToArray());
        Assert.All(Codec.DecodePixels<Rgba32>(new MemoryStream(Bitmap(zeroed, 32, infoSize: 40))).Pixels, p => Assert.Equal(255, p.A));
    }

    [Fact]
    public void Bitmap32WithAV5Header_UsesAlpha()
    {
        var source = Pattern(4, 4, alpha: true);
        Assert.Equal(source.Pixels, Codec.DecodePixels<Rgba32>(new MemoryStream(Bitmap(source, 32, infoSize: 124))).Pixels);
    }

    [Fact]
    public void Bitmap16_WidensFiveBitChannels()
    {
        var source = new PixelBuffer<Rgba32>(2, 1, [new Rgba32(255, 255, 255), new Rgba32(8, 16, 0)]);
        var decoded = Codec.DecodePixels<Rgba32>(new MemoryStream(Bitmap(source, 16, infoSize: 40))).Pixels;
        Assert.Equal(new Rgba32(255, 255, 255), decoded[0]);
        Assert.Equal(new Rgba32(8, 16, 0), decoded[1]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void PaletteBitmap_LooksUpEachIndex(int bits)
    {
        var colours = 1 << bits;
        var palette = Enumerable.Range(0, colours).Select(i => new Rgba32((byte)(i * 37), (byte)(255 - i * 11), (byte)(i * 5))).ToArray();
        var source = new PixelBuffer<Rgba32>(11, 3);
        for (var i = 0; i < source.Pixels.Length; i++)
            source.Pixels[i] = palette[i * 7 % colours];
        Assert.Equal(source.Pixels, Codec.DecodePixels<Rgba32>(new MemoryStream(PaletteBitmap(source, palette, bits))).Pixels);
    }

    /// <summary>Encoded runs, an absolute run, a delta and end-of-line escapes; skipped pixels are opaque black.</summary>
    [Fact]
    public void Rle8Bitmap_DecodesRunsAndEscapes()
    {
        byte[] rle =
        [
            12, 1, 0, 0,                              // bottom row: twelve of colour 1
            3, 2, 0, 5, 3, 4, 5, 6, 7, 0, 4, 2, 0, 0, // three of 2, absolute 3 4 5 6 7, four of 2
            0, 2, 4, 1,                               // delta right 4 and up 1, skipping the third row
            8, 3, 0, 0,                               // top row from x = 4: eight of 3
            12, 0, 0, 1,                              // past the top: ignored; end of bitmap
        ];
        var palette = Enumerable.Range(0, 8).Select(i => new Rgba32((byte)(i * 30), (byte)(200 - i * 20), (byte)(i * 11 + 5))).ToArray();
        var decoded = Codec.DecodePixels<Rgba32>(new MemoryStream(RleBitmap(rle, palette, 12, 4)));

        Rgba32[] Row(int y) => decoded.Row(y).ToArray();
        Assert.All(Row(3), p => Assert.Equal(palette[1], p));
        Assert.Equal(new[] { 2, 2, 2, 3, 4, 5, 6, 7, 2, 2, 2, 2 }.Select(i => palette[i]), Row(2));
        Assert.All(Row(1), p => Assert.Equal(new Rgba32(0, 0, 0), p));
        Assert.Equal(Enumerable.Repeat(new Rgba32(0, 0, 0), 4).Concat(Enumerable.Repeat(palette[3], 8)), Row(0));
    }

    // ---- Encoding ----

    [Fact]
    public async Task Jpeg_KeepsTheChromaSubsamplingOfAFullChromaSource()
    {
        foreach (var (source, expected) in new[] { (EncodeJpeg444(Pattern(64, 64), null), "4:4:4"), (TestImages.Encode(Pattern(64, 64), "jpeg", 85), "4:2:0") })
        {
            using var image = await Codec.LoadAsync(new MemoryStream(source), cancellationToken: TestContext.Current.CancellationToken);
            image.Resize(new PixelSize(32, 32));
            using var output = VipsImage.NewFromBuffer(await SaveAsync(image, ImageOutput.Jpeg(80)));
            Assert.Equal(expected, (string)output.Get("jpeg-chroma-subsample"));
        }
    }

    [Fact]
    public async Task Jpeg_DropsAlphaWithoutCompositing()
    {
        using var image = Codec.FromPixels(new PixelBuffer<Rgba32>(16, 16, new Rgba32(0, 255, 0, 0)));
        var decoded = Codec.DecodePixels<Rgba32>(new MemoryStream(await SaveAsync(image, ImageOutput.Jpeg(90))));
        Assert.All(decoded.Pixels, p => Assert.True(p.G > 240 && p.R < 16 && p.B < 16, p.ToString()));
    }

    // ---- Refusals ----

    [Theory]
    [InlineData("empty")]
    [InlineData("text")]
    [InlineData("svg")]
    [InlineData("heif")]
    public void UnknownOrBlockedFormats_AreUnsupported(string name)
    {
        var data = name switch
        {
            "empty" => [],
            "text" => "this is not an image, just some text that is long enough"u8.ToArray(),
            "svg" => "<svg xmlns='http://www.w3.org/2000/svg' width='4' height='4'/>"u8.ToArray(),
            _ => Heif(),
        };
        Assert.Throws<UnsupportedImageFormatException>(() => Codec.Identify(new MemoryStream(data)));
        Assert.Throws<UnsupportedImageFormatException>(() => Codec.DecodePixels<Rgba32>(new MemoryStream(data)));
    }

    [Theory]
    [InlineData("png")]
    [InlineData("gif")]
    [InlineData("jpeg")]
    public async Task TruncatedFiles_AreInvalid(string format)
    {
        var whole = format == "gif"
            ? TestImages.Animated("gif", TestImages.Solid(6, 4, new Rgba32(255, 0, 0)), TestImages.Solid(6, 4, new Rgba32(0, 0, 255)))
            : TestImages.Encode(Pattern(40, 30), format);
        var truncated = whole[..(format == "png" ? 33 : format == "gif" ? 13 : 6)];
        await Assert.ThrowsAsync<InvalidImageException>(async () =>
        {
            Codec.Identify(new MemoryStream(truncated));
            using var image = await Codec.LoadAsync(new MemoryStream(truncated), cancellationToken: TestContext.Current.CancellationToken);
            await SaveAsync(image, ImageOutput.Png);
        });
    }

    // ---- Malformed bitmaps: refused as image errors, never by crashing or allocating at will ----

    [Theory]
    [InlineData(1, 256)]
    [InlineData(4, 17)]
    public void PaletteBitmap_WithMoreColoursUsedThanItsDepthAllows_Decodes(int bits, int coloursUsed)
    {
        var palette = Enumerable.Range(0, 1 << bits).Select(i => new Rgba32((byte)(i * 15), 0, 0)).ToArray();
        var source = new PixelBuffer<Rgba32>(5, 2, palette[1]);
        var data = PaletteBitmap(source, palette, bits);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(46), coloursUsed);

        Assert.Equal(source.Pixels, Codec.DecodePixels<Rgba32>(new MemoryStream(data)).Pixels);
    }

    [Theory]
    [InlineData(1, 16384)]  // RLE8 declaring far more pixels than its data can describe
    [InlineData(0, 16384)]  // 24-bit with almost no pixel data
    public void Bitmap_DeclaringMorePixelsThanItHolds_IsInvalidBeforeAllocating(int compression, int size)
    {
        var data = new byte[14 + 40 + 1024];
        WriteHeaders(data, size, size, compression == 1 ? 8 : 24, 40, 54, compression, colours: 0);
        var before = GC.GetTotalAllocatedBytes(precise: true);

        Assert.Throws<InvalidImageException>(() => Codec.DecodePixels<Rgba32>(new MemoryStream(data)));
        Assert.True(GC.GetTotalAllocatedBytes(precise: true) - before < 64 << 20, "the decoder allocated for the declared size");
    }

    [Fact]
    public void Os2BitmapWithHuffmanCompression_IsUnsupported()
    {
        var data = new byte[14 + 64 + 64];
        WriteHeaders(data, 8, 8, 1, 64, 14 + 64, compression: 3, colours: 0);
        Assert.Throws<UnsupportedImageFormatException>(() => Codec.DecodePixels<Rgba32>(new MemoryStream(data)));
    }

    [Fact]
    public void Bitmap_IdentifyReadsOnlyTheHeader()
    {
        var data = Bitmap(Pattern(40, 30), 24, infoSize: 40);
        var stream = new MemoryStream(data);
        Assert.Equal(new ImageHeader(ImageFormat.Bmp, 40, 30, 1, 1), Codec.Identify(stream));
        Assert.True(stream.Position <= 1024);
    }

    // ---- Other colour spaces ----

    [Theory]
    [InlineData("lab")]
    [InlineData("scrgb")]
    public async Task OtherColourSpaces_EncodeAsTheirColours(string space)
    {
        using var red = VipsImage.Black(8, 8, bands: 3).Linear([1, 1, 1], [220, 30, 40]).Cast(Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb);
        using var converted = red.Colourspace(space == "lab" ? Enums.Interpretation.Lab : Enums.Interpretation.Scrgb);
        var tiff = converted.TiffsaveBuffer();

        foreach (var output in new[] { ImageOutput.Png, ImageOutput.Jpeg(95) })
        {
            using var image = await Codec.LoadAsync(new MemoryStream(tiff), cancellationToken: TestContext.Current.CancellationToken);
            var pixel = Codec.DecodePixels<Rgba32>(new MemoryStream(await SaveAsync(image, output))).Pixels[0];
            Assert.True(Math.Abs(pixel.R - 220) < 12 && Math.Abs(pixel.G - 30) < 12 && Math.Abs(pixel.B - 40) < 12, $"{space} as {output.Format}: {pixel}");
        }
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    public async Task TruncatedBody_IsInvalidWhenThePixelsAreRead(string format)
    {
        var whole = TestImages.Encode(Pattern(200, 150), format);
        var truncated = whole[..(whole.Length / 2)];
        Codec.Identify(new MemoryStream(truncated));

        using var image = await Codec.LoadAsync(new MemoryStream(truncated), cancellationToken: TestContext.Current.CancellationToken);
        var outcome = await Record.ExceptionAsync(() => SaveAsync(image, ImageOutput.Png));
        // libvips may decode what is there and fill the rest; when it refuses, it must be as an image error.
        Assert.True(outcome is null or InvalidImageException, outcome?.ToString());
    }

    // ---- Helpers ----

    private static PixelBuffer<Rgba32> Pattern(int width, int height, bool alpha = false)
    {
        var pixels = new PixelBuffer<Rgba32>(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                pixels[x, y] = new Rgba32(
                    (byte)(128 + 100 * Math.Sin(x / 7.0) * Math.Cos(y / 9.0)),
                    (byte)(255 * x / Math.Max(1, width - 1)),
                    (byte)((x / 8 + y / 8) % 2 == 0 ? 200 : 40),
                    alpha ? (byte)(40 + (x * 13 + y * 7) % 216) : (byte)255);
        return pixels;
    }

    private static byte[] EncodeJpeg444(PixelBuffer<Rgba32> pixels, int? orientation)
    {
        using var image = VipsImage.NewFromBuffer(TestImages.Encode(pixels, "png"));
        using var rgb = image.ExtractBand(0, n: 3);
        using var tagged = orientation is { } value ? rgb.Mutate(m => m.Set(GValue.GIntType, "orientation", value)) : rgb.Copy();
        return tagged.JpegsaveBuffer(q: 95, subsampleMode: Enums.ForeignSubsample.Off);
    }

    /// <summary>The upright picture a viewer sees for each EXIF orientation of the stored pixels.</summary>
    private static PixelBuffer<Rgba32> Orient(PixelBuffer<Rgba32> stored, int orientation)
    {
        var swap = orientation >= 5;
        var result = swap ? new PixelBuffer<Rgba32>(stored.Height, stored.Width) : new PixelBuffer<Rgba32>(stored.Width, stored.Height);
        int w = stored.Width, h = stored.Height;
        for (var y = 0; y < result.Height; y++)
            for (var x = 0; x < result.Width; x++)
            {
                var (sx, sy) = orientation switch
                {
                    1 => (x, y),
                    2 => (w - 1 - x, y),
                    3 => (w - 1 - x, h - 1 - y),
                    4 => (x, h - 1 - y),
                    5 => (y, x),
                    6 => (y, h - 1 - x),
                    7 => (w - 1 - y, h - 1 - x),
                    _ => (w - 1 - y, x),
                };
                result[x, y] = stored[sx, sy];
            }
        return result;
    }

    private static async Task<byte[]> SaveAsync(ILoadedImage image, ImageOutput output)
    {
        var stream = new MemoryStream();
        await image.SaveAsync(stream, output, TestContext.Current.CancellationToken);
        return stream.ToArray();
    }

    private static byte[] Heif()
    {
        using var black = VipsImage.Black(16, 16, bands: 3);
        using var rgb = black.Cast(Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb);
        return rgb.HeifsaveBuffer(compression: Enums.ForeignHeifCompression.Av1);
    }

    private static byte[] Bitmap(PixelBuffer<Rgba32> pixels, int bits, int infoSize, bool topDown = false)
    {
        var stride = (bits * pixels.Width + 31) / 32 * 4;
        var offset = 14 + infoSize;
        var file = new byte[offset + stride * pixels.Height];
        WriteHeaders(file, pixels.Width, topDown ? -pixels.Height : pixels.Height, bits, infoSize, offset, compression: infoSize >= 108 ? 3 : 0, colours: 0);
        if (infoSize >= 108)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(14 + 40), 0x00FF0000);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(14 + 44), 0x0000FF00);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(14 + 48), 0x000000FF);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(14 + 52), 0xFF000000);
        }
        for (var y = 0; y < pixels.Height; y++)
        {
            var row = file.AsSpan(offset + stride * (topDown ? y : pixels.Height - 1 - y));
            for (var x = 0; x < pixels.Width; x++)
            {
                var p = pixels[x, y];
                switch (bits)
                {
                    case 16:
                        BinaryPrimitives.WriteUInt16LittleEndian(row[(x * 2)..], (ushort)((p.R >> 3) << 10 | (p.G >> 3) << 5 | p.B >> 3));
                        break;
                    case 24:
                        (row[x * 3], row[x * 3 + 1], row[x * 3 + 2]) = (p.B, p.G, p.R);
                        break;
                    default:
                        (row[x * 4], row[x * 4 + 1], row[x * 4 + 2], row[x * 4 + 3]) = (p.B, p.G, p.R, p.A);
                        break;
                }
            }
        }
        return file;
    }

    private static byte[] PaletteBitmap(PixelBuffer<Rgba32> pixels, Rgba32[] palette, int bits)
    {
        var stride = (bits * pixels.Width + 31) / 32 * 4;
        var offset = 14 + 40 + palette.Length * 4;
        var file = new byte[offset + stride * pixels.Height];
        WriteHeaders(file, pixels.Width, pixels.Height, bits, 40, offset, compression: 0, colours: palette.Length);
        for (var i = 0; i < palette.Length; i++)
            (file[54 + i * 4], file[55 + i * 4], file[56 + i * 4]) = (palette[i].B, palette[i].G, palette[i].R);
        for (var y = 0; y < pixels.Height; y++)
        {
            var row = file.AsSpan(offset + stride * (pixels.Height - 1 - y));
            for (var x = 0; x < pixels.Width; x++)
            {
                var index = Array.IndexOf(palette, pixels[x, y]);
                var bit = x * bits;
                row[bit >> 3] |= (byte)(index << (8 - bits - (bit & 7)));
            }
        }
        return file;
    }

    private static byte[] RleBitmap(byte[] rle, Rgba32[] palette, int width, int height)
    {
        var offset = 14 + 40 + 256 * 4;
        var file = new byte[offset + rle.Length];
        WriteHeaders(file, width, height, 8, 40, offset, compression: 1, colours: 256);
        for (var i = 0; i < palette.Length; i++)
            (file[54 + i * 4], file[55 + i * 4], file[56 + i * 4]) = (palette[i].B, palette[i].G, palette[i].R);
        rle.CopyTo(file.AsSpan(offset));
        return file;
    }

    private static void WriteHeaders(byte[] file, int width, int height, int bits, int infoSize, int offset, int compression, int colours)
    {
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), offset);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(14), infoSize);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(28), (ushort)bits);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(30), compression);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(46), colours);
    }
}
