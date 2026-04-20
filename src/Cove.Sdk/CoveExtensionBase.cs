using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Sdk;

/// <summary>
/// Convenient base class for Cove extensions that provides sensible defaults
/// and reduces boilerplate. Override only the methods you need.
/// </summary>
public abstract class CoveExtensionBase : IExtension
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Version { get; }
    public virtual string? Description => null;
    public virtual string? Author => null;
    public virtual string? Url => null;
    public virtual string? IconUrl => null;
    public virtual IReadOnlyList<string> Categories => [];
    public virtual string? MinCoveVersion => null;
    public virtual IReadOnlyDictionary<string, string> Dependencies => new Dictionary<string, string>();

    /// <summary>
    /// Override to register services. Base implementation does nothing.
    /// </summary>
    public virtual void ConfigureServices(IServiceCollection services, ExtensionContext context) { }

    /// <summary>
    /// Override to perform async initialization after DI container is built.
    /// </summary>
    public virtual Task InitializeAsync(IServiceProvider services, CancellationToken ct = default) => Task.CompletedTask;

    public virtual Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
    public virtual Task OnInstallAsync(IServiceProvider services, CancellationToken ct = default) => Task.CompletedTask;
    public virtual Task OnUninstallAsync(IServiceProvider services, CancellationToken ct = default) => Task.CompletedTask;
}
