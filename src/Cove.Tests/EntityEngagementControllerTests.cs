using Cove.Api.Controllers;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Cove.Tests;

public class EntityEngagementControllerTests
{
    [Fact]
    public async Task FavoriteAndRatingEndpoints_AreUserScopedForPerformerEntities()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;

        context.Performers.Add(new Performer { Name = "Scoped Performer" });
        await context.SaveChangesAsync();
        var performerId = await context.Performers.Select(performer => performer.Id).SingleAsync();

        var controller = new EntityEngagementController(new UserEngagementService(context, principalAccessor), principalAccessor);

        principalAccessor.Set(CreatePrincipal(7));

        var favoriteResult = await controller.SetFavorite(AffinityHostType.Performer, performerId, new EntityFavoriteDto(true), CancellationToken.None);
        var favoriteOk = Assert.IsType<OkObjectResult>(favoriteResult.Result);
        var favoriteDto = Assert.IsType<EntityEngagementDto>(favoriteOk.Value);
        Assert.True(favoriteDto.IsFavorite);

        var ratingResult = await controller.SetRating(AffinityHostType.Performer, performerId, new VideoRatingDto(91), CancellationToken.None);
        var ratingOk = Assert.IsType<OkObjectResult>(ratingResult.Result);
        var ratingDto = Assert.IsType<EntityEngagementDto>(ratingOk.Value);
        Assert.Equal(91, ratingDto.Rating);

        var audioRatingResult = await controller.SetRating(AffinityHostType.Performer, performerId, new VideoRatingDto(40, "audio"), CancellationToken.None);
        var audioRatingOk = Assert.IsType<OkObjectResult>(audioRatingResult.Result);
        var audioRatingDto = Assert.IsType<EntityEngagementDto>(audioRatingOk.Value);
        Assert.Equal(91, audioRatingDto.Rating);

        var ratingsResult = await controller.GetRatings(AffinityHostType.Performer, performerId, CancellationToken.None);
        var ratingsOk = Assert.IsType<OkObjectResult>(ratingsResult.Result);
        var ratingsDto = Assert.IsType<EntityRatingsDto>(ratingsOk.Value);
        Assert.Equal(91, ratingsDto.Ratings["overall"]);
        Assert.Equal(40, ratingsDto.Ratings["audio"]);
        Assert.Equal(91, await context.Ratings.Where(rating => rating.UserId == 7 && rating.HostType == RatingHostType.Performer && rating.HostId == performerId && rating.Aspect == "overall").Select(rating => rating.Value).SingleAsync());

        context.ChangeTracker.Clear();
        principalAccessor.Set(CreatePrincipal(9));

        var getResult = await controller.Get(AffinityHostType.Performer, performerId, CancellationToken.None);
        var getOk = Assert.IsType<OkObjectResult>(getResult.Result);
        var otherUserDto = Assert.IsType<EntityEngagementDto>(getOk.Value);
        Assert.False(otherUserDto.IsFavorite);
        Assert.Null(otherUserDto.Rating);

        var otherRatingsResult = await controller.GetRatings(AffinityHostType.Performer, performerId, CancellationToken.None);
        var otherRatingsOk = Assert.IsType<OkObjectResult>(otherRatingsResult.Result);
        var otherRatingsDto = Assert.IsType<EntityRatingsDto>(otherRatingsOk.Value);
        Assert.Empty(otherRatingsDto.Ratings);

        principalAccessor.Set(CreatePrincipal(7));
        var batchResult = await controller.Batch(new EntityEngagementBatchRequestDto(AffinityHostType.Performer, [performerId]), CancellationToken.None);
        var batchOk = Assert.IsType<OkObjectResult>(batchResult.Result);
        var batchItems = Assert.IsAssignableFrom<IReadOnlyList<EntityEngagementDto>>(batchOk.Value);
        var batchItem = Assert.Single(batchItems);
        Assert.True(batchItem.IsFavorite);
        Assert.Equal(91, batchItem.Rating);
    }

    [Fact]
    public async Task GenericInteractionEndpoints_RecordAndQueryUserScopedHistory()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var principalAccessor = scope.PrincipalAccessor;

        context.Images.Add(new Image { Title = "Tracked Image" });
        await context.SaveChangesAsync();
        var imageId = await context.Images.Select(image => image.Id).SingleAsync();

        var controller = new EntityEngagementController(new UserEngagementService(context, principalAccessor), principalAccessor);

        principalAccessor.Set(CreatePrincipal(7));

        Assert.IsType<NoContentResult>(await controller.RecordInteraction(
            new EngagementInteractionWriteDto(
                "search",
                null,
                "searchQuery",
                Meta: JsonSerializer.SerializeToElement(new { query = "tracked phrase", resultCount = 2 })),
            CancellationToken.None));

        Assert.IsType<NoContentResult>(await controller.RecordInteraction(
            new EngagementInteractionWriteDto(
                "collection",
                null,
                "filterApply",
                Meta: JsonSerializer.SerializeToElement(new { pageKey = "images", criteria = new[] { "rating", "performers" } })),
            CancellationToken.None));

        Assert.IsType<NoContentResult>(await controller.RecordInteraction(
            new EngagementInteractionWriteDto(
                "image",
                imageId,
                "openDetail",
                Meta: JsonSerializer.SerializeToElement(new { source = "imageDetailPage" })),
            CancellationToken.None));

        Assert.IsType<BadRequestObjectResult>(await controller.RecordInteraction(
            new EngagementInteractionWriteDto("image", imageId, "likeCount"),
            CancellationToken.None));

        var imageInteractionsResult = await controller.GetInteractions("image", imageId, 10, CancellationToken.None);
        var imageInteractionsOk = Assert.IsType<OkObjectResult>(imageInteractionsResult.Result);
        var imageInteractions = Assert.IsAssignableFrom<IReadOnlyList<EngagementInteractionDto>>(imageInteractionsOk.Value);
        var imageInteraction = Assert.Single(imageInteractions);
        Assert.Equal("image", imageInteraction.HostType);
        Assert.Equal(imageId, imageInteraction.HostId);
        Assert.Equal("openDetail", imageInteraction.Kind);
        Assert.True(imageInteraction.Meta.HasValue);
        Assert.Equal("imageDetailPage", imageInteraction.Meta.Value.GetProperty("source").GetString());

        var searchInteractionsResult = await controller.GetInteractions("search", null, 10, CancellationToken.None);
        var searchInteractionsOk = Assert.IsType<OkObjectResult>(searchInteractionsResult.Result);
        var searchInteractions = Assert.IsAssignableFrom<IReadOnlyList<EngagementInteractionDto>>(searchInteractionsOk.Value);
        var searchInteraction = Assert.Single(searchInteractions);
        Assert.Equal("search", searchInteraction.HostType);
        Assert.Null(searchInteraction.HostId);
        Assert.Equal("searchQuery", searchInteraction.Kind);
        Assert.True(searchInteraction.Meta.HasValue);
        Assert.Equal("tracked phrase", searchInteraction.Meta.Value.GetProperty("query").GetString());

        context.ChangeTracker.Clear();
        principalAccessor.Set(CreatePrincipal(9));

        var otherUserInteractionsResult = await controller.GetInteractions(null, null, 20, CancellationToken.None);
        var otherUserInteractionsOk = Assert.IsType<OkObjectResult>(otherUserInteractionsResult.Result);
        var otherUserInteractions = Assert.IsAssignableFrom<IReadOnlyList<EngagementInteractionDto>>(otherUserInteractionsOk.Value);
        Assert.Empty(otherUserInteractions);

        principalAccessor.Set(CreatePrincipal(7));
        var interactionRows = await context.Interactions.IgnoreQueryFilters().OrderBy(interaction => interaction.Id).ToListAsync();
        Assert.Equal(3, interactionRows.Count);
        Assert.Contains(interactionRows, interaction => interaction.HostType == InteractionHostType.Search && interaction.Kind == InteractionKind.SearchQuery && interaction.HostId == 0);
        Assert.Contains(interactionRows, interaction => interaction.HostType == InteractionHostType.Collection && interaction.Kind == InteractionKind.FilterApply && interaction.HostId == 0);
        Assert.Contains(interactionRows, interaction => interaction.HostType == InteractionHostType.Image && interaction.Kind == InteractionKind.OpenDetail && interaction.HostId == imageId);
    }

    private static CovePrincipal CreatePrincipal(int userId) => new()
    {
        UserId = userId,
        Username = $"user-{userId}",
        Kind = PrincipalKind.User,
        Roles = new HashSet<string>(),
        Permissions = new HashSet<string>
        {
            Permissions.PerformersRead,
            Permissions.ImagesRead,
        },
    };

    private static async Task<TestContextScope> CreateContextAsync()
    {
        var principalAccessor = new CurrentPrincipalAccessor();
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;

        var context = new EntityEngagementTestContext(options, principalAccessor);
        await context.Database.EnsureCreatedAsync();
        return new TestContextScope(context, connection, principalAccessor);
    }

    private sealed class EntityEngagementTestContext(DbContextOptions<CoveContext> options, ICurrentPrincipalAccessor principalAccessor) : CoveContext(options, principalAccessor)
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
}
