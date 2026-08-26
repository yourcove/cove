using System.Net;
using System.Net.WebSockets;
using System.Text;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Auth;

public sealed class DerivedDiscoveryAuthorizationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenRestrictedVideoEmbeddings_WhenDerivedDiscoveryIsRead_ThenHiddenHostsAndArtifactsStayConcealed()
    {
        const string kindFamily = "video.scoped-discovery.v1";
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var hiddenTag = await owner.CreateTagAsync($"Discovery hidden tag {suffix}", TestContext.Current.CancellationToken);
        var visibleVideo = await owner.CreateVideoAsync($"Discovery visible video {suffix}", TestContext.Current.CancellationToken);
        var hiddenVideo = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Discovery hidden video {suffix}")
            .WithTags([hiddenTag])
            .Build(), TestContext.Current.CancellationToken);
        var duplicateTitle = $"Discovery duplicate {suffix}";
        var visibleDuplicateOne = await owner.CreateVideoAsync(duplicateTitle, TestContext.Current.CancellationToken);
        var visibleDuplicateTwo = await owner.CreateVideoAsync(duplicateTitle, TestContext.Current.CancellationToken);
        var hiddenDuplicate = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle(duplicateTitle)
            .WithTags([hiddenTag])
            .Build(), TestContext.Current.CancellationToken);
        var visiblePHashLeft = await owner.CreateVideoAsync($"Discovery pHash left {suffix}", TestContext.Current.CancellationToken);
        var visiblePHashRight = await owner.CreateVideoAsync($"Discovery pHash right {suffix}", TestContext.Current.CancellationToken);
        var hiddenPHashBridge = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Discovery pHash bridge {suffix}")
            .WithTags([hiddenTag])
            .Build(), TestContext.Current.CancellationToken);
        await AsDbUser().AttachVideoFileAsync(visiblePHashLeft.Id, 1, 1, new Dictionary<string, string> { ["phash"] = "0000000000000000" }, TestContext.Current.CancellationToken);
        await AsDbUser().AttachVideoFileAsync(hiddenPHashBridge.Id, 1, 1, new Dictionary<string, string> { ["phash"] = "0000000000000001" }, TestContext.Current.CancellationToken);
        await AsDbUser().AttachVideoFileAsync(visiblePHashRight.Id, 1, 1, new Dictionary<string, string> { ["phash"] = "0000000000000003" }, TestContext.Current.CancellationToken);
        var visibleSourceKey = $"visible-source-{suffix}";
        var hiddenSourceKey = $"hidden-source-{suffix}";
        var visibleSegment = await owner.CreateVideoSegmentAsync(visibleVideo, CreateSegment(visibleSourceKey), TestContext.Current.CancellationToken);
        var hiddenSegment = await owner.CreateVideoSegmentAsync(hiddenVideo, CreateSegment(hiddenSourceKey), TestContext.Current.CancellationToken);
        var visibleEmbeddingId = await AsDbUser().CreateEmbeddingAsync(EmbeddingHostType.Video, visibleVideo.Id, [0.8f, 0.2f, 0f], kindFamily, cancellationToken: TestContext.Current.CancellationToken);
        var hiddenEmbeddingId = await AsDbUser().CreateEmbeddingAsync(EmbeddingHostType.Video, hiddenVideo.Id, [1f, 0f, 0f], kindFamily, cancellationToken: TestContext.Current.CancellationToken);
        var visibleSegmentEmbeddingId = await AsDbUser().CreateEmbeddingAsync(EmbeddingHostType.Segment, visibleSegment.Id, [0.7f, 0.3f, 0f], kindFamily, cancellationToken: TestContext.Current.CancellationToken);
        var hiddenSegmentEmbeddingId = await AsDbUser().CreateEmbeddingAsync(EmbeddingHostType.Segment, hiddenSegment.Id, [1f, 0f, 0f], kindFamily, cancellationToken: TestContext.Current.CancellationToken);
        var visibleRunId = await AsDbUser().CreateCompletedAiRunAsync($"visible-run-{suffix}", AiRunTargetType.Video, visibleVideo.Id, DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddMinutes(-1), TestContext.Current.CancellationToken);
        var hiddenRunId = await AsDbUser().CreateCompletedAiRunAsync($"hidden-run-{suffix}", AiRunTargetType.Video, hiddenVideo.Id, DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddMinutes(-1), TestContext.Current.CancellationToken);

        var roleName = $"Derived discovery viewer {suffix}";
        var role = await owner.CreateRoleAsync(new CreateRoleRequest(
            roleName,
            "Reads derived discovery artifacts without restricted host disclosure.",
            [
                Permissions.VideosRead, Permissions.SegmentsRead,
                Permissions.EmbeddingsRead, Permissions.AiRunsRead, Permissions.AiDataRead,
                Permissions.SystemRead, Permissions.JobsRun, Permissions.JobsCancel, Permissions.AuditRead,
            ]), TestContext.Current.CancellationToken);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            role.Id, EntityKinds.Video, "deny", "tag", $"{{\"tagId\":{hiddenTag.Id}}}", "read"), TestContext.Current.CancellationToken);
        var username = $"derived-discovery-{suffix}";
        const string password = "Derived discovery password 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]), TestContext.Current.CancellationToken);
        using var session = await owner.CreateAuthSessionAsync(username, password, TestContext.Current.CancellationToken);
        var user = session.Client;

        var allowOnlyRoleName = $"Allow-only derived discovery {suffix}";
        var allowOnlyRole = await owner.CreateRoleAsync(new CreateRoleRequest(
            allowOnlyRoleName,
            "Exercises global discovery denial for allow-only read scopes.",
            [Permissions.JobsRead, Permissions.SystemRead, Permissions.AiDataRead, Permissions.AuditRead]), TestContext.Current.CancellationToken);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            allowOnlyRole.Id, EntityKinds.Video, "allow", "tag", $"{{\"tagId\":{hiddenTag.Id}}}", "read"), TestContext.Current.CancellationToken);
        var allowOnlyUsername = $"allow-only-discovery-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(allowOnlyUsername, password, Roles: [allowOnlyRoleName]), TestContext.Current.CancellationToken);
        using var allowOnlySession = await owner.CreateAuthSessionAsync(allowOnlyUsername, password, TestContext.Current.CancellationToken);
        await allowOnlySession.Client.AssertResponseAsync("/api/ai-data/summary", HttpStatusCode.Forbidden, TestContext.Current.CancellationToken);
        await allowOnlySession.Client.AssertResponseAsync("/api/system/stats", HttpStatusCode.Forbidden, TestContext.Current.CancellationToken);
        await allowOnlySession.Client.AssertResponseAsync("/api/jobs", HttpStatusCode.OK, TestContext.Current.CancellationToken);
        (await GetHubConnectionOutcomeAsync(allowOnlySession.Client, "/hubs/jobs")).Should().Be(HubConnectionOutcome.Established);
        (await GetHubConnectionOutcomeAsync(allowOnlySession.Client, "/hubs/logs")).Should().Be(HubConnectionOutcome.Rejected);

        var wall = await user.GetVideoWallAsync(suffix, 100, TestContext.Current.CancellationToken);
        wall.Select(video => video.Id).Should().Contain([visibleVideo.Id, visibleDuplicateOne.Id, visibleDuplicateTwo.Id]);
        wall.Select(video => video.Id).Should().NotContain([hiddenVideo.Id, hiddenDuplicate.Id]);
        var duplicateGroup = (await user.FindDuplicateVideosAsync("title", cancellationToken: TestContext.Current.CancellationToken))
            .Should().ContainSingle(group => group.Any(video => video.Id == visibleDuplicateOne.Id)).Which;
        duplicateGroup.Select(video => video.Id).Should().BeEquivalentTo([visibleDuplicateOne.Id, visibleDuplicateTwo.Id]);
        (await user.FindDuplicateVideosAsync("phash", distance: 1, cancellationToken: TestContext.Current.CancellationToken))
            .Should().NotContain(group => group.Any(video => video.Id == visiblePHashLeft.Id || video.Id == visiblePHashRight.Id));
        var ownedDuplicateSearchJobs = (await user.GetJobHistoryAsync(TestContext.Current.CancellationToken))
            .Where(job => job.Type == "duplicate-search")
            .ToArray();
        ownedDuplicateSearchJobs.Should().HaveCount(2);
        ownedDuplicateSearchJobs.Should().OnlyContain(job => job.Status == JobStatus.Completed);
        foreach (var ownedJob in ownedDuplicateSearchJobs)
            (await user.GetJobAsync(ownedJob.Id, TestContext.Current.CancellationToken)).Id.Should().Be(ownedJob.Id);
        var sourceKeys = await user.GetDistinctSegmentSourceKeysAsync(TestContext.Current.CancellationToken);
        sourceKeys.Should().ContainSingle(item => item.Value == visibleSourceKey && item.Count == 1);
        sourceKeys.Should().NotContain(item => item.Value == hiddenSourceKey);

        var page = await user.GetEmbeddingsAsync(EmbeddingHostType.Video, TestContext.Current.CancellationToken);
        page.Items.Select(item => item.Id).Should().Contain(visibleEmbeddingId).And.NotContain(hiddenEmbeddingId);
        page.Items.Select(item => item.HostId).Should().Contain(visibleVideo.Id).And.NotContain(hiddenVideo.Id);
        var segmentPage = await user.GetEmbeddingsAsync(EmbeddingHostType.Segment, TestContext.Current.CancellationToken);
        segmentPage.Items.Select(item => item.Id).Should().Contain(visibleSegmentEmbeddingId).And.NotContain(hiddenSegmentEmbeddingId);
        (await user.GetEmbeddingAsync(visibleEmbeddingId, TestContext.Current.CancellationToken)).HostId.Should().Be(visibleVideo.Id);
        await user.AssertResponseAsync($"/api/embeddings/{hiddenEmbeddingId}", HttpStatusCode.NotFound, TestContext.Current.CancellationToken);
        await user.AssertResponseAsync("/api/embeddings/2147483647", HttpStatusCode.NotFound, TestContext.Current.CancellationToken);
        var runs = await user.GetAiRunsAsync(AiRunTargetType.Video, TestContext.Current.CancellationToken);
        runs.Items.Select(run => run.Id).Should().Contain(visibleRunId).And.NotContain(hiddenRunId);
        runs.Items.Select(run => run.TargetId).Should().Contain(visibleVideo.Id).And.NotContain(hiddenVideo.Id);
        (await user.GetAiRunAsync(visibleRunId, TestContext.Current.CancellationToken)).TargetId.Should().Be(visibleVideo.Id);
        await user.AssertResponseAsync($"/api/ai-runs/{hiddenRunId}", HttpStatusCode.NotFound, TestContext.Current.CancellationToken);
        await user.AssertResponseAsync("/api/ai-runs/2147483647", HttpStatusCode.NotFound, TestContext.Current.CancellationToken);
        await user.AssertResponseAsync("/api/ai-data/summary", HttpStatusCode.Forbidden, TestContext.Current.CancellationToken);
        await user.AssertResponseAsync("/api/system/stats", HttpStatusCode.Forbidden, TestContext.Current.CancellationToken);
        await user.AssertResponseAsync("/api/jobs", HttpStatusCode.OK, TestContext.Current.CancellationToken);
        await user.AssertResponseAsync("/api/jobs/history", HttpStatusCode.OK, TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Delete, "/api/jobs/scoped-job", HttpStatusCode.NotFound, cancellationToken: TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Put, "/api/jobs/scoped-job/reorder", HttpStatusCode.NotFound, new { }, TestContext.Current.CancellationToken);
        (await GetHubConnectionOutcomeAsync(user, "/hubs/jobs")).Should().Be(HubConnectionOutcome.Established);
        (await GetHubConnectionOutcomeAsync(user, "/hubs/logs")).Should().Be(HubConnectionOutcome.Rejected);
        (await GetHubConnectionOutcomeAsync(AsAnonymous(), "/hubs/logs")).Should().Be(HubConnectionOutcome.Rejected);

        var results = await user.SearchEmbeddingsAsync(new EmbeddingSearchRequestDto(
            QueryText: null,
            QueryVector: [1f, 0f, 0f],
            Kind: null,
            KindFamily: kindFamily,
            HostType: EmbeddingHostType.Video,
            HostId: null,
            Modality: EmbeddingModality.Visual,
            IsSemantic: true,
            SourceKey: "api-test",
            K: 10), TestContext.Current.CancellationToken);
        results.Select(result => result.EmbeddingId).Should().Equal(visibleEmbeddingId);
        results.Select(result => result.HostId).Should().Equal(visibleVideo.Id);
        var nearestVisible = await user.SearchEmbeddingsAsync(new EmbeddingSearchRequestDto(
            null, [1f, 0f, 0f], null, kindFamily, EmbeddingHostType.Video, null,
            EmbeddingModality.Visual, true, "api-test", 1), TestContext.Current.CancellationToken);
        nearestVisible.Should().ContainSingle().Which.EmbeddingId.Should().Be(visibleEmbeddingId);

        var ownerPage = await owner.GetEmbeddingsAsync(EmbeddingHostType.Video, TestContext.Current.CancellationToken);
        ownerPage.Items.Select(item => item.Id).Should().Contain([visibleEmbeddingId, hiddenEmbeddingId]);
        (await owner.GetEmbeddingsAsync(EmbeddingHostType.Segment, TestContext.Current.CancellationToken)).Items.Select(item => item.Id)
            .Should().Contain([visibleSegmentEmbeddingId, hiddenSegmentEmbeddingId]);
        var ownerResults = await owner.SearchEmbeddingsAsync(new EmbeddingSearchRequestDto(
            null, [1f, 0f, 0f], null, kindFamily, EmbeddingHostType.Video, null,
            EmbeddingModality.Visual, true, "api-test", 10), TestContext.Current.CancellationToken);
        ownerResults.Select(result => result.EmbeddingId).Should().Contain([visibleEmbeddingId, hiddenEmbeddingId]);
        (await owner.GetAiRunsAsync(AiRunTargetType.Video, TestContext.Current.CancellationToken)).Items.Select(run => run.Id)
            .Should().Contain([visibleRunId, hiddenRunId]);
        await owner.AssertResponseAsync("/api/ai-data/summary", cancellationToken: TestContext.Current.CancellationToken);
        await owner.AssertResponseAsync("/api/system/stats", cancellationToken: TestContext.Current.CancellationToken);
        await owner.AssertResponseAsync("/api/jobs", cancellationToken: TestContext.Current.CancellationToken);
        (await GetHubConnectionOutcomeAsync(owner, "/hubs/jobs")).Should().Be(HubConnectionOutcome.Established);
        (await GetHubConnectionOutcomeAsync(owner, "/hubs/logs")).Should().Be(HubConnectionOutcome.Established);
        var ownerDuplicateGroup = (await owner.FindDuplicateVideosAsync("title", cancellationToken: TestContext.Current.CancellationToken))
            .Should().ContainSingle(group => group.Any(video => video.Id == visibleDuplicateOne.Id)).Which;
        ownerDuplicateGroup.Select(video => video.Id).Should().BeEquivalentTo(
            [visibleDuplicateOne.Id, visibleDuplicateTwo.Id, hiddenDuplicate.Id]);
        (await owner.FindDuplicateVideosAsync("phash", distance: 1, cancellationToken: TestContext.Current.CancellationToken))
            .Should().ContainSingle(group => group.Any(video => video.Id == hiddenPHashBridge.Id)).Which
            .Select(video => video.Id).Should().BeEquivalentTo(
                [visiblePHashLeft.Id, hiddenPHashBridge.Id, visiblePHashRight.Id]);
        (await owner.GetDistinctSegmentSourceKeysAsync(TestContext.Current.CancellationToken)).Should().Contain(item => item.Value == hiddenSourceKey && item.Count == 1);
    }

    private static SegmentCreateDto CreateSegment(string sourceKey)
        => new(0, 1, null, "chapter", null, null, sourceKey, null, null, sourceKey, null);

    private static async Task<HubConnectionOutcome> GetHubConnectionOutcomeAsync(CoveClient client, string path)
    {
        var uri = new UriBuilder(client.BaseAddress)
        {
            Scheme = client.BaseAddress.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = path,
            Query = $"access_token={Uri.EscapeDataString(client.AccessToken)}",
        }.Uri;
        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await socket.ConnectAsync(uri, timeout.Token);
            var handshake = Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e");
            await socket.SendAsync(handshake, WebSocketMessageType.Text, true, timeout.Token);
            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open && !timeout.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(buffer, timeout.Token);
                if (received.MessageType == WebSocketMessageType.Close)
                    return HubConnectionOutcome.Rejected;
                var payload = Encoding.UTF8.GetString(buffer, 0, received.Count);
                if (payload.Contains("ConnectionEstablished", StringComparison.Ordinal))
                    return HubConnectionOutcome.Established;
            }
        }
        catch (OperationCanceledException)
        {
            return HubConnectionOutcome.TimedOut;
        }
        catch (WebSocketException)
        {
            return HubConnectionOutcome.Rejected;
        }

        return HubConnectionOutcome.Rejected;
    }

    private enum HubConnectionOutcome
    {
        Established,
        Rejected,
        TimedOut,
    }
}
