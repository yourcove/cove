using System.Net.Http.Json;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests.Integration;

[RequiresPostgres]
public sealed class GroupItemsControllerSmokeTests
{
    [Fact]
    public async Task CreateFromSpans_DerivedBranch_ReturnsOk()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();

        var (groupId, videoId) = await factory.WithDbContextAsync(async db =>
        {
            var video = new Video { Title = "Explicit Derived Query Video", MaxDuration = 120 };
            var group = new Group { Name = "Explicit Derived Query Group" };
            db.Videos.Add(video);
            db.Groups.Add(group);
            await db.SaveChangesAsync();

            db.Segments.AddRange(
                new Segment
                {
                    HostType = SegmentHostType.Video,
                    HostId = video.Id,
                    StartSec = 10,
                    EndSec = 12,
                    Kind = "face",
                    SourceKey = "ext:ai.faces",
                },
                new Segment
                {
                    HostType = SegmentHostType.Video,
                    HostId = video.Id,
                    StartSec = 11,
                    EndSec = 13,
                    Kind = "user.face",
                    SourceKey = "user",
                });
            await db.SaveChangesAsync();
            return (group.Id, video.Id);
        });

        var request = new GroupItemsFromSpansDto([
            new GroupItemSpanInputDto(
                null,
                videoId,
                null,
                null,
                "Intersection snapshot",
                null,
                new SegmentSpanDerivedQueryDto(
                    "intersection",
                    [
                        new SegmentSpanOperandDto("ext:ai.faces", null, null, null),
                        new SegmentSpanOperandDto("user", null, null, null),
                    ],
                    0,
                    0))
        ]);

        using var client = factory.CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync($"/api/groups/{groupId}/items/from-spans", request, cancellationToken: TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadApiJsonAsync<List<GroupItemDto>>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(payload);
        var item = Assert.Single(payload);
        Assert.Equal(GroupItemKind.VideoRange, item.Kind);
        Assert.StartsWith("dq-intersection-", item.SourceSpanKey, StringComparison.Ordinal);

        await factory.WithDbContextAsync(async db =>
        {
            Assert.Single(await db.GroupItems.ToListAsync());
        });
    }
}

[RequiresPostgres]
public sealed class VideoSegmentsControllerSmokeTests
{
    [Fact]
    public async Task List_ReturnsOk()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();

        var videoId = await factory.WithDbContextAsync(async db =>
        {
            var video = new Video { Title = "Segment Video" };
            db.Videos.Add(video);
            await db.SaveChangesAsync();

            db.Segments.Add(new Segment
            {
                HostType = SegmentHostType.Video,
                HostId = video.Id,
                StartSec = 12.5,
                EndSec = 18.25,
                Kind = "face",
                SourceKey = "ext:ai.faces",
                SourceRunId = "run-1",
                Confidence = 0.96f,
                Title = "Lead face",
                ColorHint = "#ffaa00",
            });
            await db.SaveChangesAsync();
            return video.Id;
        });

        using var client = factory.CreateAuthenticatedClient();
        var response = await client.GetAsync($"/api/videos/{videoId}/segments", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadApiJsonAsync<List<SegmentDto>>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(payload);
        Assert.Single(payload);
    }
}

[RequiresPostgres]
public sealed class SegmentDisplayProfilesControllerSmokeTests
{
    [Fact]
    public async Task List_And_Preview_ReturnOk()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();

        var videoId = await factory.WithDbContextAsync(async db =>
        {
            var video = new Video { Title = "Preview Video" };
            var tag = new Tag { Name = "Highlight" };
            db.AddRange(video, tag);
            await db.SaveChangesAsync();

            db.Segments.Add(new Segment
            {
                HostType = SegmentHostType.Video,
                HostId = video.Id,
                StartSec = 3,
                EndSec = 9,
                TagId = tag.Id,
                Kind = "action",
                SourceKey = "ext:ai.actions",
            });
            await db.SaveChangesAsync();
            return video.Id;
        });

        using var client = factory.CreateAuthenticatedClient();

        var listResponse = await client.GetAsync("/api/segment-display-profiles", TestContext.Current.CancellationToken);
        listResponse.EnsureSuccessStatusCode();
        var profiles = await listResponse.Content.ReadApiJsonAsync<List<SegmentDisplayProfileDto>>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(profiles);
        Assert.Equal(2, profiles.Count);
        var globalDefaultProfile = Assert.Single(profiles, profile => profile.Name == "Default" && profile.UserId == null && profile.IsDefault);
        Assert.Single(profiles, profile => profile.Name == "Raw" && profile.UserId == null);

        var globalRulesResponse = await client.GetAsync($"/api/segment-display-profiles/{globalDefaultProfile.Id}/rules", TestContext.Current.CancellationToken);
        globalRulesResponse.EnsureSuccessStatusCode();
        var globalRules = await globalRulesResponse.Content.ReadApiJsonAsync<List<SegmentDisplayRuleDto>>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(globalRules);
        var globalDefaultRule = Assert.Single(globalRules);
        Assert.Equal(SegmentHostType.Video, globalDefaultRule.HostType);
        Assert.True(globalDefaultRule.Visible);
        Assert.Equal(10, globalDefaultRule.MinDurationSec);
        Assert.Equal(8, globalDefaultRule.MergeGapSec);

        var previewResponse = await client.PostAsJsonAsync("/api/segment-display-profiles/preview", new SegmentDisplayProfilePreviewRequestDto(
            videoId,
            [
                new SegmentDisplayRuleCreateDto(
                    "ext:ai.actions",
                    "action",
                    null,
                    null,
                    SegmentHostType.Video,
                    true,
                    null,
                    null,
                    0,
                    false,
                    "#33ccaa",
                    2,
                    null),
            ]), cancellationToken: TestContext.Current.CancellationToken);
        previewResponse.EnsureSuccessStatusCode();

        var preview = await previewResponse.Content.ReadApiJsonAsync<ResolvedSpanListDto>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(preview);
        Assert.Single(preview.Spans);
    }
}

[RequiresPostgres]
public sealed class SegmentsControllerSmokeTests
{
    [Fact]
    public async Task List_Distincts_And_SpansSearch_ReturnOk()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();

        var (videoId, profileId) = await factory.WithDbContextAsync(async db =>
        {
            var video = new Video
            {
                Title = "Library Video",
                MaxDuration = 120,
                UpdatedAt = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            };
            db.Videos.Add(video);
            await db.SaveChangesAsync();

            var profile = new SegmentDisplayProfile
            {
                Name = "Search Profile",
                UserId = CoveWebApplicationFactory.TestUserId,
                IsDefault = true,
                Version = 1,
            };
            db.SegmentDisplayProfiles.Add(profile);
            await db.SaveChangesAsync();

            db.SegmentDisplayRules.Add(new SegmentDisplayRule
            {
                ProfileId = profile.Id,
                UserId = CoveWebApplicationFactory.TestUserId,
                SourceKey = "user",
                Visible = true,
            });
            db.Segments.AddRange(
                new Segment
                {
                    HostType = SegmentHostType.Video,
                    HostId = video.Id,
                    StartSec = 5,
                    EndSec = 7,
                    Kind = "clip",
                    Title = "User span",
                    SourceKey = "user",
                },
                new Segment
                {
                    HostType = SegmentHostType.Video,
                    HostId = video.Id,
                    StartSec = 10,
                    EndSec = 14,
                    Kind = "action",
                    Title = "AI span",
                    SourceKey = "ext:ai.actions",
                });
            await db.SaveChangesAsync();

            return (video.Id, profile.Id);
        });

        using var client = factory.CreateAuthenticatedClient();

        var listResponse = await client.GetAsync($"/api/segments?videoId={videoId}&page=1&perPage=20", TestContext.Current.CancellationToken);
        listResponse.EnsureSuccessStatusCode();
        var listPayload = await listResponse.Content.ReadApiJsonAsync<PaginatedResponse<SegmentRecordDto>>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(listPayload);
        Assert.Equal(2, listPayload.TotalCount);

        var distinctsResponse = await client.GetAsync("/api/segments/source-keys/distinct", TestContext.Current.CancellationToken);
        distinctsResponse.EnsureSuccessStatusCode();
        var distincts = await distinctsResponse.Content.ReadApiJsonAsync<List<SegmentDistinctValueDto>>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(distincts);
        Assert.Contains(distincts, item => item.Value == "user");

        var spansResponse = await client.PostAsJsonAsync("/api/segments/spans/search", new SegmentSpanSearchRequestDto(
            profileId,
            null,
            1,
            10,
            "title",
            "asc",
            null,
            null,
            [videoId],
            null), cancellationToken: TestContext.Current.CancellationToken);
        spansResponse.EnsureSuccessStatusCode();
        var spansPayload = await spansResponse.Content.ReadApiJsonAsync<SegmentSpanSearchResponseDto>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(spansPayload);
        Assert.Equal(2, spansPayload.Items.Count);
        Assert.Contains(spansPayload.Items, item => item.Span.SourceKey == "user");
        Assert.Contains(spansPayload.Items, item => item.Span.SourceKey == "ext:ai.actions");
    }

    // Every span of these queries is cut from the same long tag segment, so they all share their first segment id, and
    // each span of the difference has no other; random order must still shuffle them rather than fall back to start time.
    [Theory]
    [InlineData("intersection")]
    [InlineData("difference")]
    public async Task SpansSearch_RandomSort_ShufflesDerivedSpansThatShareASegment(string queryOperator)
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();

        var (videoId, profileId) = await factory.WithDbContextAsync(async db =>
        {
            var video = new Video { Title = "Shared Segment Video", MaxDuration = 120 };
            db.Videos.Add(video);
            var profile = new SegmentDisplayProfile
            {
                Name = "Random Profile",
                UserId = CoveWebApplicationFactory.TestUserId,
                IsDefault = true,
                Version = 1,
            };
            db.SegmentDisplayProfiles.Add(profile);
            await db.SaveChangesAsync();

            db.Segments.Add(new Segment
            {
                HostType = SegmentHostType.Video,
                HostId = video.Id,
                StartSec = 0,
                EndSec = 100,
                Kind = "tag",
                SourceKey = "user",
            });
            await db.SaveChangesAsync();
            db.Segments.AddRange(Enumerable.Range(1, 8).Select(index => new Segment
            {
                HostType = SegmentHostType.Video,
                HostId = video.Id,
                StartSec = index * 10,
                EndSec = index * 10 + 2,
                Kind = "face",
                SourceKey = "ext:ai.faces",
            }));
            await db.SaveChangesAsync();
            return (video.Id, profile.Id);
        });

        using var client = factory.CreateAuthenticatedClient();
        async Task<List<double>> SearchStartsAsync(int seed)
        {
            var response = await client.PostAsJsonAsync("/api/segments/spans/search", new SegmentSpanSearchRequestDto(
                profileId,
                new SegmentSpanDerivedQueryDto(
                    queryOperator,
                    [
                        new SegmentSpanOperandDto(null, "tag", null, null),
                        new SegmentSpanOperandDto(null, "face", null, null),
                    ],
                    null,
                    null),
                1,
                20,
                "random",
                "desc",
                null,
                null,
                [videoId],
                null,
                Seed: seed), cancellationToken: TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadApiJsonAsync<SegmentSpanSearchResponseDto>(cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(payload);
            return payload.Items.Select(item => item.Span.StartSec).ToList();
        }

        var orders = new List<List<double>>();
        foreach (var seed in new[] { 11, 22, 33, 44 })
        {
            orders.Add(await SearchStartsAsync(seed));
        }

        var byStart = orders[0].Order().ToList();
        Assert.All(orders, order => Assert.Equal(byStart, order.Order().ToList()));
        Assert.True(byStart.Count >= 8);
        Assert.Contains(orders, order => !order.SequenceEqual(byStart) && !order.SequenceEqual(Enumerable.Reverse(byStart)));
        Assert.Equal(orders[0], await SearchStartsAsync(11));
    }
}

