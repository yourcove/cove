using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cove.Tests.Integration;

/// <summary>
/// The OpenAPI document and Swagger UI are served in every environment, but only to a signed-in caller
/// while authentication is enabled. The factory runs in a non-Development environment, so these tests also
/// cover production hosting.
/// </summary>
public sealed class ApiDocumentationAccessTests
{
    private const string DocumentPath = "/openapi/v1.json";
    private const string SwaggerUiPath = "/swagger/index.html";

    [Theory]
    [InlineData(SwaggerUiPath, "/login?redirect=%2Fswagger%2Findex.html")]
    [InlineData("/swagger", "/login?redirect=%2Fswagger")]
    [InlineData("/swagger/index.html?urls.primaryName=Cove%20v1", "/login?redirect=%2Fswagger%2Findex.html%3Furls.primaryName%3DCove%2520v1")]
    [InlineData(DocumentPath, "/login?redirect=%2Fopenapi%2Fv1.json")]
    public async Task Anonymous_browser_navigation_is_sent_to_sign_in_and_back(string path, string expectedLocation)
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<CoveConfiguration>().Auth.Enabled = true;
        using var client = CreateNonRedirectingClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expectedLocation, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Swagger_ui_document_fetch_without_a_session_gets_401_not_a_redirect()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<CoveConfiguration>().Auth.Enabled = true;
        using var client = CreateNonRedirectingClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, DocumentPath);
        request.Headers.Accept.ParseAdd("application/json,*/*");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(DocumentPath)]
    [InlineData(SwaggerUiPath)]
    [InlineData("/swagger")]
    [InlineData("/SWAGGER/index.html")]
    public async Task Anonymous_requests_are_rejected_when_auth_is_enabled(string path)
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<CoveConfiguration>().Auth.Enabled = true;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signed_in_bearer_caller_receives_the_openapi_document()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<CoveConfiguration>().Auth.Enabled = true;
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync(DocumentPath, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.TryGetProperty("openapi", out _));
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/Auth/me", out _));
    }

    [Fact]
    public async Task Signed_in_cookie_caller_receives_swagger_ui_pointed_at_the_openapi_document()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<CoveConfiguration>().Auth.Enabled = true;
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "cove_access_token=integration-test-token");

        using var page = await client.GetAsync(SwaggerUiPath, TestContext.Current.CancellationToken);
        using var config = await client.GetAsync("/swagger/index.js", TestContext.Current.CancellationToken);
        using var document = await client.GetAsync(DocumentPath, TestContext.Current.CancellationToken);
        var configBody = await config.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);
        Assert.Contains(DocumentPath, configBody, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
    }

    [Fact]
    public async Task Api_token_with_a_narrow_scope_receives_the_document()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<CoveConfiguration>().Auth.Enabled = true;
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "integration-scoped-token");

        using var response = await client.GetAsync(DocumentPath, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(DocumentPath)]
    [InlineData(SwaggerUiPath)]
    public async Task Share_link_principal_is_rejected(string path)
    {
        using var factory = new CoveWebApplicationFactory(configureTestServices: services =>
        {
            services.RemoveAll<IShareLinkService>();
            services.AddScoped<IShareLinkService, ShareLinkPrincipalService>();
        });
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<CoveConfiguration>().Auth.Enabled = true;
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Share-Token", "integration-share-token");

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Document_uses_a_relative_server_url_and_declares_no_security_scheme()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<CoveConfiguration>().Auth.Enabled = true;
        using var client = factory.CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, DocumentPath);
        request.Headers.Host = "internal-upstream:5073";

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var servers = document.RootElement.GetProperty("servers");
        Assert.Equal(1, servers.GetArrayLength());
        Assert.Equal("/", servers[0].GetProperty("url").GetString());
        // Swagger UI reuses the signed-in session's cookie; a declared scheme would make it ask for a token.
        Assert.False(document.RootElement.TryGetProperty("security", out _));
        Assert.False(document.RootElement.TryGetProperty("components", out var components)
            && components.TryGetProperty("securitySchemes", out _));
    }

    [Fact]
    public async Task Extension_style_multipart_form_endpoint_does_not_break_generation()
    {
        // Swashbuckle's generator failed the whole document on a minimal-API [FromForm] IFormFile parameter,
        // which extension endpoints use for uploads. The built-in generator must keep producing a document.
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<CoveConfiguration>().Auth.Enabled = true;
        var extensions = factory.Services.GetRequiredService<ExtensionManager>();
        extensions.Register(new UploadEndpointExtension(), "integration-test");
        Assert.True(await extensions.InitializeExtensionAsync(
            "integration.upload",
            factory.Services,
            TestContext.Current.CancellationToken));
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync(DocumentPath, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty(UploadEndpointExtension.Route, out _));
    }

    [Fact]
    public async Task Swashbuckle_document_route_is_not_served()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<CoveConfiguration>().Auth.Enabled = true;
        using var client = factory.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/swagger/v1/swagger.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Trusted_local_caller_receives_the_document_when_auth_is_disabled()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<CoveConfiguration>().Auth.Enabled = false;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(DocumentPath, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static HttpClient CreateNonRedirectingClient(CoveWebApplicationFactory factory)
    {
        // The factory listens on a real Kestrel port, and its clients follow redirects in the socket handler
        // whatever WebApplicationFactoryClientOptions says, so build a plain client against that address.
        using var probe = factory.CreateClient();
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = probe.BaseAddress,
        };
    }

    private sealed class ShareLinkPrincipalService : IShareLinkService
    {
        public Task<IReadOnlyList<ShareLinkDto>> ListAsync(int? createdByUserId = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ShareLinkIssued> CreateAsync(CreateShareLinkRequest req, CovePrincipal? actor, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task RevokeAsync(Guid id, CovePrincipal? actor, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<CovePrincipal?> ResolveAsync(string token, string? password, string? ip, string? userAgent, CancellationToken ct = default)
            => Task.FromResult<CovePrincipal?>(new CovePrincipal
            {
                UserId = null,
                Username = "share-link",
                Kind = PrincipalKind.ShareLink,
                Roles = new HashSet<string>(),
                Permissions = new HashSet<string>(StringComparer.Ordinal) { Permissions.VideosRead },
                TokenId = Guid.NewGuid(),
            });
    }

    private sealed class UploadEndpointExtension : IApiExtension
    {
        public const string Route = "/api/ext/integration.upload/items/{id}/media";

        public string Id => "integration.upload";
        public string Name => "Integration upload";
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;

        public void ConfigureServices(IServiceCollection services, ExtensionContext context) { }

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
            => endpoints.MapPost(Route, (int id, [FromForm] IFormFile file) => Results.Ok(id))
                .DisableAntiforgery()
                .AllowWithoutCovePermission();
    }
}
