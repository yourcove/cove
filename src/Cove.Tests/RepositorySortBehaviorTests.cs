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
    public async Task TagRepository_SupportsRequestedCountAndUpdatedAtSorts()
    {
        await using var context = CreateContext();

        var countsLeader = new Tag
        {
            Name = "Counts Leader",
            UpdatedAt = new DateTime(2024, 1, 12, 0, 0, 0, DateTimeKind.Utc),
        };

        var lighter = new Tag
        {
            Name = "Lighter",
            UpdatedAt = new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc),
        };

        var quiet = new Tag
        {
            Name = "Quiet",
            UpdatedAt = new DateTime(2024, 1, 8, 0, 0, 0, DateTimeKind.Utc),
        };

        countsLeader.ImageTags.Add(new ImageTag { Tag = countsLeader, Image = new Image { Title = "image-1" } });
        countsLeader.ImageTags.Add(new ImageTag { Tag = countsLeader, Image = new Image { Title = "image-2" } });
        countsLeader.GalleryTags.Add(new GalleryTag { Tag = countsLeader, Gallery = new Gallery { Title = "gallery-1" } });
        countsLeader.GalleryTags.Add(new GalleryTag { Tag = countsLeader, Gallery = new Gallery { Title = "gallery-2" } });
        countsLeader.GroupTags.Add(new GroupTag { Tag = countsLeader, Group = new Group { Name = "group-1" } });
        countsLeader.GroupTags.Add(new GroupTag { Tag = countsLeader, Group = new Group { Name = "group-2" } });
        countsLeader.PerformerTags.Add(new PerformerTag { Tag = countsLeader, Performer = new Performer { Name = "performer-1" } });
        countsLeader.PerformerTags.Add(new PerformerTag { Tag = countsLeader, Performer = new Performer { Name = "performer-2" } });
        countsLeader.StudioTags.Add(new StudioTag { Tag = countsLeader, Studio = new Studio { Name = "studio-1" } });
        countsLeader.StudioTags.Add(new StudioTag { Tag = countsLeader, Studio = new Studio { Name = "studio-2" } });

        lighter.ImageTags.Add(new ImageTag { Tag = lighter, Image = new Image { Title = "image-3" } });
        lighter.GalleryTags.Add(new GalleryTag { Tag = lighter, Gallery = new Gallery { Title = "gallery-3" } });
        lighter.GroupTags.Add(new GroupTag { Tag = lighter, Group = new Group { Name = "group-3" } });
        lighter.PerformerTags.Add(new PerformerTag { Tag = lighter, Performer = new Performer { Name = "performer-3" } });
        lighter.StudioTags.Add(new StudioTag { Tag = lighter, Studio = new Studio { Name = "studio-3" } });

        context.Tags.AddRange(countsLeader, lighter, quiet);
        await context.SaveChangesAsync();

        var repository = new TagRepository(context);

        var (galleryItems, _) = await repository.FindAsync(null, new FindFilter { Page = 1, PerPage = 20, Sort = "gallery_count", Direction = Cove.Core.Enums.SortDirection.Desc });
        var (groupItems, _) = await repository.FindAsync(null, new FindFilter { Page = 1, PerPage = 20, Sort = "group_count", Direction = Cove.Core.Enums.SortDirection.Desc });
        var (imageItems, _) = await repository.FindAsync(null, new FindFilter { Page = 1, PerPage = 20, Sort = "image_count", Direction = Cove.Core.Enums.SortDirection.Desc });
        var (performerItems, _) = await repository.FindAsync(null, new FindFilter { Page = 1, PerPage = 20, Sort = "performer_count", Direction = Cove.Core.Enums.SortDirection.Desc });
        var (studioItems, _) = await repository.FindAsync(null, new FindFilter { Page = 1, PerPage = 20, Sort = "studio_count", Direction = Cove.Core.Enums.SortDirection.Desc });
        var (updatedItems, _) = await repository.FindAsync(null, new FindFilter { Page = 1, PerPage = 20, Sort = "updated_at", Direction = Cove.Core.Enums.SortDirection.Desc });

        Assert.Equal(["Counts Leader", "Lighter", "Quiet"], galleryItems.Select(tag => tag.Name).ToArray());
        Assert.Equal(["Counts Leader", "Lighter", "Quiet"], groupItems.Select(tag => tag.Name).ToArray());
        Assert.Equal(["Counts Leader", "Lighter", "Quiet"], imageItems.Select(tag => tag.Name).ToArray());
        Assert.Equal(["Counts Leader", "Lighter", "Quiet"], performerItems.Select(tag => tag.Name).ToArray());
        Assert.Equal(["Counts Leader", "Lighter", "Quiet"], studioItems.Select(tag => tag.Name).ToArray());
        Assert.Equal(["Counts Leader", "Lighter", "Quiet"], updatedItems.Select(tag => tag.Name).ToArray());
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
    public async Task StudioRepository_SupportsRequestedCountRatingAndUpdatedAtSorts()
    {
        await using var context = CreateContext();

        var alphaTag = new Tag { Name = "Alpha Tag" };
        var betaTag = new Tag { Name = "Beta Tag" };

        var highestRated = new Studio
        {
            Name = "Highest Rated",
            Rating = 95,
            UpdatedAt = new DateTime(2024, 1, 12, 0, 0, 0, DateTimeKind.Utc),
        };

        var countsLeader = new Studio
        {
            Name = "Counts Leader",
            Rating = 40,
            UpdatedAt = new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc),
        };

        var unrated = new Studio
        {
            Name = "Unrated",
            Rating = null,
            UpdatedAt = new DateTime(2024, 1, 8, 0, 0, 0, DateTimeKind.Utc),
        };

        countsLeader.Galleries.Add(new Gallery { Title = "g1" });
        countsLeader.Galleries.Add(new Gallery { Title = "g2" });
        countsLeader.Images.Add(new Image { Title = "i1" });
        countsLeader.Images.Add(new Image { Title = "i2" });
        countsLeader.Children.Add(new Studio { Name = "Child Studio" });
        countsLeader.StudioTags.Add(new StudioTag { Studio = countsLeader, Tag = alphaTag });
        countsLeader.StudioTags.Add(new StudioTag { Studio = countsLeader, Tag = betaTag });

        highestRated.Galleries.Add(new Gallery { Title = "g3" });
        highestRated.Images.Add(new Image { Title = "i3" });

        unrated.Images.Add(new Image { Title = "i4" });

        context.Studios.AddRange(highestRated, countsLeader, unrated);
        await context.SaveChangesAsync();

        var repository = new StudioRepository(context);

        var (galleryItems, _) = await repository.FindAsync(null, new FindFilter { Page = 1, PerPage = 20, Sort = "gallery_count", Direction = Cove.Core.Enums.SortDirection.Desc });
        var (imageItems, _) = await repository.FindAsync(null, new FindFilter { Page = 1, PerPage = 20, Sort = "image_count", Direction = Cove.Core.Enums.SortDirection.Desc });
        var (ratingItems, _) = await repository.FindAsync(null, new FindFilter { Page = 1, PerPage = 20, Sort = "rating", Direction = Cove.Core.Enums.SortDirection.Desc });
        var (childItems, _) = await repository.FindAsync(null, new FindFilter { Page = 1, PerPage = 20, Sort = "child_count", Direction = Cove.Core.Enums.SortDirection.Desc });
        var (tagItems, _) = await repository.FindAsync(null, new FindFilter { Page = 1, PerPage = 20, Sort = "tag_count", Direction = Cove.Core.Enums.SortDirection.Desc });
        var (updatedItems, _) = await repository.FindAsync(null, new FindFilter { Page = 1, PerPage = 20, Sort = "updated_at", Direction = Cove.Core.Enums.SortDirection.Desc });

        Assert.Equal(["Counts Leader", "Highest Rated", "Unrated"], galleryItems.Select(studio => studio.Name).ToArray());
        Assert.Equal(["Counts Leader", "Highest Rated", "Unrated"], imageItems.Select(studio => studio.Name).ToArray());
        Assert.Equal(["Highest Rated", "Counts Leader", "Unrated"], ratingItems.Select(studio => studio.Name).ToArray());
        Assert.Equal(["Counts Leader", "Highest Rated", "Unrated"], childItems.Select(studio => studio.Name).ToArray());
        Assert.Equal(["Counts Leader", "Highest Rated", "Unrated"], tagItems.Select(studio => studio.Name).ToArray());
        Assert.Equal(["Highest Rated", "Counts Leader", "Unrated"], updatedItems.Select(studio => studio.Name).ToArray());
    }

    [Fact]
    public async Task GalleryRepository_KeepsUnratedItemsLastAndMatchesFolderBackedPaths()
    {
        await using var context = CreateContext();

        var ratedFolderGallery = new Gallery
        {
            Title = "folder-gallery",
            Rating = 80,
            Folder = new Folder { Path = @"C:\library\matched-folder", ModTime = new DateTime(2024, 1, 12, 0, 0, 0, DateTimeKind.Utc) },
        };

        var unratedFileGallery = new Gallery
        {
            Title = "file-gallery",
            Rating = null,
        };
        unratedFileGallery.Files.Add(new GalleryFile
        {
            Basename = "set.zip",
            ParentFolder = new Folder { Path = @"C:\library\other-folder", ModTime = new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc) },
            ModTime = new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc),
            Size = 1024,
        });

        context.Galleries.AddRange(ratedFolderGallery, unratedFileGallery);
        await context.SaveChangesAsync();

        var repository = new GalleryRepository(context);

        var (ratingItems, ratingCount) = await repository.FindAsync(
            filter: null,
            new FindFilter { Page = 1, PerPage = 20, Sort = "rating", Direction = Cove.Core.Enums.SortDirection.Desc });

        var (pathItems, pathCount) = await repository.FindAsync(
            new GalleryFilter
            {
                PathCriterion = new StringCriterion
                {
                    Value = @"C:\library\matched-folder",
                    Modifier = CriterionModifier.Equals,
                },
            },
            new FindFilter { Page = 1, PerPage = 20, Sort = "title", Direction = Cove.Core.Enums.SortDirection.Asc });

        Assert.Equal(2, ratingCount);
        Assert.Equal(["folder-gallery", "file-gallery"], ratingItems.Select(gallery => gallery.Title ?? string.Empty).ToArray());
        Assert.Equal(1, pathCount);
        Assert.Equal(["folder-gallery"], pathItems.Select(gallery => gallery.Title ?? string.Empty).ToArray());
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
    public async Task RatingSorts_PlaceUnratedAndZeroRatedItemsFirstWhenSortingAscending()
    {
        await using var context = CreateContext();

        context.Performers.AddRange(
            new Performer { Name = "Performer Low", Rating = 20 },
            new Performer { Name = "Performer High", Rating = 80 },
            new Performer { Name = "Performer Zero", Rating = 0 },
            new Performer { Name = "Performer Unrated", Rating = null });

        context.Images.AddRange(
            new Image { Title = "Image Low", Rating = 20 },
            new Image { Title = "Image High", Rating = 80 },
            new Image { Title = "Image Zero", Rating = 0 },
            new Image { Title = "Image Unrated", Rating = null });

        context.Groups.AddRange(
            new Group { Name = "Group Low", Rating = 20 },
            new Group { Name = "Group High", Rating = 80 },
            new Group { Name = "Group Zero", Rating = 0 },
            new Group { Name = "Group Unrated", Rating = null });

        context.Studios.AddRange(
            new Studio { Name = "Studio Low", Rating = 20 },
            new Studio { Name = "Studio High", Rating = 80 },
            new Studio { Name = "Studio Zero", Rating = 0 },
            new Studio { Name = "Studio Unrated", Rating = null });

        context.Galleries.AddRange(
            new Gallery { Title = "Gallery Low", Rating = 20 },
            new Gallery { Title = "Gallery High", Rating = 80 },
            new Gallery { Title = "Gallery Zero", Rating = 0 },
            new Gallery { Title = "Gallery Unrated", Rating = null });

        context.Scenes.AddRange(
            new Scene { Title = "Scene Low", Rating = 20 },
            new Scene { Title = "Scene High", Rating = 80 },
            new Scene { Title = "Scene Zero", Rating = 0 },
            new Scene { Title = "Scene Unrated", Rating = null });

        await context.SaveChangesAsync();

        var performerRepository = new PerformerRepository(context);
        var imageRepository = new ImageRepository(context);
        var groupRepository = new GroupRepository(context);
        var studioRepository = new StudioRepository(context);
        var galleryRepository = new GalleryRepository(context);
        var sceneRepository = new SceneRepository(context);

        var (performerItems, _) = await performerRepository.FindAsync(
            filter: null,
            new FindFilter { Page = 1, PerPage = 20, Sort = "rating", Direction = Cove.Core.Enums.SortDirection.Asc });

        var (imageItems, _) = await imageRepository.FindAsync(
            filter: null,
            new FindFilter { Page = 1, PerPage = 20, Sort = "rating", Direction = Cove.Core.Enums.SortDirection.Asc });

        var (groupItems, _) = await groupRepository.FindAsync(
            filter: null,
            new FindFilter { Page = 1, PerPage = 20, Sort = "rating", Direction = Cove.Core.Enums.SortDirection.Asc });

        var (studioItems, _) = await studioRepository.FindAsync(
            filter: null,
            new FindFilter { Page = 1, PerPage = 20, Sort = "rating", Direction = Cove.Core.Enums.SortDirection.Asc });

        var (galleryItems, _) = await galleryRepository.FindAsync(
            filter: null,
            new FindFilter { Page = 1, PerPage = 20, Sort = "rating", Direction = Cove.Core.Enums.SortDirection.Asc });

        var (sceneItems, _) = await sceneRepository.FindAsync(
            filter: null,
            new FindFilter { Page = 1, PerPage = 20, Sort = "rating", Direction = Cove.Core.Enums.SortDirection.Asc });

        Assert.Equal(["Performer Unrated", "Performer Zero", "Performer Low", "Performer High"], performerItems.Select(performer => performer.Name).ToArray());
        Assert.Equal(["Image Unrated", "Image Zero", "Image Low", "Image High"], imageItems.Select(image => image.Title ?? string.Empty).ToArray());
        Assert.Equal(["Group Unrated", "Group Zero", "Group Low", "Group High"], groupItems.Select(group => group.Name).ToArray());
        Assert.Equal(["Studio Unrated", "Studio Zero", "Studio Low", "Studio High"], studioItems.Select(studio => studio.Name).ToArray());
        Assert.Equal(["Gallery Unrated", "Gallery Zero", "Gallery Low", "Gallery High"], galleryItems.Select(gallery => gallery.Title ?? string.Empty).ToArray());
        Assert.Equal(["Scene Unrated", "Scene Zero", "Scene Low", "Scene High"], sceneItems.Select(scene => scene.Title ?? string.Empty).ToArray());
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
