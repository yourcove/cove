using Cove.Api.Services;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Tests.Entities.Faces;

public sealed class FaceCoverSourceApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/faces/{id:int}/image-sources")]
    public async Task GivenFaceWithUploadedCover_WhenImageSourcesAreRead_ThenCoverHasNoSourceAndOnlyItsOwnDetectionsAreDescribed()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Cover source video {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var face = await AsUser().CreateFaceAsync(new FaceCreateDto("Cover source candidate", null, false, null), TestContext.Current.CancellationToken);
        var otherFace = await AsUser().CreateFaceAsync(new FaceCreateDto("Another candidate", null, false, null), TestContext.Current.CancellationToken);
        var detection = await AsUser().CreateVideoFaceDetectionAsync(video, face, TestContext.Current.CancellationToken);
        var foreignDetection = await AsUser().CreateVideoFaceDetectionAsync(video, otherFace, TestContext.Current.CancellationToken);
        await AsUser().UploadFaceImageAsync(face, ApiTestImages.OnePixelPng(), TestContext.Current.CancellationToken);

        // Act
        var sources = await AsUser().GetFaceImageSourcesAsync(face.Id, [detection.Id, foreignDetection.Id], TestContext.Current.CancellationToken);

        // Assert
        sources.Cover.Should().Be(new FaceCoverStateDto(HasStoredCover: true, DetectionId: null, SourceOnFace: false));
        sources.Sources.Should().Equal(new FaceImageSourceDto(detection.Id, "video", video.Id, video.Title!, 2, null));
    }

    [Fact]
    [CoversEndpoint("POST", "/api/faces/{id:int}/cover/detection")]
    public async Task GivenAnotherFacesDetection_WhenItIsChosenAsCover_ThenTheRequestIsRejectedAndNoCoverIsStored()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Cover choice video {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var face = await AsUser().CreateFaceAsync(new FaceCreateDto("Cover choice candidate", null, false, null), TestContext.Current.CancellationToken);
        var otherFace = await AsUser().CreateFaceAsync(new FaceCreateDto("Detection owner", null, false, null), TestContext.Current.CancellationToken);
        var foreignDetection = await AsUser().CreateVideoFaceDetectionAsync(video, otherFace, TestContext.Current.CancellationToken);

        // Act
        var choose = () => AsUser().SetFaceCoverFromDetectionAsync(face.Id, foreignDetection.Id, TestContext.Current.CancellationToken);

        // Assert
        await choose.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*");
        var sources = await AsUser().GetFaceImageSourcesAsync(face.Id, [], TestContext.Current.CancellationToken);
        sources.Cover.HasStoredCover.Should().BeFalse();
    }

    [Fact]
    public async Task GivenFaceMergedIntoAnother_WhenTheTargetIsRead_ThenItsDetectionsAndCountsIncludeTheMergedFace()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Merged evidence video {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var target = await AsUser().CreateFaceAsync(new FaceCreateDto("Merge target", null, false, null), TestContext.Current.CancellationToken);
        var merged = await AsUser().CreateFaceAsync(new FaceCreateDto("Merged away", null, false, null), TestContext.Current.CancellationToken);
        var detection = await AsUser().CreateVideoFaceDetectionAsync(video, merged, TestContext.Current.CancellationToken);
        await AsUser().MergeFaceIntoAsync(merged.Id, target.Id, TestContext.Current.CancellationToken);

        // Act
        var detections = await AsUser().GetFaceDetectionsAsync(target.Id, TestContext.Current.CancellationToken);
        var face = await AsUser().GetFaceByIdAsync(target.Id, TestContext.Current.CancellationToken);
        var sources = await AsUser().GetFaceImageSourcesAsync(target.Id, [detection.Id], TestContext.Current.CancellationToken);

        // Assert
        detections.Select(item => item.Id).Should().Equal(detection.Id);
        face.DetectionCount.Should().Be(1);
        face.VideoCount.Should().Be(1);
        sources.Sources.Select(source => source.DetectionId).Should().Equal(detection.Id);
    }
}
