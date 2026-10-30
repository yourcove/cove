using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public sealed class ExtensionZipInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cove-zip-install-{Guid.NewGuid():N}");
    private readonly IServiceProvider _services = new ServiceCollection().BuildServiceProvider();
    private readonly CoveConfiguration _configuration = new();

    [Fact]
    public async Task Uploaded_zip_installs_initializes_records_source_and_cleans_temporary_files()
    {
        var (controller, manager) = CreateController();
        var archive = CreateZip(("extension.json", Manifest("com.example.upload", "1.2.3")));

        var result = await controller.InstallFromZip(FormFile(archive), true, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = JsonSerializer.SerializeToElement(ok.Value);
        Assert.Equal("com.example.upload", body.GetProperty("extensionId").GetString());
        Assert.Equal("1.2.3", body.GetProperty("version").GetString());
        Assert.Contains("uploaded ZIP", body.GetProperty("message").GetString());
        Assert.Equal("upload", manager.GetInstallation("com.example.upload")?.Source);
        Assert.NotNull(manager.GetManifestFile("com.example.upload"));
        Assert.Empty(Directory.GetDirectories(ExtensionsDir(), ".upload-install-*"));
    }

    [Fact]
    public async Task Upload_rejects_missing_or_empty_file_and_missing_trust()
    {
        var (controller, _) = CreateController();

        Assert.IsType<BadRequestObjectResult>(await controller.InstallFromZip(null, true, TestContext.Current.CancellationToken));
        Assert.IsType<BadRequestObjectResult>(await controller.InstallFromZip(FormFile([]), true, TestContext.Current.CancellationToken));
        Assert.IsType<BadRequestObjectResult>(await controller.InstallFromZip(FormFile(CreateZip(("extension.json", Manifest("com.example.upload", "1.0.0")))), false, TestContext.Current.CancellationToken));
    }

    [Theory]
    [MemberData(nameof(InvalidPackages))]
    public async Task Upload_rejects_invalid_packages(byte[] archive, string expectedMessage)
    {
        var (controller, _) = CreateController();

        var result = Assert.IsType<BadRequestObjectResult>(await controller.InstallFromZip(FormFile(archive), true, TestContext.Current.CancellationToken));

        Assert.Contains(expectedMessage, Assert.IsType<string>(result.Value), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(ExtensionsDir(), ".upload-install-*"));
    }

    [Fact]
    public async Task Upload_replaces_an_existing_extension()
    {
        var (controller, manager) = CreateController();
        var first = CreateZip(("extension.json", Manifest("com.example.replace", "1.0.0")), ("old.txt", "old"));
        var second = CreateZip(("extension.json", Manifest("com.example.replace", "2.0.0")), ("new.txt", "new"));
        Assert.IsType<OkObjectResult>(await controller.InstallFromZip(FormFile(first), true, TestContext.Current.CancellationToken));

        var result = await controller.InstallFromZip(FormFile(second), true, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result);
        var installedDir = Path.Combine(ExtensionsDir(), "com.example.replace");
        Assert.False(File.Exists(Path.Combine(installedDir, "old.txt")));
        Assert.True(File.Exists(Path.Combine(installedDir, "new.txt")));
        Assert.Equal("2.0.0", manager.GetInstallation("com.example.replace")?.Version);
        Assert.Equal("upload", manager.GetInstallation("com.example.replace")?.Source);
    }

    [Fact]
    public async Task Url_install_still_uses_the_shared_package_pipeline()
    {
        var archive = CreateZip(("extension.json", Manifest("com.example.url", "3.0.0")));
        var (controller, manager) = CreateController(archive);

        var result = await controller.InstallFromUrl(new InstallExtensionFromUrlRequest { Url = "https://example.invalid/extension.zip", TrustUnverified = true }, new BytesHttpClientFactory(archive), TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("from URL", JsonSerializer.SerializeToElement(ok.Value).GetProperty("message").GetString());
        Assert.Equal("url", manager.GetInstallation("com.example.url")?.Source);
        Assert.Empty(Directory.GetDirectories(ExtensionsDir(), ".url-install-*"));
    }

    [Fact]
    public async Task Installing_an_extension_without_navigation_pages_leaves_the_menu_unchanged()
    {
        _configuration.Interface.MenuItems = ["videos", "images"];
        var (controller, _) = CreateController();
        var archive = CreateZip(("extension.json", Manifest("com.example.no-pages", "1.0.0")));

        Assert.IsType<OkObjectResult>(await controller.InstallFromZip(FormFile(archive), true, TestContext.Current.CancellationToken));

        Assert.Equal(["videos", "images"], _configuration.Interface.MenuItems);
        Assert.False(File.Exists(ConfigPath()));
    }

    [Fact]
    public async Task Newly_installed_extension_navigation_pages_are_shown()
    {
        _configuration.Interface.MenuItems = ["videos", "images"];
        var (controller, manager) = CreateController();
        var before = controller.TakeNavigationSnapshot(["com.example.nav"]);
        manager.Register(new NavigationPageExtension("com.example.nav", "nav-page"));
        manager.Register(new NavigationPageExtension("com.example.other", "other-page"));

        await controller.ShowNewNavigationPagesAsync(before);

        Assert.NotNull(before);
        Assert.Empty(before.Routes);
        Assert.Equal(["videos", "images", "nav-page"], _configuration.Interface.MenuItems);
    }

    [Fact]
    public async Task Update_shows_only_navigation_pages_the_extension_did_not_have_before()
    {
        // The user hid "nav-page"; the update adds "added-page".
        _configuration.Interface.MenuItems = ["videos", "images"];
        var (controller, manager) = CreateController();
        manager.Register(new NavigationPageExtension("com.example.nav", "nav-page", "added-page"));
        var before = new ExtensionsController.NavigationSnapshot(
            new HashSet<string>(["com.example.nav"]),
            new HashSet<string>(["nav-page"]));

        await controller.ShowNewNavigationPagesAsync(before);

        Assert.Equal(["videos", "images", "added-page"], _configuration.Interface.MenuItems);
    }

    [Fact]
    public async Task Reinstall_keeps_a_hidden_navigation_page_hidden()
    {
        _configuration.Interface.MenuItems = ["videos", "images"];
        var (controller, manager) = CreateController();
        manager.Register(new NavigationPageExtension("com.example.nav", "nav-page"));
        var before = controller.TakeNavigationSnapshot(["com.example.nav"]);

        await controller.ShowNewNavigationPagesAsync(before);

        Assert.NotNull(before);
        Assert.Equal(["nav-page"], before.Routes);
        Assert.Equal(["videos", "images"], _configuration.Interface.MenuItems);
        Assert.False(File.Exists(ConfigPath()));
    }

    [Fact]
    public async Task Disabled_extension_navigation_pages_are_left_alone()
    {
        _configuration.Interface.MenuItems = ["videos", "images"];
        var (controller, manager) = CreateController();
        manager.Register(new NavigationPageExtension("com.example.nav", "nav-page"));
        await manager.DisableExtensionAsync("com.example.nav", TestContext.Current.CancellationToken);
        var before = controller.TakeNavigationSnapshot(["com.example.nav"]);
        // Replacing the extension forgets that it was disabled, so it comes back with its pages.
        await manager.EnableExtensionAsync("com.example.nav", TestContext.Current.CancellationToken);

        await controller.ShowNewNavigationPagesAsync(before);

        Assert.NotNull(before);
        Assert.Empty(before.ExtensionIds);
        Assert.Equal(["videos", "images"], _configuration.Interface.MenuItems);
    }

    [Fact]
    public async Task Excluded_extensions_do_not_get_navigation_pages()
    {
        _configuration.Interface.MenuItems = ["videos", "images"];
        var (controller, manager) = CreateController();
        var before = controller.TakeNavigationSnapshot(["com.example.dependency", "com.example.failed"]);
        manager.Register(new NavigationPageExtension("com.example.dependency", "dependency-page"));
        manager.Register(new NavigationPageExtension("com.example.failed", "failed-page"));

        await controller.ShowNewNavigationPagesAsync(before, "com.example.failed");

        Assert.Equal(["videos", "images", "dependency-page"], _configuration.Interface.MenuItems);
    }

    public static IEnumerable<object[]> InvalidPackages()
    {
        yield return [Encoding.UTF8.GetBytes("not a zip"), "central directory"];
        yield return [CreateZip(("../escaped.txt", "nope"), ("extension.json", Manifest("com.example.safe", "1.0.0"))), "outside the extraction directory"];
        yield return [CreateZip(("readme.txt", "missing")), "extension.json"];
        yield return [CreateZip(("one/extension.json", Manifest("com.example.one", "1.0.0")), ("two/extension.json", Manifest("com.example.two", "1.0.0"))), "extension.json"];
        yield return [CreateZip(("extension.json", Manifest("../unsafe", "1.0.0"))), "invalid path"];
        yield return [CreateZip(("extension.json", Manifest(".", "1.0.0"))), "invalid path"];
        yield return [CreateZip(("extension.json", Manifest("com.example.future", "1.0.0", "99.0.0"))), "requires Cove"];
    }

    private (ExtensionsController Controller, ExtensionManager Manager) CreateController(byte[]? responseBytes = null)
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.Combine(_root, "data"),
            CoveVersion = "1.0.0",
        });
        var factory = new BytesHttpClientFactory(responseBytes ?? []);
        var controller = new ExtensionsController(
            manager,
            new ScraperService(new CoveConfiguration(), NullLogger<ScraperService>.Instance, factory, manager),
            new ConfigService(_configuration, NullLogger<ConfigService>.Instance, ConfigPath()),
            NullLogger<ExtensionsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = _services },
            },
        };
        return (controller, manager);
    }

    private string ExtensionsDir() => Path.Combine(_root, "extensions");

    private string ConfigPath() => Path.Combine(_root, "data", "cove-config.json");

    private static IFormFile FormFile(byte[] bytes) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "ignored-client-name.zip");

    private static string Manifest(string id, string version, string? minCoveVersion = null) =>
        JsonSerializer.Serialize(new ExtensionManifestFile
        {
            Id = id,
            Name = id,
            Version = version,
            Kind = "bundle",
            MinCoveVersion = minCoveVersion,
        });

    private static byte[] CreateZip(params (string Path, string Contents)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, contents) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8, leaveOpen: false);
                writer.Write(contents);
            }
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        (_services as IDisposable)?.Dispose();
    }

    private sealed class NavigationPageExtension(string id, params string[] navRoutes) : CoveExtensionBase
    {
        public override string Id => id;
        public override string Name => id;
        public override string Version => "1.0.0";

        public override UIManifest GetUIManifest()
        {
            var builder = ManifestBuilder()
                .AddPage(new UIPageDefinition($"{id}-detail", "Detail", ShowInNav: false));
            foreach (var route in navRoutes)
                builder.AddPage(new UIPageDefinition(route, route));
            return builder.Build();
        }
    }

    private sealed class BytesHttpClientFactory(byte[] bytes) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(bytes));

        private sealed class Handler(byte[] bytes) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes),
                });
        }
    }
}
