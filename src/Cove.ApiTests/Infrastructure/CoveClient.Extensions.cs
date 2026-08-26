using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cove.Api.Controllers;
using Cove.Plugins;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<IReadOnlyList<ExtensionInfo>> GetExtensionsAsync(
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<ExtensionInfo>>(
            HttpMethod.Get,
            WithCacheNonce("/api/extensions"),
            payload: null,
            cancellationToken);

    public Task<UIManifest> GetExtensionManifestAsync(
        CancellationToken cancellationToken = default)
        => SendAsync<UIManifest>(
            HttpMethod.Get,
            WithCacheNonce("/api/extensions/manifest"),
            payload: null,
            cancellationToken);

    public Task<IReadOnlyList<string>> GetExtensionCategoriesAsync(
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<string>>(
            HttpMethod.Get,
            WithCacheNonce("/api/extensions/categories"),
            payload: null,
            cancellationToken);

    public Task<IReadOnlyList<string>> GetMissingExtensionDependenciesAsync(
        string extensionId,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<string>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/extensions/{Uri.EscapeDataString(extensionId)}/dependencies/missing"),
            payload: null,
            cancellationToken);

    public Task<IReadOnlyList<DependencyProblem>> ValidateExtensionDependenciesAsync(
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<DependencyProblem>>(
            HttpMethod.Get,
            WithCacheNonce("/api/extensions/dependencies/validate"),
            payload: null,
            cancellationToken);

    public Task<RegistrySearchResult> SearchExtensionRegistryAsync(
        string? query,
        string? category,
        string? type,
        string sort,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>
        {
            $"sort={Uri.EscapeDataString(sort)}",
            $"page={page}",
            $"pageSize={pageSize}",
        };
        if (!string.IsNullOrWhiteSpace(query))
            parameters.Add($"q={Uri.EscapeDataString(query)}");
        if (!string.IsNullOrWhiteSpace(category))
            parameters.Add($"category={Uri.EscapeDataString(category)}");
        if (!string.IsNullOrWhiteSpace(type))
            parameters.Add($"type={Uri.EscapeDataString(type)}");

        return SendForExpectedStatusAsync<RegistrySearchResult>(
            HttpMethod.Get,
            WithCacheNonce($"/api/extensions/registry/search?{string.Join('&', parameters)}"),
            payload: null,
            HttpStatusCode.OK,
            cancellationToken);
    }

    public Task<RegistryExtensionDetail> GetRegistryExtensionAsync(
        string extensionId,
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<RegistryExtensionDetail>(
            HttpMethod.Get,
            WithCacheNonce($"/api/extensions/registry/{Uri.EscapeDataString(extensionId)}"),
            payload: null,
            HttpStatusCode.OK,
            cancellationToken);

    public Task<IReadOnlyList<RegistryUpdateInfo>> GetExtensionRegistryUpdatesAsync(
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<IReadOnlyList<RegistryUpdateInfo>>(
            HttpMethod.Get,
            WithCacheNonce("/api/extensions/registry/updates"),
            payload: null,
            HttpStatusCode.OK,
            cancellationToken);

    public Task<IReadOnlyList<string>> GetExtensionRegistryCategoriesAsync(
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<IReadOnlyList<string>>(
            HttpMethod.Get,
            WithCacheNonce("/api/extensions/registry/categories"),
            payload: null,
            HttpStatusCode.OK,
            cancellationToken);

    public Task<IReadOnlyList<DependencyInfo>> GetRegistryExtensionDependenciesAsync(
        string extensionId,
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<IReadOnlyList<DependencyInfo>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/extensions/registry/{Uri.EscapeDataString(extensionId)}/dependencies"),
            payload: null,
            HttpStatusCode.OK,
            cancellationToken);

    public Task<RegistryInstallPreviewResponse> PreviewRegistryExtensionInstallAsync(
        string extensionId,
        string version,
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<RegistryInstallPreviewResponse>(
            HttpMethod.Post,
            "/api/extensions/registry/install",
            new RegistryInstallRequest
            {
                ExtensionId = extensionId,
                Version = version,
                InstallDependencies = false,
            },
            HttpStatusCode.OK,
            cancellationToken);

    public Task<RegistryInstallResult> InstallRegistryExtensionAsync(
        string extensionId,
        string version,
        CancellationToken cancellationToken = default)
        => SendForExpectedStatusAsync<RegistryInstallResult>(
            HttpMethod.Post,
            "/api/extensions/registry/install",
            new RegistryInstallRequest
            {
                ExtensionId = extensionId,
                Version = version,
                InstallDependencies = true,
            },
            HttpStatusCode.OK,
            cancellationToken);

    public Task<Dictionary<string, string>> GetExtensionDataAsync(
        string extensionId,
        CancellationToken cancellationToken = default)
        => SendAsync<Dictionary<string, string>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/extensions/{Uri.EscapeDataString(extensionId)}/data"),
            payload: null,
            cancellationToken);

    public async Task SetExtensionDataAsync(
        string extensionId,
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"/api/extensions/{Uri.EscapeDataString(extensionId)}/data/{Uri.EscapeDataString(key)}";
        using var response = await _client.PutAsJsonAsync(requestUri, value, ApiJson.Options, cancellationToken);
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"PUT {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
    }

    public async Task<ExtensionJobRunResponse> RunExtensionJobAsync(
        string extensionId,
        string jobId,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"/api/extensions/{Uri.EscapeDataString(extensionId)}/jobs/{Uri.EscapeDataString(jobId)}/run";
        using var response = await _client.PostAsJsonAsync(requestUri, parameters, ApiJson.Options, cancellationToken);
        if (response.StatusCode is not HttpStatusCode.Accepted)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"POST {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
        }

        return await ApiResponse.ReadAsync<ExtensionJobRunResponse>(
            response,
            $"POST {requestUri}",
            cancellationToken);
    }

    public async Task<ExtensionAssetContent> GetExtensionAssetAsync(
        string extensionId,
        string path,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"/api/extensions/assets/{Uri.EscapeDataString(extensionId)}/{path}";
        using var response = await _client.GetAsync(requestUri, cancellationToken);
        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GET {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {System.Text.Encoding.UTF8.GetString(content)}");
        }

        return new ExtensionAssetContent(
            content,
            response.Content.Headers.ContentType?.MediaType,
            response.Headers.CacheControl,
            GetHeader(response, "Pragma"),
            GetHeader(response, "Expires"));
    }

    private static string? GetHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var responseValues))
            return string.Join(", ", responseValues);
        if (response.Content.Headers.TryGetValues(name, out var contentValues))
            return string.Join(", ", contentValues);
        return null;
    }

    public Task<ExtensionTextContent> GetCombinedExtensionJavaScriptAsync(
        CancellationToken cancellationToken = default)
        => GetExtensionTextContentAsync("/api/extensions/bundles/ui.mjs", cancellationToken);

    public Task<ExtensionTextContent> GetCombinedExtensionCssAsync(
        CancellationToken cancellationToken = default)
        => GetExtensionTextContentAsync("/api/extensions/bundles/ui.css", cancellationToken);

    public Task<ExtensionInstallResponse> InstallExtensionFromUrlAsync(
        Uri packageUri,
        bool trustUnverified = true,
        CancellationToken cancellationToken = default)
        => SendAsync<ExtensionInstallResponse>(
            HttpMethod.Post,
            "/api/extensions/install-from-url",
            new InstallExtensionFromUrlRequest
            {
                Url = packageUri.AbsoluteUri,
                TrustUnverified = trustUnverified,
            },
            cancellationToken);

    public async Task<ExtensionInstallResponse> InstallExtensionFromZipAsync(
        byte[] package,
        bool trustUnverified = true,
        CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(package);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(file, "file", "extension.zip");
        form.Add(new StringContent(trustUnverified.ToString().ToLowerInvariant()), "trustUnverified");

        const string requestUri = "/api/extensions/install-from-zip";
        using var response = await _client.PostAsync(requestUri, form, cancellationToken);
        return await ApiResponse.ReadAsync<ExtensionInstallResponse>(
            response,
            $"POST {requestUri}",
            cancellationToken);
    }

    public Task<ExtensionEnableResponse> EnableExtensionAsync(
        string extensionId,
        CancellationToken cancellationToken = default)
        => SendAsync<ExtensionEnableResponse>(
            HttpMethod.Post,
            $"/api/extensions/{Uri.EscapeDataString(extensionId)}/enable",
            payload: null,
            cancellationToken);

    public Task<ExtensionDisableResponse> DisableExtensionAsync(
        string extensionId,
        CancellationToken cancellationToken = default)
        => SendAsync<ExtensionDisableResponse>(
            HttpMethod.Post,
            $"/api/extensions/{Uri.EscapeDataString(extensionId)}/disable",
            payload: null,
            cancellationToken);

    public Task<ExtensionUninstallResponse> UninstallExtensionAsync(
        string extensionId,
        CancellationToken cancellationToken = default)
        => SendAsync<ExtensionUninstallResponse>(
            HttpMethod.Post,
            "/api/extensions/registry/uninstall",
            new RegistryUninstallRequest
            {
                ExtensionId = extensionId,
                UninstallDependents = false,
            },
            cancellationToken);

    public async Task EnsureExtensionUninstalledAsync(
        string extensionId,
        CancellationToken cancellationToken = default)
    {
        const string requestUri = "/api/extensions/registry/uninstall";
        using var response = await _client.PostAsJsonAsync(
            requestUri,
            new RegistryUninstallRequest
            {
                ExtensionId = extensionId,
                UninstallDependents = false,
            },
            ApiJson.Options,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;

        _ = await ApiResponse.ReadAsync<ExtensionUninstallResponse>(
            response,
            $"POST {requestUri}",
            cancellationToken);
    }

    private async Task<ExtensionTextContent> GetExtensionTextContentAsync(
        string requestUri,
        CancellationToken cancellationToken)
    {
        var uncachedUri = WithCacheNonce(requestUri);
        using var response = await _client.GetAsync(uncachedUri, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GET {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {content}");
        }

        return new ExtensionTextContent(
            content,
            response.Content.Headers.ContentType?.MediaType);
    }
}

public sealed record ExtensionTextContent(string Content, string? MediaType);

public sealed record ExtensionAssetContent(
    byte[] Content,
    string? MediaType,
    CacheControlHeaderValue? CacheControl,
    string? Pragma,
    string? Expires);

public sealed record ExtensionJobRunResponse(string Message, string JobId);

public sealed record RegistryInstallExtension(string Id, string Name, string Version);

public sealed record RegistryInstallPreviewResponse(
    bool RequiresDependencies,
    RegistryInstallExtension Extension,
    IReadOnlyList<DependencyInfo> MissingDependencies);

public sealed record RegistryInstallResult(
    string Message,
    string Path,
    IReadOnlyList<string> InstalledDependencies);

public sealed record ExtensionInstallResponse(
    string Message,
    string ExtensionId,
    string Version,
    string Path);

public sealed record ExtensionEnableResponse(IReadOnlyList<string> EnabledExtensions);

public sealed record ExtensionDisableResponse(IReadOnlyList<string> DisabledExtensions);

public sealed record ExtensionUninstallResponse(
    string Message,
    bool RequiresDependents,
    IReadOnlyList<string> UninstalledExtensions);
