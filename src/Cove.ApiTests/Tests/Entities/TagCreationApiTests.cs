using System.Text.Json;
using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;

namespace Cove.ApiTests.Tests.Entities;

[Collection(ApiTestLane2Collection.Name)]
public sealed class TagCreationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenTag_WhenMemberReadsTags_ThenTagIsReturned()
    {
        // Arrange
        var tag = await AsUser().CreateTagAsync(TestCatalog.Tags.TheatricalEntrance.Name, TestContext.Current.CancellationToken);

        // Act
        var tags = await AsUser(ApiTestUsers.Eva).GetTagsAsync(TestContext.Current.CancellationToken);

        // Assert
        tags.Should().ContainSingle(candidate => candidate.Id == tag.Id);
    }

    [Theory]
    [InlineData("Dramatic Standoff")]
    [InlineData("dramatic standoff")]
    [InlineData(" Dramatic Standoff")]
    [InlineData("Dramatic Standoff ")]
    [InlineData(" DRAMATIC STANDOFF ")]
    public async Task GivenTag_WhenTagWithEquivalentNameIsCreated_ThenConflictIsReturned(string duplicateName)
    {
        // Arrange
        await AsUser().CreateTagAsync(TestCatalog.Tags.DramaticStandoff.Name, TestContext.Current.CancellationToken);

        // Act
        var action = () => AsUser().CreateTagAsync(duplicateName);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 409 (Conflict)*");
    }

    [Fact]
    public async Task GivenTagAlias_WhenTagWithEquivalentNameIsCreated_ThenConflictIsReturned()
    {
        // Arrange
        await AsUser().CreateTagAsync(new TagBuilder()
                .WithName(TestCatalog.Tags.CowboyBoots.Name)
                .WithAlias(TestCatalog.Tags.QuestionableAlibi.Name)
                .Build(), TestContext.Current.CancellationToken);

        // Act
        var action = () => AsUser().CreateTagAsync($" {TestCatalog.Tags.QuestionableAlibi.Name.ToUpperInvariant()} ");

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 409 (Conflict)*");
    }

    [Fact]
    public async Task GivenTag_WhenAnotherTagUsesEquivalentAlias_ThenConflictIsReturned()
    {
        // Arrange
        await AsUser().CreateTagAsync(TestCatalog.Tags.QuestionableAlibi.Name, TestContext.Current.CancellationToken);
        var request = new TagBuilder()
            .WithName(TestCatalog.Tags.CowboyBoots.Name)
            .WithAlias($" {TestCatalog.Tags.QuestionableAlibi.Name.ToUpperInvariant()} ")
            .Build();

        // Act
        var action = () => AsUser().CreateTagAsync(request);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 409 (Conflict)*");
    }

    [Fact]
    public async Task GivenTagAlias_WhenTagWithDuplicateAliasIsCreated_ThenConflictIsReturned()
    {
        // Arrange
        await AsUser().CreateTagAsync(new TagBuilder()
                .WithName(TestCatalog.Tags.CowboyBoots.Name)
                .WithAlias(TestCatalog.Tags.QuestionableAlibi.Name)
                .Build(), TestContext.Current.CancellationToken);
        var request = new TagBuilder()
            .WithName(TestCatalog.Tags.CandleBudgetExceeded.Name)
            .WithAlias(TestCatalog.Tags.QuestionableAlibi.Name.ToUpperInvariant())
            .Build();

        // Act
        var action = () => AsUser().CreateTagAsync(request);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 409 (Conflict)*");
    }

    [Fact]
    public async Task GivenTag_WhenItsNameAndAliasAreEquivalent_ThenConflictIsReturned()
    {
        // Arrange
        var request = new TagBuilder()
            .WithName(TestCatalog.Tags.CowboyBoots.Name)
            .WithAlias($" {TestCatalog.Tags.CowboyBoots.Name.ToUpperInvariant()} ")
            .Build();

        // Act
        var action = () => AsUser().CreateTagAsync(request);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 409 (Conflict)*");
    }

    [Fact]
    public async Task GivenTag_WhenItsAliasesAreEquivalent_ThenConflictIsReturned()
    {
        // Arrange
        var request = new TagBuilder()
            .WithName(TestCatalog.Tags.CowboyBoots.Name)
            .WithAlias(TestCatalog.Tags.QuestionableAlibi.Name)
            .WithAlias($" {TestCatalog.Tags.QuestionableAlibi.Name.ToUpperInvariant()} ")
            .Build();

        // Act
        var action = () => AsUser().CreateTagAsync(request);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 409 (Conflict)*");
    }

    [Fact]
    public async Task GivenPaddedNameAndAliases_WhenTagIsCreated_ThenValuesAreNormalized()
    {
        // Arrange
        var request = new TagBuilder()
            .WithName($" {TestCatalog.Tags.CowboyBoots.Name} ")
            .WithAlias($" {TestCatalog.Tags.QuestionableAlibi.Name} ")
            .WithAlias(" \t ")
            .Build();

        // Act
        var tag = await AsUser().CreateTagAsync(request, TestContext.Current.CancellationToken);

        // Assert
        tag.Name.Should().Be(TestCatalog.Tags.CowboyBoots.Name);
        tag.Aliases.Should().Equal(TestCatalog.Tags.QuestionableAlibi.Name);
    }

    [Fact]
    public async Task GivenBlankTagName_WhenCreated_ThenEmptySentinelClaimsNamespace()
    {
        // Arrange
        var tag = await AsUser().CreateTagAsync(" \t ", TestContext.Current.CancellationToken);

        // Act & Assert
        tag.Name.Should().Be(TagNameRules.EmptyCanonicalName);

        var action = () => AsUser().CreateTagAsync($" {TagNameRules.EmptyCanonicalName.ToUpperInvariant()} ");

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 409 (Conflict)*");
    }

    [Fact]
    [CoversEndpoint("POST", "/api/tags")]
    [CoversEndpoint("GET", "/api/tags/{id:int}")]
    public async Task GivenTagMetadata_WhenTagIsCreated_ThenAllMetadataCanBeRetrieved()
    {
        // Arrange
        const string customFieldKey = "wardrobe_department";
        var parent = await AsUser().CreateTagAsync("Costume Comedy", TestContext.Current.CancellationToken);
        var child = await AsUser().CreateTagAsync(TestCatalog.Tags.WardrobeMalfunction.Name, TestContext.Current.CancellationToken);
        var tagGroup = await AsUser().CreateTagGroupAsync(new TagGroupCreateDto(
            Name: "Production Motifs",
            Description: "Recurring production details",
            Color: "#6b4f3a",
            SortOrder: 3), TestContext.Current.CancellationToken);
        await AsUser().CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = customFieldKey,
            Label = "Wardrobe department",
            Type = "text",
            EntityTypes = ["tag"]
        }, TestContext.Current.CancellationToken);
        var request = new TagBuilder()
            .WithName(TestCatalog.Tags.PeriodCostume.Name)
            .WithSortName("Period Costume, The")
            .WithDescription(TestCatalog.Tags.PeriodCostume.Description)
            .WithAlias("Historical-ish Wardrobe")
            .WithParent(parent)
            .WithChild(child)
            .WithColor("#8a5a44")
            .WithTagGroup(tagGroup)
            .WithSegmentDisplay("#d4af37", 2)
            .WithMinimumOccurrence(3.5, 12.5)
            .WithRemoteId("https://metadata.example/graphql", "tag-period-costume")
            .WithCustomField(customFieldKey, "Velvet and buckles")
            .AsFavorite()
            .AsOrganized()
            .Build();

        // Act
        var tag = await AsUser().CreateTagAsync(request, TestContext.Current.CancellationToken);

        // Assert
        var tagAfter = await AsUser().GetTagByIdAsync(tag.Id, TestContext.Current.CancellationToken);
        tagAfter.Name.Should().Be(request.Name);
        tagAfter.SortName.Should().Be(request.SortName);
        tagAfter.Description.Should().Be(request.Description);
        tagAfter.Favorite.Should().BeTrue();
        tagAfter.Organized.Should().BeTrue();
        tagAfter.Aliases.Should().Equal(request.Aliases!);
        tagAfter.Parents.Should().ContainSingle(candidate => candidate.Id == parent.Id);
        tagAfter.Children.Should().ContainSingle(candidate => candidate.Id == child.Id);
        tagAfter.Color.Should().Be(request.Color);
        tagAfter.TagGroupId.Should().Be(tagGroup.Id);
        tagAfter.TagGroupName.Should().Be(tagGroup.Name);
        tagAfter.TagGroupColor.Should().Be(tagGroup.Color);
        tagAfter.ShowAsSegment.Should().BeTrue();
        tagAfter.SegmentColorOverride.Should().Be(request.SegmentColorOverride);
        tagAfter.SegmentLaneOverride.Should().Be(request.SegmentLaneOverride);
        tagAfter.MinOccurrenceSec.Should().Be(request.MinOccurrenceSec);
        tagAfter.MinOccurrencePercent.Should().Be(request.MinOccurrencePercent);
        tagAfter.RemoteIds.Should().Equal(request.RemoteIds!);
        tagAfter.CustomFields.Should().ContainKey(customFieldKey)
            .WhoseValue.Should().BeOfType<JsonElement>()
            .Which.GetString().Should().Be("Velvet and buckles");
    }
}
