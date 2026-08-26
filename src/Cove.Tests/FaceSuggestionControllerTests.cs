using Cove.Api.Controllers;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public sealed class FaceSuggestionControllerTests
{
    [Fact]
    public async Task FaceSuggestionsController_ReturnsEmptyListWithoutSuggesters()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var face = new Face { Label = "Lead", PrimarySourceKey = "ext:ai.faces" };
        context.Faces.Add(face);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = CreateController(context, scope.PrincipalAccessor);

        var result = await controller.GetSuggestions(face.Id, 5, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var suggestions = Assert.IsAssignableFrom<IReadOnlyList<FaceSuggestionDto>>(ok.Value);

        Assert.Empty(suggestions);
    }

    [Fact]
    public async Task FaceSuggestionsAggregator_DedupesAcrossSuggesters()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var face = new Face { Label = "Lead", PrimarySourceKey = "ext:ai.faces" };
        var performerA = new Performer { Name = "Performer A" };
        var performerB = new Performer { Name = "Performer B" };
        context.Faces.Add(face);
        context.Performers.AddRange(performerA, performerB);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = CreateController(
            context,
            scope.PrincipalAccessor,
            new StubFaceSuggester([
                new FaceSuggestionDto(performerA.Id, performerA.Name, "/covers/a.jpg", 0.61f, "match-a1", [new FaceSuggestionEvidenceDto(11, "/thumbs/11.jpg", 0.61f)]),
                new FaceSuggestionDto(performerB.Id, performerB.Name, "/covers/b.jpg", 0.55f, "match-b1", [new FaceSuggestionEvidenceDto(12, "/thumbs/12.jpg", 0.55f)]),
            ]),
            new StubFaceSuggester([
                new FaceSuggestionDto(performerA.Id, performerA.Name, "/covers/a-2.jpg", 0.87f, "match-a2", [new FaceSuggestionEvidenceDto(13, "/thumbs/13.jpg", 0.87f), new FaceSuggestionEvidenceDto(14, "/thumbs/14.jpg", 0.83f)]),
            ]));

        var result = await controller.GetSuggestions(face.Id, 5, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var suggestions = Assert.IsAssignableFrom<IReadOnlyList<FaceSuggestionDto>>(ok.Value);

        Assert.Equal(2, suggestions.Count);
        Assert.Equal(performerA.Id, suggestions[0].PerformerId);
        Assert.Equal(0.87f, suggestions[0].Confidence);
        Assert.Equal("match-a2", suggestions[0].Why);
        Assert.Equal(performerB.Id, suggestions[1].PerformerId);
    }

    [Fact]
    public async Task FaceSuggestionsController_RejectionsAreHonored()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var face = new Face { Label = "Lead", PrimarySourceKey = "ext:ai.faces" };
        var rejectedPerformer = new Performer { Name = "Rejected Performer" };
        var remainingPerformer = new Performer { Name = "Remaining Performer" };
        context.Faces.Add(face);
        context.Performers.AddRange(rejectedPerformer, remainingPerformer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        SetCurrentUser(scope.PrincipalAccessor, 7, "tester");

        var controller = CreateController(
            context,
            scope.PrincipalAccessor,
            new StubFaceSuggester([
                new FaceSuggestionDto(rejectedPerformer.Id, rejectedPerformer.Name, null, 0.82f, "reject-me", []),
                new FaceSuggestionDto(remainingPerformer.Id, remainingPerformer.Name, null, 0.58f, "keep-me", []),
            ]));

        var rejectResult = await controller.RecordSuggestionDecision(face.Id, new FaceSuggestionDecisionDto(rejectedPerformer.Id, FaceSuggestionDecisionValues.Reject), CancellationToken.None);
        Assert.IsType<OkObjectResult>(rejectResult.Result);

        var suggestionsResult = await controller.GetSuggestions(face.Id, 5, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(suggestionsResult.Result);
        var suggestions = Assert.IsAssignableFrom<IReadOnlyList<FaceSuggestionDto>>(ok.Value);

        var remaining = Assert.Single(suggestions);
        Assert.Equal(remainingPerformer.Id, remaining.PerformerId);

        var persistedDecision = await context.FaceSuggestionDecisions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(face.Id, persistedDecision.FaceId);
        Assert.Equal(rejectedPerformer.Id, persistedDecision.PerformerId);
        Assert.Equal(7, persistedDecision.UserId);
        Assert.Equal(FaceSuggestionDecisionValues.Reject, persistedDecision.Decision);
    }

    [Fact]
    public async Task FaceSuggestionsController_AcceptLinksPerformerAndSuppressesFutureSuggestions()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var face = new Face { Label = "Lead", PrimarySourceKey = "ext:ai.faces" };
        var performer = new Performer { Name = "Chosen Performer" };
        context.Faces.Add(face);
        context.Performers.Add(performer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        SetCurrentUser(scope.PrincipalAccessor, 11, "linker");

        var controller = CreateController(
            context,
            scope.PrincipalAccessor,
            new StubFaceSuggester([
                new FaceSuggestionDto(performer.Id, performer.Name, null, 0.91f, "best-match", []),
            ]));

        var acceptResult = await controller.RecordSuggestionDecision(face.Id, new FaceSuggestionDecisionDto(performer.Id, FaceSuggestionDecisionValues.Accept), CancellationToken.None);
        Assert.IsType<OkObjectResult>(acceptResult.Result);

        var persistedFace = await context.Faces.SingleAsync(item => item.Id == face.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(performer.Id, persistedFace.PerformerId);

        var suggestionsResult = await controller.GetSuggestions(face.Id, 5, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(suggestionsResult.Result);
        var suggestions = Assert.IsAssignableFrom<IReadOnlyList<FaceSuggestionDto>>(ok.Value);
        Assert.Empty(suggestions);
    }

    private static FacesController CreateController(CoveContext context, CurrentPrincipalAccessor principalAccessor, params IFaceSuggester[] suggesters)
    {
        var embeddingService = new EmbeddingService(context, []);
        return new FacesController(
            context,
            embeddingService,
            new StubBlobService(),
            new FacePerformerPropagationService(context),
            [],
            NullLogger<FacesController>.Instance,
            suggesters,
            principalAccessor);
    }

    private static void SetCurrentUser(CurrentPrincipalAccessor principalAccessor, int userId, string username)
    {
        principalAccessor.Set(new CovePrincipal
        {
            UserId = userId,
            Username = username,
            Kind = PrincipalKind.User,
            Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        });
    }

    private static async Task<TestContextScope> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var principalAccessor = new CurrentPrincipalAccessor();
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;

        var context = new FaceSuggestionTestContext(options, principalAccessor);
        await context.Database.EnsureCreatedAsync();
        return new TestContextScope(context, connection, principalAccessor);
    }

    private sealed class FaceSuggestionTestContext(DbContextOptions<CoveContext> options, ICurrentPrincipalAccessor principalAccessor) : CoveContext(options, principalAccessor)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

        }
    }

    private sealed class TestContextScope(CoveContext context, SqliteConnection connection, CurrentPrincipalAccessor principalAccessor) : IAsyncDisposable
    {
        public CoveContext Context { get; } = context;
        public CurrentPrincipalAccessor PrincipalAccessor { get; } = principalAccessor;

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class StubFaceSuggester(IReadOnlyList<FaceSuggestionDto> suggestions) : IFaceSuggester
    {
        public Task<IReadOnlyList<FaceSuggestionDto>> SuggestForAsync(int faceId, int maxResults, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FaceSuggestionDto>>(suggestions.Take(maxResults).ToList());
    }

    private sealed class StubBlobService : IBlobService
    {
        public Task<string> StoreBlobAsync(Stream data, string contentType, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<(Stream Stream, string ContentType)?> GetBlobAsync(string blobId, CancellationToken ct = default)
            => Task.FromResult<(Stream, string)?>(null);

        public Task DeleteBlobAsync(string blobId, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
