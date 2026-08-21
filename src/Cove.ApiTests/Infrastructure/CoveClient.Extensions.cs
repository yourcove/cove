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
