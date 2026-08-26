using System.Text.Json;
using Cove.Api.Controllers;
using Cove.Core.Auth;
using Cove.Core.Common;
using Cove.Core.Entities.Auth;
using Cove.Data;
using Cove.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class DashboardsControllerTests
{
    [Fact]
    public async Task Bootstrap_is_idempotent_and_preserves_widget_configuration()
    {
        await using var scope = CreateScope();
        var controller = scope.ControllerFor(7);
        var widget = Widget("legacy-row", "cove.core", "collection", "Legacy row", new { mode = "videos", sortBy = "date" });

        var first = await controller.Bootstrap(new DashboardBootstrapRequest([widget]), default);
        var created = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(first.Result).Value);
        var second = await controller.Bootstrap(new DashboardBootstrapRequest([]), default);
        var returned = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(second.Result).Value);

        Assert.Equal(created.Id, returned.Id);
        Assert.Equal("Home", created.Name);
        Assert.True(created.IsDefault);
        var savedWidget = Assert.Single(created.Widgets);
        Assert.Equal("legacy-row", savedWidget.InstanceId);
        Assert.Equal("videos", savedWidget.Configuration.GetProperty("mode").GetString());
        Assert.Single(scope.Context.Dashboards);
    }

    [Fact]
    public async Task Update_requires_the_current_version_and_preserves_the_draft_on_conflict()
    {
        await using var scope = CreateScope();
        var controller = scope.ControllerFor(7);
        var dashboard = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await controller.Bootstrap(new DashboardBootstrapRequest([]), default)).Result).Value);

        var updated = await controller.Update(
            dashboard.Id,
            new DashboardUpdateRequest("Renamed", dashboard.Version, [Widget("one", "cove.core", "continue-watching", "Continue Watching", new { })]),
            default);
        var updatedDto = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(updated.Result).Value);
        Assert.Equal(dashboard.Version + 1, updatedDto.Version);

        var stale = await controller.Update(
            dashboard.Id,
            new DashboardUpdateRequest("Stale", dashboard.Version, []),
            default);
        var conflict = Assert.IsType<ConflictObjectResult>(stale.Result);
        var versionConflict = Assert.IsType<DashboardVersionConflictDto>(conflict.Value);
        Assert.Equal("DASHBOARD_VERSION_CONFLICT", versionConflict.Code);
        var current = versionConflict.Current;
        Assert.Equal("Renamed", current.Name);
        Assert.Single(current.Widgets);
    }

    [Fact]
    public async Task Bootstrap_preserves_an_explicitly_empty_legacy_layout_and_defaults_only_when_omitted()
    {
        await using var emptyScope = CreateScope();
        var empty = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await emptyScope.ControllerFor(7).Bootstrap(new DashboardBootstrapRequest([]), default)).Result).Value);
        Assert.Empty(empty.Widgets);

        await using var defaultScope = CreateScope();
        var defaults = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await defaultScope.ControllerFor(7).Bootstrap(new DashboardBootstrapRequest(null), default)).Result).Value);
        Assert.Equal(6, defaults.Widgets.Count);
    }

    [Fact]
    public async Task Dashboards_are_scoped_to_the_current_user_and_the_last_cannot_be_deleted()
    {
        await using var scope = CreateScope();
        var owner = scope.ControllerFor(7);
        var dashboard = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await owner.Bootstrap(new DashboardBootstrapRequest([]), default)).Result).Value);

        var otherUser = scope.ControllerFor(8);
        Assert.IsType<NotFoundResult>((await otherUser.GetById(dashboard.Id, default)).Result);
        Assert.IsType<NotFoundResult>(await otherUser.Delete(dashboard.Id, default));
        Assert.IsType<ConflictObjectResult>(await owner.Delete(dashboard.Id, default));
    }

    [Fact]
    public async Task Duplicate_and_delete_default_assign_a_deterministic_fallback()
    {
        await using var scope = CreateScope();
        var controller = scope.ControllerFor(7);
        var first = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await controller.Bootstrap(new DashboardBootstrapRequest([]), default)).Result).Value);
        var duplicateResult = await controller.Duplicate(first.Id, new DashboardDuplicateRequest("Discovery"), default);
        var duplicate = Assert.IsType<DashboardDto>(Assert.IsType<CreatedAtActionResult>(duplicateResult.Result).Value);

        Assert.False(duplicate.IsDefault);
        Assert.IsType<NoContentResult>(await controller.Delete(first.Id, default));

        var remaining = Assert.IsAssignableFrom<IReadOnlyList<DashboardSummaryDto>>(
            Assert.IsType<OkObjectResult>((await controller.List(default)).Result).Value);
        Assert.True(Assert.Single(remaining).IsDefault);
    }

    [Fact]
    public async Task Update_requires_canvas_widgets_to_be_the_only_dashboard_widget()
    {
        await using var scope = CreateScope();
        var controller = scope.ControllerFor(7);
        var dashboard = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await controller.Bootstrap(new DashboardBootstrapRequest([]), default)).Result).Value);
        var canvas = Widget("canvas", "example.extension", "feed", "Feed", new { }, DashboardWidgetPresentation.Canvas);
        var flow = Widget("flow", "cove.core", "collection", "Collection", new { });

        var mixed = await controller.Update(
            dashboard.Id,
            new DashboardUpdateRequest("Home", dashboard.Version, [canvas, flow]),
            default);
        var badRequest = Assert.IsType<BadRequestObjectResult>(mixed.Result);
        Assert.Contains("Canvas", JsonSerializer.Serialize(badRequest.Value));

        var canvasOnly = await controller.Update(
            dashboard.Id,
            new DashboardUpdateRequest("Home", dashboard.Version, [canvas]),
            default);
        var saved = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(canvasOnly.Result).Value);
        Assert.Equal(DashboardWidgetPresentation.Canvas, Assert.Single(saved.Widgets).Presentation);
    }

    [Fact]
    public void Legacy_widget_json_without_presentation_defaults_to_flow()
    {
        const string json = """
            [{"instanceId":"legacy","owner":"cove.core","widgetKey":"collection","label":"Legacy","configuration":{}}]
            """;

        var widget = Assert.Single(JsonSerializer.Deserialize<List<DashboardWidgetDto>>(json, CoveJson.Default)!);

        Assert.Equal(DashboardWidgetPresentation.Flow, widget.Presentation);
    }

    [Fact]
    public async Task Legacy_database_json_without_presentation_can_be_serialized_in_the_response()
    {
        await using var scope = CreateScope();
        scope.Context.Dashboards.Add(new Dashboard
        {
            UserId = 7,
            Name = "Home",
            NormalizedName = "HOME",
            IsDefault = true,
            Version = 1,
            WidgetsJson = JsonDocument.Parse("""
                [{"instanceId":"legacy","owner":"cove.core","widgetKey":"collection","label":"Legacy","configuration":{"source":"saved","savedFilterId":1}}]
                """),
        });
        await scope.Context.SaveChangesAsync();

        var response = await scope.ControllerFor(7).Bootstrap(new DashboardBootstrapRequest(null), default);
        var dashboard = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(response.Result).Value);

        var json = JsonSerializer.Serialize(dashboard, CoveJson.Default);
        Assert.Contains("\"configuration\":{", json);
        Assert.Equal(DashboardWidgetPresentation.Flow, Assert.Single(dashboard.Widgets).Presentation);
    }

    private static DashboardWidgetDto Widget(
        string instanceId,
        string owner,
        string widgetKey,
        string label,
        object configuration,
        DashboardWidgetPresentation presentation = DashboardWidgetPresentation.Flow)
        => new(instanceId, owner, widgetKey, label, JsonSerializer.SerializeToElement(configuration), presentation);

    private static TestScope CreateScope()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"dashboards-{Guid.NewGuid():N}")
            .Options;
        return new TestScope(new CoveContext(options));
    }

    private sealed class TestScope(CoveContext context) : IAsyncDisposable
    {
        public CoveContext Context { get; } = context;

        public DashboardsController ControllerFor(int? userId)
            => new(Context, new TestPrincipalAccessor(userId));

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed class TestPrincipalAccessor(int? userId) : ICurrentPrincipalAccessor
    {
        public CovePrincipal? Current { get; private set; } = new()
        {
            UserId = userId,
            Username = userId?.ToString() ?? "anonymous",
            Kind = userId is null ? PrincipalKind.Anonymous : PrincipalKind.User,
            Roles = new HashSet<string>(),
            Permissions = new HashSet<string> { "*" },
        };

        public void Set(CovePrincipal? principal) => Current = principal;
    }
}
