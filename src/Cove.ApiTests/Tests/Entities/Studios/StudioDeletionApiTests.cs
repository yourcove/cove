using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;

namespace Cove.ApiTests.Tests.Entities.Studios;

[Collection(ApiTestLane1Collection.Name)]
public sealed class StudioDeletionApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("DELETE", "/api/studios/{id:int}")]
    public async Task GivenStudio_WhenDeleted_ThenStudioCanNoLongerBeReadOrListed()
    {
        // Arrange
        var studio = await AsUser().CreateStudioAsync(TestCatalog.Studio.Name);

        // Act
        await AsUser().DeleteStudioAsync(studio.Id);

        // Assert
        (await AsUser().GetStudiosAsync()).Should().NotContain(candidate => candidate.Id == studio.Id);
        var read = () => AsUser().GetStudioByIdAsync(studio.Id);
        await read.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    public async Task GivenDeletedStudio_WhenOwnerDeletesItAgain_ThenNotFoundIsReturned()
    {
        // Arrange
        var studio = await AsUser().CreateStudioAsync(TestCatalog.Studio.Name);
        await AsUser().DeleteStudioAsync(studio.Id);

        // Act
        var action = () => AsUser().DeleteStudioAsync(studio.Id);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    public async Task GivenMember_WhenStudioIsDeleted_ThenForbiddenIsReturnedWithoutDeletingStudio()
    {
        // Arrange
        var studio = await AsUser().CreateStudioAsync(TestCatalog.Studio.Name);

        // Act
        var action = () => AsUser(ApiTestUsers.Eva).DeleteStudioAsync(studio.Id);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        var retrieved = await AsUser().GetStudioByIdAsync(studio.Id);
        retrieved.Id.Should().Be(studio.Id);
    }

    [Fact]
    public async Task GivenMissingStudio_WhenDeleted_ThenNotFoundIsReturned()
    {
        // Arrange
        const int missingId = int.MaxValue;

        // Act
        var action = () => AsUser().DeleteStudioAsync(missingId);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    public async Task GivenParentStudioWithChild_WhenParentIsDeleted_ThenChildIsPreservedWithoutParent()
    {
        // Arrange
        var parent = await AsUser().CreateStudioAsync("Temporary Parent");
        var child = await AsUser().CreateStudioAsync(
            new StudioBuilder().WithName(TestCatalog.Studio.Name).WithParent(parent).Build());

        // Act
        await AsUser().DeleteStudioAsync(parent.Id);
        var childAfter = await AsUser().GetStudioByIdAsync(child.Id);

        // Assert
        childAfter.ParentId.Should().BeNull();
        childAfter.ParentName.Should().BeNull();
    }
}
