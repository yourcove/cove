using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace Cove.Plugins;

/// <summary>
/// An <see cref="EndpointDataSource"/> that also implements <see cref="IEndpointRouteBuilder"/>
/// so extension endpoints can be collected in an isolated container per extension.
/// Supports runtime invalidation so ASP.NET Core rebuilds the routing DFA when endpoints change.
/// </summary>
public class ExtensionEndpointDataSource : EndpointDataSource, IEndpointRouteBuilder
{
    private readonly IEndpointRouteBuilder _parent;
    private readonly string? _extensionId;
    private readonly IServiceProvider? _serviceProvider;
    private readonly List<EndpointDataSource> _dataSources = new();
    private CancellationTokenSource _cts = new();

    public ExtensionEndpointDataSource(IEndpointRouteBuilder parent, string? extensionId = null, IServiceProvider? serviceProvider = null)
    {
        _parent = parent;
        _extensionId = extensionId;
        _serviceProvider = serviceProvider;
    }

    public override IReadOnlyList<Endpoint> Endpoints =>
        _dataSources.SelectMany(ds => ds.Endpoints).Select(Tag).ToList();

    /// <summary>
    /// Stamp each extension endpoint with <see cref="ExtensionEndpointMetadata"/> so the host
    /// pipeline can execute it against the extension service overlay rather than the root
    /// container. Endpoints are otherwise preserved exactly (route pattern, delegate, order).
    /// </summary>
    private Endpoint Tag(Endpoint endpoint)
    {
        if (_extensionId == null || endpoint is not RouteEndpoint routeEndpoint)
            return endpoint;

        // Ownership is host-assigned. ExtensionEndpointMetadata is public because the request
        // pipeline consumes it, so an extension may have attached its own marker while building
        // the route. Remove every supplied marker before stamping the data source's real owner.
        var metadata = routeEndpoint.Metadata
            .Where(item => item is not ExtensionEndpointMetadata)
            .Append<object>(new ExtensionEndpointMetadata(_extensionId))
            .ToList();
        return new RouteEndpoint(
            routeEndpoint.RequestDelegate!,
            routeEndpoint.RoutePattern,
            routeEndpoint.Order,
            new EndpointMetadataCollection(metadata),
            routeEndpoint.DisplayName);
    }

    public override IChangeToken GetChangeToken() =>
        new CancellationChangeToken(_cts.Token);

    public IApplicationBuilder CreateApplicationBuilder() => _parent.CreateApplicationBuilder();

    public ICollection<EndpointDataSource> DataSources => _dataSources;

    /// <summary>
    /// The service provider used while endpoints are built. This is the extension overlay (when
    /// provided) rather than the root container, which is essential: minimal-API parameter binding
    /// uses <see cref="IServiceProviderIsService"/> from this provider at build time to decide
    /// whether each handler parameter is injected from DI or bound from the request body. The root
    /// container doesn't know an extension's services, so without this its parameters would be
    /// misclassified as body parameters (the bug that previously required a host restart).
    /// </summary>
    public IServiceProvider ServiceProvider => _serviceProvider ?? _parent.ServiceProvider;

    public void NotifyChanged()
    {
        var oldCts = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        oldCts.Cancel();
        oldCts.Dispose();
    }
}

public sealed class ExtensionEndpointRegistry : EndpointDataSource
{
    private readonly object _lock = new();
    private readonly Dictionary<string, EndpointDataSource> _sources = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _cts = new();
    private IReadOnlyList<Endpoint>? _endpoints;

    public override IReadOnlyList<Endpoint> Endpoints
    {
        get
        {
            lock (_lock)
                return _endpoints ??= _sources.Values.SelectMany(s => s.Endpoints).ToList();
        }
    }

    public override IChangeToken GetChangeToken()
    {
        lock (_lock)
            return new CancellationChangeToken(_cts.Token);
    }

    /// <summary>Add or replace one extension's endpoints, then rebuild the route table.</summary>
    public void SetExtension(string extensionId, EndpointDataSource source)
    {
        lock (_lock)
            _sources[extensionId] = source;
        Invalidate();
    }

    /// <summary>Remove one extension's endpoints, then rebuild the route table.</summary>
    public void RemoveExtension(string extensionId)
    {
        bool removed;
        lock (_lock)
            removed = _sources.Remove(extensionId);
        if (removed)
            Invalidate();
    }

    private void Invalidate()
    {
        CancellationTokenSource old;
        lock (_lock)
        {
            _endpoints = null;
            old = _cts;
            _cts = new CancellationTokenSource();
        }
        // Cancel the previously-handed-out token so the matcher rebuilds and calls GetChangeToken again.
        old.Cancel();
        old.Dispose();
    }
}
