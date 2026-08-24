using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Metadata;

[Collection(ApiTestLane1Collection.Name)]
public sealed class MetadataReconciliationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/metadata/identify")]
    public async Task GivenFingerprintMatchedMetadataScene_WhenIdentifyCompletes_ThenRemoteMetadataIsPersisted()
    {
        var member = AsUser(ApiTestUsers.Eva);
        var token = Guid.NewGuid().ToString("N");
        const string matchingMd5 = "aabbccddeeff00112233445566778899";
        var metadataScene = AsMetadataService().CreateScene(
            new MetadataServiceSceneBuilder()
                .WithId($"remote-{token}")
                .WithTitle($"Identified title {token}")
                .WithTag($"Identified tag {token}")
                .WithFingerprint("MD5", matchingMd5)
                .Build());
        var video = await member.CreateVideoAsync($"Unidentified title {token}", TestContext.Current.CancellationToken);
        var control = await member.CreateVideoAsync($"Excluded title {token}", TestContext.Current.CancellationToken);
        foreach (var candidate in new[] { video, control })
        {
            for (var index = 0; index < 4; index++)
            {
                await AsDbUser().AttachVideoFileAsync(candidate.Id, duration: 1, size: 1, fingerprints: new Dictionary<string, string> { ["md5"] = matchingMd5 }, cancellationToken: TestContext.Current.CancellationToken);
            }
        }

        var jobId = await member.StartMetadataIdentifyAsync(new IdentifyOptionsDto
        {
            VideoIds = [video.Id],
            Sources = [metadataScene.Endpoint.AbsoluteUri],
            SetCoverImage = false,
            SetTags = true,
            CreateTags = true,
        }, TestContext.Current.CancellationToken);
        var job = await member.WaitForTerminalJobAsync(jobId, TestContext.Current.CancellationToken);
        var identified = await member.GetVideoByIdAsync(video.Id, TestContext.Current.CancellationToken);
        var controlAfter = await member.GetVideoByIdAsync(control.Id, TestContext.Current.CancellationToken);

        job.Type.Should().Be("identify");
        job.Status.Should().Be(JobStatus.Completed);
        job.Error.Should().BeNull();
        identified.Title.Should().Be($"Identified title {token}");
        identified.Tags.Select(tag => tag.Name).Should().Equal($"Identified tag {token}");
        var remoteId = identified.RemoteIds.Should().ContainSingle().Which;
        remoteId.Endpoint.Should().Be(metadataScene.Endpoint.AbsoluteUri);
        remoteId.RemoteId.Should().Be(metadataScene.Id);
        controlAfter.Title.Should().Be($"Excluded title {token}");
        controlAfter.Tags.Should().BeEmpty();
        controlAfter.RemoteIds.Should().BeEmpty();
    }

    [Fact]
    [CoversEndpoint("POST", "/api/metadata/sync-fingerprints")]
    public async Task GivenSourceFingerprintMappings_WhenSyncCompletes_ThenMatchingLocalFilesAreNormalizedAndUpdated()
    {
        var member = AsUser(ApiTestUsers.Eva);
        const string firstOshash = "a1";
        const string secondOshash = "b2";
        const string controlOshash = "c3";
        const string firstPhash = "00000000000000aa";
        const string secondPhash = "00000000000000bb";
        const string controlPhash = "00000000000000cc";
        var first = await member.CreateVideoAsync($"Fingerprint sync first {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var second = await member.CreateVideoAsync($"Fingerprint sync second {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var control = await member.CreateVideoAsync($"Fingerprint sync control {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        await AsDbUser().AttachVideoFileAsync(first.Id, duration: 1, size: 1, fingerprints: new Dictionary<string, string>
        {
            ["oshash"] = firstOshash,
            ["phash"] = "stale-phash",
        }, cancellationToken: TestContext.Current.CancellationToken);
        await AsDbUser().AttachVideoFileAsync(second.Id, duration: 1, size: 1, fingerprints: new Dictionary<string, string>
        {
            ["oshash"] = secondOshash,
        }, cancellationToken: TestContext.Current.CancellationToken);
        await AsDbUser().AttachVideoFileAsync(control.Id, duration: 1, size: 1, fingerprints: new Dictionary<string, string>
        {
            ["oshash"] = controlOshash,
            ["phash"] = controlPhash,
        }, cancellationToken: TestContext.Current.CancellationToken);
        AsMetadataService().SetFingerprintSyncSource(
        [
            new MetadataServiceFingerprintSourceVideo(
            [
                new MetadataServiceFingerprintSourceEntry("oshash", firstOshash.PadLeft(16, '0')),
                new MetadataServiceFingerprintSourceEntry("phash", firstPhash),
            ]),
            new MetadataServiceFingerprintSourceVideo(
            [
                new MetadataServiceFingerprintSourceEntry("oshash", secondOshash.PadLeft(16, '0')),
                new MetadataServiceFingerprintSourceEntry("phash", secondPhash),
            ]),
        ]);

        var jobId = await member.StartMetadataFingerprintSyncAsync(new SyncFingerprintsOptionsDto
        {
            SourceUrl = AsMetadataService().Endpoint.AbsoluteUri,
            ApiKey = MetadataServiceSimulator.ApiKey,
        }, TestContext.Current.CancellationToken);
        var job = await member.WaitForTerminalJobAsync(jobId, TestContext.Current.CancellationToken);
        var firstFingerprints = (await member.GetVideoByIdAsync(first.Id, TestContext.Current.CancellationToken)).Files.Should().ContainSingle().Which.Fingerprints;
        var secondFingerprints = (await member.GetVideoByIdAsync(second.Id, TestContext.Current.CancellationToken)).Files.Should().ContainSingle().Which.Fingerprints;
        var controlFingerprints = (await member.GetVideoByIdAsync(control.Id, TestContext.Current.CancellationToken)).Files.Should().ContainSingle().Which.Fingerprints;

        job.Type.Should().Be("sync-fingerprints");
        job.Status.Should().Be(JobStatus.Completed);
        job.Error.Should().BeNull();
        ToFingerprintMap(firstFingerprints).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["oshash"] = firstOshash.PadLeft(16, '0'),
            ["phash"] = firstPhash,
        });
        ToFingerprintMap(secondFingerprints).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["oshash"] = secondOshash.PadLeft(16, '0'),
            ["phash"] = secondPhash,
        });
        ToFingerprintMap(controlFingerprints).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["oshash"] = controlOshash,
            ["phash"] = controlPhash,
        });
    }

    private static IReadOnlyDictionary<string, string> ToFingerprintMap(IEnumerable<FingerprintDto> fingerprints)
        => fingerprints.ToDictionary(fingerprint => fingerprint.Type, fingerprint => fingerprint.Value, StringComparer.OrdinalIgnoreCase);
}
