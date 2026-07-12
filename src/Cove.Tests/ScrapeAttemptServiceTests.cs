using System.Text.Json;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public class ScrapeAttemptServiceTests
{
    [Fact]
    public async Task ApplyAttemptAsync_AudioAttemptAppliesSelectedFieldsAndNormalizesTags()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);

        var existingStudio = new Studio { Name = "Existing Studio" };
        var existingTag = new Tag { Name = "Legacy" };
        var existingPerformer = new Performer { Name = "Existing Performer" };

        var audio = new Audio
        {
            Title = "Current Title",
            Studio = existingStudio,
            Urls = [new AudioUrl { Url = "https://existing.example/audio" }],
            AudioTags = [new AudioTag { Tag = existingTag }],
            AudioPerformers = [new AudioPerformer { Performer = existingPerformer }],
            TagIds = [],
            PerformerIds = [],
        };

        db.Audios.Add(audio);
        await db.SaveChangesAsync();

        audio.TagIds = [existingTag.Id];
        audio.PerformerIds = [existingPerformer.Id];

        var attempt = new ScrapeAttempt
        {
            ScraperId = "tests.fake-scraper/audio",
            EntityType = EntityKinds.Audio,
            EntityId = audio.Id,
            InputKind = "url",
            InputJson = JsonSerializer.Serialize(new { url = "https://example.com/audio" }),
            ResultJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["Title"] = "Scraped Title",
                ["Artist"] = "Scraped Artist",
                ["URLs"] = new[] { "https://existing.example/audio", "https://new.example/audio" },
                ["TagNames"] = new[] { "[F4M]" },
                ["PerformerNames"] = new[] { "New Performer" },
                ["StudioName"] = "Scraped Studio",
            }),
        };

        db.ScrapeAttempts.Add(attempt);
        await db.SaveChangesAsync();

        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new NoOpTagProvenanceService(),
            null!,
            NullLogger<ScrapeAttemptService>.Instance);

        var result = await service.ApplyAttemptAsync(
            attempt.Id,
            new ApplyVideoScrapeAttemptDto(
                ReplaceFields: ["title"],
                CollectionModes: new Dictionary<string, string>
                {
                    ["urls"] = "merge",
                    ["tags"] = "replace",
                    ["performers"] = "merge",
                    ["studio"] = "replace",
                },
                CreateMissingTags: true,
                CreateMissingPerformers: true,
                CreateMissingStudio: true,
                MarkOrganized: true),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Applied", result!.Status);
        Assert.NotNull(result.EntitySnapshotJson);

        var updatedAudio = await db.Audios
            .Include(item => item.Urls)
            .Include(item => item.AudioTags).ThenInclude(item => item.Tag)
            .Include(item => item.AudioPerformers).ThenInclude(item => item.Performer)
            .Include(item => item.Studio)
            .SingleAsync(item => item.Id == audio.Id);

        Assert.Equal("Scraped Title", updatedAudio.Title);
        Assert.True(updatedAudio.Organized);
        Assert.Equal("Scraped Studio", updatedAudio.Studio?.Name);
        Assert.Equal(
            ["https://existing.example/audio", "https://new.example/audio"],
            updatedAudio.Urls.Select(item => item.Url).OrderBy(item => item).ToArray());
        Assert.Equal(["F4M"], updatedAudio.AudioTags.Select(item => item.Tag!.Name).OrderBy(item => item).ToArray());
        Assert.Equal(
            ["Existing Performer", "New Performer", "Scraped Artist"],
            updatedAudio.AudioPerformers.Select(item => item.Performer!.Name).OrderBy(item => item).ToArray());
        Assert.Single(updatedAudio.TagIds);
        Assert.Equal(3, updatedAudio.PerformerIds.Length);
    }

    [Fact]
    public async Task ApplyAttemptAsync_VideoPerformerMatchesExistingByAliasInsteadOfCreating()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);

        // Existing performer whose primary name differs from the scraped name, but carries it as an alias.
        var existingPerformer = new Performer
        {
            Name = "Jane Doe",
            Aliases = [new PerformerAlias { Alias = "Myra Moans" }],
        };
        db.Performers.Add(existingPerformer);

        var video = new Video { Title = "Current Title", TagIds = [], PerformerIds = [] };
        db.Videos.Add(video);
        await db.SaveChangesAsync();

        var attempt = new ScrapeAttempt
        {
            ScraperId = "tests.fake-scraper/video",
            EntityType = EntityKinds.Video,
            EntityId = video.Id,
            InputKind = "url",
            InputJson = JsonSerializer.Serialize(new { url = "https://example.com/scene" }),
            ResultJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["PerformerNames"] = new[] { "Myra Moans" },
            }),
        };
        db.ScrapeAttempts.Add(attempt);
        await db.SaveChangesAsync();

        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new NoOpTagProvenanceService(),
            null!,
            NullLogger<ScrapeAttemptService>.Instance);

        var result = await service.ApplyAttemptAsync(
            attempt.Id,
            new ApplyVideoScrapeAttemptDto(
                ReplaceFields: [],
                CollectionModes: new Dictionary<string, string> { ["performers"] = "merge" },
                CreateMissingPerformers: true,
                // The UI predicted "create", but an alias match must still win server-side.
                PerformerSelections: [new ScrapeCollectionItemSelectionDto("Myra Moans", "create")]),
            CancellationToken.None);

        Assert.NotNull(result);

        // No duplicate: still exactly one performer, and the video links to the aliased existing one.
        Assert.Equal(1, await db.Performers.CountAsync());
        Assert.False(await db.Performers.AnyAsync(performer => performer.Name == "Myra Moans"));

        var updatedVideo = await db.Videos
            .Include(item => item.VideoPerformers).ThenInclude(item => item.Performer)
            .SingleAsync(item => item.Id == video.Id);
        Assert.Equal(["Jane Doe"], updatedVideo.VideoPerformers.Select(item => item.Performer!.Name).ToArray());
    }

    [Fact]
    public async Task ResolveRelationsAsync_MatchesPerformerByAliasAndReportsMissingAsUnmatched()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);

        db.Performers.Add(new Performer
        {
            Name = "Jane Doe",
            Aliases = [new PerformerAlias { Alias = "Myra Moans" }],
        });
        db.Tags.Add(new Tag { Name = "Redhead" });
        await db.SaveChangesAsync();

        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new NoOpTagProvenanceService(),
            null!,
            NullLogger<ScrapeAttemptService>.Instance);

        var result = await service.ResolveRelationsAsync(
            new ResolveScrapeRelationsRequestDto
            {
                Performers = ["Myra Moans", "Nobody New"],
                Tags = ["Redhead", "Unseen Tag"],
            },
            CancellationToken.None);

        // Alias match reports the existing primary name; the unmatched name is simply absent.
        var performerMatch = Assert.Single(result.Performers);
        Assert.Equal("Myra Moans", performerMatch.Input);
        Assert.Equal("Jane Doe", performerMatch.MatchedName);

        var tagMatch = Assert.Single(result.Tags);
        Assert.Equal("Redhead", tagMatch.Input);
        Assert.Equal("Redhead", tagMatch.MatchedName);
    }

    [Fact]
    public async Task ResolveRelationsAsync_MatchesNamesStoredWithSurroundingWhitespace()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);

        // Names can be stored with stray whitespace (e.g. a scraper applied " Feet "). A later scrape
        // returning the trimmed "Feet" must still resolve to the existing entity, not predict "create".
        db.Tags.Add(new Tag { Name = " Feet " });
        db.Performers.Add(new Performer { Name = " Jane Doe " });
        await db.SaveChangesAsync();

        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new NoOpTagProvenanceService(),
            null!,
            NullLogger<ScrapeAttemptService>.Instance);

        var result = await service.ResolveRelationsAsync(
            new ResolveScrapeRelationsRequestDto
            {
                Performers = ["Jane Doe"],
                Tags = ["Feet"],
            },
            CancellationToken.None);

        // A match is found despite the stored whitespace (MatchedName echoes the raw stored value,
        // which the client normalizes via relationKey anyway).
        var tagMatch = Assert.Single(result.Tags);
        Assert.Equal("Feet", tagMatch.Input);
        Assert.Equal("Feet", tagMatch.MatchedName.Trim());

        var performerMatch = Assert.Single(result.Performers);
        Assert.Equal("Jane Doe", performerMatch.Input);
        Assert.Equal("Jane Doe", performerMatch.MatchedName.Trim());
    }

    [Fact]
    public async Task ResolveRelationsAsync_MatchesTagByAliasCaseInsensitively()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);

        // Tag "Feet" has the alias "Foot". A scrape returning lowercase "foot" must resolve to the
        // existing tag via its alias (case-insensitive) instead of predicting "will create".
        db.Tags.Add(new Tag { Name = "Feet", Aliases = [new TagAlias { Alias = "Foot" }] });
        await db.SaveChangesAsync();

        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new NoOpTagProvenanceService(),
            null!,
            NullLogger<ScrapeAttemptService>.Instance);

        var result = await service.ResolveRelationsAsync(
            new ResolveScrapeRelationsRequestDto { Tags = ["foot"], Performers = [] },
            CancellationToken.None);

        var tagMatch = Assert.Single(result.Tags);
        Assert.Equal("foot", tagMatch.Input);
        Assert.Equal("Feet", tagMatch.MatchedName);
    }

    [Fact]
    public async Task ApplyAttemptAsync_TextAttemptHonorsPerItemSelections()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);

        var existingTag = new Tag { Name = "Existing Tag" };
        var skippedExistingTag = new Tag { Name = "Skipped Existing Tag" };
        var existingPerformer = new Performer { Name = "Existing Performer" };
        db.Tags.AddRange(existingTag, skippedExistingTag);
        db.Performers.Add(existingPerformer);

        var text = new TextDocument
        {
            Title = "Current Text",
            TextTags = [new TextTag { Tag = skippedExistingTag }],
            TextPerformers = [],
            TagIds = [],
            PerformerIds = [],
        };
        db.TextDocuments.Add(text);
        await db.SaveChangesAsync();

        text.TagIds = [skippedExistingTag.Id];

        var attempt = new ScrapeAttempt
        {
            ScraperId = "tests.fake-scraper/text",
            EntityType = EntityKinds.Text,
            EntityId = text.Id,
            InputKind = "url",
            InputJson = JsonSerializer.Serialize(new { url = "https://example.com/story" }),
            ResultJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["TagNames"] = new[] { "Existing Tag", "Created Tag", "Skipped Tag" },
                ["PerformerNames"] = new[] { "Existing Performer", "Created Performer", "Skipped Performer" },
            }),
        };

        db.ScrapeAttempts.Add(attempt);
        await db.SaveChangesAsync();

        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new NoOpTagProvenanceService(),
            null!,
            NullLogger<ScrapeAttemptService>.Instance);

        var result = await service.ApplyAttemptAsync(
            attempt.Id,
            new ApplyVideoScrapeAttemptDto(
                ReplaceFields: [],
                CollectionModes: new Dictionary<string, string>
                {
                    ["tags"] = "replace",
                    ["performers"] = "replace",
                },
                CreateMissingTags: false,
                CreateMissingPerformers: false,
                TagSelections:
                [
                    new ScrapeCollectionItemSelectionDto("Existing Tag", "include"),
                    new ScrapeCollectionItemSelectionDto("Created Tag", "create"),
                    new ScrapeCollectionItemSelectionDto("Skipped Tag", "exclude"),
                ],
                PerformerSelections:
                [
                    new ScrapeCollectionItemSelectionDto("Existing Performer", "include"),
                    new ScrapeCollectionItemSelectionDto("Created Performer", "create"),
                    new ScrapeCollectionItemSelectionDto("Skipped Performer", "exclude"),
                ]),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("AppliedPartial", result!.Status);

        var updatedText = await db.TextDocuments
            .Include(item => item.TextTags).ThenInclude(item => item.Tag)
            .Include(item => item.TextPerformers).ThenInclude(item => item.Performer)
            .SingleAsync(item => item.Id == text.Id);

        Assert.Equal(["Created Tag", "Existing Tag"], updatedText.TextTags.Select(item => item.Tag!.Name).OrderBy(item => item).ToArray());
        Assert.Equal(["Created Performer", "Existing Performer"], updatedText.TextPerformers.Select(item => item.Performer!.Name).OrderBy(item => item).ToArray());
        Assert.False(await db.Tags.AnyAsync(item => item.Name == "Skipped Tag"));
        Assert.False(await db.Performers.AnyAsync(item => item.Name == "Skipped Performer"));
    }

    [Fact]
    public async Task ApplyAttemptAsync_VideoTagReplaceRemovesCurrentTagsNotInScrapedSet()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);

        var keptTag = new Tag { Name = "Big Dick" };          // scraped AND current — should survive
        var currentOnlyTag = new Tag { Name = "adult interview" }; // current, NOT scraped — must be removed on replace
        db.Tags.AddRange(keptTag, currentOnlyTag);

        var video = new Video
        {
            Title = "Current Video",
            VideoTags = [new VideoTag { Tag = keptTag }, new VideoTag { Tag = currentOnlyTag }],
            TagIds = [],
            PerformerIds = [],
        };
        db.Videos.Add(video);
        await db.SaveChangesAsync();
        video.TagIds = [keptTag.Id, currentOnlyTag.Id];

        var attempt = new ScrapeAttempt
        {
            ScraperId = "tests.fake-scraper/video",
            EntityType = EntityKinds.Video,
            EntityId = video.Id,
            InputKind = "url",
            InputJson = JsonSerializer.Serialize(new { url = "https://example.com/video" }),
            ResultJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["Tags"] = new[] { "Big Dick", "Toys" },
            }),
        };
        db.ScrapeAttempts.Add(attempt);
        await db.SaveChangesAsync();

        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new NoOpTagProvenanceService(),
            null!,
            NullLogger<ScrapeAttemptService>.Instance);

        await service.ApplyAttemptAsync(
            attempt.Id,
            new ApplyVideoScrapeAttemptDto(
                ReplaceFields: [],
                CollectionModes: new Dictionary<string, string> { ["tags"] = "replace" },
                CreateMissingTags: true,
                TagSelections:
                [
                    new ScrapeCollectionItemSelectionDto("Big Dick", "include"),
                    new ScrapeCollectionItemSelectionDto("Toys", "create"),
                ]),
            CancellationToken.None);

        var updated = await db.Videos
            .Include(item => item.VideoTags).ThenInclude(item => item.Tag)
            .SingleAsync(item => item.Id == video.Id);

        // Replace must leave ONLY the scraped tags; the current-only "adult interview" is gone.
        Assert.Equal(["Big Dick", "Toys"], updated.VideoTags.Select(item => item.Tag!.Name).OrderBy(item => item).ToArray());
        Assert.DoesNotContain(updated.TagIds, id => id == currentOnlyTag.Id);
    }

    [Fact]
    public async Task ApplyAttemptAsync_VideoTagReplacePrunesStaleSameSourceProvenance()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);

        const string scraperId = "tests.fake-scraper/video";
        var sourceKey = $"scraper:{scraperId}";

        // "adult interview" was applied by this scraper before but has no VideoTag now — it survives
        // only as a provenance row, which the effective-tag query surfaces as a derived tag.
        var staleTag = new Tag { Name = "adult interview" };
        var scrapedTag = new Tag { Name = "Big Dick" };
        db.Tags.AddRange(staleTag, scrapedTag);
        var video = new Video { Title = "Current Video", VideoTags = [], TagIds = [], PerformerIds = [] };
        db.Videos.Add(video);
        await db.SaveChangesAsync();

        db.TagApplications.Add(new TagApplication
        {
            HostType = AffinityHostType.Video,
            HostId = video.Id,
            TagId = staleTag.Id,
            SourceKey = sourceKey,
            SourceRunId = string.Empty,
            ModelKey = string.Empty,
        });
        await db.SaveChangesAsync();

        var attempt = new ScrapeAttempt
        {
            ScraperId = scraperId,
            EntityType = EntityKinds.Video,
            EntityId = video.Id,
            InputKind = "url",
            InputJson = JsonSerializer.Serialize(new { url = "https://example.com/video" }),
            ResultJson = JsonSerializer.Serialize(new Dictionary<string, object?> { ["Tags"] = new[] { "Big Dick" } }),
        };
        db.ScrapeAttempts.Add(attempt);
        await db.SaveChangesAsync();

        // Uses the real provenance service (not the no-op) so the prune actually runs.
        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new TagProvenanceService(db),
            null!,
            NullLogger<ScrapeAttemptService>.Instance);

        await service.ApplyAttemptAsync(
            attempt.Id,
            new ApplyVideoScrapeAttemptDto(
                ReplaceFields: [],
                CollectionModes: new Dictionary<string, string> { ["tags"] = "replace" },
                CreateMissingTags: true,
                TagSelections: [new ScrapeCollectionItemSelectionDto("Big Dick", "include")]),
            CancellationToken.None);

        // The stale scraper provenance must be pruned so "adult interview" no longer lingers as a derived tag.
        Assert.False(await db.TagApplications.AnyAsync(item => item.TagId == staleTag.Id && item.HostId == video.Id));
    }

    [Fact]
    public async Task ApplyAttemptAsync_GroupAttemptAppliesMetadataAndRelations()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);

        var existingTag = new Tag { Name = "Legacy" };
        var group = new Group
        {
            Name = "Current Group",
            Aliases = "Old Alias",
            Urls = [new GroupUrl { Url = "https://existing.example/group" }],
            GroupTags = [new GroupTag { Tag = existingTag }],
        };

        db.Groups.Add(group);
        await db.SaveChangesAsync();

        var attempt = new ScrapeAttempt
        {
            ScraperId = "tests.fake-scraper/group",
            EntityType = EntityKinds.Group,
            EntityId = group.Id,
            InputKind = "url",
            InputJson = JsonSerializer.Serialize(new { url = "https://example.com/group" }),
            ResultJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["Name"] = "Scraped Group",
                ["Aliases"] = new[] { "Alias A" },
                ["Duration"] = 120,
                ["Date"] = "2025-01-02",
                ["Director"] = "Scraped Director",
                ["Synopsis"] = "Scraped synopsis",
                ["URLs"] = new[] { "https://existing.example/group", "https://new.example/group" },
                ["TagNames"] = new[] { "New Tag" },
                ["StudioName"] = "Scraped Studio",
            }),
        };

        db.ScrapeAttempts.Add(attempt);
        await db.SaveChangesAsync();

        var tagProvenanceService = new NoOpTagProvenanceService();
        var groupApplyService = new GroupMetadataApplyService(
            db,
            null!,
            null!,
            new EventBus(),
            new NoOpUserEngagementService(),
            tagProvenanceService,
            null,
            NullLogger<GroupMetadataApplyService>.Instance);

        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            tagProvenanceService,
            groupApplyService,
            NullLogger<ScrapeAttemptService>.Instance);

        var result = await service.ApplyAttemptAsync(
            attempt.Id,
            new ApplyVideoScrapeAttemptDto(
                ReplaceFields: ["name", "duration", "date", "director", "details"],
                CollectionModes: new Dictionary<string, string>
                {
                    ["aliases"] = "merge",
                    ["urls"] = "merge",
                    ["tags"] = "replace",
                    ["studio"] = "replace",
                },
                CreateMissingTags: true,
                CreateMissingStudio: true),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Applied", result!.Status);
        Assert.NotNull(result.EntitySnapshotJson);

        var updatedGroup = await db.Groups
            .Include(item => item.Urls)
            .Include(item => item.GroupTags).ThenInclude(item => item.Tag)
            .Include(item => item.Studio)
            .SingleAsync(item => item.Id == group.Id);

        Assert.Equal("Scraped Group", updatedGroup.Name);
        Assert.Equal("Old Alias, Alias A", updatedGroup.Aliases);
        Assert.Equal(120, updatedGroup.Duration);
        Assert.Equal(new DateOnly(2025, 1, 2), updatedGroup.Date);
        Assert.Equal("Scraped Director", updatedGroup.Director);
        Assert.Equal("Scraped synopsis", updatedGroup.Synopsis);
        Assert.Equal("Scraped Studio", updatedGroup.Studio?.Name);
        Assert.Equal(
            ["https://existing.example/group", "https://new.example/group"],
            updatedGroup.Urls.Select(item => item.Url).OrderBy(item => item).ToArray());
        Assert.Equal(["New Tag"], updatedGroup.GroupTags.Select(item => item.Tag!.Name).OrderBy(item => item).ToArray());
    }

    [Fact]
    public async Task ApplyAttemptAsync_GalleryAttemptAppliesMetadataRelationsAndProvenance()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);

        var existingTag = new Tag { Name = "Legacy" };
        var existingPerformer = new Performer { Name = "Existing Performer" };
        var gallery = new Gallery
        {
            Title = "Current Gallery",
            Urls = [new GalleryUrl { Url = "https://existing.example/gallery" }],
            GalleryTags = [new GalleryTag { Tag = existingTag }],
            GalleryPerformers = [new GalleryPerformer { Performer = existingPerformer }],
            TagIds = [],
            PerformerIds = [],
        };

        db.Galleries.Add(gallery);
        await db.SaveChangesAsync();

        gallery.TagIds = [existingTag.Id];
        gallery.PerformerIds = [existingPerformer.Id];

        var attempt = new ScrapeAttempt
        {
            ScraperId = "tests.fake-scraper/gallery",
            EntityType = EntityKinds.Gallery,
            EntityId = gallery.Id,
            InputKind = "url",
            InputJson = JsonSerializer.Serialize(new { url = "https://example.com/gallery" }),
            ResultJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["Title"] = "Scraped Gallery",
                ["Code"] = "G-001",
                ["Details"] = "Scraped details",
                ["Photographer"] = "Scraped Photographer",
                ["Date"] = "2025-02-03",
                ["URLs"] = new[] { "https://existing.example/gallery", "https://new.example/gallery" },
                ["TagNames"] = new[] { "New Tag" },
                ["PerformerNames"] = new[] { "New Performer" },
                ["StudioName"] = "Scraped Studio",
            }),
        };

        db.ScrapeAttempts.Add(attempt);
        await db.SaveChangesAsync();

        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new NoOpTagProvenanceService(),
            null!,
            NullLogger<ScrapeAttemptService>.Instance,
            new FieldProvenanceService(db));

        var result = await service.ApplyAttemptAsync(
            attempt.Id,
            new ApplyVideoScrapeAttemptDto(
                ReplaceFields: ["title", "code", "details", "photographer", "date"],
                CollectionModes: new Dictionary<string, string>
                {
                    ["urls"] = "merge",
                    ["tags"] = "replace",
                    ["performers"] = "merge",
                    ["studio"] = "replace",
                },
                CreateMissingTags: true,
                CreateMissingPerformers: true,
                CreateMissingStudio: true,
                MarkOrganized: true),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Applied", result!.Status);
        Assert.NotNull(result.EntitySnapshotJson);

        var updatedGallery = await db.Galleries
            .Include(item => item.Urls)
            .Include(item => item.GalleryTags).ThenInclude(item => item.Tag)
            .Include(item => item.GalleryPerformers).ThenInclude(item => item.Performer)
            .Include(item => item.Studio)
            .SingleAsync(item => item.Id == gallery.Id);

        Assert.Equal("Scraped Gallery", updatedGallery.Title);
        Assert.Equal("G-001", updatedGallery.Code);
        Assert.Equal("Scraped details", updatedGallery.Details);
        Assert.Equal("Scraped Photographer", updatedGallery.Photographer);
        Assert.Equal(new DateOnly(2025, 2, 3), updatedGallery.Date);
        Assert.True(updatedGallery.Organized);
        Assert.Equal("Scraped Studio", updatedGallery.Studio?.Name);
        Assert.Equal(
            ["https://existing.example/gallery", "https://new.example/gallery"],
            updatedGallery.Urls.Select(item => item.Url).OrderBy(item => item).ToArray());
        Assert.Equal(["New Tag"], updatedGallery.GalleryTags.Select(item => item.Tag!.Name).OrderBy(item => item).ToArray());
        Assert.Equal(
            ["Existing Performer", "New Performer"],
            updatedGallery.GalleryPerformers.Select(item => item.Performer!.Name).OrderBy(item => item).ToArray());
        Assert.Single(updatedGallery.TagIds);
        Assert.Equal(2, updatedGallery.PerformerIds.Length);

        var provenance = await db.FieldProvenance
            .Where(item => item.HostType == AffinityHostType.Gallery && item.HostId == gallery.Id)
            .ToListAsync();

        Assert.Contains(provenance, item => item.FieldKey == "title" && item.SourceKey == "scraper:tests.fake-scraper/gallery");
        Assert.Contains(provenance, item => item.FieldKey == "tags" && item.SourceKey == "scraper:tests.fake-scraper/gallery");
    }

    private static CoveContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        return new CoveContext(options);
    }

    private sealed class NoOpTagProvenanceService : ITagProvenanceService
    {
        public Task RecordAsync(AffinityHostType hostType, int hostId, int tagId, string sourceKey, string? sourceRunId = null, string? modelKey = null, float? confidence = null, string? contextType = null, int? contextId = null, double? totalDurationSec = null, double? hostDurationSec = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RecordAsync(AffinityHostType hostType, int hostId, Tag tag, string sourceKey, string? sourceRunId = null, string? modelKey = null, float? confidence = null, string? contextType = null, int? contextId = null, double? totalDurationSec = null, double? hostDurationSec = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SyncTagSetAsync(AffinityHostType hostType, int hostId, IReadOnlyCollection<int> previousTagIds, IReadOnlyCollection<int> currentTagIds, string sourceKey = "user", CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveForHostAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveHostSourceApplicationsExceptAsync(AffinityHostType hostType, int hostId, string sourceKey, IReadOnlyCollection<int> keepTagIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyDictionary<int, List<TagProvenanceDto>>> GetLookupAsync(AffinityHostType hostType, int hostId, IReadOnlyCollection<int> tagIds, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<int, List<TagProvenanceDto>>>(new Dictionary<int, List<TagProvenanceDto>>());
    }
}
