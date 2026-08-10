using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class TagNameConflictScannerTests
{
    [Theory]
    [InlineData("\u00a0Alpha\u00a0", "Alpha", "alpha")]
    [InlineData("\u2003Alpha\u2003", "Alpha", "alpha")]
    [InlineData(" STRA\u00dfE ", "STRA\u00dfE", "stra\u00dfe")]
    public void SharedRules_UseDotNetTrimAndInvariantLowercase(
        string original,
        string expectedNormalized,
        string expectedNamespaceKey)
    {
        var normalized = TagNameRules.NormalizeCanonicalName(original);

        Assert.Equal(expectedNormalized, normalized);
        Assert.Equal(expectedNamespaceKey, TagNameRules.NamespaceKey(normalized));
    }

    [Fact]
    public async Task ScanSummaryAsync_SkipsImpactQueries()
    {
        await using var db = CreateContext();
        db.Tags.AddRange(new Tag { Name = "Alpha" }, new Tag { Name = " alpha " });
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var summary = await new TagNameConflictScanner(db, new ThrowingExternalReferenceInspector())
            .ScanSummaryAsync();

        Assert.Equal(1, summary.UnresolvedGroupCount);
    }

    [Fact]
    public async Task ScanAsync_GroupsEveryFutureNamespaceConflictAndUsesSharedSurvivorPolicy()
    {
        await using var db = CreateContext();
        var tags = new[]
        {
            new Tag { Name = " Alpha " },
            new Tag { Name = "alpha" },
            new Tag
            {
                Name = "Beta",
                Aliases =
                [
                    new TagAlias { Alias = " Shared " },
                    new TagAlias { Alias = "shared" },
                ],
            },
            new Tag { Name = "Gamma", Aliases = [new TagAlias { Alias = " beta " }] },
            new Tag { Name = "Delta", Aliases = [new TagAlias { Alias = "SHARED" }] },
            new Tag { Name = "Echo", Aliases = [new TagAlias { Alias = " echo " }] },
            new Tag { Name = "   " },
            new Tag { Name = "<empty>" },
            new Tag { Name = "Zeta", Aliases = [new TagAlias { Alias = " \t " }] },
        };
        db.Tags.AddRange(tags);
        var video = new Video { Title = "Conflict impact fixture" };
        db.Videos.Add(video);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        db.Set<VideoTag>().Add(new VideoTag { VideoId = video.Id, TagId = tags[1].Id });
        db.Segments.Add(new Segment
        {
            HostType = SegmentHostType.Video,
            HostId = video.Id,
            TagId = tags[1].Id,
            Payload = System.Text.Json.JsonDocument.Parse($$"""{"secondaryTagIds":[{{tags[1].Id}}]}"""),
            StartSec = 0,
            EndSec = 1,
        });
        db.Segments.Add(new Segment
        {
            HostType = SegmentHostType.Video,
            HostId = video.Id,
            TagId = tags[0].Id,
            Payload = System.Text.Json.JsonDocument.Parse($$"""{"secondaryTagIds":[{{tags[1].Id}}]}"""),
            StartSec = 1,
            EndSec = 2,
        });
        db.Set<TagParent>().Add(new TagParent { ParentId = tags[1].Id, ChildId = tags[2].Id });
        db.Ratings.Add(new Rating
        {
            UserId = 1,
            HostType = RatingHostType.Tag,
            HostId = tags[1].Id,
            Value = 80,
        });
        db.Set<TagRemoteId>().Add(new TagRemoteId
        {
            TagId = tags[1].Id,
            Endpoint = "fixture",
            RemoteId = "fixture",
        });
        var displayProfile = new SegmentDisplayProfile { Name = "Conflict impact profile" };
        db.SegmentDisplayProfiles.Add(displayProfile);
        db.SegmentDisplayRules.Add(new SegmentDisplayRule
        {
            Profile = displayProfile,
            TagId = tags[1].Id,
        });
        db.SavedFilters.Add(new SavedFilter
        {
            Mode = "videos",
            Name = "Conflict impact filter",
            ObjectFilter = System.Text.Json.JsonSerializer.Serialize(new { tagIds = new[] { tags[1].Id } }),
        });
        db.Users.Add(new User
        {
            Username = "conflict-impact-preferences",
            PasswordHash = "fixture",
            UiPreferencesJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                theme = new { activeThemeId = "dark" },
                defaultFilters = new Dictionary<string, string>
                {
                    ["segments"] = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        objectFilter = new
                        {
                            videoTagsCriterion = new { value = new[] { tags[1].Id } },
                            rawTagsCriterion = new { excludes = new[] { tags[1].Id } },
                        },
                    }),
                    ["malformed"] = "{",
                },
            }),
        });
        db.Interactions.Add(new Interaction
        {
            UserId = 1,
            HostType = InteractionHostType.Image,
            HostId = 1,
            Kind = InteractionKind.OpenLightbox,
            Meta = System.Text.Json.JsonDocument.Parse($$"""{"tagId":{{tags[1].Id}}}"""),
        });
        await db.SaveChangesAsync();
        var provenanceSegmentId = await db.Segments
            .Where(segment => segment.TagId == tags[1].Id)
            .Select(segment => segment.Id)
            .FirstAsync();
        db.FieldProvenance.AddRange(
            new FieldProvenance
            {
                HostType = AffinityHostType.Segment,
                HostId = provenanceSegmentId,
                FieldKey = "tag_id",
                ValueJson = tags[1].Id.ToString(),
                SourceKey = "fixture",
            },
            new FieldProvenance
            {
                HostType = AffinityHostType.Segment,
                HostId = provenanceSegmentId,
                FieldKey = "payload",
                ValueJson = $$"""{"secondaryTagIds":[{{tags[1].Id}}]}""",
                SourceKey = "fixture",
            },
            new FieldProvenance
            {
                HostType = AffinityHostType.Segment,
                HostId = provenanceSegmentId,
                FieldKey = "ref_id",
                ValueJson = tags[1].Id.ToString(),
                SourceKey = "fixture",
            });
        await db.SaveChangesAsync();
        var playbackSession = new PlaybackSession
        {
            UserId = 1,
            HostType = InteractionHostType.Video,
            HostId = video.Id,
            SessionId = Guid.NewGuid(),
            ParentHostType = InteractionHostType.Tag,
            ParentHostId = tags[1].Id,
            ItemHostType = InteractionHostType.Tag,
            ItemHostId = tags[1].Id,
            Context = System.Text.Json.JsonDocument.Parse($$"""{"tagId":{{tags[1].Id}}}"""),
        };
        db.PlaybackSessions.Add(playbackSession);
        await db.SaveChangesAsync();
        db.PlaybackIntervals.Add(new PlaybackInterval
        {
            PlaybackSessionId = playbackSession.Id,
            UserId = 1,
            HostType = InteractionHostType.Video,
            HostId = video.Id,
            StartSec = 0,
            EndSec = 1,
            ParentHostType = InteractionHostType.Tag,
            ParentHostId = tags[1].Id,
            ItemHostType = InteractionHostType.Tag,
            ItemHostId = tags[1].Id,
            Context = System.Text.Json.JsonDocument.Parse($$"""{"tagId":{{tags[1].Id}}}"""),
        });
        await db.SaveChangesAsync();

        var result = await new TagNameConflictScanner(
            db,
            new StubExternalReferenceInspector(new Dictionary<int, int> { [tags[1].Id] = 4 }))
            .ScanAsync();

        Assert.Equal(6, result.UnresolvedGroupCount);

        var alpha = Assert.Single(result.Groups, group => group.NormalizedName == "Alpha");
        Assert.Contains(TagNameConflictKinds.CanonicalNameCollision, alpha.Kinds);
        Assert.Equal(tags[0].Id, alpha.RecommendedSurvivorTagId);
        Assert.True(alpha.RequiresMerge);
        Assert.Equal([tags[1].Id], alpha.RecommendedMergeTagIds);
        Assert.Equal(2, alpha.Claims.Count(claim => claim.ClaimType == TagNameClaimTypes.CanonicalName));

        var beta = Assert.Single(result.Groups, group => group.NormalizedName == "Beta");
        Assert.Contains(TagNameConflictKinds.NameAliasCollision, beta.Kinds);
        Assert.False(beta.RequiresMerge);
        Assert.Empty(beta.RecommendedMergeTagIds);
        Assert.Equal([tags[3].Aliases.Single().Id], beta.RecommendedRemoveAliasIds);
        Assert.Equal([tags[2].Id, tags[3].Id], beta.Impacts.Select(impact => impact.TagId).Order().ToArray());

        var shared = Assert.Single(result.Groups, group => group.NormalizedName == "Shared");
        Assert.Contains(TagNameConflictKinds.AliasOwnershipCollision, shared.Kinds);
        Assert.Contains(TagNameConflictKinds.DuplicateAlias, shared.Kinds);
        Assert.Equal(tags[2].Id, shared.RecommendedSurvivorTagId);
        Assert.False(shared.RequiresMerge);
        Assert.Equal(2, shared.RecommendedRemoveAliasIds.Count);

        var selfAlias = Assert.Single(result.Groups, group => group.NormalizedName == "Echo");
        Assert.Contains(TagNameConflictKinds.RedundantSelfAlias, selfAlias.Kinds);
        Assert.False(selfAlias.RequiresMerge);
        Assert.Equal([tags[5].Aliases.Single().Id], selfAlias.RecommendedRemoveAliasIds);

        var empty = Assert.Single(result.Groups, group => group.NormalizedName == TagNameRules.EmptyCanonicalName);
        Assert.Contains(TagNameConflictKinds.WhitespaceOnlyCanonicalName, empty.Kinds);
        Assert.Contains(TagNameConflictKinds.EmptyNameCollision, empty.Kinds);
        Assert.Equal(tags[6].Id, empty.RecommendedSurvivorTagId);

        var blankAlias = Assert.Single(result.Groups, group => group.Kinds.Contains(TagNameConflictKinds.BlankAlias));
        Assert.False(blankAlias.RequiresMerge);
        Assert.Equal(tags[8].Id, Assert.Single(blankAlias.Claims).TagId);
        Assert.Equal([tags[8].Aliases.Single().Id], blankAlias.RecommendedRemoveAliasIds);

        var impacted = Assert.Single(alpha.Impacts, impact => impact.TagId == tags[1].Id);
        Assert.Equal(1, impacted.TaggedEntityCount);
        Assert.Equal(2, impacted.SegmentCount);
        Assert.Equal(1, impacted.ChildRelationshipCount);
        Assert.Equal(1, impacted.RatingCount);
        Assert.Equal(13, impacted.OtherMetadataCount);
        Assert.Equal(4, impacted.ExtensionMetadataCount);
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

    private sealed class StubExternalReferenceInspector(IReadOnlyDictionary<int, int> counts)
        : ITagExternalReferenceInspector
    {
        public Task<IReadOnlyDictionary<int, int>> CountAsync(
            IReadOnlyCollection<int> tagIds,
            CancellationToken ct = default)
            => Task.FromResult(counts);
    }

    private sealed class ThrowingExternalReferenceInspector : ITagExternalReferenceInspector
    {
        public Task<IReadOnlyDictionary<int, int>> CountAsync(
            IReadOnlyCollection<int> tagIds,
            CancellationToken ct = default)
            => throw new InvalidOperationException("Summary scans must not load impact data.");
    }
}
