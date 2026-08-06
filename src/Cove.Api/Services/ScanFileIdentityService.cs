using System.Collections.Concurrent;
using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

/// <summary>
/// Maintains byte identity for scanned files and reconciles moves, duplicates, and in-place changes.
/// </summary>
internal sealed class ScanFileIdentityService(IFingerprintService fingerprintService)
{
    /// <summary>
    /// Matches a newly observed path to an existing row by byte identity. A single missing candidate is
    /// re-pointed as a move; an on-disk candidate identifies a duplicate's parent entity.
    /// </summary>
    public async Task<(TFile? Match, bool IsMove)> MatchExistingAsync<TFile>(
        DbSet<TFile> trackedSet,
        string path,
        int folderId,
        string basename,
        FileStat stat,
        MoveDetectionIndex moveIndex,
        CancellationToken ct)
        where TFile : BaseFileEntity
    {
        var oshash = await ComputeOshashAsync(path, ct);
        if (string.IsNullOrEmpty(oshash))
            return (null, false);

        var candidates = await trackedSet
            .Where(file => file.ZipFileId == null
                && file.Fingerprints.Any(fingerprint => fingerprint.Type == "oshash" && fingerprint.Value == oshash))
            .ToListAsync(ct);

        if (candidates.Count == 0)
            return (null, false);

        var missing = candidates
            .Where(candidate => !string.IsNullOrEmpty(candidate.Path) && !File.Exists(candidate.Path))
            .ToList();

        if (missing.Count == 1)
        {
            var claim = missing[0];
            if (!moveIndex.ClaimedFilePaths.TryAdd(claim.Id, path)
                && (!moveIndex.ClaimedFilePaths.TryGetValue(claim.Id, out var claimedPath)
                    || !string.Equals(claimedPath, path, FilesystemPaths.PathComparison)))
            {
                return (claim, false);
            }

            // The claiming path may retry after a failed batch save. Re-apply the move so the fallback
            // preserves the existing row and entity identity.
            claim.ParentFolderId = folderId;
            claim.ParentFolder = null;
            claim.Basename = basename;
            claim.Size = stat.Size;
            claim.ModTime = stat.ModTime;
            return (claim, true);
        }

        var present = candidates
            .Where(candidate => !string.IsNullOrEmpty(candidate.Path) && File.Exists(candidate.Path))
            .OrderBy(candidate => candidate.Id)
            .FirstOrDefault();

        return present != null ? (present, false) : (null, false);
    }

    public async Task RefreshChangedFingerprintsAsync(
        BaseFileEntity file,
        string path,
        bool phashEnabled,
        bool md5Enabled,
        CancellationToken ct)
    {
        var oshash = await ComputeOshashAsync(path, ct);
        if (oshash != null)
            UpsertFingerprint(file, "oshash", oshash);

        if (md5Enabled)
        {
            var md5 = await fingerprintService.ComputeMd5Async(path, ct);
            if (!string.IsNullOrWhiteSpace(md5))
                UpsertFingerprint(file, "md5", md5);
            else
                BlankFingerprint(file, "md5");
        }
        else
        {
            BlankFingerprint(file, "md5");
        }

        // Perceptual hashes use media-specific pipelines in the asset-generation phase.
        if (phashEnabled)
            BlankFingerprint(file, "phash");
    }

    public static void UpsertFingerprint(BaseFileEntity file, string type, string value)
    {
        var existing = file.Fingerprints.FirstOrDefault(fingerprint =>
            string.Equals(fingerprint.Type, type, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Value = value;
            return;
        }

        file.Fingerprints.Add(new FileFingerprint
        {
            Type = type,
            Value = value,
        });
    }

    public static void BlankFingerprint(BaseFileEntity file, string type)
    {
        var existing = file.Fingerprints.FirstOrDefault(fingerprint =>
            string.Equals(fingerprint.Type, type, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            existing.Value = string.Empty;
    }

    /// <summary>
    /// Computes the OpenSubtitles hash from the file size plus its first and last 64 KiB.
    /// </summary>
    public static async Task<string?> ComputeOshashAsync(string path, CancellationToken ct)
    {
        const int chunkSize = 65_536;
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                chunkSize,
                useAsync: true);
            var fileSize = stream.Length;
            if (fileSize < chunkSize)
                return null;

            ulong hash = (ulong)fileSize;
            var buffer = new byte[chunkSize];

            await stream.ReadExactlyAsync(buffer, ct);
            for (var index = 0; index < chunkSize; index += 8)
                hash += BitConverter.ToUInt64(buffer, index);

            stream.Seek(-chunkSize, SeekOrigin.End);
            await stream.ReadExactlyAsync(buffer, ct);
            for (var index = 0; index < chunkSize; index += 8)
                hash += BitConverter.ToUInt64(buffer, index);

            return hash.ToString("x16");
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Coordinates concurrent move claims so only one discovered path re-points an existing file row.
/// </summary>
internal sealed class MoveDetectionIndex
{
    public required bool Enabled { get; init; }
    public ConcurrentDictionary<int, string> ClaimedFilePaths { get; } = new();
}
