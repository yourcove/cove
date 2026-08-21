using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Entities.Segments;

[Collection(ApiTestLane2Collection.Name)]
public sealed class SegmentSpanLifecycleApiTests(ITestOutputHelper output, CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/videos/{videoid:int}/segments/spans")]
    [CoversEndpoint("POST", "/api/videos/{videoid:int}/segments/spans/query")]
    [CoversEndpoint("GET", "/api/videos/{videoid:int}/spans/{spankey}")]
    [CoversEndpoint("GET", "/api/videos/{videoid:int}/segments/{id:int}")]
    [CoversEndpoint("PUT", "/api/videos/{videoid:int}/segments/{id:int}")]
    [CoversEndpoint("DELETE", "/api/videos/{videoid:int}/segments/{id:int}")]
    public async Task GivenResolvedVideoSpans_WhenMemberReadsQueriesUpdatesAndDeletes_ThenMetadataDetailsAndCachesStayCurrent()
    {
        var eva = AsUser(ApiTestUsers.Eva);
        var suffix = Guid.NewGuid().ToString("N");
        var video = await AsUser().CreateVideoAsync($"Span lifecycle {suffix}");
        var tag = await AsUser().CreateTagAsync($"Span tag {suffix}");
        var profile = await eva.CreateSegmentDisplayProfileAsync(new SegmentDisplayProfileCreateDto($"Span profile {suffix}", null, false));
        await eva.CreateSegmentDisplayRuleAsync(profile.Id, new SegmentDisplayRuleCreateDto("span-slice", "chapter", tag.Id, null, SegmentHostType.Video, true, null, null, 1, false, "#224466", 3, 100));
        var first = await AsUser().CreateVideoSegmentAsync(video, Segment(2, 4, tag.Id, "chapter", "span-slice", "First"));
        var second = await AsUser().CreateVideoSegmentAsync(video, Segment(4.5, 7, tag.Id, "chapter", "span-slice", "Second"));

        var resolved = await eva.GetVideoResolvedSpansAsync(video, profile.Id);
        var span = resolved.Spans.Should().ContainSingle().Which;
        span.StartSec.Should().Be(2);
        span.EndSec.Should().Be(7);
        span.TagId.Should().Be(tag.Id);
        span.TagName.Should().Be(tag.Name);
        span.ColorHint.Should().Be("#224466");
        span.Lane.Should().Be(3);
        span.SegmentIds.Should().Equal(first.Id, second.Id);
        var detail = await eva.GetVideoResolvedSpanDetailAsync(video, span.SpanKey, profile.Id);
        detail.VideoId.Should().Be(video.Id);
        detail.VideoTitle.Should().Be(video.Title);
        detail.ProfileId.Should().Be(profile.Id);
        detail.Span.SegmentIds.Should().Equal(first.Id, second.Id);
        detail.Intervals.Should().Equal(new ResolvedSpanIntervalDto(2, 4), new ResolvedSpanIntervalDto(4.5, 7));

        var derived = await eva.QueryVideoResolvedSpansAsync(video, new SegmentSpanQueryRequestDto(profile.Id, "union", [new SegmentSpanOperandDto("span-slice", "chapter", [tag.Id], null)], 0, 0));
        derived.Spans.Select(item => (item.StartSec, item.EndSec)).Should().Equal((2, 4), (4.5, 7));
        derived.Spans.Select(item => item.SegmentIds.Single()).Should().Equal(first.Id, second.Id);

        var update = new SegmentUpdateDto(10, 14, tag.Id, "chapter", 42, Json("{\"source\":\"api\"}"), "span-slice", "run-1", .8f, "Updated segment", "#abcdef");
        var updated = await eva.UpdateVideoSegmentAsync(video, first.Id, update);
        var persisted = await eva.GetVideoSegmentAsync(video, first.Id);
        foreach (var actual in new[] { updated, persisted })
        {
            actual.Id.Should().Be(first.Id);
            actual.HostType.Should().Be(SegmentHostType.Video);
            actual.HostId.Should().Be(video.Id);
            actual.StartSec.Should().Be(10);
            actual.EndSec.Should().Be(14);
            actual.TagId.Should().Be(tag.Id);
            actual.TagName.Should().Be(tag.Name);
            actual.Kind.Should().Be("chapter");
            actual.RefId.Should().Be(42);
            actual.Payload.HasValue.Should().BeTrue();
            actual.Payload!.Value.GetProperty("source").GetString().Should().Be("api");
            actual.SourceKey.Should().Be("span-slice");
            actual.SourceRunId.Should().Be("run-1");
            actual.Confidence.Should().Be(.8f);
            actual.Title.Should().Be("Updated segment");
            actual.ColorHint.Should().Be("#abcdef");
        }

        var global = await eva.GetSegmentByIdAsync(first.Id);
        global.HostType.Should().Be(persisted.HostType);
        global.HostId.Should().Be(persisted.HostId);
        global.StartSec.Should().Be(persisted.StartSec);
        global.EndSec.Should().Be(persisted.EndSec);
        global.TagId.Should().Be(persisted.TagId);
        global.TagName.Should().Be(persisted.TagName);
        global.Kind.Should().Be(persisted.Kind);
        global.RefId.Should().Be(persisted.RefId);
        global.Payload.HasValue.Should().BeTrue();
        global.Payload!.Value.GetProperty("source").GetString().Should().Be("api");
        global.SourceKey.Should().Be(persisted.SourceKey);
        global.SourceRunId.Should().Be(persisted.SourceRunId);
        global.Confidence.Should().Be(persisted.Confidence);
        global.Title.Should().Be(persisted.Title);
        global.ColorHint.Should().Be(persisted.ColorHint);
        var afterUpdate = await eva.GetVideoResolvedSpansAsync(video, profile.Id);
        afterUpdate.Spans.Select(item => (item.StartSec, item.EndSec)).Should().Equal((4.5, 7), (10, 14));
        afterUpdate.Spans[0].SegmentIds.Should().Equal(second.Id);
        afterUpdate.Spans[1].SegmentIds.Should().Equal(first.Id);

        await eva.DeleteVideoSegmentAsync(video, first.Id);
        var afterDelete = await eva.GetVideoResolvedSpansAsync(video, profile.Id);
        afterDelete.Spans.Should().ContainSingle().Which.SegmentIds.Should().Equal(second.Id);
        var deleted = () => eva.GetVideoSegmentAsync(video, first.Id);
        await deleted.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    [CoversEndpoint("POST", "/api/segments/spans/search")]
    [CoversEndpoint("POST", "/api/segments/spans/count")]
    public async Task GivenScopedResolvedSpans_WhenMemberSearchesAndCounts_ThenExactSetAndDurationAreReturned()
    {
        var eva = AsUser(ApiTestUsers.Eva);
        var suffix = Guid.NewGuid().ToString("N");
        var profile = await eva.CreateSegmentDisplayProfileAsync(new SegmentDisplayProfileCreateDto($"Search span profile {suffix}", null, false));
        await eva.CreateSegmentDisplayRuleAsync(profile.Id, new SegmentDisplayRuleCreateDto("span-search", "chapter", null, null, SegmentHostType.Video, true, null, null, 0, false, null, 1, 100));
        var firstVideo = await AsUser().CreateVideoAsync($"First scoped span {suffix}");
        var secondVideo = await AsUser().CreateVideoAsync($"Second scoped span {suffix}");
        var excludedVideo = await AsUser().CreateVideoAsync($"Excluded scoped span {suffix}");
        await AsUser().CreateVideoSegmentAsync(firstVideo, Segment(1, 4, null, "chapter", "span-search", "First"));
        await AsUser().CreateVideoSegmentAsync(secondVideo, Segment(10, 16, null, "chapter", "span-search", "Second"));
        await AsUser().CreateVideoSegmentAsync(excludedVideo, Segment(20, 29, null, "chapter", "span-search", "Excluded"));
        var request = new SegmentSpanSearchRequestDto(profile.Id, null, 1, 10, "start_sec", "asc", null, null, [firstVideo.Id, secondVideo.Id], null, null, "chapter", "span-search");

        var search = await eva.SearchResolvedSpansAsync(request);
        var count = await eva.CountResolvedSpansAsync(request);

        search.TotalCount.Should().Be(2);
        search.Page.Should().Be(1);
        search.PerPage.Should().Be(10);
        search.HasMore.Should().BeFalse();
        search.Items.Select(item => item.VideoId).Should().Equal(firstVideo.Id, secondVideo.Id);
        search.Items.Select(item => (item.Span.StartSec, item.Span.EndSec)).Should().Equal((1, 4), (10, 16));
        count.Should().Be(new SegmentSpanCountResponseDto(2, 9));
    }

    private static SegmentCreateDto Segment(double start, double end, int? tagId, string kind, string sourceKey, string title)
        => new(start, end, tagId, kind, null, null, sourceKey, null, .8f, title, null);

    private static global::System.Text.Json.JsonElement Json(string value)
        => global::System.Text.Json.JsonDocument.Parse(value).RootElement.Clone();
}
