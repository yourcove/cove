using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Services;

/// <summary>
/// Single source of truth for matching scraped relation names (performers, studios, tags) to existing
/// entities. Both the scrape-apply path and the scrape dialog's resolve endpoint go through here
/// so the UI's "matches existing" vs "will create" prediction can never drift from what a save
/// actually does. Name-only performer relations mean the exact (name, null-disambiguation) identity;
/// performer aliases never resolve identity because aliases are intentionally non-unique.
/// </summary>
public static class RelationNameResolver
{
    /// <summary>
    /// Resolves each requested name to the exact canonical performer identity with no disambiguation.
    /// The returned dictionary is keyed by the requested name so callers can look up by the scraped value.
    /// Entities are tracked by <paramref name="db"/> so callers on the apply path can attach them.
    /// </summary>
    public static async Task<Dictionary<string, Performer>> ResolvePerformersAsync(CoveContext db, IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        var requested = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => new RequestedName(
                EntityNameRules.NormalizeCanonicalName(name),
                EntityNameRules.PerformerIdentityKey(name, null)))
            .DistinctBy(item => item.LookupName, StringComparer.Ordinal)
            .ToArray();
        if (requested.Length == 0)
            return new Dictionary<string, Performer>(StringComparer.Ordinal);

        var requestedKeys = requested
            .Select(item => item.IdentityKey)
            .ToHashSet(StringComparer.Ordinal);
        var rows = await db.Performers.AsNoTracking()
            .Select(performer => new PerformerIdentityRow(performer.Id, performer.Name, performer.Disambiguation))
            .ToListAsync(ct);
        var idsByIdentity = BuildUniqueIdentityLookup(
            rows,
            performer => EntityNameRules.PerformerIdentityKey(performer.Name, performer.Disambiguation),
            requestedKeys,
            NameConflictEntityTypes.Performer);
        var matchedIds = idsByIdentity.Values.Select(row => row.Id).ToArray();
        var candidates = await db.Performers
            .Where(performer => matchedIds.Contains(performer.Id))
            .ToDictionaryAsync(performer => performer.Id, ct);

        var result = new Dictionary<string, Performer>(StringComparer.Ordinal);
        foreach (var item in requested)
            if (idsByIdentity.TryGetValue(item.IdentityKey, out var row))
                result[item.LookupName] = candidates[row.Id];
        return result;
    }

    public static async Task<Performer?> ResolvePerformerAsync(
        CoveContext db,
        string name,
        string? disambiguation,
        CancellationToken ct = default)
    {
        var identityKey = EntityNameRules.PerformerIdentityKey(name, disambiguation);
        var rows = await db.Performers.AsNoTracking()
            .Select(performer => new PerformerIdentityRow(performer.Id, performer.Name, performer.Disambiguation))
            .ToListAsync(ct);
        var matched = BuildUniqueIdentityLookup(
            rows,
            performer => EntityNameRules.PerformerIdentityKey(performer.Name, performer.Disambiguation),
            new HashSet<string>(StringComparer.Ordinal) { identityKey },
            NameConflictEntityTypes.Performer).GetValueOrDefault(identityKey);
        if (matched == null)
            return null;

        return await db.Performers
            .Include(performer => performer.Urls)
            .Include(performer => performer.Aliases)
            .Include(performer => performer.PerformerTags)
            .SingleAsync(performer => performer.Id == matched.Id, ct);
    }

    /// <summary>
    /// Resolves studios by the same trimmed, invariant-case-folded canonical identity used by writes,
    /// cleanup, and enforcement. Ambiguous legacy identities are rejected instead of picking a row.
    /// </summary>
    public static async Task<Dictionary<string, Studio>> ResolveStudiosAsync(CoveContext db, IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        var requested = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => new RequestedName(
                EntityNameRules.NormalizeCanonicalName(name),
                EntityNameRules.StudioIdentityKey(name)))
            .DistinctBy(item => item.LookupName, StringComparer.Ordinal)
            .ToArray();
        if (requested.Length == 0)
            return new Dictionary<string, Studio>(StringComparer.Ordinal);

        var requestedKeys = requested.Select(item => item.IdentityKey).ToHashSet(StringComparer.Ordinal);
        var rows = await db.Studios.AsNoTracking()
            .Select(studio => new StudioIdentityRow(studio.Id, studio.Name))
            .ToListAsync(ct);
        var idsByIdentity = BuildUniqueIdentityLookup(
            rows,
            studio => EntityNameRules.StudioIdentityKey(studio.Name),
            requestedKeys,
            NameConflictEntityTypes.Studio);
        var matchedIds = idsByIdentity.Values.Select(row => row.Id).ToArray();
        var candidates = await db.Studios
            .Where(studio => matchedIds.Contains(studio.Id))
            .ToDictionaryAsync(studio => studio.Id, ct);

        var result = new Dictionary<string, Studio>(StringComparer.Ordinal);
        foreach (var item in requested)
            if (idsByIdentity.TryGetValue(item.IdentityKey, out var row))
                result[item.LookupName] = candidates[row.Id];
        return result;
    }

    public static async Task<Studio?> ResolveStudioAsync(CoveContext db, string name, CancellationToken ct = default)
    {
        var identityKey = EntityNameRules.StudioIdentityKey(name);
        var rows = await db.Studios.AsNoTracking()
            .Select(studio => new StudioIdentityRow(studio.Id, studio.Name))
            .ToListAsync(ct);
        var matched = BuildUniqueIdentityLookup(
            rows,
            studio => EntityNameRules.StudioIdentityKey(studio.Name),
            new HashSet<string>(StringComparer.Ordinal) { identityKey },
            NameConflictEntityTypes.Studio).GetValueOrDefault(identityKey);
        return matched == null
            ? null
            : await db.Studios.SingleAsync(studio => studio.Id == matched.Id, ct);
    }

    /// <summary>
    /// Resolves each requested name to an existing tag by primary name or alias (case-insensitive;
    /// a primary-name match takes precedence over an alias match), mirroring how tags are applied.
    /// Keyed by the requested name.
    /// </summary>
    public static async Task<Dictionary<string, Tag>> ResolveTagsAsync(CoveContext db, IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        var requested = names
            .Select(name => TagNameRules.NormalizeAlias(name))
            .Where(name => name != null)
            .Select(name => name!)
            .Distinct(TagNameRules.NamespaceComparer)
            .ToArray();
        if (requested.Length == 0)
            return new Dictionary<string, Tag>(TagNameRules.NamespaceComparer);

        // Deliberately evaluate the shared namespace key in .NET. SQL trim/case-fold behavior varies
        // by provider and collation; the scanner, write validator, and resolver must interpret a name
        // identically during the 1.2 compatibility window.
        var candidates = await db.Tags
            .Include(tag => tag.Aliases)
            .OrderBy(tag => tag.Id)
            .ToListAsync(ct);

        var byName = new Dictionary<string, Tag>(StringComparer.Ordinal);
        var byAlias = new Dictionary<string, Tag>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            byName.TryAdd(TagNameRules.NamespaceKey(TagNameRules.NormalizeCanonicalName(candidate.Name)), candidate);
            foreach (var alias in candidate.Aliases.OrderBy(alias => alias.Id))
            {
                var normalizedAlias = TagNameRules.NormalizeAlias(alias.Alias);
                if (normalizedAlias != null)
                    byAlias.TryAdd(TagNameRules.NamespaceKey(normalizedAlias), candidate);
            }
        }

        var result = new Dictionary<string, Tag>(TagNameRules.NamespaceComparer);
        foreach (var name in requested)
        {
            var key = TagNameRules.NamespaceKey(name);
            if (byName.TryGetValue(key, out var canonicalMatch))
                result[name] = canonicalMatch;
            else if (byAlias.TryGetValue(key, out var aliasMatch))
                result[name] = aliasMatch;
        }

        return result;
    }

    private static Dictionary<string, T> BuildUniqueIdentityLookup<T>(
        IReadOnlyCollection<T> candidates,
        Func<T, string> identitySelector,
        IReadOnlySet<string> requestedKeys,
        string entityType)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var identityKey = identitySelector(candidate);
            if (!requestedKeys.Contains(identityKey))
                continue;
            if (!result.TryAdd(identityKey, candidate))
                throw new EntityNameConflictException(entityType);
        }

        return result;
    }

    private sealed record PerformerIdentityRow(int Id, string Name, string? Disambiguation);
    private sealed record StudioIdentityRow(int Id, string Name);
    private sealed record RequestedName(string LookupName, string IdentityKey);
}
