using Cove.Api.Services;
using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;

namespace Cove.Tests;

public class FfmpegHwAccelProbeTests
{
    private const string DriverTooOld =
        "[h264_nvenc @ 0x55d5c3a0f2c0] Driver does not support the required nvenc API version. Required: 13.1 Found: 13.0\n" +
        "[h264_nvenc @ 0x55d5c3a0f2c0] The minimum required Nvidia driver for nvenc is 610.00 or newer\n" +
        "[vost#0:0/h264_nvenc @ 0x55d5c3a10a00] Error while opening encoder - maybe incorrect parameters such as bit_rate, rate, width or height.\n";

    private const string NoNvidiaGpu = "[h264_nvenc @ 0x5600] Cannot load libcuda.so.1\n[vost#0:0/h264_nvenc @ 0x5601] Error while opening encoder\n";
    private const string NoVaapiDevice = "[AVHWDeviceContext @ 0x5602] No VA display found for device /dev/dri/renderD128.\n";

    [Fact]
    public void DriverRequirementMessage_KeepsOnlyTheDriverLinesWithoutPrefixes()
    {
        Assert.Equal(
            "Driver does not support the required nvenc API version. Required: 13.1 Found: 13.0; " +
            "The minimum required Nvidia driver for nvenc is 610.00 or newer",
            FfmpegHwAccel.DriverRequirementMessage(DriverTooOld));
        Assert.Null(FfmpegHwAccel.DriverRequirementMessage(NoNvidiaGpu));
        Assert.Null(FfmpegHwAccel.DriverRequirementMessage(string.Empty));
    }

    [Fact]
    public void DetectAvailableAccelerators_WarnsWithTheDriverRequirementWhenNvencNeedsANewerDriver()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var ffmpeg = new FakeFfmpeg(nvencStderr: DriverTooOld);
        var logger = new RecordingLogger();

        Assert.Empty(FfmpegHwAccel.DetectAvailableAccelerators(ffmpeg.Path, logger));

        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("h264_nvenc", warning.Message);
        Assert.Contains("Required: 13.1 Found: 13.0", warning.Message);
        Assert.Contains("610.00 or newer", warning.Message);
        Assert.Contains("driver", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectH264Encoder_AutoWarnsWithTheDriverRequirementAndFallsBackToSoftware()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var ffmpeg = new FakeFfmpeg(nvencStderr: DriverTooOld);
        var logger = new RecordingLogger();

        Assert.Equal("libx264", FfmpegHwAccel.SelectH264Encoder(ffmpeg.Path, "auto", logger));

        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("610.00 or newer", warning.Message);
    }

    [Fact]
    public void SelectH264Encoder_PinnedNvencNamesTheDriverRequirement()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var ffmpeg = new FakeFfmpeg(nvencStderr: DriverTooOld);
        var logger = new RecordingLogger();

        Assert.Equal("libx264", FfmpegHwAccel.SelectH264Encoder(ffmpeg.Path, "nvenc", logger));

        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("610.00 or newer", warning.Message);
    }

    [Fact]
    public void DetectAvailableAccelerators_StaysQuietWhenTheMachineSimplyHasNoSuchGpu()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var ffmpeg = new FakeFfmpeg(nvencStderr: NoNvidiaGpu);
        var logger = new RecordingLogger();

        Assert.Empty(FfmpegHwAccel.DetectAvailableAccelerators(ffmpeg.Path, logger));
        Assert.Equal("libx264", FfmpegHwAccel.SelectH264Encoder(ffmpeg.Path, "auto", logger));

        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    /// <summary>A shell script standing in for ffmpeg: lists h264_nvenc and h264_vaapi, and fails every
    /// test encode, writing <c>nvencStderr</c> for NVENC and a missing-device error for VAAPI.</summary>
    [UnsupportedOSPlatform("windows")]
    private sealed class FakeFfmpeg : IDisposable
    {
        private readonly string _directory;

        public FakeFfmpeg(string nvencStderr)
        {
            _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cove-fake-ffmpeg-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "ffmpeg");
            var script =
                "#!/bin/sh\n" +
                "for arg in \"$@\"; do\n" +
                "  if [ \"$arg\" = \"-encoders\" ]; then\n" +
                "    printf '%s\\n' ' V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)'" +
                " ' V....D h264_vaapi           H.264/AVC (VAAPI) (codec h264)'" +
                " ' V....D libx264              libx264 H.264 / AVC (codec h264)'\n" +
                "    exit 0\n" +
                "  fi\n" +
                "done\n" +
                "case \"$*\" in\n" +
                "  *h264_nvenc*) printf '%s' " + ShellQuote(nvencStderr) + " >&2 ;;\n" +
                "  *h264_vaapi*) printf '%s' " + ShellQuote(NoVaapiDevice) + " >&2 ;;\n" +
                "esac\n" +
                "exit 1\n";
            File.WriteAllText(Path, script);
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }

        private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
