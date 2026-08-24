using System.Text.Json;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Metadata;

[Collection(ApiTestLane1Collection.Name)]
public sealed class MetadataTransferApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/metadata/export")]
    public async Task GivenRepresentativeMetadata_WhenExportCompletes_ThenEverySelectedSectionIsWritten()
    {
        // Arrange
        var suffix = Guid.NewGuid().ToString("N");
        var tag = await AsUser().CreateTagAsync(new TagBuilder()
                .WithName($"Export tag {suffix}")
                .WithDescription("Distinctive exported tag description")
                .AsFavorite()
                .Build(), TestContext.Current.CancellationToken);
        var studio = await AsUser().CreateStudioAsync(new StudioBuilder()
                .WithName($"Export studio {suffix}")
                .WithDetails("Distinctive exported studio details")
                .AsFavorite()
                .AsOrganized()
                .Build(), TestContext.Current.CancellationToken);
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder()
                .WithName($"Export performer {suffix}")
                .WithDetails("Distinctive exported performer details")
                .AsFavorite()
                .Build(), TestContext.Current.CancellationToken);
        var group = await AsUser().CreateGroupAsync($"Export group {suffix}", TestContext.Current.CancellationToken);
        var gallery = await AsUser().CreateGalleryAsync(new GalleryBuilder()
                .WithTitle($"Export gallery {suffix}")
                .WithDetails("Distinctive exported gallery details")
                .WithPhotographer("Export photographer")
                .WithStudio(studio)
                .WithTag(tag)
                .WithPerformer(performer)
                .AsOrganized()
                .Build(), TestContext.Current.CancellationToken);
        var video = await AsUser().CreateVideoAsync(new VideoBuilder()
                .WithTitle($"Export video {suffix}")
                .WithDetails("Distinctive exported video details")
                .WithDirector("Export director")
                .WithStudio(studio)
                .WithTags([tag])
                .WithPerformers([performer])
                .WithGallery(gallery)
                .WithGroup(group)
                .AsOrganized()
                .Build(), TestContext.Current.CancellationToken);

        // Act
        var jobId = await AsUser().StartMetadataExportAsync(new ExportOptionsDto
        {
            IncludeVideos = true,
            IncludePerformers = true,
            IncludeStudios = true,
            IncludeTags = true,
            IncludeGalleries = true,
            IncludeGroups = true,
        }, TestContext.Current.CancellationToken);
        var job = await AsUser().WaitForTerminalJobAsync(jobId, TestContext.Current.CancellationToken);

        // Assert
        job.Type.Should().Be("export");
        job.Status.Should().Be(JobStatus.Completed);
        job.Error.Should().BeNull();

        var exportDirectory = Path.Combine(AsTestFileSystem().GeneratedPath, "export");
        Directory.Exists(exportDirectory).Should().BeTrue();
        var exportFile = Directory.GetFiles(exportDirectory).Should().ContainSingle().Which;
        Path.GetFileName(exportFile).Should().MatchRegex(@"^cove-export-\d{8}_\d{6}\.json$");

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(exportFile, TestContext.Current.CancellationToken));
        var root = document.RootElement;
        root.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "videos",
            "performers",
            "studios",
            "tags",
            "galleries",
            "groups");

        var exportedVideo = FindExportedEntity(root, "videos", "title", video.Title!);
        exportedVideo.GetProperty("details").GetString().Should().Be("Distinctive exported video details");
        exportedVideo.GetProperty("director").GetString().Should().Be("Export director");
        exportedVideo.GetProperty("organized").GetBoolean().Should().BeTrue();

        var exportedPerformer = FindExportedEntity(root, "performers", "name", performer.Name);
        exportedPerformer.GetProperty("details").GetString().Should().Be("Distinctive exported performer details");
        exportedPerformer.GetProperty("favorite").GetBoolean().Should().BeTrue();

        var exportedStudio = FindExportedEntity(root, "studios", "name", studio.Name);
        exportedStudio.GetProperty("details").GetString().Should().Be("Distinctive exported studio details");
        exportedStudio.GetProperty("favorite").GetBoolean().Should().BeTrue();
        exportedStudio.GetProperty("organized").GetBoolean().Should().BeTrue();

        var exportedTag = FindExportedEntity(root, "tags", "name", tag.Name);
        exportedTag.GetProperty("description").GetString().Should().Be("Distinctive exported tag description");
        exportedTag.GetProperty("favorite").GetBoolean().Should().BeTrue();

        var exportedGallery = FindExportedEntity(root, "galleries", "title", gallery.Title!);
        exportedGallery.GetProperty("details").GetString().Should().Be("Distinctive exported gallery details");
        exportedGallery.GetProperty("photographer").GetString().Should().Be("Export photographer");
        exportedGallery.GetProperty("organized").GetBoolean().Should().BeTrue();

        _ = FindExportedEntity(root, "groups", "name", group.Name);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/metadata/import")]
    public async Task GivenSupportedMetadataFile_WhenImportCompletes_ThenEveryEntityIsPubliclyVisible()
    {
        // Arrange
        var suffix = Guid.NewGuid().ToString("N");
        var tagName = $"Imported tag {suffix}";
        var studioName = $"Imported studio {suffix}";
        var performerName = $"Imported performer {suffix}";
        var groupName = $"Imported group {suffix}";
        var importFile = AsTestFileSystem().CreateTextFile(JsonSerializer.Serialize(new
        {
            tags = new[]
            {
                new { name = tagName, description = "Imported tag description", favorite = true },
            },
            studios = new[]
            {
                new { name = studioName, details = "Imported studio details", favorite = true, organized = true },
            },
            performers = new[]
            {
                new
                {
                    name = performerName,
                    disambiguation = "Imported identity",
                    country = "Imported country",
                    details = "Imported performer details",
                    favorite = true,
                },
            },
            groups = new[]
            {
                new
                {
                    name = groupName,
                    director = "Imported director",
                    synopsis = "Imported group description",
                    duration = 321,
                },
            },
        }, ApiJson.Options));

        // Act
        var jobId = await AsUser().StartMetadataImportAsync(new ImportOptionsDto
        {
            FilePath = importFile,
            DuplicateHandling = false,
        }, TestContext.Current.CancellationToken);
        var job = await AsUser().WaitForTerminalJobAsync(jobId, TestContext.Current.CancellationToken);

        // Assert
        job.Type.Should().Be("import");
        job.Status.Should().Be(JobStatus.Completed);
        job.Error.Should().BeNull();

        var importedTag = (await AsUser().GetTagsAsync(TestContext.Current.CancellationToken)).Should()
            .ContainSingle(candidate => candidate.Name == tagName).Which;
        importedTag.Description.Should().Be("Imported tag description");
        importedTag.Favorite.Should().BeTrue();

        var importedStudio = (await AsUser().GetStudiosAsync(TestContext.Current.CancellationToken)).Should()
            .ContainSingle(candidate => candidate.Name == studioName).Which;
        importedStudio.Details.Should().Be("Imported studio details");
        importedStudio.Favorite.Should().BeTrue();
        importedStudio.Organized.Should().BeTrue();

        var importedPerformer = (await AsUser().GetPerformersAsync(TestContext.Current.CancellationToken)).Should()
            .ContainSingle(candidate => candidate.Name == performerName).Which;
        importedPerformer.Disambiguation.Should().Be("Imported identity");
        importedPerformer.Country.Should().Be("Imported country");
        importedPerformer.Details.Should().Be("Imported performer details");
        importedPerformer.Favorite.Should().BeTrue();

        var importedGroup = (await AsUser().GetGroupsAsync(TestContext.Current.CancellationToken)).Should()
            .ContainSingle(candidate => candidate.Name == groupName).Which;
        importedGroup.Director.Should().Be("Imported director");
        importedGroup.Description.Should().Be("Imported group description");
    }

    [Fact]
    public async Task GivenMember_WhenExportStarts_ThenForbiddenIsReturnedWithoutWritingAnArtifact()
    {
        // Act
        var action = () => AsUser(ApiTestUsers.Eva).StartMetadataExportAsync(new ExportOptionsDto());

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        GetExportFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task GivenMember_WhenImportStarts_ThenForbiddenIsReturnedWithoutCreatingAnEntity()
    {
        // Arrange
        var tagName = $"Forbidden imported tag {Guid.NewGuid():N}";
        var importFile = AsTestFileSystem().CreateTextFile(JsonSerializer.Serialize(new
        {
            tags = new[] { new { name = tagName } },
        }, ApiJson.Options));

        // Act
        var action = () => AsUser(ApiTestUsers.Eva).StartMetadataImportAsync(new ImportOptionsDto
        {
            FilePath = importFile,
        });

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        (await AsUser().GetTagsAsync(TestContext.Current.CancellationToken)).Should().NotContain(candidate => candidate.Name == tagName);
    }

    private static JsonElement FindExportedEntity(
        JsonElement root,
        string section,
        string identityProperty,
        string identityValue)
        => root.GetProperty(section)
            .EnumerateArray()
            .Where(entity => entity.GetProperty(identityProperty).GetString() == identityValue)
            .Should()
            .ContainSingle()
            .Which;

    private IReadOnlyList<string> GetExportFiles()
    {
        var exportDirectory = Path.Combine(AsTestFileSystem().GeneratedPath, "export");
        return Directory.Exists(exportDirectory)
            ? Directory.GetFiles(exportDirectory)
            : [];
    }
}
