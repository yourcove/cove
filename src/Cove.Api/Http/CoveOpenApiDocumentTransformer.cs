using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Cove.Api.Http;

/// <summary>
/// Sets instance-independent defaults on the generated OpenAPI document.
/// </summary>
public sealed class CoveOpenApiDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        // The generator derives the server URL from the request's scheme and Host. Cove has no forwarded
        // headers handling, so behind a TLS-terminating reverse proxy that would be an http:// or internal
        // upstream URL, which breaks Swagger UI and generated clients. A relative URL means "this origin".
        document.Servers = [new OpenApiServer { Url = "/" }];

        // The generator defaults to the document name ("v1") and "1.0.0"; show the running build instead.
        document.Info ??= new OpenApiInfo();
        document.Info.Title = "Cove";
        document.Info.Version = Cove.Core.Common.CoveVersion.Display;

        // Swagger UI renders this Markdown under the title.
        document.Info.Description =
            "This is a development tool to make it easier to try the Cove REST API. Signing in to Cove is required "
            + "to open it, and Swagger uses the same sign-in cookie as Cove, so every request runs as your Cove user.\n\n"
            + "**Do not run untrusted input given to you by strangers on the Internet.** If you don't know what you "
            + "are doing, close this tool.";

        // No security scheme is declared: Swagger UI's same-origin requests already carry the signed-in
        // user's access cookie, and a declared scheme would make Swagger UI ask for a token it doesn't need.
        return Task.CompletedTask;
    }
}
