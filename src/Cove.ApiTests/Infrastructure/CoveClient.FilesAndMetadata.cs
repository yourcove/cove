using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cove.Api.Services;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<IReadOnlyList<LibraryFolderDto>> GetMetadataLibraryFoldersAsync(
        string? path = null,
        bool probeChildren = true,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"/api/metadata/library-folders?probeChildren={probeChildren.ToString().ToLowerInvariant()}";
        if (!string.IsNullOrWhiteSpace(path))
            requestUri += $"&path={Uri.EscapeDataString(path)}";

        return SendAsync<IReadOnlyList<LibraryFolderDto>>(
            HttpMethod.Get,
            WithCacheNonce(requestUri),
            payload: null,
            cancellationToken);
    }

    public Task<string> StartMetadataScanAsync(
        ScanOptionsDto options,
        CancellationToken cancellationToken = default)
        => StartMetadataJobAsync("/api/metadata/scan", options, cancellationToken);

    public Task<string> StartMetadataGenerateAsync(
        GenerateOptionsDto options,
        CancellationToken cancellationToken = default)
        => StartMetadataJobAsync("/api/metadata/generate", options, cancellationToken);

    public Task<string> StartMetadataCleanAsync(
        CleanOptionsDto options,
        CancellationToken cancellationToken = default)
        => StartMetadataJobAsync("/api/metadata/clean", options, cancellationToken);

    public Task<string> StartMetadataCleanGeneratedAsync(
        CancellationToken cancellationToken = default)
        => StartMetadataJobAsync("/api/metadata/clean-generated", payload: null, cancellationToken);

    public Task<string> StartMetadataExportAsync(
        ExportOptionsDto options,
        CancellationToken cancellationToken = default)
        => StartMetadataJobAsync("/api/metadata/export", options, cancellationToken);

    public Task<string> StartMetadataImportAsync(
        ImportOptionsDto options,
        CancellationToken cancellationToken = default)
        => StartMetadataJobAsync("/api/metadata/import", options, cancellationToken);

    public Task<string> StartMetadataIdentifyAsync(
        IdentifyOptionsDto options,
        CancellationToken cancellationToken = default)
        => StartMetadataJobAsync("/api/metadata/identify", options, cancellationToken);

    public Task<string> StartMetadataFingerprintSyncAsync(
        SyncFingerprintsOptionsDto options,
        CancellationToken cancellationToken = default)
        => StartMetadataJobAsync("/api/metadata/sync-fingerprints", options, cancellationToken);

    public Task<IReadOnlyList<CustomFieldDefinitionDto>> GetCustomFieldDefinitionsAsync(
        string? entityType = null,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<CustomFieldDefinitionDto>>(
            HttpMethod.Get,
            WithCacheNonce(entityType is null
                ? "/api/custom-fields"
                : $"/api/custom-fields?entityType={Uri.EscapeDataString(entityType)}"),
            payload: null,
            cancellationToken);

    public async Task<CustomFieldDefinitionDto> CreateCustomFieldDefinitionAsync(
        CustomFieldDefinitionCreateDto definition,
        CancellationToken cancellationToken = default)
    {
        const string requestUri = "/api/custom-fields";
        using var response = await _client.PostAsJsonAsync(requestUri, definition, ApiJson.Options, cancellationToken);
        if (response.StatusCode is not HttpStatusCode.Created)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"POST {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
        }

        return await ApiResponse.ReadAsync<CustomFieldDefinitionDto>(response, $"POST {requestUri}", cancellationToken);
    }

    public async Task<CustomFieldDefinitionDto> UpdateCustomFieldDefinitionAsync(
        int definitionId,
        CustomFieldDefinitionUpdateDto definition,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"/api/custom-fields/{definitionId}";
        using var response = await _client.PutAsJsonAsync(requestUri, definition, ApiJson.Options, cancellationToken);
        if (response.StatusCode is not HttpStatusCode.OK)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"PUT {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
        }

        return await ApiResponse.ReadAsync<CustomFieldDefinitionDto>(response, $"PUT {requestUri}", cancellationToken);
    }

    public async Task<IReadOnlyList<CustomFieldDefinitionDto>> ReplaceCustomFieldDefinitionsAsync(
        IReadOnlyList<CustomFieldDefinitionSyncDto> definitions,
        CancellationToken cancellationToken = default)
    {
        const string requestUri = "/api/custom-fields";
        using var response = await _client.PutAsJsonAsync(requestUri, definitions, ApiJson.Options, cancellationToken);
        if (response.StatusCode is not HttpStatusCode.OK)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"PUT {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
        }

        return await ApiResponse.ReadAsync<IReadOnlyList<CustomFieldDefinitionDto>>(
            response,
            $"PUT {requestUri}",
            cancellationToken);
    }

    public Task DeleteCustomFieldDefinitionAsync(
        int definitionId,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(
            HttpMethod.Delete,
            $"/api/custom-fields/{definitionId}",
            new { },
            cancellationToken);

    public async Task<ApiBinaryContent> GetTextFileAsync(
        TextDocumentDto text,
        CancellationToken cancellationToken = default)
    {
        var requestUri = WithCacheNonce($"/api/texts/{text.Id}/file");
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

    public async Task<string> StartStashImportAsync(
        string databasePath,
        bool migrateGeneratedContent,
        CancellationToken cancellationToken = default)
    {
        var response = await SendForExpectedStatusAsync<JsonElement>(
            HttpMethod.Post,
            "/api/stash-migration/import",
            new
            {
                stashDbPath = databasePath,
                generatedPath = (string?)null,
                migrateGeneratedContent,
                pathMappings = (object?)null,
            },
            HttpStatusCode.Accepted,
            cancellationToken);
        return response.GetProperty("jobId").GetString()
            ?? throw new InvalidOperationException("POST /api/stash-migration/import did not return a job id.");
    }

    public Task<StashImportResult> GetStashImportResultAsync(
        string jobId,
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<StashImportResult>(
            HttpMethod.Get,
            WithCacheNonce($"/api/stash-migration/import/{Uri.EscapeDataString(jobId)}"),
            payload: null,
            HttpStatusCode.OK,
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

    public Task<IReadOnlyList<MetadataServerVideoMatchDto>> SearchVideoMetadataServiceAsync(
        VideoDto video,
        string term,
        MetadataServiceSceneHandle metadataScene,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<MetadataServerVideoMatchDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/videos/{video.Id}/metadata-server/search?term={Uri.EscapeDataString(term)}&endpoint={Uri.EscapeDataString(metadataScene.Endpoint.AbsoluteUri)}"),
            payload: null,
            cancellationToken);

    public Task<IReadOnlyList<MetadataServerVideoMatchDto>> FindVideoMetadataServiceByIdsAsync(
        MetadataServiceSceneHandle metadataScene,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<MetadataServerVideoMatchDto>>(
            HttpMethod.Post,
            "/api/videos/metadata-server/find-by-ids",
            new MetadataServerFindByIdsRequestDto(metadataScene.Endpoint.AbsoluteUri, ids.ToList()),
            cancellationToken);

    public Task SubmitVideoFingerprintsToMetadataServiceAsync(
        VideoDto video,
        MetadataServiceSceneHandle metadataScene,
        CancellationToken cancellationToken = default)
        => SendForOkAsync(
            HttpMethod.Post,
            $"/api/videos/{video.Id}/metadata-server/submit-fingerprints",
            new MetadataServerEndpointDto(metadataScene.Endpoint.AbsoluteUri),
            cancellationToken);

    public async Task<string?> SubmitVideoDraftToMetadataServiceAsync(
        VideoDto video,
        MetadataServiceSceneHandle metadataScene,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Post,
            $"/api/videos/{video.Id}/metadata-server/submit-draft",
            new MetadataServerEndpointDto(metadataScene.Endpoint.AbsoluteUri),
            cancellationToken);
        return response.GetProperty("draftId").GetString();
    }

    public Task<IReadOnlyList<MetadataServerPerformerMatchDto>> SearchPerformerMetadataServiceAsync(
        PerformerDto performer,
        string name,
        MetadataServicePerformerHandle metadataPerformer,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<MetadataServerPerformerMatchDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/performers/{performer.Id}/metadata-server/search?term={Uri.EscapeDataString(name)}&endpoint={Uri.EscapeDataString(metadataPerformer.Endpoint.AbsoluteUri)}"),
            payload: null,
            cancellationToken);

    public Task<IReadOnlyList<MetadataServerPerformerMatchDto>> FindPerformerMetadataServiceByIdsAsync(
        MetadataServicePerformerHandle metadataPerformer,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<MetadataServerPerformerMatchDto>>(
            HttpMethod.Post,
            "/api/performers/metadata-server/find-by-ids",
            new MetadataServerFindByIdsRequestDto(metadataPerformer.Endpoint.AbsoluteUri, ids.ToList()),
            cancellationToken);

    public Task<PerformerDto> ImportPerformerFromMetadataServiceAsync(
        PerformerDto performer,
        MetadataServerPerformerMatchDto match,
        CancellationToken cancellationToken = default)
        => SendAsync<PerformerDto>(
            HttpMethod.Post,
            $"/api/performers/{performer.Id}/metadata-server/import",
            new MetadataServerPerformerImportRequestDto
            {
                Endpoint = match.Endpoint,
                PerformerId = match.Id,
            },
            cancellationToken);

    public async Task<string?> SubmitPerformerDraftToMetadataServiceAsync(
        PerformerDto performer,
        MetadataServicePerformerHandle metadataPerformer,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Post,
            $"/api/performers/{performer.Id}/metadata-server/submit-draft",
            new MetadataServerEndpointDto(metadataPerformer.Endpoint.AbsoluteUri),
            cancellationToken);
        return response.GetProperty("draftId").GetString();
    }

    public async Task<MetadataServerBatchTagStartResult> StartPerformerMetadataBatchTagAsync(
        MetadataServerPerformerBatchTagRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Post,
            "/api/performers/metadata-server/batch-tag",
            request,
            cancellationToken);
        return new MetadataServerBatchTagStartResult(
            response.GetProperty("jobId").GetString()
                ?? throw new InvalidOperationException("POST /api/performers/metadata-server/batch-tag did not return a job id."),
            response.GetProperty("itemCount").GetInt32());
    }

    public Task<IReadOnlyList<MetadataServerTagMatchDto>> SearchTagMetadataServiceAsync(
        TagDetailDto tag,
        string name,
        MetadataServiceTagHandle metadataTag,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<MetadataServerTagMatchDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/tags/{tag.Id}/metadata-server/search?term={Uri.EscapeDataString(name)}&endpoint={Uri.EscapeDataString(metadataTag.Endpoint.AbsoluteUri)}"),
            null,
            cancellationToken);

    public Task<IReadOnlyList<MetadataServerTagMatchDto>> FindTagMetadataServiceByIdsAsync(
        MetadataServiceTagHandle metadataTag,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<MetadataServerTagMatchDto>>(
            HttpMethod.Post,
            "/api/tags/metadata-server/find-by-ids",
            new MetadataServerFindByIdsRequestDto(metadataTag.Endpoint.AbsoluteUri, ids.ToList()),
            cancellationToken);

    public Task<TagDetailDto> ImportTagFromMetadataServiceAsync(
        TagDetailDto tag,
        MetadataServerTagMatchDto match,
        CancellationToken cancellationToken = default)
        => SendAsync<TagDetailDto>(
            HttpMethod.Post,
            $"/api/tags/{tag.Id}/metadata-server/import",
            new MetadataServerTagImportRequestDto(match.Endpoint, match.Id),
            cancellationToken);

    public async Task<string?> SubmitTagDraftToMetadataServiceAsync(
        TagDetailDto tag,
        MetadataServiceTagHandle metadataTag,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Post,
            $"/api/tags/{tag.Id}/metadata-server/submit-draft",
            new MetadataServerEndpointDto(metadataTag.Endpoint.AbsoluteUri),
            cancellationToken);
        return response.GetProperty("draftId").GetString();
    }

    public async Task<MetadataServerBatchTagStartResult> StartTagMetadataBatchTagAsync(
        MetadataServerTagBatchTagRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Post,
            "/api/tags/metadata-server/batch-tag",
            request,
            cancellationToken);
        return new MetadataServerBatchTagStartResult(
            response.GetProperty("jobId").GetString()
                ?? throw new InvalidOperationException("POST /api/tags/metadata-server/batch-tag did not return a job id."),
            response.GetProperty("itemCount").GetInt32());
    }

    public Task<IReadOnlyList<MetadataServerStudioMatchDto>> SearchStudioMetadataServiceAsync(
        StudioDto studio,
        string name,
        MetadataServiceStudioHandle metadataStudio,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"/api/studios/{studio.Id}/metadata-server/search"
            + $"?term={Uri.EscapeDataString(name)}"
            + $"&endpoint={Uri.EscapeDataString(metadataStudio.Endpoint.AbsoluteUri)}";
        return SendAsync<IReadOnlyList<MetadataServerStudioMatchDto>>(
            HttpMethod.Get,
            WithCacheNonce(requestUri),
            null,
            cancellationToken);
    }

    public Task<IReadOnlyList<MetadataServerStudioMatchDto>> FindStudioMetadataServiceByIdsAsync(
        MetadataServiceStudioHandle metadataStudio,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<MetadataServerStudioMatchDto>>(
            HttpMethod.Post,
            "/api/studios/metadata-server/find-by-ids",
            new MetadataServerFindByIdsRequestDto(metadataStudio.Endpoint.AbsoluteUri, ids.ToList()),
            cancellationToken);

    public Task<StudioDto> ImportStudioFromMetadataServiceAsync(
        StudioDto studio,
        MetadataServerStudioMatchDto match,
        CancellationToken cancellationToken = default)
        => SendAsync<StudioDto>(
            HttpMethod.Post,
            $"/api/studios/{studio.Id}/metadata-server/import",
            new MetadataServerStudioImportRequestDto { Endpoint = match.Endpoint, StudioId = match.Id },
            cancellationToken);

    public async Task<string?> SubmitStudioDraftToMetadataServiceAsync(
        StudioDto studio,
        MetadataServiceStudioHandle metadataStudio,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Post,
            $"/api/studios/{studio.Id}/metadata-server/submit-draft",
            new MetadataServerEndpointDto(metadataStudio.Endpoint.AbsoluteUri),
            cancellationToken);
        return response.GetProperty("draftId").GetString();
    }

    public async Task<MetadataServerBatchTagStartResult> StartStudioMetadataBatchTagAsync(
        MetadataServerStudioBatchTagRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Post,
            "/api/studios/metadata-server/batch-tag",
            request,
            cancellationToken);
        return new MetadataServerBatchTagStartResult(
            response.GetProperty("jobId").GetString()
                ?? throw new InvalidOperationException(
                    "POST /api/studios/metadata-server/batch-tag did not return a job id."),
            response.GetProperty("itemCount").GetInt32());
    }

    private async Task<string> StartMetadataJobAsync(
        string requestUri,
        object? payload,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Post,
            requestUri,
            payload,
            cancellationToken);
        return response.GetProperty("jobId").GetString()
            ?? throw new InvalidOperationException($"POST {requestUri} did not return a job id.");
    }
}

public sealed record ApiBinaryContent(
    byte[] Content,
    string? MediaType,
    string? CacheControl = null,
    Uri? RedirectTarget = null);

public sealed record MetadataServerBatchTagStartResult(string JobId, int ItemCount);
