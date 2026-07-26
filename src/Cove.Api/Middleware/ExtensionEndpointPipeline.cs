using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Api.Middleware;

/// <summary>
/// Configures host authorization and extension-owned request services for extension endpoints.
/// Host authorization must run before the extension overlay becomes the request service provider.
/// </summary>
internal static class ExtensionEndpointPipeline
{
    public static void Configure(
        IApplicationBuilder app,
        Func<string, IServiceScope> createExtensionScope)
    {
        // MVC authorization filters do not run for minimal APIs. Resolve and execute Cove's
        // authorization services while RequestServices still points at the host request scope.
        app.UseMiddleware<ExtensionEndpointAuthorizationMiddleware>();

        // Only endpoint execution sees extension registrations. Host singletons are shared and host
        // scoped services (for example, the pooled DbContext) are overlay-owned and disposed once.
        app.Use(async (context, next) =>
        {
            var marker = context.GetEndpoint()?.Metadata.GetMetadata<ExtensionEndpointMetadata>();
            if (marker == null)
            {
                await next(context);
                return;
            }

            using var scope = createExtensionScope(marker.ExtensionId);
            var hostServices = context.RequestServices;
            context.RequestServices = scope.ServiceProvider;
            try
            {
                await next(context);
            }
            finally
            {
                context.RequestServices = hostServices;
            }
        });
    }
}
