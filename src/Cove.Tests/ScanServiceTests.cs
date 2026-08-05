using Cove.Core.Events;
using Cove.Api.Services;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Core.Entities.Galleries.Zip;
using Cove.Data;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;

namespace Cove.Tests;

public class ScanServiceTests
{
    private const string TinyValidMp4Base64 = "AAAAIGZ0eXBpc29tAAACAGlzb21pc28yYXZjMW1wNDEAAAN1bW9vdgAAAGxtdmhkAAAAAAAAAAAAAAAAAAAD6AAAAMgAAQAAAQAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgAAAp90cmFrAAAAXHRraGQAAAADAAAAAAAAAAAAAAABAAAAAAAAAMgAAAAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAABAAAAAABAAAAAQAAAAAAAkZWR0cwAAABxlbHN0AAAAAAAAAAEAAADIAAAEAAABAAAAAAIXbWRpYQAAACBtZGhkAAAAAAAAAAAAAAAAAAAyAAAACgBVxAAAAAAALWhkbHIAAAAAAAAAAHZpZGUAAAAAAAAAAAAAAABWaWRlb0hhbmRsZXIAAAABwm1pbmYAAAAUdm1oZAAAAAEAAAAAAAAAAAAAACRkaW5mAAAAHGRyZWYAAAAAAAAAAQAAAAx1cmwgAAAAAQAAAYJzdGJsAAAAvnN0c2QAAAAAAAAAAQAAAK5hdmMxAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAABAAEABIAAAASAAAAAAAAAABFUxhdmM1OS4zNy4xMDAgbGlieDI2NAAAAAAAAAAAAAAAGP//AAAANGF2Y0MBZAAK/+EAF2dkAAqs2V7ARAAAAwAEAAADAMg8SJZYAQAGaOvjyyLA/fj4AAAAABBwYXNwAAAAAQAAAAEAAAAUYnRydAAAAAAAAHZIAAB2SAAAABhzdHRzAAAAAAAAAAEAAAAFAAACAAAAABRzdHNzAAAAAAAAAAEAAAABAAAAOGN0dHMAAAAAAAAABQAAAAEAAAQAAAAAAQAACgAAAAABAAAEAAAAAAEAAAAAAAAAAQAAAgAAAAAcc3RzYwAAAAAAAAABAAAAAQAAAAUAAAABAAAAKHN0c3oAAAAAAAAAAAAAAAUAAALFAAAADAAAAAwAAAAMAAAADAAAABRzdGNvAAAAAAAAAAEAAAOlAAAAYnVkdGEAAABabWV0YQAAAAAAAAAhaGRscgAAAAAAAAAAbWRpcmFwcGwAAAAAAAAAAAAAAAAtaWxzdAAAACWpdG9vAAAAHWRhdGEAAAABAAAAAExhdmY1OS4yNy4xMDAAAAAIZnJlZQAAAv1tZGF0AAACrgYF//+q3EXpvebZSLeWLNgg2SPu73gyNjQgLSBjb3JlIDE2NCByMzA5NSBiYWVlNDAwIC0gSC4yNjQvTVBFRy00IEFWQyBjb2RlYyAtIENvcHlsZWZ0IDIwMDMtMjAyMiAtIGh0dHA6Ly93d3cudmlkZW9sYW4ub3JnL3gyNjQuaHRtbCAtIG9wdGlvbnM6IGNhYmFjPTEgcmVmPTMgZGVibG9jaz0xOjA6MCBhbmFseXNlPTB4MzoweDExMyBtZT1oZXggc3VibWU9NyBwc3k9MSBwc3lfcmQ9MS4wMDowLjAwIG1peGVkX3JlZj0xIG1lX3JhbmdlPTE2IGNocm9tYV9tZT0xIHRyZWxsaXM9MSA4eDhkY3Q9MSBjcW09MCBkZWFkem9uZT0yMSwxMSBmYXN0X3Bza2lwPTEgY2hyb21hX3FwX29mZnNldD0tMiB0aHJlYWRzPTEgbG9va2FoZWFkX3RocmVhZHM9MSBzbGljZWRfdGhyZWFkcz0wIG5yPTAgZGVjaW1hdGU9MSBpbnRlcmxhY2VkPTAgYmx1cmF5X2NvbXBhdD0wIGNvbnN0cmFpbmVkX2ludHJhPTAgYmZyYW1lcz0zIGJfcHlyYW1pZD0yIGJfYWRhcHQ9MSBiX2JpYXM9MCBkaXJlY3Q9MSB3ZWlnaHRiPTEgb3Blbl9nb3A9MCB3ZWlnaHRwPTIga2V5aW50PTI1MCBrZXlpbnRfbWluPTI1IHNjZW5lY3V0PTQwIGludHJhX3JlZnJlc2g9MCByY19sb29rYWhlYWQ9NDAgcmM9Y3JmIG1idHJlZT0xIGNyZj0yMy4wIHFjb21wPTAuNjAgcXBtaW49MCBxcG1heD02OSBxcHN0ZXA9NCBpcF9yYXRpbz0xLjQwIGFxPTE6MS4wMACAAAAAD2WIhAAz//727L4FNhTIwQAAAAhBmiRsQr/+wAAAAAhBnkJ4hf/BgQAAAAgBnmF0Qr/EgAAAAAgBnmNqQr/EgQ==";

    private const string ValidVideoProbeJson = """
        {
          "format": { "duration": "0.2", "bit_rate": "100000" },
          "streams": [
            { "codec_type": "video", "codec_name": "h264", "width": 16, "height": 16, "r_frame_rate": "25/1" }
          ]
        }
        """;
    private const string ValidAudioProbeJson = """
        {
          "format": { "duration": "12.5", "bit_rate": "192000", "tags": { "title": "Track" } },
          "streams": [
            { "codec_type": "audio", "codec_name": "mp3", "sample_rate": "44100", "channels": 2 }
          ]
        }
        """;

    private static readonly TimeProvider ReadyTimeProvider = new OffsetTimeProvider(TimeSpan.FromMinutes(1));
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4" };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg" };
    private static readonly HashSet<string> GalleryExtensions = new(StringComparer.OrdinalIgnoreCase) { ".zip" };
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp3" };
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase) { ".epub" };

    [Fact]
    public void NeedsVideoMetadataProbe_ReturnsTrueWhenDurationIsMissing()
    {
        var videoFile = new VideoFile
        {
            Width = 1920,
            Height = 1080,
            Duration = 0,
        };

        Assert.True(ScanService.NeedsVideoMetadataProbe(videoFile));
    }

    [Fact]
    public void NeedsVideoMetadataProbe_ReturnsTrueWhenDimensionsAreMissing()
    {
        var videoFile = new VideoFile
        {
            Width = 0,
            Height = 1080,
            Duration = 307.9,
        };

        Assert.True(ScanService.NeedsVideoMetadataProbe(videoFile));
    }

    [Fact]
    public void NeedsVideoMetadataProbe_ReturnsFalseWhenCoreVideoMetricsExist()
    {
        var videoFile = new VideoFile
        {
            Width = 1920,
            Height = 1080,
            Duration = 307.9,
        };

        Assert.False(ScanService.NeedsVideoMetadataProbe(videoFile));
    }

    [Fact]
    public void DidFileChangeDuringProbe_ReturnsTrueWhenCopyAdvanced()
    {
        var discoveredModTime = new DateTime(2026, 8, 2, 9, 41, 8, DateTimeKind.Utc);

        Assert.True(ScanFileValidator.DidFileChangeDuringValidation(
            discoveredSize: 368_050_176,
            discoveredModTime,
            currentSize: 2_461_499_588,
            currentModTime: discoveredModTime.AddSeconds(10)));
    }

    [Fact]
    public void DidFileChangeDuringProbe_ReturnsFalseWhenFileStayedStable()
    {
        var modTime = new DateTime(2026, 8, 2, 9, 41, 8, DateTimeKind.Utc);

        Assert.False(ScanFileValidator.DidFileChangeDuringValidation(
            discoveredSize: 2_461_499_588,
            modTime,
            currentSize: 2_461_499_588,
            currentModTime: modTime));
    }

    [Fact]
    public void DidFileChangeDuringProbe_DetectsSameSecondModification()
    {
        var discoveredModTime = new DateTime(2026, 8, 2, 9, 41, 8, 100, DateTimeKind.Utc);

        Assert.True(ScanFileValidator.DidFileChangeDuringValidation(
            discoveredSize: 1_000,
            discoveredModTime,
            currentSize: 1_000,
            currentModTime: discoveredModTime.AddMilliseconds(500)));
    }

    [Fact]
    public void IsFileQuiet_RequiresTheFullQuietPeriod()
    {
        var now = new DateTime(2026, 8, 2, 9, 41, 10, DateTimeKind.Utc);

        Assert.False(ScanFileValidator.IsFileQuiet(now - ScanFileValidator.FileQuietPeriod + TimeSpan.FromMilliseconds(1), now));
        Assert.True(ScanFileValidator.IsFileQuiet(now - ScanFileValidator.FileQuietPeriod, now));
        Assert.True(ScanFileValidator.IsFileQuiet(now.AddMinutes(5), now));
    }

    [Fact]
    public async Task ValidateDeclaredContainerLengthAsync_RejectsTruncatedMp4BoxWithoutReadingPayload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cove-truncated-{Guid.NewGuid():N}.mp4");
        try
        {
            // Declares a 32-byte ftyp box but provides only its 8-byte header plus four payload bytes.
            await File.WriteAllBytesAsync(path, [0, 0, 0, 32, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 1, 2, 3, 4]);

            var failure = await ScanFileValidator.ValidateDeclaredContainerLengthAsync(path, CancellationToken.None);

            Assert.Contains("shorter than a declared box", failure);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ValidateDeclaredContainerLengthAsync_RejectsPathologicalIsoBoxCounts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cove-many-boxes-{Guid.NewGuid():N}.mp4");
        try
        {
            var bytes = new byte[4_097 * 8];
            for (var offset = 0; offset < bytes.Length; offset += 8)
            {
                bytes[offset + 3] = 8;
                bytes[offset + 4] = (byte)'f';
                bytes[offset + 5] = (byte)'r';
                bytes[offset + 6] = (byte)'e';
                bytes[offset + 7] = (byte)'e';
            }
            await File.WriteAllBytesAsync(path, bytes);

            var failure = await ScanFileValidator.ValidateDeclaredContainerLengthAsync(path, CancellationToken.None);

            Assert.Contains("more than 4,096 top-level boxes", failure);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ValidateAsync_AcceptsLargeSvgWithBoundedHeaderAndTailChecks()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cove-large-{Guid.NewGuid():N}.svg");
        try
        {
            await File.WriteAllTextAsync(path, $"<svg xmlns=\"http://www.w3.org/2000/svg\"><text>{new string('x', 256 * 1024)}</text></svg>");
            var probe = new StubMediaProbeService(MediaProbeResult.Succeeded(ValidVideoProbeJson));
            var validator = new ScanFileValidator(probe, new ZipGalleryReader(new ZipFileReader()), ReadyTimeProvider);
            var info = new FileInfo(path);

            var result = await validator.ValidateAsync(path, info.Length, info.LastWriteTimeUtc, ScanMediaKind.Image);

            Assert.Equal(ScanFileValidationStatus.Ready, result.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsMediaTypeExcludedByScanTarget_ReturnsTrueForGalleryArchiveWhenImagesAreExcluded()
    {
        Assert.True(ScanService.IsMediaTypeExcludedByScanTarget(
            ".zip",
            excludeVideo: false,
            excludeImage: true,
            excludeAudio: false,
            excludeText: false,
            VideoExtensions,
            ImageExtensions,
            GalleryExtensions,
            AudioExtensions,
            TextExtensions));
    }

    [Fact]
    public void IsMediaTypeExcludedByScanTarget_ReturnsTrueForTextsWhenTextsAreExcluded()
    {
        Assert.True(ScanService.IsMediaTypeExcludedByScanTarget(
            ".epub",
            excludeVideo: false,
            excludeImage: false,
            excludeAudio: false,
            excludeText: true,
            VideoExtensions,
            ImageExtensions,
            GalleryExtensions,
            AudioExtensions,
            TextExtensions));
    }

    [Fact]
    public void IsMediaTypeExcludedByScanTarget_ReturnsFalseForAllowedMediaTypes()
    {
        Assert.False(ScanService.IsMediaTypeExcludedByScanTarget(
            ".zip",
            excludeVideo: false,
            excludeImage: false,
            excludeAudio: false,
            excludeText: false,
            VideoExtensions,
            ImageExtensions,
            GalleryExtensions,
            AudioExtensions,
            TextExtensions));
    }

    [Fact]
    public async Task StartScan_SkipsZeroByteVideoWithoutCreatingLibraryRecord()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(tempRoot, "copying.mp4"), []);
            await using var environment = await CreateBareEnvironmentAsync(tempRoot);

            environment.Service.StartScan();

            await using var scope = environment.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            Assert.Empty(await db.Videos.ToListAsync());
            Assert.Empty(await db.VideoFiles.ToListAsync());
            Assert.Contains("1 unsettled file deferred", environment.JobService.LatestSubTask);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_SkipsVideoRejectedByFfprobeWithoutCreatingLibraryRecord()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            await WriteValidVideoAsync(Path.Combine(tempRoot, "rejected.mp4"));
            await using var environment = await CreateBareEnvironmentAsync(
                tempRoot,
                new StubMediaProbeService(MediaProbeResult.Rejected("invalid media data")));

            environment.Service.StartScan();

            await using var scope = environment.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            Assert.Empty(await db.Videos.ToListAsync());
            Assert.Empty(await db.VideoFiles.ToListAsync());
            Assert.Contains("1 invalid media file skipped", environment.JobService.LatestSubTask);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_DefersRecentlyModifiedFileWithoutRunningProbeThenImportsItLater()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var videoPath = Path.Combine(tempRoot, "recent.mp4");
            await WriteValidVideoAsync(videoPath);
            var probe = new StubMediaProbeService(MediaProbeResult.Succeeded(ValidVideoProbeJson));
            await using var environment = await CreateBareEnvironmentAsync(tempRoot, probe, TimeProvider.System);

            environment.Service.StartScan();

            await using (var scope = environment.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                Assert.Empty(await db.VideoFiles.ToListAsync());
            }
            Assert.Equal(0, probe.CallCount);
            Assert.Contains("1 unsettled file deferred", environment.JobService.LatestSubTask);

            File.SetLastWriteTimeUtc(videoPath, DateTime.UtcNow.Subtract(ScanFileValidator.FileQuietPeriod).AddSeconds(-1));
            environment.Service.StartScan();

            await using var verificationScope = environment.Services.CreateAsyncScope();
            var verificationDb = verificationScope.ServiceProvider.GetRequiredService<CoveContext>();
            Assert.Single(await verificationDb.VideoFiles.ToListAsync());
            Assert.Equal(1, probe.CallCount);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_ReportsUnavailableProbeAsFileFailureRatherThanInvalidMedia()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            await WriteValidVideoAsync(Path.Combine(tempRoot, "valid.mp4"));
            var probe = new StubMediaProbeService(MediaProbeResult.ToolUnavailable("FFprobe is unavailable"));
            await using var environment = await CreateBareEnvironmentAsync(tempRoot, probe);

            environment.Service.StartScan();

            await using var scope = environment.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            Assert.Empty(await db.VideoFiles.ToListAsync());
            Assert.Contains("0 invalid media files skipped", environment.JobService.LatestSubTask);
            Assert.Contains("1 file failure", environment.JobService.LatestSubTask);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_DefersFileThatChangesDuringProbeAndImportsItOnRetry()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var videoPath = Path.Combine(tempRoot, "changing.mp4");
            await WriteValidVideoAsync(videoPath);
            var mutated = 0;
            var probe = new StubMediaProbeService(
                MediaProbeResult.Succeeded(ValidVideoProbeJson),
                path =>
                {
                    if (Interlocked.Exchange(ref mutated, 1) != 0)
                        return;

                    using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    stream.Position = stream.Length - 1;
                    var original = stream.ReadByte();
                    stream.Position = stream.Length - 1;
                    stream.WriteByte((byte)(original ^ 1));
                });
            await using var environment = await CreateBareEnvironmentAsync(tempRoot, probe);

            environment.Service.StartScan();

            await using (var scope = environment.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                Assert.Empty(await db.VideoFiles.ToListAsync());
            }
            Assert.Contains("1 unsettled file deferred", environment.JobService.LatestSubTask);

            environment.Service.StartScan();

            await using var verificationScope = environment.Services.CreateAsyncScope();
            var verificationDb = verificationScope.ServiceProvider.GetRequiredService<CoveContext>();
            Assert.Single(await verificationDb.VideoFiles.ToListAsync());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_AcceptsVideoStreamWithoutDeclaredDuration()
    {
        const string probeJson = """
            { "streams": [{ "codec_type": "video", "codec_name": "h264", "width": 16, "height": 16 }] }
            """;
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            await WriteValidVideoAsync(Path.Combine(tempRoot, "unknown-duration.mp4"));
            await using var environment = await CreateBareEnvironmentAsync(
                tempRoot,
                new StubMediaProbeService(MediaProbeResult.Succeeded(probeJson)));

            environment.Service.StartScan();

            await using var scope = environment.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            var file = await db.VideoFiles.SingleAsync();
            Assert.Equal(0, file.Duration);
            Assert.Contains("1 imported", environment.JobService.LatestSubTask);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_SkipsCorruptImageWithoutCreatingLibraryRecord()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(tempRoot, "corrupt.jpg"), [1, 2, 3, 4]);
            await using var environment = await CreateBareEnvironmentAsync(tempRoot);

            environment.Service.StartScan();

            await using var scope = environment.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            Assert.Empty(await db.Images.ToListAsync());
            Assert.Empty(await db.ImageFiles.ToListAsync());
            Assert.Contains("1 invalid media file skipped", environment.JobService.LatestSubTask);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_SkipsCorruptGalleryWithoutCreatingLibraryRecord()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(tempRoot, "corrupt.zip"), [1, 2, 3, 4]);
            await using var environment = await CreateBareEnvironmentAsync(tempRoot);

            environment.Service.StartScan();

            await using var scope = environment.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            Assert.Empty(await db.Galleries.ToListAsync());
            Assert.Empty(await db.GalleryFiles.ToListAsync());
            Assert.Contains("1 invalid media file skipped", environment.JobService.LatestSubTask);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_SkipsTruncatedEpubWithoutCreatingLibraryRecord()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(tempRoot, "truncated.epub"), [0x50, 0x4B, 0x03, 0x04]);
            await using var environment = await CreateBareEnvironmentAsync(tempRoot);

            environment.Service.StartScan();

            await using var scope = environment.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            Assert.Empty(await db.TextDocuments.ToListAsync());
            Assert.Empty(await db.TextFiles.ToListAsync());
            Assert.Contains("1 invalid media file skipped", environment.JobService.LatestSubTask);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_ValidatesAndImportsAudioWithOneProbe()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(tempRoot, "track.mp3"), [1, 2, 3, 4]);
            var probe = new StubMediaProbeService(MediaProbeResult.Succeeded(ValidAudioProbeJson));
            await using var environment = await CreateBareEnvironmentAsync(tempRoot, probe);

            environment.Service.StartScan(new ScanOperationOptions { GenerateAudioPhashes = true });

            await using var scope = environment.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            var audio = await db.Audios.SingleAsync();
            var file = await db.AudioFiles.SingleAsync();
            Assert.Equal("Track", audio.Title);
            Assert.Equal("mp3", file.AudioCodec);
            Assert.Equal(12.5, file.Duration);
            Assert.Equal(1, probe.CallCount);
            Assert.Contains("1 asset generation failure", environment.JobService.LatestSubTask);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_ImportsPreviouslySkippedVideoAfterCopyCompletes()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var videoPath = Path.Combine(tempRoot, "copying.mp4");
            await File.WriteAllBytesAsync(videoPath, []);
            await using var environment = await CreateBareEnvironmentAsync(tempRoot);

            environment.Service.StartScan();
            await WriteValidVideoAsync(videoPath);
            environment.Service.StartScan();

            await using var scope = environment.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            var file = await db.VideoFiles.SingleAsync();
            Assert.True(file.Width > 0);
            Assert.True(file.Height > 0);
            Assert.True(file.Duration > 0);
            Assert.Contains("1 imported", environment.JobService.LatestSubTask);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_RepairsExistingBrokenVideoAfterCopyCompletes()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var videoPath = Path.Combine(tempRoot, "copying.mp4");
            await File.WriteAllBytesAsync(videoPath, []);
            await using var environment = await CreateEnvironmentAsync(tempRoot, videoPath);

            int existingFileId;
            await using (var scope = environment.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                var file = await db.VideoFiles.SingleAsync();
                existingFileId = file.Id;
                file.Width = 0;
                file.Height = 0;
                file.Duration = 0;
                await db.SaveChangesAsync();
            }

            environment.Service.StartScan();
            await WriteValidVideoAsync(videoPath);
            environment.Service.StartScan();

            await using var verificationScope = environment.Services.CreateAsyncScope();
            var verificationDb = verificationScope.ServiceProvider.GetRequiredService<CoveContext>();
            var repaired = await verificationDb.VideoFiles.SingleAsync();
            Assert.Equal(existingFileId, repaired.Id);
            Assert.True(repaired.Width > 0);
            Assert.True(repaired.Height > 0);
            Assert.True(repaired.Duration > 0);
            Assert.Contains("1 updated", environment.JobService.LatestSubTask);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_ReportsCoverGenerationThatProducesNoFileAsFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            await WriteValidVideoAsync(Path.Combine(tempRoot, "valid.mp4"));
            await using var environment = await CreateBareEnvironmentAsync(tempRoot);

            environment.Service.StartScan(new ScanOperationOptions { GenerateCovers = true });

            Assert.Contains("1 asset generation failure", environment.JobService.LatestSubTask);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_RetriesMissingCoverForMetadataCompleteExistingVideo()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            await WriteValidVideoAsync(Path.Combine(tempRoot, "valid.mp4"));
            await using var environment = await CreateBareEnvironmentAsync(tempRoot);
            environment.Service.StartScan();

            environment.Service.StartScan(new ScanOperationOptions { GenerateCovers = true });

            Assert.Equal(1, environment.ThumbnailService.VideoThumbnailCallCount);
            Assert.Contains("1 asset generation failure", environment.JobService.LatestSubTask);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_ReportsImageThumbnailThatProducesNoOutputAsFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            await WriteValidImageAsync(Path.Combine(tempRoot, "valid.jpg"));
            await using var environment = await CreateBareEnvironmentAsync(tempRoot);

            environment.Service.StartScan(new ScanOperationOptions { GenerateImageThumbnails = true });

            Assert.Equal(1, environment.ThumbnailService.ImageThumbnailCallCount);
            Assert.Contains("1 asset generation failure", environment.JobService.LatestSubTask);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_SkipsCaptionSyncForKnownUnchangedVideosDuringNormalScan()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var videoPath = Path.Combine(tempRoot, "known.mp4");
            await File.WriteAllBytesAsync(videoPath, [1, 2, 3, 4]);
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "known.en.vtt"), "WEBVTT");

            await using var environment = await CreateEnvironmentAsync(tempRoot, videoPath);

            environment.Service.StartScan();

            await using var verificationScope = environment.Services.CreateAsyncScope();
            var db = verificationScope.ServiceProvider.GetRequiredService<CoveContext>();
            var video = await db.VideoFiles.Include(item => item.Captions).SingleAsync();

            Assert.Empty(video.Captions);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_TreatsSubSecondStoredModTimeDifferenceAsUnchanged()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var videoPath = Path.Combine(tempRoot, "known.mp4");
            await File.WriteAllBytesAsync(videoPath, [1, 2, 3, 4]);
            var wholeSecond = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(videoPath, wholeSecond.AddMilliseconds(500));
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "known.en.vtt"), "WEBVTT");

            await using var environment = await CreateEnvironmentAsync(tempRoot, videoPath, storedModTime: wholeSecond);

            environment.Service.StartScan();

            await using var verificationScope = environment.Services.CreateAsyncScope();
            var db = verificationScope.ServiceProvider.GetRequiredService<CoveContext>();
            var video = await db.VideoFiles.Include(item => item.Captions).SingleAsync();

            Assert.Empty(video.Captions);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_SkipsChangedPathWhenExistingFileKindDiffersFromExtension()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var path = Path.Combine(tempRoot, "known.mp3");
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
            var oldStoredModTime = DateTime.UtcNow.AddDays(-1);

            await using var environment = await CreateEnvironmentAsync(tempRoot, path, storedModTime: oldStoredModTime);

            environment.Service.StartScan();

            await using var verificationScope = environment.Services.CreateAsyncScope();
            var db = verificationScope.ServiceProvider.GetRequiredService<CoveContext>();

            Assert.Equal(1, await db.Set<BaseFileEntity>().CountAsync());
            Assert.Equal(0, await db.AudioFiles.CountAsync());
            Assert.Equal(1, await db.VideoFiles.CountAsync());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_RescanSyncsCaptionsForKnownVideos()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var videoPath = Path.Combine(tempRoot, "known.mp4");
            await WriteValidVideoAsync(videoPath);
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "known.en.vtt"), "WEBVTT");

            await using var environment = await CreateEnvironmentAsync(tempRoot, videoPath);

            environment.Service.StartScan(new ScanOperationOptions { Rescan = true });

            await using var verificationScope = environment.Services.CreateAsyncScope();
            var db = verificationScope.ServiceProvider.GetRequiredService<CoveContext>();
            var video = await db.VideoFiles.Include(item => item.Captions).SingleAsync();
            var caption = Assert.Single(video.Captions);

            Assert.Equal("known.en.vtt", caption.Filename);
            Assert.Equal("en", caption.LanguageCode);
            Assert.Equal("vtt", caption.CaptionType);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ApplyFfprobeMetadata_OverwritesStaleCodecAndResolutionOnReEncode()
    {
        // Simulates the reported HEVC -> AV1 in-place re-encode: an already-populated file being re-probed
        // must have its codec/resolution/duration replaced, not silently kept.
        var videoFile = new VideoFile
        {
            Width = 1920,
            Height = 1080,
            Duration = 100,
            BitRate = 5_000_000,
            VideoCodec = "hevc",
            AudioCodec = "aac",
            FrameRate = 25,
        };

        const string json = """
        {
          "format": { "duration": "42.5", "bit_rate": "1000000" },
          "streams": [
            { "codec_type": "video", "codec_name": "av1", "width": 1280, "height": 720, "r_frame_rate": "30/1" },
            { "codec_type": "audio", "codec_name": "opus" }
          ]
        }
        """;

        ScanService.ApplyFfprobeMetadata(videoFile, json);

        Assert.Equal("av1", videoFile.VideoCodec);
        Assert.Equal(1280, videoFile.Width);
        Assert.Equal(720, videoFile.Height);
        Assert.Equal("opus", videoFile.AudioCodec);
        Assert.Equal(42.5, videoFile.Duration);
        Assert.Equal(1_000_000, videoFile.BitRate);
        Assert.Equal(30, videoFile.FrameRate);
    }

    [Fact]
    public async Task StartScan_RelinksMovedVideoInsteadOfCreatingDuplicate()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var originalPath = Path.Combine(tempRoot, "original.mp4");
            await WriteValidVideoAsync(originalPath, minimumLength: 70_000);

            await using var environment = await CreateBareEnvironmentAsync(tempRoot);

            // First scan: creates the Video + VideoFile and its oshash identity fingerprint.
            environment.Service.StartScan();

            int videoFileId;
            await using (var scope = environment.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                var seededFile = await db.VideoFiles.Include(f => f.Fingerprints).SingleAsync();
                videoFileId = seededFile.Id;
                Assert.Contains(seededFile.Fingerprints, fp => fp.Type == "oshash" && !string.IsNullOrEmpty(fp.Value));

                // Stamp the entity so we can prove the move preserves it rather than recreating it.
                var seededVideo = await db.Videos.SingleAsync();
                seededVideo.Title = "Preserve me";
                await db.SaveChangesAsync();
            }

            // Move the file to a subfolder (identical bytes -> identical oshash) and remove the original.
            var subDir = Path.Combine(tempRoot, "sub");
            Directory.CreateDirectory(subDir);
            var movedPath = Path.Combine(subDir, "renamed.mp4");
            File.Move(originalPath, movedPath);

            // Second scan: should re-point the existing record, not create a duplicate.
            environment.Service.StartScan();

            await using (var scope = environment.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                Assert.Equal(1, await db.Videos.CountAsync());
                var movedFile = await db.VideoFiles.SingleAsync();
                Assert.Equal(videoFileId, movedFile.Id);
                Assert.Equal("renamed.mp4", movedFile.Basename);
                Assert.EndsWith("sub/renamed.mp4", movedFile.Path);
                Assert.Equal("Preserve me", (await db.Videos.SingleAsync()).Title);
            }
            Assert.Contains("0 imported, 1 updated", environment.JobService.LatestSubTask);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_AttachesDuplicateFileToExistingEntityInsteadOfCreatingSecondVideo()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var originalPath = Path.Combine(tempRoot, "original.mp4");
            await WriteValidVideoAsync(originalPath, minimumLength: 70_000);

            await using var environment = await CreateBareEnvironmentAsync(tempRoot);
            environment.Service.StartScan();

            // A COPY of the same bytes appears while the original remains on disk: identical content should
            // join the existing video as a second file, not spawn a separate duplicate entity.
            var copyPath = Path.Combine(tempRoot, "copy.mp4");
            File.Copy(originalPath, copyPath);

            environment.Service.StartScan();

            await using var scope = environment.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            Assert.Equal(1, await db.Videos.CountAsync());
            Assert.Equal(2, await db.VideoFiles.CountAsync());
            var video = await db.Videos.Include(v => v.Files).SingleAsync();
            Assert.Equal(2, video.Files.Count);
            Assert.Contains(video.Files, f => f.Basename == "original.mp4");
            Assert.Contains(video.Files, f => f.Basename == "copy.mp4");
            Assert.Contains("1 imported, 0 updated", environment.JobService.LatestSubTask);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static async Task<TestEnvironment> CreateBareEnvironmentAsync(
        string libraryRoot,
        IMediaProbeService? mediaProbeService = null,
        TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        var dbOptions = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"scan-service-{Guid.NewGuid():N}")
            .Options;

        services.AddSingleton(dbOptions);
        services.AddScoped<CoveContext>(_ => new TestCoveContext(dbOptions));

        var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            await db.Database.EnsureCreatedAsync();
        }

        var config = new CoveConfiguration
        {
            CovePaths = [new CovePath { Path = libraryRoot }],
        };

        var extensionManager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = libraryRoot,
            CoveVersion = "test",
        });

        var jobService = new ImmediateJobService();
        var probeService = mediaProbeService ?? new StubMediaProbeService(MediaProbeResult.Succeeded(ValidVideoProbeJson));
        var clock = timeProvider ?? ReadyTimeProvider;
        var galleryReader = new ZipGalleryReader(new ZipFileReader());
        var thumbnailService = new NoOpThumbnailService();
        var service = new ScanService(
            jobService,
            provider.GetRequiredService<IServiceScopeFactory>(),
            config,
            new EventBus(),
            new NoOpFingerprintService(),
            thumbnailService,
            new TextExtractionService(),
            galleryReader,
            extensionManager,
            probeService,
            new ScanFileValidator(probeService, galleryReader, clock),
            NullLogger<ScanService>.Instance);

        return new TestEnvironment(provider, service, jobService, thumbnailService);
    }

    private static async Task<TestEnvironment> CreateEnvironmentAsync(
        string libraryRoot,
        string videoPath,
        DateTime? storedModTime = null,
        IMediaProbeService? mediaProbeService = null,
        TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        var dbOptions = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"scan-service-{Guid.NewGuid():N}")
            .Options;

        services.AddSingleton(dbOptions);
        services.AddScoped<CoveContext>(_ => new TestCoveContext(dbOptions));

        var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            await db.Database.EnsureCreatedAsync();

            var folder = new Folder
            {
                Path = NormalizeStoredFolderPath(libraryRoot),
                ModTime = Directory.GetLastWriteTimeUtc(libraryRoot),
            };

            var video = new Video
            {
                Title = "Known video",
            };

            var fileInfo = new FileInfo(videoPath);
            var effectiveStoredModTime = storedModTime ?? fileInfo.LastWriteTimeUtc;
            video.Files.Add(new VideoFile
            {
                Basename = Path.GetFileName(videoPath),
                ParentFolder = folder,
                Size = fileInfo.Length,
                ModTime = effectiveStoredModTime,
                Format = "mp4",
                Width = 1920,
                Height = 1080,
                Duration = 42,
                VideoCodec = "h264",
                AudioCodec = "aac",
            });

            db.Videos.Add(video);
            await db.SaveChangesAsync();
        }
        var jobService = new ImmediateJobService();
        var config = new CoveConfiguration
        {
            CovePaths =
            [
                new CovePath
                {
                    Path = libraryRoot,
                }
            ],
        };

        var extensionManager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = libraryRoot,
            CoveVersion = "test",
        });

        var probeService = mediaProbeService ?? new StubMediaProbeService(MediaProbeResult.Succeeded(ValidVideoProbeJson));
        var clock = timeProvider ?? ReadyTimeProvider;
        var galleryReader = new ZipGalleryReader(new ZipFileReader());
        var thumbnailService = new NoOpThumbnailService();
        var service = new ScanService(
            jobService,
            provider.GetRequiredService<IServiceScopeFactory>(),
            config,
            new EventBus(),
            new NoOpFingerprintService(),
            thumbnailService,
            new TextExtractionService(),
            galleryReader,
            extensionManager,
            probeService,
            new ScanFileValidator(probeService, galleryReader, clock),
            NullLogger<ScanService>.Instance);

        return new TestEnvironment(provider, service, jobService, thumbnailService);
    }

    private static string NormalizeStoredFolderPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        var normalized = !string.IsNullOrEmpty(root) && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return normalized.Replace('\\', '/');
    }

    private static async Task WriteValidVideoAsync(string path, int minimumLength = 0)
    {
        var bytes = Convert.FromBase64String(TinyValidMp4Base64);
        if (bytes.Length < minimumLength)
            Array.Resize(ref bytes, minimumLength);
        await File.WriteAllBytesAsync(path, bytes);
    }

    private static async Task WriteValidImageAsync(string path)
    {
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(16, 16);
        await image.SaveAsJpegAsync(path);
    }

    private sealed class TestCoveContext(DbContextOptions<CoveContext> options) : CoveContext(options);

    private sealed class OffsetTimeProvider(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.Add(offset);
    }

    private sealed class StubMediaProbeService(MediaProbeResult result, Action<string>? onProbe = null) : IMediaProbeService
    {
        public int CallCount { get; private set; }

        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            onProbe?.Invoke(path);
            return Task.FromResult(result);
        }
    }

    private sealed class ImmediateJobService : IJobService
    {
        private int _nextId;

        public string? LatestSubTask { get; set; }

        public string Enqueue(string type, string description, Func<Cove.Core.Interfaces.IJobProgress, CancellationToken, Task> work, bool exclusive = true)
        {
            work(new ImmediateJobProgress(this), CancellationToken.None).GetAwaiter().GetResult();
            return $"job-{Interlocked.Increment(ref _nextId)}";
        }

        public bool Cancel(string jobId) => false;

        public bool ReorderQueued(string jobId, string? beforeJobId) => false;

        public JobInfo? GetJob(string jobId) => null;

        public IReadOnlyList<JobInfo> GetAllJobs() => [];

        public IReadOnlyList<JobInfo> GetJobHistory() => [];
    }

    private sealed class ImmediateJobProgress(ImmediateJobService owner) : Cove.Core.Interfaces.IJobProgress
    {
        public void Report(double progress, string? subTask = null)
        {
            owner.LatestSubTask = subTask;
        }
    }

    private sealed class NoOpFingerprintService : IFingerprintService
    {
        public Task<string?> ComputeMd5Async(string path, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<string?> ComputeImagePhashAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<string?> ComputeVideoPhashAsync(string path, double duration, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<string?> ComputeAudioPhashAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<string?> ComputeTextPhashAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public string StartGenerateVideoPhashes() => "noop";

        public string StartGenerateImagePhashes() => "noop";
    }

    private sealed class NoOpThumbnailService : IThumbnailService
    {
        public int VideoThumbnailCallCount { get; private set; }
        public int ImageThumbnailCallCount { get; private set; }

        public Task<string?> GetVideoThumbnailPathAsync(int videoId, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<string?> GetImageFilePathAsync(int imageId, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageStreamAsync(int imageId, CancellationToken ct = default) => Task.FromResult<(Stream stream, string contentType, bool supportsRangeRequests)?>(null);

        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageThumbnailStreamAsync(int imageId, int maxDimension = 640, CancellationToken ct = default) => Task.FromResult<(Stream stream, string contentType, bool supportsRangeRequests)?>(null);

        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetBlobImageThumbnailStreamAsync(string blobId, int maxDimension = 640, CancellationToken ct = default) => Task.FromResult<(Stream stream, string contentType, bool supportsRangeRequests)?>(null);

        public Task DeleteVideoGeneratedFilesAsync(int videoId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteImageGeneratedFilesAsync(int imageId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteBlobGeneratedFilesAsync(string blobId, CancellationToken ct = default) => Task.CompletedTask;

        public Task GenerateVideoThumbnailAsync(int videoId, double? atSeconds = null, CancellationToken ct = default)
        {
            VideoThumbnailCallCount++;
            return Task.CompletedTask;
        }

        public Task<bool> GenerateImageThumbnailAsync(int imageId, int maxDimension = 640, bool overwrite = false, CancellationToken ct = default)
        {
            ImageThumbnailCallCount++;
            return Task.FromResult(false);
        }

        public Task GenerateVideoPreviewAsync(int videoId, CancellationToken ct = default) => Task.CompletedTask;

        public Task GenerateSegmentAnimatedPreviewAsync(int videoId, double startSec, double? endSec = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task GenerateVideoSpriteAsync(int videoId, CancellationToken ct = default) => Task.CompletedTask;

        public string GetThumbnailPathForVideo(int videoId) => string.Empty;

        public string GetTimestampedThumbnailPath(int videoId, double seconds) => string.Empty;

        public string GetSegmentAnimatedPreviewPath(int videoId, double seconds) => string.Empty;

        public string GetPreviewPath(int videoId) => string.Empty;

        public string GetSpritePath(int videoId) => string.Empty;

        public string GetSpriteVttPath(int videoId) => string.Empty;

        public string StartGenerateAllThumbnails() => "noop";
    }

    private sealed class TestEnvironment(
        ServiceProvider services,
        ScanService service,
        ImmediateJobService jobService,
        NoOpThumbnailService thumbnailService) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;
        public ScanService Service { get; } = service;
        public ImmediateJobService JobService { get; } = jobService;
        public NoOpThumbnailService ThumbnailService { get; } = thumbnailService;

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
        }
    }
}
