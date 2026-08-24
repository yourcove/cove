using System.Net;
using System.Text;
using Cove.ApiTests.Infrastructure;
using Cove.ApiTests.Builders;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Tests.System;

[Collection(ApiTestLane2Collection.Name)]
public sealed class SystemControllerApiTests(ITestOutputHelper output, CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/system/scrapers")]
    [CoversEndpoint("POST", "/api/system/scrapers/reload")]
    [CoversEndpoint("POST", "/api/system/scrapers/match-url")]
    [CoversEndpoint("POST", "/api/system/scrapers/scrape-url")]
    [CoversEndpoint("POST", "/api/system/scrapers/scrape-url-auto")]
    public async Task GivenLocalHtml_WhenBuiltinTextScraperRuns_ThenMetadataIsDeterministic()
    {
        var source = AsDownloadSource().CreateFile("page.html", "text/html", Encoding.UTF8.GetBytes("<html><head><title>Local [Tag One] page</title><meta name=\"description\" content=\"Local description\"></head><body></body></html>"));
        var eva = AsUser(ApiTestUsers.Eva);
        (await eva.GetScrapersAsync(TestContext.Current.CancellationToken)).Should().Contain(scraper => scraper.Id == "builtin.generic:text");
        (await AsUser().ReloadScrapersAsync(TestContext.Current.CancellationToken)).Should().Contain(scraper => scraper.Id == "builtin.generic:text");
        (await eva.MatchScrapersAsync(new ScraperMatchUrlRequest(source.Uri.AbsoluteUri, "text"), TestContext.Current.CancellationToken)).Select(scraper => scraper.Id).Should().Equal("builtin.generic:text");
        var forbiddenReload = () => eva.ReloadScrapersAsync();
        await forbiddenReload.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await eva.GetScrapersAsync(TestContext.Current.CancellationToken)).Should().ContainSingle(scraper => scraper.Id == "builtin.generic:text");
        var scraped = await eva.ScrapeUrlAsync(new ScrapeUrlRequest("builtin.generic:text", "text", source.Uri.AbsoluteUri), TestContext.Current.CancellationToken);
        scraped.GetProperty("title").GetString().Should().Be("Local page");
        scraped.GetProperty("details").GetString().Should().Be("Local description");
        scraped.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).Should().Equal("Tag One");
        scraped.GetProperty("urls").EnumerateArray().Select(url => url.GetString()).Should().Equal(source.Uri.AbsoluteUri);
        var automatic = await eva.ScrapeUrlAutoAsync(new ScraperMatchUrlRequest(source.Uri.AbsoluteUri, "text"), TestContext.Current.CancellationToken);
        automatic.GetProperty("scraperId").GetString().Should().Be("builtin.generic:text");
        var automaticResult = automatic.GetProperty("result");
        automaticResult.GetProperty("title").GetString().Should().Be("Local page");
        automaticResult.GetProperty("details").GetString().Should().Be("Local description");
        automaticResult.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).Should().Equal("Tag One");
        automaticResult.GetProperty("urls").EnumerateArray().Select(url => url.GetString()).Should().Equal(source.Uri.AbsoluteUri);

        var noName = () => eva.ScrapeNameAsync(new ScrapeNameRequest("builtin.generic:text", "text", "not found"));
        var noFragment = () => eva.ScrapeFragmentAsync(new ScrapeFragmentRequest("builtin.generic:text", "text", new Dictionary<string, object>()));
        await noName.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        await noFragment.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    [CoversEndpoint("POST", "/api/system/metadata-servers/validate")]
    public async Task GivenLocalMetadataServer_WhenOwnerValidatesIt_ThenAuthenticatedIdentityIsReturned()
    {
        var result = await AsUser().ValidateMetadataServerAsync(new MetadataServerDto { Endpoint = AsMetadataService().Endpoint.AbsoluteUri, ApiKey = MetadataServiceSimulator.ApiKey, Name = "Local metadata" }, TestContext.Current.CancellationToken);
        result.Valid.Should().BeTrue();
        result.Username.Should().Be("API test metadata user");
        result.Status.Should().Contain("Successfully authenticated");
        var invalid = await AsUser().ValidateMetadataServerAsync(new MetadataServerDto { Endpoint = AsMetadataService().Endpoint.AbsoluteUri, ApiKey = "invalid", Name = "Local metadata" }, TestContext.Current.CancellationToken);
        invalid.Valid.Should().BeFalse();
        invalid.Username.Should().BeNull();
        invalid.Status.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    [CoversEndpoint("GET", "/api/system/config")]
    [CoversEndpoint("GET", "/api/system/stats")]
    public async Task GivenPublicLibraryMetadata_WhenSystemReadsConfigurationAndStats_ThenNonsecretConfigurationAndEntityDeltasAreReturned()
    {
        var owner = AsUser();
        var eva = AsUser(ApiTestUsers.Eva);

        var config = await owner.GetSystemConfigAsync(TestContext.Current.CancellationToken);
        config.Security.Enabled.Should().BeTrue();
        config.CovePaths.Count.Should().Be(1);
        config.CovePaths.All(path => !string.IsNullOrWhiteSpace(path.Path)).Should().BeTrue();
        string.IsNullOrWhiteSpace(config.GeneratedPath).Should().BeFalse();
        string.IsNullOrWhiteSpace(config.CachePath).Should().BeFalse();
        config.VideoExtensions.Should().NotBeEmpty();
        config.ImageExtensions.Should().NotBeEmpty();
        config.GalleryExtensions.Should().NotBeEmpty();
        config.AudioExtensions.Should().NotBeEmpty();
        config.TextExtensions.Should().NotBeEmpty();
        config.Scraping.MetadataServers.Count.Should().Be(1);
        var ownerMetadataServer = config.Scraping.MetadataServers.Single();
        string.IsNullOrWhiteSpace(ownerMetadataServer.ApiKey).Should().BeFalse();
        Uri.TryCreate(ownerMetadataServer.Endpoint, UriKind.Absolute, out var metadataServerUri).Should().BeTrue();
        metadataServerUri!.IsLoopback.Should().BeTrue();

        var memberConfig = await eva.GetSystemConfigAsync(TestContext.Current.CancellationToken);
        AssertSensitiveConfigurationIsRedacted(memberConfig, config);

        var baseline = await eva.GetSystemStatsAsync(TestContext.Current.CancellationToken);
        await owner.CreateVideoAsync(new VideoBuilder().WithTitle($"System stats video {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        await owner.CreateImageAsync(new ImageBuilder().WithTitle($"System stats image {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"System stats gallery {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"System stats performer {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        await owner.CreateStudioAsync($"System stats studio {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        await owner.CreateTagAsync($"System stats tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        await owner.CreateGroupAsync($"System stats group {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        await owner.CreateAudioAsync(new AudioBuilder().WithTitle($"System stats audio {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        await owner.CreateTextAsync(new TextDocumentBuilder().WithTitle($"System stats text {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);

        var after = await eva.GetSystemStatsAsync(TestContext.Current.CancellationToken);
        after.VideoCount.Should().Be(baseline.VideoCount + 1);
        after.ImageCount.Should().Be(baseline.ImageCount + 1);
        after.GalleryCount.Should().Be(baseline.GalleryCount + 1);
        after.PerformerCount.Should().Be(baseline.PerformerCount + 1);
        after.StudioCount.Should().Be(baseline.StudioCount + 1);
        after.TagCount.Should().Be(baseline.TagCount + 1);
        after.GroupCount.Should().Be(baseline.GroupCount + 1);
        after.AudioCount.Should().Be(baseline.AudioCount + 1);
        after.TextCount.Should().Be(baseline.TextCount + 1);
        baseline.VideoFileSize.Should().Be(0);
        baseline.ImageFileSize.Should().Be(0);
        baseline.AudioFileSize.Should().Be(0);
        baseline.TextFileSize.Should().Be(0);
        baseline.TotalFileSize.Should().Be(0);
        baseline.VideoDuration.Should().Be(0);
        baseline.AudioDuration.Should().Be(0);
        after.VideoFileSize.Should().Be(0);
        after.ImageFileSize.Should().Be(0);
        after.AudioFileSize.Should().Be(0);
        after.TextFileSize.Should().Be(0);
        after.TotalFileSize.Should().Be(0);
        after.VideoDuration.Should().Be(0);
        after.AudioDuration.Should().Be(0);
    }

    [Fact]
    [CoversEndpoint("GET", "/api/system/log-level")]
    [CoversEndpoint("PATCH", "/api/system/log-level")]
    public async Task GivenLogLevelControls_WhenOwnerChangesLevels_ThenPersistentAndTemporaryStateAreAuthorizedAndRestored()
    {
        var owner = AsUser();
        var eva = AsUser(ApiTestUsers.Eva);
        var original = await owner.GetSystemLogLevelAsync(TestContext.Current.CancellationToken);
        var restorationLevel = original.ConfiguredLevel.Equals("Trace", StringComparison.OrdinalIgnoreCase)
            ? "Info"
            : original.ConfiguredLevel;

        try
        {
            var normalized = await owner.SetSystemLogLevelAsync(restorationLevel, TestContext.Current.CancellationToken);
            normalized.Level.Should().Be(restorationLevel);
            normalized.ConfiguredLevel.Should().Be(restorationLevel);
            normalized.TraceExpiresAt.Should().BeNull();

            var invalid = () => owner.SetSystemLogLevelAsync("not-a-log-level");
            await invalid.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
            (await owner.GetSystemLogLevelAsync(TestContext.Current.CancellationToken)).Should().BeEquivalentTo(normalized);

            var forbidden = () => eva.SetSystemLogLevelAsync("Warning");
            await forbidden.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
            (await owner.GetSystemLogLevelAsync(TestContext.Current.CancellationToken)).Should().BeEquivalentTo(normalized);

            var warning = await owner.SetSystemLogLevelAsync("Warning", TestContext.Current.CancellationToken);
            warning.Level.Should().Be("Warning");
            warning.ConfiguredLevel.Should().Be("Warning");
            warning.TraceExpiresAt.Should().BeNull();
            (await owner.GetSystemLogLevelAsync(TestContext.Current.CancellationToken)).Should().BeEquivalentTo(warning);

            var debug = await owner.SetSystemLogLevelAsync("Debug", TestContext.Current.CancellationToken);
            debug.Level.Should().Be("Debug");
            debug.ConfiguredLevel.Should().Be("Debug");
            debug.TraceExpiresAt.Should().BeNull();
            (await owner.GetSystemLogLevelAsync(TestContext.Current.CancellationToken)).Should().BeEquivalentTo(debug);

            var traceStartedAt = DateTimeOffset.UtcNow;
            var trace = await owner.SetSystemLogLevelAsync("Trace", TestContext.Current.CancellationToken);
            trace.Level.Should().Be("Trace");
            trace.ConfiguredLevel.Should().Be("Debug");
            trace.TraceExpiresAt.Should().BeAfter(traceStartedAt);
            (await owner.GetSystemLogLevelAsync(TestContext.Current.CancellationToken)).Should().BeEquivalentTo(trace);
        }
        finally
        {
            await owner.SetSystemLogLevelAsync(restorationLevel, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    [CoversEndpoint("GET", "/api/system/ui-assets/{filename}")]
    [CoversEndpoint("POST", "/api/system/ui/favicon")]
    [CoversEndpoint("POST", "/api/system/ui/logo")]
    public async Task GivenOwnerUploadsUiAssets_WhenAnonymousClientReadsThem_ThenDistinctPngBytesAndCachePolicyAreReturned()
    {
        var owner = AsUser();
        var eva = AsUser(ApiTestUsers.Eva);
        var faviconBytes = ApiTestImages.RedPixelPng();
        var logoBytes = ApiTestImages.BluePixelPng();

        var forbiddenFavicon = () => eva.UploadFaviconAsync(faviconBytes, $"forbidden-favicon-{Guid.NewGuid():N}.png");
        var forbiddenLogo = () => eva.UploadLogoAsync(logoBytes, $"forbidden-logo-{Guid.NewGuid():N}.png");
        await forbiddenFavicon.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        await forbiddenLogo.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");

        var favicon = await owner.UploadFaviconAsync(faviconBytes, $"source-favicon-{Guid.NewGuid():N}.png", cancellationToken: TestContext.Current.CancellationToken);
        var logo = await owner.UploadLogoAsync(logoBytes, $"source-logo-{Guid.NewGuid():N}.png", cancellationToken: TestContext.Current.CancellationToken);

        favicon.FileName.Should().StartWith("favicon-").And.EndWith(".png");
        favicon.Path.Should().Be($"/api/system/ui-assets/{favicon.FileName}");
        logo.FileName.Should().StartWith("logo-").And.EndWith(".png");
        logo.Path.Should().Be($"/api/system/ui-assets/{logo.FileName}");

        using var anonymous = new HttpClient { BaseAddress = ApiUri };
        anonymous.DefaultRequestHeaders.Authorization.Should().BeNull();
        await AssertAnonymousPngAssetAsync(anonymous, favicon.Path, faviconBytes);
        await AssertAnonymousPngAssetAsync(anonymous, logo.Path, logoBytes);

        var invalidExtension = () => owner.UploadFaviconAsync(ApiTestImages.OnePixelPng(), $"invalid-favicon-{Guid.NewGuid():N}.txt", "text/plain");
        var empty = () => owner.UploadLogoAsync([], $"empty-logo-{Guid.NewGuid():N}.png");
        await invalidExtension.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        await empty.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
    }

    [Fact]
    [CoversEndpoint("POST", "/api/system/maintenance/recompute-derived-counts")]
    public async Task GivenStaleStudioRollups_WhenOwnerRecomputesDerivedCounts_ThenPublicVideoCountOrderingIsRepaired()
    {
        var owner = AsUser();
        var eva = AsUser(ApiTestUsers.Eva);
        var studioWithVideo = await owner.CreateStudioAsync($"Recompute source studio {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var studioWithoutVideo = await owner.CreateStudioAsync($"Recompute stale studio {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        await owner.CreateVideoAsync(new VideoBuilder()
                .WithTitle($"Recompute source video {Guid.NewGuid():N}")
                .WithStudio(studioWithVideo)
                .Build(), TestContext.Current.CancellationToken);
        await AsDbUser().SetStoredStudioVideoCountsAsync(studioWithVideo.Id, studioWithoutVideo.Id, TestContext.Current.CancellationToken);

        var staleOrder = await eva.GetStudiosAsync("video_count", "desc", TestContext.Current.CancellationToken);
        IndexOf(staleOrder, studioWithoutVideo.Id).Should().BeLessThan(IndexOf(staleOrder, studioWithVideo.Id));
        staleOrder.Single(studio => studio.Id == studioWithVideo.Id).VideoCount.Should().Be(1);
        staleOrder.Single(studio => studio.Id == studioWithoutVideo.Id).VideoCount.Should().Be(0);

        var forbidden = () => eva.RecomputeDerivedCountsAsync();
        await forbidden.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        var stillStale = await eva.GetStudiosAsync("video_count", "desc", TestContext.Current.CancellationToken);
        IndexOf(stillStale, studioWithoutVideo.Id).Should().BeLessThan(IndexOf(stillStale, studioWithVideo.Id));

        var result = await owner.RecomputeDerivedCountsAsync(TestContext.Current.CancellationToken);
        result.EntitiesRecomputed.Should().BeGreaterThan(0);
        var repairedOrder = await eva.GetStudiosAsync("video_count", "desc", TestContext.Current.CancellationToken);
        IndexOf(repairedOrder, studioWithVideo.Id).Should().BeLessThan(IndexOf(repairedOrder, studioWithoutVideo.Id));
        repairedOrder.Single(studio => studio.Id == studioWithVideo.Id).VideoCount.Should().Be(1);
        repairedOrder.Single(studio => studio.Id == studioWithoutVideo.Id).VideoCount.Should().Be(0);
    }

    private static int IndexOf(IReadOnlyList<StudioDto> studios, int studioId)
    {
        var index = studios.Select(studio => studio.Id).ToList().IndexOf(studioId);
        index.Should().BeGreaterThanOrEqualTo(0);
        return index;
    }

    private static async Task AssertAnonymousPngAssetAsync(HttpClient anonymous, string path, byte[] expectedBytes)
    {
        using var response = await anonymous.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        response.Headers.CacheControl?.ToString().Should().Be("public, max-age=86400");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(expectedBytes);
    }

    private static void AssertSensitiveConfigurationIsRedacted(CoveConfigDto config, CoveConfigDto fullConfig)
    {
        config.Scraping.MetadataServers.Count.Should().Be(fullConfig.Scraping.MetadataServers.Count);
        config.Scraping.MetadataServers.All(server => string.IsNullOrEmpty(server.ApiKey)).Should().BeTrue();
        config.Scraping.MetadataServers
            .Select(server => (server.Endpoint, server.Name, server.MaxRequestsPerMinute))
            .SequenceEqual(fullConfig.Scraping.MetadataServers.Select(server => (server.Endpoint, server.Name, server.MaxRequestsPerMinute)))
            .Should().BeTrue();
        string.IsNullOrEmpty(config.Interface.HandyKey).Should().BeTrue();
        config.PluginConfigurations.Count.Should().Be(0);
        config.CovePaths.Count.Should().Be(fullConfig.CovePaths.Count);
        config.CovePaths
            .Select(path => (path.Path, path.ExcludeVideo, path.ExcludeImage, path.ExcludeAudio, path.ExcludeText))
            .SequenceEqual(fullConfig.CovePaths.Select(path => (path.Path, path.ExcludeVideo, path.ExcludeImage, path.ExcludeAudio, path.ExcludeText)))
            .Should().BeTrue();
        string.IsNullOrEmpty(config.GeneratedPath).Should().BeTrue();
        string.IsNullOrEmpty(config.CachePath).Should().BeTrue();
        config.DownloaderPathOverrides.Count.Should().Be(fullConfig.DownloaderPathOverrides.Count);
        config.DownloaderPathOverrides.All(path => string.IsNullOrEmpty(path.Path)).Should().BeTrue();
        string.IsNullOrEmpty(config.FfmpegPath).Should().BeTrue();
        string.IsNullOrEmpty(config.FfprobePath).Should().BeTrue();
        string.IsNullOrEmpty(config.FfmpegInputArgs).Should().BeTrue();
        string.IsNullOrEmpty(config.FfmpegOutputArgs).Should().BeTrue();
        config.ExcludePatterns.Count.Should().Be(0);
        config.ExcludeImagePatterns.Count.Should().Be(0);
        config.ExcludeGalleryPatterns.Count.Should().Be(0);
        string.IsNullOrEmpty(config.Ui.CustomLocalesPath).Should().BeTrue();
        string.IsNullOrEmpty(config.Security.Username).Should().BeTrue();
        (config.Security.KnownProxies?.Count ?? 0).Should().Be(0);
        (config.Security.TrustedHosts?.Count ?? 0).Should().Be(0);
        config.Scraping.ScraperDirectories.Count.Should().Be(0);
        config.Security.Enabled.Should().Be(fullConfig.Security.Enabled);
        config.VideoExtensions.SequenceEqual(fullConfig.VideoExtensions).Should().BeTrue();
        string.Equals(config.Ui.Title, fullConfig.Ui.Title, StringComparison.Ordinal).Should().BeTrue();
    }
}
