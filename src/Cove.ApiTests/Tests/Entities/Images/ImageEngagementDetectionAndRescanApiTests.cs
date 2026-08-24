using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Cove.ApiTests.Tests.Entities.Images;

[Collection(ApiTestLane2Collection.Name)]
public sealed class ImageEngagementDetectionAndRescanApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/images/{id:int}/rescan")]
    public async Task GivenFileBackedImage_WhenRescanRuns_ThenJobCompletesAndPermissionFailuresDoNotChangeTheImage()
    {
        var owner = AsUser();
        var member = AsUser(ApiTestUsers.Eva);
        var image = await owner.CreateImageAsync($"Rescan image {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var fileSystem = AsTestFileSystem();
        var initialBytes = await CreatePngAsync(16, 12, new Rgba32(20, 40, 60));
        var replacementBytes = await CreatePngAsync(32, 24, new Rgba32(180, 60, 20));
        replacementBytes.Length.Should().NotBe(initialBytes.Length, "the rescan must prove public file metadata changed");
        var path = fileSystem.CreateLibraryFile($"rescan-{image.Id}.png", initialBytes);
        await AsDbUser().AttachStreamImageFileAsync(image.Id, path, width: 16, height: 12, cancellationToken: TestContext.Current.CancellationToken);
        var before = await owner.GetImageByIdAsync(image.Id, TestContext.Current.CancellationToken);
        var beforeFile = before.Files.Should().ContainSingle().Which;
        fileSystem.ReplaceLibraryFile(path, replacementBytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));

        var jobId = await member.RescanImageAsync(image, TestContext.Current.CancellationToken);
        var job = await member.WaitForTerminalJobAsync(jobId, TestContext.Current.CancellationToken);
        job.Status.Should().Be(JobStatus.Completed);
        job.Type.Should().Be("scan");
        var after = await owner.GetImageByIdAsync(image.Id, TestContext.Current.CancellationToken);
        after.Id.Should().Be(image.Id);
        var afterFile = after.Files.Should().ContainSingle().Which;
        afterFile.Id.Should().Be(beforeFile.Id);
        Path.GetFullPath(afterFile.Path).Should().Be(Path.GetFullPath(path));
        afterFile.Size.Should().Be(replacementBytes.Length).And.NotBe(beforeFile.Size);
        afterFile.Width.Should().Be(32);
        afterFile.Height.Should().Be(24);
        (await owner.GetImagesAsync(TestContext.Current.CancellationToken)).Select(item => item.Id).Should().Equal(image.Id);

        var noFile = await owner.CreateImageAsync($"No-file image {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var noFileRescan = () => member.RescanImageAsync(noFile);
        await noFileRescan.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");

        using var client = member.CreateHttpClient();
        using var missing = await client.PostAsync("/api/images/2147483647/rescan", content: null, cancellationToken: TestContext.Current.CancellationToken);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var viewerUsername = $"image-rescan-viewer-{Guid.NewGuid():N}";
        const string viewerPassword = "Image rescan viewer 123!";
        await owner.CreateUserAsync(new CreateUserRequest(viewerUsername, viewerPassword, Roles: [BuiltinRoles.Viewer]), TestContext.Current.CancellationToken);
        using var viewerSession = await owner.CreateAuthSessionAsync(viewerUsername, viewerPassword, TestContext.Current.CancellationToken);
        var forbidden = () => viewerSession.Client.RescanImageAsync(image);
        await forbidden.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        var afterForbidden = await owner.GetImageByIdAsync(image.Id, TestContext.Current.CancellationToken);
        afterForbidden.Should().BeEquivalentTo(after);
    }

    [Fact]
    [CoversEndpoint("GET", "/api/images/{imageid:int}/detections/{id:int}")]
    [CoversEndpoint("PUT", "/api/images/{imageid:int}/detections/{id:int}")]
    [CoversEndpoint("DELETE", "/api/images/{imageid:int}/detections/{id:int}")]
    public async Task GivenImageDetection_WhenReadUpdatedAndDeleted_ThenContainmentPersistenceAndPermissionsAreExact()
    {
        var owner = AsUser();
        var member = AsUser(ApiTestUsers.Eva);
        var image = await owner.CreateImageAsync($"Detection host {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var otherImage = await owner.CreateImageAsync($"Other detection host {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var created = await owner.CreateImageDetectionAsync(image, DetectionCreate("initial", " create-source "), TestContext.Current.CancellationToken);
        AssertDetectionEquivalent(created, await member.GetImageDetectionAsync(image, created.Id, TestContext.Current.CancellationToken));

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
        var updated = await member.UpdateImageDetectionAsync(image, created.Id, replacement, TestContext.Current.CancellationToken);
        updated.SourceKey.Should().Be("updated-source");
        AssertDetectionEquivalent(updated, await owner.GetImageDetectionAsync(image, created.Id, TestContext.Current.CancellationToken));
        AssertDetectionEquivalent(updated, (await owner.GetImageDetectionsAsync(image, TestContext.Current.CancellationToken)).Should().ContainSingle().Which);

        await AssertNotFoundAsync(() => member.GetImageDetectionAsync(otherImage, created.Id));
        await AssertNotFoundAsync(() => member.UpdateImageDetectionAsync(otherImage, created.Id, replacement));
        await AssertNotFoundAsync(() => member.DeleteImageDetectionAsync(otherImage, created.Id));
        AssertDetectionEquivalent(updated, await owner.GetImageDetectionAsync(image, created.Id, TestContext.Current.CancellationToken));

        var invalid = replacement with { FrameWidth = 0 };
        var invalidUpdate = () => member.UpdateImageDetectionAsync(image, created.Id, invalid);
        await invalidUpdate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        AssertDetectionEquivalent(updated, await owner.GetImageDetectionAsync(image, created.Id, TestContext.Current.CancellationToken));
        var invalidBox = () => member.UpdateImageDetectionAsync(image, created.Id, replacement with { W = 0 });
        await invalidBox.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        AssertDetectionEquivalent(updated, await owner.GetImageDetectionAsync(image, created.Id, TestContext.Current.CancellationToken));

        var viewerUsername = $"detection-viewer-{Guid.NewGuid():N}";
        const string viewerPassword = "Detection viewer 123!";
        await owner.CreateUserAsync(new CreateUserRequest(viewerUsername, viewerPassword, Roles: [BuiltinRoles.Viewer]), TestContext.Current.CancellationToken);
        using var viewerSession = await owner.CreateAuthSessionAsync(viewerUsername, viewerPassword, TestContext.Current.CancellationToken);
        var viewer = viewerSession.Client;
        AssertDetectionEquivalent(updated, await viewer.GetImageDetectionAsync(image, created.Id, TestContext.Current.CancellationToken));
        var viewerUpdate = () => viewer.UpdateImageDetectionAsync(image, created.Id, replacement with { Class = "forbidden" });
        await viewerUpdate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        AssertDetectionEquivalent(updated, await owner.GetImageDetectionAsync(image, created.Id, TestContext.Current.CancellationToken));
        var viewerDelete = () => viewer.DeleteImageDetectionAsync(image, created.Id);
        await viewerDelete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        AssertDetectionEquivalent(updated, await owner.GetImageDetectionAsync(image, created.Id, TestContext.Current.CancellationToken));

        await member.DeleteImageDetectionAsync(image, created.Id, TestContext.Current.CancellationToken);
        await AssertNotFoundAsync(() => owner.GetImageDetectionAsync(image, created.Id));
        (await owner.GetImageDetectionsAsync(image, TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    [CoversEndpoint("GET", "/api/images/{id:int}/history")]
    [CoversEndpoint("POST", "/api/images/{id:int}/like/historical")]
    [CoversEndpoint("DELETE", "/api/images/{id:int}/like/history")]
    [CoversEndpoint("DELETE", "/api/images/{id:int}/like")]
    [CoversEndpoint("POST", "/api/images/{id:int}/like/reset")]
    public async Task GivenUserScopedImageLikes_WhenHistoricalDeleteDecrementAndResetRun_ThenIsolationAndPermissionSideEffectsAreExact()
    {
        var owner = AsUser();
        var eva = AsUser(ApiTestUsers.Eva);
        var anthony = AsUser(ApiTestUsers.Anthony);
        var image = await owner.CreateImageAsync($"Engagement image {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var now = DateTime.UtcNow;
        var historicalAt = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, DateTimeKind.Utc).AddDays(-1);

        (await eva.AddHistoricalImageLikeAsync(image, historicalAt, TestContext.Current.CancellationToken)).Should().Be(1);
        (await anthony.IncrementImageLikeAsync(image, TestContext.Current.CancellationToken)).Should().Be(1);
        AssertLikeHistory(await eva.GetImageHistoryAsync(image, TestContext.Current.CancellationToken), historicalAt);
        AssertLikeHistory(await anthony.GetImageHistoryAsync(image, TestContext.Current.CancellationToken), expectedCount: 1);
        (await owner.GetImageHistoryAsync(image, TestContext.Current.CancellationToken)).LikeHistory.Should().BeEmpty();

        var viewerUsername = $"image-like-viewer-{Guid.NewGuid():N}";
        const string viewerPassword = "Image like viewer 123!";
        var viewerUser = await owner.CreateUserAsync(new CreateUserRequest(viewerUsername, viewerPassword, Roles: [BuiltinRoles.Member]), TestContext.Current.CancellationToken);
        var viewerHistoricalAt = historicalAt.AddHours(1);
        using (var memberSession = await owner.CreateAuthSessionAsync(viewerUsername, viewerPassword, TestContext.Current.CancellationToken))
        {
            (await memberSession.Client.AddHistoricalImageLikeAsync(image, viewerHistoricalAt, TestContext.Current.CancellationToken)).Should().Be(1);
            (await memberSession.Client.IncrementImageLikeAsync(image, TestContext.Current.CancellationToken)).Should().Be(2);
        }
        VideoHistoryDto seededViewerHistory;
        using (var memberReadSession = await owner.CreateAuthSessionAsync(viewerUsername, viewerPassword, TestContext.Current.CancellationToken))
            seededViewerHistory = await memberReadSession.Client.GetImageHistoryAsync(image, TestContext.Current.CancellationToken);
        _ = await owner.SetUserRolesAsync(viewerUser.Id, [BuiltinRoles.Viewer], TestContext.Current.CancellationToken);
        using var viewerSession = await owner.CreateAuthSessionAsync(viewerUsername, viewerPassword, TestContext.Current.CancellationToken);
        var viewer = viewerSession.Client;
        var forbiddenWrites = new Func<Task>[]
        {
            async () => _ = await viewer.AddHistoricalImageLikeAsync(image, historicalAt.AddHours(2)),
            () => viewer.DeleteHistoricalImageLikeAsync(image, viewerHistoricalAt),
            async () => _ = await viewer.DecrementImageLikeAsync(image),
            async () => _ = await viewer.ResetImageLikeAsync(image),
        };
        foreach (var forbiddenWrite in forbiddenWrites)
        {
            await forbiddenWrite.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
            var viewerHistory = await viewer.GetImageHistoryAsync(image, TestContext.Current.CancellationToken);
            viewerHistory.Should().BeEquivalentTo(seededViewerHistory);
            AssertLikeHistory(viewerHistory, viewerHistoricalAt, expectedCount: 2);
            (await viewer.GetEntityEngagementAsync(AffinityHostType.Image, image.Id, TestContext.Current.CancellationToken)).LikeCount.Should().Be(2);
        }

        await eva.DeleteHistoricalImageLikeAsync(image, historicalAt, TestContext.Current.CancellationToken);
        (await eva.GetImageHistoryAsync(image, TestContext.Current.CancellationToken)).LikeHistory.Should().BeEmpty();
        (await anthony.GetImageHistoryAsync(image, TestContext.Current.CancellationToken)).LikeHistory.Should().ContainSingle();
        var future = () => eva.AddHistoricalImageLikeAsync(image, DateTime.UtcNow.AddDays(1));
        await future.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        (await eva.GetImageHistoryAsync(image, TestContext.Current.CancellationToken)).LikeHistory.Should().BeEmpty();

        (await eva.IncrementImageLikeAsync(image, TestContext.Current.CancellationToken)).Should().Be(1);
        (await eva.IncrementImageLikeAsync(image, TestContext.Current.CancellationToken)).Should().Be(2);
        (await eva.DecrementImageLikeAsync(image, TestContext.Current.CancellationToken)).Should().Be(1);
        (await eva.GetImageHistoryAsync(image, TestContext.Current.CancellationToken)).LikeHistory.Should().ContainSingle();
        (await eva.ResetImageLikeAsync(image, TestContext.Current.CancellationToken)).Should().Be(0);
        (await eva.GetImageHistoryAsync(image, TestContext.Current.CancellationToken)).LikeHistory.Should().BeEmpty();
        (await anthony.GetEntityEngagementAsync(AffinityHostType.Image, image.Id, TestContext.Current.CancellationToken)).LikeCount.Should().Be(1);
        (await owner.GetEntityEngagementAsync(AffinityHostType.Image, image.Id, TestContext.Current.CancellationToken)).LikeCount.Should().Be(0);

        using var client = owner.CreateHttpClient();
        using var missingHistory = await client.GetAsync("/api/images/2147483647/history", TestContext.Current.CancellationToken);
        missingHistory.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var missingHistorical = await client.PostAsJsonAsync("/api/images/2147483647/like/historical", new HistoricalLikeDto(historicalAt), cancellationToken: TestContext.Current.CancellationToken);
        missingHistorical.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static DetectionCreateDto DetectionCreate(string classification, string sourceKey)
        => new(
            ObservedAtSec: 1.25,
            FrameWidth: 320,
            FrameHeight: 240,
            Class: classification,
            Score: 0.91f,
            X: 0.1f,
            Y: 0.2f,
            W: 0.3f,
            H: 0.4f,
            Extra: JsonDocument.Parse("{\"initial\":true}").RootElement.Clone(),
            RefKind: "face",
            RefId: 9,
            GroupKey: "initial-group",
            SourceKey: sourceKey,
            SourceRunId: "run-1");

    private static async Task<byte[]> CreatePngAsync(int width, int height, Rgba32 color)
    {
        using var image = new Image<Rgba32>(width, height, color);
        await using var output = new MemoryStream();
        await image.SaveAsPngAsync(output);
        return output.ToArray();
    }

    private static void AssertDetectionEquivalent(DetectionDto expected, DetectionDto actual)
    {
        actual.Should().BeEquivalentTo(expected, options => options
            .Excluding(item => item.Extra)
            .Excluding(item => item.CreatedAt)
            .Excluding(item => item.UpdatedAt));
        PostgreSqlTimestamp(actual.CreatedAt).Should().Be(PostgreSqlTimestamp(expected.CreatedAt));
        PostgreSqlTimestamp(actual.UpdatedAt).Should().Be(PostgreSqlTimestamp(expected.UpdatedAt));
        actual.Extra.Should().NotBeNull();
        JsonElement.DeepEquals(actual.Extra!.Value, expected.Extra!.Value).Should().BeTrue();
    }

    private static DateTime PostgreSqlTimestamp(string value)
    {
        var parsed = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return new DateTime(parsed.Ticks / 10 * 10, parsed.Kind);
    }

    private static void AssertLikeHistory(VideoHistoryDto history, DateTime? expectedAt = null, int expectedCount = 1)
    {
        history.LikeHistory.Should().HaveCount(expectedCount);
        if (expectedAt.HasValue)
        {
            history.LikeHistory.Select(value => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
                .Should().Contain(expectedAt.Value);
        }
    }

    private static async Task AssertNotFoundAsync<T>(Func<Task<T>> action)
        => await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");

    private static async Task AssertNotFoundAsync(Func<Task> action)
        => await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
}
