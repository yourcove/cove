namespace Cove.Api.Services;

/// <summary>
/// nfnt/resize's <c>Resize(64, 64, img, Bilinear)</c>, as goimagehash calls it: a horizontal pass
/// that writes a transposed intermediate image, then the same pass over that image. Weights are
/// truncated to fixed point (8.8 for 8-bit images, 16.16 for 16-bit), edge samples are clamped, and
/// each output is the integer quotient of the weighted sum by the sum of the weights.
/// </summary>
internal static class NfntBilinear
{
    public const int Size = GoImageHash.Size;

    /// <summary>Fills a span with one source row's filter inputs, <c>channels</c> values per pixel.</summary>
    public delegate void RowReader<T>(int y, Span<T> destination);

    public static byte[] Resize8(int width, int height, int channels, RowReader<byte> readRow)
    {
        var (coeffsX, offsetX, lengthX) = Weights8(Size, width / (double)Size);
        var (coeffsY, offsetY, lengthY) = Weights8(Size, height / (double)Size);

        // The intermediate image is transposed: one row per output column, one value per source row.
        var temp = new byte[Size * height * channels];
        var row = new byte[width * channels];
        for (var y = 0; y < height; y++)
        {
            readRow(y, row);
            Filter8(row, width, channels, coeffsX, offsetX, lengthX, temp, y, height * channels);
        }

        var result = new byte[Size * Size * channels];
        for (var column = 0; column < Size; column++)
            Filter8(temp.AsSpan(column * height * channels, height * channels), height, channels, coeffsY, offsetY, lengthY, result, column, Size * channels);
        return result;
    }

    public static ushort[] Resize16(int width, int height, int channels, RowReader<ushort> readRow)
    {
        var (coeffsX, offsetX, lengthX) = Weights16(Size, width / (double)Size);
        var (coeffsY, offsetY, lengthY) = Weights16(Size, height / (double)Size);

        var temp = new ushort[Size * height * channels];
        var row = new ushort[width * channels];
        for (var y = 0; y < height; y++)
        {
            readRow(y, row);
            Filter16(row, width, channels, coeffsX, offsetX, lengthX, temp, y, height * channels);
        }

        var result = new ushort[Size * Size * channels];
        for (var column = 0; column < Size; column++)
            Filter16(temp.AsSpan(column * height * channels, height * channels), height, channels, coeffsY, offsetY, lengthY, result, column, Size * channels);
        return result;
    }

    /// <summary>
    /// Filters one input line into output position <paramref name="position"/> of each of the
    /// <see cref="Size"/> output lines, which are <paramref name="outputStride"/> values apart.
    /// </summary>
    private static void Filter8(ReadOnlySpan<byte> line, int length, int channels, short[] coeffs, int[] offset, int filterLength,
        Span<byte> output, int position, int outputStride)
    {
        var maxX = length - 1;
        Span<int> sums = stackalloc int[4];
        for (var y = 0; y < Size; y++)
        {
            sums.Clear();
            var weight = 0;
            var start = offset[y];
            var ci = y * filterLength;
            for (var i = 0; i < filterLength; i++)
            {
                int coeff = coeffs[ci + i];
                if (coeff == 0)
                    continue;
                var xi = Clamp(start + i, maxX) * channels;
                for (var c = 0; c < channels; c++)
                    sums[c] += coeff * line[xi + c];
                weight += coeff;
            }

            var o = y * outputStride + position * channels;
            for (var c = 0; c < channels; c++)
                output[o + c] = ClampUint8(sums[c] / weight);
        }
    }

    private static void Filter16(ReadOnlySpan<ushort> line, int length, int channels, int[] coeffs, int[] offset, int filterLength,
        Span<ushort> output, int position, int outputStride)
    {
        var maxX = length - 1;
        Span<long> sums = stackalloc long[4];
        for (var y = 0; y < Size; y++)
        {
            sums.Clear();
            long weight = 0;
            var start = offset[y];
            var ci = y * filterLength;
            for (var i = 0; i < filterLength; i++)
            {
                long coeff = coeffs[ci + i];
                if (coeff == 0)
                    continue;
                var xi = Clamp(start + i, maxX) * channels;
                for (var c = 0; c < channels; c++)
                    sums[c] += coeff * line[xi + c];
                weight += coeff;
            }

            var o = y * outputStride + position * channels;
            for (var c = 0; c < channels; c++)
                output[o + c] = ClampUint16(sums[c] / weight);
        }
    }

    private static int Clamp(int xi, int maxX) => xi < 0 ? 0 : xi >= maxX ? maxX : xi;

    private static byte ClampUint8(int value) => (uint)value < 256 ? (byte)value : value > 255 ? (byte)255 : (byte)0;

    private static ushort ClampUint16(long value) => (ulong)value < 65536 ? (ushort)value : value > 65535 ? (ushort)65535 : (ushort)0;

    private static double Linear(double x)
    {
        x = Math.Abs(x);
        return x <= 1 ? 1 - x : 0;
    }

    // createWeights8 / createWeights16 with blur = 1 and the two-tap linear kernel.
    private static (short[] Coeffs, int[] Offset, int FilterLength) Weights8(int dy, double scale)
    {
        var (raw, offset, filterLength) = Weights(dy, scale);
        return (Array.ConvertAll(raw, weight => (short)(weight * 256)), offset, filterLength);
    }

    private static (int[] Coeffs, int[] Offset, int FilterLength) Weights16(int dy, double scale)
    {
        var (raw, offset, filterLength) = Weights(dy, scale);
        return (Array.ConvertAll(raw, weight => (int)(weight * 65536)), offset, filterLength);
    }

    private static (double[] Weights, int[] Offset, int FilterLength) Weights(int dy, double scale)
    {
        var filterLength = 2 * (int)Math.Max(Math.Ceiling(scale), 1);
        var filterFactor = Math.Min(1.0 / scale, 1);

        var weights = new double[dy * filterLength];
        var start = new int[dy];
        for (var y = 0; y < dy; y++)
        {
            var interpX = scale * (y + 0.5) - 0.5;
            start[y] = (int)interpX - filterLength / 2 + 1;
            interpX -= start[y];
            for (var i = 0; i < filterLength; i++)
                weights[y * filterLength + i] = Linear((interpX - i) * filterFactor);
        }
        return (weights, start, filterLength);
    }
}
