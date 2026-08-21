using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Entities.Videos;

[Collection(ApiTestLane2Collection.Name)]
public sealed class VideoMergeApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/videos/merge")]
    public async Task GivenSelectedVideoSources_WhenMerged_ThenFilesAndDistinctRelationshipsMoveToTheTargetAndSourcesAreDeleted()
    {
        // Arrange
        var targetTag = await AsUser().CreateTagAsync($"Target tag {Guid.NewGuid():N}");
        var firstSourceTag = await AsUser().CreateTagAsync($"First source tag {Guid.NewGuid():N}");
        var secondSourceTag = await AsUser().CreateTagAsync($"Second source tag {Guid.NewGuid():N}");
        var sharedSourceTag = await AsUser().CreateTagAsync($"Shared source tag {Guid.NewGuid():N}");
        var targetPerformer = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName($"Target performer {Guid.NewGuid():N}").Build());
        var firstSourcePerformer = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName($"First source performer {Guid.NewGuid():N}").Build());
        var secondSourcePerformer = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName($"Second source performer {Guid.NewGuid():N}").Build());
        var sharedSourcePerformer = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName($"Shared source performer {Guid.NewGuid():N}").Build());
        var targetGallery = await AsUser().CreateGalleryAsync(new GalleryBuilder().WithTitle($"Target gallery {Guid.NewGuid():N}").Build());
        var sourceGallery = await AsUser().CreateGalleryAsync(new GalleryBuilder().WithTitle($"Source gallery {Guid.NewGuid():N}").Build());
        var targetUrl = $"https://merge.example/target/{Guid.NewGuid():N}";
        var sourceUrl = $"https://merge.example/source/{Guid.NewGuid():N}";
        var target = await AsUser().CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Merge target {Guid.NewGuid():N}")
            .WithTags([targetTag])
            .WithPerformers([targetPerformer])
            .WithGallery(targetGallery)
            .WithUrl(targetUrl)
            .Build());
        var firstSourcePath = AsTestFileSystem().CreateTextFile("A first source file that must move during merge.");
        var secondSourcePath = AsTestFileSystem().CreateTextFile("A second source file that must move during merge.");
        var firstSource = await AsUser().CreateVideoFromFileAsync(firstSourcePath);
        firstSource = await AsUser().UpdateVideoAsync(firstSource.Id, new
        {
            tagIds = new[] { firstSourceTag.Id, sharedSourceTag.Id },
            performerIds = new[] { firstSourcePerformer.Id, sharedSourcePerformer.Id },
            galleryIds = new[] { sourceGallery.Id },
            urls = new[] { sourceUrl },
        });
        var secondSource = await AsUser().CreateVideoFromFileAsync(secondSourcePath);
        secondSource = await AsUser().UpdateVideoAsync(secondSource.Id, new
        {
            tagIds = new[] { secondSourceTag.Id, sharedSourceTag.Id },
            performerIds = new[] { secondSourcePerformer.Id, sharedSourcePerformer.Id },
            galleryIds = new[] { sourceGallery.Id },
            urls = new[] { sourceUrl },
        });
        var control = await AsUser().CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Merge control {Guid.NewGuid():N}")
            .WithTags([sharedSourceTag])
            .WithPerformers([sharedSourcePerformer])
            .WithGallery(sourceGallery)
            .WithUrl(sourceUrl)
            .Build());
        var forbiddenMerge = () => AsUser(ApiTestUsers.Eva).MergeVideosAsync(target, firstSource, secondSource);
        await forbiddenMerge.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        var memberRole = (await AsUser().GetRolesAsync()).Single(role => role.Name == BuiltinRoles.Member);
        await AsUser().UpdateRoleAsync(
            memberRole.Id,
            new UpdateRoleRequest(
                Description: null,
                Permissions: memberRole.Permissions.Append(Permissions.VideosDelete).Distinct().ToArray()));
        await AsUser().CreateContentRuleAsync(new CreateContentRuleRequest(
            memberRole.Id,
            EntityKinds.Performer,
            Effect: "deny",
            ScopeKind: "all",
            ScopeValue: "{}",
            AppliesTo: "read"));

        // Act
        var merged = await AsUser(ApiTestUsers.Eva).MergeVideosAsync(target, firstSource, secondSource);
        var persisted = await AsUser().GetVideoByIdAsync(target.Id);
        var controlAfter = await AsUser().GetVideoByIdAsync(control.Id);

        // Assert
        merged.Id.Should().Be(target.Id);
        merged.Performers.Should().BeEmpty();
        persisted.Performers.Select(performer => performer.Id).Should().BeEquivalentTo(new[] { targetPerformer.Id, firstSourcePerformer.Id, secondSourcePerformer.Id, sharedSourcePerformer.Id });
        foreach (var actual in new[] { merged, persisted })
        {
            actual.Tags.Select(tag => tag.Id).Should().BeEquivalentTo(new[] { targetTag.Id, firstSourceTag.Id, secondSourceTag.Id, sharedSourceTag.Id });
            actual.Galleries.Select(gallery => gallery.Id).Should().BeEquivalentTo(new[] { targetGallery.Id, sourceGallery.Id });
            actual.Urls.Should().BeEquivalentTo(targetUrl, sourceUrl);
            actual.Files.Select(file => file.Path).Should().BeEquivalentTo(firstSourcePath, secondSourcePath);
        }
        foreach (var source in new[] { firstSource, secondSource })
        {
            var sourceRead = () => AsUser().GetVideoByIdAsync(source.Id);
            await sourceRead.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        }
        controlAfter.Tags.Select(tag => tag.Id).Should().Equal(sharedSourceTag.Id);
        controlAfter.Performers.Select(performer => performer.Id).Should().Equal(sharedSourcePerformer.Id);
        controlAfter.Galleries.Select(gallery => gallery.Id).Should().Equal(sourceGallery.Id);
        controlAfter.Urls.Should().Equal(sourceUrl);
    }
}
