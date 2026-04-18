using Cove.Core.Interfaces;

namespace Cove.Api.Services;

public class BlobService(CoveConfiguration config, ILogger<BlobService> logger) : IBlobService
{
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

    public async Task<string> StoreBlobAsync(Stream data, string contentType, CancellationToken ct = default)
    {
        var blobId = Guid.NewGuid().ToString();
        var extension = GetExtension(contentType);
        var path = GetBlobPath(blobId, extension);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await data.CopyToAsync(fs, ct);

        logger.LogDebug("Stored blob {BlobId} at {Path}", blobId, path);
        return blobId;
    }

    public Task<(Stream Stream, string ContentType)?> GetBlobAsync(string blobId, CancellationToken ct = default)
    {
        var (path, contentType) = ResolveBlobFile(blobId);
        if (path == null || contentType == null)
            return Task.FromResult<(Stream Stream, string ContentType)?>(null);

        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult<(Stream Stream, string ContentType)?>((fs, contentType));
    }

    public Task DeleteBlobAsync(string blobId, CancellationToken ct = default)
    {
        var (path, _) = ResolveBlobFile(blobId);
        if (path != null)
        {
            File.Delete(path);
            logger.LogDebug("Deleted blob {BlobId} at {Path}", blobId, path);
        }

        return Task.CompletedTask;
    }

    private string GetBlobPath(string blobId, string extension)
    {
        var bucket = blobId[..2];
        return Path.Combine(BlobDir, bucket, $"{blobId}{extension}");
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
        return $".{subtype}";
    }
}
