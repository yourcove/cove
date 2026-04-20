using Cove.Plugins;

namespace Cove.Sdk;

/// <summary>
/// Base class for extensions that react to entity lifecycle events.
/// Provides a typed event routing system so you can handle specific event types
/// with dedicated methods instead of a single OnEventAsync switch.
/// </summary>
public abstract class EventExtensionBase : CoveExtensionBase, IEventExtension
{
    private readonly Dictionary<string, Func<ExtensionEvent, CancellationToken, Task>> _handlers = new(StringComparer.OrdinalIgnoreCase);

    protected EventExtensionBase()
    {
        RegisterHandlers();
    }

    /// <summary>
    /// Override to register event handlers using <see cref="On"/>.
    /// </summary>
    protected virtual void RegisterHandlers() { }

    /// <summary>
    /// Register a handler for a specific event type (e.g. "scene.created", "performer.updated").
    /// </summary>
    protected void On(string eventType, Func<ExtensionEvent, CancellationToken, Task> handler)
    {
        _handlers[eventType] = handler;
    }

    /// <summary>
    /// Register a handler for entity created events.
    /// </summary>
    protected void OnCreated(string entityType, Func<ExtensionEvent, CancellationToken, Task> handler)
        => On($"{entityType}.created", handler);

    /// <summary>
    /// Register a handler for entity updated events.
    /// </summary>
    protected void OnUpdated(string entityType, Func<ExtensionEvent, CancellationToken, Task> handler)
        => On($"{entityType}.updated", handler);

    /// <summary>
    /// Register a handler for entity deleted events.
    /// </summary>
    protected void OnDeleted(string entityType, Func<ExtensionEvent, CancellationToken, Task> handler)
        => On($"{entityType}.deleted", handler);

    public Task OnEventAsync(ExtensionEvent evt, CancellationToken ct = default)
    {
        return _handlers.TryGetValue(evt.EventType, out var handler)
            ? handler(evt, ct)
            : Task.CompletedTask;
    }
}
