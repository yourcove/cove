using System.Reflection;
using Cove.Api.Imaging;
using Cove.Api.Services;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Tiff.Constants;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SharpRgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace Cove.Tests.Imaging;

/// <summary>
/// Runs the same images through <see cref="ImageSharpCodec"/> and <see cref="NetVipsCodec"/> and
/// compares what comes out: headers and lossless pixels must match exactly, lossy decodes and
/// resampled output must stay within a stated tolerance, and both must refuse the same bad input.
/// Each case writes its measurements to the test output, so a run doubles as a report of what the
/// switch of decoder changes.
/// </summary>
public sealed class ImageCodecComparisonTests
{
    private static readonly IImageCodec Reference = new ImageSharpCodec();
    private static readonly IImageCodec Candidate = new NetVipsCodec();

    public static TheoryData<string, int, int> PhashFixtures()
    {
        var data = new TheoryData<string, int, int>();
        var seen = new HashSet<(string, int, int)>();
        foreach (var name in new[] { nameof(StashImagePhashTests.LosslessImage_HashesExactlyAsStashDoes), nameof(StashImagePhashTests.LossyImage_HashesCloseToStash) })
        {
            var method = typeof(StashImagePhashTests).GetMethod(name)!;
            foreach (var row in method.GetCustomAttributes<InlineDataAttribute>())
            {
                var values = row.Data;
                var key = ((string)values[0]!, (int)values[1]!, (int)values[2]!);
                if (seen.Add(key))
                    data.Add(key.Item1, key.Item2, key.Item3);
            }
        }
        return data;
    }

    public static TheoryData<string> PhotoFixtures() => new(Photos.Keys);

    [Theory]
    [MemberData(nameof(PhashFixtures))]
    public void PhashFixture_IdentifiesAlike(string kind, int width, int height)
    {
        var data = StashImagePhashFixtures.Create(kind, width, height);
        Assert.Equal(Identify(Reference, data), Identify(Candidate, data));
    }

    [Theory]
    [MemberData(nameof(PhashFixtures))]
    public void PhashFixture_DecodesAlike(string kind, int width, int height)
    {
        var data = StashImagePhashFixtures.Create(kind, width, height);
        var format = Identify(Reference, data).Format;
        var lossy = IsLossy(kind);

        // The layouts StashImagePhash asks for, which together cover every conversion it relies on.
        // Two known differences are compared on their own terms, see KnownDifference.
        var known = KnownDifference(kind, width, height);
        var region = known == Known.OutsideFirstGifFrame ? GifFirstFrame(data) : (PixelRect?)null;
        Func<Rgba64, int[]> wide = known == Known.SixteenBitColorKey ? p => [p.R, p.G, p.B] : p => [p.R, p.G, p.B, p.A];
        Func<Rgba32, int[]> narrow = known == Known.SixteenBitColorKey ? p => [p.R, p.G, p.B] : p => [p.R, p.G, p.B, p.A];
        var rgba64 = Compare(Decode<Rgba64>(Reference, data), Decode<Rgba64>(Candidate, data), wide, region);
        var rgba32 = Compare(Decode<Rgba32>(Reference, data), Decode<Rgba32>(Candidate, data), narrow, region);
        Log($"{kind} {width}x{height} ({format}): Rgba64 {rgba64}; Rgba32 {rgba32}{(known == Known.None ? "" : $" (compared without {known})")}");

        if (known == Known.SixteenBitColorKey)
        {
            // libvips applies the key, as Go does; ImageSharp left every pixel opaque.
            var keyed = Decode<Rgba64>(Candidate, data).Pixels.Count(p => p.A == 0);
            Log($"  libvips made {keyed} keyed pixels transparent");
            Assert.True(keyed > 0);
        }

        if (lossy)
        {
            Assert.True(rgba32.Mean <= LossyMeanTolerance(kind), $"mean difference {rgba32.Mean:F3}");
            return;
        }
        Assert.Equal(0, rgba64.Max);
        Assert.Equal(0, rgba32.Max);
        if (kind.StartsWith("png_g", StringComparison.Ordinal) && !kind.Contains("trns", StringComparison.Ordinal))
        {
            Assert.Equal(0, Compare(Decode<L8>(Reference, data), Decode<L8>(Candidate, data), p => [p.Value]).Max);
            Assert.Equal(0, Compare(Decode<L16>(Reference, data), Decode<L16>(Candidate, data), p => [p.Value]).Max);
        }
    }

    [Theory]
    [MemberData(nameof(PhashFixtures))]
    public void PhashFixture_HashesAlike(string kind, int width, int height)
    {
        var data = StashImagePhashFixtures.Create(kind, width, height);
        var reference = StashImagePhash.Compute(new MemoryStream(data), Reference);
        var candidate = StashImagePhash.Compute(new MemoryStream(data), Candidate);
        var distance = MetadataServerService.ComputePhashHammingDistance(GoImageHash.Format(reference.Hash), GoImageHash.Format(candidate.Hash));
        Log($"{kind} {width}x{height}: {GoImageHash.Format(reference.Hash)} vs {GoImageHash.Format(candidate.Hash)}, distance {distance}");

        Assert.Equal((reference.Width, reference.Height), (candidate.Width, candidate.Height));
        Assert.InRange(distance, 0, IsLossy(kind) ? 2 : 0);
    }

    [Theory]
    [MemberData(nameof(PhotoFixtures))]
    public void Photo_IdentifiesAlike(string name)
    {
        var data = Photos[name]();
        Assert.Equal(Identify(Reference, data), Identify(Candidate, data));
    }

    [Theory]
    [MemberData(nameof(PhotoFixtures))]
    public void Photo_DecodesAlike(string name)
    {
        var data = Photos[name]();
        var difference = Compare(Decode<Rgba32>(Reference, data), Decode<Rgba32>(Candidate, data), p => [p.R, p.G, p.B, p.A]);
        Log($"{name}: {difference}");
        if (name == "jpeg_cmyk")
            Assert.True(difference.Psnr >= 40, difference.ToString());
        else if (name.StartsWith("jpeg", StringComparison.Ordinal) || name.StartsWith("webp_lossy", StringComparison.Ordinal))
            Assert.True(difference.Mean <= LossyMeanTolerance(name) && difference.Psnr >= 28, difference.ToString());
        else
            Assert.Equal(0, difference.Max);
    }

    /// <summary>The thumbnail pipeline: orient, fit inside a box, encode. Losslessly, so only the resample differs.</summary>
    [Theory]
    [MemberData(nameof(PhotoFixtures))]
    public async Task Photo_ThumbnailsAlike(string name)
    {
        var data = Photos[name]();
        foreach (var box in new[] { 160, 77 })
        {
            var reference = await ThumbnailAsync(Reference, data, box, ImageOutput.Png);
            var candidate = await ThumbnailAsync(Candidate, data, box, ImageOutput.Png);
            var difference = Compare(Decode<Rgba32>(Reference, reference), Decode<Rgba32>(Reference, candidate), p => [p.R, p.G, p.B, p.A]);
            Log($"{name} fit {box}: {difference}");
            Assert.Equal(Identify(Reference, reference) with { Format = ImageFormat.Png }, Identify(Reference, candidate) with { Format = ImageFormat.Png });
            // libvips converts CMYK to RGB through an ICC profile; ImageSharp used the plain formula.
            Assert.True(difference.Psnr >= (name == "jpeg_cmyk" ? 15 : 30), difference.ToString());
        }

        // Re-encoding a tiny image as JPEG compares the encoders' block artefacts, not the pipelines.
        if (name == "bmp_rle8")
            return;
        var referenceJpeg = await ThumbnailAsync(Reference, data, 160, ImageOutput.Jpeg(80));
        var candidateJpeg = await ThumbnailAsync(Candidate, data, 160, ImageOutput.Jpeg(80));
        var jpeg = Compare(Decode<Rgba32>(Reference, referenceJpeg), Decode<Rgba32>(Reference, candidateJpeg), p => [p.R, p.G, p.B]);
        Log($"{name} fit 160 as JPEG q80: {jpeg}; {referenceJpeg.Length} vs {candidateJpeg.Length} bytes");
        Assert.True(jpeg.Psnr >= (name == "jpeg_cmyk" ? 15 : 28), jpeg.ToString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task Orientation_IsAppliedAlike(ushort orientation)
    {
        // Full-resolution chroma, so the decoders agree closely and only the rotation is under test.
        var data = Encode(Photo(96, 64), new JpegEncoder { Quality = 95, ColorType = JpegEncodingColor.YCbCrRatio444 }, orientation);
        Assert.Equal(orientation, Identify(Candidate, data).Orientation);
        Assert.Equal(Identify(Reference, data), Identify(Candidate, data));

        // Upright and losslessly encoded, the two must agree to within the JPEG decoders' rounding.
        var reference = await ThumbnailAsync(Reference, data, 1000, ImageOutput.Png);
        var candidate = await ThumbnailAsync(Candidate, data, 1000, ImageOutput.Png);
        var difference = Compare(Decode<Rgba32>(Reference, reference), Decode<Rgba32>(Reference, candidate), p => [p.R, p.G, p.B]);
        Log($"orientation {orientation}: {difference}");
        Assert.True(difference.Mean <= LossyMeanTolerance("jpeg_444"), difference.ToString());
    }

    [Fact]
    public async Task Crop_CopiesTheSamePixels()
    {
        var data = Encode(Photo(120, 80), new SixLabors.ImageSharp.Formats.Png.PngEncoder());
        var area = new PixelRect(17, 9, 61, 40);
        var reference = await EditAsync(Reference, data, image => image.Crop(area), ImageOutput.Png);
        var candidate = await EditAsync(Candidate, data, image => image.Crop(area), ImageOutput.Png);
        Assert.Equal(0, Compare(Decode<Rgba32>(Reference, reference), Decode<Rgba32>(Reference, candidate), p => [p.R, p.G, p.B, p.A]).Max);
    }

    [Fact]
    public async Task TargetSize_ReducesTheDecode()
    {
        var data = Encode(Photo(1600, 900), new JpegEncoder { Quality = 85 });
        var target = new PixelSize(160, 90);
        foreach (var codec in new[] { Reference, Candidate })
        {
            using var image = await codec.LoadAsync(new MemoryStream(data), new ImageLoadOptions(target), TestContext.Current.CancellationToken);
            Log($"{codec.GetType().Name} loaded {image.Width}x{image.Height} for a {target.Width}x{target.Height} target");
            Assert.True(image.Width < 1600 && image.Height < 900);
        }

        // libvips shrinks by a power of two and never below the target, so its resample is always a reduction.
        using var shrunk = await Candidate.LoadAsync(new MemoryStream(data), new ImageLoadOptions(target), TestContext.Current.CancellationToken);
        Assert.Equal((200, 112), (shrunk.Width, shrunk.Height));
    }

    [Fact]
    public async Task SpriteSheet_EncodesAlike()
    {
        var sheet = new PixelBuffer<Rgba32>(300, 200);
        var photo = Decode<Rgba32>(Reference, Encode(Photo(150, 100), new SixLabors.ImageSharp.Formats.Png.PngEncoder()));
        for (var y = 0; y < 100; y++)
            photo.Row(y).CopyTo(sheet.Row(y + 50)[75..]);

        var encoded = new List<byte[]>();
        foreach (var codec in new[] { Reference, Candidate })
        {
            using var image = codec.FromPixels(sheet);
            var output = new MemoryStream();
            await image.SaveAsync(output, ImageOutput.Jpeg(75), TestContext.Current.CancellationToken);
            encoded.Add(output.ToArray());
        }
        var difference = Compare(Decode<Rgba32>(Reference, encoded[0]), Decode<Rgba32>(Reference, encoded[1]), p => [p.R, p.G, p.B]);
        Log($"sprite sheet q75: {difference}; {encoded[0].Length} vs {encoded[1].Length} bytes");
        Assert.True(difference.Psnr >= 30, difference.ToString());
    }

    public static TheoryData<string> BadInputs() => new(Bad.Keys);

    [Theory]
    [MemberData(nameof(BadInputs))]
    public async Task BadInput_IsRefusedAlike(string name)
    {
        var data = Bad[name]();
        var reference = await RefusalAsync(Reference, data);
        var candidate = await RefusalAsync(Candidate, data);
        Log($"{name}: ImageSharp {reference?.GetType().Name ?? "accepted"}, NetVips {candidate?.GetType().Name ?? "accepted"}");
        Assert.NotNull(reference);
        Assert.NotNull(candidate);
        // A file that starts like a JPEG but stops is unrecognised to ImageSharp and damaged to libvips.
        if (name != "jpeg_soi_only")
            Assert.Equal(reference.GetType(), candidate.GetType());
    }

    // ---- Inputs ----

    private static readonly Dictionary<string, Func<byte[]>> Photos = new()
    {
        ["jpeg_420"] = () => Encode(Photo(333, 211), new JpegEncoder { Quality = 85, ColorType = JpegEncodingColor.YCbCrRatio420 }),
        ["jpeg_444"] = () => Encode(Photo(333, 211), new JpegEncoder { Quality = 92, ColorType = JpegEncodingColor.YCbCrRatio444 }),
        ["jpeg_gray"] = () => Encode(Photo(333, 211), new JpegEncoder { Quality = 85, ColorType = JpegEncodingColor.Luminance }),
        ["jpeg_progressive"] = () => Progressive(Encode(Photo(333, 211), new SixLabors.ImageSharp.Formats.Png.PngEncoder())),
        ["jpeg_cmyk"] = () => Encode(Photo(333, 211), new JpegEncoder { Quality = 90, ColorType = JpegEncodingColor.Cmyk }),
        ["png_rgba"] = () => Encode(Photo(333, 211, alpha: true), new SixLabors.ImageSharp.Formats.Png.PngEncoder()),
        ["png_rgb"] = () => Encode(Photo(333, 211), new SixLabors.ImageSharp.Formats.Png.PngEncoder()),
        ["webp_lossy"] = () => Encode(Photo(333, 211), new WebpEncoder { FileFormat = WebpFileFormatType.Lossy, Quality = 85 }),
        ["webp_lossless_alpha"] = () => Encode(Photo(333, 211, alpha: true), new WebpEncoder { FileFormat = WebpFileFormatType.Lossless }),
        ["tiff_lzw"] = () => Encode(Photo(333, 211), new TiffEncoder { Compression = TiffCompression.Lzw }),
        ["tiff_deflate_rgba"] = () => Encode(Photo(333, 211, alpha: true), new TiffEncoder { Compression = TiffCompression.Deflate }),
        ["bmp_24"] = () => Encode(Photo(333, 211), new SixLabors.ImageSharp.Formats.Bmp.BmpEncoder { BitsPerPixel = SixLabors.ImageSharp.Formats.Bmp.BmpBitsPerPixel.Pixel24 }),
        ["bmp_32_alpha"] = () => Encode(Photo(333, 211, alpha: true), new SixLabors.ImageSharp.Formats.Bmp.BmpEncoder { BitsPerPixel = SixLabors.ImageSharp.Formats.Bmp.BmpBitsPerPixel.Pixel32, SupportTransparency = true }),
        ["bmp_16"] = () => Encode(Photo(333, 211), new SixLabors.ImageSharp.Formats.Bmp.BmpEncoder { BitsPerPixel = SixLabors.ImageSharp.Formats.Bmp.BmpBitsPerPixel.Pixel16 }),
        ["bmp_8"] = () => Encode(Photo(333, 211), new SixLabors.ImageSharp.Formats.Bmp.BmpEncoder { BitsPerPixel = SixLabors.ImageSharp.Formats.Bmp.BmpBitsPerPixel.Pixel8 }),
        ["bmp_rle8"] = () => RleBitmap(),
        ["gif_animated"] = () => Animated(Photo(120, 80), Photo(120, 80, phase: 2), "gif"),
        ["webp_animated"] = () => Animated(Photo(120, 80), Photo(120, 80, phase: 2), "webp"),
    };

    private static readonly Dictionary<string, Func<byte[]>> Bad = new()
    {
        ["empty"] = () => [],
        ["text"] = () => "this is not an image, just some text that is long enough"u8.ToArray(),
        ["svg"] = () => "<svg xmlns='http://www.w3.org/2000/svg' width='4' height='4'/>"u8.ToArray(),
        ["png_header_only"] = () => Photos["png_rgb"]()[..33],
        ["jpeg_soi_only"] = () => [0xFF, 0xD8, 0xFF, 0xE0, 0, 16],
        ["gif_header_only"] = () => Photos["gif_animated"]()[..13],
    };

    private static SixLabors.ImageSharp.Image<SharpRgba32> Photo(int width, int height, bool alpha = false, int phase = 0)
    {
        var image = new SixLabors.ImageSharp.Image<SharpRgba32>(width, height);
        var random = new Random(width * 31 + height + phase);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    // Smooth gradients, a fine pattern, hard edges and a little noise, like a photo.
                    var r = 128 + 100 * Math.Sin((x + phase * 10) / 23.0) * Math.Cos(y / 31.0);
                    var g = 255.0 * x / width;
                    var b = (x / 16 + y / 16) % 2 == 0 ? 200 : 40;
                    var noise = random.Next(-6, 7);
                    var a = alpha ? (byte)Math.Clamp(255 * (1 - Math.Abs(x - width / 2.0) / (width / 2.0)) + 30, 0, 255) : (byte)255;
                    row[x] = new SharpRgba32(Clamp(r + noise), Clamp(g + noise), Clamp(b + noise), a);
                }
            }
        });
        return image;
    }

    private static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static byte[] Encode(SixLabors.ImageSharp.Image<SharpRgba32> image, SixLabors.ImageSharp.Formats.IImageEncoder encoder, ushort? orientation = null)
    {
        using (image)
        {
            if (orientation is { } value)
            {
                image.Metadata.ExifProfile = new ExifProfile();
                image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, value);
            }
            var output = new MemoryStream();
            image.Save(output, encoder);
            return output.ToArray();
        }
    }

    /// <summary>ImageSharp writes only baseline JPEG, so libvips encodes the progressive case.</summary>
    private static byte[] Progressive(byte[] png)
    {
        using var image = NetVips.Image.NewFromBuffer(png);
        return image.JpegsaveBuffer(q: 85, interlace: true);
    }

    private static byte[] Animated(SixLabors.ImageSharp.Image<SharpRgba32> first, SixLabors.ImageSharp.Image<SharpRgba32> second, string format)
    {
        using (second)
        {
            first.Frames.AddFrame(second.Frames.RootFrame);
            return Encode(first, format == "gif"
                ? new SixLabors.ImageSharp.Formats.Gif.GifEncoder()
                : new WebpEncoder { FileFormat = WebpFileFormatType.Lossless });
        }
    }

    /// <summary>An 8-bit RLE bitmap with encoded runs, an absolute run, a delta and end-of-line escapes.</summary>
    private static byte[] RleBitmap()
    {
        const int width = 12, height = 4;
        byte[] rle =
        [
            12, 1, 0, 0,                       // row 0: twelve of colour 1
            3, 2, 0, 5, 3, 4, 5, 6, 7, 0, 4, 2, 0, 0, // row 1: three of 2, absolute 3 4 5 6 7, four of 2
            0, 2, 4, 1,                        // delta right 4, down 1
            8, 3, 0, 0,                        // row 2 from x=4: eight of 3
            12, 0, 0, 1,                       // row 3: twelve of 0, end of bitmap
        ];
        var palette = new byte[256 * 4];
        for (var i = 0; i < 8; i++)
        {
            palette[i * 4] = (byte)(i * 30);
            palette[i * 4 + 1] = (byte)(200 - i * 20);
            palette[i * 4 + 2] = (byte)(i * 11 + 5);
        }
        var offset = 14 + 40 + palette.Length;
        var file = new byte[offset + rle.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.TryWriteBytes(file.AsSpan(2), file.Length);
        BitConverter.TryWriteBytes(file.AsSpan(10), offset);
        BitConverter.TryWriteBytes(file.AsSpan(14), 40);
        BitConverter.TryWriteBytes(file.AsSpan(18), width);
        BitConverter.TryWriteBytes(file.AsSpan(22), height);
        BitConverter.TryWriteBytes(file.AsSpan(26), (ushort)1);
        BitConverter.TryWriteBytes(file.AsSpan(28), (ushort)8);
        BitConverter.TryWriteBytes(file.AsSpan(30), 1);
        BitConverter.TryWriteBytes(file.AsSpan(34), rle.Length);
        BitConverter.TryWriteBytes(file.AsSpan(46), 256);
        palette.CopyTo(file.AsSpan(54));
        rle.CopyTo(file.AsSpan(offset));
        return file;
    }

    private enum Known
    {
        None,
        /// <summary>Outside a GIF's first frame ImageSharp shows other content; libvips leaves it transparent.</summary>
        OutsideFirstGifFrame,
        /// <summary>libvips applies a 16-bit PNG's tRNS key; ImageSharp ignored it.</summary>
        SixteenBitColorKey,
    }

    private static Known KnownDifference(string kind, int width, int height)
    {
        if (kind is "png_g16_trns" or "png_rgb16_trns")
            return Known.SixteenBitColorKey;
        if (kind.StartsWith("gif", StringComparison.Ordinal) && GifFirstFrame(StashImagePhashFixtures.Create(kind, width, height)) != new PixelRect(0, 0, width, height))
            return Known.OutsideFirstGifFrame;
        return Known.None;
    }

    private static PixelRect GifFirstFrame(byte[] data) => StashImagePhash.ReadGifFirstFrameBounds(new MemoryStream(data)) ?? default;

    /// <summary>
    /// libjpeg-turbo upsamples 4:2:0 chroma with a smoothing filter where ImageSharp did not, which moves
    /// colours at sharp edges; full-resolution chroma decodes nearly alike.
    /// </summary>
    private static double LossyMeanTolerance(string kind) => kind.Contains("444", StringComparison.Ordinal) || kind.Contains("gray", StringComparison.Ordinal) ? 1.0 : 6.0;

    private static bool IsLossy(string kind) => kind.StartsWith("jpg", StringComparison.Ordinal) || kind is "webp_lossy" or "webp_lossy_alpha";

    // ---- Helpers ----

    private static ImageHeader Identify(IImageCodec codec, byte[] data) => codec.Identify(new MemoryStream(data));

    private static PixelBuffer<TPixel> Decode<TPixel>(IImageCodec codec, byte[] data) where TPixel : unmanaged, IPixel<TPixel>
        => codec.DecodePixels<TPixel>(new MemoryStream(data));

    private static Task<byte[]> ThumbnailAsync(IImageCodec codec, byte[] data, int box, ImageOutput output)
        => EditAsync(codec, data, image =>
        {
            image.AutoOrient();
            if (image.Width > box || image.Height > box)
                image.Resize(ImageGeometry.FitWithin(new PixelSize(image.Width, image.Height), new PixelSize(box, box)));
        }, output);

    private static async Task<byte[]> EditAsync(IImageCodec codec, byte[] data, Action<ILoadedImage> edit, ImageOutput output)
    {
        using var image = await codec.LoadAsync(new MemoryStream(data), cancellationToken: TestContext.Current.CancellationToken);
        edit(image);
        var stream = new MemoryStream();
        await image.SaveAsync(stream, output, TestContext.Current.CancellationToken);
        return stream.ToArray();
    }

    private static async Task<Exception?> RefusalAsync(IImageCodec codec, byte[] data)
    {
        try
        {
            codec.Identify(new MemoryStream(data));
            using var image = await codec.LoadAsync(new MemoryStream(data), cancellationToken: TestContext.Current.CancellationToken);
            await image.SaveAsync(new MemoryStream(), ImageOutput.Png, TestContext.Current.CancellationToken);
            return null;
        }
        catch (ImageDecodeException ex)
        {
            return ex;
        }
    }

    private readonly record struct Difference(int Max, double Mean, double Psnr)
    {
        public override string ToString() => $"max {Max}, mean {Mean:F3}, PSNR {(double.IsPositiveInfinity(Psnr) ? "∞" : Psnr.ToString("F1"))} dB";
    }

    /// <summary>Per-sample differences, scaled to 8 bits so 16-bit and 8-bit layouts read alike.</summary>
    private static Difference Compare<TPixel>(PixelBuffer<TPixel> expected, PixelBuffer<TPixel> actual, Func<TPixel, int[]> samples, PixelRect? region = null)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        var scale = TPixel.Layout is PixelLayout.L16 or PixelLayout.Rgba64 ? 257.0 : 1.0;
        long count = 0;
        double sum = 0, squares = 0;
        var max = 0;
        for (var i = 0; i < expected.Pixels.Length; i++)
        {
            if (region is { } area && !area.Contains(i % expected.Width, i / expected.Width))
                continue;
            var e = samples(expected.Pixels[i]);
            var a = samples(actual.Pixels[i]);
            for (var c = 0; c < e.Length; c++)
            {
                var d = Math.Abs(e[c] - a[c]);
                max = Math.Max(max, d);
                sum += d / scale;
                squares += d / scale * (d / scale);
                count++;
            }
        }
        var mse = squares / count;
        return new Difference((int)Math.Ceiling(max / scale), sum / count, mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse));
    }

    private static void Log(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);
}
