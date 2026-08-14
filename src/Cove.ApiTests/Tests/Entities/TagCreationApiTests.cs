using System.Text.Json;
using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Entities;

[Collection(ApiTestLane2Collection.Name)]
public sealed class TagCreationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenTag_WhenMemberReadsTags_ThenTagIsReturned()
    {
        var tag = await AsUser().CreateTagAsync(TestCatalog.Tags.TheatricalEntrance.Name);

        var tags = await AsUser(ApiTestUsers.Eva).GetTagsAsync();

        tags.Should().ContainSingle(candidate => candidate.Id == tag.Id);
    }

    [Fact]
    public async Task GivenTagMetadata_WhenTagIsCreated_ThenAllMetadataCanBeRetrieved()
    {
        const string customFieldKey = "wardrobe_department";
        var parent = await AsUser().CreateTagAsync("Costume Comedy");
        var child = await AsUser().CreateTagAsync(TestCatalog.Tags.WardrobeMalfunction.Name);
        var tagGroup = await AsUser().CreateTagGroupAsync(new TagGroupCreateDto(
            Name: "Production Motifs",
            Description: "Recurring production details",
            Color: "#6b4f3a",
            SortOrder: 3));
        await AsUser().CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = customFieldKey,
            Label = "Wardrobe department",
            Type = "text",
            EntityTypes = ["tag"]
        });
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

        var tag = await AsUser().CreateTagAsync(request);

        var tagAfter = await AsUser().GetTagByIdAsync(tag.Id);
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
