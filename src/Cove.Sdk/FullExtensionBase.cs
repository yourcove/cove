using Cove.Plugins;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Cove.Sdk;

/// <summary>
/// Full-stack extension base combining data, API, UI, events, and jobs.
/// Extend this when your extension needs multiple capabilities.
/// Override only the methods relevant to your extension — all capabilities
/// have safe no-op defaults.
/// </summary>
public abstract class FullExtensionBase : CoveExtensionBase,
    IDataExtension, IApiExtension, IUIExtension, IEventExtension, IJobExtension, IStatefulExtension, IScanParticipant, IAutoTagParticipant
{
    private readonly List<ExtensionMigration> _migrations = [];
    private readonly List<ExtensionJobDefinition> _jobs = [];
    private readonly Dictionary<string, Func<IReadOnlyDictionary<string, string>?, IJobProgress, CancellationToken, Task>> _jobRunners = [];
    private readonly Dictionary<string, Func<ExtensionEvent, CancellationToken, Task>> _eventHandlers = new(StringComparer.OrdinalIgnoreCase);
    private IExtensionStore? _store;

    protected FullExtensionBase()
    {
        DefineJobs();
        DefineEventHandlers();
    }

    // ── IExtensionStore ──────────────────────────────────────────────
    /// <summary>Access the extension's key-value store (available after SetStore is called).</summary>
    protected IExtensionStore Store => _store ?? throw new InvalidOperationException("Store not yet initialized");
    void IStatefulExtension.SetStore(IExtensionStore store) => _store = store;

    // ── IDataExtension ───────────────────────────────────────────────
    public virtual void ConfigureModel(ModelBuilder modelBuilder) { }

    /// <summary>Override to define migrations. Call <see cref="Migration"/> to add each one.</summary>
    protected virtual void DefineMigrations() { }

    protected void Migration(string name, string sql) => _migrations.Add(new ExtensionMigration(name, sql));

    public IReadOnlyList<ExtensionMigration> GetMigrations()
    {
        _migrations.Clear();
        DefineMigrations();
        return _migrations;
    }

    // ── IApiExtension ────────────────────────────────────────────────
    public virtual void MapEndpoints(IEndpointRouteBuilder endpoints) { }

    // ── IUIExtension ─────────────────────────────────────────────────
    /// <summary>
    /// Override to build your UI manifest. Use <see cref="UIManifestBuilder"/> for ergonomic creation.
    /// Default implementation returns an empty manifest.
    /// </summary>
    public virtual UIManifest GetUIManifest() => new();

    /// <summary>Create a UIManifestBuilder pre-configured with this extension's ID.</summary>
    protected UIManifestBuilder ManifestBuilder() => new(Id);

    // ── IEventExtension ──────────────────────────────────────────────
    /// <summary>Override to register event handlers with <see cref="OnEvent"/>, <see cref="OnCreated"/>, etc.</summary>
    protected virtual void DefineEventHandlers() { }

    protected void OnEvent(string eventType, Func<ExtensionEvent, CancellationToken, Task> handler)
        => _eventHandlers[eventType] = handler;
    protected void OnCreated(string entityType, Func<ExtensionEvent, CancellationToken, Task> handler)
        => OnEvent($"{entityType}.created", handler);
    protected void OnUpdated(string entityType, Func<ExtensionEvent, CancellationToken, Task> handler)
        => OnEvent($"{entityType}.updated", handler);
    protected void OnDeleted(string entityType, Func<ExtensionEvent, CancellationToken, Task> handler)
        => OnEvent($"{entityType}.deleted", handler);

    public Task OnEventAsync(ExtensionEvent evt, CancellationToken ct = default)
        => _eventHandlers.TryGetValue(evt.EventType, out var handler)
            ? handler(evt, ct)
            : Task.CompletedTask;

    // ── IJobExtension ────────────────────────────────────────────────
    public IReadOnlyList<ExtensionJobDefinition> Jobs => _jobs;

    /// <summary>Override to register jobs with <see cref="Job"/>.</summary>
    protected virtual void DefineJobs() { }

    protected void Job(
        string id,
        string name,
        Func<IReadOnlyDictionary<string, string>?, IJobProgress, CancellationToken, Task> handler,
        string? description = null,
        bool supportsParameters = false)
    {
        _jobs.Add(new ExtensionJobDefinition(id, name, description, supportsParameters));
        _jobRunners[id] = handler;
    }

    public Task RunJobAsync(string jobId, IReadOnlyDictionary<string, string>? parameters, IJobProgress progress, CancellationToken ct)
        => _jobRunners.TryGetValue(jobId, out var runner)
            ? runner(parameters, progress, ct)
            : throw new InvalidOperationException($"Unknown job '{jobId}' in extension '{Id}'");

    // ── IScanParticipant ─────────────────────────────────────────────
    /// <summary>
    /// Override to participate in the core library scan.
    /// Default implementation is a no-op. Extensions that manage their own file types
    /// should override this to scan for and ingest those files.
    /// </summary>
    public virtual Task ScanAsync(ScanContext context, CancellationToken ct = default)
        => Task.CompletedTask;

    // ── IAutoTagParticipant ──────────────────────────────────────────
    /// <summary>
    /// Override to participate in the core auto-tag operation.
    /// Default implementation is a no-op.
    /// </summary>
    public virtual Task AutoTagAsync(AutoTagContext context, CancellationToken ct = default)
        => Task.CompletedTask;
}
