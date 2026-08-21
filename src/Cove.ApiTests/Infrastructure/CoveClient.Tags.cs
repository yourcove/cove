using System.Net.Http.Json;
using System.Text.Json;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<TagDetailDto> CreateTagAsync(
        TagCreateDto tag,
        CancellationToken cancellationToken = default)
        => SendAsync<TagDetailDto>(HttpMethod.Post, "/api/tags", tag, cancellationToken);

    public Task<TagGroupDto> CreateTagGroupAsync(
        TagGroupCreateDto tagGroup,
        CancellationToken cancellationToken = default)
        => SendAsync<TagGroupDto>(HttpMethod.Post, "/api/taggroups", tagGroup, cancellationToken);

    public Task<IReadOnlyList<TagGroupDto>> GetTagGroupsAsync(
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<TagGroupDto>>(
            HttpMethod.Get,
            WithCacheNonce("/api/taggroups"),
            payload: null,
            cancellationToken);

    public Task<TagGroupDto> GetTagGroupByIdAsync(
        int tagGroupId,
        CancellationToken cancellationToken = default)
        => SendAsync<TagGroupDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/taggroups/{tagGroupId}"),
            payload: null,
            cancellationToken);

    public Task<TagGroupDto> UpdateTagGroupAsync(
        int tagGroupId,
        TagGroupUpdateDto tagGroup,
        CancellationToken cancellationToken = default)
        => SendAsync<TagGroupDto>(
            HttpMethod.Put,
            $"/api/taggroups/{tagGroupId}",
            tagGroup,
            cancellationToken);

    public async Task DeleteTagGroupAsync(
        int tagGroupId,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"/api/taggroups/{tagGroupId}";
        using var response = await _client.DeleteAsync(requestUri, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.NoContent)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"DELETE {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
    }

    public Task<TagDetailDto> CreateTagAsync(
        string name,
        CancellationToken cancellationToken = default)
        => CreateTagAsync(
            new TagCreateDto(
                Name: name,
                SortName: null,
                Description: null,
                Favorite: false,
                Aliases: [],
                ParentIds: [],
                ChildIds: []),
            cancellationToken);

    public Task<TagDetailDto> GetTagByIdAsync(
        int tagId,
        CancellationToken cancellationToken = default)
        => SendAsync<TagDetailDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/tags/{tagId}"),
            payload: null,
            cancellationToken);

    public Task<TagDetailDto> UpdateTagAsync(
        int tagId,
        TagUpdateDto tag,
        CancellationToken cancellationToken = default)
        => SendAsync<TagDetailDto>(
            HttpMethod.Put,
            $"/api/tags/{tagId}",
            tag,
            cancellationToken);

    public async Task<IReadOnlyList<TagListDto>> GetTagsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<PaginatedResponse<TagListDto>>(
            HttpMethod.Get,
            WithCacheNonce("/api/tags?perPage=250"),
            payload: null,
            cancellationToken);
        return result.Items;
    }

    public Task<PaginatedResponse<TagListDto>> FindTagsAsync(
        FilteredQueryRequest<TagFilter> request,
        CancellationToken cancellationToken = default)
        => SendAsync<PaginatedResponse<TagListDto>>(HttpMethod.Post, "/api/tags/find", request, cancellationToken);

    public Task<TagGraphResponseDto> GetTagGraphAsync(
        FilteredQueryRequest<TagFilter> request,
        CancellationToken cancellationToken = default)
        => SendAsync<TagGraphResponseDto>(HttpMethod.Post, "/api/tags/graph", request, cancellationToken);

    public Task<IReadOnlyList<TagSegmentWallDto>> GetTagSegmentsAsync(
        int tagId,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<TagSegmentWallDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/tags/{tagId}/segments"),
            payload: null,
            cancellationToken);

    public Task<IReadOnlyList<string>> GetTagSegmentTitlesAsync(
        string query,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<string>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/tags/segment-titles?q={Uri.EscapeDataString(query)}"),
            payload: null,
            cancellationToken);

    public async Task<int> BulkUpdateTagsAsync(
        BulkTagUpdateDto request,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(HttpMethod.Post, "/api/tags/bulk", request, cancellationToken);
        return response.GetProperty("updated").GetInt32();
    }

    public Task<EntityEngagementDto> SetTagRatingAsync(
        TagDetailDto tag,
        int rating,
        CancellationToken cancellationToken = default)
        => SendAsync<EntityEngagementDto>(
            HttpMethod.Put,
            $"/api/engagement/{AffinityHostType.Tag}/{tag.Id}/rating",
            new VideoRatingDto(rating, "overall"),
            cancellationToken);

    public async Task<int> BulkDeleteTagsAsync(
        BatchDeleteDto request,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(HttpMethod.Delete, "/api/tags/bulk", request, cancellationToken);
        return response.GetProperty("deleted").GetInt32();
    }

    public async Task DeleteTagAsync(
        TagDetailDto tag,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"/api/tags/{tag.Id}";
        using var response = await _client.DeleteAsync(requestUri, cancellationToken);
        if (response.StatusCode is not System.Net.HttpStatusCode.NoContent)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"DELETE {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
        }
    }

    public async Task<System.Net.HttpStatusCode> TryDeleteTagAsync(
        TagDetailDto tag,
        CancellationToken cancellationToken = default)
    {
        using var response = await _client.DeleteAsync($"/api/tags/{tag.Id}", cancellationToken);
        return response.StatusCode;
    }

    public async Task<System.Net.HttpStatusCode> TryBulkDeleteTagsAsync(
        IReadOnlyCollection<TagDetailDto> tags,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/tags/bulk")
        {
            Content = JsonContent.Create(
                new { ids = tags.Select(tag => tag.Id).ToArray() },
                options: ApiJson.Options),
        };
        using var response = await _client.SendAsync(request, cancellationToken);
        return response.StatusCode;
    }

    public Task<TagDetailDto> MergeTagAsync(
        TagDetailDto source,
        TagDetailDto target,
        CancellationToken cancellationToken = default)
        => SendAsync<TagDetailDto>(
            HttpMethod.Post,
            "/api/tags/merge",
            new { targetId = target.Id, sourceIds = new[] { source.Id } },
            cancellationToken);

    public async Task<System.Net.HttpStatusCode> TryMergeTagAsync(
        TagDetailDto source,
        TagDetailDto target,
        CancellationToken cancellationToken = default)
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/tags/merge",
            new { targetId = target.Id, sourceIds = new[] { source.Id } },
            ApiJson.Options,
            cancellationToken);
        return response.StatusCode;
    }

    public async Task<bool> TagExistsAsync(
        int tagId,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"/api/tags/{tagId}?apiTestNonce={Guid.NewGuid():N}";
        using var response = await _client.GetAsync(requestUri, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.OK)
            return true;
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound)
            return false;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"GET {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
    }
}
