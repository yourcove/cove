using System.Runtime.InteropServices;
using NetVips;
using VipsImage = NetVips.Image;

namespace Cove.Api.Imaging;

/// <summary>
/// <see cref="IImageCodec"/> backed by libvips through NetVips. Bitmaps, which libvips reads only
/// through ImageMagick, are decoded by <see cref="BmpDecoder"/>.
/// </summary>
/// <remarks>
/// libvips is configured process-wide on first use: its operation cache is off, because Cove
/// processes each file once and the cache would only hold memory and file handles, and every loader
/// is blocked except JPEG, PNG, GIF, WebP and TIFF. The blocked loaders include formats ImageSharp
/// never read (HEIF, SVG) whose callers already route them elsewhere, and loaders libvips itself
/// marks as unsafe for untrusted input.
/// </remarks>
public sealed class NetVipsCodec : IImageCodec
{
    private static readonly string[] AllowedLoaders =
    [
        "VipsForeignLoadJpeg",
        "VipsForeignLoadPng",
        "VipsForeignLoadNsgif",
        "VipsForeignLoadWebp",
        "VipsForeignLoadTiff",
    ];

    private static readonly Lazy<bool> Configured = new(Configure, LazyThreadSafetyMode.ExecutionAndPublication);

    public NetVipsCodec()
    {
        _ = Configured.Value;
    }

    private static bool Configure()
    {
        if (!ModuleInitializer.VipsInitialized)
            throw new InvalidOperationException("libvips could not be loaded.", ModuleInitializer.Exception);

        Cache.Max = 0;
        Cache.MaxMem = 0;
        Cache.MaxFiles = 0;
        NetVips.NetVips.BlockUntrusted = true;
        Operation.Block("VipsForeignLoad", true);
        foreach (var loader in AllowedLoaders)
            Operation.Block(loader, false);
        return true;
    }

    public ImageHeader Identify(Stream stream)
    {
        if (!stream.CanSeek)
            return IdentifyBuffered(ReadAll(stream));

        var start = stream.Position;
        Span<byte> signature = stackalloc byte[2];
        var read = stream.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false);
        stream.Position = start;
        if (read == 2 && signature[0] == 'B' && signature[1] == 'M')
            return IdentifyBuffered(ReadAll(stream));

        string? loader;
        try
        {
            loader = VipsImage.FindLoadStream(stream);
        }
        catch (VipsException)
        {
            loader = null;
        }
        if (loader is null)
            throw new UnsupportedImageFormatException("The data is not in a supported image format.");

        stream.Position = start;
        try
        {
            using var image = VipsImage.NewFromStream(stream, access: Enums.Access.Sequential);
            return ToHeader(loader, image);
        }
        catch (VipsException ex)
        {
            throw new InvalidImageException(FirstLine(ex), ex);
        }
    }

    public async Task<ImageHeader> IdentifyAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (stream.CanSeek)
            return Identify(stream);
        return IdentifyBuffered(await ReadAllAsync(stream, cancellationToken));
    }

    private static ImageHeader IdentifyBuffered(byte[] data)
    {
        if (BmpDecoder.IsBmp(data))
        {
            var (width, height) = BmpDecoder.ReadSize(data);
            return new ImageHeader(ImageFormat.Bmp, width, height, 1, 1);
        }

        var loader = FindLoader(data);
        try
        {
            using var image = VipsImage.NewFromBuffer(data, access: Enums.Access.Sequential);
            return ToHeader(loader, image);
        }
        catch (VipsException ex)
        {
            throw new InvalidImageException(FirstLine(ex), ex);
        }
    }

    public async Task<ILoadedImage> LoadAsync(Stream stream, ImageLoadOptions options = default, CancellationToken cancellationToken = default)
    {
        var data = await ReadAllAsync(stream, cancellationToken);
        return new Loaded(Open(data, options.TargetSize));
    }

    public PixelBuffer<TPixel> DecodePixels<TPixel>(Stream stream) where TPixel : unmanaged, IPixel<TPixel>
    {
        var data = ReadAll(stream);
        if (BmpDecoder.IsBmp(data))
            return PixelConverter.FromRgba32<TPixel>(BmpDecoder.Decode(data));

        using var image = Open(data, targetSize: null);
        if (TiffColorMap(data) is { } colorMap)
        {
            var narrow = Translate(() => PixelConverter.FromVips<Rgba32>(image));
            if (RestorePalette(narrow, colorMap) is { } exact)
                return PixelConverter.FromRgba64<TPixel>(exact);
            return PixelConverter.FromRgba32<TPixel>(narrow);
        }
        return Translate(() => PixelConverter.FromVips<TPixel>(image));
    }

    /// <summary>The 16-bit colour map of a palette TIFF, which libvips keeps only to 8 bits.</summary>
    private static ushort[]? TiffColorMap(byte[] data)
    {
        if (data.Length < 4 || !((data[0] == 'I' && data[1] == 'I') || (data[0] == 'M' && data[1] == 'M')))
            return null;
        return EncodedImageHeaders.ReadTiff(new MemoryStream(data, writable: false))?.ColorMap;
    }

    /// <summary>
    /// Maps each pixel libvips decoded from a palette TIFF back to its colour-map entry, whose 16-bit
    /// value is what other decoders return. libvips keeps the high byte of each entry, or the entry
    /// itself when every entry is below 256. Returns null when a pixel matches no entry, or matches
    /// entries that differ only in their low bytes.
    /// </summary>
    private static PixelBuffer<Rgba64>? RestorePalette(PixelBuffer<Rgba32> decoded, ushort[] colorMap)
    {
        var entries = colorMap.Length / 3;
        var small = colorMap.All(static value => value < 256);
        var lookup = new Dictionary<int, int>(entries);
        for (var i = 0; i < entries; i++)
        {
            int Narrow(ushort value) => small ? value : value >> 8;
            var key = Narrow(colorMap[i]) << 16 | Narrow(colorMap[entries + i]) << 8 | Narrow(colorMap[2 * entries + i]);
            if (lookup.TryGetValue(key, out var existing)
                && (colorMap[existing] != colorMap[i] || colorMap[entries + existing] != colorMap[entries + i] || colorMap[2 * entries + existing] != colorMap[2 * entries + i]))
                lookup[key] = -1;
            else
                lookup.TryAdd(key, i);
        }

        var exact = new PixelBuffer<Rgba64>(decoded.Width, decoded.Height);
        for (var i = 0; i < decoded.Pixels.Length; i++)
        {
            var p = decoded.Pixels[i];
            if (!lookup.TryGetValue(p.R << 16 | p.G << 8 | p.B, out var entry) || entry < 0)
                return null;
            exact.Pixels[i] = new Rgba64(colorMap[entry], colorMap[entries + entry], colorMap[2 * entries + entry], (ushort)(p.A * 0x101));
        }
        return exact;
    }

    public ILoadedImage FromPixels(PixelBuffer<Rgba32> pixels) => new Loaded(ToVips(pixels));

    private static VipsImage ToVips(PixelBuffer<Rgba32> pixels)
    {
        using var raw = VipsImage.NewFromMemoryCopy(MemoryMarshal.AsBytes(pixels.Pixels.AsSpan()), pixels.Width, pixels.Height, 4, Enums.BandFormat.Uchar);
        return raw.Copy(interpretation: Enums.Interpretation.Srgb);
    }

    /// <summary>Opens the first frame. Nothing is decoded until pixels are read.</summary>
    private static VipsImage Open(byte[] data, PixelSize? targetSize)
    {
        if (BmpDecoder.IsBmp(data))
            return ToVips(BmpDecoder.Decode(data));

        var loader = FindLoader(data);
        try
        {
            if (targetSize is { } target && loader.Contains("Jpeg", StringComparison.Ordinal))
            {
                // JPEG decodes at 1/2, 1/4 or 1/8 scale for little more than the cost of reading it.
                using var header = VipsImage.NewFromBuffer(data, access: Enums.Access.Sequential);
                var shrink = 8;
                // libvips rounds the reduced size down, so the check does too.
                while (shrink > 1 && (header.Width / shrink < target.Width || header.Height / shrink < target.Height))
                    shrink /= 2;
                if (shrink > 1)
                    return VipsImage.NewFromBuffer(data, kwargs: new VOption { { "shrink", shrink } });
            }
            return VipsImage.NewFromBuffer(data);
        }
        catch (VipsException ex)
        {
            throw new InvalidImageException(FirstLine(ex), ex);
        }
    }

    private static string FindLoader(byte[] data)
    {
        string? loader;
        try
        {
            loader = VipsImage.FindLoadBuffer(data);
        }
        catch (VipsException)
        {
            loader = null;
        }
        return loader ?? throw new UnsupportedImageFormatException("The data is not in a supported image format.");
    }

    private static ImageHeader ToHeader(string loader, VipsImage image)
    {
        var format = loader switch
        {
            _ when loader.Contains("Jpeg", StringComparison.Ordinal) => ImageFormat.Jpeg,
            _ when loader.Contains("Png", StringComparison.Ordinal) => ImageFormat.Png,
            _ when loader.Contains("gif", StringComparison.OrdinalIgnoreCase) => ImageFormat.Gif,
            _ when loader.Contains("Webp", StringComparison.Ordinal) => ImageFormat.Webp,
            _ when loader.Contains("Tiff", StringComparison.Ordinal) => ImageFormat.Tiff,
            _ => ImageFormat.Unknown,
        };
        var frames = image.Contains("n-pages") ? Math.Max(1, (int)image.Get("n-pages")) : 1;
        var orientation = image.Contains("orientation") ? (int)image.Get("orientation") : 1;
        return new ImageHeader(format, image.Width, image.Height, frames, orientation is >= 1 and <= 8 ? orientation : 1);
    }

    private static T Translate<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (VipsException ex)
        {
            throw new InvalidImageException(FirstLine(ex), ex);
        }
    }

    private static string FirstLine(VipsException ex)
    {
        var message = ex.Message.Trim();
        var newline = message.IndexOf('\n');
        return newline < 0 ? message : message[..newline];
    }

    private static byte[] ReadAll(Stream stream)
    {
        if (stream is MemoryStream memory && memory.TryGetBuffer(out var buffer))
        {
            var start = (int)memory.Position;
            memory.Position = memory.Length;
            return buffer.AsSpan(start, buffer.Count - start).ToArray();
        }
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream is MemoryStream)
            return ReadAll(stream);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, cancellationToken);
        return copy.ToArray();
    }

    private sealed class Loaded(VipsImage image) : ILoadedImage
    {
        private VipsImage _image = image;

        public int Width => _image.Width;
        public int Height => _image.Height;

        public void AutoOrient() => Replace(Translate(() => _image.Autorot()));

        public void Crop(PixelRect area)
        {
            if (area.IsEmpty || area.X < 0 || area.Y < 0 || area.Right > Width || area.Bottom > Height)
                throw new ArgumentOutOfRangeException(nameof(area), area, $"The crop must lie inside the {Width}×{Height} image.");
            Replace(Translate(() => _image.Crop(area.X, area.Y, area.Width, area.Height)));
        }

        public void Resize(PixelSize size)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size.Width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size.Height);
            if (size.Width == Width && size.Height == Height)
                return;

            Replace(Translate(() =>
            {
                // Filtering straight alpha would bleed the colour of transparent pixels into their
                // neighbours, so the colour is premultiplied for the resample, as ImageSharp did.
                var format = _image.Format;
                var source = _image.HasAlpha() ? _image.Premultiply() : _image;
                try
                {
                    // libvips rounds the output size; scales of target / source round back to the target.
                    using var resized = source.Resize(size.Width / (double)Width, kernel: Enums.Kernel.Cubic, vscale: size.Height / (double)Height);
                    if (resized.Width != size.Width || resized.Height != size.Height)
                        throw new InvalidOperationException($"libvips resized to {resized.Width}×{resized.Height} rather than {size.Width}×{size.Height}.");
                    using var straight = _image.HasAlpha() ? resized.Unpremultiply() : resized.Copy();
                    return straight.Cast(format);
                }
                finally
                {
                    if (!ReferenceEquals(source, _image))
                        source.Dispose();
                }
            }));
        }

        public async Task SaveAsync(Stream destination, ImageOutput output, CancellationToken cancellationToken = default)
        {
            var encoded = Translate(() => Encode(_image, output));
            await destination.WriteAsync(encoded, cancellationToken);
        }

        public void Dispose() => _image.Dispose();

        private void Replace(VipsImage next)
        {
            var previous = _image;
            _image = next;
            previous.Dispose();
        }

        private static byte[] Encode(VipsImage image, ImageOutput output)
        {
            var disposables = new List<VipsImage>();
            try
            {
                VipsImage Keep(VipsImage next)
                {
                    disposables.Add(next);
                    return next;
                }

                if (image.Interpretation == Enums.Interpretation.Cmyk)
                    image = Keep(image.Colourspace(Enums.Interpretation.Srgb));
                if (image.Format is not (Enums.BandFormat.Uchar or Enums.BandFormat.Ushort))
                    image = Keep(image.Cast(Enums.BandFormat.Ushort));

                if (output.Format == ImageOutputFormat.Png)
                    return image.PngsaveBuffer();

                if (image.Format == Enums.BandFormat.Ushort)
                    image = Keep(image.Cast(Enums.BandFormat.Uchar, shift: true));
                // JPEG has no alpha: keep the colour bands as stored, without compositing.
                if (image.Bands is 2 or 4)
                    image = Keep(image.ExtractBand(0, n: image.Bands - 1));
                // ImageSharp re-encoded a JPEG with the chroma subsampling it was read with.
                var fullChroma = image.Contains("jpeg-chroma-subsample") && (string)image.Get("jpeg-chroma-subsample") == "4:4:4";
                return image.JpegsaveBuffer(q: output.Quality, subsampleMode: fullChroma ? Enums.ForeignSubsample.Off : Enums.ForeignSubsample.On);
            }
            finally
            {
                foreach (var disposable in disposables)
                    disposable.Dispose();
            }
        }
    }
}
