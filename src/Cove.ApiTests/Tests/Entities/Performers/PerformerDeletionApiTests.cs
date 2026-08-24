using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;

namespace Cove.ApiTests.Tests.Entities.Performers;

[Collection(ApiTestLane1Collection.Name)]
public sealed class PerformerDeletionApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("DELETE", "/api/performers/{id:int}")]
    public async Task GivenPerformer_WhenDeleted_ThenPerformerCanNoLongerBeReadOrListed()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName(TestCatalog.Performers.CherryPoppins.Name).Build(), TestContext.Current.CancellationToken);

        // Act
        await AsUser().DeletePerformerAsync(performer.Id, TestContext.Current.CancellationToken);

        // Assert
        (await AsUser().GetPerformersAsync(TestContext.Current.CancellationToken)).Should().NotContain(candidate => candidate.Id == performer.Id);
        var read = () => AsUser().GetPerformerByIdAsync(performer.Id);
        await read.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    public async Task GivenDeletedPerformer_WhenOwnerDeletesItAgain_ThenNotFoundIsReturned()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName(TestCatalog.Performers.CherryPoppins.Name).Build(), TestContext.Current.CancellationToken);
        await AsUser().DeletePerformerAsync(performer.Id, TestContext.Current.CancellationToken);

        // Act
        var action = () => AsUser().DeletePerformerAsync(performer.Id);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    public async Task GivenMember_WhenPerformerIsDeleted_ThenForbiddenIsReturnedWithoutDeletingPerformer()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName(TestCatalog.Performers.BeaHaven.Name).Build(), TestContext.Current.CancellationToken);

        // Act
        var action = () => AsUser(ApiTestUsers.Eva).DeletePerformerAsync(performer.Id);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        var retrieved = await AsUser().GetPerformerByIdAsync(performer.Id, TestContext.Current.CancellationToken);
        retrieved.Id.Should().Be(performer.Id);
    }

    [Fact]
    public async Task GivenMissingPerformer_WhenDeleted_ThenNotFoundIsReturned()
    {
        // Arrange
        const int missingId = int.MaxValue;

        // Act
        var action = () => AsUser().DeletePerformerAsync(missingId);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }
}
