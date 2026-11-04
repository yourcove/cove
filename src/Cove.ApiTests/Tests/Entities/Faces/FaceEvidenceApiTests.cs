using Cove.Api.Http;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;

namespace Cove.ApiTests.Tests.Entities.Faces;

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
        var video = await AsUser().CreateVideoAsync($"Face evidence video {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var image = await AsUser().CreateImageAsync($"Face evidence image {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var evidenceFace = await AsUser().CreateFaceAsync(new FaceCreateDto("Evidence candidate", null, false, "detector.primary"), TestContext.Current.CancellationToken);
        var projectedFace = await AsUser().CreateFaceAsync(new FaceCreateDto("Host candidate", null, false, null), TestContext.Current.CancellationToken);
        var videoDetection = await AsUser().CreateVideoFaceDetectionAsync(video, evidenceFace, TestContext.Current.CancellationToken);
        var imageDetection = await AsUser().CreateImageFaceDetectionAsync(image, evidenceFace, TestContext.Current.CancellationToken);
        await AsDbUser().CreateFaceAppearanceAsync(projectedFace.Id, FaceAppearanceHostType.Video, video.Id, sampleCount: 6, retainedSpatialSampleCount: 4, segmentCount: 2, firstSeenAtSec: 1.25, lastSeenAtSec: 8.5, topConfidence: 0.97f, cancellationToken: TestContext.Current.CancellationToken);
        await AsDbUser().CreateFaceAppearanceAsync(projectedFace.Id, FaceAppearanceHostType.Image, image.Id, sampleCount: 3, retainedSpatialSampleCount: 2, segmentCount: 1, firstSeenAtSec: null, lastSeenAtSec: null, topConfidence: 0.89f, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var appearances = await AsUser().GetFaceAppearancesAsync(evidenceFace.Id, TestContext.Current.CancellationToken);
        var detections = await AsUser().GetFaceDetectionsAsync(evidenceFace.Id, TestContext.Current.CancellationToken);
        var videoFaces = await AsUser().GetVideoFacesAsync(video, TestContext.Current.CancellationToken);
        var imageFaces = await AsUser().GetImageFacesAsync(image, TestContext.Current.CancellationToken);

        // Assert
        appearances.TotalCount.Should().Be(2);
        var videoAppearance = appearances.Items.Single(item => item.HostType == "video");
        videoAppearance.HostId.Should().Be(video.Id);
        videoAppearance.Title.Should().Be(video.Title);
        videoAppearance.ThumbnailUrl.Should().Be($"/api/stream/video/{video.Id}/screenshot?r={MediaCacheRevisions.VideoScreenshots}");
        videoAppearance.FrameSampleCount.Should().Be(1);
        videoAppearance.RetainedSpatialSampleCount.Should().Be(1);
        videoAppearance.SegmentCount.Should().Be(0);
        videoAppearance.FirstSeenAtSec.Should().Be(2);
        videoAppearance.LastSeenAtSec.Should().Be(2);
        videoAppearance.TopConfidence.Should().Be(0.95f);
        var imageAppearance = appearances.Items.Single(item => item.HostType == "image");
        imageAppearance.HostId.Should().Be(image.Id);
        imageAppearance.Title.Should().Be(image.Title);
        imageAppearance.ThumbnailUrl.Should().Be($"/api/stream/image/{image.Id}/thumbnail?max=320&r={MediaCacheRevisions.LibraryImages}");
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
    public async Task GivenUntitledHosts_WhenFaceAppearancesAreRead_ThenTitlesFallBackToFileBasename()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync(string.Empty, TestContext.Current.CancellationToken);
        var image = await AsUser().CreateImageAsync(string.Empty, TestContext.Current.CancellationToken);
        var filelessVideo = await AsUser().CreateVideoAsync(string.Empty, TestContext.Current.CancellationToken);
        await AsDbUser().AttachVideoFileAsync(video.Id, duration: 10, size: 1_000, cancellationToken: TestContext.Current.CancellationToken);
        await AsDbUser().AttachImageFileAsync(image.Id, size: 1_000, cancellationToken: TestContext.Current.CancellationToken);
        var detectedFace = await AsUser().CreateFaceAsync(new FaceCreateDto("Untitled detections", null, false, null), TestContext.Current.CancellationToken);
        var projectedFace = await AsUser().CreateFaceAsync(new FaceCreateDto("Untitled appearances", null, false, null), TestContext.Current.CancellationToken);
        await AsUser().CreateVideoFaceDetectionAsync(video, detectedFace, TestContext.Current.CancellationToken);
        await AsUser().CreateImageFaceDetectionAsync(image, detectedFace, TestContext.Current.CancellationToken);
        await AsDbUser().CreateFaceAppearanceAsync(projectedFace.Id, FaceAppearanceHostType.Video, video.Id, sampleCount: 1, retainedSpatialSampleCount: 1, segmentCount: 1, firstSeenAtSec: 1, lastSeenAtSec: 2, topConfidence: 0.9f, cancellationToken: TestContext.Current.CancellationToken);
        await AsDbUser().CreateFaceAppearanceAsync(projectedFace.Id, FaceAppearanceHostType.Video, filelessVideo.Id, sampleCount: 1, retainedSpatialSampleCount: 1, segmentCount: 1, firstSeenAtSec: 1, lastSeenAtSec: 2, topConfidence: 0.9f, cancellationToken: TestContext.Current.CancellationToken);
        await AsDbUser().CreateFaceAppearanceAsync(projectedFace.Id, FaceAppearanceHostType.Image, image.Id, sampleCount: 1, retainedSpatialSampleCount: 1, segmentCount: 1, firstSeenAtSec: null, lastSeenAtSec: null, topConfidence: 0.9f, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var fromDetections = await AsUser().GetFaceAppearancesAsync(detectedFace.Id, TestContext.Current.CancellationToken);
        var fromAppearances = await AsUser().GetFaceAppearancesAsync(projectedFace.Id, TestContext.Current.CancellationToken);

        // Assert
        foreach (var appearances in new[] { fromDetections, fromAppearances })
        {
            appearances.Items.Single(item => item.HostType == "video" && item.HostId == video.Id).Title.Should().Be("aggregate-source.mp4");
            appearances.Items.Single(item => item.HostType == "image").Title.Should().Be("aggregate-source.png");
        }
        fromAppearances.Items.Single(item => item.HostId == filelessVideo.Id).Title.Should().Be($"Video {filelessVideo.Id}");
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
