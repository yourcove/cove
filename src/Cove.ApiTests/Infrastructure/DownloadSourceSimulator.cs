using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cove.ApiTests.Infrastructure;

public sealed class DownloadSourceSimulator : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly ConcurrentDictionary<string, DownloadSourceResource> _resources;

    private DownloadSourceSimulator(
        WebApplication application,
        ConcurrentDictionary<string, DownloadSourceResource> resources,
        Uri endpoint)
    {
        _application = application;
        _resources = resources;
        Endpoint = endpoint;
    }

    public Uri Endpoint { get; }

    internal static async Task<DownloadSourceSimulator> StartAsync(
        CancellationToken cancellationToken = default)
    {
        var resources = new ConcurrentDictionary<string, DownloadSourceResource>(StringComparer.Ordinal);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

        var application = builder.Build();
        application.MapGet("/files/{id}/{fileName}", (
            HttpContext context,
            string id,
            string fileName) => HandleRequestAsync(context, id, fileName, resources));

        try
        {
            await application.StartAsync(cancellationToken);
            var addresses = application.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                ?.Addresses;
            var address = addresses?.SingleOrDefault()
                ?? throw new InvalidOperationException("The download-source simulator did not publish a listening address.");

            return new DownloadSourceSimulator(application, resources, new Uri(address));
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
    }

    public DownloadSourceHandle CreateTextFile(string fileName, string contents)
        => CreateFile(fileName, "text/plain", System.Text.Encoding.UTF8.GetBytes(contents));

    public DownloadSourceHandle CreateFile(string fileName, string contentType, byte[] contents)
    {
        ValidateFileName(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(contents);

        var resource = new DownloadSourceResource(
            Guid.NewGuid().ToString("N"),
            fileName,
            contentType,
            contents.ToArray(),
            StatusCodes.Status200OK);
        if (!_resources.TryAdd(resource.Id, resource))
            throw new InvalidOperationException("The download-source simulator generated a duplicate resource id.");
        return new DownloadSourceHandle(BuildResourceUri(resource), resource);
    }

    public DownloadSourceHandle CreateFailure(string fileName, HttpStatusCode statusCode)
    {
        ValidateFileName(fileName);
        if ((int)statusCode is < 400 or > 599)
            throw new ArgumentOutOfRangeException(nameof(statusCode), "A simulated failure must use a 4xx or 5xx status code.");

        var resource = new DownloadSourceResource(
            Guid.NewGuid().ToString("N"),
            fileName,
            "text/plain",
            [],
            (int)statusCode);
        if (!_resources.TryAdd(resource.Id, resource))
            throw new InvalidOperationException("The download-source simulator generated a duplicate resource id.");
        return new DownloadSourceHandle(BuildResourceUri(resource), resource);
    }

    internal void Reset() => _resources.Clear();

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync();
        await _application.DisposeAsync();
    }

    private Uri BuildResourceUri(DownloadSourceResource resource)
        => new(Endpoint, $"files/{resource.Id}/{Uri.EscapeDataString(resource.FileName)}");

    private static IResult HandleRequestAsync(
        HttpContext context,
        string id,
        string fileName,
        ConcurrentDictionary<string, DownloadSourceResource> resources)
    {
        if (!resources.TryGetValue(id, out var resource)
            || !string.Equals(fileName, resource.FileName, StringComparison.Ordinal))
        {
            return Results.NotFound();
        }

        Interlocked.Increment(ref resource.RequestCount);
        if (resource.StatusCode != StatusCodes.Status200OK)
            return Results.StatusCode(resource.StatusCode);

        context.Response.Headers.ContentDisposition = $"attachment; filename*=UTF-8''{Uri.EscapeDataString(resource.FileName)}";
        return Results.File(resource.Contents, resource.ContentType);
    }

    private static void ValidateFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName is "." or ".."
            || fileName.Contains('/')
            || fileName.Contains('\\')
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("The download-source filename must be a safe basename.", nameof(fileName));
        }
    }

    internal sealed class DownloadSourceResource(
        string id,
        string fileName,
        string contentType,
        byte[] contents,
        int statusCode)
    {
        public string Id { get; } = id;
        public string FileName { get; } = fileName;
        public string ContentType { get; } = contentType;
        public byte[] Contents { get; } = contents;
        public int StatusCode { get; } = statusCode;
        public int RequestCount;
    }
}

public sealed class DownloadSourceHandle
{
    private readonly DownloadSourceSimulator.DownloadSourceResource _resource;

    internal DownloadSourceHandle(
        Uri uri,
        DownloadSourceSimulator.DownloadSourceResource resource)
    {
        Uri = uri;
        _resource = resource;
    }

    public Uri Uri { get; }
    public string FileName => _resource.FileName;
    public int RequestCount => Volatile.Read(ref _resource.RequestCount);
}
