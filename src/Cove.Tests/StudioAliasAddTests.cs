using Cove.Api.Controllers;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Repositories;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

/// <summary>
/// The video tagger's studio Link remembers a scraped name as an alias of a library studio. The alias is
/// appended on the server, so an alias saved meanwhile is kept, and refused when another studio already
/// goes by that name, since lookups would keep finding that one.
/// </summary>
public class StudioAliasAddTests
{
    [Fact]
    public async Task AddsTheAliasOnceAndKeepsTheOthers()
    {
        await using var db = CreateContext();
        var studio = new Studio { Name = "Miss Example", Aliases = [new StudioAlias { Alias = "Saved meanwhile" }] };
        db.Studios.Add(studio);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var controller = new StudiosController(new StudioRepository(db), null!, db, null!);

        Assert.IsType<OkObjectResult>((await controller.AddAlias(studio.Id, new StudioAliasAddDto { Alias = " Example Midas " }, TestContext.Current.CancellationToken)).Result);
        // The same name again, in other case, and the studio's own name are no-ops.
        Assert.IsType<OkObjectResult>((await controller.AddAlias(studio.Id, new StudioAliasAddDto { Alias = "example midas" }, TestContext.Current.CancellationToken)).Result);
        Assert.IsType<OkObjectResult>((await controller.AddAlias(studio.Id, new StudioAliasAddDto { Alias = "Miss Example" }, TestContext.Current.CancellationToken)).Result);

        var aliases = await db.Set<StudioAlias>().AsNoTracking()
            .Where(alias => alias.StudioId == studio.Id)
            .Select(alias => alias.Alias)
            .OrderBy(alias => alias)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["Example Midas", "Saved meanwhile"], aliases);
    }

    [Fact]
    public async Task RefusesANameAnotherStudioGoesBy()
    {
        await using var db = CreateContext();
        var studio = new Studio { Name = "Miss Example" };
        var other = new Studio { Name = "Other Studio", Aliases = [new StudioAlias { Alias = "Taken Alias" }] };
        db.Studios.AddRange(studio, other);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var controller = new StudiosController(new StudioRepository(db), null!, db, null!);

        Assert.IsType<ConflictObjectResult>((await controller.AddAlias(studio.Id, new StudioAliasAddDto { Alias = "Other Studio" }, TestContext.Current.CancellationToken)).Result);
        Assert.IsType<ConflictObjectResult>((await controller.AddAlias(studio.Id, new StudioAliasAddDto { Alias = "taken alias" }, TestContext.Current.CancellationToken)).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.AddAlias(studio.Id, new StudioAliasAddDto { Alias = "  " }, TestContext.Current.CancellationToken)).Result);
        Assert.IsType<NotFoundResult>((await controller.AddAlias(9999, new StudioAliasAddDto { Alias = "Anything" }, TestContext.Current.CancellationToken)).Result);
        Assert.False(await db.Set<StudioAlias>().AnyAsync(alias => alias.StudioId == studio.Id, TestContext.Current.CancellationToken));
    }

    private static CoveContext CreateContext()
        => new(new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"studio-alias-add-{Guid.NewGuid():N}")
            .Options);
}
