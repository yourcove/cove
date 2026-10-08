using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class TagsControllerAliasTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public TagsControllerAliasTests()
    {
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task AddAlias_AppendsWithoutTouchingTheOtherAliases()
    {
        var tagId = await SeedTagAsync("Big Breasts", "Busty");

        await using var db = CreateContext();
        var result = await Controller(db).AddAlias(tagId, new TagAliasAddDto { Alias = " Big Tits " }, TestContext.Current.CancellationToken);

        var detail = Assert.IsType<TagDetailDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(["Big Tits", "Busty"], detail.Aliases.Order());
    }

    [Fact]
    public async Task AddAlias_IsANoOpForTheTagsOwnNameOrAnAliasItHas()
    {
        var tagId = await SeedTagAsync("Big Breasts", "Busty");

        await using var db = CreateContext();
        var controller = Controller(db);
        await controller.AddAlias(tagId, new TagAliasAddDto { Alias = "busty" }, TestContext.Current.CancellationToken);
        var result = await controller.AddAlias(tagId, new TagAliasAddDto { Alias = "BIG BREASTS" }, TestContext.Current.CancellationToken);

        var detail = Assert.IsType<TagDetailDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(["Busty"], detail.Aliases);
    }

    [Fact]
    public async Task AddAlias_RefusesANameAnotherTagClaims()
    {
        var tagId = await SeedTagAsync("Big Breasts");
        await SeedTagAsync("Big Tits");

        await using var db = CreateContext();
        var result = await Controller(db).AddAlias(tagId, new TagAliasAddDto { Alias = "big tits" }, TestContext.Current.CancellationToken);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task AddAlias_RejectsABlankAlias()
    {
        var tagId = await SeedTagAsync("Big Breasts");

        await using var db = CreateContext();
        var result = await Controller(db).AddAlias(tagId, new TagAliasAddDto { Alias = "   " }, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task AddAlias_KeepsBothOfTwoLinksMadeAtNearlyTheSameTime()
    {
        // Each request has loaded the tag before the other saves, which is where a read-then-write of the
        // whole alias list loses one of them.
        var tagId = await SeedTagAsync("Big Breasts");
        await using var first = CreateContext();
        await using var second = CreateContext();
        await first.Tags.Include(tag => tag.Aliases).SingleAsync(tag => tag.Id == tagId, TestContext.Current.CancellationToken);
        await second.Tags.Include(tag => tag.Aliases).SingleAsync(tag => tag.Id == tagId, TestContext.Current.CancellationToken);

        await Controller(first).AddAlias(tagId, new TagAliasAddDto { Alias = "Big Tits" }, TestContext.Current.CancellationToken);
        await Controller(second).AddAlias(tagId, new TagAliasAddDto { Alias = "Busty" }, TestContext.Current.CancellationToken);

        await using var check = CreateContext();
        var aliases = await check.Set<TagAlias>().Where(alias => alias.TagId == tagId).Select(alias => alias.Alias).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["Big Tits", "Busty"], aliases.Order());
    }

    [Fact]
    public async Task AddAlias_TreatsTheSameAliasAddedAtNearlyTheSameTimeAsANoOp()
    {
        var tagId = await SeedTagAsync("Big Breasts");
        await using var first = CreateContext();
        await using var second = CreateContext();
        await first.Tags.Include(tag => tag.Aliases).SingleAsync(tag => tag.Id == tagId, TestContext.Current.CancellationToken);
        await second.Tags.Include(tag => tag.Aliases).SingleAsync(tag => tag.Id == tagId, TestContext.Current.CancellationToken);

        await Controller(first).AddAlias(tagId, new TagAliasAddDto { Alias = "Big Tits" }, TestContext.Current.CancellationToken);
        var result = await Controller(second).AddAlias(tagId, new TagAliasAddDto { Alias = "big tits" }, TestContext.Current.CancellationToken);

        var detail = Assert.IsType<TagDetailDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(["Big Tits"], detail.Aliases);
    }

    private async Task<int> SeedTagAsync(string name, params string[] aliases)
    {
        await using var db = CreateContext();
        var tag = new Tag { Name = name, Aliases = aliases.Select(alias => new TagAlias { Alias = alias }).ToList() };
        db.Tags.Add(tag);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return tag.Id;
    }

    private static TagsController Controller(CoveContext db) => new(null!, db, new CustomFieldService(db), null!);

    private CoveContext CreateContext() =>
        new(new DbContextOptionsBuilder<CoveContext>().UseSqlite(_connection).Options);
}
