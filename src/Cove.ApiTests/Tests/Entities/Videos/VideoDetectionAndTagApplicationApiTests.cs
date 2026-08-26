using System.Globalization;
using System.Text.Json;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;

namespace Cove.ApiTests.Tests.Entities.Videos;

public sealed class VideoDetectionAndTagApplicationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/videos/{videoid:int}/detections/{id:int}")]
    [CoversEndpoint("PUT", "/api/videos/{videoid:int}/detections/{id:int}")]
    [CoversEndpoint("DELETE", "/api/videos/{videoid:int}/detections/{id:int}")]
    public async Task GivenVideoDetection_WhenReadUpdatedAndDeleted_ThenContainmentPersistenceAndPermissionsAreExact()
    {
        var owner = AsUser();
        var member = AsUser(ApiTestUsers.Eva);
        var suffix = Guid.NewGuid().ToString("N");
        var video = await owner.CreateVideoAsync($"Detection host {suffix}", TestContext.Current.CancellationToken);
        var otherVideo = await owner.CreateVideoAsync($"Other detection host {suffix}", TestContext.Current.CancellationToken);
        var initial = new DetectionCreateDto(
            ObservedAtSec: 1.25,
            FrameWidth: 320,
            FrameHeight: 240,
            Class: "initial",
            Score: 0.91f,
            X: 0.1f,
            Y: 0.2f,
            W: 0.3f,
            H: 0.4f,
            Extra: null,
            RefKind: "face",
            RefId: 9,
            GroupKey: "initial-group",
            SourceKey: " \t ",
            SourceRunId: "run-1");
        var created = await owner.CreateVideoDetectionAsync(video, initial, TestContext.Current.CancellationToken);
        AssertDetection(created, video.Id, ToUpdate(initial, "user"));
        AssertDetectionEquivalent(created, await member.GetVideoDetectionAsync(video, created.Id, TestContext.Current.CancellationToken));

        var replacement = new DetectionUpdateDto(
            ObservedAtSec: 3.5,
            FrameWidth: 640,
            FrameHeight: 480,
            Class: "updated",
            Score: 0.72f,
            X: 0.15f,
            Y: 0.25f,
            W: 0.45f,
            H: 0.35f,
            Extra: JsonDocument.Parse("{\"nested\":{\"value\":7}}").RootElement.Clone(),
            RefKind: "performer",
            RefId: 17,
            GroupKey: "updated-group",
            SourceKey: "updated-source",
            SourceRunId: "run-2");
        var updated = await member.UpdateVideoDetectionAsync(video, created.Id, replacement, TestContext.Current.CancellationToken);
        AssertDetection(updated, video.Id, replacement);
        PostgreSqlTimestamp(updated.CreatedAt).Should().Be(PostgreSqlTimestamp(created.CreatedAt));
        AssertDetectionEquivalent(updated, await owner.GetVideoDetectionAsync(video, created.Id, TestContext.Current.CancellationToken));
        AssertDetectionEquivalent(updated, (await owner.GetVideoDetectionsAsync(video, TestContext.Current.CancellationToken)).Should().ContainSingle().Which);

        await AssertNotFoundAsync(() => member.GetVideoDetectionAsync(otherVideo, created.Id));
        await AssertNotFoundAsync(() => member.UpdateVideoDetectionAsync(otherVideo, created.Id, replacement));
        await AssertNotFoundAsync(() => member.DeleteVideoDetectionAsync(otherVideo, created.Id));
        AssertDetectionEquivalent(updated, await owner.GetVideoDetectionAsync(video, created.Id, TestContext.Current.CancellationToken));

        var invalidCreates = new[]
        {
            initial with { FrameWidth = 0 },
            initial with { FrameHeight = 0 },
            initial with { W = 0 },
            initial with { H = 0 },
        };
        foreach (var invalidCreate in invalidCreates)
        {
            var create = () => member.CreateVideoDetectionAsync(video, invalidCreate);
            await create.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
            AssertDetectionEquivalent(updated, (await owner.GetVideoDetectionsAsync(video, TestContext.Current.CancellationToken)).Should().ContainSingle().Which);
        }

        var invalidUpdates = new[]
        {
            replacement with { FrameWidth = 0 },
            replacement with { FrameHeight = 0 },
            replacement with { W = 0 },
            replacement with { H = 0 },
        };
        foreach (var invalidUpdate in invalidUpdates)
        {
            var update = () => member.UpdateVideoDetectionAsync(video, created.Id, invalidUpdate);
            await update.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
            AssertDetectionEquivalent(updated, await owner.GetVideoDetectionAsync(video, created.Id, TestContext.Current.CancellationToken));
        }

        var noRoleUsername = $"detection-no-role-{suffix}";
        var viewerUsername = $"detection-viewer-{suffix}";
        const string password = "Detection permissions 123!";
        await owner.CreateUserAsync(new CreateUserRequest(noRoleUsername, password, Roles: []), TestContext.Current.CancellationToken);
        await owner.CreateUserAsync(new CreateUserRequest(viewerUsername, password, Roles: [BuiltinRoles.Viewer]), TestContext.Current.CancellationToken);
        using var noRoleSession = await owner.CreateAuthSessionAsync(noRoleUsername, password, TestContext.Current.CancellationToken);
        using var viewerSession = await owner.CreateAuthSessionAsync(viewerUsername, password, TestContext.Current.CancellationToken);
        var noRoleRead = () => noRoleSession.Client.GetVideoDetectionAsync(video, created.Id);
        await noRoleRead.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        AssertDetectionEquivalent(updated, await viewerSession.Client.GetVideoDetectionAsync(video, created.Id, TestContext.Current.CancellationToken));
        var viewerUpdate = () => viewerSession.Client.UpdateVideoDetectionAsync(video, created.Id, replacement with { Class = "forbidden" });
        await viewerUpdate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        AssertDetectionEquivalent(updated, await owner.GetVideoDetectionAsync(video, created.Id, TestContext.Current.CancellationToken));
        var viewerDelete = () => viewerSession.Client.DeleteVideoDetectionAsync(video, created.Id);
        await viewerDelete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        AssertDetectionEquivalent(updated, await owner.GetVideoDetectionAsync(video, created.Id, TestContext.Current.CancellationToken));

        await member.DeleteVideoDetectionAsync(video, created.Id, TestContext.Current.CancellationToken);
        await AssertNotFoundAsync(() => owner.GetVideoDetectionAsync(video, created.Id));
        (await owner.GetVideoDetectionsAsync(video, TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    [CoversEndpoint("POST", "/api/tagapplications")]
    [CoversEndpoint("DELETE", "/api/tagapplications/{id:int}")]
    [CoversEndpoint("DELETE", "/api/tagapplications/host/{hosttype}/{hostid:int}/tag/{tagid:int}")]
    public async Task GivenHostAndContextTagApplications_WhenUpsertedAndDeleted_ThenDerivedTagsIsolationAndPermissionsAreExact()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var tagGroup = await owner.CreateTagGroupAsync(new TagGroupCreateDto($"Application group {suffix}", Color: "#2563eb"), TestContext.Current.CancellationToken);
        var tag = await owner.CreateTagAsync(new TagBuilder()
                .WithName($"Derived application tag {suffix}")
                .WithAlias($"Application alias {suffix}")
                .WithDescription("Tag derived from host-level applications.")
                .WithColor("#f97316")
                .WithTagGroup(tagGroup)
                .WithMinimumOccurrence(10, 20)
                .Build(), TestContext.Current.CancellationToken);
        var otherTag = await owner.CreateTagAsync($"Other application tag {suffix}", TestContext.Current.CancellationToken);
        var video = await owner.CreateVideoAsync($"Application host {suffix}", TestContext.Current.CancellationToken);
        var controlVideo = await owner.CreateVideoAsync($"Application control host {suffix}", TestContext.Current.CancellationToken);
        var detection = await owner.CreateVideoDetectionAsync(video, "application-context", TestContext.Current.CancellationToken);
        var segment = await owner.CreateVideoSegmentAsync(video, new SegmentCreateDto(
                StartSec: 4,
                EndSec: 9,
                TagId: tag.Id,
                Kind: "chapter",
                RefId: null,
                Payload: null,
                SourceKey: "application-test",
                SourceRunId: "segment-run",
                Confidence: 0.8f,
                Title: "Preserved timeline segment",
                ColorHint: null), TestContext.Current.CancellationToken);

        var hostRequest = new TagApplicationCreateDto(
            HostType: "ViDeO",
            HostId: video.Id,
            TagId: tag.Id,
            SourceKey: " scraper ",
            ContextType: null,
            ContextId: null,
            SourceRunId: " run-1 ",
            ModelKey: " model-1 ",
            Confidence: 0.91f,
            TotalDurationSec: 12,
            HostDurationSec: 40);
        var created = await owner.CreateTagApplicationAsync(hostRequest, TestContext.Current.CancellationToken);
        AssertTagApplication(created, video.Id, tag, tagGroup, "scraper:local", "run-1", "model-1", 0.91f, 12, 40, null, null);

        var upserted = await owner.CreateTagApplicationAsync(hostRequest with
        {
            SourceKey = "SCRAPER",
            SourceRunId = "run-1",
            ModelKey = "model-1",
            Confidence = 0.73f,
            TotalDurationSec = null,
            HostDurationSec = 50,
        }, TestContext.Current.CancellationToken);
        upserted.Id.Should().Be(created.Id);
        PostgreSqlTimestamp(upserted.AppliedAt).Should().Be(PostgreSqlTimestamp(created.AppliedAt));
        AssertTagApplication(upserted, video.Id, tag, tagGroup, "scraper:local", "run-1", "model-1", 0.73f, 12, 50, null, null);

        var secondary = await owner.CreateTagApplicationAsync(hostRequest with
        {
            SourceKey = " metadata ",
            SourceRunId = " ",
            ModelKey = null,
            Confidence = null,
            TotalDurationSec = 1,
            HostDurationSec = 50,
        }, TestContext.Current.CancellationToken);
        AssertTagApplication(secondary, video.Id, tag, tagGroup, "metadata:default", null, null, null, 1, 50, null, null);
        var contextual = await owner.CreateTagApplicationAsync(hostRequest with
        {
            SourceKey = "context-source",
            ContextType = " DeTeCtIoN ",
            ContextId = detection.Id,
            SourceRunId = "context-run",
            ModelKey = "context-model",
            Confidence = 0.88f,
            TotalDurationSec = 3,
            HostDurationSec = 50,
        }, TestContext.Current.CancellationToken);
        AssertTagApplication(contextual, video.Id, tag, tagGroup, "context-source", "context-run", "context-model", 0.88f, 3, 50, "detection", detection.Id);
        var otherTagApplication = await owner.CreateTagApplicationAsync(hostRequest with
        {
            TagId = otherTag.Id,
            SourceKey = "other-tag-source",
            SourceRunId = null,
            ModelKey = null,
            Confidence = null,
            TotalDurationSec = null,
            HostDurationSec = null,
        }, TestContext.Current.CancellationToken);
        var control = await owner.CreateTagApplicationAsync(hostRequest with
        {
            HostId = controlVideo.Id,
            SourceKey = "control-source",
            SourceRunId = null,
            ModelKey = null,
            Confidence = null,
            TotalDurationSec = 12,
            HostDurationSec = 40,
        }, TestContext.Current.CancellationToken);

        var hostBeforePermissions = await owner.GetTagApplicationsAsync("video", video.Id, cancellationToken: TestContext.Current.CancellationToken);
        hostBeforePermissions.Select(application => application.Id).Should().BeEquivalentTo(
            [created.Id, secondary.Id, contextual.Id, otherTagApplication.Id]);
        var videoBeforeDelete = await owner.GetVideoByIdAsync(video.Id, TestContext.Current.CancellationToken);
        videoBeforeDelete.Tags.Select(item => item.Id).Should().Contain([tag.Id, otherTag.Id]);
        videoBeforeDelete.ContextTagApplications.Should().ContainSingle(application => application.Id == contextual.Id);

        var invalidContext = () => owner.CreateTagApplicationAsync(hostRequest with
        {
            HostId = controlVideo.Id,
            ContextType = "detection",
            ContextId = detection.Id,
            SourceKey = "invalid-context",
        });
        await invalidContext.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        (await owner.GetTagApplicationsAsync("video", controlVideo.Id, cancellationToken: TestContext.Current.CancellationToken)).Select(application => application.Id).Should().Equal(control.Id);
        var invalidHostDelete = () => owner.DeleteHostTagApplicationsAsync("invalid-host", video.Id, tag.Id);
        await invalidHostDelete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        (await owner.GetTagApplicationsAsync("video", video.Id, cancellationToken: TestContext.Current.CancellationToken)).Should().BeEquivalentTo(hostBeforePermissions);

        var noRoleUsername = $"tag-application-no-role-{suffix}";
        var viewerUsername = $"tag-application-viewer-{suffix}";
        const string password = "Tag application permissions 123!";
        await owner.CreateUserAsync(new CreateUserRequest(noRoleUsername, password, Roles: []), TestContext.Current.CancellationToken);
        await owner.CreateUserAsync(new CreateUserRequest(viewerUsername, password, Roles: [BuiltinRoles.Viewer]), TestContext.Current.CancellationToken);
        using var noRoleSession = await owner.CreateAuthSessionAsync(noRoleUsername, password, TestContext.Current.CancellationToken);
        using var viewerSession = await owner.CreateAuthSessionAsync(viewerUsername, password, TestContext.Current.CancellationToken);
        var noRoleRead = () => noRoleSession.Client.GetTagApplicationsAsync("video", video.Id);
        await noRoleRead.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await viewerSession.Client.GetTagApplicationsAsync("video", video.Id, cancellationToken: TestContext.Current.CancellationToken)).Should().BeEquivalentTo(hostBeforePermissions);
        var forbiddenCreate = () => viewerSession.Client.CreateTagApplicationAsync(hostRequest with { SourceKey = "forbidden" });
        await forbiddenCreate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await owner.GetTagApplicationsAsync("video", video.Id, cancellationToken: TestContext.Current.CancellationToken)).Should().BeEquivalentTo(hostBeforePermissions);
        var forbiddenIdDelete = () => viewerSession.Client.DeleteTagApplicationAsync(contextual.Id);
        await forbiddenIdDelete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await owner.GetTagApplicationsAsync("video", video.Id, cancellationToken: TestContext.Current.CancellationToken)).Should().BeEquivalentTo(hostBeforePermissions);
        var forbiddenHostDelete = () => viewerSession.Client.DeleteHostTagApplicationsAsync("video", video.Id, tag.Id);
        await forbiddenHostDelete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await owner.GetTagApplicationsAsync("video", video.Id, cancellationToken: TestContext.Current.CancellationToken)).Should().BeEquivalentTo(hostBeforePermissions);

        await owner.DeleteHostTagApplicationsAsync("VIDEO", video.Id, tag.Id, TestContext.Current.CancellationToken);
        var afterHostDelete = await owner.GetTagApplicationsAsync("video", video.Id, cancellationToken: TestContext.Current.CancellationToken);
        afterHostDelete.Should().ContainSingle(application => application.Id == contextual.Id);
        afterHostDelete.Should().ContainSingle(application => application.Id == otherTagApplication.Id);
        afterHostDelete.Should().NotContain(application => application.Id == created.Id || application.Id == secondary.Id);
        (await owner.GetTagApplicationsAsync("video", controlVideo.Id, cancellationToken: TestContext.Current.CancellationToken)).Should().ContainSingle(application => application.Id == control.Id);
        AssertDetectionEquivalent(detection, await owner.GetVideoDetectionAsync(video, detection.Id, TestContext.Current.CancellationToken));
        (await owner.GetVideoSegmentsAsync(video, TestContext.Current.CancellationToken)).Should().ContainSingle(item => item.Id == segment.Id);
        var videoAfterHostDelete = await owner.GetVideoByIdAsync(video.Id, TestContext.Current.CancellationToken);
        videoAfterHostDelete.Tags.Select(item => item.Id).Should().Contain(otherTag.Id).And.NotContain(tag.Id);
        videoAfterHostDelete.ContextTagApplications.Should().ContainSingle(application => application.Id == contextual.Id);

        var repeatedHostDelete = () => owner.DeleteHostTagApplicationsAsync("video", video.Id, tag.Id);
        await repeatedHostDelete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        (await owner.GetTagApplicationsAsync("video", video.Id, cancellationToken: TestContext.Current.CancellationToken)).Should().BeEquivalentTo(afterHostDelete);

        await owner.DeleteTagApplicationAsync(contextual.Id, TestContext.Current.CancellationToken);
        var repeatedIdDelete = () => owner.DeleteTagApplicationAsync(contextual.Id);
        await repeatedIdDelete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        var finalHostApplications = await owner.GetTagApplicationsAsync("video", video.Id, cancellationToken: TestContext.Current.CancellationToken);
        finalHostApplications.Should().ContainSingle(application => application.Id == otherTagApplication.Id);
        (await owner.GetVideoByIdAsync(video.Id, TestContext.Current.CancellationToken)).ContextTagApplications.Should().BeEmpty();
        (await owner.GetTagApplicationsAsync("video", controlVideo.Id, cancellationToken: TestContext.Current.CancellationToken)).Should().ContainSingle(application => application.Id == control.Id);
    }

    private static DetectionUpdateDto ToUpdate(DetectionCreateDto detection, string sourceKey)
        => new(
            detection.ObservedAtSec,
            detection.FrameWidth,
            detection.FrameHeight,
            detection.Class,
            detection.Score,
            detection.X,
            detection.Y,
            detection.W,
            detection.H,
            detection.Extra,
            detection.RefKind,
            detection.RefId,
            detection.GroupKey,
            sourceKey,
            detection.SourceRunId);

    private static void AssertDetection(DetectionDto actual, int hostId, DetectionUpdateDto expected)
    {
        actual.HostType.Should().Be(DetectionHostType.Video);
        actual.HostId.Should().Be(hostId);
        actual.ObservedAtSec.Should().Be(expected.ObservedAtSec);
        actual.FrameWidth.Should().Be(expected.FrameWidth);
        actual.FrameHeight.Should().Be(expected.FrameHeight);
        actual.Class.Should().Be(expected.Class);
        actual.Score.Should().Be(expected.Score);
        actual.X.Should().Be(expected.X);
        actual.Y.Should().Be(expected.Y);
        actual.W.Should().Be(expected.W);
        actual.H.Should().Be(expected.H);
        if (expected.Extra.HasValue)
        {
            actual.Extra.Should().NotBeNull();
            JsonElement.DeepEquals(actual.Extra!.Value, expected.Extra.Value).Should().BeTrue();
        }
        else
        {
            actual.Extra.Should().BeNull();
        }
        actual.RefKind.Should().Be(expected.RefKind);
        actual.RefId.Should().Be(expected.RefId);
        actual.GroupKey.Should().Be(expected.GroupKey);
        actual.SourceKey.Should().Be(expected.SourceKey);
        actual.SourceRunId.Should().Be(expected.SourceRunId);
        _ = PostgreSqlTimestamp(actual.CreatedAt);
        _ = PostgreSqlTimestamp(actual.UpdatedAt);
    }

    private static void AssertDetectionEquivalent(DetectionDto expected, DetectionDto actual)
    {
        actual.Should().BeEquivalentTo(expected, options => options
            .Excluding(item => item.Extra)
            .Excluding(item => item.CreatedAt)
            .Excluding(item => item.UpdatedAt));
        PostgreSqlTimestamp(actual.CreatedAt).Should().Be(PostgreSqlTimestamp(expected.CreatedAt));
        PostgreSqlTimestamp(actual.UpdatedAt).Should().Be(PostgreSqlTimestamp(expected.UpdatedAt));
        if (expected.Extra.HasValue)
        {
            actual.Extra.Should().NotBeNull();
            JsonElement.DeepEquals(actual.Extra!.Value, expected.Extra.Value).Should().BeTrue();
        }
        else
        {
            actual.Extra.Should().BeNull();
        }
    }

    private static void AssertTagApplication(
        TagApplicationDto actual,
        int hostId,
        TagDetailDto tag,
        TagGroupDto tagGroup,
        string sourceKey,
        string? sourceRunId,
        string? modelKey,
        float? confidence,
        double? totalDurationSec,
        double? hostDurationSec,
        string? contextType,
        int? contextId)
    {
        actual.HostType.Should().Be("video");
        actual.HostId.Should().Be(hostId);
        actual.ContextType.Should().Be(contextType);
        actual.ContextId.Should().Be(contextId);
        actual.SourceKey.Should().Be(sourceKey);
        actual.SourceRunId.Should().Be(sourceRunId);
        actual.ModelKey.Should().Be(modelKey);
        actual.Confidence.Should().Be(confidence);
        actual.TotalDurationSec.Should().Be(totalDurationSec);
        actual.HostDurationSec.Should().Be(hostDurationSec);
        actual.Tag.Id.Should().Be(tag.Id);
        actual.Tag.Name.Should().Be(tag.Name);
        actual.Tag.Description.Should().Be(tag.Description);
        actual.Tag.Aliases.Should().Equal(tag.Aliases);
        actual.Tag.Color.Should().Be(tag.Color);
        actual.Tag.TagGroupId.Should().Be(tagGroup.Id);
        actual.Tag.TagGroupName.Should().Be(tagGroup.Name);
        actual.Tag.TagGroupColor.Should().Be(tagGroup.Color);
        _ = PostgreSqlTimestamp(actual.AppliedAt);
    }

    private static DateTime PostgreSqlTimestamp(string value)
    {
        var parsed = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return new DateTime(parsed.Ticks / 10 * 10, parsed.Kind);
    }

    private static async Task AssertNotFoundAsync<T>(Func<Task<T>> action)
        => await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");

    private static async Task AssertNotFoundAsync(Func<Task> action)
        => await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
}
