using Cove.Core.Entities;

namespace Cove.Api.Helpers;

internal static class MetadataCollectionUpdater
{
    public static bool ReplaceIfChanged<TEntity, TKey>(
        ICollection<TEntity> current,
        IEnumerable<TKey> requested,
        Func<TEntity, TKey> keySelector,
        Func<TKey, TEntity> factory,
        IEqualityComparer<TKey>? comparer = null) where TKey : notnull
    {
        comparer ??= EqualityComparer<TKey>.Default;
        var requestedKeys = requested.ToList();
        if (HaveSameValues(current.Select(keySelector), requestedKeys, comparer))
            return false;

        current.Clear();
        foreach (var key in requestedKeys)
            current.Add(factory(key));
        return true;
    }

    public static void Touch(BaseEntity entity) => entity.UpdatedAt = DateTime.UtcNow;

    private static bool HaveSameValues<TKey>(IEnumerable<TKey> left, IEnumerable<TKey> right, IEqualityComparer<TKey> comparer) where TKey : notnull
    {
        var counts = new Dictionary<TKey, int>(comparer);
        foreach (var value in left)
            counts[value] = counts.GetValueOrDefault(value) + 1;
        foreach (var value in right)
        {
            if (!counts.TryGetValue(value, out var count))
                return false;
            if (count == 1)
                counts.Remove(value);
            else
                counts[value] = count - 1;
        }
        return counts.Count == 0;
    }
}
