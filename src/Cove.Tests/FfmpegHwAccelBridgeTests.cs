using Cove.Api.Services;
using Xunit;

namespace Cove.Tests;

/// <summary>
/// Locks in the GPU-surface → software-filter bridge (issue #30): user decode args that pin frames
/// to GPU memory (e.g. <c>-hwaccel cuda -hwaccel_output_format cuda</c>) must get a
/// <c>hwdownload,format=nv12</c> prefix ahead of any software filter chain, while plain software
/// decodes, GPU-aware chains, and empty chains are left untouched.
/// </summary>
public class FfmpegHwAccelBridgeTests
{
    private const string Chain = "scale=1280:720";

    [Theory]
    [InlineData("-hwaccel cuda -hwaccel_output_format cuda")]
    [InlineData("-hwaccel qsv -hwaccel_output_format qsv")]
    [InlineData("-hwaccel vaapi -hwaccel_output_format vaapi")]
    [InlineData("-hwaccel vulkan -hwaccel_output_format vulkan")]
    public void GpuSurfaceDecode_BridgesSoftwareChain(string inputArgs)
    {
        Assert.True(FfmpegHwAccel.InputArgsKeepFramesOnGpu(inputArgs));
        Assert.Equal($"hwdownload,format=nv12,{Chain}",
            FfmpegHwAccel.BridgeGpuFramesForSoftwareFilters(inputArgs, Chain));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-hwaccel cuda")] // no output format: frames land in system memory automatically
    [InlineData("-hwaccel cuda -hwaccel_output_format nv12")] // software format: forces download
    [InlineData("-fflags +discardcorrupt")]
    public void SoftwareDecode_LeavesChainUntouched(string? inputArgs)
    {
        Assert.False(FfmpegHwAccel.InputArgsKeepFramesOnGpu(inputArgs));
        Assert.Equal(Chain, FfmpegHwAccel.BridgeGpuFramesForSoftwareFilters(inputArgs, Chain));
    }

    [Theory]
    [InlineData("scale_cuda=1280:720")]
    [InlineData("hwdownload,format=nv12,scale=1280:720")]
    [InlineData("scale_npp=1280:720")]
    public void GpuAwareChain_IsNotSecondGuessed(string chain)
    {
        Assert.Equal(chain, FfmpegHwAccel.BridgeGpuFramesForSoftwareFilters(
            "-hwaccel cuda -hwaccel_output_format cuda", chain));
    }

    [Fact]
    public void EmptyChain_StaysEmpty_SoNvencCanConsumeGpuFramesDirectly()
    {
        Assert.Equal(string.Empty, FfmpegHwAccel.BridgeGpuFramesForSoftwareFilters(
            "-hwaccel cuda -hwaccel_output_format cuda", string.Empty));
    }
}
