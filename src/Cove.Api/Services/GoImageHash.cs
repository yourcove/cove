using System.Globalization;

namespace Cove.Api.Services;

/// <summary>
/// The part of goimagehash's <c>PerceptionHash</c> that runs after the image has been reduced to
/// 64×64: grayscale conversion, the 2D DCT, the median of the low-frequency 8×8 block, and the bit
/// layout. Stash computes every pHash with it, so each step reproduces Go's floating-point
/// operations in Go's order.
/// </summary>
internal static class GoImageHash
{
    public const int Size = 64;
    private const int LowFrequencySize = 8;

    /// <summary>
    /// goimagehash's <c>pixel2Gray</c>, applied to the 16-bit premultiplied channels Go's
    /// <c>color.Color.RGBA()</c> returns. The integer divisions are part of the formula.
    /// </summary>
    public static double PixelToGray(uint r, uint g, uint b)
        => 0.299 * (r / 257) + 0.587 * (g / 257) + 0.114 * (b / 256);

    /// <summary>Hashes a row-major 64×64 grayscale image. The array is transformed in place.</summary>
    public static ulong Hash(double[] pixels)
    {
        if (pixels.Length != Size * Size)
            throw new ArgumentException("Expected a 64×64 grayscale image.", nameof(pixels));

        Dct2DInPlace(pixels);

        var flattened = new double[LowFrequencySize * LowFrequencySize];
        for (var i = 0; i < LowFrequencySize; i++)
            for (var j = 0; j < LowFrequencySize; j++)
                flattened[LowFrequencySize * i + j] = pixels[i * Size + j];

        var median = MedianQuickSelect(flattened);

        // Go's leftShiftSet(64 - idx - 1): the first coefficient is the most significant bit.
        ulong hash = 0;
        for (var idx = 0; idx < flattened.Length; idx++)
        {
            if (flattened[idx] > median)
                hash |= 1UL << (63 - idx);
        }
        return hash;
    }

    /// <summary>Stash's hex form: lowercase, without leading zeros.</summary>
    public static string Format(ulong hash) => hash.ToString("x", CultureInfo.InvariantCulture);

    // goimagehash's static DCT tables (2·cos((i + 0.5)·π / n)), as Go's math.Cos computes them on
    // amd64; arm64 builds of Go fuse multiply-adds in math.Cos and may differ in the same last bits.
    // .NET's Math.Cos defers to the platform C library, which disagrees with Go in the last bit for
    // about half of these entries and can differ between operating systems. That only flips a hash
    // bit when a coefficient lands next to the median, which flat or synthetic images do routinely.
    private static readonly double[] Dct64 = Bits(
        0x3ffffd886084cd0cUL, 0x3fffe9cdad01883aUL, 0x3fffc26470e19fd4UL, 0x3fff8764fa714ba9UL,
        0x3fff38f3ac64e589UL, 0x3ffed740e7684963UL, 0x3ffe6288ec48e112UL, 0x3ffddb13b6ccc23cUL,
        0x3ffd4134d14dc93aUL, 0x3ffc954b213411f5UL, 0x3ffbd7c0ac6f9529UL, 0x3ffb090a58150200UL,
        0x3ffa29a7a0462782UL, 0x3ff93a22499263fcUL, 0x3ff83b0e0bff976eUL, 0x3ff72d0837efff97UL,
        0x3ff610b7551d2cdfUL, 0x3ff4e6cabbe3e5eaUL, 0x3ff3affa292050baUL, 0x3ff26d054cdd12e0UL,
        0x3ff11eb3541b4b24UL, 0x3fef8ba4dbf89abcUL, 0x3fecc66e9931c45eUL, 0x3fe9ef7943a8ed89UL,
        0x3fe7088530fa45a1UL, 0x3fe4135c94176602UL, 0x3fe111d262b1f678UL, 0x3fdc0b826a7e4f62UL,
        0x3fd5e214448b3fcbUL, 0x3fcf564e56a97314UL, 0x3fc2d52092ce19f8UL, 0x3fa92155f7a36678UL);

    private static readonly double[] Dct32 = Bits(
        0x3ffff621e3796d7eUL, 0x3fffa7557f08a517UL, 0x3fff0a7efb9230d8UL, 0x3ffe212104f686e5UL,
        0x3ffced7af43cc773UL, 0x3ffb728345196e3eUL, 0x3ff9b3e047f38741UL, 0x3ff7b5df226aafafUL,
        0x3ff57d69348ceca0UL, 0x3ff30ff7fce17036UL, 0x3ff073879922ffeeUL, 0x3feb5d1009e15cc2UL,
        0x3fe58f9a75ab1fddUL, 0x3fdf19f97b215f1eUL, 0x3fd2c8106e8e613aUL, 0x3fb91f65f10dd825UL);

    private static readonly double[] Dct16 = Bits(
        0x3fffd88da3d12525UL, 0x3ffe9f4156c62ddaUL, 0x3ffc38b2f180bdb1UL, 0x3ff8bc806b151741UL,
        0x3ff44cf325091dd7UL, 0x3fee2b5d3806f63eUL, 0x3fe294062ed59f04UL, 0x3fc917a6bc29b438UL);

    private static readonly double[] Dct8 = Bits(
        0x3fff6297cff75cb0UL, 0x3ffa9b66290ea1a4UL, 0x3ff1c73b39ae68c9UL, 0x3fd8f8b83c69a60cUL);

    private static readonly double[] Dct4 = Bits(0x3ffd906bcf328d46UL, 0x3fe87de2a6aea964UL);

    private static readonly double[] Dct2 = Bits(0x3ff6a09e667f3bcdUL);

    private static double[] Bits(params ulong[] bits) => Array.ConvertAll(bits, BitConverter.UInt64BitsToDouble);

    private static double[] Table(int length) => length switch
    {
        64 => Dct64,
        32 => Dct32,
        16 => Dct16,
        8 => Dct8,
        4 => Dct4,
        2 => Dct2,
        _ => throw new ArgumentOutOfRangeException(nameof(length)),
    };

    /// <summary>
    /// DCT-II, unscaled, by Byeong Gi Lee's 1984 recursion: goimagehash's DCT1DFast64.
    /// </summary>
    private static void Dct1DInPlace(Span<double> input)
    {
        ForwardTransform(input, stackalloc double[Size], Size);
    }

    private static void ForwardTransform(Span<double> input, Span<double> temp, int len)
    {
        if (len == 1) return;

        var halfLen = len / 2;
        var table = Table(len);
        for (var i = 0; i < halfLen; i++)
        {
            double x = input[i], y = input[len - 1 - i];
            temp[i] = x + y;
            temp[i + halfLen] = (x - y) / table[i];
        }

        ForwardTransform(temp, input, halfLen);
        ForwardTransform(temp.Slice(halfLen), input, halfLen);

        for (var i = 0; i < halfLen - 1; i++)
        {
            input[i * 2] = temp[i];
            input[i * 2 + 1] = temp[i + halfLen] + temp[i + halfLen + 1];
        }
        input[len - 2] = temp[halfLen - 1];
        input[len - 1] = temp[len - 1];
    }

    /// <summary>goimagehash's DCT2DFast64: rows first, then columns.</summary>
    private static void Dct2DInPlace(double[] pixels)
    {
        for (var i = 0; i < Size; i++)
            Dct1DInPlace(pixels.AsSpan(i * Size, Size));

        Span<double> column = stackalloc double[Size];
        for (var i = 0; i < Size; i++)
        {
            for (var j = 0; j < Size; j++)
                column[j] = pixels[i + j * Size];

            Dct1DInPlace(column);

            for (var j = 0; j < Size; j++)
                pixels[i + j * Size] = column[j];
        }
    }

    /// <summary>
    /// Median matching Go's MedianOfPixelsFast64: quickselect to position len/2,
    /// then average seq[k-1] and seq[k] when len is even.
    /// </summary>
    internal static double MedianQuickSelect(double[] input)
    {
        var tmp = new double[input.Length];
        Array.Copy(input, tmp, input.Length);
        var pos = tmp.Length / 2;
        QuickSelect(tmp, 0, tmp.Length - 1, pos);

        // Go averages two middle elements for even-length arrays
        if (tmp.Length % 2 == 0)
            return tmp[pos - 1] / 2 + tmp[pos] / 2;
        return tmp[pos];
    }

    private static void QuickSelect(double[] seq, int low, int hi, int k)
    {
        if (low == hi) return;

        while (low < hi)
        {
            var pivot = low / 2 + hi / 2;
            var pivotValue = seq[pivot];
            var storeIdx = low;
            (seq[pivot], seq[hi]) = (seq[hi], seq[pivot]);

            for (var i = low; i < hi; i++)
            {
                if (seq[i] < pivotValue)
                {
                    (seq[storeIdx], seq[i]) = (seq[i], seq[storeIdx]);
                    storeIdx++;
                }
            }
            (seq[hi], seq[storeIdx]) = (seq[storeIdx], seq[hi]);

            if (k <= storeIdx)
                hi = storeIdx;
            else
                low = storeIdx + 1;
        }
    }
}
