using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace Cove.Plugins;

/// <summary>
/// An <see cref="EndpointDataSource"/> that also implements <see cref="IEndpointRouteBuilder"/>
/// so extension endpoints can be collected in an isolated container per extension.
/// Runtime changes replace this immutable per-generation source in
/// <see cref="ExtensionEndpointRegistry"/>, which invalidates ASP.NET Core's routing DFA.
/// </summary>
public class ExtensionEndpointDataSource : EndpointDataSource, IEndpointRouteBuilder
{
    private readonly IEndpointRouteBuilder _parent;
    private readonly string? _extensionId;
    private readonly IServiceProvider? _serviceProvider;
    private readonly ExtensionManager.ExtensionExecutionHandle? _execution;
    private readonly List<EndpointDataSource> _dataSources = new();
    private readonly object _endpointsGate = new();
    private IReadOnlyList<Endpoint>? _endpoints;

    public ExtensionEndpointDataSource(
        IEndpointRouteBuilder parent,
        string? extensionId = null,
        IServiceProvider? serviceProvider = null,
        ExtensionManager.ExtensionExecutionHandle? execution = null)
    {
        _parent = parent;
        _extensionId = extensionId;
        _serviceProvider = serviceProvider;
        _execution = execution;
    }

    public override IReadOnlyList<Endpoint> Endpoints => MaterializeEndpoints();

    /// <summary>
    /// Build and cache request delegates while the owning provider generation is leased. Runtime
    /// publication calls this before exposing the source to the routing matcher.
    /// </summary>
    public IReadOnlyList<Endpoint> MaterializeEndpoints()
    {
        lock (_endpointsGate)
            return _endpoints ??= _dataSources.SelectMany(ds => ds.Endpoints).Select(Tag).ToList();
    }

    /// <summary>
    /// Stamp each extension endpoint with <see cref="ExtensionEndpointMetadata"/> so the host
    /// pipeline can execute it against the extension service overlay rather than the root
    /// container. Endpoints are otherwise preserved exactly (route pattern, delegate, order).
    /// </summary>
    private Endpoint Tag(Endpoint endpoint)
    {
        if (_extensionId == null || endpoint is not RouteEndpoint routeEndpoint)
            return endpoint;

        // Ownership and generation are host-stamped. Replace any extension-supplied marker so an
        // endpoint cannot spoof another owner or retain an ID-only marker across provider reloads.
        var metadata = routeEndpoint.Metadata
            .Where(item => item is not ExtensionEndpointMetadata)
            .ToList();
        metadata.Add(new ExtensionEndpointMetadata(_extensionId, _execution));
        return new RouteEndpoint(
            routeEndpoint.RequestDelegate!,
            routeEndpoint.RoutePattern,
            routeEndpoint.Order,
            new EndpointMetadataCollection(metadata),
            routeEndpoint.DisplayName);
    }

    // A published per-generation source is immutable. Runtime changes replace the entire source in
    // ExtensionEndpointRegistry, whose change token rebuilds the matcher.
    public override IChangeToken GetChangeToken() => new CancellationChangeToken(CancellationToken.None);

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
