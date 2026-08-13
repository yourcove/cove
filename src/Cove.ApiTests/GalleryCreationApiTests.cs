using System.Text.Json;
using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Xunit.Abstractions;

namespace Cove.ApiTests;

[Collection(ApiTestLane1Collection.Name)]
public sealed class GalleryCreationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenGallery_WhenMemberReadsGalleries_ThenGalleryIsReturned()
    {
        var gallery = await AsUser().CreateGalleryAsync(new GalleryBuilder().WithTitle("Wardrobe Vault Stills").Build());

        var galleries = await AsUser(ApiTestUsers.Eva).GetGalleriesAsync();

        galleries.Should().ContainSingle(candidate => candidate.Id == gallery.Id);
    }

    [Fact]
    public async Task GivenGalleryMetadata_WhenGalleryIsCreated_ThenAllMetadataCanBeRetrieved()
    {
        const string customFieldKey = "contact_sheet";
        var studio = await AsUser().CreateStudioAsync(TestCatalog.Studio.Name);
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName(TestCatalog.Performers.BeaHaven.Name).Build());
        var tag = await AsUser().CreateTagAsync(TestCatalog.Tags.PeriodCostume.Name);
        var video = await AsUser().CreateVideoAsync(TestCatalog.Movies.RaidersOfTheLostCorset.Title);
        await AsUser().CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = customFieldKey,
            Label = "Contact sheet",
            Type = "text",
            EntityTypes = ["gallery"]
        });
        var request = new GalleryBuilder()
            .WithTitle("Wardrobe Vault Stills")
            .WithCode("BDP-GALLERY-007")
            .WithDate("2026-07-17")
            .WithDetails("Production stills from the theatrical costume vault.")
            .WithPhotographer("Faye Stop")
            .WithRating(86)
            .WithStudio(studio)
            .WithUrl("https://barely-dressed.example/galleries/vault")
            .WithTag(tag)
            .WithPerformer(performer)
            .WithVideo(video)
            .WithCustomField(customFieldKey, "Sheet seven")
            .AsOrganized()
            .Build();

        var gallery = await AsUser().CreateGalleryAsync(request);

        var galleryAfter = await AsUser().GetGalleryByIdAsync(gallery.Id);
        var engagement = await AsUser().GetEntityEngagementAsync(AffinityHostType.Gallery, gallery.Id);
        galleryAfter.Title.Should().Be(request.Title);
        galleryAfter.Code.Should().Be(request.Code);
        galleryAfter.Date.Should().Be(request.Date);
        galleryAfter.Details.Should().Be(request.Details);
        galleryAfter.Photographer.Should().Be(request.Photographer);
        galleryAfter.Organized.Should().BeTrue();
        galleryAfter.StudioId.Should().Be(studio.Id);
        galleryAfter.StudioName.Should().Be(studio.Name);
        galleryAfter.Urls.Should().Equal(request.Urls!);
        galleryAfter.Tags.Should().ContainSingle(candidate => candidate.Id == tag.Id);
        galleryAfter.Performers.Should().ContainSingle(candidate => candidate.Id == performer.Id);
        galleryAfter.VideoIds.Should().ContainSingle().Which.Should().Be(video.Id);
        galleryAfter.CustomFields.Should().ContainKey(customFieldKey)
            .WhoseValue.Should().BeOfType<JsonElement>()
            .Which.GetString().Should().Be("Sheet seven");
        engagement.Rating.Should().Be(request.Rating);
    }
}
