using Cove.Api.Controllers;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;

namespace Cove.ApiTests.Tests.Entities.Performers;

public sealed class PerformerPairingsApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/performers/{id:int}/pairings")]
    public async Task GivenSharedAndSoloVideos_WhenReadingPairings_ThenEachSharedVideoCarriesItsVisibleCast()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var focal = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Pairing focal {suffix}").Build(), ct);
        var partner = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Pairing partner {suffix}").WithGender("Female").Build(), ct);
        var guest = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Pairing guest {suffix}").Build(), ct);
        var hidden = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Pairing hidden {suffix}").AsFavorite().Build(), ct);
        var unrelated = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Pairing unrelated {suffix}").Build(), ct);
        var studio = await owner.CreateStudioAsync($"Pairing studio {suffix}", ct);
        var duo = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Pairing duo {suffix}")
            .WithPerformers([focal, partner])
            .WithStudio(studio)
            .WithDate("2014-03-02")
            .Build(), ct);
        var trio = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Pairing trio {suffix}")
            .WithPerformers([focal, partner, guest])
            .WithDate("2015")
            .Build(), ct);
        var hiddenDuo = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Pairing hidden duo {suffix}")
            .WithPerformers([focal, hidden])
            .WithDate("2016-01-05")
            .Build(), ct);
        await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Pairing solo {suffix}")
            .WithPerformers([focal])
            .WithDate("2013-05-01")
            .Build(), ct);
        await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Pairing undated solo {suffix}")
            .WithPerformers([focal])
            .Build(), ct);
        await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Pairing without focal {suffix}")
            .WithPerformers([partner, unrelated])
            .Build(), ct);

        var pairings = await owner.GetPerformerPairingsAsync(focal.Id, ct);

        pairings.PerformerId.Should().Be(focal.Id);
        pairings.VideoCount.Should().Be(5);
        pairings.SoloVideoYears.Should().BeEquivalentTo(new Dictionary<int, int> { [2013] = 1 });
        pairings.Videos.Select(video => video.Id).Should().Equal(hiddenDuo.Id, trio.Id, duo.Id);
        var duoRow = pairings.Videos.Single(video => video.Id == duo.Id);
        duoRow.Title.Should().Be($"Pairing duo {suffix}");
        duoRow.Date.Should().Be("2014-03-02");
        duoRow.StudioId.Should().Be(studio.Id);
        duoRow.StudioName.Should().Be(studio.Name);
        duoRow.PerformerIds.Should().Equal(new[] { focal.Id, partner.Id }.Order());
        var trioRow = pairings.Videos.Single(video => video.Id == trio.Id);
        trioRow.Date.Should().Be("2015");
        trioRow.StudioId.Should().BeNull();
        trioRow.PerformerIds.Should().Equal(new[] { focal.Id, partner.Id, guest.Id }.Order());
        pairings.CoStars.Select(coStar => coStar.Id).Should().BeEquivalentTo([partner.Id, guest.Id, hidden.Id]);
        var partnerSummary = pairings.CoStars.Single(coStar => coStar.Id == partner.Id);
        partnerSummary.Name.Should().Be(partner.Name);
        partnerSummary.Gender.Should().Be("Female");
        partnerSummary.VideoCount.Should().Be(3);
        pairings.CoStars.Single(coStar => coStar.Id == guest.Id).VideoCount.Should().BeNull();
        pairings.CoStars.Single(coStar => coStar.Id == hidden.Id).Favorite.Should().BeTrue();

        var roleName = $"Pairings restricted {suffix}";
        var role = await owner.CreateRoleAsync(new CreateRoleRequest(
            roleName,
            "Hides favorite performers from pairings.",
            [Permissions.VideosRead, Permissions.PerformersRead, Permissions.StudiosRead]), ct);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            role.Id,
            EntityKinds.Performer,
            "deny",
            "attribute",
            "{\"path\":\"favorite\",\"equals\":true}",
            "read"), ct);
        var username = $"pairings-restricted-{suffix}";
        const string password = "Pairings restricted password 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]), ct);
        using var session = await owner.CreateAuthSessionAsync(username, password, ct);

        var restricted = await session.Client.GetPerformerPairingsAsync(focal.Id, ct);

        restricted.VideoCount.Should().Be(5);
        restricted.SoloVideoYears.Should().BeEquivalentTo(new Dictionary<int, int> { [2013] = 1, [2016] = 1 });
        restricted.Videos.Select(video => video.Id).Should().Equal(trio.Id, duo.Id);
        restricted.Videos.SelectMany(video => video.PerformerIds).Should().NotContain(hidden.Id);
        restricted.CoStars.Select(coStar => coStar.Id).Should().BeEquivalentTo([partner.Id, guest.Id]);
        var readHidden = () => session.Client.GetPerformerPairingsAsync(hidden.Id, ct);
        await readHidden.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");

        var performersOnlyRole = $"Pairings without videos {suffix}";
        await owner.CreateRoleAsync(new CreateRoleRequest(
            performersOnlyRole,
            "Reads performers but not videos.",
            [Permissions.PerformersRead]), ct);
        var performersOnlyUser = $"pairings-no-videos-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(performersOnlyUser, password, Roles: [performersOnlyRole]), ct);
        using var performersOnly = await owner.CreateAuthSessionAsync(performersOnlyUser, password, ct);
        var readWithoutVideos = () => performersOnly.Client.GetPerformerPairingsAsync(focal.Id, ct);
        await readWithoutVideos.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
    }

    [Fact]
    public async Task GivenUntitledPortraitVideoAndClip_WhenReadingPairings_ThenBothCarryTheFileNameAndFrameTheirCardsShow()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var focal = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Frame focal {suffix}").Build(), ct);
        var partner = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Frame partner {suffix}").Build(), ct);
        var capabilities = await owner.GetFfmpegCapabilitiesAsync(ct);
        capabilities.FfmpegFound.Should().BeTrue();
        var fileName = $"pairing-portrait-{suffix}.mp4";
        var path = await AsTestFileSystem().CreateSyntheticVideoAsync(
            capabilities.FfmpegPath!, fileName,
            width: 180, height: 320,
            durationSeconds: 1, color: "blue",
            cancellationToken: ct);
        var video = await owner.CreateVideoFromFileAsync(path, ct);
        await owner.UpdateVideoAsync(video.Id, new { title = "", performerIds = new[] { focal.Id, partner.Id } }, ct);
        var clip = await owner.CreateVideoAsync(new VideoCreateDto(
            Title: null, Code: null, Details: null, Director: null, Date: null, Rating: null, Organized: false,
            StudioId: null, Captions: null, Urls: null, TagIds: null, PerformerIds: [focal.Id, partner.Id],
            GalleryIds: null, Groups: null, ParentVideoId: video.Id, ClipStartSec: 0, ClipEndSec: 0.5), ct);

        var pairings = await owner.GetPerformerPairingsAsync(focal.Id, ct);

        pairings.Videos.Select(row => row.Id).Should().BeEquivalentTo([video.Id, clip.Id]);
        pairings.Videos.Should().AllSatisfy(row =>
        {
            row.Title.Should().Be(fileName);
            row.Width.Should().Be(180);
            row.Height.Should().Be(320);
        });
    }

    [Fact]
    public async Task GivenMissingPerformer_WhenReadingPairings_ThenNotFoundIsReturned()
    {
        var read = () => AsUser().GetPerformerPairingsAsync(int.MaxValue, TestContext.Current.CancellationToken);

        await read.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }
}
