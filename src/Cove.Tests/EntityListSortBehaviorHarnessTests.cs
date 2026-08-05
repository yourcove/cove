using Cove.Api.Controllers;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Data.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using CoveSortDirection = Cove.Core.Enums.SortDirection;

namespace Cove.Tests;

public class EntityListSortBehaviorHarnessTests
{
    private const int TestUserId = 81001;

    private static readonly HashSet<string> SortRowsWithBehaviorExemptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "sort:videos:random",
        "sort:videos:organized",
        "sort:images:random",
        "sort:images:visual_match",
        "sort:audios:random",
        "sort:texts:random",
        "sort:galleries:random",
        "sort:groups:random",
        "sort:performers:random",
        "sort:studios:random",
        "sort:tags:random",
    };

    public static IEnumerable<object[]> SortRows()
    {
        foreach (var sort in EntityListSortFilterCatalog.Sorts)
        {
            if (!IsBehaviorTested(sort))
                continue;

            if (sort.Entity.Equals("faces", StringComparison.OrdinalIgnoreCase)
                && sort.Key.Equals("suggestion_confidence", StringComparison.OrdinalIgnoreCase))
            {
                // suggestion_confidence is a composite review ordering that intentionally ignores
                // the direction toggle, so only the descending surface is meaningful.
                yield return [sort.Entity, sort.Key, CoveSortDirection.Desc];
                continue;
            }

            yield return [sort.Entity, sort.Key, CoveSortDirection.Asc];
            yield return [sort.Entity, sort.Key, CoveSortDirection.Desc];
        }
    }

    public static IEnumerable<object[]> FilterRows()
    {
        yield return [new FilterProbe(
            "filter:videos:TitleCriterion/includes",
            fixture => QueryFilteredIdsAsync(fixture.Context, "videos", new VideoFilter { TitleCriterion = new StringCriterion { Modifier = CriterionModifier.Includes, Value = "Alpha" } }),
            _ => [401])];
        yield return [new FilterProbe(
            "filter:videos:TagsCriterion/includes",
            fixture => QueryFilteredIdsAsync(fixture.Context, "videos", new VideoFilter { TagsCriterion = new MultiIdCriterion { Modifier = CriterionModifier.Includes, Value = [201] } }),
            _ => [401])];
        yield return [new FilterProbe(
            "filter:videos:RatingCriterion/greater_than",
            fixture => QueryFilteredIdsAsync(fixture.Context, "videos", new VideoFilter { RatingCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 2 } }),
            _ => [402, 403])];
        yield return [new FilterProbe(
            "filter:videos:DurationCriterion/greater_than",
            fixture => QueryFilteredIdsAsync(fixture.Context, "videos", new VideoFilter { DurationCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 20 } }),
            _ => [402, 403])];
        yield return [new FilterProbe(
            "filter:videos:OrganizedCriterion/true",
            fixture => QueryFilteredIdsAsync(fixture.Context, "videos", new VideoFilter { OrganizedCriterion = new BoolCriterion { Value = true } }),
            _ => [402, 403])];

        yield return [new FilterProbe(
            "filter:images:TitleCriterion/includes",
            fixture => QueryFilteredIdsAsync(fixture.Context, "images", new ImageFilter { TitleCriterion = new StringCriterion { Modifier = CriterionModifier.Includes, Value = "Beta" } }),
            _ => [502])];
        yield return [new FilterProbe(
            "filter:images:ResolutionCriterion/equals_720_bucket",
            fixture => QueryFilteredIdsAsync(fixture.Context, "images", new ImageFilter { ResolutionCriterion = new IntCriterion { Modifier = CriterionModifier.Equals, Value = 720 } }),
            _ => [502])];
        yield return [new FilterProbe(
            "filter:images:LikeCounterCriterion/greater_than",
            fixture => QueryFilteredIdsAsync(fixture.Context, "images", new ImageFilter { LikeCounterCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 10 } }),
            _ => [502, 503])];
        yield return [new FilterProbe(
            "filter:images:PerformerCountCriterion/greater_than",
            fixture => QueryFilteredIdsAsync(fixture.Context, "images", new ImageFilter { PerformerCountCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 1 } }),
            _ => [502, 503])];

        yield return [new FilterProbe(
            "filter:audios:TitleCriterion/includes",
            fixture => QueryFilteredIdsAsync(fixture.Context, "audios", new AudioFilter { TitleCriterion = new StringCriterion { Modifier = CriterionModifier.Includes, Value = "Beta" } }),
            _ => [802])];
        yield return [new FilterProbe(
            "filter:audios:DurationCriterion/greater_than",
            fixture => QueryFilteredIdsAsync(fixture.Context, "audios", new AudioFilter { DurationCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 100 } }),
            _ => [802, 803])];
        yield return [new FilterProbe(
            "filter:audios:RatingCriterion/greater_than",
            fixture => QueryFilteredIdsAsync(fixture.Context, "audios", new AudioFilter { RatingCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 4 } }),
            _ => [802, 803])];
        yield return [new FilterProbe(
            "filter:audios:HasVideoFilesCriterion/true",
            fixture => QueryFilteredIdsAsync(fixture.Context, "audios", new AudioFilter { HasVideoFilesCriterion = new BoolCriterion { Value = true } }),
            _ => [802, 803])];
        yield return [new FilterProbe(
            "filter:audios:FileSizeCriterion/greater_than",
            fixture => QueryFilteredIdsAsync(fixture.Context, "audios", new AudioFilter { FileSizeCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 1_500 } }),
            _ => [802, 803])];

        yield return [new FilterProbe(
            "filter:texts:TitleCriterion/includes",
            fixture => QueryFilteredIdsAsync(fixture.Context, "texts", new TextDocumentFilter { TitleCriterion = new StringCriterion { Modifier = CriterionModifier.Includes, Value = "Gamma" } }),
            _ => [903])];
        yield return [new FilterProbe(
            "filter:texts:WordCountCriterion/greater_than",
            fixture => QueryFilteredIdsAsync(fixture.Context, "texts", new TextDocumentFilter { WordCountCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 150 } }),
            _ => [902, 903])];
        yield return [new FilterProbe(
            "filter:texts:RatingCriterion/greater_than",
            fixture => QueryFilteredIdsAsync(fixture.Context, "texts", new TextDocumentFilter { RatingCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 4 } }),
            _ => [902, 903])];
        yield return [new FilterProbe(
            "filter:texts:FileSizeCriterion/greater_than",
            fixture => QueryFilteredIdsAsync(fixture.Context, "texts", new TextDocumentFilter { FileSizeCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 1_500 } }),
            _ => [902, 903])];
        yield return [new FilterProbe(
            "filter:texts:ContentCriterion/includes",
            fixture => QueryFilteredIdsAsync(fixture.Context, "texts", new TextDocumentFilter { ContentCriterion = new StringCriterion { Modifier = CriterionModifier.Includes, Value = "gamma text content" } }),
            _ => [903])];

        yield return [new FilterProbe(
            "filter:galleries:TitleCriterion/includes",
            fixture => QueryFilteredIdsAsync(fixture.Context, "galleries", new GalleryFilter { TitleCriterion = new StringCriterion { Modifier = CriterionModifier.Includes, Value = "Gamma" } }),
            _ => [603])];
        yield return [new FilterProbe(
            "filter:galleries:ImageCountCriterion/greater_than",
            fixture => QueryFilteredIdsAsync(fixture.Context, "galleries", new GalleryFilter { ImageCountCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 1 } }),
            _ => [602, 603])];
        yield return [new FilterProbe(
            "filter:galleries:LikeCounterCriterion/greater_than",
            fixture => QueryFilteredIdsAsync(fixture.Context, "galleries", new GalleryFilter { LikeCounterCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 20 } }),
            _ => [602, 603])];
        yield return [new FilterProbe(
            "filter:galleries:LastLikedAtCriterion/greater_than",
            fixture => QueryFilteredIdsAsync(fixture.Context, "galleries", new GalleryFilter { LastLikedAtCriterion = new TimestampCriterion { Modifier = CriterionModifier.GreaterThan, Value = fixture.Now.AddDays(-7).ToString("o") } }),
            _ => [602, 603])];

        yield return [new FilterProbe(
            "filter:groups:NameCriterion/includes",
            fixture => QueryFilteredIdsAsync(fixture.Context, "groups", new GroupFilter { NameCriterion = new StringCriterion { Modifier = CriterionModifier.Includes, Value = "Beta" } }),
            _ => [702])];
        yield return [new FilterProbe(
            "filter:groups:DateCriterion/greater_than",
            fixture => QueryFilteredIdsAsync(fixture.Context, "groups", new GroupFilter { DateCriterion = new DateCriterion { Modifier = CriterionModifier.GreaterThan, Value = "2018-02-01" } }),
            _ => [702, 703])];
        yield return [new FilterProbe(
            "filter:groups:RatingCriterion/greater_than",
            fixture => QueryFilteredIdsAsync(fixture.Context, "groups", new GroupFilter { RatingCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 5 } }),
            _ => [702, 703])];
        yield return [new FilterProbe(
            "filter:groups:ImageCountCriterion/greater_than",
            fixture => QueryFilteredIdsAsync(fixture.Context, "groups", new GroupFilter { ImageCountCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 1 } }),
            _ => [702, 703])];
        yield return [new FilterProbe(
            "filter:groups:AudioCountCriterion/greater_than",
            fixture => QueryFilteredIdsAsync(fixture.Context, "groups", new GroupFilter { AudioCountCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 2 } }),
            _ => [703])];
        yield return [new FilterProbe(
            "filter:groups:CachedItemCountCriterion/greater_than",
            fixture => QueryFilteredIdsAsync(fixture.Context, "groups", new GroupFilter { CachedItemCountCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 10 } }),
            _ => [703])];

        yield return [new FilterProbe(
            "filter:segments:kind/includes",
            fixture => QuerySegmentIdsAsync(fixture.Context, q: null, ids: null, videoId: null, videoIds: null, videoTitle: null, tagId: null, tagIds: null, kind: "beat", sourceKey: null, tagged: null, minConfidence: null, minDurationSec: null, excludeVideoIds: null),
            _ => [1002])];
        yield return [new FilterProbe(
            "filter:segments:minConfidence/greater_than_or_equal",
            fixture => QuerySegmentIdsAsync(fixture.Context, q: null, ids: null, videoId: null, videoIds: null, videoTitle: null, tagId: null, tagIds: null, kind: null, sourceKey: null, tagged: null, minConfidence: 0.5f, minDurationSec: null, excludeVideoIds: null),
            _ => [1002, 1003])];
        yield return [new FilterProbe(
            "filter:segments:title/includes",
            fixture => QuerySegmentIdsAsync(fixture.Context, q: null, ids: null, videoId: null, videoIds: null, videoTitle: null, tagId: null, tagIds: null, kind: null, sourceKey: null, tagged: null, minConfidence: null, minDurationSec: null, excludeVideoIds: null, title: "Beta", titleModifier: "INCLUDES"),
            _ => [1002])];
        yield return [new FilterProbe(
            "filter:segments:startSec/greater_than",
            fixture => QuerySegmentIdsAsync(fixture.Context, q: null, ids: null, videoId: null, videoIds: null, videoTitle: null, tagId: null, tagIds: null, kind: null, sourceKey: null, tagged: null, minConfidence: null, minDurationSec: null, excludeVideoIds: null, startSec: 4, startSecModifier: "GREATER_THAN"),
            _ => [1002, 1003])];
        yield return [new FilterProbe(
            "filter:segments:createdAt/greater_than",
            fixture => QuerySegmentIdsAsync(fixture.Context, q: null, ids: null, videoId: null, videoIds: null, videoTitle: null, tagId: null, tagIds: null, kind: null, sourceKey: null, tagged: null, minConfidence: null, minDurationSec: null, excludeVideoIds: null, createdAt: "2024-03-21T00:00:00Z", createdAtModifier: "GREATER_THAN"),
            _ => [1002, 1003])];
        yield return [new FilterProbe(
            "filter:segments:hasImage/true",
            fixture => QuerySegmentIdsAsync(fixture.Context, q: null, ids: null, videoId: null, videoIds: null, videoTitle: null, tagId: null, tagIds: null, kind: null, sourceKey: null, tagged: null, minConfidence: null, minDurationSec: null, excludeVideoIds: null, hasImage: true),
            _ => [1002])];
        yield return [new FilterProbe(
            "filter:segments:hasPayload/true",
            fixture => QuerySegmentIdsAsync(fixture.Context, q: null, ids: null, videoId: null, videoIds: null, videoTitle: null, tagId: null, tagIds: null, kind: null, sourceKey: null, tagged: null, minConfidence: null, minDurationSec: null, excludeVideoIds: null, hasPayload: true),
            _ => [1003])];

        yield return [new FilterProbe(
            "filter:performers:NameCriterion/includes",
            fixture => QueryFilteredIdsAsync(fixture.Context, "performers", new PerformerFilter { NameCriterion = new StringCriterion { Modifier = CriterionModifier.Includes, Value = "Cora" } }),
            _ => [303])];
        yield return [new FilterProbe(
            "filter:performers:HeightCriterion/greater_than",
            fixture => QueryFilteredIdsAsync(fixture.Context, "performers", new PerformerFilter { HeightCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 165 } }),
            _ => [302, 303])];
        yield return [new FilterProbe(
            "filter:performers:VideoCountCriterion/greater_than",
            fixture => QueryFilteredIdsAsync(fixture.Context, "performers", new PerformerFilter { VideoCountCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 1 } }),
            _ => [301, 302])];
        yield return [new FilterProbe(
            "filter:performers:ImageCountCriterion/greater_than",
            fixture => QueryFilteredIdsAsync(fixture.Context, "performers", new PerformerFilter { ImageCountCriterion = new IntCriterion { Modifier = CriterionModifier.GreaterThan, Value = 1 } }),
            _ => [301, 302])];

        yield return [new FilterProbe(
            "filter:studios:NameCriterion/includes",
            fixture => QueryFilteredIdsAsync(fixture.Context, "studios", new StudioFilter { NameCriterion = new StringCriterion { Modifier = CriterionModifier.Includes, Value = "Beryl" } }),
            _ => [102])];
        yield return [new FilterProbe(
            "filter:studios:OrganizedCriterion/true",
            fixture => QueryFilteredIdsAsync(fixture.Context, "studios", new StudioFilter { OrganizedCriterion = new BoolCriterion { Value = true } }),
            _ => [101, 103])];

        yield return [new FilterProbe(
            "filter:tags:NameCriterion/includes",
            fixture => QueryFilteredIdsAsync(fixture.Context, "tags", new TagFilter { NameCriterion = new StringCriterion { Modifier = CriterionModifier.Includes, Value = "Bloom" } }),
            _ => [202])];
        yield return [new FilterProbe(
            "filter:tags:FavoriteCriterion/true",
            fixture => QueryFilteredIdsAsync(fixture.Context, "tags", new TagFilter { FavoriteCriterion = new BoolCriterion { Value = true } }),
            _ => [202])];

        yield return [new FilterProbe(
            "filter:faces:performerId/equals",
            // Performer 302 belongs to the merged face 1102, which is intentionally hidden from the list
            // (a merged face is a tombstone). Target the non-merged 1101 (performer 301) so this still
            // positively exercises the performerId filter.
            fixture => QueryFaceIdsAsync(fixture.Context, performerId: 301, linked: null, ignored: null, merged: null),
            _ => [1101])];
        yield return [new FilterProbe(
            "filter:faces:linked/true",
            // 1102 is linked to a performer but merged, so it never surfaces.
            fixture => QueryFaceIdsAsync(fixture.Context, performerId: null, linked: true, ignored: null, merged: null),
            _ => [1101, 1103])];
        yield return [new FilterProbe(
            "filter:faces:label/includes",
            fixture => QueryFaceIdsAsync(fixture.Context, performerId: null, linked: null, ignored: null, merged: null, label: "Gamma", labelModifier: "INCLUDES"),
            _ => [1103])];
        yield return [new FilterProbe(
            "filter:faces:ignored/true",
            fixture => QueryFaceIdsAsync(fixture.Context, performerId: null, linked: null, ignored: true, merged: null),
            _ => [1103])];
        yield return [new FilterProbe(
            "filter:faces:merged/true",
            // The merged param is intentionally inert: merged faces are tombstones hidden from the list,
            // so merged=true still returns only the non-merged visible faces (the merged 1102 never
            // surfaces). This guards that the blanket exclusion overrides the merged param.
            fixture => QueryFaceIdsAsync(fixture.Context, performerId: null, linked: null, ignored: null, merged: true),
            _ => [1101, 1103])];
        yield return [new FilterProbe(
            "filter:faces:hasCover/true",
            // 1102 has a cover but is merged (hidden); only the non-merged 1103 surfaces.
            fixture => QueryFaceIdsAsync(fixture.Context, performerId: null, linked: null, ignored: null, merged: null, hasCover: true),
            _ => [1103])];
        yield return [new FilterProbe(
            "filter:faces:detectionCount/greater_than",
            fixture => QueryFaceIdsAsync(fixture.Context, performerId: null, linked: null, ignored: null, merged: null, detectionCount: 4, detectionCountModifier: "GREATER_THAN"),
            _ => [1103])];
    }

    [Fact]
    public void NonBrokenPublishedSortsHaveBehaviorProbeOrExplicitExemption()
    {
        var missing = EntityListSortFilterCatalog.Sorts
            .Where(sort => sort.KnownBrokenReason is null)
            .Where(sort => !IsBehaviorTested(sort))
            .Where(sort => !SortRowsWithBehaviorExemptions.Contains(sort.RowId))
            .Select(sort => sort.RowId)
            .OrderBy(row => row, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Empty(missing);
    }

    [Theory]
    [MemberData(nameof(SortRows))]
    public async Task PublishedSortOrdersMatchSeededFixtureProjection(string entity, string sortKey, CoveSortDirection direction)
    {
        await using var fixture = await SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();

        var actualIds = await QueryIdsAsync(fixture.Context, entity, sortKey, direction);
        var expectedIds = ProjectExpectedIds(fixture, entity, sortKey, direction);

        Assert.Equal(expectedIds, actualIds);
    }

    [Theory]
    [InlineData("images")]
    [InlineData("galleries")]
    [InlineData("audios")]
    [InlineData("texts")]
    [InlineData("performers")]
    [InlineData("studios")]
    [InlineData("tags")]
    public async Task CompoundSortUsesSecondaryClauseAcrossSupportedEntityLists(string entity)
    {
        await using var fixture = await SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();

        var sharedUpdatedAt = DateTime.UtcNow.AddYears(-10);
        switch (entity)
        {
            case "images": (await fixture.Context.Images.ToListAsync()).ForEach(item => item.UpdatedAt = sharedUpdatedAt); break;
            case "galleries": (await fixture.Context.Galleries.ToListAsync()).ForEach(item => item.UpdatedAt = sharedUpdatedAt); break;
            case "audios": (await fixture.Context.Audios.ToListAsync()).ForEach(item => item.UpdatedAt = sharedUpdatedAt); break;
            case "texts": (await fixture.Context.TextDocuments.ToListAsync()).ForEach(item => item.UpdatedAt = sharedUpdatedAt); break;
            case "performers": (await fixture.Context.Performers.ToListAsync()).ForEach(item => item.UpdatedAt = sharedUpdatedAt); break;
            case "studios": (await fixture.Context.Studios.ToListAsync()).ForEach(item => item.UpdatedAt = sharedUpdatedAt); break;
            case "tags": (await fixture.Context.Tags.ToListAsync()).ForEach(item => item.UpdatedAt = sharedUpdatedAt); break;
        }
        await fixture.Context.SaveChangesAsync();

        var labelKey = entity is "performers" or "studios" or "tags" ? "name" : "title";
        var updatedKey = entity is "audios" or "texts" ? "updatedAt" : "updated_at";
        var findFilter = new FindFilter
        {
            Page = 1,
            PerPage = 50,
            Sort = updatedKey,
            Direction = CoveSortDirection.Asc,
            Sorts =
            [
                new SortClause(updatedKey, CoveSortDirection.Asc),
                new SortClause(labelKey, CoveSortDirection.Desc),
            ],
        };

        var actualIds = entity switch
        {
            "images" => (await new ImageRepository(fixture.Context).FindAsync(null, findFilter)).Items.Select(item => item.Id).ToArray(),
            "galleries" => (await new GalleryRepository(fixture.Context).FindAsync(null, findFilter)).Items.Select(item => item.Id).ToArray(),
            "audios" => await QueryFilteredAudioIdsAsync(fixture.Context, new AudioFilter(), findFilter),
            "texts" => await QueryFilteredTextIdsAsync(fixture.Context, new TextDocumentFilter(), findFilter),
            "performers" => (await new PerformerRepository(fixture.Context).FindAsync(null, findFilter)).Items.Select(item => item.Id).ToArray(),
            "studios" => (await new StudioRepository(fixture.Context).FindAsync(null, findFilter)).Items.Select(item => item.Id).ToArray(),
            "tags" => (await new TagRepository(fixture.Context).FindAsync(null, findFilter)).Items.Select(item => item.Id).ToArray(),
            _ => throw new InvalidOperationException($"Unsupported entity '{entity}'."),
        };

        Assert.Equal(actualIds.OrderByDescending(id => id).ToArray(), actualIds);
    }

    [Theory]
    [InlineData("videos")]
    [InlineData("images")]
    [InlineData("galleries")]
    [InlineData("audios")]
    [InlineData("texts")]
    [InlineData("performers")]
    [InlineData("studios")]
    [InlineData("tags")]
    public async Task CompoundRatingSortUsesSecondaryClauseAcrossSupportedEntityLists(string entity)
    {
        await using var fixture = await SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();

        var (hostType, entityIds) = entity switch
        {
            "videos" => (RatingHostType.Video, fixture.Videos.Select(item => item.Id).ToArray()),
            "images" => (RatingHostType.Image, fixture.Images.Select(item => item.Id).ToArray()),
            "galleries" => (RatingHostType.Gallery, fixture.Galleries.Select(item => item.Id).ToArray()),
            "audios" => (RatingHostType.Audio, fixture.Audios.Select(item => item.Id).ToArray()),
            "texts" => (RatingHostType.Text, fixture.Texts.Select(item => item.Id).ToArray()),
            "performers" => (RatingHostType.Performer, fixture.Performers.Select(item => item.Id).ToArray()),
            "studios" => (RatingHostType.Studio, fixture.Studios.Select(item => item.Id).ToArray()),
            "tags" => (RatingHostType.Tag, fixture.Tags.Select(item => item.Id).ToArray()),
            _ => throw new InvalidOperationException($"Unsupported entity '{entity}'."),
        };

        var existingRatings = await fixture.Context.Ratings
            .Where(rating => rating.UserId == TestUserId && rating.HostType == hostType && rating.Aspect == "overall")
            .ToListAsync();
        fixture.Context.Ratings.RemoveRange(existingRatings);
        fixture.Context.Ratings.AddRange(
            new Rating { UserId = TestUserId, HostType = hostType, HostId = entityIds[0], Aspect = "overall", Value = 5 },
            new Rating { UserId = TestUserId, HostType = hostType, HostId = entityIds[1], Aspect = "overall", Value = 5 },
            new Rating { UserId = TestUserId, HostType = hostType, HostId = entityIds[2], Aspect = "overall", Value = 1 });
        await fixture.Context.SaveChangesAsync();

        var labelKey = entity is "performers" or "studios" or "tags" ? "name" : "title";
        var findFilter = new FindFilter
        {
            Page = 1,
            PerPage = 50,
            Sorts =
            [
                new SortClause("rating", CoveSortDirection.Desc),
                new SortClause(labelKey, CoveSortDirection.Desc),
            ],
        };

        var actualIds = entity switch
        {
            "videos" => (await new VideoRepository(fixture.Context).FindAsync(null, findFilter)).Items.Select(item => item.Id).ToArray(),
            "images" => (await new ImageRepository(fixture.Context).FindAsync(null, findFilter)).Items.Select(item => item.Id).ToArray(),
            "galleries" => (await new GalleryRepository(fixture.Context).FindAsync(null, findFilter)).Items.Select(item => item.Id).ToArray(),
            "audios" => await QueryFilteredAudioIdsAsync(fixture.Context, new AudioFilter(), findFilter),
            "texts" => await QueryFilteredTextIdsAsync(fixture.Context, new TextDocumentFilter(), findFilter),
            "performers" => (await new PerformerRepository(fixture.Context).FindAsync(null, findFilter)).Items.Select(item => item.Id).ToArray(),
            "studios" => (await new StudioRepository(fixture.Context).FindAsync(null, findFilter)).Items.Select(item => item.Id).ToArray(),
            "tags" => (await new TagRepository(fixture.Context).FindAsync(null, findFilter)).Items.Select(item => item.Id).ToArray(),
            _ => throw new InvalidOperationException($"Unsupported entity '{entity}'."),
        };

        Assert.Equal([entityIds[1], entityIds[0], entityIds[2]], actualIds);
    }

    [Theory]
    [InlineData("videos", "play_count")]
    [InlineData("videos", "like_counter")]
    [InlineData("videos", "last_like_at")]
    [InlineData("videos", "last_played_at")]
    [InlineData("videos", "play_duration")]
    [InlineData("videos", "resume_time")]
    [InlineData("images", "like_counter")]
    [InlineData("audios", "play_count")]
    [InlineData("audios", "like_counter")]
    [InlineData("audios", "play_duration")]
    [InlineData("audios", "last_played_at")]
    [InlineData("texts", "read_count")]
    [InlineData("texts", "like_counter")]
    [InlineData("texts", "read_duration")]
    [InlineData("texts", "last_read_at")]
    public async Task CompoundEngagementSortUsesSecondaryClause(string entity, string sortKey)
    {
        await using var fixture = await SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();

        var (hostType, entityIds) = entity switch
        {
            "videos" => (AffinityHostType.Video, fixture.Videos.Select(item => item.Id).ToArray()),
            "images" => (AffinityHostType.Image, fixture.Images.Select(item => item.Id).ToArray()),
            "audios" => (AffinityHostType.Audio, fixture.Audios.Select(item => item.Id).ToArray()),
            "texts" => (AffinityHostType.Text, fixture.Texts.Select(item => item.Id).ToArray()),
            _ => throw new InvalidOperationException($"Unsupported entity '{entity}'."),
        };

        for (var index = 0; index < entityIds.Length; index++)
        {
            var affinity = fixture.Affinity(hostType, entityIds[index]);
            var tiedValue = index < 2;
            switch (sortKey)
            {
                case "play_count":
                case "read_count": affinity.ViewCount = tiedValue ? 5 : 1; break;
                case "like_counter": affinity.LikeCount = tiedValue ? 5 : 1; break;
                case "play_duration":
                case "read_duration": affinity.TotalConsumedSec = tiedValue ? 500 : 100; break;
                case "resume_time": affinity.LastPositionSec = tiedValue ? 50 : 10; break;
                case "last_like_at": affinity.FavoritedAt = tiedValue ? new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc) : new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc); break;
                case "last_played_at":
                case "last_read_at": affinity.LastConsumedAt = tiedValue ? new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc) : new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc); break;
            }
        }
        await fixture.Context.SaveChangesAsync();

        var findFilter = new FindFilter
        {
            Page = 1,
            PerPage = 50,
            Sorts =
            [
                new SortClause(sortKey, CoveSortDirection.Desc),
                new SortClause("title", CoveSortDirection.Desc),
            ],
        };

        var actualIds = entity switch
        {
            "videos" => (await new VideoRepository(fixture.Context).FindAsync(null, findFilter)).Items.Select(item => item.Id).ToArray(),
            "images" => (await new ImageRepository(fixture.Context).FindAsync(null, findFilter)).Items.Select(item => item.Id).ToArray(),
            "audios" => await QueryFilteredAudioIdsAsync(fixture.Context, new AudioFilter(), findFilter),
            "texts" => await QueryFilteredTextIdsAsync(fixture.Context, new TextDocumentFilter(), findFilter),
            _ => throw new InvalidOperationException($"Unsupported entity '{entity}'."),
        };

        Assert.Equal([entityIds[1], entityIds[0], entityIds[2]], actualIds);
    }

    [Fact]
    public async Task GalleryCompoundLikeSortUsesLikeCountBeforeSecondaryDate()
    {
        await using var fixture = await SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();

        var galleriesByLikes = fixture.Galleries
            .OrderByDescending(gallery =>
                gallery.ImageGalleries.Sum(link => fixture.Affinity(AffinityHostType.Image, link.ImageId).LikeCount)
                + gallery.VideoGalleries.Sum(link => fixture.Affinity(AffinityHostType.Video, link.VideoId).LikeCount))
            .ToArray();
        galleriesByLikes[0].Date = new DateOnly(2020, 1, 1);
        galleriesByLikes[1].Date = new DateOnly(2030, 1, 1);
        galleriesByLikes[2].Date = new DateOnly(2025, 1, 1);
        await fixture.Context.SaveChangesAsync();

        var findFilter = new FindFilter
        {
            Page = 1,
            PerPage = 50,
            Sorts =
            [
                new SortClause("like_counter", CoveSortDirection.Desc),
                new SortClause("date", CoveSortDirection.Desc),
            ],
        };

        var actualIds = (await new GalleryRepository(fixture.Context).FindAsync(null, findFilter)).Items.Select(item => item.Id).ToArray();
        var expectedIds = fixture.Galleries
            .OrderByDescending(gallery =>
                gallery.ImageGalleries.Sum(link => fixture.Affinity(AffinityHostType.Image, link.ImageId).LikeCount)
                + gallery.VideoGalleries.Sum(link => fixture.Affinity(AffinityHostType.Video, link.VideoId).LikeCount))
            .ThenByDescending(gallery => gallery.Date)
            .ThenBy(gallery => gallery.Id)
            .Select(gallery => gallery.Id)
            .ToArray();

        Assert.Equal(expectedIds, actualIds);
    }

    [Theory]
    [MemberData(nameof(FilterRows))]
    public async Task RepresentativeFiltersMatchSeededFixtureSet(FilterProbe probe)
    {
        await using var fixture = await SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();

        var actualIds = await probe.QueryIds(fixture);
        var expectedIds = probe.ExpectedIds(fixture);

        Assert.Equal(
            expectedIds.OrderBy(id => id).ToArray(),
            actualIds.OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task CustomFieldFiltersAndSortsExecuteForExtensionListContributions()
    {
        await using var fixture = await SortHarnessFixture.CreateAsync();
        fixture.ActivatePrincipal();

        var criterion = new CustomFieldCriterion
        {
            Key = "extension_score",
            Type = CustomFieldTypes.Number,
            Modifier = CriterionModifier.GreaterThan,
            Value = "15",
        };

        var videoFilteredIds = await QueryFilteredIdsAsync(fixture.Context, "videos", new VideoFilter { CustomFieldCriteria = [criterion] });
        Assert.Equal([401, 403], videoFilteredIds.OrderBy(id => id).ToArray());

        var videoSortedIds = await QueryIdsAsync(fixture.Context, "videos", "custom:number:extension_score", CoveSortDirection.Asc);
        Assert.Equal([402, 401, 403], videoSortedIds);

        var audioFilteredIds = await QueryFilteredIdsAsync(fixture.Context, "audios", new AudioFilter { CustomFieldCriteria = [criterion] });
        Assert.Equal([801, 803], audioFilteredIds.OrderBy(id => id).ToArray());

        var audioSortedIds = await QueryAudioIdsAsync(fixture.Context, "custom:number:extension_score", CoveSortDirection.Desc);
        Assert.Equal([803, 801, 802], audioSortedIds);

        var faceFilteredIds = await QueryFaceIdsAsync(
            fixture.Context,
            performerId: null,
            linked: null,
            ignored: null,
            merged: null,
            customFieldCriteria: "[{\"key\":\"extension_score\",\"type\":\"number\",\"modifier\":\"GREATER_THAN\",\"value\":\"15\"}]");
        Assert.Equal([1101, 1103], faceFilteredIds.OrderBy(id => id).ToArray());

        var faceSortedIds = await QueryFaceIdsAsync(fixture.Context, "custom:number:extension_score", CoveSortDirection.Desc);
        // 1102 is merged and hidden from the list, so it never appears in the sorted result.
        Assert.Equal([1103, 1101], faceSortedIds);
    }

    private static bool IsBehaviorTested(EntityListSortDefinition sort)
        => sort.KnownBrokenReason is null && !SortRowsWithBehaviorExemptions.Contains(sort.RowId);

    private static async Task<IReadOnlyList<int>> QueryIdsAsync(CoveContext context, string entity, string sortKey, CoveSortDirection direction)
    {
        var findFilter = new FindFilter
        {
            Page = 1,
            PerPage = 50,
            Sort = sortKey,
            Direction = direction,
            Seed = 12345,
        };

        return entity switch
        {
            "videos" => (await new VideoRepository(context).FindAsync(null, findFilter)).Items.Select(item => item.Id).ToArray(),
            "images" => (await new ImageRepository(context).FindAsync(null, findFilter)).Items.Select(item => item.Id).ToArray(),
            "galleries" => (await new GalleryRepository(context).FindAsync(null, findFilter)).Items.Select(item => item.Id).ToArray(),
            "groups" => (await new GroupRepository(context).FindAsync(null, findFilter)).Items.Select(item => item.Id).ToArray(),
            "performers" => (await new PerformerRepository(context).FindAsync(null, findFilter)).Items.Select(item => item.Id).ToArray(),
            "studios" => (await new StudioRepository(context).FindAsync(null, findFilter)).Items.Select(item => item.Id).ToArray(),
            "tags" => (await new TagRepository(context).FindAsync(null, findFilter)).Items.Select(item => item.Id).ToArray(),
            "audios" => await QueryAudioIdsAsync(context, sortKey, direction),
            "texts" => await QueryTextIdsAsync(context, sortKey, direction),
            "segments" => await QuerySegmentIdsAsync(context, sortKey, direction),
            "faces" => await QueryFaceIdsAsync(context, sortKey, direction),
            _ => throw new InvalidOperationException($"No sort behavior query configured for entity '{entity}'."),
        };
    }

    private static async Task<IReadOnlyList<int>> QueryAudioIdsAsync(CoveContext context, string sortKey, CoveSortDirection direction)
    {
        var controller = new AudiosController(context, null!, null!, null!, null!, null);
        var response = await controller.Find(null, 1, 50, sortKey, DirectionValue(direction));
        return ExtractItems(response).Select(item => item.Id).ToArray();
    }

    private static async Task<IReadOnlyList<int>> QueryTextIdsAsync(CoveContext context, string sortKey, CoveSortDirection direction)
    {
        var controller = new TextsController(context, null!, null!, null!, null!, null!, null);
        var response = await controller.Find(null, 1, 50, sortKey, DirectionValue(direction));
        return ExtractItems(response).Select(item => item.Id).ToArray();
    }

    private static async Task<IReadOnlyList<int>> QuerySegmentIdsAsync(CoveContext context, string sortKey, CoveSortDirection direction)
    {
        var controller = new SegmentsController(context, null!, new MemoryCache(new MemoryCacheOptions()));
        var response = await controller.List(
            q: null,
            ids: null,
            videoId: null,
            videoIds: null,
            videoTitle: null,
            tagId: null,
            tagIds: null,
            kind: null,
            sourceKey: null,
            sourceCategory: null,
            refIds: null,
            performerIds: null,
            tagged: null,
            minConfidence: null,
            minDurationSec: null,
            confidence: null,
            confidence2: null,
            confidenceModifier: null,
            durationSec: null,
            durationSec2: null,
            durationModifier: null,
            sort: sortKey,
            direction: DirectionValue(direction),
            excludeVideoIds: null,
            page: 1,
            perPage: 50);

        return ExtractItems(response).Select(item => item.Id).ToArray();
    }

    private static async Task<IReadOnlyList<int>> QuerySegmentIdsAsync(
        CoveContext context,
        string? q,
        string? ids,
        int? videoId,
        string? videoIds,
        string? videoTitle,
        int? tagId,
        string? tagIds,
        string? kind,
        string? sourceKey,
        bool? tagged,
        float? minConfidence,
        double? minDurationSec,
        string? excludeVideoIds,
        string? title = null,
        string? titleModifier = null,
        double? startSec = null,
        string? startSecModifier = null,
        string? createdAt = null,
        string? createdAtModifier = null,
        bool? hasImage = null,
        bool? hasPayload = null)
    {
        var controller = new SegmentsController(context, null!, new MemoryCache(new MemoryCacheOptions()));
        var response = await controller.List(
            q,
            ids,
            videoId,
            videoIds,
            videoTitle,
            tagId,
            tagIds,
            kind,
            sourceKey,
            sourceCategory: null,
            refIds: null,
            performerIds: null,
            tagged,
            minConfidence,
            minDurationSec,
            confidence: null,
            confidence2: null,
            confidenceModifier: null,
            durationSec: null,
            durationSec2: null,
            durationModifier: null,
            sort: "updated_at",
            direction: "asc",
            excludeVideoIds: excludeVideoIds,
            title: title,
            titleModifier: titleModifier,
            hasImage: hasImage,
            hasPayload: hasPayload,
            startSec: startSec,
            startSecModifier: startSecModifier,
            createdAt: createdAt,
            createdAtModifier: createdAtModifier,
            page: 1,
            perPage: 50);

        return ExtractItems(response).Select(item => item.Id).ToArray();
    }

    private static async Task<IReadOnlyList<int>> QueryFaceIdsAsync(CoveContext context, string sortKey, CoveSortDirection direction = CoveSortDirection.Desc)
    {
        var controller = new FacesController(context, null!, null!, null!, [], NullLogger<FacesController>.Instance, [], null);
        var response = await controller.List(
            q: null,
            performerId: null,
            performerIds: null,
            linked: null,
            ignored: null,
            merged: null,
            minSuggestionConfidence: null,
            suggestionConfidence: null,
            suggestionConfidence2: null,
            suggestionConfidenceModifier: null,
            topSuggestionPerformerIds: null,
            sort: sortKey,
            direction: direction,
            customFieldCriteria: null,
            page: 1,
            perPage: 50);

        return ExtractItems(response).Select(item => item.Id).ToArray();
    }

    private static async Task<IReadOnlyList<int>> QueryFaceIdsAsync(
        CoveContext context,
        int? performerId,
        bool? linked,
        bool? ignored,
        bool? merged,
        int? mergedIntoFaceId = null,
        string? label = null,
        string? labelModifier = null,
        string? primarySourceKey = null,
        string? primarySourceKeyModifier = null,
        bool? hasCover = null,
        int? detectionCount = null,
        int? detectionCount2 = null,
        string? detectionCountModifier = null,
        int? appearanceCount = null,
        int? appearanceCount2 = null,
        string? appearanceCountModifier = null,
        int? frameSampleCount = null,
        int? frameSampleCount2 = null,
        string? frameSampleCountModifier = null,
        int? videoCount = null,
        int? videoCount2 = null,
        string? videoCountModifier = null,
        int? imageCount = null,
        int? imageCount2 = null,
        string? imageCountModifier = null,
        string? customFieldCriteria = null)
    {
        var controller = new FacesController(context, null!, null!, null!, [], NullLogger<FacesController>.Instance, [], null);
        var response = await controller.List(
            q: null,
            performerId: performerId,
            performerIds: null,
            linked: linked,
            ignored: ignored,
            merged: merged,
            mergedIntoFaceId: mergedIntoFaceId,
            label: label,
            labelModifier: labelModifier,
            primarySourceKey: primarySourceKey,
            primarySourceKeyModifier: primarySourceKeyModifier,
            hasCover: hasCover,
            detectionCount: detectionCount,
            detectionCount2: detectionCount2,
            detectionCountModifier: detectionCountModifier,
            appearanceCount: appearanceCount,
            appearanceCount2: appearanceCount2,
            appearanceCountModifier: appearanceCountModifier,
            frameSampleCount: frameSampleCount,
            frameSampleCount2: frameSampleCount2,
            frameSampleCountModifier: frameSampleCountModifier,
            videoCount: videoCount,
            videoCount2: videoCount2,
            videoCountModifier: videoCountModifier,
            imageCount: imageCount,
            imageCount2: imageCount2,
            imageCountModifier: imageCountModifier,
            minSuggestionConfidence: null,
            suggestionConfidence: null,
            suggestionConfidence2: null,
            suggestionConfidenceModifier: null,
            topSuggestionPerformerIds: null,
            sort: "created_desc",
            direction: CoveSortDirection.Desc,
            customFieldCriteria: customFieldCriteria,
            page: 1,
            perPage: 50);

        return ExtractItems(response).Select(item => item.Id).ToArray();
    }

    private static async Task<IReadOnlyList<int>> QueryFilteredIdsAsync(CoveContext context, string entity, object filter)
    {
        var findFilter = new FindFilter { Page = 1, PerPage = 50, Sort = "created_at", Direction = CoveSortDirection.Asc };
        return (entity, filter) switch
        {
            ("videos", VideoFilter videoFilter) => (await new VideoRepository(context).FindAsync(videoFilter, findFilter)).Items.Select(item => item.Id).ToArray(),
            ("images", ImageFilter imageFilter) => (await new ImageRepository(context).FindAsync(imageFilter, findFilter)).Items.Select(item => item.Id).ToArray(),
            ("galleries", GalleryFilter galleryFilter) => (await new GalleryRepository(context).FindAsync(galleryFilter, findFilter)).Items.Select(item => item.Id).ToArray(),
            ("groups", GroupFilter groupFilter) => (await new GroupRepository(context).FindAsync(groupFilter, findFilter)).Items.Select(item => item.Id).ToArray(),
            ("performers", PerformerFilter performerFilter) => (await new PerformerRepository(context).FindAsync(performerFilter, findFilter)).Items.Select(item => item.Id).ToArray(),
            ("studios", StudioFilter studioFilter) => (await new StudioRepository(context).FindAsync(studioFilter, findFilter)).Items.Select(item => item.Id).ToArray(),
            ("tags", TagFilter tagFilter) => (await new TagRepository(context).FindAsync(tagFilter, findFilter)).Items.Select(item => item.Id).ToArray(),
            ("audios", AudioFilter audioFilter) => await QueryFilteredAudioIdsAsync(context, audioFilter, findFilter),
            ("texts", TextDocumentFilter textFilter) => await QueryFilteredTextIdsAsync(context, textFilter, findFilter),
            _ => throw new InvalidOperationException($"No filter behavior query configured for entity '{entity}'."),
        };
    }

    private static async Task<IReadOnlyList<int>> QueryFilteredAudioIdsAsync(CoveContext context, AudioFilter filter, FindFilter findFilter)
    {
        var controller = new AudiosController(context, null!, null!, null!, null!, null);
        var response = await controller.FindPost(new FilteredQueryRequest<AudioFilter> { ObjectFilter = filter, FindFilter = findFilter }, CancellationToken.None);
        return ExtractItems(response).Select(item => item.Id).ToArray();
    }

    private static async Task<IReadOnlyList<int>> QueryFilteredTextIdsAsync(CoveContext context, TextDocumentFilter filter, FindFilter findFilter)
    {
        var controller = new TextsController(context, null!, null!, null!, null!, null!, null);
        var response = await controller.FindPost(new FilteredQueryRequest<TextDocumentFilter> { ObjectFilter = filter, FindFilter = findFilter }, CancellationToken.None);
        return ExtractItems(response).Select(item => item.Id).ToArray();
    }

    private static IReadOnlyList<T> ExtractItems<T>(ActionResult<PaginatedResponse<T>> actionResult)
    {
        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var response = Assert.IsType<PaginatedResponse<T>>(ok.Value);
        return response.Items;
    }

    private static string DirectionValue(CoveSortDirection direction)
        => direction == CoveSortDirection.Desc ? "desc" : "asc";

    private static IReadOnlyList<int> ProjectExpectedIds(SortHarnessFixture fixture, string entity, string sortKey, CoveSortDirection direction)
    {
        var descending = direction == CoveSortDirection.Desc;
        return entity switch
        {
            "videos" => ProjectVideoIds(fixture, sortKey, descending),
            "images" => ProjectImageIds(fixture, sortKey, descending),
            "audios" => ProjectAudioIds(fixture, sortKey, descending),
            "texts" => ProjectTextIds(fixture, sortKey, descending),
            "galleries" => ProjectGalleryIds(fixture, sortKey, descending),
            "groups" => ProjectGroupIds(fixture, sortKey, descending),
            "segments" => ProjectSegmentIds(fixture, sortKey, descending),
            "performers" => ProjectPerformerIds(fixture, sortKey, descending),
            "studios" => ProjectStudioIds(fixture, sortKey, descending),
            "tags" => ProjectTagIds(fixture, sortKey, descending),
            "faces" => ProjectFaceIds(fixture, sortKey, descending),
            _ => throw new InvalidOperationException($"No sort projection configured for entity '{entity}'."),
        };
    }

    private static IReadOnlyList<int> ProjectVideoIds(SortHarnessFixture fixture, string sortKey, bool descending)
        => sortKey switch
        {
            "updated_at" => Order(fixture.Videos, video => video.UpdatedAt, descending),
            "created_at" => Order(fixture.Videos, video => video.CreatedAt, descending),
            "title" => Order(fixture.Videos, video => video.Title, descending),
            "date" => Order(fixture.Videos, video => video.Date ?? DateOnly.MinValue, descending),
            "rating" => Order(fixture.Videos, video => fixture.Rating(RatingHostType.Video, video.Id), descending),
            "play_count" => Order(fixture.Videos, video => fixture.Affinity(AffinityHostType.Video, video.Id).ViewCount, descending),
            "like_counter" => Order(fixture.Videos, video => fixture.Affinity(AffinityHostType.Video, video.Id).LikeCount, descending),
            "last_like_at" => Order(fixture.Videos, video => fixture.Affinity(AffinityHostType.Video, video.Id).FavoritedAt, descending),
            "duration" => Order(fixture.Videos, video => video.MaxDuration, descending),
            "file_size" => Order(fixture.Videos, video => video.MaxFileSize, descending),
            "file_mod_time" => Order(fixture.Videos, video => video.MaxFileModTime, descending),
            "file_count" => Order(fixture.Videos, video => video.Files.Count, descending),
            "path" => descending ? Order(fixture.Videos, video => video.MaxPath, true) : Order(fixture.Videos, video => video.MinPath, false),
            "resolution" => Order(fixture.Videos, video => video.MaxHeight, descending),
            "framerate" => Order(fixture.Videos, video => video.MaxFrameRate, descending),
            "bitrate" => Order(fixture.Videos, video => video.MaxBitRate, descending),
            "phash" => Order(fixture.Videos, video => VideoPhash(video, descending), descending),
            "tag_count" => Order(fixture.Videos, video => video.VideoTags.Count, descending),
            "performer_count" => Order(fixture.Videos, video => video.VideoPerformers.Count, descending),
            "performer_age" => Order(fixture.Videos, video => PerformerAge(video, descending), descending),
            "studio" => Order(fixture.Videos, video => video.Studio!.Name, descending),
            "code" => Order(fixture.Videos, video => video.Code, descending),
            "last_played_at" => Order(fixture.Videos, video => fixture.Affinity(AffinityHostType.Video, video.Id).LastConsumedAt, descending),
            "play_duration" => Order(fixture.Videos, video => fixture.Affinity(AffinityHostType.Video, video.Id).TotalConsumedSec, descending),
            "resume_time" => Order(fixture.Videos, video => fixture.Affinity(AffinityHostType.Video, video.Id).LastPositionSec, descending),
            "organized" => Order(fixture.Videos, video => video.Organized, descending),
            _ => throw new InvalidOperationException($"No video sort projection configured for '{sortKey}'."),
        };

    private static IReadOnlyList<int> ProjectImageIds(SortHarnessFixture fixture, string sortKey, bool descending)
        => sortKey switch
        {
            "updated_at" => Order(fixture.Images, image => image.UpdatedAt, descending),
            "created_at" => Order(fixture.Images, image => image.CreatedAt, descending),
            "date" => Order(fixture.Images, image => image.Date ?? DateOnly.MinValue, descending),
            "file_mod_time" => Order(fixture.Images, image => image.MaxFileModTime, descending),
            "file_size" => Order(fixture.Images, image => image.MaxFileSize, descending),
            "resolution" => Order(fixture.Images, image => image.MaxResolution, descending),
            "path" => descending ? Order(fixture.Images, image => image.MaxPath, true) : Order(fixture.Images, image => image.MinPath, false),
            "title" => Order(fixture.Images, image => image.Title, descending),
            "rating" => Order(fixture.Images, image => fixture.Rating(RatingHostType.Image, image.Id), descending),
            "like_counter" => Order(fixture.Images, image => fixture.Affinity(AffinityHostType.Image, image.Id).LikeCount, descending),
            "performer_count" => Order(fixture.Images, image => image.ImagePerformers.Count, descending),
            "tag_count" => Order(fixture.Images, image => image.ImageTags.Count, descending),
            _ => throw new InvalidOperationException($"No image sort projection configured for '{sortKey}'."),
        };

    private static IReadOnlyList<int> ProjectAudioIds(SortHarnessFixture fixture, string sortKey, bool descending)
        => NormalizeControllerSortKey(sortKey) switch
        {
            "updatedat" => OrderWithDirectionalIdTieBreaker(fixture.Audios, audio => audio.UpdatedAt, descending),
            "createdat" => OrderWithDirectionalIdTieBreaker(fixture.Audios, audio => audio.CreatedAt, descending),
            "date" => OrderWithDirectionalIdTieBreaker(fixture.Audios, audio => audio.Date, descending),
            "duration" => OrderWithDirectionalIdTieBreaker(fixture.Audios, audio => audio.MaxDuration, descending),
            "rating" => OrderWithDirectionalIdTieBreaker(fixture.Audios, audio => fixture.Rating(RatingHostType.Audio, audio.Id), descending),
            "playcount" => OrderWithDirectionalIdTieBreaker(fixture.Audios, audio => fixture.Affinity(AffinityHostType.Audio, audio.Id).ViewCount, descending),
            "likecounter" => OrderWithDirectionalIdTieBreaker(fixture.Audios, audio => fixture.Affinity(AffinityHostType.Audio, audio.Id).LikeCount, descending),
            "playduration" => OrderWithDirectionalIdTieBreaker(fixture.Audios, audio => fixture.Affinity(AffinityHostType.Audio, audio.Id).TotalConsumedSec, descending),
            "lastplayedat" => OrderWithDirectionalIdTieBreaker(fixture.Audios, audio => fixture.Affinity(AffinityHostType.Audio, audio.Id).LastConsumedAt, descending),
            "filesize" => OrderWithDirectionalIdTieBreaker(fixture.Audios, audio => audio.MaxFileSize, descending),
            "filemodtime" => OrderWithDirectionalIdTieBreaker(fixture.Audios, audio => audio.MaxFileModTime, descending),
            "filecount" => OrderWithDirectionalIdTieBreaker(fixture.Audios, audio => audio.FileCount, descending),
            "path" => descending ? OrderWithDirectionalIdTieBreaker(fixture.Audios, audio => audio.MaxPath, true) : OrderWithDirectionalIdTieBreaker(fixture.Audios, audio => audio.MinPath, false),
            "bitrate" => OrderWithDirectionalIdTieBreaker(fixture.Audios, audio => audio.MaxBitRate, descending),
            "hasvideofiles" => OrderWithDirectionalIdTieBreaker(fixture.Audios, audio => audio.HasVideoFiles, descending),
            "trackcount" => OrderWithDirectionalIdTieBreaker(fixture.Audios, audio => audio.Tracks.Count, descending),
            "tagcount" => OrderWithDirectionalIdTieBreaker(fixture.Audios, audio => audio.AudioTags.Count, descending),
            "performercount" => OrderWithDirectionalIdTieBreaker(fixture.Audios, audio => audio.AudioPerformers.Count, descending),
            "title" => OrderWithDirectionalIdTieBreaker(fixture.Audios, audio => audio.Title, descending),
            _ => throw new InvalidOperationException($"No audio sort projection configured for '{sortKey}'."),
        };

    private static IReadOnlyList<int> ProjectTextIds(SortHarnessFixture fixture, string sortKey, bool descending)
        => NormalizeControllerSortKey(sortKey) switch
        {
            "updatedat" => OrderWithDirectionalIdTieBreaker(fixture.Texts, text => text.UpdatedAt, descending),
            "createdat" => OrderWithDirectionalIdTieBreaker(fixture.Texts, text => text.CreatedAt, descending),
            "date" => OrderWithDirectionalIdTieBreaker(fixture.Texts, text => text.Date, descending),
            "words" => OrderWithDirectionalIdTieBreaker(fixture.Texts, text => text.MaxWordCount, descending),
            "pages" => OrderWithDirectionalIdTieBreaker(fixture.Texts, text => text.MaxPageCount, descending),
            "rating" => OrderWithDirectionalIdTieBreaker(fixture.Texts, text => fixture.Rating(RatingHostType.Text, text.Id), descending),
            "readcount" => OrderWithDirectionalIdTieBreaker(fixture.Texts, text => fixture.Affinity(AffinityHostType.Text, text.Id).ViewCount, descending),
            "likecounter" => OrderWithDirectionalIdTieBreaker(fixture.Texts, text => fixture.Affinity(AffinityHostType.Text, text.Id).LikeCount, descending),
            "readduration" => OrderWithDirectionalIdTieBreaker(fixture.Texts, text => fixture.Affinity(AffinityHostType.Text, text.Id).TotalConsumedSec, descending),
            "lastreadat" => OrderWithDirectionalIdTieBreaker(fixture.Texts, text => fixture.Affinity(AffinityHostType.Text, text.Id).LastConsumedAt, descending),
            "filesize" => OrderWithDirectionalIdTieBreaker(fixture.Texts, text => text.MaxFileSize, descending),
            "filemodtime" => OrderWithDirectionalIdTieBreaker(fixture.Texts, text => text.MaxFileModTime, descending),
            "filecount" => OrderWithDirectionalIdTieBreaker(fixture.Texts, text => text.FileCount, descending),
            "path" => descending ? OrderWithDirectionalIdTieBreaker(fixture.Texts, text => text.MaxPath, true) : OrderWithDirectionalIdTieBreaker(fixture.Texts, text => text.MinPath, false),
            "tagcount" => OrderWithDirectionalIdTieBreaker(fixture.Texts, text => text.TextTags.Count, descending),
            "performercount" => OrderWithDirectionalIdTieBreaker(fixture.Texts, text => text.TextPerformers.Count, descending),
            "title" => OrderWithDirectionalIdTieBreaker(fixture.Texts, text => text.Title, descending),
            _ => throw new InvalidOperationException($"No text sort projection configured for '{sortKey}'."),
        };

    private static IReadOnlyList<int> ProjectGalleryIds(SortHarnessFixture fixture, string sortKey, bool descending)
        => sortKey switch
        {
            "updated_at" => Order(fixture.Galleries, gallery => gallery.UpdatedAt, descending),
            "created_at" => Order(fixture.Galleries, gallery => gallery.CreatedAt, descending),
            "date" => Order(fixture.Galleries, gallery => gallery.Date ?? DateOnly.MinValue, descending),
            "studio" => Order(fixture.Galleries, gallery => gallery.Studio!.Name, descending),
            "file_mod_time" => Order(fixture.Galleries, gallery => gallery.Files.Max(file => file.ModTime), descending),
            "file_count" => Order(fixture.Galleries, gallery => gallery.Files.Count, descending),
            "path" => Order(fixture.Galleries, gallery => gallery.Folder!.Path, descending),
            "title" => Order(fixture.Galleries, gallery => gallery.Title, descending),
            "code" => Order(fixture.Galleries, gallery => gallery.Code, descending),
            "photographer" => Order(fixture.Galleries, gallery => gallery.Photographer, descending),
            "organized" => OrderWithDirectionalIdTieBreaker(fixture.Galleries, gallery => gallery.Organized, descending),
            "rating" => Order(fixture.Galleries, gallery => fixture.Rating(RatingHostType.Gallery, gallery.Id), descending),
            "like_counter" => Order(fixture.Galleries, gallery => gallery.ImageGalleries.Sum(link => fixture.Affinity(AffinityHostType.Image, link.ImageId).LikeCount) + gallery.VideoGalleries.Sum(link => fixture.Affinity(AffinityHostType.Video, link.VideoId).LikeCount), descending),
            "last_like_at" => Order(fixture.Galleries, gallery => gallery.ImageGalleries.Select(link => fixture.LastLike(InteractionHostType.Image, link.ImageId)).Concat(gallery.VideoGalleries.Select(link => fixture.LastLike(InteractionHostType.Video, link.VideoId))).Max(), descending),
            "image_count" => Order(fixture.Galleries, gallery => gallery.ImageGalleries.Count, descending),
            "video_count" => Order(fixture.Galleries, gallery => gallery.VideoGalleries.Count, descending),
            "performer_count" => Order(fixture.Galleries, gallery => gallery.GalleryPerformers.Count, descending),
            "tag_count" => Order(fixture.Galleries, gallery => gallery.GalleryTags.Count, descending),
            "typical_resolution" => Order(fixture.Galleries, gallery => TypicalGalleryResolution(gallery), descending),
            _ => throw new InvalidOperationException($"No gallery sort projection configured for '{sortKey}'."),
        };

    private static IReadOnlyList<int> ProjectGroupIds(SortHarnessFixture fixture, string sortKey, bool descending)
        => sortKey switch
        {
            "sort_order" => OrderWithNameAndIdTieBreaker(fixture.Groups, group => group.SortOrder, group => group.Name, descending),
            "name" => Order(fixture.Groups, group => group.Name, descending),
            "date" => Order(fixture.Groups, group => group.Date ?? DateOnly.MinValue, descending),
            "rating" => Order(fixture.Groups, group => fixture.Rating(RatingHostType.Group, group.Id), descending),
            "created_at" => Order(fixture.Groups, group => group.CreatedAt, descending),
            "updated_at" => OrderWithDirectionalIdTieBreaker(fixture.Groups, group => group.UpdatedAt, descending),
            "item_count" => OrderWithDirectionalIdTieBreaker(fixture.Groups, group => group.GroupItems.Count, descending),
            "video_count" => OrderWithDirectionalIdTieBreaker(fixture.Groups, group => group.GroupItems.Where(item => item.VideoId != null).Select(item => item.VideoId).Distinct().Count(), descending),
            "image_count" => OrderWithDirectionalIdTieBreaker(fixture.Groups, group => group.GroupItems.Count(item => item.Kind == GroupItemKind.Image), descending),
            "audio_count" => OrderWithDirectionalIdTieBreaker(fixture.Groups, group => group.GroupItems.Count(item => item.Kind == GroupItemKind.Audio), descending),
            "text_count" => OrderWithDirectionalIdTieBreaker(fixture.Groups, group => group.GroupItems.Count(item => item.Kind == GroupItemKind.Text), descending),
            "gallery_count" => OrderWithDirectionalIdTieBreaker(fixture.Groups, group => group.GroupItems.Count(item => item.Kind == GroupItemKind.Gallery), descending),
            "performer_count" => OrderWithDirectionalIdTieBreaker(fixture.Groups, group => group.GroupItems.Count(item => item.Kind == GroupItemKind.Performer), descending),
            "studio_count" => OrderWithDirectionalIdTieBreaker(fixture.Groups, group => group.GroupItems.Count(item => item.Kind == GroupItemKind.Studio), descending),
            "tag_item_count" => OrderWithDirectionalIdTieBreaker(fixture.Groups, group => group.GroupItems.Count(item => item.Kind == GroupItemKind.Tag), descending),
            "tag_count" => OrderWithDirectionalIdTieBreaker(fixture.Groups, group => group.GroupTags.Count, descending),
            "face_count" => OrderWithDirectionalIdTieBreaker(fixture.Groups, group => group.GroupItems.Count(item => item.Kind == GroupItemKind.Face), descending),
            "segment_count" => OrderWithDirectionalIdTieBreaker(fixture.Groups, group => group.GroupItems.Count(item => item.Kind == GroupItemKind.Segment), descending),
            "subgroup_count" => OrderWithDirectionalIdTieBreaker(fixture.Groups, group => group.SubGroupRelations.Count, descending),
            "containing_group_count" => OrderWithDirectionalIdTieBreaker(fixture.Groups, group => group.ContainingGroupRelations.Count, descending),
            "cached_item_count" => OrderWithDirectionalIdTieBreaker(fixture.Groups, group => group.CachedItemCount ?? 0, descending),
            "last_resolved_at" => OrderWithDirectionalIdTieBreaker(fixture.Groups, group => group.LastResolvedAt, descending),
            "query_source_key" => OrderWithDirectionalIdTieBreaker(fixture.Groups, group => group.QuerySourceKey, descending),
            "show_in_video_lists" => OrderWithDirectionalIdTieBreaker(fixture.Groups, group => group.ShowInVideoLists, descending),
            "aliases" => OrderWithDirectionalIdTieBreaker(fixture.Groups, group => group.Aliases ?? group.Name, descending),
            _ => throw new InvalidOperationException($"No group sort projection configured for '{sortKey}'."),
        };

    private static IReadOnlyList<int> ProjectSegmentIds(SortHarnessFixture fixture, string sortKey, bool descending)
        => sortKey switch
        {
            "random" => OrderSeededRandom(fixture.SegmentRows, row => row.Segment.Id, descending),
            "updated_at" => OrderWithDirectionalIdTieBreaker(fixture.SegmentRows, row => row.Segment.UpdatedAt, descending),
            "created_at" => OrderWithDirectionalIdTieBreaker(fixture.SegmentRows, row => row.Segment.CreatedAt, descending),
            "start_sec" => OrderWithDirectionalIdTieBreaker(fixture.SegmentRows, row => row.Segment.StartSec, descending),
            "end_sec" => OrderWithDirectionalIdTieBreaker(fixture.SegmentRows, row => row.Segment.EndSec ?? row.Segment.StartSec, descending),
            "duration" => OrderWithDirectionalIdTieBreaker(fixture.SegmentRows, row => (row.Segment.EndSec ?? row.Segment.StartSec) - row.Segment.StartSec, descending),
            "confidence" => OrderWithDirectionalIdTieBreaker(fixture.SegmentRows, row => row.Segment.Confidence ?? -1f, descending),
            "title" => OrderWithDirectionalIdTieBreaker(fixture.SegmentRows, row => row.Segment.Title ?? row.Segment.Kind ?? row.TagName ?? string.Empty, descending),
            "video_title" => OrderWithDirectionalIdTieBreaker(fixture.SegmentRows, row => row.VideoTitle ?? string.Empty, descending),
            "kind" => OrderWithDirectionalIdTieBreaker(fixture.SegmentRows, row => row.Segment.Kind ?? string.Empty, descending),
            "source_key" => OrderWithDirectionalIdTieBreaker(fixture.SegmentRows, row => row.Segment.SourceKey, descending),
            "tag_name" => OrderWithDirectionalIdTieBreaker(fixture.SegmentRows, row => row.TagName ?? string.Empty, descending),
            "performer" => OrderWithDirectionalIdTieBreaker(fixture.SegmentRows, row => row.PerformerName ?? string.Empty, descending),
            "ref" => OrderWithDirectionalIdTieBreaker(fixture.SegmentRows, row => row.RefLabel ?? row.PerformerName ?? string.Empty, descending),
            _ => throw new InvalidOperationException($"No segment sort projection configured for '{sortKey}'."),
        };

    private static IReadOnlyList<int> OrderSeededRandom<T>(IEnumerable<T> items, Func<T, int> idSelector, bool descending)
    {
        static long Primary(int id) => (id * 17L + 31L) % 13L;
        static long Secondary(int id) => (id * 101L + 131L) % 97L;
        static long Tertiary(int id) => (id * 1103515245L + 12345L) % 2147483647L;

        return descending
            ? items.OrderByDescending(item => Primary(idSelector(item))).ThenByDescending(item => Secondary(idSelector(item))).ThenByDescending(item => Tertiary(idSelector(item))).ThenByDescending(idSelector).Select(idSelector).ToArray()
            : items.OrderBy(item => Primary(idSelector(item))).ThenBy(item => Secondary(idSelector(item))).ThenBy(item => Tertiary(idSelector(item))).ThenBy(idSelector).Select(idSelector).ToArray();
    }

    private static IReadOnlyList<int> ProjectPerformerIds(SortHarnessFixture fixture, string sortKey, bool descending)
        => sortKey switch
        {
            "name" => Order(fixture.Performers, performer => performer.Name, descending),
            "rating" => Order(fixture.Performers, performer => fixture.Rating(RatingHostType.Performer, performer.Id), descending),
            "video_count" => Order(fixture.Performers, performer => performer.VideoPerformers.Count, descending),
            "image_count" => Order(fixture.Performers, performer => performer.ImagePerformers.Count, descending),
            "gallery_count" => Order(fixture.Performers, performer => performer.GalleryPerformers.Count, descending),
            "latest_video_date" => Order(fixture.Performers, performer => performer.VideoPerformers.Max(link => link.Video!.Date), descending),
            "total_file_size" => Order(fixture.Performers, performer => performer.VideoPerformers.Sum(link => link.Video!.MaxFileSize), descending),
            "tag_count" => Order(fixture.Performers, performer => performer.PerformerTags.Count, descending),
            "career_length" => OrderWithDirectionalIdTieBreaker(fixture.Performers, CareerLength, descending),
            "last_like_at" => OrderWithDirectionalIdTieBreaker(fixture.Performers, performer => fixture.Affinity(AffinityHostType.Performer, performer.Id).FavoritedAt, descending),
            "last_played_at" => OrderWithDirectionalIdTieBreaker(fixture.Performers, performer => performer.VideoPerformers.Max(link => fixture.Affinity(AffinityHostType.Video, link.VideoId).LastConsumedAt), descending),
            "measurements" => OrderMeasurements(fixture.Performers, descending),
            "like_counter" => OrderWithDirectionalIdTieBreaker(fixture.Performers, performer => performer.VideoPerformers.Sum(link => fixture.Affinity(AffinityHostType.Video, link.VideoId).LikeCount), descending),
            "play_count" => OrderWithDirectionalIdTieBreaker(fixture.Performers, performer => performer.VideoPerformers.Sum(link => fixture.Affinity(AffinityHostType.Video, link.VideoId).ViewCount), descending),
            "birthdate" => Order(fixture.Performers, performer => performer.Birthdate, descending),
            "height" => OrderWithDirectionalIdTieBreaker(fixture.Performers, performer => performer.HeightCm ?? 0, descending),
            "weight" => Order(fixture.Performers, performer => performer.Weight, descending),
            "created_at" => Order(fixture.Performers, performer => performer.CreatedAt, descending),
            "updated_at" => Order(fixture.Performers, performer => performer.UpdatedAt, descending),
            _ => throw new InvalidOperationException($"No performer sort projection configured for '{sortKey}'."),
        };

    private static IReadOnlyList<int> ProjectStudioIds(SortHarnessFixture fixture, string sortKey, bool descending)
        => sortKey switch
        {
            "name" => Order(fixture.Studios, studio => studio.Name, descending),
            "rating" => Order(fixture.Studios, studio => fixture.Rating(RatingHostType.Studio, studio.Id), descending),
            "video_count" => Order(fixture.Studios, studio => studio.VideoCount, descending),
            "gallery_count" => Order(fixture.Studios, studio => studio.GalleryCount, descending),
            "image_count" => Order(fixture.Studios, studio => studio.ImageCount, descending),
            "latest_video_date" => Order(fixture.Studios, studio => studio.Videos.Max(video => video.Date), descending),
            "total_file_size" => Order(fixture.Studios, studio => studio.Videos.Sum(video => video.MaxFileSize), descending),
            "parent_count" => OrderWithDirectionalIdTieBreaker(fixture.Studios, studio => studio.ParentId.HasValue ? 1 : 0, descending),
            "child_count" => Order(fixture.Studios, studio => studio.ChildStudioCount, descending),
            "tag_count" => Order(fixture.Studios, studio => studio.TagCount, descending),
            "updated_at" => Order(fixture.Studios, studio => studio.UpdatedAt, descending),
            "created_at" => Order(fixture.Studios, studio => studio.CreatedAt, descending),
            _ => throw new InvalidOperationException($"No studio sort projection configured for '{sortKey}'."),
        };

    private static IReadOnlyList<int> ProjectTagIds(SortHarnessFixture fixture, string sortKey, bool descending)
        => sortKey switch
        {
            "name" => Order(fixture.Tags, tag => tag.Name, descending),
            "rating" => Order(fixture.Tags, tag => fixture.Rating(RatingHostType.Tag, tag.Id), descending),
            "tag_group" => Order(fixture.Tags, tag => tag.TagGroup!.Name, descending),
            "video_count" => Order(fixture.Tags, tag => tag.VideoTags.Select(link => link.VideoId).Distinct().Count(), descending),
            "gallery_count" => Order(fixture.Tags, tag => tag.GalleryTags.Select(link => link.GalleryId).Distinct().Count(), descending),
            "group_count" => Order(fixture.Tags, tag => tag.GroupTags.Select(link => link.GroupId).Distinct().Count(), descending),
            "image_count" => Order(fixture.Tags, tag => tag.ImageTags.Select(link => link.ImageId).Distinct().Count(), descending),
            "performer_count" => Order(fixture.Tags, tag => tag.PerformerTags.Select(link => link.PerformerId).Distinct().Count(), descending),
            "studio_count" => Order(fixture.Tags, tag => tag.StudioTags.Select(link => link.StudioId).Distinct().Count(), descending),
            "latest_video_date" => Order(fixture.Tags, tag => tag.VideoTags.Max(link => link.Video!.Date), descending),
            "total_file_size" => Order(fixture.Tags, tag => tag.VideoTags.Sum(link => link.Video!.MaxFileSize), descending),
            "created_at" => Order(fixture.Tags, tag => tag.CreatedAt, descending),
            "updated_at" => Order(fixture.Tags, tag => tag.UpdatedAt, descending),
            _ => throw new InvalidOperationException($"No tag sort projection configured for '{sortKey}'."),
        };

    private static IReadOnlyList<int> ProjectFaceIds(SortHarnessFixture fixture, string sortKey, bool descending)
    {
        // Merged faces are tombstones hidden from the list (see FacesController's blanket exclusion),
        // so the expected ordering must exclude them too.
        var faces = fixture.Faces.Where(face => face.MergedIntoFaceId == null).ToArray();
        return sortKey switch
        {
            // Composite review ordering: direction-agnostic (ignores the toggle).
            "suggestion_confidence" => faces.OrderByDescending(face => face.UpdatedAt).ThenBy(face => face.Id).Select(face => face.Id).ToArray(),
            "created" => OrderWithDirectionalIdTieBreaker(faces, face => face.CreatedAt, descending),
            "updated" => OrderWithDirectionalIdTieBreaker(faces, face => face.UpdatedAt, descending),
            "label" => OrderWithDirectionalIdTieBreaker(faces, FaceLabel, descending),
            "performer_name" => descending
                ? faces.OrderByDescending(FacePerformerName).ThenByDescending(face => face.Label).ThenByDescending(face => face.Id).Select(face => face.Id).ToArray()
                : faces.OrderBy(FacePerformerName).ThenBy(face => face.Label).ThenBy(face => face.Id).Select(face => face.Id).ToArray(),
            "primary_source_key" => OrderWithDirectionalIdTieBreaker(faces, face => face.PrimarySourceKey ?? string.Empty, descending),
            "detection_count" => OrderWithDirectionalIdTieBreaker(faces, face => face.DetectionCount, descending),
            "appearance_count" => OrderWithDirectionalIdTieBreaker(faces, face => face.AppearanceCount, descending),
            "frame_sample_count" => OrderWithDirectionalIdTieBreaker(faces, face => face.FrameSampleCount, descending),
            "video_count" => OrderWithDirectionalIdTieBreaker(faces, face => face.VideoCount, descending),
            "image_count" => OrderWithDirectionalIdTieBreaker(faces, face => face.ImageCount, descending),
            "cover_present" => OrderWithDirectionalIdTieBreaker(faces, face => !string.IsNullOrEmpty(face.CoverBlobId), descending),
            "random" => OrderSeededRandom(faces, face => face.Id, descending),
            _ => throw new InvalidOperationException($"No face sort projection configured for '{sortKey}'."),
        };
    }

    private static string FaceLabel(Face face)
        => face.Label ?? FacePerformerName(face);

    private static string FacePerformerName(Face face)
        => face.Performer?.Name ?? string.Empty;

    private static IReadOnlyList<int> Order<T, TKey>(IEnumerable<T> items, Func<T, TKey> keySelector, bool descending)
        where T : BaseEntity
        => descending
            ? items.OrderByDescending(keySelector).Select(item => item.Id).ToArray()
            : items.OrderBy(keySelector).Select(item => item.Id).ToArray();

    private static IReadOnlyList<int> OrderWithDirectionalIdTieBreaker<T, TKey>(IEnumerable<T> items, Func<T, TKey> keySelector, bool descending)
        where T : BaseEntity
        => descending
            ? items.OrderByDescending(keySelector).ThenByDescending(item => item.Id).Select(item => item.Id).ToArray()
            : items.OrderBy(keySelector).ThenBy(item => item.Id).Select(item => item.Id).ToArray();

    private static IReadOnlyList<int> OrderWithDirectionalIdTieBreaker<TKey>(IEnumerable<SegmentHarnessRow> items, Func<SegmentHarnessRow, TKey> keySelector, bool descending)
        => descending
            ? items.OrderByDescending(keySelector).ThenByDescending(item => item.Segment.Id).Select(item => item.Segment.Id).ToArray()
            : items.OrderBy(keySelector).ThenBy(item => item.Segment.Id).Select(item => item.Segment.Id).ToArray();

    private static IReadOnlyList<int> OrderWithNameAndIdTieBreaker<TKey>(IEnumerable<Group> items, Func<Group, TKey> keySelector, Func<Group, string> nameSelector, bool descending)
        => descending
            ? items.OrderByDescending(keySelector).ThenByDescending(nameSelector).ThenByDescending(item => item.Id).Select(item => item.Id).ToArray()
            : items.OrderBy(keySelector).ThenBy(nameSelector).ThenBy(item => item.Id).Select(item => item.Id).ToArray();

    private static int TypicalGalleryResolution(Gallery gallery)
        => gallery.ImageGalleries
            .SelectMany(imageGallery => imageGallery.Image!.Files.Select(file => ResolutionBucket(Math.Max(file.Width, file.Height))))
            .Where(bucket => bucket > 0)
            .GroupBy(bucket => bucket)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Key)
            .Select(group => group.Key)
            .FirstOrDefault();

    private static int ResolutionBucket(int resolution)
        => resolution >= 9840 ? 9999 :
            resolution >= 7424 ? 4320 :
            resolution >= 6656 ? 4032 :
            resolution >= 5632 ? 3384 :
            resolution >= 4480 ? 2880 :
            resolution >= 3200 ? 2160 :
            resolution >= 2240 ? 1440 :
            resolution >= 1600 ? 1080 :
            resolution >= 1120 ? 720 :
            resolution >= 907 ? 540 :
            resolution >= 747 ? 480 :
            resolution >= 533 ? 360 :
            resolution >= 341 ? 240 :
            resolution >= 144 ? 144 : 0;

    private static IReadOnlyList<int> OrderMeasurements(IEnumerable<Performer> performers, bool descending)
    {
        var query = performers.Select(performer =>
        {
            var normalized = (performer.Measurements ?? string.Empty).Trim().ToUpperInvariant();
            var parts = normalized.Split('-', 3);
            var bust = parts.ElementAtOrDefault(0) ?? string.Empty;
            var waist = parts.ElementAtOrDefault(1) ?? string.Empty;
            var hips = parts.ElementAtOrDefault(2) ?? string.Empty;
            return new { Performer = performer, HasMeasurements = !string.IsNullOrEmpty(normalized), Bust = bust, Waist = waist, Hips = hips };
        });

        return descending
            ? query.OrderBy(item => item.HasMeasurements ? 0 : 1)
                .ThenByDescending(item => item.Bust.Length)
                .ThenByDescending(item => item.Bust)
                .ThenByDescending(item => item.Waist.Length)
                .ThenByDescending(item => item.Waist)
                .ThenByDescending(item => item.Hips.Length)
                .ThenByDescending(item => item.Hips)
                .ThenByDescending(item => item.Performer.Id)
                .Select(item => item.Performer.Id)
                .ToArray()
            : query.OrderBy(item => item.HasMeasurements ? 0 : 1)
                .ThenBy(item => item.Bust.Length)
                .ThenBy(item => item.Bust)
                .ThenBy(item => item.Waist.Length)
                .ThenBy(item => item.Waist)
                .ThenBy(item => item.Hips.Length)
                .ThenBy(item => item.Hips)
                .ThenBy(item => item.Performer.Id)
                .Select(item => item.Performer.Id)
                .ToArray();
    }

    private static string NormalizeControllerSortKey(string sortKey)
        => sortKey.Replace("_", string.Empty, StringComparison.OrdinalIgnoreCase).ToLowerInvariant();

    private static string? VideoPhash(Video video, bool descending)
    {
        var values = video.Files
            .SelectMany(file => file.Fingerprints)
            .Where(fingerprint => fingerprint.Type == "phash" && fingerprint.Value != string.Empty)
            .Select(fingerprint => fingerprint.Value);

        return descending ? values.OrderByDescending(value => value).FirstOrDefault() : values.OrderBy(value => value).FirstOrDefault();
    }

    private static int? PerformerAge(Video video, bool descending)
    {
        var ages = video.VideoPerformers
            .Where(link => video.Date != null && link.Performer!.Birthdate != null)
            .Select(link => AgeOnDate(link.Performer!.Birthdate!.Value, video.Date!.Value))
            .ToArray();

        if (ages.Length == 0)
            return null;

        return descending ? ages.Max() : ages.Min();
    }

    private static int AgeOnDate(DateOnly birthdate, DateOnly date)
    {
        var years = date.Year - birthdate.Year;
        if (date.Month < birthdate.Month || (date.Month == birthdate.Month && date.Day < birthdate.Day))
            years--;
        return years;
    }

    private static int CareerLength(Performer performer)
    {
        var end = performer.CareerEnd ?? DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var start = performer.CareerStart ?? end;
        return AgeOnDate(start, end);
    }

    public sealed record FilterProbe(string RowId, Func<SortHarnessFixture, Task<IReadOnlyList<int>>> QueryIds, Func<SortHarnessFixture, IReadOnlyList<int>> ExpectedIds)
    {
        public override string ToString() => RowId;
    }

    public sealed class SortHarnessFixture : IAsyncDisposable
    {
        private readonly CurrentPrincipalAccessor _principalAccessor;
        private readonly CovePrincipal _principal;

        private SortHarnessFixture(CoveContext context, CurrentPrincipalAccessor principalAccessor, CovePrincipal principal)
        {
            Context = context;
            _principalAccessor = principalAccessor;
            _principal = principal;
        }

        public CoveContext Context { get; }
        public IReadOnlyList<Video> Videos { get; private set; } = [];
        public IReadOnlyList<Image> Images { get; private set; } = [];
        public IReadOnlyList<Audio> Audios { get; private set; } = [];
        public IReadOnlyList<TextDocument> Texts { get; private set; } = [];
        public IReadOnlyList<Gallery> Galleries { get; private set; } = [];
        public IReadOnlyList<Group> Groups { get; private set; } = [];
        public IReadOnlyList<SegmentHarnessRow> SegmentRows { get; private set; } = [];
        public IReadOnlyList<Performer> Performers { get; private set; } = [];
        public IReadOnlyList<Studio> Studios { get; private set; } = [];
        public IReadOnlyList<Tag> Tags { get; private set; } = [];
        public IReadOnlyList<Face> Faces { get; private set; } = [];
        private Dictionary<(RatingHostType HostType, int HostId), int> Ratings { get; } = [];
        private Dictionary<(AffinityHostType HostType, int HostId), UserEntityAffinity> Affinities { get; } = [];

        public static async Task<SortHarnessFixture> CreateAsync()
        {
            var options = new DbContextOptionsBuilder<CoveContext>()
                .UseInMemoryDatabase($"entity-list-sort-harness-{Guid.NewGuid():N}")
                .Options;

            var principalAccessor = new CurrentPrincipalAccessor();
            var principal = new CovePrincipal
            {
                UserId = TestUserId,
                Username = "sort-harness-user",
                Kind = PrincipalKind.User,
                Permissions = new HashSet<string> { "*" },
                Roles = new HashSet<string>(),
            };
            principalAccessor.Set(principal);

            var fixture = new SortHarnessFixture(new HarnessCoveContext(options, principalAccessor), principalAccessor, principal);
            await fixture.SeedAsync();
            return fixture;
        }

        public void ActivatePrincipal() => _principalAccessor.Set(_principal);

        public int Rating(RatingHostType hostType, int hostId) => Ratings[(hostType, hostId)];

        public UserEntityAffinity Affinity(AffinityHostType hostType, int hostId) => Affinities[(hostType, hostId)];

        public DateTime Now { get; } = new(2024, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        public DateTime? LastLike(InteractionHostType hostType, int hostId)
            => Context.Interactions.Local
                .Where(item => item.UserId == TestUserId && item.HostType == hostType && item.HostId == hostId && item.Kind == InteractionKind.LikeCount)
                .Select(item => (DateTime?)item.At)
                .Max();

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
        }

        private async Task SeedAsync()
        {
            var now = Now;

            var folderA = new Folder { Id = 1, Path = "Z:/cove/a", ModTime = now.AddDays(-7) };
            var folderB = new Folder { Id = 2, Path = "Z:/cove/b", ModTime = now.AddDays(-6) };
            var folderC = new Folder { Id = 3, Path = "Z:/cove/c", ModTime = now.AddDays(-5) };

            var tagGroups = new[]
            {
                new TagGroup { Id = 11, Name = "Activity", CreatedAt = now.AddDays(-30), UpdatedAt = now.AddDays(-3) },
                new TagGroup { Id = 12, Name = "Mood", CreatedAt = now.AddDays(-29), UpdatedAt = now.AddDays(-2) },
                new TagGroup { Id = 13, Name = "Wardrobe", CreatedAt = now.AddDays(-28), UpdatedAt = now.AddDays(-1) },
            };

            var studios = new[]
            {
                new Studio { Id = 101, Name = "Aster Studio", CreatedAt = now.AddDays(-13), UpdatedAt = now.AddHours(-7), VideoCount = 1, GalleryCount = 2, ImageCount = 3, ChildStudioCount = 1, TagCount = 4, Organized = true },
                new Studio { Id = 102, Name = "Beryl Studio", CreatedAt = now.AddDays(-12), UpdatedAt = now.AddHours(-5), VideoCount = 2, GalleryCount = 3, ImageCount = 1, ChildStudioCount = 2, TagCount = 2, Organized = false },
                new Studio { Id = 103, Name = "Citrine Studio", CreatedAt = now.AddDays(-11), UpdatedAt = now.AddHours(-3), VideoCount = 3, GalleryCount = 1, ImageCount = 2, ChildStudioCount = 3, TagCount = 1, Organized = true },
            };

            var tags = new[]
            {
                new Tag { Id = 201, Name = "Arc", SortName = "Arc", TagGroup = tagGroups[0], CreatedAt = now.AddDays(-23), UpdatedAt = now.AddHours(-9), VideoCount = 1, GalleryCount = 3, GroupCount = 2, ImageCount = 1, PerformerCount = 2, StudioCount = 3 },
                new Tag { Id = 202, Name = "Bloom", SortName = "Bloom", TagGroup = tagGroups[1], Favorite = true, CreatedAt = now.AddDays(-22), UpdatedAt = now.AddHours(-8), VideoCount = 2, GalleryCount = 1, GroupCount = 3, ImageCount = 2, PerformerCount = 3, StudioCount = 1 },
                new Tag { Id = 203, Name = "Cinder", SortName = "Cinder", TagGroup = tagGroups[2], CreatedAt = now.AddDays(-21), UpdatedAt = now.AddHours(-6), VideoCount = 3, GalleryCount = 2, GroupCount = 1, ImageCount = 3, PerformerCount = 1, StudioCount = 2 },
            };

            var performers = new[]
            {
                new Performer { Id = 301, Name = "Ava", Birthdate = new DateOnly(1990, 2, 3), CareerStart = new DateOnly(2012, 1, 1), CareerEnd = new DateOnly(2016, 1, 1), HeightCm = 160, Weight = 52, Measurements = "30-20-30", VideoCount = 1, ImageCount = 3, GalleryCount = 1, TagCount = 2, CreatedAt = now.AddDays(-33), UpdatedAt = now.AddHours(-12) },
                new Performer { Id = 302, Name = "Bianca", Birthdate = new DateOnly(1988, 6, 4), CareerStart = new DateOnly(2010, 1, 1), CareerEnd = new DateOnly(2018, 1, 1), HeightCm = 170, Weight = 58, Measurements = "34-24-34", VideoCount = 2, ImageCount = 1, GalleryCount = 3, TagCount = 3, CreatedAt = now.AddDays(-32), UpdatedAt = now.AddHours(-10) },
                new Performer { Id = 303, Name = "Cora", Birthdate = new DateOnly(1995, 10, 5), CareerStart = new DateOnly(2016, 1, 1), CareerEnd = new DateOnly(2026, 1, 1), HeightCm = 180, Weight = 64, Measurements = "40-30-40", VideoCount = 3, ImageCount = 2, GalleryCount = 2, TagCount = 1, CreatedAt = now.AddDays(-31), UpdatedAt = now.AddHours(-8) },
            };

            var videos = new[]
            {
                new Video
                {
                    Id = 401,
                    Title = "Alpha Video",
                    Code = "A-001",
                    Date = new DateOnly(2021, 1, 5),
                    Studio = studios[0],
                    Organized = false,
                    CreatedAt = now.AddDays(-43),
                    UpdatedAt = now.AddHours(-15),
                    FileCount = 1,
                    MaxDuration = 11,
                    MaxFileSize = 1_100,
                    MaxHeight = 720,
                    MaxResolution = 720,
                    MaxFrameRate = 24,
                    MaxBitRate = 1_100_000,
                    MaxFileModTime = now.AddDays(-4),
                    MinPath = "Z:/cove/a/alpha.mp4",
                    MaxPath = "Z:/cove/a/alpha.mp4",
                    Files =
                    [
                        new VideoFile { Id = 1201, Basename = "alpha.mp4", ParentFolder = folderA, ParentFolderId = folderA.Id, Path = "Z:/cove/a/alpha.mp4", Size = 1_100, ModTime = now.AddDays(-4), Height = 720, Duration = 11, FrameRate = 24, BitRate = 1_100_000, Fingerprints = [new FileFingerprint { Id = 1301, Type = "phash", Value = "11aa" }] },
                    ],
                },
                new Video
                {
                    Id = 402,
                    Title = "Beta Video",
                    Code = "B-002",
                    Date = new DateOnly(2022, 2, 6),
                    Studio = studios[1],
                    Organized = true,
                    CreatedAt = now.AddDays(-42),
                    UpdatedAt = now.AddHours(-13),
                    FileCount = 2,
                    MaxDuration = 22,
                    MaxFileSize = 2_200,
                    MaxHeight = 1080,
                    MaxResolution = 1080,
                    MaxFrameRate = 30,
                    MaxBitRate = 2_200_000,
                    MaxFileModTime = now.AddDays(-3),
                    MinPath = "Z:/cove/b/beta-a.mp4",
                    MaxPath = "Z:/cove/b/beta-b.mp4",
                    Files =
                    [
                        new VideoFile { Id = 1202, Basename = "beta-a.mp4", ParentFolder = folderB, ParentFolderId = folderB.Id, Path = "Z:/cove/b/beta-a.mp4", Size = 1_000, ModTime = now.AddDays(-3), Height = 1080, Duration = 22, FrameRate = 30, BitRate = 2_200_000, Fingerprints = [new FileFingerprint { Id = 1302, Type = "phash", Value = "22bb" }] },
                        new VideoFile { Id = 1204, Basename = "beta-b.mp4", ParentFolder = folderB, ParentFolderId = folderB.Id, Path = "Z:/cove/b/beta-b.mp4", Size = 2_200, ModTime = now.AddDays(-3), Height = 1080, Duration = 20, FrameRate = 30, BitRate = 2_100_000 },
                    ],
                },
                new Video
                {
                    Id = 403,
                    Title = "Gamma Video",
                    Code = "C-003",
                    Date = new DateOnly(2023, 3, 7),
                    Studio = studios[2],
                    Organized = true,
                    CreatedAt = now.AddDays(-41),
                    UpdatedAt = now.AddHours(-11),
                    FileCount = 3,
                    MaxDuration = 33,
                    MaxFileSize = 5_500,
                    MaxHeight = 2160,
                    MaxResolution = 2160,
                    MaxFrameRate = 60,
                    MaxBitRate = 3_300_000,
                    MaxFileModTime = now.AddDays(-2),
                    MinPath = "Z:/cove/c/gamma-a.mp4",
                    MaxPath = "Z:/cove/c/gamma-c.mp4",
                    Files =
                    [
                        new VideoFile { Id = 1203, Basename = "gamma.mp4", ParentFolder = folderC, ParentFolderId = folderC.Id, Path = "Z:/cove/c/gamma.mp4", Size = 5_500, ModTime = now.AddDays(-2), Height = 2160, Duration = 33, FrameRate = 60, BitRate = 3_300_000, Fingerprints = [new FileFingerprint { Id = 1303, Type = "phash", Value = "33cc" }] },
                        new VideoFile { Id = 1205, Basename = "gamma-b.mp4", ParentFolder = folderC, ParentFolderId = folderC.Id, Path = "Z:/cove/c/gamma-b.mp4", Size = 4_500, ModTime = now.AddDays(-2), Height = 1440, Duration = 30, FrameRate = 48, BitRate = 2_900_000 },
                        new VideoFile { Id = 1206, Basename = "gamma-c.mp4", ParentFolder = folderC, ParentFolderId = folderC.Id, Path = "Z:/cove/c/gamma-c.mp4", Size = 3_500, ModTime = now.AddDays(-2), Height = 1080, Duration = 28, FrameRate = 24, BitRate = 2_500_000 },
                    ],
                },
            };

            LinkVideo(videos[0], tags[0], performers[0]);
            LinkVideo(videos[1], tags[1], performers[0], performers[1]);
            LinkVideo(videos[2], tags[2], performers[0], performers[1], performers[2]);
            LinkVideoTag(videos[0], tags[1]);
            LinkVideoTag(videos[0], tags[2]);
            LinkVideoTag(videos[1], tags[2]);

            var images = new[]
            {
                new Image { Id = 501, Title = "Alpha Image", Date = new DateOnly(2020, 1, 2), CreatedAt = now.AddDays(-53), UpdatedAt = now.AddHours(-22), MaxFileModTime = now.AddDays(-9), MaxFileSize = 900, MaxResolution = 600, MinPath = "Z:/cove/a/alpha.jpg", MaxPath = "Z:/cove/a/alpha.jpg", PerformerCount = 1, TagCount = 3, Files = [new ImageFile { Id = 1501, Basename = "alpha.jpg", ParentFolder = folderA, ParentFolderId = folderA.Id, Path = "Z:/cove/a/alpha.jpg", Size = 900, ModTime = now.AddDays(-9), Width = 600, Height = 600 }] },
                new Image { Id = 502, Title = "Beta Image", Date = new DateOnly(2020, 2, 3), CreatedAt = now.AddDays(-52), UpdatedAt = now.AddHours(-20), MaxFileModTime = now.AddDays(-8), MaxFileSize = 1_900, MaxResolution = 1_200, MinPath = "Z:/cove/b/beta.jpg", MaxPath = "Z:/cove/b/beta.jpg", PerformerCount = 3, TagCount = 1, Files = [new ImageFile { Id = 1502, Basename = "beta.jpg", ParentFolder = folderB, ParentFolderId = folderB.Id, Path = "Z:/cove/b/beta.jpg", Size = 1_900, ModTime = now.AddDays(-8), Width = 1200, Height = 800 }] },
                new Image { Id = 503, Title = "Gamma Image", Date = new DateOnly(2020, 3, 4), CreatedAt = now.AddDays(-51), UpdatedAt = now.AddHours(-18), MaxFileModTime = now.AddDays(-7), MaxFileSize = 2_900, MaxResolution = 1_800, MinPath = "Z:/cove/c/gamma.jpg", MaxPath = "Z:/cove/c/gamma.jpg", PerformerCount = 2, TagCount = 2, Files = [new ImageFile { Id = 1503, Basename = "gamma.jpg", ParentFolder = folderC, ParentFolderId = folderC.Id, Path = "Z:/cove/c/gamma.jpg", Size = 2_900, ModTime = now.AddDays(-7), Width = 1800, Height = 1200 }] },
            };

            LinkImage(images[0], performers[0]);
            LinkImage(images[1], performers[0], performers[1]);
            LinkImage(images[2], performers[0], performers[1], performers[2]);
            LinkImageTag(images[0], tags[0]);
            LinkImageTag(images[1], tags[0], tags[1]);
            LinkImageTag(images[2], tags[0], tags[1], tags[2]);

            var galleries = new[]
            {
                new Gallery { Id = 601, Title = "Alpha Gallery", Code = "GA-001", Photographer = "Zoe Lens", Organized = false, Studio = studios[2], Date = new DateOnly(2019, 1, 2), Folder = folderA, CreatedAt = now.AddDays(-63), UpdatedAt = now.AddHours(-32), ImageCount = 4, PerformerCount = 1, TagCount = 3, Files = [new GalleryFile { Id = 1601, Basename = "alpha.zip", ParentFolder = folderA, ParentFolderId = folderA.Id, Path = "Z:/cove/a/alpha.zip", Size = 600, ModTime = now.AddDays(-12) }] },
                new Gallery { Id = 602, Title = "Beta Gallery", Code = "GB-002", Photographer = "Uma Frame", Organized = true, Studio = studios[0], Date = new DateOnly(2019, 2, 3), Folder = folderB, CreatedAt = now.AddDays(-62), UpdatedAt = now.AddHours(-30), ImageCount = 8, PerformerCount = 3, TagCount = 1, Files = [new GalleryFile { Id = 1602, Basename = "beta.zip", ParentFolder = folderB, ParentFolderId = folderB.Id, Path = "Z:/cove/b/beta.zip", Size = 700, ModTime = now.AddDays(-11) }, new GalleryFile { Id = 1604, Basename = "beta-extra.zip", ParentFolder = folderB, ParentFolderId = folderB.Id, Path = "Z:/cove/b/beta-extra.zip", Size = 710, ModTime = now.AddDays(-20) }] },
                new Gallery { Id = 603, Title = "Gamma Gallery", Code = "GC-003", Photographer = "Vic Shutter", Organized = true, Studio = studios[1], Date = new DateOnly(2019, 3, 4), Folder = folderC, CreatedAt = now.AddDays(-61), UpdatedAt = now.AddHours(-28), ImageCount = 6, PerformerCount = 2, TagCount = 2, Files = [new GalleryFile { Id = 1603, Basename = "gamma.zip", ParentFolder = folderC, ParentFolderId = folderC.Id, Path = "Z:/cove/c/gamma.zip", Size = 800, ModTime = now.AddDays(-10) }, new GalleryFile { Id = 1605, Basename = "gamma-b.zip", ParentFolder = folderC, ParentFolderId = folderC.Id, Path = "Z:/cove/c/gamma-b.zip", Size = 810, ModTime = now.AddDays(-21) }, new GalleryFile { Id = 1606, Basename = "gamma-c.zip", ParentFolder = folderC, ParentFolderId = folderC.Id, Path = "Z:/cove/c/gamma-c.zip", Size = 820, ModTime = now.AddDays(-22) }] },
            };

            var groups = new[]
            {
                new Group { Id = 701, Name = "Alpha Group", Aliases = "A One", SortOrder = 3, Date = new DateOnly(2018, 1, 2), CreatedAt = now.AddDays(-73), UpdatedAt = now.AddHours(-42), AllowedHostTypes = ["video", "image", "audio"] },
                new Group { Id = 702, Name = "Beta Group", Aliases = "B Two", QuerySourceKey = "manual", QueryJson = "{}", LastResolvedAt = now.AddDays(-3), CachedItemCount = 6, ShowInVideoLists = true, SortOrder = 1, Date = new DateOnly(2018, 2, 3), CreatedAt = now.AddDays(-72), UpdatedAt = now.AddHours(-40), AllowedHostTypes = ["video", "image", "audio", "text", "gallery"] },
                new Group { Id = 703, Name = "Gamma Group", Aliases = "C Three", Kind = GroupKind.Dynamic, QuerySourceKey = "filter", QueryJson = "{\"entityType\":\"video\"}", LastResolvedAt = now.AddDays(-1), CachedItemCount = 16, SortOrder = 2, Date = new DateOnly(2018, 3, 4), CreatedAt = now.AddDays(-71), UpdatedAt = now.AddHours(-38) },
            };

            LinkGalleryImage(galleries[0], images[0]);
            LinkGalleryImage(galleries[1], images[0], images[1]);
            LinkGalleryImage(galleries[2], images[0], images[1], images[2]);
            LinkGalleryVideo(galleries[0], videos[0]);
            LinkGalleryVideo(galleries[1], videos[0], videos[1]);
            LinkGalleryVideo(galleries[2], videos[0], videos[1], videos[2]);
            LinkGalleryPerformer(galleries[0], performers[0]);
            LinkGalleryPerformer(galleries[1], performers[0], performers[1]);
            LinkGalleryPerformer(galleries[2], performers[0], performers[1], performers[2]);
            LinkGalleryTag(galleries[0], tags[0]);
            LinkGalleryTag(galleries[1], tags[0], tags[1]);
            LinkGalleryTag(galleries[2], tags[0], tags[1], tags[2]);
            LinkGroupTag(groups[0], tags[0]);
            LinkGroupTag(groups[1], tags[0], tags[1]);
            LinkGroupTag(groups[2], tags[0], tags[1], tags[2]);
            LinkPerformerTag(performers[0], tags[0]);
            LinkPerformerTag(performers[1], tags[0], tags[1]);
            LinkPerformerTag(performers[2], tags[0], tags[1], tags[2]);
            LinkStudioTag(studios[0], tags[0]);
            LinkStudioTag(studios[1], tags[0], tags[1]);
            LinkStudioTag(studios[2], tags[0], tags[1], tags[2]);

            var audios = new[]
            {
                new Audio { Id = 801, Title = "Alpha Audio", Date = new DateOnly(2017, 1, 2), MaxDuration = 60, MaxBitRate = 96_000, MaxFileSize = 1_000, MaxFileModTime = now.AddDays(-15), MinPath = "Z:/cove/a/alpha.mp3", MaxPath = "Z:/cove/a/alpha.mp3", CreatedAt = now.AddDays(-83), UpdatedAt = now.AddHours(-52), FileCount = 1, HasVideoFiles = false, Files = [new AudioFile { Id = 1801, Basename = "alpha.mp3", ParentFolder = folderA, ParentFolderId = folderA.Id, Path = "Z:/cove/a/alpha.mp3", Format = "mp3", AudioCodec = "mp3", BitRate = 96_000, Size = 1_000, ModTime = now.AddDays(-15), Duration = 60, SampleRate = 44100, Channels = 2 }], Tracks = [new AudioTrack { Id = 1851, OrderIndex = 1, Title = "Alpha Track", StartSec = 0, EndSec = 60 }] },
                new Audio { Id = 802, Title = "Beta Audio", Date = new DateOnly(2017, 2, 3), MaxDuration = 120, MaxBitRate = 192_000, MaxFileSize = 2_000, MaxFileModTime = now.AddDays(-14), MinPath = "Z:/cove/b/beta.m4a", MaxPath = "Z:/cove/b/beta.m4a", ImageBlobId = "audio-beta-cover", CreatedAt = now.AddDays(-82), UpdatedAt = now.AddHours(-50), FileCount = 2, HasVideoFiles = true, Files = [new AudioFile { Id = 1802, Basename = "beta.m4a", ParentFolder = folderB, ParentFolderId = folderB.Id, Path = "Z:/cove/b/beta.m4a", Format = "m4a", AudioCodec = "aac", BitRate = 192_000, Size = 2_000, ModTime = now.AddDays(-14), Duration = 120, SampleRate = 48000, Channels = 2, HasVideoTrack = true }], Tracks = [new AudioTrack { Id = 1852, OrderIndex = 1, Title = "Beta Intro", StartSec = 0, EndSec = 40 }, new AudioTrack { Id = 1853, OrderIndex = 2, Title = "Beta Main", StartSec = 40, EndSec = 120 }] },
                new Audio { Id = 803, Title = "Gamma Audio", Date = new DateOnly(2017, 3, 4), MaxDuration = 180, MaxBitRate = 320_000, MaxFileSize = 3_000, MaxFileModTime = now.AddDays(-13), MinPath = "Z:/cove/c/gamma.flac", MaxPath = "Z:/cove/c/gamma.flac", ImageBlobId = "audio-gamma-cover", CreatedAt = now.AddDays(-81), UpdatedAt = now.AddHours(-48), FileCount = 3, HasVideoFiles = true, Files = [new AudioFile { Id = 1803, Basename = "gamma.flac", ParentFolder = folderC, ParentFolderId = folderC.Id, Path = "Z:/cove/c/gamma.flac", Format = "flac", AudioCodec = "flac", BitRate = 320_000, Size = 3_000, ModTime = now.AddDays(-13), Duration = 180, SampleRate = 96000, Channels = 6, HasVideoTrack = true }], Tracks = [new AudioTrack { Id = 1854, OrderIndex = 1, Title = "Gamma Intro", StartSec = 0, EndSec = 30 }, new AudioTrack { Id = 1855, OrderIndex = 2, Title = "Gamma Middle", StartSec = 30, EndSec = 100 }, new AudioTrack { Id = 1856, OrderIndex = 3, Title = "Gamma Finale", StartSec = 100, EndSec = 180 }] },
            };

            LinkAudioTag(audios[0], tags[0]);
            LinkAudioTag(audios[1], tags[0], tags[1]);
            LinkAudioTag(audios[2], tags[0], tags[1], tags[2]);
            LinkAudioPerformer(audios[0], performers[0]);
            LinkAudioPerformer(audios[1], performers[0], performers[1]);
            LinkAudioPerformer(audios[2], performers[0], performers[1], performers[2]);

            var texts = new[]
            {
                new TextDocument { Id = 901, Title = "Alpha Text", Date = new DateOnly(2016, 1, 2), MaxWordCount = 100, MaxPageCount = 10, MaxFileSize = 1_000, MaxFileModTime = now.AddDays(-12), MinPath = "Z:/cove/a/alpha.txt", MaxPath = "Z:/cove/a/alpha.txt", SearchText = "alpha text content", CreatedAt = now.AddDays(-93), UpdatedAt = now.AddHours(-62), FileCount = 1, Files = [new TextFile { Id = 1901, Basename = "alpha.txt", ParentFolder = folderA, ParentFolderId = folderA.Id, Path = "Z:/cove/a/alpha.txt", Format = "txt", WordCount = 100, PageCount = 10, Size = 1_000, ModTime = now.AddDays(-12), ExcerptText = "alpha text content" }] },
                new TextDocument { Id = 902, Title = "Beta Text", Date = new DateOnly(2016, 2, 3), MaxWordCount = 200, MaxPageCount = 20, MaxFileSize = 2_000, MaxFileModTime = now.AddDays(-11), MinPath = "Z:/cove/b/beta.pdf", MaxPath = "Z:/cove/b/beta.pdf", SearchText = "beta text content", ImageBlobId = "text-beta-cover", CreatedAt = now.AddDays(-92), UpdatedAt = now.AddHours(-60), FileCount = 2, Files = [new TextFile { Id = 1902, Basename = "beta.pdf", ParentFolder = folderB, ParentFolderId = folderB.Id, Path = "Z:/cove/b/beta.pdf", Format = "pdf", WordCount = 200, PageCount = 20, Size = 2_000, ModTime = now.AddDays(-11), ExcerptText = "beta text content" }] },
                new TextDocument { Id = 903, Title = "Gamma Text", Date = new DateOnly(2016, 3, 4), MaxWordCount = 300, MaxPageCount = 30, MaxFileSize = 3_000, MaxFileModTime = now.AddDays(-10), MinPath = "Z:/cove/c/gamma.epub", MaxPath = "Z:/cove/c/gamma.epub", SearchText = "gamma text content", ImageBlobId = "text-gamma-cover", CreatedAt = now.AddDays(-91), UpdatedAt = now.AddHours(-58), FileCount = 3, Files = [new TextFile { Id = 1903, Basename = "gamma.epub", ParentFolder = folderC, ParentFolderId = folderC.Id, Path = "Z:/cove/c/gamma.epub", Format = "epub", WordCount = 300, PageCount = 30, Size = 3_000, ModTime = now.AddDays(-10), ExcerptText = "gamma text content" }] },
            };

            LinkTextTag(texts[0], tags[0]);
            LinkTextTag(texts[1], tags[0], tags[1]);
            LinkTextTag(texts[2], tags[0], tags[1], tags[2]);
            LinkTextPerformer(texts[0], performers[0]);
            LinkTextPerformer(texts[1], performers[0], performers[1]);
            LinkTextPerformer(texts[2], performers[0], performers[1], performers[2]);

            var segments = new[]
            {
                new Segment { Id = 1001, HostType = SegmentHostType.Video, HostId = videos[0].Id, StartSec = 1, EndSec = 4, Tag = tags[0], Kind = "action", RefId = 1101, SourceKey = "alpha-source", SourceRunId = "run-alpha", ColorHint = "red", Confidence = 0.2f, Title = "Alpha Segment", CreatedAt = now.AddDays(-103), UpdatedAt = now.AddHours(-72) },
                new Segment { Id = 1002, HostType = SegmentHostType.Video, HostId = videos[1].Id, StartSec = 5, EndSec = 12, Tag = tags[1], Kind = "beat", RefId = 1102, SourceKey = "beta-source", SourceRunId = "run-beta", ColorHint = "green", Confidence = 0.6f, Title = "Beta Segment", ImageBlobId = "segment-beta-image", CreatedAt = now.AddDays(-102), UpdatedAt = now.AddHours(-70) },
                new Segment { Id = 1003, HostType = SegmentHostType.Video, HostId = videos[2].Id, StartSec = 9, EndSec = 20, Tag = tags[2], Kind = "performer", RefId = performers[2].Id, SourceKey = "gamma-source", SourceRunId = "run-gamma", ColorHint = "blue", Confidence = 0.9f, Title = "Gamma Segment", Payload = JsonDocument.Parse("{\"source\":\"harness\"}"), CreatedAt = now.AddDays(-101), UpdatedAt = now.AddHours(-68) },
            };

            var faces = new[]
            {
                new Face { Id = 1101, Label = "Alpha Face", Performer = performers[0], AppearanceCount = 2, FrameSampleCount = 20, VideoCount = 1, ImageCount = 0, DetectionCount = 2, PrimarySourceKey = "source-c", CreatedAt = now.AddDays(-113), UpdatedAt = now.AddHours(-82) },
                new Face { Id = 1102, Label = "Beta Face", Performer = performers[1], CoverBlobId = "face-beta-cover", MergedIntoFaceId = 1101, AppearanceCount = 3, FrameSampleCount = 30, VideoCount = 1, ImageCount = 1, DetectionCount = 4, PrimarySourceKey = "source-b", CreatedAt = now.AddDays(-112), UpdatedAt = now.AddHours(-80) },
                new Face { Id = 1103, Label = "Gamma Face", Performer = performers[2], CoverBlobId = "face-gamma-cover", Ignored = true, AppearanceCount = 4, FrameSampleCount = 40, VideoCount = 2, ImageCount = 2, DetectionCount = 6, PrimarySourceKey = "source-a", CreatedAt = now.AddDays(-111), UpdatedAt = now.AddHours(-78) },
            };

            LinkGroupRelation(groups[0], groups[1]);
            LinkGroupRelation(groups[2], groups[0]);
            LinkGroupRelation(groups[2], groups[1]);
            LinkGroupItem(groups[0], 1701, GroupItemKind.Video, "video", videos[0].Id, videoId: videos[0].Id);
            LinkGroupItem(groups[0], 1702, GroupItemKind.Image, "image", images[0].Id);
            LinkGroupItem(groups[0], 1703, GroupItemKind.Audio, "audio", audios[0].Id);
            LinkGroupItem(groups[1], 1704, GroupItemKind.Video, "video", videos[0].Id, videoId: videos[0].Id);
            LinkGroupItem(groups[1], 1705, GroupItemKind.Video, "video", videos[1].Id, videoId: videos[1].Id);
            LinkGroupItem(groups[1], 1706, GroupItemKind.Image, "image", images[0].Id);
            LinkGroupItem(groups[1], 1707, GroupItemKind.Image, "image", images[1].Id);
            LinkGroupItem(groups[1], 1708, GroupItemKind.Audio, "audio", audios[0].Id);
            LinkGroupItem(groups[1], 1709, GroupItemKind.Audio, "audio", audios[1].Id);
            LinkGroupItem(groups[1], 1710, GroupItemKind.Text, "text", texts[0].Id);
            LinkGroupItem(groups[1], 1711, GroupItemKind.Gallery, "gallery", galleries[0].Id);
            LinkGroupItem(groups[2], 1712, GroupItemKind.Video, "video", videos[0].Id, videoId: videos[0].Id);
            LinkGroupItem(groups[2], 1713, GroupItemKind.Video, "video", videos[1].Id, videoId: videos[1].Id);
            LinkGroupItem(groups[2], 1714, GroupItemKind.Video, "video", videos[2].Id, videoId: videos[2].Id);
            LinkGroupItem(groups[2], 1715, GroupItemKind.Image, "image", images[0].Id);
            LinkGroupItem(groups[2], 1716, GroupItemKind.Image, "image", images[1].Id);
            LinkGroupItem(groups[2], 1717, GroupItemKind.Image, "image", images[2].Id);
            LinkGroupItem(groups[2], 1718, GroupItemKind.Audio, "audio", audios[0].Id);
            LinkGroupItem(groups[2], 1719, GroupItemKind.Audio, "audio", audios[1].Id);
            LinkGroupItem(groups[2], 1720, GroupItemKind.Audio, "audio", audios[2].Id);
            LinkGroupItem(groups[2], 1721, GroupItemKind.Text, "text", texts[0].Id);
            LinkGroupItem(groups[2], 1722, GroupItemKind.Text, "text", texts[1].Id);
            LinkGroupItem(groups[2], 1723, GroupItemKind.Gallery, "gallery", galleries[0].Id);
            LinkGroupItem(groups[2], 1724, GroupItemKind.Gallery, "gallery", galleries[1].Id);
            LinkGroupItem(groups[2], 1725, GroupItemKind.Performer, "performer", performers[0].Id);
            LinkGroupItem(groups[2], 1726, GroupItemKind.Studio, "studio", studios[0].Id);
            LinkGroupItem(groups[2], 1727, GroupItemKind.Tag, "tag", tags[0].Id);
            LinkGroupItem(groups[2], 1728, GroupItemKind.Face, "face", faces[0].Id);
            LinkGroupItem(groups[2], 1729, GroupItemKind.Segment, "segment", segments[0].Id);

            Context.Folders.AddRange(folderA, folderB, folderC);
            Context.TagGroups.AddRange(tagGroups);
            Context.Studios.AddRange(studios);
            Context.Tags.AddRange(tags);
            Context.Performers.AddRange(performers);
            Context.Videos.AddRange(videos);
            Context.Images.AddRange(images);
            Context.Galleries.AddRange(galleries);
            Context.Groups.AddRange(groups);
            Context.Audios.AddRange(audios);
            Context.TextDocuments.AddRange(texts);
            Context.Segments.AddRange(segments);
            Context.Faces.AddRange(faces);

            var extensionScoreDefinition = new CustomFieldDefinition
            {
                Id = 2101,
                Key = "extension_score",
                Label = "Extension Score",
                Type = CustomFieldTypes.Number,
                EntityTypes = [CustomFieldEntityTypes.Video, CustomFieldEntityTypes.Audio, CustomFieldEntityTypes.Face],
                Filterable = true,
                Sortable = true,
            };

            Context.CustomFieldDefinitions.Add(extensionScoreDefinition);
            Context.CustomFieldValues.AddRange(
                CustomNumberValue(21101, extensionScoreDefinition, CustomFieldEntityTypes.Video, videos[0].Id, 20),
                CustomNumberValue(21102, extensionScoreDefinition, CustomFieldEntityTypes.Video, videos[1].Id, 10),
                CustomNumberValue(21103, extensionScoreDefinition, CustomFieldEntityTypes.Video, videos[2].Id, 30),
                CustomNumberValue(21104, extensionScoreDefinition, CustomFieldEntityTypes.Audio, audios[0].Id, 20),
                CustomNumberValue(21105, extensionScoreDefinition, CustomFieldEntityTypes.Audio, audios[1].Id, 10),
                CustomNumberValue(21106, extensionScoreDefinition, CustomFieldEntityTypes.Audio, audios[2].Id, 30),
                CustomNumberValue(21107, extensionScoreDefinition, CustomFieldEntityTypes.Face, faces[0].Id, 20),
                CustomNumberValue(21108, extensionScoreDefinition, CustomFieldEntityTypes.Face, faces[1].Id, 10),
                CustomNumberValue(21109, extensionScoreDefinition, CustomFieldEntityTypes.Face, faces[2].Id, 30));

            AddRating(RatingHostType.Video, videos[0].Id, 1);
            AddRating(RatingHostType.Video, videos[1].Id, 3);
            AddRating(RatingHostType.Video, videos[2].Id, 5);
            AddRating(RatingHostType.Image, images[0].Id, 2);
            AddRating(RatingHostType.Image, images[1].Id, 4);
            AddRating(RatingHostType.Image, images[2].Id, 6);
            AddRating(RatingHostType.Gallery, galleries[0].Id, 2);
            AddRating(RatingHostType.Gallery, galleries[1].Id, 5);
            AddRating(RatingHostType.Gallery, galleries[2].Id, 8);
            AddRating(RatingHostType.Group, groups[0].Id, 3);
            AddRating(RatingHostType.Group, groups[1].Id, 6);
            AddRating(RatingHostType.Group, groups[2].Id, 9);
            AddRating(RatingHostType.Audio, audios[0].Id, 2);
            AddRating(RatingHostType.Audio, audios[1].Id, 5);
            AddRating(RatingHostType.Audio, audios[2].Id, 8);
            AddRating(RatingHostType.Text, texts[0].Id, 2);
            AddRating(RatingHostType.Text, texts[1].Id, 5);
            AddRating(RatingHostType.Text, texts[2].Id, 8);
            AddRating(RatingHostType.Performer, performers[0].Id, 4);
            AddRating(RatingHostType.Performer, performers[1].Id, 7);
            AddRating(RatingHostType.Performer, performers[2].Id, 10);
            AddRating(RatingHostType.Studio, studios[0].Id, 5);
            AddRating(RatingHostType.Studio, studios[1].Id, 8);
            AddRating(RatingHostType.Studio, studios[2].Id, 9);
            AddRating(RatingHostType.Tag, tags[0].Id, 3);
            AddRating(RatingHostType.Tag, tags[1].Id, 6);
            AddRating(RatingHostType.Tag, tags[2].Id, 9);

            AddAffinity(AffinityHostType.Video, videos[0].Id, viewCount: 1, likeCount: 10, totalConsumedSec: 100, lastPositionSec: 11, lastConsumedAt: now.AddDays(-30), favoritedAt: now.AddDays(-10));
            AddAffinity(AffinityHostType.Video, videos[1].Id, viewCount: 3, likeCount: 20, totalConsumedSec: 200, lastPositionSec: 22, lastConsumedAt: now.AddDays(-20), favoritedAt: now.AddDays(-8));
            AddAffinity(AffinityHostType.Video, videos[2].Id, viewCount: 9, likeCount: 30, totalConsumedSec: 300, lastPositionSec: 33, lastConsumedAt: now.AddDays(-10), favoritedAt: now.AddDays(-6));
            // Performer-host FavoritedAt drives the performer last_like_at sort.
            AddAffinity(AffinityHostType.Performer, performers[0].Id, viewCount: 0, likeCount: 0, totalConsumedSec: 0, lastPositionSec: null, lastConsumedAt: null, favoritedAt: now.AddDays(-10));
            AddAffinity(AffinityHostType.Performer, performers[1].Id, viewCount: 0, likeCount: 0, totalConsumedSec: 0, lastPositionSec: null, lastConsumedAt: null, favoritedAt: now.AddDays(-8));
            AddAffinity(AffinityHostType.Performer, performers[2].Id, viewCount: 0, likeCount: 0, totalConsumedSec: 0, lastPositionSec: null, lastConsumedAt: null, favoritedAt: now.AddDays(-6));
            AddAffinity(AffinityHostType.Image, images[0].Id, viewCount: 1, likeCount: 5, totalConsumedSec: 0, lastPositionSec: null, lastConsumedAt: now.AddDays(-30));
            AddAffinity(AffinityHostType.Image, images[1].Id, viewCount: 1, likeCount: 15, totalConsumedSec: 0, lastPositionSec: null, lastConsumedAt: now.AddDays(-20));
            AddAffinity(AffinityHostType.Image, images[2].Id, viewCount: 1, likeCount: 25, totalConsumedSec: 0, lastPositionSec: null, lastConsumedAt: now.AddDays(-10));
            AddAffinity(AffinityHostType.Audio, audios[0].Id, viewCount: 2, likeCount: 4, totalConsumedSec: 40, lastPositionSec: 4, lastConsumedAt: now.AddDays(-25));
            AddAffinity(AffinityHostType.Audio, audios[1].Id, viewCount: 4, likeCount: 8, totalConsumedSec: 80, lastPositionSec: 8, lastConsumedAt: now.AddDays(-15));
            AddAffinity(AffinityHostType.Audio, audios[2].Id, viewCount: 6, likeCount: 12, totalConsumedSec: 120, lastPositionSec: 12, lastConsumedAt: now.AddDays(-5));
            AddAffinity(AffinityHostType.Text, texts[0].Id, viewCount: 2, likeCount: 4, totalConsumedSec: 40, lastPositionSec: 4, lastConsumedAt: now.AddDays(-24));
            AddAffinity(AffinityHostType.Text, texts[1].Id, viewCount: 4, likeCount: 8, totalConsumedSec: 80, lastPositionSec: 8, lastConsumedAt: now.AddDays(-14));
            AddAffinity(AffinityHostType.Text, texts[2].Id, viewCount: 6, likeCount: 12, totalConsumedSec: 120, lastPositionSec: 12, lastConsumedAt: now.AddDays(-4));
            Context.Interactions.AddRange(
                new Interaction { UserId = TestUserId, HostType = InteractionHostType.Image, HostId = images[0].Id, Kind = InteractionKind.LikeCount, At = now.AddDays(-9) },
                new Interaction { UserId = TestUserId, HostType = InteractionHostType.Image, HostId = images[1].Id, Kind = InteractionKind.LikeCount, At = now.AddDays(-6) },
                new Interaction { UserId = TestUserId, HostType = InteractionHostType.Image, HostId = images[2].Id, Kind = InteractionKind.LikeCount, At = now.AddDays(-3) },
                new Interaction { UserId = TestUserId, HostType = InteractionHostType.Video, HostId = videos[0].Id, Kind = InteractionKind.LikeCount, At = now.AddDays(-8) },
                new Interaction { UserId = TestUserId, HostType = InteractionHostType.Video, HostId = videos[1].Id, Kind = InteractionKind.LikeCount, At = now.AddDays(-5) },
                new Interaction { UserId = TestUserId, HostType = InteractionHostType.Video, HostId = videos[2].Id, Kind = InteractionKind.LikeCount, At = now.AddDays(-2) });

            await Context.SaveChangesAsync();
            await CorruptCountColumnsAsync(performers, images, studios);

            Videos = videos;
            Images = images;
            Audios = audios;
            Texts = texts;
            Galleries = galleries;
            Groups = groups;
            SegmentRows =
            [
                new(segments[0], videos[0].Title, tags[0].Name, "Alpha Face", performers[0].Name),
                new(segments[1], videos[1].Title, tags[1].Name, "Beta Face", performers[1].Name),
                new(segments[2], videos[2].Title, tags[2].Name, performers[2].Name, performers[2].Name),
            ];
            Performers = performers;
            Studios = studios;
            Tags = tags;
            Faces = faces;
        }

        private void AddRating(RatingHostType hostType, int hostId, int value)
        {
            Ratings[(hostType, hostId)] = value;
            Context.Ratings.Add(new Rating { UserId = TestUserId, HostType = hostType, HostId = hostId, Value = value, Aspect = "overall" });
        }

        private void AddAffinity(AffinityHostType hostType, int hostId, int viewCount, int likeCount, double totalConsumedSec, double? lastPositionSec, DateTime? lastConsumedAt, DateTime? favoritedAt = null)
        {
            var affinity = new UserEntityAffinity
            {
                UserId = TestUserId,
                HostType = hostType,
                HostId = hostId,
                ViewCount = viewCount,
                LikeCount = likeCount,
                TotalConsumedSec = totalConsumedSec,
                LastPositionSec = lastPositionSec,
                LastConsumedAt = lastConsumedAt,
                IsFavorite = favoritedAt != null,
                FavoritedAt = favoritedAt,
            };
            Affinities[(hostType, hostId)] = affinity;
            Context.UserEntityAffinities.Add(affinity);
        }

        private async Task CorruptCountColumnsAsync(IReadOnlyList<Performer> performers, IReadOnlyList<Image> images, IReadOnlyList<Studio> studios)
        {
            performers[0].VideoCount = 0;
            performers[1].VideoCount = 9;
            performers[2].VideoCount = 5;
            performers[0].ImageCount = 0;
            performers[1].ImageCount = 9;
            performers[2].ImageCount = 5;
            images[0].PerformerCount = 9;
            images[1].PerformerCount = 0;
            images[2].PerformerCount = 5;
            studios[0].VideoCount = 1;
            studios[1].VideoCount = 2;
            studios[2].VideoCount = 3;
            studios[0].ImageCount = 1;
            studios[1].ImageCount = 2;
            studios[2].ImageCount = 3;
            studios[0].GalleryCount = 1;
            studios[1].GalleryCount = 2;
            studios[2].GalleryCount = 3;
            studios[0].ChildStudioCount = 1;
            studios[1].ChildStudioCount = 2;
            studios[2].ChildStudioCount = 3;

            await ((HarnessCoveContext)Context).SaveWithoutDerivedCountsAsync();
        }

        private static void LinkVideo(Video video, Tag tag, params Performer[] performers)
        {
            LinkVideoTag(video, tag);

            foreach (var performer in performers)
            {
                var videoPerformer = new VideoPerformer { Video = video, VideoId = video.Id, Performer = performer, PerformerId = performer.Id };
                video.VideoPerformers.Add(videoPerformer);
                performer.VideoPerformers.Add(videoPerformer);
            }
        }

        private static CustomFieldValue CustomNumberValue(int id, CustomFieldDefinition definition, string entityType, int entityId, decimal value)
            => new()
            {
                Id = id,
                Definition = definition,
                DefinitionId = definition.Id,
                EntityType = entityType,
                EntityId = entityId,
                NumberValue = value,
            };

        private static void LinkVideoTag(Video video, Tag tag)
        {
            var videoTag = new VideoTag { Video = video, VideoId = video.Id, Tag = tag, TagId = tag.Id };
            video.VideoTags.Add(videoTag);
            tag.VideoTags.Add(videoTag);
        }

        private static void LinkImage(Image image, params Performer[] performers)
        {
            foreach (var performer in performers)
            {
                var imagePerformer = new ImagePerformer { Image = image, ImageId = image.Id, Performer = performer, PerformerId = performer.Id };
                image.ImagePerformers.Add(imagePerformer);
                performer.ImagePerformers.Add(imagePerformer);
            }
        }

        private static void LinkImageTag(Image image, params Tag[] tags)
        {
            foreach (var tag in tags)
            {
                var imageTag = new ImageTag { Image = image, ImageId = image.Id, Tag = tag, TagId = tag.Id };
                image.ImageTags.Add(imageTag);
                tag.ImageTags.Add(imageTag);
            }
        }

        private static void LinkGalleryImage(Gallery gallery, params Image[] images)
        {
            foreach (var image in images)
            {
                var imageGallery = new ImageGallery { Gallery = gallery, GalleryId = gallery.Id, Image = image, ImageId = image.Id };
                gallery.ImageGalleries.Add(imageGallery);
                image.ImageGalleries.Add(imageGallery);
            }
        }

        private static void LinkGalleryVideo(Gallery gallery, params Video[] videos)
        {
            foreach (var video in videos)
            {
                var videoGallery = new VideoGallery { Gallery = gallery, GalleryId = gallery.Id, Video = video, VideoId = video.Id };
                gallery.VideoGalleries.Add(videoGallery);
                video.VideoGalleries.Add(videoGallery);
            }
        }

        private static void LinkGalleryPerformer(Gallery gallery, params Performer[] performers)
        {
            foreach (var performer in performers)
            {
                var galleryPerformer = new GalleryPerformer { Gallery = gallery, GalleryId = gallery.Id, Performer = performer, PerformerId = performer.Id };
                gallery.GalleryPerformers.Add(galleryPerformer);
                performer.GalleryPerformers.Add(galleryPerformer);
            }
        }

        private static void LinkGalleryTag(Gallery gallery, params Tag[] tags)
        {
            foreach (var tag in tags)
            {
                var galleryTag = new GalleryTag { Gallery = gallery, GalleryId = gallery.Id, Tag = tag, TagId = tag.Id };
                gallery.GalleryTags.Add(galleryTag);
                tag.GalleryTags.Add(galleryTag);
            }
        }

        private static void LinkGroupTag(Group group, params Tag[] tags)
        {
            foreach (var tag in tags)
            {
                var groupTag = new GroupTag { Group = group, GroupId = group.Id, Tag = tag, TagId = tag.Id };
                group.GroupTags.Add(groupTag);
                tag.GroupTags.Add(groupTag);
            }
        }

        private static void LinkGroupItem(Group group, int id, GroupItemKind kind, string hostType, int hostId, int? videoId = null)
        {
            group.GroupItems.Add(new GroupItem
            {
                Id = id,
                Group = group,
                GroupId = group.Id,
                Kind = kind,
                HostType = hostType,
                HostId = hostId,
                VideoId = videoId,
                OrderIndex = group.GroupItems.Count + 1,
            });
        }

        private static void LinkGroupRelation(Group containingGroup, Group subGroup)
        {
            var relation = new GroupRelation
            {
                ContainingGroup = containingGroup,
                ContainingGroupId = containingGroup.Id,
                SubGroup = subGroup,
                SubGroupId = subGroup.Id,
                OrderIndex = containingGroup.SubGroupRelations.Count + 1,
            };
            containingGroup.SubGroupRelations.Add(relation);
            subGroup.ContainingGroupRelations.Add(relation);
        }

        private static void LinkAudioTag(Audio audio, params Tag[] tags)
        {
            foreach (var tag in tags)
            {
                var audioTag = new AudioTag { Audio = audio, AudioId = audio.Id, Tag = tag, TagId = tag.Id };
                audio.AudioTags.Add(audioTag);
            }
        }

        private static void LinkAudioPerformer(Audio audio, params Performer[] performers)
        {
            foreach (var performer in performers)
            {
                var audioPerformer = new AudioPerformer { Audio = audio, AudioId = audio.Id, Performer = performer, PerformerId = performer.Id };
                audio.AudioPerformers.Add(audioPerformer);
            }
        }

        private static void LinkTextTag(TextDocument text, params Tag[] tags)
        {
            foreach (var tag in tags)
            {
                var textTag = new TextTag { TextDocument = text, TextDocumentId = text.Id, Tag = tag, TagId = tag.Id };
                text.TextTags.Add(textTag);
            }
        }

        private static void LinkTextPerformer(TextDocument text, params Performer[] performers)
        {
            foreach (var performer in performers)
            {
                var textPerformer = new TextPerformer { TextDocument = text, TextDocumentId = text.Id, Performer = performer, PerformerId = performer.Id };
                text.TextPerformers.Add(textPerformer);
            }
        }

        private static void LinkPerformerTag(Performer performer, params Tag[] tags)
        {
            foreach (var tag in tags)
            {
                var performerTag = new PerformerTag { Performer = performer, PerformerId = performer.Id, Tag = tag, TagId = tag.Id };
                performer.PerformerTags.Add(performerTag);
                tag.PerformerTags.Add(performerTag);
            }
        }

        private static void LinkStudioTag(Studio studio, params Tag[] tags)
        {
            foreach (var tag in tags)
            {
                var studioTag = new StudioTag { Studio = studio, StudioId = studio.Id, Tag = tag, TagId = tag.Id };
                studio.StudioTags.Add(studioTag);
                tag.StudioTags.Add(studioTag);
            }
        }
    }

    public sealed record SegmentHarnessRow(Segment Segment, string? VideoTitle, string? TagName, string? RefLabel, string? PerformerName);

    private sealed class HarnessCoveContext(DbContextOptions<CoveContext> options, ICurrentPrincipalAccessor principalAccessor) : CoveContext(options, principalAccessor)
    {
        public async Task SaveWithoutDerivedCountsAsync(CancellationToken cancellationToken = default)
        {
            var field = typeof(CoveContext).GetField("_persistingDerivedCounts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find CoveContext derived-count guard field.");
            field.SetValue(this, true);
            try
            {
                await SaveChangesAsync(cancellationToken);
            }
            finally
            {
                field.SetValue(this, false);
            }
        }
    }
}
