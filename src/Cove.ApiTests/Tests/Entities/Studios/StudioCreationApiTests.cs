using System.Text.Json;
using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;

namespace Cove.ApiTests.Tests.Entities.Studios;

public sealed class StudioCreationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Theory]
    [InlineData("Barely Dressed Pictures")]
    [InlineData("barely dressed pictures")]
    [InlineData(" Barely Dressed Pictures")]
    [InlineData("Barely Dressed Pictures ")]
    [InlineData(" BARELY DRESSED PICTURES ")]
    public async Task GivenStudio_WhenStudioWithEquivalentNameIsCreated_ThenCreationIsRejected(string duplicateName)
    {
        // Arrange
        await AsUser().CreateStudioAsync(TestCatalog.Studio.Name, TestContext.Current.CancellationToken);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AsUser().CreateStudioAsync(duplicateName, TestContext.Current.CancellationToken));

        exception.Message.Should().Contain("409 (Conflict)");
        exception.Message.Should().Contain("\"code\":\"STUDIO_NAME_CONFLICT\"");
    }

    [Fact]
    public async Task GivenBlankName_WhenStudioIsCreated_ThenEmptySentinelClaimsNamespace()
    {
        // Arrange
        var studio = await AsUser().CreateStudioAsync(" \t ", TestContext.Current.CancellationToken);

        // Act & Assert
        studio.Name.Should().Be(EntityNameRules.EmptyCanonicalName);

        var action = () => AsUser().CreateStudioAsync(
            $" {EntityNameRules.EmptyCanonicalName.ToUpperInvariant()} ");
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 409 (Conflict)*");
    }

    [Fact]
    public async Task GivenPaddedUniqueName_WhenStudioIsCreated_ThenNameIsNormalized()
    {
        // Arrange
        var request = new StudioBuilder()
            .WithName(" The Lantern Room ")
            .Build();

        // Act
        var studio = await AsUser().CreateStudioAsync(request, TestContext.Current.CancellationToken);

        // Assert
        studio.Name.Should().Be("The Lantern Room");
    }

    [Fact]
    public async Task GivenStudioAlias_WhenAnotherStudioUsesAlias_ThenBothStudiosExist()
    {
        // Arrange
        var first = await AsUser().CreateStudioAsync(new StudioBuilder()
                .WithName(TestCatalog.Studio.Name)
                .WithAlias("Shared production label")
                .Build(), TestContext.Current.CancellationToken);

        // Act
        var second = await AsUser().CreateStudioAsync(new StudioBuilder()
                .WithName("The Lantern Room")
                .WithAlias("Shared production label")
                .Build(), TestContext.Current.CancellationToken);

        // Assert
        second.Id.Should().NotBe(first.Id);
        first.Aliases.Should().Equal("Shared production label");
        second.Aliases.Should().Equal("Shared production label");
    }

    [Fact]
    [CoversEndpoint("POST", "/api/studios")]
    [CoversEndpoint("GET", "/api/studios/{id:int}")]
    public async Task GivenStudioMetadata_WhenStudioIsCreated_ThenAllMetadataCanBeRetrieved()
    {
        // Arrange
        const string customFieldKey = "production_style";
        var parent = await AsUser().CreateStudioAsync("Barely Dressed Holdings", TestContext.Current.CancellationToken);
        var tag = await AsUser().CreateTagAsync(TestCatalog.Tags.PlotOptional.Name, TestContext.Current.CancellationToken);
        await AsUser().CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = customFieldKey,
            Label = "Production style",
            Type = "text",
            EntityTypes = ["studio"]
        }, TestContext.Current.CancellationToken);
        var request = new StudioBuilder()
            .WithName(TestCatalog.Studio.Name)
            .WithParent(parent)
            .WithRating(84)
            .WithDetails(TestCatalog.Studio.Description)
            .WithUrl("https://barely-dressed.example")
            .WithAlias("BDP")
            .WithTag(tag)
            .WithRemoteId("https://metadata.example/graphql", "studio-barely-dressed")
            .WithCustomField(customFieldKey, "Camp adventure")
            .AsFavorite()
            .AsOrganized()
            .Build();

        // Act
        var studio = await AsUser().CreateStudioAsync(request, TestContext.Current.CancellationToken);
        var studioAfter = await AsUser().GetStudioByIdAsync(studio.Id, TestContext.Current.CancellationToken);
        var engagement = await AsUser().GetEntityEngagementAsync(AffinityHostType.Studio, studio.Id, TestContext.Current.CancellationToken);

        // Assert
        studioAfter.Should().BeEquivalentTo(request, options => options
            .Excluding(dto => dto.Rating)
            .Excluding(dto => dto.TagIds)
            .Excluding(dto => dto.CustomFields));
        studioAfter.ParentName.Should().Be(parent.Name);
        studioAfter.Tags.Should().ContainSingle(candidate => candidate.Id == tag.Id);
        studioAfter.CustomFields.Should().ContainKey(customFieldKey)
            .WhoseValue.Should().BeOfType<JsonElement>()
            .Which.GetString().Should().Be("Camp adventure");
        engagement.Rating.Should().Be(request.Rating);
    }
}
