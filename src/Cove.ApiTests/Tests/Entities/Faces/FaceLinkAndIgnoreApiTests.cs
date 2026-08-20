using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Entities.Faces;

[Collection(ApiTestLane2Collection.Name)]
public sealed class FaceLinkAndIgnoreApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/faces/{id:int}/link")]
    public async Task GivenFace_WhenMemberLinksUnlinksAndRelinksIt_ThenRelationshipFollowsRequest()
    {
        // Arrange
        var firstPerformer = await AsUser().CreatePerformerAsync(new PerformerBuilder().Build());
        var secondPerformer = await AsUser().CreatePerformerAsync(new PerformerBuilder().Build());
        var video = await AsUser().CreateVideoAsync($"Face host {Guid.NewGuid():N}");
        var face = await AsUser().CreateFaceAsync(new FaceCreateDto("Candidate", null, false, null));
        await AsUser().CreateVideoFaceDetectionAsync(video, face);

        // Act
        var firstLink = await AsUser(ApiTestUsers.Eva).LinkFaceAsync(face.Id, new FaceLinkDto(firstPerformer.Id));
        var firstPerformerVideosAfterLink = await AsUser().GetVideosByPerformerAsync(firstPerformer.Id);
        var unlinked = await AsUser(ApiTestUsers.Eva).LinkFaceAsync(face.Id, new FaceLinkDto(null));
        var firstPerformerVideosAfterUnlink = await AsUser().GetVideosByPerformerAsync(firstPerformer.Id);
        var relinked = await AsUser(ApiTestUsers.Eva).LinkFaceAsync(face.Id, new FaceLinkDto(secondPerformer.Id));
        var retrieved = await AsUser().GetFaceByIdAsync(face.Id);
        var firstPerformerVideosAfterRelink = await AsUser().GetVideosByPerformerAsync(firstPerformer.Id);
        var secondPerformerVideosAfterRelink = await AsUser().GetVideosByPerformerAsync(secondPerformer.Id);

        // Assert
        firstLink.PerformerId.Should().Be(firstPerformer.Id);
        firstLink.PerformerName.Should().Be(firstPerformer.Name);
        firstPerformerVideosAfterLink.Should().ContainSingle(candidate => candidate.Id == video.Id);
        unlinked.PerformerId.Should().BeNull();
        unlinked.PerformerName.Should().BeNull();
        firstPerformerVideosAfterUnlink.Should().NotContain(candidate => candidate.Id == video.Id);
        relinked.PerformerId.Should().Be(secondPerformer.Id);
        retrieved.PerformerId.Should().Be(secondPerformer.Id);
        retrieved.PerformerName.Should().Be(secondPerformer.Name);
        firstPerformerVideosAfterRelink.Should().NotContain(candidate => candidate.Id == video.Id);
        secondPerformerVideosAfterRelink.Should().ContainSingle(candidate => candidate.Id == video.Id);
    }

    [Fact]
    public async Task GivenMissingPerformer_WhenFaceIsLinked_ThenValidationProblemPreservesRelationship()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder().Build());
        var face = await AsUser().CreateFaceAsync(new FaceCreateDto("Candidate", performer.Id, false, null));

        // Act
        var action = () => AsUser(ApiTestUsers.Eva).LinkFaceAsync(face.Id, new FaceLinkDto(int.MaxValue));

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*");
        var retrieved = await AsUser().GetFaceByIdAsync(face.Id);
        retrieved.PerformerId.Should().Be(performer.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/faces/{id:int}/ignore")]
    public async Task GivenFace_WhenMemberChangesIgnoredState_ThenStateCanBeSetAndCleared()
    {
        // Arrange
        var face = await AsUser().CreateFaceAsync(new FaceCreateDto("Candidate", null, false, null));

        // Act
        var ignored = await AsUser(ApiTestUsers.Eva).SetFaceIgnoredAsync(face.Id, ignored: true);
        var restored = await AsUser(ApiTestUsers.Eva).SetFaceIgnoredAsync(face.Id, ignored: false);
        var retrieved = await AsUser().GetFaceByIdAsync(face.Id);

        // Assert
        ignored.Ignored.Should().BeTrue();
        restored.Ignored.Should().BeFalse();
        retrieved.Ignored.Should().BeFalse();
    }
}
