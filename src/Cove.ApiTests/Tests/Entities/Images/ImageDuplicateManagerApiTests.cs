using Cove.ApiTests.Infrastructure;

namespace Cove.ApiTests.Tests.Entities.Images;

public sealed class ImageDuplicateManagerApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/images/duplicate-searches")]
    [CoversEndpoint("GET", "/api/images/duplicate-searches/{searchid:guid}")]
    [CoversEndpoint("GET", "/api/images/duplicate-searches/{searchid:guid}/groups")]
    [CoversEndpoint("PATCH", "/api/images/duplicate-searches/{searchid:guid}/groups/{groupid:int}")]
    [CoversEndpoint("POST", "/api/images/duplicate-searches/{searchid:guid}/cleanup")]
    public async Task GivenExactImageHashes_WhenReviewedAndCleaned_ThenKeeperSurvives()
    {
        var owner = AsUser();
        var token = Guid.NewGuid().ToString("N");
        var keeper = await owner.CreateImageAsync($"Image duplicate keeper {token}", TestContext.Current.CancellationToken);
        var unwanted = await owner.CreateImageAsync($"Image duplicate unwanted {token}", TestContext.Current.CancellationToken);
        var keeperFileId = await AsDbUser().AttachImageFingerprintFileAsync(keeper.Id, token, 4_000, TestContext.Current.CancellationToken);
        await AsDbUser().AttachImageFingerprintFileAsync(unwanted.Id, token, 3_000, TestContext.Current.CancellationToken);

        var started = await owner.StartImageDuplicateSearchAsync(cancellationToken: TestContext.Current.CancellationToken);
        (await owner.WaitForTerminalJobAsync(started.JobId, TestContext.Current.CancellationToken)).Status.Should().Be(Cove.Core.Interfaces.JobStatus.Completed);
        (await owner.GetImageDuplicateSearchAsync(started.SearchId, TestContext.Current.CancellationToken)).GroupCount.Should().BeGreaterThan(0);
        var group = (await owner.GetImageDuplicateGroupsAsync(started.SearchId, TestContext.Current.CancellationToken)).Items
            .Should().ContainSingle(candidate => candidate.Files.Any(file => file.ImageId == keeper.Id) && candidate.Files.Any(file => file.ImageId == unwanted.Id)).Which;
        await owner.UpdateImageDuplicateKeeperAsync(started.SearchId, group.Id, keeperFileId, TestContext.Current.CancellationToken);

        var cleanup = await owner.CleanupImageDuplicatesAsync(started.SearchId, TestContext.Current.CancellationToken);
        (await owner.WaitForTerminalJobAsync(cleanup.JobId, TestContext.Current.CancellationToken)).Status.Should().Be(Cove.Core.Interfaces.JobStatus.Completed);
        (await owner.GetImageByIdAsync(keeper.Id, TestContext.Current.CancellationToken)).Id.Should().Be(keeper.Id);
        var removedRead = () => owner.GetImageByIdAsync(unwanted.Id, TestContext.Current.CancellationToken);
        await removedRead.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
    }
}
