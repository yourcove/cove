using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Services;

/// <summary>
/// Single source of truth for matching scraped relation names (performers, tags) to existing
/// entities. Both the scrape-apply path and the scrape dialog's resolve endpoint go through here
/// so the UI's "matches existing" vs "will create" prediction can never drift from what a save
/// actually does. Both performers and tags match on primary name or alias (a primary-name match
/// wins over an alias match), mirroring how each is applied.
/// </summary>
public static class RelationNameResolver
{
    /// <summary>
    /// Resolves each requested name to an existing performer by primary name or alias
    /// (case-insensitive; a primary-name match takes precedence over an alias match). The returned
    /// dictionary is keyed by the requested name so callers can look up by the scraped value.
    /// Entities are tracked by <paramref name="db"/> so callers on the apply path can attach them.
    /// </summary>
    public static async Task<Dictionary<string, Performer>> ResolvePerformersAsync(CoveContext db, IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        var normalized = NormalizeSet(names);
        if (normalized.Count == 0)
            return new Dictionary<string, Performer>(StringComparer.OrdinalIgnoreCase);

        var candidates = await db.Performers
            .Include(performer => performer.Aliases)
            .Where(performer => normalized.Contains(performer.Name.Trim().ToLower())
                || performer.Aliases.Any(alias => normalized.Contains(alias.Alias.Trim().ToLower())))
            .ToListAsync(ct);

        return BuildLookup(names, candidates, performer => performer.Name, performer => performer.Aliases.Select(alias => alias.Alias));
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

    private static Dictionary<string, T> BuildLookup<T>(
        IReadOnlyCollection<string> requestedNames,
        IReadOnlyCollection<T> candidates,
        Func<T, string> nameSelector,
        Func<T, IEnumerable<string>>? aliasSelector)
    {
        var byName = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        var byAlias = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            byName.TryAdd(nameSelector(candidate).Trim(), candidate);
            if (aliasSelector == null)
                continue;

            foreach (var alias in aliasSelector(candidate))
            {
                if (!string.IsNullOrWhiteSpace(alias))
                    byAlias.TryAdd(alias.Trim(), candidate);
            }
        }

        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var requested in requestedNames)
        {
            var key = requested?.Trim();
            if (string.IsNullOrWhiteSpace(key) || result.ContainsKey(key))
                continue;

            if (byName.TryGetValue(key, out var byNameMatch))
                result[key] = byNameMatch;
            else if (byAlias.TryGetValue(key, out var byAliasMatch))
                result[key] = byAliasMatch;
        }

        return result;
    }

    private static HashSet<string> NormalizeSet(IEnumerable<string> names)
        => names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim().ToLowerInvariant())
            .ToHashSet();
}
