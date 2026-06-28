using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Sdk;

/// <summary>
/// Convenient base class for Cove extensions that provides sensible defaults
/// and reduces boilerplate. Override only the methods you need.
/// </summary>
public abstract class CoveExtensionBase : IUIExtension, IManifestAware
{
    private ExtensionManifestFile? _manifest;

    /// <summary>
    /// The parsed <c>extension.json</c> manifest, injected by the host immediately after construction.
    /// This is the single source of truth for the extension's identity and metadata so it never has to
    /// be duplicated in code. Available from <see cref="ConfigureServices"/> onward.
    /// </summary>
    protected ExtensionManifestFile Manifest => _manifest
        ?? throw new InvalidOperationException(
            "Extension metadata is unavailable. Manifest-backed extensions read it from extension.json " +
            "(ensure the file ships next to the DLL); code-registered/built-in extensions must override " +
            "Id, Name and Version.");

    void IManifestAware.ApplyManifest(ExtensionManifestFile manifest) => _manifest = manifest;

    // Metadata is sourced from extension.json by default. Override any of these only for code-registered
    // (built-in) extensions that have no manifest, or to intentionally diverge from it.
    public virtual string Id => Manifest.Id;
    public virtual string Name => Manifest.Name;
    public virtual string Version => Manifest.Version;
    public virtual string? Description => _manifest?.Description;
    public virtual string? Author => _manifest?.Author;
    public virtual string? Url => _manifest?.Url;
    public virtual string? IconUrl => _manifest?.IconUrl;
    public virtual IReadOnlyList<string> Categories => _manifest?.Categories ?? [];
    public virtual string? MinCoveVersion => _manifest?.MinCoveVersion;
    public virtual IReadOnlyDictionary<string, string> Dependencies => _manifest?.Dependencies ?? new Dictionary<string, string>();

    /// <summary>
    /// Override to register services. Base implementation does nothing.
    /// </summary>
    public virtual void ConfigureServices(IServiceCollection services, ExtensionContext context) { }

    /// <summary>
    /// Override to contribute UI pages, settings tabs, settings panels, or other frontend manifest entries.
    /// Default implementation returns an empty manifest.
    /// </summary>
    public virtual UIManifest GetUIManifest() => new();

    /// <summary>Create a UIManifestBuilder pre-configured with this extension's ID.</summary>
    protected UIManifestBuilder ManifestBuilder() => new(Id);

    /// <summary>
    /// Override to perform async initialization after DI container is built.
    /// </summary>
    public virtual Task InitializeAsync(IServiceProvider services, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Publish every service of contract <typeparamref name="T"/> registered in THIS extension's
    /// container to the cross-extension <see cref="IExtensionServiceExchange"/>, so sibling extensions
    /// (which are isolated in their own containers) can consume them. Call this from
    /// <see cref="InitializeAsync"/>. The host withdraws these entries automatically when the extension
    /// is disabled or uninstalled, so there is nothing to clean up.
    /// </summary>
    protected void PublishContributions<T>(IServiceProvider services) where T : class
    {
        var exchange = services.GetService<IExtensionServiceExchange>();
        if (exchange is null)
            return;

        foreach (var instance in services.GetServices<T>())
            exchange.Publish(Id, typeof(T), instance);
    }

    public virtual Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
    public virtual Task OnInstallAsync(IServiceProvider services, CancellationToken ct = default) => Task.CompletedTask;
    public virtual Task OnUninstallAsync(IServiceProvider services, CancellationToken ct = default) => Task.CompletedTask;
}
