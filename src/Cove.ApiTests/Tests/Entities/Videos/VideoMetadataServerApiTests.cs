using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities.Auth;

namespace Cove.ApiTests.Tests.Entities.Videos;

public sealed class VideoMetadataServerApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/videos/{id:int}/metadata-server/search")]
    [CoversEndpoint("POST", "/api/videos/metadata-server/find-by-ids")]
    [CoversEndpoint("POST", "/api/videos/{id:int}/metadata-server/import")]
    [CoversEndpoint("POST", "/api/videos/{id:int}/metadata-server/submit-fingerprints")]
    [CoversEndpoint("POST", "/api/videos/{id:int}/metadata-server/submit-draft")]
    public async Task GivenFixtureMetadataVideo_WhenSearchImportAndSubmissionsRun_ThenPermissionsPersistenceAndPayloadsAreExact()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        const string matchingMd5 = "aabbccddeeff00112233445566778899";
        const int durationSeconds = 42;
        var metadataScene = AsMetadataService().CreateScene(
            new MetadataServiceSceneBuilder()
                .WithId($"remote-video-{suffix}")
                .WithTitle($"Remote metadata video {suffix}")
                .WithTag($"remote-tag-{suffix}", $"Remote metadata tag {suffix}")
                .WithFingerprint("MD5", matchingMd5, durationSeconds)
                .Build());
        var metadataTag = metadataScene.Scene.Tags.Should().ContainSingle().Which;
        var video = await owner.CreateVideoAsync($"Local metadata video {suffix}", TestContext.Current.CancellationToken);
        await AsDbUser().AttachVideoFileAsync(video.Id, duration: durationSeconds, size: 1_024, fingerprints: new Dictionary<string, string> { ["md5"] = matchingMd5 }, cancellationToken: TestContext.Current.CancellationToken);
        var beforeWrites = await owner.GetVideoByIdAsync(video.Id, TestContext.Current.CancellationToken);

        var noRoleUsername = $"video-metadata-no-role-{suffix}";
        var viewerUsername = $"video-metadata-viewer-{suffix}";
        const string password = "Video metadata permissions 123!";
        await owner.CreateUserAsync(new CreateUserRequest(noRoleUsername, password, Roles: []), TestContext.Current.CancellationToken);
        await owner.CreateUserAsync(new CreateUserRequest(viewerUsername, password, Roles: [BuiltinRoles.Viewer]), TestContext.Current.CancellationToken);
        using var noRoleSession = await owner.CreateAuthSessionAsync(noRoleUsername, password, TestContext.Current.CancellationToken);
        using var viewerSession = await owner.CreateAuthSessionAsync(viewerUsername, password, TestContext.Current.CancellationToken);
        var noRole = noRoleSession.Client;
        var viewer = viewerSession.Client;

        var forbiddenSearch = () => noRole.SearchVideoMetadataServiceAsync(video, metadataScene.Scene.Title, metadataScene);
        var forbiddenFindByIds = () => noRole.FindVideoMetadataServiceByIdsAsync(metadataScene, [metadataScene.Id]);
        await forbiddenSearch.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        await forbiddenFindByIds.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");

        var searchMatches = await viewer.SearchVideoMetadataServiceAsync(video, metadataScene.Scene.Title, metadataScene, TestContext.Current.CancellationToken);
        AssertVideoMatch(searchMatches.Should().ContainSingle().Which, metadataScene, metadataTag.Name, matchingMd5, expectFingerprintMatch: true);
        var foundByIds = await viewer.FindVideoMetadataServiceByIdsAsync(metadataScene, [metadataScene.Id, $"missing-{suffix}", metadataScene.Id], TestContext.Current.CancellationToken);
        AssertVideoMatch(foundByIds.Should().ContainSingle().Which, metadataScene, metadataTag.Name, matchingMd5, expectFingerprintMatch: false);

        var forbiddenWrites = new Func<Task>[]
        {
            async () => _ = await viewer.ImportVideoFromMetadataServiceAsync(video, metadataScene),
            () => viewer.SubmitVideoFingerprintsToMetadataServiceAsync(video, metadataScene),
            async () => _ = await viewer.SubmitVideoDraftToMetadataServiceAsync(video, metadataScene),
        };
        foreach (var forbiddenWrite in forbiddenWrites)
            await forbiddenWrite.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");

        AsMetadataService().FingerprintSubmissions.Should().BeEmpty();
        AsMetadataService().SceneDraftSubmissions.Should().BeEmpty();
        AssertUnchangedBeforeImport(await owner.GetVideoByIdAsync(video.Id, TestContext.Current.CancellationToken), beforeWrites);

        var imported = await owner.ImportVideoFromMetadataServiceAsync(video, metadataScene, TestContext.Current.CancellationToken);
        AssertImportedVideo(imported, metadataScene, metadataTag.Name);
        var afterImport = await owner.GetVideoByIdAsync(video.Id, TestContext.Current.CancellationToken);
        AssertImportedVideo(afterImport, metadataScene, metadataTag.Name);

        await owner.SubmitVideoFingerprintsToMetadataServiceAsync(video, metadataScene, TestContext.Current.CancellationToken);
        var fingerprintSubmission = AsMetadataService().FingerprintSubmissions.Should().ContainSingle().Which.Input;
        fingerprintSubmission.GetProperty("scene_id").GetString().Should().Be(metadataScene.Id);
        var submittedFingerprint = fingerprintSubmission.GetProperty("fingerprint");
        submittedFingerprint.GetProperty("hash").GetString().Should().Be(matchingMd5);
        submittedFingerprint.GetProperty("algorithm").GetString().Should().Be("MD5");
        submittedFingerprint.GetProperty("duration").GetInt32().Should().Be(durationSeconds);

        var draftId = await owner.SubmitVideoDraftToMetadataServiceAsync(video, metadataScene, TestContext.Current.CancellationToken);
        draftId.Should().Be("draft-1");
        var draftSubmission = AsMetadataService().SceneDraftSubmissions.Should().ContainSingle().Which;
        draftSubmission.DraftId.Should().Be(draftId);
        var draftInput = draftSubmission.Input;
        draftInput.GetProperty("id").GetString().Should().Be(metadataScene.Id);
        draftInput.GetProperty("title").GetString().Should().Be(metadataScene.Scene.Title);
        var submittedTags = draftInput.GetProperty("tags").EnumerateArray().ToArray();
        submittedTags.Should().ContainSingle();
        submittedTags.Single().GetProperty("name").GetString().Should().Be(metadataTag.Name);
        var draftFingerprints = draftInput.GetProperty("fingerprints").EnumerateArray().ToArray();
        draftFingerprints.Should().ContainSingle();
        draftFingerprints.Single().GetProperty("hash").GetString().Should().Be(matchingMd5);
        draftFingerprints.Single().GetProperty("algorithm").GetString().Should().Be("MD5");
        draftFingerprints.Single().GetProperty("duration").GetInt32().Should().Be(durationSeconds);
    }

    private static void AssertVideoMatch(
        MetadataServerVideoMatchDto match,
        MetadataServiceSceneHandle metadataScene,
        string metadataTagName,
        string matchingMd5,
        bool expectFingerprintMatch)
    {
        match.Endpoint.Should().Be(metadataScene.Endpoint.AbsoluteUri);
        match.Id.Should().Be(metadataScene.Id);
        match.Title.Should().Be(metadataScene.Scene.Title);
        match.TagNames.Should().Equal(metadataTagName);
        match.Fingerprints.Should().ContainSingle();
        match.Fingerprints.Single().Algorithm.Should().Be("MD5");
        match.Fingerprints.Single().Hash.Should().Be(matchingMd5);
        if (expectFingerprintMatch)
        {
            match.MatchCount.Should().Be(1);
            match.FingerprintAlgorithms.Should().Equal("MD5");
        }
        else
        {
            match.MatchCount.Should().Be(0);
            match.FingerprintAlgorithms.Should().BeEmpty();
        }
    }

    private static void AssertUnchangedBeforeImport(VideoDto actual, VideoDto beforeWrites)
    {
        actual.Id.Should().Be(beforeWrites.Id);
        actual.Title.Should().Be(beforeWrites.Title);
        actual.Tags.Should().BeEmpty();
        actual.RemoteIds.Should().BeEmpty();
        actual.Files.Select(file => file.Id).Should().Equal(beforeWrites.Files.Select(file => file.Id));
    }

    private static void AssertImportedVideo(
        VideoDto actual,
        MetadataServiceSceneHandle metadataScene,
        string metadataTagName)
    {
        actual.Title.Should().Be(metadataScene.Scene.Title);
        actual.Tags.Select(tag => tag.Name).Should().Equal(metadataTagName);
        var remoteId = actual.RemoteIds.Should().ContainSingle().Which;
        remoteId.Endpoint.Should().Be(metadataScene.Endpoint.AbsoluteUri);
        remoteId.RemoteId.Should().Be(metadataScene.Id);
    }
}
