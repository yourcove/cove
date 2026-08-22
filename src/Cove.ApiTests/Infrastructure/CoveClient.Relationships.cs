using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Infrastructure;

public sealed record GroupBulkDeleteResponse(int Deleted, int Skipped);

public sealed partial class CoveClient
{
    public Task<GroupItemDto> CreateGroupItemAsync(
        int groupId,
        GroupItemCreateDto request,
        CancellationToken cancellationToken = default)
        => SendAsync<GroupItemDto>(
            HttpMethod.Post,
            $"/api/groups/{groupId}/items",
            request,
            cancellationToken);

    public Task<GroupDto> CreateGroupAsync(
        string name,
        CancellationToken cancellationToken = default)
        => CreateGroupAsync(
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

    public Task<GroupDto> CreateGroupAsync(
        GroupCreateDto request,
        CancellationToken cancellationToken = default)
        => SendAsync<GroupDto>(HttpMethod.Post, "/api/groups", request, cancellationToken);

    public Task<GroupDto> CreateCompilationAsync(
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
                TagIds: [],
                ShowInVideoLists: true),
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

    public Task<GroupDto> GetGroupByIdAsync(
        int groupId,
        CancellationToken cancellationToken = default)
        => SendAsync<GroupDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/groups/{groupId}"),
            payload: null,
            cancellationToken);

    public Task<GroupDto> UpdateGroupAsync(
        int groupId,
        GroupUpdateDto request,
        CancellationToken cancellationToken = default)
        => SendAsync<GroupDto>(HttpMethod.Put, $"/api/groups/{groupId}", request, cancellationToken);

    public Task<PaginatedResponse<GroupDto>> FindGroupsAsync(
        FilteredQueryRequest<GroupFilter> request,
        CancellationToken cancellationToken = default)
        => SendAsync<PaginatedResponse<GroupDto>>(HttpMethod.Post, "/api/groups/find", request, cancellationToken);

    public async Task<int> BulkUpdateGroupsAsync(
        BulkGroupUpdateDto request,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(HttpMethod.Post, "/api/groups/bulk", request, cancellationToken);
        return response.GetProperty("updated").GetInt32();
    }

    public Task<EntityEngagementDto> SetGroupRatingAsync(
        GroupDto group,
        int rating,
        CancellationToken cancellationToken = default)
        => SendAsync<EntityEngagementDto>(
            HttpMethod.Put,
            $"/api/engagement/{AffinityHostType.Group}/{group.Id}/rating",
            new VideoRatingDto(rating, "overall"),
            cancellationToken);

    public Task DeleteGroupAsync(
        int groupId,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(HttpMethod.Delete, $"/api/groups/{groupId}", new { }, cancellationToken);

    public async Task AddSubGroupAsync(
        int groupId,
        AddSubGroupDto request,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"/api/groups/{groupId}/subgroups";
        using var response = await _client.PostAsJsonAsync(requestUri, request, ApiJson.Options, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.OK)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"POST {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
    }

    public Task<IReadOnlyList<GroupDto>> GetSubGroupsAsync(
        int groupId,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<GroupDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/groups/{groupId}/subgroups"),
            payload: null,
            cancellationToken);

    public Task<IReadOnlyList<GroupDto>> GetContainingGroupsAsync(
        int groupId,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<GroupDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/groups/{groupId}/containinggroups"),
            payload: null,
            cancellationToken);

    public Task RemoveSubGroupAsync(
        int groupId,
        int subGroupId,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(
            HttpMethod.Delete,
            $"/api/groups/{groupId}/subgroups/{subGroupId}",
            new { },
            cancellationToken);

    public Task<IReadOnlyList<DynamicGroupSourceDto>> GetDynamicGroupSourcesAsync(
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<DynamicGroupSourceDto>>(
            HttpMethod.Get,
            WithCacheNonce("/api/groups/dynamic-sources"),
            payload: null,
            cancellationToken);

    public Task UpdateGroupQueryAsync(
        int groupId,
        GroupQueryUpdateDto request,
        CancellationToken cancellationToken = default)
        => SendForOkAsync(
            HttpMethod.Put,
            $"/api/groups/{groupId}/query",
            request,
            cancellationToken);

    public Task SnapshotGroupAsync(
        int groupId,
        CancellationToken cancellationToken = default)
        => SendForOkAsync(
            HttpMethod.Post,
            $"/api/groups/{groupId}/snapshot",
            new { },
            cancellationToken);

    public Task ReorderGroupsAsync(
        GroupItemsReorderDto request,
        CancellationToken cancellationToken = default)
        => SendForOkAsync(HttpMethod.Put, "/api/groups/reorder", request, cancellationToken);

    public Task ReorderSubGroupsAsync(
        int groupId,
        ReorderSubGroupsDto request,
        CancellationToken cancellationToken = default)
        => SendForOkAsync(
            HttpMethod.Put,
            $"/api/groups/{groupId}/subgroups/reorder",
            request,
            cancellationToken);

    public Task<IReadOnlyList<GroupItemDto>> CreateGroupItemsFromSpansAsync(
        int groupId,
        GroupItemsFromSpansDto request,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<GroupItemDto>>(
            HttpMethod.Post,
            $"/api/groups/{groupId}/items/from-spans",
            request,
            cancellationToken);

    public async Task<GroupBulkDeleteResponse> BulkDeleteGroupsAsync(
        BatchDeleteDto request,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Delete,
            "/api/groups/bulk",
            request,
            cancellationToken);
        return new GroupBulkDeleteResponse(
            response.GetProperty("deleted").GetInt32(),
            response.GetProperty("skipped").GetInt32());
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

    public Task<GroupItemDto> AddPerformerToGroupAsync(
        PerformerDto performer,
        GroupDto group,
        CancellationToken cancellationToken = default)
        => SendAsync<GroupItemDto>(
            HttpMethod.Post,
            $"/api/groups/{group.Id}/items",
            new GroupItemCreateDto(
                OrderIndex: 0,
                Kind: GroupItemKind.Performer,
                VideoId: null,
                HostType: "performer",
                HostId: performer.Id,
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

    public Task<PaginatedResponse<GroupItemDto>> GetGroupItemsPageAsync(
        int groupId,
        int page = 1,
        int perPage = 40,
        string? sort = null,
        string? direction = null,
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>
        {
            $"page={page}",
            $"perPage={perPage}",
        };
        if (!string.IsNullOrWhiteSpace(sort))
            parameters.Add($"sort={Uri.EscapeDataString(sort)}");
        if (!string.IsNullOrWhiteSpace(direction))
            parameters.Add($"direction={Uri.EscapeDataString(direction)}");
        if (!string.IsNullOrWhiteSpace(query))
            parameters.Add($"q={Uri.EscapeDataString(query)}");

        return SendAsync<PaginatedResponse<GroupItemDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/groups/{groupId}/items/page?{string.Join('&', parameters)}"),
            payload: null,
            cancellationToken);
    }

    public Task<GroupItemDto> UpdateGroupItemAsync(
        int groupId,
        int itemId,
        GroupItemUpdateDto update,
        CancellationToken cancellationToken = default)
        => SendAsync<GroupItemDto>(
            HttpMethod.Put,
            $"/api/groups/{groupId}/items/{itemId}",
            update,
            cancellationToken);

    public async Task DeleteGroupItemAsync(
        int groupId,
        int itemId,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"/api/groups/{groupId}/items/{itemId}";
        using var response = await _client.DeleteAsync(requestUri, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.NoContent)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"DELETE {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
    }

    public async Task<int> RemoveGroupItemHostsAsync(
        int groupId,
        GroupItemsRemoveHostsDto request,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Post,
            $"/api/groups/{groupId}/items/remove-hosts",
            request,
            cancellationToken);
        return response.GetProperty("removed").GetInt32();
    }

    public async Task ReorderGroupItemsAsync(
        int groupId,
        GroupItemsReorderDto reorder,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"/api/groups/{groupId}/items/reorder";
        using var request = new HttpRequestMessage(HttpMethod.Put, requestUri)
        {
            Content = JsonContent.Create(reorder, options: ApiJson.Options),
        };
        using var response = await _client.SendAsync(request, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.OK)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"PUT {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
    }

    public Task<GroupPlaybackManifestDto> GetGroupPlaybackManifestAsync(
        int groupId,
        CancellationToken cancellationToken = default)
        => SendAsync<GroupPlaybackManifestDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/groups/{groupId}/playback-manifest"),
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

    public Task<DetectionDto> GetImageDetectionAsync(
        ImageDto image,
        int detectionId,
        CancellationToken cancellationToken = default)
        => SendAsync<DetectionDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/images/{image.Id}/detections/{detectionId}"),
            payload: null,
            cancellationToken);

    public Task<DetectionDto> UpdateImageDetectionAsync(
        ImageDto image,
        int detectionId,
        DetectionUpdateDto detection,
        CancellationToken cancellationToken = default)
        => SendAsync<DetectionDto>(
            HttpMethod.Put,
            $"/api/images/{image.Id}/detections/{detectionId}",
            detection,
            cancellationToken);

    public Task DeleteImageDetectionAsync(
        ImageDto image,
        int detectionId,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(
            HttpMethod.Delete,
            $"/api/images/{image.Id}/detections/{detectionId}",
            new { },
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

    public Task<DetectionDto> GetVideoDetectionAsync(
        VideoDto video,
        int detectionId,
        CancellationToken cancellationToken = default)
        => SendAsync<DetectionDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/videos/{video.Id}/detections/{detectionId}"),
            payload: null,
            cancellationToken);

    public Task<DetectionDto> UpdateVideoDetectionAsync(
        VideoDto video,
        int detectionId,
        DetectionUpdateDto detection,
        CancellationToken cancellationToken = default)
        => SendAsync<DetectionDto>(
            HttpMethod.Put,
            $"/api/videos/{video.Id}/detections/{detectionId}",
            detection,
            cancellationToken);

    public Task DeleteVideoDetectionAsync(
        VideoDto video,
        int detectionId,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(
            HttpMethod.Delete,
            $"/api/videos/{video.Id}/detections/{detectionId}",
            new { },
            cancellationToken);

    public Task<IReadOnlyList<TagApplicationDto>> GetTagApplicationsAsync(
        string? hostType = null,
        int? hostId = null,
        string? contextType = null,
        int? contextId = null,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(hostType)) query.Add($"hostType={Uri.EscapeDataString(hostType)}");
        if (hostId.HasValue) query.Add($"hostId={hostId.Value}");
        if (!string.IsNullOrWhiteSpace(contextType)) query.Add($"contextType={Uri.EscapeDataString(contextType)}");
        if (contextId.HasValue) query.Add($"contextId={contextId.Value}");
        var requestUri = "/api/tagapplications" + (query.Count == 0 ? string.Empty : "?" + string.Join("&", query));
        return SendAsync<IReadOnlyList<TagApplicationDto>>(
            HttpMethod.Get,
            WithCacheNonce(requestUri),
            payload: null,
            cancellationToken);
    }

    public async Task<TagApplicationDto> CreateTagApplicationAsync(
        TagApplicationCreateDto application,
        CancellationToken cancellationToken = default)
    {
        const string requestUri = "/api/tagapplications";
        using var response = await _client.PostAsJsonAsync(requestUri, application, ApiJson.Options, cancellationToken);
        if (response.StatusCode is not HttpStatusCode.Created)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"POST {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
        }

        return await ApiResponse.ReadAsync<TagApplicationDto>(response, $"POST {requestUri}", cancellationToken);
    }

    public Task DeleteTagApplicationAsync(
        int applicationId,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(
            HttpMethod.Delete,
            $"/api/tagapplications/{applicationId}",
            new { },
            cancellationToken);

    public Task DeleteHostTagApplicationsAsync(
        string hostType,
        int hostId,
        int tagId,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(
            HttpMethod.Delete,
            $"/api/tagapplications/host/{Uri.EscapeDataString(hostType)}/{hostId}/tag/{tagId}",
            new { },
            cancellationToken);

    public Task<SegmentDto> CreateVideoSegmentAsync(
        VideoDto video,
        string title,
        CancellationToken cancellationToken = default)
        => CreateVideoSegmentAsync(
            video,
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

    public Task<SegmentDto> CreateVideoSegmentAsync(
        VideoDto video,
        SegmentCreateDto segment,
        CancellationToken cancellationToken = default)
        => SendAsync<SegmentDto>(
            HttpMethod.Post,
            $"/api/videos/{video.Id}/segments",
            segment,
            cancellationToken);

    public Task<IReadOnlyList<SegmentDto>> GetVideoSegmentsAsync(
        VideoDto video,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<SegmentDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/videos/{video.Id}/segments"),
            payload: null,
            cancellationToken);

    public Task<SegmentRecordDto> GetSegmentByIdAsync(
        int segmentId,
        CancellationToken cancellationToken = default)
        => SendAsync<SegmentRecordDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/segments/{segmentId}"),
            payload: null,
            cancellationToken);

    public async Task<int> RemoveTagFromSegmentsAsync(
        int tagId,
        IReadOnlyList<int>? segmentIds,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Post,
            "/api/segments/bulk/remove-tag",
            new { tagId, ids = segmentIds },
            cancellationToken);
        return response.GetProperty("count").GetInt32();
    }

    public Task<IReadOnlyList<SegmentDistinctValueDto>> GetDistinctSegmentSourceKeysAsync(
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<SegmentDistinctValueDto>>(
            HttpMethod.Get,
            WithCacheNonce("/api/segments/source-keys/distinct"),
            payload: null,
            cancellationToken);

    public Task<IReadOnlyList<SegmentDistinctValueDto>> GetDistinctSegmentKindsAsync(
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<SegmentDistinctValueDto>>(
            HttpMethod.Get,
            WithCacheNonce("/api/segments/kinds/distinct"),
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
