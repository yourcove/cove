using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;

namespace Cove.ApiTests.Tests.Entities.Studios;

[Collection(ApiTestLane2Collection.Name)]
public sealed class StudioReadApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenStudio_WhenMemberReadsStudios_ThenStudioIsReturned()
    {
        // Arrange
        var studio = await AsUser().CreateStudioAsync(TestCatalog.Studio.Name);

        // Act
        var studios = await AsUser(ApiTestUsers.Eva).GetStudiosAsync();

        // Assert
        studios.Should().ContainSingle(candidate => candidate.Id == studio.Id);
    }

    [Fact]
    public async Task GivenMissingStudio_WhenRead_ThenNotFoundIsReturned()
    {
        // Arrange
        const int missingId = int.MaxValue;

        // Act
        var action = () => AsUser().GetStudioByIdAsync(missingId);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }
}
