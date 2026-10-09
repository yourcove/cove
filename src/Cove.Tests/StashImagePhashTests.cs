using Cove.Api.Services;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

/// <summary>
/// Image pHashes against the values Stash computes. Each expected hash is the output of Stash's
/// <c>imagephash.Generate</c> (stash e7d33c9, Go 1.27.1) for the bytes
/// <see cref="StashImagePhashFixtures"/> produces, decoded with the image formats the Stash server
/// registers. The comment on each case names the Go image type that file decodes to, which selects
/// the nfnt/resize path. The sizes cover a downscale in one dimension with an upscale in the other
/// (130×41 and 23×170), and an image already 64×64, which nfnt/resize returns unchanged.
/// </summary>
public sealed class StashImagePhashTests
{
    [Theory]
    [InlineData("png_g1", 130, 41, "accc69169aa5b70d")] // *image.Gray
    [InlineData("png_g1", 64, 64, "accc69169aa5b70d")] // *image.Gray
    [InlineData("png_g1", 23, 170, "adcc6d169aa5360d")] // *image.Gray
    [InlineData("png_g2", 130, 41, "ecc56b1a53b9224d")] // *image.Gray
    [InlineData("png_g2", 64, 64, "ecc56b1853bb224d")] // *image.Gray
    [InlineData("png_g2", 23, 170, "e8c56b18db3b224d")] // *image.Gray
    [InlineData("png_g4", 130, 41, "ecc56b1a5639ce0c")] // *image.Gray
    [InlineData("png_g4", 64, 64, "ecc5691a563bce0c")] // *image.Gray
    [InlineData("png_g4", 23, 170, "ecc56b1a5623ce0d")] // *image.Gray
    [InlineData("png_g8", 130, 41, "ecc56b1a562bce0c")] // *image.Gray
    [InlineData("png_g8", 64, 64, "ecc56b1a562bce0c")] // *image.Gray
    [InlineData("png_g8", 23, 170, "ecc56b1a562bce0c")] // *image.Gray
    [InlineData("png_g16", 130, 41, "ecc56b1a562bce0c")] // *image.Gray16
    [InlineData("png_g16", 64, 64, "ecc56b1a562bce0c")] // *image.Gray16
    [InlineData("png_g16", 23, 170, "ecc56b1a562bce0c")] // *image.Gray16
    [InlineData("png_g4_trns", 130, 41, "f8c1cece253a59b0")] // *image.NRGBA
    [InlineData("png_g4_trns", 64, 64, "f8c1c6cf253259b8")] // *image.NRGBA
    [InlineData("png_g4_trns", 23, 170, "b8c1c3cf253a58bc")] // *image.NRGBA
    [InlineData("png_g8_trns", 130, 41, "b8c14fcec739381c")] // *image.NRGBA
    [InlineData("png_g8_trns", 64, 64, "b8c14fcec739388c")] // *image.NRGBA
    [InlineData("png_g8_trns", 23, 170, "b8d147c7c738391c")] // *image.NRGBA
    [InlineData("png_g16_trns", 130, 41, "b8c14fcec739381c")] // *image.NRGBA64
    [InlineData("png_g16_trns", 64, 64, "b8c14fcec739390c")] // *image.NRGBA64
    [InlineData("png_g16_trns", 23, 170, "b8d147c7c739381c")] // *image.NRGBA64
    [InlineData("png_ga8", 130, 41, "acc56b3a552b4e0d")] // *image.NRGBA
    [InlineData("png_ga8", 64, 64, "acc56b3a542b4e8e")] // *image.NRGBA
    [InlineData("png_ga8", 23, 170, "acc56b3a562b4a8d")] // *image.NRGBA
    [InlineData("png_ga16", 130, 41, "acc56b3a552b4e0d")] // *image.NRGBA64
    [InlineData("png_ga16", 64, 64, "acc56b3a542b4e8e")] // *image.NRGBA64
    [InlineData("png_ga16", 23, 170, "acc56b3a562b4a8d")] // *image.NRGBA64
    [InlineData("png_rgb8", 130, 41, "ecc56b1a562bce0c")] // *image.RGBA
    [InlineData("png_rgb8", 64, 64, "ecc56b1a562bce0c")] // *image.RGBA
    [InlineData("png_rgb8", 23, 170, "ecc56b1a562bce0c")] // *image.RGBA
    [InlineData("png_rgb16", 130, 41, "ecc56b1a562bce0c")] // *image.RGBA64
    [InlineData("png_rgb16", 64, 64, "ecc56b1a562bce0c")] // *image.RGBA64
    [InlineData("png_rgb16", 23, 170, "ecc56b1a562bce0c")] // *image.RGBA64
    [InlineData("png_rgb8_trns", 130, 41, "b8c14fcec739381c")] // *image.NRGBA
    [InlineData("png_rgb8_trns", 64, 64, "b8c14fcec739390c")] // *image.NRGBA
    [InlineData("png_rgb8_trns", 23, 170, "b8d147c7c739381c")] // *image.NRGBA
    [InlineData("png_rgb16_trns", 130, 41, "b8c14fcec739381c")] // *image.NRGBA64
    [InlineData("png_rgb16_trns", 64, 64, "b8c14fcec739390c")] // *image.NRGBA64
    [InlineData("png_rgb16_trns", 23, 170, "b8d147c7c739381c")] // *image.NRGBA64
    [InlineData("png_rgba8", 130, 41, "acc56b3a552b4e0d")] // *image.NRGBA
    [InlineData("png_rgba8", 64, 64, "acc56b3a542b4e8e")] // *image.NRGBA
    [InlineData("png_rgba8", 23, 170, "acc56b3a562b4e8c")] // *image.NRGBA
    [InlineData("png_rgba16", 130, 41, "acc56b3a552b4e0d")] // *image.NRGBA64
    [InlineData("png_rgba16", 64, 64, "acc56b3a542b4e8e")] // *image.NRGBA64
    [InlineData("png_rgba16", 23, 170, "acc56b3a562b4a8d")] // *image.NRGBA64
    [InlineData("png_p8", 130, 41, "ecc56b1a462dc798")] // *image.Paletted
    [InlineData("png_p8", 64, 64, "eec56b1a4629c798")] // *image.Paletted
    [InlineData("png_p8", 23, 170, "ecc56b1a462dc3d8")] // *image.Paletted
    [InlineData("png_p8_trns", 130, 41, "9ccd7b7a4419aa8c")] // *image.Paletted
    [InlineData("png_p8_trns", 64, 64, "9ccd7b7a4419aa8c")] // *image.Paletted
    [InlineData("png_p8_trns", 23, 170, "9ccd7b7a4518a88d")] // *image.Paletted
    [InlineData("png_p4", 130, 41, "d33bb4c86d1a8ae4")] // *image.Paletted
    [InlineData("png_p4", 64, 64, "d33bb4c86d1a8ae4")] // *image.Paletted
    [InlineData("png_p4", 23, 170, "d33b95c86d1a8ae4")] // *image.Paletted
    [InlineData("png_rgb8_adam7", 130, 41, "ecc56b1a562bce0c")] // *image.RGBA
    [InlineData("png_rgb8_adam7", 64, 64, "ecc56b1a562bce0c")] // *image.RGBA
    [InlineData("png_rgb8_adam7", 23, 170, "ecc56b1a562bce0c")] // *image.RGBA
    [InlineData("png_rgb8_solid", 130, 41, "8000000000000000")] // *image.RGBA
    [InlineData("png_rgb8_solid", 64, 64, "8000000000000000")] // *image.RGBA
    [InlineData("png_rgb8_solid", 23, 170, "8000000000000000")] // *image.RGBA
    [InlineData("gif_full", 130, 41, "ecc56b1a462dc798")] // *image.Paletted
    [InlineData("gif_full", 64, 64, "eec56b1a4629c798")] // *image.Paletted
    [InlineData("gif_full", 23, 170, "ecc56b1a462dc3d8")] // *image.Paletted
    [InlineData("gif_transparent", 130, 41, "b8c14fcec739311c")] // *image.Paletted
    [InlineData("gif_transparent", 64, 64, "b8c14fcec739309c")] // *image.Paletted
    [InlineData("gif_transparent", 23, 170, "b8c147c7c739389c")] // *image.Paletted
    [InlineData("gif_offset", 130, 41, "a4e47bdc5628603f")] // *image.Paletted
    [InlineData("gif_offset", 64, 64, "a5e47ad54e28683b")] // *image.Paletted
    [InlineData("gif_offset", 23, 170, "ade17ad54e2a4932")] // *image.Paletted
    [InlineData("bmp_24", 130, 41, "ecc56b1a562bce0c")] // *image.RGBA
    [InlineData("bmp_24", 64, 64, "ecc56b1a562bce0c")] // *image.RGBA
    [InlineData("bmp_24", 23, 170, "ecc56b1a562bce0c")] // *image.RGBA
    [InlineData("bmp_32_v3", 130, 41, "ecc56b1a562bce0c")] // *image.NRGBA
    [InlineData("bmp_32_v3", 64, 64, "ecc56b1a562bce0c")] // *image.NRGBA
    [InlineData("bmp_32_v3", 23, 170, "ecc56b1a562bce0c")] // *image.NRGBA
    [InlineData("bmp_32_v5", 130, 41, "acc56b3a552b4e0d")] // *image.NRGBA
    [InlineData("bmp_32_v5", 64, 64, "acc56b3a542b4e8e")] // *image.NRGBA
    [InlineData("bmp_32_v5", 23, 170, "acc56b3a562b4e8c")] // *image.NRGBA
    [InlineData("bmp_8", 130, 41, "ecc56b1a462dc798")] // *image.Paletted
    [InlineData("bmp_8", 64, 64, "eec56b1a4629c798")] // *image.Paletted
    [InlineData("bmp_8", 23, 170, "ecc56b1a462dc3d8")] // *image.Paletted
    [InlineData("webp_lossless", 130, 41, "ecc56b1a562bce0c")] // *image.NRGBA
    [InlineData("webp_lossless", 64, 64, "ecc56b1a562bce0c")] // *image.NRGBA
    [InlineData("webp_lossless", 23, 170, "ecc56b1a562bce0c")] // *image.NRGBA
    [InlineData("webp_lossless_alpha", 130, 41, "acc56b3a552b4e0d")] // *image.NRGBA
    [InlineData("webp_lossless_alpha", 64, 64, "acc56b3a542b4e8e")] // *image.NRGBA
    [InlineData("webp_lossless_alpha", 23, 170, "acc56b3a562b4e8c")] // *image.NRGBA
    [InlineData("tiff_rgb", 130, 41, "ecc56b1a562bce0c")] // *image.RGBA
    [InlineData("tiff_rgb", 64, 64, "ecc56b1a562bce0c")] // *image.RGBA
    [InlineData("tiff_rgb", 23, 170, "ecc56b1a562bce0c")] // *image.RGBA
    [InlineData("tiff_gray", 130, 41, "acc47b3a76334e0c")] // *image.Gray
    [InlineData("tiff_gray", 64, 64, "acc57b3a56334e0c")] // *image.Gray
    [InlineData("tiff_gray", 23, 170, "acc47b3a56334e8c")] // *image.Gray
    [InlineData("tiff_palette", 130, 41, "ecc56b1a5629ce0e")] // *image.Paletted
    [InlineData("tiff_palette", 64, 64, "ecc56b1a5629ce8c")] // *image.Paletted
    [InlineData("tiff_palette", 23, 170, "ecc56b1a57234e89")] // *image.Paletted
    [InlineData("tiff_palette16", 130, 41, "ecc56b1a462dc798")] // *image.Paletted
    [InlineData("tiff_palette16", 64, 64, "eec56b1a4629c798")] // *image.Paletted
    [InlineData("tiff_palette16", 23, 170, "ecc56b1a4629c7d8")] // *image.Paletted
    [InlineData("gif_frame64", 130, 100, "9cd96762669c9c98")] // *image.Paletted
    [InlineData("gif_frame64_t0", 130, 100, "98676763669c9c98")] // *image.Paletted
    [InlineData("gif_frame64", 100, 90, "9cce6161689e9e9c")] // *image.Paletted
    [InlineData("gif_frame64_t0", 100, 90, "9e446171699e9e9c")] // *image.Paletted
    [InlineData("png_g8_diagonal", 128, 128, "8a7ee84bf813f409")] // *image.Gray
    [InlineData("png_g8_diagonal", 130, 41, "8a3890668a76fb3e")] // *image.Gray
    [InlineData("png_g8_box", 64, 64, "9866679898676766")] // *image.Gray
    [InlineData("png_g8_box", 128, 128, "9867679898676726")] // *image.Gray
    [InlineData("png_g8_box", 100, 100, "9867679898676726")] // *image.Gray
    public void LosslessImage_HashesExactlyAsStashDoes(string kind, int width, int height, string expected)
    {
        Assert.Equal(expected, Hash(StashImagePhashFixtures.Create(kind, width, height)));
    }

    /// <summary>
    /// Lossy JPEG and WebP decode to slightly different pixels in ImageSharp than in Go, so their
    /// hashes are close to Stash's rather than equal. Against Go, eleven of these cases match and four
    /// differ by two bits; the bound leaves room for ImageSharp's encoder to change the fixture bytes.
    /// </summary>
    [Theory]
    [InlineData("jpg_420", 130, 41, "ecc56b1a562bce0c")] // *image.YCbCr
    [InlineData("jpg_420", 64, 64, "ecc56b1a562bce0c")] // *image.YCbCr
    [InlineData("jpg_420", 23, 170, "ecc56b1a562b4e0d")] // *image.YCbCr
    [InlineData("jpg_444", 130, 41, "ecc56b1a562bce0c")] // *image.YCbCr
    [InlineData("jpg_444", 64, 64, "ecc56b1a562bce0c")] // *image.YCbCr
    [InlineData("jpg_444", 23, 170, "ecc56b1a562b4e0d")] // *image.YCbCr
    [InlineData("jpg_gray", 130, 41, "ecc56b1a562bce0c")] // *image.Gray
    [InlineData("jpg_gray", 64, 64, "ecc56b1a562bce0c")] // *image.Gray
    [InlineData("jpg_gray", 23, 170, "ecc56b1a562bce0c")] // *image.Gray
    [InlineData("webp_lossy", 130, 41, "ecc56b1a562bce0c")] // *image.YCbCr
    [InlineData("webp_lossy", 64, 64, "ecc56b1a562bce0c")] // *image.YCbCr
    [InlineData("webp_lossy", 23, 170, "ecc56b1a562ace8c")] // *image.YCbCr
    [InlineData("webp_lossy_alpha", 130, 41, "acc56b3a542b4e8d")] // *image.NYCbCrA
    [InlineData("webp_lossy_alpha", 64, 64, "acc56b3a542b4e8e")] // *image.NYCbCrA
    [InlineData("webp_lossy_alpha", 23, 170, "a8c56b3a572b4e8c")] // *image.NYCbCrA
    public void LossyImage_HashesCloseToStash(string kind, int width, int height, string expected)
    {
        var actual = Hash(StashImagePhashFixtures.Create(kind, width, height));

        Assert.InRange(MetadataServerService.ComputePhashHammingDistance(expected, actual), 0, 4);
    }

    [Fact]
    public void ComputeImageSignature_ReportsTheEncodedSize()
    {
        var data = StashImagePhashFixtures.Create("gif_offset", 130, 41);

        var signature = CreateService().ComputeImageSignature(data);

        // The hash covers only the first frame, but the size is the GIF's logical screen.
        Assert.Equal(new ImageSignature("a4e47bdc5628603f", 130, 41, data.Length), signature);
    }

    [Fact]
    public async Task ComputeImagePhashAsync_Cancelled_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cove-phash-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, StashImagePhashFixtures.Create("png_rgb8", 130, 41), TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => CreateService().ComputeImagePhashAsync(path, new CancellationToken(canceled: true)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadGifFirstFrameBounds_SkipsExtensionsBeforeTheFrame()
    {
        // gif_transparent puts a graphic control extension ahead of the image descriptor.
        using var stream = new MemoryStream(StashImagePhashFixtures.Create("gif_transparent", 130, 41));

        Assert.Equal(new SixLabors.ImageSharp.Rectangle(0, 0, 130, 41), StashImagePhash.ReadGifFirstFrameBounds(stream));
    }

    [Theory]
    [InlineData(5)]     // inside the logical screen descriptor
    [InlineData(400)]   // inside the global colour table
    [InlineData(785)]   // inside the graphic control extension
    [InlineData(795)]   // inside the image descriptor
    public void ReadGifFirstFrameBounds_TruncatedFile_ReturnsNull(int length)
    {
        var data = StashImagePhashFixtures.Create("gif_transparent", 130, 41);
        using var stream = new MemoryStream(data, 0, length);

        Assert.Null(StashImagePhash.ReadGifFirstFrameBounds(stream));
    }

    [Fact]
    public void ReadGifFirstFrameBounds_TrailerOnly_ReturnsNull()
    {
        byte[] data = [.. "GIF89a"u8, 1, 0, 1, 0, 0, 0, 0, 0x3B];
        using var stream = new MemoryStream(data);

        Assert.Null(StashImagePhash.ReadGifFirstFrameBounds(stream));
    }

    [Fact]
    public void ReadPngColorKey16_ReadsTheKeySamples()
    {
        using var stream = new MemoryStream(StashImagePhashFixtures.Create("png_rgb16_trns", 130, 41));

        Assert.Equal(3, StashImagePhash.ReadPngColorKey16(stream)?.Length);
    }

    [Fact]
    public void ReadPngColorKey16_OversizedChunkLength_ReturnsNull()
    {
        byte[] data = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x7F, 0xFF, 0xFF, 0xFF, .. "tEXt"u8, 0, 0, 0, 0];
        using var stream = new MemoryStream(data);

        Assert.Null(StashImagePhash.ReadPngColorKey16(stream));
    }

    private static FingerprintService CreateService()
        => new(null!, null!, new CoveConfiguration(), null!, NullLogger<FingerprintService>.Instance);

    private static string Hash(byte[] data)
    {
        using var stream = new MemoryStream(data);
        return GoImageHash.Format(StashImagePhash.Compute(stream).Hash);
    }
}
