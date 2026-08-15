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
    [Fact]
    [CoversEndpoint("POST", "/api/performers/{id:int}/image")]
    [CoversEndpoint("GET", "/api/performers/{id:int}/image")]
    public async Task GivenPerformer_WhenImageIsUploaded_ThenImageCanBeRead()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .Build());
        var image = ApiTestImages.OnePixelPng();

        // Act
        await AsUser().UploadPerformerImageAsync(performer, image);

        // Assert
        var uploaded = await AsUser().GetPerformerImageAsync(performer);
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
        var entries = await AsUser().BrowseDirectoryAsync(AsTestFileSystem().LibraryPath);

        // Assert
        entries.Should().Contain(entry => entry.Path == filePath && !entry.IsDirectory);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/stash-migration/preview")]
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
