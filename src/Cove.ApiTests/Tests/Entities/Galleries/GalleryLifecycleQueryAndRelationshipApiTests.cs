using System.Text.Json;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Entities.Galleries;

public sealed class GalleryLifecycleQueryAndRelationshipApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/galleries/{id:int}/chapters")]
    [CoversEndpoint("POST", "/api/galleries/{id:int}/chapters")]
    [CoversEndpoint("PUT", "/api/galleries/{galleryid:int}/chapters/{chapterid:int}")]
    [CoversEndpoint("DELETE", "/api/galleries/{galleryid:int}/chapters/{chapterid:int}")]
    public async Task GivenGalleryChapters_WhenOwnerMaintainsOrderedChapters_ThenViewerMutationsAreDeniedAndOtherGalleryRemainsUnchanged()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var gallery = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Chapter gallery {suffix}").Build(), TestContext.Current.CancellationToken);
        var controlGallery = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Control chapter gallery {suffix}").Build(), TestContext.Current.CancellationToken);
        var viewerUsername = $"gallery-chapter-viewer-{suffix}";
        const string viewerPassword = "Gallery chapter viewer 123!";
        await owner.CreateUserAsync(new CreateUserRequest(viewerUsername, viewerPassword, Roles: [BuiltinRoles.Viewer]), TestContext.Current.CancellationToken);
        using var viewerSession = await owner.CreateAuthSessionAsync(viewerUsername, viewerPassword, TestContext.Current.CancellationToken);
        var viewer = viewerSession.Client;

        var later = await owner.CreateGalleryChapterAsync(gallery, new GalleryChapterCreateDto("Later chapter", 30), TestContext.Current.CancellationToken);
        var earlier = await owner.CreateGalleryChapterAsync(gallery, new GalleryChapterCreateDto("Earlier chapter", 10), TestContext.Current.CancellationToken);
        var control = await owner.CreateGalleryChapterAsync(controlGallery, new GalleryChapterCreateDto("Control chapter", 20), TestContext.Current.CancellationToken);
        var initial = await owner.GetGalleryChaptersAsync(gallery, TestContext.Current.CancellationToken);
        var initialControl = await owner.GetGalleryChaptersAsync(controlGallery, TestContext.Current.CancellationToken);

        initial.Should().HaveCount(2);
        AssertChapter(initial[0], earlier, "Earlier chapter", 10, gallery.Id);
        AssertChapter(initial[1], later, "Later chapter", 30, gallery.Id);
        (await viewer.GetGalleryChaptersAsync(gallery, TestContext.Current.CancellationToken)).Select(chapter => chapter.Id).Should().Equal(initial.Select(chapter => chapter.Id));

        var forbiddenMutations = new Func<Task>[]
        {
            async () => _ = await viewer.CreateGalleryChapterAsync(gallery, new GalleryChapterCreateDto("Forbidden chapter", 5)),
            async () => _ = await viewer.UpdateGalleryChapterAsync(gallery, earlier, new GalleryChapterUpdateDto("Forbidden update", 1)),
            () => viewer.DeleteGalleryChapterAsync(gallery, earlier),
        };
        foreach (var forbiddenMutation in forbiddenMutations)
        {
            await forbiddenMutation.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
            AssertChapters(await owner.GetGalleryChaptersAsync(gallery, TestContext.Current.CancellationToken), initial);
        }

        var mismatchedUpdate = () => owner.UpdateGalleryChapterAsync(controlGallery, earlier, new GalleryChapterUpdateDto("Mismatched update", 1));
        var mismatchedDelete = () => owner.DeleteGalleryChapterAsync(controlGallery, earlier);
        await mismatchedUpdate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        AssertChapters(await owner.GetGalleryChaptersAsync(gallery, TestContext.Current.CancellationToken), initial);
        AssertChapters(await owner.GetGalleryChaptersAsync(controlGallery, TestContext.Current.CancellationToken), initialControl);
        await mismatchedDelete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        AssertChapters(await owner.GetGalleryChaptersAsync(gallery, TestContext.Current.CancellationToken), initial);
        AssertChapters(await owner.GetGalleryChaptersAsync(controlGallery, TestContext.Current.CancellationToken), initialControl);

        var updated = await owner.UpdateGalleryChapterAsync(gallery, later, new GalleryChapterUpdateDto("Updated first chapter", 2), TestContext.Current.CancellationToken);
        AssertChapter(updated, later, "Updated first chapter", 2, gallery.Id);
        updated.CreatedAt.Should().NotBeNullOrWhiteSpace();
        updated.UpdatedAt.Should().NotBeNullOrWhiteSpace();
        var afterUpdate = await owner.GetGalleryChaptersAsync(gallery, TestContext.Current.CancellationToken);
        afterUpdate.Should().HaveCount(2);
        AssertChapter(afterUpdate[0], updated, "Updated first chapter", 2, gallery.Id);
        AssertChapter(afterUpdate[1], earlier, "Earlier chapter", 10, gallery.Id);

        await owner.DeleteGalleryChapterAsync(gallery, earlier, TestContext.Current.CancellationToken);
        var afterDelete = await owner.GetGalleryChaptersAsync(gallery, TestContext.Current.CancellationToken);
        afterDelete.Should().ContainSingle();
        AssertChapter(afterDelete.Single(), updated, "Updated first chapter", 2, gallery.Id);
        var controlChapters = await owner.GetGalleryChaptersAsync(controlGallery, TestContext.Current.CancellationToken);
        controlChapters.Should().ContainSingle();
        AssertChapter(controlChapters.Single(), control, "Control chapter", 20, controlGallery.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/galleries")]
    [CoversEndpoint("GET", "/api/galleries/{id:int}")]
    public async Task GivenGalleryMetadata_WhenOwnerCreatesAndMemberReadsIt_ThenRelationshipsRoundTrip()
    {
        const string customFieldKey = "gallery_lighting";
        var owner = AsUser();
        var studio = await owner.CreateStudioAsync($"Gallery studio {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var tag = await owner.CreateTagAsync($"Gallery tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var performer = await owner.CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Gallery performer {Guid.NewGuid():N}")
            .Build(), TestContext.Current.CancellationToken);
        var video = await owner.CreateVideoAsync($"Gallery video {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        await owner.CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = customFieldKey,
            Label = "Gallery lighting",
            Type = "text",
            EntityTypes = ["gallery"],
        }, TestContext.Current.CancellationToken);
        var request = new GalleryBuilder()
            .WithTitle($"Gallery lifecycle {Guid.NewGuid():N}")
            .WithCode("GALLERY-CODE")
            .WithDate("2026-08-10")
            .WithDetails("Gallery lifecycle details")
            .WithPhotographer("Gallery photographer")
            .WithRating(84)
            .WithStudio(studio)
            .WithUrl("https://gallery.example/item")
            .WithTag(tag)
            .WithPerformer(performer)
            .WithVideo(video)
            .WithCustomField(customFieldKey, "Warm key light")
            .AsOrganized()
            .Build();

        var created = await owner.CreateGalleryAsync(request, TestContext.Current.CancellationToken);
        var retrieved = await AsUser(ApiTestUsers.Eva).GetGalleryByIdAsync(created.Id, TestContext.Current.CancellationToken);
        var engagement = await owner.GetEntityEngagementAsync(AffinityHostType.Gallery, created.Id, TestContext.Current.CancellationToken);
        var memberEngagement = await AsUser(ApiTestUsers.Eva).GetEntityEngagementAsync(AffinityHostType.Gallery, created.Id, TestContext.Current.CancellationToken);

        retrieved.Title.Should().Be(request.Title);
        retrieved.Code.Should().Be(request.Code);
        retrieved.Date.Should().Be(request.Date);
        retrieved.Details.Should().Be(request.Details);
        retrieved.Photographer.Should().Be(request.Photographer);
        retrieved.Organized.Should().BeTrue();
        retrieved.StudioId.Should().Be(studio.Id);
        retrieved.StudioName.Should().Be(studio.Name);
        retrieved.Urls.Should().Equal(request.Urls!);
        retrieved.Tags.Select(candidate => candidate.Id).Should().Equal(tag.Id);
        retrieved.Performers.Select(candidate => candidate.Id).Should().Equal(performer.Id);
        retrieved.VideoIds.Should().Equal(video.Id);
        retrieved.CustomFields.Should().ContainKey(customFieldKey)
            .WhoseValue.Should().BeOfType<JsonElement>()
            .Which.GetString().Should().Be("Warm key light");
        engagement.Rating.Should().Be(request.Rating);
        memberEngagement.Rating.Should().BeNull();
    }

    [Fact]
    [CoversEndpoint("PUT", "/api/galleries/{id:int}")]
    public async Task GivenGalleryMetadata_WhenMemberPartiallyUpdatesIt_ThenResponseAndReadPreserveRelationships()
    {
        var owner = AsUser();
        var studio = await owner.CreateStudioAsync($"Original gallery studio {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var tag = await owner.CreateTagAsync($"Original gallery tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var performer = await owner.CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Original gallery performer {Guid.NewGuid():N}")
            .Build(), TestContext.Current.CancellationToken);
        var video = await owner.CreateVideoAsync($"Original gallery video {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var gallery = await owner.CreateGalleryAsync(new GalleryBuilder()
            .WithTitle($"Original gallery {Guid.NewGuid():N}")
            .WithCode("ORIGINAL-CODE")
            .WithDate("2026-08-11")
            .WithDetails("Original details")
            .WithStudio(studio)
            .WithUrl("https://gallery.example/original")
            .WithTag(tag)
            .WithPerformer(performer)
            .WithVideo(video)
            .Build(), TestContext.Current.CancellationToken);

        var updated = await AsUser(ApiTestUsers.Eva).UpdateGalleryAsync(gallery.Id, new
        {
            title = "Updated gallery title",
            details = "Updated details",
            urls = new[] { "https://gallery.example/updated" },
            clearFields = new[] { "studioId" },
        }, TestContext.Current.CancellationToken);
        var retrieved = await owner.GetGalleryByIdAsync(gallery.Id, TestContext.Current.CancellationToken);

        foreach (var actual in new[] { updated, retrieved })
        {
            actual.Title.Should().Be("Updated gallery title");
            actual.Details.Should().Be("Updated details");
            actual.Urls.Should().Equal("https://gallery.example/updated");
            actual.Code.Should().Be("ORIGINAL-CODE");
            actual.Date.Should().Be("2026-08-11");
            actual.Organized.Should().BeFalse();
            actual.StudioId.Should().BeNull();
            actual.Tags.Select(candidate => candidate.Id).Should().Equal(tag.Id);
            actual.Performers.Select(candidate => candidate.Id).Should().Equal(performer.Id);
            actual.VideoIds.Should().Equal(video.Id);
        }
    }

    [Fact]
    [CoversEndpoint("POST", "/api/galleries/find")]
    public async Task GivenMatchingGalleries_WhenFilteredAndPaged_ThenOnlyTheRequestedPageIsReturned()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var owner = AsUser();
        var first = await owner.CreateGalleryAsync(new GalleryBuilder()
            .WithTitle($"A filtered gallery {suffix}")
            .AsOrganized()
            .Build(), TestContext.Current.CancellationToken);
        var second = await owner.CreateGalleryAsync(new GalleryBuilder()
            .WithTitle($"B filtered gallery {suffix}")
            .AsOrganized()
            .Build(), TestContext.Current.CancellationToken);
        await owner.CreateGalleryAsync(new GalleryBuilder()
            .WithTitle($"Excluded gallery {suffix}")
            .Build(), TestContext.Current.CancellationToken);
        var request = new FilteredQueryRequest<GalleryFilter>
        {
            ObjectFilter = new GalleryFilter
            {
                OrganizedCriterion = new BoolCriterion { Value = true },
            },
            FindFilter = new FindFilter
            {
                Q = suffix,
                Page = 2,
                PerPage = 1,
                Sort = "title",
            },
        };

        var result = await AsUser(ApiTestUsers.Eva).FindGalleriesAsync(request, TestContext.Current.CancellationToken);

        result.TotalCount.Should().Be(2);
        result.Page.Should().Be(2);
        result.PerPage.Should().Be(1);
        result.Items.Should().ContainSingle().Which.Id.Should().Be(second.Id);
        result.Items.Should().NotContain(candidate => candidate.Id == first.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/galleries/aggregate")]
    public async Task GivenSelectedGalleries_WhenAggregated_ThenNonzeroFileTotalsAreScopedToSelection()
    {
        var owner = AsUser();
        var first = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Aggregate gallery first {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var second = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Aggregate gallery second {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var excluded = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Aggregate gallery excluded {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        await AsDbUser().AttachGalleryFileAsync(first.Id, size: 1_000, cancellationToken: TestContext.Current.CancellationToken);
        await AsDbUser().AttachGalleryFileAsync(second.Id, size: 2_500, cancellationToken: TestContext.Current.CancellationToken);
        await AsDbUser().AttachGalleryFileAsync(excluded.Id, size: 9_999, cancellationToken: TestContext.Current.CancellationToken);
        var request = new FilteredQueryRequest<GalleryFilter> { Ids = [first.Id, second.Id] };

        var aggregate = await AsUser(ApiTestUsers.Eva).AggregateGalleriesAsync(request, TestContext.Current.CancellationToken);

        aggregate.Should().Be(new GalleryAggregate(Count: 2, FileSize: 3_500));
    }

    [Fact]
    [CoversEndpoint("POST", "/api/galleries/bulk")]
    public async Task GivenGalleriesWithRelationships_WhenMemberBulkSetsValues_ThenOnlySelectedGalleriesChange()
    {
        var owner = AsUser();
        var originalStudio = await owner.CreateStudioAsync($"Original bulk gallery studio {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var originalTag = await owner.CreateTagAsync($"Original bulk gallery tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var originalPerformer = await owner.CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Original bulk gallery performer {Guid.NewGuid():N}")
            .Build(), TestContext.Current.CancellationToken);
        var replacementTag = await owner.CreateTagAsync($"Replacement bulk gallery tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var replacementPerformer = await owner.CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Replacement bulk gallery performer {Guid.NewGuid():N}")
            .Build(), TestContext.Current.CancellationToken);
        var selected = await Task.WhenAll(Enumerable.Range(1, 2).Select(index => owner.CreateGalleryAsync(new GalleryBuilder()
            .WithTitle($"Selected bulk gallery {index} {Guid.NewGuid():N}")
            .WithCode($"ORIGINAL-{index}")
            .WithDate("2026-08-12")
            .WithDetails($"Original details {index}")
            .WithPhotographer($"Original photographer {index}")
            .WithStudio(originalStudio)
            .WithTag(originalTag)
            .WithPerformer(originalPerformer)
            .Build())));
        var control = await owner.CreateGalleryAsync(new GalleryBuilder()
            .WithTitle($"Unselected bulk gallery {Guid.NewGuid():N}")
            .WithCode("CONTROL-CODE")
            .WithDate("2026-08-13")
            .WithDetails("Control details")
            .WithPhotographer("Control photographer")
            .WithStudio(originalStudio)
            .WithTag(originalTag)
            .WithPerformer(originalPerformer)
            .Build(), TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).SetGalleryRatingAsync(control, 17, TestContext.Current.CancellationToken);
        var request = new BulkGalleryUpdateDto
        {
            Ids = selected.Select(gallery => gallery.Id).ToList(),
            ClearFields = ["studioId", "date", "code", "details", "photographer"],
            Organized = true,
            Rating = 91,
            TagIds = [replacementTag.Id],
            TagMode = BulkUpdateMode.Set,
            PerformerIds = [replacementPerformer.Id],
            PerformerMode = BulkUpdateMode.Set,
        };

        var updatedCount = await AsUser(ApiTestUsers.Eva).BulkUpdateGalleriesAsync(request, TestContext.Current.CancellationToken);
        var updated = await Task.WhenAll(selected.Select(gallery => owner.GetGalleryByIdAsync(gallery.Id)));
        var retained = await owner.GetGalleryByIdAsync(control.Id, TestContext.Current.CancellationToken);
        var engagements = await Task.WhenAll(selected.Select(gallery => AsUser(ApiTestUsers.Eva).GetEntityEngagementAsync(AffinityHostType.Gallery, gallery.Id)));
        var retainedEngagement = await AsUser(ApiTestUsers.Eva).GetEntityEngagementAsync(AffinityHostType.Gallery, control.Id, TestContext.Current.CancellationToken);

        updatedCount.Should().Be(2);
        updated.Should().AllSatisfy(gallery =>
        {
            gallery.Code.Should().BeNull();
            gallery.Date.Should().BeNull();
            gallery.Details.Should().BeNull();
            gallery.Photographer.Should().BeNull();
            gallery.Organized.Should().BeTrue();
            gallery.StudioId.Should().BeNull();
            gallery.Tags.Select(candidate => candidate.Id).Should().Equal(replacementTag.Id);
            gallery.Performers.Select(candidate => candidate.Id).Should().Equal(replacementPerformer.Id);
        });
        engagements.Should().AllSatisfy(engagement => engagement.Rating.Should().Be(91));
        retained.Code.Should().Be("CONTROL-CODE");
        retained.Date.Should().Be("2026-08-13");
        retained.Details.Should().Be("Control details");
        retained.Photographer.Should().Be("Control photographer");
        retained.Organized.Should().BeFalse();
        retained.StudioId.Should().Be(originalStudio.Id);
        retained.Tags.Select(candidate => candidate.Id).Should().Equal(originalTag.Id);
        retained.Performers.Select(candidate => candidate.Id).Should().Equal(originalPerformer.Id);
        retainedEngagement.Rating.Should().Be(17);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/galleries/{id:int}/images")]
    [CoversEndpoint("DELETE", "/api/galleries/{id:int}/images")]
    public async Task GivenGalleryAndImages_WhenMemberChangesMembership_ThenCountsAndBothDirectionsStayInSync()
    {
        var owner = AsUser();
        var gallery = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Image membership gallery {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var first = await owner.CreateImageAsync($"First gallery image {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var second = await owner.CreateImageAsync($"Second gallery image {Guid.NewGuid():N}", TestContext.Current.CancellationToken);

        var added = await AsUser(ApiTestUsers.Eva).AddGalleryImagesAsync(gallery, [first, second, first], TestContext.Current.CancellationToken);
        var afterAdd = await owner.GetGalleryByIdAsync(gallery.Id, TestContext.Current.CancellationToken);
        var firstAfterAdd = await owner.GetImageByIdAsync(first.Id, TestContext.Current.CancellationToken);
        var secondAfterAdd = await owner.GetImageByIdAsync(second.Id, TestContext.Current.CancellationToken);

        added.Should().Be(2);
        afterAdd.ImageCount.Should().Be(2);
        firstAfterAdd.GalleryIds.Should().Contain(gallery.Id);
        secondAfterAdd.GalleryIds.Should().Contain(gallery.Id);

        var removed = await AsUser(ApiTestUsers.Eva).RemoveGalleryImagesAsync(gallery, [first, first], TestContext.Current.CancellationToken);
        var afterRemove = await owner.GetGalleryByIdAsync(gallery.Id, TestContext.Current.CancellationToken);
        var firstAfterRemove = await owner.GetImageByIdAsync(first.Id, TestContext.Current.CancellationToken);
        var secondAfterRemove = await owner.GetImageByIdAsync(second.Id, TestContext.Current.CancellationToken);

        removed.Should().Be(1);
        afterRemove.ImageCount.Should().Be(1);
        firstAfterRemove.GalleryIds.Should().NotContain(gallery.Id);
        secondAfterRemove.GalleryIds.Should().Contain(gallery.Id);
    }

    [Fact]
    [CoversEndpoint("DELETE", "/api/galleries/{id:int}")]
    [CoversEndpoint("DELETE", "/api/galleries/bulk")]
    public async Task GivenGalleries_WhenOwnerDeletesSelectedRecords_ThenMemberCannotDeleteAndControlsRemain()
    {
        var owner = AsUser();
        var member = AsUser(ApiTestUsers.Eva);
        var single = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Single delete gallery {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var forbiddenSingle = () => member.DeleteGalleryAsync(single.Id);

        await forbiddenSingle.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        (await owner.GetGalleryByIdAsync(single.Id, TestContext.Current.CancellationToken)).Id.Should().Be(single.Id);
        await owner.DeleteGalleryAsync(single.Id, TestContext.Current.CancellationToken);
        var deletedSingle = () => owner.GetGalleryByIdAsync(single.Id);
        await deletedSingle.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");

        var first = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Bulk delete gallery first {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var second = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Bulk delete gallery second {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var retained = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Retained gallery {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var request = new BatchDeleteDto([first.Id, int.MaxValue, second.Id]);
        var forbiddenBulk = () => member.BulkDeleteGalleriesAsync(request);

        await forbiddenBulk.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        var deleted = await owner.BulkDeleteGalleriesAsync(request, TestContext.Current.CancellationToken);

        deleted.Should().Be(2);
        foreach (var gallery in new[] { first, second })
        {
            var read = () => owner.GetGalleryByIdAsync(gallery.Id);
            await read.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*returned 404 (NotFound)*");
        }
        (await owner.GetGalleryByIdAsync(retained.Id, TestContext.Current.CancellationToken)).Id.Should().Be(retained.Id);
    }

    private static void AssertChapters(IReadOnlyList<GalleryChapterDto> actual, IReadOnlyList<GalleryChapterDto> expected)
    {
        actual.Select(chapter => chapter.Id).Should().Equal(expected.Select(chapter => chapter.Id));
        foreach (var expectedChapter in expected)
        {
            var actualChapter = actual.Single(chapter => chapter.Id == expectedChapter.Id);
            AssertChapter(actualChapter, expectedChapter, expectedChapter.Title, expectedChapter.ImageIndex, expectedChapter.GalleryId);
            actualChapter.UpdatedAt.Should().Be(expectedChapter.UpdatedAt);
        }
    }

    private static void AssertChapter(
        GalleryChapterDto actual,
        GalleryChapterDto expected,
        string title,
        int imageIndex,
        int galleryId)
    {
        actual.Id.Should().Be(expected.Id);
        actual.Title.Should().Be(title);
        actual.ImageIndex.Should().Be(imageIndex);
        actual.GalleryId.Should().Be(galleryId);
    }
}
