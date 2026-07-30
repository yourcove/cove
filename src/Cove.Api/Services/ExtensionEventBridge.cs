using Cove.Core.Events;
using Cove.Plugins;

namespace Cove.Api.Services;

/// <summary>
/// Bridges the core EventBus to the extension event dispatch system.
/// Subscribes to all EntityEvent publications and dispatches them
/// as ExtensionEvents to all IEventExtension instances.
/// </summary>
public sealed class ExtensionEventBridge : IHostedService, IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly ExtensionManager _extensionManager;
    private readonly ILogger<ExtensionEventBridge> _logger;
    private IDisposable? _subscription;

    public ExtensionEventBridge(
        IEventBus eventBus,
        ExtensionManager extensionManager,
        ILogger<ExtensionEventBridge> logger)
    {
        _eventBus = eventBus;
        _extensionManager = extensionManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _eventBus.Subscribe<EntityEvent>(OnEntityEvent);
        _logger.LogInformation("Extension event bridge started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _logger.LogInformation("Extension event bridge stopped");
        return Task.CompletedTask;
    }

    private void OnEntityEvent(EntityEvent evt)
    {
        var extensionEvent = new ExtensionEvent(
            EventType: MapEventType(evt.Type),
            EntityType: evt.EntityType.ToLowerInvariant(),
            EntityId: evt.EntityId,
            Data: evt.Entity != null ? new Dictionary<string, object?> { ["entity"] = evt.Entity } : null
        );

        // Fire-and-forget dispatch to extensions (don't block the publisher)
        _ = Task.Run(async () =>
        {
            try
            {
                await _extensionManager.DispatchEventAsync(extensionEvent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error dispatching event {EventType} to extensions", extensionEvent.EventType);
            }
        });
    }

    /// <summary>The verbs an <see cref="EventType"/> name ends in, longest first so a prefix never wins.</summary>
    private static readonly string[] EventVerbs =
        ["Completed", "Progress", "Stopping", "Created", "Updated", "Deleted", "Started", "Merged"];

    /// <summary>
    /// Derives the extension-facing <c>noun.verb</c> name from an <see cref="EventType"/> member's own name.
    /// </summary>
    /// <remarks>
    /// Derived rather than enumerated so a new member is named correctly the day it is added: a name no
    /// rule produces is a dotless one like <c>"audiocreated"</c>, which matches no other event and so can
    /// have no subscriber written against it. An unrecognised verb keeps the plain lowercase name rather
    /// than guessing a split point.
    /// </remarks>
    internal static string MapEventType(EventType type)
    {
        var name = type.ToString();
        foreach (var verb in EventVerbs)
        {
            if (name.Length > verb.Length && name.EndsWith(verb, StringComparison.Ordinal))
            {
                return $"{name[..^verb.Length].ToLowerInvariant()}.{verb.ToLowerInvariant()}";
            }
        }

        return name.ToLowerInvariant();
    }

    public void Dispose() => _subscription?.Dispose();
}

