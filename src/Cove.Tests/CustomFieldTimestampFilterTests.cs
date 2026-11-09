using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public class CustomFieldTimestampFilterTests
{
    private static readonly DateTimeOffset Now = new(2024, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime NowUtc = Now.UtcDateTime;

    [Theory]
    [RequiresPostgres]
    [InlineData("-30s", new[] { 1 })]
    [InlineData("-1min", new[] { 1, 2 })]
    [InlineData("-1h30min", new[] { 1, 2, 3 })]
    public async Task RelativeTimestampFiltersResolveToTheSecond(string value, int[] expected)
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = database.CreateContext();
        await SeedTimestampsAsync(context,
            (1, NowUtc.AddSeconds(-20)),
            (2, NowUtc.AddSeconds(-40)),
            (3, NowUtc.AddMinutes(-80)),
            (4, NowUtc.AddMinutes(-100)));

        using var scope = RelativeDateEvaluation.Begin(new RelativeDateExpressionTests.Clock(Now));
        var ids = await FilterAsync(context, CustomFieldTypes.Timestamp, CriterionModifier.GreaterThan, value);

        Assert.Equal(expected, ids);
    }

    [Fact]
    [RequiresPostgres]
    public async Task ReversedRelativeTimestampBoundsAreOrdered()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = database.CreateContext();
        await SeedTimestampsAsync(context,
            (1, NowUtc.AddSeconds(-5)),
            (2, NowUtc.AddSeconds(-45)),
            (3, NowUtc.AddSeconds(-90)));

        using var scope = RelativeDateEvaluation.Begin(new RelativeDateExpressionTests.Clock(Now));
        Assert.Equal([2], await FilterAsync(context, CustomFieldTypes.Timestamp, CriterionModifier.Between, "-10s", "-1min"));
        Assert.Equal([1, 3], await FilterAsync(context, CustomFieldTypes.Timestamp, CriterionModifier.NotBetween, "-10s", "-1min"));
    }

    [Fact]
    [RequiresPostgres]
    public async Task AbsoluteTimestampComparisonsUseWholeSeconds()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = database.CreateContext();
        var second = new DateTime(2024, 2, 1, 8, 30, 15, DateTimeKind.Utc);
        await SeedTimestampsAsync(context,
            (1, second.AddMilliseconds(750)),
            (2, second.AddSeconds(1)),
            (3, second.AddMilliseconds(-1)));

        Assert.Equal([1], await FilterAsync(context, CustomFieldTypes.Timestamp, CriterionModifier.Equals, "2024-02-01T08:30:15Z"));
        Assert.Equal([2, 3], await FilterAsync(context, CustomFieldTypes.Timestamp, CriterionModifier.NotEquals, "2024-02-01T08:30:15"));
        Assert.Equal([2], await FilterAsync(context, CustomFieldTypes.Timestamp, CriterionModifier.GreaterThan, "2024-02-01T08:30:15.200Z"));
        Assert.Equal([3], await FilterAsync(context, CustomFieldTypes.Timestamp, CriterionModifier.LessThan, "2024-02-01T10:30:15+02:00"));
        Assert.Equal([1, 3], await FilterAsync(context, CustomFieldTypes.Timestamp, CriterionModifier.Between, "2024-02-01T08:30:14Z", "2024-02-01T08:30:15Z"));
    }

    [Fact]
    [RequiresPostgres]
    public async Task TheLastSupportedSecondCanBeCompared()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = database.CreateContext();
        await SeedTimestampsAsync(context,
            (1, new DateTime(9999, 12, 31, 23, 59, 59, 500, DateTimeKind.Utc)),
            (2, new DateTime(9999, 12, 31, 23, 59, 58, DateTimeKind.Utc)));

        Assert.Equal([1], await FilterAsync(context, CustomFieldTypes.Timestamp, CriterionModifier.Equals, "9999-12-31T23:59:59Z"));
        Assert.Empty(await FilterAsync(context, CustomFieldTypes.Timestamp, CriterionModifier.GreaterThan, "9999-12-31T23:59:59Z"));
        Assert.Equal([2], await FilterAsync(context, CustomFieldTypes.Timestamp, CriterionModifier.LessThan, "9999-12-31T23:59:59Z"));
    }

    [Fact]
    [RequiresPostgres]
    public async Task RelativeDateFiltersApplyToDateCustomFields()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = database.CreateContext();
        var definition = Definition(CustomFieldTypes.Date);
        context.Videos.AddRange(new Video { Id = 1, Title = "1" }, new Video { Id = 2, Title = "2" }, new Video { Id = 3, Title = "3" });
        context.CustomFieldValues.AddRange(
            Value(definition, 1, value => value.DateValue = new DateOnly(2024, 2, 23)),
            Value(definition, 2, value => value.DateValue = new DateOnly(2024, 3, 1)),
            Value(definition, 3, value => value.DateValue = new DateOnly(2024, 2, 22)));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var scope = RelativeDateEvaluation.Begin(new RelativeDateExpressionTests.Clock(Now));
        Assert.Equal([1, 2], await FilterAsync(context, CustomFieldTypes.Date, CriterionModifier.Between, "0d", "-7d"));
        Assert.Throws<RelativeDateFilterException>(() =>
            context.Videos.ApplyCustomFieldCriterion(context, CustomFieldEntityTypes.Video, Criterion(CustomFieldTypes.Date, CriterionModifier.GreaterThan, "-30s")));
    }

    [Fact]
    [RequiresPostgres]
    public async Task TimestampCustomFieldsSortChronologically()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = database.CreateContext();
        await SeedTimestampsAsync(context,
            (1, NowUtc.AddSeconds(2)),
            (2, NowUtc),
            (3, NowUtc.AddSeconds(1)));

        var ascending = await context.Videos
            .ApplyCustomFieldSort(context, CustomFieldEntityTypes.Video, "custom:timestamp:recorded_at", desc: false)
            .Select(video => video.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([2, 3, 1], ascending);
    }

    [Theory]
    [InlineData("""{"objectFilter":{"customFieldCriteria":[{"key":"recorded_on","type":"date","modifier":"GREATER_THAN","value":"-30s"}]}}""")]
    [InlineData("""{"customFieldCriterion":{"key":"recorded_at","type":"timestamp","modifier":"GREATER_THAN","value":"-30s1min"}}""")]
    [InlineData("""{"filterExpression":{"children":[{"filter":{"customFieldCriteria":[{"key":"recorded_at","type":"timestamp","modifier":"BETWEEN","value":"-1h","value2":"+1.5s"}]}}]}}""")]
    public void InvalidSavedCustomFieldRulesAreRejected(string json)
        => Assert.Throws<RelativeDateFilterException>(() => RelativeDateFilterJson.Validate(json));

    [Theory]
    [InlineData("""{"objectFilter":{"customFieldCriteria":[{"key":"recorded_at","type":"timestamp","modifier":"GREATER_THAN","value":"-1h30min15s"}]}}""")]
    [InlineData("""{"objectFilter":{"customFieldCriteria":[{"key":"label","type":"text","modifier":"EQUALS","value":"-6h"}]}}""")]
    [InlineData("""{"objectFilter":{"customFieldCriteria":[{"key":"doc","type":"text","jsonPath":"/when","modifier":"EQUALS","value":"-6h"}]}}""")]
    public void ValidSavedCustomFieldRulesAreAccepted(string json) => RelativeDateFilterJson.Validate(json);

    private static async Task SeedTimestampsAsync(CoveContext context, params (int Id, DateTime Timestamp)[] rows)
    {
        var definition = Definition(CustomFieldTypes.Timestamp);
        foreach (var (id, timestamp) in rows)
        {
            context.Videos.Add(new Video { Id = id, Title = id.ToString() });
            context.CustomFieldValues.Add(Value(definition, id, value => value.TimestampValue = timestamp));
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static Task<List<int>> FilterAsync(CoveContext context, string type, CriterionModifier modifier, string value, string? value2 = null)
        => context.Videos
            .ApplyCustomFieldCriterion(context, CustomFieldEntityTypes.Video, Criterion(type, modifier, value, value2))
            .OrderBy(video => video.Id)
            .Select(video => video.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

    private static CustomFieldCriterion Criterion(string type, CriterionModifier modifier, string value, string? value2 = null) => new()
    {
        Key = type == CustomFieldTypes.Date ? "recorded_on" : "recorded_at",
        Type = type,
        Modifier = modifier,
        Value = value,
        Value2 = value2,
    };

    private static CustomFieldDefinition Definition(string type) => new()
    {
        Key = type == CustomFieldTypes.Date ? "recorded_on" : "recorded_at",
        Label = "Recorded",
        Type = type,
        EntityTypes = [CustomFieldEntityTypes.Video],
        Sortable = true,
    };

    private static CustomFieldValue Value(CustomFieldDefinition definition, int entityId, Action<CustomFieldValue> assign)
    {
        var value = new CustomFieldValue { Definition = definition, EntityType = CustomFieldEntityTypes.Video, EntityId = entityId };
        assign(value);
        return value;
    }
}
