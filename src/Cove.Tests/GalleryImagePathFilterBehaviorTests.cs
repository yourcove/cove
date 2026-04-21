using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public class GalleryImagePathFilterBehaviorTests
{
    [Fact]
    public async Task GalleryPathCriterion_Equals_UsesFullNormalizedPath()
    {
        await using var context = CreateContext();
        context.Galleries.AddRange(
            CreateGalleryWithFile("match", folderPath: @"C:\library\matching", basename: "cover.jpg"),
            CreateGalleryWithFile("same-name-other-folder", folderPath: @"C:\library\other", basename: "cover.jpg"));
        await context.SaveChangesAsync();

        var repository = new GalleryRepository(context);
        var filter = new GalleryFilter
        {
            PathCriterion = new StringCriterion
            {
                Value = @"C:\library\matching\cover.jpg",
                Modifier = CriterionModifier.Equals,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(1, totalCount);
        Assert.Equal(["match"], items.Select(gallery => gallery.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task ImagePathCriterion_RegexAndNullModifiers_UseFullPathSemantics()
    {
        await using var context = CreateContext();
        context.Images.AddRange(
            CreateImageWithFile("regex-match", folderPath: @"C:\library\matching", basename: "still.jpg"),
            CreateImageWithFile("regex-miss", folderPath: @"C:\library\other", basename: "still.jpg"),
            new Image { Title = "missing-file" });
        await context.SaveChangesAsync();

        var repository = new ImageRepository(context);

        var (regexItems, regexCount) = await repository.FindAsync(
            new ImageFilter
            {
                PathCriterion = new StringCriterion
                {
                    Value = "matching/.+[.]jpg$",
                    Modifier = CriterionModifier.MatchesRegex,
                },
            },
            new FindFilter { Page = 1, PerPage = 50 });

        var (nullItems, nullCount) = await repository.FindAsync(
            new ImageFilter
            {
                PathCriterion = new StringCriterion
                {
                    Value = string.Empty,
                    Modifier = CriterionModifier.IsNull,
                },
            },
            new FindFilter { Page = 1, PerPage = 50 });

        var (notNullItems, notNullCount) = await repository.FindAsync(
            new ImageFilter
            {
                PathCriterion = new StringCriterion
                {
                    Value = string.Empty,
                    Modifier = CriterionModifier.NotNull,
                },
            },
            new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(1, regexCount);
        Assert.Equal(["regex-match"], regexItems.Select(image => image.Title ?? string.Empty).ToArray());
        Assert.Equal(1, nullCount);
        Assert.Equal(["missing-file"], nullItems.Select(image => image.Title ?? string.Empty).ToArray());
        Assert.Equal(2, notNullCount);
        Assert.Equal(["regex-match", "regex-miss"], notNullItems.Select(image => image.Title ?? string.Empty).OrderBy(title => title).ToArray());
    }

    private static Gallery CreateGalleryWithFile(string title, string folderPath, string basename)
    {
        var gallery = new Gallery
        {
            Title = title,
        };

        gallery.Files.Add(new GalleryFile
        {
            Basename = basename,
            ParentFolder = new Folder { Path = folderPath, ModTime = DateTime.UtcNow },
            Size = 1024,
            ModTime = DateTime.UtcNow,
        });

        return gallery;
    }

    private static Image CreateImageWithFile(string title, string folderPath, string basename)
    {
        var image = new Image
        {
            Title = title,
        };

        image.Files.Add(new ImageFile
        {
            Basename = basename,
            ParentFolder = new Folder { Path = folderPath, ModTime = DateTime.UtcNow },
            Format = "jpg",
            Width = 800,
            Height = 600,
            Size = 1024,
            ModTime = DateTime.UtcNow,
        });

        return image;
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"gallery-image-path-filter-{Guid.NewGuid():N}")
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