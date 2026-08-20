using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Entities.Faces;

[Collection(ApiTestLane1Collection.Name)]
public sealed class FaceEvidenceApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/faces/{id:int}/appearances")]
    [CoversEndpoint("GET", "/api/faces/{id:int}/detections")]
    [CoversEndpoint("GET", "/api/videos/{videoId:int}/faces")]
    [CoversEndpoint("GET", "/api/images/{imageId:int}/faces")]
    public async Task GivenVideoAndImageEvidence_WhenFaceRelationshipsAreRead_ThenMetadataIsProjectedFromBothHosts()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Face evidence video {Guid.NewGuid():N}");
        var image = await AsUser().CreateImageAsync($"Face evidence image {Guid.NewGuid():N}");
        var evidenceFace = await AsUser().CreateFaceAsync(new FaceCreateDto("Evidence candidate", null, false, "detector.primary"));
        var projectedFace = await AsUser().CreateFaceAsync(new FaceCreateDto("Host candidate", null, false, null));
        var videoDetection = await AsUser().CreateVideoFaceDetectionAsync(video, evidenceFace);
        var imageDetection = await AsUser().CreateImageFaceDetectionAsync(image, evidenceFace);
        await AsDbUser().CreateFaceAppearanceAsync(
            projectedFace.Id,
            FaceAppearanceHostType.Video,
            video.Id,
            sampleCount: 6,
            retainedSpatialSampleCount: 4,
            segmentCount: 2,
            firstSeenAtSec: 1.25,
            lastSeenAtSec: 8.5,
            topConfidence: 0.97f);
        await AsDbUser().CreateFaceAppearanceAsync(
            projectedFace.Id,
            FaceAppearanceHostType.Image,
            image.Id,
            sampleCount: 3,
            retainedSpatialSampleCount: 2,
            segmentCount: 1,
            firstSeenAtSec: null,
            lastSeenAtSec: null,
            topConfidence: 0.89f);

        // Act
        var appearances = await AsUser().GetFaceAppearancesAsync(evidenceFace.Id);
        var detections = await AsUser().GetFaceDetectionsAsync(evidenceFace.Id);
        var videoFaces = await AsUser().GetVideoFacesAsync(video);
        var imageFaces = await AsUser().GetImageFacesAsync(image);

        // Assert
        appearances.TotalCount.Should().Be(2);
        var videoAppearance = appearances.Items.Single(item => item.HostType == "video");
        videoAppearance.HostId.Should().Be(video.Id);
        videoAppearance.Title.Should().Be(video.Title);
        videoAppearance.ThumbnailUrl.Should().Be($"/api/stream/video/{video.Id}/screenshot");
        videoAppearance.FrameSampleCount.Should().Be(1);
        videoAppearance.RetainedSpatialSampleCount.Should().Be(1);
        videoAppearance.SegmentCount.Should().Be(0);
        videoAppearance.FirstSeenAtSec.Should().Be(2);
        videoAppearance.LastSeenAtSec.Should().Be(2);
        videoAppearance.TopConfidence.Should().Be(0.95f);
        var imageAppearance = appearances.Items.Single(item => item.HostType == "image");
        imageAppearance.HostId.Should().Be(image.Id);
        imageAppearance.Title.Should().Be(image.Title);
        imageAppearance.ThumbnailUrl.Should().Be($"/api/stream/image/{image.Id}/thumbnail?max=320");
        imageAppearance.FrameSampleCount.Should().Be(1);
        imageAppearance.RetainedSpatialSampleCount.Should().Be(1);
        imageAppearance.SegmentCount.Should().Be(0);
        imageAppearance.FirstSeenAtSec.Should().BeNull();
        imageAppearance.LastSeenAtSec.Should().BeNull();
        imageAppearance.TopConfidence.Should().Be(0.95f);

        detections.Select(detection => detection.Id).Should().BeEquivalentTo([videoDetection.Id, imageDetection.Id]);
        detections.Should().OnlyContain(detection =>
            detection.RefKind == "face"
            && detection.RefId == evidenceFace.Id
            && detection.GroupKey == null
            && detection.SourceKey == "api-test"
            && detection.SourceRunId == null
            && detection.Class == "face"
            && detection.Score == 0.95f
            && detection.FrameWidth == 100
            && detection.FrameHeight == 100);

        videoFaces.Should().ContainSingle();
        AssertHostProjection(videoFaces.Single(), projectedFace, appearanceCount: 2, frameSampleCount: 9);
        videoFaces.Single().FirstSeenAtSec.Should().Be(1.25);
        videoFaces.Single().LastSeenAtSec.Should().Be(8.5);
        videoFaces.Single().TopConfidence.Should().Be(0.97f);

        imageFaces.Should().ContainSingle();
        AssertHostProjection(imageFaces.Single(), projectedFace, appearanceCount: 2, frameSampleCount: 9);
        imageFaces.Single().FirstSeenAtSec.Should().BeNull();
        imageFaces.Single().LastSeenAtSec.Should().BeNull();
        imageFaces.Single().TopConfidence.Should().Be(0.89f);
    }

    [Fact]
    public async Task GivenMissingFace_WhenEvidenceIsRead_ThenNotFoundIsReturned()
    {
        // Arrange
        const int missingId = int.MaxValue;

        // Act
        var appearances = () => AsUser().GetFaceAppearancesAsync(missingId);
        var detections = () => AsUser().GetFaceDetectionsAsync(missingId);

        // Assert
        await appearances.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
        await detections.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }

    private static void AssertHostProjection(
        FaceHostFaceDto projected,
        FaceDto face,
        int appearanceCount,
        int frameSampleCount)
    {
        projected.Id.Should().Be(face.Id);
        projected.Label.Should().Be(face.Label);
        projected.AppearanceCount.Should().Be(appearanceCount);
        projected.FrameSampleCount.Should().Be(frameSampleCount);
        projected.VideoCount.Should().Be(1);
        projected.ImageCount.Should().Be(1);
    }
}
