using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Entities.Performers;

[Collection(ApiTestLane2Collection.Name)]
public sealed class PerformerQueryBulkAndRelationshipApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenVisibleGroupWithHiddenPerformer_WhenMemberReadsItems_ThenDirectPerformerItemIsConcealed()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var performer = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Scoped direct performer {suffix}").Build());
        var group = await owner.CreateGroupAsync($"Visible scoped performer group {suffix}");
        await owner.AddPerformerToGroupAsync(performer, group);
        var memberRole = (await owner.GetRolesAsync()).Single(role => role.Name == BuiltinRoles.Member);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            memberRole.Id,
            EntityKinds.Performer,
            Effect: "deny",
            ScopeKind: "all",
            ScopeValue: "{}",
            AppliesTo: "read"));

        var items = await AsUser(ApiTestUsers.Eva).GetGroupItemsAsync(group);

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task GivenVisiblePerformerWithHiddenGroup_WhenMemberReadsGroups_ThenDirectGroupIsConcealed()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var performer = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Visible scoped performer {suffix}").Build());
        var group = await owner.CreateGroupAsync($"Hidden scoped performer group {suffix}");
        await owner.AddPerformerToGroupAsync(performer, group);
        var memberRole = (await owner.GetRolesAsync()).Single(role => role.Name == BuiltinRoles.Member);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            memberRole.Id,
            EntityKinds.Group,
            Effect: "deny",
            ScopeKind: "all",
            ScopeValue: "{}",
            AppliesTo: "read"));

        var groups = await AsUser(ApiTestUsers.Eva).GetPerformerGroupsAsync(performer.Id, page: 1, perPage: 10);

        groups.TotalCount.Should().Be(0);
        groups.Items.Should().BeEmpty();
    }

    [Fact]
    [CoversEndpoint("POST", "/api/performers/find")]
    public async Task GivenMatchingPerformers_WhenMemberFiltersSortsAndPages_ThenOnlyRequestedPageIsReturned()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var first = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"A favorite performer {suffix}").AsFavorite().Build());
        var second = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"B favorite performer {suffix}").AsFavorite().Build());
        await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Excluded performer {suffix}").Build());
        await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Unrelated favorite performer {Guid.NewGuid():N}").AsFavorite().Build());
        var request = new FilteredQueryRequest<PerformerFilter>
        {
            ObjectFilter = new PerformerFilter { FavoriteCriterion = new BoolCriterion { Value = true } },
            FindFilter = new FindFilter { Q = suffix, Page = 2, PerPage = 1, Sort = "name" },
        };

        var result = await AsUser(ApiTestUsers.Eva).FindPerformersAsync(request);

        result.TotalCount.Should().Be(2);
        result.Page.Should().Be(2);
        result.PerPage.Should().Be(1);
        var item = result.Items.Should().ContainSingle().Which;
        item.Id.Should().Be(second.Id);
        item.Name.Should().Be(second.Name);
        item.Favorite.Should().BeTrue();
        result.Items.Should().NotContain(performer => performer.Id == first.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/performers/bulk")]
    public async Task GivenTaggedPerformers_WhenMemberBulkSetsValues_ThenOnlySelectedPerformersChange()
    {
        var owner = AsUser();
        var originalTag = await owner.CreateTagAsync($"Original bulk performer tag {Guid.NewGuid():N}");
        var replacementTag = await owner.CreateTagAsync($"Replacement bulk performer tag {Guid.NewGuid():N}");
        var selected = await Task.WhenAll(Enumerable.Range(1, 2).Select(index => owner.CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Selected bulk performer {index} {Guid.NewGuid():N}")
            .WithGender("Female")
            .WithDetails($"Original details {index}")
            .WithTag(originalTag)
            .Build())));
        var control = await owner.CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Control bulk performer {Guid.NewGuid():N}")
            .WithGender("Male")
            .WithDetails("Control details")
            .WithTag(originalTag)
            .Build());
        await AsUser(ApiTestUsers.Eva).SetPerformerRatingAsync(control, 17);
        var request = new BulkPerformerUpdateDto
        {
            Ids = selected.Select(performer => performer.Id).ToList(),
            Favorite = true,
            Gender = "NonBinary",
            Details = "Updated details",
            Rating = 91,
            TagIds = [replacementTag.Id],
            TagMode = BulkUpdateMode.Set,
        };

        var updatedCount = await AsUser(ApiTestUsers.Eva).BulkUpdatePerformersAsync(request);
        var updated = await Task.WhenAll(selected.Select(performer => owner.GetPerformerByIdAsync(performer.Id)));
        var retained = await owner.GetPerformerByIdAsync(control.Id);
        var engagements = await Task.WhenAll(selected.Select(performer => AsUser(ApiTestUsers.Eva).GetEntityEngagementAsync(AffinityHostType.Performer, performer.Id)));
        var retainedEngagement = await AsUser(ApiTestUsers.Eva).GetEntityEngagementAsync(AffinityHostType.Performer, control.Id);
        var ownerEngagements = await Task.WhenAll(selected.Append(control).Select(performer => owner.GetEntityEngagementAsync(AffinityHostType.Performer, performer.Id)));
        var originalsById = selected.ToDictionary(performer => performer.Id);

        updatedCount.Should().Be(2);
        updated.Should().AllSatisfy(performer =>
        {
            performer.Favorite.Should().BeTrue();
            performer.Name.Should().Be(originalsById[performer.Id].Name);
            performer.Gender.Should().Be("NonBinary");
            performer.Details.Should().Be("Updated details");
            performer.Tags.Select(tag => tag.Id).Should().Equal(replacementTag.Id);
        });
        engagements.Should().AllSatisfy(engagement => engagement.Rating.Should().Be(91));
        ownerEngagements.Should().AllSatisfy(engagement => engagement.Rating.Should().BeNull());
        retained.Favorite.Should().BeFalse();
        retained.Gender.Should().Be("Male");
        retained.Details.Should().Be("Control details");
        retained.Tags.Select(tag => tag.Id).Should().Equal(originalTag.Id);
        retainedEngagement.Rating.Should().Be(17);
    }

    [Fact]
    [CoversEndpoint("GET", "/api/performers/{id:int}/groups")]
    [CoversEndpoint("GET", "/api/performers/{id:int}/appears-with")]
    public async Task GivenPerformerVideosInGroups_WhenMemberReadsRelationships_ThenGroupAndCoPerformerArePaged()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var focal = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Focal performer {suffix}").Build());
        var coPerformer = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Co performer {suffix}").Build());
        var unrelated = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Unrelated performer {suffix}").Build());
        var directOnlyGroup = await owner.CreateGroupAsync($"Direct performer relationship group {suffix}");
        var videoOnlyGroup = await owner.CreateGroupAsync($"Video performer relationship group {suffix}");
        var dualPathGroup = await owner.CreateGroupAsync($"Dual performer relationship group {suffix}");
        await owner.AddPerformerToGroupAsync(focal, directOnlyGroup);
        await owner.AddPerformerToGroupAsync(focal, dualPathGroup);
        await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"First relationship video {suffix}")
            .WithPerformers([focal, coPerformer])
            .WithGroup(videoOnlyGroup)
            .Build());
        await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Second relationship video {suffix}")
            .WithPerformers([focal, coPerformer])
            .WithGroup(dualPathGroup)
            .Build());

        var groups = await AsUser(ApiTestUsers.Eva).GetPerformerGroupsAsync(focal.Id, page: 1, perPage: 10);
        var appearsWith = await AsUser(ApiTestUsers.Eva).GetPerformerAppearsWithAsync(focal.Id, page: 1, perPage: 1);

        groups.TotalCount.Should().Be(3);
        groups.Page.Should().Be(1);
        groups.PerPage.Should().Be(10);
        groups.Items.Select(group => group.Id).Should().BeEquivalentTo(
            [directOnlyGroup.Id, videoOnlyGroup.Id, dualPathGroup.Id]);
        appearsWith.TotalCount.Should().Be(1);
        appearsWith.Page.Should().Be(1);
        appearsWith.PerPage.Should().Be(1);
        appearsWith.Items.Should().ContainSingle().Which.Id.Should().Be(coPerformer.Id);
        appearsWith.Items.Should().NotContain(performer => performer.Id == unrelated.Id);
    }

    [Fact]
    [CoversEndpoint("DELETE", "/api/performers/bulk")]
    public async Task GivenPerformers_WhenOwnerBulkDeletesSelection_ThenMemberCannotDeleteAndControlRemains()
    {
        var owner = AsUser();
        var first = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Bulk delete performer first {Guid.NewGuid():N}").Build());
        var second = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Bulk delete performer second {Guid.NewGuid():N}").Build());
        var retained = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Retained bulk delete performer {Guid.NewGuid():N}").Build());
        var request = new BatchDeleteDto([first.Id, int.MaxValue, second.Id]);
        var forbidden = () => AsUser(ApiTestUsers.Eva).BulkDeletePerformersAsync(request);

        await forbidden.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await owner.GetPerformerByIdAsync(first.Id)).Id.Should().Be(first.Id);
        (await owner.GetPerformerByIdAsync(second.Id)).Id.Should().Be(second.Id);
        var deleted = await owner.BulkDeletePerformersAsync(request);

        deleted.Should().Be(2);
        foreach (var performer in new[] { first, second })
        {
            var missing = () => owner.GetPerformerByIdAsync(performer.Id);
            await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        }
        (await owner.GetPerformerByIdAsync(retained.Id)).Id.Should().Be(retained.Id);
    }
}
