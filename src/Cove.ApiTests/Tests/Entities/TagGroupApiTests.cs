using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Tests.Entities;

[Collection(ApiTestLane2Collection.Name)]
public sealed class TagGroupApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/taggroups")]
    [CoversEndpoint("GET", "/api/taggroups/{id:int}")]
    public async Task GivenTagGroupMetadata_WhenTagGroupIsCreated_ThenMetadataCanBeRetrieved()
    {
        // Arrange
        var request = new TagGroupCreateDto(
            Name: " Production Motifs ",
            Description: " Recurring production details ",
            Color: " #6b4f3a ",
            SortOrder: 3);

        // Act
        var created = await AsUser().CreateTagGroupAsync(request, TestContext.Current.CancellationToken);
        var retrieved = await AsUser().GetTagGroupByIdAsync(created.Id, TestContext.Current.CancellationToken);

        // Assert
        retrieved.Should().BeEquivalentTo(created, options => options
            .Excluding(group => group.CreatedAt)
            .Excluding(group => group.UpdatedAt));
        created.Id.Should().BePositive();
        created.Name.Should().Be("Production Motifs");
        created.Description.Should().Be("Recurring production details");
        created.Color.Should().Be("#6b4f3a");
        created.SortOrder.Should().Be(3);
        created.TagCount.Should().Be(0);
        DateTimeOffset.TryParse(created.CreatedAt, out var createdAt).Should().BeTrue();
        DateTimeOffset.TryParse(created.UpdatedAt, out var updatedAt).Should().BeTrue();
        updatedAt.Should().BeOnOrAfter(createdAt);
        DateTimeOffset.Parse(retrieved.CreatedAt).Should().BeCloseTo(createdAt, TimeSpan.FromMilliseconds(1));
        DateTimeOffset.Parse(retrieved.UpdatedAt).Should().BeCloseTo(updatedAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task GivenBlankOptionalMetadata_WhenTagGroupIsCreated_ThenOptionalMetadataIsNull()
    {
        // Arrange
        var request = new TagGroupCreateDto(
            Name: "Lighting Cues",
            Description: " \t ",
            Color: " \t ");

        // Act
        var created = await AsUser().CreateTagGroupAsync(request, TestContext.Current.CancellationToken);

        // Assert
        created.Description.Should().BeNull();
        created.Color.Should().BeNull();
    }

    [Theory]
    [InlineData("#012345")]
    [InlineData("#abcdef")]
    [InlineData("#ABCDEF")]
    [InlineData("#01234567")]
    [InlineData(" #89aBcDeF ")]
    public async Task GivenSupportedColor_WhenTagGroupIsCreated_ThenColorIsAccepted(string color)
    {
        // Arrange
        var request = new TagGroupCreateDto("Color Group", Color: color);

        // Act
        var created = await AsUser().CreateTagGroupAsync(request, TestContext.Current.CancellationToken);

        // Assert
        created.Color.Should().Be(color.Trim());
    }

    [Theory]
    [InlineData("012345")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#123456789")]
    [InlineData("#12345g")]
    public async Task GivenUnsupportedColor_WhenTagGroupIsCreated_ThenBadRequestIsReturned(string color)
    {
        // Arrange
        var request = new TagGroupCreateDto("Invalid Color Group", Color: color);

        // Act
        var action = () => AsUser().CreateTagGroupAsync(request);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*");
    }

    [Fact]
    public async Task GivenBlankName_WhenTagGroupIsCreated_ThenBadRequestIsReturned()
    {
        // Arrange
        var request = new TagGroupCreateDto(" \t ");

        // Act
        var action = () => AsUser().CreateTagGroupAsync(request);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*");
    }

    [Fact]
    public async Task GivenTagGroup_WhenExactNormalizedNameIsCreatedAgain_ThenConflictIsReturned()
    {
        // Arrange
        await AsUser().CreateTagGroupAsync(new TagGroupCreateDto("Camera Movement"), TestContext.Current.CancellationToken);

        // Act
        var action = () => AsUser().CreateTagGroupAsync(
            new TagGroupCreateDto(" Camera Movement "));

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 409 (Conflict)*");
    }

    [Fact]
    public async Task GivenExistingSortOrders_WhenTagGroupsUseDefaultSortOrder_ThenDefaultsAdvanceByTen()
    {
        // Arrange
        var first = await AsUser().CreateTagGroupAsync(new TagGroupCreateDto("First Default"), TestContext.Current.CancellationToken);
        await AsUser().CreateTagGroupAsync(new TagGroupCreateDto("Explicit Order", SortOrder: 50), TestContext.Current.CancellationToken);

        // Act
        var afterExplicit = await AsUser().CreateTagGroupAsync(new TagGroupCreateDto("After Explicit"), TestContext.Current.CancellationToken);

        // Assert
        first.SortOrder.Should().Be(10);
        afterExplicit.SortOrder.Should().Be(60);
    }

    [Fact]
    public async Task GivenTagGroups_WhenListed_ThenTheyAreOrderedBySortOrderAndName()
    {
        // Arrange
        var secondByName = await AsUser().CreateTagGroupAsync(new TagGroupCreateDto("Zulu", SortOrder: 20), TestContext.Current.CancellationToken);
        var firstByOrder = await AsUser().CreateTagGroupAsync(new TagGroupCreateDto("Middle", SortOrder: 10), TestContext.Current.CancellationToken);
        var firstByName = await AsUser().CreateTagGroupAsync(new TagGroupCreateDto("Alpha", SortOrder: 20), TestContext.Current.CancellationToken);

        // Act
        var groups = await AsUser().GetTagGroupsAsync(TestContext.Current.CancellationToken);

        // Assert
        groups.Select(group => group.Id).Should().Equal(
            firstByOrder.Id,
            firstByName.Id,
            secondByName.Id);
    }

    [Fact]
    [CoversEndpoint("PUT", "/api/taggroups/{id:int}")]
    public async Task GivenTagGroup_WhenPartiallyUpdated_ThenOnlySuppliedMetadataChanges()
    {
        // Arrange
        var created = await AsUser().CreateTagGroupAsync(new TagGroupCreateDto(
            Name: "Original Group",
            Description: "Original description",
            Color: "#123456",
            SortOrder: 15), TestContext.Current.CancellationToken);

        // Act
        var updated = await AsUser().UpdateTagGroupAsync(created.Id, new TagGroupUpdateDto(Name: " Updated Group ", Color: " #abcdef ", SortOrder: 25), TestContext.Current.CancellationToken);

        // Assert
        updated.Name.Should().Be("Updated Group");
        updated.Description.Should().Be(created.Description);
        updated.Color.Should().Be("#abcdef");
        updated.SortOrder.Should().Be(25);
        DateTimeOffset.Parse(updated.UpdatedAt).Should().BeOnOrAfter(
            DateTimeOffset.Parse(created.UpdatedAt));
    }

    [Fact]
    public async Task GivenOptionalMetadata_WhenUpdatedWithBlankValues_ThenOptionalMetadataIsCleared()
    {
        // Arrange
        var created = await AsUser().CreateTagGroupAsync(new TagGroupCreateDto(
            Name: "Clearable Group",
            Description: "Description",
            Color: "#123456"), TestContext.Current.CancellationToken);

        // Act
        var updated = await AsUser().UpdateTagGroupAsync(created.Id, new TagGroupUpdateDto(Description: " \t ", Color: " \t "), TestContext.Current.CancellationToken);

        // Assert
        updated.Description.Should().BeNull();
        updated.Color.Should().BeNull();
    }

    [Fact]
    public async Task GivenUnsupportedColor_WhenTagGroupIsUpdated_ThenBadRequestIsReturnedWithoutChangingGroup()
    {
        // Arrange
        var created = await AsUser().CreateTagGroupAsync(new TagGroupCreateDto(
            Name: "Stable Color Group",
            Color: "#123456"), TestContext.Current.CancellationToken);

        // Act
        var action = () => AsUser().UpdateTagGroupAsync(
            created.Id,
            new TagGroupUpdateDto(Color: "#12345g"));

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*");
        var retrieved = await AsUser().GetTagGroupByIdAsync(created.Id, TestContext.Current.CancellationToken);
        retrieved.Color.Should().Be(created.Color);
    }

    [Fact]
    public async Task GivenExistingTagGroup_WhenAnotherGroupIsRenamedToExactNormalizedName_ThenConflictIsReturnedWithoutChangingGroup()
    {
        // Arrange
        var existing = await AsUser().CreateTagGroupAsync(new TagGroupCreateDto("Existing Group"), TestContext.Current.CancellationToken);
        var renamed = await AsUser().CreateTagGroupAsync(new TagGroupCreateDto("Renamed Group"), TestContext.Current.CancellationToken);

        // Act
        var action = () => AsUser().UpdateTagGroupAsync(
            renamed.Id,
            new TagGroupUpdateDto(Name: $" {existing.Name} "));

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 409 (Conflict)*");
        var retrieved = await AsUser().GetTagGroupByIdAsync(renamed.Id, TestContext.Current.CancellationToken);
        retrieved.Name.Should().Be(renamed.Name);
    }

    [Fact]
    public async Task GivenTagsInTagGroup_WhenTagGroupIsRead_ThenTagCountIsCurrent()
    {
        // Arrange
        var group = await AsUser().CreateTagGroupAsync(new TagGroupCreateDto("Production Details"), TestContext.Current.CancellationToken);
        await AsUser().CreateTagAsync(new TagBuilder().WithName("Practical Lighting").WithTagGroup(group).Build(), TestContext.Current.CancellationToken);
        await AsUser().CreateTagAsync(new TagBuilder().WithName("Visible Boom Microphone").WithTagGroup(group).Build(), TestContext.Current.CancellationToken);

        // Act
        var detail = await AsUser().GetTagGroupByIdAsync(group.Id, TestContext.Current.CancellationToken);
        var listed = (await AsUser().GetTagGroupsAsync(TestContext.Current.CancellationToken)).Single(candidate => candidate.Id == group.Id);

        // Assert
        detail.TagCount.Should().Be(2);
        listed.TagCount.Should().Be(2);
    }

    [Fact]
    [CoversEndpoint("PUT", "/api/tags/{id:int}")]
    public async Task GivenTagInTagGroup_WhenAssignedToAnotherTagGroup_ThenTagBelongsOnlyToNewGroup()
    {
        // Arrange
        var originalGroup = await AsUser().CreateTagGroupAsync(new TagGroupCreateDto("Original Group"), TestContext.Current.CancellationToken);
        var newGroup = await AsUser().CreateTagGroupAsync(new TagGroupCreateDto("New Group"), TestContext.Current.CancellationToken);
        var tag = await AsUser().CreateTagAsync(new TagBuilder().WithName("Reassigned Tag").WithTagGroup(originalGroup).Build(), TestContext.Current.CancellationToken);
        var update = new TagUpdateDto(
            Name: null,
            SortName: null,
            Description: null,
            Favorite: null,
            Aliases: null,
            ParentIds: null,
            ChildIds: null,
            CustomFields: null,
            TagGroupId: newGroup.Id);

        // Act
        var updated = await AsUser().UpdateTagAsync(tag.Id, update, TestContext.Current.CancellationToken);
        var originalGroupAfter = await AsUser().GetTagGroupByIdAsync(originalGroup.Id, TestContext.Current.CancellationToken);
        var newGroupAfter = await AsUser().GetTagGroupByIdAsync(newGroup.Id, TestContext.Current.CancellationToken);

        // Assert
        updated.TagGroupId.Should().Be(newGroup.Id);
        updated.TagGroupName.Should().Be(newGroup.Name);
        originalGroupAfter.TagCount.Should().Be(0);
        newGroupAfter.TagCount.Should().Be(1);
    }

    [Fact]
    [CoversEndpoint("DELETE", "/api/taggroups/{id:int}")]
    public async Task GivenTagGroupWithTag_WhenDeleted_ThenTagIsPreservedWithoutTagGroup()
    {
        // Arrange
        var group = await AsUser().CreateTagGroupAsync(new TagGroupCreateDto(
            Name: "Temporary Category",
            Color: "#654321"), TestContext.Current.CancellationToken);
        var tag = await AsUser().CreateTagAsync(new TagBuilder().WithName("Preserved Tag").WithTagGroup(group).Build(), TestContext.Current.CancellationToken);

        // Act
        await AsUser().DeleteTagGroupAsync(group.Id, TestContext.Current.CancellationToken);
        var tagAfter = await AsUser().GetTagByIdAsync(tag.Id, TestContext.Current.CancellationToken);

        // Assert
        tagAfter.TagGroupId.Should().BeNull();
        tagAfter.TagGroupName.Should().BeNull();
        tagAfter.TagGroupColor.Should().BeNull();
        (await AsUser().GetTagGroupsAsync(TestContext.Current.CancellationToken)).Should().NotContain(candidate => candidate.Id == group.Id);
        var getDeleted = () => AsUser().GetTagGroupByIdAsync(group.Id);
        await getDeleted.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    public async Task GivenMember_WhenTagGroupIsCreatedAndUpdated_ThenWriteAccessIsAllowed()
    {
        // Arrange
        var member = AsUser(ApiTestUsers.Eva);

        // Act
        var created = await member.CreateTagGroupAsync(new TagGroupCreateDto("Member Managed"), TestContext.Current.CancellationToken);
        var updated = await member.UpdateTagGroupAsync(created.Id, new TagGroupUpdateDto(Description: "Updated by member"), TestContext.Current.CancellationToken);
        var retrieved = await member.GetTagGroupByIdAsync(created.Id, TestContext.Current.CancellationToken);

        // Assert
        updated.Description.Should().Be("Updated by member");
        retrieved.Should().BeEquivalentTo(updated, options => options
            .Excluding(group => group.CreatedAt)
            .Excluding(group => group.UpdatedAt));
        DateTimeOffset.Parse(retrieved.UpdatedAt).Should().BeCloseTo(
            DateTimeOffset.Parse(updated.UpdatedAt),
            TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task GivenMember_WhenTagGroupIsDeleted_ThenForbiddenIsReturned()
    {
        // Arrange
        var group = await AsUser().CreateTagGroupAsync(new TagGroupCreateDto("Owner Managed"), TestContext.Current.CancellationToken);

        // Act
        var action = () => AsUser(ApiTestUsers.Eva).DeleteTagGroupAsync(group.Id);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        (await AsUser().GetTagGroupsAsync(TestContext.Current.CancellationToken)).Should().Contain(candidate => candidate.Id == group.Id);
    }

    [Fact]
    public async Task GivenMissingTagGroup_WhenReadUpdatedOrDeleted_ThenNotFoundIsReturned()
    {
        // Arrange
        const int missingId = int.MaxValue;

        // Act
        var read = () => AsUser().GetTagGroupByIdAsync(missingId);
        var update = () => AsUser().UpdateTagGroupAsync(
            missingId,
            new TagGroupUpdateDto(Description: "Missing"));
        var delete = () => AsUser().DeleteTagGroupAsync(missingId);

        // Assert
        await read.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
        await update.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
        await delete.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }
}
