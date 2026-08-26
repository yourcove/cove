using System.Text.Json;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Entities.Images;

public sealed class ImageLifecycleQueryApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/images")]
    [CoversEndpoint("GET", "/api/images/{id:int}")]
    public async Task GivenImageMetadata_WhenCreatedAndRead_ThenRelationshipsAndUserRatingRoundTrip()
    {
        const string customFieldKey = "image_lighting";
        var owner = AsUser();
        var studio = await owner.CreateStudioAsync($"Image studio {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var tag = await owner.CreateTagAsync($"Image tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var performer = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Image performer {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var gallery = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Image gallery {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var group = await owner.CreateGroupAsync($"Image group {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        await owner.CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = customFieldKey,
            Label = "Image lighting",
            Type = "text",
            EntityTypes = ["image"],
        }, TestContext.Current.CancellationToken);
        var request = new ImageBuilder()
            .WithTitle($"Image lifecycle {Guid.NewGuid():N}")
            .WithCode("IMAGE-CODE")
            .WithDetails("Image lifecycle details")
            .WithPhotographer("Image photographer")
            .WithRating(83)
            .WithStudio(studio)
            .WithDate("2026-08-14")
            .WithUrl("https://image.example/item")
            .WithTag(tag)
            .WithPerformer(performer)
            .WithGallery(gallery)
            .WithGroup(group)
            .WithCustomField(customFieldKey, "Warm key light")
            .AsOrganized()
            .Build();

        var created = await owner.CreateImageAsync(request, TestContext.Current.CancellationToken);
        var retrieved = await AsUser(ApiTestUsers.Eva).GetImageByIdAsync(created.Id, TestContext.Current.CancellationToken);
        var ownerEngagement = await owner.GetEntityEngagementAsync(AffinityHostType.Image, created.Id, TestContext.Current.CancellationToken);
        var memberEngagement = await AsUser(ApiTestUsers.Eva).GetEntityEngagementAsync(AffinityHostType.Image, created.Id, TestContext.Current.CancellationToken);

        foreach (var actual in new[] { created, retrieved })
        {
            actual.Title.Should().Be(request.Title);
            actual.Code.Should().Be(request.Code);
            actual.Details.Should().Be(request.Details);
            actual.Photographer.Should().Be(request.Photographer);
            actual.Organized.Should().BeTrue();
            actual.StudioId.Should().Be(studio.Id);
            actual.StudioName.Should().Be(studio.Name);
            actual.Date.Should().Be(request.Date);
            actual.Urls.Should().Equal(request.Urls!);
            actual.Tags.Select(item => item.Id).Should().Equal(tag.Id);
            actual.Performers.Select(item => item.Id).Should().Equal(performer.Id);
            actual.GalleryIds.Should().Equal(gallery.Id);
            actual.Groups.Select(item => (item.Id, item.VideoIndex)).Should().Equal((group.Id, 0));
            actual.CustomFields.Should().ContainKey(customFieldKey).WhoseValue.Should().BeOfType<JsonElement>().Which.GetString().Should().Be("Warm key light");
        }
        ownerEngagement.Rating.Should().Be(83);
        memberEngagement.Rating.Should().BeNull();
    }

    [Fact]
    [CoversEndpoint("PUT", "/api/images/{id:int}")]
    public async Task GivenImageMetadata_WhenMemberPartiallyUpdatesIt_ThenResponseAndReadPreserveExactRelationships()
    {
        var owner = AsUser();
        var studio = await owner.CreateStudioAsync($"Original image studio {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var tag = await owner.CreateTagAsync($"Original image tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var replacementTag = await owner.CreateTagAsync($"Replacement image tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var performer = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Original image performer {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var gallery = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Original image gallery {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var group = await owner.CreateGroupAsync($"Original image group {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var image = await owner.CreateImageAsync(new ImageBuilder()
            .WithTitle($"Original image {Guid.NewGuid():N}").WithCode("ORIGINAL-CODE").WithDetails("Original details")
            .WithPhotographer("Original photographer").WithDate("2026-08-14").WithStudio(studio)
            .WithUrl("https://image.example/original").WithTag(tag).WithPerformer(performer).WithGallery(gallery).WithGroup(group).Build(), TestContext.Current.CancellationToken);

        var updated = await AsUser(ApiTestUsers.Eva).UpdateImageAsync(image.Id, new
        {
            title = "Updated image title",
            details = "Updated details",
            urls = new[] { "https://image.example/updated" },
            tagIds = new[] { replacementTag.Id },
            clearFields = new[] { "studioId" },
        }, TestContext.Current.CancellationToken);
        var retrieved = await owner.GetImageByIdAsync(image.Id, TestContext.Current.CancellationToken);

        foreach (var actual in new[] { updated, retrieved })
        {
            actual.Title.Should().Be("Updated image title");
            actual.Details.Should().Be("Updated details");
            actual.Urls.Should().Equal("https://image.example/updated");
            actual.Code.Should().Be("ORIGINAL-CODE");
            actual.Photographer.Should().Be("Original photographer");
            actual.Date.Should().Be("2026-08-14");
            actual.StudioId.Should().BeNull();
            actual.Tags.Select(item => item.Id).Should().Equal(replacementTag.Id);
            actual.Performers.Select(item => item.Id).Should().Equal(performer.Id);
            actual.GalleryIds.Should().Equal(gallery.Id);
            actual.Groups.Select(item => (item.Id, item.VideoIndex)).Should().Equal((group.Id, 0));
        }
    }

    [Fact]
    [CoversEndpoint("POST", "/api/images/find")]
    public async Task GivenMatchingImages_WhenFilteredSortedAndPaged_ThenOnlyTheRequestedPageIsReturned()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var owner = AsUser();
        var first = await owner.CreateImageAsync(new ImageBuilder().WithTitle($"A filtered image {suffix}").AsOrganized().Build(), TestContext.Current.CancellationToken);
        var second = await owner.CreateImageAsync(new ImageBuilder().WithTitle($"B filtered image {suffix}").AsOrganized().Build(), TestContext.Current.CancellationToken);
        await owner.CreateImageAsync(new ImageBuilder().WithTitle($"Excluded image {suffix}").Build(), TestContext.Current.CancellationToken);
        var result = await AsUser(ApiTestUsers.Eva).FindImagesAsync(new FilteredQueryRequest<ImageFilter>
        {
            ObjectFilter = new ImageFilter { OrganizedCriterion = new BoolCriterion { Value = true } },
            FindFilter = new FindFilter { Q = suffix, Page = 2, PerPage = 1, Sort = "title" },
        }, TestContext.Current.CancellationToken);

        result.TotalCount.Should().Be(2);
        result.Page.Should().Be(2);
        result.PerPage.Should().Be(1);
        result.Items.Should().ContainSingle().Which.Id.Should().Be(second.Id);
        result.Items.Should().NotContain(item => item.Id == first.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/images/aggregate")]
    public async Task GivenSelectedImages_WhenAggregated_ThenNonzeroFileSizeIsScopedToSelection()
    {
        var owner = AsUser();
        var first = await owner.CreateImageAsync($"Aggregate image first {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var second = await owner.CreateImageAsync($"Aggregate image second {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var excluded = await owner.CreateImageAsync($"Aggregate image excluded {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        await AsDbUser().AttachImageFileAsync(first.Id, 1_000, TestContext.Current.CancellationToken);
        await AsDbUser().AttachImageFileAsync(second.Id, 2_500, TestContext.Current.CancellationToken);
        await AsDbUser().AttachImageFileAsync(excluded.Id, 9_999, TestContext.Current.CancellationToken);

        var aggregate = await AsUser(ApiTestUsers.Eva).AggregateImagesAsync(new FilteredQueryRequest<ImageFilter> { Ids = [first.Id, second.Id] }, TestContext.Current.CancellationToken);

        aggregate.Should().Be(new ImageAggregate(Count: 2, FileSize: 3_500));
    }

    [Fact]
    [CoversEndpoint("POST", "/api/images/bulk")]
    public async Task GivenImagesWithRelationships_WhenMemberBulkSetsValues_ThenOnlySelectedImagesChange()
    {
        var owner = AsUser();
        var originalStudio = await owner.CreateStudioAsync($"Original bulk image studio {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var originalTag = await owner.CreateTagAsync($"Original bulk image tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var originalPerformer = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Original bulk image performer {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var originalGallery = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Original bulk image gallery {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var replacementTag = await owner.CreateTagAsync($"Replacement bulk image tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var replacementPerformer = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Replacement bulk image performer {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var replacementGallery = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Replacement bulk image gallery {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var selected = await Task.WhenAll(Enumerable.Range(1, 2).Select(index => owner.CreateImageAsync(new ImageBuilder()
            .WithTitle($"Selected bulk image {index} {Guid.NewGuid():N}").WithCode($"ORIGINAL-{index}").WithDetails($"Original details {index}")
            .WithPhotographer($"Original photographer {index}").WithDate("2026-08-14").WithStudio(originalStudio)
            .WithTag(originalTag).WithPerformer(originalPerformer).WithGallery(originalGallery).Build())));
        var control = await owner.CreateImageAsync(new ImageBuilder().WithTitle($"Control bulk image {Guid.NewGuid():N}").WithCode("CONTROL-CODE")
            .WithDetails("Control details").WithPhotographer("Control photographer").WithDate("2026-08-15").WithStudio(originalStudio)
            .WithTag(originalTag).WithPerformer(originalPerformer).WithGallery(originalGallery).Build(), TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).SetImageRatingAsync(control, 17, TestContext.Current.CancellationToken);
        var request = new BulkImageUpdateDto
        {
            Ids = selected.Select(item => item.Id).ToList(),
            ClearFields = ["studioId", "date", "code", "details", "photographer"],
            Organized = true,
            Rating = 91,
            TagIds = [replacementTag.Id],
            TagMode = BulkUpdateMode.Set,
            PerformerIds = [replacementPerformer.Id],
            PerformerMode = BulkUpdateMode.Set,
            GalleryIds = [replacementGallery.Id],
            GalleryMode = BulkUpdateMode.Set,
        };

        var updatedCount = await AsUser(ApiTestUsers.Eva).BulkUpdateImagesAsync(request, TestContext.Current.CancellationToken);
        var updated = await Task.WhenAll(selected.Select(item => owner.GetImageByIdAsync(item.Id)));
        var controlAfter = await owner.GetImageByIdAsync(control.Id, TestContext.Current.CancellationToken);
        var memberEngagements = await Task.WhenAll(selected.Select(item => AsUser(ApiTestUsers.Eva).GetEntityEngagementAsync(AffinityHostType.Image, item.Id)));
        var ownerEngagements = await Task.WhenAll(selected.Select(item => owner.GetEntityEngagementAsync(AffinityHostType.Image, item.Id)));
        var controlEngagement = await AsUser(ApiTestUsers.Eva).GetEntityEngagementAsync(AffinityHostType.Image, control.Id, TestContext.Current.CancellationToken);

        updatedCount.Should().Be(2);
        updated.Should().AllSatisfy(item =>
        {
            item.Code.Should().BeNull();
            item.Date.Should().BeNull();
            item.Details.Should().BeNull();
            item.Photographer.Should().BeNull();
            item.Organized.Should().BeTrue();
            item.StudioId.Should().BeNull();
            item.Tags.Select(candidate => candidate.Id).Should().Equal(replacementTag.Id);
            item.Performers.Select(candidate => candidate.Id).Should().Equal(replacementPerformer.Id);
            item.GalleryIds.Should().Equal(replacementGallery.Id);
        });
        memberEngagements.Should().AllSatisfy(item => item.Rating.Should().Be(91));
        ownerEngagements.Should().AllSatisfy(item => item.Rating.Should().BeNull());
        controlAfter.Code.Should().Be("CONTROL-CODE");
        controlAfter.Date.Should().Be("2026-08-15");
        controlAfter.Details.Should().Be("Control details");
        controlAfter.Photographer.Should().Be("Control photographer");
        controlAfter.Organized.Should().BeFalse();
        controlAfter.StudioId.Should().Be(originalStudio.Id);
        controlAfter.Tags.Select(item => item.Id).Should().Equal(originalTag.Id);
        controlAfter.Performers.Select(item => item.Id).Should().Equal(originalPerformer.Id);
        controlAfter.GalleryIds.Should().Equal(originalGallery.Id);
        controlEngagement.Rating.Should().Be(17);
    }

    [Fact]
    [CoversEndpoint("DELETE", "/api/images/{id:int}")]
    [CoversEndpoint("DELETE", "/api/images/bulk")]
    public async Task GivenImages_WhenOwnerDeletesSelectedRecords_ThenMemberCannotDeleteAndControlsRemain()
    {
        var owner = AsUser();
        var member = AsUser(ApiTestUsers.Eva);
        var single = await owner.CreateImageAsync($"Single delete image {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var forbiddenSingle = () => member.DeleteImageAsync(single.Id);
        await forbiddenSingle.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await owner.GetImageByIdAsync(single.Id, TestContext.Current.CancellationToken)).Id.Should().Be(single.Id);
        await owner.DeleteImageAsync(single.Id, TestContext.Current.CancellationToken);
        var deletedSingle = () => owner.GetImageByIdAsync(single.Id);
        await deletedSingle.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");

        var first = await owner.CreateImageAsync($"Bulk delete image first {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var second = await owner.CreateImageAsync($"Bulk delete image second {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var retained = await owner.CreateImageAsync($"Retained image {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var request = new BatchDeleteDto([first.Id, int.MaxValue, second.Id]);
        var forbiddenBulk = () => member.BulkDeleteImagesAsync(request);
        await forbiddenBulk.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        foreach (var image in new[] { first, second })
            (await owner.GetImageByIdAsync(image.Id, TestContext.Current.CancellationToken)).Id.Should().Be(image.Id);

        var queued = await owner.BulkDeleteImagesAsync(request, TestContext.Current.CancellationToken);
        queued.ItemCount.Should().Be(3);
        AssertCompletedBulkDeletion(
            await owner.WaitForTerminalJobAsync(queued.JobId, TestContext.Current.CancellationToken),
            succeeded: 2,
            skipped: 1);

        foreach (var image in new[] { first, second })
        {
            var read = () => owner.GetImageByIdAsync(image.Id);
            await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        }
        (await owner.GetImageByIdAsync(retained.Id, TestContext.Current.CancellationToken)).Id.Should().Be(retained.Id);
    }
}
