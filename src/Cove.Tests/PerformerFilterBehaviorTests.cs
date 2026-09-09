using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public class PerformerFilterBehaviorTests
{
    [Theory]
    [InlineData(CriterionModifier.Equals, "Canada", "Canadian")]
    [InlineData(CriterionModifier.NotEquals, "Canada", "American")]
    [InlineData(CriterionModifier.Includes, "Canada", "Canadian")]
    [InlineData(CriterionModifier.Excludes, "Canada", "American")]
    public async Task CountryCriterion_NormalizesKnownNamesForSavedFilters(CriterionModifier modifier, string value, string expectedName)
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        context.Performers.AddRange(
            new Performer { Name = "Canadian", Country = "CA" },
            new Performer { Name = "American", Country = "US" });
        if (modifier == CriterionModifier.Includes)
            context.Performers.AddRange(
                new Performer { Name = "Custom", Country = "Canada West" },
                new Performer { Name = "Unrelated", Country = "Catalonia" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new PerformerRepository(context);
        var filter = new PerformerFilter
        {
            CountryCriterion = new StringCriterion { Value = value, Modifier = modifier },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20 }, TestContext.Current.CancellationToken);

        if (modifier == CriterionModifier.Includes)
        {
            Assert.Equal(2, totalCount);
            Assert.Equal(["Canadian", "Custom"], items.Select(item => item.Name).Order().ToArray());
        }
        else
        {
            Assert.Equal(1, totalCount);
            Assert.Equal(expectedName, Assert.Single(items).Name);
        }
    }

    [Fact]
    public async Task StudiosCriterion_IncludesAll_RequiresVideosFromAllSelectedStudios()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var alphaStudio = new Studio { Name = "Alpha" };
        var betaStudio = new Studio { Name = "Beta" };

        context.Studios.AddRange(alphaStudio, betaStudio);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await SeedPerformerAsync(context, "both-studios", alphaStudio, betaStudio);
        await SeedPerformerAsync(context, "alpha-only", alphaStudio);
        await SeedPerformerAsync(context, "beta-only", betaStudio);

        var repository = new PerformerRepository(context);
        var filter = new PerformerFilter
        {
            StudiosCriterion = new MultiIdCriterion
            {
                Value = [alphaStudio.Id, betaStudio.Id],
                Modifier = CriterionModifier.IncludesAll,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken);

        Assert.Equal(1, totalCount);
        Assert.Equal(["both-studios"], items.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task StudiosCriterion_ExcludedIds_RemovePerformersWithExcludedStudios()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var alphaStudio = new Studio { Name = "Alpha" };
        var betaStudio = new Studio { Name = "Beta" };

        context.Studios.AddRange(alphaStudio, betaStudio);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await SeedPerformerAsync(context, "alpha-only", alphaStudio);
        await SeedPerformerAsync(context, "alpha-and-beta", alphaStudio, betaStudio);
        await SeedPerformerAsync(context, "beta-only", betaStudio);

        var repository = new PerformerRepository(context);
        var filter = new PerformerFilter
        {
            StudiosCriterion = new MultiIdCriterion
            {
                Value = [alphaStudio.Id],
                Excludes = [betaStudio.Id],
                Modifier = CriterionModifier.Includes,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken);

        Assert.Equal(1, totalCount);
        Assert.Equal(["alpha-only"], items.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task StudiosCriterion_DepthIncludesChildStudios()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var parentStudio = new Studio { Name = "Parent" };
        var childStudio = new Studio { Name = "Child", Parent = parentStudio };
        var otherStudio = new Studio { Name = "Other" };

        context.Studios.AddRange(parentStudio, childStudio, otherStudio);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await SeedPerformerAsync(context, "child-performer", childStudio);
        await SeedPerformerAsync(context, "other-performer", otherStudio);

        var repository = new PerformerRepository(context);
        var filter = new PerformerFilter
        {
            StudiosCriterion = new MultiIdCriterion
            {
                Value = [parentStudio.Id],
                Modifier = CriterionModifier.Includes,
                Depth = -1,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken);

        Assert.Equal(1, totalCount);
        Assert.Equal(["child-performer"], items.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task StudiosCriterion_IncludesAll_WithHierarchy_RequiresMatchPerSelectedStudioGroup()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var parentA = new Studio { Name = "Parent A" };
        var childA = new Studio { Name = "Child A", Parent = parentA };
        var parentB = new Studio { Name = "Parent B" };
        var childB = new Studio { Name = "Child B", Parent = parentB };

        context.Studios.AddRange(parentA, childA, parentB, childB);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await SeedPerformerAsync(context, "both-groups", childA, childB);
        await SeedPerformerAsync(context, "only-first-group", childA);
        await SeedPerformerAsync(context, "only-second-group", childB);

        var repository = new PerformerRepository(context);
        var filter = new PerformerFilter
        {
            StudiosCriterion = new MultiIdCriterion
            {
                Value = [parentA.Id, parentB.Id],
                Modifier = CriterionModifier.IncludesAll,
                Depth = -1,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken);

        Assert.Equal(1, totalCount);
        Assert.Equal(["both-groups"], items.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task NameCriterion_FiltersByPerformerName()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        context.Performers.AddRange(
            new Performer { Name = "Alice Example" },
            new Performer { Name = "Beth Example" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new PerformerRepository(context);
        var filter = new PerformerFilter
        {
            NameCriterion = new StringCriterion
            {
                Value = "alice",
                Modifier = CriterionModifier.Includes,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken);

        Assert.Equal(1, totalCount);
        Assert.Equal(["Alice Example"], items.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task VideoCountCriterion_IsNullAndNotNull_UsePresenceSemantics()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var alphaStudio = new Studio { Name = "Alpha" };
        context.Studios.Add(alphaStudio);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Performers.Add(new Performer { Name = "No Videos" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await SeedPerformerAsync(context, "Has Video", alphaStudio);

        var repository = new PerformerRepository(context);

        var (nullItems, nullCount) = await repository.FindAsync(new PerformerFilter { VideoCountCriterion = new IntCriterion { Modifier = CriterionModifier.IsNull } }, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken);
        var (notNullItems, notNullCount) = await repository.FindAsync(new PerformerFilter { VideoCountCriterion = new IntCriterion { Modifier = CriterionModifier.NotNull } }, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken);

        Assert.Equal(1, nullCount);
        Assert.Equal(["No Videos"], nullItems.Select(performer => performer.Name ?? string.Empty).ToArray());
        Assert.Equal(1, notNullCount);
        Assert.Equal(["Has Video"], notNullItems.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task StudioCountCriterion_CountsDistinctStudios()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var alphaStudio = new Studio { Name = "Alpha" };
        var betaStudio = new Studio { Name = "Beta" };
        context.Studios.AddRange(alphaStudio, betaStudio);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await SeedPerformerAsync(context, "one-studio", alphaStudio);
        await SeedPerformerAsync(context, "two-studios", alphaStudio, betaStudio, alphaStudio);
        await SeedPerformerWithVideoAsync(context, "no-studio", null);

        var repository = new PerformerRepository(context);
        var filter = new PerformerFilter
        {
            StudioCountCriterion = new IntCriterion
            {
                Value = 2,
                Modifier = CriterionModifier.Equals,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken);

        Assert.Equal(1, totalCount);
        Assert.Equal(["two-studios"], items.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task RemoteIdCriterion_WithProviderUsesProviderSpecificNullChecks()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        context.Performers.AddRange(
            new Performer
            {
                Name = "Has PMVStash",
                RemoteIds = [new PerformerRemoteId { Endpoint = "PMVStash", RemoteId = "pmv-1" }],
            },
            new Performer
            {
                Name = "Has StashDB",
                RemoteIds = [new PerformerRemoteId { Endpoint = "StashDB", RemoteId = "stash-1" }],
            },
            new Performer { Name = "No Remote" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new PerformerRepository(context);

        var (withProviderItems, withProviderCount) = await repository.FindAsync(new PerformerFilter
            {
                RemoteIdCriterion = new StringCriterion
                {
                    Value = "PMVStash",
                    Modifier = CriterionModifier.NotNull,
                },
            }, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken);

        var (withoutProviderItems, withoutProviderCount) = await repository.FindAsync(new PerformerFilter
            {
                RemoteIdCriterion = new StringCriterion
                {
                    Value = "PMVStash",
                    Modifier = CriterionModifier.IsNull,
                },
            }, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken);

        Assert.Equal(1, withProviderCount);
        Assert.Equal(["Has PMVStash"], withProviderItems.Select(performer => performer.Name ?? string.Empty).ToArray());
        Assert.Equal(2, withoutProviderCount);
        Assert.Equal(["Has StashDB", "No Remote"], withoutProviderItems.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task RemoteIdValueCriterion_FiltersByRemoteIdValue()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        context.Performers.AddRange(
            new Performer
            {
                Name = "Has PMV Value",
                RemoteIds = [new PerformerRemoteId { Endpoint = "PMVStash", RemoteId = "pmv-123" }],
            },
            new Performer
            {
                Name = "Has Different Value",
                RemoteIds = [new PerformerRemoteId { Endpoint = "PMVStash", RemoteId = "other-456" }],
            },
            new Performer { Name = "No Remote" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new PerformerRepository(context);
        var filter = new PerformerFilter
        {
            RemoteIdValueCriterion = new StringCriterion
            {
                Value = "pmv-123",
                Modifier = CriterionModifier.Equals,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken);

        Assert.Equal(1, totalCount);
        Assert.Equal(["Has PMV Value"], items.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task CareerLengthCriterion_FiltersByComputedCareerYears()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        context.Performers.AddRange(
            new Performer
            {
                Name = "Long Career",
                CareerStart = new DateOnly(2010, 1, 1),
                CareerEnd = new DateOnly(2024, 1, 1),
            },
            new Performer
            {
                Name = "Short Career",
                CareerStart = new DateOnly(2021, 1, 1),
                CareerEnd = new DateOnly(2024, 1, 1),
            },
            new Performer { Name = "Unknown Career" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new PerformerRepository(context);
        var filter = new PerformerFilter
        {
            CareerLengthCriterion = new IntCriterion
            {
                Value = 10,
                Modifier = CriterionModifier.GreaterThan,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken);

        Assert.Equal(1, totalCount);
        Assert.Equal(["Long Career"], items.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task AgeCriterion_UsesAgeAtDeathForDeceasedPerformers()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        context.Performers.AddRange(
            new Performer
            {
                Name = "Deceased Match",
                Birthdate = new DateOnly(1980, 6, 15),
                DeathDate = new DateOnly(2005, 6, 14),
            },
            new Performer
            {
                Name = "Living Match",
                Birthdate = today.AddYears(-24),
            },
            new Performer
            {
                Name = "Future Death Match",
                Birthdate = today.AddYears(-24),
                DeathDate = today.AddYears(5),
            });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new PerformerRepository(context);
        var filter = new PerformerFilter
        {
            AgeCriterion = new IntCriterion
            {
                Value = 24,
                Modifier = CriterionModifier.Equals,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken);

        Assert.Equal(3, totalCount);
        Assert.Equal(["Deceased Match", "Future Death Match", "Living Match"], items.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task AgeCriterion_MatchesPossibleAgesForPartialDates()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        context.Performers.AddRange(
            new Performer
            {
                Name = "Partial Birth",
                Birthdate = new DateOnly(2000, 1, 1),
                BirthdatePrecision = DatePrecision.Year,
                DeathDate = new DateOnly(2026, 6, 15),
                DeathDatePrecision = DatePrecision.Day,
            },
            new Performer
            {
                Name = "Partial Death",
                Birthdate = new DateOnly(1994, 1, 1),
                BirthdatePrecision = DatePrecision.Year,
                DeathDate = new DateOnly(2017, 1, 1),
                DeathDatePrecision = DatePrecision.Year,
            });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new PerformerRepository(context);
        var age25 = await repository.FindAsync(
            new PerformerFilter { AgeCriterion = new IntCriterion { Value = 25, Modifier = CriterionModifier.Equals } },
            new FindFilter { Page = 1, PerPage = 20, Sort = "name" },
            TestContext.Current.CancellationToken);
        var age26 = await repository.FindAsync(
            new PerformerFilter { AgeCriterion = new IntCriterion { Value = 26, Modifier = CriterionModifier.Equals } },
            new FindFilter { Page = 1, PerPage = 20, Sort = "name" },
            TestContext.Current.CancellationToken);
        var age22 = await repository.FindAsync(
            new PerformerFilter { AgeCriterion = new IntCriterion { Value = 22, Modifier = CriterionModifier.Equals } },
            new FindFilter { Page = 1, PerPage = 20, Sort = "name" },
            TestContext.Current.CancellationToken);
        var age23 = await repository.FindAsync(
            new PerformerFilter { AgeCriterion = new IntCriterion { Value = 23, Modifier = CriterionModifier.Equals } },
            new FindFilter { Page = 1, PerPage = 20, Sort = "name" },
            TestContext.Current.CancellationToken);
        var notAge25 = await repository.FindAsync(
            new PerformerFilter { AgeCriterion = new IntCriterion { Value = 25, Modifier = CriterionModifier.NotEquals } },
            new FindFilter { Page = 1, PerPage = 20, Sort = "name" },
            TestContext.Current.CancellationToken);
        var notBetween25 = await repository.FindAsync(
            new PerformerFilter { AgeCriterion = new IntCriterion { Value = 25, Value2 = 25, Modifier = CriterionModifier.NotBetween } },
            new FindFilter { Page = 1, PerPage = 20, Sort = "name" },
            TestContext.Current.CancellationToken);

        Assert.Equal(["Partial Birth"], age25.Items.Select(performer => performer.Name).ToArray());
        Assert.Equal(["Partial Birth"], age26.Items.Select(performer => performer.Name).ToArray());
        Assert.Equal(["Partial Death"], age22.Items.Select(performer => performer.Name).ToArray());
        Assert.Equal(["Partial Death"], age23.Items.Select(performer => performer.Name).ToArray());
        Assert.Equal(["Partial Death"], notAge25.Items.Select(performer => performer.Name).ToArray());
        Assert.Equal(["Partial Death"], notBetween25.Items.Select(performer => performer.Name).ToArray());
    }

    private static async Task SeedPerformerAsync(CoveContext context, string name, params Studio[] studios)
    {
        var performer = new Performer { Name = name };
        context.Performers.Add(performer);
        await context.SaveChangesAsync();

        foreach (var studio in studios)
        {
            var video = new Video
            {
                Title = $"{name}-{studio.Name}",
                StudioId = studio.Id,
            };

            context.Videos.Add(video);
            await context.SaveChangesAsync();

            context.Set<VideoPerformer>().Add(new VideoPerformer
            {
                VideoId = video.Id,
                PerformerId = performer.Id,
            });
            await context.SaveChangesAsync();
        }
    }

    private static async Task SeedPerformerWithVideoAsync(CoveContext context, string name, Studio? studio)
    {
        var performer = new Performer { Name = name };
        context.Performers.Add(performer);
        await context.SaveChangesAsync();

        var video = new Video
        {
            Title = $"{name}-video",
            StudioId = studio?.Id,
        };

        context.Videos.Add(video);
        await context.SaveChangesAsync();

        context.Set<VideoPerformer>().Add(new VideoPerformer
        {
            VideoId = video.Id,
            PerformerId = performer.Id,
        });
        await context.SaveChangesAsync();
    }

    private static async Task<TestContextScope> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;

        var context = new PerformerFilterTestContext(options);
        await context.Database.EnsureCreatedAsync();
        return new TestContextScope(context, connection);
    }

    private sealed class PerformerFilterTestContext(DbContextOptions<CoveContext> options) : CoveContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

        }
    }

    private sealed class TestContextScope(CoveContext context, SqliteConnection connection) : IAsyncDisposable
    {
        public CoveContext Context { get; } = context;

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
