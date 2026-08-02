using Cove.Api.Controllers;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public class FileOpsEventTests
{
    [Fact]
    public async Task MoveFilesPublishesOnlyOwnersWhoseFilesMoved()
    {
        var root = Directory.CreateTempSubdirectory("cove-file-events-");
        try
        {
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "source"));
            var destinationDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "destination"));
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory.FullName, "moved.mp4"), "test");

            var options = new DbContextOptionsBuilder<CoveContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            await using var db = new CoveContext(options);
            var movedOwner = new Video { Title = "Moved owner" };
            var skippedOwner = new Video { Title = "Skipped owner" };
            var folder = new Folder { Path = sourceDirectory.FullName };
            var movedFile = new VideoFile { Basename = "moved.mp4", ParentFolder = folder, Video = movedOwner };
            var missingFile = new VideoFile { Basename = "missing.mp4", ParentFolder = folder, Video = skippedOwner };
            db.AddRange(movedOwner, skippedOwner, folder, movedFile, missingFile);
            await db.SaveChangesAsync();

            var eventBus = new EventBus();
            var published = new List<EntityEvent>();
            using var subscription = eventBus.Subscribe<EntityEvent>(published.Add);
            var controller = new FileOpsController(db, eventBus, NullLogger<FileOpsController>.Instance);

            await controller.MoveFiles(
                new MoveFilesDto([movedFile.Id, missingFile.Id], destinationDirectory.FullName),
                CancellationToken.None);

            var evt = Assert.Single(published);
            Assert.Equal(EventType.VideoUpdated, evt.Type);
            Assert.Equal(movedOwner.Id, evt.EntityId);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
