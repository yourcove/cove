using System.Net;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Tests.Entities.Videos;

public sealed class VideoShotApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/videos/{videoid:int}/shots")]
    [CoversEndpoint("GET", "/api/videos/{videoid:int}/shots/sets")]
    public async Task GivenShotsOnTwoFiles_WhenRead_ThenThePrimaryFileSetAndEverySummaryAreReturned()
    {
        var ct = TestContext.Current.CancellationToken;
        var member = AsUser(ApiTestUsers.Eva);
        var video = await AsUser().CreateVideoAsync($"Shots {Guid.NewGuid():N}", ct);
        var primaryFileId = await AsDbUser().AttachVideoFileAsync(video.Id, duration: 10, size: 1_000, cancellationToken: ct);
        var otherFileId = await AsDbUser().AttachVideoFileAsync(video.Id, duration: 10, size: 2_000, cancellationToken: ct);
        await AsDbUser().SetVideoPrimaryFileAsync(video.Id, primaryFileId, ct);
        await WriteShotsAsync(primaryFileId, (0, 4, "General", null), (4, 10, "Dissolve", "Hard_Cut"));
        await WriteShotsAsync(otherFileId, (0, 10, null, null));

        var primary = await member.GetVideoShotsAsync(video, cancellationToken: ct);
        var other = await member.GetVideoShotsAsync(video, otherFileId, ct);
        var sets = await member.ListVideoShotSetsAsync(video, ct);

        primary.FileId.Should().Be(primaryFileId);
        primary.VideoId.Should().Be(video.Id);
        primary.IsPrimaryFile.Should().BeTrue();
        primary.Shots!.Select(shot => (shot.StartSec, shot.EndSec, shot.ShotType, shot.TransitionIn))
            .Should().Equal((0d, 4d, "General", (string?)null), (4d, 10d, "Dissolve", "Hard_Cut"));
        other.FileId.Should().Be(otherFileId);
        other.Shots.Should().ContainSingle();
        sets.Select(set => (set.FileId, set.IsPrimaryFile, set.ShotCount))
            .Should().Equal((primaryFileId, true, 2), (otherFileId, false, 1));
        sets.Should().AllSatisfy(set => set.Shots.Should().BeNull());
    }

    [Fact]
    [CoversEndpoint("GET", "/api/videos/{videoid:int}/shots")]
    [CoversEndpoint("DELETE", "/api/videos/{videoid:int}/shots")]
    public async Task GivenShots_WhenDeleted_ThenOnlyCallersAllowedToDeleteSegmentsCanRemoveThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var video = await owner.CreateVideoAsync($"Shots {suffix}", ct);
        var fileId = await AsDbUser().AttachVideoFileAsync(video.Id, duration: 10, size: 1_000, cancellationToken: ct);
        await AsDbUser().SetVideoPrimaryFileAsync(video.Id, fileId, ct);
        await WriteShotsAsync(fileId, (0, 5, null, null), (5, 10, null, null));
        const string password = "Shot boundary permissions 123!";
        using var reader = await CreateSessionAsync(owner, $"shot-reader-{suffix}", password, [Permissions.VideosRead, Permissions.SegmentsRead]);
        using var outsider = await CreateSessionAsync(owner, $"shot-outsider-{suffix}", password, [Permissions.VideosRead]);

        await outsider.Client.AssertResponseAsync(CoveClient.VideoShotsUri(video), HttpStatusCode.Forbidden, ct);
        await reader.Client.AssertResponseAsync(CoveClient.VideoShotsUri(video), cancellationToken: ct);
        await reader.Client.AssertResponseAsync(HttpMethod.Delete, CoveClient.VideoShotsUri(video), HttpStatusCode.Forbidden, cancellationToken: ct);

        await owner.DeleteVideoShotsAsync(video, cancellationToken: ct);

        await owner.AssertResponseAsync(CoveClient.VideoShotsUri(video), HttpStatusCode.NoContent, ct);
        await owner.AssertResponseAsync(HttpMethod.Delete, CoveClient.VideoShotsUri(video), HttpStatusCode.NotFound, cancellationToken: ct);
    }

    [Fact]
    [CoversEndpoint("GET", "/api/videos/{videoid:int}/shots")]
    [CoversEndpoint("GET", "/api/videos/{videoid:int}/shots/sets")]
    public async Task GivenNoShotsOrAnotherVideosFile_WhenRead_ThenNoContentOrNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = AsUser();
        var withoutFiles = await owner.CreateVideoAsync($"No files {Guid.NewGuid():N}", ct);
        var withoutShots = await owner.CreateVideoAsync($"No shots {Guid.NewGuid():N}", ct);
        var fileId = await AsDbUser().AttachVideoFileAsync(withoutShots.Id, duration: 10, size: 1_000, cancellationToken: ct);
        await AsDbUser().SetVideoPrimaryFileAsync(withoutShots.Id, fileId, ct);

        await owner.AssertResponseAsync(CoveClient.VideoShotsUri(withoutFiles), HttpStatusCode.NoContent, ct);
        await owner.AssertResponseAsync(CoveClient.VideoShotsUri(withoutShots), HttpStatusCode.NoContent, ct);
        (await owner.ListVideoShotSetsAsync(withoutShots, ct)).Should().BeEmpty();
        await owner.AssertResponseAsync(CoveClient.VideoShotsUri(withoutFiles, fileId), HttpStatusCode.NotFound, ct);
        await owner.AssertResponseAsync("/api/videos/2147483000/shots", HttpStatusCode.NotFound, ct);
        await owner.AssertResponseAsync("/api/videos/2147483000/shots/sets", HttpStatusCode.NotFound, ct);
    }

    [Fact]
    [CoversEndpoint("GET", "/api/videos/{videoid:int}/shots/sets")]
    public async Task GivenShotsOnTwoVideos_WhenMergedAndThenDeleted_ThenShotsFollowTheirFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = AsUser();
        var kept = await owner.CreateVideoAsync($"Kept {Guid.NewGuid():N}", ct);
        var copy = await owner.CreateVideoAsync($"Copy {Guid.NewGuid():N}", ct);
        var keptFileId = await AsDbUser().AttachVideoFileAsync(kept.Id, duration: 10, size: 1_000, cancellationToken: ct);
        var copyFileId = await AsDbUser().AttachVideoFileAsync(copy.Id, duration: 20, size: 2_000, cancellationToken: ct);
        await AsDbUser().SetVideoPrimaryFileAsync(kept.Id, keptFileId, ct);
        await AsDbUser().SetVideoPrimaryFileAsync(copy.Id, copyFileId, ct);
        await WriteShotsAsync(keptFileId, (0, 10, null, null));
        await WriteShotsAsync(copyFileId, (0, 8, null, null), (8, 20, null, null));

        await owner.MergeVideosAsync(kept, ct, copy);

        (await owner.ListVideoShotSetsAsync(kept, ct)).Select(set => (set.FileId, set.IsPrimaryFile, set.ShotCount))
            .Should().Equal((keptFileId, true, 1), (copyFileId, false, 2));

        await owner.DeleteVideoAsync(kept.Id, ct);

        (await AsDbUser().CountVideoShotSetsAsync(ct)).Should().Be(0);
    }

    [Fact]
    public async Task GivenShotsOnAnEquivalentDuplicate_WhenItIsDeletedFromTheFilesTab_ThenTheShotsMoveToThePrimaryFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = AsUser();
        var video = await owner.CreateVideoAsync($"Shots {Guid.NewGuid():N}", ct);
        var fingerprints = new Dictionary<string, string> { ["phash"] = "00ff00ff00ff00ff" };
        var primaryFileId = await AsDbUser().AttachVideoFileAsync(video.Id, duration: 10, size: 1_000, fingerprints, ct);
        var duplicateFileId = await AsDbUser().AttachVideoFileAsync(video.Id, duration: 10, size: 1_000, fingerprints, ct);
        await AsDbUser().SetVideoPrimaryFileAsync(video.Id, primaryFileId, ct);
        await WriteShotsAsync(duplicateFileId, (0, 4, null, null), (4, 10, null, null));

        await owner.DeleteFilesAsync(new DeleteFilesDto([duplicateFileId], DeleteFromDisk: false), ct);

        var set = await owner.GetVideoShotsAsync(video, cancellationToken: ct);
        (set.FileId, set.IsPrimaryFile, set.ShotCount, set.Revision).Should().Be((primaryFileId, true, 2, 2));
    }

    [Fact]
    public async Task GivenShotSets_WhenPurgedAsAiData_ThenOnlyTheSelectedSourceIsRemoved()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = AsUser();
        var video = await owner.CreateVideoAsync($"Purge {Guid.NewGuid():N}", ct);
        var aiFileId = await AsDbUser().AttachVideoFileAsync(video.Id, duration: 10, size: 1_000, cancellationToken: ct);
        var manualFileId = await AsDbUser().AttachVideoFileAsync(video.Id, duration: 10, size: 1_000, cancellationToken: ct);
        await AsDbUser().SetVideoPrimaryFileAsync(video.Id, aiFileId, ct);
        await WriteShotsAsync(aiFileId, (0, 10, null, null));
        await WriteShotsAsync(manualFileId, (0, 10, null, null), sourceKey: "user");

        var result = await owner.PurgeAiDataAsync(new AiDataPurgeRequestDto("ext:ai.shots", null, null, null, "video", video.Id, ["shotSet"]), ct);

        result.RemovedCounts["shotSet"].Should().Be(1);
        await owner.AssertResponseAsync(CoveClient.VideoShotsUri(video), HttpStatusCode.NoContent, ct);
        (await owner.ListVideoShotSetsAsync(video, ct)).Should().ContainSingle().Which.FileId.Should().Be(manualFileId);
    }

    private async Task WriteShotsAsync(int fileId, params (double Start, double End, string? ShotType, string? TransitionIn)[] shots)
        => await WriteShotsAsync(fileId, shots, "ext:ai.shots");

    private async Task WriteShotsAsync(int fileId, (double Start, double End, string? ShotType, string? TransitionIn) shot, string sourceKey)
        => await WriteShotsAsync(fileId, [shot], sourceKey);

    private async Task WriteShotsAsync(int fileId, (double Start, double End, string? ShotType, string? TransitionIn)[] shots, string sourceKey)
    {
        var result = await AsDbUser().WriteVideoShotsAsync(new VideoShotSetWrite
        {
            FileId = fileId,
            SourceKey = sourceKey,
            DurationSec = shots.Max(shot => shot.End),
            Shots = shots.Select(shot => new VideoShotInput
            {
                StartSec = shot.Start,
                EndSec = shot.End,
                ShotType = shot.ShotType,
                TransitionIn = shot.TransitionIn,
            }).ToList(),
        }, TestContext.Current.CancellationToken);
        result.Outcome.Should().Be(VideoShotWriteOutcome.Written, result.Reason);
    }

    private static async Task<CoveAuthSession> CreateSessionAsync(CoveClient owner, string username, string password, IReadOnlyList<string> permissions)
    {
        var ct = TestContext.Current.CancellationToken;
        await owner.CreateRoleAsync(new CreateRoleRequest(username, "Shot boundary permission test role.", permissions), ct);
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [username]), ct);
        return await owner.CreateAuthSessionAsync(username, password, ct);
    }
}
