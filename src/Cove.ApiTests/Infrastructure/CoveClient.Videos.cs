using Cove.Core.DTOs;

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

    public Task<BulkUpdateResult> BulkUpdateVideosAsync(
        BulkVideoUpdateDto update,
        CancellationToken cancellationToken = default)
        => SendAsync<BulkUpdateResult>(HttpMethod.Post, "/api/videos/bulk", update, cancellationToken);

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
