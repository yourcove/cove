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

    private static string MapEventType(EventType type) => type switch
    {
        EventType.SceneCreated => "scene.created",
        EventType.SceneUpdated => "scene.updated",
        EventType.SceneDeleted => "scene.deleted",
        EventType.PerformerCreated => "performer.created",
        EventType.PerformerUpdated => "performer.updated",
        EventType.PerformerDeleted => "performer.deleted",
        EventType.TagCreated => "tag.created",
        EventType.TagUpdated => "tag.updated",
        EventType.TagDeleted => "tag.deleted",
        EventType.TagMerged => "tag.merged",
        EventType.StudioCreated => "studio.created",
        EventType.StudioUpdated => "studio.updated",
        EventType.StudioDeleted => "studio.deleted",
        EventType.GalleryCreated => "gallery.created",
        EventType.GalleryUpdated => "gallery.updated",
        EventType.GalleryDeleted => "gallery.deleted",
        EventType.ImageCreated => "image.created",
        EventType.ImageUpdated => "image.updated",
        EventType.ImageDeleted => "image.deleted",
        EventType.GroupCreated => "group.created",
        EventType.GroupUpdated => "group.updated",
        EventType.GroupDeleted => "group.deleted",
        EventType.SceneMarkerCreated => "scenemarker.created",
        EventType.SceneMarkerUpdated => "scenemarker.updated",
        EventType.SceneMarkerDeleted => "scenemarker.deleted",
        EventType.ScanStarted => "scan.started",
        EventType.ScanCompleted => "scan.completed",
        _ => type.ToString().ToLowerInvariant(),
    };

    public void Dispose() => _subscription?.Dispose();
}
