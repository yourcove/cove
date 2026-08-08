using System.Net;
using System.Text;
using System.Text.Json;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public sealed class MetadataServerServiceTests
{
    private const string Endpoint = "https://metadata.example/graphql";
    private const string ApiKey = "fixture-key";

    [Fact]
    public async Task SearchVideosAsync_MapsGraphQlFixtureAndLocalCandidates()
    {
        await using var context = CreateContext();
        context.Studios.Add(new Studio { Name = "Fixture Studio" });
        context.Performers.Add(new Performer { Name = "Jane Doe" });
        context.Tags.Add(new Tag { Name = "Action" });
        await context.SaveChangesAsync();

        var video = new Video { Title = "Local Video" };
        video.Files.Add(new VideoFile { Duration = 118 });

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(request =>
        {
            Assert.Equal(ApiKey, request.ApiKey);
            Assert.Contains("query SearchVideo", request.Query);
            Assert.Equal("Remote Video", GetVariableString(request, "term"));

            return GraphQlData($$"""
                "searchVideo": [{{RemoteVideoJson}}]
                """);
        }));

        var service = CreateService(context, httpClient);

        var matches = await service.SearchVideosAsync(video, "Remote Video", Endpoint, VideoMetadataSearchStrategy.Text, CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal("remote-video-1", match.Id);
        Assert.Equal("Fixture Box", match.MetadataServerName);
        Assert.Equal("Remote Video", match.Title);
        Assert.Equal("Fixture Studio", match.StudioName);
        Assert.Equal(["Jane Doe"], match.PerformerNames);
        Assert.Equal(["Action"], match.TagNames);
        Assert.NotNull(match.StudioCandidate);
        Assert.True(match.StudioCandidate.ExistsLocally);
        Assert.Contains(match.PerformerCandidates, candidate => candidate.Name == "Jane Doe" && candidate.ExistsLocally);
        Assert.Contains(match.TagCandidates, candidate => candidate.Name == "Action" && candidate.ExistsLocally);
    }

    [Fact]
    public async Task SearchVideosAsync_FingerprintOnly_DoesNotUseRemoteIdOrTextFallback()
    {
        await using var context = CreateContext();
        var video = new Video { Title = "Local Video" };
        var file = new VideoFile { Duration = 118 };
        file.Fingerprints.Add(new FileFingerprint { Type = "oshash", Value = "1a2b" });
        video.Files.Add(file);
        video.RemoteIds.Add(new VideoRemoteId { Endpoint = Endpoint, RemoteId = "existing-remote-id" });

        var handler = new FixtureMetadataServerHandler(request =>
        {
            Assert.Contains("query FindVideosByVideoFingerprints", request.Query);
            Assert.DoesNotContain("query FindVideoByID", request.Query);
            Assert.DoesNotContain("query SearchVideo", request.Query);
            return GraphQlData("\"findVideosByVideoFingerprints\": [[]]");
        });
        using var httpClient = new HttpClient(handler);
        var service = CreateService(context, httpClient);

        var matches = await service.SearchVideosAsync(video, null, Endpoint, VideoMetadataSearchStrategy.Fingerprint, CancellationToken.None);

        Assert.Empty(matches);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SearchVideosAsync_RemoteIdMatchStopsBeforeFingerprintAndUsesEquivalentEndpoint()
    {
        await using var context = CreateContext();
        var video = CreateSearchStrategyVideo();
        video.RemoteIds.Add(new VideoRemoteId { Endpoint = "https://api.metadata.example/graphql", RemoteId = "remote-video-1" });
        var handler = new FixtureMetadataServerHandler(request =>
        {
            Assert.Contains("query FindVideoByID", request.Query);
            return GraphQlData($$"""
                "findVideo": {{RemoteVideoJson}}
                """);
        });
        using var httpClient = new HttpClient(handler);
        var service = CreateService(context, httpClient);

        var matches = await service.SearchVideosAsync(video, "Local Video", Endpoint, VideoMetadataSearchStrategy.RemoteIdFingerprint, CancellationToken.None);

        Assert.Single(matches);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SearchVideosAsync_RemoteIdFingerprint_DoesNotUseTextFallback()
    {
        await using var context = CreateContext();
        var video = CreateSearchStrategyVideo();
        video.RemoteIds.Add(new VideoRemoteId { Endpoint = Endpoint, RemoteId = "missing-video" });
        var handler = new FixtureMetadataServerHandler(request =>
        {
            if (request.Query.Contains("query FindVideoByID", StringComparison.Ordinal))
                return GraphQlData("\"findVideo\": null");

            Assert.Contains("query FindVideosByVideoFingerprints", request.Query);
            Assert.DoesNotContain("query SearchVideo", request.Query);
            return GraphQlData("\"findVideosByVideoFingerprints\": [[]]");
        });
        using var httpClient = new HttpClient(handler);
        var service = CreateService(context, httpClient);

        var matches = await service.SearchVideosAsync(video, "Local Video", Endpoint, VideoMetadataSearchStrategy.RemoteIdFingerprint, CancellationToken.None);

        Assert.Empty(matches);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SearchVideosAsync_RemoteIdOnly_DoesNotUseFingerprintOrTextFallback()
    {
        await using var context = CreateContext();
        var video = CreateSearchStrategyVideo();
        video.RemoteIds.Add(new VideoRemoteId { Endpoint = Endpoint, RemoteId = "missing-video" });
        var handler = new FixtureMetadataServerHandler(request =>
        {
            Assert.Contains("query FindVideoByID", request.Query);
            Assert.DoesNotContain("query FindVideosByVideoFingerprints", request.Query);
            Assert.DoesNotContain("query SearchVideo", request.Query);
            return GraphQlData("\"findVideo\": null");
        });
        using var httpClient = new HttpClient(handler);
        var service = CreateService(context, httpClient);

        var matches = await service.SearchVideosAsync(video, "Local Video", Endpoint, VideoMetadataSearchStrategy.RemoteId, CancellationToken.None);

        Assert.Empty(matches);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SearchVideosAsync_CombinedStrategyFallsBackInOrderToText()
    {
        await using var context = CreateContext();
        var video = CreateSearchStrategyVideo();
        video.RemoteIds.Add(new VideoRemoteId { Endpoint = Endpoint, RemoteId = "missing-video" });
        var handler = new FixtureMetadataServerHandler(request =>
        {
            if (request.Query.Contains("query FindVideoByID", StringComparison.Ordinal))
                return GraphQlData("\"findVideo\": null");
            if (request.Query.Contains("query FindVideosByVideoFingerprints", StringComparison.Ordinal))
                return GraphQlData("\"findVideosByVideoFingerprints\": [[]]");

            Assert.Contains("query SearchVideo", request.Query);
            Assert.Equal("Local Video", GetVariableString(request, "term"));
            return GraphQlData($$"""
                "searchVideo": [{{RemoteVideoJson}}]
                """);
        });
        using var httpClient = new HttpClient(handler);
        var service = CreateService(context, httpClient);

        var matches = await service.SearchVideosAsync(video, "Local Video", Endpoint, VideoMetadataSearchStrategy.RemoteIdAndFingerprintThenText, CancellationToken.None);

        Assert.Single(matches);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("query FindVideoByID", handler.Requests[0].Query);
        Assert.Contains("query FindVideosByVideoFingerprints", handler.Requests[1].Query);
        Assert.Contains("query SearchVideo", handler.Requests[2].Query);
    }

    [Fact]
    public async Task SearchVideosAsync_CombinedStrategyKeepsFingerprintCandidateAlongsideStaleRemoteId()
    {
        await using var context = CreateContext();
        var video = CreateSearchStrategyVideo();
        video.RemoteIds.Add(new VideoRemoteId { Endpoint = Endpoint, RemoteId = "stale-video" });
        var staleVideoJson = RemoteVideoJson.Replace("remote-video-1", "stale-video", StringComparison.Ordinal);
        var handler = new FixtureMetadataServerHandler(request =>
        {
            if (request.Query.Contains("query FindVideoByID", StringComparison.Ordinal))
                return GraphQlData($$"""
                    "findVideo": {{staleVideoJson}}
                    """);

            Assert.Contains("query FindVideosByVideoFingerprints", request.Query);
            return GraphQlData($$"""
                "findVideosByVideoFingerprints": [[{{RemoteVideoJson}}]]
                """);
        });
        using var httpClient = new HttpClient(handler);
        var service = CreateService(context, httpClient);

        var matches = await service.SearchVideosAsync(video, null, Endpoint, VideoMetadataSearchStrategy.RemoteIdAndFingerprintThenText, CancellationToken.None);

        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, match => match.Id == "stale-video");
        Assert.Contains(matches, match => match.Id == "remote-video-1");
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SearchVideosAsync_OmittedStrategyKeepsCombinedCandidateBehavior()
    {
        await using var context = CreateContext();
        var video = CreateSearchStrategyVideo();
        video.RemoteIds.Add(new VideoRemoteId { Endpoint = Endpoint, RemoteId = "stale-video" });
        var staleVideoJson = RemoteVideoJson.Replace("remote-video-1", "stale-video", StringComparison.Ordinal);
        var handler = new FixtureMetadataServerHandler(request =>
        {
            if (request.Query.Contains("query FindVideoByID", StringComparison.Ordinal))
                return GraphQlData($$"""
                    "findVideo": {{staleVideoJson}}
                    """);

            Assert.Contains("query FindVideosByVideoFingerprints", request.Query);
            return GraphQlData($$"""
                "findVideosByVideoFingerprints": [[{{RemoteVideoJson}}]]
                """);
        });
        using var httpClient = new HttpClient(handler);
        var service = CreateService(context, httpClient);

        var matches = await service.SearchVideosAsync(video, null, Endpoint, null, CancellationToken.None);

        Assert.Equal(2, matches.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task NonStrictSearches_LogOneWarningOnlyWhenAllMetadataServersFail()
    {
        await using var context = CreateContext();
        var logger = new RecordingLogger<MetadataServerService>();
        using var httpClient = new HttpClient(new FailingMetadataServerHandler());
        var configuration = new CoveConfiguration
        {
            Scraping = new ScrapingConfig
            {
                MetadataServers =
                [
                    new MetadataServerInstance { Endpoint = "https://one.example/graphql", ApiKey = ApiKey, Name = "One" },
                    new MetadataServerInstance { Endpoint = "https://two.example/graphql", ApiKey = ApiKey, Name = "Two" },
                ],
            },
        };
        var service = CreateService(context, httpClient, configuration: configuration, logger: logger);
        var video = new Video { Title = "Local Video" };

        Assert.Empty(await service.SearchPerformersAsync("term", null, CancellationToken.None));
        Assert.Empty(await service.SearchStudiosAsync("term", null, CancellationToken.None));
        Assert.Empty(await service.SearchTagsAsync("term", null, CancellationToken.None));
        Assert.Empty(await service.SearchVideosAsync(video, "term", null, VideoMetadataSearchStrategy.Text, CancellationToken.None));

        Assert.Equal(8, logger.Entries.Count(entry => entry.Level == LogLevel.Debug));
        Assert.Equal(4, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
    }

    [Fact]
    public async Task StrictTagSearch_ReturnsEmptyAndLogsWarningWhenEndpointFails()
    {
        await using var context = CreateContext();
        var logger = new RecordingLogger<MetadataServerService>();
        using var httpClient = new HttpClient(new FailingMetadataServerHandler());
        var service = CreateService(context, httpClient, logger: logger);

        var matches = await service.SearchTagsAsync("term", Endpoint, CancellationToken.None);

        Assert.Empty(matches);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task NonStrictSearch_DoesNotLogAggregateWarningWhenAnotherEndpointSucceeds()
    {
        await using var context = CreateContext();
        var logger = new RecordingLogger<MetadataServerService>();
        using var httpClient = new HttpClient(new MixedMetadataServerHandler());
        var configuration = new CoveConfiguration
        {
            Scraping = new ScrapingConfig
            {
                MetadataServers =
                [
                    new MetadataServerInstance { Endpoint = "https://one.example/graphql", ApiKey = ApiKey, Name = "One" },
                    new MetadataServerInstance { Endpoint = "https://two.example/graphql", ApiKey = ApiKey, Name = "Two" },
                ],
            },
        };
        var service = CreateService(context, httpClient, configuration: configuration, logger: logger);

        Assert.Empty(await service.SearchPerformersAsync("term", null, CancellationToken.None));
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Debug);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task MergeVideoAsync_ImportsFixtureAndRecordsMetadataProvenance()
    {
        await using var context = CreateContext();
        var video = new Video { Title = "Original Video" };
        context.Videos.Add(video);
        await context.SaveChangesAsync();

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(request =>
        {
            Assert.Contains("query FindVideoByID", request.Query);
            Assert.Equal("remote-video-1", GetVariableString(request, "id"));

            return GraphQlData($$"""
                "findVideo": {{RemoteVideoJson}}
                """);
        }));

        var fieldProvenance = new FieldProvenanceService(context);
        var service = CreateService(context, httpClient, fieldProvenance: fieldProvenance, tagProvenance: new TagProvenanceService(context));

        var imported = await service.MergeVideoAsync(
            video,
            Endpoint,
            "remote-video-1",
            new MetadataServerVideoImportRequestDto
            {
                SetCoverImage = false,
                MarkOrganized = true,
                FieldStrategies = new Dictionary<string, string>
                {
                    ["title"] = "overwrite",
                    ["details"] = "overwrite",
                    ["director"] = "overwrite",
                    ["date"] = "overwrite",
                },
            },
            CancellationToken.None);
        await context.SaveChangesAsync();

        Assert.True(imported);
        Assert.Equal("Remote Video", video.Title);
        Assert.Equal("RS-001", video.Code);
        Assert.Equal("Imported details", video.Details);
        Assert.Equal("Fixture Director", video.Director);
        Assert.Equal(new DateOnly(2024, 5, 1), video.Date);
        Assert.True(video.Organized);
        Assert.Contains(video.Urls, url => url.Url == "https://metadata.example/videos/remote-video-1");
        Assert.Contains(video.RemoteIds, remoteId => remoteId.Endpoint == Endpoint && remoteId.RemoteId == "remote-video-1");

        var savedVideo = await context.Videos
            .Include(item => item.VideoTags).ThenInclude(link => link.Tag)
            .Include(item => item.VideoPerformers).ThenInclude(link => link.Performer)
            .Include(item => item.Studio)
            .SingleAsync();
        Assert.Equal("Fixture Studio", savedVideo.Studio?.Name);
        Assert.Contains(savedVideo.VideoTags, link => link.Tag != null && link.Tag.Name == "Action");
        Assert.Contains(savedVideo.VideoPerformers, link => link.Performer != null && link.Performer.Name == "Jane Doe");

        var tagApplication = await context.TagApplications.Include(application => application.Tag).SingleAsync();
        Assert.NotNull(tagApplication.Tag);
        Assert.Equal(AffinityHostType.Video, tagApplication.HostType);
        Assert.Equal(video.Id, tagApplication.HostId);
        Assert.Equal("Action", tagApplication.Tag.Name);
        Assert.Equal($"metadata:{Endpoint}", tagApplication.SourceKey);
        Assert.Equal(Endpoint, tagApplication.SourceRunId);

        var provenanceRows = await fieldProvenance.GetForHostAsync(AffinityHostType.Video, video.Id);
        Assert.Contains(provenanceRows, row => row.FieldKey == "title" && row.Value.HasValue && row.Value.Value.GetString() == "Remote Video");
        Assert.Contains(provenanceRows, row => row.FieldKey == "details" && row.Value.HasValue && row.Value.Value.GetString() == "Imported details");
        Assert.Contains(provenanceRows, row => row.FieldKey == "studio" && row.Value.HasValue && row.Value.Value.GetString() == "Fixture Studio");
        Assert.Contains(provenanceRows, row => row.FieldKey == "tags" && row.Value.HasValue && row.Value.Value.EnumerateArray().Any(value => value.GetString() == "Action"));
        Assert.All(provenanceRows, row => Assert.Equal($"metadata:{Endpoint}", row.SourceKey));
    }

    [Fact]
    public async Task MergeVideoAsync_AllowsRemoteTagsWithNullAliases()
    {
        await using var context = CreateContext();
        var video = new Video { Title = "Original Video" };
        context.Videos.Add(video);
        await context.SaveChangesAsync();

        var remoteVideoJson = RemoteVideoJson.Replace("\"aliases\": [\"Activity\"]", "\"aliases\": null", StringComparison.Ordinal);
        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(request =>
        {
            Assert.Contains("query FindVideoByID", request.Query);
            return GraphQlData($$"""
                "findVideo": {{remoteVideoJson}}
                """);
        }));

        var service = CreateService(context, httpClient);

        var imported = await service.MergeVideoAsync(
            video,
            Endpoint,
            "remote-video-1",
            new MetadataServerVideoImportRequestDto { SetCoverImage = false },
            CancellationToken.None);
        await context.SaveChangesAsync();

        Assert.True(imported);
        var savedTag = await context.Tags.Include(tag => tag.Aliases).SingleAsync(tag => tag.Name == "Action");
        Assert.Empty(savedTag.Aliases);
        var savedVideo = await context.Videos.Include(item => item.VideoTags).ThenInclude(link => link.Tag).SingleAsync();
        Assert.Contains(savedVideo.VideoTags, link => link.Tag != null && link.Tag.Name == "Action");
    }

    [Fact]
    public async Task BatchTagPerformersAsync_UsesGraphQlImportAndRestoresExcludedFields()
    {
        await using var context = CreateContext();
        var performer = new Performer { Name = "Local Jane" };
        performer.RemoteIds.Add(new PerformerRemoteId { Endpoint = Endpoint, RemoteId = "remote-performer-1" });
        context.Performers.Add(performer);
        await context.SaveChangesAsync();

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(request =>
        {
            Assert.Contains("query FindPerformerByID", request.Query);
            Assert.Equal("remote-performer-1", GetVariableString(request, "id"));

            return GraphQlData($$"""
                "findPerformer": {{RemotePerformerJson}}
                """);
        }));

        var eventBus = new EventBus();
        var publishedEvents = new List<EntityEvent>();
        using var subscription = eventBus.Subscribe<EntityEvent>(publishedEvents.Add);
        var service = CreateService(context, httpClient, fieldProvenance: new FieldProvenanceService(context), eventBus: eventBus);

        var result = await service.BatchTagPerformersAsync(
            Endpoint,
            [performer.Id],
            refreshAlreadyTagged: true,
            excludeFields: ["name"],
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, result.Processed);
        Assert.Equal(1, result.Updated);
        var item = Assert.Single(result.Items);
        Assert.Equal("updated", item.Outcome);
        Assert.Equal("remote-performer-1", item.RemoteId);
        var publishedEvent = Assert.Single(publishedEvents);
        Assert.Equal(EventType.PerformerUpdated, publishedEvent.Type);
        Assert.Equal(performer.Id, publishedEvent.EntityId);

        var updated = await context.Performers.Include(item => item.Urls).SingleAsync();
        Assert.Equal("Local Jane", updated.Name);
        Assert.Equal(GenderEnum.Female, updated.Gender);
        Assert.Contains(updated.Urls, url => url.Url == "https://metadata.example/performers/remote-performer-1");
    }

    [Fact]
    public async Task SubmitVideoDraftAsync_SendsExpectedGraphQlPayload()
    {
        await using var context = CreateContext();
        var studio = new Studio { Name = "Fixture Studio" };
        studio.RemoteIds.Add(new StudioRemoteId { Endpoint = Endpoint, RemoteId = "remote-studio-1" });
        var performer = new Performer { Name = "Jane Doe" };
        performer.RemoteIds.Add(new PerformerRemoteId { Endpoint = Endpoint, RemoteId = "remote-performer-1" });
        var tag = new Tag { Name = "Action" };
        tag.RemoteIds.Add(new TagRemoteId { Endpoint = Endpoint, RemoteId = "remote-tag-1" });
        var video = new Video
        {
            Title = "Draft Video",
            Code = "D-001",
            Details = "Draft details",
            Director = "Draft Director",
            Date = new DateOnly(2024, 6, 2),
            Studio = studio,
        };
        video.RemoteIds.Add(new VideoRemoteId { Endpoint = Endpoint, RemoteId = "remote-video-1" });
        video.Urls.Add(new VideoUrl { Url = "https://cove.example/videos/draft" });
        video.VideoPerformers.Add(new VideoPerformer { Performer = performer });
        video.VideoTags.Add(new VideoTag { Tag = tag });
        var file = new VideoFile { Duration = 121 };
        file.Fingerprints.Add(new FileFingerprint { Type = "oshash", Value = "1a2b" });
        video.Files.Add(file);
        context.Videos.Add(video);
        await context.SaveChangesAsync();

        FixtureMetadataServerHandler? handler = null;
        handler = new FixtureMetadataServerHandler(request =>
        {
            Assert.Contains("mutation SubmitSceneDraft", request.Query);
            Assert.Contains("SceneDraftInput", request.Query);
            Assert.Contains("submitSceneDraft", request.Query);
            Assert.DoesNotContain("VideoDraftInput", request.Query);
            Assert.DoesNotContain("submitVideoDraft", request.Query);
            return GraphQlData("""
                "submitSceneDraft": { "id": "draft-video-1" }
                """);
        });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(context, httpClient);

        var draftId = await service.SubmitVideoDraftAsync(video, Endpoint, CancellationToken.None);

        Assert.Equal("draft-video-1", draftId);
        var request = Assert.Single(handler.Requests);
        using var variables = JsonDocument.Parse(request.VariablesJson);
        var input = variables.RootElement.GetProperty("input");
        Assert.Equal("remote-video-1", input.GetProperty("id").GetString());
        Assert.Equal("Draft Video", input.GetProperty("title").GetString());
        Assert.Equal("https://cove.example/videos/draft", input.GetProperty("url").GetString());
        Assert.False(input.TryGetProperty("urls", out _));
        Assert.Equal("2024-06-02", input.GetProperty("date").GetString());
        Assert.Equal("remote-studio-1", input.GetProperty("studio").GetProperty("id").GetString());
        Assert.Equal("remote-performer-1", input.GetProperty("performers")[0].GetProperty("id").GetString());
        Assert.Equal("remote-tag-1", input.GetProperty("tags")[0].GetProperty("id").GetString());
        var fingerprint = input.GetProperty("fingerprints")[0];
        Assert.Equal("OSHASH", fingerprint.GetProperty("algorithm").GetString());
        Assert.Equal("0000000000001a2b", fingerprint.GetProperty("hash").GetString());
        Assert.Equal(121, fingerprint.GetProperty("duration").GetInt32());
    }

    [Fact]
    public async Task SubmitFingerprintsAsync_UsesSceneIdFieldForMetadataServerSchema()
    {
        await using var context = CreateContext();
        var video = new Video { Title = "Fingerprint Video" };
        video.RemoteIds.Add(new VideoRemoteId { Endpoint = Endpoint, RemoteId = "remote-video-1" });
        var file = new VideoFile { Duration = 121 };
        file.Fingerprints.Add(new FileFingerprint { Type = "oshash", Value = "1a2b" });
        video.Files.Add(file);
        context.Videos.Add(video);
        await context.SaveChangesAsync();

        FixtureMetadataServerHandler? handler = null;
        handler = new FixtureMetadataServerHandler(request =>
        {
            Assert.Contains("mutation SubmitFingerprint", request.Query);
            return GraphQlData("\"submitFingerprint\": true");
        });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(context, httpClient);

        await service.SubmitFingerprintsAsync(video, Endpoint, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        using var variables = JsonDocument.Parse(request.VariablesJson);
        var input = variables.RootElement.GetProperty("input");
        Assert.Equal("remote-video-1", input.GetProperty("scene_id").GetString());
        Assert.False(input.TryGetProperty("video_id", out _));

        var fingerprint = input.GetProperty("fingerprint");
        Assert.Equal("OSHASH", fingerprint.GetProperty("algorithm").GetString());
        Assert.Equal("0000000000001a2b", fingerprint.GetProperty("hash").GetString());
        Assert.Equal(121, fingerprint.GetProperty("duration").GetInt32());
    }

    private static MetadataServerService CreateService(CoveContext context, HttpClient httpClient, IFieldProvenanceService? fieldProvenance = null, ITagProvenanceService? tagProvenance = null, CoveConfiguration? configuration = null, ILogger<MetadataServerService>? logger = null, IEventBus? eventBus = null)
        => new(
            httpClient,
            configuration ?? new CoveConfiguration
            {
                Scraping = new ScrapingConfig
                {
                    MetadataServers =
                    [
                        new MetadataServerInstance
                        {
                            Endpoint = Endpoint,
                            ApiKey = ApiKey,
                            Name = "Fixture Box",
                        },
                    ],
                },
            },
            context,
            new NullBlobService(),
            new NullVideoCoverService(),
            tagProvenance ?? new TagProvenanceService(context),
            logger ?? NullLogger<MetadataServerService>.Instance,
            fieldProvenance,
            eventBus);

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"metadata-server-service-{Guid.NewGuid():N}")
            .Options;

        return new CoveContext(options);
    }

    private static Video CreateSearchStrategyVideo()
    {
        var video = new Video { Title = "Local Video" };
        var file = new VideoFile { Duration = 118 };
        file.Fingerprints.Add(new FileFingerprint { Type = "oshash", Value = "1a2b" });
        video.Files.Add(file);
        return video;
    }

    private static string GetVariableString(GraphQlRequestSnapshot request, string propertyName)
    {
        using var document = JsonDocument.Parse(request.VariablesJson);
        return document.RootElement.GetProperty(propertyName).GetString() ?? string.Empty;
    }

    private static string GraphQlData(string dataProperties)
        => $$"""
           {
             "data": {
               {{dataProperties}}
             }
           }
           """;

    private const string RemoteVideoJson = """
        {
          "id": "remote-video-1",
          "title": "Remote Video",
          "code": "RS-001",
          "details": "Imported details",
          "director": "Fixture Director",
          "duration": 120,
          "date": "2024-05-01",
          "urls": [
            { "url": "https://metadata.example/videos/remote-video-1" }
          ],
          "images": [],
          "studio": {
            "id": "remote-studio-1",
            "name": "Fixture Studio",
            "aliases": [],
            "urls": [],
            "images": [],
            "parent": null
          },
          "tags": [
            { "id": "remote-tag-1", "name": "Action", "description": "Movement", "aliases": ["Activity"] }
          ],
          "performers": [
            {
              "performer": {
                "id": "remote-performer-1",
                "name": "Jane Doe",
                "disambiguation": null,
                "aliases": ["J. Doe"],
                "gender": "FEMALE",
                "deleted": false,
                "merged_into_id": null,
                "urls": [],
                "images": [],
                "birth_date": null,
                "death_date": null,
                "ethnicity": null,
                "country": "US",
                "eye_color": null,
                "hair_color": null,
                "height": null,
                "measurements": null,
                "breast_type": null,
                "career_start_year": null,
                "career_end_year": null,
                "tattoos": [],
                "piercings": []
              }
            }
          ],
          "fingerprints": [
            { "algorithm": "MD5", "hash": "abcdef", "duration": 120 }
          ]
        }
        """;

    private const string RemotePerformerJson = """
        {
          "id": "remote-performer-1",
          "name": "Remote Jane",
          "disambiguation": "Fixture performer",
          "aliases": ["Jane Fixture"],
          "gender": "FEMALE",
          "deleted": false,
          "merged_into_id": null,
          "urls": [
            { "url": "https://metadata.example/performers/remote-performer-1" }
          ],
          "images": [],
          "birth_date": "1990-01-01",
          "death_date": null,
          "ethnicity": null,
          "country": "US",
          "eye_color": "BLUE",
          "hair_color": "BROWN",
          "height": 170,
          "measurements": null,
          "breast_type": null,
          "career_start_year": 2010,
          "career_end_year": null,
          "tattoos": [],
          "piercings": []
        }
        """;

    private sealed class FailingMetadataServerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Metadata server unavailable");
    }

    private sealed class MixedMetadataServerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host == "one.example")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(GraphQlData("\"searchPerformer\": []"), Encoding.UTF8, "application/json"),
                });

            throw new HttpRequestException("Metadata server unavailable");
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class FixtureMetadataServerHandler(Func<GraphQlRequestSnapshot, string> responseFactory) : HttpMessageHandler
    {
        private readonly Func<GraphQlRequestSnapshot, string> _responseFactory = responseFactory;

        public List<GraphQlRequestSnapshot> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var query = GetProperty(root, "query").GetString() ?? string.Empty;
            var variables = GetProperty(root, "variables");
            var apiKey = request.Headers.TryGetValues("ApiKey", out var values) ? values.SingleOrDefault() : null;
            var snapshot = new GraphQlRequestSnapshot(query, variables.GetRawText(), request.RequestUri, apiKey);
            Requests.Add(snapshot);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseFactory(snapshot), Encoding.UTF8, "application/json"),
            };
        }

        private static JsonElement GetProperty(JsonElement element, string propertyName)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    return property.Value;
            }

            throw new KeyNotFoundException(propertyName);
        }
    }

    private sealed record GraphQlRequestSnapshot(string Query, string VariablesJson, Uri? RequestUri, string? ApiKey);

    private sealed class NullBlobService : IBlobService
    {
        public Task<string> StoreBlobAsync(Stream data, string contentType, CancellationToken ct = default)
            => Task.FromResult("blob-fixture");

        public Task<(Stream Stream, string ContentType)?> GetBlobAsync(string blobId, CancellationToken ct = default)
            => Task.FromResult<(Stream Stream, string ContentType)?>(null);

        public Task DeleteBlobAsync(string blobId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class NullVideoCoverService : IVideoCoverService
    {
        public Task<bool> TryApplyRemoteCoverAsync(Video video, string? imageUrl, CancellationToken ct = default)
            => Task.FromResult(true);
    }
}
