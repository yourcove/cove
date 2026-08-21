using System.Net.Http.Headers;
using System.Globalization;
using System.Text.Json;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<FaceDto> CreateFaceAsync(
        FaceCreateDto face,
        CancellationToken cancellationToken = default)
        => SendAsync<FaceDto>(HttpMethod.Post, "/api/faces", face, cancellationToken);

    public Task<FaceDto> GetFaceByIdAsync(
        int faceId,
        CancellationToken cancellationToken = default)
        => SendAsync<FaceDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/faces/{faceId}"),
            payload: null,
            cancellationToken);

    public Task<FaceDto> UpdateFaceAsync(
        int faceId,
        FaceUpdateDto face,
        CancellationToken cancellationToken = default)
        => SendAsync<FaceDto>(
            HttpMethod.Put,
            $"/api/faces/{faceId}",
            face,
            cancellationToken);

    public Task<FaceDto> LinkFaceAsync(
        int faceId,
        FaceLinkDto link,
        CancellationToken cancellationToken = default)
        => SendAsync<FaceDto>(
            HttpMethod.Post,
            $"/api/faces/{faceId}/link",
            link,
            cancellationToken);

    public Task<FaceDto> SetFaceIgnoredAsync(
        int faceId,
        bool ignored,
        CancellationToken cancellationToken = default)
        => SendAsync<FaceDto>(
            HttpMethod.Post,
            $"/api/faces/{faceId}/ignore",
            new FaceIgnoreDto(ignored),
            cancellationToken);

    public Task<DetectionDto> CreateVideoFaceDetectionAsync(
        VideoDto video,
        FaceDto face,
        CancellationToken cancellationToken = default)
        => CreateVideoDetectionAsync(
            video,
            BuildFaceDetection(face, observedAtSec: 2),
            cancellationToken);

    public Task<DetectionDto> CreateImageFaceDetectionAsync(
        ImageDto image,
        FaceDto face,
        CancellationToken cancellationToken = default)
        => CreateImageDetectionAsync(
            image,
            BuildFaceDetection(face, observedAtSec: null),
            cancellationToken);

    public Task<PaginatedResponse<FaceAppearanceDto>> GetFaceAppearancesAsync(
        int faceId,
        CancellationToken cancellationToken = default)
        => SendAsync<PaginatedResponse<FaceAppearanceDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/faces/{faceId}/appearances?perPage=250"),
            payload: null,
            cancellationToken);

    public Task<IReadOnlyList<DetectionDto>> GetFaceDetectionsAsync(
        int faceId,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<DetectionDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/faces/{faceId}/detections"),
            payload: null,
            cancellationToken);

    public Task<IReadOnlyList<FaceHostFaceDto>> GetVideoFacesAsync(
        VideoDto video,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<FaceHostFaceDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/videos/{video.Id}/faces"),
            payload: null,
            cancellationToken);

    public Task<IReadOnlyList<FaceHostFaceDto>> GetImageFacesAsync(
        ImageDto image,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<FaceHostFaceDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/images/{image.Id}/faces"),
            payload: null,
            cancellationToken);

    public Task<IReadOnlyList<FaceDto>> GetPerformerFacesAsync(
        int performerId,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<FaceDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/performers/{performerId}/faces"),
            payload: null,
            cancellationToken);

    public Task<FaceDto> CreatePerformerFromFaceAsync(
        int faceId,
        FaceCreatePerformerDto performer,
        CancellationToken cancellationToken = default)
        => SendAsync<FaceDto>(
            HttpMethod.Post,
            $"/api/faces/{faceId}/create-performer",
            performer,
            cancellationToken);

    public Task<FaceDto> MergeFaceIntoAsync(
        int faceId,
        int targetFaceId,
        CancellationToken cancellationToken = default)
        => SendAsync<FaceDto>(
            HttpMethod.Post,
            $"/api/faces/{faceId}/merge-into",
            new FaceMergeDto(targetFaceId),
            cancellationToken);

    public Task<FaceDeleteImpactDto> GetFaceDeleteImpactAsync(
        int faceId,
        CancellationToken cancellationToken = default)
        => SendAsync<FaceDeleteImpactDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/faces/{faceId}/delete-impact"),
            payload: null,
            cancellationToken);

    public Task<IReadOnlyList<FaceDto>> GetUnlinkedFaceReviewAsync(
        int take = 24,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<FaceDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/faces/review/unlinked?take={take}"),
            payload: null,
            cancellationToken);

    public Task<IReadOnlyList<FaceDto>> GetAiRunFaceReviewAsync(
        DateTime? startedAt,
        DateTime? completedAt,
        int take = 12,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string> { $"take={take}" };
        if (startedAt.HasValue)
            query.Add($"startedAt={Uri.EscapeDataString(startedAt.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))}");
        if (completedAt.HasValue)
            query.Add($"completedAt={Uri.EscapeDataString(completedAt.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))}");

        return SendAsync<IReadOnlyList<FaceDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/faces/review/ai-run?{string.Join("&", query)}"),
            payload: null,
            cancellationToken);
    }

    public Task<IReadOnlyList<FaceSuggestionDto>> GetFaceSuggestionsAsync(
        int faceId,
        int maxResults = 5,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<FaceSuggestionDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/faces/{faceId}/suggestions?maxResults={maxResults}"),
            payload: null,
            cancellationToken);

    public Task<FaceBatchOperationResultDto> BatchLinkTopSuggestionAsync(
        FaceBatchLinkTopSuggestionDto request,
        CancellationToken cancellationToken = default)
        => SendAsync<FaceBatchOperationResultDto>(
            HttpMethod.Post,
            "/api/faces/batch/link-top-suggestion",
            request,
            cancellationToken);

    public Task<PaginatedResponse<FaceSimilarDto>> GetSimilarFacesAsync(
        int faceId,
        string kindFamily,
        string? query = null,
        int page = 1,
        int perPage = 18,
        int candidateCount = 80,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"/api/faces/{faceId}/similar?kindFamily={Uri.EscapeDataString(kindFamily)}&page={page}&perPage={perPage}&k={candidateCount}";
        if (!string.IsNullOrWhiteSpace(query))
            requestUri += $"&q={Uri.EscapeDataString(query)}";

        return SendAsync<PaginatedResponse<FaceSimilarDto>>(
            HttpMethod.Get,
            WithCacheNonce(requestUri),
            payload: null,
            cancellationToken);
    }

    public Task<FaceBatchOperationResultDto> BatchDeleteFacesAsync(
        IReadOnlyList<int> faceIds,
        CancellationToken cancellationToken = default)
        => SendAsync<FaceBatchOperationResultDto>(
            HttpMethod.Post,
            "/api/faces/batch/delete",
            new FaceBatchDeleteDto(faceIds),
            cancellationToken);

    public Task<FaceDto> RecordFaceSuggestionDecisionAsync(
        int faceId,
        FaceSuggestionDecisionDto decision,
        CancellationToken cancellationToken = default)
        => SendAsync<FaceDto>(
            HttpMethod.Post,
            $"/api/faces/{faceId}/suggestions/decision",
            decision,
            cancellationToken);

    public async Task UploadFaceImageAsync(
        FaceDto face,
        byte[] image,
        CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent();
        using var imageContent = new ByteArrayContent(image);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(imageContent, "file", "face.png");

        var requestUri = $"/api/faces/{face.Id}/image";
        using var response = await _client.PostAsync(requestUri, content, cancellationToken);
        _ = await ApiResponse.ReadAsync<JsonElement>(
            response,
            $"POST {requestUri}",
            cancellationToken);
    }

    public async Task<ApiBinaryContent> GetFaceImageAsync(
        FaceDto face,
        CancellationToken cancellationToken = default)
    {
        var requestUri = WithCacheNonce($"/api/faces/{face.Id}/image");
        using var response = await _client.GetAsync(requestUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"GET {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
        }

        return new ApiBinaryContent(
            await response.Content.ReadAsByteArrayAsync(cancellationToken),
            response.Content.Headers.ContentType?.MediaType);
    }

    public async Task DeleteFaceImageAsync(
        FaceDto face,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"/api/faces/{face.Id}/image";
        using var response = await _client.DeleteAsync(requestUri, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.NoContent)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"DELETE {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
    }

    public async Task DeleteFaceAsync(
        int faceId,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"/api/faces/{faceId}";
        using var response = await _client.DeleteAsync(requestUri, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.NoContent)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"DELETE {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
    }

    private static DetectionCreateDto BuildFaceDetection(FaceDto face, double? observedAtSec)
        => new(
            ObservedAtSec: observedAtSec,
            FrameWidth: 100,
            FrameHeight: 100,
            Class: "face",
            Score: 0.95f,
            X: 0.1f,
            Y: 0.2f,
            W: 0.3f,
            H: 0.4f,
            Extra: null,
            RefKind: "face",
            RefId: face.Id,
            GroupKey: null,
            SourceKey: "api-test",
            SourceRunId: null);
}
