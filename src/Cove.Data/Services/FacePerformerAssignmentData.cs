using System.Globalization;

namespace Cove.Data.Services;

/// <summary>
/// Describes the Cove-owned extension-data rows used to remember which performer links were created
/// by face propagation. These rows are part of Cove's performer-reference contract even though they
/// live in the generic extension-data store.
/// </summary>
internal static class FacePerformerAssignmentData
{
    public const string ExtensionId = "cove.face-performer-propagation";
    public const string KeyPrefix = "performer-assignment:";

    public static string BuildKey(Assignment assignment)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{KeyPrefix}{assignment.FaceId}:{assignment.PerformerId}:{assignment.HostType}:{assignment.HostId}");

    public static bool TryParseKey(string key, out Assignment assignment)
    {
        assignment = default;
        if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal))
            return false;

        var parts = key[KeyPrefix.Length..].Split(':');
        if (parts.Length != 4
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var faceId)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var performerId)
            || !parts[2].Equals("video", StringComparison.OrdinalIgnoreCase)
                && !parts[2].Equals("image", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hostId))
            return false;

        assignment = new Assignment(faceId, performerId, parts[2].ToLowerInvariant(), hostId);
        return true;
    }

    internal readonly record struct Assignment(int FaceId, int PerformerId, string HostType, int HostId);
}
