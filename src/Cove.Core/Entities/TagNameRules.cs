using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cove.Core.Entities;

/// <summary>
/// The shared tag namespace rules planned for Cove 1.3.0. The compatibility scanner, interactive
/// cleanup flow, write-time validation, and the eventual enforcement migration must all use this
/// specification so a database reported as ready is interpreted identically during upgrade.
/// </summary>
public static class TagNameRules
{
    public const string EmptyCanonicalName = "<empty>";
    public const string BlankAliasGroupKey = "blank-aliases";
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

    public static string NamespaceGroupKey(string namespaceKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(namespaceKey));
        return $"namespace-{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    public static string ConflictGroupRevision(
        IEnumerable<TagNameRevisionClaim> claims,
        int recommendedSurvivorTagId,
        IEnumerable<TagExternalReferenceRevision>? externalReferences = null)
    {
        var ordered = claims
            .OrderBy(claim => claim.TagId)
            .ThenBy(claim => claim.ClaimType, StringComparer.Ordinal)
            .ThenBy(claim => claim.AliasId)
            .ThenBy(claim => claim.OriginalValue, StringComparer.Ordinal)
            .ToArray();
        var orderedExternalReferences = (externalReferences ?? [])
            .OrderBy(reference => reference.TagId)
            .ThenBy(reference => reference.ReferenceKey, StringComparer.Ordinal)
            .ThenBy(reference => reference.RowCount)
            .ToArray();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Claims = ordered,
            RecommendedSurvivorTagId = recommendedSurvivorTagId,
            ExternalReferences = orderedExternalReferences,
        });
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    public static string ConflictScanRevision(IEnumerable<TagNameGroupRevision> groups)
    {
        var ordered = groups
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ThenBy(group => group.Revision, StringComparer.Ordinal)
            .ToArray();
        var payload = JsonSerializer.SerializeToUtf8Bytes(ordered);
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

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

public sealed record TagNameRevisionClaim(
    int TagId,
    string OwningTagName,
    string ClaimType,
    int? AliasId,
    string OriginalValue,
    string? NormalizedValue);

public sealed record TagNameGroupRevision(string Key, string Revision);

public sealed record TagExternalReferenceRevision(
    int TagId,
    string ReferenceKey,
    int? RowCount,
    string? AccessLimitation = null);

public static class TagNameClaimTypes
{
    public const string CanonicalName = "tag-name";
    public const string Alias = "alias";
}

public static class TagNameConflictKinds
{
    public const string CanonicalNameCollision = "canonical-name-collision";
    public const string NameAliasCollision = "name-alias-collision";
    public const string AliasOwnershipCollision = "alias-ownership-collision";
    public const string RedundantSelfAlias = "redundant-self-alias";
    public const string DuplicateAlias = "duplicate-alias";
    public const string BlankAlias = "blank-alias";
    public const string WhitespaceOnlyCanonicalName = "whitespace-only-canonical-name";
    public const string EmptyNameCollision = "empty-name-collision";
}

public static class TagNameConflictActions
{
    public const string Keep = "keep";
    public const string MergeTag = "merge-tag";
    public const string RemoveAlias = "remove-alias";
    public const string Rename = "rename";
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

public readonly record struct TagNameClaimIdentity(int TagId, int? AliasId);

public sealed record TagNamePolicyClaim(int TagId, string ClaimType, int? AliasId)
{
    public TagNameClaimIdentity Identity => new(TagId, AliasId);
}

public sealed record TagNameClaimRecommendation(
    TagNameClaimIdentity Identity,
    string Action,
    bool IsSurvivingClaim);

public sealed record TagNameResolutionRecommendation(
    int SurvivorTagId,
    IReadOnlyDictionary<TagNameClaimIdentity, TagNameClaimRecommendation> Claims)
{
    public IReadOnlyList<int> MergeTagIds => Claims.Values
        .Where(claim => claim.Action == TagNameConflictActions.MergeTag)
        .Select(claim => claim.Identity.TagId)
        .Distinct()
        .Order()
        .ToArray();

    public IReadOnlyList<int> RemoveAliasIds => Claims.Values
        .Where(claim => claim.Action == TagNameConflictActions.RemoveAlias && claim.Identity.AliasId != null)
        .Select(claim => claim.Identity.AliasId!.Value)
        .Distinct()
        .Order()
        .ToArray();
}

/// <summary>
/// Deterministic survivor and default-action policy shared by the scanner and cleanup executor.
/// Canonical claims remain the candidate set whenever one exists so an alias collision does not
/// create a needless whole-tag merge. Within that candidate set, the owner with the most references
/// wins and the lowest tag ID breaks a tie. Alias claims outside the survivor are removed by default;
/// other canonical claims merge by default.
/// </summary>
public static class TagNameResolutionPolicy
{
    public static TagNameResolutionRecommendation Recommend(
        IReadOnlyCollection<TagNamePolicyClaim> claims,
        bool isBlankAliasGroup = false,
        int? survivorTagId = null,
        IReadOnlyDictionary<int, long>? referenceCounts = null)
    {
        if (claims.Count == 0)
            throw new ArgumentException("At least one tag-name claim is required.", nameof(claims));

        var ownerIds = claims.Select(claim => claim.TagId).Distinct().Order().ToArray();
        var canonicalOwnerIds = claims
            .Where(claim => claim.ClaimType == TagNameClaimTypes.CanonicalName)
            .Select(claim => claim.TagId)
            .Distinct()
            .Order()
            .ToArray();
        var candidateOwnerIds = canonicalOwnerIds.Length > 0 ? canonicalOwnerIds : ownerIds;
        var survivor = survivorTagId ?? candidateOwnerIds
            .OrderByDescending(tagId => referenceCounts?.GetValueOrDefault(tagId) ?? 0)
            .ThenBy(tagId => tagId)
            .First();
        if (!ownerIds.Contains(survivor))
            throw new ArgumentException("The selected survivor does not own a claim in this conflict group.", nameof(survivorTagId));

        TagNamePolicyClaim? survivingClaim = null;
        if (!isBlankAliasGroup)
        {
            survivingClaim = claims
                .Where(claim => claim.TagId == survivor && claim.ClaimType == TagNameClaimTypes.CanonicalName)
                .OrderBy(claim => claim.AliasId)
                .FirstOrDefault()
                ?? claims
                    .Where(claim => claim.TagId == survivor)
                    .OrderBy(claim => claim.AliasId)
                    .First();
        }

        var mergeTagIds = claims
            .Where(claim => claim.ClaimType == TagNameClaimTypes.CanonicalName && claim.TagId != survivor)
            .Select(claim => claim.TagId)
            .ToHashSet();
        var recommendations = new Dictionary<TagNameClaimIdentity, TagNameClaimRecommendation>();
        foreach (var claim in claims.OrderBy(claim => claim.TagId).ThenBy(claim => claim.AliasId))
        {
            var isSurvivingClaim = survivingClaim?.Identity == claim.Identity;
            var action = isSurvivingClaim
                ? TagNameConflictActions.Keep
                : mergeTagIds.Contains(claim.TagId)
                    ? TagNameConflictActions.MergeTag
                    : claim.ClaimType == TagNameClaimTypes.Alias
                        ? TagNameConflictActions.RemoveAlias
                        : TagNameConflictActions.Keep;
            recommendations.Add(
                claim.Identity,
                new TagNameClaimRecommendation(claim.Identity, action, isSurvivingClaim));
        }

        return new TagNameResolutionRecommendation(survivor, recommendations);
    }
}
