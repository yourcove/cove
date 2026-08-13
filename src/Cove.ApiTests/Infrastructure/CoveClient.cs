using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;

namespace Cove.ApiTests.Infrastructure;

public sealed class CoveClient : IDisposable
{
    private readonly HttpClient _client;

    internal CoveClient(string username, Uri baseAddress, string accessToken)
    {
        Username = username;
        BaseAddress = baseAddress;
        AccessToken = accessToken;
        _client = new HttpClient { BaseAddress = baseAddress };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    public string Username { get; }

    public Uri BaseAddress { get; }

    public string AccessToken { get; }

    public Task<UserDto> CreateUserAsync(
        CreateUserRequest user,
        CancellationToken cancellationToken = default)
        => SendAsync<UserDto>(HttpMethod.Post, "/api/users", user, cancellationToken);

    public Task<JsonElement> ReadEndpointAsync(
        ReadEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        var definition = ReadEndpointCatalog.Get(endpoint);

        return SendAsync<JsonElement>(
            HttpMethod.Get,
            WithCacheNonce(definition.RequestUri),
            payload: null,
            cancellationToken);
    }

    public Task<PerformerDto> CreatePerformerAsync(
        PerformerCreateDto performer,
        CancellationToken cancellationToken = default)
        => SendAsync<PerformerDto>(HttpMethod.Post, "/api/performers", performer, cancellationToken);

    public Task<CustomFieldDefinitionDto> CreateCustomFieldDefinitionAsync(
        CustomFieldDefinitionCreateDto definition,
        CancellationToken cancellationToken = default)
        => SendAsync<CustomFieldDefinitionDto>(HttpMethod.Post, "/api/custom-fields", definition, cancellationToken);

    public Task<EntityEngagementDto> GetPerformerEngagementAsync(
        PerformerDto performer,
        CancellationToken cancellationToken = default)
        => SendAsync<EntityEngagementDto>(
            HttpMethod.Get,
            $"/api/engagement/{AffinityHostType.Performer}/{performer.Id}",
            payload: null,
            cancellationToken);

    public Task<EntityEngagementDto> SetPerformerRatingAsync(
        PerformerDto performer,
        int rating,
        string aspect = "overall",
        CancellationToken cancellationToken = default)
        => SendAsync<EntityEngagementDto>(
            HttpMethod.Put,
            $"/api/engagement/{AffinityHostType.Performer}/{performer.Id}/rating",
            new VideoRatingDto(rating, aspect),
            cancellationToken);

    public Task<EntityRatingsDto> GetPerformerRatingsAsync(
        PerformerDto performer,
        CancellationToken cancellationToken = default)
        => SendAsync<EntityRatingsDto>(
            HttpMethod.Get,
            $"/api/engagement/{AffinityHostType.Performer}/{performer.Id}/ratings",
            payload: null,
            cancellationToken);

    public Task<EntityEngagementDto> SetPerformerFavoriteAsync(
        PerformerDto performer,
        bool isFavorite,
        CancellationToken cancellationToken = default)
        => SendAsync<EntityEngagementDto>(
            HttpMethod.Put,
            $"/api/engagement/{AffinityHostType.Performer}/{performer.Id}/favorite",
            new EntityFavoriteDto(isFavorite),
            cancellationToken);

    public Task<BookmarkStateDto> SetPerformerBookmarkAsync(
        PerformerDto performer,
        bool isSaved,
        CancellationToken cancellationToken = default)
        => SendAsync<BookmarkStateDto>(
            HttpMethod.Post,
            "/api/me/bookmarks",
            new BookmarkToggleDto(AffinityHostType.Performer, performer.Id, isSaved),
            cancellationToken);

    public async Task<BookmarkStateDto> GetPerformerBookmarkAsync(
        PerformerDto performer,
        CancellationToken cancellationToken = default)
    {
        var states = await SendAsync<IReadOnlyList<BookmarkStateDto>>(
            HttpMethod.Post,
            "/api/me/bookmarks/batch",
            new BookmarkBatchRequestDto(AffinityHostType.Performer, [performer.Id]),
            cancellationToken);
        return states.Single();
    }

    public Task<StudioDto> CreateStudioAsync(
        string name,
        CancellationToken cancellationToken = default)
        => CreateStudioAsync(
            new StudioCreateDto(
                Name: name,
                ParentId: null,
                Rating: null,
                Favorite: false,
                Details: null,
                Organized: false,
                Urls: [],
                Aliases: [],
                TagIds: []),
            cancellationToken);

    public Task<StudioDto> CreateStudioAsync(
        StudioCreateDto studio,
        CancellationToken cancellationToken = default)
        => SendAsync<StudioDto>(HttpMethod.Post, "/api/studios", studio, cancellationToken);

    public Task<StudioDto> GetStudioByIdAsync(
        int studioId,
        CancellationToken cancellationToken = default)
        => SendAsync<StudioDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/studios/{studioId}"),
            payload: null,
            cancellationToken);

    public async Task<IReadOnlyList<StudioDto>> GetStudiosAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<PaginatedResponse<StudioDto>>(
            HttpMethod.Get,
            WithCacheNonce("/api/studios?perPage=250"),
            payload: null,
            cancellationToken);
        return result.Items;
    }

    public Task<ImageDto> CreateImageAsync(
        string title,
        CancellationToken cancellationToken = default)
        => CreateImageAsync(
            new ImageCreateDto(
                Title: title,
                Code: null,
                Details: null,
                Photographer: null,
                Rating: null,
                Organized: false,
                StudioId: null,
                Date: null,
                Urls: [],
                TagIds: [],
                PerformerIds: [],
                GalleryIds: [],
                GroupIds: []),
            cancellationToken);

    public Task<ImageDto> CreateImageAsync(
        ImageCreateDto image,
        CancellationToken cancellationToken = default)
        => SendAsync<ImageDto>(HttpMethod.Post, "/api/images", image, cancellationToken);

    public Task<ImageDto> GetImageByIdAsync(
        int imageId,
        CancellationToken cancellationToken = default)
        => SendAsync<ImageDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/images/{imageId}"),
            payload: null,
            cancellationToken);

    public async Task<IReadOnlyList<ImageDto>> GetImagesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<PaginatedResponse<ImageDto>>(
            HttpMethod.Get,
            WithCacheNonce("/api/images?perPage=250"),
            payload: null,
            cancellationToken);
        return result.Items;
    }

    public Task<GalleryDto> CreateGalleryAsync(
        GalleryCreateDto gallery,
        CancellationToken cancellationToken = default)
        => SendAsync<GalleryDto>(HttpMethod.Post, "/api/galleries", gallery, cancellationToken);

    public Task<GalleryDto> GetGalleryByIdAsync(
        int galleryId,
        CancellationToken cancellationToken = default)
        => SendAsync<GalleryDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/galleries/{galleryId}"),
            payload: null,
            cancellationToken);

    public Task<int> GetGalleryLikeCountAsync(
        GalleryDto gallery,
        CancellationToken cancellationToken = default)
        => SendAsync<int>(
            HttpMethod.Get,
            WithCacheNonce($"/api/galleries/{gallery.Id}/like-count"),
            payload: null,
            cancellationToken);

    public async Task<IReadOnlyList<GalleryDto>> GetGalleriesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<PaginatedResponse<GalleryDto>>(
            HttpMethod.Get,
            WithCacheNonce("/api/galleries?perPage=250"),
            payload: null,
            cancellationToken);
        return result.Items;
    }

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

    public Task<EntityEngagementDto> SetVideoFavoriteAsync(
        VideoDto video,
        bool isFavorite,
        CancellationToken cancellationToken = default)
        => SendAsync<EntityEngagementDto>(
            HttpMethod.Put,
            $"/api/engagement/video/{video.Id}/favorite",
            new EntityFavoriteDto(isFavorite),
            cancellationToken);

    public Task<int> IncrementVideoLikeAsync(
        VideoDto video,
        CancellationToken cancellationToken = default)
        => SendAsync<int>(
            HttpMethod.Post,
            $"/api/videos/{video.Id}/like",
            payload: null,
            cancellationToken);

    public Task<int> IncrementImageLikeAsync(
        ImageDto image,
        CancellationToken cancellationToken = default)
        => SendAsync<int>(
            HttpMethod.Post,
            $"/api/images/{image.Id}/like",
            payload: null,
            cancellationToken);

    public Task<EntityEngagementDto> SetVideoRatingAsync(
        VideoDto video,
        int rating,
        string aspect = "overall",
        CancellationToken cancellationToken = default)
        => SendAsync<EntityEngagementDto>(
            HttpMethod.Put,
            $"/api/engagement/{AffinityHostType.Video}/{video.Id}/rating",
            new VideoRatingDto(rating, aspect),
            cancellationToken);

    public Task<EntityRatingsDto> GetVideoRatingsAsync(
        VideoDto video,
        CancellationToken cancellationToken = default)
        => SendAsync<EntityRatingsDto>(
            HttpMethod.Get,
            $"/api/engagement/{AffinityHostType.Video}/{video.Id}/ratings",
            payload: null,
            cancellationToken);

    public Task<EntityEngagementDto> GetVideoEngagementAsync(
        VideoDto video,
        CancellationToken cancellationToken = default)
        => SendAsync<EntityEngagementDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/engagement/video/{video.Id}"),
            payload: null,
            cancellationToken);

    public async Task UploadPerformerImageAsync(
        PerformerDto performer,
        byte[] image,
        CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent();
        using var imageContent = new ByteArrayContent(image);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(imageContent, "file", "performer.png");

        var requestUri = $"/api/performers/{performer.Id}/image";
        using var response = await _client.PostAsync(requestUri, content, cancellationToken);
        _ = await ApiResponse.ReadAsync<JsonElement>(
            response,
            $"POST {requestUri}",
            cancellationToken);
    }

    public async Task<ApiBinaryContent> GetPerformerImageAsync(
        PerformerDto performer,
        CancellationToken cancellationToken = default)
    {
        var requestUri = WithCacheNonce($"/api/performers/{performer.Id}/image");
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

    public Task<IReadOnlyList<DirectoryEntryDto>> BrowseDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<DirectoryEntryDto>>(
            HttpMethod.Get,
            $"/api/files/browse?path={Uri.EscapeDataString(path)}&apiTestNonce={Guid.NewGuid():N}",
            payload: null,
            cancellationToken);

    public Task RecordVideoPlaybackAsync(
        VideoDto video,
        Guid sessionId,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(
            HttpMethod.Post,
            "/api/playback/intervals",
            new PlaybackIntervalsRequestDto(
                HostType: "video",
                HostId: video.Id,
                SessionId: sessionId,
                MediaDurationSec: 20,
                CurrentPositionSec: 8,
                State: "paused",
                Intervals: [new PlaybackIntervalInputDto(2, 8)]),
            cancellationToken);

    public Task<VideoHistoryDto> GetVideoHistoryAsync(
        VideoDto video,
        CancellationToken cancellationToken = default)
        => SendAsync<VideoHistoryDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/videos/{video.Id}/history"),
            payload: null,
            cancellationToken);

    public Task<IReadOnlyList<ScrapeAttemptDto>> GetVideoScrapeAttemptsAsync(
        VideoDto video,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<ScrapeAttemptDto>>(
            HttpMethod.Get,
            $"/api/scrape-attempts?entityType=video&entityId={video.Id}&apiTestNonce={Guid.NewGuid():N}",
            payload: null,
            cancellationToken);

    public Task<StashPreviewResult> PreviewStashMigrationAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
        => SendAsync<StashPreviewResult>(
            HttpMethod.Post,
            "/api/stash-migration/preview",
            new { stashDbPath = databasePath },
            cancellationToken);

    public async Task<bool> GetVideoPreviewAvailabilityAsync(
        VideoDto video,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Get,
            WithCacheNonce($"/api/stream/video/{video.Id}/preview/status"),
            payload: null,
            cancellationToken);
        return response.GetProperty("available").GetBoolean();
    }

    public Task<VideoDto> ImportVideoFromMetadataServiceAsync(
        VideoDto video,
        MetadataServiceSceneHandle metadataScene,
        CancellationToken cancellationToken = default)
        => SendAsync<VideoDto>(
            HttpMethod.Post,
            $"/api/videos/{video.Id}/metadata-server/import",
            new MetadataServerVideoImportRequestDto
            {
                Endpoint = metadataScene.Endpoint.AbsoluteUri,
                VideoId = metadataScene.Id,
            },
            cancellationToken);

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

    public Task<TagDetailDto> CreateTagAsync(
        TagCreateDto tag,
        CancellationToken cancellationToken = default)
        => SendAsync<TagDetailDto>(HttpMethod.Post, "/api/tags", tag, cancellationToken);

    public Task<TagGroupDto> CreateTagGroupAsync(
        TagGroupCreateDto tagGroup,
        CancellationToken cancellationToken = default)
        => SendAsync<TagGroupDto>(HttpMethod.Post, "/api/taggroups", tagGroup, cancellationToken);

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

    public Task<EntityEngagementDto> GetEntityEngagementAsync(
        AffinityHostType hostType,
        int hostId,
        CancellationToken cancellationToken = default)
        => SendAsync<EntityEngagementDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/engagement/{hostType}/{hostId}"),
            payload: null,
            cancellationToken);

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

    public Task<PerformerDto> GetPerformerByIdAsync(
        int performerId,
        CancellationToken cancellationToken = default)
        => SendAsync<PerformerDto>(
            HttpMethod.Get,
            $"/api/performers/{performerId}?apiTestNonce={Guid.NewGuid():N}",
            payload: null,
            cancellationToken);

    public async Task<IReadOnlyList<PerformerDto>> GetPerformersAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<PaginatedResponse<PerformerDto>>(
            HttpMethod.Get,
            $"/api/performers?perPage=250&apiTestNonce={Guid.NewGuid():N}",
            payload: null,
            cancellationToken);
        return result.Items;
    }

    public async Task<PerformerDto> LinkTagToPerformerAsync(
        TagDetailDto tag,
        PerformerDto performer,
        CancellationToken cancellationToken = default)
    {
        var current = await GetPerformerByIdAsync(performer.Id, cancellationToken);
        var tagIds = current.Tags
            .Select(existingTag => existingTag.Id)
            .Append(tag.Id)
            .Distinct()
            .ToList();

        return await SendAsync<PerformerDto>(
            HttpMethod.Put,
            $"/api/performers/{performer.Id}",
            new { tagIds },
            cancellationToken);
    }

    public HttpClient CreateHttpClient()
    {
        var client = new HttpClient { BaseAddress = BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        return client;
    }

    public void Dispose() => _client.Dispose();

    private Task<DetectionDto> CreateDetectionAsync(
        string requestUri,
        string classification,
        double? observedAtSec,
        CancellationToken cancellationToken)
        => SendAsync<DetectionDto>(
            HttpMethod.Post,
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

    private async Task SendForNoContentAsync(
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
        if (response.StatusCode is System.Net.HttpStatusCode.NoContent)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"{method} {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
    }

    private static string WithCacheNonce(string requestUri)
        => $"{requestUri}{(requestUri.Contains('?') ? '&' : '?')}apiTestNonce={Guid.NewGuid():N}";

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string requestUri,
        object? payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, requestUri);
        if (payload is not null)
            request.Content = JsonContent.Create(payload, options: ApiJson.Options);

        using var response = await _client.SendAsync(request, cancellationToken);
        return await ApiResponse.ReadAsync<T>(
            response,
            $"{method} {requestUri}",
            cancellationToken);
    }
}

public sealed record ApiBinaryContent(byte[] Content, string? MediaType);
