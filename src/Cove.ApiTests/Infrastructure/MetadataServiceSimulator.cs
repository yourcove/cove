using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cove.ApiTests.Infrastructure;

public sealed class MetadataServiceSimulator : IAsyncDisposable
{
    internal const string ApiKey = "cove-api-tests-metadata-service-key";

    private readonly WebApplication _application;
    private readonly ConcurrentDictionary<string, MetadataServiceScene> _scenes;

    private MetadataServiceSimulator(
        WebApplication application,
        ConcurrentDictionary<string, MetadataServiceScene> scenes,
        Uri endpoint)
    {
        _application = application;
        _scenes = scenes;
        Endpoint = endpoint;
    }

    public Uri Endpoint { get; }

    internal static async Task<MetadataServiceSimulator> StartAsync(
        CancellationToken cancellationToken = default)
    {
        var scenes = new ConcurrentDictionary<string, MetadataServiceScene>(StringComparer.Ordinal);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

        var application = builder.Build();
        application.MapPost("/", context => HandleRequestAsync(context, scenes));

        try
        {
            await application.StartAsync(cancellationToken);
            var addresses = application.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                ?.Addresses;
            var address = addresses?.SingleOrDefault()
                ?? throw new InvalidOperationException("The metadata-service simulator did not publish a listening address.");

            return new MetadataServiceSimulator(application, scenes, new Uri(address));
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
    }

    public MetadataServiceSceneHandle CreateScene(MetadataServiceScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (string.IsNullOrWhiteSpace(scene.Id))
            throw new ArgumentException("A metadata scene id is required.", nameof(scene));
        if (string.IsNullOrWhiteSpace(scene.Title))
            throw new ArgumentException("A metadata scene title is required.", nameof(scene));
        if (scene.Tags.Any(tag => string.IsNullOrWhiteSpace(tag.Id) || string.IsNullOrWhiteSpace(tag.Name)))
            throw new ArgumentException("Every metadata scene tag requires an id and name.", nameof(scene));
        if (!_scenes.TryAdd(scene.Id, scene))
            throw new InvalidOperationException($"Metadata scene '{scene.Id}' is already registered.");
        return new MetadataServiceSceneHandle(Endpoint, scene);
    }

    internal void Reset()
        => _scenes.Clear();

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync();
        await _application.DisposeAsync();
    }

    private static async Task HandleRequestAsync(
        HttpContext context,
        ConcurrentDictionary<string, MetadataServiceScene> scenes)
    {
        if (!string.Equals(context.Request.Headers["ApiKey"], ApiKey, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var request = await JsonSerializer.DeserializeAsync<GraphQlRequest>(
            context.Request.Body,
            ApiJson.Options,
            context.RequestAborted);
        if (request is null
            || !request.Query.Contains("query FindVideoByID", StringComparison.Ordinal)
            || !request.Query.Contains("findVideo: findScene(id: $id)", StringComparison.Ordinal)
            || !request.Query.Contains("tags {", StringComparison.Ordinal))
        {
            await WriteGraphQlErrorAsync(
                context,
                "The simulator requires a FindVideoByID scene query that selects tags.");
            return;
        }

        if (!request.Variables.TryGetProperty("id", out var idProperty)
            || string.IsNullOrWhiteSpace(idProperty.GetString()))
        {
            await WriteGraphQlErrorAsync(context, "FindVideoByID requires an id variable.");
            return;
        }

        scenes.TryGetValue(idProperty.GetString()!, out var scene);
        var remoteScene = scene is null
            ? null
            : new
            {
                id = scene.Id,
                title = scene.Title,
                code = (string?)null,
                details = (string?)null,
                director = (string?)null,
                duration = (int?)null,
                date = (string?)null,
                urls = Array.Empty<object>(),
                images = Array.Empty<object>(),
                studio = (object?)null,
                tags = scene.Tags.Select(tag => new
                {
                    id = tag.Id,
                    name = tag.Name,
                    description = (string?)null,
                    aliases = Array.Empty<string>(),
                }),
                performers = Array.Empty<object>(),
                fingerprints = Array.Empty<object>(),
            };

        await context.Response.WriteAsJsonAsync(
            new { data = new { findVideo = remoteScene } },
            ApiJson.Options,
            context.RequestAborted);
    }

    private static Task WriteGraphQlErrorAsync(HttpContext context, string message)
        => context.Response.WriteAsJsonAsync(
            new { errors = new[] { new { message } } },
            ApiJson.Options,
            context.RequestAborted);

    private sealed record GraphQlRequest(string Query, JsonElement Variables);
}

public sealed record MetadataServiceScene(
    string Id,
    string Title,
    IReadOnlyList<MetadataServiceTag> Tags);

public sealed record MetadataServiceTag(string Id, string Name);

public sealed record MetadataServiceSceneHandle(
    Uri Endpoint,
    MetadataServiceScene Scene)
{
    public string Id => Scene.Id;
}
