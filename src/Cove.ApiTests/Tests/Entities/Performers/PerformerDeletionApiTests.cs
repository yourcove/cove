using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Entities.Performers;

[Collection(ApiTestLane1Collection.Name)]
public sealed class PerformerDeletionApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenPerformer_WhenDeleted_ThenPerformerCanNoLongerBeReadOrListed()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder().WithName(TestCatalog.Performers.CherryPoppins.Name).Build());

        // Act
        await AsUser().DeletePerformerAsync(performer.Id);

        // Assert
        (await AsUser().GetPerformersAsync()).Should().NotContain(candidate => candidate.Id == performer.Id);
        var read = () => AsUser().GetPerformerByIdAsync(performer.Id);
        await read.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    public async Task GivenDeletedPerformer_WhenOwnerDeletesItAgain_ThenNotFoundIsReturned()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder().WithName(TestCatalog.Performers.CherryPoppins.Name).Build());
        await AsUser().DeletePerformerAsync(performer.Id);

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
        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder().WithName(TestCatalog.Performers.BeaHaven.Name).Build());

        // Act
        var action = () => AsUser(ApiTestUsers.Eva).DeletePerformerAsync(performer.Id);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        var retrieved = await AsUser().GetPerformerByIdAsync(performer.Id);
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
