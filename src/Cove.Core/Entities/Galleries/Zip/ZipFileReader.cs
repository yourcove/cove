using System.IO.Compression;
using System.Text;

namespace Cove.Core.Entities.Galleries.Zip;

/// <summary>
/// Default implementation of IZipFileReader using .NET's System.IO.Compression.
/// Handles reading zip archives and extracting image files for gallery support.
/// </summary>
public class ZipFileReader : IZipFileReader
{
    // Supported image file extensions (case-insensitive)
    // Based on Cove's supported formats: JPEG, PNG, GIF, WebP, BMP
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg"
    };

    // A single-byte, lossless code page (ISO-8859-1) used as the ZipArchive entryNameEncoding. .NET only
    // applies entryNameEncoding to entries WITHOUT the UTF-8 language-encoding flag; UTF-8-flagged entries
    // are always decoded as UTF-8. So for modern archives this is a no-op, and for legacy archives it maps
    // each raw name byte 1:1 to a char (0-255), letting us recover and re-decode the original bytes below
    // instead of getting U+FFFD mojibake from a mistaken UTF-8 decode.
    private static readonly Encoding RawByteEncoding = Encoding.Latin1;

    // Preferred legacy (DBCS) code page to try when an entry name is not valid UTF-8. Defaults to 949
    // (Korean UHC) — the reported failing case. Configurable so libraries dominated by another legacy
    // encoding (932 Shift-JIS, 936 GBK, 950 Big5, 1251 Cyrillic, …) can override it.
    private readonly int _legacyCodePage;

    static ZipFileReader()
    {
        // Required for Encoding.GetEncoding(949) etc. on .NET Core / non-Windows, where DBCS code pages
        // are not registered by default. Idempotent, so registering per-process at type init is safe.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public ZipFileReader(int legacyCodePage = 949)
    {
        _legacyCodePage = legacyCodePage;
    }

    /// <inheritdoc/>
    public async Task<List<ZipEntryInfo>> ListEntriesAsync(string zipFilePath, CancellationToken ct = default)
    {
        // Validate that the zip file exists before attempting to open
        if (!File.Exists(zipFilePath))
            throw new FileNotFoundException($"Zip file not found: {zipFilePath}");

        // Open the zip file in read mode
        // Using FileMode.Open ensures we only read existing files
        // FileAccess.Read prevents any accidental modifications
        // FileShare.Read allows other processes to read the file concurrently
        await using var fileStream = new FileStream(
            zipFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );

        // Create a ZipArchive from the file stream
        // ZipArchiveMode.Read is efficient for read-only operations
        // leaveOpen: false ensures the stream is disposed when ZipArchive is disposed
        // entryNameEncoding: RawByteEncoding preserves raw bytes for legacy (non-UTF-8-flagged) names so
        // FixEntryEncoding can recover them; UTF-8-flagged names are still decoded as UTF-8 by the runtime.
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding: RawByteEncoding);

        var entries = new List<ZipEntryInfo>();

        // Iterate through all entries in the zip archive
        foreach (var entry in archive.Entries)
        {
            // Check for cancellation before processing each entry
            ct.ThrowIfCancellationRequested();

            // Skip directory entries (they have no content, only metadata)
            // Directory entries typically end with '/' and have 0 length
            if (entry.FullName.EndsWith('/') || entry.Length == 0)
                continue;

            // Create metadata record for this entry
            var entryInfo = new ZipEntryInfo(
                FullName: entry.FullName,
                Name: entry.Name,
                Length: entry.Length,
                CompressedLength: entry.CompressedLength,
                LastWriteTime: entry.LastWriteTime
            );

            // Attempt to fix encoding issues in filenames
            // Many zip tools use non-UTF8 encodings (CP437, Shift-JIS, etc.)
            entryInfo = FixEntryEncoding(entryInfo);

            entries.Add(entryInfo);
        }

        return entries;
    }

    /// <inheritdoc/>
    public async Task<Stream> ExtractEntryAsync(string zipFilePath, string entryPath, CancellationToken ct = default)
    {
        // Validate zip file exists
        if (!File.Exists(zipFilePath))
            throw new FileNotFoundException($"Zip file not found: {zipFilePath}");

        // Open zip archive for reading
        await using var fileStream = new FileStream(
            zipFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );

        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding: RawByteEncoding);

        // Find the requested entry by its full path. entryPath is the DECODED name we stored at scan time,
        // but the archive's raw entry names are byte-passthrough (see RawByteEncoding), so a direct
        // GetEntry(entryPath) would miss legacy-encoded entries. Decode each entry name the same way we did
        // at scan and match on the result, so Korean/CP949 (and other legacy) names resolve correctly.
        var entry = archive.Entries.FirstOrDefault(e => string.Equals(DecodeEntryName(e.FullName), entryPath, StringComparison.Ordinal))
            ?? archive.GetEntry(entryPath);
        if (entry == null)
            throw new FileNotFoundException($"Entry '{entryPath}' not found in zip archive");

        // Create a memory stream to hold the extracted data
        // We can't return the entry.Open() stream directly because it depends on
        // the ZipArchive remaining open, but we're disposing it at the end of this method.
        // So we copy the data to a MemoryStream that the caller can use independently.
        var memoryStream = new MemoryStream();

        // Open the entry's data stream and copy to memory
        await using (var entryStream = entry.Open())
        {
            await entryStream.CopyToAsync(memoryStream, ct);
        }

        // Reset the memory stream position to the beginning so the caller can read it
        memoryStream.Position = 0;

        return memoryStream;
    }

    /// <inheritdoc/>
    public List<ZipEntryInfo> FilterImageEntries(List<ZipEntryInfo> entries)
    {
        // Filter entries to only include files with image extensions
        // This prevents processing non-image files (txt, nfo, metadata files, etc.)
        return entries
            .Where(e => IsImageFile(e.Name))
            .ToList();
    }

    /// <inheritdoc/>
    public ZipEntryInfo FixEntryEncoding(ZipEntryInfo entry)
    {
        var fixedFullName = DecodeEntryName(entry.FullName);
        if (ReferenceEquals(fixedFullName, entry.FullName))
            return entry;

        // Recompute Name from the corrected FullName so lookups and extension checks stay consistent.
        var slash = fixedFullName.LastIndexOf('/');
        var fixedName = slash >= 0 ? fixedFullName[(slash + 1)..] : fixedFullName;
        return entry with { FullName = fixedFullName, Name = fixedName };
    }

    /// <summary>
    /// Recovers a zip entry name that was read with the raw-byte passthrough encoding. UTF-8-flagged
    /// entries arrive already-correct and are returned unchanged; legacy (non-UTF-8-flagged) entries
    /// arrive as raw bytes mapped 1:1 to chars 0-255, which we re-decode as UTF-8 first (handles archives
    /// that wrote UTF-8 without setting the flag) and then as the configured legacy DBCS code page.
    /// </summary>
    private string DecodeEntryName(string rawName)
    {
        var anyHigh = false;
        foreach (var c in rawName)
        {
            // A char above the single-byte range can only have come from the runtime's UTF-8 decode of a
            // properly-flagged entry — it's already correct, leave it alone.
            if (c > 0xFF)
                return rawName;
            if (c > 0x7F)
                anyHigh = true;
        }

        // Pure ASCII names are identical under every encoding — nothing to fix.
        if (!anyHigh)
            return rawName;

        // All chars are 0-255 with at least one > 127: these are the original raw name bytes.
        var raw = RawByteEncoding.GetBytes(rawName);

        // Prefer a strict UTF-8 decode: unambiguous, and covers archives that stored UTF-8 bytes without
        // setting the language-encoding flag.
        if (TryDecodeStrict(raw, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), out var utf8Name))
            return utf8Name;

        // Otherwise fall back to the configured legacy code page (default CP949 / Korean).
        try
        {
            var decoded = Encoding.GetEncoding(_legacyCodePage).GetString(raw);
            if (!string.IsNullOrEmpty(decoded))
                return decoded;
        }
        catch (ArgumentException)
        {
            // Unknown/unregistered code page — fall through and keep the raw name.
        }

        return rawName;
    }

    private static bool TryDecodeStrict(byte[] bytes, Encoding strict, out string result)
    {
        try
        {
            result = strict.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            result = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Checks if a filename has a supported image extension.
    /// </summary>
    /// <param name="filename">Name of the file to check</param>
    /// <returns>True if the file is a recognized image format</returns>
    private static bool IsImageFile(string filename)
    {
        var extension = Path.GetExtension(filename);
        return !string.IsNullOrEmpty(extension) && ImageExtensions.Contains(extension);
    }
}
