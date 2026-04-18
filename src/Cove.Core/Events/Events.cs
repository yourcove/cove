namespace Cove.Core.Events;

public enum EventType
{
    // Entity lifecycle
    SceneCreated, SceneUpdated, SceneDeleted,
    PerformerCreated, PerformerUpdated, PerformerDeleted,
    TagCreated, TagUpdated, TagDeleted, TagMerged,
    StudioCreated, StudioUpdated, StudioDeleted,
    GalleryCreated, GalleryUpdated, GalleryDeleted,
    ImageCreated, ImageUpdated, ImageDeleted,
    GroupCreated, GroupUpdated, GroupDeleted,
    SceneMarkerCreated, SceneMarkerUpdated, SceneMarkerDeleted,

    // Jobs
    ScanStarted, ScanProgress, ScanCompleted,
    GenerateStarted, GenerateProgress, GenerateCompleted,
    AutoTagStarted, AutoTagProgress, AutoTagCompleted,
    CleanStarted, CleanProgress, CleanCompleted,

    // System
    ServerStarted, ServerStopping
}

public record CoveEvent(EventType Type, object? Data = null);

public record EntityEvent(EventType Type, string EntityType, int EntityId, object? Entity = null) : CoveEvent(Type, Entity);

public record JobEvent(EventType Type, string JobId, string Description, double Progress, string? SubTask = null) : CoveEvent(Type);

public interface IEventBus
{
    void Publish(CoveEvent evt);
    IDisposable Subscribe(Action<CoveEvent> handler);
    IDisposable Subscribe(EventType type, Action<CoveEvent> handler);
    IDisposable Subscribe<T>(Action<T> handler) where T : CoveEvent;
}

public class EventBus : IEventBus
{
    private readonly List<Subscription> _subscriptions = [];
    private readonly Lock _lock = new();

    public void Publish(CoveEvent evt)
    {
        List<Subscription> subs;
        lock (_lock) { subs = [.. _subscriptions]; }

        foreach (var sub in subs)
        {
            if (sub.Type == null || sub.Type == evt.Type)
            {
                try { sub.Handler(evt); }
                catch { /* Don't let subscriber errors crash publisher */ }
            }
        }
    }

    public IDisposable Subscribe(Action<CoveEvent> handler)
    {
        var sub = new Subscription(null, handler);
        lock (_lock) { _subscriptions.Add(sub); }
        return new Unsubscriber(() => { lock (_lock) { _subscriptions.Remove(sub); } });
    }

    public IDisposable Subscribe(EventType type, Action<CoveEvent> handler)
    {
        var sub = new Subscription(type, handler);
        lock (_lock) { _subscriptions.Add(sub); }
        return new Unsubscriber(() => { lock (_lock) { _subscriptions.Remove(sub); } });
    }

    public IDisposable Subscribe<T>(Action<T> handler) where T : CoveEvent
    {
        var sub = new Subscription(null, evt => { if (evt is T typed) handler(typed); });
        lock (_lock) { _subscriptions.Add(sub); }
        return new Unsubscriber(() => { lock (_lock) { _subscriptions.Remove(sub); } });
    }

    private record Subscription(EventType? Type, Action<CoveEvent> Handler);

    private class Unsubscriber(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
