using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions.Execution;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Ai;

[Collection(ApiTestLane2Collection.Name)]
public sealed class AiArtifactLifecycleApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
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
