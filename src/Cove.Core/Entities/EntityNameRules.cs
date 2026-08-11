namespace Cove.Core.Entities;

/// <summary>
/// Canonical policy for performer and studio identities. Keep these operations in
/// managed code: PostgreSQL trim, lower, collation, and regular-expression whitespace rules are not
/// byte-for-byte equivalents of .NET Trim() and ToLowerInvariant().
/// </summary>
public static class EntityNameRules
{
    public const string EmptyCanonicalName = "<empty>";

    public static string NormalizeCanonicalName(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? EmptyCanonicalName : trimmed;
    }

    public static string? NormalizeDisambiguation(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    public static string NameKey(string? value)
        => NormalizeCanonicalName(value).ToLowerInvariant();

    public static string DisambiguationKey(string? value)
        => NormalizeDisambiguation(value)?.ToLowerInvariant() ?? string.Empty;

    public static string PerformerIdentityKey(string? name, string? disambiguation)
    {
        var nameKey = NameKey(name);
        var disambiguationKey = DisambiguationKey(disambiguation);
        return $"{nameKey.Length}:{nameKey}{disambiguationKey.Length}:{disambiguationKey}";
    }

    public static string StudioIdentityKey(string? name) => NameKey(name);

}

public static class NameConflictEntityTypes
{
    public const string Performer = "performer";
    public const string Studio = "studio";

    public static bool IsSupported(string? value)
        => value is Performer or Studio;
}

public static class EntityExternalReferenceActions
{
    public const string UpdateToSurvivor = "update-to-survivor";
    public const string DeleteRows = "delete-rows";
}

public static class EntityExternalReferenceAccessLimitations
{
    public const string RowLevelSecurity = "row-level-security";
    public const string DatabasePermission = "database-permission";
}
