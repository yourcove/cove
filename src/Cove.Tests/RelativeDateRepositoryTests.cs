using Cove.Core.Entities;
using Cove.Api.Controllers;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace Cove.Tests;

public class RelativeDateRepositoryTests
{
    [Fact]
    public async Task VideoResultsAndCountMoveTogether_WhenSameFilterIsReused()
    {
        await using var db = new CoveContext(new DbContextOptionsBuilder<CoveContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Videos.AddRange(
            new Video { Title = "first", Date = new DateOnly(2024, 2, 24) },
            new Video { Title = "second", Date = new DateOnly(2024, 3, 1) },
            new Video { Title = "third", Date = new DateOnly(2024, 3, 8) });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var filter = new VideoFilter { DateCriterion = new() { Modifier = CriterionModifier.Between, Value = "-7d", Value2 = "+0d" } };
        var repository = new VideoRepository(db);
        using (RelativeDateEvaluation.Begin(new RelativeDateExpressionTests.Clock(new DateTimeOffset(2024, 3, 1, 12, 0, 0, TimeSpan.Zero))))
        {
            var (items, total) = await repository.FindAsync(filter, new() { Sort = "title" }, TestContext.Current.CancellationToken);
            Assert.Equal(2, total);
            Assert.Equal(new[] { "first", "second" }, items.Select(item => item.Title));
        }
        using (RelativeDateEvaluation.Begin(new RelativeDateExpressionTests.Clock(new DateTimeOffset(2024, 3, 8, 12, 0, 0, TimeSpan.Zero))))
        {
            var (items, total) = await repository.FindAsync(filter, new() { Sort = "title" }, TestContext.Current.CancellationToken);
            Assert.Equal(2, total);
            Assert.Equal(new[] { "second", "third" }, items.Select(item => item.Title));
        }
        Assert.Equal("-7d", filter.DateCriterion.Value);
        Assert.Equal("+0d", filter.DateCriterion.Value2);
    }

    [Fact]
    public async Task NestedExpressionAndRelatedPerformerShareOneClockReading()
    {
        await using var db = new CoveContext(new DbContextOptionsBuilder<CoveContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var today = new DateOnly(2024, 2, 29);
        db.Videos.AddRange(
            new Video { Title = "matches", Date = today, VideoPerformers = [new() { Performer = new Performer { Name = "today", Birthdate = today } }] },
            new Video { Title = "future relation", Date = today, VideoPerformers = [new() { Performer = new Performer { Name = "tomorrow", Birthdate = today.AddDays(1) } }] });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        DateCriterion CurrentDate() => new() { Modifier = CriterionModifier.Equals, Value = "+0d" };
        var expression = new FilterExpression<VideoFilter>
        {
            Children = [new() { Group = new() { Children = [
                new() { Filter = new() { DateCriterion = CurrentDate() } },
                new() { Filter = new() { PerformerFilterCriterion = new() { ObjectFilter = new() { BirthdateCriterion = CurrentDate() } } } },
            ] } }],
        };
        var clock = new AdvancingClock();
        using var scope = RelativeDateEvaluation.Begin(clock);
        var (items, total) = await new VideoRepository(db).FindAsync(null, new() { Sort = "title" }, TestContext.Current.CancellationToken, expression);
        Assert.Equal(1, total);
        Assert.Equal("matches", Assert.Single(items).Title);
        Assert.Equal(1, clock.ReadCount);
    }

    private sealed class AdvancingClock : TimeProvider
    {
        public int ReadCount { get; private set; }
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2024, 2, 29, 23, 59, 59, TimeSpan.Zero).AddSeconds(2 * ReadCount++);
    }

    [Theory]
    [InlineData("2024-03-01T12:00:00Z")]
    [InlineData("2024-03-01T14:00:00+02:00")]
    [InlineData("2024-03-01T12:00:00")]
    public void SegmentTimestampBoundsTranslateToPostgresAsUtc(string upper)
    {
        using var db = new CoveContext(new DbContextOptionsBuilder<CoveContext>().UseNpgsql("Host=localhost;Database=unused", options => options.UseVector()).Options);
        Assert.True(SegmentsController.TryParseDateTime(upper, out var parsed));
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        Assert.Equal(new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc), parsed);
        var sql = SegmentsController.ApplyDateTimeCriterion(db.Segments, parsed.AddDays(-7), upper, "BETWEEN", segment => segment.CreatedAt).ToQueryString();
        Assert.Contains("2024-02-23", sql);
        Assert.Contains("2024-03-01", sql);
    }

    [Fact]
    public void RelativeComparisonsTranslateToPostgres()
    {
        using var db = new CoveContext(new DbContextOptionsBuilder<CoveContext>().UseNpgsql("Host=localhost;Database=unused", options => options.UseVector()).Options);
        using var scope = RelativeDateEvaluation.Begin(new RelativeDateExpressionTests.Clock(new DateTimeOffset(2024, 3, 1, 12, 0, 0, TimeSpan.Zero)));
        var date = new DateCriterion { Modifier = CriterionModifier.Between, Value = "-7d", Value2 = "+0d" };
        var timestamp = new TimestampCriterion { Modifier = CriterionModifier.Between, Value = "-7d", Value2 = "+0d" };
        var query = FilterHelpers.ApplyDate(db.Videos, date, video => video.Date);
        query = FilterHelpers.ApplyTimestamp(query, timestamp, video => video.CreatedAt);
        var sql = query.ToQueryString();
        Assert.Contains("2024-02-23", sql);
        Assert.Contains("2024-03-01", sql);
        Assert.Contains(">=", sql);
        Assert.Contains("<=", sql);
    }
}
