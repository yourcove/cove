using System.Net.Http.Json;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests.Integration;

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
        var response = await client.PostAsJsonAsync($"/api/groups/{groupId}/items/from-spans", request);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadApiJsonAsync<List<GroupItemDto>>();
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
        var response = await client.GetAsync($"/api/videos/{videoId}/segments");
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadApiJsonAsync<List<SegmentDto>>();
        Assert.NotNull(payload);
        Assert.Single(payload);
    }
}

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

        var listResponse = await client.GetAsync("/api/segment-display-profiles");
        listResponse.EnsureSuccessStatusCode();
        var profiles = await listResponse.Content.ReadApiJsonAsync<List<SegmentDisplayProfileDto>>();
        Assert.NotNull(profiles);
        Assert.Equal(2, profiles.Count);
        var globalDefaultProfile = Assert.Single(profiles, profile => profile.Name == "Default" && profile.UserId == null && profile.IsDefault);
        Assert.Single(profiles, profile => profile.Name == "Raw" && profile.UserId == null);

        var globalRulesResponse = await client.GetAsync($"/api/segment-display-profiles/{globalDefaultProfile.Id}/rules");
        globalRulesResponse.EnsureSuccessStatusCode();
        var globalRules = await globalRulesResponse.Content.ReadApiJsonAsync<List<SegmentDisplayRuleDto>>();
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
            ]));
        previewResponse.EnsureSuccessStatusCode();

        var preview = await previewResponse.Content.ReadApiJsonAsync<ResolvedSpanListDto>();
        Assert.NotNull(preview);
        Assert.Single(preview.Spans);
    }
}

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

        var listResponse = await client.GetAsync($"/api/segments?videoId={videoId}&page=1&perPage=20");
        listResponse.EnsureSuccessStatusCode();
        var listPayload = await listResponse.Content.ReadApiJsonAsync<PaginatedResponse<SegmentRecordDto>>();
        Assert.NotNull(listPayload);
        Assert.Equal(2, listPayload.TotalCount);

        var distinctsResponse = await client.GetAsync("/api/segments/source-keys/distinct");
        distinctsResponse.EnsureSuccessStatusCode();
        var distincts = await distinctsResponse.Content.ReadApiJsonAsync<List<SegmentDistinctValueDto>>();
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
            null));
        spansResponse.EnsureSuccessStatusCode();
        var spansPayload = await spansResponse.Content.ReadApiJsonAsync<SegmentSpanSearchResponseDto>();
        Assert.NotNull(spansPayload);
        Assert.Equal(2, spansPayload.Items.Count);
        Assert.Contains(spansPayload.Items, item => item.Span.SourceKey == "user");
        Assert.Contains(spansPayload.Items, item => item.Span.SourceKey == "ext:ai.actions");
    }
}

