using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;

namespace Cove.ApiTests.Tests.Entities.Faces;

public sealed class FacePerformerCreationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/faces/{id:int}/create-performer")]
    [CoversEndpoint("GET", "/api/performers/{performerId:int}/faces")]
    public async Task GivenUnlinkedFace_WhenPerformerIsCreatedFromIt_ThenBothSidesExposeTheRelationship()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Face performer host {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var face = await AsUser().CreateFaceAsync(new FaceCreateDto("Candidate label", null, false, null), TestContext.Current.CancellationToken);
        await AsDbUser().CreateFaceAppearanceAsync(face.Id, FaceAppearanceHostType.Video, video.Id, sampleCount: 1, retainedSpatialSampleCount: 1, segmentCount: 0, firstSeenAtSec: 2, lastSeenAtSec: 2, topConfidence: 0.95f, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var linkedFace = await AsUser(ApiTestUsers.Eva).CreatePerformerFromFaceAsync(face.Id, new FaceCreatePerformerDto("  New face performer  ", SetPerformerImage: false), TestContext.Current.CancellationToken);
        var performer = await AsUser().GetPerformerByIdAsync(linkedFace.PerformerId!.Value, TestContext.Current.CancellationToken);
        var performerFaces = await AsUser().GetPerformerFacesAsync(performer.Id, TestContext.Current.CancellationToken);
        var performerVideos = await AsUser().GetVideosByPerformerAsync(performer.Id, TestContext.Current.CancellationToken);

        // Assert
        linkedFace.PerformerName.Should().Be("New face performer");
        performer.Name.Should().Be("New face performer");
        performerFaces.Should().ContainSingle(candidate => candidate.Id == face.Id);
        performerFaces.Single().PerformerId.Should().Be(performer.Id);
        performerFaces.Single().PerformerFaceIndex.Should().Be(1);
        performerFaces.Single().PerformerFaceCount.Should().Be(1);
        performerVideos.Should().ContainSingle(candidate => candidate.Id == video.Id);
    }

    [Fact]
    public async Task GivenUnlinkedFace_WhenPerformerNameIsBlankOrConflicts_ThenFaceRemainsUnlinked()
    {
        // Arrange
        var existing = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName("Existing face performer").Build(), TestContext.Current.CancellationToken);
        var face = await AsUser().CreateFaceAsync(new FaceCreateDto("Candidate", null, false, null), TestContext.Current.CancellationToken);

        // Act
        var blankName = () => AsUser(ApiTestUsers.Eva).CreatePerformerFromFaceAsync(
            face.Id,
            new FaceCreatePerformerDto("   ", SetPerformerImage: false));
        var conflictingName = () => AsUser(ApiTestUsers.Eva).CreatePerformerFromFaceAsync(
            face.Id,
            new FaceCreatePerformerDto($" {existing.Name.ToUpperInvariant()} ", SetPerformerImage: false));

        // Assert
        await blankName.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*");
        await conflictingName.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 409 (Conflict)*PERFORMER_NAME_CONFLICT*");
        var retrieved = await AsUser().GetFaceByIdAsync(face.Id, TestContext.Current.CancellationToken);
        retrieved.PerformerId.Should().BeNull();
        retrieved.PerformerName.Should().BeNull();
    }

    [Fact]
    public async Task GivenMissingOrLinkedFace_WhenPerformerCreationIsRequested_ThenRequestIsRejected()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder().Build(), TestContext.Current.CancellationToken);
        var linkedFace = await AsUser().CreateFaceAsync(new FaceCreateDto("Linked", performer.Id, false, null), TestContext.Current.CancellationToken);

        // Act
        var missing = () => AsUser(ApiTestUsers.Eva).CreatePerformerFromFaceAsync(
            int.MaxValue,
            new FaceCreatePerformerDto("Unused", SetPerformerImage: false));
        var alreadyLinked = () => AsUser(ApiTestUsers.Eva).CreatePerformerFromFaceAsync(
            linkedFace.Id,
            new FaceCreatePerformerDto("Unused", SetPerformerImage: false));

        // Assert
        await missing.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
        await alreadyLinked.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*");
    }
}
