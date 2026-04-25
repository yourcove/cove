using Cove.Api.Controllers;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Cove.Tests;

public class SceneFilterBehaviorTests
{
    [Fact]
    public async Task PathCriterion_Equals_UsesFullNormalizedPath()
    {
        await using var context = CreateContext();
        context.Scenes.AddRange(
            CreateSceneWithFile("match", folderPath: @"C:\library\matching", basename: "clip.mp4"),
            CreateSceneWithFile("same-name-other-folder", folderPath: @"C:\library\other", basename: "clip.mp4"));
        await context.SaveChangesAsync();

        var repository = new SceneRepository(context);
        var filter = new SceneFilter
        {
            PathCriterion = new StringCriterion
            {
                Value = @"C:\library\matching\clip.mp4",
                Modifier = CriterionModifier.Equals,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(1, totalCount);
        Assert.Equal(["match"], items.Select(scene => scene.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task AudioCodecCriterion_HandlesRegexAndNullModifiers()
    {
        await using var context = CreateContext();
        context.Scenes.AddRange(
            CreateSceneWithFile("aac-scene", audioCodec: "AAC"),
            CreateSceneWithFile("mp3-scene", audioCodec: "MP3"),
            CreateSceneWithFile("missing-audio", audioCodec: ""));
        await context.SaveChangesAsync();

        var repository = new SceneRepository(context);

        var (notRegexItems, notRegexCount) = await repository.FindAsync(
            new SceneFilter
            {
                AudioCodecCriterion = new StringCriterion
                {
                    Value = "^aa",
                    Modifier = CriterionModifier.NotMatchesRegex,
                },
            },
            new FindFilter { Page = 1, PerPage = 50 });

        var (nullItems, nullCount) = await repository.FindAsync(
            new SceneFilter
            {
                AudioCodecCriterion = new StringCriterion
                {
                    Value = string.Empty,
                    Modifier = CriterionModifier.IsNull,
                },
            },
            new FindFilter { Page = 1, PerPage = 50 });

        var (notNullItems, notNullCount) = await repository.FindAsync(
            new SceneFilter
            {
                AudioCodecCriterion = new StringCriterion
                {
                    Value = string.Empty,
                    Modifier = CriterionModifier.NotNull,
                },
            },
            new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(2, notRegexCount);
        Assert.Equal(["missing-audio", "mp3-scene"], notRegexItems.Select(scene => scene.Title ?? string.Empty).OrderBy(title => title).ToArray());
        Assert.Equal(1, nullCount);
        Assert.Equal(["missing-audio"], nullItems.Select(scene => scene.Title ?? string.Empty).ToArray());
        Assert.Equal(2, notNullCount);
        Assert.Equal(["aac-scene", "mp3-scene"], notNullItems.Select(scene => scene.Title ?? string.Empty).OrderBy(title => title).ToArray());
    }

    [Fact]
    public async Task BitrateInterval_GreaterThan_UsesSceneFileBitrate()
    {
        await using var context = CreateContext();
        context.Scenes.AddRange(
            CreateSceneWithFile("high-bitrate", bitRate: 2_500_000),
            CreateSceneWithFile("low-bitrate", bitRate: 500_000),
            new Scene { Title = "no-file" });
        await context.SaveChangesAsync();

        var repository = new SceneRepository(context);
        var filter = new SceneFilter
        {
            BitrateInterval = new IntCriterion
            {
                Value = 1000,
                Modifier = CriterionModifier.GreaterThan,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(1, totalCount);
        Assert.Equal(["high-bitrate"], items.Select(scene => scene.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task DirectorCriterion_NotMatchesRegex_UsesRegexSemantics()
    {
        await using var context = CreateContext();
        context.Scenes.AddRange(
            CreateSceneWithFile("jane-scene", director: "Jane Smith"),
            CreateSceneWithFile("john-scene", director: "John Doe"));
        await context.SaveChangesAsync();

        var repository = new SceneRepository(context);
        var filter = new SceneFilter
        {
            DirectorCriterion = new StringCriterion
            {
                Value = "^Jane",
                Modifier = CriterionModifier.NotMatchesRegex,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(1, totalCount);
        Assert.Equal(["john-scene"], items.Select(scene => scene.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task PerformerAgeCriterion_Equals_UsesAgeAtSceneDate()
    {
        await using var context = CreateContext();
        var performer = CreatePerformer("Boundary Performer", new DateOnly(2006, 1, 15));

        context.Scenes.AddRange(
            CreateSceneWithFile("before-birthday", sceneDate: new DateOnly(2024, 1, 10), performer: performer),
            CreateSceneWithFile("after-birthday", sceneDate: new DateOnly(2024, 1, 20), performer: performer));
        await context.SaveChangesAsync();

        var repository = new SceneRepository(context);
        var filter = new SceneFilter
        {
            PerformerAgeCriterion = new IntCriterion
            {
                Value = 18,
                Modifier = CriterionModifier.Equals,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(1, totalCount);
        Assert.Equal(["after-birthday"], items.Select(scene => scene.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task PerformerTagsCriterion_Includes_MatchesScenesByPerformerTag()
    {
        await using var context = CreateContext();
        var tag = new Tag { Name = "Featured" };

        context.Scenes.AddRange(
            CreateSceneWithFile("tagged-performer-scene", performer: CreatePerformer("Tagged", new DateOnly(2000, 1, 1), tag)),
            CreateSceneWithFile("untagged-performer-scene", performer: CreatePerformer("Untagged", new DateOnly(2000, 1, 1))));
        await context.SaveChangesAsync();

        var repository = new SceneRepository(context);
        var filter = new SceneFilter
        {
            PerformerTagsCriterion = new MultiIdCriterion
            {
                Value = [tag.Id],
                Modifier = CriterionModifier.Includes,
            },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(1, totalCount);
        Assert.Equal(["tagged-performer-scene"], items.Select(scene => scene.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task HashAndChecksumCriteria_FilterSceneFingerprints()
    {
        await using var context = CreateContext();
        context.Scenes.AddRange(
            CreateSceneWithFile(
                "matching-hashes",
                fingerprints:
                [
                    new FileFingerprint { Type = "oshash", Value = "osh-match" },
                    new FileFingerprint { Type = "md5", Value = "md5-match" },
                ]),
            CreateSceneWithFile(
                "other-hashes",
                fingerprints:
                [
                    new FileFingerprint { Type = "oshash", Value = "osh-other" },
                    new FileFingerprint { Type = "md5", Value = "md5-other" },
                ]));
        await context.SaveChangesAsync();

        var repository = new SceneRepository(context);

        var (hashItems, hashCount) = await repository.FindAsync(
            new SceneFilter
            {
                HashCriterion = new StringCriterion
                {
                    Value = "osh-match",
                    Modifier = CriterionModifier.Equals,
                },
            },
            new FindFilter { Page = 1, PerPage = 50 });

        var (checksumItems, checksumCount) = await repository.FindAsync(
            new SceneFilter
            {
                ChecksumCriterion = new StringCriterion
                {
                    Value = "md5-match",
                    Modifier = CriterionModifier.Equals,
                },
            },
            new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(1, hashCount);
        Assert.Equal(["matching-hashes"], hashItems.Select(scene => scene.Title ?? string.Empty).ToArray());
        Assert.Equal(1, checksumCount);
        Assert.Equal(["matching-hashes"], checksumItems.Select(scene => scene.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task FingerprintCriterion_FiltersScenesBySelectedAlgorithm()
    {
        await using var context = CreateContext();
        context.Scenes.AddRange(
            CreateSceneWithFile(
                "matching-fingerprint-types",
                fingerprints:
                [
                    new FileFingerprint { Type = "oshash", Value = "osh-match" },
                    new FileFingerprint { Type = "md5", Value = "md5-match" },
                    new FileFingerprint { Type = "phash", Value = "phash-match" },
                ]),
            CreateSceneWithFile(
                "other-fingerprint-types",
                fingerprints:
                [
                    new FileFingerprint { Type = "oshash", Value = "osh-other" },
                    new FileFingerprint { Type = "md5", Value = "md5-other" },
                    new FileFingerprint { Type = "phash", Value = "phash-other" },
                ]));
        await context.SaveChangesAsync();

        var repository = new SceneRepository(context);

        var (oshashItems, oshashCount) = await repository.FindAsync(
            new SceneFilter
            {
                FingerprintCriterion = new FingerprintCriterion
                {
                    Type = "oshash",
                    Value = "osh-match",
                    Modifier = CriterionModifier.Equals,
                },
            },
            new FindFilter { Page = 1, PerPage = 50 });

        var (md5Items, md5Count) = await repository.FindAsync(
            new SceneFilter
            {
                FingerprintCriterion = new FingerprintCriterion
                {
                    Type = "md5",
                    Value = "md5-match",
                    Modifier = CriterionModifier.Equals,
                },
            },
            new FindFilter { Page = 1, PerPage = 50 });

        var (phashItems, phashCount) = await repository.FindAsync(
            new SceneFilter
            {
                FingerprintCriterion = new FingerprintCriterion
                {
                    Type = "phash",
                    Value = "phash-match",
                    Modifier = CriterionModifier.Equals,
                },
            },
            new FindFilter { Page = 1, PerPage = 50 });

        Assert.Equal(1, oshashCount);
        Assert.Equal(["matching-fingerprint-types"], oshashItems.Select(scene => scene.Title ?? string.Empty).ToArray());
        Assert.Equal(1, md5Count);
        Assert.Equal(["matching-fingerprint-types"], md5Items.Select(scene => scene.Title ?? string.Empty).ToArray());
        Assert.Equal(1, phashCount);
        Assert.Equal(["matching-fingerprint-types"], phashItems.Select(scene => scene.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task DuplicatedPhashCriterion_True_FindsScenesSharingAPhashAcrossScenes()
    {
        await using var context = CreateContext();
        context.Scenes.AddRange(
            CreateSceneWithFile("duplicate-a", fingerprints: [new FileFingerprint { Type = "phash", Value = "same-phash" }]),
            CreateSceneWithFile("duplicate-b", fingerprints: [new FileFingerprint { Type = "phash", Value = "same-phash" }]),
            CreateSceneWithFile("unique", fingerprints: [new FileFingerprint { Type = "phash", Value = "unique-phash" }]));
        await context.SaveChangesAsync();

        var repository = new SceneRepository(context);
        var filter = new SceneFilter
        {
            DuplicatedPhashCriterion = new BoolCriterion { Value = true },
        };

        var (items, totalCount) = await repository.FindAsync(filter, new FindFilter { Page = 1, PerPage = 50, Sort = "title" });

        Assert.Equal(2, totalCount);
        Assert.Equal(["duplicate-a", "duplicate-b"], items.Select(scene => scene.Title ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task ScenesController_Find_BindsSeedFromQuery()
    {
        var repository = new CapturingSceneRepository();
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var controller = new ScenesController(repository, null!, null!, null!, null!, memoryCache, null!, null!);

        await controller.Find(q: null, page: 1, perPage: 25, sort: "random", direction: "desc", seed: 12345, ct: default);

        Assert.Equal(12345, repository.LastFindFilter?.Seed);
        Assert.Equal("random", repository.LastFindFilter?.Sort);
        Assert.Equal(Cove.Core.Enums.SortDirection.Desc, repository.LastFindFilter?.Direction);
    }

    [Fact]
    public async Task LastPlayedAtSort_Descending_PutsPlayedScenesBeforeUnplayedScenes()
    {
        await using var context = CreateContext();
        context.Scenes.AddRange(
            new Scene { Title = "never-played" },
            new Scene { Title = "older-play" , LastPlayedAt = new DateTime(2024, 1, 10, 8, 0, 0, DateTimeKind.Utc) },
            new Scene { Title = "recent-play", LastPlayedAt = new DateTime(2024, 1, 12, 8, 0, 0, DateTimeKind.Utc) });
        await context.SaveChangesAsync();

        var repository = new SceneRepository(context);

        var (items, totalCount) = await repository.FindAsync(
            filter: null,
            new FindFilter
            {
                Page = 1,
                PerPage = 50,
                Sort = "last_played_at",
                Direction = Cove.Core.Enums.SortDirection.Desc,
            });

        Assert.Equal(3, totalCount);
        Assert.Equal(["recent-play", "older-play", "never-played"], items.Select(scene => scene.Title ?? string.Empty).ToArray());
    }

    private static Scene CreateSceneWithFile(
        string title,
        string? director = null,
        DateOnly? sceneDate = null,
        string folderPath = @"C:\library",
        string basename = "clip.mp4",
        string audioCodec = "AAC",
        string videoCodec = "H264",
        long bitRate = 1_000_000,
        Performer? performer = null,
        IEnumerable<FileFingerprint>? fingerprints = null)
    {
        var scene = new Scene
        {
            Title = title,
            Director = director,
            Date = sceneDate ?? new DateOnly(2024, 1, 1),
        };

        var file = new VideoFile
        {
            Basename = basename,
            ParentFolder = new Folder { Path = folderPath, ModTime = DateTime.UtcNow },
            AudioCodec = audioCodec,
            VideoCodec = videoCodec,
            BitRate = bitRate,
            FrameRate = 30,
            Duration = 120,
            Width = 1920,
            Height = 1080,
            Format = "mp4",
            Size = 1024,
            ModTime = DateTime.UtcNow,
        };

        if (fingerprints != null)
        {
            foreach (var fingerprint in fingerprints)
            {
                file.Fingerprints.Add(fingerprint);
            }
        }

        scene.Files.Add(file);

        if (performer != null)
        {
            scene.ScenePerformers.Add(new ScenePerformer { Performer = performer });
        }

        return scene;
    }

    private static Performer CreatePerformer(string name, DateOnly birthdate, params Tag[] tags)
    {
        var performer = new Performer
        {
            Name = name,
            Birthdate = birthdate,
        };

        foreach (var tag in tags)
        {
            performer.PerformerTags.Add(new PerformerTag { Performer = performer, Tag = tag });
        }

        return performer;
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"scene-filter-behavior-{Guid.NewGuid():N}")
            .Options;

        return new TestCoveContext(options);
    }

    private sealed class TestCoveContext(DbContextOptions<CoveContext> options) : CoveContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Scene>().Ignore(scene => scene.CustomFields);
            modelBuilder.Entity<Image>().Ignore(image => image.CustomFields);
            modelBuilder.Entity<Tag>().Ignore(tag => tag.CustomFields);
            modelBuilder.Entity<Studio>().Ignore(studio => studio.CustomFields);
            modelBuilder.Entity<Performer>().Ignore(performer => performer.CustomFields);
            modelBuilder.Entity<Gallery>().Ignore(gallery => gallery.CustomFields);
            modelBuilder.Entity<Group>().Ignore(group => group.CustomFields);
        }
    }

    private sealed class CapturingSceneRepository : ISceneRepository
    {
        public FindFilter? LastFindFilter { get; private set; }

        public Task<(IReadOnlyList<Scene> Items, int TotalCount)> FindAsync(SceneFilter? filter, FindFilter? findFilter, CancellationToken ct = default)
        {
            LastFindFilter = findFilter;
            return Task.FromResult<(IReadOnlyList<Scene>, int)>((Array.Empty<Scene>(), 0));
        }

        public Task<Scene?> GetByIdAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Scene>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Scene> AddAsync(Scene entity, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(Scene entity, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> CountAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Scene?> GetByIdWithRelationsAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}