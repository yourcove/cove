using Cove.Core.Interfaces;
using Cove.Data.Repositories;
using Cove.PerformanceTests.Infrastructure;

namespace Cove.PerformanceTests;

[Collection("performance")]
[Trait("Category", "Performance")]
public sealed class RepositoryPerformanceRegressionTests(PostgresPerformanceFixture fixture)
{
    public static IEnumerable<object[]> RepositoryBudgets()
    {
        yield return [new RepositoryPerformanceBudget(
            Name: "video_sort_duration",
            MaxMeanMs: 140,
            MaxP95Ms: 220,
            Operation: static async (performanceFixture, ct) =>
            {
                await using var context = performanceFixture.CreateContext();
                var repository = new VideoRepository(context);
                _ = await repository.FindAsync(null, Desc("duration"), ct);
            })];

        yield return [new RepositoryPerformanceBudget(
            Name: "video_find_stack5_duration",
            MaxMeanMs: 170,
            MaxP95Ms: 260,
            Operation: static async (performanceFixture, ct) =>
            {
                await using var context = performanceFixture.CreateContext();
                var repository = new VideoRepository(context);
                _ = await repository.FindAsync(
                    new VideoFilter
                    {
                        DurationCriterion = GreaterThan(300),
                        ResolutionCriterion = GreaterThan(720),
                        Organized = false,
                        FrameRateCriterion = GreaterThan(24),
                        BitrateInterval = GreaterThan(5_000),
                    },
                    Desc("duration"),
                    ct);
            })];

        yield return [new RepositoryPerformanceBudget(
            Name: "video_find_has_segments",
            MaxMeanMs: 140,
            MaxP95Ms: 220,
            Operation: static async (performanceFixture, ct) =>
            {
                await using var context = performanceFixture.CreateContext();
                var repository = new VideoRepository(context);
                _ = await repository.FindAsync(
                    new VideoFilter
                    {
                        HasSegmentsCriterion = new BoolCriterion { Value = true },
                    },
                    Desc("date"),
                    ct);
            })];

        yield return [new RepositoryPerformanceBudget(
            Name: "video_find_without_segments",
            MaxMeanMs: 140,
            MaxP95Ms: 220,
            Operation: static async (performanceFixture, ct) =>
            {
                await using var context = performanceFixture.CreateContext();
                var repository = new VideoRepository(context);
                _ = await repository.FindAsync(
                    new VideoFilter
                    {
                        HasSegmentsCriterion = new BoolCriterion { Value = false },
                    },
                    Desc("date"),
                    ct);
            })];

        yield return [new RepositoryPerformanceBudget(
            Name: "video_sort_path",
            MaxMeanMs: 130,
            MaxP95Ms: 210,
            Operation: static async (performanceFixture, ct) =>
            {
                await using var context = performanceFixture.CreateContext();
                var repository = new VideoRepository(context);
                _ = await repository.FindAsync(null, Asc("path"), ct);
            })];

        yield return [new RepositoryPerformanceBudget(
            Name: "video_detail_relations",
            MaxMeanMs: 90,
            MaxP95Ms: 140,
            Operation: static async (performanceFixture, ct) =>
            {
                await using var context = performanceFixture.CreateContext();
                var repository = new VideoRepository(context);
                _ = await repository.GetByIdWithRelationsAsync(performanceFixture.SampleVideoId, ct);
            })];

        yield return [new RepositoryPerformanceBudget(
            Name: "image_sort_filesize",
            MaxMeanMs: 130,
            MaxP95Ms: 210,
            Operation: static async (performanceFixture, ct) =>
            {
                await using var context = performanceFixture.CreateContext();
                var repository = new ImageRepository(context);
                _ = await repository.FindAsync(null, Desc("file_size"), ct);
            })];

        yield return [new RepositoryPerformanceBudget(
            Name: "image_find_organized_resolution",
            MaxMeanMs: 135,
            MaxP95Ms: 210,
            Operation: static async (performanceFixture, ct) =>
            {
                await using var context = performanceFixture.CreateContext();
                var repository = new ImageRepository(context);
                _ = await repository.FindAsync(
                    new ImageFilter
                    {
                        Organized = false,
                        ResolutionCriterion = GreaterThan(720),
                    },
                    Desc("file_size"),
                    ct);
            })];

        yield return [new RepositoryPerformanceBudget(
            Name: "image_find_orientation_performer",
            MaxMeanMs: 145,
            MaxP95Ms: 225,
            Operation: static async (performanceFixture, ct) =>
            {
                await using var context = performanceFixture.CreateContext();
                var repository = new ImageRepository(context);
                _ = await repository.FindAsync(
                    new ImageFilter
                    {
                        OrientationCriterion = Equals("landscape"),
                        PerformerCountCriterion = GreaterThan(1),
                    },
                    Desc("file_mod_time"),
                    ct);
            })];

        yield return [new RepositoryPerformanceBudget(
            Name: "image_detail_relations",
            MaxMeanMs: 70,
            MaxP95Ms: 110,
            Operation: static async (performanceFixture, ct) =>
            {
                await using var context = performanceFixture.CreateContext();
                var repository = new ImageRepository(context);
                _ = await repository.GetByIdWithRelationsAsync(performanceFixture.SampleImageId, ct);
            })];

        yield return [new RepositoryPerformanceBudget(
            Name: "performer_sort_video_count",
            MaxMeanMs: 90,
            MaxP95Ms: 150,
            Operation: static async (performanceFixture, ct) =>
            {
                await using var context = performanceFixture.CreateContext();
                var repository = new PerformerRepository(context);
                _ = await repository.FindAsync(null, Desc("video_count"), ct);
            })];

        yield return [new RepositoryPerformanceBudget(
            Name: "performer_find_rating_video_count",
            MaxMeanMs: 95,
            MaxP95Ms: 150,
            Operation: static async (performanceFixture, ct) =>
            {
                await using var context = performanceFixture.CreateContext();
                var repository = new PerformerRepository(context);
                _ = await repository.FindAsync(
                    new PerformerFilter
                    {
                        RatingCriterion = GreaterThan(60),
                        VideoCountCriterion = GreaterThan(1),
                    },
                    Desc("video_count"),
                    ct);
            })];

        yield return [new RepositoryPerformanceBudget(
            Name: "tag_sort_video_count",
            MaxMeanMs: 80,
            MaxP95Ms: 130,
            Operation: static async (performanceFixture, ct) =>
            {
                await using var context = performanceFixture.CreateContext();
                var repository = new TagRepository(context);
                _ = await repository.FindAsync(null, Desc("video_count"), ct);
            })];

        yield return [new RepositoryPerformanceBudget(
            Name: "tag_find_favorite_counts",
            MaxMeanMs: 80,
            MaxP95Ms: 130,
            Operation: static async (performanceFixture, ct) =>
            {
                await using var context = performanceFixture.CreateContext();
                var repository = new TagRepository(context);
                _ = await repository.FindAsync(
                    new TagFilter
                    {
                        Favorite = true,
                        VideoCountCriterion = GreaterThan(1),
                    },
                    Desc("video_count"),
                    ct);
            })];

        yield return [new RepositoryPerformanceBudget(
            Name: "tag_detail_relations",
            MaxMeanMs: 60,
            MaxP95Ms: 100,
            Operation: static async (performanceFixture, ct) =>
            {
                await using var context = performanceFixture.CreateContext();
                var repository = new TagRepository(context);
                _ = await repository.GetByIdWithRelationsAsync(performanceFixture.SampleTagId, ct);
            })];

        yield return [new RepositoryPerformanceBudget(
            Name: "studio_sort_image_count",
            MaxMeanMs: 90,
            MaxP95Ms: 140,
            Operation: static async (performanceFixture, ct) =>
            {
                await using var context = performanceFixture.CreateContext();
                var repository = new StudioRepository(context);
                _ = await repository.FindAsync(null, Desc("image_count"), ct);
            })];

        yield return [new RepositoryPerformanceBudget(
            Name: "studio_find_rating_images",
            MaxMeanMs: 90,
            MaxP95Ms: 140,
            Operation: static async (performanceFixture, ct) =>
            {
                await using var context = performanceFixture.CreateContext();
                var repository = new StudioRepository(context);
                _ = await repository.FindAsync(
                    new StudioFilter
                    {
                        RatingCriterion = GreaterThan(50),
                        ImageCountCriterion = GreaterThan(1),
                    },
                    Desc("image_count"),
                    ct);
            })];

        yield return [new RepositoryPerformanceBudget(
            Name: "gallery_sort_rating",
            MaxMeanMs: 95,
            MaxP95Ms: 150,
            Operation: static async (performanceFixture, ct) =>
            {
                await using var context = performanceFixture.CreateContext();
                var repository = new GalleryRepository(context);
                _ = await repository.FindAsync(null, Desc("rating"), ct);
            })];

        yield return [new RepositoryPerformanceBudget(
            Name: "gallery_find_organized_rating",
            MaxMeanMs: 95,
            MaxP95Ms: 150,
            Operation: static async (performanceFixture, ct) =>
            {
                await using var context = performanceFixture.CreateContext();
                var repository = new GalleryRepository(context);
                _ = await repository.FindAsync(
                    new GalleryFilter
                    {
                        Organized = false,
                        RatingCriterion = GreaterThan(50),
                    },
                    Desc("rating"),
                    ct);
            })];

        yield return [new RepositoryPerformanceBudget(
            Name: "group_sort_date",
            MaxMeanMs: 75,
            MaxP95Ms: 120,
            Operation: static async (performanceFixture, ct) =>
            {
                await using var context = performanceFixture.CreateContext();
                var repository = new GroupRepository(context);
                _ = await repository.FindAsync(null, Desc("date"), ct);
            })];

        yield return [new RepositoryPerformanceBudget(
            Name: "group_find_rating_duration",
            MaxMeanMs: 80,
            MaxP95Ms: 125,
            Operation: static async (performanceFixture, ct) =>
            {
                await using var context = performanceFixture.CreateContext();
                var repository = new GroupRepository(context);
                _ = await repository.FindAsync(
                    new GroupFilter
                    {
                        RatingCriterion = GreaterThan(50),
                        DurationCriterion = GreaterThan(600),
                    },
                    Desc("date"),
                    ct);
            })];
    }

    [Theory]
    [MemberData(nameof(RepositoryBudgets))]
    public async Task RepositoryWorkload_StaysWithinBudget(RepositoryPerformanceBudget budget)
    {
        var measurement = await PerformanceProbe.MeasureAsync(
            operation: cancellationToken => budget.Operation(fixture, cancellationToken),
            warmupIterations: 2,
            measuredIterations: 6,
            cancellationToken: CancellationToken.None);

        Assert.True(
            measurement.MeanMs <= budget.MaxMeanMs,
            $"{budget.Name} mean {measurement.MeanMs}ms exceeded budget {budget.MaxMeanMs}ms (p95 {measurement.P95Ms}ms, range {measurement.MinMs}-{measurement.MaxMs}ms).");

        Assert.True(
            measurement.P95Ms <= budget.MaxP95Ms,
            $"{budget.Name} p95 {measurement.P95Ms}ms exceeded budget {budget.MaxP95Ms}ms (mean {measurement.MeanMs}ms, range {measurement.MinMs}-{measurement.MaxMs}ms).");
    }

    private static FindFilter Desc(string sort, int perPage = 25)
        => new()
        {
            Page = 1,
            PerPage = perPage,
            Sort = sort,
            Direction = Cove.Core.Enums.SortDirection.Desc,
        };

    private static FindFilter Asc(string sort, int perPage = 25)
        => new()
        {
            Page = 1,
            PerPage = perPage,
            Sort = sort,
            Direction = Cove.Core.Enums.SortDirection.Asc,
        };

    private static IntCriterion GreaterThan(int value)
        => new()
        {
            Value = value,
            Modifier = CriterionModifier.GreaterThan,
        };

    private static StringCriterion Equals(string value)
        => new()
        {
            Value = value,
            Modifier = CriterionModifier.Equals,
        };
}

public sealed record RepositoryPerformanceBudget(
    string Name,
    double MaxMeanMs,
    double MaxP95Ms,
    Func<PostgresPerformanceFixture, CancellationToken, Task> Operation)
{
    public override string ToString() => Name;
}
