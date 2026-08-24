using System.Net;
using System.Net.Http.Json;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.System;

[Collection(ApiTestLane2Collection.Name)]
public sealed class JobQueueControlApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("DELETE", "/api/jobs/{jobId}")]
    [CoversEndpoint("PUT", "/api/jobs/{jobId}/reorder")]
    public async Task GivenBlockedExclusiveWork_WhenQueuedJobsAreReorderedAndCancelled_ThenQueueAndHistoryAreExact()
    {
        var remotePerformer = AsMetadataService().CreatePerformer(
            new MetadataServicePerformerBuilder().Build());
        var localPerformer = await AsUser().CreatePerformerAsync(new PerformerBuilder()
                .WithName(remotePerformer.Performer.Name)
                .Build(), TestContext.Current.CancellationToken);
        var viewerUsername = $"job-control-viewer-{Guid.NewGuid():N}";
        const string viewerPassword = "Job control viewer password 123!";
        await AsUser().CreateUserAsync(new CreateUserRequest(
            viewerUsername,
            viewerPassword,
            Roles: [BuiltinRoles.Viewer]), TestContext.Current.CancellationToken);
        using var viewerSession = await AsUser().CreateAuthSessionAsync(viewerUsername, viewerPassword, TestContext.Current.CancellationToken);

        using var requestGate = AsMetadataService().HoldNextRequestContaining("query SearchPerformer");
        var blockingJob = await AsUser(ApiTestUsers.Eva).StartPerformerMetadataBatchTagAsync(new MetadataServerPerformerBatchTagRequestDto
            {
                Endpoint = remotePerformer.Endpoint.AbsoluteUri,
                Ids = [localPerformer.Id],
                RefreshAlreadyTagged = true,
            }, TestContext.Current.CancellationToken);
        blockingJob.ItemCount.Should().Be(1);
        await requestGate.WaitUntilBlockedAsync(TestContext.Current.CancellationToken);
        (await AsUser().GetJobAsync(blockingJob.JobId, TestContext.Current.CancellationToken)).Status.Should().Be(JobStatus.Running);

        var scanJobId = await AsUser(ApiTestUsers.Eva).StartLibraryScanJobAsync(TestContext.Current.CancellationToken);
        var thumbnailJobId = await AsUser(ApiTestUsers.Eva).StartThumbnailGenerationJobAsync(TestContext.Current.CancellationToken);
        var phashJobId = await AsUser(ApiTestUsers.Eva).StartImagePhashGenerationJobAsync(TestContext.Current.CancellationToken);

        AssertLiveQueue(
            await AsUser().GetJobsAsync(TestContext.Current.CancellationToken),
            (blockingJob.JobId, JobStatus.Running),
            (scanJobId, JobStatus.Pending),
            (thumbnailJobId, JobStatus.Pending),
            (phashJobId, JobStatus.Pending));

        (await SendJobControlForStatusAsync(
            viewerSession.Client,
            HttpMethod.Delete,
            $"/api/jobs/{scanJobId}",
            payload: null)).Should().Be(HttpStatusCode.Forbidden);
        (await SendJobControlForStatusAsync(
            viewerSession.Client,
            HttpMethod.Put,
            $"/api/jobs/{phashJobId}/reorder",
            new { BeforeJobId = scanJobId })).Should().Be(HttpStatusCode.Forbidden);
        (await SendJobControlForStatusAsync(
            AsUser(),
            HttpMethod.Delete,
            "/api/jobs/missing-job",
            payload: null)).Should().Be(HttpStatusCode.NotFound);
        (await SendJobControlForStatusAsync(
            AsUser(),
            HttpMethod.Put,
            $"/api/jobs/{blockingJob.JobId}/reorder",
            new { BeforeJobId = scanJobId })).Should().Be(HttpStatusCode.NotFound);

        AssertLiveQueue(
            await AsUser().GetJobsAsync(TestContext.Current.CancellationToken),
            (blockingJob.JobId, JobStatus.Running),
            (scanJobId, JobStatus.Pending),
            (thumbnailJobId, JobStatus.Pending),
            (phashJobId, JobStatus.Pending));
        (await AsUser().GetJobHistoryAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();

        await AsUser().ReorderJobAsync(phashJobId, scanJobId, TestContext.Current.CancellationToken);
        AssertLiveQueue(
            await AsUser().GetJobsAsync(TestContext.Current.CancellationToken),
            (blockingJob.JobId, JobStatus.Running),
            (phashJobId, JobStatus.Pending),
            (scanJobId, JobStatus.Pending),
            (thumbnailJobId, JobStatus.Pending));

        await AsUser().CancelJobAsync(scanJobId, TestContext.Current.CancellationToken);
        AssertCancelled(await WaitForHistoryEntryAsync(scanJobId), scanJobId, "scan");
        AssertLiveQueue(
            await AsUser().GetJobsAsync(TestContext.Current.CancellationToken),
            (blockingJob.JobId, JobStatus.Running),
            (phashJobId, JobStatus.Pending),
            (thumbnailJobId, JobStatus.Pending));
        (await SendJobControlForStatusAsync(
            AsUser(),
            HttpMethod.Delete,
            $"/api/jobs/{scanJobId}",
            payload: null)).Should().Be(HttpStatusCode.NotFound);
        (await SendJobControlForStatusAsync(
            AsUser(),
            HttpMethod.Put,
            $"/api/jobs/{scanJobId}/reorder",
            new { BeforeJobId = thumbnailJobId })).Should().Be(HttpStatusCode.NotFound);

        await AsUser().CancelJobAsync(blockingJob.JobId, TestContext.Current.CancellationToken);
        requestGate.Release();
        AssertCancelled(
            await WaitForHistoryEntryAsync(blockingJob.JobId),
            blockingJob.JobId,
            "metadata-server:performers");

        var completedPhash = await WaitForHistoryEntryAsync(phashJobId);
        AssertCompleted(completedPhash, phashJobId, "generate_image_phashes");
        var completedThumbnail = await WaitForHistoryEntryAsync(thumbnailJobId);
        AssertCompleted(completedThumbnail, thumbnailJobId, "generate_thumbnails");

        (await AsUser().GetJobsAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
        var history = await AsUser().GetJobHistoryAsync(TestContext.Current.CancellationToken);
        history.Select(job => job.Id).Should().Equal(
            thumbnailJobId,
            phashJobId,
            blockingJob.JobId,
            scanJobId);
        history.Select(job => job.Status).Should().Equal(
            JobStatus.Completed,
            JobStatus.Completed,
            JobStatus.Cancelled,
            JobStatus.Cancelled);
        (await viewerSession.Client.GetJobHistoryAsync(TestContext.Current.CancellationToken)).Select(job => job.Id)
            .Should().Equal(history.Select(job => job.Id));

        var unchangedPerformer = await AsUser().GetPerformerByIdAsync(localPerformer.Id, TestContext.Current.CancellationToken);
        unchangedPerformer.Name.Should().Be(localPerformer.Name);
        unchangedPerformer.RemoteIds.Should().BeEmpty();
    }

    private async Task<JobInfo> WaitForHistoryEntryAsync(string jobId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        JobInfo? match = null;
        try
        {
            while (match is null)
            {
                match = (await AsUser().GetJobHistoryAsync(timeout.Token))
                    .SingleOrDefault(job => job.Id == jobId);
                if (match is null)
                    await Task.Delay(100, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"Job '{jobId}' did not move to history within 15 seconds.");
        }

        return match;
    }

    private static void AssertLiveQueue(
        IReadOnlyList<JobInfo> jobs,
        params (string Id, JobStatus Status)[] expected)
    {
        jobs.Select(job => job.Id).Should().Equal(expected.Select(job => job.Id));
        jobs.Select(job => job.Status).Should().Equal(expected.Select(job => job.Status));
    }

    private static void AssertCancelled(JobInfo job, string expectedId, string expectedType)
    {
        job.Id.Should().Be(expectedId);
        job.Type.Should().Be(expectedType);
        job.Status.Should().Be(JobStatus.Cancelled);
        job.CompletedAt.Should().NotBeNull().And.BeOnOrAfter(job.StartedAt);
        job.Error.Should().BeNull();
    }

    private static void AssertCompleted(JobInfo job, string expectedId, string expectedType)
    {
        job.Id.Should().Be(expectedId);
        job.Type.Should().Be(expectedType);
        job.Status.Should().Be(JobStatus.Completed);
        job.Progress.Should().Be(1);
        job.CompletedAt.Should().NotBeNull().And.BeOnOrAfter(job.StartedAt);
        job.Error.Should().BeNull();
    }

    private static async Task<HttpStatusCode> SendJobControlForStatusAsync(
        CoveClient client,
        HttpMethod method,
        string requestUri,
        object? payload)
    {
        using var request = new HttpRequestMessage(method, requestUri);
        if (payload is not null)
            request.Content = JsonContent.Create(payload, options: ApiJson.Options);
        using var httpClient = client.CreateHttpClient();
        using var response = await httpClient.SendAsync(request);
        return response.StatusCode;
    }
}
