using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

/// <summary>
/// Decides whether two video files carry the same timeline, so that anything bound to one file's
/// timeline (user markers, timed group items) lands on the right frames of the other. Shared by the
/// set-as-primary flow and the merge so that both answer the question the same way.
/// </summary>
public static class VideoFileEquivalence
{
    public const int EquivalentPhashDistance = 8;
    public const double EquivalentDurationToleranceSeconds = 1;

    /// <summary>Both hashes known and close, and the running times within tolerance.</summary>
    public static bool AreEquivalent(double sourceDuration, string? sourcePhash, double targetDuration, string? targetPhash)
        => !string.IsNullOrWhiteSpace(sourcePhash) && !string.IsNullOrWhiteSpace(targetPhash)
            && MetadataServerService.ComputePhashHammingDistance(sourcePhash, targetPhash) <= EquivalentPhashDistance
            && SameLength(sourceDuration, targetDuration);

    /// <summary>
    /// <see cref="AreEquivalent"/> when both hashes are known; otherwise the running times alone decide,
    /// so a merge never throws timeline items away just because a file was never fingerprinted.
    /// </summary>
    public static bool AreEquivalentOrSameLength(double sourceDuration, string? sourcePhash, double targetDuration, string? targetPhash)
        => string.IsNullOrWhiteSpace(sourcePhash) || string.IsNullOrWhiteSpace(targetPhash)
            ? SameLength(sourceDuration, targetDuration)
            : AreEquivalent(sourceDuration, sourcePhash, targetDuration, targetPhash);

    /// <summary>
    /// The perceptual hash stored for each of the files that has one. Hashes are never computed here:
    /// decoding a file can take minutes.
    /// </summary>
    public static Task<Dictionary<int, string>> LoadStoredPhashesAsync(CoveContext db, IReadOnlyCollection<int> fileIds, CancellationToken ct)
    {
        var ids = fileIds.ToArray();
        return db.FileFingerprints
            .AsNoTracking()
            .Where(fingerprint => ids.Contains(fingerprint.FileId) && fingerprint.Type == "phash" && fingerprint.Value != "")
            .GroupBy(fingerprint => fingerprint.FileId)
            .Select(group => new { FileId = group.Key, Value = group.Min(fingerprint => fingerprint.Value) })
            .ToDictionaryAsync(row => row.FileId, row => row.Value!, ct);
    }

    private static bool SameLength(double sourceDuration, double targetDuration)
        => sourceDuration > 0 && targetDuration > 0
            && Math.Abs(sourceDuration - targetDuration) <= EquivalentDurationToleranceSeconds;
}
