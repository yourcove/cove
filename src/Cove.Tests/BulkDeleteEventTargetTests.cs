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
    public async Task ImageBulkDeleteRemovesSelectedMetadataAndPreservesUnselectedMetadata()
    {
        await using var db = CreateContext();
        var deleted = new Image { Title = "Deleted" };
        var kept = new Image { Title = "Kept" };
        var tag = new Tag { Name = "Tag" };
        var definition = new CustomFieldDefinition
        {
            Key = "bulk_cleanup",
            Label = "Bulk cleanup",
            EntityTypes = [CustomFieldEntityTypes.Image],
        };
        db.AddRange(deleted, kept, tag, definition);
        await db.SaveChangesAsync();

        db.TagApplications.AddRange(
            new TagApplication { HostType = AffinityHostType.Image, HostId = deleted.Id, TagId = tag.Id, SourceKey = "test" },
            new TagApplication { HostType = AffinityHostType.Image, HostId = kept.Id, TagId = tag.Id, SourceKey = "test" });
        db.CustomFieldValues.AddRange(
            new CustomFieldValue { DefinitionId = definition.Id, EntityType = CustomFieldEntityTypes.Image, EntityId = deleted.Id, TextValue = "remove" },
            new CustomFieldValue { DefinitionId = definition.Id, EntityType = CustomFieldEntityTypes.Image, EntityId = kept.Id, TextValue = "keep" });
        await db.SaveChangesAsync();

        var controller = new ImagesController(
            new ImageRepository(db),
            db,
            new NoOpUserEngagementService(),
            new CustomFieldService(db),
            null!,
            null!,
            new TagProvenanceService(db));

        await controller.BulkDelete(new BatchDeleteDto([deleted.Id]), CancellationToken.None);

        Assert.False(await db.Images.AnyAsync(image => image.Id == deleted.Id));
        Assert.True(await db.Images.AnyAsync(image => image.Id == kept.Id));
        Assert.False(await db.TagApplications.AnyAsync(application => application.HostType == AffinityHostType.Image && application.HostId == deleted.Id));
        Assert.True(await db.TagApplications.AnyAsync(application => application.HostType == AffinityHostType.Image && application.HostId == kept.Id));
        Assert.False(await db.CustomFieldValues.AnyAsync(value => value.EntityType == CustomFieldEntityTypes.Image && value.EntityId == deleted.Id));
        Assert.True(await db.CustomFieldValues.AnyAsync(value => value.EntityType == CustomFieldEntityTypes.Image && value.EntityId == kept.Id));
    }

    [Fact]
    public async Task ImageBulkDeleteDeletesExclusiveFileAndPreservesPathReferencedByKeptImage()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("cove-bulk-delete-");
        try
        {
            var sharedPath = Path.Combine(tempDirectory.FullName, "shared.jpg");
            var exclusivePath = Path.Combine(tempDirectory.FullName, "exclusive.jpg");
            await File.WriteAllTextAsync(sharedPath, "shared");
            await File.WriteAllTextAsync(exclusivePath, "exclusive");

            await using var db = CreateContext();
            var folder = new Folder { Path = tempDirectory.FullName };
            var deleted = new Image { Title = "Deleted" };
            var kept = new Image { Title = "Kept" };
            db.AddRange(folder, deleted, kept);
            await db.SaveChangesAsync();

            var keptReference = new ImageFile { Basename = "kept-reference.jpg", ParentFolderId = folder.Id, ImageId = kept.Id };
            db.ImageFiles.AddRange(
                new ImageFile { Basename = "shared.jpg", ParentFolderId = folder.Id, ImageId = deleted.Id },
                new ImageFile { Basename = "exclusive.jpg", ParentFolderId = folder.Id, ImageId = deleted.Id },
                keptReference);
            await db.SaveChangesAsync();
            var normalizedSharedPath = sharedPath.Replace('\\', '/');
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE files SET Path = {normalizedSharedPath} WHERE Id = {keptReference.Id}");
            db.ChangeTracker.Clear();

            var controller = new ImagesController(
                new ImageRepository(db),
                db,
                new NoOpUserEngagementService(),
                new CustomFieldService(db),
                null!,
                null!);

            await controller.BulkDelete(new BatchDeleteDto([deleted.Id], DeleteFiles: true), CancellationToken.None);

            Assert.True(File.Exists(sharedPath));
            Assert.False(File.Exists(exclusivePath));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
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
