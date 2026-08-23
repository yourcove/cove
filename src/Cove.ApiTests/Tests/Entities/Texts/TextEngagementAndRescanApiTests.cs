using System.Globalization;
using System.Net;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities.Auth;
using Cove.Core.Enums;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Entities.Texts;

[Collection(ApiTestLane2Collection.Name)]
public sealed class TextEngagementAndRescanApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/texts/{id:int}/history")]
    [CoversEndpoint("POST", "/api/texts/{id:int}/like")]
    [CoversEndpoint("POST", "/api/texts/{id:int}/like/historical")]
    [CoversEndpoint("DELETE", "/api/texts/{id:int}/like/history")]
    [CoversEndpoint("DELETE", "/api/texts/{id:int}/like")]
    [CoversEndpoint("POST", "/api/texts/{id:int}/like/reset")]
    public async Task GivenUserScopedTextLikes_WhenHistoricalCurrentAndResetOperationsRun_ThenHistoryCountsPermissionsAndSortRemainExact()
    {
        var owner = AsUser();
        var eva = AsUser(ApiTestUsers.Eva);
        var anthony = AsUser(ApiTestUsers.Anthony);
        var suffix = Guid.NewGuid().ToString("N");
        var primary = await owner.CreateTextAsync($"Primary like text {suffix}");
        var secondary = await owner.CreateTextAsync($"Secondary like text {suffix}");
        var control = await owner.CreateTextAsync($"Control like text {suffix}");
        var now = DateTime.UtcNow;
        var historicalAt = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, DateTimeKind.Utc).AddDays(-1);

        (await eva.AddHistoricalTextLikeAsync(primary, historicalAt)).Should().Be(1);
        var historical = await eva.GetTextHistoryAsync(primary);
        (await anthony.IncrementTextLikeAsync(primary)).Should().Be(1);

        var viewerUsername = $"text-viewer-{Guid.NewGuid():N}";
        const string viewerPassword = "Text viewer 123!";
        var viewerUser = await owner.CreateUserAsync(new CreateUserRequest(viewerUsername, viewerPassword, Roles: [BuiltinRoles.Member]));
        var viewerHistoricalAt = historicalAt.AddHours(1);
        using (var memberSession = await owner.CreateAuthSessionAsync(viewerUsername, viewerPassword))
            (await memberSession.Client.AddHistoricalTextLikeAsync(primary, viewerHistoricalAt)).Should().Be(1);
        _ = await owner.SetUserRolesAsync(viewerUser.Id, [BuiltinRoles.Viewer]);
        using var viewerSession = await owner.CreateAuthSessionAsync(viewerUsername, viewerPassword);
        var viewer = viewerSession.Client;
        var forbiddenWrites = new Func<Task>[]
        {
            async () => _ = await viewer.IncrementTextLikeAsync(primary),
            async () => _ = await viewer.AddHistoricalTextLikeAsync(primary, historicalAt),
            () => viewer.DeleteHistoricalTextLikeAsync(primary, viewerHistoricalAt),
            async () => _ = await viewer.DecrementTextLikeAsync(primary),
            async () => _ = await viewer.ResetTextLikeAsync(primary),
        };
        foreach (var forbiddenWrite in forbiddenWrites)
        {
            await forbiddenWrite.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
            var viewerAfterForbiddenWrite = await viewer.GetTextHistoryAsync(primary);
            viewerAfterForbiddenWrite.LikeHistory.Should().ContainSingle();
            DateTime.Parse(viewerAfterForbiddenWrite.LikeHistory.Single(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                .Should().Be(viewerHistoricalAt);
        }

        (await eva.GetTextHistoryAsync(primary)).LikeHistory.Should().ContainSingle();
        (await anthony.GetTextHistoryAsync(primary)).LikeHistory.Should().ContainSingle();
        (await owner.GetTextHistoryAsync(primary)).LikeHistory.Should().BeEmpty();
        await eva.DeleteHistoricalTextLikeAsync(primary, historicalAt);
        (await eva.GetTextHistoryAsync(primary)).LikeHistory.Should().BeEmpty();
        var futureHistoricalLike = () => eva.AddHistoricalTextLikeAsync(primary, DateTime.UtcNow.AddDays(1));
        await futureHistoricalLike.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");

        (await eva.IncrementTextLikeAsync(primary)).Should().Be(1);
        (await eva.IncrementTextLikeAsync(primary)).Should().Be(2);
        (await eva.IncrementTextLikeAsync(secondary)).Should().Be(1);
        (await anthony.IncrementTextLikeAsync(secondary)).Should().Be(1);
        (await anthony.IncrementTextLikeAsync(secondary)).Should().Be(2);
        var sortedByLikes = await eva.FindTextsAsync(SortRequest([primary.Id, secondary.Id, control.Id], "like_counter"));
        sortedByLikes.Items.Select(text => text.Id).Should().Equal(primary.Id, secondary.Id, control.Id);
        var anthonySortedByLikes = await anthony.FindTextsAsync(SortRequest([primary.Id, secondary.Id, control.Id], "like_counter"));
        anthonySortedByLikes.Items.Select(text => text.Id).Should().Equal(secondary.Id, primary.Id, control.Id);

        (await eva.DecrementTextLikeAsync(primary)).Should().Be(1);
        (await eva.GetTextHistoryAsync(primary)).LikeHistory.Should().ContainSingle();
        (await eva.ResetTextLikeAsync(primary)).Should().Be(0);
        (await eva.GetTextHistoryAsync(primary)).LikeHistory.Should().BeEmpty();
        (await anthony.GetTextHistoryAsync(primary)).LikeHistory.Should().ContainSingle();

        historical.LikeHistory.Should().ContainSingle();
        DateTime.Parse(historical.LikeHistory.Single(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .Should().Be(historicalAt);

        using var client = owner.CreateHttpClient();
        using var missingHistory = await client.GetAsync("/api/texts/2147483647/history");
        missingHistory.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var missingLike = await client.PostAsync("/api/texts/2147483647/like", content: null);
        missingLike.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/texts/{id:int}/rescan")]
    public async Task GivenFileBackedText_WhenRescanRuns_ThenJobCompletesAndPublicContentAndMetadataReflectTheChangedFile()
    {
        var owner = AsUser();
        var member = AsUser(ApiTestUsers.Eva);
        var initialContent = "Initial deterministic text content.";
        var replacementContent = "Replacement deterministic text content with additional words.";
        var path = AsTestFileSystem().CreateTextFile(initialContent);
        var text = await owner.CreateTextFromFileAsync(path);
        var before = await owner.GetTextByIdAsync(text.Id);
        var beforeFile = before.Files.Should().ContainSingle().Which;
        File.WriteAllText(path, replacementContent);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));

        var job = await member.WaitForTerminalJobAsync(await member.RescanTextAsync(text.Id));
        job.Status.Should().Be(JobStatus.Completed);
        job.Type.Should().Be("scan");
        var after = await owner.GetTextByIdAsync(text.Id);
        var afterFile = after.Files.Should().ContainSingle().Which;
        afterFile.Id.Should().Be(beforeFile.Id);
        afterFile.Path.Should().Be(path);
        afterFile.Size.Should().Be(new FileInfo(path).Length).And.NotBe(beforeFile.Size);
        afterFile.WordCount.Should().BeGreaterThan(beforeFile.WordCount ?? 0);
        afterFile.ExcerptText.Should().Be(replacementContent);
        after.MaxWordCount.Should().Be(afterFile.WordCount);
        (await owner.GetTextContentAsync(text.Id)).Should().Be(new TextContentDto("txt", "text", replacementContent));

        var viewerUsername = $"text-rescan-viewer-{Guid.NewGuid():N}";
        const string viewerPassword = "Text rescan viewer 123!";
        await owner.CreateUserAsync(new CreateUserRequest(viewerUsername, viewerPassword, Roles: [BuiltinRoles.Viewer]));
        using var viewerSession = await owner.CreateAuthSessionAsync(viewerUsername, viewerPassword);
        var forbiddenRescan = () => viewerSession.Client.RescanTextAsync(text.Id);
        await forbiddenRescan.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await owner.GetTextByIdAsync(text.Id)).Should().BeEquivalentTo(after);
    }

    private static FilteredQueryRequest<TextDocumentFilter> SortRequest(IReadOnlyList<int> ids, string sort)
        => new()
        {
            Ids = ids.ToList(),
            FindFilter = new FindFilter
            {
                Page = 1,
                PerPage = 10,
                Sort = sort,
                Direction = SortDirection.Desc,
            },
        };
}
