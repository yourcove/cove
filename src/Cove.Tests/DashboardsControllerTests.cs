using System.Text.Json;
using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Common;
using Cove.Core.Entities.Auth;
using Cove.Data;
using Cove.Plugins;
using Cove.Tests.Postgres;
using Microsoft.AspNetCore.Mvc;

namespace Cove.Tests;

public sealed class DashboardsControllerTests
{
    [Fact]
    [RequiresPostgres]
    public async Task Bootstrap_is_idempotent_and_preserves_widget_configuration()
    {
        await using var scope = await CreateScopeAsync();
        var controller = scope.ControllerFor(7);
        var widget = Widget("legacy-row", "cove.core", "collection", "Legacy row", new { mode = "videos", sortBy = "date" });

        var first = await controller.Bootstrap(new DashboardBootstrapRequest([widget]), TestContext.Current.CancellationToken);
        var created = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(first.Result).Value);
        var second = await controller.Bootstrap(new DashboardBootstrapRequest([]), TestContext.Current.CancellationToken);
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
    [RequiresPostgres]
    public async Task Update_requires_the_current_version_and_preserves_the_draft_on_conflict()
    {
        await using var scope = await CreateScopeAsync();
        await SeedContinueWatchingGroupAsync(scope);
        var controller = scope.ControllerFor(7);
        var dashboard = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await controller.Bootstrap(new DashboardBootstrapRequest([]), TestContext.Current.CancellationToken)).Result).Value);

        var updated = await controller.Update(
            dashboard.Id,
            new DashboardUpdateRequest("Renamed", dashboard.Version, [Widget("one", "cove.core", "continue-watching", "Continue Watching", new { })]),
            TestContext.Current.CancellationToken);
        var updatedDto = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(updated.Result).Value);
        Assert.Equal(dashboard.Version + 1, updatedDto.Version);

        var stale = await controller.Update(
            dashboard.Id,
            new DashboardUpdateRequest("Stale", dashboard.Version, []),
            TestContext.Current.CancellationToken);
        var conflict = Assert.IsType<ConflictObjectResult>(stale.Result);
        var versionConflict = Assert.IsType<DashboardVersionConflictDto>(conflict.Value);
        Assert.Equal("DASHBOARD_VERSION_CONFLICT", versionConflict.Code);
        var current = versionConflict.Current;
        Assert.Equal("Renamed", current.Name);
        Assert.Single(current.Widgets);
    }

    [Fact]
    [RequiresPostgres]
    public async Task Bootstrap_preserves_an_explicitly_empty_legacy_layout_and_defaults_only_when_omitted()
    {
        await using var emptyScope = await CreateScopeAsync();
        var empty = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await emptyScope.ControllerFor(7).Bootstrap(new DashboardBootstrapRequest([]), TestContext.Current.CancellationToken)).Result).Value);
        Assert.Empty(empty.Widgets);

        await using var defaultScope = await CreateScopeAsync();
        var group = await SeedContinueWatchingGroupAsync(defaultScope);
        var defaults = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await defaultScope.ControllerFor(7).Bootstrap(new DashboardBootstrapRequest(null), TestContext.Current.CancellationToken)).Result).Value);
        Assert.Equal(6, defaults.Widgets.Count);
        var continueWatching = defaults.Widgets[0];
        Assert.Equal("collection", continueWatching.WidgetKey);
        Assert.Equal("Continue Watching", continueWatching.Label);
        Assert.Equal("group", continueWatching.Configuration.GetProperty("source").GetString());
        Assert.Equal(group.Id, continueWatching.Configuration.GetProperty("groupId").GetInt32());
    }

    [Fact]
    [RequiresPostgres]
    public async Task Bootstrap_defaults_omit_Continue_Watching_when_the_built_in_group_is_missing()
    {
        // A database whose migrations are still pending has no built-in groups to reference.
        await using var scope = await CreateScopeAsync();
        var defaults = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await scope.ControllerFor(7).Bootstrap(new DashboardBootstrapRequest(null), TestContext.Current.CancellationToken)).Result).Value);

        Assert.Equal(5, defaults.Widgets.Count);
        Assert.DoesNotContain(defaults.Widgets, widget => widget.Label == "Continue Watching");
    }

    [Fact]
    [RequiresPostgres]
    public async Task Continue_Watching_is_left_out_for_a_principal_that_cannot_read_groups()
    {
        await using var scope = await CreateScopeAsync();
        await SeedContinueWatchingGroupAsync(scope);
        var restricted = scope.ControllerFor(7, Permissions.VideosRead);

        var defaults = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await restricted.Bootstrap(new DashboardBootstrapRequest(null), TestContext.Current.CancellationToken)).Result).Value);
        Assert.Equal(5, defaults.Widgets.Count);
        Assert.DoesNotContain(defaults.Widgets, widget => widget.Label == "Continue Watching");
    }

    [Fact]
    [RequiresPostgres]
    public async Task Dashboards_saved_with_the_legacy_Continue_Watching_widget_are_read_as_the_group_widget()
    {
        await using var scope = await CreateScopeAsync();
        var group = await SeedContinueWatchingGroupAsync(scope);
        var controller = scope.ControllerFor(7);
        var legacy = Widget("continue", "cove.core", "continue-watching", "Continue Watching", new { });
        var created = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await controller.Bootstrap(new DashboardBootstrapRequest([legacy]), TestContext.Current.CancellationToken)).Result).Value);

        var converted = Assert.Single(created.Widgets);
        Assert.Equal("continue", converted.InstanceId);
        Assert.Equal("collection", converted.WidgetKey);
        Assert.Equal("Continue Watching", converted.Label);
        Assert.Equal("group", converted.Configuration.GetProperty("source").GetString());
        Assert.Equal(group.Id, converted.Configuration.GetProperty("groupId").GetInt32());

        // The stored layout is untouched until the user saves; reads keep converting it.
        var reread = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await controller.GetById(created.Id, TestContext.Current.CancellationToken)).Result).Value);
        Assert.Equal("collection", Assert.Single(reread.Widgets).WidgetKey);
    }

    [Fact]
    [RequiresPostgres]
    public async Task A_legacy_Continue_Watching_widget_is_dropped_when_the_built_in_group_is_missing()
    {
        await using var scope = await CreateScopeAsync();
        var controller = scope.ControllerFor(7);
        var created = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await controller.Bootstrap(
                new DashboardBootstrapRequest([
                    Widget("continue", "cove.core", "continue-watching", "Continue Watching", new { }),
                    Widget("videos", "cove.core", "collection", "Recent videos", new { source = "premade", mode = "videos" }),
                ]),
                TestContext.Current.CancellationToken)).Result).Value);

        Assert.Equal("videos", Assert.Single(created.Widgets).InstanceId);
    }

    [Fact]
    [RequiresPostgres]
    public async Task Dashboards_are_scoped_to_the_current_user_and_the_last_cannot_be_deleted()
    {
        await using var scope = await CreateScopeAsync();
        var owner = scope.ControllerFor(7);
        var dashboard = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await owner.Bootstrap(new DashboardBootstrapRequest([]), TestContext.Current.CancellationToken)).Result).Value);

        var otherUser = scope.ControllerFor(8);
        Assert.IsType<NotFoundResult>((await otherUser.GetById(dashboard.Id, TestContext.Current.CancellationToken)).Result);
        Assert.IsType<NotFoundResult>(await otherUser.Delete(dashboard.Id, TestContext.Current.CancellationToken));
        Assert.IsType<ConflictObjectResult>(await owner.Delete(dashboard.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    [RequiresPostgres]
    public async Task Duplicate_and_delete_default_assign_a_deterministic_fallback()
    {
        await using var scope = await CreateScopeAsync();
        var controller = scope.ControllerFor(7);
        var first = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await controller.Bootstrap(new DashboardBootstrapRequest([]), TestContext.Current.CancellationToken)).Result).Value);
        var duplicateResult = await controller.Duplicate(first.Id, new DashboardDuplicateRequest("Discovery"), TestContext.Current.CancellationToken);
        var duplicate = Assert.IsType<DashboardDto>(Assert.IsType<CreatedAtActionResult>(duplicateResult.Result).Value);

        Assert.False(duplicate.IsDefault);
        Assert.IsType<NoContentResult>(await controller.Delete(first.Id, TestContext.Current.CancellationToken));

        var remaining = Assert.IsAssignableFrom<IReadOnlyList<DashboardSummaryDto>>(
            Assert.IsType<OkObjectResult>((await controller.List(TestContext.Current.CancellationToken)).Result).Value);
        Assert.True(Assert.Single(remaining).IsDefault);
    }

    [Fact]
    [RequiresPostgres]
    public async Task Update_requires_canvas_widgets_to_be_the_only_dashboard_widget()
    {
        await using var scope = await CreateScopeAsync();
        var controller = scope.ControllerFor(7);
        var dashboard = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(
            (await controller.Bootstrap(new DashboardBootstrapRequest([]), TestContext.Current.CancellationToken)).Result).Value);
        var canvas = Widget("canvas", "example.extension", "feed", "Feed", new { }, DashboardWidgetPresentation.Canvas);
        var flow = Widget("flow", "cove.core", "collection", "Collection", new { });

        var mixed = await controller.Update(
            dashboard.Id,
            new DashboardUpdateRequest("Home", dashboard.Version, [canvas, flow]),
            TestContext.Current.CancellationToken);
        var badRequest = Assert.IsType<BadRequestObjectResult>(mixed.Result);
        Assert.Contains("Canvas", JsonSerializer.Serialize(badRequest.Value));

        var canvasOnly = await controller.Update(
            dashboard.Id,
            new DashboardUpdateRequest("Home", dashboard.Version, [canvas]),
            TestContext.Current.CancellationToken);
        var saved = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(canvasOnly.Result).Value);
        Assert.Equal(DashboardWidgetPresentation.Canvas, Assert.Single(saved.Widgets).Presentation);
    }

    [Fact]
    [RequiresPostgres]
    public async Task Update_validates_widget_layout_and_round_trips_it()
    {
        await using var scope = await CreateScopeAsync();
        var controller = scope.ControllerFor(7);
        var dashboard = await BootstrapAsync(controller, []);
        DashboardWidgetDto WithLayout(DashboardWidgetLayoutDto layout)
            => Widget("row", "cove.core", "collection", "Collection", new { }) with { Layout = layout };

        foreach (var invalid in new[] { new DashboardWidgetLayoutDto(Width: 6), new(Rows: 0), new(Rows: 21), new(CardSize: -1), new(CardSize: 9), new(Pages: 0), new(Pages: 21) })
        {
            var rejected = await controller.Update(
                dashboard.Id,
                new DashboardUpdateRequest("Home", dashboard.Version, [WithLayout(invalid)]),
                TestContext.Current.CancellationToken);
            Assert.IsType<BadRequestObjectResult>(rejected.Result);
        }

        var accepted = await controller.Update(
            dashboard.Id,
            new DashboardUpdateRequest("Home", dashboard.Version, [WithLayout(new DashboardWidgetLayoutDto(Width: 3, Rows: 4, CardSize: 2, Pages: 6))]),
            TestContext.Current.CancellationToken);
        var saved = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(accepted.Result).Value);
        var layout = Assert.Single(saved.Widgets).Layout;
        Assert.NotNull(layout);
        Assert.Equal((3, 4, 2, 6), (layout.Width, layout.Rows, layout.CardSize, layout.Pages));

        var reloaded = await ViewAsync(controller, saved.Id);
        Assert.Equal(layout, Assert.Single(reloaded.Dashboard!.Widgets).Layout);
    }

    [Fact]
    public void Legacy_widget_json_without_presentation_defaults_to_flow()
    {
        const string json = """
            [{"instanceId":"legacy","owner":"cove.core","widgetKey":"collection","label":"Legacy","configuration":{}}]
            """;

        var widget = Assert.Single(JsonSerializer.Deserialize<List<DashboardWidgetDto>>(json, CoveJson.Default)!);

        Assert.Equal(DashboardWidgetPresentation.Flow, widget.Presentation);
        Assert.Null(widget.Layout);
    }

    [Fact]
    [RequiresPostgres]
    public async Task Legacy_database_json_without_presentation_can_be_serialized_in_the_response()
    {
        await using var scope = await CreateScopeAsync();
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
        await scope.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var response = await scope.ControllerFor(7).Bootstrap(new DashboardBootstrapRequest(null), TestContext.Current.CancellationToken);
        var dashboard = Assert.IsType<DashboardDto>(Assert.IsType<OkObjectResult>(response.Result).Value);

        var json = JsonSerializer.Serialize(dashboard, CoveJson.Default);
        Assert.Contains("\"configuration\":{", json);
        Assert.Equal(DashboardWidgetPresentation.Flow, Assert.Single(dashboard.Widgets).Presentation);
    }

    [Fact]
    [RequiresPostgres]
    public async Task View_returns_the_default_dashboard_with_its_list_and_the_saved_filters_its_rows_read()
    {
        await using var scope = await CreateScopeAsync();
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
    [RequiresPostgres]
    public async Task View_shows_a_requested_dashboard_and_falls_back_to_the_default_for_one_that_is_not_the_callers()
    {
        await using var scope = await CreateScopeAsync();
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
    [RequiresPostgres]
    public async Task View_leaves_saved_filters_out_for_a_principal_that_cannot_read_them()
    {
        await using var scope = await CreateScopeAsync();
        var own = await SeedSavedFilterAsync(scope, "Mine", userId: 7);
        await BootstrapAsync(scope.ControllerFor(7), [SavedFilterWidget("own", own.Id)]);

        var view = await ViewAsync(scope.ControllerFor(7, Permissions.VideosRead), id: null);

        Assert.Single(view.Dashboard!.Widgets);
        Assert.Empty(view.SavedFilters);
    }

    [Fact]
    [RequiresPostgres]
    public async Task View_reports_no_dashboard_before_the_first_one_is_bootstrapped()
    {
        await using var scope = await CreateScopeAsync();

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

    private static async Task<TestScope> CreateScopeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await CovePostgresDatabase.CreateAsync(ct);
        try
        {
            // Dashboards and saved filters belong to a user, so the users the tests sign in as have to exist.
            await using (var seed = database.CreateContext())
            {
                seed.Users.AddRange(
                    new User { Id = 7, Username = "user-7", PasswordHash = "test" },
                    new User { Id = 8, Username = "user-8", PasswordHash = "test" });
                await seed.SaveChangesAsync(ct);
            }
            await database.SyncIdentitySequencesAsync(ct);
            return new TestScope(database, database.CreateContext());
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private sealed class TestScope(CovePostgresDatabase database, CoveContext context) : IAsyncDisposable
    {
        public CoveContext Context { get; } = context;

        public DashboardsController ControllerFor(int? userId, params string[] permissions)
            => new(Context, new TestPrincipalAccessor(userId, permissions.Length == 0 ? ["*"] : permissions));

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await database.DisposeAsync();
        }
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
