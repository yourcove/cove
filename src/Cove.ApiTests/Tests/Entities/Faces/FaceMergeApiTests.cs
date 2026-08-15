using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Entities.Faces;

[Collection(ApiTestLane2Collection.Name)]
public sealed class FaceMergeApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/faces/{id:int}/merge-into")]
    public async Task GivenTwoFaces_WhenSourceIsMerged_ThenOnlyTargetRemainsVisibleAndInheritsIdentity()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder().Build());
        var video = await AsUser().CreateVideoAsync($"Face merge host {Guid.NewGuid():N}");
        var sourceOnlyImage = await AsUser().CreateImageAsync($"Source-only face merge host {Guid.NewGuid():N}");
        var source = await AsUser().CreateFaceAsync(new FaceCreateDto("Source identity", performer.Id, false, null));
        var target = await AsUser().CreateFaceAsync(new FaceCreateDto(null, null, false, null));
        await AsDbUser().CreateFaceAppearanceAsync(
            source.Id,
            FaceAppearanceHostType.Video,
            video.Id,
            sampleCount: 2,
            retainedSpatialSampleCount: 2,
            segmentCount: 1,
            firstSeenAtSec: 1,
            lastSeenAtSec: 2,
            topConfidence: 0.80f);
        await AsDbUser().CreateFaceAppearanceAsync(
            source.Id,
            FaceAppearanceHostType.Image,
            sourceOnlyImage.Id,
            sampleCount: 3,
            retainedSpatialSampleCount: 2,
            segmentCount: 1,
            firstSeenAtSec: null,
            lastSeenAtSec: null,
            topConfidence: 0.93f);
        await AsDbUser().CreateFaceAppearanceAsync(
            target.Id,
            FaceAppearanceHostType.Video,
            video.Id,
            sampleCount: 4,
            retainedSpatialSampleCount: 3,
            segmentCount: 1,
            firstSeenAtSec: 3,
            lastSeenAtSec: 7,
            topConfidence: 0.99f);

        // Act
        var merged = await AsUser(ApiTestUsers.Eva).MergeFaceIntoAsync(source.Id, target.Id);
        var sourceAfter = await AsUser().GetFaceByIdAsync(source.Id);
        var targetAfter = await AsUser().GetFaceByIdAsync(target.Id);
        var performerFaces = await AsUser().GetPerformerFacesAsync(performer.Id);
        var videoFaces = await AsUser().GetVideoFacesAsync(video);
        var sourceOnlyImageFaces = await AsUser().GetImageFacesAsync(sourceOnlyImage);
        var performerVideos = await AsUser().GetVideosByPerformerAsync(performer.Id);

        // Assert
        merged.MergedIntoFaceId.Should().Be(target.Id);
        sourceAfter.MergedIntoFaceId.Should().Be(target.Id);
        targetAfter.MergedIntoFaceId.Should().BeNull();
        targetAfter.Label.Should().Be(source.Label);
        targetAfter.PerformerId.Should().Be(performer.Id);
        targetAfter.PerformerName.Should().Be(performer.Name);
        performerFaces.Should().ContainSingle(candidate => candidate.Id == target.Id);
        performerFaces.Should().NotContain(candidate => candidate.Id == source.Id);
        videoFaces.Should().ContainSingle(candidate => candidate.Id == target.Id);
        videoFaces.Should().NotContain(candidate => candidate.Id == source.Id);
        sourceOnlyImageFaces.Should().ContainSingle(candidate => candidate.Id == target.Id);
        sourceOnlyImageFaces.Should().NotContain(candidate => candidate.Id == source.Id);
        performerVideos.Should().ContainSingle(candidate => candidate.Id == video.Id);
    }

    [Fact]
    public async Task GivenFace_WhenMergeTargetIsSelfMissingOrAlreadyMerged_ThenStateIsPreserved()
    {
        // Arrange
        var source = await AsUser().CreateFaceAsync(new FaceCreateDto("Source", null, false, null));
        var mergedTarget = await AsUser().CreateFaceAsync(new FaceCreateDto("Merged target", null, false, null));
        var survivor = await AsUser().CreateFaceAsync(new FaceCreateDto("Survivor", null, false, null));
        await AsUser(ApiTestUsers.Eva).MergeFaceIntoAsync(mergedTarget.Id, survivor.Id);

        // Act
        var self = () => AsUser(ApiTestUsers.Eva).MergeFaceIntoAsync(source.Id, source.Id);
        var missing = () => AsUser(ApiTestUsers.Eva).MergeFaceIntoAsync(source.Id, int.MaxValue);
        var alreadyMerged = () => AsUser(ApiTestUsers.Eva).MergeFaceIntoAsync(source.Id, mergedTarget.Id);
        var missingSource = () => AsUser(ApiTestUsers.Eva).MergeFaceIntoAsync(int.MaxValue, survivor.Id);

        // Assert
        await self.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*");
        await missing.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*");
        await alreadyMerged.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*");
        await missingSource.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
        var sourceAfter = await AsUser().GetFaceByIdAsync(source.Id);
        sourceAfter.MergedIntoFaceId.Should().BeNull();
    }
}
