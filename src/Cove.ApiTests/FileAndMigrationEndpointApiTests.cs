using Cove.Api.Controllers;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Xunit.Abstractions;

namespace Cove.ApiTests;

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
        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName("API test image performer")
                .Build());
        var image = Convert.FromBase64String(OnePixelPng);

        await AsUser().UploadPerformerImageAsync(performer, image);

        var uploaded = await AsUser().GetPerformerImageAsync(performer);
        uploaded.MediaType.Should().Be("image/png");
        uploaded.Content.Should().Equal(image);
    }

    [Fact]
    [CoversEndpoints(typeof(FileOpsController))]
    public async Task GivenLibraryFile_WhenDirectoryIsBrowsed_ThenFileIsListed()
    {
        var filePath = AsTestFileSystem().CreateTextFile("API test file");

        var entries = await AsUser().BrowseDirectoryAsync(AsTestFileSystem().LibraryPath);

        entries.Should().Contain(entry => entry.Path == filePath && !entry.IsDirectory);
    }

    [Fact]
    [CoversEndpoints(typeof(StashMigrationController))]
    public async Task GivenEmptyStashDatabase_WhenMigrationIsPreviewed_ThenPreviewIsValidAndEmpty()
    {
        var databasePath = await AsTestFileSystem().CreateEmptyStashDatabaseAsync();

        var preview = await AsUser().PreviewStashMigrationAsync(databasePath);

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
