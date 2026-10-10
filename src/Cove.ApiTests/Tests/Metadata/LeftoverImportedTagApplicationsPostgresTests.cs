using Cove.ApiTests.Infrastructure;
using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Cove.ApiTests.Tests.Metadata;

// The cleanup is raw PostgreSQL (a join on scrape attempts and a recount), which the in-memory provider
// cannot run.
public sealed class LeftoverImportedTagApplicationsPostgresTests
{
    private const string Endpoint = "https://metadata.example/graphql";
    private const string ScraperKey = "scraper:tests.fake-scraper/video";

    [Fact]
    public async Task RemovesOnlyTheRecordsAHandRemovalLeftBehindAndRecountsTheirTags()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(ct);
        await using var db = new CoveContext(new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(database.ConnectionString, options => options.UseVector())
            .Options);
        var migrations = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        var cleanupIndex = migrations.IndexOf(MigrationId<RemoveLeftoverImportedVideoTagApplications>());
        Assert.True(cleanupIndex > 0);
        await db.GetService<IMigrator>().MigrateAsync(migrations[cleanupIndex - 1], ct);

        // "Scraped" is also linked on a second video, recorded long enough on a third, and recorded too briefly
        // on a fourth, so after the cleanup it counts two videos.
        var scraped = new Tag { Name = "Scraped then removed", MinOccurrenceSec = 10 };
        var imported = new Tag { Name = "Imported then removed" };
        var linked = new Tag { Name = "Still linked" };
        var lookalike = new Tag { Name = "Recorded in a near-miss shape" };
        var fromExtension = new Tag { Name = "Added by an extension" };
        var fromUser = new Tag { Name = "Recorded for the user" };
        var inContext = new Tag { Name = "Scraped for a performer" };
        var onImage = new Tag { Name = "Scraped onto an image" };
        var video = new Video { Title = "Video", VideoTags = [new VideoTag { Tag = linked }] };
        var alsoLinked = new Video { Title = "Also linked", VideoTags = [new VideoTag { Tag = scraped }] };
        var longEnough = new Video { Title = "Recorded long enough" };
        var tooBrief = new Video { Title = "Recorded too briefly" };
        db.AddRange(scraped, imported, lookalike, fromExtension, fromUser, inContext, onImage, video, alsoLinked, longEnough, tooBrief);
        await db.SaveChangesAsync(ct);

        var attempt = new ScrapeAttempt { ScraperId = "tests.fake-scraper/video", EntityType = "video", EntityId = video.Id, InputKind = "url", InputJson = "{}" };
        var otherVideosAttempt = new ScrapeAttempt { ScraperId = "tests.fake-scraper/video", EntityType = "video", EntityId = alsoLinked.Id, InputKind = "url", InputJson = "{}" };
        db.ScrapeAttempts.AddRange(attempt, otherVideosAttempt);
        await db.SaveChangesAsync(ct);

        TagApplication Record(Tag tag, string sourceKey, string sourceRunId = "", int? hostId = null, double? durationSec = null)
            => new() { HostType = AffinityHostType.Video, HostId = hostId ?? video.Id, TagId = tag.Id, SourceKey = sourceKey, SourceRunId = sourceRunId, TotalDurationSec = durationSec };
        var leftovers = new[]
        {
            // What a scrape apply and a metadata-server import left after the tag was taken off by hand.
            Record(scraped, ScraperKey, attempt.Id.ToString()),
            Record(imported, $"metadata:{Endpoint}", Endpoint),
        };
        var kept = new[]
        {
            Record(linked, ScraperKey, attempt.Id.ToString()),
            Record(scraped, "ext:ai.tagging", "run-1", longEnough.Id, durationSec: 20),
            Record(scraped, "ext:ai.tagging", "run-1", tooBrief.Id, durationSec: 5),
            // Shapes close to an import's that it did not write: a run that is no attempt, another video's
            // attempt, a source without an endpoint, and an endpoint that is not the source's.
            Record(lookalike, ScraperKey, "run-1"),
            Record(lookalike, ScraperKey, otherVideosAttempt.Id.ToString()),
            Record(lookalike, "metadata:default"),
            Record(lookalike, "metadata:https://a.example/graphql", "https://b.example/graphql"),
            Record(fromExtension, "ext:ai.tagging", "run-1"),
            Record(fromUser, "user"),
            new TagApplication { HostType = AffinityHostType.Video, HostId = video.Id, TagId = inContext.Id, ContextType = "performer", ContextId = 1, SourceKey = ScraperKey, SourceRunId = attempt.Id.ToString() },
            new TagApplication { HostType = AffinityHostType.Image, HostId = video.Id, TagId = onImage.Id, SourceKey = ScraperKey, SourceRunId = attempt.Id.ToString() },
        };
        db.TagApplications.AddRange([.. leftovers, .. kept]);
        await db.SaveChangesAsync(ct);
        var keptIds = kept.Select(application => application.Id).Order().ToList();
        await db.Tags.Where(tag => tag.Id == scraped.Id || tag.Id == imported.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(tag => tag.VideoCount, 99), ct);

        await db.Database.MigrateAsync(ct);

        db.ChangeTracker.Clear();
        var remaining = await db.TagApplications.Select(application => application.Id).OrderBy(id => id).ToListAsync(ct);
        Assert.Equal(keptIds, remaining);
        var counts = await db.Tags.ToDictionaryAsync(tag => tag.Id, tag => tag.VideoCount, ct);
        Assert.Equal(2, counts[scraped.Id]);
        Assert.Equal(0, counts[imported.Id]);
    }

    [Fact]
    public async Task RemovesOnlyTheScrapeRecordsAudioAndTextEditsLeftBehind()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(ct);
        await using var db = new CoveContext(new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(database.ConnectionString, options => options.UseVector())
            .Options);
        var migrations = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        var cleanupIndex = migrations.IndexOf(MigrationId<RemoveLeftoverScrapedAudioAndTextTagApplications>());
        Assert.True(cleanupIndex > 0);
        await db.GetService<IMigrator>().MigrateAsync(migrations[cleanupIndex - 1], ct);

        var removed = new Tag { Name = "Scraped then removed" };
        var linked = new Tag { Name = "Still linked" };
        var lookalike = new Tag { Name = "Recorded in a near-miss shape" };
        var audio = new Audio { Title = "Audio", AudioTags = [new AudioTag { Tag = linked }] };
        var text = new TextDocument { Title = "Text", TextTags = [new TextTag { Tag = linked }] };
        var otherAudio = new Audio { Title = "Other audio" };
        db.AddRange(removed, lookalike, audio, text, otherAudio);
        await db.SaveChangesAsync(ct);

        ScrapeAttempt Attempt(string entityType, int entityId)
            => new() { ScraperId = "tests.fake-scraper/item", EntityType = entityType, EntityId = entityId, InputKind = "url", InputJson = "{}" };
        var audioAttempt = Attempt("audio", audio.Id);
        var textAttempt = Attempt("text", text.Id);
        var otherAudioAttempt = Attempt("audio", otherAudio.Id);
        // A video attempt whose entity id happens to be the text's.
        var videoAttempt = Attempt("video", text.Id);
        db.ScrapeAttempts.AddRange(audioAttempt, textAttempt, otherAudioAttempt, videoAttempt);
        await db.SaveChangesAsync(ct);

        TagApplication Record(AffinityHostType hostType, int hostId, Tag tag, string sourceKey, string sourceRunId)
            => new() { HostType = hostType, HostId = hostId, TagId = tag.Id, SourceKey = sourceKey, SourceRunId = sourceRunId };
        var leftovers = new[]
        {
            Record(AffinityHostType.Audio, audio.Id, removed, ScraperKey, audioAttempt.Id.ToString()),
            Record(AffinityHostType.Text, text.Id, removed, ScraperKey, textAttempt.Id.ToString()),
        };
        var kept = new[]
        {
            Record(AffinityHostType.Audio, audio.Id, linked, ScraperKey, audioAttempt.Id.ToString()),
            Record(AffinityHostType.Text, text.Id, linked, ScraperKey, textAttempt.Id.ToString()),
            Record(AffinityHostType.Audio, audio.Id, lookalike, ScraperKey, "run-1"),
            Record(AffinityHostType.Audio, audio.Id, lookalike, ScraperKey, otherAudioAttempt.Id.ToString()),
            Record(AffinityHostType.Text, text.Id, lookalike, ScraperKey, videoAttempt.Id.ToString()),
            Record(AffinityHostType.Audio, audio.Id, lookalike, "ext:ai.tagging", audioAttempt.Id.ToString()),
            Record(AffinityHostType.Text, text.Id, lookalike, "user", ""),
        };
        db.TagApplications.AddRange([.. leftovers, .. kept]);
        await db.SaveChangesAsync(ct);
        var keptIds = kept.Select(application => application.Id).Order().ToList();

        await db.Database.MigrateAsync(ct);

        db.ChangeTracker.Clear();
        var remaining = await db.TagApplications.Select(application => application.Id).OrderBy(id => id).ToListAsync(ct);
        Assert.Equal(keptIds, remaining);
    }

    private static string MigrationId<TMigration>() where TMigration : Migration
        => typeof(TMigration).GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>().Single().Id;
}
