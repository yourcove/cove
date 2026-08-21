using System.Net.Http.Headers;
using System.Text.Json;
using Cove.Core.Interfaces;

namespace Cove.Api.Services;

public partial class BlobService(
    CoveConfiguration config,
    ILogger<BlobService> logger,
    IBlobReferenceCounter referenceCounter,
    IBlobReferenceCoordinator referenceCoordinator) : IBlobService
{
    private const string MetadataSuffix = ".metadata.json";

    private static readonly Dictionary<string, string> ContentTypeToExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
        ["image/gif"] = ".gif",
        ["image/svg+xml"] = ".svg",
        ["image/avif"] = ".avif",
        ["image/bmp"] = ".bmp",
        ["image/jxl"] = ".jxl",
        ["image/heic"] = ".heic",
    };

    private static readonly Dictionary<string, string> ExtensionToContentType = ContentTypeToExtension
        .ToDictionary(kvp => kvp.Value, kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);

    private string BlobDir => Path.Combine(config.GeneratedPath, "blobs");
    internal IBlobReferenceCounter ReferenceCounter { get; set; } = referenceCounter;

    [LoggerMessage(EventId = 2601, Level = LogLevel.Trace,
        Message = "Stored blob {BlobId} at {Path}")]
    private partial void TraceBlobStored(string blobId, string path);

    [LoggerMessage(EventId = 2602, Level = LogLevel.Trace,
        Message = "Deleted blob {BlobId} at {Path}")]
    private partial void TraceBlobDeleted(string blobId, string path);

    public async Task<string> StoreBlobAsync(Stream data, string contentType, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var normalizedContentType = NormalizeContentType(contentType);
        var blobId = Guid.NewGuid().ToString();
        var extension = GetExtension(normalizedContentType);
        var path = GetBlobPath(blobId, extension);
        var metadataPath = GetMetadataPath(blobId);
        var temporaryPrefix = $".tmp-{Guid.NewGuid():N}";
        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $"{temporaryPrefix}.payload{extension}");
        var temporaryMetadataPath = Path.Combine(Path.GetDirectoryName(path)!, $"{temporaryPrefix}{MetadataSuffix}");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            ct.ThrowIfCancellationRequested();

            await using (var fs = new FileStream(
                temporaryPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous,
                }))
            {
                await data.CopyToAsync(fs, ct);
                await fs.FlushAsync(ct);
            }

            var metadata = JsonSerializer.Serialize(new BlobMetadata(normalizedContentType));
            await File.WriteAllTextAsync(temporaryMetadataPath, metadata, ct);

            File.Move(temporaryMetadataPath, metadataPath);
            // Publish the payload last so a discoverable blob always has its MIME metadata.
            File.Move(temporaryPath, path);
        }
        catch
        {
            DeleteIfExists(temporaryPath);
            DeleteIfExists(temporaryMetadataPath);
            DeleteIfExists(path);
            DeleteIfExists(metadataPath);
            throw;
        }

        referenceCoordinator.MarkAvailable(blobId);
        TraceBlobStored(blobId, path);
        return blobId;
    }

    public async Task<(Stream Stream, string ContentType)?> GetBlobAsync(string blobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsCanonicalBlobId(blobId))
            return null;

        var (path, contentType) = ResolveBlobFile(blobId);
        if (path == null || contentType == null)
            return null;

        // Capture MIME metadata before opening the payload. If deletion wins after this read,
        // opening returns null; if opening wins, the returned stream keeps this content type.
        var storedContentType = await ReadStoredContentTypeAsync(blobId, ct);

        try
        {
            var fs = new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read | FileShare.Delete,
                    Options = FileOptions.Asynchronous | FileOptions.RandomAccess,
                });
            return (fs, storedContentType ?? contentType);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (UnauthorizedAccessException) when (!File.Exists(path))
        {
            // Windows can answer a concurrent delete with ERROR_ACCESS_DENIED instead of
            // ERROR_FILE_NOT_FOUND: once the delete is issued the entry stops resolving, but an open
            // keeps reporting access denied until the last handle closes. That is the same "deletion
            // won" outcome as the catch above.
            //
            // Reading it as a deletion rather than as a permission problem is sound because
            // ResolveBlobFile only ever returns a path it has just seen File.Exists succeed on, so
            // the payload going missing between that check and this open is a delete. A blob that is
            // present but unreadable still throws: denying read on the file leaves File.Exists true,
            // so it never reaches this filter.
            return null;
        }
    }

    public Task DeleteBlobAsync(string blobId, CancellationToken ct = default)
        => DeleteBlobCoreAsync(blobId, ct);

    public Task DeleteBlobIfUnreferencedAsync(string blobId, CancellationToken ct = default)
        => DeleteBlobCoreAsync(blobId, ct);

    private async Task DeleteBlobCoreAsync(string blobId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsCanonicalBlobId(blobId))
            return;

        await using var referenceLease = await referenceCoordinator.AcquireAsync(ct);
        if (referenceCoordinator.WasDeleted(blobId))
            return;

        var references = await ReferenceCounter.CountReferencesAsync(blobId, maximum: 1, ct);
        if (references > 0)
        {
            logger.LogDebug(
                "Retaining blob {BlobId} because {ReferenceCount} database reference(s) remain",
                blobId,
                references);
            return;
        }

        var (path, _) = ResolveBlobFile(blobId);
        if (path != null)
        {
            File.Delete(path);
        }

        referenceCoordinator.MarkDeleted(blobId);
        DeleteIfExists(GetMetadataPath(blobId));
        if (path != null)
            TraceBlobDeleted(blobId, path);
    }

    private string GetBlobPath(string blobId, string extension)
    {
        var bucket = blobId[..2];
        return Path.Combine(BlobDir, bucket, $"{blobId}{extension}");
    }

    private string GetMetadataPath(string blobId)
    {
        var bucket = blobId[..2];
        return Path.Combine(BlobDir, bucket, $".{blobId}{MetadataSuffix}");
    }

    /// <summary>
    /// Finds the blob file on disk by checking all known extensions in the bucket directory.
    /// </summary>
    private (string? Path, string? ContentType) ResolveBlobFile(string blobId)
    {
        var bucket = blobId[..2];
        var dir = System.IO.Path.Combine(BlobDir, bucket);

        // Fast path: check known extensions
        foreach (var (ext, ct) in ExtensionToContentType)
        {
            var candidate = System.IO.Path.Combine(dir, $"{blobId}{ext}");
            if (File.Exists(candidate))
                return (candidate, ct);
        }

        // Older Cove versions may have stored blobs without a file extension.
        var extensionlessCandidate = System.IO.Path.Combine(dir, blobId);
        if (File.Exists(extensionlessCandidate))
            return (extensionlessCandidate, "application/octet-stream");

        // Fallback: scan directory for any file starting with the blobId
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, $"{blobId}.*"))
            {
                // Extract extension after the GUID (handles multi-part like ".svg+xml")
                var fileName = System.IO.Path.GetFileName(file);
                var dotIdx = fileName.IndexOf('.');
                var rawExt = dotIdx >= 0 ? fileName[dotIdx..].ToLowerInvariant() : "";

                // Try direct lookup first, then try common normalizations
                if (ExtensionToContentType.TryGetValue(rawExt, out var contentType))
                    return (file, contentType);

                // Handle malformed extensions like ".svg+xml" → try ".svg"
                var plusIdx = rawExt.IndexOf('+');
                if (plusIdx > 0)
                {
                    var normalized = rawExt[..plusIdx];
                    if (ExtensionToContentType.TryGetValue(normalized, out contentType))
                        return (file, contentType);
                }

                // Last resort: guess from extension
                return (file, rawExt switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".png" => "image/png",
                    ".webp" => "image/webp",
                    ".gif" => "image/gif",
                    ".svg" or ".svg+xml" => "image/svg+xml",
                    _ => "application/octet-stream",
                });
            }
        }

        return (null, null);
    }

    private async Task<string?> ReadStoredContentTypeAsync(string blobId, CancellationToken ct)
    {
        var metadataPath = GetMetadataPath(blobId);
        if (!File.Exists(metadataPath))
            return null;

        try
        {
            await using var metadataStream = new FileStream(
                metadataPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                });
            var metadata = await JsonSerializer.DeserializeAsync<BlobMetadata>(metadataStream, cancellationToken: ct);
            return metadata != null && TryNormalizeContentType(metadata.ContentType, out var normalizedContentType)
                ? normalizedContentType
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            logger.LogWarning(ex, "Could not read metadata for blob {BlobId}; falling back to its file extension", blobId);
            return null;
        }
    }

    private static string GetExtension(string contentType)
    {
        if (ContentTypeToExtension.TryGetValue(contentType, out var ext))
            return ext;

        // Normalize: strip parameters (e.g. "; charset=utf-8")
        var semi = contentType.IndexOf(';');
        if (semi >= 0)
        {
            var trimmed = contentType[..semi].Trim();
            if (ContentTypeToExtension.TryGetValue(trimmed, out ext))
                return ext;
            contentType = trimmed;
        }

        // Fallback: derive from subtype, stripping suffixes like "+xml"
        var slash = contentType.IndexOf('/');
        if (slash < 0) return ".bin";
        var subtype = contentType[(slash + 1)..];
        var plus = subtype.IndexOf('+');
        if (plus >= 0) subtype = subtype[..plus];
        var safeSubtype = new string(subtype
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
            .ToArray());
        return string.IsNullOrEmpty(safeSubtype) ? ".bin" : $".{safeSubtype.ToLowerInvariant()}";
    }

    private static string NormalizeContentType(string contentType)
    {
        return TryNormalizeContentType(contentType, out var normalizedContentType)
            ? normalizedContentType
            : "application/octet-stream";
    }

    private static bool TryNormalizeContentType(string? contentType, out string normalizedContentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed) || string.IsNullOrWhiteSpace(parsed.MediaType))
        {
            normalizedContentType = string.Empty;
            return false;
        }

        normalizedContentType = parsed.MediaType.ToLowerInvariant();
        return true;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static bool IsCanonicalBlobId(string? blobId)
        => Guid.TryParseExact(blobId, "D", out var parsed)
            && string.Equals(parsed.ToString("D"), blobId, StringComparison.Ordinal);

    private sealed record BlobMetadata(string ContentType);
}
