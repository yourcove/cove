using System.Text.Json;
using Cove.ApiTests.Assertions;
using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Entities.Performers;

[Collection(ApiTestLane2Collection.Name)]
public sealed class PerformerUpdateApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenPerformer_WhenPartiallyUpdated_ThenSuppliedMetadataChangesAndOtherMetadataIsPreserved()
    {
        // Arrange
        const string customFieldKey = "character_archetype";
        var originalTag = await AsUser().CreateTagAsync(TestCatalog.Tags.Brooding.Name);
        var updatedTag = await AsUser().CreateTagAsync(TestCatalog.Tags.TheatricalEntrance.Name);
        await AsUser().CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = customFieldKey,
            Label = "Character archetype",
            Type = "text",
            EntityTypes = ["performer"]
        });
        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName(TestCatalog.Performers.VelvetThunder.Name)
                .WithDisambiguation("Original role")
                .WithBirthdate("1986-02-14")
                .WithCountry("Monaco")
                .WithDetails("Original details")
                .WithUrl("https://original.example")
                .WithAlias("Original alias")
                .WithTag(originalTag)
                .WithRemoteId("https://original-metadata.example/graphql", "original-id")
                .WithCustomField(customFieldKey, "Original archetype")
                .WithRating(40)
                .Build());

        // Act
        var updated = await AsUser().UpdatePerformerAsync(performer.Id, new
        {
            name = TestCatalog.Performers.RandyDandy.Name,
            country = "Canada",
            favorite = true,
            details = "Updated details",
            urls = new[] { "https://updated.example" },
            aliases = new[] { "Updated alias" },
            tagIds = new[] { updatedTag.Id },
            remoteIds = new[] { new PerformerRemoteIdDto("https://updated-metadata.example/graphql", "updated-id") },
            customFields = new Dictionary<string, object> { [customFieldKey] = "Updated archetype" },
            rating = 85,
        });
        var retrieved = await AsUser().GetPerformerByIdAsync(performer.Id);
        var engagement = await AsUser().GetPerformerEngagementAsync(retrieved);

        // Assert
        updated.Name.Should().Be(TestCatalog.Performers.RandyDandy.Name);
        retrieved.Disambiguation.Should().Be("Original role");
        retrieved.Birthdate.Should().Be("1986-02-14");
        retrieved.Country.Should().Be("Canada");
        retrieved.Favorite.Should().BeTrue();
        retrieved.Details.Should().Be("Updated details");
        retrieved.Urls.Should().Equal("https://updated.example");
        retrieved.Aliases.Should().Equal("Updated alias");
        retrieved.ShouldHaveOnlyTag(updatedTag);
        retrieved.RemoteIds.Should().Equal(new PerformerRemoteIdDto("https://updated-metadata.example/graphql", "updated-id"));
        retrieved.CustomFields.Should().ContainKey(customFieldKey)
            .WhoseValue.Should().BeOfType<JsonElement>()
            .Which.GetString().Should().Be("Updated archetype");
        engagement.Rating.Should().Be(85);
    }

    [Fact]
    public async Task GivenOptionalMetadata_WhenFieldsAreCleared_ThenValuesBecomeNull()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .WithDisambiguation("Original role")
                .WithCountry("Canada")
                .WithHeightCm(170)
                .WithDetails("Original details")
                .Build());

        // Act
        var updated = await AsUser().UpdatePerformerAsync(performer.Id, new
        {
            clearFields = new[] { "disambiguation", "country", "heightCm", "details" },
        });

        // Assert
        updated.Disambiguation.Should().BeNull();
        updated.Country.Should().BeNull();
        updated.HeightCm.Should().BeNull();
        updated.Details.Should().BeNull();
    }

    [Fact]
    public async Task GivenExistingPerformer_WhenAnotherPerformerIsUpdatedToSameIdentity_ThenConflictIsReturnedWithoutChangingPerformer()
    {
        // Arrange
        var existing = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .WithDisambiguation("Silent Era")
                .Build());
        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder().WithName(TestCatalog.Performers.BeaHaven.Name).Build());

        // Act
        var action = () => AsUser().UpdatePerformerAsync(performer.Id, new
        {
            name = existing.Name.ToUpperInvariant(),
            disambiguation = existing.Disambiguation!.ToUpperInvariant(),
        });

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 409 (Conflict)*");
        var retrieved = await AsUser().GetPerformerByIdAsync(performer.Id);
        retrieved.Name.Should().Be(performer.Name);
        retrieved.Disambiguation.Should().BeNull();
    }

    [Fact]
    public async Task GivenMember_WhenPerformerIsUpdated_ThenWriteAccessIsAllowed()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder().WithName(TestCatalog.Performers.BeaHaven.Name).Build());

        // Act
        var updated = await AsUser(ApiTestUsers.Eva).UpdatePerformerAsync(
            performer.Id,
            new { details = "Updated by member" });

        // Assert
        updated.Details.Should().Be("Updated by member");
    }

    [Fact]
    public async Task GivenMissingPerformer_WhenUpdated_ThenNotFoundIsReturned()
    {
        // Arrange
        const int missingId = int.MaxValue;

        // Act
        var action = () => AsUser().UpdatePerformerAsync(missingId, new { details = "Missing" });

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }
}
