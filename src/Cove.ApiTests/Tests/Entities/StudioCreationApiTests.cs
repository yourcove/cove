using System.Text.Json;
using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Entities;

[Collection(ApiTestLane1Collection.Name)]
public sealed class StudioCreationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenStudio_WhenMemberReadsStudios_ThenStudioIsReturned()
    {
        // Arrange
        var studio = await AsUser().CreateStudioAsync(TestCatalog.Studio.Name);

        // Act
        var studios = await AsUser(ApiTestUsers.Eva).GetStudiosAsync();

        // Assert
        studios.Should().ContainSingle(candidate => candidate.Id == studio.Id);
    }

    [Theory]
    [InlineData("Barely Dressed Pictures")]
    [InlineData("barely dressed pictures")]
    [InlineData(" Barely Dressed Pictures")]
    [InlineData("Barely Dressed Pictures ")]
    [InlineData(" BARELY DRESSED PICTURES ")]
    public async Task GivenStudio_WhenStudioWithEquivalentNameIsCreated_ThenCreationIsRejected(string duplicateName)
    {
        // Arrange
        await AsUser().CreateStudioAsync(TestCatalog.Studio.Name);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AsUser().CreateStudioAsync(duplicateName));

        exception.Message.Should().Contain("409 (Conflict)");
        exception.Message.Should().Contain("\"code\":\"STUDIO_NAME_CONFLICT\"");
    }

    [Fact]
    public async Task GivenStudioMetadata_WhenStudioIsCreated_ThenAllMetadataCanBeRetrieved()
    {
        // Arrange
        const string customFieldKey = "production_style";
        var parent = await AsUser().CreateStudioAsync("Barely Dressed Holdings");
        var tag = await AsUser().CreateTagAsync(TestCatalog.Tags.PlotOptional.Name);
        await AsUser().CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = customFieldKey,
            Label = "Production style",
            Type = "text",
            EntityTypes = ["studio"]
        });
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
        var studio = await AsUser().CreateStudioAsync(request);
        var studioAfter = await AsUser().GetStudioByIdAsync(studio.Id);
        var engagement = await AsUser().GetEntityEngagementAsync(AffinityHostType.Studio, studio.Id);

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
