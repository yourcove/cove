using System.Globalization;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Entities.Performers;

public sealed class PerformerQueryBulkAndRelationshipApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenVisibleGroupWithHiddenPerformer_WhenMemberReadsItems_ThenDirectPerformerItemIsConcealed()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var performer = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Scoped direct performer {suffix}").Build(), TestContext.Current.CancellationToken);
        var group = await owner.CreateGroupAsync($"Visible scoped performer group {suffix}", TestContext.Current.CancellationToken);
        await owner.AddPerformerToGroupAsync(performer, group, TestContext.Current.CancellationToken);
        var memberRole = (await owner.GetRolesAsync(TestContext.Current.CancellationToken)).Single(role => role.Name == BuiltinRoles.Member);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            memberRole.Id,
            EntityKinds.Performer,
            Effect: "deny",
            ScopeKind: "all",
            ScopeValue: "{}",
            AppliesTo: "read"), TestContext.Current.CancellationToken);

        var items = await AsUser(ApiTestUsers.Eva).GetGroupItemsAsync(group, TestContext.Current.CancellationToken);

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task GivenVisiblePerformerWithHiddenGroup_WhenMemberReadsGroups_ThenDirectGroupIsConcealed()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var performer = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Visible scoped performer {suffix}").Build(), TestContext.Current.CancellationToken);
        var group = await owner.CreateGroupAsync($"Hidden scoped performer group {suffix}", TestContext.Current.CancellationToken);
        await owner.AddPerformerToGroupAsync(performer, group, TestContext.Current.CancellationToken);
        var memberRole = (await owner.GetRolesAsync(TestContext.Current.CancellationToken)).Single(role => role.Name == BuiltinRoles.Member);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            memberRole.Id,
            EntityKinds.Group,
            Effect: "deny",
            ScopeKind: "all",
            ScopeValue: "{}",
            AppliesTo: "read"), TestContext.Current.CancellationToken);

        var groups = await AsUser(ApiTestUsers.Eva).GetPerformerGroupsAsync(performer.Id, page: 1, perPage: 10, cancellationToken: TestContext.Current.CancellationToken);

        groups.TotalCount.Should().Be(0);
        groups.Items.Should().BeEmpty();
    }

    [Fact]
    [CoversEndpoint("POST", "/api/performers/find")]
    public async Task GivenMatchingPerformers_WhenMemberFiltersSortsAndPages_ThenOnlyRequestedPageIsReturned()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var second = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"B favorite performer {suffix}").AsFavorite().Build(), TestContext.Current.CancellationToken);
        var first = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"A favorite performer {suffix}").AsFavorite().Build(), TestContext.Current.CancellationToken);
        await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Excluded performer {suffix}").Build(), TestContext.Current.CancellationToken);
        await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Unrelated favorite performer {Guid.NewGuid():N}").AsFavorite().Build(), TestContext.Current.CancellationToken);
        var request = new FilteredQueryRequest<PerformerFilter>
        {
            ObjectFilter = new PerformerFilter { FavoriteCriterion = new BoolCriterion { Value = true } },
            FindFilter = new FindFilter { Q = suffix, Page = 2, PerPage = 1, Sort = "name" },
        };

        var result = await AsUser(ApiTestUsers.Eva).FindPerformersAsync(request, TestContext.Current.CancellationToken);

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
        var originalTag = await owner.CreateTagAsync($"Original bulk performer tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var replacementTag = await owner.CreateTagAsync($"Replacement bulk performer tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
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
            .Build(), TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).SetPerformerRatingAsync(control, 17, cancellationToken: TestContext.Current.CancellationToken);
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

        var updatedCount = await AsUser(ApiTestUsers.Eva).BulkUpdatePerformersAsync(request, TestContext.Current.CancellationToken);
        var updated = await Task.WhenAll(selected.Select(performer => owner.GetPerformerByIdAsync(performer.Id)));
        var retained = await owner.GetPerformerByIdAsync(control.Id, TestContext.Current.CancellationToken);
        var engagements = await Task.WhenAll(selected.Select(performer => AsUser(ApiTestUsers.Eva).GetEntityEngagementAsync(AffinityHostType.Performer, performer.Id)));
        var retainedEngagement = await AsUser(ApiTestUsers.Eva).GetEntityEngagementAsync(AffinityHostType.Performer, control.Id, TestContext.Current.CancellationToken);
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

        var addedCount = await AsUser(ApiTestUsers.Eva).BulkUpdatePerformersAsync(new BulkPerformerUpdateDto
        {
            Ids = selected.Select(performer => performer.Id).ToList(),
            TagIds = [originalTag.Id],
            TagMode = BulkUpdateMode.Add,
        }, TestContext.Current.CancellationToken);
        var afterAdd = await Task.WhenAll(selected.Select(performer => owner.GetPerformerByIdAsync(performer.Id)));
        var removedCount = await AsUser(ApiTestUsers.Eva).BulkUpdatePerformersAsync(new BulkPerformerUpdateDto
        {
            Ids = selected.Select(performer => performer.Id).ToList(),
            TagIds = [replacementTag.Id],
            TagMode = BulkUpdateMode.Remove,
        }, TestContext.Current.CancellationToken);
        var afterRemove = await Task.WhenAll(selected.Select(performer => owner.GetPerformerByIdAsync(performer.Id)));

        addedCount.Should().Be(2);
        afterAdd.Should().AllSatisfy(performer => performer.Tags.Select(tag => tag.Id).Should().BeEquivalentTo([originalTag.Id, replacementTag.Id]));
        removedCount.Should().Be(2);
        afterRemove.Should().AllSatisfy(performer => performer.Tags.Select(tag => tag.Id).Should().Equal(originalTag.Id));
        (await owner.GetPerformerByIdAsync(control.Id, TestContext.Current.CancellationToken)).Tags.Select(tag => tag.Id).Should().Equal(originalTag.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/content-rules/overrides")]
    public async Task GivenPerformerWriteOverride_WhenMemberBulkUpdatesMixedScope_ThenEntireRequestIsForbidden()
    {
        var owner = AsUser();
        var memberRole = (await owner.GetRolesAsync(TestContext.Current.CancellationToken)).Should().ContainSingle(role => role.Name == BuiltinRoles.Member).Which;
        var allowed = await owner.CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Allowed bulk performer {Guid.NewGuid():N}")
            .WithDetails("Allowed original details")
            .Build(), TestContext.Current.CancellationToken);
        var denied = await owner.CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Denied bulk performer {Guid.NewGuid():N}")
            .WithDetails("Denied original details")
            .Build(), TestContext.Current.CancellationToken);
        var entityOverride = await owner.CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            memberRole.Id,
            EntityKinds.Performer,
            denied.Id.ToString(CultureInfo.InvariantCulture),
            "deny",
            "write"), TestContext.Current.CancellationToken);
        var mixedRequest = new BulkPerformerUpdateDto
        {
            Ids = [allowed.Id, denied.Id],
            Details = "Mixed request must not persist",
        };
        var forbidden = () => AsUser(ApiTestUsers.Eva).BulkUpdatePerformersAsync(mixedRequest);

        entityOverride.RoleId.Should().Be(memberRole.Id);
        entityOverride.EntityKind.Should().Be(EntityKinds.Performer);
        entityOverride.EntityId.Should().Be(denied.Id.ToString(CultureInfo.InvariantCulture));
        entityOverride.Effect.Should().Be("deny");
        entityOverride.AppliesTo.Should().Be("write");
        await forbidden.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await owner.GetPerformerByIdAsync(allowed.Id, TestContext.Current.CancellationToken)).Details.Should().Be("Allowed original details");
        (await owner.GetPerformerByIdAsync(denied.Id, TestContext.Current.CancellationToken)).Details.Should().Be("Denied original details");

        var updatedCount = await AsUser(ApiTestUsers.Eva).BulkUpdatePerformersAsync(new BulkPerformerUpdateDto
        {
            Ids = [allowed.Id],
            Details = "Allowed updated details",
        }, TestContext.Current.CancellationToken);

        updatedCount.Should().Be(1);
        (await owner.GetPerformerByIdAsync(allowed.Id, TestContext.Current.CancellationToken)).Details.Should().Be("Allowed updated details");
        (await owner.GetPerformerByIdAsync(denied.Id, TestContext.Current.CancellationToken)).Details.Should().Be("Denied original details");
    }

    [Fact]
    [CoversEndpoint("GET", "/api/performers/{id:int}/groups")]
    [CoversEndpoint("GET", "/api/performers/{id:int}/appears-with")]
    public async Task GivenPerformerVideosInGroups_WhenMemberReadsRelationships_ThenGroupAndCoPerformerArePaged()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var focal = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Focal performer {suffix}").Build(), TestContext.Current.CancellationToken);
        var frequentCoPerformer = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Frequent co performer {suffix}").Build(), TestContext.Current.CancellationToken);
        var rareCoPerformer = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Rare co performer {suffix}").Build(), TestContext.Current.CancellationToken);
        var unrelated = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Unrelated performer {suffix}").Build(), TestContext.Current.CancellationToken);
        var directOnlyGroup = await owner.CreateGroupAsync($"Direct performer relationship group {suffix}", TestContext.Current.CancellationToken);
        var videoOnlyGroup = await owner.CreateGroupAsync($"Video performer relationship group {suffix}", TestContext.Current.CancellationToken);
        var dualPathGroup = await owner.CreateGroupAsync($"Dual performer relationship group {suffix}", TestContext.Current.CancellationToken);
        await owner.AddPerformerToGroupAsync(focal, directOnlyGroup, TestContext.Current.CancellationToken);
        await owner.AddPerformerToGroupAsync(focal, dualPathGroup, TestContext.Current.CancellationToken);
        await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"First relationship video {suffix}")
            .WithPerformers([focal, frequentCoPerformer, rareCoPerformer])
            .WithGroup(videoOnlyGroup)
            .Build(), TestContext.Current.CancellationToken);
        await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Second relationship video {suffix}")
            .WithPerformers([focal, frequentCoPerformer])
            .WithGroup(dualPathGroup)
            .Build(), TestContext.Current.CancellationToken);

        var groups = await Task.WhenAll(Enumerable.Range(1, 3).Select(page => AsUser(ApiTestUsers.Eva).GetPerformerGroupsAsync(focal.Id, page, perPage: 1)));
        var appearsWith = await Task.WhenAll(Enumerable.Range(1, 2).Select(page => AsUser(ApiTestUsers.Eva).GetPerformerAppearsWithAsync(focal.Id, page, perPage: 1)));
        var reverse = await Task.WhenAll(Enumerable.Range(1, 2).Select(page => AsUser(ApiTestUsers.Eva).GetPerformerAppearsWithAsync(frequentCoPerformer.Id, page, perPage: 1)));

        groups.Should().AllSatisfy(page =>
        {
            page.TotalCount.Should().Be(3);
            page.PerPage.Should().Be(1);
            page.Items.Should().ContainSingle();
        });
        groups.Select(page => page.Page).Should().Equal(1, 2, 3);
        var groupIds = groups.SelectMany(page => page.Items).Select(group => group.Id).ToList();
        groupIds.Should().OnlyHaveUniqueItems();
        groupIds.Should().BeEquivalentTo(
            [directOnlyGroup.Id, videoOnlyGroup.Id, dualPathGroup.Id]);
        appearsWith.Should().AllSatisfy(page =>
        {
            page.TotalCount.Should().Be(2);
            page.PerPage.Should().Be(1);
            page.Items.Should().ContainSingle();
        });
        appearsWith.Select(page => page.Page).Should().Equal(1, 2);
        appearsWith[0].Items.Single().Id.Should().Be(frequentCoPerformer.Id);
        appearsWith[1].Items.Single().Id.Should().Be(rareCoPerformer.Id);
        appearsWith.SelectMany(page => page.Items).Should().NotContain(performer => performer.Id == unrelated.Id);
        reverse.Should().AllSatisfy(page =>
        {
            page.TotalCount.Should().Be(2);
            page.PerPage.Should().Be(1);
            page.Items.Should().ContainSingle();
        });
        reverse[0].Items.Single().Id.Should().Be(focal.Id);
        reverse[1].Items.Single().Id.Should().Be(rareCoPerformer.Id);
    }

    [Fact]
    [CoversEndpoint("DELETE", "/api/performers/bulk")]
    public async Task GivenPerformers_WhenOwnerBulkDeletesSelection_ThenMemberCannotDeleteAndControlRemains()
    {
        var owner = AsUser();
        var first = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Bulk delete performer first {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var second = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Bulk delete performer second {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var retained = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Retained bulk delete performer {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var request = new BatchDeleteDto([first.Id, int.MaxValue, second.Id]);
        var forbidden = () => AsUser(ApiTestUsers.Eva).BulkDeletePerformersAsync(request);

        await forbidden.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await owner.GetPerformerByIdAsync(first.Id, TestContext.Current.CancellationToken)).Id.Should().Be(first.Id);
        (await owner.GetPerformerByIdAsync(second.Id, TestContext.Current.CancellationToken)).Id.Should().Be(second.Id);
        var queued = await owner.BulkDeletePerformersAsync(request, TestContext.Current.CancellationToken);
        queued.ItemCount.Should().Be(3);
        AssertCompletedBulkDeletion(
            await owner.WaitForTerminalJobAsync(queued.JobId, TestContext.Current.CancellationToken),
            succeeded: 2,
            skipped: 1);

        foreach (var performer in new[] { first, second })
        {
            var missing = () => owner.GetPerformerByIdAsync(performer.Id);
            await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        }
        (await owner.GetPerformerByIdAsync(retained.Id, TestContext.Current.CancellationToken)).Id.Should().Be(retained.Id);
    }
}
