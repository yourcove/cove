using Cove.Api.Controllers;
using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Files;

[Collection(ApiTestLane2Collection.Name)]
public sealed class FileAndMigrationEndpointApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M/wHwAF/gL+XhZ8AAAAAElFTkSuQmCC";

    [Fact]
    [CoversEndpoints(typeof(EntityImageController))]
    public async Task GivenPerformer_WhenImageIsUploaded_ThenImageCanBeRead()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .Build());
        var image = Convert.FromBase64String(OnePixelPng);

        // Act
        await AsUser().UploadPerformerImageAsync(performer, image);

        // Assert
        var uploaded = await AsUser().GetPerformerImageAsync(performer);
        uploaded.MediaType.Should().Be("image/png");
        uploaded.Content.Should().Equal(image);
    }

    [Fact]
    [CoversEndpoints(typeof(FileOpsController))]
    public async Task GivenLibraryFile_WhenDirectoryIsBrowsed_ThenFileIsListed()
    {
        // Arrange
        var filePath = AsTestFileSystem().CreateTextFile("API test file");

        // Act
        var entries = await AsUser().BrowseDirectoryAsync(AsTestFileSystem().LibraryPath);

        // Assert
        entries.Should().Contain(entry => entry.Path == filePath && !entry.IsDirectory);
    }

    [Fact]
    [CoversEndpoints(typeof(StashMigrationController))]
    public async Task GivenEmptyStashDatabase_WhenMigrationIsPreviewed_ThenPreviewIsValidAndEmpty()
    {
        // Arrange
        var databasePath = await AsTestFileSystem().CreateEmptyStashDatabaseAsync();

        // Act
        var preview = await AsUser().PreviewStashMigrationAsync(databasePath);

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
}
