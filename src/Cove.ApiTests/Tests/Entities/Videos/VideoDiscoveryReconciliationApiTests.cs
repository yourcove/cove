using System.Text.Json;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Entities.Videos;

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
        var matchingVideo = await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle($"Discovery {token} video").Build(), TestContext.Current.CancellationToken);
        var matchingCompilation = await AsUser().CreateCompilationAsync($"Discovery {token} compilation", TestContext.Current.CancellationToken);
        await AsUser().AddVideoToGroupAsync(matchingVideo, matchingCompilation, TestContext.Current.CancellationToken);
        await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle($"Excluded {token}").Build(), TestContext.Current.CancellationToken);

        // Act
        var result = await AsUser(ApiTestUsers.Eva).GetVideosWithCompilationsAsync(
            $"Discovery {token}",
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.TotalCount.Should().Be(2);
        result.Items.Should().HaveCount(2);
        result.Items.Should().ContainSingle(entry => entry.Kind == "video" && entry.Id == matchingVideo.Id)
            .Which.Video!.Title.Should().Be(matchingVideo.Title);
        result.Items.Should().ContainSingle(entry => entry.Kind == "compilation" && entry.Id == matchingCompilation.Id)
            .Which.Group!.Name.Should().Be(matchingCompilation.Name);
    }

    [Fact]
    [CoversEndpoint("GET", "/api/videos/with-compilations")]
    [CoversEndpoint("POST", "/api/custom-fields")]
    [CoversEndpoint("POST", "/api/videos")]
    public async Task GivenQueryableJsonValuesAndCompilation_WhenListedTogether_ThenJsonSortOrdersVideosAndPlacesCompilationLast()
    {
        var owner = AsUser();
        var token = Guid.NewGuid().ToString("N");
        var key = $"mixed_json_{token}";
        const string path = "/profile/score";
        await owner.CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = key,
            Label = "Mixed JSON metadata",
            Type = CustomFieldTypes.Json,
            EntityTypes = [CustomFieldEntityTypes.Video],
            JsonPaths =
            [
                new CustomFieldJsonPathDefinitionDto
                {
                    Path = path,
                    Label = "Score",
                    Type = CustomFieldTypes.Number,
                    Sortable = true,
                },
            ],
        }, TestContext.Current.CancellationToken);
        var high = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Mixed JSON {token} high")
            .WithCustomField(key, JsonSerializer.SerializeToElement(new { profile = new { score = 30 } }))
            .Build(), TestContext.Current.CancellationToken);
        var low = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Mixed JSON {token} low")
            .WithCustomField(key, JsonSerializer.SerializeToElement(new { profile = new { score = 10 } }))
            .Build(), TestContext.Current.CancellationToken);
        var compilation = await owner.CreateCompilationAsync($"Mixed JSON {token} compilation", TestContext.Current.CancellationToken);
        await owner.AddVideoToGroupAsync(high, compilation, TestContext.Current.CancellationToken);

        var result = await owner.GetVideosWithCompilationsAsync(
            $"Mixed JSON {token}",
            $"custom-json:number:{key}:{Uri.EscapeDataString(path)}",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Items.Select(entry => (entry.Kind, entry.Id)).Should().Equal(
            ("video", low.Id),
            ("video", high.Id),
            ("compilation", compilation.Id));
    }

    [Fact]
    [CoversEndpoint("GET", "/api/videos/wall")]
    [CoversEndpoint("GET", "/api/videos/duplicates")]
    [CoversEndpoint("POST", "/api/videos/duplicate-searches")]
    [CoversEndpoint("GET", "/api/videos/duplicate-searches/{searchid:guid}/groups")]
    public async Task GivenDiscoverableVideos_WhenWallAndDuplicateModesAreRequested_ThenEachModeReturnsOnlyItsMatchingGroups()
    {
        // Arrange
        var token = Guid.NewGuid().ToString("N");
        var wallFirst = await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle($"Wall {token} first").Build(), TestContext.Current.CancellationToken);
        var wallSecond = await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle($"Wall {token} second").Build(), TestContext.Current.CancellationToken);
        var titleFirst = await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle($"Title duplicate {token}").Build(), TestContext.Current.CancellationToken);
        var titleSecond = await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle($" title duplicate {token} ").Build(), TestContext.Current.CancellationToken);
        var fingerprintFirst = await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle($"Fingerprint first {token}").Build(), TestContext.Current.CancellationToken);
        var fingerprintSecond = await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle($"Fingerprint second {token}").Build(), TestContext.Current.CancellationToken);
        var remoteFirst = await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle($"Remote first {token}").WithRemoteId("https://metadata.example", $"remote-{token}").Build(), TestContext.Current.CancellationToken);
        var remoteSecond = await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle($"Remote second {token}").WithRemoteId("HTTPS://METADATA.EXAMPLE", $" REMOTE-{token} ").Build(), TestContext.Current.CancellationToken);
        await AsDbUser().AttachVideoFileAsync(fingerprintFirst.Id, duration: 1, size: 1, fingerprints: new Dictionary<string, string> { ["md5"] = token }, cancellationToken: TestContext.Current.CancellationToken);
        await AsDbUser().AttachVideoFileAsync(fingerprintSecond.Id, duration: 1, size: 1, fingerprints: new Dictionary<string, string> { ["md5"] = token }, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        var wall = await AsUser(ApiTestUsers.Eva).GetVideoWallAsync($"Wall {token}", 2, TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).AssertResponseAsync(
            $"/api/videos/duplicates?matchType=title&distance=0",
            global::System.Net.HttpStatusCode.Gone,
            TestContext.Current.CancellationToken);
        var titleGroups = await AsUser(ApiTestUsers.Eva).FindDuplicateVideosAsync("title", cancellationToken: TestContext.Current.CancellationToken);
        var fingerprintGroups = await AsUser(ApiTestUsers.Eva).FindDuplicateVideosAsync("fingerprint", cancellationToken: TestContext.Current.CancellationToken);
        var remoteGroups = await AsUser(ApiTestUsers.Eva).FindDuplicateVideosAsync("remote-id", cancellationToken: TestContext.Current.CancellationToken);

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
    [CoversEndpoint("POST", "/api/videos/duplicate-searches")]
    [CoversEndpoint("GET", "/api/videos/duplicate-searches/{searchid:guid}")]
    [CoversEndpoint("GET", "/api/videos/duplicate-searches/{searchid:guid}/groups")]
    [CoversEndpoint("PATCH", "/api/videos/duplicate-searches/{searchid:guid}/groups/{groupid:int}")]
    [CoversEndpoint("POST", "/api/videos/duplicate-searches/{searchid:guid}/delete-unkept")]
    public async Task GivenDuplicateSearch_WhenKeeperChangesAndDeletionRuns_ThenResultsAndSelectionsPersist()
    {
        var owner = AsUser();
        var title = $"Durable duplicate {Guid.NewGuid():N}";
        var removed = await owner.CreateVideoAsync(title, TestContext.Current.CancellationToken);
        var keeper = await owner.CreateVideoAsync(title, TestContext.Current.CancellationToken);

        var restrictedRoleName = $"Owner-scoped duplicate search {Guid.NewGuid():N}";
        var restrictedTag = await owner.CreateTagAsync($"Owner-scoped duplicate search {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var restrictedRole = await owner.CreateRoleAsync(new CreateRoleRequest(
            restrictedRoleName,
            "Can run and delete owned duplicate searches without reading other owners' jobs.",
            [Permissions.VideosRead, Permissions.VideosDelete, Permissions.JobsRun, Permissions.JobsCancel]), TestContext.Current.CancellationToken);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            restrictedRole.Id,
            EntityKinds.Video,
            "deny",
            "tag",
            $"{{\"tagId\":{restrictedTag.Id}}}",
            "read"), TestContext.Current.CancellationToken);
        var restrictedUsername = $"owner-scoped-duplicates-{Guid.NewGuid():N}";
        const string restrictedPassword = "Owner scoped duplicate password 123!";
        await owner.CreateUserAsync(new CreateUserRequest(
            restrictedUsername,
            restrictedPassword,
            Roles: [restrictedRoleName]), TestContext.Current.CancellationToken);
        using var restrictedSession = await owner.CreateAuthSessionAsync(restrictedUsername, restrictedPassword, TestContext.Current.CancellationToken);
        var restricted = restrictedSession.Client;

        var started = await owner.StartDuplicateSearchAsync(
            new DuplicateSearchRequestDto("title", Distance: 0),
            TestContext.Current.CancellationToken);
        (await owner.WaitForTerminalJobAsync(started.JobId, TestContext.Current.CancellationToken)).Status.Should().Be(JobStatus.Completed);

        var info = await owner.GetDuplicateSearchAsync(started.SearchId, TestContext.Current.CancellationToken);
        info.Status.Should().Be("completed");
        var page = await owner.GetDuplicateSearchGroupsAsync(started.SearchId, perPage: 20, cancellationToken: TestContext.Current.CancellationToken);
        var group = page.Items.Should().ContainSingle(candidate =>
            candidate.Videos.Select(video => video.Id).ToHashSet().IsSupersetOf(new[] { removed.Id, keeper.Id })).Which;

        (await restricted.GetJobHistoryAsync(TestContext.Current.CancellationToken)).Should().NotContain(job => job.Id == started.JobId);
        await restricted.AssertResponseAsync($"/api/jobs/{started.JobId}", global::System.Net.HttpStatusCode.NotFound, TestContext.Current.CancellationToken);
        await restricted.AssertResponseAsync($"/api/videos/duplicate-searches/{started.SearchId}", global::System.Net.HttpStatusCode.NotFound, TestContext.Current.CancellationToken);
        await restricted.AssertResponseAsync($"/api/videos/duplicate-searches/{started.SearchId}/groups", global::System.Net.HttpStatusCode.NotFound, TestContext.Current.CancellationToken);
        await restricted.AssertResponseAsync(
            HttpMethod.Patch,
            $"/api/videos/duplicate-searches/{started.SearchId}/groups/{group.Id}",
            global::System.Net.HttpStatusCode.NotFound,
            new DuplicateSearchGroupDecisionDto([keeper.Id]),
            TestContext.Current.CancellationToken);
        await restricted.AssertResponseAsync(
            HttpMethod.Post,
            $"/api/videos/duplicate-searches/{started.SearchId}/delete-unkept",
            global::System.Net.HttpStatusCode.NotFound,
            new DuplicateSearchDeleteRequestDto(),
            TestContext.Current.CancellationToken);

        var restrictedStarted = await restricted.StartDuplicateSearchAsync(
            new DuplicateSearchRequestDto("title", Distance: 0),
            TestContext.Current.CancellationToken);
        (await restricted.WaitForTerminalJobAsync(restrictedStarted.JobId, TestContext.Current.CancellationToken)).Status.Should().Be(JobStatus.Completed);
        (await restricted.GetDuplicateSearchAsync(restrictedStarted.SearchId, TestContext.Current.CancellationToken)).Status.Should().Be("completed");
        var restrictedPage = await restricted.GetDuplicateSearchGroupsAsync(restrictedStarted.SearchId, perPage: 20, cancellationToken: TestContext.Current.CancellationToken);
        var restrictedGroup = restrictedPage.Items.Should().ContainSingle(candidate =>
            candidate.Videos.Select(video => video.Id).ToHashSet().IsSupersetOf(new[] { removed.Id, keeper.Id })).Which;
        await restricted.UpdateDuplicateSearchGroupDecisionAsync(
            restrictedStarted.SearchId,
            restrictedGroup.Id,
            new DuplicateSearchGroupDecisionDto([keeper.Id]),
            TestContext.Current.CancellationToken);
        var restrictedHistory = await restricted.GetJobHistoryAsync(TestContext.Current.CancellationToken);
        restrictedHistory.Should().ContainSingle(job => job.Id == restrictedStarted.JobId);
        restrictedHistory.Should().NotContain(job => job.Id == started.JobId);

        await owner.UpdateDuplicateSearchGroupDecisionAsync(
            started.SearchId,
            group.Id,
            new DuplicateSearchGroupDecisionDto([keeper.Id]),
            TestContext.Current.CancellationToken);
        var updatedPage = await owner.GetDuplicateSearchGroupsAsync(started.SearchId, perPage: 20, cancellationToken: TestContext.Current.CancellationToken);
        updatedPage.Items.Single(candidate => candidate.Id == group.Id).KeepVideoIds.Should().Equal(keeper.Id);

        var deletion = await owner.DeleteUnkeptDuplicateVideosAsync(
            started.SearchId,
            new DuplicateSearchDeleteRequestDto(),
            TestContext.Current.CancellationToken);
        (await owner.WaitForTerminalJobAsync(deletion.JobId, TestContext.Current.CancellationToken)).Status.Should().Be(JobStatus.Completed);

        var removedRead = () => owner.GetVideoByIdAsync(removed.Id);
        await removedRead.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        (await owner.GetVideoByIdAsync(keeper.Id, TestContext.Current.CancellationToken)).Id.Should().Be(keeper.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/videos/from-file")]
    [CoversEndpoint("POST", "/api/videos/{id:int}/assign-file")]
    public async Task GivenImportedFile_WhenAssignedToAnotherVideo_ThenTheNewOwnerPersistsTheFile()
    {
        // Arrange
        var path = AsTestFileSystem().CreateTextFile("A local file imported through the video API.");
        var target = await AsUser().CreateVideoAsync($"Assignment target {Guid.NewGuid():N}", TestContext.Current.CancellationToken);

        // Act
        var imported = await AsUser(ApiTestUsers.Eva).CreateVideoFromFileAsync(path, TestContext.Current.CancellationToken);
        var file = imported.Files.Should().ContainSingle().Which;
        await AsUser(ApiTestUsers.Eva).AssignVideoFileAsync(target, file.Id, TestContext.Current.CancellationToken);
        var targetAfter = await AsUser().GetVideoByIdAsync(target.Id, TestContext.Current.CancellationToken);
        var sourceAfter = await AsUser().GetVideoByIdAsync(imported.Id, TestContext.Current.CancellationToken);

        // Assert
        targetAfter.Files.Should().ContainSingle(candidate => candidate.Id == file.Id && candidate.Path == path);
        sourceAfter.Files.Should().BeEmpty();
    }

}
