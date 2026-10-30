using Cove.Api.Services;

namespace Cove.Tests;

/// <summary>
/// The truncation check exists to skip work that cannot succeed. Its cost of being wrong is
/// asymmetric: flagging a complete video silently denies it every generated asset, while missing an
/// incomplete one only means generation fails the slow way it always did. These tests pin that bias.
/// </summary>
public class VideoSourceHealthTests
{
    private const long Mb = 1024 * 1024;

    [Fact]
    public void CompleteFileIsReadable()
    {
        // 10 minutes at 8 Mbps ≈ 600 MB on disk.
        var verdict = VideoSourceHealth.Classify(fileSize: 600 * Mb, declaredDuration: 600, streamBitRate: 8_000_000);
        Assert.True(verdict.IsReadable);
        Assert.Null(verdict.Reason);
    }

    [Fact]
    public void TruncatedDownloadIsFlagged()
    {
        // The real shape of the failures in this library: the container still claims 20 minutes of
        // 4K VR while holding 9 MB.
        var verdict = VideoSourceHealth.Classify(fileSize: 9 * Mb, declaredDuration: 1231, streamBitRate: 10_000_000);
        Assert.False(verdict.IsReadable);
        Assert.NotNull(verdict.Reason);
        Assert.Contains("incomplete", verdict.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A complete file's container average runs slightly ABOVE the video stream's rate, because it
    /// also carries audio and container overhead. Ratios at or just under 1 must stay readable.
    /// </summary>
    [Theory]
    [InlineData(1.002)]   // lowest ratio seen across 400 complete files in the measured library
    [InlineData(1.0)]
    [InlineData(0.9)]
    [InlineData(0.6)]
    [InlineData(0.51)]
    public void RatiosNearOrAboveOneAreReadable(double ratio)
    {
        const long streamBitRate = 8_000_000;
        const double duration = 600;
        var size = (long)(streamBitRate * duration * ratio / 8d);

        Assert.True(VideoSourceHealth.Classify(size, duration, streamBitRate).IsReadable);
    }

    [Theory]
    [InlineData(0.45)]
    [InlineData(0.1)]
    [InlineData(0.02)]    // median of the known-broken files
    public void RatiosWellBelowOneAreFlagged(double ratio)
    {
        const long streamBitRate = 8_000_000;
        const double duration = 600;
        var size = (long)(streamBitRate * duration * ratio / 8d);

        Assert.False(VideoSourceHealth.Classify(size, duration, streamBitRate).IsReadable);
    }

    /// <summary>
    /// Missing inputs mean the check cannot form an opinion, and "no opinion" must mean "generate
    /// normally" — never a skip.
    /// </summary>
    [Theory]
    [InlineData(0, 600, 8_000_000)]      // unknown size
    [InlineData(1000, 0, 8_000_000)]     // unknown duration
    [InlineData(1000, 600, 0)]           // stream bitrate not reported
    public void InconclusiveInputsAreTreatedAsReadable(long size, double duration, long streamBitRate)
    {
        Assert.True(VideoSourceHealth.Classify(size, duration, streamBitRate).IsReadable);
    }

    [Fact]
    public void ReasonNamesTheActualAndExpectedSize()
    {
        var verdict = VideoSourceHealth.Classify(fileSize: 9 * Mb, declaredDuration: 1231, streamBitRate: 10_000_000);
        Assert.Contains("9 MB", verdict.Reason!);
        Assert.Contains("GB", verdict.Reason!);          // ~1.4 GB expected
        Assert.Contains("Re-download", verdict.Reason!);
    }

    [Fact]
    public void ProbeJsonYieldsDurationAndVideoStreamBitrate()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "audio", "bit_rate": "128000" },
            { "codec_type": "video", "bit_rate": "9969000" }
          ],
          "format": { "duration": "945.030000", "bit_rate": "6579000" }
        }
        """;

        Assert.True(VideoSourceHealthProbe.TryReadProbe(json, out var duration, out var bitRate));
        Assert.Equal(945.03, duration, 2);
        // The VIDEO stream's rate, not the container average and not the audio stream.
        Assert.Equal(9_969_000, bitRate);
    }

    [Fact]
    public void ProbeJsonWithoutAVideoStreamBitrateIsInconclusive()
    {
        const string json = """
        { "streams": [ { "codec_type": "video" } ], "format": { "duration": "945.0" } }
        """;

        Assert.False(VideoSourceHealthProbe.TryReadProbe(json, out _, out _));
    }

    [Fact]
    public void TruncatedMpegDeclaringARealRateIsStillCaught()
    {
        // The sentinel guard must narrow the check, not disable it for MPEG. A .mpg that declares
        // a genuine rate still has to be judged on its average.
        const string json = """
        {
          "streams": [ { "codec_type": "video", "bit_rate": "800000" } ],
          "format": { "duration": "1478.668000" }
        }
        """;

        Assert.True(VideoSourceHealthProbe.TryReadProbe(json, out var duration, out var bitRate));
        Assert.Equal(800_000, bitRate);
        // 9 MB of a file whose 1478s at 800 kbps implies about 148 MB.
        Assert.False(VideoSourceHealth.Classify(9 * Mb, duration, bitRate).IsReadable);
    }

    [Fact]
    public void MpegVariableBitRateSentinelIsInconclusive()
    {
        // 262143 * 400 is MPEG-1's all-ones sequence-header bitrate, meaning "variable", which a
        // complete 900 kbps .mpg declares just as a truncated one would.
        const string json = """
        {
          "streams": [ { "codec_type": "video", "bit_rate": "104857200" } ],
          "format": { "duration": "618.528000" }
        }
        """;

        Assert.False(VideoSourceHealthProbe.TryReadProbe(json, out _, out _));
    }
}
