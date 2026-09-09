using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Entities.Tags;

public sealed class TagDiscoveryBulkAndSegmentApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/tags")]
    public async Task GivenTaggedAudioAndText_WhenMemberListsTags_ThenUsageCountsAreSerialized()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var tag = await owner.CreateTagAsync($"Media usage tag {suffix}");
        await owner.CreateAudioAsync(new AudioBuilder()
            .WithTitle($"Tagged audio {suffix}")
            .WithTag(tag)
            .Build());
        await owner.CreateTextAsync(new TextDocumentBuilder()
            .WithTitle($"Tagged text {suffix}")
            .WithTag(tag)
            .Build());

        var tags = await AsUser(ApiTestUsers.Eva).GetTagsAsync();

        var listed = tags.Should().ContainSingle(candidate => candidate.Id == tag.Id).Which;
        listed.AudioCount.Should().Be(1);
        listed.TextCount.Should().Be(1);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/tags/find")]
    public async Task GivenFavoriteTags_WhenMemberFiltersSortsAndPages_ThenOnlyRequestedPageIsReturned()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var first = await owner.CreateTagAsync(new TagBuilder().WithName($"A favorite tag {suffix}").AsFavorite().Build(), TestContext.Current.CancellationToken);
        var second = await owner.CreateTagAsync(new TagBuilder().WithName($"B favorite tag {suffix}").AsFavorite().Build(), TestContext.Current.CancellationToken);
        await owner.CreateTagAsync(new TagBuilder().WithName($"Excluded tag {suffix}").Build(), TestContext.Current.CancellationToken);
        await owner.CreateTagAsync(new TagBuilder().WithName($"Unrelated favorite tag {Guid.NewGuid():N}").AsFavorite().Build(), TestContext.Current.CancellationToken);
        var request = new FilteredQueryRequest<TagFilter>
        {
            ObjectFilter = new TagFilter { FavoriteCriterion = new BoolCriterion { Value = true } },
            FindFilter = new FindFilter { Q = suffix, Page = 2, PerPage = 1, Sort = "name" },
        };

        var result = await AsUser(ApiTestUsers.Eva).FindTagsAsync(request, TestContext.Current.CancellationToken);

        result.TotalCount.Should().Be(2);
        result.Page.Should().Be(2);
        result.PerPage.Should().Be(1);
        var item = result.Items.Should().ContainSingle().Which;
        item.Id.Should().Be(second.Id);
        item.Name.Should().Be(second.Name);
        item.Favorite.Should().BeTrue();
        result.Items.Should().NotContain(tag => tag.Id == first.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/tags/find")]
    public async Task GivenDeepTagHierarchy_WhenMemberIncludesSubTags_ThenEveryDescendantIsReturned()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var greatGrandchild = await owner.CreateTagAsync($"Great-grandchild filter tag {suffix}", TestContext.Current.CancellationToken);
        var grandchild = await owner.CreateTagAsync(new TagBuilder()
            .WithName($"Grandchild filter tag {suffix}")
            .WithChild(greatGrandchild)
            .Build(), TestContext.Current.CancellationToken);
        var child = await owner.CreateTagAsync(new TagBuilder()
            .WithName($"Child filter tag {suffix}")
            .WithChild(grandchild)
            .Build(), TestContext.Current.CancellationToken);
        var parent = await owner.CreateTagAsync(new TagBuilder()
            .WithName($"Parent filter tag {suffix}")
            .WithChild(child)
            .Build(), TestContext.Current.CancellationToken);

        var direct = await AsUser(ApiTestUsers.Eva).FindTagsAsync(new FilteredQueryRequest<TagFilter>
        {
            ObjectFilter = new TagFilter
            {
                ParentsCriterion = new MultiIdCriterion
                {
                    Value = [parent.Id],
                    Modifier = CriterionModifier.Includes,
                },
            },
            FindFilter = new FindFilter { Q = suffix, Page = 1, PerPage = 10, Sort = "name" },
        }, TestContext.Current.CancellationToken);
        var recursive = await AsUser(ApiTestUsers.Eva).FindTagsAsync(new FilteredQueryRequest<TagFilter>
        {
            ObjectFilter = new TagFilter
            {
                ParentsCriterion = new MultiIdCriterion
                {
                    Value = [parent.Id],
                    Modifier = CriterionModifier.Includes,
                    Depth = -1,
                },
            },
            FindFilter = new FindFilter { Q = suffix, Page = 1, PerPage = 10, Sort = "name" },
        }, TestContext.Current.CancellationToken);

        direct.Items.Should().ContainSingle().Which.Id.Should().Be(child.Id);
        recursive.TotalCount.Should().Be(3);
        recursive.Items.Select(tag => tag.Id).Should().BeEquivalentTo([child.Id, grandchild.Id, greatGrandchild.Id]);
        recursive.Items.Should().NotContain(tag => tag.Id == parent.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/tags/graph")]
    public async Task GivenRelatedTags_WhenMemberReadsFilteredGraph_ThenNodesAndLinksAreScopedToMatches()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var child = await owner.CreateTagAsync($"Child graph tag {suffix}", TestContext.Current.CancellationToken);
        var parent = await owner.CreateTagAsync(new TagBuilder()
            .WithName($"Parent graph tag {suffix}")
            .WithChild(child)
            .Build(), TestContext.Current.CancellationToken);
        var excludedChild = await owner.CreateTagAsync($"Excluded graph child {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        await owner.CreateTagAsync(new TagBuilder()
            .WithName($"Excluded graph parent {Guid.NewGuid():N}")
            .WithChild(excludedChild)
            .Build(), TestContext.Current.CancellationToken);

        var graph = await AsUser(ApiTestUsers.Eva).GetTagGraphAsync(new FilteredQueryRequest<TagFilter>
        {
            FindFilter = new FindFilter { Q = suffix, Page = 1, PerPage = 10, Sort = "name" },
        }, TestContext.Current.CancellationToken);

        graph.TotalCount.Should().Be(2);
        graph.Items.Select(tag => tag.Id).Should().Equal(child.Id, parent.Id);
        graph.Items.Select(tag => tag.Name).Should().Equal(child.Name, parent.Name);
        graph.Links.Select(link => (link.SourceId, link.TargetId)).Should().Equal((parent.Id, child.Id));
        graph.Items.Single(tag => tag.Id == parent.Id).ChildIds.Should().Equal(child.Id);
        graph.Items.Single(tag => tag.Id == child.Id).ParentIds.Should().Equal(parent.Id);
    }

    [Fact]
    [CoversEndpoint("GET", "/api/tags/{id:int}/segments")]
    [CoversEndpoint("GET", "/api/tags/segment-titles")]
    public async Task GivenTaggedVideoSegments_WhenMemberReadsTagWallAndTitles_ThenOnlyMatchingPublicSegmentsAppear()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var tag = await owner.CreateTagAsync($"Segment wall tag {suffix}", TestContext.Current.CancellationToken);
        var otherTag = await owner.CreateTagAsync($"Other segment wall tag {suffix}", TestContext.Current.CancellationToken);
        var video = await owner.CreateVideoAsync($"Segment wall video {suffix}", TestContext.Current.CancellationToken);
        var matching = await owner.CreateVideoSegmentAsync(video, new SegmentCreateDto(
            2, 6, tag.Id, "chapter", null, null, "tag-wall", null, 0.8f, $"Matching title {suffix}", null), TestContext.Current.CancellationToken);
        await owner.CreateVideoSegmentAsync(video, new SegmentCreateDto(
            7, 9, otherTag.Id, "chapter", null, null, "tag-wall", null, 0.7f, $"Matching title {suffix}", null), TestContext.Current.CancellationToken);

        var wall = await AsUser(ApiTestUsers.Eva).GetTagSegmentsAsync(tag.Id, TestContext.Current.CancellationToken);
        var titles = await AsUser(ApiTestUsers.Eva).GetTagSegmentTitlesAsync($"MATCHING TITLE {suffix.ToUpperInvariant()}", TestContext.Current.CancellationToken);

        var wallItem = wall.Should().ContainSingle().Which;
        wallItem.Id.Should().Be(matching.Id);
        wallItem.VideoId.Should().Be(video.Id);
        wallItem.VideoTitle.Should().Be(video.Title);
        wallItem.StartSec.Should().Be(2);
        wallItem.EndSec.Should().Be(6);
        wallItem.Title.Should().Be($"Matching title {suffix}");
        wallItem.Kind.Should().Be("chapter");
        wallItem.SourceKey.Should().Be("tag-wall");
        wallItem.Confidence.Should().Be(0.8f);
        titles.Should().Equal($"Matching title {suffix}");
    }

    [Fact]
    [CoversEndpoint("POST", "/api/tags/bulk")]
    public async Task GivenRelatedTags_WhenMemberBulkSetsValues_ThenOnlySelectedTagsAndRatingsChange()
    {
        var owner = AsUser();
        var originalParent = await owner.CreateTagAsync($"Original parent tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var replacementParent = await owner.CreateTagAsync($"Replacement parent tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var originalChild = await owner.CreateTagAsync($"Original child tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var replacementChild = await owner.CreateTagAsync($"Replacement child tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var originalTagGroup = await owner.CreateTagGroupAsync(new TagGroupCreateDto($"Original bulk tag group {Guid.NewGuid():N}"), TestContext.Current.CancellationToken);
        var selected = await Task.WhenAll(Enumerable.Range(1, 2).Select(index => owner.CreateTagAsync(new TagBuilder()
            .WithName($"Selected bulk tag {index} {Guid.NewGuid():N}")
            .WithDescription($"Original description {index}")
            .WithColor("#112233")
            .WithTagGroup(originalTagGroup)
            .WithMinimumOccurrence(3.5, 12.5)
            .WithParent(originalParent)
            .WithChild(originalChild)
            .Build())));
        var control = await owner.CreateTagAsync(new TagBuilder()
            .WithName($"Control bulk tag {Guid.NewGuid():N}")
            .WithDescription("Control description")
            .WithColor("#445566")
            .WithTagGroup(originalTagGroup)
            .WithMinimumOccurrence(3.5, 12.5)
            .WithParent(originalParent)
            .WithChild(originalChild)
            .Build(), TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).SetTagRatingAsync(control, 17, TestContext.Current.CancellationToken);
        var request = new BulkTagUpdateDto
        {
            Ids = selected.Select(tag => tag.Id).ToList(),
            Description = "Updated description",
            Color = "#AABBCC",
            ClearFields = ["tagGroupId"],
            MinOccurrenceSec = 8.5,
            MinOccurrencePercent = 37.5,
            Favorite = true,
            Organized = true,
            Rating = 91,
            ParentIds = [replacementParent.Id],
            ParentMode = BulkUpdateMode.Set,
            ChildIds = [replacementChild.Id],
            ChildMode = BulkUpdateMode.Set,
        };

        var updatedCount = await AsUser(ApiTestUsers.Eva).BulkUpdateTagsAsync(request, TestContext.Current.CancellationToken);
        var updated = await Task.WhenAll(selected.Select(tag => owner.GetTagByIdAsync(tag.Id)));
        var retained = await owner.GetTagByIdAsync(control.Id, TestContext.Current.CancellationToken);
        var engagements = await Task.WhenAll(selected.Select(tag => AsUser(ApiTestUsers.Eva).GetEntityEngagementAsync(AffinityHostType.Tag, tag.Id)));
        var retainedEngagement = await AsUser(ApiTestUsers.Eva).GetEntityEngagementAsync(AffinityHostType.Tag, control.Id, TestContext.Current.CancellationToken);
        var ownerEngagements = await Task.WhenAll(selected.Append(control).Select(tag => owner.GetEntityEngagementAsync(AffinityHostType.Tag, tag.Id)));
        var originalsById = selected.ToDictionary(tag => tag.Id);

        updatedCount.Should().Be(2);
        updated.Should().AllSatisfy(tag =>
        {
            tag.Description.Should().Be("Updated description");
            tag.Color.Should().Be("#AABBCC");
            tag.Favorite.Should().BeTrue();
            tag.Organized.Should().BeTrue();
            tag.Name.Should().Be(originalsById[tag.Id].Name);
            tag.TagGroupId.Should().BeNull();
            tag.TagGroupName.Should().BeNull();
            tag.MinOccurrenceSec.Should().Be(8.5);
            tag.MinOccurrencePercent.Should().Be(37.5);
            tag.Parents.Select(parent => parent.Id).Should().Equal(replacementParent.Id);
            tag.Children.Select(child => child.Id).Should().Equal(replacementChild.Id);
        });
        engagements.Should().AllSatisfy(engagement => engagement.Rating.Should().Be(91));
        ownerEngagements.Should().AllSatisfy(engagement => engagement.Rating.Should().BeNull());
        retained.Description.Should().Be("Control description");
        retained.Color.Should().Be("#445566");
        retained.Favorite.Should().BeFalse();
        retained.Organized.Should().BeFalse();
        retained.TagGroupId.Should().Be(originalTagGroup.Id);
        retained.TagGroupName.Should().Be(originalTagGroup.Name);
        retained.MinOccurrenceSec.Should().Be(3.5);
        retained.MinOccurrencePercent.Should().Be(12.5);
        retained.Parents.Select(parent => parent.Id).Should().Equal(originalParent.Id);
        retained.Children.Select(child => child.Id).Should().Equal(originalChild.Id);
        retainedEngagement.Rating.Should().Be(17);
    }

    [Fact]
    [CoversEndpoint("DELETE", "/api/tags/bulk")]
    public async Task GivenTags_WhenOwnerBulkDeletesSelection_ThenMemberCannotDeleteAndControlRemains()
    {
        var owner = AsUser();
        var first = await owner.CreateTagAsync($"Bulk delete tag first {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var second = await owner.CreateTagAsync($"Bulk delete tag second {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var retained = await owner.CreateTagAsync($"Retained bulk delete tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var request = new BatchDeleteDto([first.Id, int.MaxValue, second.Id]);
        var forbidden = () => AsUser(ApiTestUsers.Eva).BulkDeleteTagsAsync(request);

        await forbidden.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await owner.GetTagByIdAsync(first.Id, TestContext.Current.CancellationToken)).Id.Should().Be(first.Id);
        (await owner.GetTagByIdAsync(second.Id, TestContext.Current.CancellationToken)).Id.Should().Be(second.Id);
        var queued = await owner.BulkDeleteTagsAsync(request, TestContext.Current.CancellationToken);
        queued.ItemCount.Should().Be(3);
        AssertCompletedBulkDeletion(
            await owner.WaitForTerminalJobAsync(queued.JobId, TestContext.Current.CancellationToken),
            succeeded: 2,
            skipped: 1);

        foreach (var tag in new[] { first, second })
        {
            var missing = () => owner.GetTagByIdAsync(tag.Id);
            await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        }
        (await owner.GetTagByIdAsync(retained.Id, TestContext.Current.CancellationToken)).Id.Should().Be(retained.Id);
    }
}
