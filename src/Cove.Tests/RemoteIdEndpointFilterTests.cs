using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public class RemoteIdEndpointFilterTests
{
    public static TheoryData<string, CriterionModifier, string[]> Cases => new()
    {
        { "videos", CriterionModifier.Equals, ["A only", "Both"] },
        { "videos", CriterionModifier.NotEquals, ["B only", "None"] },
        { "videos", CriterionModifier.Includes, ["A only", "Both"] },
        { "videos", CriterionModifier.Excludes, ["B only", "None"] },
        { "videos", CriterionModifier.MatchesRegex, ["A only", "Both"] },
        { "videos", CriterionModifier.NotMatchesRegex, ["B only", "None"] },
        { "videos", CriterionModifier.IsNull, ["B only", "None"] },
        { "videos", CriterionModifier.NotNull, ["A only", "Both"] },
        { "performers", CriterionModifier.Equals, ["A only", "Both"] },
        { "performers", CriterionModifier.NotEquals, ["B only", "None"] },
        { "performers", CriterionModifier.Includes, ["A only", "Both"] },
        { "performers", CriterionModifier.Excludes, ["B only", "None"] },
        { "performers", CriterionModifier.MatchesRegex, ["A only", "Both"] },
        { "performers", CriterionModifier.NotMatchesRegex, ["B only", "None"] },
        { "performers", CriterionModifier.IsNull, ["B only", "None"] },
        { "performers", CriterionModifier.NotNull, ["A only", "Both"] },
        { "studios", CriterionModifier.Equals, ["A only", "Both"] },
        { "studios", CriterionModifier.NotEquals, ["B only", "None"] },
        { "studios", CriterionModifier.Includes, ["A only", "Both"] },
        { "studios", CriterionModifier.Excludes, ["B only", "None"] },
        { "studios", CriterionModifier.MatchesRegex, ["A only", "Both"] },
        { "studios", CriterionModifier.NotMatchesRegex, ["B only", "None"] },
        { "studios", CriterionModifier.IsNull, ["B only", "None"] },
        { "studios", CriterionModifier.NotNull, ["A only", "Both"] },
        { "tags", CriterionModifier.Equals, ["A only", "Both"] },
        { "tags", CriterionModifier.NotEquals, ["B only", "None"] },
        { "tags", CriterionModifier.Includes, ["A only", "Both"] },
        { "tags", CriterionModifier.Excludes, ["B only", "None"] },
        { "tags", CriterionModifier.MatchesRegex, ["A only", "Both"] },
        { "tags", CriterionModifier.NotMatchesRegex, ["B only", "None"] },
        { "tags", CriterionModifier.IsNull, ["B only", "None"] },
        { "tags", CriterionModifier.NotNull, ["A only", "Both"] },
    };

    public static TheoryData<string, CriterionModifier, string[]> PairedCases
    {
        get
        {
            var data = new TheoryData<string, CriterionModifier, string[]>();
            foreach (var entityType in new[] { "videos", "performers", "studios", "tags" })
            {
                data.Add(entityType, CriterionModifier.Equals, ["Both"]);
                data.Add(entityType, CriterionModifier.NotEquals, ["A only", "B only", "None"]);
                data.Add(entityType, CriterionModifier.Includes, ["Both"]);
                data.Add(entityType, CriterionModifier.Excludes, ["A only", "B only", "None"]);
                data.Add(entityType, CriterionModifier.MatchesRegex, ["Both"]);
                data.Add(entityType, CriterionModifier.NotMatchesRegex, ["A only", "B only", "None"]);
                data.Add(entityType, CriterionModifier.IsNull, ["B only", "None"]);
                data.Add(entityType, CriterionModifier.NotNull, ["A only", "Both"]);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RemoteIdCriterion_UsesConsistentServiceSpecificSemantics(
        string entityType,
        CriterionModifier modifier,
        string[] expected)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<CoveContext>().UseSqlite(connection).Options;
        await using var context = new CoveContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        Seed(context);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var criterion = new StringCriterion { Value = "service-a", Modifier = modifier };
        var actual = entityType switch
        {
            "videos" => (await new VideoRepository(context).FindAsync(new VideoFilter { RemoteIdCriterion = criterion }, new FindFilter { Page = 1, PerPage = 20, Sort = "title" }, TestContext.Current.CancellationToken)).Items.Select(item => item.Title!).ToArray(),
            "performers" => (await new PerformerRepository(context).FindAsync(new PerformerFilter { RemoteIdCriterion = criterion }, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken)).Items.Select(item => item.Name!).ToArray(),
            "studios" => (await new StudioRepository(context).FindAsync(new StudioFilter { RemoteIdCriterion = criterion }, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken)).Items.Select(item => item.Name).ToArray(),
            "tags" => (await new TagRepository(context).FindAsync(new TagFilter { RemoteIdCriterion = criterion }, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken)).Items.Select(item => item.Name).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(entityType)),
        };

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(PairedCases))]
    public async Task RemoteIdValueCriterion_MatchesEndpointAndValueAsAPair(
        string entityType,
        CriterionModifier modifier,
        string[] expected)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<CoveContext>().UseSqlite(connection).Options;
        await using var context = new CoveContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        Seed(context);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var endpointCriterion = new StringCriterion { Value = "service-a", Modifier = CriterionModifier.Equals };
        var valueCriterion = new StringCriterion { Value = "3", Modifier = modifier };
        var actual = entityType switch
        {
            "videos" => (await new VideoRepository(context).FindAsync(new VideoFilter { RemoteIdCriterion = endpointCriterion, RemoteIdValueCriterion = valueCriterion }, new FindFilter { Page = 1, PerPage = 20, Sort = "title" }, TestContext.Current.CancellationToken)).Items.Select(item => item.Title!).ToArray(),
            "performers" => (await new PerformerRepository(context).FindAsync(new PerformerFilter { RemoteIdCriterion = endpointCriterion, RemoteIdValueCriterion = valueCriterion }, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken)).Items.Select(item => item.Name!).ToArray(),
            "studios" => (await new StudioRepository(context).FindAsync(new StudioFilter { RemoteIdCriterion = endpointCriterion, RemoteIdValueCriterion = valueCriterion }, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken)).Items.Select(item => item.Name).ToArray(),
            "tags" => (await new TagRepository(context).FindAsync(new TagFilter { RemoteIdCriterion = endpointCriterion, RemoteIdValueCriterion = valueCriterion }, new FindFilter { Page = 1, PerPage = 20, Sort = "name" }, TestContext.Current.CancellationToken)).Items.Select(item => item.Name).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(entityType)),
        };

        Assert.Equal(expected, actual);
    }

    private static void Seed(CoveContext context)
    {
        context.Videos.AddRange(
            new Video { Title = "A only", RemoteIds = [new VideoRemoteId { Endpoint = "SERVICE-A", RemoteId = "1" }] },
            new Video { Title = "B only", RemoteIds = [new VideoRemoteId { Endpoint = "service-b", RemoteId = "3" }] },
            new Video { Title = "Both", RemoteIds = [new VideoRemoteId { Endpoint = "service-a", RemoteId = "3" }, new VideoRemoteId { Endpoint = "service-b", RemoteId = "4" }] },
            new Video { Title = "None" });
        context.Performers.AddRange(
            new Performer { Name = "A only", RemoteIds = [new PerformerRemoteId { Endpoint = "SERVICE-A", RemoteId = "1" }] },
            new Performer { Name = "B only", RemoteIds = [new PerformerRemoteId { Endpoint = "service-b", RemoteId = "3" }] },
            new Performer { Name = "Both", RemoteIds = [new PerformerRemoteId { Endpoint = "service-a", RemoteId = "3" }, new PerformerRemoteId { Endpoint = "service-b", RemoteId = "4" }] },
            new Performer { Name = "None" });
        context.Studios.AddRange(
            new Studio { Name = "A only", RemoteIds = [new StudioRemoteId { Endpoint = "SERVICE-A", RemoteId = "1" }] },
            new Studio { Name = "B only", RemoteIds = [new StudioRemoteId { Endpoint = "service-b", RemoteId = "3" }] },
            new Studio { Name = "Both", RemoteIds = [new StudioRemoteId { Endpoint = "service-a", RemoteId = "3" }, new StudioRemoteId { Endpoint = "service-b", RemoteId = "4" }] },
            new Studio { Name = "None" });
        context.Tags.AddRange(
            new Tag { Name = "A only", RemoteIds = [new TagRemoteId { Endpoint = "SERVICE-A", RemoteId = "1" }] },
            new Tag { Name = "B only", RemoteIds = [new TagRemoteId { Endpoint = "service-b", RemoteId = "3" }] },
            new Tag { Name = "Both", RemoteIds = [new TagRemoteId { Endpoint = "service-a", RemoteId = "3" }, new TagRemoteId { Endpoint = "service-b", RemoteId = "4" }] },
            new Tag { Name = "None" });
    }
}
