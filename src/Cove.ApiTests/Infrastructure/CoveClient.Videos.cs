using System.Net;
using System.Net.Http.Json;
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

    public Task<VideoDto> CreateVideoFromFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
        => SendAsync<VideoDto>(
            HttpMethod.Post,
            "/api/videos/from-file",
            new FileBackedCreateDto(filePath),
            cancellationToken);

    public Task<VideoGenerationResult> GenerateVideoScreenshotAsync(
        int videoId,
        double? atSeconds,
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<VideoGenerationResult>(
            HttpMethod.Post,
            $"/api/videos/{videoId}/generate-screenshot",
            new { atSeconds },
            HttpStatusCode.OK,
            cancellationToken);

    public Task<VideoGenerationResult> SetVideoCoverFromFrameAsync(
        int videoId,
        double? atSeconds,
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<VideoGenerationResult>(
            HttpMethod.Post,
            $"/api/videos/{videoId}/cover/from-frame",
            new { atSeconds },
            HttpStatusCode.OK,
            cancellationToken);

    public async Task<string> RescanVideoAsync(
        int videoId,
        CancellationToken cancellationToken = default)
    {
        var response = await SendForExpectedStatusAsync<JsonElement>(
            HttpMethod.Post,
            $"/api/videos/{videoId}/rescan",
            payload: null,
            HttpStatusCode.OK,
            cancellationToken);
        return response.GetProperty("jobId").GetString()
            ?? throw new InvalidOperationException($"POST /api/videos/{videoId}/rescan did not return a job id.");
    }

    public Task<IReadOnlyList<VideoDto>> GetVideoWallAsync(
        string query,
        int count,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<VideoDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/videos/wall?q={Uri.EscapeDataString(query)}&count={count}"),
            payload: null,
            cancellationToken);

    public async Task<IReadOnlyList<IReadOnlyList<VideoDto>>> FindDuplicateVideosAsync(
        string matchType,
        int distance = 0,
        CancellationToken cancellationToken = default)
    {
        var started = await StartDuplicateSearchAsync(
            new DuplicateSearchRequestDto(matchType, distance),
            cancellationToken);
        var job = await WaitForTerminalJobAsync(started.JobId, cancellationToken);
        if (job.Status != JobStatus.Completed)
            throw new InvalidOperationException($"Duplicate search job '{started.JobId}' ended with status {job.Status}: {job.Error}");

        var groups = new List<IReadOnlyList<VideoDto>>();
        var pageNumber = 1;
        DuplicateSearchGroupPageDto page;
        do
        {
            page = await GetDuplicateSearchGroupsAsync(
                started.SearchId,
                pageNumber++,
                perPage: 20,
                cancellationToken: cancellationToken);
            groups.AddRange(page.Items.Select(group => group.Videos));
        }
        while (page.HasMore);
        return groups;
    }

    public Task<DuplicateSearchStartDto> StartDuplicateSearchAsync(
        DuplicateSearchRequestDto request,
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<DuplicateSearchStartDto>(
            HttpMethod.Post,
            "/api/videos/duplicate-searches",
            request,
            HttpStatusCode.Accepted,
            cancellationToken);

    public Task<DuplicateSearchInfoDto> GetDuplicateSearchAsync(
        Guid searchId,
        CancellationToken cancellationToken = default)
        => SendAsync<DuplicateSearchInfoDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/videos/duplicate-searches/{searchId}"),
            payload: null,
            cancellationToken);

    public Task<DuplicateSearchGroupPageDto> GetDuplicateSearchGroupsAsync(
        Guid searchId,
        int page = 1,
        int perPage = 10,
        CancellationToken cancellationToken = default)
        => SendAsync<DuplicateSearchGroupPageDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/videos/duplicate-searches/{searchId}/groups?page={page}&perPage={perPage}"),
            payload: null,
            cancellationToken);

    public Task UpdateDuplicateSearchGroupDecisionAsync(
        Guid searchId,
        int groupId,
        DuplicateSearchGroupDecisionDto request,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(
            HttpMethod.Patch,
            $"/api/videos/duplicate-searches/{searchId}/groups/{groupId}",
            request,
            cancellationToken);

    public Task<BulkDeletionJobStartResponse> DeleteUnkeptDuplicateVideosAsync(
        Guid searchId,
        DuplicateSearchDeleteRequestDto request,
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<BulkDeletionJobStartResponse>(
            HttpMethod.Post,
            $"/api/videos/duplicate-searches/{searchId}/delete-unkept",
            request,
            HttpStatusCode.Accepted,
            cancellationToken);

    public Task<PaginatedResponse<VideoListEntryDto>> GetVideosWithCompilationsAsync(
        string title,
        string sort = "title",
        string direction = "asc",
        CancellationToken cancellationToken = default)
        => SendAsync<PaginatedResponse<VideoListEntryDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/videos/with-compilations?title={Uri.EscapeDataString(title)}&sort={Uri.EscapeDataString(sort)}&direction={Uri.EscapeDataString(direction)}&perPage=250"),
            payload: null,
            cancellationToken);

    public Task<VideoDto> MergeVideosAsync(
        VideoDto target,
        CancellationToken cancellationToken,
        params VideoDto[] sources)
        => SendAsync<VideoDto>(
            HttpMethod.Post,
            "/api/videos/merge",
            new VideoMergeDto(target.Id, sources.Select(source => source.Id).ToList()),
            cancellationToken);

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

    public Task<BulkDeletionJobStartResponse> DestroyVideosAsync(
        BatchDeleteDto request,
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<BulkDeletionJobStartResponse>(
            HttpMethod.Post,
            "/api/videos/destroy",
            request,
            HttpStatusCode.Accepted,
            cancellationToken);

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

public sealed record VideoGenerationResult(bool Success);
