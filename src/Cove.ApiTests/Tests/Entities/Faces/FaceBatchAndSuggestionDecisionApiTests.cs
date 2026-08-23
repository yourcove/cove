using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;

namespace Cove.ApiTests.Tests.Entities.Faces;

[Collection(ApiTestLane2Collection.Name)]
public sealed class FaceBatchAndSuggestionDecisionApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/faces/batch/delete")]
    public async Task GivenDuplicateMissingAndMergedFaces_WhenBatchDeleteRuns_ThenResultsAndRelationshipsAreConsistent()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Face batch-delete host {Guid.NewGuid():N}");
        var first = await AsUser().CreateFaceAsync(new FaceCreateDto("First deletion", null, false, null));
        var target = await AsUser().CreateFaceAsync(new FaceCreateDto("Target deletion", null, false, null));
        var mergedChild = await AsUser().CreateFaceAsync(new FaceCreateDto("Released child", null, false, null));
        var firstDetection = await AsUser().CreateVideoFaceDetectionAsync(video, first);
        var targetDetection = await AsUser().CreateVideoFaceDetectionAsync(video, target);
        await AsUser().MergeFaceIntoAsync(mergedChild.Id, target.Id);
        const int missingId = int.MaxValue;

        // Act
        var result = await AsUser(ApiTestUsers.Eva).BatchDeleteFacesAsync(
            [first.Id, first.Id, target.Id, missingId]);

        // Assert
        result.Succeeded.Should().Equal(first.Id, target.Id);
        result.Skipped.Should().ContainSingle();
        result.Skipped.Single().FaceId.Should().Be(missingId);
        result.Failed.Should().BeEmpty();

        var firstRead = () => AsUser().GetFaceByIdAsync(first.Id);
        var targetRead = () => AsUser().GetFaceByIdAsync(target.Id);
        await firstRead.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
        await targetRead.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");

        var releasedChild = await AsUser().GetFaceByIdAsync(mergedChild.Id);
        releasedChild.MergedIntoFaceId.Should().BeNull();
        var detections = await AsUser().GetVideoDetectionsAsync(video);
        detections.Should().NotContain(detection => detection.Id == firstDetection.Id || detection.Id == targetDetection.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/faces/{id:int}/suggestions/decision")]
    public async Task GivenLocalPerformerSuggestion_WhenMemberRejectsThenAcceptsIt_ThenDecisionAndHostLinkAreUpdated()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder().Build());
        var video = await AsUser().CreateVideoAsync($"Face suggestion host {Guid.NewGuid():N}");
        var face = await AsUser().CreateFaceAsync(new FaceCreateDto("Suggestion candidate", null, false, null));
        await AsUser().CreateVideoFaceDetectionAsync(video, face);

        // Act
        var rejected = await AsUser(ApiTestUsers.Eva).RecordFaceSuggestionDecisionAsync(
            face.Id,
            new FaceSuggestionDecisionDto(performer.Id, FaceSuggestionDecisionValues.Reject));
        var videosAfterReject = await AsUser().GetVideosByPerformerAsync(performer.Id);
        var accepted = await AsUser(ApiTestUsers.Eva).RecordFaceSuggestionDecisionAsync(
            face.Id,
            new FaceSuggestionDecisionDto(performer.Id, FaceSuggestionDecisionValues.Accept));
        var retrieved = await AsUser().GetFaceByIdAsync(face.Id);
        var videosAfterAccept = await AsUser().GetVideosByPerformerAsync(performer.Id);

        // Assert
        rejected.PerformerId.Should().BeNull();
        videosAfterReject.Should().NotContain(candidate => candidate.Id == video.Id);
        accepted.PerformerId.Should().Be(performer.Id);
        accepted.PerformerName.Should().Be(performer.Name);
        retrieved.PerformerId.Should().Be(performer.Id);
        videosAfterAccept.Should().ContainSingle(candidate => candidate.Id == video.Id);
    }
}
