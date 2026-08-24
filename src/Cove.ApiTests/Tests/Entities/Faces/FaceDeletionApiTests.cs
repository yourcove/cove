using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Tests.Entities.Faces;

[Collection(ApiTestLane1Collection.Name)]
public sealed class FaceDeletionApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/faces/{id:int}/delete-impact")]
    [CoversEndpoint("DELETE", "/api/faces/{id:int}")]
    public async Task GivenDetectedFace_WhenMemberChecksImpactAndDeletesIt_ThenFaceAndDetectionAreRemoved()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Face host {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var face = await AsUser().CreateFaceAsync(new FaceCreateDto("Detected candidate", null, false, null), TestContext.Current.CancellationToken);
        var detection = await AsUser().CreateVideoFaceDetectionAsync(video, face, TestContext.Current.CancellationToken);

        // Act
        var impact = await AsUser(ApiTestUsers.Eva).GetFaceDeleteImpactAsync(face.Id, TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).DeleteFaceAsync(face.Id, TestContext.Current.CancellationToken);
        var detectionsAfter = await AsUser().GetVideoDetectionsAsync(video, TestContext.Current.CancellationToken);

        // Assert
        impact.Should().Be(new FaceDeleteImpactDto(
            DetectionCount: 1,
            EmbeddingCount: 0,
            SegmentCount: 0,
            HasCoverImage: false,
            ReleasedMergedFaceCount: 0));
        detectionsAfter.Should().NotContain(candidate => candidate.Id == detection.Id);
        var read = () => AsUser().GetFaceByIdAsync(face.Id);
        await read.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    public async Task GivenMissingFace_WhenMemberRequestsImpactOrDeletion_ThenNotFoundIsReturned()
    {
        // Arrange
        const int missingId = int.MaxValue;

        // Act
        var impact = () => AsUser(ApiTestUsers.Eva).GetFaceDeleteImpactAsync(missingId);
        var deletion = () => AsUser(ApiTestUsers.Eva).DeleteFaceAsync(missingId);

        // Assert
        await impact.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
        await deletion.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }
}
