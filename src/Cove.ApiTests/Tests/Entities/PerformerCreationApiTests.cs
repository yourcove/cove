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
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .Build());

        // Act
        var performers = await AsUser(ApiTestUsers.Eva).GetPerformersAsync();

        // Assert
        performers.Should().ContainSingle(candidate => candidate.Id == performer.Id);
    }

    [Fact]
    public async Task GivenPerformer_WhenPerformerWithDuplicateNameIsCreated_ThenConflictIsReturned()
    {
        // Arrange
        await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .Build());
        var request = new PerformerBuilder()
            .WithName(TestCatalog.Performers.CherryPoppins.Name.ToUpperInvariant())
            .Build();

        // Act
        var action = () => AsUser().CreatePerformerAsync(request);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 409 (Conflict)*");
    }

    [Fact]
    public async Task GivenPerformer_WhenSameNameWithDistinctDisambiguationIsCreated_ThenBothPerformersExist()
    {
        // Arrange
        var first = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .Build());

        // Act
        var second = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .WithDisambiguation("Silent Era")
                .Build());

        // Assert
        second.Id.Should().NotBe(first.Id);
        second.Name.Should().Be(first.Name);
        second.Disambiguation.Should().Be("Silent Era");
    }

    [Fact]
    public async Task GivenPerformer_WhenNameAndDisambiguationAreDuplicated_ThenConflictIsReturned()
    {
        // Arrange
        await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .WithDisambiguation("Silent Era")
                .Build());
        var request = new PerformerBuilder()
            .WithName(TestCatalog.Performers.CherryPoppins.Name.ToUpperInvariant())
            .WithDisambiguation("SILENT ERA")
            .Build();

        // Act
        var action = () => AsUser().CreatePerformerAsync(request);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 409 (Conflict)*");
    }

    [Fact]
    public async Task GivenPerformerAlias_WhenAnotherPerformerUsesAlias_ThenBothPerformersExist()
    {
        // Arrange
        var first = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .WithAlias(TestCatalog.Performers.RandyDandy.Name)
                .Build());

        // Act
        var second = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName(TestCatalog.Performers.VelvetThunder.Name)
                .WithAlias(TestCatalog.Performers.RandyDandy.Name)
                .Build());

        // Assert
        second.Id.Should().NotBe(first.Id);
        first.Aliases.Should().ContainSingle().Which.Should().Be(TestCatalog.Performers.RandyDandy.Name);
        second.Aliases.Should().ContainSingle().Which.Should().Be(TestCatalog.Performers.RandyDandy.Name);
    }

    [Fact]
    public async Task GivenPerformerMetadata_WhenPerformerIsCreated_ThenAllMetadataCanBeRetrieved()
    {
        // Arrange
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

        // Act
        var performerAfter = await AsUser().GetPerformerByIdAsync(performer.Id);
        var engagement = await AsUser().GetPerformerEngagementAsync(performerAfter);

        // Assert
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
