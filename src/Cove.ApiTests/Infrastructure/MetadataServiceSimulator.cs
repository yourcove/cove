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
    private readonly ConcurrentDictionary<string, MetadataServicePerformer> _performers;
    private readonly ConcurrentDictionary<string, MetadataServiceRemoteStudio> _studios;
    private readonly ConcurrentDictionary<string, MetadataServiceRemoteTag> _tags;
    private readonly ConcurrentDictionary<string, MetadataServiceScene> _scenes;
    private readonly ConcurrentDictionary<int, MetadataServiceFingerprintSourceVideo> _fingerprintSyncVideos;
    private readonly MetadataServiceSubmissionLog _submissions;
    private readonly MetadataServiceRequestBlocker _requestBlocker;

    private MetadataServiceSimulator(
        WebApplication application,
        ConcurrentDictionary<string, MetadataServicePerformer> performers,
        ConcurrentDictionary<string, MetadataServiceRemoteStudio> studios,
        ConcurrentDictionary<string, MetadataServiceRemoteTag> tags,
        ConcurrentDictionary<string, MetadataServiceScene> scenes,
        ConcurrentDictionary<int, MetadataServiceFingerprintSourceVideo> fingerprintSyncVideos,
        MetadataServiceSubmissionLog submissions,
        MetadataServiceRequestBlocker requestBlocker,
        Uri endpoint)
    {
        _application = application;
        _performers = performers;
        _studios = studios;
        _tags = tags;
        _scenes = scenes;
        _fingerprintSyncVideos = fingerprintSyncVideos;
        _submissions = submissions;
        _requestBlocker = requestBlocker;
        Endpoint = endpoint;
    }

    public Uri Endpoint { get; }

    public IReadOnlyList<MetadataServiceFingerprintSubmission> FingerprintSubmissions
        => _submissions.FingerprintSubmissions;

    public IReadOnlyList<MetadataServiceSceneDraftSubmission> SceneDraftSubmissions
        => _submissions.SceneDraftSubmissions;

    public IReadOnlyList<MetadataServicePerformerDraftSubmission> PerformerDraftSubmissions
        => _submissions.PerformerDraftSubmissions;

    public IReadOnlyList<MetadataServiceTagDraftSubmission> TagDraftSubmissions
        => _submissions.TagDraftSubmissions;

    public IReadOnlyList<MetadataServiceStudioDraftSubmission> StudioDraftSubmissions
        => _submissions.StudioDraftSubmissions;

    internal static async Task<MetadataServiceSimulator> StartAsync(
        CancellationToken cancellationToken = default)
    {
        var performers = new ConcurrentDictionary<string, MetadataServicePerformer>(StringComparer.Ordinal);
        var studios = new ConcurrentDictionary<string, MetadataServiceRemoteStudio>(StringComparer.OrdinalIgnoreCase);
        var tags = new ConcurrentDictionary<string, MetadataServiceRemoteTag>(StringComparer.OrdinalIgnoreCase);
        var scenes = new ConcurrentDictionary<string, MetadataServiceScene>(StringComparer.Ordinal);
        var fingerprintSyncVideos = new ConcurrentDictionary<int, MetadataServiceFingerprintSourceVideo>();
        var submissions = new MetadataServiceSubmissionLog();
        var requestBlocker = new MetadataServiceRequestBlocker();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

        var application = builder.Build();
        application.MapPost("/", context => HandleRequestAsync(
            context,
            performers,
            studios,
            tags,
            scenes,
            fingerprintSyncVideos,
            submissions,
            requestBlocker));

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

            return new MetadataServiceSimulator(
                application,
                performers,
                studios,
                tags,
                scenes,
                fingerprintSyncVideos,
                submissions,
                requestBlocker,
                new Uri(address));
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
        if (scene.Fingerprints.Any(fingerprint =>
                string.IsNullOrWhiteSpace(fingerprint.Algorithm) || string.IsNullOrWhiteSpace(fingerprint.Hash)))
        {
            throw new ArgumentException("Every metadata scene fingerprint requires an algorithm and hash.", nameof(scene));
        }
        if (!_scenes.TryAdd(scene.Id, scene))
            throw new InvalidOperationException($"Metadata scene '{scene.Id}' is already registered.");
        return new MetadataServiceSceneHandle(Endpoint, scene);
    }

    public MetadataServicePerformerHandle CreatePerformer(MetadataServicePerformer performer)
    {
        ArgumentNullException.ThrowIfNull(performer);
        if (string.IsNullOrWhiteSpace(performer.Id))
            throw new ArgumentException("A metadata performer id is required.", nameof(performer));
        if (string.IsNullOrWhiteSpace(performer.Name))
            throw new ArgumentException("A metadata performer name is required.", nameof(performer));
        if (!_performers.TryAdd(performer.Id, performer))
            throw new InvalidOperationException($"Metadata performer '{performer.Id}' is already registered.");
        return new MetadataServicePerformerHandle(Endpoint, performer);
    }

    public MetadataServiceTagHandle CreateTag(MetadataServiceRemoteTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (string.IsNullOrWhiteSpace(tag.Id) || string.IsNullOrWhiteSpace(tag.Name))
            throw new ArgumentException("A metadata tag id and name are required.", nameof(tag));
        if (!_tags.TryAdd(tag.Id, tag))
            throw new InvalidOperationException($"Metadata tag '{tag.Id}' is already registered.");
        return new MetadataServiceTagHandle(Endpoint, tag);
    }

    public MetadataServiceStudioHandle CreateStudio(MetadataServiceRemoteStudio studio)
    {
        ArgumentNullException.ThrowIfNull(studio);
        if (string.IsNullOrWhiteSpace(studio.Id) || string.IsNullOrWhiteSpace(studio.Name))
            throw new ArgumentException("A metadata studio id and name are required.", nameof(studio));
        if (!_studios.TryAdd(studio.Id, studio))
            throw new InvalidOperationException($"Metadata studio '{studio.Id}' is already registered.");
        return new MetadataServiceStudioHandle(Endpoint, studio);
    }

    public void SetFingerprintSyncSource(IReadOnlyList<MetadataServiceFingerprintSourceVideo> videos)
    {
        ArgumentNullException.ThrowIfNull(videos);
        if (videos.Any(video => video.Fingerprints.Any(fingerprint =>
                string.IsNullOrWhiteSpace(fingerprint.Type) || string.IsNullOrWhiteSpace(fingerprint.Value))))
        {
            throw new ArgumentException("Every source fingerprint requires a type and value.", nameof(videos));
        }

        _fingerprintSyncVideos.Clear();
        for (var index = 0; index < videos.Count; index++)
            _fingerprintSyncVideos.TryAdd(index, videos[index]);
    }

    internal void Reset()
    {
        _requestBlocker.Release();
        _performers.Clear();
        _studios.Clear();
        _tags.Clear();
        _scenes.Clear();
        _fingerprintSyncVideos.Clear();
        _submissions.Reset();
    }

    internal MetadataServiceRequestGate HoldNextRequestContaining(string queryMarker)
        => _requestBlocker.HoldNext(queryMarker);

    internal void ReleaseBlockedRequests()
        => _requestBlocker.Release();

    public async ValueTask DisposeAsync()
    {
        _requestBlocker.Release();
        await _application.StopAsync();
        await _application.DisposeAsync();
    }

    private static async Task HandleRequestAsync(
        HttpContext context,
        ConcurrentDictionary<string, MetadataServicePerformer> performers,
        ConcurrentDictionary<string, MetadataServiceRemoteStudio> studios,
        ConcurrentDictionary<string, MetadataServiceRemoteTag> tags,
        ConcurrentDictionary<string, MetadataServiceScene> scenes,
        ConcurrentDictionary<int, MetadataServiceFingerprintSourceVideo> fingerprintSyncVideos,
        MetadataServiceSubmissionLog submissions,
        MetadataServiceRequestBlocker requestBlocker)
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
        if (request is null)
        {
            await WriteGraphQlErrorAsync(context, "The simulator requires a GraphQL request.");
            return;
        }

        await requestBlocker.WaitIfMatchedAsync(request.Query, context.RequestAborted);

        if (request.Query.Contains("query Me", StringComparison.Ordinal)
            && request.Query.Contains("me", StringComparison.Ordinal)
            && request.Query.Contains("name", StringComparison.Ordinal))
        {
            await context.Response.WriteAsJsonAsync(
                new { data = new { me = new { name = "API test metadata user" } } },
                ApiJson.Options,
                context.RequestAborted);
            return;
        }

        if (request.Query.Contains("query SearchPerformer", StringComparison.Ordinal)
            && request.Query.Contains("searchPerformer(term: $term)", StringComparison.Ordinal))
        {
            await HandlePerformerSearchAsync(context, request, performers);
            return;
        }

        if (request.Query.Contains("query FindPerformerByID", StringComparison.Ordinal)
            && request.Query.Contains("findPerformer(id: $id)", StringComparison.Ordinal))
        {
            await HandlePerformerFindAsync(context, request, performers);
            return;
        }

        if (request.Query.Contains("query FindTag", StringComparison.Ordinal)
            && request.Query.Contains("findTag(id: $id, name: $name)", StringComparison.Ordinal))
        {
            await HandleTagFindAsync(context, request, tags);
            return;
        }

        if (request.Query.Contains("query SearchStudio", StringComparison.Ordinal)
            && request.Query.Contains("searchStudio(term: $term)", StringComparison.Ordinal))
        {
            await HandleStudioSearchAsync(context, request, studios);
            return;
        }

        if (request.Query.Contains("query FindStudio", StringComparison.Ordinal)
            && request.Query.Contains("findStudio(id: $id, name: $name)", StringComparison.Ordinal))
        {
            await HandleStudioFindAsync(context, request, studios);
            return;
        }

        if (request.Query.Contains("query SearchVideo", StringComparison.Ordinal)
            && request.Query.Contains("searchVideo: searchScene(term: $term)", StringComparison.Ordinal))
        {
            await HandleVideoSearchAsync(context, request, scenes);
            return;
        }

        if (request.Query.Contains("query FindVideosByVideoFingerprints", StringComparison.Ordinal))
        {
            if (!request.Query.Contains("findVideosByVideoFingerprints: findScenesBySceneFingerprints", StringComparison.Ordinal)
                || !request.Query.Contains("fingerprints {", StringComparison.Ordinal)
                || !request.Query.Contains("algorithm", StringComparison.Ordinal)
                || !request.Query.Contains("hash", StringComparison.Ordinal))
            {
                await WriteGraphQlErrorAsync(context, "Fingerprint search must select remote fingerprint algorithms and hashes.");
                return;
            }

            await HandleVideoFingerprintSearchAsync(context, request, scenes);
            return;
        }

        if (request.Query.Contains("query FindVideos($filter: FindFilterType!)", StringComparison.Ordinal))
        {
            if (!request.Query.Contains("findVideos(filter: $filter)", StringComparison.Ordinal)
                || !request.Query.Contains("files {", StringComparison.Ordinal)
                || !request.Query.Contains("fingerprints {", StringComparison.Ordinal)
                || !request.Query.Contains("type", StringComparison.Ordinal)
                || !request.Query.Contains("value", StringComparison.Ordinal))
            {
                await WriteGraphQlErrorAsync(context, "Fingerprint sync must select files and fingerprint types and values.");
                return;
            }

            await HandleFingerprintSyncSourceAsync(context, request, fingerprintSyncVideos);
            return;
        }

        if (request.Query.Contains("mutation SubmitFingerprint", StringComparison.Ordinal)
            && request.Query.Contains("submitFingerprint(input: $input)", StringComparison.Ordinal))
        {
            await HandleFingerprintSubmissionAsync(context, request, submissions);
            return;
        }

        if (request.Query.Contains("mutation SubmitSceneDraft", StringComparison.Ordinal)
            && request.Query.Contains("submitSceneDraft(input: $input)", StringComparison.Ordinal))
        {
            await HandleSceneDraftSubmissionAsync(context, request, submissions);
            return;
        }

        if (request.Query.Contains("mutation SubmitPerformerDraft", StringComparison.Ordinal)
            && request.Query.Contains("submitPerformerDraft(input: $input)", StringComparison.Ordinal))
        {
            await HandlePerformerDraftSubmissionAsync(context, request, submissions);
            return;
        }

        if (request.Query.Contains("mutation SubmitTagDraft", StringComparison.Ordinal)
            && request.Query.Contains("submitTagDraft(input: $input)", StringComparison.Ordinal))
        {
            await HandleTagDraftSubmissionAsync(context, request, submissions);
            return;
        }

        if (request.Query.Contains("mutation SubmitStudioDraft", StringComparison.Ordinal)
            && request.Query.Contains("submitStudioDraft(input: $input)", StringComparison.Ordinal))
        {
            await HandleStudioDraftSubmissionAsync(context, request, submissions);
            return;
        }

        if (!request.Query.Contains("query FindVideoByID", StringComparison.Ordinal)
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
        var remoteScene = scene is null ? null : ToRemoteScene(scene);

        await context.Response.WriteAsJsonAsync(
            new { data = new { findVideo = remoteScene } },
            ApiJson.Options,
            context.RequestAborted);
    }

    private static async Task HandleVideoSearchAsync(
        HttpContext context,
        GraphQlRequest request,
        ConcurrentDictionary<string, MetadataServiceScene> scenes)
    {
        if (!request.Variables.TryGetProperty("term", out var termProperty)
            || string.IsNullOrWhiteSpace(termProperty.GetString()))
        {
            await WriteGraphQlErrorAsync(context, "SearchVideo requires a term variable.");
            return;
        }

        var term = termProperty.GetString()!;
        var matches = scenes.Values
            .Where(scene => scene.Title.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(scene => scene.Title, StringComparer.OrdinalIgnoreCase)
            .Select(ToRemoteScene)
            .ToArray();

        await context.Response.WriteAsJsonAsync(
            new { data = new { searchVideo = matches } },
            ApiJson.Options,
            context.RequestAborted);
    }

    private static async Task HandleFingerprintSubmissionAsync(
        HttpContext context,
        GraphQlRequest request,
        MetadataServiceSubmissionLog submissions)
    {
        if (!request.Variables.TryGetProperty("input", out var input)
            || input.ValueKind != JsonValueKind.Object)
        {
            await WriteGraphQlErrorAsync(context, "SubmitFingerprint requires an input object.");
            return;
        }

        submissions.RecordFingerprint(input);
        await context.Response.WriteAsJsonAsync(
            new { data = new { submitFingerprint = true } },
            ApiJson.Options,
            context.RequestAborted);
    }

    private static async Task HandleSceneDraftSubmissionAsync(
        HttpContext context,
        GraphQlRequest request,
        MetadataServiceSubmissionLog submissions)
    {
        if (!request.Variables.TryGetProperty("input", out var input)
            || input.ValueKind != JsonValueKind.Object)
        {
            await WriteGraphQlErrorAsync(context, "SubmitSceneDraft requires an input object.");
            return;
        }

        var submission = submissions.RecordSceneDraft(input);
        await context.Response.WriteAsJsonAsync(
            new { data = new { submitSceneDraft = new { id = submission.DraftId } } },
            ApiJson.Options,
            context.RequestAborted);
    }

    private static async Task HandlePerformerDraftSubmissionAsync(
        HttpContext context,
        GraphQlRequest request,
        MetadataServiceSubmissionLog submissions)
    {
        if (!request.Variables.TryGetProperty("input", out var input)
            || input.ValueKind != JsonValueKind.Object)
        {
            await WriteGraphQlErrorAsync(context, "SubmitPerformerDraft requires an input object.");
            return;
        }

        var submission = submissions.RecordPerformerDraft(input);
        await context.Response.WriteAsJsonAsync(
            new { data = new { submitPerformerDraft = new { id = submission.DraftId } } },
            ApiJson.Options,
            context.RequestAborted);
    }

    private static async Task HandleVideoFingerprintSearchAsync(
        HttpContext context,
        GraphQlRequest request,
        ConcurrentDictionary<string, MetadataServiceScene> scenes)
    {
        if (!request.Variables.TryGetProperty("fingerprints", out var fingerprintBatches)
            || fingerprintBatches.ValueKind != JsonValueKind.Array)
        {
            await WriteGraphQlErrorAsync(context, "FindVideosByVideoFingerprints requires fingerprint batches.");
            return;
        }

        var matchesByBatch = new List<object[]>();
        foreach (var batch in fingerprintBatches.EnumerateArray())
        {
            if (batch.ValueKind != JsonValueKind.Array)
            {
                await WriteGraphQlErrorAsync(context, "Each fingerprint batch must be an array.");
                return;
            }

            var requestedFingerprints = batch
                .EnumerateArray()
                .Where(fingerprint => fingerprint.TryGetProperty("algorithm", out _)
                    && fingerprint.TryGetProperty("hash", out _))
                .Select(fingerprint => (
                    Algorithm: fingerprint.GetProperty("algorithm").GetString(),
                    Hash: fingerprint.GetProperty("hash").GetString()))
                .Where(fingerprint => !string.IsNullOrWhiteSpace(fingerprint.Algorithm)
                    && !string.IsNullOrWhiteSpace(fingerprint.Hash))
                .ToList();

            matchesByBatch.Add(scenes.Values
                .Where(scene => scene.Fingerprints.Any(sceneFingerprint => requestedFingerprints.Any(requested =>
                    string.Equals(sceneFingerprint.Algorithm, requested.Algorithm, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(sceneFingerprint.Hash, requested.Hash, StringComparison.OrdinalIgnoreCase))))
                .OrderBy(scene => scene.Id, StringComparer.Ordinal)
                .Select(scene => (object)ToRemoteScene(scene))
                .ToArray());
        }

        await context.Response.WriteAsJsonAsync(
            new { data = new { findVideosByVideoFingerprints = matchesByBatch } },
            ApiJson.Options,
            context.RequestAborted);
    }

    private static async Task HandleFingerprintSyncSourceAsync(
        HttpContext context,
        GraphQlRequest request,
        ConcurrentDictionary<int, MetadataServiceFingerprintSourceVideo> fingerprintSyncVideos)
    {
        if (!request.Variables.TryGetProperty("filter", out var filter)
            || !filter.TryGetProperty("page", out var pageProperty)
            || !filter.TryGetProperty("per_page", out var perPageProperty)
            || !pageProperty.TryGetInt32(out var page)
            || !perPageProperty.TryGetInt32(out var perPage)
            || page < 1
            || perPage < 1)
        {
            await WriteGraphQlErrorAsync(context, "FindVideos requires a positive page and per_page filter.");
            return;
        }

        var sourceVideos = fingerprintSyncVideos.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToList();
        var videos = sourceVideos
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .Select(video => new
            {
                files = new[]
                {
                    new
                    {
                        fingerprints = video.Fingerprints.Select(fingerprint => new
                        {
                            type = fingerprint.Type,
                            value = fingerprint.Value,
                        }),
                    },
                },
            })
            .ToArray();

        await context.Response.WriteAsJsonAsync(
            new { data = new { findVideos = new { count = sourceVideos.Count, videos } } },
            ApiJson.Options,
            context.RequestAborted);
    }

    private static object ToRemoteScene(MetadataServiceScene scene)
        => new
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
            fingerprints = scene.Fingerprints.Select(fingerprint => new
            {
                algorithm = fingerprint.Algorithm,
                hash = fingerprint.Hash,
                duration = fingerprint.Duration,
            }),
        };

    private static async Task HandlePerformerSearchAsync(
        HttpContext context,
        GraphQlRequest request,
        ConcurrentDictionary<string, MetadataServicePerformer> performers)
    {
        if (!request.Variables.TryGetProperty("term", out var termProperty)
            || string.IsNullOrWhiteSpace(termProperty.GetString()))
        {
            await WriteGraphQlErrorAsync(context, "SearchPerformer requires a term variable.");
            return;
        }

        var term = termProperty.GetString()!;
        var matches = performers.Values
            .Where(performer => performer.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || performer.Aliases.Any(alias => alias.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(performer => performer.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToRemotePerformer)
            .ToArray();

        await context.Response.WriteAsJsonAsync(
            new { data = new { searchPerformer = matches } },
            ApiJson.Options,
            context.RequestAborted);
    }

    private static async Task HandlePerformerFindAsync(
        HttpContext context,
        GraphQlRequest request,
        ConcurrentDictionary<string, MetadataServicePerformer> performers)
    {
        if (!request.Variables.TryGetProperty("id", out var idProperty)
            || string.IsNullOrWhiteSpace(idProperty.GetString()))
        {
            await WriteGraphQlErrorAsync(context, "FindPerformerByID requires an id variable.");
            return;
        }

        performers.TryGetValue(idProperty.GetString()!, out var performer);
        await context.Response.WriteAsJsonAsync(
            new { data = new { findPerformer = performer is null ? null : ToRemotePerformer(performer) } },
            ApiJson.Options,
            context.RequestAborted);
    }

    private static async Task HandleTagFindAsync(HttpContext context, GraphQlRequest request, ConcurrentDictionary<string, MetadataServiceRemoteTag> tags)
    {
        MetadataServiceRemoteTag? tag = null;
        if (request.Variables.TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString()))
            tags.TryGetValue(id.GetString()!, out tag);
        else if (request.Variables.TryGetProperty("name", out var name) && !string.IsNullOrWhiteSpace(name.GetString()))
            tag = tags.Values.FirstOrDefault(candidate => string.Equals(candidate.Name, name.GetString(), StringComparison.OrdinalIgnoreCase));
        await context.Response.WriteAsJsonAsync(new { data = new { findTag = tag is null ? null : ToRemoteTag(tag) } }, ApiJson.Options, context.RequestAborted);
    }

    private static async Task HandleTagDraftSubmissionAsync(HttpContext context, GraphQlRequest request, MetadataServiceSubmissionLog submissions)
    {
        if (!request.Variables.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
        {
            await WriteGraphQlErrorAsync(context, "SubmitTagDraft requires an input object.");
            return;
        }
        var submission = submissions.RecordTagDraft(input);
        await context.Response.WriteAsJsonAsync(new { data = new { submitTagDraft = new { id = submission.DraftId } } }, ApiJson.Options, context.RequestAborted);
    }

    private static object ToRemoteTag(MetadataServiceRemoteTag tag) => new { id = tag.Id, name = tag.Name, description = tag.Description, aliases = tag.Aliases };

    private static async Task HandleStudioSearchAsync(
        HttpContext context,
        GraphQlRequest request,
        ConcurrentDictionary<string, MetadataServiceRemoteStudio> studios)
    {
        if (!request.Variables.TryGetProperty("term", out var term) || string.IsNullOrWhiteSpace(term.GetString()))
        {
            await WriteGraphQlErrorAsync(context, "SearchStudio requires a term variable.");
            return;
        }
        var matches = studios.Values
            .Where(studio => studio.Name.Contains(term.GetString()!, StringComparison.OrdinalIgnoreCase))
            .OrderBy(studio => studio.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToRemoteStudio)
            .ToArray();
        await context.Response.WriteAsJsonAsync(
            new { data = new { searchStudio = matches } },
            ApiJson.Options,
            context.RequestAborted);
    }

    private static async Task HandleStudioFindAsync(
        HttpContext context,
        GraphQlRequest request,
        ConcurrentDictionary<string, MetadataServiceRemoteStudio> studios)
    {
        MetadataServiceRemoteStudio? studio = null;
        if (request.Variables.TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString()))
            studios.TryGetValue(id.GetString()!, out studio);
        else if (request.Variables.TryGetProperty("name", out var name)
            && !string.IsNullOrWhiteSpace(name.GetString()))
        {
            studio = studios.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name.GetString(), StringComparison.OrdinalIgnoreCase));
        }
        await context.Response.WriteAsJsonAsync(
            new { data = new { findStudio = studio is null ? null : ToRemoteStudio(studio) } },
            ApiJson.Options,
            context.RequestAborted);
    }

    private static async Task HandleStudioDraftSubmissionAsync(
        HttpContext context,
        GraphQlRequest request,
        MetadataServiceSubmissionLog submissions)
    {
        if (!request.Variables.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
        {
            await WriteGraphQlErrorAsync(context, "SubmitStudioDraft requires an input object.");
            return;
        }
        var submission = submissions.RecordStudioDraft(input);
        await context.Response.WriteAsJsonAsync(
            new { data = new { submitStudioDraft = new { id = submission.DraftId } } },
            ApiJson.Options,
            context.RequestAborted);
    }

    private static object ToRemoteStudio(MetadataServiceRemoteStudio studio) => new
    {
        id = studio.Id,
        name = studio.Name,
        aliases = studio.Aliases,
        urls = studio.Urls.Select(url => new { url }),
        images = Array.Empty<object>(),
        parent = studio.Parent is null ? null : new { id = studio.Parent.Id, name = studio.Parent.Name },
    };

    private static object ToRemotePerformer(MetadataServicePerformer performer)
        => new
        {
            id = performer.Id,
            name = performer.Name,
            disambiguation = performer.Disambiguation,
            aliases = performer.Aliases,
            gender = performer.Gender,
            deleted = false,
            merged_into_id = (string?)null,
            urls = performer.Urls.Select(url => new { url }),
            images = Array.Empty<object>(),
            birth_date = performer.BirthDate,
            death_date = (string?)null,
            ethnicity = performer.Ethnicity,
            country = performer.Country,
            eye_color = performer.EyeColor,
            hair_color = performer.HairColor,
            height = performer.HeightCm,
            measurements = (object?)null,
            breast_type = (string?)null,
            career_start_year = performer.CareerStartYear,
            career_end_year = (int?)null,
            tattoos = Array.Empty<object>(),
            piercings = Array.Empty<object>(),
        };

    private static Task WriteGraphQlErrorAsync(HttpContext context, string message)
        => context.Response.WriteAsJsonAsync(
            new { errors = new[] { new { message } } },
            ApiJson.Options,
            context.RequestAborted);

    private sealed class MetadataServiceSubmissionLog
    {
        private readonly ConcurrentQueue<MetadataServiceFingerprintSubmission> _fingerprintSubmissions = new();
        private readonly ConcurrentQueue<MetadataServiceSceneDraftSubmission> _sceneDraftSubmissions = new();
        private readonly ConcurrentQueue<MetadataServicePerformerDraftSubmission> _performerDraftSubmissions = new();
        private readonly ConcurrentQueue<MetadataServiceTagDraftSubmission> _tagDraftSubmissions = new();
        private readonly ConcurrentQueue<MetadataServiceStudioDraftSubmission> _studioDraftSubmissions = new();
        private int _nextDraftNumber;

        public IReadOnlyList<MetadataServiceFingerprintSubmission> FingerprintSubmissions
            => _fingerprintSubmissions.ToArray();

        public IReadOnlyList<MetadataServiceSceneDraftSubmission> SceneDraftSubmissions
            => _sceneDraftSubmissions.ToArray();

        public IReadOnlyList<MetadataServicePerformerDraftSubmission> PerformerDraftSubmissions
            => _performerDraftSubmissions.ToArray();
        public IReadOnlyList<MetadataServiceTagDraftSubmission> TagDraftSubmissions => _tagDraftSubmissions.ToArray();
        public IReadOnlyList<MetadataServiceStudioDraftSubmission> StudioDraftSubmissions
            => _studioDraftSubmissions.ToArray();

        public void RecordFingerprint(JsonElement input)
            => _fingerprintSubmissions.Enqueue(new MetadataServiceFingerprintSubmission(input.Clone()));

        public MetadataServiceSceneDraftSubmission RecordSceneDraft(JsonElement input)
        {
            var submission = new MetadataServiceSceneDraftSubmission(
                $"draft-{Interlocked.Increment(ref _nextDraftNumber)}",
                input.Clone());
            _sceneDraftSubmissions.Enqueue(submission);
            return submission;
        }

        public MetadataServicePerformerDraftSubmission RecordPerformerDraft(JsonElement input)
        {
            var submission = new MetadataServicePerformerDraftSubmission(
                $"draft-{Interlocked.Increment(ref _nextDraftNumber)}",
                input.Clone());
            _performerDraftSubmissions.Enqueue(submission);
            return submission;
        }

        public MetadataServiceTagDraftSubmission RecordTagDraft(JsonElement input)
        {
            var submission = new MetadataServiceTagDraftSubmission($"draft-{Interlocked.Increment(ref _nextDraftNumber)}", input.Clone());
            _tagDraftSubmissions.Enqueue(submission);
            return submission;
        }

        public MetadataServiceStudioDraftSubmission RecordStudioDraft(JsonElement input)
        {
            var submission = new MetadataServiceStudioDraftSubmission(
                $"draft-{Interlocked.Increment(ref _nextDraftNumber)}",
                input.Clone());
            _studioDraftSubmissions.Enqueue(submission);
            return submission;
        }

        public void Reset()
        {
            _fingerprintSubmissions.Clear();
            _sceneDraftSubmissions.Clear();
            _performerDraftSubmissions.Clear();
            _tagDraftSubmissions.Clear();
            _studioDraftSubmissions.Clear();
            Interlocked.Exchange(ref _nextDraftNumber, 0);
        }
    }

    private sealed class MetadataServiceRequestBlocker
    {
        private readonly object _lock = new();
        private MetadataServiceRequestGate? _gate;

        public MetadataServiceRequestGate HoldNext(string queryMarker)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(queryMarker);

            lock (_lock)
            {
                if (_gate is { IsReleased: false })
                    throw new InvalidOperationException("A metadata-service request gate is already active.");

                _gate = new MetadataServiceRequestGate(queryMarker);
                return _gate;
            }
        }

        public async Task WaitIfMatchedAsync(string query, CancellationToken cancellationToken)
        {
            MetadataServiceRequestGate? gate;
            lock (_lock)
                gate = _gate;

            if (gate?.TryConsume(query) == true)
                await gate.WaitForReleaseAsync(cancellationToken);
        }

        public void Release()
        {
            MetadataServiceRequestGate? gate;
            lock (_lock)
            {
                gate = _gate;
                _gate = null;
            }

            gate?.Release();
        }
    }

    private sealed record GraphQlRequest(string Query, JsonElement Variables);
}

internal sealed class MetadataServiceRequestGate : IDisposable
{
    private readonly string _queryMarker;
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _consumed;
    private int _isReleased;

    internal MetadataServiceRequestGate(string queryMarker)
        => _queryMarker = queryMarker;

    internal bool IsReleased => Volatile.Read(ref _isReleased) != 0;

    public async Task WaitUntilBlockedAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await _reached.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"No metadata-service GraphQL request containing '{_queryMarker}' arrived within 10 seconds.");
        }
    }

    public void Release()
    {
        Interlocked.Exchange(ref _isReleased, 1);
        _released.TrySetResult();
    }

    public void Dispose()
        => Release();

    internal bool TryConsume(string query)
    {
        if (!query.Contains(_queryMarker, StringComparison.Ordinal)
            || Interlocked.CompareExchange(ref _consumed, 1, 0) != 0)
        {
            return false;
        }

        _reached.TrySetResult();
        return true;
    }

    internal Task WaitForReleaseAsync(CancellationToken cancellationToken)
        => _released.Task.WaitAsync(cancellationToken);
}

public sealed record MetadataServiceScene(string Id, string Title, IReadOnlyList<MetadataServiceTag> Tags)
{
    public MetadataServiceScene(
        string id,
        string title,
        IReadOnlyList<MetadataServiceTag> tags,
        IReadOnlyList<MetadataServiceFingerprint> fingerprints)
        : this(id, title, tags)
        => Fingerprints = fingerprints;

    public IReadOnlyList<MetadataServiceFingerprint> Fingerprints { get; init; } = [];
}

public sealed record MetadataServiceTag(string Id, string Name);

public sealed record MetadataServiceFingerprint(string Algorithm, string Hash, int? Duration = null);

public sealed record MetadataServiceFingerprintSourceVideo(IReadOnlyList<MetadataServiceFingerprintSourceEntry> Fingerprints);

public sealed record MetadataServiceFingerprintSourceEntry(string Type, string Value);

public sealed record MetadataServiceFingerprintSubmission(JsonElement Input);

public sealed record MetadataServiceSceneDraftSubmission(string DraftId, JsonElement Input);

public sealed record MetadataServicePerformerDraftSubmission(string DraftId, JsonElement Input);

public sealed record MetadataServiceTagDraftSubmission(string DraftId, JsonElement Input);

public sealed record MetadataServiceStudioDraftSubmission(string DraftId, JsonElement Input);

public sealed record MetadataServiceRemoteTag(string Id, string Name, string? Description, IReadOnlyList<string> Aliases);

public sealed record MetadataServiceRemoteStudio(
    string Id,
    string Name,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Urls,
    MetadataServiceStudioParent? Parent = null);

public sealed record MetadataServiceStudioParent(string Id, string Name);

public sealed record MetadataServicePerformer(
    string Id,
    string Name,
    string? Disambiguation,
    IReadOnlyList<string> Aliases,
    string? Gender,
    string? BirthDate,
    string? Ethnicity,
    string? Country,
    string? EyeColor,
    string? HairColor,
    int? HeightCm,
    int? CareerStartYear,
    IReadOnlyList<string> Urls);

public sealed record MetadataServiceSceneHandle(
    Uri Endpoint,
    MetadataServiceScene Scene)
{
    public string Id => Scene.Id;
}

public sealed record MetadataServicePerformerHandle(
    Uri Endpoint,
    MetadataServicePerformer Performer)
{
    public string Id => Performer.Id;
}

public sealed record MetadataServiceTagHandle(Uri Endpoint, MetadataServiceRemoteTag Tag)
{
    public string Id => Tag.Id;
}

public sealed record MetadataServiceStudioHandle(Uri Endpoint, MetadataServiceRemoteStudio Studio)
{
    public string Id => Studio.Id;
}
