using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using Cove.Core.Entities.Galleries.Zip;

namespace Cove.Api.Services;

public enum ScanMediaKind
{
    Video,
    Image,
    Gallery,
    Audio,
    Text,
}

public enum ScanFileValidationStatus
{
    Ready,
    Deferred,
    Invalid,
    Failed,
}

public sealed record ScanFileValidationResult(
    ScanFileValidationStatus Status,
    string? Reason,
    string? ProbeJson,
    IReadOnlyList<ZipEntryInfo>? GalleryEntries)
{
    public int? Width { get; init; }
    public int? Height { get; init; }

    public static ScanFileValidationResult Ready(string? probeJson = null, IReadOnlyList<ZipEntryInfo>? galleryEntries = null)
        => new(ScanFileValidationStatus.Ready, null, probeJson, galleryEntries);
    public static ScanFileValidationResult Deferred(string reason)
        => new(ScanFileValidationStatus.Deferred, reason, null, null);
    public static ScanFileValidationResult Invalid(string reason)
        => new(ScanFileValidationStatus.Invalid, reason, null, null);
    public static ScanFileValidationResult Failed(string reason)
        => new(ScanFileValidationStatus.Failed, reason, null, null);
}

public interface IScanFileValidator
{
    Task<ScanFileValidationResult> ValidateAsync(
        string path,
        long discoveredSize,
        DateTime discoveredModTime,
        ScanMediaKind kind,
        CancellationToken ct = default);
}

/// <summary>
/// Performs bounded, scan-safe validation. It deliberately avoids full-payload reads: readiness comes
/// from a quiet period and before/after stats, with cheap format headers/tails strengthening the result.
/// </summary>
public sealed class ScanFileValidator(
    IMediaProbeService mediaProbeService,
    ZipGalleryReader zipGalleryReader,
    TimeProvider timeProvider) : IScanFileValidator
{
    private const int MaxIsoBmffTopLevelBoxes = 4_096;
    private const int MaxAsfHeaderBytes = 64 * 1024;
    private const int MaxAsfHeaderObjects = 4_096;
    private const int MaxEbmlHeaderBytes = 64 * 1024;
    private const int MaxSvgBoundaryBytes = 64 * 1024;
    public static readonly TimeSpan FileQuietPeriod = TimeSpan.FromSeconds(2);

    public async Task<ScanFileValidationResult> ValidateAsync(
        string path,
        long discoveredSize,
        DateTime discoveredModTime,
        ScanMediaKind kind,
        CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (!IsFileQuiet(discoveredModTime, now))
            return ScanFileValidationResult.Deferred("the file has not been unchanged for the quiet period");

        // Empty plain-text files are valid documents. Empty binary media is overwhelmingly a copy or
        // download that has only just created its destination, so defer it instead of creating a broken row.
        if (discoveredSize == 0 && kind != ScanMediaKind.Text)
            return ScanFileValidationResult.Deferred("the file is empty");

        ScanFileValidationResult validation;
        try
        {
            validation = kind switch
            {
                ScanMediaKind.Video => await ValidateProbedMediaAsync(path, "video", requireDimensions: true, ct),
                ScanMediaKind.Audio => await ValidateProbedMediaAsync(path, "audio", requireDimensions: false, ct),
                ScanMediaKind.Image => await ValidateImageFileAsync(path, ct),
                ScanMediaKind.Gallery => await ValidateGalleryFileAsync(path, ct),
                ScanMediaKind.Text => await ValidateTextFileAsync(path, ct),
                _ => ScanFileValidationResult.Failed("unsupported media type"),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException
            or SixLabors.ImageSharp.InvalidImageContentException
            or SixLabors.ImageSharp.UnknownImageFormatException
            or XmlException
            or JsonException)
        {
            return ScanFileValidationResult.Invalid(ex.Message);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ScanFileValidationResult.Deferred("the file disappeared during validation");
        }
        catch (UnauthorizedAccessException ex) when (FileReadRace.IsWindowsDeletionRace(ex, path))
        {
            return ScanFileValidationResult.Deferred("the file disappeared during validation");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ScanFileValidationResult.Failed(ex.Message);
        }

        if (validation.Status != ScanFileValidationStatus.Ready)
            return validation;

        try
        {
            var currentStat = GetFileStat(path);
            if (DidFileChangeDuringValidation(discoveredSize, discoveredModTime, currentStat.Size, currentStat.ModTime))
                return ScanFileValidationResult.Deferred("the file changed while it was being validated");
            if (!IsFileQuiet(currentStat.ModTime, timeProvider.GetUtcNow().UtcDateTime))
                return ScanFileValidationResult.Deferred("the file changed within the quiet period");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ScanFileValidationResult.Deferred("the file disappeared during validation");
        }
        catch (UnauthorizedAccessException ex) when (FileReadRace.IsWindowsDeletionRace(ex, path))
        {
            return ScanFileValidationResult.Deferred("the file disappeared during validation");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ScanFileValidationResult.Failed(ex.Message);
        }

        return validation;
    }

    public static bool DidFileChangeDuringValidation(
        long discoveredSize,
        DateTime discoveredModTime,
        long currentSize,
        DateTime currentModTime)
    {
        return discoveredSize != currentSize || discoveredModTime != currentModTime;
    }

    public static bool IsFileQuiet(DateTime observedModTime, DateTime utcNow)
    {
        var observedUtc = observedModTime.Kind == DateTimeKind.Utc ? observedModTime : observedModTime.ToUniversalTime();
        var nowUtc = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
        // Network shares can report timestamps from a server whose clock is ahead. Such files must not
        // be deferred forever; the before/after stat and format checks remain active for them.
        if (observedUtc > nowUtc)
            return true;
        return nowUtc - observedUtc >= FileQuietPeriod;
    }

    private async Task<ScanFileValidationResult> ValidateProbedMediaAsync(
        string path,
        string streamType,
        bool requireDimensions,
        CancellationToken ct)
    {
        var declaredLengthFailure = await ValidateDeclaredContainerLengthAsync(path, ct);
        if (declaredLengthFailure != null)
            return ScanFileValidationResult.Invalid(declaredLengthFailure);

        var probe = await mediaProbeService.ProbeAsync(path, ct);
        switch (probe.Status)
        {
            case MediaProbeStatus.Invalid:
                return ScanFileValidationResult.Invalid(probe.Reason ?? "FFprobe rejected the file");
            case MediaProbeStatus.Unavailable:
            case MediaProbeStatus.TimedOut:
            case MediaProbeStatus.Failed:
                return ScanFileValidationResult.Failed(probe.Reason ?? "FFprobe failed");
            case MediaProbeStatus.Success when string.IsNullOrWhiteSpace(probe.Json):
                return ScanFileValidationResult.Failed("FFprobe returned no metadata");
        }

        if (!TryGetUsableMediaStream(probe.Json!, streamType, requireDimensions, out var width, out var height))
            return ScanFileValidationResult.Invalid($"FFprobe did not report a usable {streamType} stream");

        return ScanFileValidationResult.Ready(probeJson: probe.Json) with
        {
            Width = width,
            Height = height,
        };
    }

    private static bool TryGetUsableMediaStream(
        string json,
        string streamType,
        bool requireDimensions,
        out int? widthValue,
        out int? heightValue)
    {
        widthValue = null;
        heightValue = null;
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("streams", out var streams)
            || streams.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var stream in streams.EnumerateArray())
        {
            if (!stream.TryGetProperty("codec_type", out var codecType)
                || !string.Equals(codecType.GetString(), streamType, StringComparison.Ordinal))
                continue;

            if (!requireDimensions)
                return true;

            var parsedWidth = 0;
            var parsedHeight = 0;
            var hasWidth = stream.TryGetProperty("width", out var width)
                && width.TryGetInt32(out parsedWidth)
                && parsedWidth > 0;
            var hasHeight = stream.TryGetProperty("height", out var height)
                && height.TryGetInt32(out parsedHeight)
                && parsedHeight > 0;
            if (!hasWidth || !hasHeight)
                continue;

            widthValue = hasWidth ? parsedWidth : null;
            heightValue = hasHeight ? parsedHeight : null;
            return true;
        }

        return false;
    }

    private async Task<ScanFileValidationResult> ValidateImageFileAsync(string path, CancellationToken ct)
    {
        if (string.Equals(Path.GetExtension(path), ".avif", StringComparison.OrdinalIgnoreCase))
            return await ValidateProbedMediaAsync(path, "video", requireDimensions: true, ct);

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
        if (string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase))
        {
            await ValidateSvgStreamAsync(stream, ct);
            await ValidateImageContainerTailAsync(stream, Path.GetExtension(path), ct);
            return ScanFileValidationResult.Ready();
        }

        var (width, height) = await ValidateImageStreamAsync(stream, ct);
        await ValidateImageContainerTailAsync(stream, Path.GetExtension(path), ct);
        return ScanFileValidationResult.Ready() with
        {
            Width = width,
            Height = height,
        };
    }

    private async Task<ScanFileValidationResult> ValidateGalleryFileAsync(string path, CancellationToken ct)
    {
        var entries = await zipGalleryReader.GetImageEntriesAsync(path, ct);
        if (entries.Count == 0)
            return ScanFileValidationResult.Invalid("the gallery archive contains no readable images");

        // Reading the central directory is the same work gallery import already needs. Reuse this list
        // rather than extracting every payload during scanning, which can be prohibitively expensive.
        return ScanFileValidationResult.Ready(galleryEntries: entries);
    }

    private static async Task<(int Width, int Height)> ValidateImageStreamAsync(Stream stream, CancellationToken ct)
    {
        var image = await SixLabors.ImageSharp.Image.IdentifyAsync(stream, ct);
        if (image == null || image.Width <= 0 || image.Height <= 0)
            throw new InvalidDataException("the image has invalid dimensions");
        return (image.Width, image.Height);
    }

    private static async Task ValidateSvgStreamAsync(FileStream stream, CancellationToken ct)
    {
        var headerLength = (int)Math.Min(stream.Length, MaxSvgBoundaryBytes);
        if (headerLength == 0)
            throw new InvalidDataException("the SVG is empty");

        var header = new byte[headerLength];
        stream.Position = 0;
        await stream.ReadExactlyAsync(header, ct);

        string? rootName = null;
        var emptyRoot = false;
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            CloseInput = false,
        };
        await using (var headerStream = new MemoryStream(header, writable: false))
        using (var reader = XmlReader.Create(headerStream, settings))
        {
            while (await reader.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                if (!string.Equals(reader.LocalName, "svg", StringComparison.Ordinal))
                    throw new InvalidDataException("the SVG does not have an svg root element");

                rootName = reader.Name;
                emptyRoot = reader.IsEmptyElement;
                break;
            }
        }

        if (rootName == null)
            throw new InvalidDataException($"the SVG root element was not found within the first {MaxSvgBoundaryBytes:N0} bytes");
        if (emptyRoot)
            return;

        var encoding = DetectSvgEncoding(header);
        var alignment = encoding is System.Text.UnicodeEncoding ? 2 : 1;
        var tailStart = Math.Max(0, stream.Length - MaxSvgBoundaryBytes);
        if (alignment > 1 && tailStart % alignment != 0)
            tailStart--;
        var tail = new byte[checked((int)(stream.Length - tailStart))];
        stream.Position = tailStart;
        await stream.ReadExactlyAsync(tail, ct);

        if (!encoding.GetString(tail).Contains($"</{rootName}", StringComparison.Ordinal))
            throw new InvalidDataException("the SVG has no closing root element near EOF");
    }

    private static System.Text.Encoding DetectSvgEncoding(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 2)
        {
            if (header[0] == 0xFF && header[1] == 0xFE)
                return System.Text.Encoding.Unicode;
            if (header[0] == 0xFE && header[1] == 0xFF)
                return System.Text.Encoding.BigEndianUnicode;
        }
        if (header.Length >= 4)
        {
            if (header[0] == 0x3C && header[1] == 0x00 && header[2] == 0x3F && header[3] == 0x00)
                return System.Text.Encoding.Unicode;
            if (header[0] == 0x00 && header[1] == 0x3C && header[2] == 0x00 && header[3] == 0x3F)
                return System.Text.Encoding.BigEndianUnicode;
        }

        return System.Text.Encoding.UTF8;
    }

    private static async Task ValidateImageContainerTailAsync(FileStream stream, string extension, CancellationToken ct)
    {
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            if (stream.Length < 12)
                throw new InvalidDataException("the PNG is shorter than its end marker");
            var tail = new byte[12];
            stream.Position = stream.Length - tail.Length;
            await stream.ReadExactlyAsync(tail, ct);
            if (BinaryPrimitives.ReadUInt32BigEndian(tail.AsSpan(0, 4)) != 0
                || !tail.AsSpan(4, 4).SequenceEqual("IEND"u8))
                throw new InvalidDataException("the PNG has no final IEND chunk");
        }
        else if (extension.Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            if (stream.Length == 0)
                throw new InvalidDataException("the GIF is empty");
            stream.Position = stream.Length - 1;
            var trailer = new byte[1];
            await stream.ReadExactlyAsync(trailer, ct);
            if (trailer[0] != 0x3B)
                throw new InvalidDataException("the GIF has no trailer");
        }
        else if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            // JPEG permits trailing application data in practice, so search a bounded tail rather than
            // requiring EOI to be the final two bytes.
            var tailLength = (int)Math.Min(stream.Length, 64 * 1024);
            var tail = new byte[tailLength];
            stream.Position = stream.Length - tailLength;
            await stream.ReadExactlyAsync(tail, ct);
            var foundEnd = false;
            for (var index = tail.Length - 2; index >= 0; index--)
            {
                if (tail[index] == 0xFF && tail[index + 1] == 0xD9)
                {
                    foundEnd = true;
                    break;
                }
            }
            if (!foundEnd)
                throw new InvalidDataException("the JPEG has no end-of-image marker near EOF");
        }
        else if (extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            stream.Position = 0;
            if (stream.Length < 12)
                throw new InvalidDataException("the WebP is shorter than its RIFF header");
            var header = new byte[12];
            await stream.ReadExactlyAsync(header, ct);
            var declaredPayloadSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
            if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                || !header.AsSpan(8, 4).SequenceEqual("WEBP"u8)
                || (long)declaredPayloadSize + 8 > stream.Length)
                throw new InvalidDataException("the WebP is shorter than its declared RIFF size");
        }
    }

    private static async Task<ScanFileValidationResult> ValidateTextFileAsync(string path, CancellationToken ct)
    {
        // Text metadata extraction happens in ProcessTextFileAsync. Only perform cheap tail/index checks
        // here so large PDFs and EPUBs are not parsed twice.
        var extension = Path.GetExtension(path);
        if (extension.Equals(".epub", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count == 0)
                return ScanFileValidationResult.Invalid("the EPUB archive is empty");
        }
        else if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            var tailLength = (int)Math.Min(stream.Length, 4096);
            var tail = new byte[tailLength];
            stream.Position = stream.Length - tailLength;
            await stream.ReadExactlyAsync(tail, ct);
            if (!System.Text.Encoding.ASCII.GetString(tail).Contains("%%EOF", StringComparison.Ordinal))
                return ScanFileValidationResult.Invalid("the PDF has no end-of-file marker");
        }

        return ScanFileValidationResult.Ready();
    }

    /// <summary>
    /// Cheap container-specific truncation checks. They complement ffprobe's metadata read: formats
    /// such as MP4 and RIFF frequently declare byte ranges that must exist even when their headers are
    /// readable at the start of an in-progress copy.
    /// </summary>
    public static async Task<string?> ValidateDeclaredContainerLengthAsync(string path, CancellationToken ct)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".f4v", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4b", StringComparison.OrdinalIgnoreCase))
        {
            return await ValidateIsoBmffLengthAsync(path, ct);
        }

        if (extension.Equals(".avi", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            return await ValidateRiffLengthAsync(path, ct);
        }

        if (extension.Equals(".wmv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".asf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".wma", StringComparison.OrdinalIgnoreCase))
        {
            return await ValidateAsfLengthAsync(path, ct);
        }

        if (extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mka", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".weba", StringComparison.OrdinalIgnoreCase))
        {
            return await ValidateMatroskaLengthAsync(path, ct);
        }

        return null;
    }

    private static async Task<string?> ValidateIsoBmffLengthAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        var length = stream.Length;
        var header = new byte[16];
        long offset = 0;
        var boxes = 0;

        while (offset < length)
        {
            ct.ThrowIfCancellationRequested();
            if (boxes >= MaxIsoBmffTopLevelBoxes)
                return $"the ISO media container has more than {MaxIsoBmffTopLevelBoxes:N0} top-level boxes";
            if (length - offset < 8)
                return "the ISO media container ends in a partial box header";

            stream.Position = offset;
            await stream.ReadExactlyAsync(header.AsMemory(0, 8), ct);
            var size32 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            long boxSize;
            var headerSize = 8;
            if (size32 == 1)
            {
                if (length - offset < 16)
                    return "the ISO media container ends in a partial extended box header";
                await stream.ReadExactlyAsync(header.AsMemory(8, 8), ct);
                var size64 = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
                if (size64 > long.MaxValue)
                    return "the ISO media container declares an unsupported box size";
                boxSize = (long)size64;
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                boxSize = length - offset;
            }
            else
            {
                boxSize = size32;
            }

            if (boxSize < headerSize)
                return "the ISO media container declares an invalid box size";
            if (boxSize > length - offset)
                return "the ISO media container is shorter than a declared box";

            offset += boxSize;
            boxes++;
        }

        return boxes == 0 ? "the ISO media container has no boxes" : null;
    }

    private static async Task<string?> ValidateRiffLengthAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        if (stream.Length < 12)
            return "the RIFF container is shorter than its header";

        var header = new byte[12];
        await stream.ReadExactlyAsync(header, ct);
        var signature = System.Text.Encoding.ASCII.GetString(header, 0, 4);
        if (signature is not ("RIFF" or "RF64"))
            return "the file does not contain a RIFF header";

        var declaredPayloadSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        if (signature == "RIFF" && (long)declaredPayloadSize + 8 > stream.Length)
            return "the RIFF container is shorter than its declared size";

        return null;
    }

    private static async Task<string?> ValidateAsfLengthAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        if (stream.Length < 30)
            return null;

        var prefix = new byte[30];
        await stream.ReadExactlyAsync(prefix, ct);
        ReadOnlySpan<byte> headerObjectId = [0x30, 0x26, 0xb2, 0x75, 0x8e, 0x66, 0xcf, 0x11, 0xa6, 0xd9, 0x00, 0xaa, 0x00, 0x62, 0xce, 0x6c];
        if (!prefix.AsSpan(0, 16).SequenceEqual(headerObjectId))
            return null;

        var declaredHeaderSize = BinaryPrimitives.ReadUInt64LittleEndian(prefix.AsSpan(16, 8));
        if (declaredHeaderSize < 30)
            return "the ASF container declares an invalid header size";
        if (declaredHeaderSize > (ulong)stream.Length)
            return "the ASF container is shorter than its declared header";

        var objectCount = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(24, 4));
        var bytesToRead = (int)Math.Min((ulong)MaxAsfHeaderBytes, declaredHeaderSize);
        var header = new byte[bytesToRead];
        prefix.CopyTo(header, 0);
        if (bytesToRead > prefix.Length)
            await stream.ReadExactlyAsync(header.AsMemory(prefix.Length), ct);

        ReadOnlySpan<byte> filePropertiesObjectId = [0xa1, 0xdc, 0xab, 0x8c, 0x47, 0xa9, 0xcf, 0x11, 0x8e, 0xe4, 0x00, 0xc0, 0x0c, 0x20, 0x53, 0x65];
        long offset = 30;
        var objectsToInspect = Math.Min(objectCount, (uint)MaxAsfHeaderObjects);
        for (var index = 0u; index < objectsToInspect && offset + 24 <= header.Length; index++)
        {
            var objectSize = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan((int)offset + 16, 8));
            if (objectSize < 24)
                return "the ASF container declares an invalid header object size";
            if (objectSize > declaredHeaderSize - (ulong)offset)
                return "the ASF header object exceeds the declared header size";

            if (header.AsSpan((int)offset, 16).SequenceEqual(filePropertiesObjectId))
            {
                if (objectSize < 104)
                    return "the ASF file properties object is truncated";
                if (offset + 48 > header.Length)
                    return declaredHeaderSize <= MaxAsfHeaderBytes
                        ? "the ASF file properties object is truncated"
                        : null;

                var declaredFileSize = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan((int)offset + 40, 8));
                if (declaredFileSize > (ulong)stream.Length)
                    return "the ASF container is shorter than the size declared by its file properties";
                return null;
            }

            if (objectSize > (ulong)(header.Length - offset))
                return null;
            offset += (long)objectSize;
        }

        return null;
    }

    private static async Task<string?> ValidateMatroskaLengthAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        var bytesToRead = (int)Math.Min(stream.Length, MaxEbmlHeaderBytes);
        if (bytesToRead < 5)
            return null;

        var header = new byte[bytesToRead];
        await stream.ReadExactlyAsync(header, ct);
        ReadOnlySpan<byte> ebmlHeaderId = [0x1a, 0x45, 0xdf, 0xa3];
        if (!header.AsSpan(0, 4).SequenceEqual(ebmlHeaderId)
            || !TryReadEbmlSize(header.AsSpan(4), out var ebmlHeaderSize, out var ebmlSizeLength, out var ebmlSizeUnknown)
            || ebmlSizeUnknown)
        {
            return null;
        }

        var segmentOffset = 4L + ebmlSizeLength + (long)ebmlHeaderSize;
        if (segmentOffset < 0 || segmentOffset + 5 > header.Length)
            return null;

        ReadOnlySpan<byte> segmentId = [0x18, 0x53, 0x80, 0x67];
        if (!header.AsSpan((int)segmentOffset, 4).SequenceEqual(segmentId)
            || !TryReadEbmlSize(header.AsSpan((int)segmentOffset + 4), out var segmentSize, out var segmentSizeLength, out var segmentSizeUnknown)
            || segmentSizeUnknown)
        {
            return null;
        }

        var segmentDataOffset = segmentOffset + 4 + segmentSizeLength;
        if (segmentSize > (ulong)(stream.Length - segmentDataOffset))
            return "the Matroska container is shorter than its declared segment";

        return null;
    }

    private static bool TryReadEbmlSize(
        ReadOnlySpan<byte> bytes,
        out ulong value,
        out int encodedLength,
        out bool isUnknown)
    {
        value = 0;
        encodedLength = 0;
        isUnknown = false;
        if (bytes.IsEmpty || bytes[0] == 0)
            return false;

        var marker = 0x80;
        encodedLength = 1;
        while ((bytes[0] & marker) == 0)
        {
            marker >>= 1;
            encodedLength++;
        }

        if (encodedLength > 8 || bytes.Length < encodedLength)
            return false;

        value = (ulong)(bytes[0] & (marker - 1));
        for (var index = 1; index < encodedLength; index++)
            value = (value << 8) | bytes[index];

        var unknownValue = (1UL << (encodedLength * 7)) - 1;
        isUnknown = value == unknownValue;
        return true;
    }

    private static ObservedFileStat GetFileStat(string path)
    {
        var fileInfo = new FileInfo(path);
        return new ObservedFileStat(fileInfo.Length, fileInfo.LastWriteTimeUtc);
    }

    private readonly record struct ObservedFileStat(long Size, DateTime ModTime);
}
