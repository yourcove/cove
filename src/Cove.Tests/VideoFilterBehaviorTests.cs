using Cove.Api.Controllers;
using Cove.Api.Helpers;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Common;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Events;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Tests.Postgres;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Caching.Memory;

namespace Cove.Tests;

public class VideoFilterBehaviorTests
{
    private readonly Dictionary<string, Folder> _folders = new(StringComparer.Ordinal);
    private int _defaultFileNames;

    [Fact]
    [RequiresPostgres]
    public async Task PerformerSummaryCountsLoader_BatchesCountsAndHonorsHostReadPermissions()
    {
        var principals = new CurrentPrincipalAccessor();
        principals.Set(new CovePrincipal
        {
            UserId = 1,
            Username = "summary-reader",
            Kind = PrincipalKind.User,
            Permissions = new HashSet<string> { Permissions.PerformersRead, Permissions.VideosRead, Permissions.ImagesRead, Permissions.GalleriesRead, Permissions.TextsRead },
            Roles = new HashSet<string>(),
        });
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = new CoveContext(database.CreateOptions(), principals);
        context.AddRange(
            new Performer { Id = 7, Name = "Counted performer" },
            new Performer { Id = 8, Name = "Other performer" },
            new Video { Id = 1, Title = "First video" },
            new Video { Id = 2, Title = "Second video" },
            new Image { Id = 1, Title = "Image" },
            new Gallery { Id = 1, Title = "Gallery" },
            new Audio { Id = 1, Title = "Audio" },
            new TextDocument { Id = 1, Title = "Text" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.Set<VideoPerformer>().AddRange(new VideoPerformer { PerformerId = 7, VideoId = 1 }, new VideoPerformer { PerformerId = 8, VideoId = 2 });
        context.Set<ImagePerformer>().Add(new ImagePerformer { PerformerId = 7, ImageId = 1 });
        context.Set<GalleryPerformer>().Add(new GalleryPerformer { PerformerId = 7, GalleryId = 1 });
        context.Set<AudioPerformer>().Add(new AudioPerformer { PerformerId = 7, AudioId = 1 });
        context.Set<TextPerformer>().Add(new TextPerformer { PerformerId = 7, TextDocumentId = 1 });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var counts = await PerformerSummaryCountsLoader.LoadAsync(context, [7, 8], TestContext.Current.CancellationToken, principals);

        Assert.Equal(new PerformerSummaryCounts(1, 1, 1, 0, 1), counts[7]);
        Assert.Equal(new PerformerSummaryCounts(1, 0, 0, 0, 0), counts[8]);
    }

    [Fact]
    [RequiresPostgres]
    public async Task VideosController_Find_PreservesCachedPerformerCountsWithoutDetailQueries()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var performer = new Performer { Name = "List Performer", VideoCount = 4, ImageCount = 3, GalleryCount = 2 };
        var video = CreateVideoWithFile("performer-count-list");
        video.VideoPerformers.Add(new VideoPerformer { Performer = performer });
        context.Videos.Add(video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        performer.VideoCount = 4;
        performer.ImageCount = 3;
        performer.GalleryCount = 2;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = CreateVideosControllerWithRepository(context);
        var response = await controller.Find(q: null, page: 1, perPage: 25, ct: TestContext.Current.CancellationToken);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var page = Assert.IsType<PaginatedResponse<VideoDto>>(ok.Value);
        var summary = Assert.Single(Assert.Single(page.Items).Performers);

        Assert.Equal(4, summary.VideoCount);
        Assert.Equal(3, summary.ImageCount);
        Assert.Equal(2, summary.GalleryCount);
        Assert.Equal(0, summary.AudioCount);
        Assert.Equal(0, summary.TextCount);
    }

    [Fact]
    [RequiresPostgres]
    public async Task VideosController_ListAndDetail_OrderFilesByIdForStableFallbackTitles()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var video = new Video { Title = null };
        video.Files.Add(new VideoFile { Id = 20, Basename = "later.mp4", Path = "/library/later.mp4", ParentFolder = FolderAt("/library") });
        video.Files.Add(new VideoFile { Id = 10, Basename = "primary.mp4", Path = "/library/primary.mp4", ParentFolder = FolderAt("/library") });
        context.Videos.Add(video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = CreateVideosControllerWithRepository(context);
        var listResponse = await controller.Find(q: null, page: 1, perPage: 25, ct: TestContext.Current.CancellationToken);
        var listResult = Assert.IsType<OkObjectResult>(listResponse.Result);
        var page = Assert.IsType<PaginatedResponse<VideoDto>>(listResult.Value);
        var listVideo = Assert.Single(page.Items);

        var detailResponse = await controller.GetById(video.Id, TestContext.Current.CancellationToken);
        var detailResult = Assert.IsType<OkObjectResult>(detailResponse.Result);
        var detailVideo = Assert.IsType<VideoDto>(detailResult.Value);

        Assert.Equal([10, 20], listVideo.Files.Select(file => file.Id));
        Assert.Equal([10, 20], detailVideo.Files.Select(file => file.Id));
        Assert.Equal("primary.mp4", listVideo.Files[0].Basename);
        Assert.Equal(listVideo.Files[0].Basename, detailVideo.Files[0].Basename);
    }

    [Fact]
    [RequiresPostgres]
    public async Task PathCriterion_UnderPath_UsesFolderBoundaries()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Videos.AddRange(
            CreateVideoWithFile("direct", folderPath: @"C:\library\matching", basename: "direct.mp4"),
            CreateVideoWithFile("nested", folderPath: @"C:\library\matching\nested", basename: "nested.mp4"),
            CreateVideoWithFile("prefix-only", folderPath: @"C:\library\matching-other", basename: "other.mp4"));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            PathCriterion = new StringCriterion
            {
                Value = @"C:\library\matching\",
                Modifier = CriterionModifier.UnderPath,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

        Assert.Equal(2, totalCount);
        Assert.Equal(["direct", "nested"], items.Select(video => video.Title ?? string.Empty).Order().ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task PathCriterion_NotUnderPath_ExcludesEntitiesWithAnyFileInFolder()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var mixed = CreateVideoWithFile("mixed", folderPath: @"C:\library\outside", basename: "outside.mp4");
        mixed.Files.Add(new VideoFile
        {
            Basename = "inside.mp4",
            ParentFolder = FolderAt(@"C:\library\matching"),
            Size = 1024,
            ModTime = DateTime.UtcNow,
        });
        context.Videos.AddRange(
            mixed,
            CreateVideoWithFile("outside", folderPath: @"C:\library\outside", basename: "other-outside.mp4"));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            PathCriterion = new StringCriterion
            {
                Value = @"C:\library\matching",
                Modifier = CriterionModifier.NotUnderPath,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

        Assert.Equal(1, totalCount);
        Assert.Equal(["outside"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task PathCriterion_UnderPath_UsesPlatformPathCaseSemantics()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Videos.AddRange(
            CreateVideoWithFile("exact-case", folderPath: "/library/Media", basename: "clip.mp4"),
            CreateVideoWithFile("different-case", folderPath: "/library/media", basename: "clip.mp4"));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);
        var (items, totalCount) = await repository.FindAsync(new VideoFilter { PathCriterion = new StringCriterion { Value = "/library/Media", Modifier = CriterionModifier.UnderPath } }, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

        Assert.Equal(FilesystemPaths.PathComparison == StringComparison.OrdinalIgnoreCase ? 2 : 1, totalCount);
        Assert.Contains(items, video => video.Title == "exact-case");
        if (FilesystemPaths.PathComparison == StringComparison.Ordinal) Assert.DoesNotContain(items, video => video.Title == "different-case");
    }

    [Fact]
    [RequiresPostgres]
    public async Task PathCriterion_Equals_UsesFullNormalizedPath()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Videos.AddRange(
            CreateVideoWithFile("match", folderPath: @"C:\library\matching", basename: "clip.mp4"),
            CreateVideoWithFile("same-name-other-folder", folderPath: @"C:\library\other", basename: "clip.mp4"));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            PathCriterion = new StringCriterion
            {
                Value = @"C:\library\matching\clip.mp4",
                Modifier = CriterionModifier.Equals,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

        Assert.Equal(1, totalCount);
        Assert.Equal(["match"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task DateCriterion_NullModifiers_ApplyWithoutAValue()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var undated = CreateVideoWithFile("undated", basename: "undated.mp4");
        undated.Date = null;
        context.Videos.AddRange(CreateVideoWithFile("dated", videoDate: new DateOnly(2024, 5, 1)), undated);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);

        // The filter UI sends an empty value for IS_NULL/NOT_NULL, so these must not
        // depend on Value parsing as a date.
        var (isNullItems, isNullCount) = await repository.FindAsync(new VideoFilter
            {
                DateCriterion = new DateCriterion { Value = string.Empty, Modifier = CriterionModifier.IsNull },
            }, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

        var (notNullItems, notNullCount) = await repository.FindAsync(new VideoFilter
            {
                DateCriterion = new DateCriterion { Value = string.Empty, Modifier = CriterionModifier.NotNull },
            }, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

        Assert.Equal(1, isNullCount);
        Assert.Equal(["undated"], isNullItems.Select(video => video.Title ?? string.Empty).ToArray());
        Assert.Equal(1, notNullCount);
        Assert.Equal(["dated"], notNullItems.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task ProductionDateCriterion_FiltersOnTheProductionDateNotTheReleaseDate()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var older = CreateVideoWithFile("shot 2019", basename: "older.mp4", videoDate: new DateOnly(2025, 1, 1));
        older.ProductionDate = new DateOnly(2019, 6, 1);
        var newer = CreateVideoWithFile("shot 2023", basename: "newer.mp4", videoDate: new DateOnly(2018, 1, 1));
        newer.ProductionDate = new DateOnly(2023, 2, 1);
        var unknown = CreateVideoWithFile("no production date", basename: "unknown.mp4", videoDate: new DateOnly(2024, 1, 1));
        context.Videos.AddRange(older, newer, unknown);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new VideoRepository(context);

        async Task<string[]> TitlesAsync(DateCriterion criterion)
        {
            var (items, _) = await repository.FindAsync(new VideoFilter { ProductionDateCriterion = criterion },
                new FindFilter { Page = 1, PerPage = 50, Sort = "title" }, TestContext.Current.CancellationToken);
            return items.Select(video => video.Title ?? string.Empty).ToArray();
        }

        Assert.Equal(["shot 2023"], await TitlesAsync(new DateCriterion { Value = "2020-01-01", Modifier = CriterionModifier.GreaterThan }));
        Assert.Equal(["shot 2019"], await TitlesAsync(new DateCriterion { Value = "2019-01-01", Value2 = "2019-12-31", Modifier = CriterionModifier.Between }));
        Assert.Equal(["no production date"], await TitlesAsync(new DateCriterion { Value = string.Empty, Modifier = CriterionModifier.IsNull }));
        Assert.Equal(["shot 2019", "shot 2023"], await TitlesAsync(new DateCriterion { Value = string.Empty, Modifier = CriterionModifier.NotNull }));
        // Relative values resolve as they do for the release date: everything shot before a year ago.
        Assert.Equal(["shot 2019", "shot 2023"], await TitlesAsync(new DateCriterion { Value = "-1y", Modifier = CriterionModifier.LessThan }));
    }

    [Fact]
    [RequiresPostgres]
    public async Task PerformerDateCriterion_NullModifiers_ApplyWithoutAValue()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Performers.AddRange(
            new Performer { Name = "living", Birthdate = new DateOnly(1990, 3, 4) },
            new Performer { Name = "unknown-birthdate", Birthdate = null });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new PerformerRepository(context);

        var (isNullItems, isNullCount) = await repository.FindAsync(new PerformerFilter
            {
                BirthdateCriterion = new DateCriterion { Value = string.Empty, Modifier = CriterionModifier.IsNull },
            }, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

        Assert.Equal(1, isNullCount);
        Assert.Equal(["unknown-birthdate"], isNullItems.Select(performer => performer.Name).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task AudioCodecCriterion_HandlesRegexAndNullModifiers()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Videos.AddRange(
            CreateVideoWithFile("aac-video", audioCodec: "AAC"),
            CreateVideoWithFile("mp3-video", audioCodec: "MP3"),
            CreateVideoWithFile("missing-audio", audioCodec: ""));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);

        var (notRegexItems, notRegexCount) = await repository.FindAsync(new VideoFilter
            {
                AudioCodecCriterion = new StringCriterion
                {
                    Value = "^aa",
                    Modifier = CriterionModifier.NotMatchesRegex,
                },
            }, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

        var (nullItems, nullCount) = await repository.FindAsync(new VideoFilter
            {
                AudioCodecCriterion = new StringCriterion
                {
                    Value = string.Empty,
                    Modifier = CriterionModifier.IsNull,
                },
            }, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

        var (notNullItems, notNullCount) = await repository.FindAsync(new VideoFilter
            {
                AudioCodecCriterion = new StringCriterion
                {
                    Value = string.Empty,
                    Modifier = CriterionModifier.NotNull,
                },
            }, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

        Assert.Equal(2, notRegexCount);
        Assert.Equal(["missing-audio", "mp3-video"], notRegexItems.Select(video => video.Title ?? string.Empty).OrderBy(title => title).ToArray());
        Assert.Equal(1, nullCount);
        Assert.Equal(["missing-audio"], nullItems.Select(video => video.Title ?? string.Empty).ToArray());
        Assert.Equal(2, notNullCount);
        Assert.Equal(["aac-video", "mp3-video"], notNullItems.Select(video => video.Title ?? string.Empty).OrderBy(title => title).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task BitrateInterval_GreaterThan_UsesMaxBitRateSummary()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Videos.AddRange(
            CreateVideoWithFile("high-bitrate", bitRate: 2_500_000),
            CreateVideoWithFile("low-bitrate", bitRate: 500_000),
            new Video { Title = "no-file" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            BitrateInterval = new IntCriterion
            {
                Value = 1000,
                Modifier = CriterionModifier.GreaterThan,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

        Assert.Equal(1, totalCount);
        Assert.Equal(["high-bitrate"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task BitrateSort_UsesMaxBitRateSummary()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Videos.AddRange(
            CreateVideoWithFile("high-bitrate", bitRate: 2_500_000),
            CreateVideoWithFile("low-bitrate", bitRate: 500_000),
            CreateVideoWithFile("mid-bitrate", bitRate: 1_500_000));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);
        var (items, totalCount) = await repository.FindAsync(null, new FindFilter
        {
            Page = 1,
            PerPage = 50,
            Sort = "bitrate",
            Direction = SortDirection.Asc,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(3, totalCount);
        Assert.Equal(["low-bitrate", "mid-bitrate", "high-bitrate"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task DirectorCriterion_NotMatchesRegex_UsesRegexSemantics()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Videos.AddRange(
            CreateVideoWithFile("jane-video", director: "Jane Smith"),
            CreateVideoWithFile("john-video", director: "John Doe"));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            DirectorCriterion = new StringCriterion
            {
                Value = "^Jane",
                Modifier = CriterionModifier.NotMatchesRegex,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

        Assert.Equal(1, totalCount);
        Assert.Equal(["john-video"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task PerformerAgeCriterion_Equals_UsesAgeAtVideoDate()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var performer = CreatePerformer("Boundary Performer", new DateOnly(2006, 1, 15));

        context.Videos.AddRange(
            CreateVideoWithFile("before-birthday", videoDate: new DateOnly(2024, 1, 10), performer: performer),
            CreateVideoWithFile("after-birthday", videoDate: new DateOnly(2024, 1, 20), performer: performer));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            PerformerAgeCriterion = new IntCriterion
            {
                Value = 18,
                Modifier = CriterionModifier.Equals,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

        Assert.Equal(1, totalCount);
        Assert.Equal(["after-birthday"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    // A performer's age on a video counts from its production date when it has one, else from its release
    // date: the production date wins when both are set, and a video with only a production date still has an age.
    [Theory]
    [InlineData("performer age", new[] { "production-only", "released-young", "shot-young" })]
    [InlineData("age then", new[] { "production-only", "released-young", "shot-young" })]
    [InlineData("age then unknown", new[] { "undated" })]
    [RequiresPostgres]
    public async Task PerformerAgeCriteria_UseProductionDateBeforeReleaseDate(string criterion, string[] expected)
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var performer = CreatePerformer("Dated Performer", new DateOnly(2000, 6, 1));
        Video Add(string title, DateOnly? releaseDate, DateOnly? productionDate)
        {
            var video = CreateVideoWithFile(title);
            video.Date = releaseDate;
            video.ProductionDate = productionDate;
            video.VideoPerformers.Add(new VideoPerformer { Performer = performer });
            context.Videos.Add(video);
            return video;
        }
        Add("shot-young", new DateOnly(2024, 7, 1), new DateOnly(2017, 7, 1));
        Add("released-young", new DateOnly(2017, 7, 1), null);
        Add("shot-older", new DateOnly(2017, 7, 1), new DateOnly(2024, 7, 1));
        Add("production-only", null, new DateOnly(2017, 7, 1));
        Add("undated", null, null);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        RelatedFilterCriterion<PerformerFilter> Related(IntCriterion age) => new() { AgeAtHostDateCriterion = age };
        var filter = criterion switch
        {
            "performer age" => new VideoFilter { PerformerAgeCriterion = new IntCriterion { Modifier = CriterionModifier.Equals, Value = 17 } },
            "age then" => new VideoFilter { PerformerFilterCriterion = Related(new IntCriterion { Modifier = CriterionModifier.Between, Value = 17, Value2 = 17 }) },
            _ => new VideoFilter { PerformerFilterCriterion = Related(new IntCriterion { Modifier = CriterionModifier.IsNull }) },
        };

        var (items, _) = await new VideoRepository(context).FindAsync(
            filter,
            new FindFilter { Page = 1, PerPage = 50, Sort = "title", Direction = SortDirection.Asc },
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task FilterExpression_RepeatsCriteriaAndComposesNestedBooleanGroups()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var fooOnly = CreateVideoWithFile("foo-only");
        fooOnly.Urls.Add(new VideoUrl { Url = "https://example.test/foo" });
        var fooAndBar = CreateVideoWithFile("foo-and-bar");
        fooAndBar.Urls.Add(new VideoUrl { Url = "https://example.test/foo/bar" });
        var baz = CreateVideoWithFile("baz");
        baz.Urls.Add(new VideoUrl { Url = "https://example.test/baz" });
        context.Videos.AddRange(fooOnly, fooAndBar, baz);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var expression = new FilterExpression<VideoFilter>
        {
            Operator = FilterExpressionOperator.Or,
            Children =
            [
                new()
                {
                    Group = new FilterExpression<VideoFilter>
                    {
                        Children =
                        [
                            new() { Filter = new VideoFilter { UrlCriterion = new StringCriterion { Modifier = CriterionModifier.Includes, Value = "foo" } } },
                            new() { Filter = new VideoFilter { UrlCriterion = new StringCriterion { Modifier = CriterionModifier.Excludes, Value = "bar" } } },
                        ],
                    },
                },
                new() { Filter = new VideoFilter { UrlCriterion = new StringCriterion { Modifier = CriterionModifier.Includes, Value = "baz" } } },
            ],
        };

        var (items, count) = await new VideoRepository(context).FindAsync(
            null,
            new FindFilter { Page = 1, PerPage = 50, Sort = "title" },
            TestContext.Current.CancellationToken,
            expression);

        Assert.Equal(2, count);
        Assert.Equal(["baz", "foo-only"], items.Select(video => video.Title!).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task FilterExpression_JustOneMatchesExactlyOneBranch()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Videos.AddRange(
            CreateVideoWithFile("alpha-only"),
            CreateVideoWithFile("beta-only"),
            CreateVideoWithFile("gamma-only"),
            CreateVideoWithFile("alpha-beta"),
            CreateVideoWithFile("alpha-beta-gamma"),
            CreateVideoWithFile("neither"));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var expression = new FilterExpression<VideoFilter>
        {
            Operator = FilterExpressionOperator.JustOne,
            Children =
            [
                new() { Filter = new VideoFilter { TitleCriterion = new StringCriterion { Modifier = CriterionModifier.Includes, Value = "alpha" } } },
                new() { Filter = new VideoFilter { TitleCriterion = new StringCriterion { Modifier = CriterionModifier.Includes, Value = "beta" } } },
                new() { Filter = new VideoFilter { TitleCriterion = new StringCriterion { Modifier = CriterionModifier.Includes, Value = "gamma" } } },
            ],
        };

        var (items, count) = await new VideoRepository(context).FindAsync(
            null,
            new FindFilter { Page = 1, PerPage = 50, Sort = "title" },
            TestContext.Current.CancellationToken,
            expression);

        Assert.Equal(3, count);
        Assert.Equal(["alpha-only", "beta-only", "gamma-only"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task FilterExpression_OrAndJustOneUnderACondition_KeepToWhatTheConditionMatches()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Videos.AddRange(
            CreateVideoWithFile("foo-alpha"),
            CreateVideoWithFile("foo-alpha-beta"),
            CreateVideoWithFile("foo-gamma"),
            CreateVideoWithFile("bar-alpha"),
            CreateVideoWithFile("bar-beta"));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        FilterExpressionNode<VideoFilter> Title(string value) => new() { Filter = new VideoFilter { TitleCriterion = new StringCriterion { Modifier = CriterionModifier.Includes, Value = value } } };
        FilterExpression<VideoFilter> UnderFoo(FilterExpressionOperator op) => new()
        {
            Children = [Title("foo"), new() { Group = new FilterExpression<VideoFilter> { Operator = op, Children = [Title("alpha"), Title("beta")] } }],
        };

        // The branches start from the whole library, so they must not bring back what the condition before them left out.
        var (anyItems, anyCount) = await new VideoRepository(context).FindAsync(
            null, new FindFilter { Page = 1, PerPage = 50, Sort = "title" }, TestContext.Current.CancellationToken, UnderFoo(FilterExpressionOperator.Or));
        Assert.Equal(2, anyCount);
        Assert.Equal(["foo-alpha", "foo-alpha-beta"], anyItems.Select(video => video.Title ?? string.Empty).ToArray());

        var (oneItems, oneCount) = await new VideoRepository(context).FindAsync(
            null, new FindFilter { Page = 1, PerPage = 50, Sort = "title" }, TestContext.Current.CancellationToken, UnderFoo(FilterExpressionOperator.JustOne));
        Assert.Equal(1, oneCount);
        Assert.Equal("foo-alpha", Assert.Single(oneItems).Title);
    }

    [Fact]
    [RequiresPostgres]
    public async Task FilterExpression_NotNegatesOneConditionOrNestedGroup()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Videos.AddRange(
            CreateVideoWithFile("foo-only"),
            CreateVideoWithFile("foo-bar"),
            CreateVideoWithFile("foo-baz"),
            CreateVideoWithFile("other"));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var expression = new FilterExpression<VideoFilter>
        {
            Children =
            [
                new() { Filter = new VideoFilter { TitleCriterion = new StringCriterion { Modifier = CriterionModifier.Includes, Value = "foo" } } },
                new()
                {
                    Group = new FilterExpression<VideoFilter>
                    {
                        Operator = FilterExpressionOperator.Not,
                        Children =
                        [
                            new()
                            {
                                Group = new FilterExpression<VideoFilter>
                                {
                                    Operator = FilterExpressionOperator.Or,
                                    Children =
                                    [
                                        new() { Filter = new VideoFilter { TitleCriterion = new StringCriterion { Modifier = CriterionModifier.Includes, Value = "bar" } } },
                                        new() { Filter = new VideoFilter { TitleCriterion = new StringCriterion { Modifier = CriterionModifier.Includes, Value = "baz" } } },
                                    ],
                                },
                            },
                        ],
                    },
                },
            ],
        };

        var (items, count) = await new VideoRepository(context).FindAsync(
            null,
            new FindFilter { Page = 1, PerPage = 50, Sort = "title" },
            TestContext.Current.CancellationToken,
            expression);

        Assert.Equal(1, count);
        Assert.Equal("foo-only", Assert.Single(items).Title);

        var doubleNegation = new FilterExpression<VideoFilter>
        {
            Operator = FilterExpressionOperator.Not,
            Children =
            [
                new()
                {
                    Group = new FilterExpression<VideoFilter>
                    {
                        Operator = FilterExpressionOperator.Not,
                        Children =
                        [
                            new() { Filter = new VideoFilter { TitleCriterion = new StringCriterion { Modifier = CriterionModifier.Includes, Value = "foo" } } },
                        ],
                    },
                },
            ],
        };

        var (doubleNegatedItems, doubleNegatedCount) = await new VideoRepository(context).FindAsync(
            null,
            new FindFilter { Page = 1, PerPage = 50, Sort = "title" },
            TestContext.Current.CancellationToken,
            doubleNegation);

        Assert.Equal(3, doubleNegatedCount);
        Assert.Equal(["foo-bar", "foo-baz", "foo-only"], doubleNegatedItems.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public void FilterExpression_NotRequiresExactlyOneChild()
    {
        var empty = new FilterExpression<VideoFilter> { Operator = FilterExpressionOperator.Not };
        var multiple = new FilterExpression<VideoFilter>
        {
            Operator = FilterExpressionOperator.Not,
            Children =
            [
                new() { Filter = new VideoFilter() },
                new() { Filter = new VideoFilter() },
            ],
        };

        Assert.False(FilterExpressionQuery.TryValidate(empty, out var emptyError));
        Assert.Equal("NOT filter-expression groups must contain exactly one child.", emptyError);
        Assert.False(FilterExpressionQuery.TryValidate(multiple, out var multipleError));
        Assert.Equal("NOT filter-expression groups must contain exactly one child.", multipleError);
        var distinctOr = new FilterExpression<VideoFilter>
        {
            Operator = FilterExpressionOperator.Or,
            RelatedScope = new() { FilterKey = "performerFilterCriterion", MatchMode = RelatedScopeMatchMode.Distinct },
            Children = [new() { Filter = new VideoFilter() }],
        };
        Assert.False(FilterExpressionQuery.TryValidate(distinctOr, out var distinctOrError));
        Assert.Equal("Distinct related matches are supported only by AND filter-expression groups.", distinctOrError);
        var mixedScope = new FilterExpression<VideoFilter>
        {
            RelatedScope = new() { FilterKey = "performerFilterCriterion", MatchMode = RelatedScopeMatchMode.Distinct },
            Children =
            [
                new() { Filter = new VideoFilter { PerformerFilterCriterion = new() { ObjectFilter = new PerformerFilter { GenderCriterion = new StringCriterion { Modifier = CriterionModifier.Equals, Value = "Female" } } } } },
                new() { Filter = new VideoFilter { PerformerCountCriterion = new() { Modifier = CriterionModifier.Equals, Value = 2 } } },
            ],
        };
        Assert.False(FilterExpressionQuery.TryValidate(mixedScope, out var mixedScopeError));
        Assert.Equal("Every condition in a related filter-expression scope must use 'performerFilterCriterion'.", mixedScopeError);

        var titleScope = new FilterExpression<VideoFilter>
        {
            RelatedScope = new() { FilterKey = "titleCriterion", MatchMode = RelatedScopeMatchMode.Reuse },
            Children =
            [
                new() { Filter = new VideoFilter { TitleCriterion = new() { Modifier = CriterionModifier.Includes, Value = "one" } } },
                new() { Filter = new VideoFilter { TitleCriterion = new() { Modifier = CriterionModifier.Includes, Value = "two" } } },
            ],
        };
        Assert.False(FilterExpressionQuery.TryValidate(titleScope, out var titleScopeError));
        Assert.Equal("Every condition in a related filter-expression scope must use 'titleCriterion'.", titleScopeError);

        var everyScope = new FilterExpression<VideoFilter>
        {
            RelatedScope = new() { FilterKey = "performerFilterCriterion", MatchMode = RelatedScopeMatchMode.Reuse },
            Children =
            [
                new() { Filter = new VideoFilter { PerformerFilterCriterion = new() { Mode = RelatedFilterMode.Every, ObjectFilter = new PerformerFilter() } } },
                new() { Filter = new VideoFilter { PerformerFilterCriterion = new() { ObjectFilter = new PerformerFilter() } } },
            ],
        };
        Assert.False(FilterExpressionQuery.TryValidate(everyScope, out var everyScopeError));
        Assert.Equal("Related filter-expression scopes require positive at-least-one conditions.", everyScopeError);

        var nullChildScope = new FilterExpression<VideoFilter>
        {
            RelatedScope = new() { FilterKey = "performerFilterCriterion", MatchMode = RelatedScopeMatchMode.Reuse },
            Children = [null!, new() { Filter = new VideoFilter { PerformerFilterCriterion = new() { ObjectFilter = new PerformerFilter() } } }],
        };
        Assert.False(FilterExpressionQuery.TryValidate(nullChildScope, out var nullChildScopeError));
        Assert.Equal("Related filter-expression scopes must contain at least two direct filter conditions.", nullChildScopeError);

        var unsupportedDistinctScope = new FilterExpression<PerformerFilter>
        {
            RelatedScope = new() { FilterKey = "videoFilterCriterion", MatchMode = RelatedScopeMatchMode.Distinct },
            Children =
            [
                new() { Filter = new PerformerFilter { VideoFilterCriterion = new() { ObjectFilter = new VideoFilter() } } },
                new() { Filter = new PerformerFilter { VideoFilterCriterion = new() { ObjectFilter = new VideoFilter() } } },
            ],
        };
        Assert.False(FilterExpressionQuery.TryValidate(unsupportedDistinctScope, out var unsupportedDistinctScopeError));
        Assert.Equal("Distinct assignment is not supported for related scope 'videoFilterCriterion'.", unsupportedDistinctScopeError);

        var dualFormat = new FilterExpression<VideoFilter>
        {
            DistinctRelatedMatches = true,
            RelatedScope = new() { FilterKey = "performerFilterCriterion", MatchMode = RelatedScopeMatchMode.Reuse },
            Children =
            [
                new() { Filter = new VideoFilter { PerformerFilterCriterion = new() { ObjectFilter = new PerformerFilter() } } },
                new() { Filter = new VideoFilter { PerformerFilterCriterion = new() { ObjectFilter = new PerformerFilter() } } },
            ],
        };
        Assert.False(FilterExpressionQuery.TryValidate(dualFormat, out var dualFormatError));
        Assert.Equal("Filter-expression groups may not combine legacy distinct matching with a related scope.", dualFormatError);
    }

    [Fact]
    [RequiresPostgres]
    public async Task RelatedPerformerExpression_CorrelatesGenderAndAgeAtVideoDatePerClause()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var youngMan = CreatePerformer("Young Man", new DateOnly(2000, 6, 1));
        youngMan.Gender = GenderEnum.Male;
        var olderWoman = CreatePerformer("Older Woman", new DateOnly(1990, 6, 1));
        olderWoman.Gender = GenderEnum.Female;
        var wrongWoman = CreatePerformer("Young Woman", new DateOnly(2000, 6, 1));
        wrongWoman.Gender = GenderEnum.Female;

        var matches = CreateVideoWithFile("matches", videoDate: new DateOnly(2025, 6, 2));
        matches.VideoPerformers.Add(new VideoPerformer { Performer = youngMan });
        matches.VideoPerformers.Add(new VideoPerformer { Performer = olderWoman });
        var doesNotMatch = CreateVideoWithFile("does-not-match", videoDate: new DateOnly(2025, 6, 2));
        doesNotMatch.VideoPerformers.Add(new VideoPerformer { Performer = youngMan });
        doesNotMatch.VideoPerformers.Add(new VideoPerformer { Performer = wrongWoman });
        context.Videos.AddRange(matches, doesNotMatch);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        RelatedFilterCriterion<PerformerFilter> Related(string gender, int low, int high) => new()
        {
            ObjectFilter = new PerformerFilter { GenderCriterion = new StringCriterion { Modifier = CriterionModifier.Equals, Value = gender } },
            AgeAtHostDateCriterion = new IntCriterion { Modifier = CriterionModifier.Between, Value = low, Value2 = high },
        };
        var expression = new FilterExpression<VideoFilter>
        {
            Children =
            [
                new() { Filter = new VideoFilter { PerformerFilterCriterion = Related("Male", 20, 30) } },
                new() { Filter = new VideoFilter { PerformerFilterCriterion = Related("Female", 30, 40) } },
            ],
        };

        var (items, count) = await new VideoRepository(context).FindAsync(
            null,
            new FindFilter { Page = 1, PerPage = 50 },
            TestContext.Current.CancellationToken,
            expression);

        Assert.Equal(1, count);
        Assert.Equal("matches", Assert.Single(items).Title);
    }

    [Fact]
    [RequiresPostgres]
    public async Task DistinctAudioPerformerExpression_RequiresDifferentMatchingPerformers()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var alexOne = CreatePerformer("Alex One", null);
        var alexTwo = CreatePerformer("Alex Two", null);
        var oneMatch = CreateAudio("one-match", alexOne);
        var twoMatches = CreateAudio("two-matches", alexOne);
        twoMatches.AudioPerformers.Add(new AudioPerformer { Performer = alexTwo });
        context.Audios.AddRange(oneMatch, twoMatches);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        FilterExpression<AudioFilter> Expression(RelatedScopeMatchMode matchMode) => new()
        {
            RelatedScope = new() { FilterKey = "performerFilterCriterion", MatchMode = matchMode },
            Children =
            [
                new() { Filter = new AudioFilter { PerformerFilterCriterion = new() { ObjectFilter = new PerformerFilter { NameCriterion = new() { Modifier = CriterionModifier.Includes, Value = "Alex" } } } } },
                new() { Filter = new AudioFilter { PerformerFilterCriterion = new() { ObjectFilter = new PerformerFilter { NameCriterion = new() { Modifier = CriterionModifier.Includes, Value = "Alex" } } } } },
            ],
        };

        var reusable = await AudioFilterQuery.BuildAsync(context, null, null, expression: Expression(RelatedScopeMatchMode.Reuse), ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, await reusable.CountAsync(TestContext.Current.CancellationToken));
        var distinct = await AudioFilterQuery.BuildAsync(context, null, null, expression: Expression(RelatedScopeMatchMode.Distinct), ct: TestContext.Current.CancellationToken);
        Assert.Equal("two-matches", (await distinct.SingleAsync(TestContext.Current.CancellationToken)).Title);

        var compound = Expression(RelatedScopeMatchMode.Distinct);
        compound.Children[0].Filter!.TitleCriterion = new() { Modifier = CriterionModifier.Equals, Value = "missing" };
        var compoundMatches = await AudioFilterQuery.BuildAsync(context, null, null, expression: compound, ct: TestContext.Current.CancellationToken);
        Assert.Empty(await compoundMatches.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [RequiresPostgres]
    public async Task DistinctRelatedAudioExpression_RequiresDifferentMatchingAudios()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var oneAudio = CreatePerformer("One Audio", null);
        var twoAudios = CreatePerformer("Two Audios", null);
        context.Audios.Add(CreateAudio("Episode One", oneAudio));
        context.Audios.Add(CreateAudio("Episode One", twoAudios));
        context.Audios.Add(CreateAudio("Episode Two", twoAudios));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var expression = new FilterExpression<PerformerFilter>
        {
            RelatedScope = new() { FilterKey = "audioFilterCriterion", MatchMode = RelatedScopeMatchMode.Distinct },
            Children =
            [
                new() { Filter = new PerformerFilter { AudioFilterCriterion = new() { ObjectFilter = new AudioFilter { TitleCriterion = new() { Modifier = CriterionModifier.Includes, Value = "Episode" } } } } },
                new() { Filter = new PerformerFilter { AudioFilterCriterion = new() { ObjectFilter = new AudioFilter { TitleCriterion = new() { Modifier = CriterionModifier.Includes, Value = "Episode" } } } } },
            ],
        };

        var (items, count) = await new PerformerRepository(context).FindAsync(null, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken, expression);
        Assert.Equal(1, count);
        Assert.Equal("Two Audios", Assert.Single(items).Name);

        expression.Children[0].Filter!.NameCriterion = new() { Modifier = CriterionModifier.Equals, Value = "missing" };
        var (compoundItems, compoundCount) = await new PerformerRepository(context).FindAsync(null, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken, expression);
        Assert.Equal(0, compoundCount);
        Assert.Empty(compoundItems);
    }

    [Fact]
    [RequiresPostgres]
    public async Task DistinctRelatedPerformerExpression_RequiresEachClauseToMatchADifferentPerformer()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var manA = CreatePerformer("Man A", null);
        manA.Gender = GenderEnum.Male;
        var manB = CreatePerformer("Man B", null);
        manB.Gender = GenderEnum.Male;
        var womanA = CreatePerformer("Woman A", null);
        womanA.Gender = GenderEnum.Female;
        var womanB = CreatePerformer("Woman B", null);
        womanB.Gender = GenderEnum.Female;

        var oneManTwoWomen = CreateVideoWithFile("one-man-two-women", performer: manA);
        oneManTwoWomen.VideoPerformers.Add(new VideoPerformer { Performer = womanA });
        oneManTwoWomen.VideoPerformers.Add(new VideoPerformer { Performer = womanB });
        var twoMenOneWoman = CreateVideoWithFile("two-men-one-woman", performer: manA);
        twoMenOneWoman.VideoPerformers.Add(new VideoPerformer { Performer = manB });
        twoMenOneWoman.VideoPerformers.Add(new VideoPerformer { Performer = womanA });
        context.Videos.AddRange(oneManTwoWomen, twoMenOneWoman);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        static VideoFilter Gender(string value) => new()
        {
            PerformerFilterCriterion = new RelatedFilterCriterion<PerformerFilter>
            {
                ObjectFilter = new PerformerFilter
                {
                    GenderCriterion = new StringCriterion { Modifier = CriterionModifier.Equals, Value = value },
                },
            },
        };
        var expression = new FilterExpression<VideoFilter>
        {
            Children =
            [
                new() { Group = new FilterExpression<VideoFilter>
                {
                    RelatedScope = new() { FilterKey = "performerFilterCriterion", MatchMode = RelatedScopeMatchMode.Distinct },
                    Children =
                    [
                        new() { Filter = Gender("Male") },
                        new() { Filter = Gender("Female") },
                        new() { Filter = Gender("Female") },
                    ],
                } },
                new() { Filter = new VideoFilter { PerformerCountCriterion = new IntCriterion { Modifier = CriterionModifier.Equals, Value = 3 } } },
            ],
        };

        var (items, count) = await new VideoRepository(context).FindAsync(
            null,
            new FindFilter { Page = 1, PerPage = 50 },
            TestContext.Current.CancellationToken,
            expression);

        Assert.Equal(1, count);
        Assert.Equal("one-man-two-women", Assert.Single(items).Title);

        var legacyExpression = new FilterExpression<VideoFilter>
        {
            DistinctRelatedMatches = true,
            Children =
            [
                new() { Filter = Gender("Male") },
                new() { Filter = Gender("Female") },
                new() { Filter = Gender("Female") },
                new() { Filter = new VideoFilter { PerformerCountCriterion = new IntCriterion { Modifier = CriterionModifier.Equals, Value = 3 } } },
            ],
        };
        var (legacyItems, legacyCount) = await new VideoRepository(context).FindAsync(
            null,
            new FindFilter { Page = 1, PerPage = 50 },
            TestContext.Current.CancellationToken,
            legacyExpression);

        Assert.Equal(1, legacyCount);
        Assert.Equal("one-man-two-women", Assert.Single(legacyItems).Title);
    }

    [Fact]
    [RequiresPostgres]
    public async Task RelatedPerformerEveryModeAppliesAgeAtVideoDateToEveryPerformer()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var age25a = CreatePerformer("Age 25 A", new DateOnly(2000, 6, 1));
        var age25b = CreatePerformer("Age 25 B", new DateOnly(2000, 1, 1));
        var age35 = CreatePerformer("Age 35", new DateOnly(1990, 6, 1));
        var matches = CreateVideoWithFile("all-match", videoDate: new DateOnly(2025, 6, 2));
        matches.VideoPerformers.Add(new VideoPerformer { Performer = age25a });
        matches.VideoPerformers.Add(new VideoPerformer { Performer = age25b });
        var mixed = CreateVideoWithFile("mixed", videoDate: new DateOnly(2025, 6, 2));
        mixed.VideoPerformers.Add(new VideoPerformer { Performer = age25a });
        mixed.VideoPerformers.Add(new VideoPerformer { Performer = age35 });
        var empty = CreateVideoWithFile("empty", videoDate: new DateOnly(2025, 6, 2));
        context.Videos.AddRange(matches, mixed, empty);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (items, count) = await new VideoRepository(context).FindAsync(
            new VideoFilter
            {
                PerformerFilterCriterion = new RelatedFilterCriterion<PerformerFilter>
                {
                    Mode = RelatedFilterMode.Every,
                    AgeAtHostDateCriterion = new IntCriterion { Modifier = CriterionModifier.Between, Value = 20, Value2 = 30 },
                },
            },
            new FindFilter { Page = 1, PerPage = 50 },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
        Assert.Equal("all-match", Assert.Single(items).Title);
    }

    [Fact]
    [RequiresPostgres]
    public async Task RelatedPerformerEveryModeCanMatchAgeAtVideoDateOrFavorite()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var age19 = CreatePerformer("Age 19", new DateOnly(2006, 1, 1));
        var favoriteAge28 = CreatePerformer("Favorite age 28", new DateOnly(1997, 1, 1));
        var age28 = CreatePerformer("Age 28", new DateOnly(1997, 1, 1));
        var matches = CreateVideoWithFile("age-or-favorite", videoDate: new DateOnly(2025, 6, 2));
        matches.VideoPerformers.Add(new VideoPerformer { Performer = age19 });
        matches.VideoPerformers.Add(new VideoPerformer { Performer = favoriteAge28 });
        var fails = CreateVideoWithFile("neither", videoDate: new DateOnly(2025, 6, 2));
        fails.VideoPerformers.Add(new VideoPerformer { Performer = age19 });
        fails.VideoPerformers.Add(new VideoPerformer { Performer = age28 });
        context.Videos.AddRange(matches, fails, CreateVideoWithFile("empty", videoDate: new DateOnly(2025, 6, 2)));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await FavoritePerformerAsync(context, favoriteAge28);

        var (items, count) = await new VideoRepository(context).FindAsync(
            new VideoFilter
            {
                PerformerFilterCriterion = new RelatedFilterCriterion<PerformerFilter>
                {
                    Mode = RelatedFilterMode.Every,
                    ConditionOperator = RelatedFilterConditionOperator.Or,
                    ObjectFilter = new PerformerFilter { FavoriteCriterion = new BoolCriterion { Value = true } },
                    AgeAtHostDateCriterion = new IntCriterion { Modifier = CriterionModifier.Between, Value = 18, Value2 = 20 },
                },
            },
            new FindFilter { Page = 1, PerPage = 50 },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
        Assert.Equal("age-or-favorite", Assert.Single(items).Title);
    }

    [Fact]
    [RequiresPostgres]
    public async Task RelatedPerformerEveryModeCanMatchUnknownAgeAtVideoDateOrFavorite()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var unknownAge = CreatePerformer("Unknown age", null);
        var favorite = CreatePerformer("Favorite", new DateOnly(1990, 1, 1));
        var knownNonFavorite = CreatePerformer("Known non-favorite", new DateOnly(1990, 1, 1));
        var matches = CreateVideoWithFile("unknown-or-favorite", videoDate: new DateOnly(2025, 6, 2));
        matches.VideoPerformers.Add(new VideoPerformer { Performer = unknownAge });
        matches.VideoPerformers.Add(new VideoPerformer { Performer = favorite });
        var fails = CreateVideoWithFile("known-neither", videoDate: new DateOnly(2025, 6, 2));
        fails.VideoPerformers.Add(new VideoPerformer { Performer = knownNonFavorite });
        context.Videos.AddRange(matches, fails);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await FavoritePerformerAsync(context, favorite);

        var (items, count) = await new VideoRepository(context).FindAsync(
            new VideoFilter
            {
                PerformerFilterCriterion = new RelatedFilterCriterion<PerformerFilter>
                {
                    Mode = RelatedFilterMode.Every,
                    ConditionOperator = RelatedFilterConditionOperator.Or,
                    ObjectFilter = new PerformerFilter { FavoriteCriterion = new BoolCriterion { Value = true } },
                    AgeAtHostDateCriterion = new IntCriterion { Modifier = CriterionModifier.IsNull },
                },
            },
            new FindFilter { Page = 1, PerPage = 50 },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
        Assert.Equal("unknown-or-favorite", Assert.Single(items).Title);
    }

    [Fact]
    [RequiresPostgres]
    public async Task RelatedPerformerCriterion_MatchesExactPerformerOccurrenceTagsOnSameLink()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var tag = new Tag { Name = "Occurrence Tag" };
        var targetPerformer = CreatePerformer("Target", new DateOnly(2000, 1, 1));
        var otherPerformer = CreatePerformer("Other", new DateOnly(2000, 1, 1));
        var targetTaggedVideo = CreateVideoWithFile("target-tagged", performer: targetPerformer);
        targetTaggedVideo.VideoPerformers.Add(new VideoPerformer { Performer = otherPerformer });
        var wrongPerformerTaggedVideo = CreateVideoWithFile("wrong-performer-tagged", performer: targetPerformer);
        wrongPerformerTaggedVideo.VideoPerformers.Add(new VideoPerformer { Performer = otherPerformer });

        context.Tags.Add(tag);
        context.Videos.AddRange(targetTaggedVideo, wrongPerformerTaggedVideo);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TagApplications.AddRange(
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, targetTaggedVideo.Id, targetPerformer.Id, tag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, wrongPerformerTaggedVideo.Id, otherPerformer.Id, tag.Id));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (items, count) = await new VideoRepository(context).FindAsync(
            new VideoFilter
            {
                PerformerFilterCriterion = new RelatedFilterCriterion<PerformerFilter>
                {
                    PerformerIdsCriterion = new MultiIdCriterion { Modifier = CriterionModifier.Includes, Value = [targetPerformer.Id] },
                    PerformerOccurrenceTagsCriterion = new MultiIdCriterion { Modifier = CriterionModifier.Includes, Value = [tag.Id] },
                },
            },
            new FindFilter { Page = 1, PerPage = 50 },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
        Assert.Equal("target-tagged", Assert.Single(items).Title);
    }

    [Fact]
    [RequiresPostgres]
    public async Task RelatedPerformerCriterion_MatchesAttributesAgeAndOccurrenceTagsOnSameLink()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var tag = new Tag { Name = "Occurrence Tag" };
        var matchingPerformer = CreatePerformer("Matching", new DateOnly(2006, 1, 1));
        matchingPerformer.Gender = GenderEnum.Female;
        matchingPerformer.EyeColor = "Blue";
        var otherPerformer = CreatePerformer("Other", new DateOnly(1990, 1, 1));
        otherPerformer.Gender = GenderEnum.Male;
        otherPerformer.EyeColor = "Brown";

        var matches = CreateVideoWithFile("matches", videoDate: new DateOnly(2025, 6, 2), performer: matchingPerformer);
        matches.VideoPerformers.Add(new VideoPerformer { Performer = otherPerformer });
        var splitAcrossPerformers = CreateVideoWithFile("split-across-performers", videoDate: new DateOnly(2025, 6, 2), performer: matchingPerformer);
        splitAcrossPerformers.VideoPerformers.Add(new VideoPerformer { Performer = otherPerformer });

        context.Tags.Add(tag);
        context.Videos.AddRange(matches, splitAcrossPerformers);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TagApplications.AddRange(
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, matches.Id, matchingPerformer.Id, tag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, splitAcrossPerformers.Id, otherPerformer.Id, tag.Id));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (items, count) = await new VideoRepository(context).FindAsync(
            new VideoFilter
            {
                PerformerFilterCriterion = new RelatedFilterCriterion<PerformerFilter>
                {
                    ObjectFilter = new PerformerFilter
                    {
                        GenderCriterion = new StringCriterion { Modifier = CriterionModifier.Equals, Value = "Female" },
                        EyeColorCriterion = new StringCriterion { Modifier = CriterionModifier.Equals, Value = "Blue" },
                    },
                    AgeAtHostDateCriterion = new IntCriterion { Modifier = CriterionModifier.Between, Value = 18, Value2 = 20 },
                    PerformerOccurrenceTagsCriterion = new MultiIdCriterion { Modifier = CriterionModifier.Includes, Value = [tag.Id] },
                },
            },
            new FindFilter { Page = 1, PerPage = 50 },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
        Assert.Equal("matches", Assert.Single(items).Title);
    }

    [Fact]
    [RequiresPostgres]
    public async Task RelatedPerformerCriterion_IncludesAllOccurrenceTagsOnTheSameLink()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var firstTag = new Tag { Name = "First Occurrence Tag" };
        var secondTag = new Tag { Name = "Second Occurrence Tag" };
        var targetPerformer = CreatePerformer("Target", new DateOnly(2000, 1, 1));
        var otherPerformer = CreatePerformer("Other", new DateOnly(2000, 1, 1));
        var bothOnTarget = CreateVideoWithFile("both-on-target", performer: targetPerformer);
        bothOnTarget.VideoPerformers.Add(new VideoPerformer { Performer = otherPerformer });
        var splitAcrossPerformers = CreateVideoWithFile("split-across-performers", performer: targetPerformer);
        splitAcrossPerformers.VideoPerformers.Add(new VideoPerformer { Performer = otherPerformer });

        context.Tags.AddRange(firstTag, secondTag);
        context.Videos.AddRange(bothOnTarget, splitAcrossPerformers);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TagApplications.AddRange(
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, bothOnTarget.Id, targetPerformer.Id, firstTag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, bothOnTarget.Id, targetPerformer.Id, secondTag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, splitAcrossPerformers.Id, targetPerformer.Id, firstTag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, splitAcrossPerformers.Id, otherPerformer.Id, secondTag.Id));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (items, count) = await new VideoRepository(context).FindAsync(
            new VideoFilter
            {
                PerformerFilterCriterion = new RelatedFilterCriterion<PerformerFilter>
                {
                    PerformerIdsCriterion = new MultiIdCriterion { Modifier = CriterionModifier.Includes, Value = [targetPerformer.Id] },
                    PerformerOccurrenceTagsCriterion = new MultiIdCriterion
                    {
                        Modifier = CriterionModifier.IncludesAll,
                        Value = [firstTag.Id, secondTag.Id],
                    },
                },
            },
            new FindFilter { Page = 1, PerPage = 50 },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
        Assert.Equal("both-on-target", Assert.Single(items).Title);
    }

    [Fact]
    [RequiresPostgres]
    public async Task RelatedPerformerCriterion_ExcludesAllOccurrenceTagsOnlyWhenOneLinkHasEveryTag()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var firstTag = new Tag { Name = "First Occurrence Tag" };
        var firstChildTag = new Tag { Name = "First Occurrence Child Tag" };
        var secondTag = new Tag { Name = "Second Occurrence Tag" };
        var targetPerformer = CreatePerformer("Target", new DateOnly(2000, 1, 1));
        var otherPerformer = CreatePerformer("Other", new DateOnly(2000, 1, 1));
        var bothOnTarget = CreateVideoWithFile("both-on-target", performer: targetPerformer);
        var splitAcrossPerformers = CreateVideoWithFile("split-across-performers", performer: targetPerformer);
        splitAcrossPerformers.VideoPerformers.Add(new VideoPerformer { Performer = otherPerformer });
        var firstOnly = CreateVideoWithFile("first-only", performer: targetPerformer);
        var untagged = CreateVideoWithFile("untagged", performer: targetPerformer);
        var otherPerformerOnly = CreateVideoWithFile("other-performer-only", performer: otherPerformer);

        context.Tags.AddRange(firstTag, firstChildTag, secondTag);
        context.Videos.AddRange(bothOnTarget, splitAcrossPerformers, firstOnly, untagged, otherPerformerOnly);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.Set<TagParent>().Add(new TagParent { ParentId = firstTag.Id, ChildId = firstChildTag.Id });
        context.TagApplications.AddRange(
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, bothOnTarget.Id, targetPerformer.Id, firstChildTag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, bothOnTarget.Id, targetPerformer.Id, secondTag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, splitAcrossPerformers.Id, targetPerformer.Id, firstTag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, splitAcrossPerformers.Id, otherPerformer.Id, secondTag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, firstOnly.Id, targetPerformer.Id, firstTag.Id));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (items, count) = await new VideoRepository(context).FindAsync(
            new VideoFilter
            {
                PerformerFilterCriterion = new RelatedFilterCriterion<PerformerFilter>
                {
                    PerformerIdsCriterion = new MultiIdCriterion { Modifier = CriterionModifier.Includes, Value = [targetPerformer.Id] },
                    PerformerOccurrenceTagsCriterion = new MultiIdCriterion
                    {
                        Modifier = CriterionModifier.ExcludesAll,
                        Value = [firstTag.Id, secondTag.Id],
                        Depth = -1,
                    },
                },
            },
            new FindFilter { Page = 1, PerPage = 50, Sort = "title" },
            TestContext.Current.CancellationToken);

        Assert.Equal(3, count);
        Assert.Equal(["first-only", "split-across-performers", "untagged"], items.Select(video => video.Title));
    }

    [Fact]
    [RequiresPostgres]
    public async Task RelatedPerformerCriterion_ExcludesAllPerformerIdsDropsOnlyThatPerformersLinks()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var targetPerformer = CreatePerformer("Target", new DateOnly(2000, 1, 1));
        var otherPerformer = CreatePerformer("Other", new DateOnly(2000, 1, 1));
        var targetOnly = CreateVideoWithFile("target-only", performer: targetPerformer);
        var both = CreateVideoWithFile("both", performer: targetPerformer);
        both.VideoPerformers.Add(new VideoPerformer { Performer = otherPerformer });
        var otherOnly = CreateVideoWithFile("other-only", performer: otherPerformer);
        context.Videos.AddRange(targetOnly, both, otherOnly);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (items, _) = await new VideoRepository(context).FindAsync(
            new VideoFilter
            {
                PerformerFilterCriterion = new RelatedFilterCriterion<PerformerFilter>
                {
                    PerformerIdsCriterion = new MultiIdCriterion { Modifier = CriterionModifier.ExcludesAll, Value = [targetPerformer.Id] },
                },
            },
            new FindFilter { Page = 1, PerPage = 50, Sort = "title" },
            TestContext.Current.CancellationToken);

        Assert.Equal(["both", "other-only"], items.Select(video => video.Title));
    }

    [Fact]
    [RequiresPostgres]
    public async Task RelatedPerformerCriterion_ExpandsOccurrenceTagDescendantsOnTheSameLink()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var parentTag = new Tag { Name = "Parent Occurrence Tag" };
        var childTag = new Tag { Name = "Child Occurrence Tag" };
        var targetPerformer = CreatePerformer("Target", new DateOnly(2000, 1, 1));
        var otherPerformer = CreatePerformer("Other", new DateOnly(2000, 1, 1));
        var targetTagged = CreateVideoWithFile("target-tagged", performer: targetPerformer);
        var otherTagged = CreateVideoWithFile("other-tagged", performer: targetPerformer);
        otherTagged.VideoPerformers.Add(new VideoPerformer { Performer = otherPerformer });

        context.Tags.AddRange(parentTag, childTag);
        context.Videos.AddRange(targetTagged, otherTagged);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.Set<TagParent>().Add(new TagParent { ParentId = parentTag.Id, ChildId = childTag.Id });
        context.TagApplications.AddRange(
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, targetTagged.Id, targetPerformer.Id, childTag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, otherTagged.Id, otherPerformer.Id, childTag.Id));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (items, count) = await new VideoRepository(context).FindAsync(
            new VideoFilter
            {
                PerformerFilterCriterion = new RelatedFilterCriterion<PerformerFilter>
                {
                    PerformerIdsCriterion = new MultiIdCriterion { Modifier = CriterionModifier.Includes, Value = [targetPerformer.Id] },
                    PerformerOccurrenceTagsCriterion = new MultiIdCriterion
                    {
                        Modifier = CriterionModifier.Includes,
                        Value = [parentTag.Id],
                        Depth = -1,
                    },
                },
            },
            new FindFilter { Page = 1, PerPage = 50 },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
        Assert.Equal("target-tagged", Assert.Single(items).Title);
    }

    [Fact]
    [RequiresPostgres]
    public async Task RelatedPerformerOccurrenceTags_RespectRelationshipModesAndNulls()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var tag = new Tag { Name = "Occurrence Tag" };
        var firstPerformer = CreatePerformer("First", new DateOnly(2000, 1, 1));
        var secondPerformer = CreatePerformer("Second", new DateOnly(2000, 1, 1));
        Video WithBothPerformers(string title)
        {
            var video = CreateVideoWithFile(title, performer: firstPerformer);
            video.VideoPerformers.Add(new VideoPerformer { Performer = secondPerformer });
            return video;
        }

        var allTagged = WithBothPerformers("all-tagged");
        var mixed = WithBothPerformers("mixed");
        var noneTagged = WithBothPerformers("none-tagged");
        var noPerformers = CreateVideoWithFile("no-performers");
        context.Tags.Add(tag);
        context.Videos.AddRange(allTagged, mixed, noneTagged, noPerformers);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.TagApplications.AddRange(
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, allTagged.Id, firstPerformer.Id, tag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, allTagged.Id, secondPerformer.Id, tag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, mixed.Id, firstPerformer.Id, tag.Id));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        async Task<string[]> FindAsync(RelatedFilterMode mode, CriterionModifier modifier)
        {
            var (items, _) = await new VideoRepository(context).FindAsync(
                new VideoFilter
                {
                    PerformerFilterCriterion = new RelatedFilterCriterion<PerformerFilter>
                    {
                        Mode = mode,
                        PerformerOccurrenceTagsCriterion = new MultiIdCriterion
                        {
                            Modifier = modifier,
                            Value = modifier is CriterionModifier.IsNull or CriterionModifier.NotNull ? [] : [tag.Id],
                        },
                    },
                },
                new FindFilter { Page = 1, PerPage = 50, Sort = "title" },
                TestContext.Current.CancellationToken);
            return items.Select(video => video.Title ?? string.Empty).ToArray();
        }

        Assert.Equal(["all-tagged", "mixed"], await FindAsync(RelatedFilterMode.AtLeastOne, CriterionModifier.Includes));
        Assert.Equal(["all-tagged"], await FindAsync(RelatedFilterMode.Every, CriterionModifier.Includes));
        Assert.Equal(["none-tagged"], await FindAsync(RelatedFilterMode.None, CriterionModifier.Includes));
        Assert.Equal(["mixed", "none-tagged"], await FindAsync(RelatedFilterMode.AtLeastOne, CriterionModifier.IsNull));
    }

    [Fact]
    [RequiresPostgres]
    public async Task PerformerTagsCriterion_Includes_MatchesVideosByPerformerOccurrenceTag()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var tag = new Tag { Name = "Featured" };
        var taggedVideo = CreateVideoWithFile("tagged-performer-video", performer: CreatePerformer("Tagged", new DateOnly(2000, 1, 1)));
        var untaggedVideo = CreateVideoWithFile("untagged-performer-video", performer: CreatePerformer("Untagged", new DateOnly(2000, 1, 1)));

        context.Tags.Add(tag);
        context.Videos.AddRange(taggedVideo, untaggedVideo);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TagApplications.Add(new TagApplication
        {
            HostType = AffinityHostType.Video,
            HostId = taggedVideo.Id,
            ContextType = "performer",
            ContextId = taggedVideo.VideoPerformers.Single().Performer!.Id,
            TagId = tag.Id,
            SourceKey = "test",
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            PerformerTagsCriterion = new MultiIdCriterion
            {
                Value = [tag.Id],
                Modifier = CriterionModifier.Includes,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

        Assert.Equal(1, totalCount);
        Assert.Equal(["tagged-performer-video"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Theory]
    [InlineData(CriterionModifier.IsNull, "untagged-performer-video")]
    [InlineData(CriterionModifier.NotNull, "tagged-performer-video")]
    [RequiresPostgres]
    public async Task PerformerTagsCriterion_PresenceModifiers_MatchVideosByAnyPerformerOccurrenceTag(CriterionModifier modifier, string expectedTitle)
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var tag = new Tag { Name = "Featured" };
        var taggedVideo = CreateVideoWithFile("tagged-performer-video", performer: CreatePerformer("Tagged", new DateOnly(2000, 1, 1)));
        var untaggedVideo = CreateVideoWithFile("untagged-performer-video", performer: CreatePerformer("Untagged", new DateOnly(2000, 1, 1)));

        context.Tags.Add(tag);
        context.Videos.AddRange(taggedVideo, untaggedVideo);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TagApplications.AddRange(
            new TagApplication
            {
                HostType = AffinityHostType.Video,
                HostId = taggedVideo.Id,
                ContextType = "performer",
                ContextId = taggedVideo.VideoPerformers.Single().Performer!.Id,
                TagId = tag.Id,
                SourceKey = "test",
            },
            new TagApplication
            {
                HostType = AffinityHostType.Video,
                HostId = untaggedVideo.Id,
                TagId = tag.Id,
                SourceKey = "test",
            },
            new TagApplication
            {
                HostType = AffinityHostType.Image,
                HostId = untaggedVideo.Id,
                ContextType = "performer",
                ContextId = untaggedVideo.VideoPerformers.Single().Performer!.Id,
                TagId = tag.Id,
                SourceKey = "test",
            });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);

        // Depth -1 with no values is what the editor sends after "Include sub-tags" was toggled before choosing None/Any.
        foreach (var depth in new int?[] { null, -1 })
        {
            var filter = new VideoFilter
            {
                PerformerTagsCriterion = new MultiIdCriterion { Modifier = modifier, Depth = depth },
            };

            var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

            Assert.Equal(1, totalCount);
            Assert.Equal([expectedTitle], items.Select(video => video.Title ?? string.Empty).ToArray());
        }
    }

    [Theory]
    [InlineData(CriterionModifier.IsNull, "other-performer-tagged")]
    [InlineData(CriterionModifier.NotNull, "target-tagged")]
    [RequiresPostgres]
    public async Task PerformerTagsCriterion_PresenceModifiers_WithRequiredPerformerCriterion_ScopeToThatPerformer(CriterionModifier modifier, string expectedTitle)
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var tag = new Tag { Name = "Occurrence Tag" };
        var targetPerformer = CreatePerformer("Target", new DateOnly(2000, 1, 1));
        var otherPerformer = CreatePerformer("Other", new DateOnly(2000, 1, 1));
        var targetTaggedVideo = CreateVideoWithFile("target-tagged", performer: targetPerformer);
        var otherPerformerTaggedVideo = CreateVideoWithFile("other-performer-tagged", performer: targetPerformer);
        otherPerformerTaggedVideo.VideoPerformers.Add(new VideoPerformer { Performer = otherPerformer });

        context.Tags.Add(tag);
        context.Videos.AddRange(targetTaggedVideo, otherPerformerTaggedVideo);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TagApplications.AddRange(
            new TagApplication
            {
                HostType = AffinityHostType.Video,
                HostId = targetTaggedVideo.Id,
                ContextType = "performer",
                ContextId = targetPerformer.Id,
                TagId = tag.Id,
                SourceKey = "test",
            },
            new TagApplication
            {
                HostType = AffinityHostType.Video,
                HostId = otherPerformerTaggedVideo.Id,
                ContextType = "performer",
                ContextId = otherPerformer.Id,
                TagId = tag.Id,
                SourceKey = "test",
            });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            PerformersCriterion = new MultiIdCriterion
            {
                RequiredIds = [targetPerformer.Id],
                Modifier = CriterionModifier.Includes,
            },
            PerformerTagsCriterion = new MultiIdCriterion { Modifier = modifier },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

        Assert.Equal(1, totalCount);
        Assert.Equal([expectedTitle], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task PerformerTagsCriterion_WithRequiredPerformerCriterion_MatchesSamePerformerOccurrence()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var tag = new Tag { Name = "Occurrence Tag" };
        var targetPerformer = CreatePerformer("Target", new DateOnly(2000, 1, 1));
        var otherPerformer = CreatePerformer("Other", new DateOnly(2000, 1, 1));
        var targetTaggedVideo = CreateVideoWithFile("target-tagged", performer: targetPerformer);
        var wrongPerformerTaggedVideo = CreateVideoWithFile("wrong-performer-tagged", performer: targetPerformer);
        wrongPerformerTaggedVideo.VideoPerformers.Add(new VideoPerformer { Performer = otherPerformer });

        context.Tags.Add(tag);
        context.Videos.AddRange(targetTaggedVideo, wrongPerformerTaggedVideo);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TagApplications.AddRange(
            new TagApplication
            {
                HostType = AffinityHostType.Video,
                HostId = targetTaggedVideo.Id,
                ContextType = "performer",
                ContextId = targetPerformer.Id,
                TagId = tag.Id,
                SourceKey = "test",
            },
            new TagApplication
            {
                HostType = AffinityHostType.Video,
                HostId = wrongPerformerTaggedVideo.Id,
                ContextType = "performer",
                ContextId = otherPerformer.Id,
                TagId = tag.Id,
                SourceKey = "test",
            });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            PerformersCriterion = new MultiIdCriterion
            {
                RequiredIds = [targetPerformer.Id],
                Modifier = CriterionModifier.Includes,
            },
            PerformerTagsCriterion = new MultiIdCriterion
            {
                Value = [tag.Id],
                Modifier = CriterionModifier.Includes,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

        Assert.Equal(1, totalCount);
        Assert.Equal(["target-tagged"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Theory]
    [InlineData(AffinityHostType.Image, "image")]
    [InlineData(AffinityHostType.Audio, "audio")]
    [InlineData(AffinityHostType.Text, "text")]
    [RequiresPostgres]
    public async Task TagApplicationService_AddAsync_AllowsPerformerContextForMediaHosts(AffinityHostType hostType, string hostTypeValue)
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var tag = new Tag { Name = $"{hostTypeValue}-occurrence" };
        var performer = CreatePerformer($"{hostTypeValue}-performer", new DateOnly(2000, 1, 1));

        var host = hostType switch
        {
            AffinityHostType.Image => AddImageHost(context, CreateImage($"{hostTypeValue}-host", performer)),
            AffinityHostType.Audio => AddAudioHost(context, CreateAudio($"{hostTypeValue}-host", performer)),
            AffinityHostType.Text => AddTextHost(context, CreateTextDocument($"{hostTypeValue}-host", performer)),
            _ => throw new ArgumentOutOfRangeException(nameof(hostType), hostType, null),
        };
        context.Tags.Add(tag);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var hostId = host.Id;

        var service = new TagApplicationService(context);
        var application = await service.AddAsync(
            new TagApplicationCreateDto(hostTypeValue, hostId, tag.Id, "user", "performer", performer.Id),
            CancellationToken.None);

        Assert.Equal(hostType, application.HostType);
        Assert.Equal(hostId, application.HostId);
        Assert.Equal("performer", application.ContextType);
        Assert.Equal(performer.Id, application.ContextId);
        Assert.Equal(tag.Id, application.TagId);
    }

    [Fact]
    [RequiresPostgres]
    public async Task ImageFilter_PerformerTagsCriterion_WithRequiredPerformerCriterion_MatchesSamePerformerOccurrence()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var tag = new Tag { Name = "Image Occurrence Tag" };
        var targetPerformer = CreatePerformer("Image Target", new DateOnly(2000, 1, 1));
        var otherPerformer = CreatePerformer("Image Other", new DateOnly(2000, 1, 1));
        var targetTaggedImage = CreateImage("target-tagged-image", targetPerformer);
        var wrongPerformerTaggedImage = CreateImage("wrong-performer-tagged-image", targetPerformer);
        wrongPerformerTaggedImage.ImagePerformers.Add(new ImagePerformer { Performer = otherPerformer });

        context.Tags.Add(tag);
        context.Images.AddRange(targetTaggedImage, wrongPerformerTaggedImage);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TagApplications.AddRange(
            CreatePerformerOccurrenceApplication(AffinityHostType.Image, targetTaggedImage.Id, targetPerformer.Id, tag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Image, wrongPerformerTaggedImage.Id, otherPerformer.Id, tag.Id));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new ImageRepository(context);
        var filter = new ImageFilter
        {
            PerformersCriterion = new MultiIdCriterion
            {
                RequiredIds = [targetPerformer.Id],
                Modifier = CriterionModifier.Includes,
            },
            PerformerTagsCriterion = new MultiIdCriterion
            {
                Value = [tag.Id],
                Modifier = CriterionModifier.Includes,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

        Assert.Equal(1, totalCount);
        Assert.Equal(["target-tagged-image"], items.Select(image => image.Title ?? string.Empty).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task AudiosController_FindPost_PerformerTagsCriterion_WithRequiredPerformerCriterion_MatchesSamePerformerOccurrence()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var tag = new Tag { Name = "Audio Occurrence Tag" };
        var targetPerformer = CreatePerformer("Audio Target", new DateOnly(2000, 1, 1));
        var otherPerformer = CreatePerformer("Audio Other", new DateOnly(2000, 1, 1));
        var targetTaggedAudio = CreateAudio("target-tagged-audio", targetPerformer);
        var wrongPerformerTaggedAudio = CreateAudio("wrong-performer-tagged-audio", targetPerformer);
        wrongPerformerTaggedAudio.AudioPerformers.Add(new AudioPerformer { Performer = otherPerformer });

        context.Tags.Add(tag);
        context.Audios.AddRange(targetTaggedAudio, wrongPerformerTaggedAudio);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TagApplications.AddRange(
            CreatePerformerOccurrenceApplication(AffinityHostType.Audio, targetTaggedAudio.Id, targetPerformer.Id, tag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Audio, wrongPerformerTaggedAudio.Id, otherPerformer.Id, tag.Id));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = new AudiosController(context, new CustomFieldService(context), null!, null!, null!, null);
        var response = await controller.FindPost(new FilteredQueryRequest<AudioFilter>
        {
            FindFilter = new FindFilter { Page = 1, PerPage = 50, Sort = "title" },
            ObjectFilter = new AudioFilter
            {
                PerformersCriterion = new MultiIdCriterion { RequiredIds = [targetPerformer.Id], Modifier = CriterionModifier.Includes },
                PerformerTagsCriterion = new MultiIdCriterion { Value = [tag.Id], Modifier = CriterionModifier.Includes },
            },
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<PaginatedResponse<AudioDto>>(ok.Value);
        var audio = Assert.Single(payload.Items);

        Assert.Equal("target-tagged-audio", audio.Title);
    }

    [Fact]
    [RequiresPostgres]
    public async Task AudioPerformerFilterCriterion_MatchesExactPerformerOccurrenceTagsOnSameLink()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var tag = new Tag { Name = "Audio Occurrence Tag" };
        var targetPerformer = CreatePerformer("Audio Target", new DateOnly(2000, 1, 1));
        var otherPerformer = CreatePerformer("Audio Other", new DateOnly(2000, 1, 1));
        var targetTagged = CreateAudio("target-tagged-audio", targetPerformer);
        targetTagged.AudioPerformers.Add(new AudioPerformer { Performer = otherPerformer });
        var wrongPerformerTagged = CreateAudio("wrong-performer-tagged-audio", targetPerformer);
        wrongPerformerTagged.AudioPerformers.Add(new AudioPerformer { Performer = otherPerformer });

        context.Tags.Add(tag);
        context.Audios.AddRange(targetTagged, wrongPerformerTagged);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TagApplications.AddRange(
            CreatePerformerOccurrenceApplication(AffinityHostType.Audio, targetTagged.Id, targetPerformer.Id, tag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Audio, wrongPerformerTagged.Id, otherPerformer.Id, tag.Id));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var matches = await AudioFilterQuery.BuildAsync(
            context,
            new AudioFilter
            {
                PerformerFilterCriterion = new RelatedFilterCriterion<PerformerFilter>
                {
                    PerformerIdsCriterion = new MultiIdCriterion { Modifier = CriterionModifier.Includes, Value = [targetPerformer.Id] },
                    PerformerOccurrenceTagsCriterion = new MultiIdCriterion { Modifier = CriterionModifier.Includes, Value = [tag.Id] },
                },
            },
            null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(
            ["target-tagged-audio"],
            await matches.Select(audio => audio.Title!).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [RequiresPostgres]
    public async Task AudioPerformerFilterCriterion_ExcludesAllOccurrenceTagsOnlyWhenOneLinkHasEveryTag()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var firstTag = new Tag { Name = "Audio First Occurrence Tag" };
        var secondTag = new Tag { Name = "Audio Second Occurrence Tag" };
        var targetPerformer = CreatePerformer("Audio Target", new DateOnly(2000, 1, 1));
        var otherPerformer = CreatePerformer("Audio Other", new DateOnly(2000, 1, 1));
        var bothOnTarget = CreateAudio("both-on-target-audio", targetPerformer);
        var splitAcrossPerformers = CreateAudio("split-across-performers-audio", targetPerformer);
        splitAcrossPerformers.AudioPerformers.Add(new AudioPerformer { Performer = otherPerformer });
        var untagged = CreateAudio("untagged-audio", targetPerformer);

        context.Tags.AddRange(firstTag, secondTag);
        context.Audios.AddRange(bothOnTarget, splitAcrossPerformers, untagged);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TagApplications.AddRange(
            CreatePerformerOccurrenceApplication(AffinityHostType.Audio, bothOnTarget.Id, targetPerformer.Id, firstTag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Audio, bothOnTarget.Id, targetPerformer.Id, secondTag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Audio, splitAcrossPerformers.Id, targetPerformer.Id, firstTag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Audio, splitAcrossPerformers.Id, otherPerformer.Id, secondTag.Id));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var matches = await AudioFilterQuery.BuildAsync(
            context,
            new AudioFilter
            {
                PerformerFilterCriterion = new RelatedFilterCriterion<PerformerFilter>
                {
                    PerformerIdsCriterion = new MultiIdCriterion { Modifier = CriterionModifier.Includes, Value = [targetPerformer.Id] },
                    PerformerOccurrenceTagsCriterion = new MultiIdCriterion
                    {
                        Modifier = CriterionModifier.ExcludesAll,
                        Value = [firstTag.Id, secondTag.Id],
                    },
                },
            },
            null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(
            ["split-across-performers-audio", "untagged-audio"],
            await matches.Select(audio => audio.Title!).Order().ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [RequiresPostgres]
    public async Task AudioPerformerFilterCriterion_ExcludesOccurrenceTagsPerLinkAndIgnoresVideoApplications()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var tag = new Tag { Name = "Audio Excluded Tag" };
        var performer = CreatePerformer("Audio Only", new DateOnly(2000, 1, 1));
        var answered = CreateAudio("answered-audio", performer);
        var unanswered = CreateAudio("unanswered-audio", performer);
        // A video application for the same performer and tag must not answer the audio occurrence.
        var video = CreateVideoWithFile("same-performer-video", performer: performer);

        context.Tags.Add(tag);
        context.Audios.AddRange(answered, unanswered);
        context.Videos.Add(video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TagApplications.AddRange(
            CreatePerformerOccurrenceApplication(AffinityHostType.Audio, answered.Id, performer.Id, tag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, video.Id, performer.Id, tag.Id));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var matches = await AudioFilterQuery.BuildAsync(
            context,
            new AudioFilter
            {
                PerformerFilterCriterion = new RelatedFilterCriterion<PerformerFilter>
                {
                    PerformerOccurrenceTagsCriterion = new MultiIdCriterion { Modifier = CriterionModifier.Excludes, Value = [tag.Id] },
                },
            },
            null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(
            ["unanswered-audio"],
            await matches.Select(audio => audio.Title!).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [RequiresPostgres]
    public async Task TextsController_FindPost_PerformerTagsCriterion_WithRequiredPerformerCriterion_MatchesSamePerformerOccurrence()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var tag = new Tag { Name = "Text Occurrence Tag" };
        var targetPerformer = CreatePerformer("Text Target", new DateOnly(2000, 1, 1));
        var otherPerformer = CreatePerformer("Text Other", new DateOnly(2000, 1, 1));
        var targetTaggedText = CreateTextDocument("target-tagged-text", targetPerformer);
        var wrongPerformerTaggedText = CreateTextDocument("wrong-performer-tagged-text", targetPerformer);
        wrongPerformerTaggedText.TextPerformers.Add(new TextPerformer { Performer = otherPerformer });

        context.Tags.Add(tag);
        context.TextDocuments.AddRange(targetTaggedText, wrongPerformerTaggedText);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TagApplications.AddRange(
            CreatePerformerOccurrenceApplication(AffinityHostType.Text, targetTaggedText.Id, targetPerformer.Id, tag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Text, wrongPerformerTaggedText.Id, otherPerformer.Id, tag.Id));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = new TextsController(context, new CustomFieldService(context), null!, null!, null!, null!, null);
        var response = await controller.FindPost(new FilteredQueryRequest<TextDocumentFilter>
        {
            FindFilter = new FindFilter { Page = 1, PerPage = 50, Sort = "title" },
            ObjectFilter = new TextDocumentFilter
            {
                PerformersCriterion = new MultiIdCriterion { RequiredIds = [targetPerformer.Id], Modifier = CriterionModifier.Includes },
                PerformerTagsCriterion = new MultiIdCriterion { Value = [tag.Id], Modifier = CriterionModifier.Includes },
            },
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<PaginatedResponse<TextDocumentDto>>(ok.Value);
        var text = Assert.Single(payload.Items);

        Assert.Equal("target-tagged-text", text.Title);
    }

    [Fact]
    [RequiresPostgres]
    public async Task PerformerTagsCriterion_WithSubTags_MatchesDescendantOccurrenceTags()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var (parent, child, grandchild) = await AddTagChainAsync(context, "occurrence");
        var unrelated = new Tag { Name = "unrelated" };
        context.Tags.Add(unrelated);
        var performer = CreatePerformer("Occurrence", new DateOnly(2000, 1, 1));
        var childVideo = CreateVideoWithFile("child-tagged", performer: performer);
        var grandchildVideo = CreateVideoWithFile("grandchild-tagged");
        grandchildVideo.VideoPerformers.Add(new VideoPerformer { Performer = performer });
        var unrelatedVideo = CreateVideoWithFile("unrelated-tagged");
        unrelatedVideo.VideoPerformers.Add(new VideoPerformer { Performer = performer });
        context.Videos.AddRange(childVideo, grandchildVideo, unrelatedVideo);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.TagApplications.AddRange(
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, childVideo.Id, performer.Id, child.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, childVideo.Id, performer.Id, unrelated.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, grandchildVideo.Id, performer.Id, grandchild.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, unrelatedVideo.Id, performer.Id, unrelated.Id));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        async Task<string[]> FindAsync(MultiIdCriterion criterion)
        {
            var (items, _) = await new VideoRepository(context).FindAsync(
                new VideoFilter { PerformerTagsCriterion = criterion },
                new FindFilter { Page = 1, PerPage = 50, Sort = "title" },
                TestContext.Current.CancellationToken);
            return items.Select(video => video.Title ?? string.Empty).ToArray();
        }

        Assert.Equal(["child-tagged", "grandchild-tagged"], await FindAsync(new MultiIdCriterion { Value = [parent.Id], Modifier = CriterionModifier.Includes, Depth = -1 }));
        Assert.Equal(["child-tagged", "grandchild-tagged"], await FindAsync(new MultiIdCriterion { Value = [parent.Id], Modifier = CriterionModifier.IncludesAll, Depth = -1 }));
        Assert.Equal(["child-tagged"], await FindAsync(new MultiIdCriterion { Value = [parent.Id, unrelated.Id], Modifier = CriterionModifier.IncludesAll, Depth = -1 }));
        Assert.Equal(["grandchild-tagged", "unrelated-tagged"], await FindAsync(new MultiIdCriterion { Value = [parent.Id, unrelated.Id], Modifier = CriterionModifier.ExcludesAll, Depth = -1 }));
        Assert.Equal(["unrelated-tagged"], await FindAsync(new MultiIdCriterion { Value = [], Excludes = [parent.Id], Modifier = CriterionModifier.Includes, Depth = -1 }));
        Assert.Empty(await FindAsync(new MultiIdCriterion { Value = [parent.Id], Modifier = CriterionModifier.Includes }));
    }

    [Fact]
    [RequiresPostgres]
    public async Task PerformerTagsCriterion_WithSubTags_MatchesDescendantOccurrenceTagsForImagesAudiosAndTexts()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var (parent, child, _) = await AddTagChainAsync(context, "media-occurrence");
        var performer = CreatePerformer("Media Occurrence", new DateOnly(2000, 1, 1));
        var image = CreateImage("child-tagged-image", performer);
        var audio = CreateAudio("child-tagged-audio", performer);
        var text = CreateTextDocument("child-tagged-text", performer);
        context.Images.AddRange(image, CreateImage("untagged-image", performer));
        context.Audios.AddRange(audio, CreateAudio("untagged-audio", performer));
        context.TextDocuments.AddRange(text, CreateTextDocument("untagged-text", performer));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.TagApplications.AddRange(
            CreatePerformerOccurrenceApplication(AffinityHostType.Image, image.Id, performer.Id, child.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Audio, audio.Id, performer.Id, child.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Text, text.Id, performer.Id, child.Id));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        MultiIdCriterion Criterion() => new() { Value = [parent.Id], Modifier = CriterionModifier.IncludesAll, Depth = -1 };

        var (images, _) = await new ImageRepository(context).FindAsync(
            new ImageFilter { PerformerTagsCriterion = Criterion() },
            new FindFilter { Page = 1, PerPage = 50 },
            TestContext.Current.CancellationToken);
        Assert.Equal("child-tagged-image", Assert.Single(images).Title);

        var audioResponse = await new AudiosController(context, new CustomFieldService(context), null!, null!, null!, null).FindPost(new FilteredQueryRequest<AudioFilter>
        {
            FindFilter = new FindFilter { Page = 1, PerPage = 50, Sort = "title" },
            ObjectFilter = new AudioFilter { PerformerTagsCriterion = Criterion() },
        }, CancellationToken.None);
        var audios = Assert.IsType<PaginatedResponse<AudioDto>>(Assert.IsType<OkObjectResult>(audioResponse.Result).Value);
        Assert.Equal("child-tagged-audio", Assert.Single(audios.Items).Title);

        var textResponse = await new TextsController(context, new CustomFieldService(context), null!, null!, null!, null!, null).FindPost(new FilteredQueryRequest<TextDocumentFilter>
        {
            FindFilter = new FindFilter { Page = 1, PerPage = 50, Sort = "title" },
            ObjectFilter = new TextDocumentFilter { PerformerTagsCriterion = Criterion() },
        }, CancellationToken.None);
        var texts = Assert.IsType<PaginatedResponse<TextDocumentDto>>(Assert.IsType<OkObjectResult>(textResponse.Result).Value);
        Assert.Equal("child-tagged-text", Assert.Single(texts.Items).Title);
    }

    [Theory]
    [InlineData(CriterionModifier.IsNull, "untagged")]
    [InlineData(CriterionModifier.NotNull, "tagged")]
    [RequiresPostgres]
    public async Task PerformerTagsCriterion_PresenceModifiers_MatchImagesAudiosAndTexts(CriterionModifier modifier, string expectedPrefix)
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var tag = new Tag { Name = "media-presence" };
        var performer = CreatePerformer("Media Presence", new DateOnly(2000, 1, 1));
        var image = CreateImage("tagged-image", performer);
        var audio = CreateAudio("tagged-audio", performer);
        var text = CreateTextDocument("tagged-text", performer);
        context.Tags.Add(tag);
        context.Images.AddRange(image, CreateImage("untagged-image", performer));
        context.Audios.AddRange(audio, CreateAudio("untagged-audio", performer));
        context.TextDocuments.AddRange(text, CreateTextDocument("untagged-text", performer));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.TagApplications.AddRange(
            CreatePerformerOccurrenceApplication(AffinityHostType.Image, image.Id, performer.Id, tag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Audio, audio.Id, performer.Id, tag.Id),
            CreatePerformerOccurrenceApplication(AffinityHostType.Text, text.Id, performer.Id, tag.Id));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        MultiIdCriterion Criterion() => new() { Modifier = modifier };

        var (images, _) = await new ImageRepository(context).FindAsync(
            new ImageFilter { PerformerTagsCriterion = Criterion() },
            new FindFilter { Page = 1, PerPage = 50 },
            TestContext.Current.CancellationToken);
        Assert.Equal($"{expectedPrefix}-image", Assert.Single(images).Title);

        var audioResponse = await new AudiosController(context, new CustomFieldService(context), null!, null!, null!, null).FindPost(new FilteredQueryRequest<AudioFilter>
        {
            FindFilter = new FindFilter { Page = 1, PerPage = 50, Sort = "title" },
            ObjectFilter = new AudioFilter { PerformerTagsCriterion = Criterion() },
        }, CancellationToken.None);
        var audios = Assert.IsType<PaginatedResponse<AudioDto>>(Assert.IsType<OkObjectResult>(audioResponse.Result).Value);
        Assert.Equal($"{expectedPrefix}-audio", Assert.Single(audios.Items).Title);

        var textResponse = await new TextsController(context, new CustomFieldService(context), null!, null!, null!, null!, null).FindPost(new FilteredQueryRequest<TextDocumentFilter>
        {
            FindFilter = new FindFilter { Page = 1, PerPage = 50, Sort = "title" },
            ObjectFilter = new TextDocumentFilter { PerformerTagsCriterion = Criterion() },
        }, CancellationToken.None);
        var texts = Assert.IsType<PaginatedResponse<TextDocumentDto>>(Assert.IsType<OkObjectResult>(textResponse.Result).Value);
        Assert.Equal($"{expectedPrefix}-text", Assert.Single(texts.Items).Title);
    }

    [Fact]
    [RequiresPostgres]
    public async Task GalleryPerformerTagsCriterion_WithSubTags_MatchesDescendantPerformerTags()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var (parent, child, _) = await AddTagChainAsync(context, "gallery-performer");
        var taggedPerformer = CreatePerformer("Tagged Gallery Performer", new DateOnly(2000, 1, 1));
        taggedPerformer.PerformerTags.Add(new PerformerTag { TagId = child.Id });
        var taggedGallery = new Gallery { Title = "child-tagged-gallery" };
        taggedGallery.GalleryPerformers.Add(new GalleryPerformer { Performer = taggedPerformer });
        var untaggedGallery = new Gallery { Title = "untagged-gallery" };
        untaggedGallery.GalleryPerformers.Add(new GalleryPerformer { Performer = CreatePerformer("Untagged Gallery Performer", new DateOnly(2000, 1, 1)) });
        context.Galleries.AddRange(taggedGallery, untaggedGallery);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (galleries, _) = await new GalleryRepository(context).FindAsync(
            new GalleryFilter { PerformerTagsCriterion = new MultiIdCriterion { Value = [parent.Id], Modifier = CriterionModifier.IncludesAll, Depth = -1 } },
            new FindFilter { Page = 1, PerPage = 50 },
            TestContext.Current.CancellationToken);

        Assert.Equal("child-tagged-gallery", Assert.Single(galleries).Title);
    }

    [Fact]
    [RequiresPostgres]
    public async Task TextsController_GetById_OrdersEffectiveTagsByName()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var first = new Tag { Name = "Alpha" };
        var middle = new Tag { Name = "Middle" };
        var last = new Tag { Name = "Zulu" };
        var text = CreateTextDocument("tag-order-text");
        text.TextTags.Add(new TextTag { Tag = last });
        text.TextTags.Add(new TextTag { Tag = first });

        context.Tags.Add(middle);
        context.TextDocuments.Add(text);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TagApplications.Add(new TagApplication
        {
            HostType = AffinityHostType.Text,
            HostId = text.Id,
            TagId = middle.Id,
            SourceKey = "ext:test.import",
            SourceRunId = "run-text-tag-order",
            ModelKey = "test-model",
            Confidence = 0.9f,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = new TextsController(context, new CustomFieldService(context), null!, null!, null!, null!, null);
        var response = await controller.GetById(text.Id, TestContext.Current.CancellationToken);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var dto = Assert.IsType<TextDocumentDto>(ok.Value);

        Assert.Equal(["Alpha", "Middle", "Zulu"], dto.Tags.Select(tag => tag.Name).ToArray());
        Assert.True(dto.Tags[1].IsDerived);
        Assert.False(dto.Tags[1].CanRemove);
    }

    [Fact]
    [RequiresPostgres]
    public async Task TagDurationCriterion_AppliesAllClauses()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var shortTag = new Tag { Name = "Short" };
        var percentTag = new Tag { Name = "Percent" };
        var matchingVideo = CreateVideoWithFile("matching-duration");
        var longVideo = CreateVideoWithFile("too-long");
        var lowPercentVideo = CreateVideoWithFile("low-percent");

        context.Tags.AddRange(shortTag, percentTag);
        context.Videos.AddRange(matchingVideo, longVideo, lowPercentVideo);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TagApplications.AddRange(
            CreateDurationApplication(matchingVideo.Id, shortTag.Id, totalDurationSec: 20, hostDurationSec: 100),
            CreateDurationApplication(matchingVideo.Id, percentTag.Id, totalDurationSec: 20, hostDurationSec: 100),
            CreateDurationApplication(longVideo.Id, shortTag.Id, totalDurationSec: 40, hostDurationSec: 100),
            CreateDurationApplication(longVideo.Id, percentTag.Id, totalDurationSec: 20, hostDurationSec: 100),
            CreateDurationApplication(lowPercentVideo.Id, shortTag.Id, totalDurationSec: 20, hostDurationSec: 100),
            CreateDurationApplication(lowPercentVideo.Id, percentTag.Id, totalDurationSec: 5, hostDurationSec: 100));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            TagDurationCriterion = new TagDurationCriterion
            {
                Clauses =
                [
                    new TagDurationClause { TagId = shortTag.Id, Modifier = CriterionModifier.LessThan, Unit = "seconds", Value = 30 },
                    new TagDurationClause { TagId = percentTag.Id, Modifier = CriterionModifier.GreaterThan, Unit = "percent", Value = 10 },
                ],
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

        Assert.Equal(1, totalCount);
        Assert.Equal(["matching-duration"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task AggregateAsync_SumsOnlyVideosMatchingTheActiveFilter()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Videos.AddRange(
            new Video { Title = "included", Organized = true, MaxDuration = 90.5, MaxFileSize = 1_500 },
            new Video { Title = "also included", Organized = true, MaxDuration = 29.5, MaxFileSize = 2_500 },
            new Video { Title = "excluded", Organized = false, MaxDuration = 600, MaxFileSize = 50_000 });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var aggregate = await new VideoRepository(context).AggregateAsync(new VideoFilter { Organized = true }, new FindFilter(), TestContext.Current.CancellationToken);

        Assert.Equal(2, aggregate.Count);
        Assert.Equal(120, aggregate.Duration);
        Assert.Equal(4_000, aggregate.FileSize);
    }

    [Fact]
    [RequiresPostgres]
    public async Task MediaAggregates_SumFilteredImageAudioTextAndGalleryMetrics()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var image = new Image { Title = "image", MaxFileSize = 4_000 };
        image.Files.Add(new ImageFile { Path = "/media/image.jpg", Basename = "image.jpg", ParentFolder = FolderAt("/media"), Width = 2_000, Height = 1_000, Size = 4_000 });
        context.Images.AddRange(image, new Image { Title = "excluded image", MaxFileSize = 90_000 });
        var audio = new Audio { Title = "audio", MaxDuration = 75.5, MaxFileSize = 5_000 };
        var excludedAudio = new Audio { Title = "excluded audio", MaxDuration = 900, MaxFileSize = 90_000 };
        var text = new TextDocument { Title = "text", MaxFileSize = 6_000 };
        var excludedText = new TextDocument { Title = "excluded text", MaxFileSize = 90_000 };
        context.Audios.AddRange(audio, excludedAudio);
        context.TextDocuments.AddRange(text, excludedText);
        var folder = FolderAt("/media");
        var gallery = new Gallery { Title = "gallery", Organized = true };
        gallery.Files.Add(new GalleryFile { Basename = "gallery.zip", Path = "/media/gallery.zip", ParentFolder = folder, Size = 7_000 });
        var excludedGallery = new Gallery { Title = "excluded gallery", Organized = false };
        excludedGallery.Files.Add(new GalleryFile { Basename = "excluded.zip", Path = "/media/excluded.zip", ParentFolder = folder, Size = 90_000 });
        context.Galleries.AddRange(gallery, excludedGallery);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var imageAggregate = await new ImageRepository(context).AggregateAsync(new ImageFilter { Ids = [image.Id] }, new FindFilter(), TestContext.Current.CancellationToken);
        Assert.Equal(1, imageAggregate.Count);
        Assert.Equal(4_000, imageAggregate.FileSize);

        var audioResponse = await new AudiosController(context, null!, null!, null!, null!, null).Aggregate(
            new FilteredQueryRequest<AudioFilter> { Ids = [audio.Id] }, CancellationToken.None);
        var audioAggregate = Assert.IsType<AudioAggregate>(Assert.IsType<OkObjectResult>(audioResponse.Result).Value);
        Assert.Equal(75.5, audioAggregate.Duration);
        Assert.Equal(5_000, audioAggregate.FileSize);

        var textResponse = await new TextsController(context, null!, null!, null!, null!, null!, null).Aggregate(
            new FilteredQueryRequest<TextDocumentFilter> { Ids = [text.Id] }, CancellationToken.None);
        var textAggregate = Assert.IsType<TextAggregate>(Assert.IsType<OkObjectResult>(textResponse.Result).Value);
        Assert.Equal(6_000, textAggregate.FileSize);

        var galleryAggregate = await new GalleryRepository(context).AggregateAsync(new GalleryFilter { Ids = [gallery.Id] }, new FindFilter(), TestContext.Current.CancellationToken);
        Assert.Equal(1, galleryAggregate.Count);
        Assert.Equal(7_000, galleryAggregate.FileSize);
    }

    [Fact]
    [RequiresPostgres]
    public async Task TagsCriterion_UsesThresholdQualifiedDerivedTagApplications()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var tag = new Tag { Name = "Action", MinOccurrencePercent = 80 };
        var qualifyingVideo = CreateVideoWithFile("qualifying-derived");
        var belowThresholdVideo = CreateVideoWithFile("below-threshold-derived");
        var manualVideo = CreateVideoWithFile("manual-tagged");
        manualVideo.VideoTags.Add(new VideoTag { Tag = tag });

        context.Tags.Add(tag);
        context.Videos.AddRange(qualifyingVideo, belowThresholdVideo, manualVideo);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TagApplications.AddRange(
            CreateDurationApplication(qualifyingVideo.Id, tag.Id, totalDurationSec: 82, hostDurationSec: 100),
            CreateDurationApplication(belowThresholdVideo.Id, tag.Id, totalDurationSec: 72, hostDurationSec: 100),
            CreateDurationApplication(manualVideo.Id, tag.Id, totalDurationSec: 72, hostDurationSec: 100));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);
        var binaryFilter = new VideoFilter
        {
            TagsCriterion = new MultiIdCriterion { Value = [tag.Id], Modifier = CriterionModifier.Includes },
        };
        var explicitDurationFilter = new VideoFilter
        {
            TagDurationCriterion = new TagDurationCriterion
            {
                TagId = tag.Id,
                Unit = "percent",
                Modifier = CriterionModifier.GreaterThan,
                Value = 70,
            },
        };

        var (binaryItems, binaryCount) = await repository.FindAsync(binaryFilter, new FindFilter { Page = 1, PerPage = 50, Sort = "title" }, TestContext.Current.CancellationToken);
        var (durationItems, durationCount) = await repository.FindAsync(explicitDurationFilter, new FindFilter { Page = 1, PerPage = 50, Sort = "title" }, TestContext.Current.CancellationToken);

        Assert.Equal(2, binaryCount);
        Assert.Equal(["manual-tagged", "qualifying-derived"], binaryItems.Select(video => video.Title ?? string.Empty).ToArray());
        Assert.Equal(3, durationCount);
        Assert.Contains(durationItems, video => video.Title == "below-threshold-derived");
    }

    [Fact]
    [RequiresPostgres]
    public async Task TagsCriterion_WhenSecondsOrPercentThresholdMatches_TreatsDerivedTagAsEffective()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var tag = new Tag { Name = "Running", MinOccurrenceSec = 30, MinOccurrencePercent = 80 };
        var secondsVideo = CreateVideoWithFile("seconds-match");
        var percentVideo = CreateVideoWithFile("percent-match");
        var neitherVideo = CreateVideoWithFile("neither-match");

        context.Tags.Add(tag);
        context.Videos.AddRange(secondsVideo, percentVideo, neitherVideo);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TagApplications.AddRange(
            CreateDurationApplication(secondsVideo.Id, tag.Id, totalDurationSec: 35, hostDurationSec: 100),
            CreateDurationApplication(percentVideo.Id, tag.Id, totalDurationSec: 8, hostDurationSec: 10),
            CreateDurationApplication(neitherVideo.Id, tag.Id, totalDurationSec: 20, hostDurationSec: 100));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            TagsCriterion = new MultiIdCriterion { Value = [tag.Id], Modifier = CriterionModifier.Includes },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50, Sort = "title" }, TestContext.Current.CancellationToken);

        Assert.Equal(2, totalCount);
        Assert.Equal(["percent-match", "seconds-match"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [RequiresPostgres]
    public async Task TagsCriterion_Excludes_RemovesManualAndEffectiveDerivedTagsOnly(bool excludesModifier)
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var tag = new Tag { Name = "Running", MinOccurrenceSec = 30, MinOccurrencePercent = 80 };
        var performer = CreatePerformer("Performer", null);
        var manualVideo = CreateVideoWithFile("manual");
        manualVideo.VideoTags.Add(new VideoTag { Tag = tag });
        var secondsVideo = CreateVideoWithFile("seconds-derived");
        var percentVideo = CreateVideoWithFile("percent-derived");
        var belowThresholdVideo = CreateVideoWithFile("below-threshold-derived");
        var performerContextVideo = CreateVideoWithFile("performer-context", performer: performer);
        var untaggedVideo = CreateVideoWithFile("untagged");

        context.Tags.Add(tag);
        context.Videos.AddRange(manualVideo, secondsVideo, percentVideo, belowThresholdVideo, performerContextVideo, untaggedVideo);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TagApplications.AddRange(
            CreateDurationApplication(secondsVideo.Id, tag.Id, totalDurationSec: 35, hostDurationSec: 100),
            CreateDurationApplication(percentVideo.Id, tag.Id, totalDurationSec: 8, hostDurationSec: 10),
            CreateDurationApplication(belowThresholdVideo.Id, tag.Id, totalDurationSec: 20, hostDurationSec: 100),
            CreatePerformerOccurrenceApplication(AffinityHostType.Video, performerContextVideo.Id, performer.Id, tag.Id));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var filter = new VideoFilter
        {
            TagsCriterion = excludesModifier
                ? new MultiIdCriterion { Value = [tag.Id], Modifier = CriterionModifier.Excludes }
                : new MultiIdCriterion { Value = [], Excludes = [tag.Id], Modifier = CriterionModifier.Includes },
        };

        var (items, totalCount) = await new VideoRepository(context).FindAsync(filter, new FindFilter { Page = 1, PerPage = 50, Sort = "title" }, TestContext.Current.CancellationToken);

        Assert.Equal(3, totalCount);
        Assert.Equal(["below-threshold-derived", "performer-context", "untagged"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task VideosController_GetById_MapsOnlyEffectiveDerivedTagsAsNonRemovable()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var tag = new Tag { Name = "Observed", MinOccurrencePercent = 80 };
        var video = CreateVideoWithFile("thresholded-video");

        context.Tags.Add(tag);
        context.Videos.Add(video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TagApplications.Add(CreateDurationApplication(video.Id, tag.Id, totalDurationSec: 72, hostDurationSec: 100));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = CreateVideosControllerWithRepository(context);
        var initialResponse = await controller.GetById(video.Id, CancellationToken.None);
        var initialOk = Assert.IsType<OkObjectResult>(initialResponse.Result);
        var initialVideo = Assert.IsType<VideoDto>(initialOk.Value);
        Assert.Empty(initialVideo.Tags);

        tag.MinOccurrencePercent = 70;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var updatedResponse = await controller.GetById(video.Id, CancellationToken.None);
        var updatedOk = Assert.IsType<OkObjectResult>(updatedResponse.Result);
        var updatedVideo = Assert.IsType<VideoDto>(updatedOk.Value);
        var effectiveTag = Assert.Single(updatedVideo.Tags);

        Assert.Equal(tag.Id, effectiveTag.Id);
        Assert.True(effectiveTag.IsDerived);
        Assert.False(effectiveTag.CanRemove);
        Assert.Equal(72, effectiveTag.EffectiveDurationPercent.GetValueOrDefault(), 3);
    }

    [Fact]
    [RequiresPostgres]
    public async Task VideosController_GetById_LocksDirectAiOnlyTagsAsNonRemovable()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var tag = new Tag { Name = "AI Link" };
        var video = CreateVideoWithFile("ai-link-video");
        video.VideoTags.Add(new VideoTag { Tag = tag });

        context.Videos.Add(video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TagApplications.Add(new TagApplication
        {
            HostType = AffinityHostType.Video,
            HostId = video.Id,
            TagId = tag.Id,
            SourceKey = "ext:ai.tagging",
            SourceRunId = "run-ai-link",
            ModelKey = "tagger-v1",
            Confidence = 0.9f,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = CreateVideosControllerWithRepository(context);
        var response = await controller.GetById(video.Id, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var dto = Assert.IsType<VideoDto>(ok.Value);
        var effectiveTag = Assert.Single(dto.Tags);

        Assert.Equal(tag.Id, effectiveTag.Id);
        Assert.True(effectiveTag.IsDerived);
        Assert.False(effectiveTag.CanRemove);
    }

    [Fact]
    [RequiresPostgres]
    public async Task TagVideoCount_RefreshesFromEffectiveDerivedTagsWhenThresholdChanges()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var tag = new Tag { Name = "Counted", MinOccurrencePercent = 80 };
        var video = CreateVideoWithFile("counted-video");

        context.Tags.Add(tag);
        context.Videos.Add(video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TagApplications.Add(CreateDurationApplication(video.Id, tag.Id, totalDurationSec: 82, hostDurationSec: 100));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await context.Entry(tag).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, tag.VideoCount);

        tag.MinOccurrencePercent = 90;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await context.Entry(tag).ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, tag.VideoCount);
    }

    [Fact]
    [RequiresPostgres]
    public async Task AudiosController_FindPost_UsesEffectiveDerivedTagsForAudioFiltersAndDtos()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var tag = new Tag { Name = "Audio Cue", MinOccurrenceSec = 5 };
        var matchingAudio = new Audio { Title = "matching-audio" };
        var belowThresholdAudio = new Audio { Title = "below-threshold-audio" };

        context.Tags.Add(tag);
        context.Audios.AddRange(matchingAudio, belowThresholdAudio);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.TagApplications.AddRange(
            new TagApplication
            {
                HostType = AffinityHostType.Audio,
                HostId = matchingAudio.Id,
                TagId = tag.Id,
                TotalDurationSec = 6,
                HostDurationSec = 60,
                SourceKey = "test",
            },
            new TagApplication
            {
                HostType = AffinityHostType.Audio,
                HostId = belowThresholdAudio.Id,
                TagId = tag.Id,
                TotalDurationSec = 4,
                HostDurationSec = 60,
                SourceKey = "test",
            });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = new AudiosController(context, new CustomFieldService(context), null!, null!, null!);
        var response = await controller.FindPost(new FilteredQueryRequest<AudioFilter>
        {
            FindFilter = new FindFilter { Page = 1, PerPage = 50, Sort = "title" },
            ObjectFilter = new AudioFilter
            {
                TagsCriterion = new MultiIdCriterion { Value = [tag.Id], Modifier = CriterionModifier.Includes },
            },
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<PaginatedResponse<AudioDto>>(ok.Value);
        var audio = Assert.Single(payload.Items);

        Assert.Equal("matching-audio", audio.Title);
        // A list entry carries its tag ids and leaves the tags themselves to the tag lookup.
        Assert.Equal([tag.Id], audio.TagIds);
        Assert.Empty(audio.Tags);

        var detail = Assert.IsType<AudioDto>(Assert.IsType<OkObjectResult>((await controller.GetById(matchingAudio.Id, CancellationToken.None)).Result).Value);
        var detailTag = Assert.Single(detail.Tags);
        Assert.True(detailTag.IsDerived);
        Assert.False(detailTag.CanRemove);
        Assert.Equal("test", Assert.Single(detailTag.Provenance!).SourceKey);
        Assert.Equal(6, detailTag.EffectiveDurationSec.GetValueOrDefault(), 3);
    }

    [Fact]
    [RequiresPostgres]
    public async Task HashAndChecksumCriteria_FilterVideoFingerprints()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Videos.AddRange(
            CreateVideoWithFile(
                "matching-hashes",
                fingerprints:
                [
                    new FileFingerprint { Type = "oshash", Value = "osh-match" },
                    new FileFingerprint { Type = "md5", Value = "md5-match" },
                ]),
            CreateVideoWithFile(
                "other-hashes",
                fingerprints:
                [
                    new FileFingerprint { Type = "oshash", Value = "osh-other" },
                    new FileFingerprint { Type = "md5", Value = "md5-other" },
                ]));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);

        var (hashItems, hashCount) = await repository.FindAsync(new VideoFilter
            {
                HashCriterion = new StringCriterion
                {
                    Value = "osh-match",
                    Modifier = CriterionModifier.Equals,
                },
            }, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

        var (checksumItems, checksumCount) = await repository.FindAsync(new VideoFilter
            {
                ChecksumCriterion = new StringCriterion
                {
                    Value = "md5-match",
                    Modifier = CriterionModifier.Equals,
                },
            }, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

        Assert.Equal(1, hashCount);
        Assert.Equal(["matching-hashes"], hashItems.Select(video => video.Title ?? string.Empty).ToArray());
        Assert.Equal(1, checksumCount);
        Assert.Equal(["matching-hashes"], checksumItems.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task FingerprintCriterion_FiltersVideosBySelectedAlgorithm()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Videos.AddRange(
            CreateVideoWithFile(
                "matching-fingerprint-types",
                fingerprints:
                [
                    new FileFingerprint { Type = "oshash", Value = "osh-match" },
                    new FileFingerprint { Type = "md5", Value = "md5-match" },
                    new FileFingerprint { Type = "phash", Value = "phash-match" },
                ]),
            CreateVideoWithFile(
                "other-fingerprint-types",
                fingerprints:
                [
                    new FileFingerprint { Type = "oshash", Value = "osh-other" },
                    new FileFingerprint { Type = "md5", Value = "md5-other" },
                    new FileFingerprint { Type = "phash", Value = "phash-other" },
                ]));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);

        var (oshashItems, oshashCount) = await repository.FindAsync(new VideoFilter
            {
                FingerprintCriterion = new FingerprintCriterion
                {
                    Type = "oshash",
                    Value = "osh-match",
                    Modifier = CriterionModifier.Equals,
                },
            }, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

        var (md5Items, md5Count) = await repository.FindAsync(new VideoFilter
            {
                FingerprintCriterion = new FingerprintCriterion
                {
                    Type = "md5",
                    Value = "md5-match",
                    Modifier = CriterionModifier.Equals,
                },
            }, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

        var (phashItems, phashCount) = await repository.FindAsync(new VideoFilter
            {
                FingerprintCriterion = new FingerprintCriterion
                {
                    Type = "phash",
                    Value = "phash-match",
                    Modifier = CriterionModifier.Equals,
                },
            }, new FindFilter { Page = 1, PerPage = 50 }, TestContext.Current.CancellationToken);

        Assert.Equal(1, oshashCount);
        Assert.Equal(["matching-fingerprint-types"], oshashItems.Select(video => video.Title ?? string.Empty).ToArray());
        Assert.Equal(1, md5Count);
        Assert.Equal(["matching-fingerprint-types"], md5Items.Select(video => video.Title ?? string.Empty).ToArray());
        Assert.Equal(1, phashCount);
        Assert.Equal(["matching-fingerprint-types"], phashItems.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public void VideoFiles_HaveCoveringIndexForFingerprintJoins()
    {
        using var context = CreateModelContext();

        // The runtime model drops design-time annotations such as the index INCLUDE columns.
        var entityType = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(VideoFile));
        var index = Assert.Single(entityType!.GetIndexes(), candidate => candidate.GetDatabaseName() == "IX_files_Id_VideoId_video");

        Assert.Equal([nameof(VideoFile.Id)], index.Properties.Select(property => property.Name));
        Assert.Equal("\"VideoId\" IS NOT NULL", index.GetFilter());
        Assert.Equal([nameof(VideoFile.VideoId)], Assert.IsType<string[]>(index.FindAnnotation("Npgsql:IndexInclude")!.Value));
    }

    [Fact]
    [RequiresPostgres]
    public async Task HasSegmentsCriterion_FiltersVideosByRawSegmentPresence()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var withSegments = CreateVideoWithFile("with-segments");
        var withoutSegments = CreateVideoWithFile("without-segments");
        context.Videos.AddRange(withSegments, withoutSegments);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Segments.AddRange(
            new Segment
            {
                HostType = SegmentHostType.Video,
                HostId = withSegments.Id,
                StartSec = 1,
                EndSec = 2,
                SourceKey = "user",
            },
            new Segment
            {
                HostType = SegmentHostType.Image,
                HostId = withoutSegments.Id,
                StartSec = 1,
                EndSec = 2,
                SourceKey = "user",
            });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);

        var (withSegmentItems, withSegmentCount) = await repository.FindAsync(new VideoFilter { HasSegmentsCriterion = new BoolCriterion { Value = true } }, new FindFilter { Page = 1, PerPage = 50, Sort = "title" }, TestContext.Current.CancellationToken);

        var (withoutSegmentItems, withoutSegmentCount) = await repository.FindAsync(new VideoFilter { HasSegmentsCriterion = new BoolCriterion { Value = false } }, new FindFilter { Page = 1, PerPage = 50, Sort = "title" }, TestContext.Current.CancellationToken);

        Assert.Equal(1, withSegmentCount);
        Assert.Equal(["with-segments"], withSegmentItems.Select(video => video.Title ?? string.Empty).ToArray());
        Assert.Equal(1, withoutSegmentCount);
        Assert.Equal(["without-segments"], withoutSegmentItems.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task DuplicatedPhashCriterion_True_FindsVideosSharingAPhashAcrossVideos()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Videos.AddRange(
            CreateVideoWithFile("duplicate-a", fingerprints: [new FileFingerprint { Type = "phash", Value = "same-phash" }]),
            CreateVideoWithFile("duplicate-b", fingerprints: [new FileFingerprint { Type = "phash", Value = "same-phash" }]),
            CreateVideoWithFile("unique", fingerprints: [new FileFingerprint { Type = "phash", Value = "unique-phash" }]));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);
        var filter = new VideoFilter
        {
            DuplicatedPhashCriterion = new BoolCriterion { Value = true },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50, Sort = "title" }, TestContext.Current.CancellationToken);

        Assert.Equal(2, totalCount);
        Assert.Equal(["duplicate-a", "duplicate-b"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task VideosController_Find_BindsSeedFromQuery()
    {
        var repository = new CapturingVideoRepository();
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var controller = new VideosController(repository, context, null!, null!, null!, memoryCache, null!, null!, new NoOpUserEngagementService(), new CustomFieldService(context), new EventBus());

        await controller.Find(q: null, page: 1, perPage: 25, sort: "random", direction: "desc", seed: 12345, ct: TestContext.Current.CancellationToken);

        Assert.Equal(12345, repository.LastFindFilter?.Seed);
        Assert.Equal("random", repository.LastFindFilter?.Sort);
        Assert.Equal(Cove.Core.Enums.SortDirection.Desc, repository.LastFindFilter?.Direction);
    }

    [Fact]
    [RequiresPostgres]
    public async Task VideosController_DeleteChecksPhysicalFilePermissionBeforeEntityLookup()
    {
        var principals = new CurrentPrincipalAccessor();
        principals.Set(new CovePrincipal
        {
            UserId = 1,
            Username = "record-delete-only",
            Kind = PrincipalKind.User,
            Permissions = new HashSet<string> { Permissions.VideosDelete },
            Roles = new HashSet<string>(),
        });
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = new CoveContext(database.CreateOptions(), principals);
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var controller = new VideosController(
            new VideoRepository(context),
            context,
            null!,
            null!,
            null!,
            memoryCache,
            null!,
            null!,
            new NoOpUserEngagementService(),
            new CustomFieldService(context),
            new EventBus(),
            principalAccessor: principals);

        var result = await controller.Delete(999, deleteFile: true, ct: TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    [RequiresPostgres]
    public async Task VideosController_Find_BindsOrderedSortClausesFromQuery()
    {
        var repository = new CapturingVideoRepository();
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var controller = new VideosController(repository, context, null!, null!, null!, memoryCache, null!, null!, new NoOpUserEngagementService(), new CustomFieldService(context), new EventBus());

        await controller.Find(q: null, page: 1, perPage: 25, sort: null, direction: null, seed: null, sorts: "studio:asc,date:desc", ct: TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                new SortClause("studio", Cove.Core.Enums.SortDirection.Asc),
                new SortClause("date", Cove.Core.Enums.SortDirection.Desc),
            ],
            repository.LastFindFilter?.Sorts);
    }

    [Fact]
    [RequiresPostgres]
    public async Task VideoRepository_SortsByStudioThenDate()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var alphaStudio = new Studio { Name = "Alpha" };
        var betaStudio = new Studio { Name = "Beta" };

        var alphaOld = CreateVideoWithFile("alpha-old");
        alphaOld.Studio = alphaStudio;
        alphaOld.Date = new DateOnly(2024, 1, 1);
        var alphaNew = CreateVideoWithFile("alpha-new");
        alphaNew.Studio = alphaStudio;
        alphaNew.Date = new DateOnly(2024, 2, 1);
        var betaOld = CreateVideoWithFile("beta-old");
        betaOld.Studio = betaStudio;
        betaOld.Date = new DateOnly(2024, 1, 1);
        var betaNew = CreateVideoWithFile("beta-new");
        betaNew.Studio = betaStudio;
        betaNew.Date = new DateOnly(2024, 2, 1);

        context.Videos.AddRange(betaOld, alphaOld, betaNew, alphaNew);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);
        var (items, totalCount) = await repository.FindAsync(null, new FindFilter
        {
            Page = 1,
            PerPage = 20,
            Sorts =
            [
                new SortClause("studio", Cove.Core.Enums.SortDirection.Asc),
                null!,
                new SortClause("date", Cove.Core.Enums.SortDirection.Desc),
            ],
        }, TestContext.Current.CancellationToken);

        Assert.Equal(4, totalCount);
        Assert.Equal(
            ["alpha-new", "alpha-old", "beta-new", "beta-old"],
            items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    [Theory]
    [InlineData("title", false)]
    [InlineData("title", true)]
    [InlineData("date", false)]
    [InlineData("date", true)]
    [InlineData("production_date", false)]
    [InlineData("production_date", true)]
    [InlineData("rating", false)]
    [InlineData("rating", true)]
    [InlineData("created_at", false)]
    [InlineData("created_at", true)]
    [InlineData("duration", false)]
    [InlineData("duration", true)]
    [InlineData("bitrate", false)]
    [InlineData("bitrate", true)]
    [InlineData("updated_at", false)]
    [InlineData("updated_at", true)]
    [InlineData("custom:number:missing", false)]
    [InlineData("custom:number:missing", true)]
    [RequiresPostgres]
    public async Task VideosWithCompilations_PageTiedRowsByUniqueKindAndId(string sort, bool descending)
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var timestamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var id in new[] { 3, 1 })
        {
            context.Videos.Add(new Video { Id = id, Title = "Tied", CreatedAt = timestamp, UpdatedAt = timestamp });
            context.Groups.Add(new Group
            {
                Id = id, Name = "Tied", CreatedAt = timestamp, UpdatedAt = timestamp,
                ShowInVideoLists = true,
                GroupItems = [new GroupItem { Kind = GroupItemKind.Video, VideoId = id, HostId = id }],
            });
        }
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var controller = CreateVideosController(context);

        for (var pass = 0; pass < 2; pass++)
        {
            var keys = new List<(string Kind, int Id)>();
            for (var page = 1; page <= 4; page++)
            {
                var result = await controller.FindWithCompilations(
                    q: null, page: page, perPage: 1, sort: sort,
                    direction: descending ? "desc" : "asc", ct: TestContext.Current.CancellationToken);
                var response = Assert.IsType<PaginatedResponse<VideoListEntryDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
                Assert.Equal(4, response.TotalCount);
                var item = Assert.Single(response.Items);
                keys.Add((item.Kind, item.Id));
            }
            Assert.Equal(4, keys.Distinct().Count());
            (string, int)[] ascending = [("compilation", 1), ("compilation", 3), ("video", 1), ("video", 3)];
            Assert.Equal(descending ? ascending.Reverse() : ascending, keys);
        }
    }

    [Fact]
    [RequiresPostgres]
    public async Task VideosController_FindWithCompilations_ReturnsVideoRangeGroupsAsPagedRows()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var video = CreateVideoWithFile("video row");
        video.CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        video.UpdatedAt = video.CreatedAt;
        context.Videos.Add(video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Groups.AddRange(
            new Group
            {
                Name = "compilation row",
                CreatedAt = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                ShowInVideoLists = true,
                GroupItems = [new GroupItem { Kind = GroupItemKind.VideoRange, VideoId = video.Id, HostId = video.Id, StartSec = 10, EndSec = 20 }],
            },
            new Group
            {
                Name = "ordinary video group",
                CreatedAt = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                ShowInVideoLists = true,
                GroupItems = [new GroupItem { Kind = GroupItemKind.Video, VideoId = video.Id, HostId = video.Id }],
            },
            new Group
            {
                Name = "hidden compilation",
                CreatedAt = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                ShowInVideoLists = false,
                GroupItems = [new GroupItem { Kind = GroupItemKind.VideoRange, VideoId = video.Id, HostId = video.Id, StartSec = 20, EndSec = 30 }],
            });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var controller = CreateVideosController(context);

        var response = await controller.FindWithCompilations(q: null, page: 1, perPage: 10, sort: "created_at", direction: "desc", seed: null, title: null, rating: null, organized: null, studioId: null, groupId: null, galleryId: null, tagIds: null, performerIds: null, ct: TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<PaginatedResponse<VideoListEntryDto>>(ok.Value);

        Assert.Equal(3, payload.TotalCount);
        Assert.Equal(["compilation", "compilation", "video"], payload.Items.Select(item => item.Kind).ToArray());
        Assert.Equal("ordinary video group", payload.Items[0].Group?.Name);
        Assert.True(payload.Items[0].Group?.IsCompilation);
        Assert.Equal("compilation row", payload.Items[1].Group?.Name);
        Assert.True(payload.Items[1].Group?.IsCompilation);
        Assert.Equal("video row", payload.Items[2].Video?.Title);
    }

    [Fact]
    [RequiresPostgres]
    public async Task DuplicateSearch_ExactFingerprint_UsesMd5AndOshash()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Videos.AddRange(
            CreateVideoWithFile("md5 duplicate a", basename: "a.mp4", fingerprints: [new FileFingerprint { Type = "md5", Value = "same-md5" }]),
            CreateVideoWithFile("md5 duplicate b", basename: "b.mp4", fingerprints: [new FileFingerprint { Type = "md5", Value = "same-md5" }]),
            CreateVideoWithFile("oshash duplicate a", basename: "c.mp4", fingerprints: [new FileFingerprint { Type = "oshash", Value = "same-oshash" }]),
            CreateVideoWithFile("oshash duplicate b", basename: "d.mp4", fingerprints: [new FileFingerprint { Type = "oshash", Value = "same-oshash" }]),
            CreateVideoWithFile("unique", basename: "e.mp4", fingerprints: [new FileFingerprint { Type = "md5", Value = "unique-md5" }]));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var groups = await ExecuteDuplicateSearchAsync(context, "fingerprint");
        Assert.Contains(groups, group => group.Select(video => video.Title ?? "").OrderBy(title => title).SequenceEqual(["md5 duplicate a", "md5 duplicate b"]));
        Assert.Contains(groups, group => group.Select(video => video.Title ?? "").OrderBy(title => title).SequenceEqual(["oshash duplicate a", "oshash duplicate b"]));
        Assert.DoesNotContain(groups.SelectMany(group => group), video => video.Title == "unique");
    }

    [Fact]
    [RequiresPostgres]
    public async Task DuplicateSearch_Phash_UsesDistanceAndDurationTolerance()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Videos.AddRange(
            CreateVideoWithFile("visual duplicate a", basename: "a.mp4", fingerprints: [new FileFingerprint { Type = "phash", Value = "0000000000000000" }]),
            CreateVideoWithFile("visual duplicate b", basename: "b.mp4", fingerprints: [new FileFingerprint { Type = "phash", Value = "0000000000000001" }]),
            CreateVideoWithFile("different visual", basename: "c.mp4", fingerprints: [new FileFingerprint { Type = "phash", Value = "ffffffffffffffff" }]));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var groups = await ExecuteDuplicateSearchAsync(context, "phash", distance: 1, durationDiff: 0);
        var group = Assert.Single(groups);
        Assert.Equal(["visual duplicate a", "visual duplicate b"], group.Select(video => video.Title ?? "").OrderBy(title => title).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task LastPlayedAtSort_Descending_PutsPlayedVideosBeforeUnplayedVideos()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        var neverPlayed = new Video { Title = "never-played" };
        var olderPlay = new Video { Title = "older-play" };
        var recentPlay = new Video { Title = "recent-play" };
        context.Videos.AddRange(neverPlayed, olderPlay, recentPlay);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.UserEntityAffinities.AddRange(
            new UserEntityAffinity { UserId = 1, HostType = AffinityHostType.Video, HostId = olderPlay.Id, LastConsumedAt = new DateTime(2024, 1, 10, 8, 0, 0, DateTimeKind.Utc) },
            new UserEntityAffinity { UserId = 1, HostType = AffinityHostType.Video, HostId = recentPlay.Id, LastConsumedAt = new DateTime(2024, 1, 12, 8, 0, 0, DateTimeKind.Utc) });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new VideoRepository(context);

        var (items, totalCount) = await repository.FindAsync(filter: null, new FindFilter
            {
                Page = 1,
                PerPage = 50,
                Sort = "last_played_at",
                Direction = Cove.Core.Enums.SortDirection.Desc,
            }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, totalCount);
        Assert.Equal(["recent-play", "older-play", "never-played"], items.Select(video => video.Title ?? string.Empty).ToArray());
    }

    // Folder paths are unique, so each path gets one folder per test. Its explicit id lets the context work out
    // the paths of new files in the same save, which it looks up by ParentFolderId.
    private Folder FolderAt(string path)
    {
        var key = path.Replace('\\', '/');
        if (!_folders.TryGetValue(key, out var folder))
            _folders[key] = folder = new Folder { Id = _folders.Count + 1, Path = path, ModTime = DateTime.UtcNow };
        return folder;
    }

    private Video CreateVideoWithFile(
        string title,
        string? director = null,
        DateOnly? videoDate = null,
        string folderPath = @"C:\library",
        string? basename = null,
        string audioCodec = "AAC",
        string videoCodec = "H264",
        long bitRate = 1_000_000,
        Performer? performer = null,
        IEnumerable<FileFingerprint>? fingerprints = null)
    {
        // A folder holds each file name once.
        basename ??= $"clip-{++_defaultFileNames}.mp4";
        var video = new Video
        {
            Title = title,
            Director = director,
            Date = videoDate ?? new DateOnly(2024, 1, 1),
        };

        var file = new VideoFile
        {
            Basename = basename,
            ParentFolder = FolderAt(folderPath),
            AudioCodec = audioCodec,
            VideoCodec = videoCodec,
            BitRate = bitRate,
            FrameRate = 30,
            Duration = 120,
            Width = 1920,
            Height = 1080,
            Format = "mp4",
            Size = 1024,
            ModTime = DateTime.UtcNow,
        };

        if (fingerprints != null)
        {
            foreach (var fingerprint in fingerprints)
            {
                file.Fingerprints.Add(fingerprint);
            }
        }

        video.Files.Add(file);

        if (performer != null)
        {
            video.VideoPerformers.Add(new VideoPerformer { Performer = performer });
        }

        return video;
    }

    private static Performer CreatePerformer(string name, DateOnly? birthdate, params Tag[] tags)
    {
        var performer = new Performer
        {
            Name = name,
            Birthdate = birthdate,
        };

        foreach (var tag in tags)
        {
            performer.PerformerTags.Add(new PerformerTag { Performer = performer, Tag = tag });
        }

        return performer;
    }

    private static TagApplication CreateDurationApplication(int videoId, int tagId, double totalDurationSec, double hostDurationSec)
        => new()
        {
            HostType = AffinityHostType.Video,
            HostId = videoId,
            TagId = tagId,
            TotalDurationSec = totalDurationSec,
            HostDurationSec = hostDurationSec,
            SourceKey = "test",
        };

    private static TagApplication CreatePerformerOccurrenceApplication(AffinityHostType hostType, int hostId, int performerId, int tagId)
        => new()
        {
            HostType = hostType,
            HostId = hostId,
            ContextType = "performer",
            ContextId = performerId,
            TagId = tagId,
            SourceKey = "test",
        };

    private static async Task<(Tag Parent, Tag Child, Tag Grandchild)> AddTagChainAsync(CoveContext context, string prefix)
    {
        var parent = new Tag { Name = $"{prefix}-parent" };
        var child = new Tag { Name = $"{prefix}-child" };
        var grandchild = new Tag { Name = $"{prefix}-grandchild" };
        context.Tags.AddRange(parent, child, grandchild);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.Set<TagParent>().AddRange(
            new TagParent { ParentId = parent.Id, ChildId = child.Id },
            new TagParent { ParentId = child.Id, ChildId = grandchild.Id });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (parent, child, grandchild);
    }

    private static Image CreateImage(string title, Performer? performer = null)
    {
        var image = new Image
        {
            Title = title,
        };

        if (performer != null)
        {
            image.ImagePerformers.Add(new ImagePerformer { Performer = performer });
        }

        return image;
    }

    private static Audio CreateAudio(string title, Performer? performer = null)
    {
        var audio = new Audio
        {
            Title = title,
        };

        if (performer != null)
        {
            audio.AudioPerformers.Add(new AudioPerformer { Performer = performer });
        }

        return audio;
    }

    private static TextDocument CreateTextDocument(string title, Performer? performer = null)
    {
        var textDocument = new TextDocument
        {
            Title = title,
        };

        if (performer != null)
        {
            textDocument.TextPerformers.Add(new TextPerformer { Performer = performer });
        }

        return textDocument;
    }

    private static BaseEntity AddImageHost(CoveContext context, Image image)
    {
        context.Images.Add(image);
        return image;
    }

    private static BaseEntity AddAudioHost(CoveContext context, Audio audio)
    {
        context.Audios.Add(audio);
        return audio;
    }

    private static BaseEntity AddTextHost(CoveContext context, TextDocument textDocument)
    {
        context.TextDocuments.Add(textDocument);
        return textDocument;
    }

    private static async Task FavoritePerformerAsync(CoveContext context, Performer performer)
    {
        context.UserEntityAffinities.Add(new UserEntityAffinity { UserId = 1, HostType = AffinityHostType.Performer, HostId = performer.Id, IsFavorite = true });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<CoveContext> CreateContextAsync(CovePostgresDatabase database)
    {
        // The signed-in user owns the favorites and play history some tests record, so its row has to exist.
        await using (var seed = database.CreateContext())
        {
            seed.Users.Add(new User { Id = 1, Username = "test-user", PasswordHash = "test" });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await database.SyncIdentitySequencesAsync(TestContext.Current.CancellationToken);

        // CurrentPrincipalAccessor keeps the principal in an AsyncLocal, which does not flow out of this async method.
        var principalAccessor = new FixedPrincipalAccessor(new CovePrincipal
        {
            UserId = 1,
            Username = "test-user",
            Kind = PrincipalKind.User,
            Permissions = new HashSet<string> { "*" },
            Roles = new HashSet<string>(),
        });

        return new CoveContext(database.CreateOptions(), principalAccessor);
    }

    private sealed class FixedPrincipalAccessor(CovePrincipal principal) : ICurrentPrincipalAccessor
    {
        public CovePrincipal? Current => principal;

        public void Set(CovePrincipal? value) => throw new NotSupportedException();
    }

    // Builds the PostgreSQL model without connecting, for tests that only inspect it.
    private static CoveContext CreateModelContext()
        => new(new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=model_only;Password=model_only", npgsql => npgsql.UseVector())
            .Options);

    private static VideosController CreateVideosController(CoveContext context)
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        return new VideosController(new CapturingVideoRepository(), context, null!, null!, null!, memoryCache, null!, null!, new NoOpUserEngagementService(), new CustomFieldService(context), new EventBus());
    }

    private static VideosController CreateVideosControllerWithRepository(CoveContext context)
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        return new VideosController(new VideoRepository(context), context, null!, null!, null!, memoryCache, null!, null!, new NoOpUserEngagementService(), new CustomFieldService(context), new EventBus());
    }

    private static async Task<List<List<Video>>> ExecuteDuplicateSearchAsync(
        CoveContext context,
        string matchType,
        int distance = 0,
        double durationDiff = 10)
    {
        var search = new DuplicateSearch
        {
            MatchType = matchType,
            Distance = distance,
            DurationDifference = durationDiff,
        };
        context.DuplicateSearches.Add(search);
        await context.SaveChangesAsync();
        var candidateIds = await context.Videos.Select(video => video.Id).ToArrayAsync();
        var service = new DuplicateSearchExecutionService(
            context,
            new InlineJobService(),
            new CoveConfiguration { MaxParallelTasks = 2 });
        await service.ExecuteAsync(search.Id, candidateIds, new InlineProgress(), CancellationToken.None);

        var groups = await context.DuplicateSearchGroups
            .Where(group => group.SearchId == search.Id)
            .Include(group => group.Items)
            .OrderBy(group => group.Position)
            .AsNoTracking()
            .ToListAsync();
        var videos = await context.Videos.AsNoTracking().ToDictionaryAsync(video => video.Id);
        return groups.Select(group => group.Items.Select(item => videos[item.VideoId]).ToList()).ToList();
    }

    private sealed class InlineJobService : IJobService
    {
        public string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true) => "inline";
        public bool Cancel(string jobId) => false;
        public bool ReorderQueued(string jobId, string? beforeJobId) => false;
        public Cove.Core.Interfaces.JobInfo? GetJob(string jobId) => null;
        public IReadOnlyList<Cove.Core.Interfaces.JobInfo> GetAllJobs() => [];
        public IReadOnlyList<Cove.Core.Interfaces.JobInfo> GetJobHistory() => [];
    }

    private sealed class InlineProgress : IJobProgress
    {
        public void Report(double progress, string? subTask = null) { }
    }

    private sealed class CapturingVideoRepository : IVideoRepository
    {
        public FindFilter? LastFindFilter { get; private set; }

        public Task<(IReadOnlyList<Video> Items, int TotalCount)> FindAsync(VideoFilter? filter, FindFilter? findFilter, CancellationToken ct = default, FilterExpression<VideoFilter>? expression = null)
        {
            LastFindFilter = findFilter;
            return Task.FromResult<(IReadOnlyList<Video>, int)>((Array.Empty<Video>(), 0));
        }

        public Task<VideoAggregate> AggregateAsync(VideoFilter? filter, FindFilter? findFilter, CancellationToken ct = default, FilterExpression<VideoFilter>? expression = null)
            => Task.FromResult(new VideoAggregate(0, 0, 0));

        public Task<Video?> GetByIdAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Video>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Video> AddAsync(Video entity, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Video entity, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> CountAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<VideoPerformer>> GetVideoPerformersAsync(IReadOnlyList<int> videoIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Video?> GetByIdWithRelationsAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
