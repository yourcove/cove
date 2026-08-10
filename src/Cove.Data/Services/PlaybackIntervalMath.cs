using Cove.Core.Entities;

namespace Cove.Data.Services;

internal static class PlaybackIntervalMath
{
    public static double ComputeMergedWatchedSec(IEnumerable<PlaybackInterval> intervals)
    {
        var sorted = intervals.OrderBy(interval => interval.StartSec).ThenBy(interval => interval.EndSec).ToList();
        var total = 0d;
        var currentStart = double.MinValue;
        var currentEnd = double.MinValue;
        foreach (var interval in sorted)
        {
            if (interval.EndSec <= interval.StartSec)
                continue;
            if (interval.StartSec > currentEnd)
            {
                total += Math.Max(0d, currentEnd - currentStart);
                currentStart = interval.StartSec;
                currentEnd = interval.EndSec;
            }
            else
            {
                currentEnd = Math.Max(currentEnd, interval.EndSec);
            }
        }

        return total + Math.Max(0d, currentEnd - currentStart);
    }
}
