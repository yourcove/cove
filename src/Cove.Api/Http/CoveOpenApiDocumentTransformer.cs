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

        // No security scheme is declared: Swagger UI's same-origin requests already carry the signed-in
        // user's access cookie, and a declared scheme would make Swagger UI ask for a token it doesn't need.
        return Task.CompletedTask;
    }
}
