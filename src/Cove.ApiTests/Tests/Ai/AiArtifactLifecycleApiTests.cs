using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions.Execution;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Ai;

[Collection(ApiTestLane2Collection.Name)]
public sealed class AiArtifactLifecycleApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/ai-runs/{id:int}")]
    [CoversEndpoint("GET", "/api/embeddings/{id:int}")]
    [CoversEndpoint("POST", "/api/embeddings/search")]
    public async Task GivenCompletedRunAndFaceEmbeddings_WhenViewerReadsAndSearches_ThenProvenanceAndRankingAreExact()
    {
        const string kindFamily = "face.api-artifact.v1";
        const string sourceKey = "api-test:ai-artifact";
        var runKey = $"ai-artifact-{Guid.NewGuid():N}";
        var startedAt = DateTime.UtcNow.AddMinutes(-2);
        var completedAt = DateTime.UtcNow.AddMinutes(-1);
        var nearestFace = await AsUser().CreateFaceAsync(new FaceCreateDto("AI artifact nearest", null, false, null));
        var fartherFace = await AsUser().CreateFaceAsync(new FaceCreateDto("AI artifact farther", null, false, null));
        var excludedFace = await AsUser().CreateFaceAsync(new FaceCreateDto("AI artifact excluded", null, false, null));
        var runId = await AsDbUser().CreateCompletedAiRunAsync(
            runKey,
            AiRunTargetType.Face,
            nearestFace.Id,
            startedAt,
            completedAt);
        var nearestId = await AsDbUser().CreateFaceEmbeddingAsync(
            nearestFace.Id,
            [1f, 0f, 0f],
            kindFamily,
            sourceKey: sourceKey,
            sourceRunId: runKey,
            sectionIndex: 2,
            startSec: 1.25,
            endSec: 2.75,
            metaJson: """{"label":"nearest","score":0.9}""");
        var fartherId = await AsDbUser().CreateFaceEmbeddingAsync(
            fartherFace.Id,
            [0.8f, 0.2f, 0f],
            kindFamily,
            sourceKey: sourceKey,
            sourceRunId: runKey,
            sectionIndex: 3,
            startSec: 3.5,
            endSec: 4.75);
        await AsDbUser().CreateFaceEmbeddingAsync(
            excludedFace.Id,
            [0.999f, 0.001f, 0f],
            "face.api-artifact.other",
            sourceKey: sourceKey,
            sourceRunId: runKey);
        var viewerUsername = $"ai-artifact-viewer-{Guid.NewGuid():N}";
        const string viewerPassword = "AI artifact viewer password 123!";
        await AsUser().CreateUserAsync(new CreateUserRequest(
            viewerUsername,
            viewerPassword,
            Roles: [BuiltinRoles.Viewer]));
        using var viewerSession = await AsUser().CreateAuthSessionAsync(viewerUsername, viewerPassword);

        var run = await viewerSession.Client.GetAiRunAsync(runId);
        var nearest = await viewerSession.Client.GetEmbeddingAsync(nearestId);
        var results = await viewerSession.Client.SearchEmbeddingsAsync(new EmbeddingSearchRequestDto(
            QueryText: null,
            QueryVector: [1f, 0f, 0f],
            Kind: null,
            KindFamily: kindFamily,
            HostType: EmbeddingHostType.Face,
            HostId: null,
            Modality: EmbeddingModality.Face,
            IsSemantic: true,
            SourceKey: sourceKey,
            K: 2));
        using var viewerClient = viewerSession.Client.CreateHttpClient();
        using var missingRun = await viewerClient.GetAsync("/api/ai-runs/2147483647");
        using var missingEmbedding = await viewerClient.GetAsync("/api/embeddings/2147483647");
        using var emptySearch = await viewerClient.PostAsJsonAsync(
            "/api/embeddings/search",
            new EmbeddingSearchRequestDto(null, null, null, null, null, null, null, null, null));
        using var textWithoutFamily = await viewerClient.PostAsJsonAsync(
            "/api/embeddings/search",
            new EmbeddingSearchRequestDto("query", null, null, null, null, null, null, null, null));
        using var missingEncoder = await viewerClient.PostAsJsonAsync(
            "/api/embeddings/search",
            new EmbeddingSearchRequestDto(
                "query",
                null,
                null,
                $"missing.encoder.{Guid.NewGuid():N}",
                EmbeddingHostType.Face,
                null,
                EmbeddingModality.Face,
                true,
                sourceKey));

        using var assertions = new AssertionScope();
        run.Id.Should().Be(runId);
        run.RunKey.Should().Be(runKey);
        run.SourceKey.Should().Be("api-test");
        run.TargetType.Should().Be(AiRunTargetType.Face);
        run.TargetId.Should().Be(nearestFace.Id);
        run.Status.Should().Be(AiRunStatus.Completed);
        run.StartedAt.Should().BeCloseTo(startedAt, TimeSpan.FromMilliseconds(1));
        run.CompletedAt.Should().NotBeNull();
        run.CompletedAt!.Value.Should().BeCloseTo(completedAt, TimeSpan.FromMilliseconds(1));
        run.Trigger.Should().BeNull();
        run.JobId.Should().BeNull();
        run.LoadPolicy.Should().BeNull();
        run.FrameIntervalSec.Should().BeNull();
        run.Vr.Should().BeNull();
        run.Request.Should().BeNull();
        run.Models.Should().BeNull();
        run.Summary.Should().BeNull();
        run.Error.Should().BeNull();
        nearest.Id.Should().Be(nearestId);
        nearest.HostType.Should().Be(EmbeddingHostType.Face);
        nearest.HostId.Should().Be(nearestFace.Id);
        nearest.Kind.Should().Be(kindFamily);
        nearest.KindFamily.Should().Be(kindFamily);
        nearest.Modality.Should().Be(EmbeddingModality.Face);
        nearest.IsSemantic.Should().BeTrue();
        nearest.Dim.Should().Be(3);
        nearest.Vector.Should().Equal(1f, 0f, 0f);
        nearest.SectionIndex.Should().Be(2);
        nearest.StartSec.Should().Be(1.25);
        nearest.EndSec.Should().Be(2.75);
        nearest.SourceKey.Should().Be(sourceKey);
        nearest.SourceRunId.Should().Be(runKey);
        nearest.Meta.Should().NotBeNull();
        nearest.Meta!.Value.GetProperty("label").GetString().Should().Be("nearest");
        nearest.Meta.Value.GetProperty("score").GetDouble().Should().Be(0.9);
        results.Select(result => result.EmbeddingId).Should().Equal(nearestId, fartherId);
        results.Select(result => result.HostId).Should().Equal(nearestFace.Id, fartherFace.Id);
        results.Should().OnlyContain(result =>
            result.HostType == EmbeddingHostType.Face
            && result.Kind == kindFamily
            && result.KindFamily == kindFamily
            && result.Modality == EmbeddingModality.Face
            && result.IsSemantic
            && result.SourceKey == sourceKey
            && result.SourceRunId == runKey);
        results[0].SectionIndex.Should().Be(2);
        results[0].StartSec.Should().Be(1.25);
        results[0].EndSec.Should().Be(2.75);
        results[0].Distance.Should().BeApproximately(0f, 0.0001f);
        results[1].Distance.Should().BeGreaterThan(results[0].Distance);
        missingRun.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missingEmbedding.StatusCode.Should().Be(HttpStatusCode.NotFound);
        emptySearch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        textWithoutFamily.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        missingEncoder.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    [CoversEndpoint("DELETE", "/api/embeddings")]
    [CoversEndpoint("POST", "/api/ai-data/purge")]
    public async Task GivenOwnedEmbeddings_WhenDeleteAndPurgeRun_ThenPreviewPermissionsAuditAndIsolationAreExact()
    {
        const string purgeSource = "api-test:ai-purge";
        const string deleteSource = "api-test:embedding-delete";
        var purgeRun = $"purge-{Guid.NewGuid():N}";
        var deleteRun = $"delete-{Guid.NewGuid():N}";
        var purgeFace = await AsUser().CreateFaceAsync(new FaceCreateDto("AI purge target", null, false, null));
        var purgeControlFace = await AsUser().CreateFaceAsync(new FaceCreateDto("AI purge control", null, false, null));
        var deleteFace = await AsUser().CreateFaceAsync(new FaceCreateDto("Embedding delete target", null, false, null));
        var deleteControlFace = await AsUser().CreateFaceAsync(new FaceCreateDto("Embedding delete control", null, false, null));
        var purgeId = await AsDbUser().CreateFaceEmbeddingAsync(
            purgeFace.Id,
            [1f, 0f, 0f],
            "face.ai-purge.v1",
            sourceKey: purgeSource,
            sourceRunId: purgeRun);
        var purgeControlId = await AsDbUser().CreateFaceEmbeddingAsync(
            purgeControlFace.Id,
            [0f, 1f, 0f],
            "face.ai-purge.v1",
            sourceKey: purgeSource,
            sourceRunId: $"control-{Guid.NewGuid():N}");
        var deleteId = await AsDbUser().CreateFaceEmbeddingAsync(
            deleteFace.Id,
            [0f, 0f, 1f],
            "face.embedding-delete.v1",
            sourceKey: deleteSource,
            sourceRunId: deleteRun);
        var deleteControlId = await AsDbUser().CreateFaceEmbeddingAsync(
            deleteControlFace.Id,
            [0.5f, 0.5f, 0f],
            "face.embedding-delete.v1",
            sourceKey: deleteSource,
            sourceRunId: $"control-{Guid.NewGuid():N}");
        var purgeSelector = new AiDataPurgeRequestDto(
            purgeSource,
            purgeRun,
            null,
            "face",
            "face",
            purgeFace.Id,
            ["embedding"]);
        var deleteSelector = new AiDataSelectorDto(
            deleteSource,
            deleteRun,
            null,
            "face",
            "face",
            deleteFace.Id,
            ["embedding"]);
        var viewerUsername = $"ai-clear-viewer-{Guid.NewGuid():N}";
        const string viewerPassword = "AI clear viewer password 123!";
        await AsUser().CreateUserAsync(new CreateUserRequest(
            viewerUsername,
            viewerPassword,
            Roles: [BuiltinRoles.Viewer]));
        using var viewerSession = await AsUser().CreateAuthSessionAsync(viewerUsername, viewerPassword);
        using var viewerClient = viewerSession.Client.CreateHttpClient();
        using var forbiddenDeleteRequest = new HttpRequestMessage(HttpMethod.Delete, "/api/embeddings")
        {
            Content = JsonContent.Create(deleteSelector),
        };
        using var forbiddenDelete = await viewerClient.SendAsync(forbiddenDeleteRequest);
        using var forbiddenPurge = await viewerClient.PostAsJsonAsync("/api/ai-data/purge", purgeSelector);

        var auditBefore = await AsUser().GetAuditEventsAsync(AuditActions.AiDataPurge);
        var preview = await AsUser().PurgeAiDataAsync(purgeSelector with { DryRun = true });
        var auditAfterPreview = await AsUser().GetAuditEventsAsync(AuditActions.AiDataPurge);
        var purgeAfterPreview = await AsUser().GetEmbeddingAsync(purgeId);
        var purgeControlAfterPreview = await AsUser().GetEmbeddingAsync(purgeControlId);
        var deleteAfterForbidden = await AsUser().GetEmbeddingAsync(deleteId);
        var deleteControlAfterForbidden = await AsUser().GetEmbeddingAsync(deleteControlId);

        var purged = await AsUser().PurgeAiDataAsync(purgeSelector);
        var purgeAudit = await WaitForPurgeAuditAsync();
        using var ownerClient = AsUser().CreateHttpClient();
        using var missingPurged = await ownerClient.GetAsync($"/api/embeddings/{purgeId}");
        var purgeControl = await AsUser().GetEmbeddingAsync(purgeControlId);

        var deleted = await AsUser().DeleteEmbeddingsAsync(deleteSelector);
        using var missingDeleted = await ownerClient.GetAsync($"/api/embeddings/{deleteId}");
        var deleteControl = await AsUser().GetEmbeddingAsync(deleteControlId);

        using var assertions = new AssertionScope();
        forbiddenDelete.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        forbiddenPurge.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        auditBefore.Items.Should().BeEmpty();
        auditAfterPreview.Items.Should().BeEmpty();
        preview.RemovedCounts.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, int>("embedding", 1));
        purgeAfterPreview.Id.Should().Be(purgeId);
        purgeControlAfterPreview.Id.Should().Be(purgeControlId);
        deleteAfterForbidden.Id.Should().Be(deleteId);
        deleteControlAfterForbidden.Id.Should().Be(deleteControlId);
        purged.RemovedCounts.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, int>("embedding", 1));
        missingPurged.StatusCode.Should().Be(HttpStatusCode.NotFound);
        purgeControl.Id.Should().Be(purgeControlId);
        deleted.RemovedCounts.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, int>("embedding", 1));
        missingDeleted.StatusCode.Should().Be(HttpStatusCode.NotFound);
        deleteControl.Id.Should().Be(deleteControlId);
        purgeAudit.Action.Should().Be(AuditActions.AiDataPurge);
        purgeAudit.Outcome.Should().Be(AuditOutcomes.Success);
        purgeAudit.ActorUsername.Should().Be(ApiTestUsers.Owner);
        purgeAudit.ActorKind.Should().Be("user");
        purgeAudit.TargetKind.Should().Be("ai_data");
        purgeAudit.TargetId.Should().BeNull();
        purgeAudit.Detail.Should().NotBeNull();
        using var detail = JsonDocument.Parse(purgeAudit.Detail!);
        detail.RootElement.GetProperty("userId").GetInt32().Should().Be(purgeAudit.ActorUserId);
        detail.RootElement.GetProperty("selector").GetProperty("SourceKey").GetString().Should().Be(purgeSource);
        detail.RootElement.GetProperty("selector").GetProperty("SourceRunId").GetString().Should().Be(purgeRun);
        detail.RootElement.GetProperty("kindCounts").GetProperty("embedding").GetInt32().Should().Be(1);
        detail.RootElement.GetProperty("timestampUtc").GetDateTime().Should().BeCloseTo(
            purgeAudit.OccurredAt,
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GivenInvalidPurgeSelectors_WhenOwnerTargetsEmbeddings_ThenEachIsRejectedBeforeMutation()
    {
        var hostPrimary = await CreateFaceEmbeddingAsync("invalid host primary");
        var hostControl = await CreateFaceEmbeddingAsync("invalid host control");

        using var client = AsUser().CreateHttpClient();
        using var invalidHostResponse = await client.PostAsJsonAsync(
            "/api/ai-data/purge",
            new AiDataPurgeRequestDto(null, null, null, null, "not-a-host", null, ["embedding"]));
        var invalidHostBody = await invalidHostResponse.Content.ReadAsStringAsync();
        var hostPrimaryAfter = await GetStatusAsync(client, hostPrimary);
        var hostControlAfter = await GetStatusAsync(client, hostControl);

        var blankHostPrimary = await CreateFaceEmbeddingAsync("blank host primary");
        var blankHostControl = await CreateFaceEmbeddingAsync("blank host control");
        using var blankHostResponse = await client.PostAsJsonAsync(
            "/api/ai-data/purge",
            new AiDataPurgeRequestDto(null, null, null, null, " ", null, ["embedding"]));
        var blankHostPrimaryAfter = await GetStatusAsync(client, blankHostPrimary);
        var blankHostControlAfter = await GetStatusAsync(client, blankHostControl);

        var modalityPrimary = await CreateFaceEmbeddingAsync("invalid modality primary");
        var modalityControl = await CreateFaceEmbeddingAsync("invalid modality control");
        using var invalidModalityResponse = await client.PostAsJsonAsync(
            "/api/ai-data/purge",
            new AiDataPurgeRequestDto(null, null, null, "not-a-modality", "face", null, ["embedding"]));
        var invalidModalityBody = await invalidModalityResponse.Content.ReadAsStringAsync();
        var modalityPrimaryAfter = await GetStatusAsync(client, modalityPrimary);
        var modalityControlAfter = await GetStatusAsync(client, modalityControl);

        var blankModalityPrimary = await CreateFaceEmbeddingAsync("blank modality primary");
        var blankModalityControl = await CreateFaceEmbeddingAsync("blank modality control");
        using var blankModalityResponse = await client.PostAsJsonAsync(
            "/api/ai-data/purge",
            new AiDataPurgeRequestDto(null, null, null, " ", "face", null, ["embedding"]));
        var blankModalityPrimaryAfter = await GetStatusAsync(client, blankModalityPrimary);
        var blankModalityControlAfter = await GetStatusAsync(client, blankModalityControl);

        using var assertions = new AssertionScope();
        invalidHostResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"invalid host selectors must not broaden purge scope. Response: {invalidHostBody}");
        hostPrimaryAfter.Should().Be(HttpStatusCode.OK);
        hostControlAfter.Should().Be(HttpStatusCode.OK);
        blankHostResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        blankHostPrimaryAfter.Should().Be(HttpStatusCode.OK);
        blankHostControlAfter.Should().Be(HttpStatusCode.OK);
        invalidModalityResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"invalid modality selectors must not broaden purge scope. Response: {invalidModalityBody}");
        modalityPrimaryAfter.Should().Be(HttpStatusCode.OK);
        modalityControlAfter.Should().Be(HttpStatusCode.OK);
        blankModalityResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        blankModalityPrimaryAfter.Should().Be(HttpStatusCode.OK);
        blankModalityControlAfter.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GivenInvalidEmbeddingDeleteSelectors_WhenOwnerTargetsEmbeddings_ThenEachIsRejectedBeforeMutation()
    {
        using var client = AsUser().CreateHttpClient();
        var invalidHostPrimary = await CreateFaceEmbeddingAsync("invalid embedding delete host primary");
        var invalidHostControl = await CreateFaceEmbeddingAsync("invalid embedding delete host control");
        var invalidHost = await DeleteEmbeddingsAsync(client, new AiDataSelectorDto(null, null, null, null, "not-a-host", null, null));
        var invalidHostPrimaryAfter = await GetStatusAsync(client, invalidHostPrimary);
        var invalidHostControlAfter = await GetStatusAsync(client, invalidHostControl);

        var invalidModalityPrimary = await CreateFaceEmbeddingAsync("invalid embedding delete modality primary");
        var invalidModalityControl = await CreateFaceEmbeddingAsync("invalid embedding delete modality control");
        var invalidModality = await DeleteEmbeddingsAsync(client, new AiDataSelectorDto(null, null, null, "not-a-modality", "face", null, null));
        var invalidModalityPrimaryAfter = await GetStatusAsync(client, invalidModalityPrimary);
        var invalidModalityControlAfter = await GetStatusAsync(client, invalidModalityControl);

        var inapplicableHostPrimary = await CreateFaceEmbeddingAsync("inapplicable embedding delete host primary");
        var inapplicableHostControl = await CreateFaceEmbeddingAsync("inapplicable embedding delete host control");
        var inapplicableHost = await DeleteEmbeddingsAsync(client, new AiDataSelectorDto(null, null, null, null, "audio", null, null));
        var inapplicableHostPrimaryAfter = await GetStatusAsync(client, inapplicableHostPrimary);
        var inapplicableHostControlAfter = await GetStatusAsync(client, inapplicableHostControl);

        var inapplicableKindPrimary = await CreateFaceEmbeddingAsync("inapplicable embedding delete kind primary");
        var inapplicableKindControl = await CreateFaceEmbeddingAsync("inapplicable embedding delete kind control");
        var inapplicableKind = await DeleteEmbeddingsAsync(client, new AiDataSelectorDto(null, null, null, null, null, null, ["detection"]));
        var inapplicableKindPrimaryAfter = await GetStatusAsync(client, inapplicableKindPrimary);
        var inapplicableKindControlAfter = await GetStatusAsync(client, inapplicableKindControl);

        var mixedKindsPrimary = await CreateFaceEmbeddingAsync("mixed embedding delete kinds primary");
        var mixedKindsControl = await CreateFaceEmbeddingAsync("mixed embedding delete kinds control");
        var mixedKinds = await DeleteEmbeddingsAsync(client, new AiDataSelectorDto(null, null, null, null, null, null, ["embedding", "detection"]));
        var mixedKindsPrimaryAfter = await GetStatusAsync(client, mixedKindsPrimary);
        var mixedKindsControlAfter = await GetStatusAsync(client, mixedKindsControl);

        using var assertions = new AssertionScope();
        invalidHost.Should().Be(HttpStatusCode.BadRequest);
        invalidHostPrimaryAfter.Should().Be(HttpStatusCode.OK);
        invalidHostControlAfter.Should().Be(HttpStatusCode.OK);
        invalidModality.Should().Be(HttpStatusCode.BadRequest);
        invalidModalityPrimaryAfter.Should().Be(HttpStatusCode.OK);
        invalidModalityControlAfter.Should().Be(HttpStatusCode.OK);
        inapplicableHost.Should().Be(HttpStatusCode.BadRequest);
        inapplicableHostPrimaryAfter.Should().Be(HttpStatusCode.OK);
        inapplicableHostControlAfter.Should().Be(HttpStatusCode.OK);
        inapplicableKind.Should().Be(HttpStatusCode.BadRequest);
        inapplicableKindPrimaryAfter.Should().Be(HttpStatusCode.OK);
        inapplicableKindControlAfter.Should().Be(HttpStatusCode.OK);
        mixedKinds.Should().Be(HttpStatusCode.BadRequest);
        mixedKindsPrimaryAfter.Should().Be(HttpStatusCode.OK);
        mixedKindsControlAfter.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GivenNumericPurgeHostType_WhenOwnerTargetsAllKinds_ThenItIsRejectedBeforeMutation()
    {
        var primary = await CreateFaceEmbeddingAsync("numeric purge host primary");
        var control = await CreateFaceEmbeddingAsync("numeric purge host control");

        using var client = AsUser().CreateHttpClient();
        var responseStatus = await PurgeAiDataAsync(client, new AiDataPurgeRequestDto(null, null, null, null, "3", null, null));
        var primaryAfter = await GetStatusAsync(client, primary);
        var controlAfter = await GetStatusAsync(client, control);

        using var assertions = new AssertionScope();
        responseStatus.Should().Be(HttpStatusCode.BadRequest);
        primaryAfter.Should().Be(HttpStatusCode.OK);
        controlAfter.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GivenInvalidPurgeKinds_WhenOwnerTargetsEmbeddings_ThenEachIsRejectedBeforeMutation()
    {
        using var client = AsUser().CreateHttpClient();
        var unknownPrimary = await CreateFaceEmbeddingAsync("unknown purge kind primary");
        var unknownControl = await CreateFaceEmbeddingAsync("unknown purge kind control");
        var unknownKind = await PurgeAiDataAsync(client, new AiDataPurgeRequestDto(null, null, null, null, null, null, ["typo"]));
        var unknownPrimaryAfter = await GetStatusAsync(client, unknownPrimary);
        var unknownControlAfter = await GetStatusAsync(client, unknownControl);

        var mixedPrimary = await CreateFaceEmbeddingAsync("mixed purge kind primary");
        var mixedControl = await CreateFaceEmbeddingAsync("mixed purge kind control");
        var mixedKinds = await PurgeAiDataAsync(client, new AiDataPurgeRequestDto(null, null, null, null, null, null, ["embedding", "typo"]));
        var mixedPrimaryAfter = await GetStatusAsync(client, mixedPrimary);
        var mixedControlAfter = await GetStatusAsync(client, mixedControl);

        var blankPrimary = await CreateFaceEmbeddingAsync("blank purge kind primary");
        var blankControl = await CreateFaceEmbeddingAsync("blank purge kind control");
        var blankKind = await PurgeAiDataAsync(client, new AiDataPurgeRequestDto(null, null, null, null, null, null, [" "]));
        var blankPrimaryAfter = await GetStatusAsync(client, blankPrimary);
        var blankControlAfter = await GetStatusAsync(client, blankControl);

        using var assertions = new AssertionScope();
        unknownKind.Should().Be(HttpStatusCode.BadRequest);
        unknownPrimaryAfter.Should().Be(HttpStatusCode.OK);
        unknownControlAfter.Should().Be(HttpStatusCode.OK);
        mixedKinds.Should().Be(HttpStatusCode.BadRequest);
        mixedPrimaryAfter.Should().Be(HttpStatusCode.OK);
        mixedControlAfter.Should().Be(HttpStatusCode.OK);
        blankKind.Should().Be(HttpStatusCode.BadRequest);
        blankPrimaryAfter.Should().Be(HttpStatusCode.OK);
        blankControlAfter.Should().Be(HttpStatusCode.OK);
    }

    private async Task<int> CreateFaceEmbeddingAsync(string label)
    {
        var face = await AsUser().CreateFaceAsync(new FaceCreateDto($"AI artifact {label} {Guid.NewGuid():N}", null, false, null));
        return await AsDbUser().CreateFaceEmbeddingAsync(face.Id, [1f, 0f, 0f], "ai-artifact-risk.v1");
    }

    private async Task<AuditEventDto> WaitForPurgeAuditAsync()
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var page = await AsUser().GetAuditEventsAsync(AuditActions.AiDataPurge);
            if (page.Items.Count == 1)
                return page.Items[0];
            if (page.Items.Count > 1)
                throw new InvalidOperationException($"Expected one AI purge audit event, but found {page.Items.Count}.");
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException("The AI purge audit event was not persisted within two seconds.");
    }

    private static async Task<HttpStatusCode> GetStatusAsync(HttpClient client, int embeddingId)
    {
        using var response = await client.GetAsync($"/api/embeddings/{embeddingId}");
        return response.StatusCode;
    }

    private static async Task<HttpStatusCode> DeleteEmbeddingsAsync(HttpClient client, AiDataSelectorDto selector)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/embeddings")
        {
            Content = JsonContent.Create(selector),
        };
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<HttpStatusCode> PurgeAiDataAsync(HttpClient client, AiDataPurgeRequestDto request)
    {
        using var response = await client.PostAsJsonAsync("/api/ai-data/purge", request);
        return response.StatusCode;
    }
}
