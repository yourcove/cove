using System.Globalization;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Enums;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Entities.Studios;

public sealed class StudioDiscoveryBulkAndMergeApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/studios/find")]
    public async Task GivenMatchingStudios_WhenMemberFiltersSortsAndPages_ThenOnlyRequestedPageIsReturned()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var second = await owner.CreateStudioAsync(new StudioBuilder().WithName($"B favorite studio {suffix}").AsFavorite().Build(), TestContext.Current.CancellationToken);
        var first = await owner.CreateStudioAsync(new StudioBuilder().WithName($"A favorite studio {suffix}").AsFavorite().Build(), TestContext.Current.CancellationToken);
        await owner.CreateStudioAsync(new StudioBuilder().WithName($"Excluded studio {suffix}").Build(), TestContext.Current.CancellationToken);
        await owner.CreateStudioAsync(new StudioBuilder().WithName($"Unrelated favorite studio {Guid.NewGuid():N}").AsFavorite().Build(), TestContext.Current.CancellationToken);
        var request = new FilteredQueryRequest<StudioFilter>
        {
            ObjectFilter = new StudioFilter { FavoriteCriterion = new BoolCriterion { Value = true } },
            FindFilter = new FindFilter { Q = suffix, Page = 2, PerPage = 1, Sort = "name" },
        };

        var result = await AsUser(ApiTestUsers.Eva).FindStudiosAsync(request, TestContext.Current.CancellationToken);

        result.TotalCount.Should().Be(2);
        result.Page.Should().Be(2);
        result.PerPage.Should().Be(1);
        var item = result.Items.Should().ContainSingle().Which;
        item.Id.Should().Be(second.Id);
        item.Name.Should().Be(second.Name);
        item.Favorite.Should().BeTrue();
        result.Items.Should().NotContain(studio => studio.Id == first.Id);
    }

    [Fact]
    public async Task GivenChildStudioMedia_WhenOwnerReadsAtRecursiveDepth_ThenDescendantUsageCountsAreIncluded()
    {
        var owner = AsUser();
        var root = await owner.CreateStudioAsync(new StudioBuilder().WithName($"Root studio {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var child = await owner.CreateStudioAsync(new StudioBuilder().WithName($"Child studio {Guid.NewGuid():N}").WithParent(root).Build(), TestContext.Current.CancellationToken);
        var performer = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Recursive count performer {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        await owner.CreateVideoAsync(new VideoBuilder().WithTitle($"Recursive count video {Guid.NewGuid():N}").WithStudio(child).WithPerformers([performer]).Build(), TestContext.Current.CancellationToken);
        await owner.CreateImageAsync(new ImageBuilder().WithTitle($"Recursive count image {Guid.NewGuid():N}").WithStudio(child).Build(), TestContext.Current.CancellationToken);
        await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Recursive count gallery {Guid.NewGuid():N}").WithStudio(child).Build(), TestContext.Current.CancellationToken);
        await owner.CreateGroupAsync(new GroupCreateDto($"Recursive count group {Guid.NewGuid():N}", null, null, null, child.Id, null, null, [], []), TestContext.Current.CancellationToken);
        await owner.CreateAudioAsync(new AudioBuilder().WithTitle($"Recursive count audio {Guid.NewGuid():N}").WithStudio(child).Build(), TestContext.Current.CancellationToken);
        await owner.CreateTextAsync(new TextDocumentBuilder().WithTitle($"Recursive count text {Guid.NewGuid():N}").WithStudio(child).Build(), TestContext.Current.CancellationToken);

        var direct = await owner.GetStudioByIdAsync(root.Id, TestContext.Current.CancellationToken);
        var recursive = await owner.GetStudioByIdAtDepthAsync(root.Id, -1, TestContext.Current.CancellationToken);

        direct.VideoCount.Should().Be(0);
        direct.ImageCount.Should().Be(0);
        direct.GalleryCount.Should().Be(0);
        direct.GroupCount.Should().Be(0);
        direct.PerformerCount.Should().Be(0);
        direct.AudioCount.Should().Be(0);
        direct.TextCount.Should().Be(0);
        direct.ChildStudioCount.Should().Be(1);
        recursive.VideoCount.Should().Be(1);
        recursive.ImageCount.Should().Be(1);
        recursive.GalleryCount.Should().Be(1);
        recursive.GroupCount.Should().Be(1);
        recursive.PerformerCount.Should().Be(1);
        recursive.AudioCount.Should().Be(1);
        recursive.TextCount.Should().Be(1);
        recursive.ChildStudioCount.Should().Be(1);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/studios/bulk")]
    public async Task GivenTaggedStudios_WhenMemberBulkMutatesFieldsTagsAndRatings_ThenOnlySelectedStudiosChange()
    {
        var owner = AsUser();
        var originalTag = await owner.CreateTagAsync($"Original bulk studio tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var replacementTag = await owner.CreateTagAsync($"Replacement bulk studio tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var parent = await owner.CreateStudioAsync(new StudioBuilder().WithName($"Bulk parent studio {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var selected = await Task.WhenAll(Enumerable.Range(1, 2).Select(index => owner.CreateStudioAsync(new StudioBuilder()
            .WithName($"Selected bulk studio {index} {Guid.NewGuid():N}")
            .WithParent(parent)
            .WithDetails($"Original details {index}")
            .WithTag(originalTag)
            .Build())));
        var control = await owner.CreateStudioAsync(new StudioBuilder()
            .WithName($"Control bulk studio {Guid.NewGuid():N}")
            .WithDetails("Control details")
            .WithTag(originalTag)
            .WithRating(17)
            .Build(), TestContext.Current.CancellationToken);
        var request = new BulkStudioUpdateDto
        {
            Ids = selected.Select(studio => studio.Id).ToList(),
            ClearFields = ["parentId"],
            Favorite = true,
            Details = "Updated details",
            Organized = true,
            Rating = 91,
            TagIds = [replacementTag.Id],
            TagMode = BulkUpdateMode.Set,
        };

        var updatedCount = await AsUser(ApiTestUsers.Eva).BulkUpdateStudiosAsync(request, TestContext.Current.CancellationToken);
        var updated = await Task.WhenAll(selected.Select(studio => owner.GetStudioByIdAsync(studio.Id)));
        var retained = await owner.GetStudioByIdAsync(control.Id, TestContext.Current.CancellationToken);
        var engagements = await Task.WhenAll(selected.Select(studio => AsUser(ApiTestUsers.Eva).GetEntityEngagementAsync(AffinityHostType.Studio, studio.Id)));
        var retainedEngagement = await AsUser(ApiTestUsers.Eva).GetEntityEngagementAsync(AffinityHostType.Studio, control.Id, TestContext.Current.CancellationToken);
        var ownerEngagements = await Task.WhenAll(selected.Select(studio => owner.GetEntityEngagementAsync(AffinityHostType.Studio, studio.Id)));
        var ownerRetainedEngagement = await owner.GetEntityEngagementAsync(AffinityHostType.Studio, control.Id, TestContext.Current.CancellationToken);

        updatedCount.Should().Be(2);
        updated.Should().AllSatisfy(studio =>
        {
            studio.ParentId.Should().BeNull();
            studio.Favorite.Should().BeTrue();
            studio.Details.Should().Be("Updated details");
            studio.Organized.Should().BeTrue();
            studio.Tags.Select(tag => tag.Id).Should().Equal(replacementTag.Id);
        });
        engagements.Should().AllSatisfy(engagement => engagement.Rating.Should().Be(91));
        ownerEngagements.Should().AllSatisfy(engagement => engagement.Rating.Should().BeNull());
        retained.ParentId.Should().BeNull();
        retained.Favorite.Should().BeFalse();
        retained.Details.Should().Be("Control details");
        retained.Organized.Should().BeFalse();
        retained.Tags.Select(tag => tag.Id).Should().Equal(originalTag.Id);
        retainedEngagement.Rating.Should().BeNull();
        ownerRetainedEngagement.Rating.Should().Be(17);

        var clearedCount = await AsUser(ApiTestUsers.Eva).BulkUpdateStudiosAsync(new BulkStudioUpdateDto
        {
            Ids = selected.Select(studio => studio.Id).ToList(),
            ClearFields = ["details"],
        }, TestContext.Current.CancellationToken);
        var controlAfterClear = await owner.GetStudioByIdAsync(control.Id, TestContext.Current.CancellationToken);
        var addedCount = await AsUser(ApiTestUsers.Eva).BulkUpdateStudiosAsync(new BulkStudioUpdateDto
        {
            Ids = selected.Select(studio => studio.Id).ToList(),
            TagIds = [originalTag.Id],
            TagMode = BulkUpdateMode.Add,
        }, TestContext.Current.CancellationToken);
        var afterAdd = await Task.WhenAll(selected.Select(studio => owner.GetStudioByIdAsync(studio.Id)));
        var controlAfterAdd = await owner.GetStudioByIdAsync(control.Id, TestContext.Current.CancellationToken);
        var removedCount = await AsUser(ApiTestUsers.Eva).BulkUpdateStudiosAsync(new BulkStudioUpdateDto
        {
            Ids = selected.Select(studio => studio.Id).ToList(),
            TagIds = [replacementTag.Id],
            TagMode = BulkUpdateMode.Remove,
        }, TestContext.Current.CancellationToken);
        var afterRemove = await Task.WhenAll(selected.Select(studio => owner.GetStudioByIdAsync(studio.Id)));
        var controlAfterRemove = await owner.GetStudioByIdAsync(control.Id, TestContext.Current.CancellationToken);
        var finalOwnerEngagements = await Task.WhenAll(selected.Select(studio => owner.GetEntityEngagementAsync(AffinityHostType.Studio, studio.Id)));
        var finalMemberEngagements = await Task.WhenAll(selected.Select(studio => AsUser(ApiTestUsers.Eva).GetEntityEngagementAsync(AffinityHostType.Studio, studio.Id)));
        var finalOwnerControlEngagement = await owner.GetEntityEngagementAsync(AffinityHostType.Studio, control.Id, TestContext.Current.CancellationToken);

        clearedCount.Should().Be(2);
        addedCount.Should().Be(2);
        removedCount.Should().Be(2);
        afterAdd.Should().AllSatisfy(studio =>
        {
            studio.Details.Should().BeNull();
            studio.Tags.Select(tag => tag.Id).Should().BeEquivalentTo([originalTag.Id, replacementTag.Id]);
        });
        afterRemove.Should().AllSatisfy(studio => studio.Tags.Select(tag => tag.Id).Should().Equal(originalTag.Id));
        foreach (var controlState in new[] { controlAfterClear, controlAfterAdd, controlAfterRemove })
        {
            controlState.Details.Should().Be("Control details");
            controlState.Tags.Select(tag => tag.Id).Should().Equal(originalTag.Id);
        }
        finalOwnerEngagements.Should().AllSatisfy(engagement => engagement.Rating.Should().BeNull());
        finalMemberEngagements.Should().AllSatisfy(engagement => engagement.Rating.Should().Be(91));
        finalOwnerControlEngagement.Rating.Should().Be(17);
    }

    [Fact]
    public async Task GivenStudioWriteOverride_WhenMemberBulkUpdatesMixedScope_ThenEntireRequestIsForbidden()
    {
        var owner = AsUser();
        var memberRole = (await owner.GetRolesAsync(TestContext.Current.CancellationToken)).Should().ContainSingle(role => role.Name == BuiltinRoles.Member).Which;
        var allowed = await owner.CreateStudioAsync(new StudioBuilder()
            .WithName($"Allowed bulk studio {Guid.NewGuid():N}")
            .WithDetails("Allowed original details")
            .Build(), TestContext.Current.CancellationToken);
        var denied = await owner.CreateStudioAsync(new StudioBuilder()
            .WithName($"Denied bulk studio {Guid.NewGuid():N}")
            .WithDetails("Denied original details")
            .Build(), TestContext.Current.CancellationToken);
        var entityOverride = await owner.CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            memberRole.Id,
            EntityKinds.Studio,
            denied.Id.ToString(CultureInfo.InvariantCulture),
            "deny",
            "write"), TestContext.Current.CancellationToken);
        var mixedRequest = new BulkStudioUpdateDto
        {
            Ids = [allowed.Id, denied.Id],
            Details = "Mixed request must not persist",
        };
        var forbidden = () => AsUser(ApiTestUsers.Eva).BulkUpdateStudiosAsync(mixedRequest);

        entityOverride.RoleId.Should().Be(memberRole.Id);
        entityOverride.EntityKind.Should().Be(EntityKinds.Studio);
        entityOverride.EntityId.Should().Be(denied.Id.ToString(CultureInfo.InvariantCulture));
        entityOverride.Effect.Should().Be("deny");
        entityOverride.AppliesTo.Should().Be("write");
        await forbidden.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await owner.GetStudioByIdAsync(allowed.Id, TestContext.Current.CancellationToken)).Details.Should().Be("Allowed original details");
        (await owner.GetStudioByIdAsync(denied.Id, TestContext.Current.CancellationToken)).Details.Should().Be("Denied original details");

        var updatedCount = await AsUser(ApiTestUsers.Eva).BulkUpdateStudiosAsync(new BulkStudioUpdateDto
        {
            Ids = [allowed.Id],
            Details = "Allowed updated details",
        }, TestContext.Current.CancellationToken);

        updatedCount.Should().Be(1);
        (await owner.GetStudioByIdAsync(allowed.Id, TestContext.Current.CancellationToken)).Details.Should().Be("Allowed updated details");
        (await owner.GetStudioByIdAsync(denied.Id, TestContext.Current.CancellationToken)).Details.Should().Be("Denied original details");
    }

    [Fact]
    [CoversEndpoint("DELETE", "/api/studios/bulk")]
    public async Task GivenStudios_WhenOwnerBulkDeletesNormalizedSelection_ThenMemberCannotDeleteAndControlRemains()
    {
        var owner = AsUser();
        var first = await owner.CreateStudioAsync(new StudioBuilder().WithName($"Bulk delete studio first {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var second = await owner.CreateStudioAsync(new StudioBuilder().WithName($"Bulk delete studio second {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var retained = await owner.CreateStudioAsync(new StudioBuilder().WithName($"Retained bulk studio {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var request = new BatchDeleteDto([first.Id, 0, first.Id, int.MaxValue, second.Id]);
        var forbidden = () => AsUser(ApiTestUsers.Eva).BulkDeleteStudiosAsync(request);

        await forbidden.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await owner.GetStudioByIdAsync(first.Id, TestContext.Current.CancellationToken)).Id.Should().Be(first.Id);
        (await owner.GetStudioByIdAsync(second.Id, TestContext.Current.CancellationToken)).Id.Should().Be(second.Id);
        var deleted = await owner.BulkDeleteStudiosAsync(request, TestContext.Current.CancellationToken);

        deleted.Should().Be(2);
        foreach (var studio in new[] { first, second })
        {
            var missing = () => owner.GetStudioByIdAsync(studio.Id);
            await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        }
        (await owner.GetStudioByIdAsync(retained.Id, TestContext.Current.CancellationToken)).Id.Should().Be(retained.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/studios/merge")]
    public async Task GivenStudioSources_WhenOwnerMergesThem_ThenRelationshipsAndDistinctMetadataMoveToTheTarget()
    {
        var owner = AsUser();
        var targetTag = await owner.CreateTagAsync($"Target merge studio tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var sourceTag = await owner.CreateTagAsync($"Source merge studio tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var sharedTag = await owner.CreateTagAsync($"Shared merge studio tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var sharedUrl = $"https://studio-merge.example/shared/{Guid.NewGuid():N}";
        var target = await owner.CreateStudioAsync(new StudioBuilder()
            .WithName($"Merge target studio {Guid.NewGuid():N}")
            .WithAlias("Target studio alias")
            .WithUrl(sharedUrl)
            .WithTag(targetTag)
            .WithTag(sharedTag)
            .WithRemoteId("https://metadata.example/graphql", "shared-remote-id")
            .Build(), TestContext.Current.CancellationToken);
        var source = await owner.CreateStudioAsync(new StudioBuilder()
            .WithName($"Merge source studio {Guid.NewGuid():N}")
            .WithAlias("Source studio alias")
            .WithDetails("Source details move to target")
            .WithUrl(sharedUrl.ToUpperInvariant())
            .WithUrl($"https://studio-merge.example/source/{Guid.NewGuid():N}")
            .WithTag(sourceTag)
            .WithTag(sharedTag)
            .WithRemoteId("https://metadata.example/graphql", "shared-remote-id")
            .WithRemoteId("https://metadata.example/graphql", "source-remote-id")
            .AsFavorite()
            .AsOrganized()
            .Build(), TestContext.Current.CancellationToken);
        var control = await owner.CreateStudioAsync(new StudioBuilder()
            .WithName($"Merge control studio {Guid.NewGuid():N}")
            .WithAlias("Control studio alias")
            .WithDetails("Control details")
            .WithTag(sharedTag)
            .Build(), TestContext.Current.CancellationToken);
        var child = await owner.CreateStudioAsync(new StudioBuilder().WithName($"Merge child studio {Guid.NewGuid():N}").WithParent(source).Build(), TestContext.Current.CancellationToken);
        var performer = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Merge studio performer {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var video = await owner.CreateVideoAsync(new VideoBuilder().WithTitle($"Merge studio video {Guid.NewGuid():N}").WithStudio(source).WithPerformers([performer]).Build(), TestContext.Current.CancellationToken);
        var image = await owner.CreateImageAsync(new ImageBuilder().WithTitle($"Merge studio image {Guid.NewGuid():N}").WithStudio(source).Build(), TestContext.Current.CancellationToken);
        var gallery = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Merge studio gallery {Guid.NewGuid():N}").WithStudio(source).Build(), TestContext.Current.CancellationToken);
        var group = await owner.CreateGroupAsync(new GroupCreateDto($"Merge studio group {Guid.NewGuid():N}", null, null, null, source.Id, null, null, [], []), TestContext.Current.CancellationToken);
        var audio = await owner.CreateAudioAsync(new AudioBuilder().WithTitle($"Merge studio audio {Guid.NewGuid():N}").WithStudio(source).Build(), TestContext.Current.CancellationToken);
        var text = await owner.CreateTextAsync(new TextDocumentBuilder().WithTitle($"Merge studio text {Guid.NewGuid():N}").WithStudio(source).Build(), TestContext.Current.CancellationToken);

        var forbidden = () => AsUser(ApiTestUsers.Eva).MergeStudiosAsync(target, [source]);
        await forbidden.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await owner.GetStudioByIdAsync(source.Id, TestContext.Current.CancellationToken)).Id.Should().Be(source.Id);
        (await owner.GetVideoByIdAsync(video.Id, TestContext.Current.CancellationToken)).StudioId.Should().Be(source.Id);

        var merged = await owner.MergeStudiosAsync(target, [source], TestContext.Current.CancellationToken);
        var persisted = await owner.GetStudioByIdAsync(target.Id, TestContext.Current.CancellationToken);
        var controlAfter = await owner.GetStudioByIdAsync(control.Id, TestContext.Current.CancellationToken);

        merged.Id.Should().Be(target.Id);
        persisted.Details.Should().Be("Source details move to target");
        persisted.Favorite.Should().BeTrue();
        persisted.Organized.Should().BeTrue();
        persisted.PerformerCount.Should().Be(1);
        persisted.Aliases.Should().BeEquivalentTo(["Target studio alias", "Source studio alias", source.Name]);
        persisted.Urls.Should().ContainSingle(url => string.Equals(url, sharedUrl, StringComparison.OrdinalIgnoreCase));
        persisted.Urls.Should().Contain(url => url.Contains("/source/", StringComparison.Ordinal));
        persisted.RemoteIds.Should().BeEquivalentTo([
            new StudioRemoteIdDto("https://metadata.example/graphql", "shared-remote-id"),
            new StudioRemoteIdDto("https://metadata.example/graphql", "source-remote-id"),
        ]);
        persisted.Tags.Select(tag => tag.Id).Should().BeEquivalentTo([targetTag.Id, sourceTag.Id, sharedTag.Id]);
        (await owner.GetStudioByIdAsync(child.Id, TestContext.Current.CancellationToken)).ParentId.Should().Be(target.Id);
        var videoAfter = await owner.GetVideoByIdAsync(video.Id, TestContext.Current.CancellationToken);
        videoAfter.StudioId.Should().Be(target.Id);
        videoAfter.Performers.Select(item => item.Id).Should().Equal(performer.Id);
        (await owner.GetImageByIdAsync(image.Id, TestContext.Current.CancellationToken)).StudioId.Should().Be(target.Id);
        (await owner.GetGalleryByIdAsync(gallery.Id, TestContext.Current.CancellationToken)).StudioId.Should().Be(target.Id);
        (await owner.GetGroupByIdAsync(group.Id, TestContext.Current.CancellationToken)).StudioId.Should().Be(target.Id);
        (await owner.GetAudioByIdAsync(audio.Id, TestContext.Current.CancellationToken)).StudioId.Should().Be(target.Id);
        (await owner.GetTextByIdAsync(text.Id, TestContext.Current.CancellationToken)).StudioId.Should().Be(target.Id);
        var sourceMissing = () => owner.GetStudioByIdAsync(source.Id);
        await sourceMissing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        controlAfter.Details.Should().Be("Control details");
        controlAfter.Aliases.Should().Equal("Control studio alias");
        controlAfter.Tags.Select(tag => tag.Id).Should().Equal(sharedTag.Id);
    }
}
