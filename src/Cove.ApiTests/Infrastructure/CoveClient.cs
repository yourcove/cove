using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient : IDisposable
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

    public HttpClient CreateHttpClient()
    {
        var client = new HttpClient { BaseAddress = BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        return client;
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
