using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public class RepositorySortBehaviorTests
{
    [Fact]
    public async Task TagRepository_SceneCountSort_UsesSceneAssociations()
    {
        await using var context = CreateContext();
        var busy = new Tag { Name = "Busy", CreatedAt = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc) };
        var quiet = new Tag { Name = "Quiet", CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) };

        context.Scenes.AddRange(
            CreateSceneWithTag("first", busy),
            CreateSceneWithTag("second", busy),
            CreateSceneWithTag("third", quiet));
        await context.SaveChangesAsync();

        var repository = new TagRepository(context);
        var (items, totalCount) = await repository.FindAsync(
            filter: null,
            new FindFilter { Page = 1, PerPage = 20, Sort = "scene_count", Direction = Cove.Core.Enums.SortDirection.Desc });

        Assert.Equal(2, totalCount);
        Assert.Equal(["Busy", "Quiet"], items.Select(tag => tag.Name).ToArray());
    }

    [Fact]
    public async Task StudioRepository_SceneCountSort_UsesSceneAssociations()
    {
        await using var context = CreateContext();
        var busiest = new Studio { Name = "Busiest", CreatedAt = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc) };
        var quieter = new Studio { Name = "Quieter", CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) };

        busiest.Scenes.Add(new Scene { Title = "one" });
        busiest.Scenes.Add(new Scene { Title = "two" });
        quieter.Scenes.Add(new Scene { Title = "three" });

        context.Studios.AddRange(busiest, quieter);
        await context.SaveChangesAsync();

        var repository = new StudioRepository(context);
        var (items, totalCount) = await repository.FindAsync(
            filter: null,
            new FindFilter { Page = 1, PerPage = 20, Sort = "scene_count", Direction = Cove.Core.Enums.SortDirection.Desc });

        Assert.Equal(2, totalCount);
        Assert.Equal(["Busiest", "Quieter"], items.Select(studio => studio.Name).ToArray());
    }

    [Fact]
    public async Task GroupRepository_SupportsDateRatingAndCreatedAtSorts()
    {
        await using var context = CreateContext();
        var newest = new Group
        {
            Name = "Newest",
            Date = new DateOnly(2024, 1, 12),
            Rating = 90,
        };
        var older = new Group
        {
            Name = "Older",
            Date = new DateOnly(2024, 1, 10),
            Rating = 50,
        };
        var undated = new Group
        {
            Name = "Undated",
            Rating = null,
        };

        context.Groups.AddRange(newest, older, undated);
        await context.SaveChangesAsync();

        newest.CreatedAt = new DateTime(2024, 1, 12, 0, 0, 0, DateTimeKind.Utc);
        older.CreatedAt = new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        undated.CreatedAt = new DateTime(2024, 1, 8, 0, 0, 0, DateTimeKind.Utc);
        await context.SaveChangesAsync();

        var repository = new GroupRepository(context);

        var (dateItems, dateCount) = await repository.FindAsync(
            filter: null,
            new FindFilter { Page = 1, PerPage = 20, Sort = "date", Direction = Cove.Core.Enums.SortDirection.Desc });

        var (ratingItems, ratingCount) = await repository.FindAsync(
            filter: null,
            new FindFilter { Page = 1, PerPage = 20, Sort = "rating", Direction = Cove.Core.Enums.SortDirection.Desc });

        var (createdItems, createdCount) = await repository.FindAsync(
            filter: null,
            new FindFilter { Page = 1, PerPage = 20, Sort = "created_at", Direction = Cove.Core.Enums.SortDirection.Desc });

        Assert.Equal(3, dateCount);
        Assert.Equal(["Newest", "Older", "Undated"], dateItems.Select(group => group.Name).ToArray());
        Assert.Equal(3, ratingCount);
        Assert.Equal(["Newest", "Older", "Undated"], ratingItems.Select(group => group.Name).ToArray());
        Assert.Equal(3, createdCount);
        Assert.Equal(["Newest", "Older", "Undated"], createdItems.Select(group => group.Name).ToArray());
    }

    [Fact]
    public async Task SceneRepository_SupportsParitySceneSorts()
    {
        await using var context = CreateContext();

        var alphaStudio = new Studio { Name = "Alpha Studio" };
        var betaStudio = new Studio { Name = "Beta Studio" };

        var youngerPerformer = new Performer { Name = "Younger", Birthdate = new DateOnly(2004, 1, 1) };
        var olderPerformer = new Performer { Name = "Older", Birthdate = new DateOnly(1984, 1, 1) };

        var alphaScene = CreateSceneWithFile(
            "alpha-scene",
            folderPath: @"C:\library\a",
            basename: "a.mp4",
            fileModTime: new DateTime(2024, 1, 5, 0, 0, 0, DateTimeKind.Utc),
            code: "A-002",
            studio: alphaStudio,
            performer: youngerPerformer,
            fingerprints: [new FileFingerprint { Type = "phash", Value = "00aa" }]);
        alphaScene.Date = new DateOnly(2024, 1, 20);
        alphaScene.OHistory.Add(new SceneOHistory { OccurredAt = new DateTime(2024, 1, 5, 12, 0, 0, DateTimeKind.Utc) });

        var betaScene = CreateSceneWithFile(
            "beta-scene",
            folderPath: @"C:\library\z",
            basename: "z.mp4",
            fileModTime: new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc),
            code: "B-001",
            studio: betaStudio,
            performer: olderPerformer,
            fingerprints: [new FileFingerprint { Type = "phash", Value = "00ff" }]);
        betaScene.Date = new DateOnly(2024, 1, 20);
        betaScene.OHistory.Add(new SceneOHistory { OccurredAt = new DateTime(2024, 1, 10, 12, 0, 0, DateTimeKind.Utc) });

        context.Scenes.AddRange(alphaScene, betaScene);
        await context.SaveChangesAsync();

        var repository = new SceneRepository(context);

        var (fileModItems, _) = await repository.FindAsync(null, new FindFilter { Page = 1, PerPage = 20, Sort = "file_mod_time", Direction = Cove.Core.Enums.SortDirection.Desc });
        var (favoriteItems, _) = await repository.FindAsync(null, new FindFilter { Page = 1, PerPage = 20, Sort = "last_o_at", Direction = Cove.Core.Enums.SortDirection.Desc });
        var (pathItems, _) = await repository.FindAsync(null, new FindFilter { Page = 1, PerPage = 20, Sort = "path", Direction = Cove.Core.Enums.SortDirection.Asc });
        var (phashItems, _) = await repository.FindAsync(null, new FindFilter { Page = 1, PerPage = 20, Sort = "phash", Direction = Cove.Core.Enums.SortDirection.Asc });
        var (ageItems, _) = await repository.FindAsync(null, new FindFilter { Page = 1, PerPage = 20, Sort = "performer_age", Direction = Cove.Core.Enums.SortDirection.Asc });
        var (studioItems, _) = await repository.FindAsync(null, new FindFilter { Page = 1, PerPage = 20, Sort = "studio", Direction = Cove.Core.Enums.SortDirection.Asc });
        var (codeItems, _) = await repository.FindAsync(null, new FindFilter { Page = 1, PerPage = 20, Sort = "code", Direction = Cove.Core.Enums.SortDirection.Asc });

        Assert.Equal(["beta-scene", "alpha-scene"], fileModItems.Select(scene => scene.Title ?? string.Empty).ToArray());
        Assert.Equal(["beta-scene", "alpha-scene"], favoriteItems.Select(scene => scene.Title ?? string.Empty).ToArray());
        Assert.Equal(["alpha-scene", "beta-scene"], pathItems.Select(scene => scene.Title ?? string.Empty).ToArray());
        Assert.Equal(["alpha-scene", "beta-scene"], phashItems.Select(scene => scene.Title ?? string.Empty).ToArray());
        Assert.Equal(["alpha-scene", "beta-scene"], ageItems.Select(scene => scene.Title ?? string.Empty).ToArray());
        Assert.Equal(["alpha-scene", "beta-scene"], studioItems.Select(scene => scene.Title ?? string.Empty).ToArray());
        Assert.Equal(["alpha-scene", "beta-scene"], codeItems.Select(scene => scene.Title ?? string.Empty).ToArray());
    }

    private static Scene CreateSceneWithTag(string title, Tag tag)
    {
        var scene = new Scene { Title = title };
        scene.SceneTags.Add(new SceneTag { Scene = scene, Tag = tag });
        return scene;
    }

    private static Scene CreateSceneWithFile(
        string title,
        string folderPath,
        string basename,
        DateTime fileModTime,
        string code,
        Studio studio,
        Performer performer,
        IEnumerable<FileFingerprint> fingerprints)
    {
        var scene = new Scene
        {
            Title = title,
            Code = code,
            Studio = studio,
        };

        scene.ScenePerformers.Add(new ScenePerformer { Scene = scene, Performer = performer });

        var file = new VideoFile
        {
            Scene = scene,
            Basename = basename,
            ParentFolder = new Folder { Path = folderPath, ModTime = fileModTime },
            Format = "mp4",
            Width = 1920,
            Height = 1080,
            Duration = 120,
            VideoCodec = "h264",
            AudioCodec = "aac",
            FrameRate = 30,
            BitRate = 1_000_000,
            Size = 1024,
            ModTime = fileModTime,
        };

        foreach (var fingerprint in fingerprints)
        {
            file.Fingerprints.Add(fingerprint);
        }

        scene.Files.Add(file);

        return scene;
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"repository-sort-behavior-{Guid.NewGuid():N}")
            .Options;

        return new TestCoveContext(options);
    }

    private sealed class TestCoveContext(DbContextOptions<CoveContext> options) : CoveContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Scene>().Ignore(scene => scene.CustomFields);
            modelBuilder.Entity<Image>().Ignore(image => image.CustomFields);
            modelBuilder.Entity<Tag>().Ignore(tag => tag.CustomFields);
            modelBuilder.Entity<Studio>().Ignore(studio => studio.CustomFields);
            modelBuilder.Entity<Performer>().Ignore(performer => performer.CustomFields);
            modelBuilder.Entity<Gallery>().Ignore(gallery => gallery.CustomFields);
            modelBuilder.Entity<Group>().Ignore(group => group.CustomFields);
        }
    }
}
