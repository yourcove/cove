using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;

namespace Cove.ApiTests.Tests.Entities.Performers;

[Collection(ApiTestLane1Collection.Name)]
public sealed class PerformerReadApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenPerformer_WhenMemberReadsPerformers_ThenPerformerIsReturned()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .Build(), TestContext.Current.CancellationToken);

        // Act
        var performers = await AsUser(ApiTestUsers.Eva).GetPerformersAsync(TestContext.Current.CancellationToken);

        // Assert
        performers.Should().ContainSingle(candidate => candidate.Id == performer.Id);
    }

    [Fact]
    public async Task GivenMissingPerformer_WhenRead_ThenNotFoundIsReturned()
    {
        // Arrange
        const int missingId = int.MaxValue;

        // Act
        var action = () => AsUser().GetPerformerByIdAsync(missingId);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }
}
