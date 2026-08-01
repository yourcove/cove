namespace Cove.Core.Common;

/// <summary>
/// Orders Cove host versions for extension compatibility checks.
/// </summary>
/// <remarks>
/// Versions use normal SemVer precedence. A development version such as
/// <c>1.1.1-dev.12</c> sorts after <c>1.1.0</c> and before the eventual
/// <c>1.1.1</c> release.
/// </remarks>
public readonly record struct CoveVersionCompatibility : IComparable<CoveVersionCompatibility>
{
    private CoveVersionCompatibility(
        string major,
        string minor,
        string patch,
        string? prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    private string? Major { get; }
    private string? Minor { get; }
    private string? Patch { get; }
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

            var buildMetadata = normalized[(buildMetadataIndex + 1)..];
            if (!AreValidIdentifiers(buildMetadata, forbidNumericLeadingZeros: false))
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
            || !IsValidNumericIdentifier(components[0], forbidLeadingZeros: true)
            || !IsValidNumericIdentifier(components[1], forbidLeadingZeros: true)
            || !IsValidNumericIdentifier(components[2], forbidLeadingZeros: true))
        {
            return false;
        }

        var major = components[0];
        var minor = components[1];
        var patch = components[2];

        if (prerelease == null)
        {
            version = new CoveVersionCompatibility(major, minor, patch, null);
            return true;
        }

        if (!AreValidIdentifiers(prerelease, forbidNumericLeadingZeros: true))
            return false;

        version = new CoveVersionCompatibility(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(CoveVersionCompatibility other)
    {
        var comparison = CompareNumericIdentifier(Major ?? "0", other.Major ?? "0");
        if (comparison != 0) return comparison;

        comparison = CompareNumericIdentifier(Minor ?? "0", other.Minor ?? "0");
        if (comparison != 0) return comparison;

        comparison = CompareNumericIdentifier(Patch ?? "0", other.Patch ?? "0");
        if (comparison != 0) return comparison;

        if (Prerelease == null)
            return other.Prerelease == null ? 0 : 1;
        if (other.Prerelease == null)
            return -1;

        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    private static bool AreValidIdentifiers(string value, bool forbidNumericLeadingZeros) =>
        value.Split('.').All(identifier =>
            identifier.Length > 0
            && identifier.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')
            && (!forbidNumericLeadingZeros
                || !identifier.All(char.IsAsciiDigit)
                || IsValidNumericIdentifier(identifier, forbidLeadingZeros: true)));

    private static bool IsValidNumericIdentifier(string value, bool forbidLeadingZeros) =>
        value.Length > 0
        && value.All(char.IsAsciiDigit)
        && (!forbidLeadingZeros || value.Length == 1 || value[0] != '0');

    private static int CompareNumericIdentifier(string left, string right)
    {
        var comparison = left.Length.CompareTo(right.Length);
        return comparison != 0
            ? comparison
            : string.Compare(left, right, StringComparison.Ordinal);
    }

    private static int ComparePrerelease(string left, string right)
    {
        var leftIdentifiers = left.Split('.');
        var rightIdentifiers = right.Split('.');
        var sharedLength = Math.Min(leftIdentifiers.Length, rightIdentifiers.Length);

        for (var index = 0; index < sharedLength; index++)
        {
            var leftNumeric = leftIdentifiers[index].All(char.IsAsciiDigit);
            var rightNumeric = rightIdentifiers[index].All(char.IsAsciiDigit);

            int comparison;
            if (leftNumeric && rightNumeric)
                comparison = CompareNumericIdentifier(leftIdentifiers[index], rightIdentifiers[index]);
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
