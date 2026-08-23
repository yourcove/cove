using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;

namespace Cove.ApiTests.Tests.Entities.Videos;

[Collection(ApiTestLane2Collection.Name)]
public sealed class VideoDiscoveryReconciliationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/videos/with-compilations")]
    public async Task GivenMatchingVideoAndVisibleCompilation_WhenListedTogether_ThenFiltersAndEntryShapesArePreserved()
    {
        // Arrange
        var token = Guid.NewGuid().ToString("N");
        var matchingVideo = await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle($"Discovery {token} video").Build());
        var matchingCompilation = await AsUser().CreateCompilationAsync($"Discovery {token} compilation");
        await AsUser().AddVideoToGroupAsync(matchingVideo, matchingCompilation);
        await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle($"Excluded {token}").Build());

        // Act
        var result = await AsUser(ApiTestUsers.Eva).GetVideosWithCompilationsAsync($"Discovery {token}");

        // Assert
        result.TotalCount.Should().Be(2);
        result.Items.Should().HaveCount(2);
        result.Items.Should().ContainSingle(entry => entry.Kind == "video" && entry.Id == matchingVideo.Id)
            .Which.Video!.Title.Should().Be(matchingVideo.Title);
        result.Items.Should().ContainSingle(entry => entry.Kind == "compilation" && entry.Id == matchingCompilation.Id)
            .Which.Group!.Name.Should().Be(matchingCompilation.Name);
    }

    [Fact]
    [CoversEndpoint("GET", "/api/videos/wall")]
    [CoversEndpoint("GET", "/api/videos/duplicates")]
    public async Task GivenDiscoverableVideos_WhenWallAndDuplicateModesAreRequested_ThenEachModeReturnsOnlyItsMatchingGroups()
    {
        // Arrange
        var token = Guid.NewGuid().ToString("N");
        var wallFirst = await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle($"Wall {token} first").Build());
        var wallSecond = await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle($"Wall {token} second").Build());
        var titleFirst = await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle($"Title duplicate {token}").Build());
        var titleSecond = await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle($" title duplicate {token} ").Build());
        var fingerprintFirst = await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle($"Fingerprint first {token}").Build());
        var fingerprintSecond = await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle($"Fingerprint second {token}").Build());
        var remoteFirst = await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle($"Remote first {token}").WithRemoteId("https://metadata.example", $"remote-{token}").Build());
        var remoteSecond = await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle($"Remote second {token}").WithRemoteId("HTTPS://METADATA.EXAMPLE", $" REMOTE-{token} ").Build());
        await AsDbUser().AttachVideoFileAsync(fingerprintFirst.Id, duration: 1, size: 1, fingerprints: new Dictionary<string, string> { ["md5"] = token });
        await AsDbUser().AttachVideoFileAsync(fingerprintSecond.Id, duration: 1, size: 1, fingerprints: new Dictionary<string, string> { ["md5"] = token });

        // Act
        var wall = await AsUser(ApiTestUsers.Eva).GetVideoWallAsync($"Wall {token}", 2);
        var titleGroups = await AsUser(ApiTestUsers.Eva).FindDuplicateVideosAsync("title");
        var fingerprintGroups = await AsUser(ApiTestUsers.Eva).FindDuplicateVideosAsync("fingerprint");
        var remoteGroups = await AsUser(ApiTestUsers.Eva).FindDuplicateVideosAsync("remote-id");

        // Assert
        wall.Select(video => video.Id).Should().BeEquivalentTo([wallFirst.Id, wallSecond.Id]);
        wall.Should().AllSatisfy(video =>
        {
            video.Files.Should().NotBeNull();
            video.Tags.Should().NotBeNull();
            video.Performers.Should().NotBeNull();
        });
        titleGroups.Should().ContainSingle().Which.Select(video => video.Id).Should().BeEquivalentTo(new[] { titleFirst.Id, titleSecond.Id });
        fingerprintGroups.Should().ContainSingle().Which.Select(video => video.Id).Should().BeEquivalentTo(new[] { fingerprintFirst.Id, fingerprintSecond.Id });
        remoteGroups.Should().ContainSingle().Which.Select(video => video.Id).Should().BeEquivalentTo(new[] { remoteFirst.Id, remoteSecond.Id });
    }

    [Fact]
    [CoversEndpoint("POST", "/api/videos/from-file")]
    [CoversEndpoint("POST", "/api/videos/{id:int}/assign-file")]
    public async Task GivenImportedFile_WhenAssignedToAnotherVideo_ThenTheNewOwnerPersistsTheFile()
    {
        // Arrange
        var path = AsTestFileSystem().CreateTextFile("A local file imported through the video API.");
        var target = await AsUser().CreateVideoAsync($"Assignment target {Guid.NewGuid():N}");

        // Act
        var imported = await AsUser(ApiTestUsers.Eva).CreateVideoFromFileAsync(path);
        var file = imported.Files.Should().ContainSingle().Which;
        await AsUser(ApiTestUsers.Eva).AssignVideoFileAsync(target, file.Id);
        var targetAfter = await AsUser().GetVideoByIdAsync(target.Id);
        var sourceAfter = await AsUser().GetVideoByIdAsync(imported.Id);

        // Assert
        targetAfter.Files.Should().ContainSingle(candidate => candidate.Id == file.Id && candidate.Path == path);
        sourceAfter.Files.Should().BeEmpty();
    }

}
