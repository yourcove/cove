using Cove.Api.Services;
using Cove.Core.DTOs;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Cove.Tests;

/// <summary>
/// The tagger's cover decision. Two covers that differ only by URL are the same picture, and only a
/// materially higher incoming resolution turns that sameness into something worth suggesting.
/// </summary>
public sealed class VideoCoverComparisonServiceTests
{
    private const string CoverHash = "f0e1d2c3b4a59687";
    // One bit away from CoverHash: the same picture through a different encoder.
    private const string NearlyCoverHash = "f0e1d2c3b4a59686";
    private const string OtherCoverHash = "0f1e2d3c4b5a6978";

    [Fact]
    public void Compare_SamePictureAtTheSameResolution_RecommendsNothing()
    {
        var comparison = VideoCoverComparisonService.Compare(
            new ImageSignature(CoverHash, 1280, 720, 240_000),
            new ImageSignature(NearlyCoverHash, 1280, 720, 310_000));

        Assert.Equal(VideoCoverComparisonVerdicts.Same, comparison.Verdict);
    }

    [Fact]
    public void Compare_SamePictureAtAHigherResolution_SuggestsTheUpgrade()
    {
        var comparison = VideoCoverComparisonService.Compare(
            new ImageSignature(CoverHash, 1280, 720, 240_000),
            new ImageSignature(NearlyCoverHash, 1920, 1080, 520_000));

        Assert.Equal(VideoCoverComparisonVerdicts.Upgrade, comparison.Verdict);
        Assert.Equal(1920, comparison.Candidate?.Width);
        Assert.Equal(720, comparison.Current?.Height);
    }

    [Fact]
    public void Compare_SamePictureBarelyLarger_RecommendsNothing()
    {
        // A re-encode a few percent wider is not an upgrade worth a person's attention.
        var comparison = VideoCoverComparisonService.Compare(
            new ImageSignature(CoverHash, 1280, 720, 240_000),
            new ImageSignature(CoverHash, 1320, 743, 250_000));

        Assert.Equal(VideoCoverComparisonVerdicts.Same, comparison.Verdict);
    }

    [Fact]
    public void Compare_SamePictureAtALowerResolution_RecommendsNothing()
    {
        var comparison = VideoCoverComparisonService.Compare(
            new ImageSignature(CoverHash, 1920, 1080, 520_000),
            new ImageSignature(NearlyCoverHash, 640, 360, 90_000));

        Assert.Equal(VideoCoverComparisonVerdicts.Same, comparison.Verdict);
    }

    [Fact]
    public void Compare_DifferentPictures_StaysAChoice()
    {
        var comparison = VideoCoverComparisonService.Compare(
            new ImageSignature(CoverHash, 1280, 720, 240_000),
            new ImageSignature(OtherCoverHash, 1920, 1080, 520_000));

        Assert.Equal(VideoCoverComparisonVerdicts.Differs, comparison.Verdict);
        Assert.True(comparison.Distance > VideoCoverComparisonService.SameCoverDistance);
    }

    [Fact]
    public void Compare_SamePictureInADifferentShape_StaysAChoice()
    {
        // A landscape cover and the portrait poster of the same artwork hash alike, because the hash
        // squares both before looking at them. The shape is what tells them apart.
        var comparison = VideoCoverComparisonService.Compare(
            new ImageSignature(CoverHash, 1280, 720, 240_000),
            new ImageSignature(NearlyCoverHash, 720, 1280, 240_000));

        Assert.Equal(VideoCoverComparisonVerdicts.Differs, comparison.Verdict);
    }

    [Fact]
    public void Compare_SamePictureLetterboxedWithinTolerance_IsStillTheSamePicture()
    {
        // 16:9 against 1.78:1 rounded to whole pixels is the same shape, not a reframing.
        var comparison = VideoCoverComparisonService.Compare(
            new ImageSignature(CoverHash, 1280, 720, 240_000),
            new ImageSignature(NearlyCoverHash, 1279, 720, 239_000));

        Assert.Equal(VideoCoverComparisonVerdicts.Same, comparison.Verdict);
    }

    [Fact]
    public void Compare_WithAnUnreadableImage_ReportsUnavailable()
    {
        var current = new ImageSignature(CoverHash, 1280, 720, 240_000);

        Assert.Equal(
            VideoCoverComparisonVerdicts.Unavailable,
            VideoCoverComparisonService.Compare(current, null).Verdict);
        Assert.Equal(
            VideoCoverComparisonVerdicts.Unavailable,
            VideoCoverComparisonService.Compare(null, current).Verdict);
    }
}

/// <summary>
/// The signature the cover decision is built on. The size must be the image's own, not the 64×64 the
/// hash reduces it to: were it ever the latter, every comparison would call itself the same picture,
/// and the cover would quietly stop being offered at all.
/// </summary>
public sealed class ImageSignatureTests
{
    [Fact]
    public void ComputeImageSignature_ReportsTheImageSizeRatherThanTheHashSize()
    {
        var signature = CreateFingerprintService().ComputeImageSignature(EncodePng(1920, 1080));

        Assert.NotNull(signature);
        Assert.Equal(1920, signature!.Width);
        Assert.Equal(1080, signature.Height);
        Assert.Equal(1920L * 1080, signature.PixelCount);
        Assert.Equal(16, signature.Phash.Length);
    }

    [Fact]
    public void ComputeImageSignature_HashesAResizedCopyOfOnePictureAlike()
    {
        var service = CreateFingerprintService();

        var large = service.ComputeImageSignature(EncodePng(800, 450));
        var small = service.ComputeImageSignature(EncodePng(400, 225));

        Assert.NotNull(large);
        Assert.NotNull(small);
        Assert.Equal(large!.Phash, small!.Phash);
    }

    [Fact]
    public void ComputeImageSignature_WithBytesThatAreNotAnImage_ReturnsNull()
    {
        Assert.Null(CreateFingerprintService().ComputeImageSignature([1, 2, 3, 4]));
        Assert.Null(CreateFingerprintService().ComputeImageSignature([]));
    }

    /// <summary>A deterministic gradient with a corner block, scaled to the requested size.</summary>
    private static byte[] EncodePng(int width, int height)
    {
        using var image = new SixLabors.ImageSharp.Image<Rgba32>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var shade = (byte)(255L * x / Math.Max(row.Length - 1, 1));
                    var corner = x < row.Length / 4 && y < accessor.Height / 4;
                    row[x] = corner ? new Rgba32(20, 20, 20) : new Rgba32(shade, (byte)(255 - shade), 128);
                }
            }
        });

        using var buffer = new MemoryStream();
        image.SaveAsPng(buffer);
        return buffer.ToArray();
    }

    // Signature computation reads nothing but the bytes and the logger; the scheduling collaborators
    // belong to the job-driven hashing paths and are never reached from here.
    private static FingerprintService CreateFingerprintService()
        => new(null!, null!, null!, null!, NullLogger<FingerprintService>.Instance);
}
