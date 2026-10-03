using System.Text.Json;
using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Entities;
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
        await SeedContinueWatchingGroupAsync(scope);
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
        var group = await SeedContinueWatchingGroupAsync(defaultScope);
        var defaults = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await defaultScope.ControllerFor(7).Bootstrap(new DashboardBootstrapRequest(null), default)).Result).Value);
        Assert.Equal(6, defaults.Widgets.Count);
        var continueWatching = defaults.Widgets[0];
        Assert.Equal("collection", continueWatching.WidgetKey);
        Assert.Equal("Continue Watching", continueWatching.Label);
        Assert.Equal("group", continueWatching.Configuration.GetProperty("source").GetString());
        Assert.Equal(group.Id, continueWatching.Configuration.GetProperty("groupId").GetInt32());
    }

    [Fact]
    public async Task Bootstrap_defaults_omit_Continue_Watching_when_the_built_in_group_is_missing()
    {
        // A database whose migrations are still pending has no built-in groups to reference.
        await using var scope = CreateScope();
        var defaults = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await scope.ControllerFor(7).Bootstrap(new DashboardBootstrapRequest(null), default)).Result).Value);

        Assert.Equal(5, defaults.Widgets.Count);
        Assert.DoesNotContain(defaults.Widgets, widget => widget.Label == "Continue Watching");
    }

    [Fact]
    public async Task Continue_Watching_is_left_out_for_a_principal_that_cannot_read_groups()
    {
        await using var scope = CreateScope();
        await SeedContinueWatchingGroupAsync(scope);
        var restricted = scope.ControllerFor(7, Permissions.VideosRead);

        var defaults = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await restricted.Bootstrap(new DashboardBootstrapRequest(null), default)).Result).Value);
        Assert.Equal(5, defaults.Widgets.Count);
        Assert.DoesNotContain(defaults.Widgets, widget => widget.Label == "Continue Watching");
    }

    [Fact]
    public async Task Dashboards_saved_with_the_legacy_Continue_Watching_widget_are_read_as_the_group_widget()
    {
        await using var scope = CreateScope();
        var group = await SeedContinueWatchingGroupAsync(scope);
        var controller = scope.ControllerFor(7);
        var legacy = Widget("continue", "cove.core", "continue-watching", "Continue Watching", new { });
        var created = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await controller.Bootstrap(new DashboardBootstrapRequest([legacy]), default)).Result).Value);

        var converted = Assert.Single(created.Widgets);
        Assert.Equal("continue", converted.InstanceId);
        Assert.Equal("collection", converted.WidgetKey);
        Assert.Equal("Continue Watching", converted.Label);
        Assert.Equal("group", converted.Configuration.GetProperty("source").GetString());
        Assert.Equal(group.Id, converted.Configuration.GetProperty("groupId").GetInt32());

        // The stored layout is untouched until the user saves; reads keep converting it.
        var reread = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await controller.GetById(created.Id, default)).Result).Value);
        Assert.Equal("collection", Assert.Single(reread.Widgets).WidgetKey);
    }

    [Fact]
    public async Task A_legacy_Continue_Watching_widget_is_dropped_when_the_built_in_group_is_missing()
    {
        await using var scope = CreateScope();
        var controller = scope.ControllerFor(7);
        var created = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await controller.Bootstrap(
                new DashboardBootstrapRequest([
                    Widget("continue", "cove.core", "continue-watching", "Continue Watching", new { }),
                    Widget("videos", "cove.core", "collection", "Recent videos", new { source = "premade", mode = "videos" }),
                ]),
                default)).Result).Value);

        Assert.Equal("videos", Assert.Single(created.Widgets).InstanceId);
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

    [Fact]
    public async Task View_returns_the_default_dashboard_with_its_list_and_the_saved_filters_its_rows_read()
    {
        await using var scope = CreateScope();
        var own = await SeedSavedFilterAsync(scope, "Mine", userId: 7);
        var other = await SeedSavedFilterAsync(scope, "Someone else's", userId: 8);
        var unrelated = await SeedSavedFilterAsync(scope, "Not on the dashboard", userId: 7);
        var controller = scope.ControllerFor(7);
        var home = await BootstrapAsync(controller,
        [
            SavedFilterWidget("own", own.Id),
            SavedFilterWidget("other", other.Id),
            SavedFilterWidget("deleted", 9999),
            SavedFilterWidget("own-again", own.Id),
            Widget("premade", "cove.core", "collection", "Videos", new { source = "premade", mode = "videos", sortBy = "date", direction = "desc" }),
            Widget("extension", "ext.example", "collection", "Extension", new { source = "saved", savedFilterId = unrelated.Id }),
        ]);
        var second = Assert.IsType<DashboardDto>(Assert.IsType<CreatedAtActionResult>(
            (await controller.Create(new DashboardCreateRequest("Second"), TestContext.Current.CancellationToken)).Result).Value);

        var view = await ViewAsync(controller, id: null);

        Assert.Equal([home.Id, second.Id], view.Dashboards.Select(item => item.Id));
        Assert.Equal(home.Id, view.Dashboard?.Id);
        Assert.Equal(6, view.Dashboard!.Widgets.Count);
        Assert.True(view.RequestedFound);
        var filter = Assert.Single(view.SavedFilters);
        Assert.Equal(new SavedFilterDto(own.Id, "videos", "Mine", "{}", "{}", "{}"), filter);
    }

    [Fact]
    public async Task View_shows_a_requested_dashboard_and_falls_back_to_the_default_for_one_that_is_not_the_callers()
    {
        await using var scope = CreateScope();
        var controller = scope.ControllerFor(7);
        var home = await BootstrapAsync(controller, []);
        var second = Assert.IsType<DashboardDto>(Assert.IsType<CreatedAtActionResult>(
            (await controller.Create(new DashboardCreateRequest("Second"), TestContext.Current.CancellationToken)).Result).Value);
        var foreign = await BootstrapAsync(scope.ControllerFor(8), []);

        var requested = await ViewAsync(controller, second.Id);
        var missing = await ViewAsync(controller, foreign.Id);

        Assert.Equal(second.Id, requested.Dashboard?.Id);
        Assert.True(requested.RequestedFound);
        Assert.Equal(home.Id, missing.Dashboard?.Id);
        Assert.False(missing.RequestedFound);
        Assert.Equal(2, missing.Dashboards.Count);
    }

    [Fact]
    public async Task View_leaves_saved_filters_out_for_a_principal_that_cannot_read_them()
    {
        await using var scope = CreateScope();
        var own = await SeedSavedFilterAsync(scope, "Mine", userId: 7);
        await BootstrapAsync(scope.ControllerFor(7), [SavedFilterWidget("own", own.Id)]);

        var view = await ViewAsync(scope.ControllerFor(7, Permissions.VideosRead), id: null);

        Assert.Single(view.Dashboard!.Widgets);
        Assert.Empty(view.SavedFilters);
    }

    [Fact]
    public async Task View_reports_no_dashboard_before_the_first_one_is_bootstrapped()
    {
        await using var scope = CreateScope();

        var view = await ViewAsync(scope.ControllerFor(7), id: null);

        Assert.Empty(view.Dashboards);
        Assert.Null(view.Dashboard);
        Assert.False(view.RequestedFound);
        Assert.Empty(view.SavedFilters);
        Assert.IsType<UnauthorizedObjectResult>((await scope.ControllerFor(null).View(null, TestContext.Current.CancellationToken)).Result);
    }

    private static async Task<DashboardDto> BootstrapAsync(DashboardsController controller, IReadOnlyList<DashboardWidgetDto> widgets)
        => Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await controller.Bootstrap(new DashboardBootstrapRequest(widgets), TestContext.Current.CancellationToken)).Result).Value);

    private static async Task<DashboardViewDto> ViewAsync(DashboardsController controller, int? id)
        => Assert.IsType<DashboardViewDto>(Assert.IsType<OkObjectResult>((await controller.View(id, TestContext.Current.CancellationToken)).Result).Value);

    private static DashboardWidgetDto SavedFilterWidget(string instanceId, int savedFilterId)
        => Widget(instanceId, "cove.core", "collection", "Saved filter", new { source = "saved", savedFilterId });

    private static async Task<SavedFilter> SeedSavedFilterAsync(TestScope scope, string name, int userId)
    {
        var filter = new SavedFilter { Name = name, Mode = "videos", FindFilter = "{}", ObjectFilter = "{}", UIOptions = "{}", UserId = userId };
        scope.Context.SavedFilters.Add(filter);
        await scope.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return filter;
    }

    private static DashboardWidgetDto Widget(
        string instanceId,
        string owner,
        string widgetKey,
        string label,
        object configuration,
        DashboardWidgetPresentation presentation = DashboardWidgetPresentation.Flow)
        => new(instanceId, owner, widgetKey, label, JsonSerializer.SerializeToElement(configuration), presentation);

    private static async Task<Group> SeedContinueWatchingGroupAsync(TestScope scope)
    {
        var group = new Group
        {
            Name = "Continue Watching",
            Kind = GroupKind.Dynamic,
            QuerySourceKey = DynamicGroupResolver.ContinueWatchingSourceKey,
        };
        scope.Context.Groups.Add(group);
        await scope.Context.SaveChangesAsync();
        return group;
    }

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

        public DashboardsController ControllerFor(int? userId, params string[] permissions)
            => new(Context, new TestPrincipalAccessor(userId, permissions.Length == 0 ? ["*"] : permissions));

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed class TestPrincipalAccessor(int? userId, string[] permissions) : ICurrentPrincipalAccessor
    {
        public CovePrincipal? Current { get; private set; } = new()
        {
            UserId = userId,
            Username = userId?.ToString() ?? "anonymous",
            Kind = userId is null ? PrincipalKind.Anonymous : PrincipalKind.User,
            Roles = new HashSet<string>(),
            Permissions = new HashSet<string>(permissions),
        };

        public void Set(CovePrincipal? principal) => Current = principal;
    }
}
