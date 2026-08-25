using System.Net;
using System.Text.Json;
using Cove.Api.Controllers;
using Cove.ApiTests.Infrastructure;
using Cove.Plugins;

namespace Cove.ApiTests.Tests.Dashboards;

public sealed class DashboardLifecycleApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/dashboards/bootstrap")]
    [CoversEndpoint("GET", "/api/dashboards")]
    [CoversEndpoint("GET", "/api/dashboards/{id:int}")]
    [CoversEndpoint("POST", "/api/dashboards")]
    [CoversEndpoint("PUT", "/api/dashboards/{id:int}")]
    [CoversEndpoint("POST", "/api/dashboards/{id:int}/duplicate")]
    [CoversEndpoint("PUT", "/api/dashboards/{id:int}/default")]
    [CoversEndpoint("DELETE", "/api/dashboards/{id:int}")]
    public async Task GivenMember_WhenDashboardLifecycleRuns_ThenWidgetsVersionsAndDefaultsPersist()
    {
        var member = AsUser(ApiTestUsers.Eva);
        var cancellationToken = TestContext.Current.CancellationToken;
        var initialWidget = Widget(
            "initial-widget",
            "cove.core",
            "collection",
            "Initial collection",
            new { source = "premade", mode = "videos" });

        var home = await member.BootstrapDashboardAsync(new DashboardBootstrapRequest([initialWidget]), cancellationToken);

        home.Name.Should().Be("Home");
        home.IsDefault.Should().BeTrue();
        home.Version.Should().Be(1);
        home.Widgets.Should().ContainSingle();
        home.Widgets[0].Configuration.GetProperty("mode").GetString().Should().Be("videos");
        (await member.GetDashboardsAsync(cancellationToken)).Should().ContainSingle(summary => summary.Id == home.Id);
        (await member.GetDashboardAsync(home.Id, cancellationToken)).Widgets[0].InstanceId.Should().Be("initial-widget");

        var created = await member.CreateDashboardAsync(new DashboardCreateRequest("  Discovery  "), cancellationToken);
        created.Name.Should().Be("Discovery");
        created.IsDefault.Should().BeFalse();

        var canvas = Widget(
            "canvas-widget",
            "example.extension",
            "connection-browser",
            "Connection browser",
            new { maximumDegrees = 6 },
            DashboardWidgetPresentation.Canvas);
        var updated = await member.UpdateDashboardAsync(
            home.Id,
            new DashboardUpdateRequest("  Full canvas  ", home.Version, [canvas]),
            cancellationToken);

        updated.Name.Should().Be("Full canvas");
        updated.Version.Should().Be(home.Version + 1);
        updated.Widgets.Should().ContainSingle(widget => widget.Presentation == DashboardWidgetPresentation.Canvas);

        var duplicate = await member.DuplicateDashboardAsync(
            updated.Id,
            new DashboardDuplicateRequest("Canvas copy"),
            cancellationToken);
        duplicate.IsDefault.Should().BeFalse();
        duplicate.Widgets.Should().ContainSingle();
        duplicate.Widgets[0].InstanceId.Should().NotBe(updated.Widgets[0].InstanceId);
        duplicate.Widgets[0].Configuration.GetProperty("maximumDegrees").GetInt32().Should().Be(6);

        (await member.SetDefaultDashboardAsync(duplicate.Id, cancellationToken)).IsDefault.Should().BeTrue();
        var withNewDefault = await member.GetDashboardsAsync(cancellationToken);
        withNewDefault.Should().HaveCount(3);
        withNewDefault[0].Id.Should().Be(duplicate.Id);
        withNewDefault.Should().ContainSingle(summary => summary.IsDefault);

        await member.DeleteDashboardAsync(duplicate.Id, cancellationToken);

        var afterDelete = await member.GetDashboardsAsync(cancellationToken);
        afterDelete.Should().HaveCount(2);
        afterDelete.Should().NotContain(summary => summary.Id == duplicate.Id);
        afterDelete.Should().ContainSingle(summary => summary.IsDefault).Which.Id.Should().Be(home.Id);
    }

    [Fact]
    public async Task GivenSeparateUsersAndStaleVersion_WhenMutationsRun_ThenIsolationValidationAndConflictAreEnforced()
    {
        var eva = AsUser(ApiTestUsers.Eva);
        var anthony = AsUser(ApiTestUsers.Anthony);
        var cancellationToken = TestContext.Current.CancellationToken;
        var evaHome = await eva.BootstrapDashboardAsync(new DashboardBootstrapRequest([]), cancellationToken);
        var anthonyHome = await anthony.BootstrapDashboardAsync(new DashboardBootstrapRequest([]), cancellationToken);

        await anthony.AssertResponseAsync($"/api/dashboards/{evaHome.Id}", HttpStatusCode.NotFound, cancellationToken);
        await eva.AssertResponseAsync($"/api/dashboards/{anthonyHome.Id}", HttpStatusCode.NotFound, cancellationToken);
        await AsAnonymous().AssertResponseAsync(
            HttpMethod.Post,
            "/api/dashboards/bootstrap",
            HttpStatusCode.Unauthorized,
            new DashboardBootstrapRequest([]),
            cancellationToken);

        var saved = await eva.UpdateDashboardAsync(
            evaHome.Id,
            new DashboardUpdateRequest("Saved", evaHome.Version, [Widget("flow", "cove.core", "collection", "Flow", new { })]),
            cancellationToken);
        var staleConflict = await eva.UpdateDashboardExpectingConflictAsync(
            evaHome.Id,
            new DashboardUpdateRequest("Stale", evaHome.Version, []),
            cancellationToken);

        staleConflict.Code.Should().Be("DASHBOARD_VERSION_CONFLICT");
        staleConflict.Current.Name.Should().Be("Saved");
        staleConflict.Current.Version.Should().Be(saved.Version);

        var mixedPresentationStatus = await eva.TryUpdateDashboardAsync(
            evaHome.Id,
            new DashboardUpdateRequest(
                "Mixed",
                saved.Version,
                [
                    Widget("canvas", "example.extension", "canvas", "Canvas", new { }, DashboardWidgetPresentation.Canvas),
                    Widget("flow-two", "cove.core", "collection", "Flow", new { }),
                ]),
            cancellationToken);
        mixedPresentationStatus.Should().Be(HttpStatusCode.BadRequest);

        (await anthony.TryDeleteDashboardAsync(anthonyHome.Id, cancellationToken)).Should().Be(HttpStatusCode.Conflict);
        (await anthony.GetDashboardAsync(anthonyHome.Id, cancellationToken)).IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task GivenFreshMember_WhenBootstrapRequestsRace_ThenOneDefaultDashboardIsReturned()
    {
        var member = AsUser(ApiTestUsers.Eva);
        var cancellationToken = TestContext.Current.CancellationToken;

        var dashboards = await Task.WhenAll(Enumerable.Range(0, 4).Select(index =>
            member.BootstrapDashboardAsync(
                new DashboardBootstrapRequest([Widget($"widget-{index}", "cove.core", "collection", $"Widget {index}", new { index })]),
                cancellationToken)));

        var dashboardId = dashboards.Select(dashboard => dashboard.Id).Distinct().Should().ContainSingle().Subject;
        var persisted = await member.GetDashboardsAsync(cancellationToken);
        persisted.Should().ContainSingle();
        persisted[0].Id.Should().Be(dashboardId);
        persisted[0].IsDefault.Should().BeTrue();
    }

    private static DashboardWidgetDto Widget(
        string instanceId,
        string owner,
        string widgetKey,
        string label,
        object configuration,
        DashboardWidgetPresentation presentation = DashboardWidgetPresentation.Flow)
        => new(
            instanceId,
            owner,
            widgetKey,
            label,
            JsonSerializer.SerializeToElement(configuration, ApiJson.Options),
            presentation);
}
