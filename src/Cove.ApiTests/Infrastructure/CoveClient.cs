using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Cove.ApiTests.Infrastructure;

public sealed record BulkDeletionJobStartResponse(string JobId, int ItemCount);

public sealed partial class CoveClient : IDisposable
{
    private readonly HttpClient _client;
    private readonly Action<HttpRequestHeaders> _configureHeaders;

    internal CoveClient(string username, Uri baseAddress, string accessToken)
        : this(username, baseAddress, headers => headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken), accessToken)
    {
    }

    internal CoveClient(
        string username,
        Uri baseAddress,
        Action<HttpRequestHeaders> configureHeaders,
        string accessToken = "")
    {
        Username = username;
        BaseAddress = baseAddress;
        AccessToken = accessToken;
        _configureHeaders = configureHeaders;
        _client = new HttpClient { BaseAddress = baseAddress };
        _configureHeaders(_client.DefaultRequestHeaders);
    }

    public string Username { get; }

    public Uri BaseAddress { get; }

    public string AccessToken { get; }

    public HttpClient CreateHttpClient()
    {
        var client = new HttpClient { BaseAddress = BaseAddress };
        _configureHeaders(client.DefaultRequestHeaders);
        return client;
    }

    public Task AssertResponseAsync(
        string requestUri,
        HttpStatusCode expectedStatusCode = HttpStatusCode.OK,
        CancellationToken cancellationToken = default)
        => AssertResponseAsync(HttpMethod.Get, requestUri, expectedStatusCode, payload: null, cancellationToken);

    public async Task AssertResponseAsync(
        HttpMethod method,
        string requestUri,
        HttpStatusCode expectedStatusCode = HttpStatusCode.OK,
        object? payload = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveUri = method == HttpMethod.Get ? WithCacheNonce(requestUri) : requestUri;
        using var request = new HttpRequestMessage(method, effectiveUri);
        if (payload is not null)
            request.Content = JsonContent.Create(payload, options: ApiJson.Options);

        using var response = await _client.SendAsync(request, cancellationToken);
        if (response.StatusCode == expectedStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"Expected {method} {requestUri} to return {(int)expectedStatusCode} ({expectedStatusCode}), " +
            $"but it returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
    }

    internal async Task<HttpStatusCode> SendStatusAsync(
        HttpMethod method,
        string requestUri,
        object? payload = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, requestUri);
        if (payload is not null)
            request.Content = JsonContent.Create(payload, options: ApiJson.Options);

        using var response = await _client.SendAsync(request, cancellationToken);
        return response.StatusCode;
    }

    public void Dispose() => _client.Dispose();

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
