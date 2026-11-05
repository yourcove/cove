using Microsoft.AspNetCore.Http;

namespace Cove.Api.Http;

/// <summary>
/// The browser cache lifetimes for media responses, named by why a response may be cached for that long.
/// Change a lifetime here to change every route that uses it. Lifetimes do not evict what browsers have
/// already cached; to make them refetch a route family now, bump its revision in
/// <see cref="MediaCacheRevisions"/>.
/// </summary>
internal static class MediaCache
{
    /// <summary>The URL names a version (<c>v</c>), so the bytes behind it never change.</summary>
    internal static void Versioned(HttpResponse response)
        => response.Headers.CacheControl = "public, max-age=31536000, immutable";

    /// <summary>Unversioned, but the content rarely changes: library files and generated assets.</summary>
    internal static void Stable(HttpResponse response)
        => response.Headers.CacheControl = "public, max-age=86400";

    /// <summary>Unversioned content a user can replace under the same URL, such as an entity image or a caption.</summary>
    internal static void Replaceable(HttpResponse response)
        => response.Headers.CacheControl = "public, max-age=3600";

    /// <summary>A stand-in served until the real asset can be made, so it is replaced soon after.</summary>
    internal static void StandIn(HttpResponse response)
        => response.Headers.CacheControl = "public, max-age=300";

    /// <summary>May be reused, but only after checking with the server: playlists that grow while transcoding.</summary>
    internal static void Revalidate(HttpResponse response)
        => response.Headers.CacheControl = "no-cache";

    /// <summary>Must never be reused: a frame that is still being generated.</summary>
    internal static void None(HttpResponse response)
        => response.Headers.CacheControl = "no-store, no-cache, max-age=0, must-revalidate";
}
