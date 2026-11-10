using Cove.Core.Auth;
using Cove.Core.Interfaces;

namespace Cove.Api.Middleware;

/// <summary>
/// Restricts the OpenAPI document and Swagger UI to signed-in users and API tokens while authentication is
/// enabled. Share links are rejected because they grant access to a viewing bundle, not to the API.
/// With authentication disabled the routes follow the rest of the app, including the outside-IP failsafe.
/// Must run after <see cref="CurrentPrincipalMiddleware"/> and before the documentation is served.
/// </summary>
public sealed class ApiDocumentationAccessMiddleware(RequestDelegate next)
{
    public const string DocumentPattern = "/openapi/{documentName}.json";
    public const string DocumentPath = "/openapi/v1.json";
    public const string SwaggerUiRoutePrefix = "swagger";

    /// <summary>Marks the endpoint that serves the OpenAPI document.</summary>
    public sealed class EndpointMarker;

    /// <summary>
    /// True for the Swagger UI paths and for the OpenAPI document endpoint. The endpoint check keeps the
    /// document gated even if something between routing and this middleware rewrites the request path.
    /// </summary>
    public static bool AppliesTo(HttpContext context)
        => context.Request.Path.StartsWithSegments("/openapi", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.StartsWithSegments("/" + SwaggerUiRoutePrefix, StringComparison.OrdinalIgnoreCase)
            || context.GetEndpoint()?.Metadata.GetMetadata<EndpointMarker>() is not null;

    public async Task InvokeAsync(
        HttpContext context,
        CoveConfiguration configuration,
        ICurrentPrincipalAccessor principalAccessor)
    {
        var authDisabled = configuration.Auth is { Enabled: false };
        if (authDisabled || principalAccessor.Current?.Kind is PrincipalKind.User or PrincipalKind.ApiToken)
        {
            await next(context);
            return;
        }

        // A browser opening the docs gets no access cookie when it arrives from another site (the cookie is
        // SameSite=Strict) or after the cookie expires, although Cove still holds the session. Send it to
        // Cove's sign-in page, which restores or starts the session and loads this URL again.
        if (IsBrowserNavigation(context.Request))
        {
            var returnPath = $"{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
            context.Response.Redirect($"{context.Request.PathBase}/login?redirect={Uri.EscapeDataString(returnPath)}");
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new
        {
            code = "UNAUTHORIZED",
            message = "Authentication required.",
        }, context.RequestAborted);
    }

    private static bool IsBrowserNavigation(HttpRequest request)
        => (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            && request.GetTypedHeaders().Accept.Any(accept => accept.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase));
}
