using Microsoft.AspNetCore.Http;

namespace Cove.Api.Http;

/// <summary>
/// Headers for serving files whose bytes Cove did not author — library images, entity image blobs (uploads,
/// scraped images, library copies), uploaded UI assets. An SVG opened directly is a document that can run
/// script on Cove's origin; the sandboxing policy stops that, and nosniff stops a browser reinterpreting a
/// mislabelled file as something executable. Neither affects the file when it is used as an &lt;img&gt; or
/// CSS image.
/// </summary>
internal static class UntrustedContentHeaders
{
    private const string ContentSecurityPolicy = "default-src 'none'; img-src data:; style-src 'unsafe-inline'; sandbox";

    internal static void Apply(HttpResponse response)
    {
        response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        response.Headers.XContentTypeOptions = "nosniff";
    }
}
