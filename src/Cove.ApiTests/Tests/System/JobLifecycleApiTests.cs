using System.Net;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.System;

public sealed class JobLifecycleApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/jobs/history")]
    [CoversEndpoint("POST", "/api/jobs/scan")]
    [CoversEndpoint("POST", "/api/jobs/generate-thumbnails")]
    [CoversEndpoint("POST", "/api/jobs/generate-video-phashes")]
    [CoversEndpoint("POST", "/api/jobs/generate-image-phashes")]
    [CoversEndpoint("POST", "/api/jobs/clean")]
    public async Task GivenEmptyLibrary_WhenJobsRun_ThenPermissionsCompletionAndHistoryAreExact()
    {
        var viewerUsername = $"jobs-viewer-{Guid.NewGuid():N}";
        const string viewerPassword = "Jobs viewer password 123!";
        await AsUser().CreateUserAsync(new CreateUserRequest(
            viewerUsername,
            viewerPassword,
            Roles: [BuiltinRoles.Viewer]), TestContext.Current.CancellationToken);
        using var viewerSession = await AsUser().CreateAuthSessionAsync(viewerUsername, viewerPassword, TestContext.Current.CancellationToken);

        (await AsUser().GetJobHistoryAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await viewerSession.Client.GetJobHistoryAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await AsUser().ReadEndpointAsync(ReadEndpoint.Jobs, TestContext.Current.CancellationToken)).EnumerateArray().Should().BeEmpty();

        (await SendForStatusAsync(viewerSession.Client, "/api/jobs/scan"))
            .Should().Be(HttpStatusCode.Forbidden);
        (await SendForStatusAsync(AsUser(ApiTestUsers.Eva), "/api/jobs/clean?dryRun=true"))
            .Should().Be(HttpStatusCode.Forbidden);
        (await AsUser().GetJobHistoryAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await AsUser().ReadEndpointAsync(ReadEndpoint.Jobs, TestContext.Current.CancellationToken)).EnumerateArray().Should().BeEmpty();

        var completed = new List<JobInfo>();
        completed.Add(await WaitForCompletedJobAsync(
            await AsUser(ApiTestUsers.Eva).StartLibraryScanJobAsync(TestContext.Current.CancellationToken),
            "scan",
            "Scanning library"));
        completed.Add(await WaitForCompletedJobAsync(
            await AsUser(ApiTestUsers.Eva).StartThumbnailGenerationJobAsync(TestContext.Current.CancellationToken),
            "generate_thumbnails",
            "Generating thumbnails"));
        completed.Add(await WaitForCompletedJobAsync(
            await AsUser(ApiTestUsers.Eva).StartVideoPhashGenerationJobAsync(TestContext.Current.CancellationToken),
            "generate_video_phashes",
            "Generating video pHashes"));
        completed.Add(await WaitForCompletedJobAsync(
            await AsUser(ApiTestUsers.Eva).StartImagePhashGenerationJobAsync(TestContext.Current.CancellationToken),
            "generate_image_phashes",
            "Generating image pHashes"));
        completed.Add(await WaitForCompletedJobAsync(
            await AsUser().StartLibraryCleanJobAsync(dryRun: true, cancellationToken: TestContext.Current.CancellationToken),
            "clean",
            "Cleaning (dry run)"));

        var expectedHistory = completed.AsEnumerable().Reverse().ToArray();
        AssertHistory(await AsUser().GetJobHistoryAsync(TestContext.Current.CancellationToken), expectedHistory);
        AssertHistory(await viewerSession.Client.GetJobHistoryAsync(TestContext.Current.CancellationToken), expectedHistory);
    }

    private async Task<JobInfo> WaitForCompletedJobAsync(
        string jobId,
        string expectedType,
        string expectedDescription)
    {
        jobId.Should().NotBeNullOrWhiteSpace();
        var job = await AsUser().WaitForTerminalJobAsync(jobId);
        job.Id.Should().Be(jobId);
        job.Type.Should().Be(expectedType);
        job.Description.Should().Be(expectedDescription);
        job.Status.Should().Be(JobStatus.Completed);
        job.Progress.Should().Be(1);
        job.Error.Should().BeNull();
        job.CompletedAt.Should().NotBeNull().And.BeOnOrAfter(job.StartedAt);
        return job;
    }

    private static void AssertHistory(
        IReadOnlyList<JobInfo> actual,
        IReadOnlyList<JobInfo> expected)
    {
        actual.Select(job => job.Id).Should().Equal(expected.Select(job => job.Id));
        actual.Select(job => job.Type).Should().Equal(expected.Select(job => job.Type));
        actual.Select(job => job.Description).Should().Equal(expected.Select(job => job.Description));
        actual.Should().OnlyContain(job =>
            job.Status == JobStatus.Completed
            && job.Progress == 1
            && job.Error == null
            && job.CompletedAt.HasValue);
    }

    private static async Task<HttpStatusCode> SendForStatusAsync(
        CoveClient client,
        string requestUri)
    {
        using var httpClient = client.CreateHttpClient();
        using var response = await httpClient.PostAsync(requestUri, content: null);
        return response.StatusCode;
    }
}
