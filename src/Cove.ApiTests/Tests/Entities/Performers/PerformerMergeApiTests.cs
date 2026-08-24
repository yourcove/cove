using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Tests.Entities.Performers;

public sealed class PerformerMergeApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/performers/merge")]
    public async Task GivenComplementaryPerformers_WhenOwnerMergesThem_ThenMetadataRelationshipsAndControlsAreExact()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var targetTag = await AsUser().CreateTagAsync($"Merge target tag {suffix}", TestContext.Current.CancellationToken);
        var sourceTag = await AsUser().CreateTagAsync($"Merge source tag {suffix}", TestContext.Current.CancellationToken);
        var target = await AsUser().CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Merge target performer {suffix}")
            .WithDisambiguation("Target identity")
            .WithGender("Female")
            .WithCountry("Target country")
            .WithAlias("Target alias")
            .WithUrl($"https://performer.example/target/{suffix}")
            .WithRemoteId("https://metadata.example/merge", $"target-{suffix}")
            .WithTag(targetTag)
            .Build(), TestContext.Current.CancellationToken);
        var source = await AsUser().CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Merge source performer {suffix}")
            .WithGender("Male")
            .WithDetails("Source details transferred to the target")
            .WithHeightCm(171)
            .WithTattoos("Source tattoo")
            .AsFavorite()
            .WithAlias("Source alias")
            .WithUrl($"https://performer.example/source/{suffix}")
            .WithRemoteId("https://metadata.example/merge", $"source-{suffix}")
            .WithTag(sourceTag)
            .Build(), TestContext.Current.CancellationToken);
        var control = await AsUser().CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Merge control performer {suffix}")
            .WithDetails("Control details")
            .WithAlias("Control alias")
            .Build(), TestContext.Current.CancellationToken);
        var sourceOnlyVideo = await AsUser().CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Source-only merge video {suffix}")
            .WithPerformers([source])
            .Build(), TestContext.Current.CancellationToken);
        var sharedVideo = await AsUser().CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Shared merge video {suffix}")
            .WithPerformers([target, source])
            .Build(), TestContext.Current.CancellationToken);
        var controlVideo = await AsUser().CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Control merge video {suffix}")
            .WithPerformers([control])
            .Build(), TestContext.Current.CancellationToken);
        var sourceImage = await AsUser().CreateImageAsync(new ImageBuilder()
            .WithTitle($"Source merge image {suffix}")
            .WithPerformer(source)
            .Build(), TestContext.Current.CancellationToken);
        var sourceGallery = await AsUser().CreateGalleryAsync(new GalleryBuilder()
            .WithTitle($"Source merge gallery {suffix}")
            .WithPerformer(source)
            .Build(), TestContext.Current.CancellationToken);
        var sourceAudio = await AsUser().CreateAudioAsync(new AudioBuilder()
            .WithTitle($"Source merge audio {suffix}")
            .WithPerformer(source)
            .Build(), TestContext.Current.CancellationToken);
        var sourceText = await AsUser().CreateTextAsync(new TextDocumentBuilder()
            .WithTitle($"Source merge text {suffix}")
            .WithPerformer(source)
            .Build(), TestContext.Current.CancellationToken);

        var missingTarget = () => AsUser().MergePerformersAsync(int.MaxValue, [source.Id]);
        await missingTarget.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
        (await AsUser().GetPerformerByIdAsync(source.Id, TestContext.Current.CancellationToken)).Id.Should().Be(source.Id);

        var forbidden = () => AsUser(ApiTestUsers.Eva).MergePerformersAsync(target.Id, [source.Id]);
        await forbidden.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        AssertUnmerged(
            await AsUser().GetPerformerByIdAsync(target.Id, TestContext.Current.CancellationToken),
            await AsUser().GetPerformerByIdAsync(source.Id, TestContext.Current.CancellationToken),
            target,
            source,
            targetTag,
            sourceTag);
        (await AsUser().GetVideoByIdAsync(sourceOnlyVideo.Id, TestContext.Current.CancellationToken)).Performers.Select(performer => performer.Id).Should().Equal(source.Id);
        (await AsUser().GetVideoByIdAsync(sharedVideo.Id, TestContext.Current.CancellationToken)).Performers.Select(performer => performer.Id).Should().BeEquivalentTo([target.Id, source.Id]);

        var merged = await AsUser().MergePerformersAsync(target.Id, [source.Id, int.MaxValue], TestContext.Current.CancellationToken);

        AssertMerged(merged, target, source, targetTag, sourceTag, suffix);
        AssertMerged(await AsUser().GetPerformerByIdAsync(target.Id, TestContext.Current.CancellationToken), target, source, targetTag, sourceTag, suffix);
        var sourceMissing = () => AsUser().GetPerformerByIdAsync(source.Id);
        await sourceMissing.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
        (await AsUser().GetPerformersAsync(TestContext.Current.CancellationToken)).Should().NotContain(performer => performer.Id == source.Id);
        (await AsUser().GetVideoByIdAsync(sourceOnlyVideo.Id, TestContext.Current.CancellationToken)).Performers.Select(performer => performer.Id).Should().Equal(target.Id);
        (await AsUser().GetVideoByIdAsync(sharedVideo.Id, TestContext.Current.CancellationToken)).Performers.Select(performer => performer.Id).Should().Equal(target.Id);
        (await AsUser().GetImageByIdAsync(sourceImage.Id, TestContext.Current.CancellationToken)).Performers.Select(performer => performer.Id).Should().Equal(target.Id);
        (await AsUser().GetGalleryByIdAsync(sourceGallery.Id, TestContext.Current.CancellationToken)).Performers.Select(performer => performer.Id).Should().Equal(target.Id);
        (await AsUser().GetAudioByIdAsync(sourceAudio.Id, TestContext.Current.CancellationToken)).Performers.Select(performer => performer.Id).Should().Equal(target.Id);
        (await AsUser().GetTextByIdAsync(sourceText.Id, TestContext.Current.CancellationToken)).Performers.Select(performer => performer.Id).Should().Equal(target.Id);
        var controlAfter = await AsUser().GetPerformerByIdAsync(control.Id, TestContext.Current.CancellationToken);
        controlAfter.Name.Should().Be(control.Name);
        controlAfter.Details.Should().Be("Control details");
        controlAfter.Aliases.Should().Equal("Control alias");
        (await AsUser().GetVideoByIdAsync(controlVideo.Id, TestContext.Current.CancellationToken)).Performers.Select(performer => performer.Id).Should().Equal(control.Id);
    }

    private static void AssertUnmerged(
        PerformerDto actualTarget,
        PerformerDto actualSource,
        PerformerDto target,
        PerformerDto source,
        TagDetailDto targetTag,
        TagDetailDto sourceTag)
    {
        actualTarget.Id.Should().Be(target.Id);
        actualTarget.Name.Should().Be(target.Name);
        actualTarget.Details.Should().BeNull();
        actualTarget.Tags.Select(tag => tag.Id).Should().Equal(targetTag.Id);
        actualSource.Id.Should().Be(source.Id);
        actualSource.Details.Should().Be("Source details transferred to the target");
        actualSource.Tags.Select(tag => tag.Id).Should().Equal(sourceTag.Id);
    }

    private static void AssertMerged(
        PerformerDto actual,
        PerformerDto target,
        PerformerDto source,
        TagDetailDto targetTag,
        TagDetailDto sourceTag,
        string suffix)
    {
        actual.Id.Should().Be(target.Id);
        actual.Name.Should().Be(target.Name);
        actual.Disambiguation.Should().Be(target.Disambiguation);
        actual.Gender.Should().Be("Female");
        actual.Country.Should().Be("Target country");
        actual.Details.Should().Be("Source details transferred to the target");
        actual.HeightCm.Should().Be(171);
        actual.Tattoos.Should().Be("Source tattoo");
        actual.Favorite.Should().BeTrue();
        actual.VideoCount.Should().Be(2);
        actual.ImageCount.Should().Be(1);
        actual.GalleryCount.Should().Be(1);
        actual.AudioCount.Should().Be(1);
        actual.TextCount.Should().Be(1);
        actual.Aliases.Should().BeEquivalentTo(["Target alias", "Source alias", source.Name]);
        actual.Urls.Should().BeEquivalentTo(
            $"https://performer.example/target/{suffix}",
            $"https://performer.example/source/{suffix}");
        actual.Tags.Select(tag => tag.Id).Should().BeEquivalentTo([targetTag.Id, sourceTag.Id]);
        actual.RemoteIds.Should().BeEquivalentTo(
        [
            new PerformerRemoteIdDto("https://metadata.example/merge", $"target-{suffix}"),
            new PerformerRemoteIdDto("https://metadata.example/merge", $"source-{suffix}"),
        ]);
    }
}
