using System.Text.Json;
using Cove.ApiTests.Assertions;
using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;

namespace Cove.ApiTests.Tests.Entities.Performers;

public sealed class PerformerCreationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Theory]
    [InlineData("1986", "1986")]
    [InlineData("1986-02", "1986-02")]
    [InlineData("1986-02-14", "1986-02-14")]
    public async Task GivenPartialBirthdate_WhenPerformerIsCreated_ThenPrecisionIsPreserved(string birthdate, string expected)
    {
        var request = new PerformerBuilder()
            .WithBirthdate(birthdate)
            .Build();

        var performer = await AsUser().CreatePerformerAsync(request, TestContext.Current.CancellationToken);
        var retrieved = await AsUser().GetPerformerByIdAsync(performer.Id, TestContext.Current.CancellationToken);

        performer.Birthdate.Should().Be(expected);
        retrieved.Birthdate.Should().Be(expected);
    }

    [Theory]
    [InlineData("1986-13")]
    [InlineData("1986-02-30")]
    [InlineData("not-a-date")]
    public async Task GivenInvalidBirthdate_WhenPerformerIsCreated_ThenBadRequestIsReturned(string birthdate)
    {
        var request = new PerformerBuilder()
            .WithBirthdate(birthdate)
            .Build();

        var action = () => AsUser().CreatePerformerAsync(request);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*");
    }

    [Fact]
    public async Task GivenPerformer_WhenPerformerWithDuplicateNameIsCreated_ThenConflictIsReturned()
    {
        // Arrange
        await AsUser().CreatePerformerAsync(new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .Build(), TestContext.Current.CancellationToken);
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
        var first = await AsUser().CreatePerformerAsync(new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .Build(), TestContext.Current.CancellationToken);

        // Act
        var second = await AsUser().CreatePerformerAsync(new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .WithDisambiguation("Silent Era")
                .Build(), TestContext.Current.CancellationToken);

        // Assert
        second.Id.Should().NotBe(first.Id);
        second.Name.Should().Be(first.Name);
        second.Disambiguation.Should().Be("Silent Era");
    }

    [Fact]
    public async Task GivenPerformer_WhenNameAndDisambiguationAreDuplicated_ThenConflictIsReturned()
    {
        // Arrange
        await AsUser().CreatePerformerAsync(new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .WithDisambiguation("Silent Era")
                .Build(), TestContext.Current.CancellationToken);
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
        var first = await AsUser().CreatePerformerAsync(new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .WithAlias(TestCatalog.Performers.RandyDandy.Name)
                .Build(), TestContext.Current.CancellationToken);

        // Act
        var second = await AsUser().CreatePerformerAsync(new PerformerBuilder()
                .WithName(TestCatalog.Performers.VelvetThunder.Name)
                .WithAlias(TestCatalog.Performers.RandyDandy.Name)
                .Build(), TestContext.Current.CancellationToken);

        // Assert
        second.Id.Should().NotBe(first.Id);
        first.Aliases.Should().ContainSingle().Which.Should().Be(TestCatalog.Performers.RandyDandy.Name);
        second.Aliases.Should().ContainSingle().Which.Should().Be(TestCatalog.Performers.RandyDandy.Name);
    }

    [Fact]
    public async Task GivenPaddedNameAndDisambiguation_WhenPerformerIsCreated_ThenIdentityIsNormalized()
    {
        // Arrange
        var request = new PerformerBuilder()
            .WithName($" {TestCatalog.Performers.CherryPoppins.Name} ")
            .WithDisambiguation(" Silent Era ")
            .Build();

        // Act
        var performer = await AsUser().CreatePerformerAsync(request, TestContext.Current.CancellationToken);

        // Assert
        performer.Name.Should().Be(TestCatalog.Performers.CherryPoppins.Name);
        performer.Disambiguation.Should().Be("Silent Era");
    }

    [Fact]
    public async Task GivenBlankName_WhenPerformerIsCreated_ThenEmptySentinelClaimsIdentity()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName(" \t ").Build(), TestContext.Current.CancellationToken);

        // Act & Assert
        performer.Name.Should().Be(EntityNameRules.EmptyCanonicalName);

        var action = () => AsUser().CreatePerformerAsync(
            new PerformerBuilder().WithName($" {EntityNameRules.EmptyCanonicalName.ToUpperInvariant()} ").Build());
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 409 (Conflict)*");
    }

    [Fact]
    [CoversEndpoint("POST", "/api/performers")]
    [CoversEndpoint("GET", "/api/performers/{id:int}")]
    public async Task GivenPerformerMetadata_WhenPerformerIsCreated_ThenAllMetadataCanBeRetrieved()
    {
        // Arrange
        const string customFieldKey = "stage_persona";
        var tag = await AsUser().CreateTagAsync(TestCatalog.Tags.Brooding.Name, TestContext.Current.CancellationToken);
        await AsUser().CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = customFieldKey,
            Label = "Stage persona",
            Type = "text",
            EntityTypes = ["performer"]
        }, TestContext.Current.CancellationToken);
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

        var performer = await AsUser().CreatePerformerAsync(request, TestContext.Current.CancellationToken);

        // Act
        var performerAfter = await AsUser().GetPerformerByIdAsync(performer.Id, TestContext.Current.CancellationToken);
        var engagement = await AsUser().GetPerformerEngagementAsync(performerAfter, TestContext.Current.CancellationToken);

        // Assert
        performerAfter.Should().BeEquivalentTo(request, options => options
            .Excluding(dto => dto.Rating)
            .Excluding(dto => dto.TagIds)
            .Excluding(dto => dto.CustomFields)
            .Excluding(dto => dto.Country));
        performerAfter.Country.Should().Be("MC");
        performerAfter.ShouldHaveOnlyTag(tag);
        performerAfter.CustomFields.Should().ContainKey(customFieldKey)
            .WhoseValue.Should().BeOfType<JsonElement>()
            .Which.GetString().Should().Be("Brooding romantic lead");
        engagement.Rating.Should().Be(request.Rating);
    }
}
