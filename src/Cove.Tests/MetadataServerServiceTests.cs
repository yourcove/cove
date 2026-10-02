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

    [Theory]
    [InlineData("https://cdn.example/b.jpg", "https://cdn.example/b.jpg")]
    [InlineData(" https://cdn.example/b.jpg ", "https://cdn.example/b.jpg")]
    [InlineData(null, "https://cdn.example/a.jpg")]
    [InlineData("", "https://cdn.example/a.jpg")]
    [InlineData("https://elsewhere.example/x.jpg", "https://cdn.example/a.jpg")]
    [InlineData("https://cdn.example/B.jpg", "https://cdn.example/a.jpg")]
    public void ResolvePerformerImageUrl_HonoursOnlyAnImageTheSourceLists(string? requested, string expected)
    {
        var listed = new[] { "https://cdn.example/a.jpg", " ", "https://cdn.example/b.jpg" };

        Assert.Equal(expected, MetadataServerService.ResolvePerformerImageUrl(listed, requested));
    }

    [Fact]
    public void ResolvePerformerImageUrl_RecognisesAListedImageWithStrayWhitespace()
    {
        var listed = new[] { "https://cdn.example/a.jpg", "  https://cdn.example/b.jpg " };

        Assert.Equal("https://cdn.example/b.jpg", MetadataServerService.ResolvePerformerImageUrl(listed, "https://cdn.example/b.jpg"));
        Assert.Equal("https://cdn.example/b.jpg", MetadataServerService.ResolvePerformerImageUrl(listed, "  https://cdn.example/b.jpg "));
    }

    [Fact]
    public void ResolvePerformerImageUrl_ReturnsNullWhenTheSourceHasNoImage()
    {
        Assert.Null(MetadataServerService.ResolvePerformerImageUrl([], "https://cdn.example/a.jpg"));
    }

    [Fact]
    public async Task SearchVideosAsync_MapsGraphQlFixtureAndLocalCandidates()
    {
        await using var context = CreateContext();
        context.Studios.Add(new Studio { Name = "Fixture Studio" });
        context.Performers.Add(new Performer { Name = "Jane Doe" });
        context.Tags.Add(new Tag { Name = "Action" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

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
        // The gender travels with the candidate so the tagger can filter its preview by the same
        // performer genders the import filters by.
        Assert.Contains(match.PerformerCandidates, candidate => candidate.Name == "Jane Doe" && candidate.ExistsLocally && candidate.Gender == "FEMALE");
        Assert.Contains(match.TagCandidates, candidate => candidate.Name == "Action" && candidate.ExistsLocally);
    }

    // The tagger mirrors these rules client-side to filter its preview, so they are a contract: an absent
    // list filters nothing, a present one filters by normalized key, an unstated gender counts as
    // "Unknown", and a present but empty list allows no performer at all.
    [Theory]
    [InlineData(null, "Jane Doe,John Roe,Tess Poe,Sam Roe")]
    [InlineData("Female", "Jane Doe")]
    [InlineData("Female;Male", "Jane Doe,John Roe")]
    [InlineData("Transgender Female", "Tess Poe")]
    [InlineData("Unknown", "Sam Roe")]
    [InlineData("", "")]
    public async Task MergeVideoWithWarningsAsync_AppliesThePerformerGenderFilter(string? genders, string expectedNames)
    {
        await using var context = CreateContext();
        var video = new Video { Title = "Original Video" };
        context.Add(video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(request =>
        {
            Assert.Contains("query FindVideoByID", request.Query);
            return GraphQlData($$"""
                "findVideo": {{MixedGenderRemoteVideoJson}}
                """);
        }));
        var service = CreateService(context, httpClient);

        var result = await service.MergeVideoWithWarningsAsync(
            video,
            Endpoint,
            "remote-video-1",
            new MetadataServerVideoImportRequestDto
            {
                SetCoverImage = false,
                PerformerGenders = genders == null ? null : [.. genders.Split(';', StringSplitOptions.RemoveEmptyEntries)],
            },
            CancellationToken.None);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Imported);
        var applied = video.VideoPerformers
            .Select(link => link.Performer?.Name)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        var expected = expectedNames.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expected, applied);
    }

    // The tagger's preview drops a filtered performer and then sends the surviving ones as overrides, so
    // the gender filter has to outrank an override that asks for one of the dropped performers by name.
    [Theory]
    [InlineData("existing")]
    [InlineData("create")]
    public async Task MergeVideoWithWarningsAsync_GenderFilterOutranksAPerformerOverride(string action)
    {
        await using var context = CreateContext();
        var existing = new Performer { Name = "John Roe" };
        var video = new Video { Title = "Original Video" };
        context.AddRange(existing, video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(request =>
        {
            Assert.Contains("query FindVideoByID", request.Query);
            return GraphQlData($$"""
                "findVideo": {{MixedGenderRemoteVideoJson}}
                """);
        }));
        var service = CreateService(context, httpClient);

        var result = await service.MergeVideoWithWarningsAsync(
            video,
            Endpoint,
            "remote-video-1",
            new MetadataServerVideoImportRequestDto
            {
                SetCoverImage = false,
                PerformerGenders = ["Female"],
                PerformerOverrides =
                [
                    new MetadataServerVideoEntityOverrideDto
                    {
                        RemoteId = "remote-performer-2",
                        Name = "John Roe",
                        Action = action,
                        LocalId = action == "existing" ? existing.Id : null,
                    },
                ],
            },
            CancellationToken.None);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Imported);
        Assert.DoesNotContain(video.VideoPerformers, link => link.Performer?.Name == "John Roe" || link.PerformerId == existing.Id);
        Assert.Contains(video.VideoPerformers, link => link.Performer?.Name == "Jane Doe");
    }

    [Fact]
    public async Task MergeVideoWithWarningsAsync_KeepsExistingPerformersWhenTheGenderFilterAdmitsNothing()
    {
        await using var context = CreateContext();
        var current = new Performer { Name = "Already Linked" };
        var video = new Video { Title = "Original Video" };
        video.VideoPerformers.Add(new VideoPerformer { Performer = current });
        context.AddRange(current, video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(request =>
        {
            Assert.Contains("query FindVideoByID", request.Query);
            return GraphQlData($$"""
                "findVideo": {{MixedGenderRemoteVideoJson}}
                """);
        }));
        var service = CreateService(context, httpClient);

        // Overwrite would normally clear the video's performers before applying the remote list. With a
        // filter that admits no gender, clearing them would delete what is there and put nothing back.
        var result = await service.MergeVideoWithWarningsAsync(
            video,
            Endpoint,
            "remote-video-1",
            new MetadataServerVideoImportRequestDto
            {
                SetCoverImage = false,
                PerformerGenders = [],
                FieldStrategies = new Dictionary<string, string> { ["performers"] = "overwrite" },
            },
            CancellationToken.None);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Imported);
        var linked = Assert.Single(video.VideoPerformers);
        Assert.Equal(current.Id, linked.PerformerId == 0 ? linked.Performer?.Id : linked.PerformerId);
    }

    // Tagging a video links performers; it never edits them. Only a performer this import creates is
    // populated from the remote.
    [Fact]
    public async Task MergeVideoWithWarningsAsync_LeavesAnExistingPerformerUntouched()
    {
        await using var context = CreateContext();
        // Matched by identity: same name and disambiguation as the remote, everything else its own.
        var existing = new Performer
        {
            Name = "Jane Doe",
            Gender = GenderEnum.NonBinary,
            Country = "FI",
        };
        existing.RemoteIds.Add(new PerformerRemoteId { Endpoint = Endpoint, RemoteId = "another-remote-performer" });
        var video = new Video { Title = "Original Video" };
        context.AddRange(existing, video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(request => GraphQlData($$"""
            "findVideo": {{RemoteVideoJson}}
            """)));
        var service = CreateService(context, httpClient);

        var result = await service.MergeVideoWithWarningsAsync(
            video,
            Endpoint,
            "remote-video-1",
            new MetadataServerVideoImportRequestDto { SetCoverImage = false },
            CancellationToken.None);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Imported);
        Assert.Contains(video.VideoPerformers, link => link.PerformerId == existing.Id || ReferenceEquals(link.Performer, existing));
        // The remote fixture states a different gender, country and alias set; none of it lands.
        Assert.Equal("Jane Doe", existing.Name);
        Assert.Null(existing.Disambiguation);
        Assert.Equal(GenderEnum.NonBinary, existing.Gender);
        Assert.Equal("FI", existing.Country);
        Assert.Empty(existing.Aliases);
        // The link it already had for this endpoint is not repointed at the matched entry.
        var remoteId = Assert.Single(existing.RemoteIds);
        Assert.Equal("another-remote-performer", remoteId.RemoteId);
    }

    [Fact]
    public async Task MergeVideoWithWarningsAsync_PopulatesAndLinksAPerformerItCreates()
    {
        await using var context = CreateContext();
        var video = new Video { Title = "Original Video" };
        context.Add(video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(request => GraphQlData($$"""
            "findVideo": {{RemoteVideoJson}}
            """)));
        var service = CreateService(context, httpClient);

        var result = await service.MergeVideoWithWarningsAsync(
            video,
            Endpoint,
            "remote-video-1",
            new MetadataServerVideoImportRequestDto { SetCoverImage = false },
            CancellationToken.None);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Imported);
        var created = await context.Performers
            .Include(performer => performer.RemoteIds)
            .SingleAsync(performer => performer.Name == "Jane Doe", TestContext.Current.CancellationToken);
        Assert.Equal(GenderEnum.Female, created.Gender);
        Assert.Equal("US", created.Country);
        Assert.Contains(created.RemoteIds, id => id.Endpoint == Endpoint && id.RemoteId == "remote-performer-1");
    }

    // Two local studios can legitimately share one remote id. The import used to take whichever row the
    // database returned first and rename it to the remote name, which either renamed a studio nobody
    // pointed at or, when the name was taken, failed the whole import with a name conflict.
    [Fact]
    public async Task MergeVideoWithWarningsAsync_PrefersTheRemoteIdOwnerTheRemoteNames()
    {
        await using var context = CreateContext();
        var namesake = new Studio { Name = "Other Studio" };
        namesake.RemoteIds.Add(new StudioRemoteId { Endpoint = Endpoint, RemoteId = "remote-studio-1" });
        var named = new Studio { Name = "Fixture Studio" };
        named.RemoteIds.Add(new StudioRemoteId { Endpoint = Endpoint, RemoteId = "remote-studio-1" });
        var video = new Video { Title = "Original Video" };
        context.AddRange(namesake, named, video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(request => GraphQlData($$"""
            "findVideo": {{RemoteVideoJson}}
            """)));
        var service = CreateService(context, httpClient);

        var result = await service.MergeVideoWithWarningsAsync(
            video,
            Endpoint,
            "remote-video-1",
            new MetadataServerVideoImportRequestDto { SetCoverImage = false },
            CancellationToken.None);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Imported);
        Assert.Equal(named.Id, video.StudioId == 0 ? video.Studio?.Id : video.StudioId);
        // The studio that was not chosen keeps its own name.
        Assert.Equal("Other Studio", namesake.Name);
    }

    // Two concurrent imports needing the same missing studio both create it, and the name constraint lets
    // only one commit. The loser used to fail its whole import; it should link the winner's studio instead.
    [Fact]
    public async Task ImportFromMetadataServer_LinksTheStudioAConcurrentImportCreatedFirst()
    {
        await using var run = await RunImportAgainstARivalAsync(
            new MetadataServerVideoImportRequestDto { SetPerformers = false, SetTags = false },
            rival => rival.Add(RivalStudio(withRemoteId: true)).Entity,
            RivalCommits.OnSave(() => new EntityNameConflictException(NameConflictEntityTypes.Studio)));

        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(run.Response.Result);
        Assert.Equal(2, run.Attempts);
        var studio = Assert.Single(await run.Verify.Studios.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(run.Rival.WinnerId, studio.Id);
        Assert.Equal(studio.Id, run.Saved.StudioId);
        Assert.Equal("Remote Video", run.Saved.Title);
    }

    // A rival that created the studio by name alone, without the remote id, is found only through the name
    // identity index, which the failed attempt had already built without it. The retry must rebuild it or
    // it misses the winner and creates the duplicate the constraint just refused.
    [Fact]
    public async Task ImportFromMetadataServer_RebuildsTheNameIndexBeforeRetrying()
    {
        await using var run = await RunImportAgainstARivalAsync(
            new MetadataServerVideoImportRequestDto { SetPerformers = false, SetTags = false },
            rival => rival.Add(RivalStudio(withRemoteId: false)).Entity,
            RivalCommits.OnSave(() => new EntityNameConflictException(NameConflictEntityTypes.Studio)));

        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(run.Response.Result);
        Assert.Equal(2, run.Attempts);
        var studio = Assert.Single(await run.Verify.Studios.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(run.Rival.WinnerId, studio.Id);
        Assert.Equal(studio.Id, run.Saved.StudioId);
    }

    // Tags are shared between videos more than any other related entity, and tag writes are serialised,
    // so the loser of this race is refused by the context's own name check when it saves, with its own
    // exception. The rival commits after the import resolved its tags and before it saves.
    [Fact]
    public async Task ImportFromMetadataServer_LinksTheTagAConcurrentImportCommittedBeforeTheSave()
    {
        await using var run = await RunImportAgainstARivalAsync(
            new MetadataServerVideoImportRequestDto { SetPerformers = false, SetStudio = false },
            rival => rival.Add(new Tag { Name = "Action" }).Entity,
            RivalCommits.OnTagProvenance);

        await AssertLinkedToTheRivalTagAsync(run);
    }

    // The same race when the rival commits while the import is still resolving the tag: the import's own
    // namespace check sees the winner and refuses to create a second tag of that name.
    [Fact]
    public async Task ImportFromMetadataServer_LinksTheTagAConcurrentImportCommittedDuringResolution()
    {
        await using var run = await RunImportAgainstARivalAsync(
            new MetadataServerVideoImportRequestDto { SetPerformers = false, SetStudio = false },
            rival => rival.Add(new Tag { Name = "Action" }).Entity,
            RivalCommits.OnTagStaged);

        await AssertLinkedToTheRivalTagAsync(run);
    }

    public static TheoryData<string, string> LastingConflicts => new()
    {
        { NameConflictEntityTypes.Studio, "RELATED_ENTITY_NAME_CONFLICT" },
        { "tag", "TAG_NAME_CONFLICT" },
    };

    [Theory]
    [MemberData(nameof(LastingConflicts))]
    public async Task ImportFromMetadataServer_ReportsANameConflictThatOutlastsItsRetries(string entityType, string expectedCode)
    {
        await using var run = await RunImportAgainstARivalAsync(
            new MetadataServerVideoImportRequestDto(),
            rival => null,
            RivalCommits.OnSave(
                () => entityType == "tag"
                    ? TagNameConflictException.ForConcurrentWrite()
                    : new EntityNameConflictException(entityType),
                persistent: true));

        var conflict = Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(run.Response.Result);
        Assert.Equal(expectedCode, conflict.Value!.GetType().GetProperty("code")!.GetValue(conflict.Value));
        Assert.Equal(Cove.Api.Controllers.VideosController.MetadataImportNameConflictAttempts, run.Attempts);
    }

    private static Studio RivalStudio(bool withRemoteId)
    {
        var studio = new Studio { Name = "Fixture Studio" };
        if (withRemoteId)
            studio.RemoteIds.Add(new StudioRemoteId { Endpoint = Endpoint, RemoteId = "remote-studio-1" });
        return studio;
    }

    private static async Task AssertLinkedToTheRivalTagAsync(RivalImportRun run)
    {
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(run.Response.Result);
        Assert.Equal(2, run.Attempts);
        var tag = Assert.Single(await run.Verify.Tags.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(run.Rival.WinnerId, tag.Id);
        Assert.Contains(
            await run.Verify.Set<VideoTag>().Where(link => link.VideoId == run.Saved.Id).ToListAsync(TestContext.Current.CancellationToken),
            link => link.TagId == tag.Id);
    }

    /// <summary>When the rival entity commits, standing in for a concurrent import.</summary>
    private sealed record RivalCommits(Func<Exception>? SaveConflict, bool Persistent, bool OnStagedTag, bool OnProvenance)
    {
        // At the loser's first save, which then fails the way the deferred name constraint fails at commit.
        public static RivalCommits OnSave(Func<Exception> conflict, bool persistent = false) => new(conflict, persistent, false, false);

        // When the import stages its new tag, before it checks the tag namespace.
        public static RivalCommits OnTagStaged { get; } = new(null, false, true, false);

        // When the import records tag provenance: after it resolved its tags, before it saves.
        public static RivalCommits OnTagProvenance { get; } = new(null, false, false, true);
    }

    private sealed record RivalImportRun(
        Microsoft.AspNetCore.Mvc.ActionResult<VideoDto> Response,
        ConcurrentCreation Rival,
        int Attempts,
        CoveContext Verify,
        Video Saved) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Verify.DisposeAsync();
    }

    private static async Task<RivalImportRun> RunImportAgainstARivalAsync(
        MetadataServerVideoImportRequestDto request,
        Func<CoveContext, object?> createRival,
        RivalCommits when)
    {
        var databaseName = $"metadata-import-race-{Guid.NewGuid():N}";
        var rival = new ConcurrentCreation(databaseName, createRival, when.SaveConflict) { Persistent = when.Persistent };
        await using var context = new CoveContext(new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase(databaseName)
            .AddInterceptors(rival)
            .Options);
        var video = new Video { Title = "Original Video" };
        context.Add(video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        rival.Armed = true;
        if (when.OnStagedTag)
        {
            context.ChangeTracker.Tracked += (_, tracked) =>
            {
                if (tracked.Entry.State == EntityState.Added && tracked.Entry.Entity is Tag)
                    rival.CommitRivalOnce();
            };
        }
        var tagProvenance = RivalOnTagProvenance.Wrap(new TagProvenanceService(context), when.OnProvenance ? rival.CommitRivalOnce : null);

        var handler = new FixtureMetadataServerHandler(request => GraphQlData($$"""
            "findVideo": {{RemoteVideoJson}}
            """));
        using var httpClient = new HttpClient(handler);
        var principalAccessor = new Cove.Core.Auth.CurrentPrincipalAccessor();
        using var memoryCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var controller = new Cove.Api.Controllers.VideosController(
            new Cove.Data.Repositories.VideoRepository(context),
            context,
            CreateService(context, httpClient, tagProvenance: tagProvenance),
            null!,
            null!,
            memoryCache,
            null!,
            null!,
            new Cove.Data.Services.UserEngagementService(context, principalAccessor),
            new CustomFieldService(context),
            new EventBus(),
            null,
            principalAccessor);

        var response = await controller.ImportFromMetadataServer(
            video.Id,
            request with { Endpoint = Endpoint, VideoId = "remote-video-1", SetCoverImage = false },
            TestContext.Current.CancellationToken);

        // Every attempt starts by fetching the remote video, so the fetches count the attempts.
        var attempts = handler.Requests.Count(snapshot => snapshot.Query.Contains("findVideo", StringComparison.Ordinal));
        var verify = new CoveContext(new DbContextOptionsBuilder<CoveContext>().UseInMemoryDatabase(databaseName).Options);
        var saved = await verify.Videos.SingleAsync(item => item.Id == video.Id, TestContext.Current.CancellationToken);
        return new RivalImportRun(response, rival, attempts, verify, saved);
    }

    // Commits the rival entity through its own context, as a concurrent import would, at the moment the test
    // chose. It must never run inside the loser's SaveChanges when tags are involved: the context holds its
    // process-wide tag namespace lock for the whole save, and the rival's save would wait on it forever.
    private sealed class ConcurrentCreation(
        string databaseName,
        Func<CoveContext, object?> createRival,
        Func<Exception>? saveConflict) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private bool _rivalCommitted;
        private int _conflicts;

        public bool Armed { get; set; }
        public bool Persistent { get; init; }
        public int WinnerId { get; private set; }

        public void CommitRivalOnce()
        {
            if (!Armed || _rivalCommitted)
                return;
            _rivalCommitted = true;
            using var rival = new CoveContext(new DbContextOptionsBuilder<CoveContext>().UseInMemoryDatabase(databaseName).Options);
            var winner = createRival(rival);
            if (winner == null)
                return;
            rival.SaveChanges();
            WinnerId = (int)rival.Entry(winner).Property("Id").CurrentValue!;
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (saveConflict == null || !Armed || (!Persistent && _conflicts > 0))
                return ValueTask.FromResult(result);
            CommitRivalOnce();
            _conflicts++;
            throw saveConflict();
        }
    }

    // Passes every call through, running a hook before the first provenance record: the point where an
    // import has resolved its tags and has not yet saved.
    public class RivalOnTagProvenance : System.Reflection.DispatchProxy
    {
        private ITagProvenanceService _inner = null!;
        private Action? _beforeRecord;

        public static ITagProvenanceService Wrap(ITagProvenanceService inner, Action? beforeRecord)
        {
            var proxy = Create<ITagProvenanceService, RivalOnTagProvenance>();
            var hooked = (RivalOnTagProvenance)(object)proxy;
            hooked._inner = inner;
            hooked._beforeRecord = beforeRecord;
            return proxy;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(ITagProvenanceService.RecordAsync))
                _beforeRecord?.Invoke();
            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (System.Reflection.TargetInvocationException exception) when (exception.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception.InnerException);
                throw;
            }
        }
    }

    [Fact]
    public async Task MergeVideoWithWarningsAsync_LeavesAnExistingStudioUntouched()
    {
        await using var context = CreateContext();
        var linked = new Studio { Name = "Linked Studio" };
        linked.RemoteIds.Add(new StudioRemoteId { Endpoint = Endpoint, RemoteId = "remote-studio-1" });
        // A second studio already answers to the remote's name: renaming the linked one would have
        // failed the whole import on the unique-name rule.
        var namesake = new Studio { Name = "Fixture Studio" };
        var video = new Video { Title = "Original Video" };
        context.AddRange(linked, namesake, video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(request => GraphQlData($$"""
            "findVideo": {{RemoteVideoJson}}
            """)));
        var service = CreateService(context, httpClient);

        var result = await service.MergeVideoWithWarningsAsync(
            video,
            Endpoint,
            "remote-video-1",
            new MetadataServerVideoImportRequestDto { SetCoverImage = false },
            CancellationToken.None);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Imported);
        Assert.Equal("Linked Studio", linked.Name);
        Assert.Equal("Fixture Studio", namesake.Name);
        Assert.Empty(linked.Aliases);
        Assert.Equal(linked.Id, video.StudioId == 0 ? video.Studio?.Id : video.StudioId);
    }

    [Fact]
    public async Task MergeVideoWithWarningsAsync_LeavesAnExistingTagUntouched()
    {
        await using var context = CreateContext();
        var existing = new Tag { Name = "Local Tag Name", Description = "Local description" };
        existing.RemoteIds.Add(new TagRemoteId { Endpoint = Endpoint, RemoteId = "remote-tag-1" });
        var video = new Video { Title = "Original Video" };
        context.AddRange(existing, video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(request => GraphQlData($$"""
            "findVideo": {{RemoteVideoJson}}
            """)));
        var service = CreateService(context, httpClient);

        var result = await service.MergeVideoWithWarningsAsync(
            video,
            Endpoint,
            "remote-video-1",
            new MetadataServerVideoImportRequestDto { SetCoverImage = false },
            CancellationToken.None);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Imported);
        // The remote calls this tag "Action", with the alias "Activity" and its own description.
        Assert.Equal("Local Tag Name", existing.Name);
        Assert.Equal("Local description", existing.Description);
        Assert.Empty(existing.Aliases);
        Assert.Contains(video.VideoTags, link => link.TagId == existing.Id || ReferenceEquals(link.Tag, existing));
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
    public async Task SearchStudiosAsync_UsesFindStudioByNameForThePornDbEndpoint()
    {
        await using var context = CreateContext();
        var handler = new FixtureMetadataServerHandler(request =>
        {
            Assert.Contains("findStudio(id:", request.Query, StringComparison.Ordinal);
            Assert.DoesNotContain("searchStudio(term:", request.Query, StringComparison.Ordinal);
            return GraphQlData($$"""
                "findStudio": {
                  "id": "remote-studio-1",
                  "name": "Vixen",
                  "aliases": ["VXN"],
                  "urls": [{ "url": "https://theporndb.net/sites/vixen" }],
                  "images": [],
                  "parent": { "id": "remote-studio-parent-1", "name": "Vixen Media Group" }
                }
                """);
        });
        using var httpClient = new HttpClient(handler);
        var service = CreateService(context, httpClient, configuration: ThePornDbConfiguration());

        var matches = await service.SearchStudiosAsync("Vixen", "https://theporndb.net/graphql", CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal("remote-studio-1", match.Id);
        Assert.Equal("Vixen", match.Name);
        Assert.Equal("Vixen Media Group", match.ParentName);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SearchStudiosAsync_ReturnsEmptyWhenThePornDbHasNoExactStudioMatch()
    {
        await using var context = CreateContext();
        var handler = new FixtureMetadataServerHandler(request =>
        {
            Assert.Contains("findStudio(id:", request.Query, StringComparison.Ordinal);
            return GraphQlData("\"findStudio\": null");
        });
        using var httpClient = new HttpClient(handler);
        var service = CreateService(context, httpClient, configuration: ThePornDbConfiguration());

        Assert.Empty(await service.SearchStudiosAsync("Vix", "https://theporndb.net/graphql", CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SearchStudiosAsync_UsesSearchStudioQueryForStandardEndpoints()
    {
        await using var context = CreateContext();
        var handler = new FixtureMetadataServerHandler(request =>
        {
            Assert.Contains("searchStudio(term:", request.Query, StringComparison.Ordinal);
            return GraphQlData($$"""
                "searchStudio": [
                  { "id": "remote-studio-2", "name": "Fixture Studio", "aliases": [], "urls": [], "images": [], "parent": null }
                ]
                """);
        });
        using var httpClient = new HttpClient(handler);
        var service = CreateService(context, httpClient);

        var matches = await service.SearchStudiosAsync("Fixture", Endpoint, CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal("remote-studio-2", match.Id);
        Assert.Equal("Fixture Studio", match.Name);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MergeTagAsync_KeepsLocalNameAndSkipsRemoteAliasesClaimedByOtherTags()
    {
        await using var context = CreateContext();
        var target = new Tag { Name = "Local tag" };
        var canonicalOwner = new Tag { Name = "Remote canonical" };
        var aliasOwner = new Tag
        {
            Name = "Other tag",
            Aliases = [new TagAlias { Alias = "Remote alias" }],
        };
        context.AddRange(target, canonicalOwner, aliasOwner);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(request =>
        {
            Assert.Contains("query FindTag", request.Query);
            return GraphQlData("""
                "findTag": {
                  "id": "remote-tag-1",
                  "name": "Remote canonical",
                  "description": "Imported description",
                  "aliases": ["Remote alias", "Safe alias"]
                }
                """);
        }));
        var service = CreateService(context, httpClient, fieldProvenance: new FieldProvenanceService(context));

        var result = await service.MergeTagWithWarningsAsync(target, Endpoint, "remote-tag-1", CancellationToken.None);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Imported);
        Assert.Equal(2, result.Warnings.Count);
        Assert.Contains(result.Warnings, warning => warning.Contains("Kept the local tag name", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("Skipped remote alias", StringComparison.Ordinal));
        Assert.Equal("Local tag", target.Name);
        Assert.Equal("Imported description", target.Description);
        Assert.Equal(["Safe alias"], target.Aliases.Select(alias => alias.Alias));
        Assert.Contains(target.RemoteIds, remoteId => remoteId.Endpoint == Endpoint && remoteId.RemoteId == "remote-tag-1");
        var nameProvenance = await context.FieldProvenance
            .Where(row => row.HostType == AffinityHostType.Tag && row.HostId == target.Id && row.FieldKey == "name")
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(nameProvenance);
    }

    [Fact]
    public async Task MergeTagAsync_IdentifiesTheExistingClaimWhenANewTagWouldCollide()
    {
        await using var context = CreateContext();
        context.Tags.Add(new Tag { Name = "Remote canonical" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var target = new Tag { Name = "Temporary" };
        context.Tags.Add(target);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(_ => GraphQlData("""
            "findTag": {
              "id": "remote-tag-1",
              "name": "remote CANONICAL",
              "description": null,
              "aliases": []
            }
            """)));
        var service = CreateService(context, httpClient);

        var exception = await Assert.ThrowsAsync<TagNameConflictException>(() =>
            service.MergeTagWithWarningsAsync(target, Endpoint, "remote-tag-1", CancellationToken.None));

        Assert.Equal(
            "A tag with name \"Remote canonical\" already exists. Tag names and tag aliases must be unique.",
            exception.Message);
    }

    [Fact]
    public async Task MergeTagAsync_UsesCurrentTrackedClaimsInsteadOfStaleCachedClaims()
    {
        await using var context = CreateContext();
        var renamed = new Tag { Name = "Released canonical" };
        var aliasOwner = new Tag
        {
            Name = "Alias owner",
            Aliases = [new TagAlias { Alias = "Released alias" }],
        };
        context.AddRange(renamed, aliasOwner);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var call = 0;
        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(_ =>
        {
            call++;
            var name = call switch
            {
                1 => "Renamed canonical",
                2 => "Released canonical",
                3 => "Released alias",
                _ => throw new InvalidOperationException("Unexpected metadata request"),
            };
            return GraphQlData($$"""
                "findTag": {
                  "id": "remote-tag-{{call}}",
                  "name": "{{name}}",
                  "description": null,
                  "aliases": []
                }
                """);
        }));
        var service = CreateService(context, httpClient);

        await service.MergeTagWithWarningsAsync(renamed, Endpoint, "remote-tag-1", CancellationToken.None);
        aliasOwner.Aliases.Clear();
        var canonicalTarget = new Tag { Name = "Temporary canonical" };
        var aliasTarget = new Tag { Name = "Temporary alias" };
        context.AddRange(canonicalTarget, aliasTarget);

        await service.MergeTagWithWarningsAsync(canonicalTarget, Endpoint, "remote-tag-2", CancellationToken.None);
        await service.MergeTagWithWarningsAsync(aliasTarget, Endpoint, "remote-tag-3", CancellationToken.None);

        Assert.Equal("Released canonical", canonicalTarget.Name);
        Assert.Equal("Released alias", aliasTarget.Name);
    }

    [Fact]
    public async Task MergeTagAsync_RetainsUnloadedAliasClaimsWhenAnotherAliasChanges()
    {
        await using var context = CreateContext();
        var owner = new Tag
        {
            Name = "Owner",
            Aliases =
            [
                new TagAlias { Alias = "Still claimed" },
                new TagAlias { Alias = "Changing alias" },
            ],
        };
        context.Tags.Add(owner);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();

        owner = await context.Tags.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var changingAlias = await context.Set<TagAlias>()
            .SingleAsync(alias => alias.Alias == "Changing alias", cancellationToken: TestContext.Current.CancellationToken);
        changingAlias.Alias = "Changed alias";
        var target = new Tag { Name = "Temporary" };
        context.Tags.Add(target);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(_ => GraphQlData("""
            "findTag": {
              "id": "remote-tag-1",
              "name": "Still claimed",
              "description": null,
              "aliases": []
            }
            """)));
        var service = CreateService(context, httpClient);

        var exception = await Assert.ThrowsAsync<TagNameConflictException>(() =>
            service.MergeTagWithWarningsAsync(target, Endpoint, "remote-tag-1", CancellationToken.None));

        Assert.Equal(
            "A tag alias with name \"Still claimed\" already exists. Tag names and tag aliases must be unique.",
            exception.Message);
    }

    [Fact]
    public async Task MergeTagAsync_ReleasesASeparatelyLoadedDeletedAliasClaim()
    {
        await using var context = CreateContext();
        var owner = new Tag
        {
            Name = "Owner",
            Aliases = [new TagAlias { Alias = "Released alias" }],
        };
        context.Tags.Add(owner);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();

        owner = await context.Tags.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var releasedAlias = await context.Set<TagAlias>().SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        context.Remove(releasedAlias);
        var target = new Tag { Name = "Temporary" };
        context.Tags.Add(target);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(_ => GraphQlData("""
            "findTag": {
              "id": "remote-tag-1",
              "name": "Released alias",
              "description": null,
              "aliases": []
            }
            """)));
        var service = CreateService(context, httpClient);

        await service.MergeTagWithWarningsAsync(target, Endpoint, "remote-tag-1", CancellationToken.None);

        Assert.Equal("Released alias", target.Name);
    }

    [Fact]
    public async Task MergeTagAsync_DetectsAnAliasClaimedByAnotherNewTagBeforeSave()
    {
        await using var context = CreateContext();
        var first = new Tag { Name = "Temporary one" };
        var second = new Tag { Name = "Temporary two" };
        context.AddRange(first, second);

        var call = 0;
        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(_ =>
        {
            call++;
            return GraphQlData($$"""
                "findTag": {
                  "id": "remote-tag-{{call}}",
                  "name": "Remote tag {{call}}",
                  "description": null,
                  "aliases": ["Shared remote alias"]
                }
                """);
        }));
        var service = CreateService(context, httpClient);

        var firstResult = await service.MergeTagWithWarningsAsync(
            first, Endpoint, "remote-tag-1", CancellationToken.None);
        var secondResult = await service.MergeTagWithWarningsAsync(
            second, Endpoint, "remote-tag-2", CancellationToken.None);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(firstResult.Warnings);
        Assert.Equal(["Shared remote alias"], first.Aliases.Select(alias => alias.Alias));
        Assert.Contains(secondResult.Warnings, warning => warning.Contains("Skipped remote alias", StringComparison.Ordinal));
        Assert.Empty(second.Aliases);
    }

    [Fact]
    public async Task MergeTagAsync_DetectsAPersistedAliasOnATrackedOwnerWhoseAliasesAreNotLoaded()
    {
        await using var context = CreateContext();
        var target = new Tag { Name = "Local tag" };
        var aliasOwner = new Tag
        {
            Name = "Other tag",
            Aliases = [new TagAlias { Alias = "Remote alias" }],
        };
        context.AddRange(target, aliasOwner);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();
        target = await context.Tags.SingleAsync(tag => tag.Name == "Local tag", cancellationToken: TestContext.Current.CancellationToken);
        _ = await context.Tags.SingleAsync(tag => tag.Name == "Other tag", cancellationToken: TestContext.Current.CancellationToken);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(_ => GraphQlData("""
            "findTag": {
              "id": "remote-tag-1",
              "name": "Local tag",
              "description": null,
              "aliases": ["Remote alias"]
            }
            """)));
        var service = CreateService(context, httpClient);

        var result = await service.MergeTagWithWarningsAsync(target, Endpoint, "remote-tag-1", CancellationToken.None);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Imported);
        Assert.Contains(result.Warnings, warning => warning.Contains("Skipped remote alias", StringComparison.Ordinal));
        Assert.Empty(target.Aliases);
    }

    [Fact]
    public async Task MergeTagAsync_DoesNotTreatALegacyBlankAliasAsTheEmptyCanonicalClaim()
    {
        await using var context = CreateContext();
        var target = new Tag { Name = "Local tag" };
        var legacy = new Tag { Name = "Other tag", Aliases = [new TagAlias { Alias = "   " }] };
        context.AddRange(target, legacy);
        using (context.SuppressTagNameValidation())
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(_ => GraphQlData("""
            "findTag": {
              "id": "remote-tag-1",
              "name": "<empty>",
              "description": null,
              "aliases": []
            }
            """)));
        var service = CreateService(context, httpClient);

        var result = await service.MergeTagWithWarningsAsync(target, Endpoint, "remote-tag-1", CancellationToken.None);

        Assert.True(result.Imported);
        Assert.Empty(result.Warnings);
        Assert.Equal("<empty>", target.Name);
    }

    [Fact]
    public async Task BatchTagTagsAsync_ReportsPartialSuccessInJobProgress()
    {
        await using var context = CreateContext();
        var target = new Tag { Name = "Local tag" };
        target.RemoteIds.Add(new TagRemoteId { Endpoint = Endpoint, RemoteId = "remote-tag-1" });
        var aliasOwner = new Tag { Name = "Remote alias" };
        context.AddRange(target, aliasOwner);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var progress = new CapturingJobProgress();

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(_ => GraphQlData("""
            "findTag": {
              "id": "remote-tag-1",
              "name": "Local tag",
              "description": null,
              "aliases": ["Remote alias"]
            }
            """)));
        var service = CreateService(context, httpClient);

        var result = await service.BatchTagTagsAsync(
            Endpoint,
            [target.Id],
            refreshAlreadyTagged: true,
            excludeFields: null,
            progress,
            CancellationToken.None);

        Assert.Equal(1, result.Updated);
        Assert.NotNull(Assert.Single(result.Items).Message);
        Assert.Contains(progress.Reports, report => report.Progress == 1d && report.Message?.Contains("1 saved with skipped", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task BatchTagTagsAsync_DoesNotEvaluateOrRecordExcludedIdentityFields()
    {
        await using var context = CreateContext();
        var target = new Tag { Name = "Local tag", Description = "Local description", Aliases = [new TagAlias { Alias = "Existing alias" }] };
        target.RemoteIds.Add(new TagRemoteId { Endpoint = Endpoint, RemoteId = "remote-tag-1" });
        context.AddRange(target, new Tag { Name = "Remote canonical" }, new Tag { Name = "Remote alias" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var existingAliasId = Assert.Single(target.Aliases).Id;

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(_ => GraphQlData("""
            "findTag": {
              "id": "remote-tag-1",
              "name": "Remote canonical",
              "description": "Imported description",
              "aliases": ["Remote alias"]
            }
            """)));
        var service = CreateService(context, httpClient, fieldProvenance: new FieldProvenanceService(context));

        var result = await service.BatchTagTagsAsync(
            Endpoint,
            [target.Id],
            refreshAlreadyTagged: true,
            excludeFields: ["name", "aliases", "description"],
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, result.Updated);
        Assert.Null(Assert.Single(result.Items).Message);
        var saved = await context.Tags.Include(tag => tag.Aliases).SingleAsync(tag => tag.Id == target.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Local tag", saved.Name);
        Assert.Equal(existingAliasId, Assert.Single(saved.Aliases).Id);
        Assert.Equal("Existing alias", saved.Aliases.Single().Alias);
        Assert.Equal("Local description", saved.Description);
        Assert.Empty(await context.FieldProvenance
            .Where(row => row.HostType == AffinityHostType.Tag
                && row.HostId == target.Id
                && (row.FieldKey == "name" || row.FieldKey == "aliases" || row.FieldKey == "description"))
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BatchTagTagsAsync_KeepsNameWhenAliasesExcludedAndRemoteNameMatchesOwnAlias()
    {
        await using var context = CreateContext();
        var target = new Tag { Name = "Local tag", Aliases = [new TagAlias { Alias = "Remote canonical" }] };
        target.RemoteIds.Add(new TagRemoteId { Endpoint = Endpoint, RemoteId = "remote-tag-1" });
        context.Add(target);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var aliasId = Assert.Single(target.Aliases).Id;

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(_ => GraphQlData("""
            "findTag": {
              "id": "remote-tag-1",
              "name": "Remote canonical",
              "description": null,
              "aliases": []
            }
            """)));
        var service = CreateService(context, httpClient);

        var result = await service.BatchTagTagsAsync(
            Endpoint,
            [target.Id],
            refreshAlreadyTagged: true,
            excludeFields: ["aliases"],
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, result.Updated);
        Assert.Contains("excluded", Assert.Single(result.Items).Message, StringComparison.OrdinalIgnoreCase);
        var saved = await context.Tags.Include(tag => tag.Aliases).SingleAsync(tag => tag.Id == target.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Local tag", saved.Name);
        Assert.Equal(aliasId, Assert.Single(saved.Aliases).Id);
    }

    [Fact]
    public async Task MergeTagAsync_RemovesAnOwnAliasWhenItBecomesTheCanonicalName()
    {
        await using var context = CreateContext();
        var target = new Tag
        {
            Name = "Local tag",
            Aliases = [new TagAlias { Alias = "  REMOTE canonical  " }],
        };
        context.Add(target);
        using (context.SuppressTagNameValidation())
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(_ => GraphQlData("""
            "findTag": {
              "id": "remote-tag-1",
              "name": "Remote canonical",
              "description": null,
              "aliases": ["Remote canonical"]
            }
            """)));
        var service = CreateService(context, httpClient);

        var result = await service.MergeTagWithWarningsAsync(target, Endpoint, "remote-tag-1", CancellationToken.None);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Imported);
        Assert.Empty(result.Warnings);
        Assert.Equal("Remote canonical", target.Name);
        Assert.Empty(target.Aliases);
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
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

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
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

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
            .SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Fixture Studio", savedVideo.Studio?.Name);
        Assert.Contains(savedVideo.VideoTags, link => link.Tag != null && link.Tag.Name == "Action");
        Assert.Contains(savedVideo.VideoPerformers, link => link.Performer != null && link.Performer.Name == "Jane Doe");

        var tagApplication = await context.TagApplications.Include(application => application.Tag).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(tagApplication.Tag);
        Assert.Equal(AffinityHostType.Video, tagApplication.HostType);
        Assert.Equal(video.Id, tagApplication.HostId);
        Assert.Equal("Action", tagApplication.Tag.Name);
        Assert.Equal($"metadata:{Endpoint}", tagApplication.SourceKey);
        Assert.Equal(Endpoint, tagApplication.SourceRunId);

        var provenanceRows = await fieldProvenance.GetForHostAsync(AffinityHostType.Video, video.Id, TestContext.Current.CancellationToken);
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
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

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
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(imported);
        var savedTag = await context.Tags.Include(tag => tag.Aliases).SingleAsync(tag => tag.Name == "Action", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(savedTag.Aliases);
        var savedVideo = await context.Videos.Include(item => item.VideoTags).ThenInclude(link => link.Tag).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(savedVideo.VideoTags, link => link.Tag != null && link.Tag.Name == "Action");
    }

    [Fact]
    public async Task MergeVideoWithWarningsAsync_SkipsAConflictingRelatedTagAlias()
    {
        await using var context = CreateContext();
        var aliasOwner = new Tag { Name = "Activity" };
        var video = new Video { Title = "Original Video" };
        context.AddRange(aliasOwner, video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(request =>
        {
            Assert.Contains("query FindVideoByID", request.Query);
            return GraphQlData($$"""
                "findVideo": {{RemoteVideoJson}}
                """);
        }));
        var service = CreateService(context, httpClient);

        var result = await service.MergeVideoWithWarningsAsync(
            video,
            Endpoint,
            "remote-video-1",
            new MetadataServerVideoImportRequestDto { SetCoverImage = false },
            CancellationToken.None);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Imported);
        Assert.Contains(result.Warnings, warning => warning.Contains("Skipped remote alias 'Activity'", StringComparison.Ordinal));
        var importedTag = await context.Tags.Include(tag => tag.Aliases).SingleAsync(tag => tag.Name == "Action", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(importedTag.Aliases);
        Assert.Contains(video.VideoTags, link => link.TagId == importedTag.Id || ReferenceEquals(link.Tag, importedTag));
    }

    [Fact]
    public async Task MergeVideoWithWarningsAsync_KeepsAConflictingRelatedTagAliasSilentWhenTheRemoteIdMatched()
    {
        await using var context = CreateContext();
        var owner = new Tag { Name = "Action" };
        owner.RemoteIds.Add(new TagRemoteId { Endpoint = Endpoint, RemoteId = "remote-tag-1" });
        var aliasOwner = new Tag { Name = "Activity" };
        var video = new Video { Title = "Original Video" };
        context.AddRange(owner, aliasOwner, video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(_ => GraphQlData($$"""
            "findVideo": {{RemoteVideoJson}}
            """)));
        var service = CreateService(context, httpClient);

        var result = await service.MergeVideoWithWarningsAsync(
            video,
            Endpoint,
            "remote-video-1",
            new MetadataServerVideoImportRequestDto { SetCoverImage = false },
            CancellationToken.None);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Imported);
        // The remote id already resolved the right tag, so another tag owning the remote's alias is not
        // the operator's problem: the alias is dropped without a warning and no other record changes.
        Assert.Empty(result.Warnings);
        Assert.Contains(video.VideoTags, link => link.TagId == owner.Id || ReferenceEquals(link.Tag, owner));
        var savedOwner = await context.Tags.Include(tag => tag.Aliases).SingleAsync(tag => tag.Id == owner.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(savedOwner.Aliases);
        var savedAliasOwner = await context.Tags.Include(tag => tag.Aliases).SingleAsync(tag => tag.Id == aliasOwner.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Activity", savedAliasOwner.Name);
        Assert.Empty(savedAliasOwner.Aliases);
    }

    [Fact]
    public async Task MergeVideoWithWarningsAsync_PrefersThePersistedRemoteIdOwnerOverATagAddedByNameInTheSameSave()
    {
        await using var context = CreateContext();
        var owner = new Tag { Name = "Local canonical" };
        owner.RemoteIds.Add(new TagRemoteId { Endpoint = Endpoint, RemoteId = "remote-tag-1" });
        var video = new Video { Title = "Original Video" };
        context.AddRange(owner, video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // The earlier remote tag creates a tracked "Action" during this save. The later one carries the
        // remote id the persisted tag already holds, so it must resolve to that tag and not to the
        // freshly added namesake.
        var conflictingTag = "{ \"id\": \"remote-tag-2\", \"name\": \"Action\", \"description\": null, \"aliases\": [] }";
        var remoteTag = "{ \"id\": \"remote-tag-1\", \"name\": \"Action\", \"description\": \"Movement\", \"aliases\": [\"Activity\"] }";
        var remoteVideoJson = RemoteVideoJson.Replace(remoteTag, $"{conflictingTag}, {remoteTag}", StringComparison.Ordinal);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(_ => GraphQlData($$"""
            "findVideo": {{remoteVideoJson}}
            """)));
        var service = CreateService(context, httpClient);

        var result = await service.MergeVideoWithWarningsAsync(
            video,
            Endpoint,
            "remote-video-1",
            new MetadataServerVideoImportRequestDto { SetCoverImage = false },
            CancellationToken.None);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Imported);
        Assert.Empty(result.Warnings);
        var savedOwner = await context.Tags
            .Include(tag => tag.RemoteIds)
            .SingleAsync(tag => tag.Id == owner.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Local canonical", savedOwner.Name);
        Assert.Contains(savedOwner.RemoteIds, id => id.RemoteId == "remote-tag-1");
        Assert.Contains(video.VideoTags, link => link.TagId == owner.Id || ReferenceEquals(link.Tag, owner));
        var namesake = await context.Tags
            .Include(tag => tag.RemoteIds)
            .SingleAsync(tag => tag.Name == "Action", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(owner.Id, namesake.Id);
        Assert.Contains(namesake.RemoteIds, id => id.RemoteId == "remote-tag-2");
    }

    [Fact]
    public async Task MergeVideoWithWarningsAsync_LinksTheExistingOwnerWhenRemoteNameMatchesItsAlias()
    {
        await using var context = CreateContext();
        var existing = new Tag
        {
            Name = "Local canonical",
            Aliases = [new TagAlias { Alias = "Action" }],
        };
        var video = new Video { Title = "Original Video" };
        context.AddRange(existing, video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(request =>
        {
            Assert.Contains("query FindVideoByID", request.Query);
            return GraphQlData($$"""
                "findVideo": {{RemoteVideoJson}}
                """);
        }));
        var service = CreateService(context, httpClient);

        var result = await service.MergeVideoWithWarningsAsync(
            video,
            Endpoint,
            "remote-video-1",
            new MetadataServerVideoImportRequestDto { SetCoverImage = false },
            CancellationToken.None);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Imported);
        Assert.Empty(result.Warnings);
        Assert.Single(await context.Tags.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains(video.VideoTags, link => link.TagId == existing.Id || ReferenceEquals(link.Tag, existing));
    }

    [Fact]
    public async Task MergeVideoWithWarningsAsync_DeduplicatesRepeatedRemoteTags()
    {
        await using var context = CreateContext();
        var video = new Video { Title = "Original Video" };
        context.Add(video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repeatedTag = "{ \"id\": \"remote-tag-1\", \"name\": \"Action\", \"description\": \"Movement\", \"aliases\": [\"Activity\"] }";
        var remoteVideoJson = RemoteVideoJson.Replace(repeatedTag, $"{repeatedTag}, {repeatedTag}", StringComparison.Ordinal);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(_ => GraphQlData($$"""
            "findVideo": {{remoteVideoJson}}
            """)));
        var service = CreateService(context, httpClient);

        var result = await service.MergeVideoWithWarningsAsync(
            video,
            Endpoint,
            "remote-video-1",
            new MetadataServerVideoImportRequestDto { SetCoverImage = false },
            CancellationToken.None);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Imported);
        Assert.Single(await context.Tags.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(video.VideoTags);
    }

    [Theory]
    [InlineData(" action ")]
    [InlineData("Activity")]
    public async Task MergeVideoWithWarningsAsync_ReusesANewTrackedNamespaceOwnerForAnotherRemoteId(string secondName)
    {
        await using var context = CreateContext();
        var video = new Video { Title = "Original Video" };
        context.Add(video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var firstTag = "{ \"id\": \"remote-tag-1\", \"name\": \"Action\", \"description\": \"Movement\", \"aliases\": [\"Activity\"] }";
        var secondTag = $"{{ \"id\": \"remote-tag-2\", \"name\": \"{secondName}\", \"description\": null, \"aliases\": [] }}";
        var remoteVideoJson = RemoteVideoJson.Replace(firstTag, $"{firstTag}, {secondTag}", StringComparison.Ordinal);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(_ => GraphQlData($$"""
            "findVideo": {{remoteVideoJson}}
            """)));
        var service = CreateService(context, httpClient);

        var result = await service.MergeVideoWithWarningsAsync(
            video,
            Endpoint,
            "remote-video-1",
            new MetadataServerVideoImportRequestDto { SetCoverImage = false },
            CancellationToken.None);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Imported);
        var tag = Assert.Single(await context.Tags.Include(entity => entity.RemoteIds).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("Action", tag.Name);
        Assert.Contains(tag.RemoteIds, id => id.RemoteId == "remote-tag-1");
        Assert.Single(video.VideoTags);
    }

    [Fact]
    public async Task MergeVideoWithWarningsAsync_UsesPersistedResolverPolicyInsteadOfAPretrackedLegacyConflict()
    {
        await using var context = CreateContext();
        var lowestId = new Tag { Name = "Action" };
        var pretracked = new Tag { Name = " action " };
        var video = new Video { Title = "Original Video", VideoTags = [new VideoTag { Tag = pretracked }] };
        context.AddRange(lowestId, pretracked, video);
        using (context.SuppressTagNameValidation())
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(_ => GraphQlData($$"""
            "findVideo": {{RemoteVideoJson}}
            """)));
        var service = CreateService(context, httpClient);

        var result = await service.MergeVideoWithWarningsAsync(
            video,
            Endpoint,
            "remote-video-1",
            new MetadataServerVideoImportRequestDto { SetCoverImage = false },
            CancellationToken.None);

        Assert.True(result.Imported);
        Assert.Contains(video.VideoTags, link => link.TagId == lowestId.Id || ReferenceEquals(link.Tag, lowestId));
    }

    [Fact]
    public async Task MergeVideoAsync_MatchesPerformerIdentityAndNormalizedStudioName()
    {
        await using var context = CreateContext();
        var sameNameDifferentPerson = new Performer { Name = "Jane Doe", Disambiguation = "Other person" };
        var matchingPerformer = new Performer { Name = " jane doe ", Disambiguation = null };
        var matchingStudio = new Studio { Name = " fixture studio " };
        var video = new Video { Title = "Original Video" };
        context.AddRange(sameNameDifferentPerson, matchingPerformer, matchingStudio, video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(request =>
        {
            Assert.Contains("query FindVideoByID", request.Query);
            return GraphQlData($$"""
                "findVideo": {{RemoteVideoJson}}
                """);
        }));
        var service = CreateService(context, httpClient);

        Assert.True(await service.MergeVideoAsync(
            video,
            Endpoint,
            "remote-video-1",
            new MetadataServerVideoImportRequestDto { SetCoverImage = false },
            CancellationToken.None));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, await context.Performers.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(await context.Studios.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        var saved = await context.Videos
            .Include(item => item.VideoPerformers)
            .SingleAsync(item => item.Id == video.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(saved.VideoPerformers, link => link.PerformerId == matchingPerformer.Id);
        Assert.DoesNotContain(saved.VideoPerformers, link => link.PerformerId == sameNameDifferentPerson.Id);
        Assert.Equal(matchingStudio.Id, saved.StudioId);
    }

    [Fact]
    public async Task BatchTagPerformersAsync_UsesGraphQlImportAndRestoresExcludedFields()
    {
        await using var context = CreateContext();
        var performer = new Performer { Name = "Local Jane" };
        performer.RemoteIds.Add(new PerformerRemoteId { Endpoint = Endpoint, RemoteId = "remote-performer-1" });
        context.Performers.Add(performer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

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

        var updated = await context.Performers.Include(item => item.Urls).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Local Jane", updated.Name);
        Assert.Equal(GenderEnum.Female, updated.Gender);
        Assert.Contains(updated.Urls, url => url.Url == "https://metadata.example/performers/remote-performer-1");
    }

    [Fact]
    public async Task BatchTagPerformersAsync_IsolatesAnIdentityConflictFromLaterItems()
    {
        await using var context = CreateContext();
        var conflictingIdentity = new Performer { Name = "Collision", Disambiguation = "Fixture performer" };
        var first = new Performer { Name = "First local" };
        first.RemoteIds.Add(new PerformerRemoteId { Endpoint = Endpoint, RemoteId = "remote-first" });
        var second = new Performer { Name = "Second local" };
        second.RemoteIds.Add(new PerformerRemoteId { Endpoint = Endpoint, RemoteId = "remote-second" });
        context.AddRange(conflictingIdentity, first, second);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(request =>
        {
            var id = GetVariableString(request, "id");
            var json = RemotePerformerJson
                .Replace("remote-performer-1", id, StringComparison.Ordinal)
                .Replace("Remote Jane", id == "remote-first" ? "Collision" : "Updated second", StringComparison.Ordinal)
                .Replace("Fixture performer", id == "remote-first" ? "Fixture performer" : "Second identity", StringComparison.Ordinal);
            return GraphQlData($$"""
                "findPerformer": {{json}}
                """);
        }));
        var service = CreateService(context, httpClient);

        var result = await service.BatchTagPerformersAsync(
            Endpoint,
            [first.Id, second.Id],
            refreshAlreadyTagged: true,
            excludeFields: null,
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Updated);
        context.ChangeTracker.Clear();
        Assert.Equal("First local", (await context.Performers.SingleAsync(item => item.Id == first.Id, cancellationToken: TestContext.Current.CancellationToken)).Name);
        var updated = await context.Performers.SingleAsync(item => item.Id == second.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Updated second", updated.Name);
        Assert.Equal("Second identity", updated.Disambiguation);
    }

    [Fact]
    public async Task BatchTagStudiosAsync_RebuildsIdentityLookupAfterAnEarlierRename()
    {
        await using var context = CreateContext();
        var existingParent = new Studio { Name = "Existing parent", ImageBlobId = "existing-image" };
        var first = new Studio { Name = "Former name" };
        first.RemoteIds.Add(new StudioRemoteId { Endpoint = Endpoint, RemoteId = "remote-first" });
        var second = new Studio { Name = "Second studio" };
        second.RemoteIds.Add(new StudioRemoteId { Endpoint = Endpoint, RemoteId = "remote-second" });
        context.AddRange(existingParent, first, second);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        static string StudioJson(string id, string name, string? parentId = null, string? parentName = null)
        {
            var parent = parentId == null
                ? "null"
                : JsonSerializer.Serialize(new { id = parentId, name = parentName });
            return $$"""
                {
                  "id": {{JsonSerializer.Serialize(id)}},
                  "name": {{JsonSerializer.Serialize(name)}},
                  "aliases": [],
                  "urls": [],
                  "images": [],
                  "parent": {{parent}}
                }
                """;
        }

        using var httpClient = new HttpClient(new FixtureMetadataServerHandler(request =>
        {
            var id = GetVariableString(request, "id");
            var json = id switch
            {
                "remote-first" => StudioJson(id, "Renamed studio", "remote-existing-parent", "Existing parent"),
                "remote-second" => StudioJson(id, "Second studio", "remote-new-parent", "Former name"),
                "remote-new-parent" => StudioJson(id, "Former name"),
                _ => throw new InvalidOperationException($"Unexpected studio id {id}"),
            };
            return GraphQlData($$"""
                "findStudio": {{json}}
                """);
        }));
        var service = CreateService(context, httpClient);

        var result = await service.BatchTagStudiosAsync(
            Endpoint,
            [first.Id, second.Id],
            refreshAlreadyTagged: true,
            excludeFields: null,
            createParentStudios: true,
            progress: null,
            CancellationToken.None);

        Assert.Equal(2, result.Updated);
        context.ChangeTracker.Clear();
        var renamed = await context.Studios.SingleAsync(item => item.Id == first.Id, cancellationToken: TestContext.Current.CancellationToken);
        var updatedSecond = await context.Studios.SingleAsync(item => item.Id == second.Id, cancellationToken: TestContext.Current.CancellationToken);
        var createdParent = await context.Studios.SingleAsync(item => item.Name == "Former name", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Renamed studio", renamed.Name);
        Assert.NotEqual(renamed.Id, createdParent.Id);
        Assert.Equal(createdParent.Id, updatedSecond.ParentId);
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
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

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
        // Without a cover there is nothing to upload, so the draft is an ordinary JSON request.
        Assert.Null(request.MapJson);
        Assert.Null(request.Upload);
    }

    [Fact]
    public async Task SubmitVideoDraftAsync_UploadsTheVideoCoverAsTheDraftImage()
    {
        await using var context = CreateContext();
        var video = new Video { Title = "Covered Video", ImageBlobId = "cover-blob" };
        context.Videos.Add(video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        byte[] cover = [0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02];
        var handler = new FixtureMetadataServerHandler(_ => GraphQlData("""
            "submitSceneDraft": { "id": "draft-video-1" }
            """));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            context,
            httpClient,
            blobService: new CoverBlobService("cover-blob", cover, "image/jpeg"),
            streamService: new ScreenshotStreamService([0x89, 0x50]));

        var draftId = await service.SubmitVideoDraftAsync(video, Endpoint, CancellationToken.None);

        Assert.Equal("draft-video-1", draftId);
        var request = Assert.Single(handler.Requests);
        Assert.Contains("mutation SubmitSceneDraft", request.Query);
        Assert.Equal(ApiKey, request.ApiKey);
        Assert.Equal("""{"0":["variables.input.image"]}""", request.MapJson);
        var upload = Assert.IsType<GraphQlUploadSnapshot>(request.Upload);
        Assert.Equal("0", upload.Name);
        Assert.Equal("draft", upload.FileName);
        Assert.Equal("image/jpeg", upload.ContentType);
        Assert.Equal(cover, upload.Data);
        using var variables = JsonDocument.Parse(request.VariablesJson);
        var input = variables.RootElement.GetProperty("input");
        Assert.Equal("Covered Video", input.GetProperty("title").GetString());
        Assert.False(input.TryGetProperty("image", out _));
    }

    [Fact]
    public async Task SubmitVideoDraftAsync_UploadsTheGeneratedScreenshotWhenTheVideoHasNoCover()
    {
        await using var context = CreateContext();
        var video = new Video { Title = "Screenshot Video" };
        context.Videos.Add(video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        byte[] screenshot = [0x89, 0x50, 0x4E, 0x47];
        var handler = new FixtureMetadataServerHandler(_ => GraphQlData("""
            "submitSceneDraft": { "id": "draft-video-1" }
            """));
        using var httpClient = new HttpClient(handler);
        var screenshots = new ScreenshotStreamService(screenshot);
        var service = CreateService(context, httpClient, streamService: screenshots);

        await service.SubmitVideoDraftAsync(video, Endpoint, CancellationToken.None);

        var upload = Assert.IsType<GraphQlUploadSnapshot>(Assert.Single(handler.Requests).Upload);
        Assert.Equal(screenshot, upload.Data);
        Assert.Equal("image/jpeg", upload.ContentType);
        Assert.Equal([video.Id], screenshots.RequestedVideoIds);
    }

    [Fact]
    public async Task SubmitVideoDraftAsync_SendsPlainJsonWhenNeitherACoverNorAScreenshotExists()
    {
        await using var context = CreateContext();
        var video = new Video { Title = "Imageless Video", ImageBlobId = "missing-blob" };
        context.Videos.Add(video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var handler = new FixtureMetadataServerHandler(_ => GraphQlData("""
            "submitSceneDraft": { "id": "draft-video-1" }
            """));
        using var httpClient = new HttpClient(handler);
        var screenshots = new ScreenshotStreamService(null);
        var service = CreateService(
            context,
            httpClient,
            blobService: new CoverBlobService("cover-blob", [0xFF], "image/jpeg"),
            streamService: screenshots);

        await service.SubmitVideoDraftAsync(video, Endpoint, CancellationToken.None);

        // The missing custom cover falls back to the screenshot, and without either the request stays JSON.
        Assert.Equal([video.Id], screenshots.RequestedVideoIds);
        var request = Assert.Single(handler.Requests);
        Assert.Null(request.MapJson);
        Assert.Null(request.Upload);
    }

    [Fact]
    public async Task SubmitVideoDraftAsync_SubmitsWithoutAnImageWhenTheCoverCannotBeRead()
    {
        await using var context = CreateContext();
        var video = new Video { Title = "Unreadable Cover", ImageBlobId = "cover-blob" };
        context.Videos.Add(video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var handler = new FixtureMetadataServerHandler(_ => GraphQlData("""
            "submitSceneDraft": { "id": "draft-video-1" }
            """));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            context,
            httpClient,
            blobService: new CoverBlobService("cover-blob", null, "image/jpeg"));

        var draftId = await service.SubmitVideoDraftAsync(video, Endpoint, CancellationToken.None);

        Assert.Equal("draft-video-1", draftId);
        var request = Assert.Single(handler.Requests);
        Assert.Null(request.MapJson);
        Assert.Null(request.Upload);
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
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

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

    private static MetadataServerService CreateService(CoveContext context, HttpClient httpClient, IFieldProvenanceService? fieldProvenance = null, ITagProvenanceService? tagProvenance = null, CoveConfiguration? configuration = null, ILogger<MetadataServerService>? logger = null, IEventBus? eventBus = null, IBlobService? blobService = null, IStreamService? streamService = null)
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
            blobService ?? new NullBlobService(),
            new NullVideoCoverService(),
            tagProvenance ?? new TagProvenanceService(context),
            logger ?? NullLogger<MetadataServerService>.Instance,
            fieldProvenance,
            eventBus,
            streamService);

    private static CoveConfiguration ThePornDbConfiguration()
        => new()
        {
            Scraping = new ScrapingConfig
            {
                MetadataServers =
                [
                    new MetadataServerInstance
                    {
                        Endpoint = "https://theporndb.net/graphql",
                        ApiKey = ApiKey,
                        Name = "ThePornDB",
                    },
                ],
            },
        };

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"metadata-server-service-{Guid.NewGuid():N}")
            .Options;

        return new CoveContext(options);
    }

    private sealed class CapturingJobProgress : IJobProgress
    {
        public List<(double Progress, string? Message)> Reports { get; } = [];

        public void Report(double progress, string? subTask = null)
            => Reports.Add((progress, subTask));
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

    // One performer per gender shape the filter has to tell apart: a plain one, one whose gender only
    // matches after normalization, and one the server states no gender for.
    private const string MixedGenderRemoteVideoJson = """
        {
          "id": "remote-video-1",
          "title": "Remote Video",
          "code": null,
          "details": null,
          "director": null,
          "duration": 120,
          "date": null,
          "urls": [],
          "images": [],
          "studio": null,
          "tags": [],
          "performers": [
            { "performer": { "id": "remote-performer-1", "name": "Jane Doe", "disambiguation": null, "aliases": [], "gender": "FEMALE", "deleted": false, "merged_into_id": null, "urls": [], "images": [], "birth_date": null, "death_date": null, "ethnicity": null, "country": null, "eye_color": null, "hair_color": null, "height": null, "measurements": null, "breast_type": null, "career_start_year": null, "career_end_year": null, "tattoos": [], "piercings": [] } },
            { "performer": { "id": "remote-performer-2", "name": "John Roe", "disambiguation": null, "aliases": [], "gender": "MALE", "deleted": false, "merged_into_id": null, "urls": [], "images": [], "birth_date": null, "death_date": null, "ethnicity": null, "country": null, "eye_color": null, "hair_color": null, "height": null, "measurements": null, "breast_type": null, "career_start_year": null, "career_end_year": null, "tattoos": [], "piercings": [] } },
            { "performer": { "id": "remote-performer-3", "name": "Tess Poe", "disambiguation": null, "aliases": [], "gender": "TRANSGENDER_FEMALE", "deleted": false, "merged_into_id": null, "urls": [], "images": [], "birth_date": null, "death_date": null, "ethnicity": null, "country": null, "eye_color": null, "hair_color": null, "height": null, "measurements": null, "breast_type": null, "career_start_year": null, "career_end_year": null, "tattoos": [], "piercings": [] } },
            { "performer": { "id": "remote-performer-4", "name": "Sam Roe", "disambiguation": null, "aliases": [], "gender": null, "deleted": false, "merged_into_id": null, "urls": [], "images": [], "birth_date": null, "death_date": null, "ethnicity": null, "country": null, "eye_color": null, "hair_color": null, "height": null, "measurements": null, "breast_type": null, "career_start_year": null, "career_end_year": null, "tattoos": [], "piercings": [] } }
          ],
          "fingerprints": []
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
            string payload;
            string? mapJson = null;
            GraphQlUploadSnapshot? upload = null;
            if (request.Content is MultipartFormDataContent multipart)
            {
                payload = string.Empty;
                foreach (var part in multipart)
                {
                    var disposition = part.Headers.ContentDisposition!;
                    switch (disposition.Name?.Trim('"'))
                    {
                        case "operations":
                            payload = await part.ReadAsStringAsync(cancellationToken);
                            break;
                        case "map":
                            mapJson = await part.ReadAsStringAsync(cancellationToken);
                            break;
                        default:
                            upload = new GraphQlUploadSnapshot(
                                disposition.Name!.Trim('"'),
                                disposition.FileName?.Trim('"'),
                                part.Headers.ContentType?.MediaType,
                                await part.ReadAsByteArrayAsync(cancellationToken));
                            break;
                    }
                }
            }
            else
            {
                payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            }

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var query = GetProperty(root, "query").GetString() ?? string.Empty;
            var variables = GetProperty(root, "variables");
            var apiKey = request.Headers.TryGetValues("ApiKey", out var values) ? values.SingleOrDefault() : null;
            var snapshot = new GraphQlRequestSnapshot(query, variables.GetRawText(), request.RequestUri, apiKey, mapJson, upload);
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

    private sealed record GraphQlRequestSnapshot(
        string Query,
        string VariablesJson,
        Uri? RequestUri,
        string? ApiKey,
        string? MapJson = null,
        GraphQlUploadSnapshot? Upload = null);

    private sealed record GraphQlUploadSnapshot(string Name, string? FileName, string? ContentType, byte[] Data);

    /// <summary>Serves one cover blob, or throws when <paramref name="bytes"/> is null to model an unreadable store.</summary>
    private sealed class CoverBlobService(string blobId, byte[]? bytes, string contentType) : IBlobService
    {
        public Task<string> StoreBlobAsync(Stream data, string contentType, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<(Stream Stream, string ContentType)?> GetBlobAsync(string requestedBlobId, CancellationToken ct = default)
        {
            if (bytes == null)
                throw new IOException("The blob store is unavailable.");

            return Task.FromResult<(Stream Stream, string ContentType)?>(
                requestedBlobId == blobId ? (new MemoryStream(bytes), contentType) : null);
        }

        public Task DeleteBlobAsync(string requestedBlobId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class ScreenshotStreamService(byte[]? screenshot) : IStreamService
    {
        public List<int> RequestedVideoIds { get; } = [];

        public Task<(Stream stream, string contentType, long? fileSize)?> GetVideoStream(int videoId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<(Stream stream, string contentType, bool useLongCache)?> GetVideoScreenshot(int videoId, double? seconds, CancellationToken ct = default)
        {
            RequestedVideoIds.Add(videoId);
            return Task.FromResult<(Stream stream, string contentType, bool useLongCache)?>(
                screenshot == null ? null : (new MemoryStream(screenshot), "image/jpeg", true));
        }

        public Task<(Stream stream, string contentType, bool useLongCache)?> GetSegmentAnimatedPreview(int videoId, double seconds, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

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

        public Task<FetchedImage?> TryFetchImageAsync(string? imageUrl, CancellationToken ct = default)
            => Task.FromResult<FetchedImage?>(null);
    }
}
