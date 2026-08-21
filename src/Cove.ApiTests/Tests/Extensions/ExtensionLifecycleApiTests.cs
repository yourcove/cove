using System.IO.Compression;
using System.Text.Json;
using Cove.ApiTests.Infrastructure;
using Cove.Plugins;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Extensions;

[Collection(ApiTestLane2Collection.Name)]
public sealed class ExtensionLifecycleApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/extensions/manifest")]
    [CoversEndpoint("GET", "/api/extensions/bundles/ui.mjs")]
    [CoversEndpoint("GET", "/api/extensions/bundles/ui.css")]
    [CoversEndpoint("GET", "/api/extensions/categories")]
    [CoversEndpoint("GET", "/api/extensions/{id}/dependencies/missing")]
    public async Task GivenBuiltinExtensions_WhenMemberReadsPublicRuntimeMetadata_ThenExactCapabilitiesAndEmptyBundlesAreReturned()
    {
        var member = AsUser(ApiTestUsers.Eva);

        var extensions = await member.GetExtensionsAsync();
        var themes = extensions.Should().ContainSingle(extension => extension.Id == "com.cove.themes").Which;
        themes.Name.Should().Be("Theme Collection");
        themes.Version.Should().Be("1.0.0");
        themes.Enabled.Should().BeTrue();
        themes.HasUI.Should().BeTrue();
        themes.HasApi.Should().BeFalse();
        themes.HasState.Should().BeFalse();
        themes.HasJobs.Should().BeFalse();
        themes.HasEvents.Should().BeFalse();
        themes.HasData.Should().BeFalse();
        themes.HasMiddleware.Should().BeFalse();
        themes.HasActions.Should().BeFalse();
        themes.Categories.Should().Equal("theme", "color-palette", "style", "layout");
        themes.Source.Should().Be("builtin");

        var directFile = extensions.Should().ContainSingle(extension => extension.Id == "builtin.direct-file").Which;
        directFile.Name.Should().Be("Direct File Downloader");
        directFile.Version.Should().Be("1.0.0");
        directFile.Enabled.Should().BeTrue();
        directFile.HasUI.Should().BeFalse();
        directFile.HasApi.Should().BeFalse();
        directFile.HasState.Should().BeFalse();
        directFile.HasJobs.Should().BeFalse();
        directFile.HasEvents.Should().BeFalse();
        directFile.HasData.Should().BeFalse();
        directFile.HasMiddleware.Should().BeFalse();
        directFile.HasActions.Should().BeFalse();
        directFile.Categories.Should().Equal("downloader");
        directFile.Source.Should().Be("builtin");

        var manifest = await member.GetExtensionManifestAsync();
        manifest.FrontendRuntimeVersion.Should().Be("v1");
        manifest.ExtensionBundles.Should().BeEmpty();
        manifest.JsBundleUrl.Should().BeNull();
        manifest.CssBundleUrl.Should().BeNull();
        manifest.ComponentStyles.Select(style => style.Id).Should().Equal(
            "default", "glass", "rounded", "gradient", "animated", "floating");
        manifest.LayoutStyles.Select(style => style.Id).Should().Equal(
            "default", "detail-theater", "detail-tabs");
        manifest.Themes.Select(theme => theme.Id).Should().Contain(["default", "legacy", "light", "dark-midnight"]);

        var javaScript = await member.GetCombinedExtensionJavaScriptAsync();
        javaScript.MediaType.Should().Be("application/javascript");
        javaScript.Content.Should().Be("export default { components: {}, actionHandlers: {}, handlers: {} };");

        var css = await member.GetCombinedExtensionCssAsync();
        css.MediaType.Should().Be("text/css");
        css.Content.Should().BeEmpty();

        (await member.GetExtensionCategoriesAsync()).Should().Equal(
            "color-palette", "downloader", "layout", "style", "theme");
        (await member.GetMissingExtensionDependenciesAsync(directFile.Id)).Should().BeEmpty();
    }

    [Fact]
    [CoversEndpoint("POST", "/api/extensions/install-from-url")]
    [CoversEndpoint("POST", "/api/extensions/install-from-zip")]
    [CoversEndpoint("POST", "/api/extensions/{id}/disable")]
    [CoversEndpoint("POST", "/api/extensions/{id}/enable")]
    [CoversEndpoint("POST", "/api/extensions/registry/uninstall")]
    public async Task GivenManifestOnlyPackages_WhenOwnerManagesLifecycle_ThenPermissionsStateAndCleanupAreExact()
    {
        var owner = AsUser();
        var member = AsUser(ApiTestUsers.Eva);
        var suffix = Guid.NewGuid().ToString("N");
        var urlExtensionId = $"api.test.url.{suffix}";
        var urlTopicId = $"api-test-url-topic-{suffix}";
        var urlCategory = $"api-test-url-{suffix}";
        var urlPackage = CreateManifestOnlyPackage(urlExtensionId, "URL API test bundle", "1.2.3", urlCategory, urlTopicId);
        var source = AsDownloadSource().CreateFile("extension.zip", "application/zip", urlPackage);
        var zipExtensionId = $"api.test.zip.{suffix}";
        var zipTopicId = $"api-test-zip-topic-{suffix}";
        var zipCategory = $"api-test-zip-{suffix}";
        var zipPackage = CreateManifestOnlyPackage(zipExtensionId, "ZIP API test bundle", "4.5.6", zipCategory, zipTopicId);

        try
        {
            var forbiddenUrlInstall = () => member.InstallExtensionFromUrlAsync(source.Uri);
            await forbiddenUrlInstall.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
            source.RequestCount.Should().Be(0);
            (await owner.GetExtensionsAsync()).Should().NotContain(extension => extension.Id == urlExtensionId);

            var installedFromUrl = await owner.InstallExtensionFromUrlAsync(source.Uri);
            source.RequestCount.Should().Be(1);
            installedFromUrl.Message.Should().Be($"Extension '{urlExtensionId}' v1.2.3 installed from URL.");
            installedFromUrl.ExtensionId.Should().Be(urlExtensionId);
            installedFromUrl.Version.Should().Be("1.2.3");
            installedFromUrl.Path.Should().NotBeNullOrWhiteSpace();
            await AssertManifestOnlyExtensionAsync(owner, urlExtensionId, "1.2.3", "url", urlCategory, enabled: true);
            await AssertManifestTopicAsync(owner, urlTopicId, urlExtensionId, expected: true);

            var forbiddenDisable = () => member.DisableExtensionAsync(urlExtensionId);
            await forbiddenDisable.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
            (await owner.GetExtensionsAsync()).Single(extension => extension.Id == urlExtensionId).Enabled.Should().BeTrue();

            var disabled = await owner.DisableExtensionAsync(urlExtensionId);
            disabled.DisabledExtensions.Should().Equal(urlExtensionId);
            (await owner.GetExtensionsAsync()).Single(extension => extension.Id == urlExtensionId).Enabled.Should().BeFalse();
            await AssertManifestTopicAsync(owner, urlTopicId, urlExtensionId, expected: false);

            var forbiddenEnable = () => member.EnableExtensionAsync(urlExtensionId);
            await forbiddenEnable.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
            (await owner.GetExtensionsAsync()).Single(extension => extension.Id == urlExtensionId).Enabled.Should().BeFalse();

            var enabled = await owner.EnableExtensionAsync(urlExtensionId);
            enabled.EnabledExtensions.Should().Equal(urlExtensionId);
            await AssertManifestOnlyExtensionAsync(owner, urlExtensionId, "1.2.3", "url", urlCategory, enabled: true);
            await AssertManifestTopicAsync(owner, urlTopicId, urlExtensionId, expected: true);

            var forbiddenUninstall = () => member.UninstallExtensionAsync(urlExtensionId);
            await forbiddenUninstall.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
            (await owner.GetExtensionsAsync()).Should().Contain(extension => extension.Id == urlExtensionId);

            var uninstalledUrl = await owner.UninstallExtensionAsync(urlExtensionId);
            uninstalledUrl.Message.Should().Be($"Extension '{urlExtensionId}' uninstalled.");
            uninstalledUrl.RequiresDependents.Should().BeFalse();
            uninstalledUrl.UninstalledExtensions.Should().Equal(urlExtensionId);
            (await owner.GetExtensionsAsync()).Should().NotContain(extension => extension.Id == urlExtensionId);
            await AssertManifestTopicAsync(owner, urlTopicId, urlExtensionId, expected: false);

            var forbiddenZipInstall = () => member.InstallExtensionFromZipAsync(zipPackage);
            await forbiddenZipInstall.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
            (await owner.GetExtensionsAsync()).Should().NotContain(extension => extension.Id == zipExtensionId);

            var installedFromZip = await owner.InstallExtensionFromZipAsync(zipPackage);
            installedFromZip.Message.Should().Be($"Extension '{zipExtensionId}' v4.5.6 installed from uploaded ZIP.");
            installedFromZip.ExtensionId.Should().Be(zipExtensionId);
            installedFromZip.Version.Should().Be("4.5.6");
            installedFromZip.Path.Should().NotBeNullOrWhiteSpace();
            await AssertManifestOnlyExtensionAsync(owner, zipExtensionId, "4.5.6", "upload", zipCategory, enabled: true);
            await AssertManifestTopicAsync(owner, zipTopicId, zipExtensionId, expected: true);

            var uninstalledZip = await owner.UninstallExtensionAsync(zipExtensionId);
            uninstalledZip.Message.Should().Be($"Extension '{zipExtensionId}' uninstalled.");
            uninstalledZip.RequiresDependents.Should().BeFalse();
            uninstalledZip.UninstalledExtensions.Should().Equal(zipExtensionId);
            (await owner.GetExtensionsAsync()).Should().NotContain(extension => extension.Id == zipExtensionId);
            await AssertManifestTopicAsync(owner, zipTopicId, zipExtensionId, expected: false);
        }
        finally
        {
            await EnsureExtensionsUninstalledAsync(owner, urlExtensionId, zipExtensionId);
        }
    }

    private static async Task EnsureExtensionsUninstalledAsync(
        CoveClient client,
        params string[] extensionIds)
    {
        var errors = new List<Exception>();
        foreach (var extensionId in extensionIds)
        {
            try
            {
                await client.EnsureExtensionUninstalledAsync(extensionId);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        if (errors.Count > 0)
            throw new AggregateException("One or more API-test extensions could not be cleaned up.", errors);
    }

    private static async Task AssertManifestOnlyExtensionAsync(
        CoveClient client,
        string extensionId,
        string version,
        string source,
        string category,
        bool enabled)
    {
        var extension = (await client.GetExtensionsAsync()).Should()
            .ContainSingle(candidate => candidate.Id == extensionId)
            .Which;
        extension.Version.Should().Be(version);
        extension.Enabled.Should().Be(enabled);
        extension.HasUI.Should().BeFalse();
        extension.HasApi.Should().BeFalse();
        extension.HasState.Should().BeFalse();
        extension.HasJobs.Should().BeFalse();
        extension.HasEvents.Should().BeFalse();
        extension.HasData.Should().BeFalse();
        extension.HasMiddleware.Should().BeFalse();
        extension.HasActions.Should().BeFalse();
        extension.Categories.Should().Equal(category);
        extension.Dependencies.Should().BeEmpty();
        extension.Kind.Should().Be("bundle");
        extension.Source.Should().Be(source);
        extension.InstalledAt.Should().NotBeNull();
        extension.Jobs.Should().BeEmpty();
    }

    private static async Task AssertManifestTopicAsync(
        CoveClient client,
        string topicId,
        string extensionId,
        bool expected)
    {
        var topics = (await client.GetExtensionManifestAsync()).TutorialTopics
            .Where(topic => topic.Id == topicId)
            .ToList();
        if (!expected)
        {
            topics.Should().BeEmpty();
            return;
        }

        var topic = topics.Should().ContainSingle().Which;
        topic.Title.Should().Be($"Topic for {extensionId}");
        topic.ExtensionId.Should().Be(extensionId);
    }

    private static byte[] CreateManifestOnlyPackage(
        string extensionId,
        string name,
        string version,
        string category,
        string topicId)
    {
        var manifest = new ExtensionManifestFile
        {
            Id = extensionId,
            Name = name,
            Version = version,
            Kind = "bundle",
            Categories = [category],
            TutorialTopics =
            [
                new UITutorialTopic(topicId, $"Topic for {extensionId}"),
            ],
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, ApiJson.Options);
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("extension.json");
            using var entryStream = entry.Open();
            entryStream.Write(json);
        }
        return stream.ToArray();
    }
}
