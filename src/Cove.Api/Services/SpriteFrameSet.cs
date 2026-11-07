using Cove.Api.Imaging;

namespace Cove.Api.Services;

/// <summary>
/// Outcome of turning a sparse batch-extraction result into a complete grid of sprite frames.
/// <see cref="Frames"/> is null when too few frames decoded to be worth publishing.
/// </summary>
internal readonly record struct SpriteFrameSet(
    PixelBuffer<Rgba32>[]? Frames,
    int DecodedCount,
    int SubstitutedCount,
    int RequestedCount)
{
    public bool IsUsable => Frames is not null;
}

internal static class SpriteFrameGapFiller
{
    /// <summary>Share of requested frames that must actually decode for a sprite to be published.</summary>
    internal const double MinimumDecodedRatio = 0.9;

    /// <summary>
    /// Fills gaps left by undecodable frames with a copy of the nearest decoded neighbour, so that a
    /// video with a few corrupt GOPs still gets a usable scrubbing preview instead of nothing at all.
    ///
    /// Returns an unusable result when fewer than <see cref="MinimumDecodedRatio"/> of the frames
    /// decoded - at that point the sheet would be mostly filler and is not worth showing.
    ///
    /// Substituted slots hold clones, never a second reference to a neighbour.
    /// </summary>
    public static SpriteFrameSet Fill(PixelBuffer<Rgba32>?[] extracted)
    {
        var requested = extracted.Length;
        var decoded = 0;
        for (var i = 0; i < requested; i++)
            if (extracted[i] is not null) decoded++;

        if (requested == 0 || decoded == 0 || decoded < (int)Math.Ceiling(requested * MinimumDecodedRatio))
            return new SpriteFrameSet(null, decoded, 0, requested);

        if (decoded == requested)
            return new SpriteFrameSet(extracted!, decoded, 0, requested);

        var filled = new PixelBuffer<Rgba32>[requested];
        var substituted = 0;
        for (var i = 0; i < requested; i++)
        {
            if (extracted[i] is { } present)
            {
                filled[i] = present;
                continue;
            }

            filled[i] = NearestDecoded(extracted, i)!.Clone();
            substituted++;
        }

        return new SpriteFrameSet(filled, decoded, substituted, requested);
    }

    /// <summary>Nearest decoded frame by index distance, preferring the earlier one on a tie.</summary>
    private static PixelBuffer<Rgba32>? NearestDecoded(PixelBuffer<Rgba32>?[] frames, int index)
    {
        for (var distance = 1; distance < frames.Length; distance++)
        {
            var before = index - distance;
            if (before >= 0 && frames[before] is { } earlier)
                return earlier;

            var after = index + distance;
            if (after < frames.Length && frames[after] is { } later)
                return later;
        }

        return null;
    }
}
