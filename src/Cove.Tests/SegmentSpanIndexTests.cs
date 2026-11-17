using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Data.Services;
using Cove.Tests.Postgres;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Cove.Tests;

/// <summary>
/// The stored span index: that its rows are what the resolver produces, that the database triggers
/// mark a video stale for every kind of write, and that a stale video is refreshed before it is listed.
/// </summary>
[RequiresPostgres]
public class SegmentSpanIndexTests
{
    [Fact]
    public async Task StoredSpans_AreTheSpansTheResolverProduces()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var tag = new Tag { Name = "Chorus", Color = "#112233" };
        fixture.Context.Tags.Add(tag);
        var video = await fixture.AddVideoAsync("Mixed");
        fixture.Context.Segments.AddRange(
            // Two tagged segments within the merge gap, a third far away and too short on its own.
            Segment(video, 0, 12, tagId: tag.Id, confidence: 0.4f),
            Segment(video, 15, 30, tagId: tag.Id, confidence: 0.9f),
            Segment(video, 100, 104, tagId: tag.Id),
            // An untagged pair that only merges because the titles match.
            Segment(video, 40, 55, kind: "face", refId: 7, title: "Someone", sourceKey: "ext:ai.faces"),
            Segment(video, 56, 70, kind: "face", refId: 8, title: "Someone", sourceKey: "ext:ai.faces"),
            Segment(video, 56, 70, kind: "face", refId: 9, title: "Someone else", sourceKey: "ext:ai.faces"),
            // An instant marker.
            Segment(video, 200, null, kind: "marker", title: "Cue"));
        await fixture.Context.SaveChangesAsync(ct);

        var profileId = await fixture.Resolver.ResolveProfileIdAsync(null, ct);
        Assert.Equal(0, await fixture.Index.EnsureFreshAsync(profileId, TimeSpan.FromSeconds(30), 1000, ct));

        var expected = (await fixture.Resolver.ResolveVideoAsync(video.Id, profileId, ct)).Spans;
        var stored = await fixture.Context.StoredSegmentSpans.AsNoTracking()
            .Where(span => span.ProfileId == profileId && span.VideoId == video.Id)
            .OrderBy(span => span.Ordinal)
            .ToListAsync(ct);

        Assert.Equal(4, expected.Count);
        Assert.Equal(expected.Select(span => span.SpanKey), stored.Select(span => span.SpanKey));
        Assert.Equal(expected.Select(span => (span.StartSec, span.EndSec)), stored.Select(span => (span.StartSec, span.EndSec)));
        Assert.Equal(expected.Select(span => span.SegmentIds.ToArray()), stored.Select(span => span.SegmentIds));
        Assert.Equal(expected.Select(span => span.ColorHint), stored.Select(span => span.ColorHint));
        Assert.Equal(expected.Select(span => span.CollapsedToInstant), stored.Select(span => span.CollapsedToInstant));

        var merged = stored.Single(span => span.TagId == tag.Id);
        Assert.Equal(2, merged.SegmentCount);
        Assert.Equal(0.4f, merged.MinConfidence);
        Assert.Equal(0.9f, merged.MaxConfidence);
        var faces = stored.Single(span => span.Label == "Someone");
        Assert.Equal(new long[] { 7, 8 }, faces.RefIds);
        Assert.Equal(new[] { 7, 8 }, faces.EntityRefIds);
        Assert.Equal(new[] { "Someone" }, faces.Titles);
    }

    [Fact]
    public async Task SegmentWrittenWithPlainSql_IsListedByTheNextSearch()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var video = await fixture.AddVideoAsync("Written elsewhere");
        Assert.Empty((await fixture.SearchAsync()).Items);

        // The way an extension or a bulk import writes: no application code runs.
        await fixture.Context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO segments ("HostType", "HostId", "StartSec", "EndSec", "Kind", "SourceKey", "CreatedAt", "UpdatedAt")
            VALUES (1, {video.Id}, 5, 40, 'scene', 'ext:test', now(), now())
            """, ct);

        var listed = Assert.Single((await fixture.SearchAsync()).Items);
        Assert.Equal((5d, 40d), (listed.Span.StartSec, listed.Span.EndSec));

        await fixture.Context.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE segments SET "EndSec" = 90 WHERE "HostId" = {video.Id}""", ct);
        Assert.Equal(90d, Assert.Single((await fixture.SearchAsync()).Items).Span.EndSec);

        await fixture.Context.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM segments WHERE "HostId" = {video.Id}""", ct);
        var afterDelete = await fixture.SearchAsync();
        Assert.Empty(afterDelete.Items);
        Assert.Equal(0, afterDelete.TotalCount);
    }

    [Fact]
    public async Task MovingASegmentToAnotherVideo_RefreshesBothVideos()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var first = await fixture.AddVideoAsync("First");
        var second = await fixture.AddVideoAsync("Second");
        var segment = Segment(first, 0, 30, kind: "scene");
        fixture.Context.Segments.Add(segment);
        await fixture.Context.SaveChangesAsync(ct);
        Assert.Equal(first.Id, Assert.Single((await fixture.SearchAsync()).Items).VideoId);

        await fixture.Context.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE segments SET "HostId" = {second.Id} WHERE "Id" = {segment.Id}""", ct);

        Assert.Equal(second.Id, Assert.Single((await fixture.SearchAsync()).Items).VideoId);
    }

    [Fact]
    public async Task SegmentWrites_MarkEachVideoTheyTouchOnce_AndNothingElse()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var first = await fixture.AddVideoAsync("First");
        var second = await fixture.AddVideoAsync("Second");
        var untouched = await fixture.AddVideoAsync("Untouched");
        var missingVideoId = untouched.Id + 1000;

        async Task<Dictionary<int, long>> VersionsAsync()
            => await fixture.Context.SegmentSpanVideoVersions.AsNoTracking()
                .ToDictionaryAsync(version => version.VideoId, version => version.Version, ct);

        // One statement: two rows on the first video, one on the second, one hosted by an image with
        // the untouched video's id, and one naming a video that does not exist.
        await fixture.Context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO segments ("HostType", "HostId", "StartSec", "EndSec", "Kind", "SourceKey", "CreatedAt", "UpdatedAt")
            VALUES (1, {first.Id}, 0, 10, 'scene', 'ext:test', now(), now()),
                   (1, {first.Id}, 20, 30, 'scene', 'ext:test', now(), now()),
                   (1, {second.Id}, 0, 10, 'scene', 'ext:test', now(), now()),
                   (2, {untouched.Id}, 0, 10, 'scene', 'ext:test', now(), now()),
                   (1, {missingVideoId}, 0, 10, 'scene', 'ext:test', now(), now())
            """, ct);
        var afterInsert = await VersionsAsync();
        Assert.Equal(new Dictionary<int, long> { [first.Id] = 1, [second.Id] = 1 }, afterInsert);

        // An update counts the video a row leaves and the one it joins, each once.
        await fixture.Context.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE segments SET "HostId" = {second.Id} WHERE "HostType" = 1 AND "HostId" = {first.Id} AND "StartSec" = 20""", ct);
        Assert.Equal(new Dictionary<int, long> { [first.Id] = 2, [second.Id] = 2 }, await VersionsAsync());

        await fixture.Context.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM segments WHERE "HostType" = 1 AND "HostId" = {first.Id}""", ct);
        Assert.Equal(new Dictionary<int, long> { [first.Id] = 3, [second.Id] = 2 }, await VersionsAsync());

        // Rows of other hosts, and of videos that do not exist, never create one.
        await fixture.Context.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM segments WHERE "HostType" = 2 OR "HostId" = {missingVideoId}""", ct);
        Assert.Equal(new Dictionary<int, long> { [first.Id] = 3, [second.Id] = 2 }, await VersionsAsync());
    }

    [Fact]
    public async Task RefreshingStatistics_LetsThePlannerSeeTheStoredSpans()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        for (var index = 0; index < 3; index++)
        {
            var video = await fixture.AddVideoAsync($"Video {index}");
            fixture.Context.Segments.Add(Segment(video, 0, 30, kind: "scene"));
            fixture.Context.Segments.Add(Segment(video, 100, 130, kind: "scene"));
        }

        await fixture.Context.SaveChangesAsync(ct);
        Assert.Equal(6, (await fixture.SearchAsync()).TotalCount);

        await fixture.Index.RefreshStatisticsAsync(ct);

        var plannedRows = await fixture.Context.Database
            .SqlQueryRaw<float>("""SELECT reltuples AS "Value" FROM pg_class WHERE oid = 'public.segment_spans'::regclass""")
            .SingleAsync(ct);
        Assert.Equal(6f, plannedRows);
    }

    [Fact]
    public async Task ChangingTheProfilesRules_RebuildsItsSpans()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var video = await fixture.AddVideoAsync("Rules");
        fixture.Context.Segments.AddRange(Segment(video, 0, 20, kind: "scene"), Segment(video, 25, 45, kind: "scene"));
        await fixture.Context.SaveChangesAsync(ct);
        Assert.Single((await fixture.SearchAsync()).Items);

        // What the profile editor does: change a rule. The database gives the profile a new version.
        var profileId = await fixture.Resolver.ResolveProfileIdAsync(null, ct);
        var rule = await fixture.Context.SegmentDisplayRules.SingleAsync(item => item.ProfileId == profileId, ct);
        rule.MergeGapSec = 1;
        await fixture.Context.SaveChangesAsync(ct);

        Assert.Equal(1, await fixture.Index.CountPendingAsync(profileId, 1000, ct));
        var rebuilt = await fixture.SearchAsync();
        Assert.Equal(2, rebuilt.Items.Count);
        Assert.Equal(0, rebuilt.PendingVideoCount);
    }

    [Fact]
    public async Task TagChanges_RefreshTheSpansThatUseTheTag()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var tag = new Tag { Name = "Verse", Color = "#111111" };
        fixture.Context.Tags.Add(tag);
        var video = await fixture.AddVideoAsync("Tagged");
        fixture.Context.Segments.Add(Segment(video, 0, 30, tagId: tag.Id));
        await fixture.Context.SaveChangesAsync(ct);
        var before = Assert.Single((await fixture.SearchAsync()).Items);
        Assert.Equal(("Verse", "#111111"), (before.Span.TagName, before.Span.ColorHint));

        tag.Name = "Bridge";
        tag.Color = "#222222";
        await fixture.Context.SaveChangesAsync(ct);
        var recolored = Assert.Single((await fixture.SearchAsync()).Items);
        Assert.Equal(("Bridge", "#222222"), (recolored.Span.TagName, recolored.Span.ColorHint));

        // Deleting the tag clears it from the segment through the foreign key, not through a statement
        // the application issues against segments.
        await fixture.Context.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM tags WHERE "Id" = {tag.Id}""", ct);
        var untagged = Assert.Single((await fixture.SearchAsync()).Items);
        Assert.Null(untagged.Span.TagId);
    }

    [Fact]
    public async Task DeletingAVideo_RemovesItsStoredSpans()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var video = await fixture.AddVideoAsync("Short-lived");
        fixture.Context.Segments.Add(Segment(video, 0, 30, kind: "scene"));
        await fixture.Context.SaveChangesAsync(ct);
        Assert.Single((await fixture.SearchAsync()).Items);

        await fixture.Context.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM videos WHERE "Id" = {video.Id}""", ct);

        Assert.Empty(await fixture.Context.StoredSegmentSpans.AsNoTracking().ToListAsync(ct));
        Assert.Empty(await fixture.Context.SegmentSpanVideoStates.AsNoTracking().ToListAsync(ct));
        Assert.Empty(await fixture.Context.SegmentSpanVideoVersions.AsNoTracking().ToListAsync(ct));
        Assert.Empty((await fixture.SearchAsync()).Items);
    }

    [Fact]
    public async Task BackgroundPass_WaitsForAVideoToSettle_ARequestDoesNot()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var video = await fixture.AddVideoAsync("Still importing");
        fixture.Context.Segments.Add(Segment(video, 0, 30, kind: "scene"));
        await fixture.Context.SaveChangesAsync(ct);
        var profileId = await fixture.Resolver.ResolveProfileIdAsync(null, ct);

        Assert.Equal(0, await fixture.Index.ReconcileBatchAsync(profileId, 10, ct, settleDelay: TimeSpan.FromMinutes(5)));
        Assert.Equal(1, await fixture.Index.CountPendingAsync(profileId, 1000, ct));

        Assert.Equal(1, await fixture.Index.ReconcileBatchAsync(profileId, 10, ct));
        Assert.Equal(0, await fixture.Index.CountPendingAsync(profileId, 1000, ct));
    }

    [Fact]
    public async Task CountAndTotals_ComeFromTheStoredSpans()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        float[] confidences = [0.2f, 0.3f, 0.4f, 0.5f, 0.6f];
        for (var index = 0; index < 5; index++)
        {
            var video = await fixture.AddVideoAsync($"Video {index}");
            fixture.Context.Segments.AddRange(
                Segment(video, 0, 20, kind: "scene", confidence: confidences[index]),
                Segment(video, 100, 130, kind: "credits"));
        }

        await fixture.Context.SaveChangesAsync(ct);

        var count = await fixture.CountAsync(new SegmentSpanSearchRequestDto(null, null, null, null, null, null, null, null, null, null));
        Assert.Equal((10, 250d), (count.TotalCount, count.Duration));

        var scenes = await fixture.CountAsync(new SegmentSpanSearchRequestDto(null, null, null, null, null, null, null, null, null, null, Kind: "Scene"));
        Assert.Equal((5, 100d), (scenes.TotalCount, scenes.Duration));

        // Browsing in video order leaves the total to the count endpoint while more pages follow.
        var firstPage = await fixture.SearchAsync(new SegmentSpanSearchRequestDto(null, null, 1, 4, "updated_at", "desc", null, null, null, null));
        Assert.Equal((4, -1, true), (firstPage.Items.Count, firstPage.TotalCount, firstPage.HasMore));
        var lastPage = await fixture.SearchAsync(new SegmentSpanSearchRequestDto(null, null, 3, 4, "updated_at", "desc", null, null, null, null));
        Assert.Equal((2, 10, false), (lastPage.Items.Count, lastPage.TotalCount, lastPage.HasMore));

        var byDuration = await fixture.SearchAsync(new SegmentSpanSearchRequestDto(null, null, 1, 3, "duration", "desc", null, null, null, null));
        Assert.Equal(10, byDuration.TotalCount);
        Assert.All(byDuration.Items, item => Assert.Equal("credits", item.Span.Kind));

        var confident = await fixture.SearchAsync(new SegmentSpanSearchRequestDto(
            null, null, 1, 10, "confidence", "desc", null, null, null, null, Confidence: 0.45f));
        Assert.Equal(2, confident.TotalCount);
        Assert.Equal(new float?[] { 0.6f, 0.5f }, confident.Items.Select(item => item.Confidence).ToArray());
    }

    [Fact]
    public async Task RuleChangedWithoutTheProfileEditor_StillRebuildsTheProfilesSpans()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var hidden = new Tag { Name = "Hidden" };
        var shown = new Tag { Name = "Shown" };
        fixture.Context.Tags.AddRange(hidden, shown);
        var video = await fixture.AddVideoAsync("Merged tags");
        fixture.Context.Segments.Add(Segment(video, 0, 30, tagId: shown.Id));
        await fixture.Context.SaveChangesAsync(ct);
        var profileId = await fixture.Resolver.ResolveProfileIdAsync(null, ct);
        fixture.Context.SegmentDisplayRules.Add(new SegmentDisplayRule { ProfileId = profileId, TagId = hidden.Id, Visible = false });
        await fixture.Context.SaveChangesAsync(ct);
        Assert.Single((await fixture.SearchAsync()).Items);

        // What a tag merge does: the rule now names another tag, and nothing bumps the version in code.
        // The video's segments are untouched, so only the profile's new version marks it stale.
        var versionBefore = await fixture.Context.SegmentDisplayProfiles.AsNoTracking()
            .Where(profile => profile.Id == profileId).Select(profile => profile.Version).SingleAsync(ct);
        await fixture.Context.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE segment_display_rules SET "TagId" = {shown.Id} WHERE "TagId" = {hidden.Id}""", ct);

        var versionAfter = await fixture.Context.SegmentDisplayProfiles.AsNoTracking()
            .Where(profile => profile.Id == profileId).Select(profile => profile.Version).SingleAsync(ct);
        Assert.True(versionAfter > versionBefore);
        Assert.Empty((await fixture.SearchAsync()).Items);

        // Deleting the tag clears it from the rule (and from the segment) through foreign keys, with no
        // statement against either table. The rule now names no tag and so loses to the built-in rule
        // for videos, which shows the segment again.
        await fixture.Context.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM tags WHERE "Id" = {shown.Id}""", ct);
        var relisted = Assert.Single((await fixture.SearchAsync()).Items);
        Assert.Null(relisted.Span.TagId);

        await fixture.Context.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM segment_display_rules WHERE "Visible" = false""", ct);
        Assert.Single((await fixture.SearchAsync()).Items);
        var versionAtEnd = await fixture.Context.SegmentDisplayProfiles.AsNoTracking()
            .Where(profile => profile.Id == profileId).Select(profile => profile.Version).SingleAsync(ct);
        Assert.True(versionAtEnd >= versionAfter + 2);
    }

    [Fact]
    public async Task ReferenceFilters_AndTextSearch_ReadTheStoredSpans()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var jane = new Performer { Name = "Jane Example" };
        var kim = new Performer { Name = "Kim Other" };
        fixture.Context.Performers.AddRange(jane, kim);
        // Spare faces first, so no face shares an id with a performer.
        fixture.Context.Faces.AddRange(new Face { Label = "spare 1" }, new Face { Label = "spare 2" }, new Face { Label = "spare 3" });
        await fixture.Context.SaveChangesAsync(ct);
        var janeFace = new Face { Label = "face-0001", PerformerId = jane.Id };
        var unknownFace = new Face { Label = "Stranger" };
        fixture.Context.Faces.AddRange(janeFace, unknownFace);
        var video = await fixture.AddVideoAsync("Élan Vital");
        var other = await fixture.AddVideoAsync("Plain");
        fixture.Context.Segments.AddRange(
            Segment(video, 0, 30, kind: "face", refId: janeFace.Id, title: "a", sourceKey: "ext:ai.faces"),
            Segment(video, 100, 130, kind: "face", refId: unknownFace.Id, title: "b", sourceKey: "ext:ai.faces"),
            Segment(other, 0, 30, kind: "performer", refId: kim.Id, title: "c"),
            // A tag-kind segment whose reference happens to equal a performer id names no performer.
            Segment(other, 100, 130, kind: "scene", refId: jane.Id, title: "ÜBER scene"));
        await fixture.Context.SaveChangesAsync(ct);

        SegmentSpanSearchRequestDto Request() => new(null, null, 1, 50, "start_sec", "asc", null, null, null, null);
        async Task<string?[]> TitlesAsync(SegmentSpanSearchRequestDto request)
        {
            var response = await fixture.SearchAsync(request);
            Assert.Equal(response.Items.Count, response.TotalCount);
            var count = await fixture.CountAsync(request);
            Assert.Equal(response.Items.Count, count.TotalCount);
            return response.Items.Select(item => item.Span.TagName).Order().ToArray();
        }

        Assert.Equal(new[] { "a" }, await TitlesAsync(Request() with { PerformerIds = [jane.Id] }));
        Assert.Equal(new[] { "c" }, await TitlesAsync(Request() with { PerformerIds = [kim.Id] }));
        Assert.Equal(new[] { "a", "c" }, await TitlesAsync(Request() with { PerformerIds = [jane.Id, kim.Id] }));
        Assert.Equal(new[] { "b" }, await TitlesAsync(Request() with { RefIds = [unknownFace.Id] }));

        // The text search reaches the linked performer's name, an unlinked face's label, a directly
        // referenced performer, the segment title and the video title, whatever the letter case, and
        // also for letters outside ASCII.
        Assert.Equal(new[] { "a" }, await TitlesAsync(Request() with { Q = "jane ex" }));
        Assert.Equal(new[] { "b" }, await TitlesAsync(Request() with { Q = "STRANGER" }));
        Assert.Equal(new[] { "c" }, await TitlesAsync(Request() with { Q = "kim" }));
        Assert.Equal(new[] { "ÜBER scene" }, await TitlesAsync(Request() with { Q = "über" }));
        Assert.Equal(new[] { "a", "b" }, await TitlesAsync(Request() with { Q = "élan" }));
        Assert.Equal(new[] { "ÜBER scene" }, await TitlesAsync(Request() with { Title = "über", TitleModifier = "INCLUDES" }));
        Assert.Equal(new[] { "a", "b", "c" }, await TitlesAsync(Request() with { Title = "über", TitleModifier = "EXCLUDES" }));
    }

    [Fact]
    public async Task ReaderWithoutVideoAccess_IsShownNothing_ThoughTheSharedIndexIsBuilt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var video = await fixture.AddVideoAsync("Private");
        fixture.Context.Segments.Add(Segment(video, 0, 30, kind: "scene"));
        await fixture.Context.SaveChangesAsync(ct);

        // This reader triggers the refresh, which has to read every video to build rows all users share.
        await using var restricted = fixture.ForPrincipal(Permissions.SegmentsRead);
        var response = await restricted.SearchAsync();
        Assert.Empty(response.Items);
        Assert.Equal(0, response.TotalCount);
        Assert.Equal(0, (await restricted.CountAsync()).TotalCount);

        Assert.Equal(1, await fixture.Context.StoredSegmentSpans.AsNoTracking().CountAsync(ct));
        // The refresh lifted the reader's row filters only for its own queries.
        Assert.Equal(0, await restricted.Context.Videos.AsNoTracking().CountAsync(ct));
    }

    [Fact]
    public async Task ReaderWithoutTagAccess_SeesTheSpanWithoutTheTagsNameColourOrLane()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var tag = new Tag { Name = "Secret", Color = "#abcdef", SegmentLaneOverride = 3 };
        fixture.Context.Tags.Add(tag);
        var video = await fixture.AddVideoAsync("Shared");
        fixture.Context.Segments.Add(Segment(video, 0, 30, tagId: tag.Id));
        await fixture.Context.SaveChangesAsync(ct);

        var full = Assert.Single((await fixture.SearchAsync()).Items);
        Assert.Equal(("Secret", "#abcdef", 3), (full.Span.TagName, full.Span.ColorHint, full.Span.Lane));
        // The principal is held per async flow, so this runs before the restricted reader takes over.
        var byTagName = new SegmentSpanSearchRequestDto(null, null, 1, 50, null, null, "Secret", null, null, null);
        Assert.Single((await fixture.SearchAsync(byTagName)).Items);

        await using var restricted = fixture.ForPrincipal(Permissions.SegmentsRead, Permissions.VideosRead);
        var limited = Assert.Single((await restricted.SearchAsync()).Items);
        Assert.Equal(tag.Id, limited.Span.TagId);
        Assert.Null(limited.Span.TagName);
        Assert.Null(limited.Span.ColorHint);
        Assert.Null(limited.Span.Lane);

        // Nor can the hidden name be searched for: a reader with every video but no tags finds nothing,
        // even though their video access needs no per-row check.
        Assert.Empty((await restricted.SearchAsync(byTagName)).Items);
        Assert.Equal(0, (await restricted.CountAsync(byTagName with { Page = null, PerPage = null })).TotalCount);
        Assert.Empty((await restricted.SearchAsync(byTagName with { Sort = "random" })).Items);
    }

    [Fact]
    public async Task ReadersWhoseAccessIsLimited_CannotReachHiddenVideosOrTagsThroughFiltersOrSorts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var zulu = new Tag { Name = "Zulu" };
        var alpha = new Tag { Name = "Alpha" };
        fixture.Context.Tags.AddRange(zulu, alpha);
        var first = await fixture.AddVideoAsync("First");
        var second = await fixture.AddVideoAsync("Second");
        fixture.Context.Segments.AddRange(Segment(first, 0, 30, tagId: zulu.Id), Segment(second, 0, 30, tagId: alpha.Id));
        fixture.Context.Set<VideoTag>().Add(new VideoTag { VideoId = first.Id, TagId = zulu.Id });
        await fixture.Context.SaveChangesAsync(ct);

        SegmentSpanSearchRequestDto Request(string? sort = null, string? direction = null, int[]? videoTagIds = null)
            => new(null, null, 1, 50, sort, direction, null, null, null, null) { VideoTagIds = videoTagIds };
        static int[] Videos(SegmentSpanSearchResponseDto response) => response.Items.Select(item => item.VideoId).ToArray();

        // Controls, as a reader who sees everything. The principal is held per async flow, so these run first.
        Assert.Equal(new[] { first.Id }, Videos(await fixture.SearchAsync(Request(videoTagIds: [zulu.Id]))));
        Assert.Equal(new[] { second.Id, first.Id }, Videos(await fixture.SearchAsync(Request("tag_name", "asc"))));

        // A share link that shares nothing sees no video, so none of their segments.
        await using (var shareLink = fixture.ForPrincipal(new CovePrincipal
        {
            UserId = null,
            Username = "share-link",
            Kind = PrincipalKind.ShareLink,
            TokenId = Guid.NewGuid(),
            Roles = new HashSet<string>(),
            Permissions = new HashSet<string> { Permissions.SegmentsRead, Permissions.VideosRead },
        }))
        {
            Assert.Empty((await shareLink.SearchAsync(Request())).Items);
            Assert.Equal(0, (await shareLink.CountAsync(Request() with { Page = null, PerPage = null })).TotalCount);
            Assert.Empty((await shareLink.SearchAsync(Request("random", "desc"))).Items);
        }

        // A reader of every video but no tags can neither filter by a hidden tag nor be sorted by hidden names.
        await using var noTags = fixture.ForPrincipal(Permissions.SegmentsRead, Permissions.VideosRead);
        Assert.Empty((await noTags.SearchAsync(Request(videoTagIds: [zulu.Id]))).Items);
        Assert.Equal(0, (await noTags.CountAsync(Request(videoTagIds: [zulu.Id]) with { Page = null, PerPage = null })).TotalCount);
        // With the names hidden both spans tie, so they fall back to video order both ways.
        Assert.Equal(new[] { first.Id, second.Id }, Videos(await noTags.SearchAsync(Request("tag_name", "asc"))));
        Assert.Equal(new[] { second.Id, first.Id }, Videos(await noTags.SearchAsync(Request("tag_name", "desc"))));
    }

    [Fact]
    public async Task ConcurrentRefreshes_StoreOneSetOfRows()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        for (var index = 0; index < 20; index++)
        {
            var video = await fixture.AddVideoAsync($"Video {index}");
            fixture.Context.Segments.AddRange(Segment(video, 0, 30, kind: "scene"), Segment(video, 100, 130, kind: "credits"));
        }

        await fixture.Context.SaveChangesAsync(ct);
        var profileId = await fixture.Resolver.ResolveProfileIdAsync(null, ct);

        // A request and the background pass, or two requests, refreshing the same videos at once.
        await using var first = fixture.ForPrincipal();
        await using var second = fixture.ForPrincipal();
        await using var third = fixture.ForPrincipal();
        await Task.WhenAll(
            first.Index.ReconcileBatchAsync(profileId, 50, ct),
            second.Index.ReconcileBatchAsync(profileId, 50, ct),
            third.Index.ReconcileBatchAsync(profileId, 50, ct));

        Assert.Equal(40, await fixture.Context.StoredSegmentSpans.AsNoTracking().CountAsync(span => span.ProfileId == profileId, ct));
        Assert.Equal(20, await fixture.Context.SegmentSpanVideoStates.AsNoTracking().CountAsync(state => state.ProfileId == profileId, ct));
        Assert.Equal(0, await fixture.Index.CountPendingAsync(profileId, 1000, ct));
    }

    [Fact]
    public async Task DeletingAProfile_DropsItsStoredSpans_AndKeepsTheOthers()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var video = await fixture.AddVideoAsync("Listed twice");
        fixture.Context.Segments.Add(Segment(video, 0, 30, kind: "scene"));
        await fixture.Context.SaveChangesAsync(ct);
        var defaultProfileId = await fixture.Resolver.ResolveProfileIdAsync(null, ct);
        var own = new SegmentDisplayProfile { Name = "Mine", Version = 1 };
        fixture.Context.SegmentDisplayProfiles.Add(own);
        await fixture.Context.SaveChangesAsync(ct);

        Assert.Single((await fixture.SearchAsync()).Items);
        Assert.Single((await fixture.SearchAsync(new SegmentSpanSearchRequestDto(own.Id, null, 1, 50, null, null, null, null, null, null))).Items);
        Assert.Equal(2, await fixture.Context.StoredSegmentSpans.AsNoTracking().CountAsync(ct));

        // Listing a profile again does not register it a second time.
        await fixture.Index.RegisterProfileAsync(own.Id, ct);
        Assert.Equal(2, await fixture.Context.SegmentSpanIndexedProfiles.AsNoTracking().CountAsync(ct));

        // Deleting the profile takes its stored rows with it; the default's stay.
        await fixture.Context.SegmentDisplayProfiles.Where(profile => profile.Id == own.Id).ExecuteDeleteAsync(ct);
        Assert.Equal(new[] { defaultProfileId }, await fixture.Context.StoredSegmentSpans.AsNoTracking().Select(span => span.ProfileId).ToArrayAsync(ct));
        Assert.Equal(new[] { defaultProfileId }, await fixture.Context.SegmentSpanVideoStates.AsNoTracking().Select(state => state.ProfileId).ToArrayAsync(ct));
        Assert.Equal(new[] { defaultProfileId }, await fixture.Context.SegmentSpanIndexedProfiles.AsNoTracking().Select(indexed => indexed.ProfileId).ToArrayAsync(ct));
    }

    [Fact]
    public async Task DerivedQueries_ResolveOnlyCandidateVideos_AndGiveTheSameSpans()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var tag = new Tag { Name = "Chorus" };
        fixture.Context.Tags.Add(tag);
        var both = await fixture.AddVideoAsync("Face and tag");
        var faceOnly = await fixture.AddVideoAsync("Face only");
        var tagOnly = await fixture.AddVideoAsync("Tag only");
        var neither = await fixture.AddVideoAsync("Neither");
        fixture.Context.Segments.AddRange(
            Segment(both, 0, 60, kind: "face", refId: 5, sourceKey: "ext:ai.faces"),
            Segment(both, 30, 90, tagId: tag.Id),
            Segment(faceOnly, 0, 60, kind: "face", refId: 5, sourceKey: "ext:ai.faces"),
            Segment(tagOnly, 0, 60, tagId: tag.Id),
            Segment(neither, 0, 60, kind: "face", refId: 6, sourceKey: "ext:ai.faces"));
        await fixture.Context.SaveChangesAsync(ct);

        // The kind is given in another letter case, as the resolver accepts it.
        var face = new SegmentSpanOperandDto(null, "FACE", null, null, [5]);
        var tagged = new SegmentSpanOperandDto(null, null, [tag.Id], null);
        async Task<(int VideoId, double Start, double End)[]> SpansAsync(string @operator, params SegmentSpanOperandDto[] operands)
        {
            var request = new SegmentSpanSearchRequestDto(
                null, new SegmentSpanDerivedQueryDto(@operator, [.. operands], null, null), 1, 50, "updated_at", "asc", null, null, null, null);
            var response = await fixture.SearchAsync(request);
            Assert.Equal(response.Items.Count, (await fixture.CountAsync(request)).TotalCount);
            return response.Items.Select(item => (item.VideoId, item.Span.StartSec, item.Span.EndSec)).Order().ToArray();
        }

        Assert.Equal(new[] { (both.Id, 30d, 60d) }, await SpansAsync("intersection", face, tagged));
        Assert.Equal(new[] { (both.Id, 0d, 30d), (faceOnly.Id, 0d, 60d) }, await SpansAsync("difference", face, tagged));
        Assert.Equal(
            new[] { (both.Id, 0d, 90d), (faceOnly.Id, 0d, 60d), (tagOnly.Id, 0d, 60d) },
            await SpansAsync("union", face, tagged));

        // The same query for another face must not be answered from the first one's cached total: the
        // two differ in nothing but the reference id.
        Assert.Equal(
            new[] { (both.Id, 0d, 60d), (faceOnly.Id, 0d, 60d) },
            await SpansAsync("union", new SegmentSpanOperandDto(null, "face", null, null, [5])));
        Assert.Equal(
            new[] { (neither.Id, 0d, 60d) },
            await SpansAsync("union", new SegmentSpanOperandDto(null, "face", null, null, [6])));
        Assert.Empty(await SpansAsync("intersection", new SegmentSpanOperandDto(null, "face", null, null, [6]), tagged));
    }

    [Fact]
    public async Task CleanFreshnessCheck_IsRememberedBriefly_AndEndedByAWriteThroughTheContext()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var video = await fixture.AddVideoAsync("Remembered");
        fixture.Context.Segments.Add(Segment(video, 0, 30, kind: "scene"));
        await fixture.Context.SaveChangesAsync(ct);
        var profileId = await fixture.Resolver.ResolveProfileIdAsync(null, ct);

        // The service as the application wires it, with the memory and the write signal.
        var writes = new LibraryWriteSignal();
        var index = new SegmentSpanIndexService(
            fixture.Context, fixture.Resolver, new SegmentSpanIndexSignal(), logger: null, new MemoryCache(new MemoryCacheOptions()), writes);
        Assert.Equal(0, await index.EnsureFreshAsync(profileId, TimeSpan.FromSeconds(30), 1000, ct));
        Assert.Equal(1, await fixture.Context.StoredSegmentSpans.AsNoTracking().CountAsync(ct));
        // That call had work to do. Only a check that finds nothing stale is remembered.
        Assert.Equal(0, await index.EnsureFreshAsync(profileId, TimeSpan.FromSeconds(30), 1000, ct));

        // A write the application cannot see (plain SQL from elsewhere) is not noticed while the clean
        // answer is remembered; the background pass or the lapse of the memory picks it up.
        await fixture.Context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO segments ("HostType", "HostId", "StartSec", "EndSec", "Kind", "SourceKey", "CreatedAt", "UpdatedAt")
            VALUES (1, {video.Id}, 100, 140, 'credits', 'ext:test', now(), now())
            """, ct);
        Assert.Equal(0, await index.EnsureFreshAsync(profileId, TimeSpan.FromSeconds(30), 1000, ct));
        Assert.Equal(1, await fixture.Context.StoredSegmentSpans.AsNoTracking().CountAsync(ct));

        // Any write through the context, which is how every edit made in the application arrives, ends
        // the memory at once.
        writes.Signal();
        Assert.Equal(0, await index.EnsureFreshAsync(profileId, TimeSpan.FromSeconds(30), 1000, ct));
        Assert.Equal(2, await fixture.Context.StoredSegmentSpans.AsNoTracking().CountAsync(ct));
    }

    [Theory]
    [InlineData("updated_at", "desc")]
    [InlineData("start_sec", "asc")]
    [InlineData("start_sec", "desc")]
    [InlineData("kind", "asc")]
    [InlineData("random", "desc")]
    public async Task DeepPages_ReadFromTheFarEnd_MatchTheSameOrderReadFromTheStart(string sort, string direction)
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var first = await fixture.AddVideoAsync("First");
        var second = await fixture.AddVideoAsync("Second");

        // Instant markers, one span each: enough of them that the last pages lie past the deep-page
        // threshold, with every start shared by the two videos so the orders have ties to break.
        const int perVideo = SegmentSpanIndexSearch.DeepPageOffset * 5 / 4;
        await fixture.Context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO segments ("HostType", "HostId", "StartSec", "Kind", "SourceKey", "CreatedAt", "UpdatedAt")
            SELECT 1, video.id, marker, CASE WHEN marker % 3 = 0 THEN 'beat' ELSE 'cue' END, 'user', now(), now()
            FROM (VALUES ({first.Id}), ({second.Id})) AS video(id) CROSS JOIN generate_series(1, {perVideo}) AS marker
            """, ct);

        // A page size that leaves a partial last page.
        const int perPage = 96;
        var total = perVideo * 2;
        var pageCount = (total + perPage - 1) / perPage;
        var firstDeepPage = SegmentSpanIndexSearch.DeepPageOffset / perPage + 2;
        SegmentSpanSearchRequestDto Request(int page) => new(null, null, page, perPage, sort, direction, null, null, null, null);
        static string Key(SegmentSpanSearchResultItemDto item) => $"{item.VideoId}:{item.Span.StartSec}";

        // The whole list read from the start in pages below the threshold order, as one reference order.
        var index = new SegmentSpanIndexSearch(fixture.Context);
        var profileId = await fixture.Resolver.ResolveProfileIdAsync(null, ct);
        await fixture.Index.EnsureFreshAsync(profileId, TimeSpan.FromMinutes(1), 1000, ct);
        var reference = (await index.SearchAsync(Request(1) with { PerPage = total }, profileId, 1, total, sort, direction != "asc", ct)).Items;
        Assert.Equal(total, reference.Count);

        // A page before the threshold, a deep page still in the near half (counted, read forwards), and
        // pages in the far half (read from the end), the partial last one included.
        Assert.True((firstDeepPage - 1) * perPage < total / 2);
        foreach (var page in new[] { 10, firstDeepPage, pageCount / 2 + 2, pageCount - 1, pageCount })
        {
            var result = await fixture.SearchAsync(Request(page));
            Assert.Equal(reference.Skip((page - 1) * perPage).Take(perPage).Select(Key), result.Items.Select(Key));
            // Browsing in video order only counts once the page is deep enough to be read from the end.
            var counted = sort != "updated_at" || (page - 1) * perPage >= SegmentSpanIndexSearch.DeepPageOffset;
            Assert.Equal(counted ? total : -1, result.TotalCount);
            Assert.Equal(page < pageCount, result.HasMore);
        }

        var beyond = await fixture.SearchAsync(Request(pageCount + 1));
        Assert.Empty(beyond.Items);
        Assert.Equal((total, false), (beyond.TotalCount, beyond.HasMore));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(int.MaxValue, int.MaxValue, int.MinValue)]
    [InlineData(int.MaxValue, 0, -1)]
    [InlineData(123_456, 789, 1_997_909_738)]
    public void ShuffleKey_NeverOverflows_AndStaysA32BitValue(int videoId, int ordinal, int seed)
    {
        var key = SegmentSpanIndexSearch.ShuffleKey(seed).Compile()(new SegmentSpanIndexSearch.SpanRow
        {
            Span = new StoredSegmentSpan { VideoId = videoId, Ordinal = ordinal, SpanKey = "k" },
            Video = new Video(),
        });

        // The same mix in arbitrary precision: equal only if no 64-bit step wrapped.
        var mask = System.Numerics.BigInteger.One << 32;
        var reference = (new System.Numerics.BigInteger(videoId) * 2654435761L + new System.Numerics.BigInteger(ordinal) * 40503L) % mask;
        reference ^= (uint)seed;
        reference = reference * 2146121005L % mask;
        reference ^= reference >> 15;
        reference = reference * 1597334677L % mask;
        reference ^= reference >> 16;
        Assert.Equal(reference, new System.Numerics.BigInteger(key));
        Assert.InRange(key, 0, uint.MaxValue);
    }

    [Fact]
    public async Task RandomOrder_IsStablePerSeed_PagesWithoutRepeats_AndDiffersBetweenSeeds()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        foreach (var title in new[] { "One", "Two", "Three" })
        {
            var video = await fixture.AddVideoAsync(title);
            // Twenty separate spans per video, two of them starting together in different lanes.
            for (var index = 0; index < 20; index++)
                fixture.Context.Segments.Add(Segment(video, index * 100, index * 100 + 30, kind: index % 2 == 0 ? "scene" : "beat"));
        }
        await fixture.Context.SaveChangesAsync(ct);

        SegmentSpanSearchRequestDto Request(int seed, int page, int perPage) =>
            new(null, null, page, perPage, "random", "desc", null, null, null, null, Seed: seed);
        static string Key(SegmentSpanSearchResultItemDto item) => $"{item.VideoId}:{item.Span.SpanKey}";
        async Task<List<string>> AllAsync(int seed) => (await fixture.SearchAsync(Request(seed, 1, 100))).Items.Select(Key).ToList();

        var first = await AllAsync(11);
        Assert.Equal(60, first.Count);
        Assert.Equal(60, first.Distinct().Count());
        Assert.Equal(first, await AllAsync(11));

        // Pages of one seed line up with the whole list.
        var paged = new List<string>();
        for (var page = 1; page <= 4; page++)
            paged.AddRange((await fixture.SearchAsync(Request(11, page, 16))).Items.Select(Key));
        Assert.Equal(first, paged);

        // Another seed is another shuffle, not the same one shifted along.
        var second = await AllAsync(12);
        Assert.Equal(first.Order(), second.Order());
        Assert.NotEqual(first, second);
        var rotated = Enumerable.Range(0, 60).Any(shift => first.Skip(shift).Concat(first.Take(shift)).SequenceEqual(second));
        Assert.False(rotated);

        // Neither order is just video by video.
        Assert.NotEqual(first.OrderBy(key => key, StringComparer.Ordinal), first);
    }

    private static Segment Segment(
        Video video, double startSec, double? endSec, int? tagId = null, string? kind = "tag", long? refId = null,
        string? title = null, string sourceKey = "user", float? confidence = null)
        => new()
        {
            HostType = SegmentHostType.Video,
            HostId = video.Id,
            StartSec = startSec,
            EndSec = endSec,
            TagId = tagId,
            Kind = kind,
            RefId = refId,
            Title = title,
            SourceKey = sourceKey,
            Confidence = confidence,
        };

    [Fact]
    public void IdleChecks_SkipACleanProfile_OnlyWhileNothingHasMoved()
    {
        var checks = new SegmentSpanIndexIdleChecks();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        checks.ObserveVersionChanges(10, start);
        Assert.True(checks.NeedsScan(1, 3, start));

        // The counter has only just been seen: a video could still be settling, so the clean result is not kept.
        checks.RecordClean(1, 3, start);
        Assert.True(checks.NeedsScan(1, 3, start.AddSeconds(1)));

        var quiet = start + SegmentSpanIndexIdleChecks.QuietBeforeTrusted;
        checks.ObserveVersionChanges(10, quiet);
        checks.RecordClean(1, 3, quiet);
        Assert.False(checks.NeedsScan(1, 3, quiet.AddSeconds(5)));
        Assert.True(checks.NeedsScan(1, 4, quiet.AddSeconds(5)));
        Assert.True(checks.NeedsScan(2, 1, quiet.AddSeconds(5)));
        Assert.True(checks.NeedsScan(1, 3, quiet + SegmentSpanIndexIdleChecks.MaxCleanAge));

        checks.ObserveVersionChanges(11, quiet.AddSeconds(6));
        Assert.True(checks.NeedsScan(1, 3, quiet.AddSeconds(6)));

        // Without statistics nothing is ever skipped.
        var blind = new SegmentSpanIndexIdleChecks();
        blind.ObserveVersionChanges(null, start);
        blind.RecordClean(1, 3, start.AddMinutes(5));
        Assert.True(blind.NeedsScan(1, 3, start.AddMinutes(5)));
    }

    [Fact]
    public async Task VideoVersionChangeCounter_MovesWhenASegmentIsWritten()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var video = await fixture.AddVideoAsync("Counted");
        var before = await fixture.Index.ReadVideoVersionChangeCounterAsync(ct);
        Assert.NotNull(before);

        // The writer reports its statistics when it next flushes them; ask it to do so straight away.
        await fixture.Context.Database.OpenConnectionAsync(ct);
        try
        {
            fixture.Context.Segments.Add(Segment(video, 0, 30, kind: "scene"));
            await fixture.Context.SaveChangesAsync(ct);
            await fixture.Context.Database.ExecuteSqlRawAsync("SELECT pg_stat_force_next_flush()", ct);
            await fixture.Context.Database.ExecuteSqlRawAsync("SELECT 1", ct);
        }
        finally
        {
            await fixture.Context.Database.CloseConnectionAsync();
        }

        var after = before;
        for (var attempt = 0; attempt < 50 && after == before; attempt++)
        {
            await Task.Delay(100, ct);
            after = await fixture.Index.ReadVideoVersionChangeCounterAsync(ct);
        }

        Assert.True(after > before, $"The counter stayed at {before}.");
    }

    [Fact]
    public async Task SavingAProfilesRules_AppliesEveryChangeTogether_AndStartsItsBuild()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var editor = fixture.ForPrincipal(Permissions.SegmentsRead, Permissions.SegmentsWrite, Permissions.SegmentsDelete);
        var ct = TestContext.Current.CancellationToken;
        var (profile, kept, edited, removed) = await AddProfileWithRulesAsync(fixture);
        var version = await ProfileVersionAsync(fixture, profile.Id);
        var keptUpdatedAt = (await fixture.Context.SegmentDisplayRules.AsNoTracking().SingleAsync(rule => rule.Id == kept.Id, ct)).UpdatedAt;

        var saved = SavedRules(await editor.ProfilesController().ReplaceRules(profile.Id, new SegmentDisplayRulesReplaceDto(version,
        [
            RuleItem(kept.Id, "kept", 3),
            RuleItem(edited.Id, "edited", 2, minDurationSec: 5),
            RuleItem(null, "added", 1),
        ]), editor.Index, new SegmentSpanIndexSignal(), ct));

        Assert.True(saved.Changed);
        Assert.Equal(["kept", "edited", "added"], saved.Rules.Select(rule => rule.SourceKey));
        Assert.Equal(5, saved.Rules.Single(rule => rule.Id == edited.Id).MinDurationSec);
        Assert.All(saved.Rules, rule => Assert.Equal(42, rule.UserId));
        Assert.DoesNotContain(saved.Rules, rule => rule.Id == removed.Id);
        Assert.True(saved.Profile.Version > version);
        Assert.Equal(saved.Profile.Version, await ProfileVersionAsync(fixture, profile.Id));
        // A rule whose values did not change is not rewritten.
        Assert.Equal(keptUpdatedAt, (await fixture.Context.SegmentDisplayRules.AsNoTracking().SingleAsync(rule => rule.Id == kept.Id, ct)).UpdatedAt);
        // Saving starts the profile's build instead of waiting for it to be opened.
        Assert.True(await fixture.Context.SegmentSpanIndexedProfiles.AsNoTracking().AnyAsync(indexed => indexed.ProfileId == profile.Id, ct));

        // Saving the same list again changes nothing, so the profile is not rebuilt.
        var again = SavedRules(await editor.ProfilesController().ReplaceRules(profile.Id, new SegmentDisplayRulesReplaceDto(saved.Profile.Version,
            saved.Rules.Select(rule => RuleItem(rule.Id, rule.SourceKey!, rule.Priority!.Value, rule.MinDurationSec)).ToList()), editor.Index, new SegmentSpanIndexSignal(), ct));
        Assert.False(again.Changed);
        Assert.Equal(saved.Profile.Version, again.Profile.Version);

        // A list edited from an older version is refused and changes nothing.
        var stale = await editor.ProfilesController().ReplaceRules(profile.Id, new SegmentDisplayRulesReplaceDto(version, []), editor.Index, new SegmentSpanIndexSignal(), ct);
        Assert.Equal(409, Assert.IsType<ConflictObjectResult>(stale.Result).StatusCode);
        Assert.Equal(3, await fixture.Context.SegmentDisplayRules.AsNoTracking().CountAsync(rule => rule.ProfileId == profile.Id, ct));
    }

    [Fact]
    public async Task SavingAProfilesRules_RefusesUnknownOrRepeatedRules_AndDroppingRulesWithoutDeletePermission()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var writer = fixture.ForPrincipal(Permissions.SegmentsRead, Permissions.SegmentsWrite);
        var ct = TestContext.Current.CancellationToken;
        var (profile, kept, edited, removed) = await AddProfileWithRulesAsync(fixture);
        var version = await ProfileVersionAsync(fixture, profile.Id);
        var other = new SegmentDisplayProfile { Name = "Other", UserId = 42, Version = 1 };
        fixture.Context.SegmentDisplayProfiles.Add(other);
        await fixture.Context.SaveChangesAsync(ct);
        var otherRule = new SegmentDisplayRule { ProfileId = other.Id, UserId = 42, SourceKey = "other", Visible = true };
        fixture.Context.SegmentDisplayRules.Add(otherRule);
        await fixture.Context.SaveChangesAsync(ct);
        List<SegmentDisplayRuleReplaceItemDto> all = [RuleItem(kept.Id, "kept", 3), RuleItem(edited.Id, "edited", 2), RuleItem(removed.Id, "removed", 1)];

        var dropping = await writer.ProfilesController().ReplaceRules(profile.Id, new SegmentDisplayRulesReplaceDto(version, all[..2]), writer.Index, new SegmentSpanIndexSignal(), ct);
        Assert.IsType<ForbidResult>(dropping.Result);
        var repeated = await writer.ProfilesController().ReplaceRules(profile.Id, new SegmentDisplayRulesReplaceDto(version, [.. all, RuleItem(kept.Id, "again", 0)]), writer.Index, new SegmentSpanIndexSignal(), ct);
        Assert.IsType<BadRequestObjectResult>(repeated.Result);
        var foreign = await writer.ProfilesController().ReplaceRules(profile.Id, new SegmentDisplayRulesReplaceDto(version, [.. all, RuleItem(otherRule.Id, "other", 0)]), writer.Index, new SegmentSpanIndexSignal(), ct);
        Assert.IsType<BadRequestObjectResult>(foreign.Result);
        Assert.Equal(version, await ProfileVersionAsync(fixture, profile.Id));

        // Adding and editing need only write permission.
        var added = SavedRules(await writer.ProfilesController().ReplaceRules(profile.Id, new SegmentDisplayRulesReplaceDto(version, [.. all, RuleItem(null, "added", 0)]), writer.Index, new SegmentSpanIndexSignal(), ct));
        Assert.Equal(4, added.Rules.Count);
    }

    private static async Task<(SegmentDisplayProfile Profile, SegmentDisplayRule Kept, SegmentDisplayRule Edited, SegmentDisplayRule Removed)> AddProfileWithRulesAsync(Fixture fixture)
    {
        var ct = TestContext.Current.CancellationToken;
        // The user the fixture's principals act as.
        fixture.Context.Users.Add(new User { Id = 42, Username = "reader", PasswordHash = "test" });
        var profile = new SegmentDisplayProfile { Name = "Mine", UserId = 42, Version = 1 };
        fixture.Context.SegmentDisplayProfiles.Add(profile);
        await fixture.Context.SaveChangesAsync(ct);
        var kept = new SegmentDisplayRule { ProfileId = profile.Id, UserId = 42, SourceKey = "kept", Visible = true, Priority = 3 };
        var edited = new SegmentDisplayRule { ProfileId = profile.Id, UserId = 42, SourceKey = "edited", Visible = true, Priority = 2 };
        var removed = new SegmentDisplayRule { ProfileId = profile.Id, UserId = 42, SourceKey = "removed", Visible = true, Priority = 1 };
        fixture.Context.SegmentDisplayRules.AddRange(kept, edited, removed);
        await fixture.Context.SaveChangesAsync(ct);
        return (profile, kept, edited, removed);
    }

    private static Task<int> ProfileVersionAsync(Fixture fixture, int profileId)
        => fixture.Context.SegmentDisplayProfiles.AsNoTracking().Where(profile => profile.Id == profileId).Select(profile => profile.Version).SingleAsync(TestContext.Current.CancellationToken);

    private static SegmentDisplayRuleReplaceItemDto RuleItem(int? id, string sourceKey, int priority, double? minDurationSec = null)
        => new(id, sourceKey, null, null, null, null, true, null, minDurationSec, null, false, null, null, priority);

    private static SegmentDisplayRulesSavedDto SavedRules(ActionResult<SegmentDisplayRulesSavedDto> result)
        => Assert.IsType<SegmentDisplayRulesSavedDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    /// <summary>One database, with a context, resolver, index service and controller for one reader.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly CovePostgresDatabase _database;
        private readonly bool _ownsDatabase;
        private readonly CurrentPrincipalAccessor _accessor;

        private Fixture(CovePostgresDatabase database, CovePrincipal? principal, bool ownsDatabase)
        {
            _database = database;
            _ownsDatabase = ownsDatabase;
            var accessor = new CurrentPrincipalAccessor();
            if (principal is not null)
                accessor.Set(principal);
            _accessor = accessor;
            Context = new CoveContext(database.CreateOptions(), accessor);
            Resolver = new SegmentSpanResolver(Context, accessor, new MemoryCache(new MemoryCacheOptions()));
            Index = new SegmentSpanIndexService(Context, Resolver, new SegmentSpanIndexSignal());
            Controller = new SegmentsController(
                Context,
                new SegmentRepository(Context),
                Resolver,
                new MemoryCache(new MemoryCacheOptions()),
                Index,
                new SegmentSpanIndexSearch(Context));
        }

        public CoveContext Context { get; }
        public SegmentSpanResolver Resolver { get; }
        public SegmentSpanIndexService Index { get; }
        public SegmentsController Controller { get; }

        public SegmentDisplayProfilesController ProfilesController() => new(Context, Resolver, _accessor);

        /// <summary>The same database as the given principal sees it.</summary>
        public Fixture ForPrincipal(CovePrincipal principal) => new(_database, principal, ownsDatabase: false);

        public static async Task<Fixture> CreateAsync()
            => new(await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken), principal: null, ownsDatabase: true);

        /// <summary>
        /// The same database as another reader sees it: with exactly these permissions, or with no
        /// restrictions at all when none are given.
        /// </summary>
        public Fixture ForPrincipal(params string[] permissions)
            => new(
                _database,
                permissions.Length == 0
                    ? null
                    : new CovePrincipal
                    {
                        UserId = 42,
                        Username = "reader",
                        Kind = PrincipalKind.User,
                        Roles = new HashSet<string>(),
                        Permissions = new HashSet<string>(permissions),
                    },
                ownsDatabase: false);

        public async Task<Video> AddVideoAsync(string title)
        {
            var video = new Video { Title = title };
            Context.Videos.Add(video);
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return video;
        }

        public async Task<SegmentSpanSearchResponseDto> SearchAsync(SegmentSpanSearchRequestDto? request = null)
        {
            var result = await Controller.SearchSpans(
                request ?? new SegmentSpanSearchRequestDto(null, null, 1, 50, null, null, null, null, null, null),
                TestContext.Current.CancellationToken);
            return Assert.IsType<SegmentSpanSearchResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        }

        public async Task<SegmentSpanCountResponseDto> CountAsync(SegmentSpanSearchRequestDto? request = null)
        {
            var result = await Controller.CountSpans(
                request ?? new SegmentSpanSearchRequestDto(null, null, null, null, null, null, null, null, null, null),
                TestContext.Current.CancellationToken);
            return Assert.IsType<SegmentSpanCountResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            if (_ownsDatabase)
                await _database.DisposeAsync();
        }
    }
}
