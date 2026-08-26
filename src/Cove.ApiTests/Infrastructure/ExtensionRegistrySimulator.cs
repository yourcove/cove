using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Cove.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cove.ApiTests.Infrastructure;

public sealed class ExtensionRegistrySimulator : IAsyncDisposable
{
    public const string DependencyId = "com.cove.api-test-registry-dependency";
    public const string TargetId = "com.cove.api-test-registry-target";
    public const string FaceProviderId = "com.cove.api-test-face-provider";
    public const string DependencyVersion = "1.0.0";
    public const string TargetVersion = "1.1.0";
    public const string FaceProviderVersion = "2.0.0";

    private static readonly DateTimeOffset ReleasedAt = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private readonly WebApplication _application;
    private readonly IReadOnlyDictionary<string, RegistryFixtureExtension> _extensions;

    private ExtensionRegistrySimulator(
        WebApplication application,
        IReadOnlyDictionary<string, RegistryFixtureExtension> extensions,
        Uri endpoint)
    {
        _application = application;
        _extensions = extensions;
        Endpoint = endpoint;
    }

    public Uri Endpoint { get; }

    public int GetPackageRequestCount(string extensionId)
        => _extensions.TryGetValue(extensionId, out var extension)
            ? Volatile.Read(ref extension.PackageRequestCount)
            : 0;

    internal static async Task<ExtensionRegistrySimulator> StartAsync(
        CancellationToken cancellationToken = default)
    {
        var extensions = CreateExtensions();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

        var application = builder.Build();
        application.MapGet("/index.json", () => Results.Json(new
        {
            schemaVersion = "1",
            generatedAt = ReleasedAt,
            extensions = extensions.Values.Select(extension => new { id = extension.Id }),
        }, ApiJson.Options));
        application.MapGet("/extensions/{fileName}", (
            HttpContext context,
            string fileName) => GetMetadata(context, fileName, extensions));
        application.MapGet("/readmes/{fileName}", (
            string fileName) => GetReadme(fileName, extensions));
        application.MapGet("/packages/{extensionId}/{fileName}", (
            string extensionId,
            string fileName) => GetPackage(extensionId, fileName, extensions));

        try
        {
            await application.StartAsync(cancellationToken);
            var addresses = application.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                ?.Addresses;
            var address = addresses?.SingleOrDefault()
                ?? throw new InvalidOperationException("The extension-registry simulator did not publish a listening address.");

            return new ExtensionRegistrySimulator(application, extensions, new Uri(address));
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
    }

    internal void Reset()
    {
        foreach (var extension in _extensions.Values)
            Volatile.Write(ref extension.PackageRequestCount, 0);
    }

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync();
        await _application.DisposeAsync();
    }

    private static IResult GetMetadata(
        HttpContext context,
        string fileName,
        IReadOnlyDictionary<string, RegistryFixtureExtension> extensions)
    {
        if (!TryGetExtension(fileName, ".json", extensions, out var extension))
            return Results.NotFound();

        var origin = $"{context.Request.Scheme}://{context.Request.Host}";
        return Results.Json(new
        {
            id = extension.Id,
            name = extension.Name,
            description = extension.Description,
            author = "Cove API Tests",
            kind = "bundle",
            homepageUrl = $"https://example.invalid/{extension.Id}",
            readmeUrl = $"{origin}/readmes/{Uri.EscapeDataString(extension.Id)}.md",
            categories = extension.Categories,
            versions = new[]
            {
                new
                {
                    version = extension.Version,
                    releasedAt = ReleasedAt,
                    changelog = $"Deterministic release for {extension.Id}.",
                    checksum = extension.Checksum,
                    downloadUrl = $"{origin}/packages/{Uri.EscapeDataString(extension.Id)}/{Uri.EscapeDataString(extension.Version)}.zip",
                    dependencies = extension.Dependencies,
                },
            },
        }, ApiJson.Options);
    }

    private static IResult GetReadme(
        string fileName,
        IReadOnlyDictionary<string, RegistryFixtureExtension> extensions)
    {
        if (!TryGetExtension(fileName, ".md", extensions, out var extension))
            return Results.NotFound();

        return Results.Text($"# {extension.Name}\n\nDeterministic API-test registry entry.\n", "text/markdown");
    }

    private static IResult GetPackage(
        string extensionId,
        string fileName,
        IReadOnlyDictionary<string, RegistryFixtureExtension> extensions)
    {
        if (!extensions.TryGetValue(extensionId, out var extension)
            || !string.Equals(fileName, extension.Version + ".zip", StringComparison.Ordinal))
        {
            return Results.NotFound();
        }

        Interlocked.Increment(ref extension.PackageRequestCount);
        return Results.File(extension.Package, "application/zip", $"{extension.Id}-{extension.Version}.zip");
    }

    private static bool TryGetExtension(
        string fileName,
        string suffix,
        IReadOnlyDictionary<string, RegistryFixtureExtension> extensions,
        out RegistryFixtureExtension extension)
    {
        extension = null!;
        if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;

        var extensionId = fileName[..^suffix.Length];
        return extensions.TryGetValue(extensionId, out extension!);
    }

    private static IReadOnlyDictionary<string, RegistryFixtureExtension> CreateExtensions()
    {
        var targetDependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [DependencyId] = ">=1.0.0",
        };
        var extensions = new[]
        {
            CreateExtension(
                DependencyId,
                "API Test Registry Dependency",
                DependencyVersion,
                "A deterministic registry dependency.",
                ["api-test", "dependency", "registry"],
                new Dictionary<string, string>()),
            CreateExtension(
                TargetId,
                "API Test Registry Target",
                TargetVersion,
                "A deterministic registry target.",
                ["api-test", "extension", "registry"],
                targetDependencies),
            CreateExtension(
                FaceProviderId,
                "API Test Face Provider",
                FaceProviderVersion,
                "An update marker for the installed API-test provider.",
                ["api-test", "faces"],
                new Dictionary<string, string>()),
        };
        return extensions.ToDictionary(extension => extension.Id, StringComparer.OrdinalIgnoreCase);
    }

    private static RegistryFixtureExtension CreateExtension(
        string id,
        string name,
        string version,
        string description,
        IReadOnlyList<string> categories,
        IReadOnlyDictionary<string, string> dependencies)
    {
        var manifest = new ExtensionManifestFile
        {
            Id = id,
            Name = name,
            Version = version,
            Description = description,
            Kind = "bundle",
            Categories = categories.ToList(),
            Dependencies = new Dictionary<string, string>(dependencies, StringComparer.OrdinalIgnoreCase),
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ApiJson.Options);
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("extension.json");
            entry.LastWriteTime = ReleasedAt;
            using var entryStream = entry.Open();
            entryStream.Write(manifestBytes);
        }

        var package = stream.ToArray();
        var checksum = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
        return new RegistryFixtureExtension(
            id,
            name,
            version,
            description,
            categories.ToArray(),
            new Dictionary<string, string>(dependencies, StringComparer.OrdinalIgnoreCase),
            package,
            checksum);
    }

    private sealed class RegistryFixtureExtension(
        string id,
        string name,
        string version,
        string description,
        IReadOnlyList<string> categories,
        IReadOnlyDictionary<string, string> dependencies,
        byte[] package,
        string checksum)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string Version { get; } = version;
        public string Description { get; } = description;
        public IReadOnlyList<string> Categories { get; } = categories;
        public IReadOnlyDictionary<string, string> Dependencies { get; } = dependencies;
        public byte[] Package { get; } = package;
        public string Checksum { get; } = checksum;
        public int PackageRequestCount;
    }
}
