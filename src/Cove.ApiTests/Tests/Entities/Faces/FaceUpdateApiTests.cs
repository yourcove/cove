using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Tests.Entities.Faces;

public sealed class FaceUpdateApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("PUT", "/api/faces/{id:int}")]
    public async Task GivenFace_WhenMemberUpdatesIt_ThenReplacementMetadataIsPersisted()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder().Build(), TestContext.Current.CancellationToken);
        var face = await AsUser().CreateFaceAsync(new FaceCreateDto(
            Label: "Original label",
            PerformerId: null,
            Ignored: false,
            PrimarySourceKey: "original.source"), TestContext.Current.CancellationToken);
        var request = new FaceUpdateDto(
            Label: "  Updated label  ",
            PerformerId: performer.Id,
            Ignored: true,
            PrimarySourceKey: "  replacement.source  ");

        // Act
        var updated = await AsUser(ApiTestUsers.Eva).UpdateFaceAsync(face.Id, request, TestContext.Current.CancellationToken);
        var retrieved = await AsUser().GetFaceByIdAsync(face.Id, TestContext.Current.CancellationToken);

        // Assert
        updated.Label.Should().Be("Updated label");
        retrieved.Label.Should().Be("Updated label");
        retrieved.PerformerId.Should().Be(performer.Id);
        retrieved.PerformerName.Should().Be(performer.Name);
        retrieved.Ignored.Should().BeTrue();
        retrieved.PrimarySourceKey.Should().Be("replacement.source");
    }
}
