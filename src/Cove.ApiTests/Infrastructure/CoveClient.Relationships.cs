using Cove.Core.DTOs;
using Cove.Core.Entities;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<GroupDto> CreateGroupAsync(
        string name,
        CancellationToken cancellationToken = default)
        => SendAsync<GroupDto>(
            HttpMethod.Post,
            "/api/groups",
            new GroupCreateDto(
                Name: name,
                Aliases: null,
                Date: null,
                Rating: null,
                StudioId: null,
                Director: null,
                Description: null,
                Urls: [],
                TagIds: []),
            cancellationToken);

    public async Task<IReadOnlyList<GroupDto>> GetGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<PaginatedResponse<GroupDto>>(
            HttpMethod.Get,
            WithCacheNonce("/api/groups?perPage=250"),
            payload: null,
            cancellationToken);
        return result.Items;
    }

    public Task<GroupItemDto> AddVideoToGroupAsync(
        VideoDto video,
        GroupDto group,
        CancellationToken cancellationToken = default)
        => SendAsync<GroupItemDto>(
            HttpMethod.Post,
            $"/api/groups/{group.Id}/items",
            new GroupItemCreateDto(
                OrderIndex: 0,
                Kind: GroupItemKind.Video,
                VideoId: video.Id,
                HostType: "video",
                HostId: video.Id,
                StartSec: null,
                EndSec: null,
                Title: null,
                Notes: null,
                SourceSpanKey: null,
                SourceProfileId: null),
            cancellationToken);

    public Task<IReadOnlyList<GroupItemDto>> GetGroupItemsAsync(
        GroupDto group,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<GroupItemDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/groups/{group.Id}/items"),
            payload: null,
            cancellationToken);

    public Task<DetectionDto> CreateImageDetectionAsync(
        ImageDto image,
        string classification,
        CancellationToken cancellationToken = default)
        => CreateDetectionAsync(
            $"/api/images/{image.Id}/detections",
            classification,
            observedAtSec: null,
            cancellationToken);

    public Task<DetectionDto> CreateImageDetectionAsync(
        ImageDto image,
        DetectionCreateDto detection,
        CancellationToken cancellationToken = default)
        => CreateDetectionAsync(
            $"/api/images/{image.Id}/detections",
            detection,
            cancellationToken);

    public Task<IReadOnlyList<DetectionDto>> GetImageDetectionsAsync(
        ImageDto image,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<DetectionDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/images/{image.Id}/detections"),
            payload: null,
            cancellationToken);

    public Task<DetectionDto> CreateVideoDetectionAsync(
        VideoDto video,
        string classification,
        CancellationToken cancellationToken = default)
        => CreateDetectionAsync(
            $"/api/videos/{video.Id}/detections",
            classification,
            observedAtSec: 2,
            cancellationToken);

    public Task<DetectionDto> CreateVideoDetectionAsync(
        VideoDto video,
        DetectionCreateDto detection,
        CancellationToken cancellationToken = default)
        => CreateDetectionAsync(
            $"/api/videos/{video.Id}/detections",
            detection,
            cancellationToken);

    public Task<IReadOnlyList<DetectionDto>> GetVideoDetectionsAsync(
        VideoDto video,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<DetectionDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/videos/{video.Id}/detections"),
            payload: null,
            cancellationToken);

    public Task<SegmentDto> CreateVideoSegmentAsync(
        VideoDto video,
        string title,
        CancellationToken cancellationToken = default)
        => SendAsync<SegmentDto>(
            HttpMethod.Post,
            $"/api/videos/{video.Id}/segments",
            new SegmentCreateDto(
                StartSec: 2,
                EndSec: 5,
                TagId: null,
                Kind: "chapter",
                RefId: null,
                Payload: null,
                SourceKey: "api-test",
                SourceRunId: null,
                Confidence: null,
                Title: title,
                ColorHint: null),
            cancellationToken);

    public Task<IReadOnlyList<SegmentDto>> GetVideoSegmentsAsync(
        VideoDto video,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<SegmentDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/videos/{video.Id}/segments"),
            payload: null,
            cancellationToken);

    private Task<DetectionDto> CreateDetectionAsync(
        string requestUri,
        string classification,
        double? observedAtSec,
        CancellationToken cancellationToken)
        => CreateDetectionAsync(
            requestUri,
            new DetectionCreateDto(
                ObservedAtSec: observedAtSec,
                FrameWidth: 100,
                FrameHeight: 100,
                Class: classification,
                Score: 0.95f,
                X: 0.1f,
                Y: 0.2f,
                W: 0.3f,
                H: 0.4f,
                Extra: null,
                RefKind: null,
                RefId: null,
                GroupKey: null,
                SourceKey: "api-test",
                SourceRunId: null),
            cancellationToken);

    private Task<DetectionDto> CreateDetectionAsync(
        string requestUri,
        DetectionCreateDto detection,
        CancellationToken cancellationToken)
        => SendAsync<DetectionDto>(
            HttpMethod.Post,
            requestUri,
            detection,
            cancellationToken);
}
