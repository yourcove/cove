using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Cove.Plugins;

/// <summary>
/// Endpoint metadata that marks an ASP.NET Core endpoint as belonging to a runtime-loaded
/// extension. The host pipeline uses this to execute the endpoint against that extension's own
/// service container instead of the immutable root container.
/// </summary>
public sealed record ExtensionEndpointMetadata(string ExtensionId);

/// <summary>
/// Owns one isolated child ("overlay") dependency-injection container PER runtime-loaded (DLL)
/// extension.
///
/// <para><b>Why per-extension, built once.</b> ASP.NET Core's root <see cref="IServiceProvider"/> is
/// immutable after the host is built, so a runtime-installed extension's services can never be added
/// to it. Each extension therefore gets its own child container, built exactly once when the
/// extension is enabled (whether at host startup or at runtime install — the same code path) and
/// disposed when it is disabled or uninstalled. Critically, installing or removing ANY OTHER
/// extension does not touch this one's container, so an extension's singletons — and whatever state
/// they hold (a connected client, a warmed cache, an in-memory index) — stay stable for the
/// extension's entire lifetime. "Installed" behaves identically to "present at startup".</para>
///
/// <para><b>Cross-extension interaction.</b> Because extensions are isolated, one extension cannot
/// resolve another's services through DI. Instead they publish and consume shared-contract services
/// through the host's <see cref="IExtensionServiceExchange"/> (a root singleton, forwarded into every
/// container). That keeps cross-extension wiring explicit and decoupled from container lifetime.</para>
///
/// <para><b>The host contract.</b> Extensions reference only the shared contract assemblies
/// (<c>Cove.Sdk</c> -&gt; <c>Cove.Plugins</c> -&gt; <c>Cove.Core</c>) plus framework abstractions, so by
/// construction they can only inject host services whose type lives in those assemblies — never a
/// <c>Cove.Api</c> host-internal type. Host implementation types are still copied into the container
/// so contract services can be constructed, but they are not nameable by extensions.</para>
///
/// <para><b>Composition rules</b> for each container:</para>
/// <list type="bullet">
/// <item><b>Host singletons are forwarded</b> (resolved from the root provider) so there is exactly
/// one shared instance — one database pool, one cache, one logger factory, one exchange.</item>
/// <item><b>Host scoped/transient services are copied</b> so the container creates its own instance
/// per scope from the shared singletons. A pooled <c>DbContext</c> resolved in an overlay scope is
/// leased from the shared pool and returned exactly once.</item>
/// <item><b>Fragile framework stacks (logging, HttpClient) are rebuilt fresh</b> rather than copied,
/// because they introspect the <c>ServiceCollection</c> at registration time and break on a partial
/// copy. Logging is pointed back at the host's logger factory so output is unified.</item>
/// </list>
///
/// <para>Request-scoped state Cove exposes (e.g. the current principal) is carried via
/// <c>AsyncLocal</c> singletons, so it flows into overlay scopes without additional bridging.</para>
/// </summary>
public sealed class ExtensionServiceOverlay : IDisposable
{
    // Service types provided by the container engine itself; never copy/forward these.
    private const string HostedServiceTypeName = "Microsoft.Extensions.Hosting.IHostedService";

    // Framework assembly whose stack is rebuilt fresh per container rather than copied from the host
    // (its registration-time introspection breaks on a partial copy).
    private const string HttpClientAssemblyName = "Microsoft.Extensions.Http";

    private readonly IServiceProvider _root;
    private readonly IReadOnlyList<ServiceDescriptor> _hostDescriptors;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, ServiceProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ServiceProvider> _retired = new();

    public ExtensionServiceOverlay(
        IServiceProvider root,
        IReadOnlyList<ServiceDescriptor> hostDescriptors,
        ILogger? logger)
    {
        _root = root;
        _hostDescriptors = hostDescriptors;
        _logger = logger;
    }

    /// <summary>True once a container has been built for the given extension.</summary>
    public bool Has(string extensionId)
    {
        lock (_gate)
            return _providers.ContainsKey(extensionId);
    }

    /// <summary>The extension's container, or null if none has been built yet.</summary>
    public IServiceProvider? GetProvider(string extensionId)
    {
        lock (_gate)
            return _providers.TryGetValue(extensionId, out var p) ? p : null;
    }

    /// <summary>
    /// The provider an extension should receive (in <c>InitializeAsync</c>, job/event/scan contexts).
    /// Falls back to the root provider if no container has been built so callers never get null.
    /// Resolve scoped services by creating a scope from this provider, never directly.
    /// </summary>
    public IServiceProvider ProviderFor(string extensionId) => GetProvider(extensionId) ?? _root;

    /// <summary>
    /// Create a scope for executing one extension's work (an HTTP request, a job, a scan pass). The
    /// scope resolves the extension's own services, host singletons as the shared instances, and host
    /// scoped services as fresh container-owned instances built from those singletons.
    /// </summary>
    public IServiceScope CreateScope(string extensionId) => ProviderFor(extensionId).CreateScope();

    /// <summary>
    /// Build (or rebuild) the container for one extension. Only this extension's container is replaced;
    /// every other extension's container — and its state — is left untouched. A previous container for
    /// this id, if any, is retired and disposed at shutdown so in-flight scopes keep working. On
    /// failure the previous container is left in place.
    /// </summary>
    public void BuildProvider(
        string extensionId,
        IExtension extension,
        ExtensionContext context,
        Action<string, Exception> onConfigureFailure)
    {
        var services = new ServiceCollection();

        // Stand up the fragile framework stacks fresh, then point logging at the host so extension log
        // output is unified. AddHttpClient() in particular must own a consistent stack so an
        // extension's AddHttpClient<T>() call (which introspects the collection) succeeds.
        services.AddLogging();
        services.AddOptions();
        services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(_ => _root.GetRequiredService<ILoggerFactory>()));
        services.AddHttpClient();

        foreach (var descriptor in _hostDescriptors)
        {
            if (ShouldSkip(descriptor))
                continue;

            if (descriptor.Lifetime == ServiceLifetime.Singleton)
                AddSingletonForward(services, descriptor);
            else
                services.Add(descriptor); // copy scoped/transient — the container creates & owns its instance
        }

        try
        {
            extension.ConfigureServices(services, context);
        }
        catch (Exception ex)
        {
            onConfigureFailure(extension.Id, ex);
        }

        ServiceProvider built;
        try
        {
            // ValidateScopes=true keeps the host's "don't resolve scoped from the root" discipline.
            // ValidateOnBuild=false so a single misconfigured registration can't fail construction.
            built = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = false,
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to build service container for extension {Id}; keeping previous container", extensionId);
            return;
        }

        lock (_gate)
        {
            if (_providers.TryGetValue(extensionId, out var old))
                _retired.Add(old);
            _providers[extensionId] = built;
        }

        _logger?.LogDebug("Service container built for extension {Id}", extensionId);
    }

    /// <summary>Retire and forget an extension's container (on uninstall/disable).</summary>
    public void Remove(string extensionId)
    {
        lock (_gate)
        {
            if (_providers.Remove(extensionId, out var provider))
                _retired.Add(provider);
        }
    }

    private static bool ShouldSkip(ServiceDescriptor descriptor)
    {
        var type = descriptor.ServiceType;

        // Engine-provided services: the child container supplies its own.
        if (type == typeof(IServiceProvider)
            || type == typeof(IServiceScopeFactory)
            || type == typeof(IServiceProviderIsService)
            || type == typeof(IServiceProviderIsKeyedService))
            return true;

        // Hosted services belong to the host; the child container does not run a host.
        if (type.FullName == HostedServiceTypeName)
            return true;

        // Logging infrastructure is re-established via AddLogging() + a forwarded ILoggerFactory.
        if (type == typeof(ILoggerFactory)
            || type == typeof(ILogger)
            || type == typeof(ILoggerProvider)
            || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ILogger<>)))
            return true;

        // HttpClient factory infrastructure is re-established fresh via AddHttpClient(); copying the
        // host's descriptors leaves the registration-time introspection in an inconsistent state.
        if (ReferencesHttpClientInfrastructure(descriptor))
            return true;

        return false;
    }

    private static bool ReferencesHttpClientInfrastructure(ServiceDescriptor descriptor)
    {
        return InHttpClientAssembly(descriptor.ServiceType)
            || (descriptor.ImplementationType != null && InHttpClientAssembly(descriptor.ImplementationType))
            || (descriptor.ImplementationInstance != null && InHttpClientAssembly(descriptor.ImplementationInstance.GetType()));
    }

    // Matches the whole HttpClientFactory stack by assembly rather than namespace, so a single check
    // catches every type it registers regardless of namespace. The generic-argument walk also catches
    // option-config descriptors like IConfigureOptions<HttpClientFactoryOptions>.
    private static bool InHttpClientAssembly(Type type)
    {
        if (type.Assembly.GetName().Name == HttpClientAssemblyName)
            return true;
        if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments())
                if (InHttpClientAssembly(arg))
                    return true;
        }
        return false;
    }

    private void AddSingletonForward(IServiceCollection services, ServiceDescriptor descriptor)
    {
        var type = descriptor.ServiceType;

        // Singletons registered as a pre-created instance are copied as-is: this shares the exact same
        // instance with the host (no duplication), keeps ImplementationInstance non-null for framework
        // code that introspects it, and the container never disposes an instance it did not create.
        if ((!descriptor.IsKeyedService && descriptor.ImplementationInstance != null)
            || (descriptor.IsKeyedService && descriptor.KeyedImplementationInstance != null))
        {
            services.Add(descriptor);
            return;
        }

        // Open-generic singletons can't be expressed as a factory registration; copy as-is.
        if (type.IsGenericTypeDefinition)
        {
            services.Add(descriptor);
            return;
        }

        if (descriptor.IsKeyedService)
        {
            var key = descriptor.ServiceKey;
            services.Add(new ServiceDescriptor(
                type,
                key,
                (_, k) => ((IKeyedServiceProvider)_root).GetRequiredKeyedService(type, k),
                ServiceLifetime.Singleton));
        }
        else
        {
            services.Add(new ServiceDescriptor(
                type,
                _ => _root.GetService(type)!,
                ServiceLifetime.Singleton));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var provider in _retired)
                TryDispose(provider);
            _retired.Clear();

            foreach (var provider in _providers.Values)
                TryDispose(provider);
            _providers.Clear();
        }
    }

    private void TryDispose(ServiceProvider provider)
    {
        try
        {
            provider.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error disposing an extension service container");
        }
    }
}
