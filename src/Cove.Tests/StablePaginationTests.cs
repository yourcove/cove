using Cove.Api.Controllers;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Tests.Postgres;
using System.Collections.Concurrent;
using Npgsql;

namespace Cove.Tests;

[RequiresPostgres]
public class StablePaginationTests(StablePaginationTests.SeededDatabases seeded) : IClassFixture<StablePaginationTests.SeededDatabases>
{
    private static readonly string[] Entities = ["videos", "images", "galleries", "groups", "performers", "studios", "tags", "audios", "texts", "faces", "segments"];
    private static readonly int[] Ids = [19, 3, 14, 7, 22, 1, 11];
    private const int PageSizeSeeded = 1001;

    public static IEnumerable<object[]> Sorts()
    {
        foreach (var entity in Entities)
        foreach (var key in EntityListSortFilterCatalog.Sorts
            .Where(sort => sort.Entity == entity && sort.Key != "random" && sort.Key != "visual_match"
                // These sorts include a name that must be unique, so they cannot have tied values.
                && !(entity is "tags" or "studios" && sort.Key is "name" or "tag_group"))
            .Select(sort => sort.Key).Append("unknown_sort").Distinct())
        foreach (var direction in new[] { SortDirection.Asc, SortDirection.Desc })
            yield return [entity, key, direction, 0];

        foreach (var entity in Entities.Where(entity => entity is not ("groups" or "faces" or "segments")))
        foreach (var direction in new[] { SortDirection.Asc, SortDirection.Desc })
        foreach (var clauseCount in new[] { 1, 2 })
            yield return [entity, "created_at", direction, clauseCount];
    }

    [Theory]
    [MemberData(nameof(Sorts))]
    public async Task TiedSortValuesHaveCompleteRepeatablePagesInRequestedIdOrder(string entity, string sort, SortDirection direction, int clauseCount)
    {
        await using var context = new CoveContext(await seeded.ReadOnlyOptionsAsync($"tied:{entity}"), CreatePrincipal());

        for (var pass = 0; pass < 2; pass++)
        {
            var ids = new List<int>();
            for (var page = 1; page <= 4; page++)
            {
                var filter = new FindFilter { Page = page, PerPage = 2, Sort = sort, Direction = direction };
                if (clauseCount > 0)
                {
                    var opposite = direction == SortDirection.Desc ? SortDirection.Asc : SortDirection.Desc;
                    filter.Direction = opposite; // The explicit primary clause takes precedence.
                    filter.Sorts = [new SortClause(sort, direction)];
                    if (clauseCount > 1)
                        filter.Sorts.Add(new SortClause("updated_at", opposite));
                }
                var (items, total) = await FindAsync(context, entity, filter);
                Assert.Equal(Ids.Length, total);
                Assert.Equal(page < 4 ? 2 : 1, items.Length);
                ids.AddRange(items);
            }
            Assert.Equal(Ids.Length, ids.Distinct().Count());
            // These review orders intentionally ignore the direction toggle.
            var fixedFaceOrder = entity == "faces" && sort is "appearance" or "suggestion_confidence" or "unknown_sort";
            var expected = direction == SortDirection.Desc && !fixedFaceOrder ? Ids.OrderDescending() : Ids.Order();
            Assert.Equal(expected.ToArray(), ids);
        }
    }


    [Theory]
    [InlineData(SortDirection.Asc)]
    [InlineData(SortDirection.Desc)]
    public async Task EngagementSortsWithoutAUserFollowTheRequestedIdDirection(SortDirection direction)
    {
        await using var context = new CoveContext(await seeded.ReadOnlyOptionsAsync("engagement"));
        foreach (var sort in new[] { "rating", "play_count", "play_duration", "last_played_at", "last_like_at" })
        {
            var filter = new FindFilter { Sort = sort, Direction = direction, PerPage = 20 };
            var (ids, total) = await FindAsync(context, "videos", filter);
            Assert.Equal(Ids.Length, total);
            Assert.Equal((direction == SortDirection.Desc ? Ids.OrderDescending() : Ids.Order()).ToArray(), ids);
        }
    }


    [Theory]
    [InlineData("audios", SortDirection.Asc)]
    [InlineData("audios", SortDirection.Desc)]
    [InlineData("texts", SortDirection.Asc)]
    [InlineData("texts", SortDirection.Desc)]
    public async Task ExplicitSingleClauseKeyOverridesLegacySortKey(string entity, SortDirection direction)
    {
        await using var context = new CoveContext(await seeded.ReadOnlyOptionsAsync($"explicit:{entity}"));

        var ids = new List<int>();
        for (var page = 1; page <= 2; page++)
        {
            var filter = new FindFilter
            {
                Sort = "title", Direction = direction, Page = page, PerPage = 1,
                Sorts = [new SortClause("created_at", direction)],
            };
            var (items, total) = await FindAsync(context, entity, filter);
            Assert.Equal(2, total);
            ids.Add(Assert.Single(items));
        }
        Assert.Equal(direction == SortDirection.Asc ? [2, 1] : new[] { 1, 2 }, ids);
    }

    [Theory]
    [InlineData("audios", 500, 500)]
    [InlineData("audios", 1000, 1000)]
    [InlineData("audios", 1001, ListPaging.MaxPerPage)]
    [InlineData("texts", 500, 500)]
    [InlineData("texts", 1000, 1000)]
    [InlineData("texts", 1001, ListPaging.MaxPerPage)]
    [InlineData("segments", 500, 500)]
    [InlineData("segments", 1000, 1000)]
    [InlineData("segments", 1001, ListPaging.MaxPerPage)]
    [InlineData("faces", 500, 500)]
    [InlineData("faces", 1000, 1000)]
    [InlineData("faces", 1001, ListPaging.MaxPerPage)]
    public async Task PageSizesOfferedByTheListToolbarAreHonoured(string entity, int perPage, int expectedItems)
    {
        // The list toolbar offers 500 and 1000 per page; these lists once returned only the first 250 (#698).
        await using var context = new CoveContext(await seeded.ReadOnlyOptionsAsync($"page-size:{entity}"));

        var (items, total) = await FindAsync(context, entity, new FindFilter { Page = 1, PerPage = perPage });

        Assert.Equal(PageSizeSeeded, total);
        Assert.Equal(expectedItems, items.Length);
    }

    [Fact]
    public async Task CasesCannotChangeTheSharedSeed()
    {
        await using var context = new CoveContext(await seeded.ReadOnlyOptionsAsync("tied:videos"), CreatePrincipal());
        // Never committed, so the shared seed stays intact even if the server ever accepted the write.
        await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        context.Videos.Add(new Video { Title = "Added by a case" });

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));

        // 25006 is read_only_sql_transaction: the server refuses the write, so no case can leak rows into the next.
        Assert.Equal(PostgresErrorCodes.ReadOnlySqlTransaction, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
    }

    private static CurrentPrincipalAccessor CreatePrincipal()
    {
        var principal = new CurrentPrincipalAccessor();
        principal.Set(new CovePrincipal
        {
            UserId = 1, Username = "test-user", Kind = PrincipalKind.User,
            Permissions = new HashSet<string> { "*" }, Roles = new HashSet<string>(),
        });
        return principal;
    }

    /// <summary>
    /// Every dataset the cases read, each seeded the first time a case asks for it, in a database of its own, so each
    /// query sees exactly the rows it would in a database seeded for it alone. Cases connect with
    /// <c>default_transaction_read_only</c>, so the server rejects any write a case makes to a seed the other cases share.
    /// </summary>
    public sealed class SeededDatabases : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, Lazy<Task<(CovePostgresDatabase Database, NpgsqlDataSource ReadOnly)>>> _databases = new();

        public async Task<DbContextOptions<CoveContext>> ReadOnlyOptionsAsync(string key)
        {
            var seeded = await _databases.GetOrAdd(key, name => new(() => SeedAsync(name))).Value;
            return CovePostgresDatabase.CreateOptions(seeded.ReadOnly);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var lazy in _databases.Values.Where(lazy => lazy.IsValueCreated))
            {
                (CovePostgresDatabase Database, NpgsqlDataSource ReadOnly) seeded;
                try { seeded = await lazy.Value; }
                catch { continue; } // A failed seed already dropped its database.
                await seeded.ReadOnly.DisposeAsync();
                await seeded.Database.DisposeAsync();
            }
            _databases.Clear();
        }

        private static async Task<(CovePostgresDatabase, NpgsqlDataSource)> SeedAsync(string key)
        {
            var ct = TestContext.Current.CancellationToken;
            var (kind, entity) = key.Split(':') is [var first, var second] ? (first, second) : (key, "");
            var database = await CovePostgresDatabase.CreateAsync(ct);
            try
            {
                await using (var context = kind == "tied" ? new CoveContext(database.CreateOptions(), CreatePrincipal()) : database.CreateContext())
                {
                    switch (kind)
                    {
                        case "tied": SeedTied(context, entity); break;
                        case "engagement": SeedEngagement(context); break;
                        case "explicit": SeedExplicitClause(context, entity); break;
                        case "page-size": SeedPageSize(context, entity); break;
                        default: throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown seed.");
                    }
                    await context.SaveChangesAsync(ct);
                }
                // Nothing writes to the seed again, so do not keep its pooled connection open for the whole class.
                database.DataSource.Clear();

                var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString);
                connection.Options = string.Join(' ', new[] { connection.Options, "-c default_transaction_read_only=on" }.Where(option => !string.IsNullOrEmpty(option)));
                return (database, CovePostgresDatabase.CreateDataSource(connection.ConnectionString));
            }
            catch
            {
                // Keep the seeding failure; a failed drop of the half-seeded database must not replace it.
                try { await database.DisposeAsync(); } catch { }
                throw;
            }
        }

        private static void SeedTied(CoveContext context, string entity)
        {
            var timestamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            if (entity == "segments")
                context.Videos.Add(new Video { Id = 100, Title = "Segment host" });
            foreach (var id in Ids)
            {
                BaseEntity item = entity switch
                {
                    "audios" => new Audio { Title = "Tied" },
                    "texts" => new TextDocument { Title = "Tied" },
                    "faces" => new Face { Label = "Tied" },
                    "segments" => new Segment { Title = "Tied", HostType = SegmentHostType.Video, HostId = 100, StartSec = 1, EndSec = 2 },
                    "videos" => new Video { Title = "Tied" },
                    "images" => new Image { Title = "Tied" },
                    "galleries" => new Gallery { Title = "Tied" },
                    "groups" => new Group { Name = "Tied" },
                    "performers" => new Performer { Name = "Tied", Disambiguation = id.ToString() },
                    "studios" => new Studio { Name = $"Tied {id:D2}" },
                    "tags" => new Tag { Name = $"Tied {id:D2}" },
                    _ => throw new ArgumentOutOfRangeException(nameof(entity)),
                };
                item.Id = id;
                item.CreatedAt = item.UpdatedAt = timestamp;
                context.Add(item);
            }
        }

        private static void SeedEngagement(CoveContext context)
        {
            context.Videos.AddRange(Ids.Select(id => new Video { Id = id }));
        }

        private static void SeedExplicitClause(CoveContext context, string entity)
        {
            foreach (var id in new[] { 1, 2 })
            {
                var title = id == 1 ? "Alpha" : "Beta";
                BaseEntity item = entity == "audios" ? new Audio { Title = title } : new TextDocument { Title = title };
                item.Id = id;
                item.CreatedAt = new DateTime(2024, 1, 3 - id, 0, 0, 0, DateTimeKind.Utc);
                context.Add(item);
            }
        }

        private static void SeedPageSize(CoveContext context, string entity)
        {
            if (entity == "segments")
                context.Videos.Add(new Video { Id = 1, Title = "Segment host" });
            for (var id = 1; id <= PageSizeSeeded; id++)
            {
                context.Add<BaseEntity>(entity switch
                {
                    "audios" => new Audio { Id = id, Title = $"Audio {id}" },
                    "texts" => new TextDocument { Id = id, Title = $"Text {id}" },
                    "segments" => new Segment { Id = id, Title = $"Segment {id}", HostType = SegmentHostType.Video, HostId = 1, StartSec = 1, EndSec = 2 },
                    "faces" => new Face { Id = id, Label = $"Face {id}" },
                    _ => throw new ArgumentOutOfRangeException(nameof(entity)),
                });
            }
        }
    }

    private static async Task<(int[] Ids, int Total)> FindAsync(CoveContext context, string entity, FindFilter filter)
        => entity switch
        {
            "audios" => Extract(await new AudiosController(context, null!, null!, null!, null!, null).FindPost(new FilteredQueryRequest<AudioFilter> { FindFilter = filter }, TestContext.Current.CancellationToken), item => item.Id),
            "texts" => Extract(await new TextsController(context, null!, null!, null!, null!, null!, null).FindPost(new FilteredQueryRequest<TextDocumentFilter> { FindFilter = filter }, TestContext.Current.CancellationToken), item => item.Id),
            "faces" => await QueryFaceIdsAsync(context, filter),
            "segments" => await QuerySegmentIdsAsync(context, filter),
            "videos" => Project(await new VideoRepository(context).FindAsync(null, filter, TestContext.Current.CancellationToken)),
            "images" => Project(await new ImageRepository(context).FindAsync(null, filter, TestContext.Current.CancellationToken)),
            "galleries" => Project(await new GalleryRepository(context).FindAsync(null, filter, TestContext.Current.CancellationToken)),
            "groups" => Project(await new GroupRepository(context).FindAsync(null, filter, TestContext.Current.CancellationToken)),
            "performers" => Project(await new PerformerRepository(context).FindAsync(null, filter, TestContext.Current.CancellationToken)),
            "studios" => Project(await new StudioRepository(context).FindAsync(null, filter, TestContext.Current.CancellationToken)),
            "tags" => Project(await new TagRepository(context).FindAsync(null, filter, TestContext.Current.CancellationToken)),
            _ => throw new ArgumentOutOfRangeException(nameof(entity)),
        };

    private static async Task<(int[], int)> QueryFaceIdsAsync(CoveContext context, FindFilter filter)
    {
        var controller = new FacesController(context, null!, null!, null!, [], NullLogger<FacesController>.Instance, [], null);
        var response = await controller.List(
            q: null,
            performerId: null,
            performerIds: null,
            linked: null,
            ignored: null,
            merged: null,
            minSuggestionConfidence: null,
            suggestionConfidence: null,
            suggestionConfidence2: null,
            suggestionConfidenceModifier: null,
            topSuggestionPerformerIds: null,
            sort: filter.Sort,
            direction: filter.Direction,
            customFieldCriteria: null,
            page: filter.Page,
            perPage: filter.PerPage, cancellationToken: TestContext.Current.CancellationToken);

        return Extract(response, item => item.Id);
    }

    private static async Task<(int[], int)> QuerySegmentIdsAsync(CoveContext context, FindFilter filter)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = new SegmentsController(context, new SegmentRepository(context), null!, cache);
        var response = await controller.List(
            q: null,
            ids: null,
            videoId: null,
            videoIds: null,
            videoTitle: null,
            tagId: null,
            tagIds: null,
            kind: null,
            sourceKey: null,
            sourceCategory: null,
            refIds: null,
            performerIds: null,
            tagged: null,
            minConfidence: null,
            minDurationSec: null,
            confidence: null,
            confidence2: null,
            confidenceModifier: null,
            durationSec: null,
            durationSec2: null,
            durationModifier: null,
            sort: filter.Sort,
            direction: DirectionValue(filter.Direction),
            excludeVideoIds: null,
            page: filter.Page,
            perPage: filter.PerPage, cancellationToken: TestContext.Current.CancellationToken);

        return Extract(response, item => item.Id);
    }

    private static string DirectionValue(SortDirection direction) => direction == SortDirection.Desc ? "desc" : "asc";

    private static (int[], int) Extract<T>(ActionResult<PaginatedResponse<T>> result, Func<T, int> idSelector)
    {
        var response = Assert.IsType<PaginatedResponse<T>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        return (response.Items.Select(idSelector).ToArray(), response.TotalCount);
    }

    private static (int[], int) Project<T>((IEnumerable<T> Items, int TotalCount) result) where T : BaseEntity
        => (result.Items.Select(item => item.Id).ToArray(), result.TotalCount);
}
