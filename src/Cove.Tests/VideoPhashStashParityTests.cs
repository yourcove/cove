using System.Globalization;
using Cove.Api.Services;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace Cove.Tests;

/// <summary>
/// Cove extracts pHash frames in batches of seeks per ffmpeg process; Stash's phasher runs one
/// ffmpeg per frame. A video pHash only matches Stash's if both see the same 25 frames with the
/// same pixels, so this rebuilds Stash's extraction from its own arguments (videophash.go and
/// transcoder.ScreenshotTime) and requires the identical hash.
/// </summary>
public sealed class VideoPhashStashParityTests
{
    [Theory]
    // Unrounded durations: Stash samples from the duration rounded to hundredths of a second.
    [InlineData("testsrc2=size=640x480:rate=25", 30.0333)]
    // An odd scaled height, which scale=160:-2 rounds to an even one.
    [InlineData("mandelbrot=size=410x300:rate=30", 21.0571)]
    // Every other frame inverted, and a duration (10.06 s once rounded) that puts the ninth sample at
    // 3.40028 s, 0.28 ms past the frame that starts at 3.400 s. A seek written to the millisecond (3.400)
    // lands on that inverted frame instead of the next one.
    [InlineData("testsrc2=size=320x240:rate=25,negate=enable='mod(n,2)'", 10.0612)]
    public async Task ComputeVideoPhash_MatchesStashsOneFramePerProcessExtraction(string source, double duration)
    {
        var ffmpeg = FfmpegExecutableLocator.FindFfmpeg((string?)null);
        Assert.SkipWhen(ffmpeg is null, "Requires ffmpeg on PATH.");

        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"cove-phash-video-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var video = await MakeClipAsync(ffmpeg!, source, duration, Path.Combine(root, "clip.mov"), ct);
            var cove = await CreateService().ComputeVideoPhashAsync(video, duration, ct);

            Assert.Equal(await StashPhashAsync(ffmpeg!, video, duration, root, ct), cove);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// ffmpeg writes a source with an alpha channel as a 32-bit BMP with a BITMAPINFOHEADER, and Go's
    /// BMP decoder ignores that header's alpha byte, so Stash hashes such a video as if it were opaque.
    /// The alpha here varies across the frame, so keeping it would change the hash. Both sides read the
    /// same 32-bit frames, so the comparison does not depend on how ffmpeg converts opaque sources.
    /// </summary>
    [Fact]
    public async Task ComputeVideoPhash_IgnoresTheAlphaChannelAsStashDoes()
    {
        var ffmpeg = FfmpegExecutableLocator.FindFfmpeg((string?)null);
        Assert.SkipWhen(ffmpeg is null, "Requires ffmpeg on PATH.");

        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"cove-phash-alpha-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            const double duration = 10.0333;
            var translucent = await MakeClipAsync(ffmpeg!,
                "testsrc2=size=320x240:rate=25,format=yuva420p,geq=lum='lum(X,Y)':cb='cb(X,Y)':cr='cr(X,Y)':a='255*X/W'",
                duration, Path.Combine(root, "translucent.mov"), ct);

            var cove = await CreateService().ComputeVideoPhashAsync(translucent, duration, ct);

            Assert.Equal(await StashPhashAsync(ffmpeg!, translucent, duration, root, ct), cove);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static FingerprintService CreateService()
    {
        var config = new CoveConfiguration();
        return new FingerprintService(null!, null!, config, new FfmpegConcurrencyLimiter(config), NullLogger<FingerprintService>.Instance);
    }

    // FFV1 is lossless, so the decoded frames do not depend on an encoder's version. QuickTime keeps
    // timestamps finer than a millisecond; Matroska rounds them to the millisecond, which would hide a
    // seek that differs by less than that.
    private static async Task<string> MakeClipAsync(string ffmpeg, string source, double duration, string path, CancellationToken ct)
    {
        var make = await FfmpegProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-v", "error", "-f", "lavfi", "-i", source,
             "-t", duration.ToString(CultureInfo.InvariantCulture), "-c:v", "ffv1", path],
            TimeSpan.FromMinutes(1), ct);
        Assert.True(make.ExitCode == 0, make.StandardError);
        return path;
    }

    private static async Task<string> StashPhashAsync(string ffmpeg, string video, double duration, string root, CancellationToken ct)
    {
        var rounded = Math.Round(duration * 100, MidpointRounding.AwayFromZero) / 100;
        var offset = 0.05 * rounded;
        var step = 0.9 * rounded / 25;
        var frames = new List<Image<Rgba32>>();
        try
        {
            for (var i = 0; i < 25; i++)
            {
                var output = Path.Combine(root, $"stash_{i:D2}.bmp");
                var seek = (offset + i * step).ToString("R", CultureInfo.InvariantCulture);
                var result = await FfmpegProcessRunner.RunAsync(ffmpeg,
                    ["-v", "error", "-y", "-ss", seek, "-i", video, "-frames:v", "1", "-vf", "scale=160:-2", "-c:v", "bmp", output],
                    TimeSpan.FromMinutes(1), ct);
                Assert.Equal(0, result.ExitCode);
                frames.Add(await Image.LoadAsync<Rgba32>(output, ct));
            }

            // imaging.Paste of each decoded frame into the 5×5 montage. Go's BMP decoder returns these
            // frames opaque, whatever their alpha byte holds.
            int width = frames[0].Width, height = frames[0].Height;
            using var sprite = new Image<Rgba32>(width * 5, height * 5);
            for (var i = 0; i < frames.Count; i++)
                for (var y = 0; y < height; y++)
                {
                    var source = frames[i].Frames.RootFrame.DangerousGetPixelRowMemory(y).Span;
                    var target = sprite.Frames.RootFrame.DangerousGetPixelRowMemory(height * (i / 5) + y).Span[(width * (i % 5))..];
                    for (var x = 0; x < width; x++)
                        target[x] = source[x] with { A = 255 };
                }
            return GoImageHash.Format(StashImagePhash.ComputeNrgba(sprite));
        }
        finally
        {
            foreach (var frame in frames)
                frame.Dispose();
        }
    }
}
