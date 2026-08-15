using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Entities.SegmentDisplayProfiles;

[Collection(ApiTestLane2Collection.Name)]
public sealed class SegmentDisplayProfilesApiTests(ITestOutputHelper output, CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/segment-display-profiles")]
    [CoversEndpoint("GET", "/api/segment-display-profiles/{id:int}")]
    [CoversEndpoint("PUT", "/api/segment-display-profiles/{id:int}")]
    [CoversEndpoint("PUT", "/api/segment-display-profiles/{id:int}/default")]
    [CoversEndpoint("DELETE", "/api/segment-display-profiles/{id:int}")]
    public async Task GivenPersonalProfiles_WhenMemberManagesDefaults_ThenOwnershipAndFallbackAreEnforced()
    {
        var eva = AsUser(ApiTestUsers.Eva);
        var first = await eva.CreateSegmentDisplayProfileAsync(new SegmentDisplayProfileCreateDto("  First profile  ", "  First description  ", false));
        var second = await eva.CreateSegmentDisplayProfileAsync(new SegmentDisplayProfileCreateDto("Second profile", null, false));
        first.Name.Should().Be("First profile");
        first.Description.Should().Be("First description");
        first.UserId.Should().NotBeNull();
        first.IsSystem.Should().BeFalse();
        first.IsDefault.Should().BeTrue();
        first.Version.Should().Be(1);
        DateTimeOffset.TryParse(first.CreatedAt, out _).Should().BeTrue();
        DateTimeOffset.TryParse(first.UpdatedAt, out _).Should().BeTrue();
        second.UserId.Should().Be(first.UserId);
        second.IsDefault.Should().BeFalse();

        var updated = await eva.UpdateSegmentDisplayProfileAsync(first.Id, new SegmentDisplayProfileUpdateDto("  Renamed profile  ", "  Updated description  "));
        updated.Name.Should().Be("Renamed profile");
        updated.Description.Should().Be("Updated description");
        var persistedUpdated = await eva.GetSegmentDisplayProfileAsync(first.Id);
        persistedUpdated.Name.Should().Be("Renamed profile");
        persistedUpdated.Description.Should().Be("Updated description");
        (await eva.SetDefaultSegmentDisplayProfileAsync(second.Id)).IsDefault.Should().BeTrue();
        (await eva.GetSegmentDisplayProfilesAsync()).Where(profile => profile.UserId == first.UserId).Should().ContainSingle(profile => profile.IsDefault).Which.Id.Should().Be(second.Id);

        await eva.DeleteSegmentDisplayProfileAsync(second.Id);
        (await eva.GetSegmentDisplayProfileAsync(first.Id)).IsDefault.Should().BeTrue();
        var deleted = () => eva.GetSegmentDisplayProfileAsync(second.Id);
        var blank = () => eva.CreateSegmentDisplayProfileAsync(new SegmentDisplayProfileCreateDto("  ", null, false));
        var crossRead = () => AsUser(ApiTestUsers.Anthony).GetSegmentDisplayProfileAsync(first.Id);
        var crossWrite = () => AsUser(ApiTestUsers.Anthony).UpdateSegmentDisplayProfileAsync(first.Id, new SegmentDisplayProfileUpdateDto("No", null));
        await deleted.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        await blank.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        await crossRead.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        await crossWrite.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    [CoversEndpoint("GET", "/api/segment-display-profiles/{profileid:int}/rules")]
    [CoversEndpoint("POST", "/api/segment-display-profiles/{profileid:int}/rules")]
    [CoversEndpoint("POST", "/api/segment-display-profiles/{profileid:int}/rules/bulk")]
    [CoversEndpoint("PUT", "/api/segment-display-profiles/{profileid:int}/rules/{id:int}")]
    [CoversEndpoint("DELETE", "/api/segment-display-profiles/{profileid:int}/rules/{id:int}")]
    public async Task GivenPersonalProfile_WhenMemberManagesRules_ThenRulesAreOrderedVersionedAndOwned()
    {
        var eva = AsUser(ApiTestUsers.Eva);
        var profile = await eva.CreateSegmentDisplayProfileAsync(new SegmentDisplayProfileCreateDto("Rules profile", null, false));
        var tag = await AsUser().CreateTagAsync($"Display rule tag {Guid.NewGuid():N}");
        var rich = new SegmentDisplayRuleCreateDto("  detector  ", "  chapter  ", tag.Id, "  category  ", SegmentHostType.Video, true, 0.8f, 2.5, 0.5, false, "  #aabbcc  ", 3, 50);
        var created = await eva.CreateSegmentDisplayRuleAsync(profile.Id, rich);
        created.ShouldMatch(rich, tag.Name, profile.UserId);
        DateTimeOffset.TryParse(created.CreatedAt, out _).Should().BeTrue();
        DateTimeOffset.TryParse(created.UpdatedAt, out _).Should().BeTrue();
        (await eva.GetSegmentDisplayProfileAsync(profile.Id)).Version.Should().Be(2);

        await eva.BulkCreateSegmentDisplayRulesAsync(profile.Id, [new SegmentDisplayRuleCreateDto("bulk-low", null, null, null, null, true, null, null, null, false, null, 1, 10), new SegmentDisplayRuleCreateDto("bulk-high", null, null, null, null, true, null, null, null, false, null, 2, 70)]);
        var afterBulk = await eva.GetSegmentDisplayRulesAsync(profile.Id);
        afterBulk.Select(rule => rule.Priority).Should().Equal(70, 50, 10);
        afterBulk.Select(rule => rule.SourceKey).Should().Equal("bulk-high", "  detector  ", "bulk-low");
        afterBulk.Select(rule => rule.Lane).Should().Equal(2, 3, 1);
        afterBulk.Should().OnlyContain(rule => rule.UserId == profile.UserId);
        afterBulk.Single(rule => rule.Id == created.Id).ShouldMatch(rich, tag.Name, profile.UserId);
        (await eva.GetSegmentDisplayProfileAsync(profile.Id)).Version.Should().Be(3);

        var update = new SegmentDisplayRuleUpdateDto("edited", "marker", tag.Id, "category", SegmentHostType.Video, false, 0.9f, 3, 1, true, "#112233", 7, 60);
        var edited = await eva.UpdateSegmentDisplayRuleAsync(profile.Id, created.Id, update);
        edited.ShouldMatch(update, tag.Name, profile.UserId);
        (await eva.GetSegmentDisplayRulesAsync(profile.Id)).Single(rule => rule.Id == created.Id).ShouldMatch(update, tag.Name, profile.UserId);
        (await eva.GetSegmentDisplayProfileAsync(profile.Id)).Version.Should().Be(4);
        await eva.DeleteSegmentDisplayRuleAsync(profile.Id, created.Id);
        (await eva.GetSegmentDisplayRulesAsync(profile.Id)).Should().NotContain(rule => rule.Id == created.Id);
        (await eva.GetSegmentDisplayProfileAsync(profile.Id)).Version.Should().Be(5);
        var deleted = () => eva.DeleteSegmentDisplayRuleAsync(profile.Id, created.Id);
        await deleted.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        await eva.BulkCreateSegmentDisplayRulesAsync(profile.Id, []);
        (await eva.GetSegmentDisplayProfileAsync(profile.Id)).Version.Should().Be(5);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/segment-display-profiles/preview")]
    public async Task GivenVideoSegments_WhenMemberPreviewsTransientRules_ThenMatchingSpansAndErrorsAreReturned()
    {
        var video = await AsUser().CreateVideoAsync($"Preview video {Guid.NewGuid():N}");
        await AsUser().CreateVideoSegmentAsync(video, new SegmentCreateDto(2, 5, null, "chapter", null, null, "preview-source", null, 0.9f, "Preview", null));
        var rule = new SegmentDisplayRuleCreateDto("preview-source", "chapter", null, null, SegmentHostType.Video, true, 0.8f, null, null, false, "#00ff00", 4, 10);
        var preview = await AsUser(ApiTestUsers.Eva).PreviewSegmentDisplayProfileAsync(new SegmentDisplayProfilePreviewRequestDto(video.Id, [rule]));
        var span = preview.Spans.Should().ContainSingle().Which;
        span.StartSec.Should().Be(2);
        span.EndSec.Should().Be(5);
        span.HostType.Should().Be(SegmentHostType.Video);
        span.HostId.Should().Be(video.Id);
        span.SourceKey.Should().Be("preview-source");
        span.Kind.Should().Be("chapter");
        span.Lane.Should().Be(4);
        span.ColorHint.Should().Be("#00ff00");
        span.SegmentIds.Should().ContainSingle();
        var invalid = () => AsUser(ApiTestUsers.Eva).PreviewSegmentDisplayProfileAsync(new SegmentDisplayProfilePreviewRequestDto(0, []));
        var missing = () => AsUser(ApiTestUsers.Eva).PreviewSegmentDisplayProfileAsync(new SegmentDisplayProfilePreviewRequestDto(int.MaxValue, []));
        await invalid.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
    }
}

internal static class SegmentDisplayRuleAssertions
{
    public static void ShouldMatch(this SegmentDisplayRuleDto actual, SegmentDisplayRuleCreateDto expected, string? tagName, int? userId)
    {
        actual.SourceKey.Should().Be(expected.SourceKey);
        actual.Kind.Should().Be(expected.Kind);
        actual.TagId.Should().Be(expected.TagId);
        actual.TagName.Should().Be(tagName);
        actual.TagCategory.Should().Be(expected.TagCategory);
        actual.HostType.Should().Be(expected.HostType);
        actual.Visible.Should().Be(expected.Visible);
        actual.MinConfidence.Should().Be(expected.MinConfidence);
        actual.MinDurationSec.Should().Be(expected.MinDurationSec);
        actual.MergeGapSec.Should().Be(expected.MergeGapSec);
        actual.CollapseToInstant.Should().Be(expected.CollapseToInstant);
        actual.ColorOverride.Should().Be(expected.ColorOverride);
        actual.Lane.Should().Be(expected.Lane);
        actual.Priority.Should().Be(expected.Priority);
        actual.UserId.Should().Be(userId);
    }

    public static void ShouldMatch(this SegmentDisplayRuleDto actual, SegmentDisplayRuleUpdateDto expected, string? tagName, int? userId)
        => actual.ShouldMatch(new SegmentDisplayRuleCreateDto(expected.SourceKey, expected.Kind, expected.TagId, expected.TagCategory, expected.HostType, expected.Visible, expected.MinConfidence, expected.MinDurationSec, expected.MergeGapSec, expected.CollapseToInstant, expected.ColorOverride, expected.Lane, expected.Priority), tagName, userId);
}
