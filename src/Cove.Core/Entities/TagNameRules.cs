namespace Cove.Core.Entities;

/// <summary>
/// The canonical tag namespace rules used by write-time validation, import matching, and schema
/// enforcement. Keep normalization in managed code so every caller uses identical Unicode rules.
/// </summary>
public static class TagNameRules
{
    public const string EmptyCanonicalName = "<empty>";
    public static IEqualityComparer<string> NamespaceComparer { get; } = new TagNamespaceComparer();

    public static string NormalizeCanonicalName(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length == 0 ? EmptyCanonicalName : trimmed;
    }

    public static string? NormalizeAlias(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    public static string NamespaceKey(string normalizedName)
        => normalizedName.ToLowerInvariant();

    public static bool NamesEqual(string left, string right)
        => string.Equals(NamespaceKey(left), NamespaceKey(right), StringComparison.Ordinal);

    private sealed class TagNamespaceComparer : IEqualityComparer<string>
    {
        public bool Equals(string? left, string? right)
            => string.Equals(
                NamespaceKey(NormalizeCanonicalName(left)),
                NamespaceKey(NormalizeCanonicalName(right)),
                StringComparison.Ordinal);

        public int GetHashCode(string value)
            => StringComparer.Ordinal.GetHashCode(NamespaceKey(NormalizeCanonicalName(value)));
    }
}

public static class TagExternalReferenceActions
{
    public const string UpdateToSurvivor = "update-to-survivor";
    public const string DeleteRows = "delete-rows";
}

public static class TagExternalReferenceAccessLimitations
{
    public const string RowLevelSecurity = "row-level-security";
    public const string DatabasePermission = "database-permission";
}
