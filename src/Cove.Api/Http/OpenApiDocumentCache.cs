using System.Security.Cryptography;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Cove.Api.Http;

/// <summary>
/// Holds the serialized OpenAPI document so it is generated once per process instead of on every
/// request. Generating it walks every controller and extension route and takes over a second.
/// </summary>
/// <remarks>
/// Startup calls <see cref="WarmUp"/> once extensions have registered their routes. Until that
/// finishes, requests generate the document themselves and nothing is cached, so a request that
/// arrives while Cove is still starting cannot pin a document that lacks extension routes.
/// </remarks>
public sealed class OpenApiDocumentCache(IServiceProvider services, ILogger<OpenApiDocumentCache> logger)
{
    public const string DocumentName = "v1";

    public sealed record Document(byte[] Json, string ETag);

    private volatile Task<Document>? _cached;

    /// <summary>Starts generating the document in the background and keeps the result.</summary>
    public void WarmUp()
    {
        var generation = Task.Run(() => GenerateAsync(CancellationToken.None));
        _cached = generation;
        generation.ContinueWith(
            task =>
            {
                // A failed warm-up must not be served forever; fall back to generating per request.
                logger.LogWarning(task.Exception, "Generating the OpenAPI document failed; it will be generated per request");
                Interlocked.CompareExchange(ref _cached, null, generation);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    public Task<Document> GetAsync(CancellationToken cancellationToken)
        => _cached is { } cached ? cached.WaitAsync(cancellationToken) : GenerateAsync(cancellationToken);

    private async Task<Document> GenerateAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredKeyedService<IOpenApiDocumentProvider>(DocumentName);
        var document = await provider.GetOpenApiDocumentAsync(cancellationToken);
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1, cancellationToken);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var etag = "\"" + Convert.ToHexStringLower(SHA256.HashData(bytes).AsSpan(0, 16)) + "\"";
        return new Document(bytes, etag);
    }
}
