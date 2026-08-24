using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Files;

[Collection(ApiTestLane2Collection.Name)]
public sealed class FileAndMigrationEndpointApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/performers/{id:int}/image")]
    [CoversEndpoint("GET", "/api/performers/{id:int}/image")]
    public async Task GivenPerformer_WhenImageIsUploaded_ThenImageCanBeRead()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .Build(), TestContext.Current.CancellationToken);
        var image = ApiTestImages.OnePixelPng();

        // Act
        await AsUser().UploadPerformerImageAsync(performer, image, TestContext.Current.CancellationToken);

        // Assert
        var uploaded = await AsUser().GetPerformerImageAsync(performer, TestContext.Current.CancellationToken);
        uploaded.MediaType.Should().Be("image/png");
        uploaded.Content.Should().Equal(image);
    }

    [Fact]
    [CoversEndpoint("GET", "/api/files/browse")]
    public async Task GivenLibraryFile_WhenDirectoryIsBrowsed_ThenFileIsListed()
    {
        // Arrange
        var filePath = AsTestFileSystem().CreateTextFile("API test file");

        // Act
        var entries = await AsUser().BrowseDirectoryAsync(AsTestFileSystem().LibraryPath, TestContext.Current.CancellationToken);

        // Assert
        entries.Should().Contain(entry => entry.Path == filePath && !entry.IsDirectory);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/stash-migration/preview")]
    public async Task GivenEmptyStashDatabase_WhenMigrationIsPreviewed_ThenPreviewIsValidAndEmpty()
    {
        // Arrange
        var databasePath = await AsTestFileSystem().CreateEmptyStashDatabaseAsync(TestContext.Current.CancellationToken);

        // Act
        var preview = await AsUser().PreviewStashMigrationAsync(databasePath, TestContext.Current.CancellationToken);

        // Assert
        preview.IsValid.Should().BeTrue();
        preview.Error.Should().BeNull();
        new[]
        {
            preview.Videos,
            preview.Performers,
            preview.Tags,
            preview.Studios,
            preview.Groups,
            preview.Images,
            preview.Galleries,
        }.Should().OnlyContain(count => count == 0);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/stash-migration/import")]
    [CoversEndpoint("GET", "/api/stash-migration/import/{jobid}")]
    public async Task GivenImportableEmptyStashDatabase_WhenImportRuns_ThenJobResultAndExistingStateAreExact()
    {
        var databasePath = await AsTestFileSystem().CreateImportableEmptyStashDatabaseAsync(TestContext.Current.CancellationToken);
        var control = await AsUser().CreateVideoAsync($"Stash import control {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var historyBefore = (await AsUser().GetJobHistoryAsync(TestContext.Current.CancellationToken)).Select(job => job.Id).ToArray();

        var forbidden = () => AsUser(ApiTestUsers.Eva).StartStashImportAsync(
            databasePath,
            migrateGeneratedContent: false);
        var invalid = () => AsUser().StartStashImportAsync(
            string.Empty,
            migrateGeneratedContent: false);
        await forbidden.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        await invalid.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*");
        (await AsUser().GetJobHistoryAsync(TestContext.Current.CancellationToken)).Select(job => job.Id).Should().Equal(historyBefore);
        (await AsUser().GetVideoByIdAsync(control.Id, TestContext.Current.CancellationToken)).Title.Should().Be(control.Title);

        var jobId = await AsUser().StartStashImportAsync(databasePath, migrateGeneratedContent: false, cancellationToken: TestContext.Current.CancellationToken);
        var completed = await AsUser().WaitForTerminalJobAsync(jobId, TestContext.Current.CancellationToken);
        completed.Id.Should().Be(jobId);
        completed.Type.Should().Be("stash-import");
        completed.Status.Should().Be(JobStatus.Completed);
        completed.Progress.Should().Be(1);
        completed.Error.Should().BeNull();

        var forbiddenResult = () => AsUser(ApiTestUsers.Eva).GetStashImportResultAsync(jobId);
        var missingResult = () => AsUser().GetStashImportResultAsync($"missing-{Guid.NewGuid():N}");
        await forbiddenResult.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        await missingResult.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");

        var result = await AsUser().GetStashImportResultAsync(jobId, TestContext.Current.CancellationToken);
        new[]
        {
            result.Videos,
            result.Performers,
            result.Tags,
            result.Studios,
            result.Groups,
            result.Images,
            result.Galleries,
        }.Should().OnlyContain(count => count == 0);
        (await AsUser().GetVideoByIdAsync(control.Id, TestContext.Current.CancellationToken)).Title.Should().Be(control.Title);
        (await AsUser().GetJobHistoryAsync(TestContext.Current.CancellationToken)).Select(job => job.Id)
            .Should().Equal([jobId, .. historyBefore]);
    }
}
