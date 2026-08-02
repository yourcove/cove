using Cove.Core.Events;
using Cove.Core.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace Cove.Api.Middleware;

/// <summary>
/// Global action filter that automatically publishes EntityEvents on the core EventBus
/// whenever entity CRUD operations complete successfully. This ensures extensions receive
/// lifecycle events without modifying every controller.
/// </summary>
public sealed class EntityEventFilter : IAsyncActionFilter
{
    private static readonly Dictionary<string, string> ControllerEntityMap = new(StringComparer.OrdinalIgnoreCase)
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

        // An action can complete without throwing and still reject the mutation with a 4xx/5xx result.
        if (result.Exception != null || result.Canceled || !IsSuccessful(result.Result)) return;

        var controllerName = context.RouteData.Values["controller"]?.ToString();
        if (controllerName == null || !ControllerEntityMap.TryGetValue(controllerName, out var entityType))
            return;

        var actionName = context.RouteData.Values["action"]?.ToString()?.ToLowerInvariant();
        if (actionName == null) return;

        var eventBus = context.HttpContext.RequestServices.GetService<IEventBus>();
        if (eventBus == null) return;

        var (eventType, entityId) = DetermineEvent(actionName, entityType, context, result);
        if (eventType == null) return;

        if (actionName is "bulkupdate" or "bulkdelete" or "destroybatch")
        {
            var ids = result.Result switch
            {
                ObjectResult { Value: IEntityMutationResult mutationResult } => mutationResult.EntityIds,
                IEntityMutationResult mutationResult => mutationResult.EntityIds,
                _ => ExtractBulkIds(context),
            };
            foreach (var id in ids.Where(id => id > 0).Distinct())
                eventBus.Publish(new EntityEvent(eventType.Value, entityType, id));
            return;
        }

        eventBus.Publish(new EntityEvent(eventType.Value, entityType, entityId));
    }

    private static bool IsSuccessful(IActionResult? result) => result switch
    {
        IStatusCodeActionResult { StatusCode: int statusCode } => statusCode is >= 200 and < 300,
        _ => true,
    };

    private static IReadOnlyList<int> ExtractBulkIds(ActionExecutingContext context)
    {
        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument == null) continue;

            var idsProperty = argument.GetType().GetProperty("Ids");
            if (idsProperty?.GetValue(argument) is IEnumerable<int> ids)
                return ids.ToList();
        }

        return [];
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
            "bulkdelete" or "destroybatch" => (GetEventType(entityType, "deleted"), 0),
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

    private static EventType? GetEventType(string entityType, string operation) =>
        (entityType, operation) switch
        {
            ("video", "created") => EventType.VideoCreated,
            ("video", "updated") => EventType.VideoUpdated,
            ("video", "deleted") => EventType.VideoDeleted,
            ("performer", "created") => EventType.PerformerCreated,
            ("performer", "updated") => EventType.PerformerUpdated,
            ("performer", "deleted") => EventType.PerformerDeleted,
            ("studio", "created") => EventType.StudioCreated,
            ("studio", "updated") => EventType.StudioUpdated,
            ("studio", "deleted") => EventType.StudioDeleted,
            ("tag", "created") => EventType.TagCreated,
            ("tag", "updated") => EventType.TagUpdated,
            ("tag", "deleted") => EventType.TagDeleted,
            ("gallery", "created") => EventType.GalleryCreated,
            ("gallery", "updated") => EventType.GalleryUpdated,
            ("gallery", "deleted") => EventType.GalleryDeleted,
            ("image", "created") => EventType.ImageCreated,
            ("image", "updated") => EventType.ImageUpdated,
            ("image", "deleted") => EventType.ImageDeleted,
            ("group", "created") => EventType.GroupCreated,
            ("group", "updated") => EventType.GroupUpdated,
            ("group", "deleted") => EventType.GroupDeleted,
            ("audio", "created") => EventType.AudioCreated,
            ("audio", "updated") => EventType.AudioUpdated,
            ("audio", "deleted") => EventType.AudioDeleted,
            ("text", "created") => EventType.TextCreated,
            ("text", "updated") => EventType.TextUpdated,
            ("text", "deleted") => EventType.TextDeleted,
            _ => null,
        };
}
