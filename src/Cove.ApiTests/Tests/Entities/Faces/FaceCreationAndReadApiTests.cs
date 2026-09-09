using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Tests.Entities.Faces;

public sealed class FaceCreationAndReadApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/faces")]
    [CoversEndpoint("GET", "/api/faces/{id:int}")]
    public async Task GivenFaceMetadata_WhenMemberCreatesAndReadsFace_ThenMetadataIsPersisted()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder().Build(), TestContext.Current.CancellationToken);
        var request = new FaceCreateDto(
            Label: "  Profile candidate  ",
            PerformerId: performer.Id,
            Ignored: false,
            PrimarySourceKey: "  detector.primary  ");

        // Act
        var created = await AsUser(ApiTestUsers.Eva).CreateFaceAsync(request, TestContext.Current.CancellationToken);
        var retrieved = await AsUser(ApiTestUsers.Eva).GetFaceByIdAsync(created.Id, TestContext.Current.CancellationToken);

        // Assert
        created.Id.Should().BePositive();
        retrieved.Id.Should().Be(created.Id);
        retrieved.Label.Should().Be("Profile candidate");
        retrieved.PerformerId.Should().Be(performer.Id);
        retrieved.PerformerName.Should().Be(performer.Name);
        retrieved.Ignored.Should().BeFalse();
        retrieved.PrimarySourceKey.Should().Be("detector.primary");
        retrieved.DetectionCount.Should().Be(0);
        retrieved.AppearanceCount.Should().Be(0);
    }

    [Fact]
    public async Task GivenMissingPerformer_WhenFaceIsCreated_ThenValidationProblemIsReturned()
    {
        // Arrange
        var request = new FaceCreateDto(
            Label: "Unresolved candidate",
            PerformerId: int.MaxValue,
            Ignored: false,
            PrimarySourceKey: null);

        // Act
        var action = () => AsUser(ApiTestUsers.Eva).CreateFaceAsync(request);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*");
    }

    [Fact]
    public async Task GivenMissingFace_WhenMemberReadsIt_ThenNotFoundIsReturned()
    {
        // Arrange
        const int missingId = int.MaxValue;

        // Act
        var action = () => AsUser(ApiTestUsers.Eva).GetFaceByIdAsync(missingId);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }
}
