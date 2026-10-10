using System.Text.Json;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public class ScrapeAttemptServiceTests
{
    [Fact]
    public async Task ApplyAttemptAsync_DoesNotHydrateExistingVideoPerformer()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);

        var existingPerformer = new Performer { Name = "Existing Performer", Gender = Cove.Core.Enums.GenderEnum.Female };
        var video = new Video { Title = "Current Title", TagIds = [], PerformerIds = [] };
        db.AddRange(existingPerformer, video);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var attempt = new ScrapeAttempt
        {
            ScraperId = "tests.fake-scraper/video",
            EntityType = EntityKinds.Video,
            EntityId = video.Id,
            InputKind = "url",
            InputJson = JsonSerializer.Serialize(new { url = "https://example.com/scene" }),
            ResultJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["Performers"] = new[]
                {
                    new { Name = "Existing Performer", URL = "https://example.com/performer/existing" },
                },
            }),
        };
        db.ScrapeAttempts.Add(attempt);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // A null performer scraper makes any attempted hydration fail, proving that existing
        // performers are linked without being rescraped.
        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new NoOpTagProvenanceService(),
            null!,
            new EventBus(),
            NullLogger<ScrapeAttemptService>.Instance);

        var result = await service.ApplyAttemptAsync(
            attempt.Id,
            new ApplyVideoScrapeAttemptDto(
                ReplaceFields: [],
                CollectionModes: new Dictionary<string, string> { ["performers"] = "merge" },
                CreateMissingPerformers: true,
                HydratePerformers: true),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(Cove.Core.Enums.GenderEnum.Female, existingPerformer.Gender);
        Assert.Empty(existingPerformer.Urls);
        Assert.Equal(existingPerformer.Id, Assert.Single(video.VideoPerformers).PerformerId);
    }

    [Fact]
    public async Task ApplyAttemptAsync_AppliesReviewEditsOnTopOfTheScrape()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);

        var currentTag = new Tag { Name = "Current" };
        var libraryTag = new Tag { Name = "From the library" };
        var currentPerformer = new Performer { Name = "Current Performer" };
        var libraryPerformer = new Performer { Name = "Library Performer" };
        var video = new Video
        {
            Title = "Current Title",
            VideoTags = [new VideoTag { Tag = currentTag }],
            VideoPerformers = [new VideoPerformer { Performer = currentPerformer }],
            TagIds = [],
            PerformerIds = [],
        };
        db.AddRange(libraryTag, libraryPerformer, video);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var attempt = new ScrapeAttempt
        {
            ScraperId = "tests.fake-scraper/video",
            EntityType = EntityKinds.Video,
            EntityId = video.Id,
            InputKind = "url",
            InputJson = JsonSerializer.Serialize(new { url = "https://example.com/scene" }),
            ResultJson = JsonSerializer.Serialize(new Dictionary<string, object?> { ["Tags"] = new[] { new { Name = "Scraped" } } }),
        };
        db.ScrapeAttempts.Add(attempt);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var service = new ScrapeAttemptService(db, null!, null!, null!, new NoOpTagProvenanceService(), null!, new EventBus(), NullLogger<ScrapeAttemptService>.Instance);

        // The scrape adds "Scraped"; the review also picked a library tag and performer through search and
        // took the current tag and performer off, as the edit form would. An unknown id is ignored.
        var result = await service.ApplyAttemptAsync(
            attempt.Id,
            new ApplyVideoScrapeAttemptDto(ReplaceFields: [], CollectionModes: new Dictionary<string, string> { ["tags"] = "merge" }, CreateMissingTags: true)
            {
                AddedTagIds = [libraryTag.Id, int.MaxValue],
                RemovedTagIds = [currentTag.Id],
                AddedPerformerIds = [libraryPerformer.Id],
                RemovedPerformerIds = [currentPerformer.Id],
            },
            CancellationToken.None);

        Assert.NotNull(result);
        var tagNames = video.VideoTags.Select(link => link.Tag?.Name ?? db.Tags.Find(link.TagId)!.Name).OrderBy(name => name).ToList();
        Assert.Equal(["From the library", "Scraped"], tagNames);
        Assert.Equal(libraryPerformer.Id, Assert.Single(video.VideoPerformers).PerformerId);
    }

    [Fact]
    public async Task ApplyAttemptAsync_HydratesNewNamedVideoPerformer()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);

        var video = new Video { Title = "Current Title", TagIds = [], PerformerIds = [] };
        db.Videos.Add(video);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var attempt = new ScrapeAttempt
        {
            ScraperId = "tests.fake-scraper/video",
            EntityType = EntityKinds.Video,
            EntityId = video.Id,
            InputKind = "url",
            InputJson = JsonSerializer.Serialize(new { url = "https://example.com/scene" }),
            ResultJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["Performers"] = new[]
                {
                    new { Name = "New Performer", URL = "https://example.com/performer/new" },
                },
            }),
        };
        db.ScrapeAttempts.Add(attempt);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var extensionManager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "test",
        });
        var scraperService = new ScraperService(
            new CoveConfiguration(),
            NullLogger<ScraperService>.Instance,
            new EmptyHttpClientFactory(),
            extensionManager);
        var performerScrapeService = new PerformerScrapeService(db, scraperService);
        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            performerScrapeService,
            new NoOpTagProvenanceService(),
            null!,
            new EventBus(),
            NullLogger<ScrapeAttemptService>.Instance);

        var result = await service.ApplyAttemptAsync(
            attempt.Id,
            new ApplyVideoScrapeAttemptDto(
                ReplaceFields: [],
                CollectionModes: new Dictionary<string, string> { ["performers"] = "merge" },
                CreateMissingPerformers: true,
                HydratePerformers: true),
            CancellationToken.None);

        Assert.NotNull(result);
        var performer = await db.Performers.Include(item => item.Urls).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("https://example.com/performer/new", Assert.Single(performer.Urls).Url);
        Assert.Equal(performer.Id, Assert.Single(video.VideoPerformers).PerformerId);
    }

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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var eventBus = new EventBus();
        var publishedEvents = new List<EntityEvent>();
        using var subscription = eventBus.Subscribe<EntityEvent>(publishedEvents.Add);
        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new NoOpTagProvenanceService(),
            null!,
            eventBus,
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
            .SingleAsync(item => item.Id == audio.Id, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Scraped Title", updatedAudio.Title);
        var publishedEvent = Assert.Single(publishedEvents);
        Assert.Equal(EventType.AudioUpdated, publishedEvent.Type);
        Assert.Equal(audio.Id, publishedEvent.EntityId);
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
    public async Task ApplyAttemptAsync_VideoPerformerDoesNotTreatANonUniqueAliasAsIdentity()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);

        // Performer aliases are deliberately non-unique, so they cannot identify a relation owner.
        var existingPerformer = new Performer
        {
            Name = "Jane Doe",
            Aliases = [new PerformerAlias { Alias = "Myra Moans" }],
        };
        db.Performers.Add(existingPerformer);

        var video = new Video { Title = "Current Title", TagIds = [], PerformerIds = [] };
        db.Videos.Add(video);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new NoOpTagProvenanceService(),
            null!,
            new EventBus(),
            NullLogger<ScrapeAttemptService>.Instance);

        var result = await service.ApplyAttemptAsync(
            attempt.Id,
            new ApplyVideoScrapeAttemptDto(
                ReplaceFields: [],
                CollectionModes: new Dictionary<string, string> { ["performers"] = "merge" },
                CreateMissingPerformers: true,
                PerformerSelections: [new ScrapeCollectionItemSelectionDto("Myra Moans", "create")]),
            CancellationToken.None);

        Assert.NotNull(result);

        Assert.Equal(2, await db.Performers.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await db.Performers.AnyAsync(performer => performer.Name == "Myra Moans" && performer.Disambiguation == null, cancellationToken: TestContext.Current.CancellationToken));

        var updatedVideo = await db.Videos
            .Include(item => item.VideoPerformers).ThenInclude(item => item.Performer)
            .SingleAsync(item => item.Id == video.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["Myra Moans"], updatedVideo.VideoPerformers.Select(item => item.Performer!.Name).ToArray());
    }

    [Fact]
    public async Task ResolveRelationsAsync_DoesNotMatchPerformerAliases()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);

        db.Performers.Add(new Performer
        {
            Name = "Jane Doe",
            Aliases = [new PerformerAlias { Alias = "Myra Moans" }],
        });
        db.Tags.Add(new Tag { Name = "Redhead" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new NoOpTagProvenanceService(),
            null!,
            new EventBus(),
            NullLogger<ScrapeAttemptService>.Instance);

        var result = await service.ResolveRelationsAsync(
            new ResolveScrapeRelationsRequestDto
            {
                Performers = ["Myra Moans", "Nobody New"],
                Tags = ["Redhead", "Unseen Tag"],
            },
            CancellationToken.None);

        Assert.Empty(result.Performers);

        var tagMatch = Assert.Single(result.Tags);
        Assert.Equal("Redhead", tagMatch.Input);
        Assert.Equal("Redhead", tagMatch.MatchedName);
    }

    [Fact]
    public async Task ResolveRelationsAsync_ResolvesStudiosByNameAndAlias()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);

        db.Studios.Add(new Studio { Name = "Known Studio" });
        db.Studios.Add(new Studio { Name = "Canonical Studio", Aliases = [new StudioAlias { Alias = "Studio Nickname" }] });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new NoOpTagProvenanceService(),
            null!,
            new EventBus(),
            NullLogger<ScrapeAttemptService>.Instance);

        var result = await service.ResolveRelationsAsync(
            new ResolveScrapeRelationsRequestDto { Studios = ["known studio", "Studio Nickname", "Unseen Studio"] },
            CancellationToken.None);

        Assert.Equal(
            [("Studio Nickname", "Canonical Studio"), ("known studio", "Known Studio")],
            result.Studios.Select(match => (match.Input, match.MatchedName)).OrderBy(match => match.Item1, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ApplyAttemptAsync_NameOnlyPerformerCreatesTheNullDisambiguationIdentity()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);
        var disambiguated = new Performer { Name = "Shared name", Disambiguation = "Specific person" };
        var video = new Video { Title = "Current title", TagIds = [], PerformerIds = [] };
        db.AddRange(disambiguated, video);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var attempt = new ScrapeAttempt
        {
            ScraperId = "tests.fake-scraper/video",
            EntityType = EntityKinds.Video,
            EntityId = video.Id,
            InputKind = "url",
            InputJson = "{}",
            ResultJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["PerformerNames"] = new[] { "Shared name" },
            }),
        };
        db.ScrapeAttempts.Add(attempt);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new NoOpTagProvenanceService(),
            null!,
            new EventBus(),
            NullLogger<ScrapeAttemptService>.Instance);

        await service.ApplyAttemptAsync(
            attempt.Id,
            new ApplyVideoScrapeAttemptDto(
                ReplaceFields: [],
                CollectionModes: new Dictionary<string, string> { ["performers"] = "merge" },
                CreateMissingPerformers: true,
                PerformerSelections: [new ScrapeCollectionItemSelectionDto("Shared name", "create")]),
            CancellationToken.None);

        var performers = await db.Performers.OrderBy(item => item.Id).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, performers.Count);
        var nameOnly = Assert.Single(performers, performer => performer.Disambiguation == null);
        var updatedVideo = await db.Videos.Include(item => item.VideoPerformers).SingleAsync(item => item.Id == video.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(updatedVideo.VideoPerformers, link => link.PerformerId == nameOnly.Id);
        Assert.DoesNotContain(updatedVideo.VideoPerformers, link => link.PerformerId == disambiguated.Id);
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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new NoOpTagProvenanceService(),
            null!,
            new EventBus(),
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
    public async Task ResolveRelationsAsync_EchoesEveryRequestedSpellingAsSent()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);
        db.Tags.Add(new Tag { Name = "Blonde" });
        db.Performers.Add(new Performer { Name = "Jane Doe" });
        db.Studios.Add(new Studio { Name = "Palladium" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new NoOpTagProvenanceService(),
            null!,
            new EventBus(),
            NullLogger<ScrapeAttemptService>.Instance);

        // Several callers asked at once, each with its own spelling; each must find its own back.
        var result = await service.ResolveRelationsAsync(
            new ResolveScrapeRelationsRequestDto
            {
                Performers = [" Jane Doe", "JANE DOE", "Jane Doe", "Someone else"],
                Tags = ["Blonde", " blonde ", "\uFEFFBlonde", "BLONDE", "Blonde", "Brand new"],
                Studios = ["palladium ", "Palladium"],
            },
            CancellationToken.None);

        Assert.Equal([" Jane Doe", "JANE DOE", "Jane Doe"], result.Performers.Select(match => match.Input));
        Assert.All(result.Performers, match => Assert.Equal("Jane Doe", match.MatchedName));
        Assert.Equal(["Blonde", " blonde ", "BLONDE"], result.Tags.Select(match => match.Input));
        Assert.All(result.Tags, match => Assert.Equal("Blonde", match.MatchedName));
        Assert.Equal(["palladium ", "Palladium"], result.Studios.Select(match => match.Input));
    }

    [Fact]
    public async Task ResolveRelationsAsync_MatchesTagByAliasCaseInsensitively()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);

        // Tag "Feet" has the alias "Foot". A scrape returning lowercase "foot" must resolve to the
        // existing tag via its alias (case-insensitive) instead of predicting "will create".
        db.Tags.Add(new Tag { Name = "Feet", Aliases = [new TagAlias { Alias = "Foot" }] });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new NoOpTagProvenanceService(),
            null!,
            new EventBus(),
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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new NoOpTagProvenanceService(),
            null!,
            new EventBus(),
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
            .SingleAsync(item => item.Id == text.Id, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["Created Tag", "Existing Tag"], updatedText.TextTags.Select(item => item.Tag!.Name).OrderBy(item => item).ToArray());
        Assert.Equal(["Created Performer", "Existing Performer"], updatedText.TextPerformers.Select(item => item.Performer!.Name).OrderBy(item => item).ToArray());
        Assert.False(await db.Tags.AnyAsync(item => item.Name == "Skipped Tag", cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await db.Performers.AnyAsync(item => item.Name == "Skipped Performer", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApplyAttemptAsync_VideoRecordsOnlyTheTagsAndPerformersActuallyAttached()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);
        db.Tags.Add(new Tag { Name = "Existing" });
        db.Performers.Add(new Performer { Name = "Known Performer" });
        var video = new Video { Title = "Current Video", TagIds = [], PerformerIds = [] };
        db.Videos.Add(video);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var attempt = new ScrapeAttempt
        {
            ScraperId = "tests.fake-scraper/video",
            EntityType = EntityKinds.Video,
            EntityId = video.Id,
            InputKind = "url",
            InputJson = JsonSerializer.Serialize(new { url = "https://example.com/video" }),
            ResultJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["Tags"] = new[] { "Existing", "Deleted Since" },
                ["Performers"] = new[] { new { Name = "Known Performer" }, new { Name = "Gone Performer" } },
            }),
        };
        db.ScrapeAttempts.Add(attempt);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new NoOpTagProvenanceService(),
            null!,
            new EventBus(),
            NullLogger<ScrapeAttemptService>.Instance,
            new FieldProvenanceService(db));

        // Both "include"s were chosen while the names were in the library; the second of each has gone
        // since, and nothing is created.
        var result = await service.ApplyAttemptAsync(
            attempt.Id,
            new ApplyVideoScrapeAttemptDto(
                ReplaceFields: [],
                CollectionModes: new Dictionary<string, string> { ["tags"] = "merge", ["performers"] = "merge" },
                CreateMissingTags: false,
                CreateMissingPerformers: false,
                TagSelections:
                [
                    new ScrapeCollectionItemSelectionDto("Existing", "include"),
                    new ScrapeCollectionItemSelectionDto("Deleted Since", "include"),
                ],
                PerformerSelections:
                [
                    new ScrapeCollectionItemSelectionDto("Known Performer", "include"),
                    new ScrapeCollectionItemSelectionDto("Gone Performer", "include"),
                ]),
            CancellationToken.None);

        Assert.Equal("AppliedPartial", result!.Status);
        var provenance = await db.FieldProvenance
            .Where(item => item.HostType == AffinityHostType.Video && item.HostId == video.Id)
            .ToDictionaryAsync(item => item.FieldKey, item => item.ValueJson, TestContext.Current.CancellationToken);
        Assert.Equal(["Existing"], JsonSerializer.Deserialize<string[]>(provenance["tags"]!));
        Assert.Equal(["Known Performer"], JsonSerializer.Deserialize<string[]>(provenance["performers"]!));
    }

    [Fact]
    public async Task ApplyAttemptAsync_VideoHistoryLeavesOutATagRemovedByHandInTheSameApply()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);
        var kept = new Tag { Name = "Kept" };
        var removed = new Tag { Name = "Removed" };
        db.Tags.AddRange(kept, removed);
        var video = new Video { Title = "Current Video", VideoTags = [new VideoTag { Tag = removed }], TagIds = [], PerformerIds = [] };
        db.Videos.Add(video);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var attempt = new ScrapeAttempt
        {
            ScraperId = "tests.fake-scraper/video",
            EntityType = EntityKinds.Video,
            EntityId = video.Id,
            InputKind = "url",
            InputJson = JsonSerializer.Serialize(new { url = "https://example.com/video" }),
            ResultJson = JsonSerializer.Serialize(new Dictionary<string, object?> { ["Tags"] = new[] { "Kept", "Removed" } }),
        };
        db.ScrapeAttempts.Add(attempt);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new ScrapeAttemptService(db, null!, null!, null!, new NoOpTagProvenanceService(), null!, new EventBus(), NullLogger<ScrapeAttemptService>.Instance, new FieldProvenanceService(db));

        await service.ApplyAttemptAsync(
            attempt.Id,
            new ApplyVideoScrapeAttemptDto(
                ReplaceFields: [],
                CollectionModes: new Dictionary<string, string> { ["tags"] = "merge" },
                CreateMissingTags: false,
                TagSelections:
                [
                    new ScrapeCollectionItemSelectionDto("Kept", "include"),
                    new ScrapeCollectionItemSelectionDto("Removed", "include"),
                ])
            {
                RemovedTagIds = [removed.Id],
            },
            CancellationToken.None);

        var history = await db.FieldProvenance
            .Where(item => item.HostType == AffinityHostType.Video && item.HostId == video.Id && item.FieldKey == "tags")
            .Select(item => item.ValueJson)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["Kept"], JsonSerializer.Deserialize<string[]>(history!));
    }

    [Fact]
    public async Task ApplyAttemptAsync_ScrapedTagRemovedByHandInTheSameApplyLeavesTheVideo()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);
        var kept = new Tag { Name = "Kept" };
        var removed = new Tag { Name = "Removed" };
        db.Tags.AddRange(kept, removed);
        var video = new Video { Title = "Current Video", VideoTags = [new VideoTag { Tag = removed }], TagIds = [], PerformerIds = [] };
        db.Videos.Add(video);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var attempt = new ScrapeAttempt
        {
            ScraperId = "tests.fake-scraper/video",
            EntityType = EntityKinds.Video,
            EntityId = video.Id,
            InputKind = "url",
            InputJson = JsonSerializer.Serialize(new { url = "https://example.com/video" }),
            ResultJson = JsonSerializer.Serialize(new Dictionary<string, object?> { ["Tags"] = new[] { "Kept", "Removed" } }),
        };
        db.ScrapeAttempts.Add(attempt);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new ScrapeAttemptService(db, null!, null!, null!, new TagProvenanceService(db), null!, new EventBus(), NullLogger<ScrapeAttemptService>.Instance);

        // "Removed" is on the video and scraped again; the review takes it off by hand.
        await service.ApplyAttemptAsync(
            attempt.Id,
            new ApplyVideoScrapeAttemptDto(
                ReplaceFields: [],
                CollectionModes: new Dictionary<string, string> { ["tags"] = "merge" },
                CreateMissingTags: false,
                TagSelections:
                [
                    new ScrapeCollectionItemSelectionDto("Kept", "include"),
                    new ScrapeCollectionItemSelectionDto("Removed", "include"),
                ])
            {
                RemovedTagIds = [removed.Id],
            },
            CancellationToken.None);

        // The tags a video shows are its links plus every tag a source recorded on it, so the scrape's
        // record of "Removed" must go with the link.
        var shownTagIds = await EffectiveHostTagQuery.ForHostType(db, AffinityHostType.Video)
            .Where(row => row.HostId == video.Id)
            .Select(row => row.TagId)
            .Distinct()
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([kept.Id], shownTagIds);
    }

    [Theory]
    [InlineData(AffinityHostType.Video)]
    [InlineData(AffinityHostType.Audio)]
    [InlineData(AffinityHostType.Text)]
    public async Task ApplyAttemptAsync_ReplaceTakesOffATagItDropsWhateverRecordedIt(AffinityHostType hostType)
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);
        var kept = new Tag { Name = "Kept" };
        var dropped = new Tag { Name = "Added by hand" };
        var derived = new Tag { Name = "Added by an extension" };
        db.Tags.AddRange(kept, dropped, derived);
        // The item has two linked tags and one an extension recorded without a link.
        BaseEntity entity = hostType switch
        {
            AffinityHostType.Video => db.Videos.Add(new Video { Title = "Item", VideoTags = [new VideoTag { Tag = kept }, new VideoTag { Tag = dropped }], TagIds = [], PerformerIds = [] }).Entity,
            AffinityHostType.Audio => db.Audios.Add(new Audio { Title = "Item", AudioTags = [new AudioTag { Tag = kept }, new AudioTag { Tag = dropped }], TagIds = [], PerformerIds = [] }).Entity,
            _ => db.TextDocuments.Add(new TextDocument { Title = "Item", TextTags = [new TextTag { Tag = kept }, new TextTag { Tag = dropped }], TagIds = [], PerformerIds = [] }).Entity,
        };
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var hostId = entity.Id;
        db.TagApplications.AddRange(
            new TagApplication { HostType = hostType, HostId = hostId, TagId = dropped.Id, SourceKey = "user" },
            new TagApplication { HostType = hostType, HostId = hostId, TagId = derived.Id, SourceKey = "ext:ai.tagging", SourceRunId = "run-1" });
        var attempt = new ScrapeAttempt
        {
            ScraperId = "tests.fake-scraper/item",
            EntityType = hostType switch { AffinityHostType.Video => EntityKinds.Video, AffinityHostType.Audio => EntityKinds.Audio, _ => EntityKinds.Text },
            EntityId = hostId,
            InputKind = "url",
            InputJson = JsonSerializer.Serialize(new { url = "https://example.com/item" }),
            ResultJson = JsonSerializer.Serialize(new Dictionary<string, object?> { ["Tags"] = new[] { "Kept" } }),
        };
        db.ScrapeAttempts.Add(attempt);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new ScrapeAttemptService(db, null!, null!, null!, new TagProvenanceService(db), null!, new EventBus(), NullLogger<ScrapeAttemptService>.Instance);

        await service.ApplyAttemptAsync(
            attempt.Id,
            new ApplyVideoScrapeAttemptDto(ReplaceFields: [], CollectionModes: new Dictionary<string, string> { ["tags"] = "replace" }, CreateMissingTags: false),
            CancellationToken.None);

        // The replace drops the hand-added tag; its record must go with the link or it keeps the tag shown.
        // The extension's tag was never linked and stays, as it does after a removal in the edit form.
        var shownTagIds = await EffectiveHostTagQuery.ForHostType(db, hostType)
            .Where(row => row.HostId == hostId)
            .Select(row => row.TagId)
            .Distinct()
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { kept.Id, derived.Id }.Order(), shownTagIds.Order());
    }

    [Theory]
    [InlineData(AffinityHostType.Video)]
    [InlineData(AffinityHostType.Audio)]
    [InlineData(AffinityHostType.Text)]
    public async Task ApplyAttemptAsync_ReplaceForgetsWhatTheSameScraperRecordedBeforeAndDoesNotApplyNow(AffinityHostType hostType)
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);
        var kept = new Tag { Name = "Kept" };
        var stale = new Tag { Name = "Scraped earlier" };
        var elsewhere = new Tag { Name = "Scraped by another scraper" };
        db.Tags.AddRange(kept, stale, elsewhere);
        // Earlier scrapes recorded two tags the item has no link for: one from this scraper, one from another.
        BaseEntity entity = hostType switch
        {
            AffinityHostType.Video => db.Videos.Add(new Video { Title = "Item", TagIds = [], PerformerIds = [] }).Entity,
            AffinityHostType.Audio => db.Audios.Add(new Audio { Title = "Item", TagIds = [], PerformerIds = [] }).Entity,
            _ => db.TextDocuments.Add(new TextDocument { Title = "Item", TagIds = [], PerformerIds = [] }).Entity,
        };
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var hostId = entity.Id;
        db.TagApplications.AddRange(
            new TagApplication { HostType = hostType, HostId = hostId, TagId = stale.Id, SourceKey = "scraper:tests.fake-scraper/item", SourceRunId = "earlier-attempt" },
            new TagApplication { HostType = hostType, HostId = hostId, TagId = elsewhere.Id, SourceKey = "scraper:tests.other-scraper/item", SourceRunId = "other-attempt" });
        var attempt = new ScrapeAttempt
        {
            ScraperId = "tests.fake-scraper/item",
            EntityType = hostType switch { AffinityHostType.Video => EntityKinds.Video, AffinityHostType.Audio => EntityKinds.Audio, _ => EntityKinds.Text },
            EntityId = hostId,
            InputKind = "url",
            InputJson = JsonSerializer.Serialize(new { url = "https://example.com/item" }),
            ResultJson = JsonSerializer.Serialize(new Dictionary<string, object?> { ["Tags"] = new[] { "Kept" } }),
        };
        db.ScrapeAttempts.Add(attempt);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new ScrapeAttemptService(db, null!, null!, null!, new TagProvenanceService(db), null!, new EventBus(), NullLogger<ScrapeAttemptService>.Instance);

        await service.ApplyAttemptAsync(
            attempt.Id,
            new ApplyVideoScrapeAttemptDto(ReplaceFields: [], CollectionModes: new Dictionary<string, string> { ["tags"] = "replace" }, CreateMissingTags: false),
            CancellationToken.None);

        // The replace says this scraper's tags are now only "Kept"; what another source recorded is not its to drop.
        var shownTagIds = await EffectiveHostTagQuery.ForHostType(db, hostType)
            .Where(row => row.HostId == hostId)
            .Select(row => row.TagId)
            .Distinct()
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { kept.Id, elsewhere.Id }.Order(), shownTagIds.Order());
    }

    [Fact]
    public async Task ApplyAttemptAsync_SetsTheStudioTheReviewLinkedInsteadOfLookingTheScrapedOneUp()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);
        var library = new Studio { Name = "Library Studio" };
        db.Studios.Add(library);
        var video = new Video { Title = "Item", TagIds = [], PerformerIds = [] };
        db.Videos.Add(video);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var attempt = new ScrapeAttempt
        {
            ScraperId = "tests.fake-scraper/video",
            EntityType = EntityKinds.Video,
            EntityId = video.Id,
            InputKind = "url",
            InputJson = JsonSerializer.Serialize(new { url = "https://example.com/item" }),
            ResultJson = JsonSerializer.Serialize(new Dictionary<string, object?> { ["Studio"] = "Scraped Studio" }),
        };
        db.ScrapeAttempts.Add(attempt);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new ScrapeAttemptService(db, null!, null!, null!, new TagProvenanceService(db), null!, new EventBus(), NullLogger<ScrapeAttemptService>.Instance);

        await service.ApplyAttemptAsync(
            attempt.Id,
            new ApplyVideoScrapeAttemptDto(ReplaceFields: [], CollectionModes: new Dictionary<string, string> { ["studio"] = "replace" }, CreateMissingStudio: false)
            {
                LinkedStudioId = library.Id,
            },
            CancellationToken.None);

        var updated = await db.Videos.AsNoTracking().SingleAsync(item => item.Id == video.Id, TestContext.Current.CancellationToken);
        Assert.Equal(library.Id, updated.StudioId);
        Assert.False(await db.Studios.AnyAsync(studio => studio.Name == "Scraped Studio", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApplyAttemptAsync_ReplaceKeepsTheRecordsOfATagAddedBackByHandInTheSameApply()
    {
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);
        var kept = new Tag { Name = "Kept" };
        var readded = new Tag { Name = "Added by hand" };
        db.Tags.AddRange(kept, readded);
        var video = new Video { Title = "Item", VideoTags = [new VideoTag { Tag = kept }, new VideoTag { Tag = readded }], TagIds = [], PerformerIds = [] };
        db.Videos.Add(video);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.TagApplications.AddRange(
            new TagApplication { HostType = AffinityHostType.Video, HostId = video.Id, TagId = kept.Id, SourceKey = "user" },
            new TagApplication { HostType = AffinityHostType.Video, HostId = video.Id, TagId = readded.Id, SourceKey = "user" });
        var attempt = new ScrapeAttempt
        {
            ScraperId = "tests.fake-scraper/video",
            EntityType = EntityKinds.Video,
            EntityId = video.Id,
            InputKind = "url",
            InputJson = JsonSerializer.Serialize(new { url = "https://example.com/item" }),
            ResultJson = JsonSerializer.Serialize(new Dictionary<string, object?> { ["Tags"] = new[] { "Kept" } }),
        };
        db.ScrapeAttempts.Add(attempt);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new ScrapeAttemptService(db, null!, null!, null!, new TagProvenanceService(db), null!, new EventBus(), NullLogger<ScrapeAttemptService>.Instance);

        // The replace drops "Added by hand", and the same apply adds it back through the review's search.
        await service.ApplyAttemptAsync(
            attempt.Id,
            new ApplyVideoScrapeAttemptDto(ReplaceFields: [], CollectionModes: new Dictionary<string, string> { ["tags"] = "replace" }, CreateMissingTags: false)
            {
                AddedTagIds = [readded.Id],
            },
            CancellationToken.None);

        var userRecords = await db.TagApplications
            .Where(application => application.HostType == AffinityHostType.Video && application.HostId == video.Id && application.SourceKey == "user")
            .Select(application => application.TagId)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { kept.Id, readded.Id }.Order(), userRecords.Order());
        Assert.Equal(new[] { kept.Id, readded.Id }.Order(), video.VideoTags.Select(link => link.TagId).Order());
    }

    [Fact]
    public async Task ApplyAttemptAsync_VideoWithoutSelectionsDoesNotCountAnUncreatedNameAsMissed()
    {
        // Identify sends no selections: a name the library lacks, with creating off, was never chosen.
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);
        db.Tags.Add(new Tag { Name = "Existing" });
        var video = new Video { Title = "Current Video", TagIds = [], PerformerIds = [] };
        db.Videos.Add(video);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var attempt = new ScrapeAttempt
        {
            ScraperId = "tests.fake-scraper/video",
            EntityType = EntityKinds.Video,
            EntityId = video.Id,
            InputKind = "url",
            InputJson = JsonSerializer.Serialize(new { url = "https://example.com/video" }),
            ResultJson = JsonSerializer.Serialize(new Dictionary<string, object?> { ["Tags"] = new[] { "Existing", "Unknown" } }),
        };
        db.ScrapeAttempts.Add(attempt);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new ScrapeAttemptService(db, null!, null!, null!, new NoOpTagProvenanceService(), null!, new EventBus(), NullLogger<ScrapeAttemptService>.Instance);

        var result = await service.ApplyAttemptAsync(
            attempt.Id,
            new ApplyVideoScrapeAttemptDto(
                ReplaceFields: [],
                CollectionModes: new Dictionary<string, string> { ["tags"] = "merge" },
                CreateMissingTags: false),
            CancellationToken.None);

        Assert.Equal("Applied", result!.Status);
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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new NoOpTagProvenanceService(),
            null!,
            new EventBus(),
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
            .SingleAsync(item => item.Id == video.Id, cancellationToken: TestContext.Current.CancellationToken);

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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.TagApplications.Add(new TagApplication
        {
            HostType = AffinityHostType.Video,
            HostId = video.Id,
            TagId = staleTag.Id,
            SourceKey = sourceKey,
            SourceRunId = string.Empty,
            ModelKey = string.Empty,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Uses the real provenance service (not the no-op) so the prune actually runs.
        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new TagProvenanceService(db),
            null!,
            new EventBus(),
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
        Assert.False(await db.TagApplications.AnyAsync(item => item.TagId == staleTag.Id && item.HostId == video.Id, cancellationToken: TestContext.Current.CancellationToken));
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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

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
            new EventBus(),
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
            .SingleAsync(item => item.Id == group.Id, cancellationToken: TestContext.Current.CancellationToken);

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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new NoOpTagProvenanceService(),
            null!,
            new EventBus(),
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
            .SingleAsync(item => item.Id == gallery.Id, cancellationToken: TestContext.Current.CancellationToken);

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
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(provenance, item => item.FieldKey == "title" && item.SourceKey == "scraper:tests.fake-scraper/gallery");
        Assert.Contains(provenance, item => item.FieldKey == "tags" && item.SourceKey == "scraper:tests.fake-scraper/gallery");
    }

    [Fact]
    public async Task ApplyAttemptAsync_VideoStudioAliasResolvesMergedTargetWithoutCreatingStudio()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbName = $"scrape-attempt-service-{Guid.NewGuid():N}";
        await using var db = CreateDbContext(dbName);

        var mergedTarget = new Studio
        {
            Name = "Parent Studio",
            Aliases = [new StudioAlias { Alias = "Merged Studio" }],
        };
        var video = new Video { Title = "Video C" };
        db.Studios.Add(mergedTarget);
        db.Videos.Add(video);
        await db.SaveChangesAsync(ct);

        var attempt = new ScrapeAttempt
        {
            ScraperId = "tests.fake-scraper/video",
            EntityType = EntityKinds.Video,
            EntityId = video.Id,
            InputKind = "url",
            InputJson = JsonSerializer.Serialize(new { url = "https://example.com/video" }),
            ResultJson = JsonSerializer.Serialize(new { StudioName = "merged studio" }),
        };
        db.ScrapeAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);

        var service = new ScrapeAttemptService(
            db,
            null!,
            null!,
            null!,
            new NoOpTagProvenanceService(),
            null!,
            new EventBus(),
            NullLogger<ScrapeAttemptService>.Instance);

        var result = await service.ApplyAttemptAsync(
            attempt.Id,
            new ApplyVideoScrapeAttemptDto(
                ReplaceFields: [],
                CollectionModes: new Dictionary<string, string> { ["studio"] = "replace" },
                CreateMissingStudio: true),
            ct);

        Assert.NotNull(result);
        var updatedVideo = await db.Videos.Include(item => item.Studio).SingleAsync(item => item.Id == video.Id, ct);
        Assert.Equal(mergedTarget.Id, updatedVideo.StudioId);
        Assert.Equal("Parent Studio", updatedVideo.Studio?.Name);
        Assert.Single(await db.Studios.ToListAsync(ct));
    }

    private static CoveContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        return new CoveContext(options);
    }

    private sealed class EmptyHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
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
