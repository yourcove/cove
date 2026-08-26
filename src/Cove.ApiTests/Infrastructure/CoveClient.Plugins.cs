using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<IReadOnlyList<PluginDto>> GetLegacyPluginsAsync(
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<PluginDto>>(
            HttpMethod.Get,
            WithCacheNonce("/api/plugins"),
            payload: null,
            cancellationToken);

    public Task<IReadOnlyList<PluginTaskDto>> GetLegacyPluginTasksAsync(
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<PluginTaskDto>>(
            HttpMethod.Get,
            WithCacheNonce("/api/plugins/tasks"),
            payload: null,
            cancellationToken);

    public Task<Dictionary<string, JsonElement>> GetLegacyPluginConfigAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
        => SendAsync<Dictionary<string, JsonElement>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/plugins/{Uri.EscapeDataString(pluginId)}/config"),
            payload: null,
            cancellationToken);

    public Task<LegacyPluginTaskRunResponse> RunLegacyPluginTaskAsync(
        RunPluginTaskDto request,
        CancellationToken cancellationToken = default)
        => SendAsync<LegacyPluginTaskRunResponse>(
            HttpMethod.Post,
            "/api/plugins/run-task",
            request,
            cancellationToken);

    public async Task<HttpStatusCode> TryRunLegacyPluginTaskAsync(
        RunPluginTaskDto request,
        CancellationToken cancellationToken = default)
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/plugins/run-task",
            request,
            ApiJson.Options,
            cancellationToken);
        return response.StatusCode;
    }

    public Task UpdateLegacyPluginSettingsAsync(
        PluginSettingsDto request,
        CancellationToken cancellationToken = default)
        => SendForSuccessAsync(HttpMethod.Post, "/api/plugins/settings", request, cancellationToken);

    public Task SetLegacyPluginConfigAsync(
        string pluginId,
        Dictionary<string, object?> values,
        CancellationToken cancellationToken = default)
        => SendForSuccessAsync(
            HttpMethod.Post,
            $"/api/plugins/{Uri.EscapeDataString(pluginId)}/config",
            values,
            cancellationToken);

    public Task<LegacyPluginReloadResponse> ReloadLegacyPluginsAsync(
        CancellationToken cancellationToken = default)
        => SendAsync<LegacyPluginReloadResponse>(
            HttpMethod.Post,
            "/api/plugins/reload",
            payload: new { },
            cancellationToken);
}

public sealed record LegacyPluginTaskRunResponse(string JobId);

public sealed record LegacyPluginReloadResponse(string Message);
