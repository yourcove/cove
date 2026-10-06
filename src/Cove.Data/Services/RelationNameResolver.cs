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
        var idsByIdentity = await FindPerformerIdentitiesAsync(db, requestedKeys, ct);
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
        var matched = (await FindPerformerIdentitiesAsync(
            db,
            new HashSet<string>(StringComparer.Ordinal) { identityKey },
            ct)).GetValueOrDefault(identityKey);
        if (matched == null)
            return null;

        return await db.Performers
            .Include(performer => performer.Urls)
            .Include(performer => performer.Aliases)
            .Include(performer => performer.PerformerTags)
            .SingleAsync(performer => performer.Id == matched.Id, ct);
    }

    /// <summary>
    /// Resolves studios by canonical name first, then by alias. Canonical identities use the same
    /// trimmed, invariant-case-folded policy as writes, cleanup, and enforcement. Ambiguous legacy
    /// canonical identities are rejected instead of picking a row; duplicate aliases resolve
    /// deterministically to the oldest alias row.
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
        // The stored key narrows the read to the requested names; the identity is still recomputed
        // from the name below, so a row is matched by exactly the rule the writes enforce.
        var keyList = requestedKeys.ToArray();
        var rows = await db.Studios.AsNoTracking()
            .Where(studio => keyList.Contains(studio.NameKey))
            .Select(studio => new StudioIdentityRow(studio.Id, studio.Name))
            .ToListAsync(ct);
        var idsByIdentity = BuildUniqueIdentityLookup(
            rows,
            studio => EntityNameRules.StudioIdentityKey(studio.Name),
            requestedKeys,
            NameConflictEntityTypes.Studio);
        // Studio aliases have no stored key, so they are still read in full; there are few of them.
        var idsByAlias = new Dictionary<string, int>(StringComparer.Ordinal);
        var aliases = await db.Set<StudioAlias>().AsNoTracking()
            .OrderBy(alias => alias.Id)
            .Select(alias => new StudioAliasRow(alias.StudioId, alias.Alias))
            .ToListAsync(ct);
        foreach (var alias in aliases)
        {
            var aliasKey = EntityNameRules.StudioIdentityKey(alias.Alias);
            if (requestedKeys.Contains(aliasKey))
                idsByAlias.TryAdd(aliasKey, alias.StudioId);
        }

        var matchedIds = idsByIdentity.Values.Select(row => row.Id)
            .Concat(idsByAlias.Values)
            .Distinct()
            .ToArray();
        var candidates = await db.Studios
            .Where(studio => matchedIds.Contains(studio.Id))
            .ToDictionaryAsync(studio => studio.Id, ct);

        var result = new Dictionary<string, Studio>(StringComparer.Ordinal);
        foreach (var item in requested)
            if (idsByIdentity.TryGetValue(item.IdentityKey, out var row))
                result[item.LookupName] = candidates[row.Id];
            else if (idsByAlias.TryGetValue(item.IdentityKey, out var studioId)
                && candidates.TryGetValue(studioId, out var aliasMatch))
                result[item.LookupName] = aliasMatch;
        return result;
    }

    public static async Task<Studio?> ResolveStudioAsync(CoveContext db, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var lookupName = EntityNameRules.NormalizeCanonicalName(name);
        var matches = await ResolveStudiosAsync(db, [lookupName], ct);
        return matches.GetValueOrDefault(lookupName);
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

        // Match on the stored namespace keys, which the write path computes in .NET (SQL trim and
        // case-folding vary by provider and collation, so the key is never derived in SQL). Only the
        // requested names are read, instead of every tag and alias.
        var keys = requested.Select(TagNameRules.NamespaceKey).Distinct(StringComparer.Ordinal).ToArray();
        var aliasRows = await db.Set<TagAlias>().AsNoTracking()
            .Where(alias => keys.Contains(alias.NamespaceKey))
            .OrderBy(alias => alias.Id)
            .Select(alias => new TagAliasKeyRow(alias.TagId, alias.NamespaceKey))
            .ToListAsync(ct);
        var aliasTagIds = aliasRows.Select(row => row.TagId).Distinct().ToArray();
        var candidates = await db.Tags
            .Include(tag => tag.Aliases)
            .Where(tag => keys.Contains(tag.NamespaceKey) || aliasTagIds.Contains(tag.Id))
            .OrderBy(tag => tag.Id)
            .ToListAsync(ct);
        var tagsById = candidates.ToDictionary(tag => tag.Id);

        var requestedKeys = keys.ToHashSet(StringComparer.Ordinal);
        var byName = new Dictionary<string, Tag>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
            if (requestedKeys.Contains(candidate.NamespaceKey))
                byName.TryAdd(candidate.NamespaceKey, candidate);
        var byAlias = new Dictionary<string, Tag>(StringComparer.Ordinal);
        foreach (var row in aliasRows)
            if (tagsById.TryGetValue(row.TagId, out var owner))
                byAlias.TryAdd(row.NamespaceKey, owner);

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

    /// <summary>
    /// Reads only the performers whose stored identity key is requested. The key is recomputed from
    /// the name and disambiguation, so a row is matched by exactly the rule the writes enforce.
    /// </summary>
    private static async Task<Dictionary<string, PerformerIdentityRow>> FindPerformerIdentitiesAsync(
        CoveContext db,
        IReadOnlySet<string> requestedKeys,
        CancellationToken ct)
    {
        var keyList = requestedKeys.ToArray();
        var rows = await db.Performers.AsNoTracking()
            .Where(performer => keyList.Contains(performer.IdentityKey))
            .Select(performer => new PerformerIdentityRow(performer.Id, performer.Name, performer.Disambiguation))
            .ToListAsync(ct);
        return BuildUniqueIdentityLookup(
            rows,
            performer => EntityNameRules.PerformerIdentityKey(performer.Name, performer.Disambiguation),
            requestedKeys,
            NameConflictEntityTypes.Performer);
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
    private sealed record StudioAliasRow(int StudioId, string Alias);
    private sealed record TagAliasKeyRow(int TagId, string NamespaceKey);
    private sealed record RequestedName(string LookupName, string IdentityKey);
}
