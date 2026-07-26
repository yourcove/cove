using System.Text.Json;
using System.Net.Http.Json;
using Cove.Api.Middleware;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IAllowAnonymous = Microsoft.AspNetCore.Authorization.IAllowAnonymous;

namespace Cove.Tests;

public sealed class ExtensionEndpointAuthorizationConventionTests
{
    [Fact]
    public void Conventions_publish_permission_and_route_entity_metadata()
    {
        var conventions = new TestConventionBuilder();

        conventions
            .RequireCovePermission(PermissionMode.Any, Permissions.TagsWrite, Permissions.TagsDelete)
            .RequireCoveEntityAccess(EntityKinds.Tag, "tagId", Permissions.TagsWrite);

        var endpoint = conventions.Build();
        var permission = Assert.Single(endpoint.Metadata.OfType<CovePermissionRequirementMetadata>());
        Assert.Equal(PermissionMode.Any, permission.Mode);
        Assert.Equal([Permissions.TagsWrite, Permissions.TagsDelete], permission.Permissions);

        var entity = Assert.Single(endpoint.Metadata.OfType<CoveRouteEntityAccessRequirementMetadata>());
        Assert.Equal(EntityKinds.Tag, entity.EntityKind);
        Assert.Equal("tagId", entity.RouteValueName);
        Assert.Equal(Permissions.TagsWrite, entity.Permission);
    }

    [Fact]
    public void Entity_convention_can_infer_a_single_declared_permission()
    {
        var conventions = new TestConventionBuilder();

        conventions
            .RequireCovePermission(Permissions.TagsRead)
            .RequireCoveEntityAccess(EntityKinds.Tag, "tagId");

        var entity = Assert.Single(conventions.Build().Metadata.OfType<CoveRouteEntityAccessRequirementMetadata>());
        Assert.Null(entity.Permission);
    }

    [Fact]
    public void Conventions_reject_empty_or_invalid_contracts()
    {
        var conventions = new TestConventionBuilder();

        Assert.Throws<ArgumentException>(() => conventions.RequireCovePermission([]));
        Assert.Throws<ArgumentException>(() => conventions.RequireCovePermission(" "));
        Assert.Throws<ArgumentOutOfRangeException>(() => conventions.RequireCovePermission((PermissionMode)99, Permissions.TagsRead));
        Assert.Throws<ArgumentException>(() => conventions.RequireCoveEntityAccess(" ", "tagId", Permissions.TagsRead));
        Assert.Throws<ArgumentException>(() => conventions.RequireCoveEntityAccess(EntityKinds.Tag, " ", Permissions.TagsRead));
        Assert.Throws<ArgumentException>(() => conventions.RequireCoveEntityAccess(EntityKinds.Tag, "tagId", " "));
    }

    [Fact]
    public void Explicit_escape_conventions_publish_distinct_Cove_metadata()
    {
        var authenticated = new TestConventionBuilder();
        authenticated.AllowWithoutCovePermission();
        Assert.Single(authenticated.Build().Metadata.OfType<CoveAllowWithoutPermissionMetadata>());

        var anonymous = new TestConventionBuilder();
        anonymous.AllowCoveAnonymous();
        var endpoint = anonymous.Build();
        Assert.Single(endpoint.Metadata.OfType<CoveAllowAnonymousMetadata>());
        Assert.Single(endpoint.Metadata.OfType<IAllowAnonymous>());
    }

    [Fact]
    public async Task Extension_data_source_replaces_supplied_endpoint_owner()
    {
        var appBuilder = WebApplication.CreateBuilder();
        await using var app = appBuilder.Build();
        var source = new ExtensionEndpointDataSource(app, "actual.extension");
        var foreignEndpoint = new RouteEndpointBuilder(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/test"),
            0)
        {
            Metadata =
            {
                new ExtensionEndpointMetadata("foreign.extension"),
                new ExtensionEndpointMetadata("unknown.extension"),
            },
        }.Build();
        source.DataSources.Add(new DefaultEndpointDataSource(foreignEndpoint));

        var publishedEndpoint = Assert.Single(source.Endpoints);
        var owner = Assert.Single(publishedEndpoint.Metadata.OfType<ExtensionEndpointMetadata>());
        Assert.Equal("actual.extension", owner.ExtensionId);
    }

    [Fact]
    public async Task Endpoint_registration_warns_once_with_only_policyless_route_templates()
    {
        var logger = new RecordingLogger<ExtensionManager>();
        var appBuilder = WebApplication.CreateBuilder();
        appBuilder.Services.AddSingleton<ILogger<ExtensionManager>>(logger);
        await using var app = appBuilder.Build();
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = appBuilder.Configuration,
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "test",
        });
        manager.Register(new TestApiExtension(endpoints =>
        {
            endpoints.MapGet("/legacy-one", () => Results.Ok());
            endpoints.MapPost("/legacy-two/{id}", (string id) => Results.Ok(id));
            endpoints.MapGet("/asp-anonymous", () => Results.Ok())
                .WithMetadata(new Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute());
            for (var index = 0; index < 9; index++)
                endpoints.MapGet($"/extra-{index}", () => Results.Ok());
            endpoints.MapGet("/permission", () => Results.Ok())
                .RequireCovePermission(Permissions.TagsRead);
            endpoints.MapGet("/entity/{tagId}", () => Results.Ok())
                .RequireCoveEntityAccess(EntityKinds.Tag, "tagId", Permissions.TagsRead);
            endpoints.MapGet("/authenticated", () => Results.Ok())
                .AllowWithoutCovePermission();
            endpoints.MapGet("/anonymous", () => Results.Ok())
                .AllowCoveAnonymous();
        }));
        manager.SetRouteBuilder(app);
        manager.PrepareRuntimeServices(app.Services);

        manager.SetupDynamicEndpoints();

        var warning = Assert.Single(logger.Messages, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("test.api", warning.Message);
        Assert.Contains("12 endpoint(s)", warning.Message);
        Assert.Contains("GET /legacy-one", warning.Message);
        Assert.Contains("POST /legacy-two/{id}", warning.Message);
        Assert.Contains("GET /asp-anonymous", warning.Message);
        Assert.Contains("GET /extra-6", warning.Message);
        Assert.DoesNotContain("GET /extra-7", warning.Message);
        Assert.DoesNotContain("GET /extra-8", warning.Message);
        Assert.Contains("and 2 more", warning.Message);
        Assert.DoesNotContain("/permission", warning.Message);
        Assert.DoesNotContain("/entity/", warning.Message);
        Assert.DoesNotContain("/authenticated", warning.Message);
        Assert.DoesNotContain("/anonymous", warning.Message);
    }

    [Fact]
    public async Task Endpoint_registration_does_not_warn_when_every_endpoint_declares_Cove_access()
    {
        var logger = new RecordingLogger<ExtensionManager>();
        var appBuilder = WebApplication.CreateBuilder();
        appBuilder.Services.AddSingleton<ILogger<ExtensionManager>>(logger);
        await using var app = appBuilder.Build();
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = appBuilder.Configuration,
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "test",
        });
        manager.Register(new TestApiExtension(endpoints =>
        {
            endpoints.MapGet("/permission", () => Results.Ok())
                .RequireCovePermission(Permissions.TagsRead);
            endpoints.MapGet("/entity/{tagId}", () => Results.Ok())
                .RequireCoveEntityAccess(EntityKinds.Tag, "tagId", Permissions.TagsRead);
            endpoints.MapGet("/authenticated", () => Results.Ok())
                .AllowWithoutCovePermission();
            endpoints.MapGet("/anonymous", () => Results.Ok())
                .AllowCoveAnonymous();
        }));
        manager.SetRouteBuilder(app);
        manager.PrepareRuntimeServices(app.Services);

        manager.SetupDynamicEndpoints();

        Assert.DoesNotContain(logger.Messages, entry => entry.Level == LogLevel.Warning);
    }

    private sealed class TestConventionBuilder : IEndpointConventionBuilder
    {
        private readonly List<Action<EndpointBuilder>> _conventions = [];

        public void Add(Action<EndpointBuilder> convention) => _conventions.Add(convention);

        public Endpoint Build()
        {
            var builder = new RouteEndpointBuilder(_ => Task.CompletedTask, RoutePatternFactory.Parse("/test"), 0);
            foreach (var convention in _conventions)
                convention(builder);
            return builder.Build();
        }
    }

    private sealed class TestApiExtension(Action<IEndpointRouteBuilder> mapEndpoints) : IApiExtension
    {
        public string Id => "test.api";
        public string Name => "Test API";
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;

        public void ConfigureServices(IServiceCollection services, ExtensionContext context) { }
        public void MapEndpoints(IEndpointRouteBuilder endpoints) => mapEndpoints(endpoints);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add((logLevel, formatter(state, exception)));
    }
}
public sealed class ExtensionEndpointAuthorizationMiddlewareTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Extension_endpoint_without_policy_preserves_anonymous_access(bool enforceDefaultDeny)
    {
        var harness = CreateHarness(principal: null);
        harness.Configuration.Auth.EnforceDefaultDeny = enforceDefaultDeny;

        await harness.InvokeAsync();

        Assert.True(harness.NextCalled);
        Assert.Equal(StatusCodes.Status200OK, harness.Context.Response.StatusCode);
        Assert.Empty(harness.Audit.Events);
    }

    [Fact]
    public async Task Explicit_authenticated_escape_requires_a_principal_without_a_permission()
    {
        var anonymous = CreateHarness(principal: null, new CoveAllowWithoutPermissionMetadata());
        await anonymous.InvokeAsync();
        Assert.False(anonymous.NextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, anonymous.Context.Response.StatusCode);

        var authenticated = CreateHarness(Principal([]), new CoveAllowWithoutPermissionMetadata());
        await authenticated.InvokeAsync();
        Assert.True(authenticated.NextCalled);
    }

    [Fact]
    public async Task Explicit_Cove_anonymous_escape_allows_anonymous_principal()
    {
        var harness = CreateHarness(principal: null, new CoveAllowAnonymousMetadata());

        await harness.InvokeAsync();

        Assert.True(harness.NextCalled);
        Assert.Equal(StatusCodes.Status200OK, harness.Context.Response.StatusCode);
    }

    public static TheoryData<object[]> ConflictingEscapePolicies => new()
    {
        new object[]
        {
            new CoveAllowAnonymousMetadata(),
            new CovePermissionRequirementMetadata([Permissions.TagsRead], PermissionMode.All),
        },
        new object[]
        {
            new CoveAllowWithoutPermissionMetadata(),
            new CoveRouteEntityAccessRequirementMetadata(EntityKinds.Tag, "tagId", Permissions.TagsRead),
        },
        new object[]
        {
            new CoveAllowAnonymousMetadata(),
            new CoveAllowWithoutPermissionMetadata(),
        },
    };

    [Theory]
    [MemberData(nameof(ConflictingEscapePolicies))]
    public async Task Conflicting_escape_policies_fail_closed_and_are_audited(object[] metadata)
    {
        var harness = CreateHarness(Principal([Permissions.TagsRead]), metadata);
        harness.Context.Request.RouteValues["tagId"] = 1;

        await harness.InvokeAsync();

        Assert.False(harness.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, harness.Context.Response.StatusCode);
        Assert.Equal("INVALID_AUTHORIZATION_POLICY",
            (await ReadBodyAsync(harness.Context)).GetProperty("code").GetString());
        var audit = Assert.Single(harness.Audit.Events);
        Assert.Equal("conflicting_escape_policy",
            JsonSerializer.SerializeToElement(audit.Detail).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task AspNet_anonymous_metadata_does_not_bypass_declared_Cove_permission()
    {
        var harness = CreateHarness(
            principal: null,
            new Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute(),
            new CovePermissionRequirementMetadata([Permissions.TagsRead], PermissionMode.All));

        await harness.InvokeAsync();

        Assert.False(harness.NextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, harness.Context.Response.StatusCode);
        Assert.Empty(harness.Audit.Events);
    }

    [Fact]
    public async Task Anonymous_principal_gets_standard_unauthorized_response()
    {
        var harness = CreateHarness(
            principal: null,
            new CovePermissionRequirementMetadata([Permissions.TagsRead], PermissionMode.All));

        await harness.InvokeAsync();

        Assert.Equal(StatusCodes.Status401Unauthorized, harness.Context.Response.StatusCode);
        Assert.False(harness.NextCalled);
        Assert.Equal("UNAUTHORIZED", (await ReadBodyAsync(harness.Context)).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(PermissionMode.All, true)]
    [InlineData(PermissionMode.Any, true)]
    public async Task Permission_modes_allow_matching_principals(PermissionMode mode, bool expected)
    {
        var permissions = mode == PermissionMode.All
            ? new[] { Permissions.TagsRead, Permissions.TagsWrite }
            : new[] { Permissions.TagsWrite };
        var harness = CreateHarness(
            Principal(permissions),
            new CovePermissionRequirementMetadata([Permissions.TagsRead, Permissions.TagsWrite], mode));

        await harness.InvokeAsync();

        Assert.Equal(expected, harness.NextCalled);
        Assert.Equal(StatusCodes.Status200OK, harness.Context.Response.StatusCode);
    }

    [Fact]
    public async Task Permission_any_denial_returns_missing_permissions_and_is_audited()
    {
        var harness = CreateHarness(
            Principal([]),
            new CovePermissionRequirementMetadata([Permissions.TagsRead, Permissions.TagsWrite], PermissionMode.Any));

        await harness.InvokeAsync();

        Assert.Equal(StatusCodes.Status403Forbidden, harness.Context.Response.StatusCode);
        var body = await ReadBodyAsync(harness.Context);
        Assert.Equal("FORBIDDEN", body.GetProperty("code").GetString());
        Assert.Equal(2, body.GetProperty("missing").GetArrayLength());
        var audit = Assert.Single(harness.Audit.Events);
        Assert.Equal(AuditActions.PermissionDeny, audit.Action);
        Assert.Equal("endpoint", audit.TargetKind);
    }

    [Fact]
    public async Task Read_grants_satisfy_the_endpoint_permission_and_continue_to_entity_authorization()
    {
        var principal = Principal([], readGrantedKinds: [EntityKinds.Tag]);
        var harness = CreateHarness(
            principal,
            new CovePermissionRequirementMetadata([Permissions.TagsRead], PermissionMode.All),
            new CoveRouteEntityAccessRequirementMetadata(EntityKinds.Tag, "tagId", Permissions.TagsRead));
        harness.Context.Request.RouteValues["tagId"] = "42";

        await harness.InvokeAsync();

        Assert.True(harness.NextCalled);
        var call = Assert.Single(harness.Authorization.Calls);
        Assert.Same(principal, call.Principal);
        Assert.Equal(Permissions.TagsRead, call.Permission);
        Assert.Equal(new EntityRef(EntityKinds.Tag, "42"), call.Entity);
    }

    [Fact]
    public async Task Read_grants_do_not_satisfy_permissions_without_a_matching_entity_check()
    {
        var principal = Principal([], readGrantedKinds: [EntityKinds.Tag]);
        var permissionOnly = CreateHarness(
            principal,
            new CovePermissionRequirementMetadata([Permissions.TagsRead], PermissionMode.All));

        await permissionOnly.InvokeAsync();

        Assert.False(permissionOnly.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, permissionOnly.Context.Response.StatusCode);
        Assert.Empty(permissionOnly.Authorization.Calls);

        var unrelatedEntity = CreateHarness(
            principal,
            new CovePermissionRequirementMetadata([Permissions.TagsRead], PermissionMode.All),
            new CoveRouteEntityAccessRequirementMetadata(
                EntityKinds.Video,
                "videoId",
                Permissions.VideosRead));
        unrelatedEntity.Context.Request.RouteValues["videoId"] = "42";

        await unrelatedEntity.InvokeAsync();

        Assert.False(unrelatedEntity.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, unrelatedEntity.Context.Response.StatusCode);
        Assert.Empty(unrelatedEntity.Authorization.Calls);

        var wrongEntityKind = CreateHarness(
            principal,
            new CovePermissionRequirementMetadata([Permissions.TagsRead], PermissionMode.All),
            new CoveRouteEntityAccessRequirementMetadata(
                EntityKinds.Video,
                "videoId",
                Permissions.TagsRead));
        wrongEntityKind.Context.Request.RouteValues["videoId"] = "42";

        await wrongEntityKind.InvokeAsync();

        Assert.False(wrongEntityKind.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, wrongEntityKind.Context.Response.StatusCode);
        Assert.Empty(wrongEntityKind.Authorization.Calls);
    }

    [Fact]
    public async Task Entity_only_read_requirement_rejects_a_permission_for_the_wrong_entity_kind()
    {
        var harness = CreateHarness(
            Principal([], readGrantedKinds: [EntityKinds.Tag]),
            new CoveRouteEntityAccessRequirementMetadata(
                EntityKinds.Video,
                "videoId",
                Permissions.TagsRead));
        harness.Context.Request.RouteValues["videoId"] = "42";

        await harness.InvokeAsync();

        Assert.False(harness.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, harness.Context.Response.StatusCode);
        Assert.Equal("INVALID_AUTHORIZATION_POLICY",
            (await ReadBodyAsync(harness.Context)).GetProperty("code").GetString());
        Assert.Empty(harness.Authorization.Calls);
    }

    [Fact]
    public async Task Entity_denial_returns_standard_forbidden_response_and_is_audited()
    {
        var harness = CreateHarness(
            Principal([Permissions.TagsWrite]),
            new CovePermissionRequirementMetadata([Permissions.TagsWrite], PermissionMode.All),
            new CoveRouteEntityAccessRequirementMetadata(EntityKinds.Tag, "tagId", Permissions.TagsWrite));
        harness.Context.Request.RouteValues["tagId"] = 12;
        harness.Authorization.Result = AuthorizationResult.Deny("Restricted by a content rule.", Permissions.TagsWrite);

        await harness.InvokeAsync();

        Assert.False(harness.NextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, harness.Context.Response.StatusCode);
        var body = await ReadBodyAsync(harness.Context);
        Assert.Equal("tag", body.GetProperty("entityKind").GetString());
        Assert.Equal("12", body.GetProperty("entityId").GetString());
        Assert.Equal("Restricted by a content rule.", body.GetProperty("message").GetString());
        var audit = Assert.Single(harness.Audit.Events);
        Assert.Equal("tag", audit.TargetKind);
        Assert.Equal("12", audit.TargetId);
    }

    [Fact]
    public async Task Missing_route_value_is_denied_without_calling_the_endpoint_or_authorizer()
    {
        var harness = CreateHarness(
            Principal([Permissions.TagsWrite]),
            new CovePermissionRequirementMetadata([Permissions.TagsWrite], PermissionMode.All),
            new CoveRouteEntityAccessRequirementMetadata(EntityKinds.Tag, "tagId", Permissions.TagsWrite));

        await harness.InvokeAsync();

        Assert.False(harness.NextCalled);
        Assert.Empty(harness.Authorization.Calls);
        Assert.Equal(StatusCodes.Status403Forbidden, harness.Context.Response.StatusCode);
        Assert.Equal("INVALID_ENTITY_REFERENCE", (await ReadBodyAsync(harness.Context)).GetProperty("code").GetString());
        Assert.Single(harness.Audit.Events);
    }

    [Theory]
    [InlineData("not-an-id")]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task Invalid_route_entity_id_is_denied_before_authorization(string routeValue)
    {
        var harness = CreateHarness(
            Principal([Permissions.TagsWrite]),
            new CovePermissionRequirementMetadata([Permissions.TagsWrite], PermissionMode.All),
            new CoveRouteEntityAccessRequirementMetadata(EntityKinds.Tag, "tagId", Permissions.TagsWrite));
        harness.Context.Request.RouteValues["tagId"] = routeValue;

        await harness.InvokeAsync();

        Assert.False(harness.NextCalled);
        Assert.Empty(harness.Authorization.Calls);
        Assert.Equal("INVALID_ENTITY_REFERENCE", (await ReadBodyAsync(harness.Context)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Inferred_entity_permission_requires_exactly_one_declared_permission()
    {
        var harness = CreateHarness(
            Principal([Permissions.TagsRead, Permissions.TagsWrite]),
            new CovePermissionRequirementMetadata([Permissions.TagsRead, Permissions.TagsWrite], PermissionMode.Any),
            new CoveRouteEntityAccessRequirementMetadata(EntityKinds.Tag, "tagId", null));
        harness.Context.Request.RouteValues["tagId"] = 1;

        await harness.InvokeAsync();

        Assert.False(harness.NextCalled);
        Assert.Empty(harness.Authorization.Calls);
        Assert.Equal("INVALID_AUTHORIZATION_POLICY", (await ReadBodyAsync(harness.Context)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Auth_disabled_or_non_extension_endpoints_are_untouched()
    {
        var disabled = CreateHarness(
            principal: null,
            new CovePermissionRequirementMetadata([Permissions.TagsRead], PermissionMode.All));
        disabled.Configuration.Auth.Enabled = false;

        await disabled.InvokeAsync();
        Assert.True(disabled.NextCalled);

        var ordinary = CreateHarness(
            principal: null,
            new CovePermissionRequirementMetadata([Permissions.TagsRead], PermissionMode.All));
        ordinary.Context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new CovePermissionRequirementMetadata([Permissions.TagsRead], PermissionMode.All)),
            "ordinary"));

        await ordinary.InvokeAsync();
        Assert.True(ordinary.NextCalled);
    }

    [Fact]
    public async Task Routed_pipeline_allows_anonymous_extension_endpoint_without_policy()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new CoveConfiguration
        {
            Auth = new AuthConfig { Enabled = true, EnforceDefaultDeny = true },
        });
        builder.Services.AddSingleton<ICurrentPrincipalAccessor>(new TestPrincipalAccessor(current: null));
        builder.Services.AddSingleton<IAuthorizationService, RecordingAuthorizationService>();
        var audit = new RecordingAuditService();
        builder.Services.AddSingleton<IAuditService>(audit);

        await using var app = builder.Build();
        app.UseMiddleware<ExtensionEndpointAuthorizationMiddleware>();
        app.MapGet("/extension-test", () => Results.Ok())
            .WithMetadata(new ExtensionEndpointMetadata("test.extension"));
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/extension-test");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task Routed_pipeline_rejects_anonymous_escape_combined_with_permission_policy()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new CoveConfiguration
        {
            Auth = new AuthConfig { Enabled = true, EnforceDefaultDeny = true },
        });
        builder.Services.AddSingleton<ICurrentPrincipalAccessor>(new TestPrincipalAccessor(current: null));
        builder.Services.AddSingleton<IAuthorizationService, RecordingAuthorizationService>();
        var audit = new RecordingAuditService();
        builder.Services.AddSingleton<IAuditService>(audit);

        await using var app = builder.Build();
        app.UseMiddleware<ExtensionEndpointAuthorizationMiddleware>();
        app.MapGet("/conflicting-extension-test", () => Results.Ok())
            .AllowCoveAnonymous()
            .RequireCovePermission(Permissions.TagsRead)
            .WithMetadata(new ExtensionEndpointMetadata("test.extension"));
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/conflicting-extension-test");

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("INVALID_AUTHORIZATION_POLICY",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Single(audit.Events);
    }

    [Fact]
    public async Task Extension_service_overrides_cannot_control_endpoint_authentication_authorization_or_denial_auditing()
    {
        var hostPrincipal = new TestPrincipalAccessor(current: null);
        var hostAuthorization = new RecordingAuthorizationService
        {
            Result = AuthorizationResult.Deny("Denied by the host.", Permissions.TagsRead),
        };
        var hostAudit = new RecordingAuditService();
        var extensionPrincipal = new TestPrincipalAccessor(Principal([Permissions.TagsRead]));
        var extensionAuthorization = new RecordingAuthorizationService();
        var extensionAudit = new RecordingAuditService();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new CoveConfiguration
        {
            Auth = new AuthConfig { Enabled = true, EnforceDefaultDeny = true },
        });
        builder.Services.AddSingleton<ICurrentPrincipalAccessor>(hostPrincipal);
        builder.Services.AddSingleton<IAuthorizationService>(hostAuthorization);
        builder.Services.AddSingleton<IAuditService>(hostAudit);
        var hostDescriptors = builder.Services.ToList();

        await using var app = builder.Build();
        using var overlay = new ExtensionServiceOverlay(app.Services, hostDescriptors, logger: null);
        const string extensionId = "test.conflicting-security-services";
        overlay.BuildProvider(
            extensionId,
            new ConflictingSecurityServicesExtension(
                extensionPrincipal,
                extensionAuthorization,
                extensionAudit),
            new ExtensionContext
            {
                Configuration = builder.Configuration,
                DataDirectory = Path.GetTempPath(),
                CoveVersion = "test",
            },
            (_, exception) => throw exception);
        using (var extensionScope = overlay.CreateScope(extensionId))
        {
            Assert.Same(extensionPrincipal,
                extensionScope.ServiceProvider.GetRequiredService<ICurrentPrincipalAccessor>());
            Assert.Same(extensionAuthorization,
                extensionScope.ServiceProvider.GetRequiredService<IAuthorizationService>());
            Assert.Same(extensionAudit,
                extensionScope.ServiceProvider.GetRequiredService<IAuditService>());
        }

        ExtensionEndpointPipeline.Configure(app, overlay.CreateScope);
        app.MapGet("/extension-security/{tagId:int}", () => Results.Ok())
            .RequireCovePermission(Permissions.TagsRead)
            .RequireCoveEntityAccess(EntityKinds.Tag, "tagId", Permissions.TagsRead)
            .WithMetadata(new ExtensionEndpointMetadata(extensionId));
        await app.StartAsync();

        var client = app.GetTestClient();
        var unauthenticatedResponse = await client.GetAsync("/extension-security/42");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, unauthenticatedResponse.StatusCode);
        Assert.Empty(hostAuthorization.Calls);
        Assert.Empty(hostAudit.Events);

        var authenticatedHostPrincipal = Principal([Permissions.TagsRead]);
        hostPrincipal.Set(authenticatedHostPrincipal);

        var deniedResponse = await client.GetAsync("/extension-security/42");

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, deniedResponse.StatusCode);
        var hostCall = Assert.Single(hostAuthorization.Calls);
        Assert.Same(authenticatedHostPrincipal, hostCall.Principal);
        Assert.Equal(new EntityRef(EntityKinds.Tag, "42"), hostCall.Entity);
        var auditEvent = Assert.Single(hostAudit.Events);
        Assert.Equal(AuditActions.PermissionDeny, auditEvent.Action);
        Assert.Equal(EntityKinds.Tag, auditEvent.TargetKind);
        Assert.Equal("42", auditEvent.TargetId);
        Assert.Equal(0, extensionPrincipal.CurrentReads);
        Assert.Empty(extensionAuthorization.Calls);
        Assert.Empty(extensionAudit.Events);
    }

    private static Harness CreateHarness(CovePrincipal? principal, params object[] requirements)
    {
        var metadata = new List<object> { new ExtensionEndpointMetadata("test.extension") };
        metadata.AddRange(requirements);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Path = "/api/extensions/test/tags/42";
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "test endpoint"));

        return new Harness(context, principal);
    }

    private static CovePrincipal Principal(IEnumerable<string> permissions, IEnumerable<string>? readGrantedKinds = null) => new()
    {
        UserId = 7,
        Username = "tester",
        Kind = PrincipalKind.User,
        Roles = new HashSet<string>(),
        Permissions = permissions.ToHashSet(StringComparer.Ordinal),
        ReadGrantedEntityKinds = (readGrantedKinds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
    };

    private static async Task<JsonElement> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return (await JsonDocument.ParseAsync(context.Response.Body)).RootElement.Clone();
    }

    private sealed class Harness
    {
        private readonly ExtensionEndpointAuthorizationMiddleware _middleware;
        private readonly TestPrincipalAccessor _principalAccessor;

        public Harness(DefaultHttpContext context, CovePrincipal? principal)
        {
            Context = context;
            _principalAccessor = new TestPrincipalAccessor(principal);
            _middleware = new ExtensionEndpointAuthorizationMiddleware(_ =>
            {
                NextCalled = true;
                return Task.CompletedTask;
            });
        }

        public DefaultHttpContext Context { get; }
        public CoveConfiguration Configuration { get; } = new() { Auth = new AuthConfig { Enabled = true } };
        public RecordingAuthorizationService Authorization { get; } = new();
        public RecordingAuditService Audit { get; } = new();
        public bool NextCalled { get; private set; }

        public Task InvokeAsync() => _middleware.InvokeAsync(
            Context, Configuration, _principalAccessor, Authorization, Audit);
    }

    private sealed class TestPrincipalAccessor(CovePrincipal? current) : ICurrentPrincipalAccessor
    {
        private CovePrincipal? _current = current;

        public int CurrentReads { get; private set; }
        public CovePrincipal? Current
        {
            get
            {
                CurrentReads++;
                return _current;
            }
        }

        public void Set(CovePrincipal? principal) => _current = principal;
    }

    private sealed class ConflictingSecurityServicesExtension(
        ICurrentPrincipalAccessor principalAccessor,
        IAuthorizationService authorization,
        IAuditService audit) : IExtension
    {
        public string Id => "test.conflicting-security-services";
        public string Name => "Conflicting security services";
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
        {
            services.AddSingleton(principalAccessor);
            services.AddSingleton(authorization);
            services.AddSingleton(audit);
        }
    }

    private sealed class RecordingAuthorizationService : IAuthorizationService
    {
        public AuthorizationResult Result { get; set; } = AuthorizationResult.Allow();
        public List<(CovePrincipal? Principal, string Permission, EntityRef? Entity)> Calls { get; } = [];

        public AuthorizationResult Authorize(CovePrincipal? principal, string permission, EntityRef? entity = null)
            => throw new NotSupportedException();

        public Task<AuthorizationResult> AuthorizeAsync(CovePrincipal? principal, string permission, EntityRef? entity, CancellationToken ct)
        {
            Calls.Add((principal, permission, entity));
            return Task.FromResult(Result);
        }

        public void Require(CovePrincipal? principal, string permission, EntityRef? entity = null)
            => throw new NotSupportedException();

        public bool Has(CovePrincipal? principal, string permission) => principal?.Has(permission) == true;
    }

    private sealed class RecordingAuditService : IAuditService
    {
        public List<(string Action, string Outcome, string? TargetKind, string? TargetId, object? Detail, CancellationToken CancellationToken)> Events { get; } = [];

        public Task LogAsync(string action, string outcome, CovePrincipal? actor = null, string? targetKind = null,
            string? targetId = null, object? detail = null, CancellationToken ct = default)
        {
            Events.Add((action, outcome, targetKind, targetId, detail, ct));
            return Task.CompletedTask;
        }
    }
}
