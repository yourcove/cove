using Cove.Api.Middleware;
using Cove.Api.Http;
using Cove.Core.DTOs;
using Cove.Core.Events;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Tests;

public class EntityEventFilterTests
{
    [Fact]
    public async Task SuccessfulAudioUpdatePublishesAudioUpdated()
    {
        var published = await ExecuteAsync(
            controller: "Audios",
            action: "Update",
            routeId: 17,
            result: new OkResult());

        var evt = Assert.Single(published);
        Assert.Equal(EventType.AudioUpdated, evt.Type);
        Assert.Equal("audio", evt.EntityType);
        Assert.Equal(17, evt.EntityId);
    }

    [Fact]
    public async Task NotFoundUpdatePublishesNothing()
    {
        var published = await ExecuteAsync(
            controller: "Videos",
            action: "Update",
            routeId: 404,
            result: new NotFoundResult());

        Assert.Empty(published);
    }

    [Fact]
    public async Task BulkUpdatePublishesOnlyResultEntityIdsInsteadOfZero()
    {
        var published = await ExecuteAsync(
            controller: "Videos",
            action: "BulkUpdate",
            result: new OkObjectResult(new BulkUpdateResult([4])),
            actionArguments: new Dictionary<string, object?>
            {
                ["dto"] = new BulkVideoUpdateDto { Ids = [4, 4, 999] },
            });

        Assert.Equal([4], published.Select(evt => evt.EntityId).ToArray());
        Assert.All(published, evt => Assert.Equal(EventType.VideoUpdated, evt.Type));
    }

    [Fact]
    public async Task BulkDeletePublishesOnlyDeletedEntityIds()
    {
        var published = await ExecuteAsync(
            controller: "Tags",
            action: "BulkDelete",
            result: new OkObjectResult(new BulkDeleteResult([8, 12])),
            actionArguments: new Dictionary<string, object?>
            {
                ["dto"] = new BatchDeleteDto([8, 12, 999]),
            });

        Assert.Equal([8, 12], published.Select(evt => evt.EntityId).Order().ToArray());
        Assert.All(published, evt => Assert.Equal(EventType.TagDeleted, evt.Type));
    }

    [Fact]
    public async Task NoContentBulkDeletePublishesOnlyResultEntityIds()
    {
        var published = await ExecuteAsync(
            controller: "Audios",
            action: "BulkDelete",
            result: new EntityMutationNoContentResult([8]),
            actionArguments: new Dictionary<string, object?>
            {
                ["dto"] = new BatchDeleteDto([8, 999]),
            });

        var evt = Assert.Single(published);
        Assert.Equal(EventType.AudioDeleted, evt.Type);
        Assert.Equal(8, evt.EntityId);
    }

    private static async Task<IReadOnlyList<EntityEvent>> ExecuteAsync(
        string controller,
        string action,
        IActionResult result,
        int? routeId = null,
        Dictionary<string, object?>? actionArguments = null)
    {
        var eventBus = new EventBus();
        var published = new List<EntityEvent>();
        using var subscription = eventBus.Subscribe<EntityEvent>(published.Add);
        using var services = new ServiceCollection()
            .AddSingleton<IEventBus>(eventBus)
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services };
        var routeData = new RouteData();
        routeData.Values["controller"] = controller;
        routeData.Values["action"] = action;
        if (routeId.HasValue)
            routeData.Values["id"] = routeId.Value;

        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());
        var filters = new List<IFilterMetadata>();
        var executing = new ActionExecutingContext(
            actionContext,
            filters,
            actionArguments ?? new Dictionary<string, object?>(),
            new object());
        var executed = new ActionExecutedContext(actionContext, filters, new object())
        {
            Result = result,
        };

        await new EntityEventFilter().OnActionExecutionAsync(executing, () => Task.FromResult(executed));
        return published;
    }
}
