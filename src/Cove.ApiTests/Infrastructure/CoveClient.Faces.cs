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
