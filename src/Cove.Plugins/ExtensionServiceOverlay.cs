using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Cove.Plugins;

/// <summary>
/// Endpoint metadata that marks an ASP.NET Core endpoint as belonging to a runtime-loaded
/// extension. The host pipeline uses this to execute the endpoint against that extension's own
/// service container instead of the immutable root container.
/// </summary>
public sealed record ExtensionEndpointMetadata(
    string ExtensionId,
    ExtensionManager.ExtensionExecutionHandle? Execution = null);

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
/// <item><b>Closed host singletons are forwarded</b> (resolved from the root provider) so there is
/// exactly one shared instance — one database pool, one cache, one logger factory, one exchange.
/// Open-generic host singletons are deliberately not copied: the built-in container cannot forward
/// their future closed instances without making the extension provider their owner. An extension
/// that needs another open-generic service must register its own implementation; the fresh
/// logging/options/HTTP stacks below already supply their framework generics.</item>
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
    private readonly Dictionary<string, ProviderEntry> _providers = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

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

    /// <summary>
    /// Return the provider only for synchronous endpoint-model construction while the caller holds
    /// the extension lifecycle gate. Runtime extension work must use <see cref="CreateScope"/> so
    /// provider retirement can account for every active operation.
    /// </summary>
    internal IServiceProvider? GetProviderForEndpointBuild(
        string extensionId,
        IExtension extension,
        object generation)
    {
        lock (_gate)
        {
            if (!_providers.TryGetValue(extensionId, out var entry)
                || !ReferenceEquals(entry.Extension, extension)
                || !ReferenceEquals(entry.Generation, generation))
                return null;

            var scopeFactory = new TrackedScopeFactory(this, extensionId, entry);
            return new TrackedServiceProvider(entry.Provider, scopeFactory);
        }
    }

    /// <summary>
    /// Create a scope for executing one extension's work (an HTTP request, a job, a scan pass). The
    /// scope resolves the extension's own services, host singletons as the shared instances, and host
    /// scoped services as fresh container-owned instances built from those singletons. Throws when
    /// the extension has no current provider; it never silently falls back to the host container.
    /// </summary>
    public IServiceScope CreateScope(string extensionId)
        => TryCreateScope(extensionId, out var scope)
            ? scope
            : throw new InvalidOperationException($"No service container is available for extension '{extensionId}'.");

    /// <summary>
    /// Atomically lease the current provider generation and create a tracked scope. Returning false
    /// means the extension has no active provider; callers must not substitute the host provider for
    /// runtime extension work.
    /// </summary>
    public bool TryCreateScope(string extensionId, out IServiceScope scope)
        => TryCreateScopeCore(extensionId, expectedExtension: null, expectedGeneration: null, out scope);

    internal bool TryCreateScope(
        string extensionId,
        IExtension extension,
        object generation,
        out IServiceScope scope)
        => TryCreateScopeCore(extensionId, extension, generation, out scope);

    private bool TryCreateScopeCore(
        string extensionId,
        IExtension? expectedExtension,
        object? expectedGeneration,
        out IServiceScope scope)
    {
        ProviderEntry? entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_providers.TryGetValue(extensionId, out entry)
                || (expectedExtension != null && !ReferenceEquals(entry.Extension, expectedExtension))
                || (expectedGeneration != null && !ReferenceEquals(entry.Generation, expectedGeneration)))
            {
                scope = null!;
                return false;
            }

            Acquire(entry);
        }

        scope = CreateTrackedScope(extensionId, entry);
        return true;
    }

    internal bool TryGetGeneration(string extensionId, IExtension extension, out object generation)
    {
        lock (_gate)
        {
            if (_providers.TryGetValue(extensionId, out var entry)
                && ReferenceEquals(entry.Extension, extension))
            {
                generation = entry.Generation;
                return true;
            }

            generation = null!;
            return false;
        }
    }

    internal bool TryCreateLease(
        string extensionId,
        IExtension extension,
        object generation,
        out ProviderLease lease)
    {
        ProviderEntry? entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_providers.TryGetValue(extensionId, out entry)
                || !ReferenceEquals(entry.Extension, extension)
                || !ReferenceEquals(entry.Generation, generation))
            {
                lease = null!;
                return false;
            }

            Acquire(entry);
        }

        var scopeFactory = new TrackedScopeFactory(this, extensionId, entry);
        var services = new TrackedServiceProvider(entry.Provider, scopeFactory);
        lease = new ProviderLease(services, () => ReleaseScope(entry));
        return true;
    }

    private IServiceScope CreateNestedScope(string extensionId, ProviderEntry entry)
    {
        lock (_gate)
        {
            if (entry.Retired || entry.Disposed)
                throw new InvalidOperationException($"The service container for extension '{extensionId}' has been retired.");

            Acquire(entry);
        }

        return CreateTrackedScope(extensionId, entry);
    }

    private IServiceScope CreateTrackedScope(string extensionId, ProviderEntry entry)
    {
        IServiceScope innerScope;
        try
        {
            innerScope = entry.Provider.CreateScope();
        }
        catch
        {
            ReleaseScope(entry);
            throw;
        }

        var scopeFactory = new TrackedScopeFactory(this, extensionId, entry);
        var services = new TrackedServiceProvider(innerScope.ServiceProvider, scopeFactory);
        return new TrackedScope(innerScope, services, () => ReleaseScope(entry));
    }

    private static void Acquire(ProviderEntry entry) => entry.ActiveScopes++;

    /// <summary>
    /// Build (or rebuild) the container for one extension. Only this extension's container is replaced;
    /// every other extension's container — and its state — is left untouched. A previous container for
    /// this id, if any, is retired and disposed as soon as its final active scope drains. On failure
    /// the previous container is left in place.
    /// </summary>
    public void BuildProvider(
        string extensionId,
        IExtension extension,
        ExtensionContext context,
        Action<string, Exception> onConfigureFailure) =>
        TryBuildProvider(extensionId, extension, context, onConfigureFailure);

    /// <summary>
    /// Try to build and publish a replacement container while reporting whether publication succeeded.
    /// </summary>
    /// <returns><see langword="true"/> when the replacement container was published.</returns>
    public bool TryBuildProvider(
        string extensionId,
        IExtension extension,
        ExtensionContext context,
        Action<string, Exception> onBuildFailure)
    {
        var services = new ServiceCollection();
        var entry = new ProviderEntry(extension);
        var scopeFactory = new TrackedScopeFactory(this, extensionId, entry);

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

        var contributionStartIndex = services.Count;
        try
        {
            extension.ConfigureServices(services, context);
            ExtensionContributionServiceRegistration.KeyProvidersAddedSince(
                services,
                contributionStartIndex,
                extensionId);
        }
        catch (Exception ex)
        {
            onBuildFailure(extension.Id, ex);
            return false;
        }

        // This host-owned registration is added last so extension services can safely constructor-
        // inject a scope factory without bypassing provider-generation drain accounting.
        services.Replace(ServiceDescriptor.Singleton<IExtensionServiceScopeFactory>(scopeFactory));

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
            onBuildFailure(extension.Id, ex);
            return false;
        }
        entry.Provider = built;

        ProviderEntry? dispose = null;
        var reject = false;
        lock (_gate)
        {
            if (_disposed)
            {
                reject = true;
            }
            else
            {
                if (_providers.TryGetValue(extensionId, out var old))
                    dispose = Retire(old);
                _providers[extensionId] = entry;
            }
        }

        if (reject)
        {
            TryDispose(built);
            throw new ObjectDisposedException(nameof(ExtensionServiceOverlay));
        }
        if (dispose != null)
            TryDispose(dispose.Provider);

        _logger?.LogDebug("Service container built for extension {Id}", extensionId);
        return true;
    }

    /// <summary>Retire and forget an extension's container (on uninstall/disable).</summary>
    public void Remove(string extensionId)
    {
        ProviderEntry? dispose = null;
        lock (_gate)
        {
            if (_providers.Remove(extensionId, out var entry))
                dispose = Retire(entry);
        }

        if (dispose != null)
            TryDispose(dispose.Provider);
    }

    private static bool ShouldSkip(ServiceDescriptor descriptor)
    {
        var type = descriptor.ServiceType;

        // Engine-provided services: the child container supplies its own.
        if (type == typeof(IServiceProvider)
            || type == typeof(IServiceScopeFactory)
            || type == typeof(IExtensionServiceScopeFactory)
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

        // Open-generic singletons cannot be resolved to a pre-created instance, so there is no safe
        // way to preserve the shared, host-owned singleton contract. Copying the descriptor would
        // silently make every closed instance extension-owned and dispose it on reload. Skip the
        // descriptor instead; extensions may register their own implementation, and framework
        // generics are supplied by the fresh logging/options/HTTP stacks above.
        if (type.IsGenericTypeDefinition)
        {
            _logger?.LogDebug(
                "Skipping open-generic host singleton {ServiceType} in extension container",
                type);
            return;
        }

        if (descriptor.IsKeyedService)
        {
            var key = descriptor.ServiceKey;
            var instance = _root.GetRequiredKeyedService(type, key);
            services.Add(ServiceDescriptor.KeyedSingleton(type, key, instance));
        }
        else
        {
            var instance = _root.GetRequiredService(type);
            services.Add(ServiceDescriptor.Singleton(type, instance));
        }
    }

    public void Dispose()
    {
        List<ProviderEntry> dispose;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            dispose = _providers.Values
                .Select(Retire)
                .Where(entry => entry != null)
                .Cast<ProviderEntry>()
                .ToList();
            _providers.Clear();
        }

        foreach (var entry in dispose)
            TryDispose(entry.Provider);
    }

    private ProviderEntry? Retire(ProviderEntry entry)
    {
        entry.Retired = true;
        if (entry.ActiveScopes != 0 || entry.Disposed)
            return null;

        entry.Disposed = true;
        return entry;
    }

    private void ReleaseScope(ProviderEntry entry)
    {
        ProviderEntry? dispose = null;
        lock (_gate)
        {
            if (entry.ActiveScopes <= 0)
                return;

            entry.ActiveScopes--;
            if (entry.Retired && entry.ActiveScopes == 0 && !entry.Disposed)
            {
                entry.Disposed = true;
                dispose = entry;
            }
        }

        if (dispose != null)
            TryDispose(dispose.Provider);
    }

    private void TryDispose(ServiceProvider provider)
    {
        try
        {
            provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error disposing an extension service container");
        }
    }

    private sealed class ProviderEntry(IExtension extension)
    {
        public IExtension Extension { get; } = extension;
        public object Generation { get; } = new();
        public ServiceProvider Provider { get; set; } = null!;
        public int ActiveScopes { get; set; }
        public bool Retired { get; set; }
        public bool Disposed { get; set; }
    }

    internal sealed class ProviderLease(IServiceProvider services, Action release) : IDisposable
    {
        private int _disposed;

        public IServiceProvider Services { get; } = services;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                release();
        }
    }

    private sealed class TrackedScopeFactory(
        ExtensionServiceOverlay owner,
        string extensionId,
        ProviderEntry entry) : IServiceScopeFactory, IExtensionServiceScopeFactory
    {
        public IServiceScope CreateScope() => owner.CreateNestedScope(extensionId, entry);

        public AsyncServiceScope CreateAsyncScope() => new(CreateScope());
    }

    private sealed class TrackedServiceProvider(
        IServiceProvider services,
        IServiceScopeFactory scopeFactory) : IServiceProvider, IKeyedServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IServiceProvider) || serviceType == typeof(IKeyedServiceProvider))
                return this;
            if (serviceType == typeof(IServiceScopeFactory))
                return scopeFactory;
            if (serviceType == typeof(IExtensionServiceScopeFactory))
                return scopeFactory;

            return services.GetService(serviceType);
        }

        public object? GetKeyedService(Type serviceType, object? serviceKey)
            => ((IKeyedServiceProvider)services).GetKeyedService(serviceType, serviceKey);

        public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
            => ((IKeyedServiceProvider)services).GetRequiredKeyedService(serviceType, serviceKey);
    }

    private sealed class TrackedScope(
        IServiceScope scope,
        IServiceProvider services,
        Action release) : IServiceScope, IAsyncDisposable
    {
        private int _disposed;

        public IServiceProvider ServiceProvider => services;

        public void Dispose()
            => DisposeAsync().AsTask().GetAwaiter().GetResult();

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                if (scope is IAsyncDisposable asyncScope)
                    await asyncScope.DisposeAsync().ConfigureAwait(false);
                else
                    scope.Dispose();
            }
            finally
            {
                release();
            }
        }
    }
}
