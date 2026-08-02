using Cove.Api.Controllers;
using Cove.Api.Http;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class BulkDeleteEventTargetTests
{
    [Fact]
    public async Task AudioBulkDeleteReturnsOnlyPersistedIds()
    {
        await using var db = CreateContext();
        var audio = new Audio { Title = "Audio" };
        db.Audios.Add(audio);
        await db.SaveChangesAsync();
        var controller = new AudiosController(db, new CustomFieldService(db), null!, null!, null!);

        var result = await controller.BulkDelete(new BatchDeleteDto([audio.Id, audio.Id, 999]), CancellationToken.None);

        Assert.Equal([audio.Id], GetDeletedIds(result));
    }

    [Fact]
    public async Task ImageBulkDeleteReturnsOnlyPersistedIds()
    {
        await using var db = CreateContext();
        var image = new Image { Title = "Image" };
        db.Images.Add(image);
        await db.SaveChangesAsync();
        var controller = new ImagesController(
            new ImageRepository(db),
            db,
            new NoOpUserEngagementService(),
            new CustomFieldService(db),
            null!,
            null!);

        var result = await controller.BulkDelete(new BatchDeleteDto([image.Id, image.Id, 999]), CancellationToken.None);

        Assert.Equal([image.Id], GetDeletedIds(result));
    }

    [Fact]
    public async Task TextBulkDeleteReturnsOnlyPersistedIds()
    {
        await using var db = CreateContext();
        var text = new TextDocument { Title = "Text" };
        db.TextDocuments.Add(text);
        await db.SaveChangesAsync();
        var controller = new TextsController(db, new CustomFieldService(db), null!, null!, null!, null!);

        var result = await controller.BulkDelete(new BatchDeleteDto([text.Id, text.Id, 999]), CancellationToken.None);

        Assert.Equal([text.Id], GetDeletedIds(result));
    }

    private static IReadOnlyList<int> GetDeletedIds(IActionResult result)
    {
        var noContentResult = Assert.IsType<EntityMutationNoContentResult>(result);
        Assert.Equal(StatusCodes.Status204NoContent, noContentResult.StatusCode);
        return noContentResult.EntityIds;
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var context = new CoveContext(options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }
}
