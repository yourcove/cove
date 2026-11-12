namespace Cove.Api.Services;

/// <summary>
/// Admission limits for a duplicate search. A search retains a few machine words per candidate row (a key
/// hash or a pHash, and a video ID) rather than the row's text, so its memory grows with the library at
/// roughly 24 bytes per row and needs no row or byte budget. What stays limited is what a single row or
/// request can carry.
/// </summary>
internal static class DuplicateSearchLimits
{
    /// <summary>Largest explicit selection a caller may hand to a search. Library and folder scopes are not limited.</summary>
    internal const int MaximumExplicitVideos = 100_000;
    internal const int MaximumFieldCharacters = 4096;
    /// <summary>Rows read per keyset page. Each row is at most a few bounded fields.</summary>
    internal const int PageSize = 4_096;

    internal static int[] NormalizeIds(IEnumerable<int> ids)
    {
        var result = ids.Where(id => id > 0).Distinct().Take(MaximumExplicitVideos + 1).ToArray();
        if (result.Length > MaximumExplicitVideos)
            throw new InvalidOperationException($"Duplicate search accepts at most {MaximumExplicitVideos:N0} explicitly selected videos. Select fewer videos, or search by folder or the whole library instead.");
        return result;
    }

    internal static void CheckFieldLength(int characters)
    {
        if (characters > MaximumFieldCharacters)
            throw new InvalidOperationException($"Duplicate search metadata exceeds the {MaximumFieldCharacters:N0}-character field limit. Shorten the affected metadata and try again.");
    }
}
