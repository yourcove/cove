using System.Text.Json;
using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Xunit.Abstractions;

namespace Cove.ApiTests;

[Collection(ApiTestLane2Collection.Name)]
public sealed class ImageCreationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenImage_WhenMemberReadsImages_ThenImageIsReturned()
    {
        var image = await AsUser().CreateImageAsync("Golden Corset Discovery");

        var images = await AsUser(ApiTestUsers.Eva).GetImagesAsync();

        images.Should().ContainSingle(candidate => candidate.Id == image.Id);
    }

    [Fact]
    public async Task GivenImageMetadata_WhenImageIsCreated_ThenAllMetadataCanBeRetrieved()
    {
        const string customFieldKey = "lighting_setup";
        var studio = await AsUser().CreateStudioAsync(TestCatalog.Studio.Name);
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName(TestCatalog.Performers.CherryPoppins.Name).Build());
        var tag = await AsUser().CreateTagAsync(TestCatalog.Tags.TheatricalEntrance.Name);
        var gallery = await AsUser().CreateGalleryAsync(new GalleryBuilder().WithTitle("Wardrobe Vault Stills").Build());
        var primaryGroup = await AsUser().CreateGroupAsync("Promotional Stills");
        var secondaryGroup = await AsUser().CreateGroupAsync("Wardrobe Details");
        await AsUser().CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = customFieldKey,
            Label = "Lighting setup",
            Type = "text",
            EntityTypes = ["image"]
        });
        var request = new ImageBuilder()
            .WithTitle("Golden Corset Discovery")
            .WithCode("BDP-STILL-042")
            .WithDetails("The treasure hunters discover the production's most expensive prop.")
            .WithPhotographer("Faye Stop")
            .WithRating(88)
            .WithStudio(studio)
            .WithDate("2026-07-17")
            .WithUrl("https://barely-dressed.example/stills/42")
            .WithTag(tag)
            .WithPerformer(performer)
            .WithGallery(gallery)
            .WithGroup(primaryGroup)
            .WithGroup(secondaryGroup)
            .WithCustomField(customFieldKey, "Warm key, cool fill")
            .AsOrganized()
            .Build();

        var image = await AsUser().CreateImageAsync(request);

        var imageAfter = await AsUser().GetImageByIdAsync(image.Id);
        var engagement = await AsUser().GetEntityEngagementAsync(AffinityHostType.Image, image.Id);
        imageAfter.Title.Should().Be(request.Title);
        imageAfter.Code.Should().Be(request.Code);
        imageAfter.Details.Should().Be(request.Details);
        imageAfter.Photographer.Should().Be(request.Photographer);
        imageAfter.Organized.Should().BeTrue();
        imageAfter.StudioId.Should().Be(studio.Id);
        imageAfter.StudioName.Should().Be(studio.Name);
        imageAfter.Date.Should().Be(request.Date);
        imageAfter.Urls.Should().Equal(request.Urls!);
        imageAfter.Tags.Should().ContainSingle(candidate => candidate.Id == tag.Id);
        imageAfter.Performers.Should().ContainSingle(candidate => candidate.Id == performer.Id);
        imageAfter.Galleries.Should().ContainSingle(candidate => candidate.Id == gallery.Id);
        imageAfter.Groups.Select(group => (group.Id, group.VideoIndex)).Should().Equal(
            (primaryGroup.Id, 0),
            (secondaryGroup.Id, 1));
        imageAfter.CustomFields.Should().ContainKey(customFieldKey)
            .WhoseValue.Should().BeOfType<JsonElement>()
            .Which.GetString().Should().Be("Warm key, cool fill");
        engagement.Rating.Should().Be(request.Rating);
    }
}
