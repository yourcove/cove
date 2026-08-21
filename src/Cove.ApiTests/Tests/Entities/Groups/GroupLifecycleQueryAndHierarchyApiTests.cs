using System.Text.Json;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Entities.Groups;

[Collection(ApiTestLane1Collection.Name)]
public sealed class GroupLifecycleQueryAndHierarchyApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/groups")]
    [CoversEndpoint("GET", "/api/groups/{id:int}")]
    public async Task GivenRichGroupMetadata_WhenOwnerCreatesAndMemberReads_ThenItRoundTrips()
    {
        var owner = AsUser();
        var studio = await owner.CreateStudioAsync($"Group studio {Guid.NewGuid():N}");
        var tag = await owner.CreateTagAsync($"Group tag {Guid.NewGuid():N}");
        var customFieldKey = $"group_note_{Guid.NewGuid():N}";
        await owner.CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = customFieldKey,
            Label = "Group note",
            Type = "text",
            EntityTypes = ["group"],
        });
        var request = new GroupCreateDto(
            Name: $"Group lifecycle {Guid.NewGuid():N}",
            Aliases: "Group alternate",
            Date: "2026-08-15",
            Rating: 84,
            StudioId: studio.Id,
            Director: "Group director",
            Description: "Group description",
            Urls: ["https://groups.example/item"],
            TagIds: [tag.Id],
            CustomFields: new Dictionary<string, object> { [customFieldKey] = "Rich group" },
            ShowInVideoLists: true,
            AllowedHostTypes: ["video", "image"],
            SortOrder: 23);

        var created = await owner.CreateGroupAsync(request);
        var retrieved = await AsUser(ApiTestUsers.Eva).GetGroupByIdAsync(created.Id);
        var ownerEngagement = await owner.GetEntityEngagementAsync(AffinityHostType.Group, created.Id);
        var memberEngagement = await AsUser(ApiTestUsers.Eva).GetEntityEngagementAsync(AffinityHostType.Group, created.Id);

        foreach (var actual in new[] { created, retrieved })
        {
            actual.Name.Should().Be(request.Name);
            actual.Aliases.Should().Be(request.Aliases);
            actual.Date.Should().Be(request.Date);
            actual.StudioId.Should().Be(studio.Id);
            actual.StudioName.Should().Be(studio.Name);
            actual.Director.Should().Be(request.Director);
            actual.Description.Should().Be(request.Description);
            actual.Urls.Should().Equal(request.Urls!);
            actual.Tags.Select(candidate => candidate.Id).Should().Equal(tag.Id);
            actual.CustomFields.Should().ContainKey(customFieldKey).WhoseValue.Should().BeOfType<JsonElement>().Which.GetString().Should().Be("Rich group");
            actual.ShowInVideoLists.Should().BeTrue();
            actual.AllowedHostTypes.Should().Equal("video", "image");
            actual.SortOrder.Should().Be(23);
        }
        ownerEngagement.Rating.Should().Be(request.Rating);
        memberEngagement.Rating.Should().BeNull();
    }

    [Fact]
    [CoversEndpoint("PUT", "/api/groups/{id:int}")]
    public async Task GivenGroupMetadata_WhenMemberPartiallyUpdates_ThenResponseAndReadPreserveUntouchedValues()
    {
        var owner = AsUser();
        var studio = await owner.CreateStudioAsync($"Original group studio {Guid.NewGuid():N}");
        var tag = await owner.CreateTagAsync($"Original group tag {Guid.NewGuid():N}");
        var group = await owner.CreateGroupAsync(new GroupCreateDto(
            Name: $"Original group {Guid.NewGuid():N}",
            Aliases: "Original aliases",
            Date: "2026-08-14",
            Rating: null,
            StudioId: studio.Id,
            Director: "Original director",
            Description: "Original description",
            Urls: ["https://groups.example/original"],
            TagIds: [tag.Id]));
        var update = new GroupUpdateDto(
            Name: "Updated group",
            Aliases: null,
            Date: null,
            Rating: null,
            StudioId: null,
            Director: null,
            Description: "Updated description",
            Urls: ["https://groups.example/updated"],
            TagIds: null,
            CustomFields: null,
            ClearFields: ["studioId"]);

        var updated = await AsUser(ApiTestUsers.Eva).UpdateGroupAsync(group.Id, update);
        var retrieved = await owner.GetGroupByIdAsync(group.Id);

        foreach (var actual in new[] { updated, retrieved })
        {
            actual.Name.Should().Be("Updated group");
            actual.Aliases.Should().Be("Original aliases");
            actual.Date.Should().Be("2026-08-14");
            actual.StudioId.Should().BeNull();
            actual.StudioName.Should().BeNull();
            actual.Director.Should().Be("Original director");
            actual.Description.Should().Be("Updated description");
            actual.Urls.Should().Equal("https://groups.example/updated");
            actual.Tags.Select(candidate => candidate.Id).Should().Equal(tag.Id);
        }
    }

    [Fact]
    [CoversEndpoint("POST", "/api/groups/find")]
    public async Task GivenMatchingGroups_WhenFilteredSortedAndPaged_ThenOnlyRequestedPageIsReturned()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var first = await owner.CreateGroupAsync($"A filtered group {suffix}");
        var second = await owner.CreateGroupAsync($"B filtered group {suffix}");
        await owner.CreateGroupAsync($"Excluded group {suffix}");
        var request = new FilteredQueryRequest<GroupFilter>
        {
            ObjectFilter = new GroupFilter
            {
                NameCriterion = new StringCriterion { Value = "^[AB] filtered group", Modifier = CriterionModifier.MatchesRegex },
            },
            FindFilter = new FindFilter { Q = suffix, Page = 2, PerPage = 1, Sort = "name" },
        };

        var result = await AsUser(ApiTestUsers.Eva).FindGroupsAsync(request);

        result.TotalCount.Should().Be(2);
        result.Page.Should().Be(2);
        result.PerPage.Should().Be(1);
        result.Items.Should().ContainSingle().Which.Id.Should().Be(second.Id);
        result.Items.Should().NotContain(candidate => candidate.Id == first.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/groups/bulk")]
    public async Task GivenTaggedGroups_WhenMemberBulkUpdatesSelection_ThenControlsAndRatingsRemainIsolated()
    {
        var owner = AsUser();
        var originalStudio = await owner.CreateStudioAsync($"Original bulk group studio {Guid.NewGuid():N}");
        var originalTag = await owner.CreateTagAsync($"Original bulk group tag {Guid.NewGuid():N}");
        var replacementTag = await owner.CreateTagAsync($"Replacement bulk group tag {Guid.NewGuid():N}");
        var selected = await Task.WhenAll(Enumerable.Range(1, 2).Select(index => owner.CreateGroupAsync(new GroupCreateDto(
            Name: $"Selected bulk group {index} {Guid.NewGuid():N}", Aliases: null, Date: "2026-08-13", Rating: null,
            StudioId: originalStudio.Id, Director: $"Original director {index}", Description: $"Original description {index}",
            Urls: [$"https://groups.example/selected-{index}"], TagIds: [originalTag.Id]))));
        var control = await owner.CreateGroupAsync(new GroupCreateDto(
            Name: $"Control bulk group {Guid.NewGuid():N}", Aliases: null, Date: "2026-08-12", Rating: null,
            StudioId: originalStudio.Id, Director: "Control director", Description: "Control description", Urls: [], TagIds: [originalTag.Id]));
        await AsUser(ApiTestUsers.Eva).SetGroupRatingAsync(control, 17);
        var request = new BulkGroupUpdateDto
        {
            Ids = selected.Select(group => group.Id).ToList(),
            ClearFields = ["studioId", "date", "director", "description"],
            Rating = 91,
            TagIds = [replacementTag.Id],
            TagMode = BulkUpdateMode.Set,
        };

        var updatedCount = await AsUser(ApiTestUsers.Eva).BulkUpdateGroupsAsync(request);
        var updated = await Task.WhenAll(selected.Select(group => owner.GetGroupByIdAsync(group.Id)));
        var retained = await owner.GetGroupByIdAsync(control.Id);
        var engagements = await Task.WhenAll(selected.Select(group => AsUser(ApiTestUsers.Eva).GetEntityEngagementAsync(AffinityHostType.Group, group.Id)));
        var retainedEngagement = await AsUser(ApiTestUsers.Eva).GetEntityEngagementAsync(AffinityHostType.Group, control.Id);
        var ownerEngagements = await Task.WhenAll(selected.Append(control).Select(group => owner.GetEntityEngagementAsync(AffinityHostType.Group, group.Id)));
        var originalsById = selected.ToDictionary(group => group.Id);

        updatedCount.Should().Be(2);
        updated.Should().AllSatisfy(group =>
        {
            group.Date.Should().BeNull();
            group.Director.Should().BeNull();
            group.Description.Should().BeNull();
            group.StudioId.Should().BeNull();
            group.StudioName.Should().BeNull();
            group.Tags.Select(candidate => candidate.Id).Should().Equal(replacementTag.Id);
            group.Name.Should().Be(originalsById[group.Id].Name);
            group.Urls.Should().Equal(originalsById[group.Id].Urls);
        });
        engagements.Should().AllSatisfy(engagement => engagement.Rating.Should().Be(91));
        ownerEngagements.Should().AllSatisfy(engagement => engagement.Rating.Should().BeNull());
        retained.Date.Should().Be("2026-08-12");
        retained.Director.Should().Be("Control director");
        retained.Description.Should().Be("Control description");
        retained.StudioId.Should().Be(originalStudio.Id);
        retained.StudioName.Should().Be(originalStudio.Name);
        retained.Tags.Select(candidate => candidate.Id).Should().Equal(originalTag.Id);
        retainedEngagement.Rating.Should().Be(17);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/groups/{id:int}/subgroups")]
    [CoversEndpoint("GET", "/api/groups/{id:int}/subgroups")]
    [CoversEndpoint("GET", "/api/groups/{id:int}/containinggroups")]
    [CoversEndpoint("DELETE", "/api/groups/{id:int}/subgroups/{subgroupid:int}")]
    public async Task GivenGroups_WhenMemberAddsReadsAndRemovesSubGroup_ThenBothDirectionsStayInSync()
    {
        var owner = AsUser();
        var parent = await owner.CreateGroupAsync($"Parent group {Guid.NewGuid():N}");
        var child = await owner.CreateGroupAsync($"Child group {Guid.NewGuid():N}");
        var retainedSibling = await owner.CreateGroupAsync($"Sibling group {Guid.NewGuid():N}");
        var retainedParent = await owner.CreateGroupAsync($"Second parent group {Guid.NewGuid():N}");
        var member = AsUser(ApiTestUsers.Eva);

        await member.AddSubGroupAsync(parent.Id, new AddSubGroupDto(child.Id, OrderIndex: 3, Description: "Nested group"));
        await member.AddSubGroupAsync(parent.Id, new AddSubGroupDto(retainedSibling.Id, OrderIndex: 1, Description: "Retained sibling"));
        await member.AddSubGroupAsync(retainedParent.Id, new AddSubGroupDto(child.Id, OrderIndex: 0, Description: "Retained parent"));
        (await owner.GetSubGroupsAsync(parent.Id)).Select(group => group.Id).Should().Equal(retainedSibling.Id, child.Id);
        (await owner.GetContainingGroupsAsync(child.Id)).Select(group => group.Id).Should().Equal(retainedParent.Id, parent.Id);
        (await owner.GetGroupByIdAsync(parent.Id)).SubGroupCount.Should().Be(2);
        (await owner.GetGroupByIdAsync(child.Id)).ContainingGroupCount.Should().Be(2);

        await member.RemoveSubGroupAsync(parent.Id, child.Id);

        (await owner.GetSubGroupsAsync(parent.Id)).Select(group => group.Id).Should().Equal(retainedSibling.Id);
        (await owner.GetContainingGroupsAsync(child.Id)).Select(group => group.Id).Should().Equal(retainedParent.Id);
        (await owner.GetGroupByIdAsync(parent.Id)).SubGroupCount.Should().Be(1);
        (await owner.GetGroupByIdAsync(child.Id)).ContainingGroupCount.Should().Be(1);
    }

    [Fact]
    [CoversEndpoint("DELETE", "/api/groups/{id:int}")]
    public async Task GivenGroup_WhenMemberAttemptsDelete_ThenOwnerCanDeleteAndTheRecordIsGone()
    {
        var owner = AsUser();
        var group = await owner.CreateGroupAsync($"Delete group {Guid.NewGuid():N}");
        var forbidden = () => AsUser(ApiTestUsers.Eva).DeleteGroupAsync(group.Id);

        await forbidden.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await owner.GetGroupByIdAsync(group.Id)).Id.Should().Be(group.Id);
        await owner.DeleteGroupAsync(group.Id);
        var missing = () => owner.GetGroupByIdAsync(group.Id);
        await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
    }
}
