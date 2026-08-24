using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;

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
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder().Build(), TestContext.Current.CancellationToken);
        var video = await AsUser().CreateVideoAsync($"Face merge host {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var sourceOnlyImage = await AsUser().CreateImageAsync($"Source-only face merge host {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var source = await AsUser().CreateFaceAsync(new FaceCreateDto("Source identity", null, false, null), TestContext.Current.CancellationToken);
        var target = await AsUser().CreateFaceAsync(new FaceCreateDto(null, null, false, null), TestContext.Current.CancellationToken);
        await AsDbUser().CreateFaceAppearanceAsync(source.Id, FaceAppearanceHostType.Video, video.Id, sampleCount: 2, retainedSpatialSampleCount: 2, segmentCount: 1, firstSeenAtSec: 1, lastSeenAtSec: 2, topConfidence: 0.80f, cancellationToken: TestContext.Current.CancellationToken);
        await AsDbUser().CreateFaceAppearanceAsync(source.Id, FaceAppearanceHostType.Image, sourceOnlyImage.Id, sampleCount: 3, retainedSpatialSampleCount: 2, segmentCount: 1, firstSeenAtSec: null, lastSeenAtSec: null, topConfidence: 0.93f, cancellationToken: TestContext.Current.CancellationToken);
        await AsDbUser().CreateFaceAppearanceAsync(target.Id, FaceAppearanceHostType.Video, video.Id, sampleCount: 4, retainedSpatialSampleCount: 3, segmentCount: 1, firstSeenAtSec: 3, lastSeenAtSec: 7, topConfidence: 0.99f, cancellationToken: TestContext.Current.CancellationToken);
        source = await AsUser().LinkFaceAsync(source.Id, new FaceLinkDto(performer.Id, SetPerformerImage: false), TestContext.Current.CancellationToken);

        // Act
        var merged = await AsUser(ApiTestUsers.Eva).MergeFaceIntoAsync(source.Id, target.Id, TestContext.Current.CancellationToken);
        var sourceAfter = await AsUser().GetFaceByIdAsync(source.Id, TestContext.Current.CancellationToken);
        var targetAfter = await AsUser().GetFaceByIdAsync(target.Id, TestContext.Current.CancellationToken);
        var performerFaces = await AsUser().GetPerformerFacesAsync(performer.Id, TestContext.Current.CancellationToken);
        var videoFaces = await AsUser().GetVideoFacesAsync(video, TestContext.Current.CancellationToken);
        var sourceOnlyImageFaces = await AsUser().GetImageFacesAsync(sourceOnlyImage, TestContext.Current.CancellationToken);
        var performerVideos = await AsUser().GetVideosByPerformerAsync(performer.Id, TestContext.Current.CancellationToken);
        var performerImages = await AsUser().GetImagesByPerformerAsync(performer.Id, TestContext.Current.CancellationToken);

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
        performerImages.Should().ContainSingle(candidate => candidate.Id == sourceOnlyImage.Id);
    }

    [Fact]
    public async Task GivenMergedFaces_WhenSurvivorIsDeleted_ThenSourceReturnsWithItsOriginalEvidence()
    {
        // Arrange
        var sourceOnlyImage = await AsUser().CreateImageAsync($"Merge deletion source host {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var targetOnlyImage = await AsUser().CreateImageAsync($"Merge deletion target host {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder().Build(), TestContext.Current.CancellationToken);
        var source = await AsUser().CreateFaceAsync(new FaceCreateDto("Source", null, false, null), TestContext.Current.CancellationToken);
        var target = await AsUser().CreateFaceAsync(new FaceCreateDto("Target", null, false, null), TestContext.Current.CancellationToken);
        await AsDbUser().CreateFaceAppearanceAsync(source.Id, FaceAppearanceHostType.Image, sourceOnlyImage.Id, sampleCount: 2, retainedSpatialSampleCount: 2, segmentCount: 0, firstSeenAtSec: null, lastSeenAtSec: null, topConfidence: 0.90f, cancellationToken: TestContext.Current.CancellationToken);
        await AsDbUser().CreateFaceAppearanceAsync(target.Id, FaceAppearanceHostType.Image, targetOnlyImage.Id, sampleCount: 1, retainedSpatialSampleCount: 1, segmentCount: 0, firstSeenAtSec: null, lastSeenAtSec: null, topConfidence: 0.80f, cancellationToken: TestContext.Current.CancellationToken);
        await AsUser().LinkFaceAsync(source.Id, new FaceLinkDto(performer.Id, SetPerformerImage: false), TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).MergeFaceIntoAsync(source.Id, target.Id, TestContext.Current.CancellationToken);

        // Act
        await AsUser().DeleteFaceAsync(target.Id, TestContext.Current.CancellationToken);

        // Assert
        var sourceAfter = await AsUser().GetFaceByIdAsync(source.Id, TestContext.Current.CancellationToken);
        var sourceHostFaces = await AsUser().GetImageFacesAsync(sourceOnlyImage, TestContext.Current.CancellationToken);
        var targetHostFaces = await AsUser().GetImageFacesAsync(targetOnlyImage, TestContext.Current.CancellationToken);
        var performerImages = await AsUser().GetImagesByPerformerAsync(performer.Id, TestContext.Current.CancellationToken);
        sourceAfter.MergedIntoFaceId.Should().BeNull();
        sourceHostFaces.Should().ContainSingle(candidate => candidate.Id == source.Id);
        targetHostFaces.Should().BeEmpty();
        performerImages.Should().ContainSingle(candidate => candidate.Id == sourceOnlyImage.Id);
    }

    [Fact]
    public async Task GivenDifferentlyLinkedFaces_WhenMerged_ThenSourceHostUsesSurvivorPerformer()
    {
        // Arrange
        var sourcePerformer = await AsUser().CreatePerformerAsync(new PerformerBuilder().Build(), TestContext.Current.CancellationToken);
        var targetPerformer = await AsUser().CreatePerformerAsync(new PerformerBuilder().Build(), TestContext.Current.CancellationToken);
        var sourceOnlyImage = await AsUser().CreateImageAsync($"Merge propagation source host {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var source = await AsUser().CreateFaceAsync(new FaceCreateDto("Source", null, false, null), TestContext.Current.CancellationToken);
        var target = await AsUser().CreateFaceAsync(new FaceCreateDto("Target", null, false, null), TestContext.Current.CancellationToken);
        await AsDbUser().CreateFaceAppearanceAsync(source.Id, FaceAppearanceHostType.Image, sourceOnlyImage.Id, sampleCount: 2, retainedSpatialSampleCount: 2, segmentCount: 0, firstSeenAtSec: null, lastSeenAtSec: null, topConfidence: 0.90f, cancellationToken: TestContext.Current.CancellationToken);
        await AsUser().LinkFaceAsync(source.Id, new FaceLinkDto(sourcePerformer.Id, SetPerformerImage: false), TestContext.Current.CancellationToken);
        await AsUser().LinkFaceAsync(target.Id, new FaceLinkDto(targetPerformer.Id, SetPerformerImage: false), TestContext.Current.CancellationToken);

        // Act
        await AsUser(ApiTestUsers.Eva).MergeFaceIntoAsync(source.Id, target.Id, TestContext.Current.CancellationToken);

        // Assert
        var sourcePerformerImages = await AsUser().GetImagesByPerformerAsync(sourcePerformer.Id, TestContext.Current.CancellationToken);
        var targetPerformerImages = await AsUser().GetImagesByPerformerAsync(targetPerformer.Id, TestContext.Current.CancellationToken);
        sourcePerformerImages.Should().NotContain(candidate => candidate.Id == sourceOnlyImage.Id);
        targetPerformerImages.Should().ContainSingle(candidate => candidate.Id == sourceOnlyImage.Id);
    }

    [Fact]
    public async Task GivenMergeChain_WhenIntermediateFaceIsDeleted_ThenItsChildRemainsMergedIntoSurvivor()
    {
        // Arrange
        var image = await AsUser().CreateImageAsync($"Merge chain host {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var child = await AsUser().CreateFaceAsync(new FaceCreateDto("Child", null, false, null), TestContext.Current.CancellationToken);
        var intermediate = await AsUser().CreateFaceAsync(new FaceCreateDto("Intermediate", null, false, null), TestContext.Current.CancellationToken);
        var survivor = await AsUser().CreateFaceAsync(new FaceCreateDto("Survivor", null, false, null), TestContext.Current.CancellationToken);
        await AsDbUser().CreateFaceAppearanceAsync(child.Id, FaceAppearanceHostType.Image, image.Id, sampleCount: 1, retainedSpatialSampleCount: 1, segmentCount: 0, firstSeenAtSec: null, lastSeenAtSec: null, topConfidence: 0.90f, cancellationToken: TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).MergeFaceIntoAsync(child.Id, intermediate.Id, TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).MergeFaceIntoAsync(intermediate.Id, survivor.Id, TestContext.Current.CancellationToken);

        // Act
        await AsUser().DeleteFaceAsync(intermediate.Id, TestContext.Current.CancellationToken);

        // Assert
        var childAfter = await AsUser().GetFaceByIdAsync(child.Id, TestContext.Current.CancellationToken);
        var imageFaces = await AsUser().GetImageFacesAsync(image, TestContext.Current.CancellationToken);
        childAfter.MergedIntoFaceId.Should().Be(survivor.Id);
        imageFaces.Should().ContainSingle(candidate => candidate.Id == survivor.Id);
        imageFaces.Should().NotContain(candidate => candidate.Id == child.Id);
    }

    [Fact]
    public async Task GivenFace_WhenMergeTargetIsSelfMissingOrAlreadyMerged_ThenStateIsPreserved()
    {
        // Arrange
        var source = await AsUser().CreateFaceAsync(new FaceCreateDto("Source", null, false, null), TestContext.Current.CancellationToken);
        var mergedTarget = await AsUser().CreateFaceAsync(new FaceCreateDto("Merged target", null, false, null), TestContext.Current.CancellationToken);
        var survivor = await AsUser().CreateFaceAsync(new FaceCreateDto("Survivor", null, false, null), TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).MergeFaceIntoAsync(mergedTarget.Id, survivor.Id, TestContext.Current.CancellationToken);

        // Act
        var self = () => AsUser(ApiTestUsers.Eva).MergeFaceIntoAsync(source.Id, source.Id);
        var missing = () => AsUser(ApiTestUsers.Eva).MergeFaceIntoAsync(source.Id, int.MaxValue);
        var alreadyMerged = () => AsUser(ApiTestUsers.Eva).MergeFaceIntoAsync(source.Id, mergedTarget.Id);
        var alreadyMergedSource = () => AsUser(ApiTestUsers.Eva).MergeFaceIntoAsync(mergedTarget.Id, source.Id);
        var missingSource = () => AsUser(ApiTestUsers.Eva).MergeFaceIntoAsync(int.MaxValue, survivor.Id);

        // Assert
        await self.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*");
        await missing.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*");
        await alreadyMerged.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*");
        await alreadyMergedSource.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*");
        await missingSource.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
        var sourceAfter = await AsUser().GetFaceByIdAsync(source.Id, TestContext.Current.CancellationToken);
        sourceAfter.MergedIntoFaceId.Should().BeNull();
    }
}
