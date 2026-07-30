using Cove.Core.Events;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cove.Api.Middleware;

/// <summary>
/// Global action filter that automatically publishes EntityEvents on the core EventBus
/// whenever entity CRUD operations complete successfully. This ensures extensions receive
/// lifecycle events without modifying every controller.
/// </summary>
public sealed class EntityEventFilter : IAsyncActionFilter
{
    internal static readonly Dictionary<string, string> ControllerEntityMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Videos"] = "video",
        ["Performers"] = "performer",
        ["Studios"] = "studio",
        ["Tags"] = "tag",
        ["Galleries"] = "gallery",
        ["Images"] = "image",
        ["Groups"] = "group",
        ["Audios"] = "audio",
        ["Texts"] = "text",
    };

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var result = await next();

        // Only publish events for successful mutations
        if (result.Exception != null || result.Canceled) return;

        var controllerName = context.RouteData.Values["controller"]?.ToString();
        if (controllerName == null || !ControllerEntityMap.TryGetValue(controllerName, out var entityType))
            return;

        var actionName = context.RouteData.Values["action"]?.ToString()?.ToLowerInvariant();
        if (actionName == null) return;

        var eventBus = context.HttpContext.RequestServices.GetService<IEventBus>();
        if (eventBus == null) return;

        var (eventType, entityId) = DetermineEvent(actionName, entityType, context, result);
        if (eventType == null) return;

        // A bulk update names every entity it touched: an event carrying id 0 says something changed
        // without saying what, which is no more actionable than silence.
        if (actionName == "bulkupdate" && ExtractBulkIds(context) is { Count: > 0 } bulkIds)
        {
            foreach (var id in bulkIds)
            {
                eventBus.Publish(new EntityEvent(eventType.Value, entityType, id));
            }
            return;
        }

        eventBus.Publish(new EntityEvent(eventType.Value, entityType, entityId));
    }

    /// <summary>
    /// Reads the id list off a bulk action's request DTO by convention (an <c>Ids</c> property of
    /// <c>List&lt;int&gt;</c>). Returns null when no argument exposes one, so the caller falls back to the
    /// single-event path rather than dropping the event.
    /// </summary>
    private static List<int>? ExtractBulkIds(ActionExecutingContext context)
    {
        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument == null) continue;

            var idsProp = argument.GetType().GetProperty("Ids");
            if (idsProp?.GetValue(argument) is List<int> ids)
                return ids;
        }

        return null;
    }

    private static (EventType? eventType, int entityId) DetermineEvent(
        string action, string entityType, ActionExecutingContext context, ActionExecutedContext result)
    {
        var entityId = ExtractEntityId(context, result);

        return action switch
        {
            "create" => (GetEventType(entityType, "created"), entityId),
            "update" => (GetEventType(entityType, "updated"), entityId),
            "delete" => (GetEventType(entityType, "deleted"), entityId),
            "bulkupdate" => (GetEventType(entityType, "updated"), 0), // bulk = id 0
            _ => (null, 0),
        };
    }

    private static int ExtractEntityId(ActionExecutingContext context, ActionExecutedContext result)
    {
        // Try route parameter first
        if (context.RouteData.Values.TryGetValue("id", out var idObj) && int.TryParse(idObj?.ToString(), out var id))
            return id;

        // Try to get from response body for creates
        if (result.Result is ObjectResult { Value: not null } objResult)
        {
            var idProp = objResult.Value.GetType().GetProperty("Id") ?? objResult.Value.GetType().GetProperty("id");
            if (idProp != null && idProp.PropertyType == typeof(int))
                return (int)(idProp.GetValue(objResult.Value) ?? 0);
        }

        return 0;
    }

    /// <summary>
    /// Resolves the <see cref="EventType"/> for an entity and operation from the enum's own naming
    /// (<c>video</c> + <c>created</c> → <see cref="EventType.VideoCreated"/>).
    /// </summary>
    /// <remarks>
    /// Parsed rather than listed so registering a controller in <see cref="ControllerEntityMap"/> is the
    /// ONE edit that adds an entity. Returns null when the enum has no matching member, which is what keeps
    /// a controller whose entity has no event type from publishing a wrong one.
    /// </remarks>
    internal static EventType? GetEventType(string entityType, string operation) =>
        Enum.TryParse<EventType>($"{Capitalize(entityType)}{Capitalize(operation)}", out var eventType)
            ? eventType
            : null;

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}

