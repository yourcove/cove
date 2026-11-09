using System.Data.Common;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cove.Tests;

public sealed class FilterExpressionQueryShapeTests
{
    private static FilterExpressionNode<VideoFilter> Title(string value, CriterionModifier modifier = CriterionModifier.Includes)
        => new() { Filter = new VideoFilter { TitleCriterion = new StringCriterion { Modifier = modifier, Value = value } } };

    [Fact]
    public async Task OrAndNotBranches_CompareIdsWithoutRepeatingTheConditionsBeforeThem()
    {
        await using var db = CreatePostgresContext();
        var expression = new FilterExpression<VideoFilter>
        {
            Children =
            [
                Title("prefix"),
                new() { Group = new() { Operator = FilterExpressionOperator.Or, Children = [Title("alpha"), Title("beta"), Title("gamma")] } },
                new() { Group = new() { Operator = FilterExpressionOperator.Not, Children = [Title("delta")] } },
            ],
        };

        var query = await new VideoRepository(db).BuildFilteredQueryAsync(
            null, null, allowReadScopeOptimization: false, ct: TestContext.Current.CancellationToken, expression: expression);
        var sql = query.Select(video => video.Id).ToQueryString();

        // Each condition appears once: the OR branches and the NOT start from the library, not from the prefix.
        foreach (var value in new[] { "prefix", "alpha", "beta", "gamma", "delta" })
            Assert.Equal(1, sql.Split(value).Length - 1);
        Assert.Equal(2, sql.Split("UNION").Length - 1);
        Assert.DoesNotContain("EXCEPT", sql, StringComparison.Ordinal);
        // The branches carry ids only, so the union never compares whole rows.
        Assert.DoesNotContain("\"Captions\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PerformerAndTextBranches_StartFromTheLibraryAndCompareIds()
    {
        await using var db = CreatePostgresContext();
        static FilterExpression<TFilter> Expression<TFilter>(Func<string, TFilter> leaf) where TFilter : class => new()
        {
            Children =
            [
                new() { Group = new() { Operator = FilterExpressionOperator.Or, Children = [new() { Filter = leaf("alpha") }, new() { Filter = leaf("beta") }] } },
                new() { Group = new() { Operator = FilterExpressionOperator.Not, Children = [new() { Filter = leaf("delta") }] } },
            ],
        };
        static StringCriterion Includes(string value) => new() { Modifier = CriterionModifier.Includes, Value = value };

        var performers = await new PerformerRepository(db).BuildFilteredQueryAsync(
            new PerformerFilter { NameCriterion = Includes("prefix") },
            null,
            allowReadScopeOptimization: false,
            ct: TestContext.Current.CancellationToken,
            expression: Expression(value => new PerformerFilter { NameCriterion = Includes(value) }));
        var texts = await TextDocumentFilterQuery.BuildAsync(
            db,
            new TextDocumentFilter { TitleCriterion = Includes("prefix") },
            null,
            ct: TestContext.Current.CancellationToken,
            expression: Expression(value => new TextDocumentFilter { TitleCriterion = Includes(value) }));

        foreach (var sql in new[] { performers.Select(performer => performer.Id).ToQueryString(), texts.Select(text => text.Id).ToQueryString() })
        {
            // The top-level condition is applied once, not again inside every branch.
            foreach (var value in new[] { "prefix", "alpha", "beta", "delta" })
                Assert.Equal(1, sql.Split(value).Length - 1);
            Assert.DoesNotContain("INTERSECT", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("EXCEPT", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    [RequiresPostgres]
    public async Task PerformerAndTextBranches_KeepToWhatTheConditionsAndSearchBeforeThemMatch()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext();
        string[] names = ["prefix alpha", "prefix beta", "prefix alpha beta", "prefix alpha delta", "other alpha", "other beta"];
        foreach (var name in names)
        {
            db.Performers.Add(new Performer { Name = name });
            db.TextDocuments.Add(new TextDocument { Title = name });
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        static StringCriterion Includes(string value) => new() { Modifier = CriterionModifier.Includes, Value = value };
        static FilterExpression<TFilter> Expression<TFilter>(Func<string, TFilter> leaf, FilterExpressionOperator branches) where TFilter : class => new()
        {
            Children =
            [
                new() { Group = new() { Operator = branches, Children = [new() { Filter = leaf("alpha") }, new() { Filter = leaf("beta") }] } },
                new() { Group = new() { Operator = FilterExpressionOperator.Not, Children = [new() { Filter = leaf("delta") }] } },
            ],
        };
        var repository = new PerformerRepository(db);
        async Task<string[]> Performers(PerformerFilter? filter, string? q, FilterExpressionOperator branches)
            => (await (await repository.BuildFilteredQueryAsync(
                    filter, new FindFilter { Q = q }, allowReadScopeOptimization: false, ct: TestContext.Current.CancellationToken,
                    expression: Expression(value => new PerformerFilter { NameCriterion = Includes(value) }, branches)))
                .Select(performer => performer.Name).ToListAsync(TestContext.Current.CancellationToken)).Order().ToArray();
        async Task<string[]> Texts(TextDocumentFilter? filter, string? q, FilterExpressionOperator branches)
            => (await (await TextDocumentFilterQuery.BuildAsync(
                    db, filter, new FindFilter { Q = q }, ct: TestContext.Current.CancellationToken,
                    expression: Expression(value => new TextDocumentFilter { TitleCriterion = Includes(value) }, branches)))
                .Select(text => text.Title!).ToListAsync(TestContext.Current.CancellationToken)).Order().ToArray();
        string[] anyOf = ["prefix alpha", "prefix alpha beta", "prefix beta"];
        string[] justOne = ["prefix alpha", "prefix beta"];

        Assert.Equal(anyOf, await Performers(new PerformerFilter { NameCriterion = Includes("prefix") }, null, FilterExpressionOperator.Or));
        Assert.Equal(anyOf, await Performers(null, "prefix", FilterExpressionOperator.Or));
        Assert.Equal(justOne, await Performers(new PerformerFilter { NameCriterion = Includes("prefix") }, null, FilterExpressionOperator.JustOne));
        Assert.Equal(anyOf, await Texts(new TextDocumentFilter { TitleCriterion = Includes("prefix") }, null, FilterExpressionOperator.Or));
        Assert.Equal(anyOf, await Texts(null, "prefix", FilterExpressionOperator.Or));
        Assert.Equal(justOne, await Texts(new TextDocumentFilter { TitleCriterion = Includes("prefix") }, null, FilterExpressionOperator.JustOne));
    }

    [Fact]
    [RequiresPostgres]
    public async Task FindAsync_ReadsThePageAndItsTotalInOneQuery()
    {
        var commands = new CommandRecorder();
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext(options => options.AddInterceptors(commands));
        foreach (var title in new[] { "alpha one", "alpha two", "alpha three", "alpha four", "alpha five", "other" })
            db.Videos.Add(new Video { Title = title });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var expression = new FilterExpression<VideoFilter>
        {
            Operator = FilterExpressionOperator.Or,
            Children = [Title("alpha"), Title("five")],
        };
        var repository = new VideoRepository(db);

        commands.Texts.Clear();
        var (items, total) = await repository.FindAsync(
            null, new FindFilter { Page = 2, PerPage = 2, Sort = "title", Direction = SortDirection.Asc }, TestContext.Current.CancellationToken, expression);

        Assert.Equal(5, total);
        Assert.Equal(["alpha one", "alpha three"], items.Select(video => video.Title!).ToArray());
        var counting = commands.Texts.Where(text => text.Contains("count(", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Contains("count(*) OVER ()", Assert.Single(counting), StringComparison.Ordinal);

        // A page past the end has no row to carry the total, so it is counted on its own.
        var (pastEnd, pastEndTotal) = await repository.FindAsync(
            null, new FindFilter { Page = 4, PerPage = 2, Sort = "title", Direction = SortDirection.Asc }, TestContext.Current.CancellationToken, expression);

        Assert.Empty(pastEnd);
        Assert.Equal(5, pastEndTotal);

        // Just one, compiled for a relational database, keeps what exactly one branch matches.
        var (justOne, justOneTotal) = await repository.FindAsync(
            null,
            new FindFilter { Page = 1, PerPage = 10, Sort = "title", Direction = SortDirection.Asc },
            TestContext.Current.CancellationToken,
            new FilterExpression<VideoFilter> { Operator = FilterExpressionOperator.JustOne, Children = [Title("alpha"), Title("five"), Title("other")] });

        Assert.Equal(5, justOneTotal);
        Assert.Equal(["alpha four", "alpha one", "alpha three", "alpha two", "other"], justOne.Select(video => video.Title!).ToArray());
    }

    private static CoveContext CreatePostgresContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(
                "Host=localhost;Database=query_shape;Username=query_shape;Password=query_shape",
                npgsql => npgsql.UseVector())
            .Options;

        return new CoveContext(options);
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Texts { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Texts.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Texts.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
