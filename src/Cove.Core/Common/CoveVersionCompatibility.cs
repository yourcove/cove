using System.Globalization;

namespace Cove.Core.Common;

/// <summary>
/// Orders Cove host versions for extension compatibility checks.
/// </summary>
/// <remarks>
/// A development version such as <c>1.1.0-dev.12</c> identifies the twelfth commit
/// after release <c>1.1.0</c>. It therefore sorts after <c>1.1.0</c>, unlike a normal
/// SemVer prerelease, but before every later base version. Other prerelease labels keep
/// their normal SemVer position before the release with the same base version.
/// </remarks>
public readonly record struct CoveVersionCompatibility : IComparable<CoveVersionCompatibility>
{
    private const int PrereleaseStage = -1;
    private const int ReleaseStage = 0;
    private const int DevelopmentStage = 1;

    private CoveVersionCompatibility(
        int major,
        int minor,
        int patch,
        int stage,
        long developmentSequence,
        string? prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Stage = stage;
        DevelopmentSequence = developmentSequence;
        Prerelease = prerelease;
    }

    private int Major { get; }
    private int Minor { get; }
    private int Patch { get; }
    private int Stage { get; }
    private long DevelopmentSequence { get; }
    private string? Prerelease { get; }

    public static bool IsAtLeast(string current, string minimum) =>
        TryParse(current, out var currentVersion)
        && TryParse(minimum, out var minimumVersion)
        && currentVersion.CompareTo(minimumVersion) >= 0;

    public static bool TryParse(string? value, out CoveVersionCompatibility version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim();
        if (normalized.StartsWith('v'))
            normalized = normalized[1..];

        var buildMetadataIndex = normalized.IndexOf('+');
        if (buildMetadataIndex >= 0)
        {
            if (buildMetadataIndex == normalized.Length - 1)
                return false;
            normalized = normalized[..buildMetadataIndex];
        }

        string? prerelease = null;
        var prereleaseIndex = normalized.IndexOf('-');
        if (prereleaseIndex >= 0)
        {
            if (prereleaseIndex == normalized.Length - 1)
                return false;
            prerelease = normalized[(prereleaseIndex + 1)..];
            normalized = normalized[..prereleaseIndex];
        }

        var components = normalized.Split('.');
        if (components.Length != 3
            || !TryParseComponent(components[0], out var major)
            || !TryParseComponent(components[1], out var minor)
            || !TryParseComponent(components[2], out var patch))
        {
            return false;
        }

        if (prerelease == null)
        {
            version = new CoveVersionCompatibility(major, minor, patch, ReleaseStage, 0, null);
            return true;
        }

        if (string.Equals(prerelease, "dev", StringComparison.OrdinalIgnoreCase))
        {
            version = new CoveVersionCompatibility(major, minor, patch, DevelopmentStage, 0, null);
            return true;
        }

        if (prerelease.StartsWith("dev.", StringComparison.OrdinalIgnoreCase))
        {
            var sequenceText = prerelease[4..];
            if (!long.TryParse(sequenceText, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
                return false;

            version = new CoveVersionCompatibility(major, minor, patch, DevelopmentStage, sequence, null);
            return true;
        }

        if (!IsValidPrerelease(prerelease))
            return false;

        version = new CoveVersionCompatibility(major, minor, patch, PrereleaseStage, 0, prerelease);
        return true;
    }

    public int CompareTo(CoveVersionCompatibility other)
    {
        var comparison = Major.CompareTo(other.Major);
        if (comparison != 0) return comparison;

        comparison = Minor.CompareTo(other.Minor);
        if (comparison != 0) return comparison;

        comparison = Patch.CompareTo(other.Patch);
        if (comparison != 0) return comparison;

        comparison = Stage.CompareTo(other.Stage);
        if (comparison != 0) return comparison;

        if (Stage == DevelopmentStage)
            return DevelopmentSequence.CompareTo(other.DevelopmentSequence);

        if (Stage == PrereleaseStage)
            return ComparePrerelease(Prerelease!, other.Prerelease!);

        return 0;
    }

    private static bool TryParseComponent(string value, out int component) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out component);

    private static bool IsValidPrerelease(string prerelease) =>
        prerelease.Split('.').All(identifier =>
            identifier.Length > 0
            && identifier.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));

    private static int ComparePrerelease(string left, string right)
    {
        var leftIdentifiers = left.Split('.');
        var rightIdentifiers = right.Split('.');
        var sharedLength = Math.Min(leftIdentifiers.Length, rightIdentifiers.Length);

        for (var index = 0; index < sharedLength; index++)
        {
            var leftNumeric = long.TryParse(
                leftIdentifiers[index], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = long.TryParse(
                rightIdentifiers[index], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);

            int comparison;
            if (leftNumeric && rightNumeric)
                comparison = leftNumber.CompareTo(rightNumber);
            else if (leftNumeric)
                comparison = -1;
            else if (rightNumeric)
                comparison = 1;
            else
                comparison = string.Compare(leftIdentifiers[index], rightIdentifiers[index], StringComparison.Ordinal);

            if (comparison != 0)
                return comparison;
        }

        return leftIdentifiers.Length.CompareTo(rightIdentifiers.Length);
    }
}
