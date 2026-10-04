using Cove.ApiTests.Infrastructure;
using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Cove.ApiTests.Tests.Metadata;

// The Stash import saves with change detection off, and the repair migration picks each video's
// lowest file id. Both rely on PostgreSQL behavior that the in-memory provider cannot show.
public sealed class VideoPrimaryFilePostgresTests
{
    [Fact]
    public async Task NewVideosSavedWithoutChangeDetectionGetTheirFirstAddedFileAsPrimary()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(ct);
        await using var db = CreateContext(database);
        await db.Database.MigrateAsync(ct);
        var folder = await AddFolderAsync(db, ct);

        db.ChangeTracker.AutoDetectChangesEnabled = false;
        // The Stash import adds the file Stash marked as primary first, whatever its name or Stash id.
        var videos = Enumerable.Range(0, 3).Select(index => new Video
        {
            Title = $"video-{index}",
            Files = new[] { "z", "a", "m" }.Select(name => NewFile(folder, $"{index}-{name}.mp4")).ToList(),
        }).ToList();
        db.Videos.AddRange(videos);
        await db.SaveChangesAsync(ct);

        db.ChangeTracker.Clear();
        var saved = await db.Videos.Include(video => video.Files).OrderBy(video => video.Title).ToListAsync(ct);
        Assert.All(saved, video =>
        {
            Assert.Equal($"{video.Title![^1]}-z.mp4", video.Files.Single(file => file.Id == video.PrimaryFileId).Basename);
            Assert.Equal(video.Files.Min(file => file.Id), video.PrimaryFileId);
        });
    }

    [Fact]
    public async Task AssignMissingVideoPrimaryFilesRepairsOnlyVideosWithFilesAndNoPrimary()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(ct);
        await using var db = CreateContext(database);
        var migrations = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        var repairIndex = migrations.IndexOf(MigrationId<AssignMissingVideoPrimaryFiles>());
        Assert.True(repairIndex > 0);
        await db.GetService<IMigrator>().MigrateAsync(migrations[repairIndex - 1], ct);

        var folder = await AddFolderAsync(db, ct);
        var missing = new Video { Title = "missing", Files = [NewFile(folder, "missing-1.mp4"), NewFile(folder, "missing-2.mp4")] };
        var chosen = new Video { Title = "chosen", Files = [NewFile(folder, "chosen-1.mp4"), NewFile(folder, "chosen-2.mp4")] };
        var empty = new Video { Title = "empty" };
        db.Videos.AddRange(missing, chosen, empty);
        await db.SaveChangesAsync(ct);
        var chosenPrimary = chosen.Files.Max(file => file.Id);
        // Recreate what an affected import left behind, and a primary someone picked by hand.
        await db.Videos.Where(video => video.Id == missing.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(video => video.PrimaryFileId, (int?)null), ct);
        await db.Videos.Where(video => video.Id == chosen.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(video => video.PrimaryFileId, chosenPrimary), ct);

        await db.Database.MigrateAsync(ct);

        db.ChangeTracker.Clear();
        var primaries = await db.Videos.ToDictionaryAsync(video => video.Title!, video => video.PrimaryFileId, ct);
        Assert.Equal(missing.Files.Min(file => file.Id), primaries["missing"]);
        Assert.Equal(chosenPrimary, primaries["chosen"]);
        Assert.Null(primaries["empty"]);
    }

    private static CoveContext CreateContext(PostgreSqlTestDatabase database) => new(new DbContextOptionsBuilder<CoveContext>()
        .UseNpgsql(database.ConnectionString, options => options.UseVector())
        .Options);

    private static async Task<Folder> AddFolderAsync(CoveContext db, CancellationToken ct)
    {
        var folder = new Folder { Path = "/library", ModTime = DateTime.UtcNow };
        db.Folders.Add(folder);
        await db.SaveChangesAsync(ct);
        return folder;
    }

    private static VideoFile NewFile(Folder folder, string basename) => new()
    {
        Basename = basename,
        ParentFolderId = folder.Id,
        Size = 1,
        ModTime = DateTime.UtcNow,
        Format = "mp4",
    };

    private static string MigrationId<TMigration>() where TMigration : Migration
        => typeof(TMigration).GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Id;
}
