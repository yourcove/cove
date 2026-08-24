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
        var video = await AsUser().CreateVideoAsync($"Face batch-delete host {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var first = await AsUser().CreateFaceAsync(new FaceCreateDto("First deletion", null, false, null), TestContext.Current.CancellationToken);
        var target = await AsUser().CreateFaceAsync(new FaceCreateDto("Target deletion", null, false, null), TestContext.Current.CancellationToken);
        var mergedChild = await AsUser().CreateFaceAsync(new FaceCreateDto("Released child", null, false, null), TestContext.Current.CancellationToken);
        var firstDetection = await AsUser().CreateVideoFaceDetectionAsync(video, first, TestContext.Current.CancellationToken);
        var targetDetection = await AsUser().CreateVideoFaceDetectionAsync(video, target, TestContext.Current.CancellationToken);
        await AsUser().MergeFaceIntoAsync(mergedChild.Id, target.Id, TestContext.Current.CancellationToken);
        const int missingId = int.MaxValue;

        // Act
        var result = await AsUser(ApiTestUsers.Eva).BatchDeleteFacesAsync([first.Id, first.Id, target.Id, missingId], TestContext.Current.CancellationToken);

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

        var releasedChild = await AsUser().GetFaceByIdAsync(mergedChild.Id, TestContext.Current.CancellationToken);
        releasedChild.MergedIntoFaceId.Should().BeNull();
        var detections = await AsUser().GetVideoDetectionsAsync(video, TestContext.Current.CancellationToken);
        detections.Should().NotContain(detection => detection.Id == firstDetection.Id || detection.Id == targetDetection.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/faces/{id:int}/suggestions/decision")]
    public async Task GivenLocalPerformerSuggestion_WhenMemberRejectsThenAcceptsIt_ThenDecisionAndHostLinkAreUpdated()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder().Build(), TestContext.Current.CancellationToken);
        var video = await AsUser().CreateVideoAsync($"Face suggestion host {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var face = await AsUser().CreateFaceAsync(new FaceCreateDto("Suggestion candidate", null, false, null), TestContext.Current.CancellationToken);
        await AsUser().CreateVideoFaceDetectionAsync(video, face, TestContext.Current.CancellationToken);

        // Act
        var rejected = await AsUser(ApiTestUsers.Eva).RecordFaceSuggestionDecisionAsync(face.Id, new FaceSuggestionDecisionDto(performer.Id, FaceSuggestionDecisionValues.Reject), TestContext.Current.CancellationToken);
        var videosAfterReject = await AsUser().GetVideosByPerformerAsync(performer.Id, TestContext.Current.CancellationToken);
        var accepted = await AsUser(ApiTestUsers.Eva).RecordFaceSuggestionDecisionAsync(face.Id, new FaceSuggestionDecisionDto(performer.Id, FaceSuggestionDecisionValues.Accept), TestContext.Current.CancellationToken);
        var retrieved = await AsUser().GetFaceByIdAsync(face.Id, TestContext.Current.CancellationToken);
        var videosAfterAccept = await AsUser().GetVideosByPerformerAsync(performer.Id, TestContext.Current.CancellationToken);

        // Assert
        rejected.PerformerId.Should().BeNull();
        videosAfterReject.Should().NotContain(candidate => candidate.Id == video.Id);
        accepted.PerformerId.Should().Be(performer.Id);
        accepted.PerformerName.Should().Be(performer.Name);
        retrieved.PerformerId.Should().Be(performer.Id);
        videosAfterAccept.Should().ContainSingle(candidate => candidate.Id == video.Id);
    }
}
