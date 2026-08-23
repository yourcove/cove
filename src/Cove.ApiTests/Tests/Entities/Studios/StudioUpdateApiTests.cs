using System.Text.Json;
using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;

namespace Cove.ApiTests.Tests.Entities.Studios;

[Collection(ApiTestLane2Collection.Name)]
public sealed class StudioUpdateApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("PUT", "/api/studios/{id:int}")]
    public async Task GivenStudio_WhenPartiallyUpdated_ThenSuppliedMetadataChangesAndOtherMetadataIsPreserved()
    {
        // Arrange
        const string customFieldKey = "production_tone";
        var originalParent = await AsUser().CreateStudioAsync("Original Parent");
        var updatedParent = await AsUser().CreateStudioAsync("Updated Parent");
        var originalTag = await AsUser().CreateTagAsync(TestCatalog.Tags.Brooding.Name);
        var updatedTag = await AsUser().CreateTagAsync(TestCatalog.Tags.TheatricalEntrance.Name);
        await AsUser().CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = customFieldKey,
            Label = "Production tone",
            Type = "text",
            EntityTypes = ["studio"]
        });
        var studio = await AsUser().CreateStudioAsync(
            new StudioBuilder()
                .WithName(TestCatalog.Studio.Name)
                .WithParent(originalParent)
                .WithDetails("Original details")
                .WithUrl("https://original.example")
                .WithAlias("Original alias")
                .WithTag(originalTag)
                .WithRemoteId("https://original-metadata.example/graphql", "original-id")
                .WithCustomField(customFieldKey, "Original tone")
                .WithRating(40)
                .Build());

        // Act
        var updated = await AsUser().UpdateStudioAsync(studio.Id, new
        {
            parentId = updatedParent.Id,
            favorite = true,
            details = "Updated details",
            organized = true,
            urls = new[] { "https://updated.example" },
            aliases = new[] { "Updated alias" },
            tagIds = new[] { updatedTag.Id },
            remoteIds = new[] { new StudioRemoteIdDto("https://updated-metadata.example/graphql", "updated-id") },
            customFields = new Dictionary<string, object> { [customFieldKey] = "Updated tone" },
            rating = 85,
        });
        var retrieved = await AsUser().GetStudioByIdAsync(studio.Id);
        var engagement = await AsUser().GetEntityEngagementAsync(AffinityHostType.Studio, studio.Id);

        // Assert
        updated.Name.Should().Be(studio.Name);
        retrieved.ParentId.Should().Be(updatedParent.Id);
        retrieved.ParentName.Should().Be(updatedParent.Name);
        retrieved.Favorite.Should().BeTrue();
        retrieved.Details.Should().Be("Updated details");
        retrieved.Organized.Should().BeTrue();
        retrieved.Urls.Should().Equal("https://updated.example");
        retrieved.Aliases.Should().Equal("Updated alias");
        retrieved.Tags.Should().ContainSingle().Which.Id.Should().Be(updatedTag.Id);
        retrieved.RemoteIds.Should().Equal(new StudioRemoteIdDto("https://updated-metadata.example/graphql", "updated-id"));
        retrieved.CustomFields.Should().ContainKey(customFieldKey)
            .WhoseValue.Should().BeOfType<JsonElement>()
            .Which.GetString().Should().Be("Updated tone");
        engagement.Rating.Should().Be(85);
    }

    [Fact]
    public async Task GivenParentAndDetails_WhenFieldsAreCleared_ThenValuesBecomeNull()
    {
        // Arrange
        var parent = await AsUser().CreateStudioAsync("Parent Studio");
        var studio = await AsUser().CreateStudioAsync(
            new StudioBuilder()
                .WithName(TestCatalog.Studio.Name)
                .WithParent(parent)
                .WithDetails("Original details")
                .Build());

        // Act
        var updated = await AsUser().UpdateStudioAsync(studio.Id, new
        {
            clearFields = new[] { "parentId", "details" },
        });

        // Assert
        updated.ParentId.Should().BeNull();
        updated.ParentName.Should().BeNull();
        updated.Details.Should().BeNull();
    }

    [Fact]
    public async Task GivenExistingStudio_WhenAnotherStudioIsRenamedToSameName_ThenConflictIsReturnedWithoutChangingStudio()
    {
        // Arrange
        var existing = await AsUser().CreateStudioAsync(TestCatalog.Studio.Name);
        var studio = await AsUser().CreateStudioAsync("Distinct Studio");

        // Act
        var action = () => AsUser().UpdateStudioAsync(
            studio.Id,
            new { name = $" {existing.Name.ToUpperInvariant()} " });

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 409 (Conflict)*");
        var retrieved = await AsUser().GetStudioByIdAsync(studio.Id);
        retrieved.Name.Should().Be(studio.Name);
    }

    [Fact]
    public async Task GivenMember_WhenStudioIsUpdated_ThenWriteAccessIsAllowed()
    {
        // Arrange
        var studio = await AsUser().CreateStudioAsync(TestCatalog.Studio.Name);

        // Act
        var updated = await AsUser(ApiTestUsers.Eva).UpdateStudioAsync(
            studio.Id,
            new { details = "Updated by member" });

        // Assert
        updated.Details.Should().Be("Updated by member");
    }

    [Fact]
    public async Task GivenMissingStudio_WhenUpdated_ThenNotFoundIsReturned()
    {
        // Arrange
        const int missingId = int.MaxValue;

        // Act
        var action = () => AsUser().UpdateStudioAsync(missingId, new { details = "Missing" });

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }
}
