using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Tests.Entities.Videos;

/// <summary>
/// What only PostgreSQL can show about shot storage: concurrent writers, the database's own
/// constraints, transactions an extension owns, and identities that are never reused.
/// </summary>
public sealed class VideoShotStorageApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenTwoAnalysesOfTheSameFile_WhenWrittenConcurrently_ThenOneWinsAndTheOtherIsSkipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var fileId = await CreateFileAsync(ct);

        var outcomes = await Task.WhenAll(
            AsDbUser().WriteVideoShotsAsync(Write(fileId, 4), ct),
            AsDbUser().WriteVideoShotsAsync(Write(fileId, 6), ct));

        outcomes.Select(result => result.Outcome).Should().BeEquivalentTo([VideoShotWriteOutcome.Written, VideoShotWriteOutcome.SkippedExisting]);
    }

    [Fact]
    public async Task GivenOneVersion_WhenTwoSplitsUseItConcurrently_ThenOnlyOneApplies()
    {
        var ct = TestContext.Current.CancellationToken;
        var fileId = await CreateFileAsync(ct);
        var written = await AsDbUser().WriteVideoShotsAsync(Write(fileId, 5), ct);
        var version = new VideoShotSetVersion { SetId = written.Set!.Id, Revision = written.Set.Revision };

        var results = await Task.WhenAll(
            AsDbUser().SplitVideoShotsAsync(new VideoShotSplitRequest { FileId = fileId, AtSec = 2, Expected = version }, ct),
            AsDbUser().SplitVideoShotsAsync(new VideoShotSplitRequest { FileId = fileId, AtSec = 8, Expected = version }, ct));

        results.Select(result => result.Status).Should().BeEquivalentTo([VideoShotEditStatus.Updated, VideoShotEditStatus.Conflict]);
        results.Single(result => result.Status == VideoShotEditStatus.Conflict).Set!.Revision.Should().Be(2);
    }

    [Fact]
    public async Task GivenRowsTheServiceWouldRefuse_WhenInsertedDirectly_ThenTheDatabaseRejectsThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var fileId = await CreateFileAsync(ct);
        var otherFileId = await CreateFileAsync(ct);
        var setId = (await AsDbUser().WriteVideoShotsAsync(Write(fileId, 5), ct)).Set!.Id;
        const string columns = "\"FileId\", \"SourceKey\", \"DurationSec\", \"ShotCount\", \"Revision\", \"FileSize\", \"CreatedAt\", \"UpdatedAt\"";

        (await AsDbUser().TryExecuteSqlAsync($"INSERT INTO video_shot_sets ({columns}) VALUES ({otherFileId}, 'raw', 'NaN', 1, 1, 0, now(), now())", ct)).Should().Be("23514");
        (await AsDbUser().TryExecuteSqlAsync($"INSERT INTO video_shot_sets ({columns}) VALUES ({otherFileId}, 'raw', 'Infinity', 1, 1, 0, now(), now())", ct)).Should().Be("23514");
        (await AsDbUser().TryExecuteSqlAsync($"INSERT INTO video_shot_sets ({columns}) VALUES ({otherFileId}, 'raw', 10, 0, 1, 0, now(), now())", ct)).Should().Be("23514");
        (await AsDbUser().TryExecuteSqlAsync($"INSERT INTO video_shot_sets ({columns}) VALUES ({fileId}, 'raw', 10, 1, 1, 0, now(), now())", ct)).Should().Be("23505");
        (await AsDbUser().TryExecuteSqlAsync($"INSERT INTO video_shots (\"SetId\", \"StartSec\", \"EndSec\") VALUES ({setId}, 3, 3)", ct)).Should().Be("23514");
        (await AsDbUser().TryExecuteSqlAsync($"INSERT INTO video_shots (\"SetId\", \"StartSec\", \"EndSec\") VALUES ({setId}, -1, 3)", ct)).Should().Be("23514");
        (await AsDbUser().TryExecuteSqlAsync($"INSERT INTO video_shots (\"SetId\", \"StartSec\", \"EndSec\") VALUES ({setId}, 3, 'NaN')", ct)).Should().Be("23514");
        (await AsDbUser().TryExecuteSqlAsync($"INSERT INTO video_shots (\"SetId\", \"StartSec\", \"EndSec\", \"StartFrame\") VALUES ({setId}, 3, 4, 90)", ct)).Should().Be("23514");
        (await AsDbUser().TryExecuteSqlAsync($"INSERT INTO video_shots (\"SetId\", \"StartSec\", \"EndSec\") VALUES ({setId}, 0, 2)", ct)).Should().Be("23505");
    }

    [Fact]
    public async Task GivenAnExtensionTransaction_WhenItCommitsOrRollsBack_ThenTheShotsFollowIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var committedFileId = await CreateFileAsync(ct);
        var rolledBackFileId = await CreateFileAsync(ct);

        (await AsDbUser().WriteVideoShotsInCallerTransactionAsync(Write(committedFileId, 5), commit: true, ct)).Should().Be(VideoShotWriteOutcome.Written);
        (await AsDbUser().WriteVideoShotsInCallerTransactionAsync(Write(rolledBackFileId, 5), commit: false, ct)).Should().Be(VideoShotWriteOutcome.Written);

        (await AsDbUser().WriteVideoShotsAsync(Write(committedFileId, 5), ct)).Outcome.Should().Be(VideoShotWriteOutcome.SkippedExisting);
        (await AsDbUser().WriteVideoShotsAsync(Write(rolledBackFileId, 5), ct)).Outcome.Should().Be(VideoShotWriteOutcome.Written);
    }

    [Fact]
    public async Task GivenADeletedAndRewrittenSet_WhenEditedWithTheOldVersion_ThenTheEditConflicts()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = AsUser();
        var video = await owner.CreateVideoAsync($"Shots {Guid.NewGuid():N}", ct);
        var fileId = await AsDbUser().AttachVideoFileAsync(video.Id, duration: 10, size: 1_000, cancellationToken: ct);
        await AsDbUser().SetVideoPrimaryFileAsync(video.Id, fileId, ct);
        var first = (await AsDbUser().WriteVideoShotsAsync(Write(fileId, 5), ct)).Set!;

        await owner.DeleteVideoShotsAsync(video, cancellationToken: ct);
        var second = (await AsDbUser().WriteVideoShotsAsync(Write(fileId, 5), ct)).Set!;
        var stale = await AsDbUser().SplitVideoShotsAsync(new VideoShotSplitRequest
        {
            FileId = fileId,
            AtSec = 2,
            Expected = new VideoShotSetVersion { SetId = first.Id, Revision = first.Revision },
        }, ct);

        second.Revision.Should().Be(first.Revision);
        second.Id.Should().NotBe(first.Id);
        stale.Status.Should().Be(VideoShotEditStatus.Conflict);
    }

    [Fact]
    public async Task GivenAnEditTimeInLocalTime_WhenImported_ThenItIsStoredInUtc()
    {
        var ct = TestContext.Current.CancellationToken;
        var fileId = await CreateFileAsync(ct);
        var local = new DateTime(2026, 9, 1, 14, 0, 0, DateTimeKind.Local);

        var written = await AsDbUser().WriteVideoShotsAsync(Write(fileId, 5) with { SourceKey = "ext:segment-studio", EditedAt = local }, ct);
        var reread = await AsDbUser().WriteVideoShotsAsync(Write(fileId, 5), ct);

        written.Outcome.Should().Be(VideoShotWriteOutcome.Written);
        reread.Set!.EditedAt.Should().Be(local.ToUniversalTime());
    }

    [Fact]
    public async Task GivenAWriteInProgressOnThePrimary_WhenADuplicateWithShotsIsDeleted_ThenTheMoveWaitsAndThePrimaryKeepsItsShots()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = AsUser();
        var video = await owner.CreateVideoAsync($"Shots {Guid.NewGuid():N}", ct);
        var fingerprints = new Dictionary<string, string> { ["phash"] = "00ff00ff00ff00ff" };
        var primaryId = await AsDbUser().AttachVideoFileAsync(video.Id, duration: 10, size: 1_000, fingerprints, ct);
        var duplicateId = await AsDbUser().AttachVideoFileAsync(video.Id, duration: 10, size: 1_000, fingerprints, ct);
        await AsDbUser().SetVideoPrimaryFileAsync(video.Id, primaryId, ct);
        await AsDbUser().WriteVideoShotsAsync(Write(duplicateId, 5), ct);

        await using var write = await AsDbUser().BeginVideoShotsWriteAsync(Write(primaryId, 3), ct);
        var deletion = owner.DeleteFilesAsync(new DeleteFilesDto([duplicateId], DeleteFromDisk: false), ct);
        await AsDbUser().WaitForLockWaitAsync(ct);
        await write.CommitAsync(ct);
        await deletion;

        write.Result.Outcome.Should().Be(VideoShotWriteOutcome.Written);
        (await owner.ListVideoShotSetsAsync(video, ct)).Should().ContainSingle().Which.FileId.Should().Be(primaryId);
        (await owner.GetVideoShotsAsync(video, cancellationToken: ct)).Shots!.Select(shot => shot.StartSec).Should().Equal(0, 3);
    }

    private async Task<int> CreateFileAsync(CancellationToken ct)
    {
        var video = await AsUser().CreateVideoAsync($"Shots {Guid.NewGuid():N}", ct);
        return await AsDbUser().AttachVideoFileAsync(video.Id, duration: 10, size: 1_000, cancellationToken: ct);
    }

    private static VideoShotSetWrite Write(int fileId, double cut) => new()
    {
        FileId = fileId,
        SourceKey = "ext:ai.shots",
        DurationSec = 10,
        Shots =
        [
            new VideoShotInput { StartSec = 0, EndSec = cut },
            new VideoShotInput { StartSec = cut, EndSec = 10 },
        ],
    };
}
