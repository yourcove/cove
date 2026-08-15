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
        => SendAsync<DetectionDto>(
            HttpMethod.Post,
            $"/api/videos/{video.Id}/detections",
            new DetectionCreateDto(
                ObservedAtSec: 2,
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
                SourceRunId: null),
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
}
