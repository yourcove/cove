using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

/// <summary>
/// Locks in the one-time migration from the legacy split ffmpeg config (EnableFfmpegHwAccel +
/// TranscodeHardwareAcceleration + the Transcode/LiveTranscode Input/Output args quartet) to the unified
/// HardwareAcceleration + FfmpegInputArgs/FfmpegOutputArgs fields, plus the new-field passthrough.
/// </summary>
public class FfmpegConfigMigrationTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static CoveConfiguration ApplyJson(string json)
    {
        var dto = JsonSerializer.Deserialize<CoveConfigDto>(json, JsonOpts)!;
        var cfg = new CoveConfiguration();
        var svc = new ConfigService(cfg, NullLogger<ConfigService>.Instance);
        svc.ApplyToLive(dto);
        return cfg;
    }

    [Fact]
    public void LegacyPinnedEncoderMigratesToUnifiedField()
    {
        var cfg = ApplyJson("""
            {"transcodeHardwareAcceleration":"nvenc","enableFfmpegHwAccel":true,
             "liveTranscodeInputArgs":"-hwaccel cuda","liveTranscodeOutputArgs":"-c:v h264_nvenc -cq 23"}
            """);

        Assert.Equal("nvenc", cfg.HardwareAcceleration);
        Assert.Equal("-hwaccel cuda", cfg.FfmpegInputArgs);
        Assert.Equal("-c:v h264_nvenc -cq 23", cfg.FfmpegOutputArgs);
    }

    [Fact]
    public void LegacyNoneMeansAutoDetect()
    {
        // The old "none" actually meant "auto-detect a hardware encoder", so it maps to the new "auto".
        var cfg = ApplyJson("""{"transcodeHardwareAcceleration":"none"}""");
        Assert.Equal("auto", cfg.HardwareAcceleration);
    }

    [Fact]
    public void LegacyNonLiveArgsAlsoMigrate()
    {
        // The old lower-priority TranscodeInputArgs/OutputArgs are folded into the single input/output args.
        var cfg = ApplyJson("""{"transcodeInputArgs":"-threads 2","transcodeOutputArgs":"-c:v libx264"}""");
        Assert.Equal("-threads 2", cfg.FfmpegInputArgs);
        Assert.Equal("-c:v libx264", cfg.FfmpegOutputArgs);
    }

    [Fact]
    public void NewFieldTakesPrecedenceOverLegacy()
    {
        var cfg = ApplyJson("""{"hardwareAcceleration":"vaapi","transcodeHardwareAcceleration":"nvenc"}""");
        Assert.Equal("vaapi", cfg.HardwareAcceleration);
    }

    [Fact]
    public void NewFieldsPassThrough()
    {
        var cfg = ApplyJson("""
            {"hardwareAcceleration":"qsv","hardwareEncodeSessionLimit":4,
             "ffmpegInputArgs":"-hwaccel qsv","ffmpegOutputArgs":"-c:v h264_qsv"}
            """);

        Assert.Equal("qsv", cfg.HardwareAcceleration);
        Assert.Equal(4, cfg.HardwareEncodeSessionLimit);
        Assert.Equal("-hwaccel qsv", cfg.FfmpegInputArgs);
        Assert.Equal("-c:v h264_qsv", cfg.FfmpegOutputArgs);
    }

    [Theory]
    [InlineData("{}")]                                   // fresh / very old config: default to auto
    [InlineData("""{"hardwareAcceleration":"bogus"}""")] // unknown value normalizes to auto
    [InlineData("""{"hardwareAcceleration":""}""")]      // blank normalizes to auto
    public void DefaultsToAuto(string json)
    {
        Assert.Equal("auto", ApplyJson(json).HardwareAcceleration);
    }

    [Fact]
    public void OffIsPreserved()
    {
        Assert.Equal("off", ApplyJson("""{"hardwareAcceleration":"off"}""").HardwareAcceleration);
    }
}
