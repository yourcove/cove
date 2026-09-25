using Cove.Core.Enums;
using Cove.Core.Helpers;

namespace Cove.Tests;

/// <summary>
/// VR players need a projection, a field of view and a stereo packing. Studios encode those in file-name
/// suffixes; these tests pin the conventions detection accepts and the flat files it must leave alone.
/// </summary>
public class VrDescriptorDetectorTests
{
    [Theory]
    [InlineData("/lib/Studio - Scene_180_LR.mp4", VrProjection.Equirectangular, 180, VrStereoMode.SideBySide)]
    [InlineData("/lib/scene.180.sbs.mkv", VrProjection.Equirectangular, 180, VrStereoMode.SideBySide)]
    [InlineData("/lib/scene_3dh.mp4", VrProjection.Equirectangular, 180, VrStereoMode.SideBySide)]
    [InlineData("/lib/scene_360_TB.mp4", VrProjection.Equirectangular, 360, VrStereoMode.TopBottom)]
    [InlineData("/lib/scene_VR360_mono.mp4", VrProjection.Equirectangular, 360, VrStereoMode.Mono)]
    [InlineData("/lib/scene_MKX200.mp4", VrProjection.Mkx200, 200, VrStereoMode.SideBySide)]
    [InlineData("/lib/scene_VRCA220.mp4", VrProjection.Mkx200, 220, VrStereoMode.SideBySide)]
    [InlineData("/lib/scene_FISHEYE190.mp4", VrProjection.Fisheye, 190, VrStereoMode.SideBySide)]
    [InlineData("/lib/scene_RF52.mp4", VrProjection.Fisheye, 190, VrStereoMode.SideBySide)]
    [InlineData("/lib/scene_oculus.mp4", VrProjection.Equirectangular, 180, VrStereoMode.SideBySide)]
    public void DetectsCommonSuffixes(string path, VrProjection projection, int fov, VrStereoMode stereo)
    {
        var descriptor = VrDescriptorDetector.DetectFromFileName(path);

        Assert.NotNull(descriptor);
        Assert.Equal(projection, descriptor.Projection);
        Assert.Equal(fov, descriptor.FieldOfView);
        Assert.Equal(stereo, descriptor.StereoMode);
        Assert.True(descriptor.Inferred);
    }

    [Theory]
    [InlineData("/lib/Holiday 2019.mp4")]
    [InlineData("/lib/Top Gun 3D SBS.mkv")]
    [InlineData("/lib/Skateboarding 180 kickflip.mp4")]
    [InlineData("/lib/Grand Tour 360.mp4")]
    [InlineData("/lib/vrbangers/flat-trailer.mp4")]
    [InlineData("")]
    public void LeavesFlatFilesAlone(string path)
    {
        Assert.Null(VrDescriptorDetector.DetectFromFileName(path));
    }

    [Theory]
    [InlineData(5760, 2880, 360, VrStereoMode.Mono)]
    [InlineData(5760, 5760, 360, VrStereoMode.TopBottom)]
    [InlineData(4096, 2048, 180, VrStereoMode.SideBySide)]
    [InlineData(2880, 2880, 180, VrStereoMode.Mono)]
    public void DimensionsFillInPackingTheNameLeavesOpen(int width, int height, int fovInName, VrStereoMode stereo)
    {
        var descriptor = VrDescriptorDetector.DetectFromFileName($"/lib/scene_vr{fovInName}.mp4", width, height);

        Assert.NotNull(descriptor);
        Assert.Equal(stereo, descriptor.StereoMode);
    }

    [Fact]
    public void ResolveReturnsNullForFlatVideos()
    {
        Assert.Null(VrDescriptorDetector.Resolve(false, null, null, null, "/lib/scene_180_LR.mp4", 5760, 2880));
    }

    [Fact]
    public void ResolveFallsBackToDimensionsForAVideoFlaggedVrWithAPlainName()
    {
        var sideBySide = VrDescriptorDetector.Resolve(true, null, null, null, "/lib/scene.mp4", 5760, 2880)!;
        var square = VrDescriptorDetector.Resolve(true, null, null, null, "/lib/scene.mp4", 4096, 4096)!;

        Assert.Equal((VrProjection.Equirectangular, 180, VrStereoMode.SideBySide), (sideBySide.Projection, sideBySide.FieldOfView, sideBySide.StereoMode));
        Assert.Equal((VrProjection.Equirectangular, 360, VrStereoMode.TopBottom), (square.Projection, square.FieldOfView, square.StereoMode));
        Assert.True(sideBySide.Inferred);
    }

    [Fact]
    public void ExplicitValuesWinAndUnsetOnesAreFilledIn()
    {
        var descriptor = VrDescriptorDetector.Resolve(true, VrProjection.Mkx200, null, null, "/lib/scene_180_TB.mp4", 5760, 5760)!;

        Assert.Equal(VrProjection.Mkx200, descriptor.Projection);
        Assert.Equal(200, descriptor.FieldOfView);
        Assert.Equal(VrStereoMode.TopBottom, descriptor.StereoMode);
        Assert.False(descriptor.Inferred);
    }
}

/// <summary>Generated covers and previews of VR videos show one eye, flattened, instead of the raw frame.</summary>
public class VrFrameFilterTests
{
    [Fact]
    public void FlatVideosAreLeftAlone()
    {
        Assert.Null(Cove.Api.Services.VrFrameFilter.OneEyeFlat(null, 640));
    }

    [Theory]
    [InlineData(VrProjection.Equirectangular, 180, VrStereoMode.SideBySide, "v360=input=he:output=flat:in_stereo=sbs:out_stereo=2d:h_fov=100:v_fov=67.67:w=640:h=360")]
    [InlineData(VrProjection.Equirectangular, 360, VrStereoMode.TopBottom, "v360=input=e:output=flat:in_stereo=tb:out_stereo=2d:h_fov=100:v_fov=67.67:w=640:h=360")]
    [InlineData(VrProjection.Fisheye, 190, VrStereoMode.SideBySide, "v360=input=fisheye:ih_fov=190:iv_fov=190:output=flat:in_stereo=sbs:out_stereo=2d:h_fov=100:v_fov=67.67:w=640:h=360")]
    public void ReprojectsOneEyeToA16By9View(VrProjection projection, int fov, VrStereoMode stereo, string expected)
    {
        var filter = Cove.Api.Services.VrFrameFilter.OneEyeFlat(new Cove.Core.DTOs.VrDescriptorDto(projection, fov, stereo), 640);

        Assert.Equal(expected, filter);
    }

    [Theory]
    [InlineData(VrStereoMode.SideBySide, "sbs", "sbs")]
    [InlineData(VrStereoMode.TopBottom, "tb", "sbs")]
    [InlineData(VrStereoMode.Mono, "2d", "2d")]
    public void StereoCardsKeepBothEyesSideBySide(VrStereoMode stereo, string inStereo, string outStereo)
    {
        // v360's w/h are per eye, so an 800-wide request yields a 1600-wide side-by-side card.
        var filter = Cove.Api.Services.VrFrameFilter.StereoFlat(new Cove.Core.DTOs.VrDescriptorDto(VrProjection.Equirectangular, 180, stereo), 800);

        Assert.Equal($"v360=input=he:output=flat:in_stereo={inStereo}:out_stereo={outStereo}:h_fov=100:v_fov=67.67:w=800:h=450", filter);
    }
}
