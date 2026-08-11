using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cove.Core.Entities;

/// <summary>
/// Compatibility policy for canonical performer and studio identities. Keep these operations in
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

    public static string ConflictGroupKey(string entityType, string identityKey)
        => $"{entityType}:{Hash(identityKey)}";

    public static string ConflictGroupRevision(
        string entityType,
        string identityKey,
        int recommendedSurvivorId,
        IEnumerable<EntityNameRevisionCandidate> candidates,
        IEnumerable<EntityExternalReferenceRevision>? externalReferences = null)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            EntityType = entityType,
            IdentityKey = identityKey,
            RecommendedSurvivorId = recommendedSurvivorId,
            Candidates = candidates.OrderBy(candidate => candidate.EntityId).ToArray(),
            ExternalReferences = (externalReferences ?? [])
                .OrderBy(reference => reference.EntityId)
                .ThenBy(reference => reference.ReferenceKey, StringComparer.Ordinal)
                .ThenBy(reference => reference.RowCount)
                .ToArray(),
        });
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    public static string ConflictScanRevision(IEnumerable<EntityNameGroupRevision> groups)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(groups
            .OrderBy(group => group.EntityType, StringComparer.Ordinal)
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ThenBy(group => group.Revision, StringComparer.Ordinal)
            .ToArray());
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public static class NameConflictEntityTypes
{
    public const string Performer = "performer";
    public const string Studio = "studio";

    public static bool IsSupported(string? value)
        => value is Performer or Studio;
}

public static class EntityNameConflictActions
{
    public const string Keep = "keep";
    public const string MergeEntity = "merge-entity";
    public const string Rename = "rename";
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

public sealed record EntityNameRevisionCandidate(
    int EntityId,
    string Name,
    string? Disambiguation,
    string NormalizedName,
    string? NormalizedDisambiguation);

public sealed record EntityExternalReferenceRevision(
    int EntityId,
    string ReferenceKey,
    int? RowCount,
    string? AccessLimitation = null);

public sealed record EntityNameGroupRevision(string EntityType, string Key, string Revision);
