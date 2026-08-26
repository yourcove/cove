using System.Net;
using System.Text;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Downloads;

public sealed class DownloaderApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    private const string DirectTextDownloaderId = "builtin.direct-file/text";

    [Fact]
    [CoversEndpoint("GET", "/api/system/downloaders")]
    [CoversEndpoint("POST", "/api/system/downloaders/match")]
    public async Task GivenRemoteTextFile_WhenDownloaderUrlIsMatched_ThenDirectTextDownloaderIsSelected()
    {
        // Arrange
        var source = AsDownloadSource().CreateTextFile("matching-example.txt", "Matching content");

        // Act
        var downloaders = await AsUser().GetDownloadersAsync(TestContext.Current.CancellationToken);
        var matches = await AsUser().MatchDownloaderAsync(source.Uri, TestContext.Current.CancellationToken);

        // Assert
        var downloader = downloaders.Should().ContainSingle(candidate => candidate.Id == DirectTextDownloaderId).Which;
        downloader.SupportedEntity.Should().Be("Text");
        var match = matches.Should().ContainSingle(candidate => candidate.DownloaderId == DirectTextDownloaderId).Which;
        match.DownloaderName.Should().Be(downloader.Name);
        match.SupportedEntity.Should().Be("Text");
        match.NormalizedUrl.Should().Be(source.Uri.AbsoluteUri);
        match.Label.Should().Be(source.FileName);
        match.QualityOptions.Should().BeEmpty();
        source.RequestCount.Should().Be(0);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/system/downloaders/download")]
    [CoversEndpoint("GET", "/api/jobs/{jobId}")]
    [CoversEndpoint("GET", "/api/texts/{id:int}/file")]
    [CoversEndpoint("POST", "/api/system/downloaders/preflight")]
    public async Task GivenRemoteTextFile_WhenDownloadCompletes_ThenTextIsImportedAndDuplicateIsDetected()
    {
        // Arrange
        const string contents = "A deterministic API-test download.";
        var source = AsDownloadSource().CreateTextFile("downloaded-example.txt", contents);

        // Act
        var jobId = await AsUser().StartTextDownloadAsync(DirectTextDownloaderId, source.Uri, TestContext.Current.CancellationToken);
        var job = await AsUser().WaitForTerminalJobAsync(jobId, TestContext.Current.CancellationToken);

        // Assert
        job.Status.Should().Be(JobStatus.Completed);
        source.RequestCount.Should().Be(1);
        var text = (await AsUser().GetTextsAsync(TestContext.Current.CancellationToken)).Should().ContainSingle().Which;
        text.Urls.Should().Contain(source.Uri.AbsoluteUri);
        var textFile = text.Files.Should().ContainSingle().Which;
        textFile.Basename.Should().Be(source.FileName);
        Path.GetDirectoryName(Path.GetFullPath(textFile.Path)).Should().Be(
            Path.GetFullPath(Path.Combine(AsTestFileSystem().LibraryPath, "_downloads", "texts")));
        var downloaded = await AsUser().GetTextFileAsync(text, TestContext.Current.CancellationToken);
        Encoding.UTF8.GetString(downloaded.Content).Should().Be(contents);
        downloaded.MediaType.Should().Be("text/plain");
        var preflight = await AsUser().PreflightDownloadAsync(source.Uri, "Text", cancellationToken: TestContext.Current.CancellationToken);
        preflight.IsDuplicate.Should().BeTrue();
        preflight.DuplicateReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GivenUnavailableRemoteTextFile_WhenDownloadRuns_ThenJobFailsWithoutImport()
    {
        // Arrange
        var source = AsDownloadSource().CreateFailure("unavailable-example.txt", HttpStatusCode.ServiceUnavailable);

        // Act
        var jobId = await AsUser().StartTextDownloadAsync(DirectTextDownloaderId, source.Uri, TestContext.Current.CancellationToken);
        var job = await AsUser().WaitForTerminalJobAsync(jobId, TestContext.Current.CancellationToken);

        // Assert
        job.Status.Should().Be(JobStatus.Failed);
        job.Error.Should().NotBeNullOrWhiteSpace();
        source.RequestCount.Should().Be(1);
        (await AsUser().GetTextsAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    [CoversEndpoint("POST", "/api/system/downloaders/download-batch")]
    public async Task GivenTwoRemoteTextFiles_WhenBatchDownloadCompletes_ThenBothTextsAreImported()
    {
        // Arrange
        var firstSource = AsDownloadSource().CreateTextFile("batch-first.txt", "First batch document");
        var secondSource = AsDownloadSource().CreateTextFile("batch-second.txt", "Second batch document");

        // Act
        var batch = await AsUser().StartDownloaderBatchAsync(new DownloaderBatchStartRequestDto
        {
            Items =
            [
                CreateTextBatchItem(firstSource),
                CreateTextBatchItem(secondSource),
            ],
        }, TestContext.Current.CancellationToken);

        // Assert
        batch.QueuedCount.Should().Be(2);
        batch.Issues.Should().BeEmpty();
        batch.JobId.Should().NotBeNullOrWhiteSpace();
        var job = await AsUser().WaitForTerminalJobAsync(batch.JobId!, TestContext.Current.CancellationToken);

        job.Status.Should().Be(JobStatus.Completed);
        job.SubTask.Should().Contain("Downloaded 2 of 2 items");
        firstSource.RequestCount.Should().Be(1);
        secondSource.RequestCount.Should().Be(1);
        var texts = await AsUser().GetTextsAsync(TestContext.Current.CancellationToken);
        texts.Should().HaveCount(2);
        texts.SelectMany(text => text.Urls).Should().Contain(firstSource.Uri.AbsoluteUri);
        texts.SelectMany(text => text.Urls).Should().Contain(secondSource.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task GivenRepeatedRemoteTextUrl_WhenBatchIsPreflighted_ThenOnlyOneItemIsQueued()
    {
        // Arrange
        var source = AsDownloadSource().CreateTextFile("batch-repeated.txt", "Repeated batch document");
        var trackingVariant = new Uri($"{source.Uri.AbsoluteUri}?utm_source=api-test#duplicate");

        // Act
        var batch = await AsUser().StartDownloaderBatchAsync(new DownloaderBatchStartRequestDto
        {
            Items =
            [
                CreateTextBatchItem(source),
                CreateTextBatchItem(source, trackingVariant),
            ],
        }, TestContext.Current.CancellationToken);

        // Assert
        batch.QueuedCount.Should().Be(1);
        batch.Issues.Should().ContainSingle(issue =>
            issue.Kind == "skipped"
            && issue.Reason.Contains("already queued", StringComparison.OrdinalIgnoreCase));
        batch.JobId.Should().NotBeNullOrWhiteSpace();
        var job = await AsUser().WaitForTerminalJobAsync(batch.JobId!, TestContext.Current.CancellationToken);

        job.Status.Should().Be(JobStatus.Completed);
        source.RequestCount.Should().Be(1);
        (await AsUser().GetTextsAsync(TestContext.Current.CancellationToken)).Should().ContainSingle();
    }

    [Fact]
    public async Task GivenPreviouslyDownloadedText_WhenBatchIsPreflighted_ThenOnlyNewTextIsImported()
    {
        // Arrange
        var existingSource = AsDownloadSource().CreateTextFile("batch-existing.txt", "Existing batch document");
        var newSource = AsDownloadSource().CreateTextFile("batch-new.txt", "New batch document");
        var initialJobId = await AsUser().StartTextDownloadAsync(DirectTextDownloaderId, existingSource.Uri, TestContext.Current.CancellationToken);
        (await AsUser().WaitForTerminalJobAsync(initialJobId, TestContext.Current.CancellationToken)).Status.Should().Be(JobStatus.Completed);

        // Act
        var batch = await AsUser().StartDownloaderBatchAsync(new DownloaderBatchStartRequestDto
        {
            Items =
            [
                CreateTextBatchItem(existingSource),
                CreateTextBatchItem(newSource),
            ],
        }, TestContext.Current.CancellationToken);

        // Assert
        batch.QueuedCount.Should().Be(1);
        batch.Issues.Should().ContainSingle(issue =>
            issue.Kind == "skipped"
            && issue.Reason.Contains("already downloaded", StringComparison.OrdinalIgnoreCase));
        batch.JobId.Should().NotBeNullOrWhiteSpace();
        var job = await AsUser().WaitForTerminalJobAsync(batch.JobId!, TestContext.Current.CancellationToken);

        job.Status.Should().Be(JobStatus.Completed);
        existingSource.RequestCount.Should().Be(1);
        newSource.RequestCount.Should().Be(1);
        var texts = await AsUser().GetTextsAsync(TestContext.Current.CancellationToken);
        texts.Should().HaveCount(2);
        texts.SelectMany(text => text.Urls).Should().Contain(existingSource.Uri.AbsoluteUri);
        texts.SelectMany(text => text.Urls).Should().Contain(newSource.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task GivenSuccessfulAndUnavailableRemoteTexts_WhenBatchCompletes_ThenFailureIsReportedAndSuccessfulTextIsImported()
    {
        // Arrange
        var successfulSource = AsDownloadSource().CreateTextFile("batch-success.txt", "Successful mixed batch document");
        var unavailableSource = AsDownloadSource().CreateFailure("batch-unavailable.txt", HttpStatusCode.ServiceUnavailable);

        // Act
        var batch = await AsUser().StartDownloaderBatchAsync(new DownloaderBatchStartRequestDto
        {
            Items =
            [
                CreateTextBatchItem(successfulSource),
                CreateTextBatchItem(unavailableSource),
            ],
        }, TestContext.Current.CancellationToken);

        // Assert
        batch.QueuedCount.Should().Be(2);
        batch.Issues.Should().BeEmpty();
        batch.JobId.Should().NotBeNullOrWhiteSpace();
        var job = await AsUser().WaitForTerminalJobAsync(batch.JobId!, TestContext.Current.CancellationToken);

        job.Status.Should().Be(JobStatus.Completed);
        job.Error.Should().BeNull();
        job.SubTask.Should().Contain("Downloaded 1 of 2 items");
        job.SubTask.Should().Contain("Failed 1");
        successfulSource.RequestCount.Should().Be(1);
        unavailableSource.RequestCount.Should().Be(1);
        var text = (await AsUser().GetTextsAsync(TestContext.Current.CancellationToken)).Should().ContainSingle().Which;
        text.Urls.Should().Contain(successfulSource.Uri.AbsoluteUri);
        text.Urls.Should().NotContain(unavailableSource.Uri.AbsoluteUri);
    }

    private static DownloaderBatchItemDto CreateTextBatchItem(
        DownloadSourceHandle source,
        Uri? uri = null)
        => new()
        {
            Url = (uri ?? source.Uri).AbsoluteUri,
            Entity = "Text",
            Label = source.FileName,
        };
}
