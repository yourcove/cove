using System.Text.Json;
using Cove.ApiTests.Assertions;
using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Entities;

[Collection(ApiTestLane2Collection.Name)]
public sealed class PerformerCreationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenPerformer_WhenMemberReadsPerformers_ThenPerformerIsReturned()
    {
        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .Build());

        var performers = await AsUser(ApiTestUsers.Eva).GetPerformersAsync();

        performers.Should().ContainSingle(candidate => candidate.Id == performer.Id);
    }

    [Fact]
    public async Task GivenPerformerMetadata_WhenPerformerIsCreated_ThenAllMetadataCanBeRetrieved()
    {
        const string customFieldKey = "stage_persona";
        var tag = await AsUser().CreateTagAsync(TestCatalog.Tags.Brooding.Name);
        await AsUser().CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = customFieldKey,
            Label = "Stage persona",
            Type = "text",
            EntityTypes = ["performer"]
        });
        var request =
            new PerformerBuilder()
                .WithName(TestCatalog.Performers.VelvetThunder.Name)
                .WithDisambiguation("the lounge singer")
                .WithGender("Male")
                .WithBirthdate("1986-02-14")
                .WithDeathDate("2076-11-03")
                .WithEthnicity("Mediterranean")
                .WithCountry("Monaco")
                .WithEyeColor("Storm gray")
                .WithHairColor("Midnight black")
                .WithHeightCm(188)
                .WithWeight(86)
                .WithMeasurements("112-81-99")
                .WithFakeTits("Not applicable")
                .WithPenisLength(19.5)
                .WithCircumcised("Cut")
                .WithCareerStart("2008-01-01")
                .WithCareerEnd("2048-12-31")
                .WithTattoos("A tiny thundercloud over the left shoulder blade")
                .WithPiercings("Left ear")
                .AsFavorite()
                .WithRating(91)
                .WithDetails(TestCatalog.Performers.VelvetThunder.Description)
                .WithUrl("https://velvet-thunder.example")
                .WithAlias("The Velvet Voice")
                .WithTag(tag)
                .WithRemoteId("https://metadata.example/graphql", "performer-velvet-thunder")
                .WithCustomField(customFieldKey, "Brooding romantic lead")
                .Build();

        var performer = await AsUser().CreatePerformerAsync(request);

        var performerAfter = await AsUser().GetPerformerByIdAsync(performer.Id);
        var engagement = await AsUser().GetPerformerEngagementAsync(performerAfter);

        performerAfter.Should().BeEquivalentTo(request, options => options
            .Excluding(dto => dto.Rating)
            .Excluding(dto => dto.TagIds)
            .Excluding(dto => dto.CustomFields));
        performerAfter.ShouldHaveOnlyTag(tag);
        performerAfter.CustomFields.Should().ContainKey(customFieldKey)
            .WhoseValue.Should().BeOfType<JsonElement>()
            .Which.GetString().Should().Be("Brooding romantic lead");
        engagement.Rating.Should().Be(request.Rating);
    }
}
