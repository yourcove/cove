using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cove.Tests;

[CollectionDefinition("ManagedPostgresPerformerFilter", DisableParallelization = true)]
public sealed class ManagedPostgresPerformerFilterCollection;

[Collection("ManagedPostgresPerformerFilter")]
public class PerformerFilterBehaviorTests
{
    [Fact]
    public async Task StudiosCriterion_IncludesAll_RequiresScenesFromAllSelectedStudios()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var alphaStudio = new Studio { Name = "Alpha" };
        var betaStudio = new Studio { Name = "Beta" };

        context.Performers.AddRange(
            CreatePerformer("both-studios", alphaStudio, betaStudio),
            CreatePerformer("alpha-only", alphaStudio),
            CreatePerformer("beta-only", betaStudio));
        await context.SaveChangesAsync();

        var repository = new PerformerRepository(context);
        var filter = new PerformerFilter
        {
            StudiosCriterion = new MultiIdCriterion
            {
                Value = [alphaStudio.Id, betaStudio.Id],
                Modifier = CriterionModifier.IncludesAll,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

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

        context.Performers.AddRange(
            CreatePerformer("alpha-only", alphaStudio),
            CreatePerformer("alpha-and-beta", alphaStudio, betaStudio),
            CreatePerformer("beta-only", betaStudio));
        await context.SaveChangesAsync();

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

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

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

        context.Performers.AddRange(
            CreatePerformer("child-performer", childStudio),
            CreatePerformer("other-performer", otherStudio));
        await context.SaveChangesAsync();

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

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

        Assert.Equal(1, totalCount);
        Assert.Equal(["child-performer"], items.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task NameCriterion_FiltersByPerformerName()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        context.Performers.AddRange(
            new Performer { Name = "Alice Example" },
            new Performer { Name = "Beth Example" });
        await context.SaveChangesAsync();

        var repository = new PerformerRepository(context);
        var filter = new PerformerFilter
        {
            NameCriterion = new StringCriterion
            {
                Value = "alice",
                Modifier = CriterionModifier.Includes,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

        Assert.Equal(1, totalCount);
        Assert.Equal(["Alice Example"], items.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task SceneCountCriterion_IsNullAndNotNull_UsePresenceSemantics()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var alphaStudio = new Studio { Name = "Alpha" };
        context.Performers.AddRange(
            new Performer { Name = "No Scenes" },
            CreatePerformer("Has Scene", alphaStudio));
        await context.SaveChangesAsync();

        var repository = new PerformerRepository(context);

        var (nullItems, nullCount) = await repository.FindAsync(
            new PerformerFilter { SceneCountCriterion = new IntCriterion { Modifier = CriterionModifier.IsNull } },
            new FindFilter { Page = 1, PerPage = 20, Sort = "name" });
        var (notNullItems, notNullCount) = await repository.FindAsync(
            new PerformerFilter { SceneCountCriterion = new IntCriterion { Modifier = CriterionModifier.NotNull } },
            new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

        Assert.Equal(1, nullCount);
        Assert.Equal(["No Scenes"], nullItems.Select(performer => performer.Name ?? string.Empty).ToArray());
        Assert.Equal(1, notNullCount);
        Assert.Equal(["Has Scene"], notNullItems.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task StudioCountCriterion_CountsDistinctStudios()
    {
        await using var scope = await CreateContextAsync();
        var context = scope.Context;

        var alphaStudio = new Studio { Name = "Alpha" };
        var betaStudio = new Studio { Name = "Beta" };
        context.Performers.AddRange(
            CreatePerformer("one-studio", alphaStudio),
            CreatePerformer("two-studios", alphaStudio, betaStudio, alphaStudio),
            CreatePerformerWithScene("no-studio", null));
        await context.SaveChangesAsync();

        var repository = new PerformerRepository(context);
        var filter = new PerformerFilter
        {
            StudioCountCriterion = new IntCriterion
            {
                Value = 2,
                Modifier = CriterionModifier.Equals,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

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
        await context.SaveChangesAsync();

        var repository = new PerformerRepository(context);

        var (withProviderItems, withProviderCount) = await repository.FindAsync(
            new PerformerFilter
            {
                RemoteIdCriterion = new StringCriterion
                {
                    Value = "PMVStash",
                    Modifier = CriterionModifier.NotNull,
                },
            },
            new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

        var (withoutProviderItems, withoutProviderCount) = await repository.FindAsync(
            new PerformerFilter
            {
                RemoteIdCriterion = new StringCriterion
                {
                    Value = "PMVStash",
                    Modifier = CriterionModifier.IsNull,
                },
            },
            new FindFilter { Page = 1, PerPage = 20, Sort = "name" });

        Assert.Equal(1, withProviderCount);
        Assert.Equal(["Has PMVStash"], withProviderItems.Select(performer => performer.Name ?? string.Empty).ToArray());
        Assert.Equal(2, withoutProviderCount);
        Assert.Equal(["Has StashDB", "No Remote"], withoutProviderItems.Select(performer => performer.Name ?? string.Empty).ToArray());
    }

    private static Performer CreatePerformer(string name, params Studio[] studios)
    {
        var performer = new Performer { Name = name };

        foreach (var studio in studios)
        {
            var scene = new Scene
            {
                Title = $"{name}-{studio.Name}",
                Studio = studio,
            };

            var link = new ScenePerformer
            {
                Scene = scene,
                Performer = performer,
            };

            scene.ScenePerformers.Add(link);
            performer.ScenePerformers.Add(link);
        }

        return performer;
    }

    private static Performer CreatePerformerWithScene(string name, Studio? studio)
    {
        var performer = new Performer { Name = name };
        var scene = new Scene
        {
            Title = $"{name}-scene",
            Studio = studio,
        };

        var link = new ScenePerformer
        {
            Scene = scene,
            Performer = performer,
        };

        scene.ScenePerformers.Add(link);
        performer.ScenePerformers.Add(link);
        return performer;
    }

    private static async Task<TestContextScope> CreateContextAsync()
    {
        var managedRoot = ResolveManagedPostgresRoot();
        if (managedRoot == null)
            throw new InvalidOperationException("Managed PostgreSQL binaries are not available for performer filter tests.");

        var postgresConfig = new PostgresConfig
        {
            Managed = true,
            DataPath = managedRoot,
            Port = 5548,
            Database = $"performer_filter_{Guid.NewGuid():N}",
        };
        var connectionString = $"Host=127.0.0.1;Port={postgresConfig.Port};Database={postgresConfig.Database};Username=postgres;Trust Server Certificate=true;Timeout=15;Command Timeout=30";

        var manager = new PostgresManagerService(Options.Create(postgresConfig), NullLogger<PostgresManagerService>.Instance);
        await manager.StartAsync(CancellationToken.None);

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(connectionString)
            .Options;

        var context = new CoveContext(options);
        await context.Database.EnsureCreatedAsync();
        return new TestContextScope(context, manager);
    }

    private static string? ResolveManagedPostgresRoot()
    {
        var repoArtifactRoot = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "backup-verify-data");
        if (File.Exists(Path.Combine(repoArtifactRoot, "pgsql", "bin", Exe("pg_ctl"))))
            return repoArtifactRoot;

        var localAppDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cove");
        if (File.Exists(Path.Combine(localAppDataRoot, "pgsql", "bin", Exe("pg_ctl"))))
            return localAppDataRoot;

        return null;
    }

    private static string Exe(string toolName)
    {
        return OperatingSystem.IsWindows() ? toolName + ".exe" : toolName;
    }

    private sealed class TestContextScope(CoveContext context, PostgresManagerService manager) : IAsyncDisposable
    {
        public CoveContext Context { get; } = context;

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await manager.StopAsync(CancellationToken.None);
        }
    }
}