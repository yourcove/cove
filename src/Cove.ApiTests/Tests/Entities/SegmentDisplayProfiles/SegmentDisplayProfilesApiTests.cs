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
        first.IsDefault.Should().BeTrue();
        first.Version.Should().Be(1);

        var updated = await eva.UpdateSegmentDisplayProfileAsync(first.Id, new SegmentDisplayProfileUpdateDto("  Renamed profile  ", "  Updated description  "));
        updated.Name.Should().Be("Renamed profile");
        updated.Description.Should().Be("Updated description");
        (await eva.GetSegmentDisplayProfileAsync(first.Id)).Description.Should().Be("Updated description");
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
        created.TagName.Should().Be(tag.Name);
        created.UserId.Should().Be(profile.UserId);
        created.SourceKey.Should().Be("  detector  ");
        (await eva.GetSegmentDisplayProfileAsync(profile.Id)).Version.Should().Be(2);

        await eva.BulkCreateSegmentDisplayRulesAsync(profile.Id, [new SegmentDisplayRuleCreateDto("bulk-low", null, null, null, null, true, null, null, null, false, null, 1, 10), new SegmentDisplayRuleCreateDto("bulk-high", null, null, null, null, true, null, null, null, false, null, 2, 70)]);
        var afterBulk = await eva.GetSegmentDisplayRulesAsync(profile.Id);
        afterBulk.Select(rule => rule.Priority).Should().Equal(70, 50, 10);
        afterBulk.Single(rule => rule.Id == created.Id).ColorOverride.Should().Be("  #aabbcc  ");
        (await eva.GetSegmentDisplayProfileAsync(profile.Id)).Version.Should().Be(3);

        var edited = await eva.UpdateSegmentDisplayRuleAsync(profile.Id, created.Id, new SegmentDisplayRuleUpdateDto("edited", "marker", tag.Id, "category", SegmentHostType.Video, false, 0.9f, 3, 1, true, "#112233", 7, 60));
        edited.Priority.Should().Be(60);
        (await eva.GetSegmentDisplayRulesAsync(profile.Id)).Single(rule => rule.Id == created.Id).Lane.Should().Be(7);
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
        span.Lane.Should().Be(4);
        span.ColorHint.Should().Be("#00ff00");
        var invalid = () => AsUser(ApiTestUsers.Eva).PreviewSegmentDisplayProfileAsync(new SegmentDisplayProfilePreviewRequestDto(0, []));
        var missing = () => AsUser(ApiTestUsers.Eva).PreviewSegmentDisplayProfileAsync(new SegmentDisplayProfilePreviewRequestDto(int.MaxValue, []));
        await invalid.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
    }
}
