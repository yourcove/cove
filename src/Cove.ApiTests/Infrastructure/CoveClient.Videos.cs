using System.Text.Json;
using System.Net.Http.Json;
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

    public Task<VideoDto> CreateVideoFromFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
        => SendAsync<VideoDto>(
            HttpMethod.Post,
            "/api/videos/from-file",
            new FileBackedCreateDto(filePath),
            cancellationToken);

    public Task<IReadOnlyList<VideoDto>> GetVideoWallAsync(
        string query,
        int count,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<VideoDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/videos/wall?q={Uri.EscapeDataString(query)}&count={count}"),
            payload: null,
            cancellationToken);

    public Task<IReadOnlyList<IReadOnlyList<VideoDto>>> FindDuplicateVideosAsync(
        string matchType,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<IReadOnlyList<VideoDto>>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/videos/duplicates?matchType={Uri.EscapeDataString(matchType)}"),
            payload: null,
            cancellationToken);

    public Task<PaginatedResponse<VideoListEntryDto>> GetVideosWithCompilationsAsync(
        string title,
        CancellationToken cancellationToken = default)
        => SendAsync<PaginatedResponse<VideoListEntryDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/videos/with-compilations?title={Uri.EscapeDataString(title)}&sort=title&perPage=250"),
            payload: null,
            cancellationToken);

    public Task<VideoDto> MergeVideosAsync(
        VideoDto target,
        params VideoDto[] sources)
        => SendAsync<VideoDto>(
            HttpMethod.Post,
            "/api/videos/merge",
            new VideoMergeDto(target.Id, sources.Select(source => source.Id).ToList()),
            CancellationToken.None);

    public Task<VideoDto> MergeVideosAsync(
        VideoDto target,
        IReadOnlyList<VideoDto> sources,
        CancellationToken cancellationToken)
        => SendAsync<VideoDto>(
            HttpMethod.Post,
            "/api/videos/merge",
            new VideoMergeDto(target.Id, sources.Select(source => source.Id).ToList()),
            cancellationToken);

    public Task AssignVideoFileAsync(
        VideoDto video,
        int fileId,
        CancellationToken cancellationToken = default)
        => SendForOkAsync(
            HttpMethod.Post,
            $"/api/videos/{video.Id}/assign-file",
            new VideoAssignFileDto(fileId),
            cancellationToken);

    private async Task SendForOkAsync(
        HttpMethod method,
        string requestUri,
        object payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, requestUri)
        {
            Content = JsonContent.Create(payload, options: ApiJson.Options),
        };
        using var response = await _client.SendAsync(request, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.OK)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"{method} {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
    }

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

    public Task RecordVideoPlayAsync(
        VideoDto video,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(HttpMethod.Post, $"/api/videos/{video.Id}/play", new { }, cancellationToken);

    public Task DeleteVideoPlayAsync(
        VideoDto video,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(HttpMethod.Delete, $"/api/videos/{video.Id}/play", new { }, cancellationToken);

    public Task ResetVideoPlayAsync(
        VideoDto video,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(HttpMethod.Post, $"/api/videos/{video.Id}/play/reset", new { }, cancellationToken);

    public Task<int> AddHistoricalVideoLikeAsync(
        VideoDto video,
        DateTime at,
        CancellationToken cancellationToken = default)
        => SendAsync<int>(HttpMethod.Post, $"/api/videos/{video.Id}/like/historical", new HistoricalLikeDto(at), cancellationToken);

    public Task DeleteHistoricalVideoLikeAsync(
        VideoDto video,
        DateTime at,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(
            HttpMethod.Delete,
            $"/api/videos/{video.Id}/like/history?at={Uri.EscapeDataString(at.ToUniversalTime().ToString("O"))}",
            new { },
            cancellationToken);

    public Task DecrementVideoLikeAsync(
        VideoDto video,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(HttpMethod.Delete, $"/api/videos/{video.Id}/like", new { }, cancellationToken);

    public Task ResetVideoLikeAsync(
        VideoDto video,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(HttpMethod.Post, $"/api/videos/{video.Id}/like/reset", new { }, cancellationToken);

    public Task<int?> SetVideoRatingViaVideoAsync(
        VideoDto video,
        int? rating,
        string aspect = "overall",
        CancellationToken cancellationToken = default)
        => SendAsync<int?>(HttpMethod.Post, $"/api/videos/{video.Id}/rating", new VideoRatingDto(rating, aspect), cancellationToken);

    public Task<EntityRatingsDto> GetVideoRatingsViaVideoAsync(
        VideoDto video,
        CancellationToken cancellationToken = default)
        => SendAsync<EntityRatingsDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/videos/{video.Id}/ratings"),
            payload: null,
            cancellationToken);

    public Task ClearVideoRatingViaVideoAsync(
        VideoDto video,
        string aspect = "overall",
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(
            HttpMethod.Delete,
            $"/api/videos/{video.Id}/rating?aspect={Uri.EscapeDataString(aspect)}",
            new { },
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
