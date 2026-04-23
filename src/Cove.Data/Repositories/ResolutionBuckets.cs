namespace Cove.Data.Repositories;

internal static class ResolutionBuckets
{
    private sealed record Bucket(int Value, int MinDimension, int MaxDimensionExclusive);

    private static readonly Bucket[] Buckets =
    [
        new(144, 144, 341),
        new(240, 341, 533),
        new(360, 533, 747),
        new(480, 747, 907),
        new(540, 907, 1120),
        new(720, 1120, 1600),
        new(1080, 1600, 2240),
        new(1440, 2240, 3200),
        new(2160, 3200, 4480),
        new(2880, 4480, 5632),
        new(3384, 5632, 6656),
        new(4032, 6656, 7424),
        new(4320, 7424, 9840),
        new(9999, 9840, int.MaxValue),
    ];

    public static bool TryGetBounds(int value, out int minInclusive, out int maxInclusive)
    {
        var bucket = Buckets.FirstOrDefault(candidate => candidate.Value == value);
        if (bucket == null)
        {
            minInclusive = 0;
            maxInclusive = 0;
            return false;
        }

        minInclusive = bucket.MinDimension;
        maxInclusive = bucket.MaxDimensionExclusive == int.MaxValue ? int.MaxValue : bucket.MaxDimensionExclusive - 1;
        return true;
    }
}