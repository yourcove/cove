using System.Text.Json;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<VideoDto> CreateVideoAsync(
        string title,
        CancellationToken cancellationToken = default)
        => CreateVideoAsync(
            new VideoCreateDto(
                Title: title,
                Code: null,
                Details: null,
                Director: null,
                Date: null,
                Rating: null,
                Organized: false,
                StudioId: null,
                Captions: null,
                Urls: [],
                TagIds: [],
                PerformerIds: [],
                GalleryIds: [],
                Groups: []),
            cancellationToken);

    public Task<VideoDto> CreateVideoAsync(
        VideoCreateDto video,
        CancellationToken cancellationToken = default)
        => SendAsync<VideoDto>(HttpMethod.Post, "/api/videos", video, cancellationToken);

    public Task<VideoDto> GetVideoByIdAsync(
        int videoId,
        CancellationToken cancellationToken = default)
        => SendAsync<VideoDto>(
            HttpMethod.Get,
            $"/api/videos/{videoId}?apiTestNonce={Guid.NewGuid():N}",
            payload: null,
            cancellationToken);

    public Task<VideoDto> UpdateVideoAsync(
        int videoId,
        object update,
        CancellationToken cancellationToken = default)
        => SendAsync<VideoDto>(
            HttpMethod.Put,
            $"/api/videos/{videoId}",
            update,
            cancellationToken);

    public async Task<int> BulkUpdateVideosAsync(
        BulkVideoUpdateDto update,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Post,
            "/api/videos/bulk",
            update,
            cancellationToken);
        return response.GetProperty("updated").GetInt32();
    }

    public Task<PaginatedResponse<VideoDto>> FindVideosAsync(
        FilteredQueryRequest<VideoFilter> request,
        CancellationToken cancellationToken = default)
        => SendAsync<PaginatedResponse<VideoDto>>(
            HttpMethod.Post,
            "/api/videos/find",
            request,
            cancellationToken);

    public Task<VideoAggregate> AggregateVideosAsync(
        FilteredQueryRequest<VideoFilter> request,
        CancellationToken cancellationToken = default)
        => SendAsync<VideoAggregate>(
            HttpMethod.Post,
            "/api/videos/aggregate",
            request,
            cancellationToken);

    public async Task<int> DestroyVideosAsync(
        BatchDeleteDto request,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Post,
            "/api/videos/destroy",
            request,
            cancellationToken);
        return response.GetProperty("deleted").GetInt32();
    }

    public async Task DeleteVideoAsync(
        int videoId,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"/api/videos/{videoId}";
        using var response = await _client.DeleteAsync(requestUri, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.NoContent)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"DELETE {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
    }

    public async Task<IReadOnlyList<VideoDto>> GetVideosAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<PaginatedResponse<VideoDto>>(
            HttpMethod.Get,
            WithCacheNonce("/api/videos?perPage=250"),
            payload: null,
            cancellationToken);
        return result.Items;
    }

    public async Task<IReadOnlyList<VideoDto>> GetVideosByPerformerAsync(
        int performerId,
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<PaginatedResponse<VideoDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/videos?performerIds={performerId}&perPage=250"),
            payload: null,
            cancellationToken);
        return result.Items;
    }

    public async Task<IReadOnlyList<VideoDto>> GetVideosByStudioAsync(
        int studioId,
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<PaginatedResponse<VideoDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/videos?studioId={studioId}&perPage=250"),
            payload: null,
            cancellationToken);
        return result.Items;
    }

    public Task<VideoDto> RemoveTagFromVideoAsync(
        VideoDto video,
        TagDto tag,
        CancellationToken cancellationToken = default)
        => SendAsync<VideoDto>(
            HttpMethod.Put,
            $"/api/videos/{video.Id}",
            new
            {
                tagIds = video.Tags
                    .Where(candidate => candidate.CanRemove && candidate.Id != tag.Id)
                    .Select(candidate => candidate.Id)
                    .ToArray(),
            },
            cancellationToken);
}
