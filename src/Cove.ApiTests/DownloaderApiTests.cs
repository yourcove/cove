using System.Net;
using System.Text;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Interfaces;
using Xunit.Abstractions;

namespace Cove.ApiTests;

[Collection(ApiTestLane2Collection.Name)]
public sealed class DownloaderApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    private const string DirectTextDownloaderId = "builtin.direct-file/text";

    [Fact]
    public async Task GivenRemoteTextFile_WhenDownloaderUrlIsMatched_ThenDirectTextDownloaderIsSelected()
    {
        var source = AsDownloadSource().CreateTextFile("matching-example.txt", "Matching content");

        var downloaders = await AsUser().GetDownloadersAsync();
        var matches = await AsUser().MatchDownloaderAsync(source.Uri);

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
    public async Task GivenRemoteTextFile_WhenDownloadCompletes_ThenTextIsImportedAndDuplicateIsDetected()
    {
        const string contents = "A deterministic API-test download.";
        var source = AsDownloadSource().CreateTextFile("downloaded-example.txt", contents);

        var jobId = await AsUser().StartTextDownloadAsync(DirectTextDownloaderId, source.Uri);
        var job = await AsUser().WaitForTerminalJobAsync(jobId);

        job.Status.Should().Be(JobStatus.Completed);
        source.RequestCount.Should().Be(1);
        var text = (await AsUser().GetTextsAsync()).Should().ContainSingle().Which;
        text.Urls.Should().Contain(source.Uri.AbsoluteUri);
        var textFile = text.Files.Should().ContainSingle().Which;
        textFile.Basename.Should().Be(source.FileName);
        Path.GetDirectoryName(Path.GetFullPath(textFile.Path)).Should().Be(
            Path.GetFullPath(Path.Combine(AsTestFileSystem().LibraryPath, "_downloads", "texts")));
        var downloaded = await AsUser().GetTextFileAsync(text);
        Encoding.UTF8.GetString(downloaded.Content).Should().Be(contents);
        downloaded.MediaType.Should().Be("text/plain");
        var preflight = await AsUser().PreflightDownloadAsync(source.Uri, "Text");
        preflight.IsDuplicate.Should().BeTrue();
        preflight.DuplicateReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GivenUnavailableRemoteTextFile_WhenDownloadRuns_ThenJobFailsWithoutImport()
    {
        var source = AsDownloadSource().CreateFailure("unavailable-example.txt", HttpStatusCode.ServiceUnavailable);

        var jobId = await AsUser().StartTextDownloadAsync(DirectTextDownloaderId, source.Uri);
        var job = await AsUser().WaitForTerminalJobAsync(jobId);

        job.Status.Should().Be(JobStatus.Failed);
        job.Error.Should().NotBeNullOrWhiteSpace();
        source.RequestCount.Should().Be(1);
        (await AsUser().GetTextsAsync()).Should().BeEmpty();
    }
}
