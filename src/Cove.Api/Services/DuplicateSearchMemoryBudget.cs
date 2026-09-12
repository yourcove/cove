namespace Cove.Api.Services;

/// <summary>Admission limits for retained candidates, grouping state, and the visual index.</summary>
internal sealed class DuplicateSearchMemoryBudget
{
    internal const int MaximumVideos = 100_000;
    internal const int MaximumRows = 100_000;
    internal const int MaximumFieldCharacters = 4096;
    internal const long MaximumEstimatedBytes = 64L * 1024 * 1024;
    internal const int PageSize = 128;
    private int rows;
    private long estimatedBytes;

    internal static int[] NormalizeIds(IEnumerable<int> ids)
    {
        var result = ids.Where(id => id > 0).Distinct().Take(MaximumVideos + 1).ToArray();
        CheckVideoCount(result.Length);
        return result;
    }

    internal static void CheckVideoCount(int count)
    {
        if (count > MaximumVideos)
            throw new InvalidOperationException($"Duplicate search exceeds the {MaximumVideos:N0}-video candidate limit. Reduce the candidate set and try again.");
    }

    internal void Reserve(int characters, int largestField, bool visual)
    {
        // Exact grouping can copy trimmed/combined string keys. Visual rows additionally reserve
        // space for up to 17 segment buckets, their lists, and sorted candidate arrays.
        var charge = (visual ? 2048L : 256L) + 6L * characters;
        if (rows >= MaximumRows)
            ThrowMemoryLimit();
        ReserveBytes(charge, largestField);
        rows++;
    }

    internal void ReserveIgnoredPair() => ReserveBytes(64, 0);

    internal void ReserveGroupingNode() => ReserveBytes(64, 0);

    internal static void CheckFieldLength(int characters)
    {
        if (characters > MaximumFieldCharacters)
            throw new InvalidOperationException($"Duplicate search metadata exceeds the {MaximumFieldCharacters:N0}-character field limit. Shorten the affected metadata and try again.");
    }

    internal void ReserveKeeperFact(int characters, int largestField)
        => ReserveBytes(256L + 6L * characters, largestField);

    private void ReserveBytes(long bytes, int largestField)
    {
        CheckFieldLength(largestField);
        if (estimatedBytes + bytes > MaximumEstimatedBytes)
            ThrowMemoryLimit();
        estimatedBytes += bytes;
    }

    private static void ThrowMemoryLimit()
        => throw new InvalidOperationException("Duplicate search exceeds its candidate memory budget (100,000 metadata rows or 64 MiB estimated retained state). Reduce the candidate set and try again.");
}
