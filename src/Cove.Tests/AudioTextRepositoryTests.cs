using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

/// <summary>
/// Behavior the sort/filter harness does not cover for the audio and text repositories: id projection
/// order, count-only paging, selection totals, and that text documents now honor a filter expression.
/// </summary>
[RequiresPostgres]
public class AudioTextRepositoryTests
{
    private static async Task SeedAsync(CoveContext context)
    {
        var day = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        context.Audios.AddRange(
            new Audio { Id = 1, Title = "Cello", MaxDuration = 10, MaxFileSize = 100, CreatedAt = day.AddDays(1), UpdatedAt = day.AddDays(1) },
            new Audio { Id = 2, Title = "Alto", MaxDuration = 20, MaxFileSize = 200, CreatedAt = day.AddDays(2), UpdatedAt = day.AddDays(2) },
            new Audio { Id = 3, Title = "Bass", MaxDuration = 30, MaxFileSize = 300, CreatedAt = day.AddDays(3), UpdatedAt = day.AddDays(3) });
        context.TextDocuments.AddRange(
            new TextDocument { Id = 1, Title = "Cello", MaxFileSize = 100, CreatedAt = day.AddDays(1), UpdatedAt = day.AddDays(1) },
            new TextDocument { Id = 2, Title = "Alto", MaxFileSize = 200, CreatedAt = day.AddDays(2), UpdatedAt = day.AddDays(2) },
            new TextDocument { Id = 3, Title = "Bass", MaxFileSize = 300, CreatedAt = day.AddDays(3), UpdatedAt = day.AddDays(3) });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FindIdsAsync_FollowsFindOrderAndLimit()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = database.CreateContext();
        await SeedAsync(context);
        var byTitle = new FindFilter { Sort = "title", Direction = Cove.Core.Enums.SortDirection.Asc };

        var audios = new AudioRepository(context);
        Assert.Equal([2, 3, 1], (await audios.FindAsync(null, byTitle, TestContext.Current.CancellationToken)).Items.Select(audio => audio.Id));
        Assert.Equal([2, 3, 1], await audios.FindIdsAsync(null, byTitle, int.MaxValue, TestContext.Current.CancellationToken));
        Assert.Equal([2, 3], await audios.FindIdsAsync(null, byTitle, 2, TestContext.Current.CancellationToken));
        Assert.Empty(await audios.FindIdsAsync(null, byTitle, 0, TestContext.Current.CancellationToken));

        var texts = new TextDocumentRepository(context);
        Assert.Equal([2, 3, 1], (await texts.FindAsync(null, byTitle, TestContext.Current.CancellationToken)).Items.Select(text => text.Id));
        Assert.Equal([2, 3, 1], await texts.FindIdsAsync(null, byTitle, int.MaxValue, TestContext.Current.CancellationToken));
        Assert.Equal([2, 3], await texts.FindIdsAsync(null, byTitle, 2, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindAsync_PerPageAtOrBelowZero_CountsWithoutLoading()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = database.CreateContext();
        await SeedAsync(context);

        var (audios, audioTotal) = await new AudioRepository(context).FindAsync(null, new FindFilter { PerPage = 0 }, TestContext.Current.CancellationToken);
        Assert.Empty(audios);
        Assert.Equal(3, audioTotal);

        var (texts, textTotal) = await new TextDocumentRepository(context).FindAsync(null, new FindFilter { PerPage = 0 }, TestContext.Current.CancellationToken);
        Assert.Empty(texts);
        Assert.Equal(3, textTotal);
    }

    [Fact]
    public async Task AggregateAsync_WithIds_TotalsTheSelectionOnly()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = database.CreateContext();
        await SeedAsync(context);

        var audioRepository = new AudioRepository(context);
        Assert.Equal(new AudioAggregate(3, 60, 600), await audioRepository.AggregateAsync(null, null, TestContext.Current.CancellationToken));
        Assert.Equal(new AudioAggregate(2, 40, 400), await audioRepository.AggregateAsync(null, null, [1, 3], TestContext.Current.CancellationToken));

        var textRepository = new TextDocumentRepository(context);
        Assert.Equal(new TextAggregate(3, 600), await textRepository.AggregateAsync(null, null, TestContext.Current.CancellationToken));
        Assert.Equal(new TextAggregate(2, 400), await textRepository.AggregateAsync(null, null, [1, 3], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TextDocuments_HonorFilterExpression()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = database.CreateContext();
        await SeedAsync(context);
        var allButAlto = new FilterExpression<TextDocumentFilter>
        {
            Operator = FilterExpressionOperator.Not,
            Children = [new() { Filter = new TextDocumentFilter { TitleCriterion = new StringCriterion { Value = "Alto" } } }],
        };
        var byTitle = new FindFilter { Sort = "title", Direction = Cove.Core.Enums.SortDirection.Asc };

        var repository = new TextDocumentRepository(context);
        var (items, total) = await repository.FindAsync(null, byTitle, TestContext.Current.CancellationToken, allButAlto);
        Assert.Equal(2, total);
        Assert.Equal(["Bass", "Cello"], items.Select(text => text.Title));
        Assert.Equal(new TextAggregate(2, 400), await repository.AggregateAsync(null, null, TestContext.Current.CancellationToken, allButAlto));
    }
}
