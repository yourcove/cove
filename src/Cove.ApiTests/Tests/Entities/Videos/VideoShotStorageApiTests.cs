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
        var validFileId = await CreateFileAsync(ct);
        await AsDbUser().WriteVideoShotsAsync(Write(fileId, 5), ct);

        Task<string?> InsertAsync(
            int file, string duration = "10", int shots = 2, string cuts = "{4}", string? cutFrames = null,
            string frameCount = "NULL", string types = "{NULL,NULL}", string transitions = "{NULL}")
            => AsDbUser().TryExecuteSqlAsync(
                "INSERT INTO video_shot_sets (\"FileId\", \"SourceKey\", \"DurationSec\", \"ShotCount\", \"CutTimes\", \"CutFrames\", \"FrameCount\", " +
                "\"ShotTypes\", \"Transitions\", \"Revision\", \"FileSize\", \"CreatedAt\", \"UpdatedAt\") " +
                $"VALUES ({file}, 'raw', '{duration}', {shots}, '{cuts}', {(cutFrames is null ? "NULL" : $"'{cutFrames}'")}, {frameCount}, " +
                $"'{types}', '{transitions}', 1, 0, now(), now())", ct);

        // The set's own columns.
        (await InsertAsync(otherFileId, duration: "NaN")).Should().Be("23514");
        (await InsertAsync(otherFileId, duration: "Infinity")).Should().Be("23514");
        (await InsertAsync(otherFileId, shots: 0, cuts: "{}", types: "{}", transitions: "{}")).Should().Be("23514");
        // Array lengths and shapes.
        (await InsertAsync(otherFileId, cuts: "{}")).Should().Be("23514");
        (await InsertAsync(otherFileId, types: "{NULL}")).Should().Be("23514");
        (await InsertAsync(otherFileId, transitions: "{}")).Should().Be("23514");
        (await InsertAsync(otherFileId, cuts: "{{4}}")).Should().Be("23514");
        (await InsertAsync(otherFileId, cutFrames: "{100,200}", frameCount: "250")).Should().Be("23514");
        // Cut times: finite, inside the set, strictly increasing.
        (await InsertAsync(otherFileId, cuts: "{NULL}")).Should().Be("23514");
        (await InsertAsync(otherFileId, cuts: "{NaN}")).Should().Be("23514");
        (await InsertAsync(otherFileId, cuts: "{0}")).Should().Be("23514");
        (await InsertAsync(otherFileId, cuts: "{10}")).Should().Be("23514");
        (await InsertAsync(otherFileId, shots: 3, cuts: "{6,4}", types: "{NULL,NULL,NULL}", transitions: "{NULL,NULL}")).Should().Be("23514");
        (await InsertAsync(otherFileId, shots: 3, cuts: "{4,4}", types: "{NULL,NULL,NULL}", transitions: "{NULL,NULL}")).Should().Be("23514");
        // Cut frames: only with a frame count, inside it, strictly increasing.
        (await InsertAsync(otherFileId, cutFrames: "{100}")).Should().Be("23514");
        (await InsertAsync(otherFileId, cutFrames: "{250}", frameCount: "250")).Should().Be("23514");
        (await InsertAsync(otherFileId, cutFrames: "{0}", frameCount: "250")).Should().Be("23514");
        (await InsertAsync(otherFileId, shots: 3, cuts: "{4,6}", cutFrames: "{150,100}", frameCount: "250", types: "{NULL,NULL,NULL}", transitions: "{NULL,NULL}")).Should().Be("23514");
        // Labels keep their length limit.
        (await InsertAsync(otherFileId, types: $"{{{new string('a', 101)},NULL}}")).Should().Be("22001");

        (await InsertAsync(validFileId, cutFrames: "{100}", frameCount: "250", types: "{General,NULL}", transitions: "{Hard_Cut}")).Should().BeNull();
        (await InsertAsync(fileId)).Should().Be("23505");
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
