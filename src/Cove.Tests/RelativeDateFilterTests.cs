using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Core.Interfaces;
using Cove.Data.Repositories;

namespace Cove.Tests;

public class RelativeDateFilterTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void RelativeRule_IsNotSilentlyIgnored()
    {
        var criterion = JsonSerializer.Deserialize<DateCriterion>("""
            {"modifier":"equals","value":"-7d"}
            """, Json)!;
        var rows = new[] { new Row { Date = new DateOnly(1900, 1, 1) } }.AsQueryable();
        Assert.Empty(FilterHelpers.ApplyDate(rows, criterion, row => row.Date));
    }

    private sealed class Row
    {
        public DateOnly? Date { get; init; }
    }
}

public class RelativeDateExpressionTests
{
    private static readonly DateTimeOffset Now = new(2024, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Theory]
    [InlineData("-7d", "2024-02-23")]
    [InlineData("+2w", "2024-03-15")]
    [InlineData("-1m", "2024-02-01")]
    [InlineData("+1y", "2025-03-01")]
    [InlineData("-1y3m", "2022-12-01")]
    [InlineData("+1y3m2w4d", "2025-06-19")]
    [InlineData("+0d", "2024-03-01")]
    [InlineData("0d", "2024-03-01")]
    public void DatesResolveFromCurrentUtcDate(string expression, string expected)
    {
        using var scope = RelativeDateEvaluation.Begin(new Clock(Now));
        var criterion = new DateCriterion { Value = expression, Modifier = CriterionModifier.GreaterThan };
        var resolved = RelativeDateEvaluation.Resolve(criterion);
        Assert.Equal(expected, resolved.Value);
        Assert.Equal(CriterionModifier.GreaterThan, resolved.Modifier);
        Assert.Equal(expression, criterion.Value);
    }

    [Theory]
    [InlineData("-7d", "2024-02-23T12:00:00.0000000Z")]
    [InlineData("+1m", "2024-04-01T12:00:00.0000000Z")]
    [InlineData("-1y3m", "2022-12-01T12:00:00.0000000Z")]
    [InlineData("-6h", "2024-03-01T06:00:00.0000000Z")]
    [InlineData("-1d6h", "2024-02-29T06:00:00.0000000Z")]
    [InlineData("0d", "2024-03-01T12:00:00.0000000Z")]
    public void TimestampsResolveFromCurrentUtcInstant(string expression, string expected)
    {
        using var scope = RelativeDateEvaluation.Begin(new Clock(Now));
        Assert.Equal(expected, RelativeDateEvaluation.Resolve(new TimestampCriterion { Value = expression }).Value);
    }

    [Fact]
    public void BetweenCanUseTwoRelativeBounds()
    {
        using var scope = RelativeDateEvaluation.Begin(new Clock(Now));
        var rows = new DateOnly?[]
        {
            null, new(2024, 1, 31), new(2024, 2, 1), new(2024, 3, 8), new(2024, 3, 9),
        }.Select(value => new Row { Date = value }).AsQueryable();
        var criterion = new DateCriterion { Value = "-1m", Value2 = "+7d", Modifier = CriterionModifier.Between };
        Assert.Equal(new DateOnly?[] { new(2024, 2, 1), new(2024, 3, 8) }, FilterHelpers.ApplyDate(rows, criterion, row => row.Date).Select(row => row.Date));
    }

    [Fact]
    public void ReversedRelativeRangesAreNormalizedAfterResolution()
    {
        using var scope = RelativeDateEvaluation.Begin(new Clock(Now));
        var rows = new[]
        {
            new Row { Date = new(2024, 2, 1), CreatedAt = new(2024, 2, 1, 12, 0, 0, DateTimeKind.Utc), OptionalCreatedAt = new(2024, 2, 1, 12, 0, 0, DateTimeKind.Utc) },
            new Row { Date = new(2024, 2, 12), CreatedAt = new(2024, 2, 12, 12, 0, 0, DateTimeKind.Utc), OptionalCreatedAt = new(2024, 2, 12, 12, 0, 0, DateTimeKind.Utc) },
            new Row { Date = new(2024, 2, 25), CreatedAt = new(2024, 2, 25, 12, 0, 0, DateTimeKind.Utc), OptionalCreatedAt = new(2024, 2, 25, 12, 0, 0, DateTimeKind.Utc) },
        }.AsQueryable();
        var between = new DateCriterion { Value = "-14d", Value2 = "-21d", Modifier = CriterionModifier.Between };
        var timestampBetween = new TimestampCriterion { Value = "-14d", Value2 = "-21d", Modifier = CriterionModifier.Between };
        var notBetween = new TimestampCriterion { Value = "-14d", Value2 = "-21d", Modifier = CriterionModifier.NotBetween };

        Assert.Equal(new DateOnly?[] { new(2024, 2, 12) }, FilterHelpers.ApplyDate(rows, between, row => row.Date).Select(row => row.Date));
        Assert.Single(FilterHelpers.ApplyNullableTimestamp(rows, timestampBetween, row => row.OptionalCreatedAt));
        Assert.Equal(2, FilterHelpers.ApplyTimestamp(rows, notBetween, row => row.CreatedAt).Count());
    }

    [Theory]
    [InlineData("7d")]
    [InlineData("0w")]
    [InlineData("0m")]
    [InlineData("0y")]
    [InlineData("+d")]
    [InlineData("+1.5d")]
    [InlineData("+-1d")]
    [InlineData("1y3m")]
    [InlineData("-1m1y")]
    [InlineData("-1y2y")]
    [InlineData("-1y+3m")]
    [InlineData("1y+3m")]
    [InlineData("1y-3m")]
    [InlineData("+2147483648d")]
    public void InvalidSignedExpressionsAreRejected(string expression)
    {
        using var scope = RelativeDateEvaluation.Begin(new Clock(Now));
        Assert.Throws<RelativeDateFilterException>(() => RelativeDateEvaluation.Resolve(new DateCriterion { Value = expression }));
        Assert.Throws<RelativeDateFilterException>(() => RelativeDateEvaluation.Resolve(new TimestampCriterion { Value = expression }));
    }

    [Theory]
    [InlineData("-6h")]
    [InlineData("-1d6h")]
    public void DateOnlyCriteriaRejectHourParts(string expression)
    {
        using var scope = RelativeDateEvaluation.Begin(new Clock(Now));
        Assert.Throws<RelativeDateFilterException>(() => RelativeDateEvaluation.Resolve(new DateCriterion { Value = expression }));
    }

    [Fact]
    public void CreatedAtCanMatchThePastSixHours()
    {
        using var scope = RelativeDateEvaluation.Begin(new Clock(Now));
        var rows = new[]
        {
            new Row { CreatedAt = Now.UtcDateTime.AddHours(-7) },
            new Row { CreatedAt = Now.UtcDateTime.AddHours(-5) },
        }.AsQueryable();

        var matched = FilterHelpers.ApplyTimestamp(rows, new TimestampCriterion { Modifier = CriterionModifier.GreaterThan, Value = "-6h" }, row => row.CreatedAt);

        Assert.Equal(Now.UtcDateTime.AddHours(-5), Assert.Single(matched).CreatedAt);
    }

    [Theory]
    [InlineData("1May2024")]
    [InlineData("1Dec2024")]
    public void CompactAbsoluteDatesAreNotRelativeCandidates(string value)
    {
        Assert.False(RelativeDateEvaluation.IsRelativeExpressionCandidate(value));
        Assert.Equal(value, RelativeDateEvaluation.Resolve(new DateCriterion { Value = value }).Value);
    }

    [Fact]
    public async Task ScopeSharesClockAcrossAwaitAndNestedQueries_ThenRefreshes()
    {
        var clock = new Clock(new DateTimeOffset(2024, 2, 29, 23, 59, 59, TimeSpan.Zero));
        using (RelativeDateEvaluation.Begin(clock))
        {
            var first = RelativeDateEvaluation.Resolve(new DateCriterion { Value = "+0d" });
            clock.Now = clock.Now.AddSeconds(2);
            await Task.Yield();
            using (RelativeDateEvaluation.Begin(clock))
                Assert.Equal(first.Value, RelativeDateEvaluation.Resolve(new DateCriterion { Value = "+0d" }).Value);
            Assert.Equal("2024-02-29", RelativeDateEvaluation.Resolve(new DateCriterion { Value = "+0d" }).Value);
        }
        using (RelativeDateEvaluation.Begin(clock))
            Assert.Equal("2024-03-01", RelativeDateEvaluation.Resolve(new DateCriterion { Value = "+0d" }).Value);
    }

    [Fact]
    public async Task ConcurrentScopesDoNotShareReferenceTimes()
    {
        async Task<string> ResolveAt(DateTimeOffset now)
        {
            using var scope = RelativeDateEvaluation.Begin(new Clock(now));
            await Task.Yield();
            return RelativeDateEvaluation.Resolve(new DateCriterion { Value = "+0d" }).Value;
        }
        Assert.Equal(new[] { "2024-03-01", "2024-03-02" }, await Task.WhenAll(ResolveAt(Now), ResolveAt(Now.AddDays(1))));
    }

    [Fact]
    public void SavedJsonResolvesAgainWithoutChangingTheRule()
    {
        var criterion = new TimestampCriterion { Value = "-7d", Modifier = CriterionModifier.GreaterThan };
        var json = JsonSerializer.Serialize(criterion, Json);
        var saved = JsonSerializer.Deserialize<TimestampCriterion>(json, Json)!;
        string first;
        using (RelativeDateEvaluation.Begin(new Clock(Now))) first = RelativeDateEvaluation.Resolve(saved).Value;
        using (RelativeDateEvaluation.Begin(new Clock(Now.AddDays(1)))) Assert.NotEqual(first, RelativeDateEvaluation.Resolve(saved).Value);
        Assert.Equal(json, JsonSerializer.Serialize(saved, Json));
    }

    [Theory]
    [InlineData("{\"dateCriterion\":{\"modifier\":\"EQUALS\",\"value\":\"+1h\"}}")]
    [InlineData("{\"filterExpression\":{\"children\":[{\"filter\":{\"dateCriterion\":{\"modifier\":\"BETWEEN\",\"value\":\"-1m\",\"value2\":\"+1.5d\"}}}]}}")]
    [InlineData("{\"dateCriterion\":{\"modifier\":\"EQUALS\",\"value\":\"1y+3m\"}}")]
    [InlineData("{\"dateCriterion\":{\"modifier\":\"EQUALS\",\"value\":\"-6h\"}}")]
    public void InvalidSavedRulesAreRejected(string json) => Assert.Throws<RelativeDateFilterException>(() => RelativeDateFilterJson.Validate(json));

    [Fact]
    public void SavedTimestampRulesAcceptHours()
        => RelativeDateFilterJson.Validate("""{"objectFilter":{"addedAtCriterion":{"modifier":"GREATER_THAN","value":"-6h"}}}""");

    [Fact]
    public void AbsoluteAndNullCriteriaAreUnchanged()
    {
        var criterion = new DateCriterion { Value = "2024-02-29", Modifier = CriterionModifier.Equals };
        Assert.Same(criterion, RelativeDateEvaluation.Resolve(criterion));
        var rows = new[] { new Row(), new Row { Date = new DateOnly(2024, 2, 29) } }.AsQueryable();
        Assert.Single(FilterHelpers.ApplyDate(rows, criterion, row => row.Date));
        Assert.Null(Assert.Single(FilterHelpers.ApplyDate(rows, new() { Modifier = CriterionModifier.IsNull }, row => row.Date)).Date);
    }

    [Fact]
    public void SegmentFlatParametersResolveOnTheServer()
    {
        using var scope = RelativeDateEvaluation.Begin(new Clock(Now));
        var bounds = Cove.Api.Controllers.SegmentsController.ResolveRelativeTimestamp("-7d", "+0d");
        Assert.Equal(Now.AddDays(-7), DateTimeOffset.Parse(bounds.Value!));
        Assert.Equal(Now, DateTimeOffset.Parse(bounds.Value2!));
    }

    private sealed class Row
    {
        public DateOnly? Date { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? OptionalCreatedAt { get; init; }
    }

    internal sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
