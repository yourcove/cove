using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Entities.Texts;

[Collection(ApiTestLane2Collection.Name)]
public sealed class TextLifecycleQueryAndFileApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/texts")]
    [CoversEndpoint("GET", "/api/texts/{id:int}")]
    public async Task GivenTextMetadata_WhenMemberCreatesAndReadsIt_ThenRelationshipsRoundTrip()
    {
        // Arrange
        var studio = await AsUser().CreateStudioAsync($"Text studio {Guid.NewGuid():N}");
        var tag = await AsUser().CreateTagAsync($"Text tag {Guid.NewGuid():N}");
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Text performer {Guid.NewGuid():N}")
            .Build());
        var group = await AsUser().CreateGroupAsync($"Text group {Guid.NewGuid():N}");
        var request = new TextDocumentBuilder()
            .WithTitle("  Text lifecycle title  ")
            .WithCode("  TEXT-CODE  ")
            .WithDetails("  Text details  ")
            .WithDate("2026-08-05")
            .WithStudio(studio)
            .WithUrl("  https://text.example/item  ")
            .WithTag(tag)
            .WithPerformer(performer)
            .WithGroup(group)
            .AsOrganized()
            .Build();

        // Act
        var created = await AsUser(ApiTestUsers.Eva).CreateTextAsync(request);
        var retrieved = await AsUser(ApiTestUsers.Eva).GetTextByIdAsync(created.Id);

        // Assert
        retrieved.Title.Should().Be("Text lifecycle title");
        retrieved.Code.Should().Be("TEXT-CODE");
        retrieved.Details.Should().Be("Text details");
        retrieved.Date.Should().Be("2026-08-05");
        retrieved.Organized.Should().BeTrue();
        retrieved.StudioId.Should().Be(studio.Id);
        retrieved.Urls.Should().Equal("https://text.example/item");
        retrieved.Tags.Should().ContainSingle(candidate => candidate.Id == tag.Id);
        retrieved.Performers.Should().ContainSingle(candidate => candidate.Id == performer.Id);
        retrieved.Groups.Should().ContainSingle(candidate => candidate.Id == group.Id);
    }

    [Fact]
    [CoversEndpoint("PUT", "/api/texts/{id:int}")]
    public async Task GivenTextMetadata_WhenMemberPartiallyUpdatesIt_ThenResponseAndReadPreserveRelationships()
    {
        // Arrange
        var studio = await AsUser().CreateStudioAsync($"Original text studio {Guid.NewGuid():N}");
        var tag = await AsUser().CreateTagAsync($"Original text tag {Guid.NewGuid():N}");
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Original text performer {Guid.NewGuid():N}")
            .Build());
        var group = await AsUser().CreateGroupAsync($"Original text group {Guid.NewGuid():N}");
        var text = await AsUser().CreateTextAsync(new TextDocumentBuilder()
            .WithTitle($"Original text {Guid.NewGuid():N}")
            .WithCode("ORIGINAL-CODE")
            .WithDetails("Original details")
            .WithDate("2026-08-06")
            .WithStudio(studio)
            .WithUrl("https://text.example/original")
            .WithTag(tag)
            .WithPerformer(performer)
            .WithGroup(group)
            .Build());

        // Act
        var updated = await AsUser(ApiTestUsers.Eva).UpdateTextAsync(text.Id, new
        {
            title = "Updated text title",
            details = "Updated details",
            urls = new[] { "https://text.example/updated" },
            clearFields = new[] { "studioId" },
        });
        var retrieved = await AsUser().GetTextByIdAsync(text.Id);

        // Assert
        updated.Title.Should().Be("Updated text title");
        updated.Urls.Should().Equal("https://text.example/updated");
        retrieved.Urls.Should().Equal(updated.Urls);
        retrieved.Details.Should().Be("Updated details");
        retrieved.Code.Should().Be("ORIGINAL-CODE");
        retrieved.Date.Should().Be("2026-08-06");
        retrieved.StudioId.Should().BeNull();
        retrieved.Tags.Should().ContainSingle(candidate => candidate.Id == tag.Id);
        retrieved.Performers.Should().ContainSingle(candidate => candidate.Id == performer.Id);
        retrieved.Groups.Should().ContainSingle(candidate => candidate.Id == group.Id);
    }

    [Fact]
    public async Task GivenMissingText_WhenReadOrUpdated_ThenNotFoundIsReturned()
    {
        const int missingId = int.MaxValue;

        var read = () => AsUser().GetTextByIdAsync(missingId);
        var update = () => AsUser().UpdateTextAsync(missingId, new { details = "Missing" });

        await read.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
        await update.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    [CoversEndpoint("POST", "/api/texts/find")]
    public async Task GivenMatchingTexts_WhenFilteredAndPaged_ThenOnlyTheRequestedPageIsReturned()
    {
        // Arrange
        var suffix = Guid.NewGuid().ToString("N");
        var first = await AsUser().CreateTextAsync(new TextDocumentBuilder().WithTitle($"A filtered text {suffix}").AsOrganized().Build());
        var second = await AsUser().CreateTextAsync(new TextDocumentBuilder().WithTitle($"B filtered text {suffix}").AsOrganized().Build());
        await AsUser().CreateTextAsync(new TextDocumentBuilder().WithTitle($"Excluded text {suffix}").Build());
        var request = new FilteredQueryRequest<TextDocumentFilter>
        {
            ObjectFilter = new TextDocumentFilter { OrganizedCriterion = new BoolCriterion { Value = true } },
            FindFilter = new FindFilter { Q = suffix, Page = 2, PerPage = 1, Sort = "title" },
        };

        // Act
        var result = await AsUser(ApiTestUsers.Eva).FindTextsAsync(request);

        // Assert
        result.TotalCount.Should().Be(2);
        result.Page.Should().Be(2);
        result.PerPage.Should().Be(1);
        result.Items.Should().ContainSingle().Which.Id.Should().Be(second.Id);
        result.Items.Should().NotContain(candidate => candidate.Id == first.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/texts/from-file")]
    [CoversEndpoint("GET", "/api/texts/{id:int}/content")]
    public async Task GivenTextFiles_WhenImportedAndRead_ThenContentAndErrorsAreObservable()
    {
        // Arrange
        const string contents = "A deterministic text content source.";
        var path = AsTestFileSystem().CreateTextFile(contents);
        var invalidImport = () => AsUser(ApiTestUsers.Eva).CreateTextFromFileAsync(path + ".missing");

        // Act
        await invalidImport.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*");
        var created = await AsUser(ApiTestUsers.Eva).CreateTextFromFileAsync(path);
        var content = await AsUser(ApiTestUsers.Eva).GetTextContentAsync(created.Id);
        var metadataOnly = await AsUser().CreateTextAsync($"Text without file {Guid.NewGuid():N}");
        var missingContent = () => AsUser().GetTextContentAsync(metadataOnly.Id);

        // Assert
        created.Title.Should().Be(Path.GetFileNameWithoutExtension(path));
        created.FileCount.Should().Be(1);
        var file = created.Files.Should().ContainSingle().Which;
        Path.GetFullPath(file.Path).Should().Be(Path.GetFullPath(path));
        file.Format.Should().Be("txt");
        file.Size.Should().Be(new FileInfo(path).Length).And.BeGreaterThan(0);
        file.WordCount.Should().Be(5);
        content.Should().Be(new TextContentDto("txt", "text", contents));
        await missingContent.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    [CoversEndpoint("POST", "/api/texts/aggregate")]
    public async Task GivenSelectedFileBackedTexts_WhenAggregated_ThenNonzeroSizeIsScopedToSelection()
    {
        // Arrange
        var first = await ImportTextAsync("First aggregate text source.");
        var second = await ImportTextAsync("Second aggregate text source with more bytes.");
        var excluded = await ImportTextAsync("Excluded aggregate text source with substantially more bytes than selected documents.");
        var expectedSize = first.Files.Single().Size + second.Files.Single().Size;
        var request = new FilteredQueryRequest<TextDocumentFilter> { Ids = [first.Id, second.Id] };

        // Act
        var aggregate = await AsUser(ApiTestUsers.Eva).AggregateTextsAsync(request);

        // Assert
        expectedSize.Should().BeGreaterThan(0);
        aggregate.Should().Be(new TextAggregate(Count: 2, FileSize: expectedSize));
        aggregate.FileSize.Should().BeLessThan(expectedSize + excluded.Files.Single().Size);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/texts/bulk")]
    public async Task GivenTextsWithRelationships_WhenMemberBulkSetsValues_ThenOnlySelectedTextsChange()
    {
        // Arrange
        var originalStudio = await AsUser().CreateStudioAsync($"Original bulk text studio {Guid.NewGuid():N}");
        var originalTag = await AsUser().CreateTagAsync($"Original bulk text tag {Guid.NewGuid():N}");
        var originalPerformer = await AsUser().CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Original bulk text performer {Guid.NewGuid():N}")
            .Build());
        var replacementTag = await AsUser().CreateTagAsync($"Replacement bulk text tag {Guid.NewGuid():N}");
        var replacementPerformer = await AsUser().CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Replacement bulk text performer {Guid.NewGuid():N}")
            .Build());
        var selected = await Task.WhenAll(Enumerable.Range(1, 2).Select(index => AsUser().CreateTextAsync(new TextDocumentBuilder()
            .WithTitle($"Selected bulk text {index} {Guid.NewGuid():N}")
            .WithCode($"ORIGINAL-{index}")
            .WithDetails($"Original details {index}")
            .WithDate("2026-08-07")
            .WithStudio(originalStudio)
            .WithTag(originalTag)
            .WithPerformer(originalPerformer)
            .Build())));
        var unselected = await AsUser().CreateTextAsync(new TextDocumentBuilder()
            .WithTitle($"Unselected bulk text {Guid.NewGuid():N}")
            .WithCode("CONTROL-CODE")
            .WithDetails("Control details")
            .WithDate("2026-08-08")
            .WithStudio(originalStudio)
            .WithTag(originalTag)
            .WithPerformer(originalPerformer)
            .Build());
        var request = new BulkTextDocumentUpdateDto
        {
            Ids = selected.Select(text => text.Id).ToList(),
            ClearFields = ["studioId", "date"],
            Organized = true,
            Code = "BULK-CODE",
            Details = "Bulk details",
            TagIds = [replacementTag.Id],
            TagMode = BulkUpdateMode.Set,
            PerformerIds = [replacementPerformer.Id],
            PerformerMode = BulkUpdateMode.Set,
        };

        // Act
        var updatedCount = await AsUser(ApiTestUsers.Eva).BulkUpdateTextsAsync(request);
        var updated = await Task.WhenAll(selected.Select(text => AsUser().GetTextByIdAsync(text.Id)));
        var control = await AsUser().GetTextByIdAsync(unselected.Id);

        // Assert
        updatedCount.Should().Be(2);
        updated.Should().AllSatisfy(text =>
        {
            text.Code.Should().Be("BULK-CODE");
            text.Details.Should().Be("Bulk details");
            text.Organized.Should().BeTrue();
            text.StudioId.Should().BeNull();
            text.Date.Should().BeNull();
            text.Tags.Should().ContainSingle(candidate => candidate.Id == replacementTag.Id);
            text.Performers.Should().ContainSingle(candidate => candidate.Id == replacementPerformer.Id);
        });
        control.Code.Should().Be("CONTROL-CODE");
        control.Details.Should().Be("Control details");
        control.Organized.Should().BeFalse();
        control.StudioId.Should().Be(originalStudio.Id);
        control.Date.Should().Be("2026-08-08");
        control.Tags.Should().ContainSingle(candidate => candidate.Id == originalTag.Id);
        control.Performers.Should().ContainSingle(candidate => candidate.Id == originalPerformer.Id);
    }

    [Fact]
    [CoversEndpoint("DELETE", "/api/texts/{id:int}")]
    public async Task GivenFileBackedTexts_WhenOwnerDeletesWithFileOptions_ThenDiskSemanticsAreEnforced()
    {
        // Arrange
        var retainedPath = AsTestFileSystem().CreateTextFile("Retained text file contents.");
        var deletedPath = AsTestFileSystem().CreateTextFile("Deleted text file contents.");
        var retainedFileText = await AsUser().CreateTextFromFileAsync(retainedPath);
        var deletedFileText = await AsUser().CreateTextFromFileAsync(deletedPath);

        // Act
        await AsUser().DeleteTextAsync(retainedFileText.Id);
        await AsUser().DeleteTextAsync(deletedFileText.Id, deleteFile: true);

        // Assert
        File.Exists(retainedPath).Should().BeTrue();
        File.Exists(deletedPath).Should().BeFalse();
        foreach (var deleted in new[] { retainedFileText, deletedFileText })
        {
            var read = () => AsUser().GetTextByIdAsync(deleted.Id);
            await read.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*returned 404 (NotFound)*");
        }
    }

    [Fact]
    public async Task GivenText_WhenMemberDeletesIt_ThenForbiddenIsReturnedWithoutRemovingIt()
    {
        var text = await AsUser().CreateTextAsync($"Protected delete text {Guid.NewGuid():N}");

        var deletion = () => AsUser(ApiTestUsers.Eva).DeleteTextAsync(text.Id);

        await deletion.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        (await AsUser().GetTextByIdAsync(text.Id)).Id.Should().Be(text.Id);
    }

    [Fact]
    [CoversEndpoint("DELETE", "/api/texts/bulk")]
    public async Task GivenFileBackedTextsAndMissingId_WhenBulkDeleteRuns_ThenPermissionAndSelectionAreEnforced()
    {
        // Arrange
        var firstPath = AsTestFileSystem().CreateTextFile("First bulk delete text.");
        var secondPath = AsTestFileSystem().CreateTextFile("Second bulk delete text.");
        var retainedPath = AsTestFileSystem().CreateTextFile("Retained bulk delete text.");
        var first = await AsUser().CreateTextFromFileAsync(firstPath);
        var second = await AsUser().CreateTextFromFileAsync(secondPath);
        var retained = await AsUser().CreateTextFromFileAsync(retainedPath);
        var request = new BatchDeleteDto([first.Id, int.MaxValue, second.Id], DeleteFiles: true);

        // Act
        var forbidden = () => AsUser(ApiTestUsers.Eva).BulkDeleteTextsAsync(request);
        await forbidden.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        File.Exists(firstPath).Should().BeTrue();
        File.Exists(secondPath).Should().BeTrue();
        await AsUser().BulkDeleteTextsAsync(request);

        // Assert
        foreach (var deleted in new[] { first, second })
        {
            var read = () => AsUser().GetTextByIdAsync(deleted.Id);
            await read.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*returned 404 (NotFound)*");
        }
        File.Exists(firstPath).Should().BeFalse();
        File.Exists(secondPath).Should().BeFalse();
        File.Exists(retainedPath).Should().BeTrue();
        (await AsUser().GetTextByIdAsync(retained.Id)).Id.Should().Be(retained.Id);
    }

    private async Task<TextDocumentDto> ImportTextAsync(string contents)
    {
        var path = AsTestFileSystem().CreateTextFile(contents);
        return await AsUser().CreateTextFromFileAsync(path);
    }
}
